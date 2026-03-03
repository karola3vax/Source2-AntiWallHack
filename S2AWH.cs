using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using RayTraceAPI;
using System.Drawing;

namespace S2AWH;

[MinimumApiVersion(362)]
public partial class S2AWH : BasePlugin, IPluginConfig<S2AWHConfig>
{
    private const int VisibilitySlotCapacity = 65;
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
    private const float ViewerRayCountTextHeight = 18.0f;
    private const float ViewerRayCountTextWorldUnitsPerPx = 0.18f;
    private const float ViewerRayCountTextFontSize = 22.0f;
    private static readonly QAngle ViewerRayCountTextAngles = new(0.0f, 0.0f, 0.0f);
    private static readonly Vector ViewerRayCountTextVelocity = new(0.0f, 0.0f, 0.0f);
    private static readonly Color ViewerRayCountTextColor = Color.FromArgb(255, 255, 240, 120);

    /// <summary>
    /// Holds the set of entity handles (pawn + weapons) belonging to a single target player,
    /// cached per tick to avoid repeated resolution during transmit filtering.
    /// </summary>
    private sealed class TargetTransmitEntities
    {
        public int LastFullRefreshTick = -1;
        public int SanitizeTick = -1;
        public uint PawnHandleRaw = uint.MaxValue;
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
    private int _staggeredViewerOffset;
    private int _ticksSinceInitRetry;
    private bool _hasLoggedWaitingForCapability;
    private bool _hasLoggedGlobalsNotReady;
    private bool _hasLoggedPlayerScanError;
    private bool _hasLoggedFilterEvaluationError;
    private bool _hasLoggedWeaponSyncError;
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
    private readonly List<(TargetTransmitEntities Entities, int TargetSlot, int TargetTeam)> _eligibleTargetsWithEntities = new(VisibilitySlotCapacity - 1);
    private readonly Dictionary<uint, int> _entityHandleIndexCache = new(256);
    private int _entityHandleIndexCacheTick = -1;
    private int _eligibleTargetsWithEntitiesTick = -1;
    private int _roundStartGraceUntilTick;
    private readonly int[] _snapshotTargetSlots = new int[VisibilitySlotCapacity];
    private readonly uint[] _snapshotTargetPawnHandles = new uint[VisibilitySlotCapacity];
    private readonly bool[] _snapshotTargetStationary = new bool[VisibilitySlotCapacity];
    private readonly bool[] _snapshotTargetIsBot = new bool[VisibilitySlotCapacity];
    private readonly int[] _snapshotTargetTeams = new int[VisibilitySlotCapacity];
    private readonly int[] _snapshotStabilizeUntilTickBySlot = new int[VisibilitySlotCapacity];
    private readonly int[] _viewerRayCountsWorking = new int[VisibilitySlotCapacity];
    private readonly int[] _viewerRayCountsDisplay = new int[VisibilitySlotCapacity];
    private readonly int[] _viewerRayCountLastRendered = new int[VisibilitySlotCapacity];
    private readonly CPointWorldText?[] _viewerRayCountTextBySlot = new CPointWorldText?[VisibilitySlotCapacity];
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
        ClearVisibilityCache();
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
            _targetTransmitEntitiesCache[playerSlot] = null;
            SnapshotTransforms[playerSlot] = default;
            SnapshotPawns[playerSlot] = null;
            _liveSlotFlags[playerSlot] = false;
            _snapshotStabilizeUntilTickBySlot[playerSlot] = 0;
            _viewerRayCountsWorking[playerSlot] = 0;
            _viewerRayCountsDisplay[playerSlot] = 0;
            _viewerRayCountLastRendered[playerSlot] = int.MinValue;
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

    private static bool IsLivePlayer(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid || player.Connected != PlayerConnectedState.PlayerConnected)
        {
            return false;
        }

        return player.PawnIsAlive;
    }

