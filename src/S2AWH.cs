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
    Aim = 1,
    Preload = 2,
    Jump = 3,
    Count = 4
}

[MinimumApiVersion(362)]
public partial class S2AWH : BasePlugin, IPluginConfig<S2AWHConfig>
{
    private const int VisibilitySlotCapacity = S2AWHConstants.VisibilitySlotCapacity;
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
    private const int MaxSceneNodeClosureNodesPerTarget = 256;
    private const int MaxSceneNodeClosureDepth = 10;
    // At 64 Hz this equals ~31 ms; at 128 Hz it equals ~15 ms. Higher tick rates produce
    // a shorter (more aggressive) reacquire debounce window, which is the correct direction.
    private const int VisibleReacquireConfirmTicks = 2;
    private const int FailOpenVisibleQuarantineTicks = 3;
    private const int OwnedEntityFullResyncIntervalTicks = 128;
    private const int OwnedEntityPostSpawnRescanTicks = 8;
    private const int MaxDirtyOwnedEntityHandlesBeforeFullResync = 128;
    private const int MaxOwnedEntityDirtySyncPassesPerTick = 4;
    private const int MinOwnedEntityPeriodicResyncMarksPerTick = 64;
    private const int MaxOwnedEntityPeriodicResyncMarksPerTick = 512;
    private const int OwnedEntityPeriodicResyncMarksPerLivePlayer = 12;
    private const int KnownEntityBootstrapRetryDelayTicks = 8;
    private const int RuntimeSelfValidationIntervalTicks = 2048;

