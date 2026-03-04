using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using RayTraceAPI;
using System.Drawing;

namespace S2AWH;

internal enum ViewerRayTraceStage : byte
{
    Los = 0,
    Micro = 1,
    Aim = 2,
    Preload = 3,
    Jump = 4,
    Count = 5
}

[MinimumApiVersion(362)]
public partial class S2AWH : BasePlugin, IPluginConfig<S2AWHConfig>
{
    private const int VisibilitySlotCapacity = 65;
    private const int ViewerRayTraceStageCount = (int)ViewerRayTraceStage.Count;
    private const float StationarySpeedSqThreshold = 4.0f;
    private const int DebugSummaryIntervalTicks = 4096;
    private const float StartupDigestDelaySeconds = 6.0f;
    private const float RoundStartGraceSeconds = 0.5f;
    private const int SnapshotStabilizeGraceTicks = 32;
    private const float SnapshotZeroOriginEpsilon = 1.0f;
    private const float MaxBoundsExtentUnits = 512.0f;
    private const float MaxLocalBoundsCoordinateUnits = 384.0f;
    private const float MaxLocalHorizontalCenterOffset = 96.0f;
    private const float MinLocalVerticalCenter = -96.0f;
    private const float MaxLocalVerticalCenter = 192.0f;
    private const float MaxBoundsContainmentShrinkUnits = 8.0f;
    private const int ViewerRayCountHudRefreshIntervalTicks = 8;
    private const int HiddenEntityTransitionGraceTicks = 32;
    private const int MaxTrackedTransmitEntitiesPerTarget = 192;
    private const int VisibleReacquireConfirmTicks = 4;

    /// <summary>
    /// Holds the set of entity handles (pawn + weapons) belonging to a single target player,
    /// cached per tick to avoid repeated resolution during transmit filtering.
    /// </summary>
    private sealed class TargetTransmitEntities
    {
        public int LastFullRefreshTick = -1;
        public int SanitizeTick = -1;
        public int OwnedClosureTick = -1;
        public int RetainUntilTick = -1;
        public int LastKnownTeam;
        public bool LastKnownIsBot;
        public uint PawnHandleRaw = uint.MaxValue;
        public uint ControllerHandleRaw = uint.MaxValue;
        public uint[] RawHandles = new uint[64];
        public int Count;
    }

    private sealed class ViewerVisibilityRow
    {
        public bool[] Decisions = new bool[VisibilitySlotCapacity];
        public bool[] Known = new bool[VisibilitySlotCapacity];
        public uint[] PawnHandles = new uint[VisibilitySlotCapacity];
        public int[] EvalTicks = new int[VisibilitySlotCapacity];
    }

    private sealed class OwnedEntityBucket
    {
        public uint[] RawHandles = new uint[8];
        public int Count;
    }

    private interface ISlotRow
    {
        int ActiveCount { get; }
    }

    /// <summary>
    /// Tracks how long each target should remain revealed after LOS is lost,
    /// preventing abrupt pop-out when players momentarily break line of sight.
    /// </summary>
    private sealed class RevealHoldRow : ISlotRow
    {
        public int[] HoldUntilTick = new int[VisibilitySlotCapacity];
        public bool[] Known = new bool[VisibilitySlotCapacity];
        public int ActiveCount;
        int ISlotRow.ActiveCount => ActiveCount;
    }

    /// <summary>
    /// Stores the last stable visibility decision for each viewer-to-target pair,
    /// used as a fallback when the current evaluation returns UnknownTransient.
    /// </summary>
    private sealed class StableDecisionRow : ISlotRow
    {
        public bool[] Decisions = new bool[VisibilitySlotCapacity];
        public int[] Ticks = new int[VisibilitySlotCapacity];
        public bool[] Known = new bool[VisibilitySlotCapacity];
        public int ActiveCount;
        int ISlotRow.ActiveCount => ActiveCount;
    }

    /// <summary>
    /// Requires a few consecutive visible ticks before a recently hidden target is re-shown,
    /// reducing rapid hide/unhide churn in the engine transmit path.
    /// </summary>
    private sealed class VisibleConfirmRow : ISlotRow
    {
        public int[] FirstVisibleTick = new int[VisibilitySlotCapacity];
        public bool[] Known = new bool[VisibilitySlotCapacity];
        public int ActiveCount;
        int ISlotRow.ActiveCount => ActiveCount;
    }