    private void ClearVisibilityCache()
    {
        Array.Clear(_visibilityCache, 0, _visibilityCache.Length);
        Array.Clear(_revealHoldRows, 0, _revealHoldRows.Length);
        Array.Clear(_stableDecisionRows, 0, _stableDecisionRows.Length);
        Array.Clear(_targetTransmitEntitiesCache, 0, _targetTransmitEntitiesCache.Length);
        Array.Clear(_snapshotStabilizeUntilTickBySlot, 0, _snapshotStabilizeUntilTickBySlot.Length);
        _roundStartGraceUntilTick = 0;
        _losEvaluator?.ClearCaches();
        _predictor?.ClearCaches();
        InvalidateLivePlayersCache();
        _cachedLivePlayers.Clear();
        _eligibleTargetsWithEntities.Clear();
        Array.Clear(_liveSlotFlags, 0, _liveSlotFlags.Length);
        Array.Clear(_viewerRayCountsWorking, 0, _viewerRayCountsWorking.Length);
        Array.Clear(_viewerRayCountsDisplay, 0, _viewerRayCountsDisplay.Length);
        Array.Fill(_viewerRayCountLastRendered, int.MinValue);
        Array.Clear(SnapshotTransforms, 0, SnapshotTransforms.Length);
        Array.Clear(SnapshotPawns, 0, SnapshotPawns.Length);
        ClearViewerRayCountOverlays();
        _viewerRayCounterTick = -1;
        _viewerRayCountsDisplayDirty = false;
        _staggeredViewerOffset = 0;
        _hasLoggedGlobalsNotReady = false;
        _hasLoggedPlayerScanError = false;
        _hasLoggedFilterEvaluationError = false;
        _hasLoggedWeaponSyncError = false;
        _lastDebugCachePlayerCount = 0;
        ResetDebugWindowCounters();
    }

    private static bool IsNearWorldOrigin(float x, float y, float z)
    {
        return MathF.Abs(x) <= SnapshotZeroOriginEpsilon &&
               MathF.Abs(y) <= SnapshotZeroOriginEpsilon &&
               MathF.Abs(z) <= SnapshotZeroOriginEpsilon;
    }

    internal void RecordViewerRayTraceAttempt(int viewerSlot)
    {
        if ((uint)viewerSlot >= VisibilitySlotCapacity)
        {
            return;
        }

        BeginViewerRayCountTick(Server.TickCount);
        _viewerRayCountsWorking[viewerSlot]++;
    }

    private void BeginViewerRayCountTick(int nowTick)
    {
        if (_viewerRayCounterTick == nowTick)
        {
            return;
        }

        if (_viewerRayCounterTick >= 0)
        {
            Array.Copy(_viewerRayCountsWorking, _viewerRayCountsDisplay, VisibilitySlotCapacity);
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

            bool allowViewerType = player!.IsBot
                ? diagnostics.DrawDebugTraceBeamsForBots
                : diagnostics.DrawDebugTraceBeamsForHumans;
            if (!allowViewerType)
            {
                RemoveViewerRayCountOverlay(slot);
                continue;
            }

            int rayCount = _viewerRayCounterTick == Server.TickCount
                ? _viewerRayCountsWorking[slot]
                : _viewerRayCountsDisplay[slot];
            UpdateViewerRayCountOverlay(slot, rayCount, forceRefresh);
        }
    }

    private void UpdateViewerRayCountOverlay(int slot, int rayCount, bool forceMessageRefresh)
    {
        ref var snapshot = ref SnapshotTransforms[slot];
        string countText = rayCount.ToString();
        CPointWorldText? textEntity = _viewerRayCountTextBySlot[slot];
        bool shouldRecreate = forceMessageRefresh || _viewerRayCountLastRendered[slot] != rayCount;
        if (shouldRecreate && textEntity != null && textEntity.IsValid)
        {
            textEntity.Remove();
            textEntity = null;
            _viewerRayCountTextBySlot[slot] = null;
        }

        if (textEntity == null || !textEntity.IsValid)
        {
            textEntity = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext");
            if (textEntity == null || !textEntity.IsValid)
            {
                _viewerRayCountTextBySlot[slot] = null;
                return;
            }

            textEntity.Enabled = true;
            textEntity.Fullbright = true;
            textEntity.DrawBackground = false;
            textEntity.WorldUnitsPerPx = ViewerRayCountTextWorldUnitsPerPx;
            textEntity.FontSize = ViewerRayCountTextFontSize;
            textEntity.DepthOffset = 0.0f;
            textEntity.Color = ViewerRayCountTextColor;
            textEntity.JustifyHorizontal = PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_CENTER;
            textEntity.JustifyVertical = PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_BOTTOM;
            textEntity.ReorientMode = PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_AROUND_UP;
            textEntity.FontName = "Arial";
            textEntity.MessageText = countText;
            textEntity.Teleport(
                new Vector(snapshot.EyeX, snapshot.EyeY, snapshot.EyeZ + ViewerRayCountTextHeight),
                ViewerRayCountTextAngles,
                ViewerRayCountTextVelocity);
            textEntity.DispatchSpawn();
            _viewerRayCountLastRendered[slot] = rayCount;
            _viewerRayCountTextBySlot[slot] = textEntity;
            return;
        }

        textEntity.Teleport(
            new Vector(snapshot.EyeX, snapshot.EyeY, snapshot.EyeZ + ViewerRayCountTextHeight),
            ViewerRayCountTextAngles,
            ViewerRayCountTextVelocity);

        if (forceMessageRefresh || textEntity.MessageText != countText)
        {
            textEntity.MessageText = countText;
        }

        _viewerRayCountLastRendered[slot] = rayCount;
    }

