using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using RayTraceAPI;

namespace S2AWH;

[MinimumApiVersion(362)]
public partial class S2AWH : BasePlugin, IPluginConfig<S2AWHConfig>
{
    private const int VisibilitySlotCapacity = 128;
    private const float StationarySpeedSqThreshold = 4.0f;
    private const int DebugSummaryIntervalTicks = 4096;
    private const float StartupDigestDelaySeconds = 6.0f;

    /// <summary>
    /// Holds the set of entity handles (pawn + weapons) belonging to a single target player,
    /// cached per tick to avoid repeated resolution during transmit filtering.
    /// </summary>
    private sealed class TargetTransmitEntities
    {
        public int Tick = -1;
        public int SanitizeTick = -1;
        public uint PawnHandleRaw = uint.MaxValue;
        public uint[] RawHandles = new uint[16];
        public int Count;
    }

    private sealed class ViewerVisibilityRow
    {
        public bool[] Decisions = new bool[VisibilitySlotCapacity];
        public bool[] Known = new bool[VisibilitySlotCapacity];
        public uint[] PawnHandles = new uint[VisibilitySlotCapacity];
    }

    private interface ISlotRow
    {
        int ActiveCount { get; }
        bool IsTargetKnown(int slot);
        void ClearTargetSlot(int slot);
        bool IsEmpty { get; }
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
        /// <summary>
        /// Returns whether the target slot has an active hold entry.
        /// </summary>
        public bool IsTargetKnown(int slot) => Known[slot];
        public bool IsEmpty => ActiveCount <= 0;
        /// <summary>
        /// Clears hold state for the target slot.
        /// </summary>
        public void ClearTargetSlot(int slot)
        {
            Known[slot] = false;
            HoldUntilTick[slot] = 0;
            ActiveCount--;
        }
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
        /// <summary>
        /// Returns whether the target slot has a stored stable decision.
        /// </summary>
        public bool IsTargetKnown(int slot) => Known[slot];
        public bool IsEmpty => ActiveCount <= 0;
        /// <summary>
        /// Clears stable-decision state for the target slot.
        /// </summary>
        public void ClearTargetSlot(int slot)
        {
            Known[slot] = false;
            Decisions[slot] = false;
            Ticks[slot] = 0;
            ActiveCount--;
        }
    }

    public override string ModuleName => "S2AWH (Source2 AntiWallhack)";
    public override string ModuleVersion => "3.0.1";
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