    /// <summary>
    /// Holds the set of entity handles (pawn + weapons) belonging to a single target player,
    /// cached per tick to avoid repeated resolution during transmit filtering.
    /// </summary>
    private sealed class TargetTransmitEntities
    {
        public int LastFullRefreshTick = -1;
        public int SanitizeTick = -1;
        public int OwnedClosureTick = -1;
        public int BaseCount;
        public int ForceVisibleUntilTick = -1;
        public int RetainUntilTick = -1;
        public int LastKnownTeam;
        public bool LastKnownIsBot;
        public bool BaseHitEntityCap;
        public bool HitEntityCap;
        public bool HitSceneClosureBudget;
        public uint PawnHandleRaw = uint.MaxValue;
        public uint ControllerHandleRaw = uint.MaxValue;
        public uint[] RawHandles = new uint[MaxTrackedTransmitEntitiesPerTarget];
        public HashSet<uint> HandleMembership = new(MaxTrackedTransmitEntitiesPerTarget);
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
    public override string ModuleVersion => "3.0.5";
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
    private bool _hasLoggedEntityClosureCapError;
    private bool _hasLoggedReverseReferenceAuditError;
    private bool _hasLoggedCheckTransmitError;
    private bool _hasLoggedOnTickError;
    private uint _lastOwnedEntityBucketOverflowOwnerHandleRaw;
    private uint _lastOwnedEntityBucketOverflowEntityHandleRaw;
    private int _lastDebugCachePlayerCount;
    private int _ticksSinceLastTransmitReport;
    private int _transmitCallbacksInWindow;
    private int _transmitHiddenEntitiesInWindow;
    private int _transmitFallbackChecksInWindow;
    private int _transmitRemovalNoEffectInWindow;
    private int _transmitFailOpenOwnedClosureInWindow;
    private int _transmitFailOpenEntityClosureCapInWindow;
    private int _transmitFailOpenQuarantineInWindow;
    private int _transmitFailOpenReverseAuditInWindow;
    private int _ownedEntityFullResyncsInWindow;
    private int _ownedEntityDirtyEntityUpdatesInWindow;
    private int _ownedEntityPostSpawnRescanMarksInWindow;
    private int _ownedEntityPeriodicResyncBatchesInWindow;
    private int _ownedEntityPeriodicResyncMarksInWindow;
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
    private readonly Dictionary<uint, OwnedEntityBucket> _ownedEntityRelationsByChild = new(128);
    private readonly Dictionary<string, int> _closureOffenderCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, bool> _transmitMembershipByHandleScratch = new(256);
    private readonly HashSet<uint> _dirtyOwnedEntityHandles = new(256);
    private readonly Dictionary<uint, int> _pendingOwnedEntityRescanUntilTick = new(128);
    private readonly HashSet<nint> _sceneClosureVisitedNodes = new(256);
    private readonly OwnedEntityBucket _ownedEntityScratchHandles = new();
    private readonly List<uint> _ownedEntityDirtyHandleScratch = new(256);
    private readonly List<uint> _ownedEntityPendingRescanRemovalScratch = new(64);
    private readonly List<uint> _ownedEntityPeriodicResyncHandleSnapshot = new(1024);
    private readonly List<uint> _knownEntityHandles = new(2048);
    private readonly Dictionary<uint, int> _knownEntityHandleIndices = new(2048);
    private readonly List<uint> _knownEntityHandleBootstrapScratch = new(2048);
    private readonly Dictionary<uint, int> _knownEntityHandleBootstrapIndicesScratch = new(2048);
    private readonly List<uint> _staleKnownEntityHandleScratch = new(128);
    private int _entityHandleIndexCacheTick = -1;
    private int _ownedEntityBucketsTick = -1;
    private int _ownedEntityLastFullResyncTick = -1;
    private int _ownedEntityPeriodicResyncCursor;
    private int _eligibleTargetsWithEntitiesTick = -1;
    private int _roundStartGraceUntilTick;
    private bool _ownedEntityBucketsInitialized;
    private bool _ownedEntityPeriodicResyncInProgress;
    private bool _knownEntityHandlesInitialized;
    private bool _entityLifecycleListenersRegistered;
    private int _knownEntityBootstrapRetryUntilTick = -1;
    private bool _hasLoggedRuntimeValidationSummary;
    private bool _hasLoggedDependencySurfaceWarning;
    private bool _hasLoggedVisibilityScopeNote;
    private int _lastRuntimeSelfValidationTick = int.MinValue;
    private readonly int[] _snapshotTargetSlots = new int[VisibilitySlotCapacity];
    private readonly uint[] _snapshotTargetPawnHandles = new uint[VisibilitySlotCapacity];
    private readonly bool[] _snapshotTargetStationary = new bool[VisibilitySlotCapacity];
    private readonly bool[] _snapshotTargetIsBot = new bool[VisibilitySlotCapacity];
    private readonly int[] _snapshotTargetTeams = new int[VisibilitySlotCapacity];
    private readonly int[] _snapshotStabilizeUntilTickBySlot = new int[VisibilitySlotCapacity];
    private readonly int[][] _viewerRayCountsWorking = CreateRayCountArray();
    private readonly int[][] _viewerRayCountsDisplay = CreateRayCountArray();
    private readonly int[] _viewerTargetCounts = new int[VisibilitySlotCapacity];
    private readonly int[] _viewerRayCountLastRenderedHashBySlot = new int[VisibilitySlotCapacity];
    private readonly int[] _viewerRayCountLastHudRefreshTickBySlot = new int[VisibilitySlotCapacity];
    private int _viewerRayCounterTick = -1;
    private bool _viewerRayCountsDisplayDirty;
    internal readonly PlayerTransformSnapshot[] SnapshotTransforms = new PlayerTransformSnapshot[VisibilitySlotCapacity];
    internal readonly CBasePlayerPawn?[] SnapshotPawns = new CBasePlayerPawn?[VisibilitySlotCapacity];
    private static readonly string ViewerRayHudLosColor = ToHtmlColorHex(VisibilityGeometry.GetViewerRayCounterColor(ViewerRayTraceStage.Los));
    private static readonly string ViewerRayHudAimColor = ToHtmlColorHex(VisibilityGeometry.GetViewerRayCounterColor(ViewerRayTraceStage.Aim));
    private static readonly string ViewerRayHudPreloadColor = ToHtmlColorHex(VisibilityGeometry.GetViewerRayCounterColor(ViewerRayTraceStage.Preload));
    private static readonly string ViewerRayHudJumpColor = ToHtmlColorHex(VisibilityGeometry.GetViewerRayCounterColor(ViewerRayTraceStage.Jump));

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