    private void ClearViewerRayCountOverlays()
    {
        for (int slot = 0; slot < VisibilitySlotCapacity; slot++)
        {
            RemoveViewerRayCountOverlay(slot);
        }
    }

    private void RemoveViewerRayCountOverlay(int slot)
    {
        if ((uint)slot >= VisibilitySlotCapacity)
        {
            return;
        }

        CPointWorldText? textEntity = _viewerRayCountTextBySlot[slot];
        if (textEntity != null && textEntity.IsValid)
        {
            textEntity.Remove();
        }

        _viewerRayCountTextBySlot[slot] = null;
        _viewerRayCountLastRendered[slot] = int.MinValue;
    }

    private static bool TryGetLocalBoundsCandidate(
        Vector minsWorldOrLocal,
        Vector maxsWorldOrLocal,
        float originX,
        float originY,
        float originZ,
        float referenceMinX,
        float referenceMinY,
        float referenceMinZ,
        float referenceMaxX,
        float referenceMaxY,
        float referenceMaxZ,
        out float outMinX,
        out float outMinY,
        out float outMinZ,
        out float outMaxX,
        out float outMaxY,
        out float outMaxZ)
    {
        outMinX = 0; outMinY = 0; outMinZ = 0; outMaxX = 0; outMaxY = 0; outMaxZ = 0;

        if (minsWorldOrLocal == null || maxsWorldOrLocal == null)
            return false;

        float rawMinX = minsWorldOrLocal.X;
        float rawMinY = minsWorldOrLocal.Y;
        float rawMinZ = minsWorldOrLocal.Z;
        float rawMaxX = maxsWorldOrLocal.X;
        float rawMaxY = maxsWorldOrLocal.Y;
        float rawMaxZ = maxsWorldOrLocal.Z;

        bool hasRawLocal = TryScoreLocalBoundsCandidate(
            rawMinX,
            rawMinY,
            rawMinZ,
            rawMaxX,
            rawMaxY,
            rawMaxZ,
            referenceMinX,
            referenceMinY,
            referenceMinZ,
            referenceMaxX,
            referenceMaxY,
            referenceMaxZ,
            out float rawLocalScore);

        bool hasWorldShifted = TryScoreLocalBoundsCandidate(
            rawMinX - originX,
            rawMinY - originY,
            rawMinZ - originZ,
            rawMaxX - originX,
            rawMaxY - originY,
            rawMaxZ - originZ,
            referenceMinX,
            referenceMinY,
            referenceMinZ,
            referenceMaxX,
            referenceMaxY,
            referenceMaxZ,
            out float worldShiftedScore);

        if (!hasRawLocal && !hasWorldShifted)
        {
            return false;
        }

        if (hasRawLocal && (!hasWorldShifted || rawLocalScore <= worldShiftedScore))
        {
            outMinX = rawMinX;
            outMinY = rawMinY;
            outMinZ = rawMinZ;
            outMaxX = rawMaxX;
            outMaxY = rawMaxY;
            outMaxZ = rawMaxZ;
            return true;
        }

        outMinX = rawMinX - originX;
        outMinY = rawMinY - originY;
        outMinZ = rawMinZ - originZ;
        outMaxX = rawMaxX - originX;
        outMaxY = rawMaxY - originY;
        outMaxZ = rawMaxZ - originZ;
        return true;
    }