        if (_transmitFilter != null)
        {
            RebuildVisibilityCacheSnapshot();
        }
    }

    private readonly PluginCapability<CRayTraceInterface> _rayTraceCapability = new("raytrace:craytraceinterface");
    
    private LosEvaluator? _losEvaluator;
    private PreloadPredictor? _predictor;
    private TransmitFilter? _transmitFilter;

    // Cache: ViewerSlot -> visibility-by-target-slot
    private Dictionary<int, ViewerVisibilityRow> _visibilityCache = new(64);
    // Memory: ViewerSlot -> TargetSlot state
    private Dictionary<int, RevealHoldRow> _revealHoldRows = new(64);
    // Last stable decision memory: ViewerSlot -> TargetSlot state
    private Dictionary<int, StableDecisionRow> _stableDecisionRows = new(64);
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
    private readonly List<CCSPlayerController> _cachedLivePlayers = new(64);
    private int _cachedLivePlayersTick = -1;
    private bool _cachedLivePlayersValid;
    private readonly List<int> _viewerSlotsToRemove = new(64);
    private readonly HashSet<int> _liveSlotSet = new(64);
    private readonly Dictionary<int, TargetTransmitEntities> _targetTransmitEntitiesCache = new(64);
    private readonly List<(CCSPlayerController Target, TargetTransmitEntities Entities, int TargetSlot, bool TargetIsBot, int TargetTeam)> _eligibleTargetsWithEntities = new(64);
    private readonly Dictionary<uint, int> _entityHandleIndexCache = new(256);
    private int _entityHandleIndexCacheTick = -1;
    private int _eligibleTargetsWithEntitiesTick = -1;
    private readonly int[] _snapshotTargetSlots = new int[VisibilitySlotCapacity];
    private readonly uint[] _snapshotTargetPawnHandles = new uint[VisibilitySlotCapacity];
    private readonly bool[] _snapshotTargetStationary = new bool[VisibilitySlotCapacity];
    private readonly bool[] _snapshotTargetIsBot = new bool[VisibilitySlotCapacity];
    private readonly int[] _snapshotTargetTeams = new int[VisibilitySlotCapacity];

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

        if (initialized)
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

    private void OnClientDisconnect(int playerSlot)
    {
        _visibilityCache.Remove(playerSlot);
        foreach (var viewerVisibility in _visibilityCache.Values)
        {
            if ((uint)playerSlot < (uint)viewerVisibility.Known.Length)
            {
                viewerVisibility.Known[playerSlot] = false;
                viewerVisibility.Decisions[playerSlot] = false;
                viewerVisibility.PawnHandles[playerSlot] = 0;
            }
        }

        _revealHoldRows.Remove(playerSlot);
        RemoveTargetSlotFromRows(_revealHoldRows, playerSlot);

        _stableDecisionRows.Remove(playerSlot);
        RemoveTargetSlotFromRows(_stableDecisionRows, playerSlot);

        _targetTransmitEntitiesCache.Remove(playerSlot);
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

        _losEvaluator = new LosEvaluator(rayTrace);
        _predictor = new PreloadPredictor(rayTrace);
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

        var config = S2AWHState.Current;
        if (!config.Core.Enabled)
        {
            return;
        }

        // Staggered rebuild: spread viewer evaluations across UpdateFrequencyTicks.
        // Each tick processes ceil(viewers / UpdateFrequencyTicks) viewers.
        // This prevents N^2 pair spikes that cause slow frames at high player counts.
        RebuildVisibilityCacheSnapshot();

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

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
        {
            return false;
        }

        return pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE;
    }

    private void ClearVisibilityCache()
    {
        _visibilityCache.Clear();
        _revealHoldRows.Clear();
        _stableDecisionRows.Clear();
        _targetTransmitEntitiesCache.Clear();
        _losEvaluator?.ClearCaches();
        _predictor?.ClearCaches();
        InvalidateLivePlayersCache();
        _cachedLivePlayers.Clear();
        _viewerSlotsToRemove.Clear();
        _eligibleTargetsWithEntities.Clear();
        _staggeredViewerOffset = 0;
        _hasLoggedGlobalsNotReady = false;
        _hasLoggedPlayerScanError = false;
        _hasLoggedFilterEvaluationError = false;
        _hasLoggedWeaponSyncError = false;
        _lastDebugCachePlayerCount = 0;
        ResetDebugWindowCounters();
    }

    private void InvalidateLivePlayersCache()
    {
        _cachedLivePlayersTick = -1;
        _cachedLivePlayersValid = false;
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

        // Snapshot per-target metadata once per rebuild pass to avoid O(N^2) property reads.
        for (int i = 0; i < playerCount; i++)
        {
            var target = validPlayers[i];
            _snapshotTargetSlots[i] = target.Slot;
            _snapshotTargetIsBot[i] = target.IsBot;
            _snapshotTargetTeams[i] = target.TeamNum;

            var targetPawnEntity = target.PlayerPawn.Value ?? target.Pawn.Value;
            if (targetPawnEntity != null && targetPawnEntity.IsValid)
            {
                _snapshotTargetPawnHandles[i] = targetPawnEntity.EntityHandle.Raw;
                var tVel = targetPawnEntity.AbsVelocity;
                _snapshotTargetStationary[i] = tVel == null ||
                    (tVel.X * tVel.X + tVel.Y * tVel.Y + tVel.Z * tVel.Z) < StationarySpeedSqThreshold;
            }
            else
            {
                _snapshotTargetPawnHandles[i] = 0;
                _snapshotTargetStationary[i] = false;
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
            if (viewer.IsBot && !config.Visibility.BotsDoLOS)
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
            if (!_visibilityCache.TryGetValue(viewerSlot, out var visibilityByTargetSlot))
            {
                visibilityByTargetSlot = new ViewerVisibilityRow();
                _visibilityCache[viewerSlot] = visibilityByTargetSlot;
            }

            // Stationary-Visible optimization: if the viewer isn't moving, we can reuse
            // cached Visible decisions for targets that also aren't moving. This skips
            // expensive LOS evaluation entirely for stationary pairs (buy time, holds).
            // Safe: keeping Visible is the optimistic direction (never hides visible players).
            var viewerPawn = viewer.PlayerPawn.Value;
            bool viewerStationary = false;
            if (viewerPawn != null)
            {
                var vVel = viewerPawn.AbsVelocity;
                viewerStationary = vVel == null || (vVel.X * vVel.X + vVel.Y * vVel.Y + vVel.Z * vVel.Z) < StationarySpeedSqThreshold;
            }

            for (int targetIndex = 0; targetIndex < playerCount; targetIndex++)
            {
                if (targetIndex == viewerIndex)
                {
                    continue;
                }

                var target = validPlayers[targetIndex];
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
                        continue;
                    }

                    if (!config.Visibility.IncludeTeammates && _snapshotTargetTeams[targetIndex] == viewer.TeamNum)
                    {
                        visibilityByTargetSlot.Decisions[targetSlot] = true;
                        visibilityByTargetSlot.Known[targetSlot] = true;
                        visibilityByTargetSlot.PawnHandles[targetSlot] = currentPawnHandle;
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

                    VisibilityEval visibilityEval = EvaluateVisibilitySafe(viewer, target, config, nowTick, "cache rebuild");
                    visibilityByTargetSlot.Decisions[targetSlot] = ResolveTransmitWithMemory(viewerSlot, targetSlot, visibilityEval, nowTick);
                    visibilityByTargetSlot.Known[targetSlot] = true;
                    visibilityByTargetSlot.PawnHandles[targetSlot] = currentPawnHandle;
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


    private VisibilityEval EvaluateVisibilitySafe(CCSPlayerController viewer, CCSPlayerController target, S2AWHConfig config, int nowTick, string phase)
    {
        if (_transmitFilter == null)
        {
            return VisibilityEval.UnknownTransient;
        }

        try
        {
            return _transmitFilter.EvaluateVisibility(viewer, target, nowTick, config);
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
                    $"Phase: {phase}. Error: {ex.Message}",
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
            foreach (var player in Utilities.GetPlayers())
            {
                if (IsLivePlayer(player))
                {
                    _cachedLivePlayers.Add(player);
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
            livePlayers = _cachedLivePlayers;
            return false;
        }
    }


}