    public override string ModuleName => "S2AWH (Source2 AntiWallhack)";
    public override string ModuleVersion => "3.0.2";
    public override string ModuleAuthor => "karola3vax";
    public override string ModuleDescription => "Prevents wallhacks from working using Ray-Trace by hiding players from out of line of sight.";

    private const int InitRetryIntervalTicks = 64;
    private const float UnknownStickyWindowSeconds = 0.150f;

    public S2AWHConfig Config { get; set; } = new();

    /// <summary>
    /// Applies, validates, and activates plugin configuration values.
    /// </summary>
    public void OnConfigParsed(S2AWHConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var warnings = config.Normalize();
        Config = config;
        S2AWHState.Current = config;
        _revealHoldTicks = ConvertRevealHoldSecondsToTicks(config.Preload.RevealHoldSeconds);
        _unknownStickyWindowTicks = ConvertUnknownStickySecondsToTicks();
        _collectDebugCounters = config.Diagnostics.ShowDebugInfo;

        foreach (var warning in warnings)
        {
            WarnLog(
                "A config value was out of range and was auto-corrected.",
                warning,
                "S2AWH is running fine with the corrected value."
            );
        }

        InfoLog(
            "Settings loaded.",
            $"Update rate: every {config.Core.UpdateFrequencyTicks} tick(s), reveal hold: {config.Preload.RevealHoldSeconds:F2}s, debug logs: {(config.Diagnostics.ShowDebugInfo ? "on" : "off")}.",
            "S2AWH is using these settings now."
        );

        if (_transmitFilter != null && (!config.Core.Enabled || !config.Diagnostics.DrawAmountOfRayNumber))
        {
            ClearViewerRayCountOverlays();
        }

        if (_transmitFilter != null && config.Core.Enabled)
        {
            RebuildVisibilityCacheSnapshot();
        }
    }

    private readonly PluginCapability<CRayTraceInterface> _rayTraceCapability = new("raytrace:craytraceinterface");

    private LosEvaluator? _losEvaluator;
    private PreloadPredictor? _predictor;
    private TransmitFilter? _transmitFilter;