    private static bool TryScoreLocalBoundsCandidate(
        float minX,
        float minY,
        float minZ,
        float maxX,
        float maxY,
        float maxZ,
        float referenceMinX,
        float referenceMinY,
        float referenceMinZ,
        float referenceMaxX,
        float referenceMaxY,
        float referenceMaxZ,
        out float score)
    {
        score = 0.0f;

        float extentX = maxX - minX;
        float extentY = maxY - minY;
        float extentZ = maxZ - minZ;
        if (extentX <= 0.0f || extentY <= 0.0f || extentZ <= 0.0f ||
            extentX > MaxBoundsExtentUnits || extentY > MaxBoundsExtentUnits || extentZ > MaxBoundsExtentUnits)
        {
            return false;
        }

        if (MathF.Abs(minX) > MaxLocalBoundsCoordinateUnits || MathF.Abs(maxX) > MaxLocalBoundsCoordinateUnits ||
            MathF.Abs(minY) > MaxLocalBoundsCoordinateUnits || MathF.Abs(maxY) > MaxLocalBoundsCoordinateUnits ||
            MathF.Abs(minZ) > MaxLocalBoundsCoordinateUnits || MathF.Abs(maxZ) > MaxLocalBoundsCoordinateUnits)
        {
            return false;
        }

        float centerX = (minX + maxX) * 0.5f;
        float centerY = (minY + maxY) * 0.5f;
        float centerZ = (minZ + maxZ) * 0.5f;
        if (MathF.Abs(centerX) > MaxLocalHorizontalCenterOffset ||
            MathF.Abs(centerY) > MaxLocalHorizontalCenterOffset ||
            centerZ < MinLocalVerticalCenter ||
            centerZ > MaxLocalVerticalCenter)
        {
            return false;
        }

        float referenceExtentX = referenceMaxX - referenceMinX;
        float referenceExtentY = referenceMaxY - referenceMinY;
        float referenceExtentZ = referenceMaxZ - referenceMinZ;
        float referenceCenterX = (referenceMinX + referenceMaxX) * 0.5f;
        float referenceCenterY = (referenceMinY + referenceMaxY) * 0.5f;
        float referenceCenterZ = (referenceMinZ + referenceMaxZ) * 0.5f;

        float centerDelta =
            MathF.Abs(centerX - referenceCenterX) +
            MathF.Abs(centerY - referenceCenterY) +
            MathF.Abs(centerZ - referenceCenterZ);
        float extentDelta =
            MathF.Abs(extentX - referenceExtentX) +
            MathF.Abs(extentY - referenceExtentY) +
            MathF.Abs(extentZ - referenceExtentZ);
        float containmentShrink =
            MathF.Max(0.0f, minX - referenceMinX) +
            MathF.Max(0.0f, minY - referenceMinY) +
            MathF.Max(0.0f, minZ - referenceMinZ) +
            MathF.Max(0.0f, referenceMaxX - maxX) +
            MathF.Max(0.0f, referenceMaxY - maxY) +
            MathF.Max(0.0f, referenceMaxZ - maxZ);
        if (containmentShrink > MaxBoundsContainmentShrinkUnits)
        {
            return false;
        }

        float absoluteCoordinatePenalty =
            MathF.Abs(minX) + MathF.Abs(minY) + MathF.Abs(minZ) +
            MathF.Abs(maxX) + MathF.Abs(maxY) + MathF.Abs(maxZ);

        score = (centerDelta * 4.0f) + extentDelta + (containmentShrink * 8.0f) + (absoluteCoordinatePenalty * 0.01f);
        return true;
    }

