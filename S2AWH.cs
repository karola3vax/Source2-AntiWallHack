using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using RayTraceAPI;

namespace S2AWH;

[MinimumApiVersion(362)]
public class S2AWH : BasePlugin, IPluginConfig<S2AWHConfig>
{
    private const int VisibilitySlotCapacity = 128;
    private const string LevelInformation = "INFO";
    private const string LevelWarning = "WARN";
    private const string LevelDebug = "DEBUG";
    private const string LogColorInformation = "\u001b[36m";
    private const string LogColorWarning = "\u001b[33m";
    private const string LogColorDebug = "\u001b[90m";
    private const string LogColorReset = "\u001b[0m";
    private const int MaxLogSentenceLength = 320;
    private const int DebugSummaryIntervalTicks = 4096;
    private const float StartupDigestDelaySeconds = 6.0f;

    private sealed class TargetTransmitEntities
    {
        public int Tick = -1;
        public uint PawnHandleRaw = uint.MaxValue;
        public uint[] RawHandles = new uint[8];
        public int Count;
    }

    private sealed class ViewerVisibilityRow
    {
        public bool[] Decisions = new bool[VisibilitySlotCapacity];
        public bool[] Known = new bool[VisibilitySlotCapacity];
        public uint[] PawnHandles = new uint[VisibilitySlotCapacity];
    }

    private sealed class RevealHoldRow
    {
        public int[] HoldUntilTick = new int[VisibilitySlotCapacity];
        public bool[] Known = new bool[VisibilitySlotCapacity];
        public int ActiveCount;
    }

    private sealed class StableDecisionRow
    {
        public bool[] Decisions = new bool[VisibilitySlotCapacity];
        public int[] Ticks = new int[VisibilitySlotCapacity];
        public bool[] Known = new bool[VisibilitySlotCapacity];
        public int ActiveCount;
    }

    public override string ModuleName => "S2AWH (Source2 AntiWallhack)";
    public override string ModuleVersion => "3.0.0";
    public override string ModuleAuthor => "karola3vax";
    public override string ModuleDescription => "Prevents wallhacks from working using Ray-Trace by hiding players from out of line of sight.";

    private const int InitRetryIntervalTicks = 64;
    private const float UnknownStickyWindowSeconds = 0.150f;