    // Cache: ViewerSlot -> visibility-by-target-slot
    private readonly ViewerVisibilityRow?[] _visibilityCache = new ViewerVisibilityRow?[VisibilitySlotCapacity];
    // Memory: ViewerSlot -> TargetSlot state
    private readonly RevealHoldRow?[] _revealHoldRows = new RevealHoldRow?[VisibilitySlotCapacity];
    // Last stable decision memory: ViewerSlot -> TargetSlot state
    private readonly StableDecisionRow?[] _stableDecisionRows = new StableDecisionRow?[VisibilitySlotCapacity];
    // Re-show debounce: ViewerSlot -> TargetSlot state
    private readonly VisibleConfirmRow?[] _visibleConfirmRows = new VisibleConfirmRow?[VisibilitySlotCapacity];
    private int _staggeredViewerOffset;
    private int _ticksSinceInitRetry;
    private bool _hasLoggedWaitingForCapability;
    private bool _hasLoggedGlobalsNotReady;
    private bool _hasLoggedPlayerScanError;
    private bool _hasLoggedFilterEvaluationError;
    private bool _hasLoggedWeaponSyncError;
    private bool _hasLoggedOwnedEntityScanError;
    private int _lastDebugCachePlayerCount;
    private int _ticksSinceLastTransmitReport;
    private int _transmitCallbacksInWindow;
    private int _transmitHiddenEntitiesInWindow;
    private int _transmitFallbackChecksInWindow;
    private int _transmitRemovalNoEffectInWindow;
    private int _holdRefreshInWindow;
    private int _holdHitKeepAliveInWindow;
    private int _holdExpiredInWindow;
    private int _unknownEvalInWindow;
    private int _unknownStickyHitInWindow;
    private int _unknownHoldHitInWindow;
    private int _unknownFailOpenInWindow;
    private int _unknownFromExceptionInWindow;
    private int _revealHoldTicks = 1;
    private int _unknownStickyWindowTicks = 1;
    private bool _collectDebugCounters = true;
    private bool _startupDigestQueued;
    private readonly List<CCSPlayerController> _cachedLivePlayers = new(VisibilitySlotCapacity - 1);
    private int _cachedLivePlayersTick = -1;
    private bool _cachedLivePlayersValid;
    private readonly bool[] _liveSlotFlags = new bool[VisibilitySlotCapacity];
    private readonly TargetTransmitEntities?[] _targetTransmitEntitiesCache = new TargetTransmitEntities?[VisibilitySlotCapacity];
    private readonly List<(TargetTransmitEntities Entities, int TargetSlot, int TargetTeam, bool UseCachedDecisionOnly)> _eligibleTargetsWithEntities = new(VisibilitySlotCapacity - 1);
    private readonly Dictionary<uint, int> _entityHandleIndexCache = new(256);
    private readonly Dictionary<uint, OwnedEntityBucket> _ownedEntityBuckets = new(128);
    private int _entityHandleIndexCacheTick = -1;
    private int _ownedEntityBucketsTick = -1;
    private int _eligibleTargetsWithEntitiesTick = -1;
    private int _roundStartGraceUntilTick;
    private readonly int[] _snapshotTargetSlots = new int[VisibilitySlotCapacity];
    private readonly uint[] _snapshotTargetPawnHandles = new uint[VisibilitySlotCapacity];
    private readonly bool[] _snapshotTargetStationary = new bool[VisibilitySlotCapacity];
    private readonly bool[] _snapshotTargetIsBot = new bool[VisibilitySlotCapacity];
    private readonly int[] _snapshotTargetTeams = new int[VisibilitySlotCapacity];
    private readonly int[] _snapshotStabilizeUntilTickBySlot = new int[VisibilitySlotCapacity];
    private readonly int[,] _viewerRayCountsWorking = new int[VisibilitySlotCapacity, ViewerRayTraceStageCount];
    private readonly int[,] _viewerRayCountsDisplay = new int[VisibilitySlotCapacity, ViewerRayTraceStageCount];
    private readonly int[] _viewerRayCountLastRenderedHashBySlot = new int[VisibilitySlotCapacity];
    private readonly int[] _viewerRayCountLastHudRefreshTickBySlot = new int[VisibilitySlotCapacity];
    private int _viewerRayCounterTick = -1;
    private bool _viewerRayCountsDisplayDirty;
    internal readonly PlayerTransformSnapshot[] SnapshotTransforms = new PlayerTransformSnapshot[VisibilitySlotCapacity];
    internal readonly CBasePlayerPawn?[] SnapshotPawns = new CBasePlayerPawn?[VisibilitySlotCapacity];

    /// <summary>
    /// Registers hooks and initializes dependencies needed by the plugin.
    /// </summary>
    public override void Load(bool hotReload)
    {
        _revealHoldTicks = ConvertRevealHoldSecondsToTicks(S2AWHState.Current.Preload.RevealHoldSeconds);
        _unknownStickyWindowTicks = ConvertUnknownStickySecondsToTicks();
        _collectDebugCounters = S2AWHState.Current.Diagnostics.ShowDebugInfo;
        ResetViewerRayCountOverlayTracking();

        InfoLog(
            "S2AWH is starting up.",
            "The plugin was loaded by the server.",
            "Anti-wallhack protection will be ready shortly."
        );

        RegisterListener<Listeners.OnServerPrecacheResources>((_) => TryInitializeModules("OnServerPrecacheResources"));
        RegisterListener<Listeners.OnMetamodAllPluginsLoaded>(OnMetamodAllPluginsLoaded);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);