    private static int ConvertSecondsToTicks(float seconds)
    {
        if (seconds <= 0.0f)
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Ceiling(seconds / Server.TickInterval));
    }

    private bool IsRoundStartGraceActive(int nowTick)
    {
        return nowTick < _roundStartGraceUntilTick;
    }

    private void InvalidateLivePlayersCache()
    {
        _cachedLivePlayersTick = -1;
        _cachedLivePlayersValid = false;
        Array.Clear(_liveSlotFlags, 0, _liveSlotFlags.Length);
        _entityHandleIndexCacheTick = -1;
        _entityHandleIndexCache.Clear();
        _eligibleTargetsWithEntitiesTick = -1;
    }

    private void ResetDebugWindowCounters()
    {
        _ticksSinceLastTransmitReport = 0;
        _transmitCallbacksInWindow = 0;
        _transmitHiddenEntitiesInWindow = 0;
        _transmitFallbackChecksInWindow = 0;
        _transmitRemovalNoEffectInWindow = 0;
        _holdRefreshInWindow = 0;
        _holdHitKeepAliveInWindow = 0;
        _holdExpiredInWindow = 0;
        _unknownEvalInWindow = 0;
        _unknownStickyHitInWindow = 0;
        _unknownHoldHitInWindow = 0;
        _unknownFailOpenInWindow = 0;
        _unknownFromExceptionInWindow = 0;
    }


    private bool RebuildVisibilityCacheSnapshot()
    {
        if (_transmitFilter == null)
        {
            return false;
        }

        int nowTick = Server.TickCount;
        if (!TryGetLivePlayers(nowTick, out var validPlayers))
        {
            return false;
        }

        var config = S2AWHState.Current;
        int playerCount = validPlayers.Count;
        if (playerCount > VisibilitySlotCapacity)
        {
            playerCount = VisibilitySlotCapacity;
        }

        // Live slots are rewritten below and dead slots are excluded by _liveSlotFlags/validPlayers.
        // Avoid full-array clears every rebuild tick to keep the snapshot pass cheaper.

        // Snapshot per-target metadata once per rebuild pass to avoid O(N^2) property reads.
        for (int i = 0; i < playerCount; i++)
        {
            var target = validPlayers[i];
            int slot = target.Slot;
            _snapshotTargetSlots[i] = slot;
            _snapshotTargetPawnHandles[i] = 0;
            _snapshotTargetStationary[i] = false;
            _snapshotTargetIsBot[i] = target.IsBot;
            _snapshotTargetTeams[i] = target.TeamNum;

            var targetCsPawn = target.PlayerPawn.Value;
            var targetPawnEntity = (CBasePlayerPawn?)targetCsPawn ?? target.Pawn.Value;
            if ((uint)slot < VisibilitySlotCapacity)
            {
                ref var t = ref SnapshotTransforms[slot];
                t = default;

                if (targetPawnEntity != null && targetPawnEntity.IsValid)
                {
                    var origin = targetPawnEntity.AbsOrigin;
                    if (origin == null)
                    {
                        SnapshotPawns[slot] = null;
                        continue;
                    }

                    t.OriginX = origin.X;
                    t.OriginY = origin.Y;
                    t.OriginZ = origin.Z;

                    if (nowTick < _snapshotStabilizeUntilTickBySlot[slot] &&
                        IsNearWorldOrigin(t.OriginX, t.OriginY, t.OriginZ))
                    {
                        SnapshotPawns[slot] = null;
                        continue;
                    }

                    SnapshotPawns[slot] = targetPawnEntity;
                    _snapshotTargetPawnHandles[i] = targetPawnEntity.EntityHandle.Raw;

                    var tVel = targetPawnEntity.AbsVelocity;
                    if (tVel != null)
                    {
                        t.VelocityX = tVel.X;
                        t.VelocityY = tVel.Y;
                        t.VelocityZ = tVel.Z;
                    }
                    else
                    {
                        t.VelocityX = 0.0f;
                        t.VelocityY = 0.0f;
                        t.VelocityZ = 0.0f;
                    }

                    _snapshotTargetStationary[i] =
                        (t.VelocityX * t.VelocityX + t.VelocityY * t.VelocityY + t.VelocityZ * t.VelocityZ) < StationarySpeedSqThreshold;

                    var collision = targetPawnEntity.Collision;
                    if (collision?.Mins == null || collision.Maxs == null)
                    {
                        SnapshotPawns[slot] = null;
                        _snapshotTargetPawnHandles[i] = 0;
                        continue;
                    }

                    var mins = collision.Mins;
                    var maxs = collision.Maxs;
                    float minsX = mins.X;
                    float minsY = mins.Y;
                    float minsZ = mins.Z;
                    float maxsX = maxs.X;
                    float maxsY = maxs.Y;
                    float maxsZ = maxs.Z;

                    float mergedMinX = minsX;
                    float mergedMinY = minsY;
                    float mergedMinZ = minsZ;
                    float mergedMaxX = maxsX;
                    float mergedMaxY = maxsY;
                    float mergedMaxZ = maxsZ;
                    float referenceMinX = minsX;
                    float referenceMinY = minsY;
                    float referenceMinZ = minsZ;
                    float referenceMaxX = maxsX;
                    float referenceMaxY = maxsY;
                    float referenceMaxZ = maxsZ;

                    var surroundingMins = collision.SurroundingMins;
                    var surroundingMaxs = collision.SurroundingMaxs;
                    if (TryGetLocalBoundsCandidate(
                            surroundingMins,
                            surroundingMaxs,
                            t.OriginX,
                            t.OriginY,
                            t.OriginZ,
                            referenceMinX,
                            referenceMinY,
                            referenceMinZ,
                            referenceMaxX,
                            referenceMaxY,
                            referenceMaxZ,
                            out float surroundingLocalMinX,
                            out float surroundingLocalMinY,
                            out float surroundingLocalMinZ,
                            out float surroundingLocalMaxX,
                            out float surroundingLocalMaxY,
                            out float surroundingLocalMaxZ))
                    {
                        mergedMinX = MathF.Min(mergedMinX, surroundingLocalMinX);
                        mergedMinY = MathF.Min(mergedMinY, surroundingLocalMinY);
                        mergedMinZ = MathF.Min(mergedMinZ, surroundingLocalMinZ);
                        mergedMaxX = MathF.Max(mergedMaxX, surroundingLocalMaxX);
                        mergedMaxY = MathF.Max(mergedMaxY, surroundingLocalMaxY);
                        mergedMaxZ = MathF.Max(mergedMaxZ, surroundingLocalMaxZ);
                    }

                    var specifiedSurroundingMins = collision.SpecifiedSurroundingMins;
                    var specifiedSurroundingMaxs = collision.SpecifiedSurroundingMaxs;
                    if (TryGetLocalBoundsCandidate(
                            specifiedSurroundingMins,
                            specifiedSurroundingMaxs,
                            t.OriginX,
                            t.OriginY,
                            t.OriginZ,
                            referenceMinX,
                            referenceMinY,
                            referenceMinZ,
                            referenceMaxX,
                            referenceMaxY,
                            referenceMaxZ,
                            out float specifiedLocalMinX,
                            out float specifiedLocalMinY,
                            out float specifiedLocalMinZ,
                            out float specifiedLocalMaxX,
                            out float specifiedLocalMaxY,
                            out float specifiedLocalMaxZ))
                    {
                        mergedMinX = MathF.Min(mergedMinX, specifiedLocalMinX);
                        mergedMinY = MathF.Min(mergedMinY, specifiedLocalMinY);
                        mergedMinZ = MathF.Min(mergedMinZ, specifiedLocalMinZ);
                        mergedMaxX = MathF.Max(mergedMaxX, specifiedLocalMaxX);
                        mergedMaxY = MathF.Max(mergedMaxY, specifiedLocalMaxY);
                        mergedMaxZ = MathF.Max(mergedMaxZ, specifiedLocalMaxZ);
                    }

                    if (targetPawnEntity is CBaseModelEntity targetModelEntity)
                    {
                        float hitboxExpandRadius = targetModelEntity.CHitboxComponent.BoundsExpandRadius;
                        if (hitboxExpandRadius > 0.0f && hitboxExpandRadius <= 32.0f)
                        {
                            mergedMinX -= hitboxExpandRadius;
                            mergedMinY -= hitboxExpandRadius;
                            mergedMinZ -= hitboxExpandRadius;
                            mergedMaxX += hitboxExpandRadius;
                            mergedMaxY += hitboxExpandRadius;
                            mergedMaxZ += hitboxExpandRadius;
                        }
                    }

                    minsX = mergedMinX;
                    minsY = mergedMinY;
                    minsZ = mergedMinZ;
                    maxsX = mergedMaxX;
                    maxsY = mergedMaxY;
                    maxsZ = mergedMaxZ;

                    t.MinsX = minsX;
                    t.MinsY = minsY;
                    t.MinsZ = minsZ;
                    t.MaxsX = maxsX;
                    t.MaxsY = maxsY;
                    t.MaxsZ = maxsZ;
                    t.CenterX = (minsX + maxsX) * 0.5f;
                    t.CenterY = (minsY + maxsY) * 0.5f;
                    t.CenterZ = (minsZ + maxsZ) * 0.5f;

                    var viewOffset = targetPawnEntity.ViewOffset;
                    if (viewOffset != null)
                    {
                        t.ViewOffsetX = viewOffset.X;
                        t.ViewOffsetY = viewOffset.Y;
                        t.ViewOffsetZ = viewOffset.Z;
                    }
                    else
                    {
                        t.ViewOffsetX = 0;
                        t.ViewOffsetY = 0;
                        t.ViewOffsetZ = 64.0f;
                    }

                    t.EyeAnglesPitch = 0.0f;
                    t.EyeAnglesYaw = 0.0f;
                    t.FovNormalX = 1.0f;
                    t.FovNormalY = 0.0f;
                    t.FovNormalZ = 0.0f;

                    var angles = targetCsPawn?.EyeAngles;
                    if (angles != null)
                    {
                        t.EyeAnglesPitch = angles.X;
                        t.EyeAnglesYaw = angles.Y;

                        float pitchRad = angles.X * MathF.PI / 180.0f;
                        float yawRad = angles.Y * MathF.PI / 180.0f;
                        (float sinPitch, float cosPitch) = MathF.SinCos(pitchRad);
                        (float sinYaw, float cosYaw) = MathF.SinCos(yawRad);

                        t.FovNormalX = cosPitch * cosYaw;
                        t.FovNormalY = cosPitch * sinYaw;
                        t.FovNormalZ = -sinPitch;
                    }

                    // Fallback eye position: Origin + ViewOffset
                    t.EyeX = t.OriginX + t.ViewOffsetX;
                    t.EyeY = t.OriginY + t.ViewOffsetY;
                    t.EyeZ = t.OriginZ + t.ViewOffsetZ;
                    t.IsValid = true;
                }
                else
                {
                    SnapshotPawns[slot] = null;
                }
            }
        }

        // Count eligible viewers (respects BotsDoLOS setting).
        int eligibleViewerCount = 0;
        for (int i = 0; i < playerCount; i++)
        {
            if (!validPlayers[i].IsBot || config.Visibility.BotsDoLOS)
            {
                eligibleViewerCount++;
            }
        }

        if (eligibleViewerCount == 0)
        {
            _staggeredViewerOffset = 0;
            return true;
        }

        // Staggered batching: spread viewers across UpdateFrequencyTicks.
        // For 20 eligible viewers with UpdateFrequencyTicks=2: process 10 per tick.
        int updateTicks = Math.Max(1, config.Core.UpdateFrequencyTicks);
        int viewersPerTick = (eligibleViewerCount + updateTicks - 1) / updateTicks;

        if (_staggeredViewerOffset >= eligibleViewerCount)
        {
            _staggeredViewerOffset = 0;
        }

        int processedEligible = 0;
        int currentEligibleIndex = 0;

        for (int viewerIndex = 0; viewerIndex < playerCount && processedEligible < viewersPerTick; viewerIndex++)
        {
            var viewer = validPlayers[viewerIndex];
            bool viewerIsBot = viewer.IsBot;
            if (viewerIsBot && !config.Visibility.BotsDoLOS)
            {
                continue;
            }

            if (currentEligibleIndex < _staggeredViewerOffset)
            {
                currentEligibleIndex++;
                continue;
            }

            currentEligibleIndex++;
            processedEligible++;

            int viewerSlot = viewer.Slot;
            ViewerVisibilityRow? visibilityByTargetSlot = null;
            if ((uint)viewerSlot < VisibilitySlotCapacity)
            {
                visibilityByTargetSlot = _visibilityCache[viewerSlot];
                if (visibilityByTargetSlot == null)
                {
                    visibilityByTargetSlot = new ViewerVisibilityRow();
                    _visibilityCache[viewerSlot] = visibilityByTargetSlot;
                }
            }
            else
            {
                continue; // invalid slot
            }

            // Stationary-Visible optimization: if the viewer isn't moving, we can reuse
            // cached Visible decisions for targets that also aren't moving. This skips
            // expensive LOS evaluation entirely for stationary pairs (buy time, holds).
            // Safe: keeping Visible is the optimistic direction (never hides visible players).
            ref var viewerSnapshot = ref SnapshotTransforms[viewerSlot];
            bool viewerStationary =
                viewerSnapshot.IsValid &&
                (viewerSnapshot.VelocityX * viewerSnapshot.VelocityX +
                 viewerSnapshot.VelocityY * viewerSnapshot.VelocityY +
                 viewerSnapshot.VelocityZ * viewerSnapshot.VelocityZ) < StationarySpeedSqThreshold;
            int viewerTeam = viewer.TeamNum;

            for (int targetIndex = 0; targetIndex < playerCount; targetIndex++)
            {
                if (targetIndex == viewerIndex)
                {
                    continue;
                }

                int targetSlot = _snapshotTargetSlots[targetIndex];
                if ((uint)targetSlot < (uint)visibilityByTargetSlot.Decisions.Length)
                {
                    uint currentPawnHandle = _snapshotTargetPawnHandles[targetIndex];

                    // Always-transmit fast paths that do not require LOS/predictor work.
                    if (!config.Visibility.IncludeBots && _snapshotTargetIsBot[targetIndex])
                    {
                        visibilityByTargetSlot.Decisions[targetSlot] = true;
                        visibilityByTargetSlot.Known[targetSlot] = true;
                        visibilityByTargetSlot.PawnHandles[targetSlot] = currentPawnHandle;
                        visibilityByTargetSlot.EvalTicks[targetSlot] = nowTick;
                        continue;
                    }

                    if (!config.Visibility.IncludeTeammates && _snapshotTargetTeams[targetIndex] == viewerTeam)
                    {
                        visibilityByTargetSlot.Decisions[targetSlot] = true;
                        visibilityByTargetSlot.Known[targetSlot] = true;
                        visibilityByTargetSlot.PawnHandles[targetSlot] = currentPawnHandle;
                        visibilityByTargetSlot.EvalTicks[targetSlot] = nowTick;
                        continue;
                    }

                    // Reuse Visible decision if both are stationary and same pawn.
                    // PawnHandle guard prevents stale reuse across player slot changes.
                    if (viewerStationary &&
                        visibilityByTargetSlot.Known[targetSlot] &&
                        visibilityByTargetSlot.Decisions[targetSlot] &&
                        currentPawnHandle != 0 &&
                        visibilityByTargetSlot.PawnHandles[targetSlot] == currentPawnHandle)
                    {
                        if (_snapshotTargetStationary[targetIndex])
                        {
                            continue; // Reuse cached Visible - both stationary, same pawn.
                        }
                    }

                    if (visibilityByTargetSlot.Known[targetSlot] &&
                        visibilityByTargetSlot.EvalTicks[targetSlot] == nowTick &&
                        currentPawnHandle != 0 &&
                        visibilityByTargetSlot.PawnHandles[targetSlot] == currentPawnHandle)
                    {
                        continue; // Reuse decision already computed earlier this tick (e.g. transmit fallback path).
                    }

                    VisibilityEval visibilityEval = EvaluateVisibilitySafe(
                        viewerSlot,
                        targetSlot,
                        viewerIsBot,
                        config,
                        nowTick,
                        "cache rebuild");
                    visibilityByTargetSlot.Decisions[targetSlot] = ResolveTransmitWithMemory(viewerSlot, targetSlot, visibilityEval, nowTick);
                    visibilityByTargetSlot.Known[targetSlot] = true;
                    visibilityByTargetSlot.PawnHandles[targetSlot] = currentPawnHandle;
                    visibilityByTargetSlot.EvalTicks[targetSlot] = nowTick;
                }
            }
        }

        // Check if this batch completed a full cycle through all viewers.
        bool isFullCycleComplete = (_staggeredViewerOffset + processedEligible) >= eligibleViewerCount;

        if (isFullCycleComplete)
        {
            // Full cycle complete: purge stale viewer rows from cached state.
            PurgeInactiveViewerRows();
            _staggeredViewerOffset = 0;
        }
        else
        {
            _staggeredViewerOffset += processedEligible;
        }

        if (isFullCycleComplete && _collectDebugCounters && _lastDebugCachePlayerCount != validPlayers.Count)
        {
            DebugLog(
                "Player count changed.",
                $"{validPlayers.Count} alive players, checking {viewersPerTick} per tick.",
                "Workload is spread evenly across ticks."
            );
            _lastDebugCachePlayerCount = validPlayers.Count;
        }

        return true;
    }


    private VisibilityEval EvaluateVisibilitySafe(
        int viewerSlot,
        int targetSlot,
        bool viewerIsBot,
        S2AWHConfig config,
        int nowTick,
        string phase)
    {
        if (_transmitFilter == null)
        {
            return VisibilityEval.UnknownTransient;
        }

        try
        {
            return _transmitFilter.EvaluateVisibility(
                viewerSlot,
                targetSlot,
                viewerIsBot,
                nowTick,
                config,
                SnapshotTransforms,
                SnapshotPawns);
        }
        catch (Exception ex)
        {
            if (_collectDebugCounters)
            {
                _unknownFromExceptionInWindow++;
            }

            if (!_hasLoggedFilterEvaluationError)
            {
                WarnLog(
                    "A visibility check had an error.",
                    "A temporary issue occurred while checking if a player is visible.",
                    "S2AWH handled it safely - no crash, no impact."
                );
                DebugLog(
                    "Visibility error detail.",
                    $"Phase: {phase}. Pair: {viewerSlot}->{targetSlot}. Error: {ex.Message}",
                    "This message only shows once."
                );
                _hasLoggedFilterEvaluationError = true;
            }

            return VisibilityEval.UnknownTransient;
        }
    }

    private bool TryGetLivePlayers(int nowTick, out List<CCSPlayerController> livePlayers)
    {
        if (_cachedLivePlayersTick == nowTick)
        {
            livePlayers = _cachedLivePlayers;
            return _cachedLivePlayersValid;
        }

        _cachedLivePlayersTick = nowTick;
        _cachedLivePlayers.Clear();
        _cachedLivePlayersValid = false;

        try
        {
            Array.Clear(_liveSlotFlags, 0, _liveSlotFlags.Length);

            int maxPlayers = Math.Clamp(Server.MaxPlayers, 0, VisibilitySlotCapacity - 1);
            for (int slot = 0; slot < maxPlayers; slot++)
            {
                var player = Utilities.GetPlayerFromSlot(slot);
                if (IsLivePlayer(player))
                {
                    _cachedLivePlayers.Add(player!);
                    _liveSlotFlags[slot] = true;
                }
            }

            _hasLoggedGlobalsNotReady = false;
            _hasLoggedPlayerScanError = false;
            _cachedLivePlayersValid = true;
            livePlayers = _cachedLivePlayers;
            return _cachedLivePlayersValid;
        }
        catch (NativeException ex) when (ex.Message.Contains("Global Variables not initialized yet.", StringComparison.OrdinalIgnoreCase))
        {
            if (!_hasLoggedGlobalsNotReady)
            {
                WarnLog(
                    "Server is still loading.",
                    "Player data isn't available yet.",
                    "S2AWH will start automatically once the server is ready."
                );
                _hasLoggedGlobalsNotReady = true;
            }

            _cachedLivePlayers.Clear();
            _cachedLivePlayersValid = false;
            Array.Clear(_liveSlotFlags, 0, _liveSlotFlags.Length);
            livePlayers = _cachedLivePlayers;
            return false;
        }
        catch (Exception ex)
        {
            if (!_hasLoggedPlayerScanError)
            {
                WarnLog(
                    "Could not read player list.",
                    "A temporary issue prevented reading who is alive.",
                    "S2AWH skipped this tick and will retry."
                );
                DebugLog(
                    "Player scan error detail.",
                    $"Error: {ex.Message}",
                    "This message only shows once."
                );
                _hasLoggedPlayerScanError = true;
            }

            _cachedLivePlayers.Clear();
            _cachedLivePlayersValid = false;
            Array.Clear(_liveSlotFlags, 0, _liveSlotFlags.Length);
            livePlayers = _cachedLivePlayers;
            return false;
        }
    }


}