    public S2AWHConfig Config { get; set; } = new();

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
                "A configuration value was corrected automatically.",
                warning,
                "The plugin will continue running without interruption."
            );
        }

        InfoLog(
            "Configuration loaded successfully.",
            $"Update interval is {config.Core.UpdateFrequencyTicks} tick(s), reveal hold is {config.Preload.RevealHoldSeconds:F2} second(s), and detailed logs are {(config.Diagnostics.ShowDebugInfo ? "enabled" : "disabled")}.",
            "S2AWH is now running with these settings."
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
    private int _unknownForcedHiddenInWindow;
    private int _unknownFromExceptionInWindow;
    private int _revealHoldTicks = 1;
    private int _unknownStickyWindowTicks = 1;
    private bool _collectDebugCounters = true;
    private bool _startupDigestQueued;
    private readonly List<CCSPlayerController> _cachedLivePlayers = new(64);
    private int _cachedLivePlayersTick = -1;
    private bool _cachedLivePlayersValid;
    private readonly HashSet<int> _activeViewerSlots = new(64);
    private readonly List<int> _viewerSlotsToRemove = new(64);
    private readonly Dictionary<int, TargetTransmitEntities> _targetTransmitEntitiesCache = new(64);
    private readonly List<(CCSPlayerController Target, TargetTransmitEntities Entities)> _eligibleTargetsWithEntities = new(64);
    private readonly Dictionary<uint, int> _entityHandleIndexCache = new(256);
    private int _entityHandleIndexCacheTick = -1;

    public override void Load(bool hotReload)
    {
        _revealHoldTicks = ConvertRevealHoldSecondsToTicks(S2AWHState.Current.Preload.RevealHoldSeconds);
        _unknownStickyWindowTicks = ConvertUnknownStickySecondsToTicks();
        _collectDebugCounters = S2AWHState.Current.Diagnostics.ShowDebugInfo;

        InfoLog(
            "S2AWH is starting.",
            "The server loaded the plugin and initialization is now in progress.",
            "Visibility protection will be active in a moment."
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
            "Core listeners are active and visibility checks can now run.",
            "Player visibility is now controlled by S2AWH."
        );
    }

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
            "Listener order was refreshed.",
            "All plugins are now loaded, so S2AWH moved its transmit hook to a safer order.",
            "Visibility filtering will run more reliably with other plugins."
        );

        TryInitializeModules("OnMetamodAllPluginsLoaded");
    }

    private void OnMapStart(string mapName)
    {
        ClearVisibilityCache();
        DebugLog(
            "A new map started.",
            $"Current map is {mapName}, and old visibility data is no longer valid.",
            "Visibility cache was cleared for a clean start."
        );
        bool initialized = TryInitializeModules("OnMapStart");

        if (initialized)
        {
            if (RebuildVisibilityCacheSnapshot())
            {
                DebugLog(
                    "Initial visibility cache was built.",
                    "S2AWH was already initialized when the map started.",
                    "First transmit checks can use fresh map data."
                );
            }
            else
            {
                DebugLog(
                    "Initial cache build was delayed.",
                    "Server globals are not ready yet at this startup stage.",
                    "S2AWH will rebuild cache on the next safe tick."
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
            "A clean state is required before the next map starts.",
            "Visibility cache was reset."
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
        RemoveTargetSlotFromRevealRows(playerSlot);

        _stableDecisionRows.Remove(playerSlot);
        RemoveTargetSlotFromStableRows(playerSlot);

        _targetTransmitEntitiesCache.Remove(playerSlot);
        _losEvaluator?.InvalidateTargetSlot(playerSlot);
        _predictor?.InvalidateTargetSlot(playerSlot);

        InvalidateLivePlayersCache();

        DebugLog(
            "A player disconnected.",
            $"Slot {playerSlot} is now free, so stale slot data was removed.",
            "Related cache entries were deleted."
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
            LogWaitingForRayTrace($"RayTrace connection check failed in {source}.");
            return false;
        }

        if (rayTrace == null)
        {
            LogWaitingForRayTrace($"RayTrace interface was empty in {source}.");
            return false;
        }

        if (!IsRayTraceOperational(rayTrace))
        {
            LogWaitingForRayTrace(
                $"RayTrace was found in {source}, but trace calls are still failing. The native bridge is probably not ready yet."
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
            "RayTrace connection is ready.",
            "S2AWH can now run real line-of-sight checks.",
            "Hidden-behind-wall data filtering is now active."
        );
        DebugLog(
            "Initialization completed.",
            $"Startup trigger was {source}, and RayTrace responded correctly.",
            "S2AWH tracing is fully active."
        );
        DebugLog(
            "First cache build is delayed to the next tick.",
            "Early startup callbacks can happen before native globals are stable.",
            "Visibility cache generation will begin on the next safe tick."
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
            "RayTrace is not ready yet.",
            "This usually means RayTrace loaded later, or one dependency is missing.",
            "S2AWH will wait and retry automatically."
        );
        DebugLog(
            "RayTrace wait reason was recorded.",
            reason,
            "Automatic retry is running in the background."
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
                    "Retrying RayTrace connection.",
                    "RayTrace is still not active.",
                    "A new connection check is running on this tick."
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
        // This prevents N² pair spikes that cause slow frames at high player counts.
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
            _unknownForcedHiddenInWindow > 0 ||
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
                "Startup check completed.",
                $"Map {mapName} is running, S2AWH is {runtimeState}, and update interval is {config.Core.UpdateFrequencyTicks} tick(s).",
                "Console output is now in normal runtime mode."
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
        _activeViewerSlots.Clear();
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
        _unknownForcedHiddenInWindow = 0;
        _unknownFromExceptionInWindow = 0;
    }

    private static int ConvertRevealHoldSecondsToTicks(float revealHoldSeconds)
    {
        float holdSeconds = Math.Clamp(revealHoldSeconds, 0.0f, 1.0f);
        if (holdSeconds <= 0.0f)
        {
            return 0;
        }

        int convertedTicks = (int)Math.Ceiling(holdSeconds / Server.TickInterval);
        return Math.Max(1, convertedTicks);
    }

    private static int ConvertUnknownStickySecondsToTicks()
    {
        int convertedTicks = (int)Math.Ceiling(UnknownStickyWindowSeconds / Server.TickInterval);
        return Math.Max(1, convertedTicks);
    }

    private void StoreStableDecision(int viewerSlot, int targetSlot, bool decision, int nowTick)
    {
        if ((uint)targetSlot >= VisibilitySlotCapacity)
        {
            return;
        }

        if (!_stableDecisionRows.TryGetValue(viewerSlot, out var stableRow))
        {
            stableRow = new StableDecisionRow();
            _stableDecisionRows[viewerSlot] = stableRow;
        }

        if (!stableRow.Known[targetSlot])
        {
            stableRow.Known[targetSlot] = true;
            stableRow.ActiveCount++;
        }

        stableRow.Decisions[targetSlot] = decision;
        stableRow.Ticks[targetSlot] = nowTick;
    }

    private bool TryGetStableDecision(int viewerSlot, int targetSlot, int nowTick, out bool decision)
    {
        decision = false;

        if ((uint)targetSlot >= VisibilitySlotCapacity ||
            !_stableDecisionRows.TryGetValue(viewerSlot, out var stableRow) ||
            !stableRow.Known[targetSlot])
        {
            return false;
        }

        int ageTicks = nowTick - stableRow.Ticks[targetSlot];
        if (ageTicks < 0 || ageTicks > _unknownStickyWindowTicks)
        {
            ClearStableDecisionEntry(viewerSlot, stableRow, targetSlot);
            return false;
        }

        decision = stableRow.Decisions[targetSlot];
        return true;
    }

    private bool TryResolveRevealHold(int viewerSlot, int targetSlot, int nowTick, bool countAsGenericHoldHit)
    {
        if (_revealHoldTicks <= 0)
        {
            return false;
        }

        if ((uint)targetSlot < VisibilitySlotCapacity &&
            _revealHoldRows.TryGetValue(viewerSlot, out var holdRow) &&
            holdRow.Known[targetSlot])
        {
            int holdUntilTick = holdRow.HoldUntilTick[targetSlot];
            if (holdUntilTick >= nowTick)
            {
                if (_collectDebugCounters && countAsGenericHoldHit)
                {
                    _holdHitKeepAliveInWindow++;
                }
                return true;
            }

            ClearRevealHoldEntry(viewerSlot, holdRow, targetSlot);
            if (_collectDebugCounters)
            {
                _holdExpiredInWindow++;
            }
        }

        return false;
    }

    private bool ResolveTransmitWithMemory(int viewerSlot, int targetSlot, VisibilityEval visibilityEval, int nowTick)
    {
        switch (visibilityEval)
        {
            case VisibilityEval.Visible:
                StoreStableDecision(viewerSlot, targetSlot, true, nowTick);
                if ((uint)targetSlot < VisibilitySlotCapacity)
                {
                    if (_revealHoldTicks > 0)
                    {
                        if (!_revealHoldRows.TryGetValue(viewerSlot, out var holdRow))
                        {
                            holdRow = new RevealHoldRow();
                            _revealHoldRows[viewerSlot] = holdRow;
                        }

                        if (!holdRow.Known[targetSlot])
                        {
                            holdRow.Known[targetSlot] = true;
                            holdRow.ActiveCount++;
                        }

                        holdRow.HoldUntilTick[targetSlot] = nowTick + _revealHoldTicks;

                        if (_collectDebugCounters)
                        {
                            _holdRefreshInWindow++;
                        }
                    }
                    else if (_revealHoldRows.TryGetValue(viewerSlot, out var holdRow) && holdRow.Known[targetSlot])
                    {
                        // Reveal hold disabled: remove stale memory immediately.
                        ClearRevealHoldEntry(viewerSlot, holdRow, targetSlot);
                    }
                }
                return true;

            case VisibilityEval.Hidden:
                StoreStableDecision(viewerSlot, targetSlot, false, nowTick);
                return TryResolveRevealHold(viewerSlot, targetSlot, nowTick, countAsGenericHoldHit: true);

            case VisibilityEval.UnknownTransient:
                if (_collectDebugCounters)
                {
                    _unknownEvalInWindow++;
                }

                if (TryGetStableDecision(viewerSlot, targetSlot, nowTick, out bool stickyDecision))
                {
                    if (_collectDebugCounters)
                    {
                        _unknownStickyHitInWindow++;
                    }
                    return stickyDecision;
                }

                if (TryResolveRevealHold(viewerSlot, targetSlot, nowTick, countAsGenericHoldHit: false))
                {
                    if (_collectDebugCounters)
                    {
                        _unknownHoldHitInWindow++;
                    }
                    return true;
                }

                if (_collectDebugCounters)
                {
                    _unknownForcedHiddenInWindow++;
                }
                return false;

            default:
                return false;
        }
    }

    private void ClearRevealHoldEntry(int viewerSlot, RevealHoldRow holdRow, int targetSlot)
    {
        if (!holdRow.Known[targetSlot])
        {
            return;
        }

        holdRow.Known[targetSlot] = false;
        holdRow.HoldUntilTick[targetSlot] = 0;
        holdRow.ActiveCount--;
        if (holdRow.ActiveCount <= 0)
        {
            _revealHoldRows.Remove(viewerSlot);
        }
    }

    private void ClearStableDecisionEntry(int viewerSlot, StableDecisionRow stableRow, int targetSlot)
    {
        if (!stableRow.Known[targetSlot])
        {
            return;
        }

        stableRow.Known[targetSlot] = false;
        stableRow.Decisions[targetSlot] = false;
        stableRow.Ticks[targetSlot] = 0;
        stableRow.ActiveCount--;
        if (stableRow.ActiveCount <= 0)
        {
            _stableDecisionRows.Remove(viewerSlot);
        }
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
                viewerStationary = vVel == null || (vVel.X * vVel.X + vVel.Y * vVel.Y + vVel.Z * vVel.Z) < 4.0f;
            }

            for (int targetIndex = 0; targetIndex < playerCount; targetIndex++)
            {
                if (targetIndex == viewerIndex)
                {
                    continue;
                }

                var target = validPlayers[targetIndex];
                int targetSlot = target.Slot;
                if ((uint)targetSlot < (uint)visibilityByTargetSlot.Decisions.Length)
                {
                    // Reuse Visible decision if both are stationary and same pawn.
                    // PawnHandle guard prevents stale reuse across player slot changes.
                    if (viewerStationary &&
                        visibilityByTargetSlot.Known[targetSlot] &&
                        visibilityByTargetSlot.Decisions[targetSlot])
                    {
                        var tp = target.PlayerPawn.Value ?? target.Pawn.Value;
                        uint currentPawnHandle = (tp != null && tp.IsValid) ? tp.EntityHandle.Raw : 0;
                        if (currentPawnHandle != 0 &&
                            visibilityByTargetSlot.PawnHandles[targetSlot] == currentPawnHandle)
                        {
                            var tVel = (target.PlayerPawn.Value)?.AbsVelocity;
                            bool targetStationary = tVel == null || (tVel.X * tVel.X + tVel.Y * tVel.Y + tVel.Z * tVel.Z) < 4.0f;
                            if (targetStationary)
                            {
                                continue; // Reuse cached Visible — both stationary, same pawn.
                            }
                        }
                    }

                    VisibilityEval visibilityEval = EvaluateVisibilitySafe(viewer, target, config, nowTick, "cache rebuild");
                    visibilityByTargetSlot.Decisions[targetSlot] = ResolveTransmitWithMemory(viewerSlot, targetSlot, visibilityEval, nowTick);
                    visibilityByTargetSlot.Known[targetSlot] = true;
                    var targetPawn = target.PlayerPawn.Value ?? target.Pawn.Value;
                    visibilityByTargetSlot.PawnHandles[targetSlot] = (targetPawn != null && targetPawn.IsValid)
                        ? targetPawn.EntityHandle.Raw
                        : 0;
                }
            }
        }

        // Check if this batch completed a full cycle through all viewers.
        bool isFullCycleComplete = (_staggeredViewerOffset + processedEligible) >= eligibleViewerCount;

        if (isFullCycleComplete)
        {
            // Build the full active viewer set and purge stale rows.
            _activeViewerSlots.Clear();
            for (int i = 0; i < playerCount; i++)
            {
                if (validPlayers[i].IsBot && !config.Visibility.BotsDoLOS) continue;
                _activeViewerSlots.Add(validPlayers[i].Slot);
            }

            RemoveInactiveViewerRows(_visibilityCache);
            RemoveInactiveViewerRows(_revealHoldRows);
            RemoveInactiveViewerRows(_stableDecisionRows);
            _staggeredViewerOffset = 0;
        }
        else
        {
            _staggeredViewerOffset += processedEligible;
        }

        if (isFullCycleComplete && _collectDebugCounters && _lastDebugCachePlayerCount != validPlayers.Count)
        {
            DebugLog(
                "Visibility table was refreshed.",
                $"There are {validPlayers.Count} live players, {_visibilityCache.Count} viewer rows, batch size {viewersPerTick}.",
                "Staggered rebuild distributes load across ticks."
            );
            _lastDebugCachePlayerCount = validPlayers.Count;
        }

        return true;
    }

    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        var config = S2AWHState.Current;
        if (!config.Core.Enabled || _transmitFilter == null) return;

        int nowTick = Server.TickCount;
        if (_entityHandleIndexCacheTick != nowTick)
        {
            _entityHandleIndexCacheTick = nowTick;
            _entityHandleIndexCache.Clear();
        }
        if (!TryGetLivePlayers(nowTick, out var eligibleTargets))
        {
            return;
        }

        if (_collectDebugCounters)
        {
            _transmitCallbacksInWindow++;
        }

        _eligibleTargetsWithEntities.Clear();
        int eligibleTargetCount = eligibleTargets.Count;
        for (int i = 0; i < eligibleTargetCount; i++)
        {
            var target = eligibleTargets[i];
            if (TryGetTargetTransmitEntities(target, nowTick, out var targetEntities))
            {
                _eligibleTargetsWithEntities.Add((target, targetEntities));
            }
        }

        foreach ((CCheckTransmitInfo info, CCSPlayerController? viewer) in infoList)
        {
            if (viewer == null || !IsLivePlayer(viewer)) continue; // Dead/invalid viewers see everything
             
            // If it's a bot and we don't calculate LOS for bots, don't block anything
            if (viewer.IsBot && !config.Visibility.BotsDoLOS) continue;

            int viewerSlot = viewer.Slot;
            bool hasViewerCache = _visibilityCache.TryGetValue(viewerSlot, out var targetVisibilityBySlot);

            foreach (var targetEntry in _eligibleTargetsWithEntities)
            {
                var target = targetEntry.Target;
                int targetSlot = target.Slot;
                if (targetSlot == viewerSlot)
                {
                    continue;
                }
                var targetEntities = targetEntry.Entities;

                bool shouldTransmit;
                if (hasViewerCache &&
                    targetVisibilityBySlot != null &&
                    (uint)targetSlot < (uint)targetVisibilityBySlot.Known.Length &&
                    targetVisibilityBySlot.Known[targetSlot] &&
                    targetVisibilityBySlot.PawnHandles[targetSlot] == targetEntities.PawnHandleRaw)
                {
                    shouldTransmit = targetVisibilityBySlot.Decisions[targetSlot];
                }
                else
                {
                    if (_collectDebugCounters)
                    {
                        _transmitFallbackChecksInWindow++;
                    }
                    VisibilityEval visibilityEval = EvaluateVisibilitySafe(viewer, target, config, nowTick, "transmit fallback");
                    shouldTransmit = ResolveTransmitWithMemory(viewerSlot, targetSlot, visibilityEval, nowTick);

                    // Keep fallback decisions in the snapshot to avoid repeating work in the same tick window.
                    if ((uint)targetSlot < VisibilitySlotCapacity)
                    {
                        if (!hasViewerCache || targetVisibilityBySlot == null)
                        {
                            targetVisibilityBySlot = new ViewerVisibilityRow();
                            _visibilityCache[viewerSlot] = targetVisibilityBySlot;
                            hasViewerCache = true;
                        }

                        targetVisibilityBySlot.Decisions[targetSlot] = shouldTransmit;
                        targetVisibilityBySlot.Known[targetSlot] = true;
                        targetVisibilityBySlot.PawnHandles[targetSlot] = targetEntities.PawnHandleRaw;
                    }
                }

                if (shouldTransmit)
                {
                    // Visible decisions are fail-open: do not force-add entities.
                    // This avoids overriding removals made by other plugins in the same callback.
                    continue;
                }

                bool removedAny = RemoveTargetPlayerAndWeapons(info, targetEntities, nowTick);
                if (_collectDebugCounters)
                {
                    if (removedAny)
                    {
                        _transmitHiddenEntitiesInWindow++;
                    }
                    else
                    {
                        _transmitRemovalNoEffectInWindow++;
                    }
                }
            }
        }
    }

    private bool RemoveTargetPlayerAndWeapons(CCheckTransmitInfo info, TargetTransmitEntities targetEntities, int nowTick)
    {
        int entityCount = targetEntities.Count;
        if (entityCount <= 0)
        {
            return false;
        }

        if (!_collectDebugCounters)
        {
            for (int i = 0; i < entityCount; i++)
            {
                uint entityHandleRaw = targetEntities.RawHandles[i];
                if (!TryResolveEntityHandleIndexForTransmit(entityHandleRaw, nowTick, out int entityIndex))
                {
                    continue;
                }

                info.TransmitEntities.Remove(entityIndex);
            }
            return true;
        }

        bool removedAny = false;
        for (int i = 0; i < entityCount; i++)
        {
            uint entityHandleRaw = targetEntities.RawHandles[i];
            if (!TryResolveEntityHandleIndexForTransmit(entityHandleRaw, nowTick, out int entityIndex))
            {
                continue;
            }

            if (info.TransmitEntities.Contains(entityIndex))
            {
                info.TransmitEntities.Remove(entityIndex);
                removedAny = true;
            }

        }

        return removedAny;
    }

    private bool TryGetTargetTransmitEntities(CCSPlayerController target, int nowTick, out TargetTransmitEntities targetEntities)
    {
        targetEntities = null!;
        var targetPawnEntity = target.PlayerPawn.Value ?? target.Pawn.Value;
        if (targetPawnEntity == null || !targetPawnEntity.IsValid)
        {
            return false;
        }

        int targetSlot = target.Slot;
        uint pawnHandleRaw = targetPawnEntity.EntityHandle.Raw;
        int targetPawnIndex = (int)targetPawnEntity.Index;
        int targetControllerIndex = (int)target.Index;

        if (!_targetTransmitEntitiesCache.TryGetValue(targetSlot, out var cachedEntities) || cachedEntities == null)
        {
            cachedEntities = new TargetTransmitEntities();
            _targetTransmitEntitiesCache[targetSlot] = cachedEntities;
        }
        targetEntities = cachedEntities;

        if (targetEntities.Tick == nowTick && targetEntities.PawnHandleRaw == pawnHandleRaw)
        {
            SanitizeTargetEntityList(targetEntities, nowTick);
            return true;
        }

        targetEntities.Tick = nowTick;
        targetEntities.PawnHandleRaw = pawnHandleRaw;
        targetEntities.Count = 0;
        AddUniqueEntityHandle(targetEntities, pawnHandleRaw);

        try
        {
            var weaponServices = targetPawnEntity.WeaponServices;
            if (weaponServices == null)
            {
                SanitizeTargetEntityList(targetEntities, nowTick);
                return true;
            }

            var activeWeapon = weaponServices.ActiveWeapon;
            if (TryResolveLiveWeaponEntityHandle(activeWeapon, targetPawnIndex, targetControllerIndex, out uint activeWeaponHandleRaw))
            {
                AddUniqueEntityHandle(targetEntities, activeWeaponHandleRaw);
            }

            var lastWeapon = weaponServices.LastWeapon;
            if (TryResolveLiveWeaponEntityHandle(lastWeapon, targetPawnIndex, targetControllerIndex, out uint lastWeaponHandleRaw))
            {
                AddUniqueEntityHandle(targetEntities, lastWeaponHandleRaw);
            }

            var csWeaponServices = weaponServices.As<CCSPlayer_WeaponServices>();
            if (csWeaponServices != null)
            {
                var savedWeapon = csWeaponServices.SavedWeapon;
                if (TryResolveLiveWeaponEntityHandle(savedWeapon, targetPawnIndex, targetControllerIndex, out uint savedWeaponHandleRaw))
                {
                    AddUniqueEntityHandle(targetEntities, savedWeaponHandleRaw);
                }
            }

            var myWeapons = weaponServices.MyWeapons;
            int myWeaponCount = myWeapons.Count;
            for (int i = 0; i < myWeaponCount; i++)
            {
                var weaponHandle = myWeapons[i];
                if (!TryResolveLiveWeaponEntityHandle(weaponHandle, targetPawnIndex, targetControllerIndex, out uint weaponHandleRaw))
                {
                    continue;
                }

                AddUniqueEntityHandle(targetEntities, weaponHandleRaw);
            }
        }
        catch (Exception ex)
        {
            if (!_hasLoggedWeaponSyncError)
            {
                WarnLog(
                    "Weapon data sync failed once.",
                    "A temporary game-state issue happened while building weapon visibility data.",
                    "S2AWH continued safely and will retry on the next transmit callback."
                );
                DebugLog(
                    "Weapon sync error details were captured.",
                    $"Error message: {ex.Message}",
                    "This event is logged once to avoid console spam."
                );
                _hasLoggedWeaponSyncError = true;
            }
        }

        SanitizeTargetEntityList(targetEntities, nowTick);
        return true;
    }

    private static void AddUniqueEntityHandle(TargetTransmitEntities targetEntities, uint entityHandleRaw)
    {
        int entityIndex = (int)(entityHandleRaw & (Utilities.MaxEdicts - 1));
        if (entityIndex <= 0 || entityIndex >= Utilities.MaxEdicts)
        {
            return;
        }

        int count = targetEntities.Count;
        for (int i = 0; i < count; i++)
        {
            if (targetEntities.RawHandles[i] == entityHandleRaw)
            {
                return;
            }
        }

        if (count >= targetEntities.RawHandles.Length)
        {
            Array.Resize(ref targetEntities.RawHandles, targetEntities.RawHandles.Length * 2);
        }

        targetEntities.RawHandles[count] = entityHandleRaw;
        targetEntities.Count = count + 1;
    }

    private bool TryResolveLiveWeaponEntityHandle(CHandle<CBasePlayerWeapon> weaponHandle, int targetPawnIndex, int targetControllerIndex, out uint entityHandleRaw)
    {
        entityHandleRaw = 0;

        if (!weaponHandle.IsValid)
        {
            return false;
        }

        uint rawHandle = weaponHandle.Raw;
        IntPtr? weaponPointer = EntitySystem.GetEntityByHandle(rawHandle);
        if (!weaponPointer.HasValue || weaponPointer.Value == IntPtr.Zero)
        {
            return false;
        }

        CBasePlayerWeapon weaponEntity;
        try
        {
            weaponEntity = new CBasePlayerWeapon(weaponPointer.Value);
        }
        catch
        {
            return false;
        }

        if (!weaponEntity.IsValid)
        {
            return false;
        }

        // Owner can be transiently invalid during rapid inventory/model updates.
        // Accept unresolved-owner states, but keep strict mismatch reject when owner is resolved.
        var ownerHandle = weaponEntity.OwnerEntity;
        if (ownerHandle.IsValid)
        {
            IntPtr? ownerPointer = EntitySystem.GetEntityByHandle(ownerHandle.Raw);
            if (ownerPointer.HasValue && ownerPointer.Value != IntPtr.Zero)
            {
                try
                {
                    var ownerEntity = new CEntityInstance(ownerPointer.Value);
                    if (ownerEntity.IsValid)
                    {
                        int ownerIndex = (int)ownerEntity.Index;
                        if (ownerIndex != targetPawnIndex && ownerIndex != targetControllerIndex)
                        {
                            return false;
                        }
                    }
                }
                catch
                {
                    // Ignore transient owner resolution errors and use weapon handle from player weapon list.
                }
            }
        }

        entityHandleRaw = weaponEntity.EntityHandle.Raw;
        int entityIndex = (int)(entityHandleRaw & (Utilities.MaxEdicts - 1));
        return entityIndex > 0 && entityIndex < Utilities.MaxEdicts;
    }

    private void SanitizeTargetEntityList(TargetTransmitEntities targetEntities, int nowTick)
    {
        int writeIndex = 0;
        int count = targetEntities.Count;
        for (int i = 0; i < count; i++)
        {
            uint entityHandleRaw = targetEntities.RawHandles[i];
            if (!TryResolveEntityHandleIndexForTransmit(entityHandleRaw, nowTick, out _))
            {
                continue;
            }

            targetEntities.RawHandles[writeIndex++] = entityHandleRaw;
        }

        targetEntities.Count = writeIndex;
    }

    private bool TryResolveEntityHandleIndexForTransmit(uint entityHandleRaw, int nowTick, out int entityIndex)
    {
        entityIndex = 0;

        var handle = new CEntityHandle(entityHandleRaw);
        if (!handle.IsValid)
        {
            return false;
        }

        int index = (int)handle.Index;
        if (index <= 0 || index >= Utilities.MaxEdicts)
        {
            return false;
        }

        entityIndex = index;

        if (_entityHandleIndexCacheTick != nowTick)
        {
            _entityHandleIndexCacheTick = nowTick;
            _entityHandleIndexCache.Clear();
        }

        if (_entityHandleIndexCache.TryGetValue(entityHandleRaw, out int cachedIndex))
        {
            if (cachedIndex <= 0)
            {
                return false;
            }

            entityIndex = cachedIndex;
            return true;
        }

        IntPtr? entityPointer = EntitySystem.GetEntityByHandle(entityHandleRaw);
        bool isValid = entityPointer.HasValue && entityPointer.Value != IntPtr.Zero;
        _entityHandleIndexCache[entityHandleRaw] = isValid ? index : -1;
        if (!isValid)
        {
            return false;
        }

        entityIndex = index;
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
                    "A visibility check failed once.",
                    "A temporary trace or entity-state issue happened during runtime.",
                    "This pair is handled safely with temporary-unknown fallback logic."
                );
                DebugLog(
                    "Visibility error details were captured.",
                    $"Failure happened during {phase}. Error message: {ex.Message}",
                    "No crash will occur. Safety fallback remains active."
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
                    "Player data is not ready yet.",
                    "The game is still initializing global variables.",
                    "S2AWH will wait safely and resume automatically."
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
                    "Live player scan failed once.",
                    "A temporary runtime state prevented S2AWH from reading player data.",
                    "S2AWH skipped this tick safely and will retry automatically."
                );
                DebugLog(
                    "Live player scan error details were captured.",
                    $"Error message: {ex.Message}",
                    "This event is logged once to avoid console spam."
                );
                _hasLoggedPlayerScanError = true;
            }

            _cachedLivePlayers.Clear();
            _cachedLivePlayersValid = false;
            livePlayers = _cachedLivePlayers;
            return false;
        }
    }

    private void RemoveTargetSlotFromRevealRows(int targetSlot)
    {
        if ((uint)targetSlot >= VisibilitySlotCapacity || _revealHoldRows.Count == 0)
        {
            return;
        }

        _viewerSlotsToRemove.Clear();
        foreach (var rowEntry in _revealHoldRows)
        {
            var row = rowEntry.Value;
            if (!row.Known[targetSlot])
            {
                continue;
            }

            row.Known[targetSlot] = false;
            row.HoldUntilTick[targetSlot] = 0;
            row.ActiveCount--;
            if (row.ActiveCount <= 0)
            {
                _viewerSlotsToRemove.Add(rowEntry.Key);
            }
        }

        foreach (int viewerSlot in _viewerSlotsToRemove)
        {
            _revealHoldRows.Remove(viewerSlot);
        }
    }

    private void RemoveTargetSlotFromStableRows(int targetSlot)
    {
        if ((uint)targetSlot >= VisibilitySlotCapacity || _stableDecisionRows.Count == 0)
        {
            return;
        }

        _viewerSlotsToRemove.Clear();
        foreach (var rowEntry in _stableDecisionRows)
        {
            var row = rowEntry.Value;
            if (!row.Known[targetSlot])
            {
                continue;
            }

            row.Known[targetSlot] = false;
            row.Decisions[targetSlot] = false;
            row.Ticks[targetSlot] = 0;
            row.ActiveCount--;
            if (row.ActiveCount <= 0)
            {
                _viewerSlotsToRemove.Add(rowEntry.Key);
            }
        }

        foreach (int viewerSlot in _viewerSlotsToRemove)
        {
            _stableDecisionRows.Remove(viewerSlot);
        }
    }

    private void RemoveInactiveViewerRows<TValue>(Dictionary<int, TValue> byViewerCache)
    {
        if (byViewerCache.Count <= _activeViewerSlots.Count)
        {
            return;
        }

        _viewerSlotsToRemove.Clear();
        foreach (int viewerSlot in byViewerCache.Keys)
        {
            if (!_activeViewerSlots.Contains(viewerSlot))
            {
                _viewerSlotsToRemove.Add(viewerSlot);
            }
        }

        foreach (int viewerSlot in _viewerSlotsToRemove)
        {
            byViewerCache.Remove(viewerSlot);
        }
    }

    private void InfoLog(string whatHappened, string whyHappened, string result)
    {
        WriteLog(LevelInformation, whatHappened, whyHappened, result);
    }

    private void WarnLog(string whatHappened, string whyHappened, string result)
    {
        WriteLog(LevelWarning, whatHappened, whyHappened, result);
    }

    private void DebugLog(string whatHappened, string whyHappened, string result)
    {
        if (!_collectDebugCounters)
        {
            return;
        }

        WriteLog(LevelDebug, whatHappened, whyHappened, result);
    }

    private void DebugSummaryLog()
    {
        if (!_collectDebugCounters)
        {
            return;
        }

        var config = S2AWHState.Current;
        int serverTick = Server.TickCount;
        bool hasLivePlayers = TryGetLivePlayers(serverTick, out var livePlayers);
        int livePlayerCount = hasLivePlayers ? livePlayers.Count : 0;
        int humanPlayerCount = 0;
        int botPlayerCount = 0;

        if (hasLivePlayers)
        {
            foreach (var player in livePlayers)
            {
                if (player.IsBot)
                {
                    botPlayerCount++;
                }
                else
                {
                    humanPlayerCount++;
                }
            }
        }

        int visibilityPairCount = CountVisibilityPairEntries(livePlayerCount);
        int revealHoldPairCount = CountPairEntries(_revealHoldRows);
        int stableDecisionPairCount = CountPairEntries(_stableDecisionRows);

        int configuredRayPoints = config.Trace.RayTracePoints;
        int estimatedLosRaysPerSnapshot = livePlayerCount > 1
            ? livePlayerCount * (livePlayerCount - 1) * configuredRayPoints
            : 0;

        float snapshotsPerSecond = 0.0f;
        if (config.Core.UpdateFrequencyTicks > 0 && Server.TickInterval > 0.0f)
        {
            snapshotsPerSecond = 1.0f / (config.Core.UpdateFrequencyTicks * Server.TickInterval);
        }

        string healthStatus = _transmitFilter == null
            ? "waiting for RayTrace"
            : hasLivePlayers
                ? "active"
                : "active (waiting for live player data)";

        var lines = new List<string>(16)
        {
            $"Runtime summary for the last {DebugSummaryIntervalTicks} ticks.",
            $"S2AWH status: {healthStatus}.",
            $"Server tick: {serverTick}. Update interval: every {config.Core.UpdateFrequencyTicks} tick(s).",
            $"Snapshot rate: about {snapshotsPerSecond:F2} per second.",
            hasLivePlayers
                ? $"Live players: {livePlayerCount} total ({humanPlayerCount} humans, {botPlayerCount} bots)."
                : "Live player data is not available yet, so player counts are temporarily unknown.",
            $"Visibility cache: {_visibilityCache.Count} viewer row(s), {visibilityPairCount} stored viewer-target decision(s).",
            $"Memory helpers: reveal hold {revealHoldPairCount} pair(s), stability memory {stableDecisionPairCount} pair(s).",
            $"Trace points per check: {configuredRayPoints}. Estimated rays per full snapshot: {estimatedLosRaysPerSnapshot}.",
            $"Transmit callbacks: {_transmitCallbacksInWindow}. Hidden entities: {_transmitHiddenEntitiesInWindow}.",
            $"Extra safety checks: {_transmitFallbackChecksInWindow}. No-change removals: {_transmitRemovalNoEffectInWindow}.",
            $"Reveal hold events -> refresh: {_holdRefreshInWindow}, keep-alive: {_holdHitKeepAliveInWindow}, expire: {_holdExpiredInWindow}.",
            $"Temporary-unknown handling -> total: {_unknownEvalInWindow}, recent-decision reuse: {_unknownStickyHitInWindow}, reveal-hold reuse: {_unknownHoldHitInWindow}, forced hidden: {_unknownForcedHiddenInWindow}, from exceptions: {_unknownFromExceptionInWindow}.",
            "Use this summary to confirm that protection is running as expected."
        };

        WriteLevelBox(LevelDebug, lines);
    }

    private int CountVisibilityPairEntries(int livePlayerCount)
    {
        if (livePlayerCount <= 1)
        {
            return 0;
        }

        int total = 0;
        foreach (var row in _visibilityCache.Values)
        {
            var known = row.Known;
            for (int i = 0; i < known.Length; i++)
            {
                if (known[i])
                {
                    total++;
                }
            }
        }

        return total;
    }

    private static int CountPairEntries(Dictionary<int, RevealHoldRow> byViewerMap)
    {
        int total = 0;
        foreach (var row in byViewerMap.Values)
        {
            total += row.ActiveCount;
        }
        return total;
    }

    private static int CountPairEntries(Dictionary<int, StableDecisionRow> byViewerMap)
    {
        int total = 0;
        foreach (var row in byViewerMap.Values)
        {
            total += row.ActiveCount;
        }
        return total;
    }

    private void WriteLog(string level, string whatHappened, string whyHappened, string result)
    {
        string sentence = BuildCompactSentence(whatHappened, whyHappened, result);
        WriteLevelLine(level, sentence);
    }

    private void WriteLevelBox(string level, List<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        const int maxInnerWidth = 92;
        var wrappedLines = new List<string>(lines.Count * 2);
        int widest = 0;

        foreach (var line in lines)
        {
            foreach (var wrapped in WrapLine(line, maxInnerWidth))
            {
                wrappedLines.Add(wrapped);
                if (wrapped.Length > widest)
                {
                    widest = wrapped.Length;
                }
            }
        }

        if (widest < 24)
        {
            widest = 24;
        }

        string border = "+" + new string('-', widest + 2) + "+";
        WriteLevelLine(level, border);
        foreach (var line in wrappedLines)
        {
            WriteLevelLine(level, $"| {line.PadRight(widest)} |");
        }
        WriteLevelLine(level, border);
    }

    private static IEnumerable<string> WrapLine(string text, int maxWidth)
    {
        string normalized = NormalizeClause(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield return string.Empty;
            yield break;
        }

        int index = 0;
        while (index < normalized.Length)
        {
            int remaining = normalized.Length - index;
            if (remaining <= maxWidth)
            {
                yield return normalized[index..];
                yield break;
            }

            int end = index + maxWidth;
            int split = normalized.LastIndexOf(' ', end - 1, maxWidth);
            if (split <= index)
            {
                split = end;
            }

            yield return normalized[index..split].TrimEnd();
            index = split;
            while (index < normalized.Length && normalized[index] == ' ')
            {
                index++;
            }
        }
    }

    private void WriteLevelLine(string level, string text)
    {
        string color = ResolveLevelColor(level);
        Console.WriteLine($"{color}[S2AWH][{level}]{LogColorReset} {text}{LogColorReset}");
    }

    private static string ResolveLevelColor(string level)
    {
        return level switch
        {
            LevelWarning => LogColorWarning,
            LevelDebug => LogColorDebug,
            _ => LogColorInformation
        };
    }

    private static string BuildCompactSentence(string whatHappened, string whyHappened, string result)
    {
        string what = NormalizeClause(whatHappened);
        string why = NormalizeClause(whyHappened);
        string res = NormalizeClause(result);

        string sentence = what;
        if (!string.IsNullOrWhiteSpace(why))
        {
            sentence = $"{sentence} because {LowercaseFirst(why)}";
        }

        if (!string.IsNullOrWhiteSpace(res))
        {
            sentence = $"{sentence}, {LowercaseFirst(res)}";
        }

        sentence = EnsureSentenceEnding(sentence);
        if (sentence.Length > MaxLogSentenceLength)
        {
            sentence = sentence[..(MaxLogSentenceLength - 3)] + "...";
        }

        return sentence;
    }

    private static string NormalizeClause(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (normalized.Contains("  ", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
        }

        normalized = TrimPrefix(normalized, "So ");
        normalized = TrimPrefix(normalized, "So, ");
        normalized = TrimPrefix(normalized, "Therefore ");
        normalized = TrimPrefix(normalized, "Therefore, ");
        normalized = TrimPrefix(normalized, "As a result ");
        normalized = TrimPrefix(normalized, "As a result, ");
        normalized = normalized.TrimEnd('.', ';', ':', ' ');
        return normalized;
    }

    private static string TrimPrefix(string value, string prefix)
    {
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..].TrimStart()
            : value;
    }

    private static string LowercaseFirst(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (value.Length == 1)
        {
            return char.IsUpper(value[0])
                ? char.ToLowerInvariant(value[0]).ToString()
                : value;
        }

        // Only lowercase sentence-like words (e.g. "The ...").
        // Keep product names/acronyms like "S2AWH" unchanged.
        if (char.IsUpper(value[0]) && char.IsLower(value[1]))
        {
            return char.ToLowerInvariant(value[0]) + value[1..];
        }

        return value;
    }

    private static string EnsureSentenceEnding(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ".";
        }

        char last = value[^1];
        return last is '.' or '!' or '?' ? value : value + ".";
    }
}