        TryInitializeModules("Load");

        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);

        InfoLog(
            "S2AWH is ready.",
            "All hooks are active.",
            "Players behind walls will now be hidden from wallhacks."
        );
    }

    /// <summary>
    /// Releases runtime state and cached module references.
    /// </summary>
    public override void Unload(bool hotReload)
    {
        ClearViewerRayCountOverlays();
        ClearVisibilityCache();

        _cachedLivePlayers.Clear();
        _eligibleTargetsWithEntities.Clear();
        Array.Clear(SnapshotPawns);
        Array.Clear(SnapshotTransforms);
        Array.Clear(_targetTransmitEntitiesCache);

        _losEvaluator = null;
        _predictor = null;
        _transmitFilter = null;
        _ticksSinceInitRetry = 0;
        _hasLoggedWaitingForCapability = false;
    }

    private void OnMetamodAllPluginsLoaded()
    {
        // Re-register so this listener is placed after late-loaded plugins.
        RemoveListener<Listeners.CheckTransmit>(OnCheckTransmit);
        RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
        DebugLog(
            "Hook order updated.",
            "All plugins finished loading, so S2AWH adjusted its priority.",
            "This keeps S2AWH compatible with other plugins."
        );

        TryInitializeModules("OnMetamodAllPluginsLoaded");
    }

    private void OnMapStart(string mapName)
    {
        ClearVisibilityCache();
        DebugLog(
            "New map loaded.",
            $"Map: {mapName}. Old player data was wiped.",
            "Starting fresh for this map."
        );
        bool initialized = TryInitializeModules("OnMapStart");

        if (initialized && S2AWHState.Current.Core.Enabled)
        {
            if (RebuildVisibilityCacheSnapshot())
            {
                DebugLog(
                    "Visibility data is ready.",
                    "S2AWH was already set up when the map started.",
                    "Player hiding is active from the first round."
                );
            }
            else
            {
                DebugLog(
                    "Startup still in progress.",
                    "The server is still loading game data.",
                    "S2AWH will activate once the server is ready."
                );
            }
        }
        else if (initialized)
        {
            DebugLog(
                "Visibility warmup skipped.",
                "S2AWH is currently disabled in config.",
                "Checks will start automatically when enabled."
            );
        }

        QueueStartupDigest(mapName);
    }

    private void OnMapEnd()
    {
        _startupDigestQueued = false;
        ClearViewerRayCountOverlays();
        ClearVisibilityCache();
        DebugLog(
            "Map ended.",
            "Clearing all player data before the next map.",
            "Everything will be rebuilt on the new map."
        );
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        ClearVisibilityCache();
        int nowTick = Server.TickCount;
        _roundStartGraceUntilTick = nowTick + ConvertSecondsToTicks(RoundStartGraceSeconds);
        int stabilizeUntilTick = nowTick + SnapshotStabilizeGraceTicks;
        Array.Fill(_snapshotStabilizeUntilTickBySlot, stabilizeUntilTick);
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null)
        {
            int slot = player.Slot;
            if ((uint)slot < VisibilitySlotCapacity)
            {
                int stabilizeUntilTick = Server.TickCount + SnapshotStabilizeGraceTicks;
                if (_snapshotStabilizeUntilTickBySlot[slot] < stabilizeUntilTick)
                {
                    _snapshotStabilizeUntilTickBySlot[slot] = stabilizeUntilTick;
                }
            }
        }

        return HookResult.Continue;
    }

    private void OnClientDisconnect(int playerSlot)
    {
        if ((uint)playerSlot < VisibilitySlotCapacity)
        {
            _visibilityCache[playerSlot] = null;
            _revealHoldRows[playerSlot] = null;
            _stableDecisionRows[playerSlot] = null;
            _visibleConfirmRows[playerSlot] = null;
            _targetTransmitEntitiesCache[playerSlot] = null;
            SnapshotTransforms[playerSlot] = default;
            SnapshotPawns[playerSlot] = null;
            _liveSlotFlags[playerSlot] = false;
            _snapshotStabilizeUntilTickBySlot[playerSlot] = 0;
            ClearViewerRayCountSlotState(playerSlot);
            RemoveViewerRayCountOverlay(playerSlot);

            for (int i = 0; i < VisibilitySlotCapacity; i++)
            {
                var viewerVisibility = _visibilityCache[i];
                if (viewerVisibility != null && (uint)playerSlot < (uint)viewerVisibility.Known.Length)
                {
                    viewerVisibility.Known[playerSlot] = false;
                    viewerVisibility.Decisions[playerSlot] = false;
                    viewerVisibility.PawnHandles[playerSlot] = 0;
                    viewerVisibility.EvalTicks[playerSlot] = 0;
                }

                var revealHold = _revealHoldRows[i];
                if (revealHold != null && (uint)playerSlot < (uint)revealHold.Known.Length && revealHold.Known[playerSlot])
                {
                    revealHold.Known[playerSlot] = false;
                    revealHold.HoldUntilTick[playerSlot] = 0;
                    revealHold.ActiveCount--;
                    if (revealHold.ActiveCount <= 0) _revealHoldRows[i] = null;
                }

                var stableDecision = _stableDecisionRows[i];
                if (stableDecision != null && (uint)playerSlot < (uint)stableDecision.Known.Length && stableDecision.Known[playerSlot])
                {
                    stableDecision.Known[playerSlot] = false;
                    stableDecision.Decisions[playerSlot] = false;
                    stableDecision.Ticks[playerSlot] = 0;
                    stableDecision.ActiveCount--;
                    if (stableDecision.ActiveCount <= 0) _stableDecisionRows[i] = null;
                }

                var visibleConfirm = _visibleConfirmRows[i];
                if (visibleConfirm != null && (uint)playerSlot < (uint)visibleConfirm.Known.Length && visibleConfirm.Known[playerSlot])
                {
                    visibleConfirm.Known[playerSlot] = false;
                    visibleConfirm.FirstVisibleTick[playerSlot] = 0;
                    visibleConfirm.ActiveCount--;
                    if (visibleConfirm.ActiveCount <= 0) _visibleConfirmRows[i] = null;
                }
            }
        }

        _losEvaluator?.InvalidateTargetSlot(playerSlot);
        _predictor?.InvalidateTargetSlot(playerSlot);

        InvalidateLivePlayersCache();

        DebugLog(
            "Player left the server.",
            $"Slot {playerSlot} was freed and old data for this player was removed.",
            "No stale data remains."
        );
    }

    private bool TryInitializeModules(string source)
    {
        if (_transmitFilter != null)
        {
            return true;
        }

        CRayTraceInterface? rayTrace;
        try
        {
            rayTrace = _rayTraceCapability.Get();
        }
        catch (Exception)
        {
            LogWaitingForRayTrace($"Could not reach RayTrace during {source}.");
            return false;
        }

        if (rayTrace == null)
        {
            LogWaitingForRayTrace($"RayTrace was not found during {source}.");
            return false;
        }

        if (!IsRayTraceOperational(rayTrace))
        {
            LogWaitingForRayTrace(
                $"RayTrace was found during {source}, but it's not responding yet. It may still be loading."
            );
            return false;
        }

        _losEvaluator = new LosEvaluator(rayTrace, RecordViewerRayTraceAttempt);
        _predictor = new PreloadPredictor(rayTrace, RecordViewerRayTraceAttempt);
        _transmitFilter = new TransmitFilter(_losEvaluator, _predictor);

        _ticksSinceInitRetry = 0;
        _hasLoggedWaitingForCapability = false;
        _hasLoggedPlayerScanError = false;
        _hasLoggedFilterEvaluationError = false;
        _hasLoggedWeaponSyncError = false;
        _hasLoggedOwnedEntityScanError = false;

        InfoLog(
            "RayTrace is connected.",
            "S2AWH can now check who can see who.",
            "Wallhack protection is active."
        );
        DebugLog(
            "Setup complete.",
            $"Connected via {source}. RayTrace is responding.",
            "Line-of-sight checks are running."
        );
        DebugLog(
            "First scan starts next tick.",
            "Waiting one tick for the server to finish loading.",
            "Player visibility will update momentarily."
        );
        return true;
    }

    private void LogWaitingForRayTrace(string reason)
    {
        if (_hasLoggedWaitingForCapability)
        {
            return;
        }

        WarnLog(
            "Waiting for RayTrace.",
            "RayTrace might still be loading or is not installed.",
            "S2AWH will keep retrying automatically."
        );
        DebugLog(
            "RayTrace wait details.",
            reason,
            "Retrying in the background."
        );
        _hasLoggedWaitingForCapability = true;
    }

    private static bool IsRayTraceOperational(CRayTraceInterface rayTrace)
    {
        var start = new Vector(0.0f, 0.0f, 0.0f);
        var end = new Vector(1.0f, 1.0f, 1.0f);
        var options = new TraceOptions();

        return rayTrace.TraceEndShape(start, end, null, options, out _);
    }

    private void OnTick()
    {
        if (_transmitFilter == null)
        {
            _ticksSinceInitRetry++;
            if (_ticksSinceInitRetry >= InitRetryIntervalTicks)
            {
                _ticksSinceInitRetry = 0;
                DebugLog(
                    "Retrying RayTrace.",
                    "RayTrace is still not available.",
                    "Checking again now."
                );
                TryInitializeModules("OnTick");
            }
            return;
        }

        BeginViewerRayCountTick(Server.TickCount);

        var config = S2AWHState.Current;
        if (!config.Core.Enabled)
        {
            ClearViewerRayCountOverlays();
            return;
        }

        if (IsRoundStartGraceActive(Server.TickCount))
        {
            ClearViewerRayCountOverlays();
            return;
        }

        // Staggered rebuild: spread viewer evaluations across UpdateFrequencyTicks.
        // Each tick processes ceil(viewers / UpdateFrequencyTicks) viewers.
        // This prevents N^2 pair spikes that cause slow frames at high player counts.
        RebuildVisibilityCacheSnapshot();
        UpdateViewerRayCountOverlays();

        if (!_collectDebugCounters)
        {
            ResetDebugWindowCounters();
            return;
        }

        _ticksSinceLastTransmitReport++;
        if (_ticksSinceLastTransmitReport < DebugSummaryIntervalTicks)
        {
            return;
        }

        if (_transmitCallbacksInWindow > 0 ||
            _transmitHiddenEntitiesInWindow > 0 ||
            _transmitFallbackChecksInWindow > 0 ||
            _transmitRemovalNoEffectInWindow > 0 ||
            _holdRefreshInWindow > 0 ||
            _holdHitKeepAliveInWindow > 0 ||
            _holdExpiredInWindow > 0 ||
            _unknownEvalInWindow > 0 ||
            _unknownStickyHitInWindow > 0 ||
            _unknownHoldHitInWindow > 0 ||
            _unknownFailOpenInWindow > 0 ||
            _unknownFromExceptionInWindow > 0)
        {
            DebugSummaryLog();
        }

        ResetDebugWindowCounters();
    }

    private void QueueStartupDigest(string mapName)
    {
        if (_startupDigestQueued)
        {
            return;
        }

        _startupDigestQueued = true;
        AddTimer(StartupDigestDelaySeconds, () =>
        {
            _startupDigestQueued = false;

            var config = S2AWHState.Current;
            string runtimeState = _transmitFilter != null ? "ready" : "waiting for RayTrace";

            InfoLog(
                "Startup complete.",
                $"Map: {mapName}. S2AWH: {runtimeState}. Update rate: every {config.Core.UpdateFrequencyTicks} tick(s).",
                "Everything is running normally."
            );
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }


    internal void RecordViewerRayTraceAttempt(int viewerSlot, ViewerRayTraceStage stage)
    {
        if ((uint)viewerSlot >= VisibilitySlotCapacity || (int)stage >= ViewerRayTraceStageCount)
        {
            return;
        }

        BeginViewerRayCountTick(Server.TickCount);
        _viewerRayCountsWorking[viewerSlot, (int)stage]++;
    }

    private void BeginViewerRayCountTick(int nowTick)
    {
        if (_viewerRayCounterTick == nowTick)
        {
            return;
        }

        if (_viewerRayCounterTick >= 0)
        {
            Array.Copy(_viewerRayCountsWorking, _viewerRayCountsDisplay, _viewerRayCountsWorking.Length);
            Array.Clear(_viewerRayCountsWorking, 0, _viewerRayCountsWorking.Length);
            _viewerRayCountsDisplayDirty = true;
        }

        _viewerRayCounterTick = nowTick;
    }

    private void UpdateViewerRayCountOverlays()
    {
        var diagnostics = S2AWHState.Current.Diagnostics;
        if (!diagnostics.DrawAmountOfRayNumber)
        {
            ClearViewerRayCountOverlays();
            return;
        }

        bool forceRefresh = _viewerRayCountsDisplayDirty;
        _viewerRayCountsDisplayDirty = false;

        for (int slot = 0; slot < VisibilitySlotCapacity; slot++)
        {
            var pawn = SnapshotPawns[slot];
            if (pawn == null || !pawn.IsValid || !SnapshotTransforms[slot].IsValid)
            {
                RemoveViewerRayCountOverlay(slot);
                continue;
            }

            CCSPlayerController? player = Utilities.GetPlayerFromSlot(slot);
            if (!IsLivePlayer(player))
            {
                RemoveViewerRayCountOverlay(slot);
                continue;
            }

            UpdateViewerRayCountOverlay(slot, forceRefresh);
        }
    }

    private void UpdateViewerRayCountOverlay(int slot, bool forceMessageRefresh)
    {
        CCSPlayerController? player = Utilities.GetPlayerFromSlot(slot);
        if (!IsLivePlayer(player))
        {
            RemoveViewerRayCountOverlay(slot);
            return;
        }

        string countText = BuildViewerRayCountHudHtml(slot);
        int textHash = countText.GetHashCode(StringComparison.Ordinal);
        int nowTick = Server.TickCount;
        bool shouldRefreshHud = forceMessageRefresh
            || _viewerRayCountLastRenderedHashBySlot[slot] != textHash
            || (nowTick - _viewerRayCountLastHudRefreshTickBySlot[slot]) >= ViewerRayCountHudRefreshIntervalTicks;
        if (!shouldRefreshHud)
        {
            return;
        }

        player!.PrintToCenterHtml(countText, 1);
        _viewerRayCountLastRenderedHashBySlot[slot] = textHash;
        _viewerRayCountLastHudRefreshTickBySlot[slot] = nowTick;
    }

    private string BuildViewerRayCountHudHtml(int slot)
    {
        int los = GetViewerRayStageCount(slot, ViewerRayTraceStage.Los);
        int micro = GetViewerRayStageCount(slot, ViewerRayTraceStage.Micro);
        int aim = GetViewerRayStageCount(slot, ViewerRayTraceStage.Aim);
        int preload = GetViewerRayStageCount(slot, ViewerRayTraceStage.Preload);
        int jump = GetViewerRayStageCount(slot, ViewerRayTraceStage.Jump);
        int total = los + micro + aim + preload + jump;
        return string.Concat(
            BuildViewerRayStageHtml(ViewerRayTraceStage.Los, los),
            "&nbsp;",
            BuildViewerRayStageHtml(ViewerRayTraceStage.Micro, micro),
            "&nbsp;",
            BuildViewerRayStageHtml(ViewerRayTraceStage.Aim, aim),
            "&nbsp;",
            BuildViewerRayStageHtml(ViewerRayTraceStage.Preload, preload),
            "&nbsp;",
            BuildViewerRayStageHtml(ViewerRayTraceStage.Jump, jump),
            "&nbsp;",
            BuildViewerRayTotalHtml(total));
    }

    private static string BuildViewerRayStageHtml(ViewerRayTraceStage stage, int count)
    {
        string stageLabel = GetViewerRayTraceStageCompactLabel(stage);
        string color = ToHtmlColorHex(VisibilityGeometry.GetViewerRayCounterColor(stage));
        return $"<b><font color='{color}'>{stageLabel}:{count}</font></b>";
    }

    private static string BuildViewerRayTotalHtml(int total)
    {
        return $"<b><font color='#FFF078'>T:{total}</font></b>";
    }

    private static string GetViewerRayTraceStageCompactLabel(ViewerRayTraceStage stage)
    {
        return stage switch
        {
            ViewerRayTraceStage.Los => "L",
            ViewerRayTraceStage.Micro => "M",
            ViewerRayTraceStage.Aim => "A",
            ViewerRayTraceStage.Preload => "P",
            ViewerRayTraceStage.Jump => "J",
            _ => "R"
        };
    }

    private static string ToHtmlColorHex(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private int GetViewerRayStageCount(int slot, ViewerRayTraceStage stage)
    {
        int stageIndex = (int)stage;
        return _viewerRayCounterTick == Server.TickCount
            ? _viewerRayCountsWorking[slot, stageIndex]
            : _viewerRayCountsDisplay[slot, stageIndex];
    }

    private void ClearViewerRayCountOverlays()
    {
        for (int slot = 0; slot < VisibilitySlotCapacity; slot++)
        {
            RemoveViewerRayCountOverlay(slot);
        }
    }

    private void ResetViewerRayCountOverlayTracking()
    {
        Array.Fill(_viewerRayCountLastRenderedHashBySlot, int.MinValue);
        Array.Fill(_viewerRayCountLastHudRefreshTickBySlot, int.MinValue);
    }

    private void RemoveViewerRayCountOverlay(int slot)
    {
        if ((uint)slot >= VisibilitySlotCapacity)
        {
            return;
        }

        _viewerRayCountLastRenderedHashBySlot[slot] = int.MinValue;
        _viewerRayCountLastHudRefreshTickBySlot[slot] = int.MinValue;
    }

    private void ClearViewerRayCountSlotState(int slot)
    {
        if ((uint)slot >= VisibilitySlotCapacity)
        {
            return;
        }

        for (int stageIndex = 0; stageIndex < ViewerRayTraceStageCount; stageIndex++)
        {
            _viewerRayCountsWorking[slot, stageIndex] = 0;
            _viewerRayCountsDisplay[slot, stageIndex] = 0;
        }

        _viewerRayCountLastRenderedHashBySlot[slot] = int.MinValue;
        _viewerRayCountLastHudRefreshTickBySlot[slot] = int.MinValue;
    }
}