        RegisterListener<Listeners.OnMetamodAllPluginsLoaded>(OnMetamodAllPluginsLoaded);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);

        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
        Server.NextFrame(EnsureEntityLifecycleListenersRegistered);

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
        RemoveListener<Listeners.CheckTransmit>(OnCheckTransmit);
        RemoveListener<Listeners.OnTick>(OnTick);
        RemoveListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        if (_entityLifecycleListenersRegistered)
        {
            RemoveListener<Listeners.OnEntityParentChanged>(OnEntityParentChanged);
            RemoveListener<Listeners.OnEntityDeleted>(OnEntityDeleted);
            RemoveListener<Listeners.OnEntitySpawned>(OnEntitySpawned);
            RemoveListener<Listeners.OnEntityCreated>(OnEntityCreated);
            _entityLifecycleListenersRegistered = false;
        }
        DeregisterEventHandler<EventRoundStart>(OnRoundStart);
        DeregisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        ClearViewerRayCountOverlays();
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

    }

    private void OnMapStart(string mapName)
    {
        EnsureEntityLifecycleListenersRegistered();
        ClearVisibilityCache();
        DebugLog(
            "New map loaded.",
            $"Map: {mapName}. Old player data was wiped.",
            "Starting fresh for this map."
        );
        bool initialized = TryInitializeModules("OnMapStart");
        PrimeKnownLivePlayerHandles();

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

    private void EnsureEntityLifecycleListenersRegistered()
    {
        if (_entityLifecycleListenersRegistered)
        {
            return;
        }

        RegisterListener<Listeners.OnEntityCreated>(OnEntityCreated);
        RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned);
        RegisterListener<Listeners.OnEntityDeleted>(OnEntityDeleted);
        RegisterListener<Listeners.OnEntityParentChanged>(OnEntityParentChanged);
        _entityLifecycleListenersRegistered = true;
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
        _hasLoggedEntityClosureCapError = false;
        _hasLoggedReverseReferenceAuditError = false;
        _hasLoggedCheckTransmitError = false;
        _hasLoggedOnTickError = false;
        _hasLoggedRuntimeValidationSummary = false;
        _hasLoggedDependencySurfaceWarning = false;
        _hasLoggedVisibilityScopeNote = false;
        _lastRuntimeSelfValidationTick = int.MinValue;
        PrimeKnownLivePlayerHandles();

        InfoLog(
            "RayTrace is connected.",
            "S2AWH can now check who can see who.",
            "Wallhack protection is active."
        );
        LogRuntimeValidationSummary();
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
        try
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

            int nowTick = Server.TickCount;
            BeginViewerRayCountTick(nowTick);
            RunRuntimeSelfValidation(nowTick);

            var config = S2AWHState.Current;
            if (!config.Core.Enabled)
            {
                ClearViewerRayCountOverlays();
                if (_collectDebugCounters)
                {
                    ResetDebugWindowCounters();
                }
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
                _transmitFailOpenOwnedClosureInWindow > 0 ||
                _transmitFailOpenEntityClosureCapInWindow > 0 ||
                _transmitFailOpenQuarantineInWindow > 0 ||
                _transmitFailOpenReverseAuditInWindow > 0 ||
                _ownedEntityFullResyncsInWindow > 0 ||
                _ownedEntityDirtyEntityUpdatesInWindow > 0 ||
                _ownedEntityPostSpawnRescanMarksInWindow > 0 ||
                _ownedEntityPeriodicResyncBatchesInWindow > 0 ||
                _ownedEntityPeriodicResyncMarksInWindow > 0 ||
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
            _hasLoggedOnTickError = false;
        }
        catch (Exception ex)
        {
            if (_hasLoggedOnTickError)
            {
                return;
            }

            WarnLog(
                "Tick loop had an unexpected error.",
                "A transient native or game-state fault interrupted one simulation tick.",
                "S2AWH skipped that tick safely and will continue."
            );
            DebugLog(
                "Tick loop error detail.",
                $"Error: {ex.Message}",
                "This message only shows once."
            );
            _hasLoggedOnTickError = true;
        }
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
                "World geometry occlusion is active. Smoke-style gameplay occluders are intentionally left to the client."
            );
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }


    private static int[][] CreateRayCountArray()
    {
        var arr = new int[VisibilitySlotCapacity][];
        for (int i = 0; i < VisibilitySlotCapacity; i++)
        {
            arr[i] = new int[ViewerRayTraceStageCount];
        }

        return arr;
    }

    internal void RecordViewerRayTraceAttempt(int viewerSlot, ViewerRayTraceStage stage)
    {
        if ((uint)viewerSlot >= VisibilitySlotCapacity || (int)stage >= ViewerRayTraceStageCount)
        {
            return;
        }

        BeginViewerRayCountTick(Server.TickCount);
        _viewerRayCountsWorking[viewerSlot][(int)stage]++;
    }

    private void BeginViewerRayCountTick(int nowTick)
    {
        if (_viewerRayCounterTick == nowTick)
        {
            return;
        }

        if (_viewerRayCounterTick >= 0)
        {
            for (int s = 0; s < VisibilitySlotCapacity; s++)
            {
                Array.Copy(_viewerRayCountsWorking[s], _viewerRayCountsDisplay[s], ViewerRayTraceStageCount);
                Array.Clear(_viewerRayCountsWorking[s], 0, ViewerRayTraceStageCount);
            }

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

            UpdateViewerRayCountOverlay(slot, player!, forceRefresh);
        }
    }

    private void UpdateViewerRayCountOverlay(int slot, CCSPlayerController player, bool forceMessageRefresh)
    {
        int nowTick = Server.TickCount;
        int targets = GetViewerTargetCount(slot);
        int los = GetViewerRayStageCount(slot, ViewerRayTraceStage.Los);
        int aim = GetViewerRayStageCount(slot, ViewerRayTraceStage.Aim);
        int preload = GetViewerRayStageCount(slot, ViewerRayTraceStage.Preload);
        int jump = GetViewerRayStageCount(slot, ViewerRayTraceStage.Jump);
        int stateHash = HashCode.Combine(targets, los, aim, preload, jump);

        bool needsPeriodicRefresh =
            (nowTick - _viewerRayCountLastHudRefreshTickBySlot[slot]) >= ViewerRayCountHudRefreshIntervalTicks;
        if (!forceMessageRefresh &&
            !needsPeriodicRefresh &&
            _viewerRayCountLastRenderedHashBySlot[slot] == stateHash)
        {
            return;
        }

        string countText = BuildViewerRayCountHudHtml(targets, los, aim, preload, jump);
        try
        {
            player.PrintToCenterHtml(countText, 1);
        }
        catch
        {
            // Event listener may not be available during transient engine states
            // (round start, map change, unload). The HUD will expire naturally.
        }

        _viewerRayCountLastRenderedHashBySlot[slot] = stateHash;
        _viewerRayCountLastHudRefreshTickBySlot[slot] = nowTick;
    }

    private string BuildViewerRayCountHudHtml(int targets, int los, int aim, int preload, int jump)
    {
        int total = los + aim + preload + jump;
        return string.Concat(
            "<b><font color='#80DFFF'>RAYS</font></b><br>",
            $"<b><font color='{ViewerRayHudLosColor}'>LOS: {los}</font></b><br>",
            $"<b><font color='{ViewerRayHudAimColor}'>AIM: {aim}</font></b><br>",
            $"<b><font color='{ViewerRayHudPreloadColor}'>PRELOAD: {preload}</font></b><br>",
            $"<b><font color='{ViewerRayHudJumpColor}'>JUMP: {jump}</font></b><br>",
            $"<b><font color='#FFF078'>TOTAL: {total}</font></b><br>",
            $"<b><font color='#FFB347'>TARGETS: {targets}</font></b>");
    }

    private static string ToHtmlColorHex(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private int GetViewerTargetCount(int slot)
    {
        return _viewerTargetCounts[slot];
    }

    private int GetViewerRayStageCount(int slot, ViewerRayTraceStage stage)
    {
        int stageIndex = (int)stage;
        return _viewerRayCounterTick == Server.TickCount
            ? _viewerRayCountsWorking[slot][stageIndex]
            : _viewerRayCountsDisplay[slot][stageIndex];
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

        CCSPlayerController? player = Utilities.GetPlayerFromSlot(slot);
        if (player != null && player.IsValid && !player.IsBot)
        {
            try
            {
                player.PrintToCenterHtml("", 0);
            }
            catch
            {
                // Event listener may not be available during transient engine states
                // (round start, map change, unload). The HUD will expire naturally.
            }
        }
    }

    private void ClearViewerRayCountSlotState(int slot)
    {
        if ((uint)slot >= VisibilitySlotCapacity)
        {
            return;
        }

        Array.Clear(_viewerRayCountsWorking[slot], 0, ViewerRayTraceStageCount);
        Array.Clear(_viewerRayCountsDisplay[slot], 0, ViewerRayTraceStageCount);

        _viewerTargetCounts[slot] = 0;
        _viewerRayCountLastRenderedHashBySlot[slot] = int.MinValue;
        _viewerRayCountLastHudRefreshTickBySlot[slot] = int.MinValue;
    }
}
