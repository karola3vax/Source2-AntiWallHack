using System.Numerics;
using CounterStrikeSharp.API.Core;
using S2FOW.Config;
using S2FOW.Models;
using S2FOW.Util;

namespace S2FOW.Core;

public enum RoundPhase
{
    Warmup = 0,
    FreezeTime = 1,
    Live = 2,
    PostPlant = 3,
    RoundEnd = 4
}

public class VisibilityManager
{
    private enum ObserverFovBucket
    {
        Full = 0,
        Peripheral = 1,
        Rear = 2
    }

    private readonly RaycastEngine _raycastEngine;
    private readonly VisibilityCache _visibilityCache;
    private readonly SmokeTracker _smokeTracker;
    private readonly S2FOWConfig _config;
    private readonly PerformanceMonitor? _perfMonitor;
    private readonly AdaptiveQualityScaler _qualityScaler = new();
    private readonly Action<string>? _logger;
    // Cache the tick interval once instead of fetching it repeatedly.
    private readonly float _tickInterval;
    // Hard ceiling on effective cache TTL (after velocity extension + stagger)
    // to prevent extreme staleness at XFar range. 40 ticks ≈ 625ms at 64 tick.
    private const int MaxEffectiveCacheTTL = 40;

    // All enemies stay visible until this round-start grace tick expires.
    private int _roundStartGraceUntilTick;

    // Recently dead players stay visible until this per-slot tick expires.
    private readonly int[] _deathForceTransmitUntil = new int[FowConstants.MaxSlots];
    // Newly spawned players stay visible briefly so the client can receive
    // the pawn and child-entity chain before hide logic removes links.
    private readonly int[] _spawnForceTransmitUntil = new int[FowConstants.MaxSlots];

    // Tracks hidden-to-visible transitions per observer-target pair for flex resync.
    private readonly bool[] _wasHidden = new bool[FowConstants.MaxSlots * FowConstants.MaxSlots];

    // Target slots that became visible this frame and need a flex resync.
    private readonly bool[] _needsFlexResync = new bool[FowConstants.MaxSlots];
    private readonly int[] _observerAimTraceCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerSkeletonTraceCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerAabbTraceCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerTargetCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerRoundStartCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerDeathForceCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerDistanceCullCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerSmokeBlockCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerCrosshairRevealCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerCacheHitCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerLiveLosCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerPeekGraceCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerBudgetReuseCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerBudgetFailClosedCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerBudgetFailOpenCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerFovFullCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerFovPeripheralCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerFovRearCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _aimRevealCacheTicks = new int[FowConstants.MaxSlots];
    private readonly bool[] _aimRevealCacheValid = new bool[FowConstants.MaxSlots];
    private readonly float[] _aimRevealEndX = new float[FowConstants.MaxSlots];
    private readonly float[] _aimRevealEndY = new float[FowConstants.MaxSlots];
    private readonly float[] _aimRevealEndZ = new float[FowConstants.MaxSlots];
    private readonly int[] _observerDebugPointCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerDebugFallbackPointCounts = new int[FowConstants.MaxSlots];
    private readonly Vector3[] _observerDebugPoints = new Vector3[FowConstants.MaxSlots * RaycastEngine.MaxDebugPointsPerObserver];
    private readonly bool[] _observerDebugPointFallbacks = new bool[FowConstants.MaxSlots * RaycastEngine.MaxDebugPointsPerObserver];
    private readonly int[] _observerDebugLineCounts = new int[FowConstants.MaxSlots];
    private readonly Vector3[] _observerDebugLineStarts = new Vector3[FowConstants.MaxSlots * RaycastEngine.MaxDebugLinesPerObserver];
    private readonly Vector3[] _observerDebugLineEnds = new Vector3[FowConstants.MaxSlots * RaycastEngine.MaxDebugLinesPerObserver];
    private readonly Vector3[] _debugScratchPoints = new Vector3[RaycastEngine.MaxVisibilityTestPoints];
    private readonly bool[] _debugScratchFallbacks = new bool[RaycastEngine.MaxVisibilityTestPoints];
    private readonly float _maxRelevanceDistanceSqr;
    private readonly BudgetExceededPolicy _budgetExceededPolicy;
    // Pre-computed squared distance thresholds for tier classification.
    private readonly float _cqbDistSqr;
    private readonly float _midDistSqr;
    private readonly float _farDistSqr;
    private readonly float _stationaryThresholdSqr;
    private readonly float _slowMoveThresholdSqr;
    private readonly bool _fovSamplingEnabled;
    private readonly float _fullFovDotThreshold;
    private readonly float _peripheralFovDotThreshold;
    // Observer phase spread: pre-computed from config.
    private readonly bool _observerPhaseSpreadEnabled;
    private readonly int _observerPhaseSpreadTicks;
    // Smoke batch pre-filter margin: max check-point offset from target center.
    // Weapon muzzle tip is ~50u from center; AABB corners add ~16u padding.
    private const float SmokePreFilterMargin = 60.0f;
    private long _budgetFallbackCacheReuseCount;
    private long _budgetFallbackOpenTransmitCount;
    private long _budgetCacheOnlyHideCount;
    private long _totalSmokePreFilterSkips;
    private RoundPhase _currentRoundPhase = RoundPhase.Live;
    public VisibilityManager(
        RaycastEngine raycastEngine,
        VisibilityCache visibilityCache,
        SmokeTracker smokeTracker,
        S2FOWConfig config,
        PerformanceMonitor? perfMonitor = null,
        Action<string>? logger = null)
    {
        _raycastEngine = raycastEngine;
        _visibilityCache = visibilityCache;
        _smokeTracker = smokeTracker;
        _config = config;
        _perfMonitor = perfMonitor;
        _logger = logger;
        _maxRelevanceDistanceSqr = config.AntiWallhack.MaxVisibilityDistance > 0.0f
            ? config.AntiWallhack.MaxVisibilityDistance * config.AntiWallhack.MaxVisibilityDistance
            : 0.0f;
        _budgetExceededPolicy = config.Performance.BudgetExceededPolicy;
        // Cache the engine tick interval once for the lifetime of the manager.
        _tickInterval = raycastEngine.TickInterval;

        // Pre-compute squared distance thresholds.
        _cqbDistSqr = config.Performance.CqbDistanceThreshold * config.Performance.CqbDistanceThreshold;
        _midDistSqr = config.Performance.MidDistanceThreshold * config.Performance.MidDistanceThreshold;
        _farDistSqr = config.Performance.FarDistanceThreshold * config.Performance.FarDistanceThreshold;
        _stationaryThresholdSqr = config.Performance.StationaryThresholdUnits * config.Performance.StationaryThresholdUnits;
        _slowMoveThresholdSqr = config.Performance.SlowMoveThresholdUnits * config.Performance.SlowMoveThresholdUnits;
        _fovSamplingEnabled = config.Performance.FovSamplingEnabled;
        float fullFovHalfAngle = Math.Clamp(config.Performance.FullDetailFovHalfAngleDegrees, 0.0f, 180.0f);
        float peripheralFovHalfAngle = Math.Clamp(config.Performance.PeripheralFovHalfAngleDegrees, fullFovHalfAngle, 180.0f);
        _fullFovDotThreshold = MathF.Cos(fullFovHalfAngle * (MathF.PI / 180.0f));
        _peripheralFovDotThreshold = MathF.Cos(peripheralFovHalfAngle * (MathF.PI / 180.0f));
        _observerPhaseSpreadEnabled = config.Performance.ObserverPhaseSpreadEnabled;
        _observerPhaseSpreadTicks = Math.Max(1, config.Performance.ObserverPhaseSpreadTicks);
    }

    /// <summary>
    /// Resets frame-local counters and state before CheckTransmit runs.
    /// </summary>
    public void BeginFrame()
    {
        AdaptiveQualityScaler.QualityLevel previousQualityLevel = _qualityScaler.CurrentLevel;
        if (_perfMonitor != null && _perfMonitor.LastFrameIntervalMilliseconds > 0.0f)
        {
            _qualityScaler.RecordFrameTime(_perfMonitor.LastFrameIntervalMilliseconds);
            if (_qualityScaler.CurrentLevel != previousQualityLevel)
            {
                _logger?.Invoke(
                    $"Adaptive quality -> {_qualityScaler.CurrentLevel} " +
                    $"(avg {_qualityScaler.AverageFrameTimeMs:F2}ms over last 64 frames)");
            }
        }

        Array.Clear(_needsFlexResync);
        Array.Clear(_observerAimTraceCounts);
        Array.Clear(_observerSkeletonTraceCounts);
        Array.Clear(_observerAabbTraceCounts);
        Array.Clear(_observerTargetCounts);
        Array.Clear(_observerRoundStartCounts);
        Array.Clear(_observerDeathForceCounts);
        Array.Clear(_observerDistanceCullCounts);
        Array.Clear(_observerSmokeBlockCounts);
        Array.Clear(_observerCrosshairRevealCounts);
        Array.Clear(_observerCacheHitCounts);
        Array.Clear(_observerLiveLosCounts);
        Array.Clear(_observerPeekGraceCounts);
        Array.Clear(_observerBudgetReuseCounts);
        Array.Clear(_observerBudgetFailClosedCounts);
        Array.Clear(_observerBudgetFailOpenCounts);
        Array.Clear(_observerFovFullCounts);
        Array.Clear(_observerFovPeripheralCounts);
        Array.Clear(_observerFovRearCounts);
        Array.Clear(_observerDebugPointCounts);
        Array.Clear(_observerDebugFallbackPointCounts);
        Array.Clear(_observerDebugLineCounts);
        _smokeTracker.CullExpired(_lastBeginFrameTick, _config.AntiWallhack.SmokeLifetimeTicks);
    }

    public void SetFrameTick(int tick)
    {
        _lastBeginFrameTick = tick;
    }

    private int _lastBeginFrameTick;

    /// <summary>
    /// Returns true if the target slot needs a flex or network resync this frame.
    /// </summary>
    public bool NeedsFlexResync(int targetSlot)
    {
        return FowConstants.IsValidSlot(targetSlot) && _needsFlexResync[targetSlot];
    }

    public long BudgetFallbackCacheReuseCount => _budgetFallbackCacheReuseCount;
    public long BudgetFallbackOpenTransmitCount => _budgetFallbackOpenTransmitCount;
    public long BudgetCacheOnlyHideCount => _budgetCacheOnlyHideCount;
    public long SmokePreFilterSkips => _totalSmokePreFilterSkips;
    public AdaptiveQualityScaler.QualityLevel CurrentQualityLevel => _qualityScaler.CurrentLevel;
    public float AverageQualitySampleFrameTimeMs => _qualityScaler.AverageFrameTimeMs;
    public RoundPhase CurrentRoundPhase => _currentRoundPhase;

    public bool ShouldTransmitRecentlyDead(int targetSlot, int currentTick)
    {
        if (ShouldBypassVisibilityWorkForCurrentPhase())
            return true;

        return FowConstants.IsValidSlot(targetSlot) &&
               currentTick < _deathForceTransmitUntil[targetSlot];
    }

    public void GetObserverTraceCounts(
        int observerSlot,
        out int skeleton,
        out int aabb,
        out int aim,
        out int total,
        out int targets,
        out int debugPoints,
        out int debugFallbackPoints)
    {
        if (!FowConstants.IsValidSlot(observerSlot))
        {
            skeleton = 0;
            aabb = 0;
            aim = 0;
            total = 0;
            targets = 0;
            debugPoints = 0;
            debugFallbackPoints = 0;
            return;
        }

        aim = _observerAimTraceCounts[observerSlot];
        skeleton = _observerSkeletonTraceCounts[observerSlot];
        aabb = _observerAabbTraceCounts[observerSlot];
        total = skeleton + aabb + aim;
        targets = _observerTargetCounts[observerSlot];
        debugPoints = _observerDebugPointCounts[observerSlot];
        debugFallbackPoints = _observerDebugFallbackPointCounts[observerSlot];
    }

    public void GetObserverDecisionCounts(
        int observerSlot,
        out int roundStart,
        out int deathForce,
        out int distanceCull,
        out int smokeBlocked,
        out int crosshairReveal,
        out int cacheHit,
        out int liveLos,
        out int peekGrace,
        out int budgetReuse,
        out int budgetFailClosed,
        out int budgetFailOpen,
        out int fovFull,
        out int fovPeripheral,
        out int fovRear)
    {
        if (!FowConstants.IsValidSlot(observerSlot))
        {
            roundStart = 0;
            deathForce = 0;
            distanceCull = 0;
            smokeBlocked = 0;
            crosshairReveal = 0;
            cacheHit = 0;
            liveLos = 0;
            peekGrace = 0;
            budgetReuse = 0;
            budgetFailClosed = 0;
            budgetFailOpen = 0;
            fovFull = 0;
            fovPeripheral = 0;
            fovRear = 0;
            return;
        }

        roundStart = _observerRoundStartCounts[observerSlot];
        deathForce = _observerDeathForceCounts[observerSlot];
        distanceCull = _observerDistanceCullCounts[observerSlot];
        smokeBlocked = _observerSmokeBlockCounts[observerSlot];
        crosshairReveal = _observerCrosshairRevealCounts[observerSlot];
        cacheHit = _observerCacheHitCounts[observerSlot];
        liveLos = _observerLiveLosCounts[observerSlot];
        peekGrace = _observerPeekGraceCounts[observerSlot];
        budgetReuse = _observerBudgetReuseCounts[observerSlot];
        budgetFailClosed = _observerBudgetFailClosedCounts[observerSlot];
        budgetFailOpen = _observerBudgetFailOpenCounts[observerSlot];
        fovFull = _observerFovFullCounts[observerSlot];
        fovPeripheral = _observerFovPeripheralCounts[observerSlot];
        fovRear = _observerFovRearCounts[observerSlot];
    }

    public int FillObserverDebugPoints(int observerSlot, Span<Vector3> output, Span<bool> aabbFallbackOutput)
    {
        if (!FowConstants.IsValidSlot(observerSlot))
            return 0;

        int count = Math.Min(_observerDebugPointCounts[observerSlot], output.Length);
        int baseIndex = observerSlot * RaycastEngine.MaxDebugPointsPerObserver;
        for (int i = 0; i < count; i++)
        {
            output[i] = _observerDebugPoints[baseIndex + i];
            if (i < aabbFallbackOutput.Length)
                aabbFallbackOutput[i] = _observerDebugPointFallbacks[baseIndex + i];
        }

        return count;
    }

    public int FillObserverDebugLines(int observerSlot, Span<Vector3> startOutput, Span<Vector3> endOutput)
    {
        if (!FowConstants.IsValidSlot(observerSlot))
            return 0;

        int count = Math.Min(_observerDebugLineCounts[observerSlot], Math.Min(startOutput.Length, endOutput.Length));
        int baseIndex = observerSlot * RaycastEngine.MaxDebugLinesPerObserver;
        for (int i = 0; i < count; i++)
        {
            startOutput[i] = _observerDebugLineStarts[baseIndex + i];
            endOutput[i] = _observerDebugLineEnds[baseIndex + i];
        }

        return count;
    }

    /// <summary>
    /// Decides whether the target should be transmitted to the observer this frame.
    /// Returns true to transmit and false to hide.
    /// </summary>
    public bool ShouldTransmit(in PlayerSnapshot observer, in PlayerSnapshot target, int currentTick)
    {
        int pairIndex = observer.Slot * FowConstants.MaxSlots + target.Slot;
        if (ShouldBypassVisibilityWorkForCurrentPhase())
        {
            bool wasHiddenDuringRestrictedPhase = _wasHidden[pairIndex];
            _wasHidden[pairIndex] = false;
            if (wasHiddenDuringRestrictedPhase)
                MarkFlexResync(target.Slot);
            return true;
        }

        bool previouslyHidden = _wasHidden[pairIndex];
        if (FowConstants.IsValidSlot(observer.Slot))
            _observerTargetCounts[observer.Slot]++;

        // Keep everyone visible during the round-start grace window.
        if (currentTick < _roundStartGraceUntilTick)
        {
            if (FowConstants.IsValidSlot(observer.Slot))
                _observerRoundStartCounts[observer.Slot]++;
            _wasHidden[pairIndex] = false;
            return true;
        }

        // Keep recently killed players visible for a short safety window.
        if (currentTick < _deathForceTransmitUntil[target.Slot])
        {
            if (FowConstants.IsValidSlot(observer.Slot))
                _observerDeathForceCounts[observer.Slot]++;
            _wasHidden[pairIndex] = false;
            return true;
        }

        if (currentTick < _spawnForceTransmitUntil[target.Slot])
        {
            _wasHidden[pairIndex] = false;
            return true;
        }

        // Compute distance once — reused for max-relevance, tier selection, and smoke.
        float distSqr = VectorMath.DistanceSquared(in observer, in target);

        // Very distant targets can be rejected before any deeper checks.
        if (_maxRelevanceDistanceSqr > 0.0f && distSqr > _maxRelevanceDistanceSqr)
        {
            if (FowConstants.IsValidSlot(observer.Slot))
                _observerDistanceCullCounts[observer.Slot]++;
            _visibilityCache.SetSimple(observer.Slot, target.Slot, false, currentTick);
            _wasHidden[pairIndex] = true;
            return false;
        }

        // Determine distance tier and corresponding cache / point parameters.
        GetTierParameters(distSqr,
            out int hiddenTTL, out int visibleTTL, out int maxCheckPoints);

        ObserverFovBucket fovBucket = ClassifyObserverFov(in observer, in target);
        switch (fovBucket)
        {
            case ObserverFovBucket.Full:
                if (FowConstants.IsValidSlot(observer.Slot))
                    _observerFovFullCounts[observer.Slot]++;
                break;
            case ObserverFovBucket.Peripheral:
                if (FowConstants.IsValidSlot(observer.Slot))
                    _observerFovPeripheralCounts[observer.Slot]++;
                maxCheckPoints = Math.Max(1, (maxCheckPoints + 1) / 2);
                break;
            case ObserverFovBucket.Rear:
                if (FowConstants.IsValidSlot(observer.Slot))
                    _observerFovRearCounts[observer.Slot]++;
                _visibilityCache.SetSimple(observer.Slot, target.Slot, false, currentTick);
                _wasHidden[pairIndex] = true;
                return false;
        }

        // Stagger: deterministic per-pair offset spreads cache expirations across ticks.
        int staggerOffset = 0;
        if (_config.Performance.StaggeredCacheExpiryEnabled && hiddenTTL > 1)
            staggerOffset = pairIndex % hiddenTTL;

        // Graduated velocity-aware cache extension.
        // Four tiers based on movement speed since last evaluation:
        //   Both stationary (<6u):          3x TTL — holding angles, no position change
        //   One stationary, one slow (<20u): 3x TTL — anchor + slow peek
        //   One stationary OR both slow:     2x TTL — moderate movement
        //   Both moving:                     1x TTL — active fight, needs fresh data
        int velocityMultiplier = 1;
        if (_config.Performance.VelocityCacheExtensionEnabled)
        {
            if (_visibilityCache.TryGetMovementSinceLastEval(
                    observer.Slot, target.Slot,
                    observer.EyePosX, observer.EyePosY,
                    target.PosX, target.PosY,
                    out float obsMoveSqr, out float tgtMoveSqr))
            {
                bool obsStationary = obsMoveSqr < _stationaryThresholdSqr;
                bool tgtStationary = tgtMoveSqr < _stationaryThresholdSqr;
                bool obsSlow = obsMoveSqr < _slowMoveThresholdSqr;
                bool tgtSlow = tgtMoveSqr < _slowMoveThresholdSqr;

                if (obsStationary && tgtStationary)
                    velocityMultiplier = 3;
                else if ((obsStationary && tgtSlow) || (obsSlow && tgtStationary))
                    velocityMultiplier = 3;
                else if (obsStationary || tgtStationary)
                    velocityMultiplier = 2;
                else if (obsSlow && tgtSlow)
                    velocityMultiplier = 2;
            }
        }

        int effectiveHiddenTTL = Math.Min(hiddenTTL * velocityMultiplier + staggerOffset, MaxEffectiveCacheTTL);
        int effectiveVisibleTTL = Math.Min(visibleTTL * velocityMultiplier, MaxEffectiveCacheTTL);
        if (velocityMultiplier > 1)
            _perfMonitor?.RecordVelocityCacheExtension();

        // Observer phase spread: add a stable per-observer offset to hidden TTL
        // so different observers' caches expire on different ticks, preventing
        // thundering-herd spikes when many caches invalidate simultaneously.
        if (_observerPhaseSpreadEnabled)
        {
            int phaseWindow = Math.Max(1, _observerPhaseSpreadTicks + _qualityScaler.ExtraPhaseSpreadTicks());
            int phaseOffset = observer.Slot % phaseWindow;
            effectiveHiddenTTL = Math.Min(effectiveHiddenTTL + phaseOffset, MaxEffectiveCacheTTL);
        }

        // Stagger is now baked into the effective TTLs; pass zero to the cache lookup.
        staggerOffset = 0;

        // Compute both predicted and fixed observer origins.
        RaycastMath.ComputeObserverRayOrigin(in observer, _config, _tickInterval,
            out float eyeOriginX, out float eyeOriginY, out float eyeOriginZ);
        RaycastMath.ComputeObserverRayOriginNoPrediction(in observer, _config,
            out float fixedEyeOriginX, out float fixedEyeOriginY, out float fixedEyeOriginZ);

        // Debug target points should stay visible even when runtime visibility
        // is served by cache, crosshair reveal, or other early-exit paths.
        AppendDebugPointsForObserver(observer.Slot, in target, eyeOriginX, eyeOriginY, eyeOriginZ, currentTick, maxCheckPoints);

        // Smoke should block before cache reuse or crosshair reveal.
        // Batch pre-filter: skip the expensive per-point smoke check when
        // the observer-target line is far from all active smoke spheres.
        if (_config.AntiWallhack.SmokeBlocksWallhack &&
            _smokeTracker.ActiveCount > 0 &&
            (!_config.Performance.SmokeBatchPreFilterEnabled ||
             _smokeTracker.IsLineNearAnySmoke(
                 eyeOriginX, eyeOriginY, eyeOriginZ,
                 target.PosX, target.PosY, target.PosZ + target.ViewOffsetZ * 0.5f,
                 _config.AntiWallhack.SmokeBlockRadius, SmokePreFilterMargin,
                 currentTick, _config.AntiWallhack.SmokeLifetimeTicks)) &&
            IsFullyBlockedBySmoke(observer.Slot, in target, eyeOriginX, eyeOriginY, eyeOriginZ, currentTick, maxCheckPoints))
        {
            if (FowConstants.IsValidSlot(observer.Slot))
                _observerSmokeBlockCounts[observer.Slot]++;
            _visibilityCache.SetSimple(observer.Slot, target.Slot, false, currentTick);
            _wasHidden[pairIndex] = true;
            return false;
        }

        if (TryForceRevealFromCrosshair(in observer, in target, currentTick))
        {
            if (FowConstants.IsValidSlot(observer.Slot))
                _observerCrosshairRevealCounts[observer.Slot]++;
            _visibilityCache.Set(observer.Slot, target.Slot, true, currentTick,
                observer.EyePosX, observer.EyePosY, target.PosX, target.PosY);
            _wasHidden[pairIndex] = false;
            if (previouslyHidden)
                MarkFlexResync(target.Slot);
            return true;
        }

        _perfMonitor?.RecordEvaluation();
        if (_visibilityCache.TryGet(observer.Slot, target.Slot, currentTick,
                effectiveVisibleTTL, effectiveHiddenTTL,
                staggerOffset,
                out bool cachedVisible, out _))
        {
            _perfMonitor?.RecordCacheHit();
            if (FowConstants.IsValidSlot(observer.Slot))
                _observerCacheHitCounts[observer.Slot]++;
            _wasHidden[pairIndex] = !cachedVisible;
            if (cachedVisible && previouslyHidden)
                MarkFlexResync(target.Slot);
            return cachedVisible;
        }

        // Run the multi-point line-of-sight check.
        if (FowConstants.IsValidSlot(observer.Slot))
            _observerLiveLosCounts[observer.Slot]++;

        // Reuse the pre-computed eye origin for the expensive visibility check.
        var visibilityResult = _raycastEngine.CheckVisibility(
            in target,
            eyeOriginX, eyeOriginY, eyeOriginZ,
            fixedEyeOriginX, fixedEyeOriginY, fixedEyeOriginZ,
            currentTick,
            maxCheckPoints);
        if (FowConstants.IsValidSlot(observer.Slot))
        {
            _observerSkeletonTraceCounts[observer.Slot] += visibilityResult.TraceCounts.Skeleton;
            _observerAabbTraceCounts[observer.Slot] += visibilityResult.TraceCounts.Aabb;
        }

        if (visibilityResult.BudgetExceeded)
        {
            _perfMonitor?.RecordBudgetExceeded();
            if (_visibilityCache.TryGetRaw(observer.Slot, target.Slot, out bool rawVisible, out _))
            {
                _budgetFallbackCacheReuseCount++;
                if (FowConstants.IsValidSlot(observer.Slot))
                    _observerBudgetReuseCounts[observer.Slot]++;
                _wasHidden[pairIndex] = !rawVisible;
                if (rawVisible && previouslyHidden)
                    MarkFlexResync(target.Slot);
                return rawVisible;
            }

            switch (_budgetExceededPolicy)
            {
                case BudgetExceededPolicy.FailOpen:
                    _budgetFallbackOpenTransmitCount++;
                    if (FowConstants.IsValidSlot(observer.Slot))
                        _observerBudgetFailOpenCounts[observer.Slot]++;
                    _wasHidden[pairIndex] = false;
                    return true;

                case BudgetExceededPolicy.FailClosed:
                    if (FowConstants.IsValidSlot(observer.Slot))
                        _observerBudgetFailClosedCounts[observer.Slot]++;
                    _wasHidden[pairIndex] = true;
                    return false;

                case BudgetExceededPolicy.CacheOnly:
                default:
                    _budgetCacheOnlyHideCount++;
                    if (FowConstants.IsValidSlot(observer.Slot))
                        _observerBudgetFailClosedCounts[observer.Slot]++;
                    _wasHidden[pairIndex] = true;
                    return false;
            }
        }

        if (visibilityResult.IsVisible)
        {
            _visibilityCache.Set(observer.Slot, target.Slot, true, currentTick,
                observer.EyePosX, observer.EyePosY, target.PosX, target.PosY);
            _wasHidden[pairIndex] = false;
            if (previouslyHidden)
                MarkFlexResync(target.Slot);
            return true;
        }

        // If the target was visible recently, keep it visible through the grace window.
        // Do not overwrite the cache entry here so the original visible timestamp is preserved.
        if (_config.AntiWallhack.PeekGracePeriodTicks > 0 &&
            _visibilityCache.TryGetRaw(observer.Slot, target.Slot, out bool wasVisible, out int lastTick))
        {
            if (wasVisible && (currentTick - lastTick) < _config.AntiWallhack.PeekGracePeriodTicks)
            {
                if (FowConstants.IsValidSlot(observer.Slot))
                    _observerPeekGraceCounts[observer.Slot]++;
                _wasHidden[pairIndex] = false;
                return true;
            }
        }

        // The grace window expired, or the target was never visible. Mark it hidden now.
        _visibilityCache.Set(observer.Slot, target.Slot, false, currentTick,
            observer.EyePosX, observer.EyePosY, target.PosX, target.PosY);
        _wasHidden[pairIndex] = true;
        return false;
    }

    /// <summary>
    /// Selects cache TTLs and max trace points based on the squared distance between
    /// the observer and target. Uses pre-computed squared thresholds.
    /// </summary>
    private void GetTierParameters(float distSqr,
        out int hiddenTTL, out int visibleTTL, out int maxCheckPoints)
    {
        if (distSqr <= _cqbDistSqr)
        {
            hiddenTTL = _qualityScaler.ScaleCacheTTL(_config.Performance.CqbHiddenCacheTicks);
            visibleTTL = _qualityScaler.ScaleCacheTTL(_config.Performance.CqbVisibleCacheTicks);
            maxCheckPoints = RaycastEngine.VisibilityPrimitiveCount; // Full primitive set
        }
        else if (distSqr <= _midDistSqr)
        {
            hiddenTTL = _qualityScaler.ScaleCacheTTL(_config.Performance.MidHiddenCacheTicks);
            visibleTTL = _qualityScaler.ScaleCacheTTL(_config.Performance.MidVisibleCacheTicks);
            maxCheckPoints = RaycastEngine.VisibilityPrimitiveCount; // Full primitive set
        }
        else if (distSqr <= _farDistSqr)
        {
            hiddenTTL = _qualityScaler.ScaleCacheTTL(_config.Performance.FarHiddenCacheTicks);
            visibleTTL = _qualityScaler.ScaleCacheTTL(_config.Performance.FarVisibleCacheTicks);
            maxCheckPoints = _qualityScaler.ScaleCheckPoints(_config.Performance.FarMaxCheckPoints);
        }
        else
        {
            hiddenTTL = _qualityScaler.ScaleCacheTTL(_config.Performance.XFarHiddenCacheTicks);
            visibleTTL = _qualityScaler.ScaleCacheTTL(_config.Performance.XFarVisibleCacheTicks);
            maxCheckPoints = _qualityScaler.ScaleCheckPoints(_config.Performance.XFarMaxCheckPoints);
        }
    }

    private ObserverFovBucket ClassifyObserverFov(in PlayerSnapshot observer, in PlayerSnapshot target)
    {
        if (!_fovSamplingEnabled)
            return ObserverFovBucket.Full;

        RaycastMath.GetYawBasis(observer.Yaw, out float forwardX, out float forwardY, out _, out _);
        RaycastMath.ComputeTargetLeadPosition(
            in target,
            _config,
            _tickInterval,
            out float posX,
            out float posY,
            out _,
            out _,
            out _,
            out _,
            out _);

        float toTargetX = posX - observer.EyePosX;
        float toTargetY = posY - observer.EyePosY;
        float toTargetLenSqr = toTargetX * toTargetX + toTargetY * toTargetY;
        if (toTargetLenSqr <= float.Epsilon)
            return ObserverFovBucket.Full;

        float invLen = 1.0f / MathF.Sqrt(toTargetLenSqr);
        float dot = (toTargetX * forwardX + toTargetY * forwardY) * invLen;

        if (dot >= _fullFovDotThreshold)
            return ObserverFovBucket.Full;

        if (dot >= _peripheralFovDotThreshold)
            return ObserverFovBucket.Peripheral;

        return ObserverFovBucket.Rear;
    }

    /// <summary>
    /// Lightweight check: returns the last known visibility state from cache.
    /// Used by projectile hiding to avoid re-running full visibility evaluation.
    /// In Strict mode, missing cache entries fail closed to avoid first-frame side-channel leaks.
    /// </summary>
    public bool ShouldTransmitCached(int observerSlot, int targetSlot)
    {
        if (_visibilityCache.TryGetRaw(observerSlot, targetSlot, out bool visible, out _))
            return visible;
        return _config.General.SecurityProfile != SecurityProfile.Strict;
    }

    private void MarkFlexResync(int targetSlot)
    {
        if (FowConstants.IsValidSlot(targetSlot) && !_needsFlexResync[targetSlot])
        {
            _needsFlexResync[targetSlot] = true;
        }
    }

    private bool TryForceRevealFromCrosshair(in PlayerSnapshot observer, in PlayerSnapshot target, int currentTick)
    {
        if (_config.AntiWallhack.CrosshairRevealDistance <= 0.0f || _config.AntiWallhack.CrosshairRevealRadius <= 0.0f)
            return false;

        if (!TryGetCrosshairRevealSegment(
                in observer,
                currentTick,
                out float originX,
                out float originY,
                out float originZ,
                out float endX,
                out float endY,
                out float endZ))
            return false;

        return DoesRaySegmentHitExpandedTargetBounds(
            originX, originY, originZ,
            endX, endY, endZ,
            in target,
            _config.AntiWallhack.CrosshairRevealRadius);
    }

    public bool CanSeePlantedC4(in PlayerSnapshot observer, CPlantedC4 c4, int currentTick)
    {
        if (ShouldBypassVisibilityWorkForCurrentPhase())
            return true;

        if (!observer.IsValid || !observer.IsAlive)
            return false;

        var absOrigin = c4.AbsOrigin;
        if (absOrigin == null)
            return true;

        float c4X = absOrigin.X;
        float c4Y = absOrigin.Y;
        float c4BaseZ = absOrigin.Z + 4.0f;
        float c4TopZ = absOrigin.Z + 18.0f;

        if (_maxRelevanceDistanceSqr > 0.0f)
        {
            float distSqr = VectorMath.DistanceSquared(
                observer.EyePosX, observer.EyePosY, observer.EyePosZ,
                c4X, c4Y, c4BaseZ);
            if (distSqr > _maxRelevanceDistanceSqr)
                return false;
        }

        RaycastMath.ComputeObserverRayOrigin(in observer, _config, _tickInterval,
            out float eyeOriginX, out float eyeOriginY, out float eyeOriginZ);

        if (_config.AntiWallhack.SmokeBlocksWallhack &&
            _smokeTracker.ActiveCount > 0)
        {
            bool baseBlocked = _smokeTracker.IsBlockedBySmoke(
                eyeOriginX, eyeOriginY, eyeOriginZ,
                c4X, c4Y, c4BaseZ,
                _config.AntiWallhack.SmokeBlockRadius,
                currentTick,
                _config.AntiWallhack.SmokeBlockDelayTicks,
                _config.AntiWallhack.SmokeLifetimeTicks,
                _config.AntiWallhack.SmokeGrowthStartFraction);
            bool topBlocked = _smokeTracker.IsBlockedBySmoke(
                eyeOriginX, eyeOriginY, eyeOriginZ,
                c4X, c4Y, c4TopZ,
                _config.AntiWallhack.SmokeBlockRadius,
                currentTick,
                _config.AntiWallhack.SmokeBlockDelayTicks,
                _config.AntiWallhack.SmokeLifetimeTicks,
                _config.AntiWallhack.SmokeGrowthStartFraction);
            if (baseBlocked && topBlocked)
                return false;
        }

        bool? baseVisible = _raycastEngine.TraceLineVisibility(
            eyeOriginX, eyeOriginY, eyeOriginZ,
            c4X, c4Y, c4BaseZ);
        if (baseVisible == true)
            return true;

        bool? topVisible = _raycastEngine.TraceLineVisibility(
            eyeOriginX, eyeOriginY, eyeOriginZ,
            c4X, c4Y, c4TopZ);
        if (topVisible == true)
            return true;

        if (baseVisible == null || topVisible == null)
        {
            if (_config.General.SecurityProfile == SecurityProfile.Strict)
                return false;

            return _budgetExceededPolicy == BudgetExceededPolicy.FailOpen;
        }

        return false;
    }

    private bool IsFullyBlockedBySmoke(
        int observerSlot,
        in PlayerSnapshot target,
        float originX,
        float originY,
        float originZ,
        int currentTick,
        int maxCheckPoints)
    {
        Span<Vector3> points = stackalloc Vector3[RaycastEngine.MaxVisibilityTestPoints];
        Span<bool> fallbackFlags = stackalloc bool[RaycastEngine.MaxVisibilityTestPoints];
        int pointCount = _raycastEngine.FillVisibilityTestPoints(
            in target,
            originX,
            originY,
            originZ,
            points,
            fallbackFlags,
            currentTick,
            maxCheckPoints);
        if (pointCount <= 0)
            return false;

        for (int i = 0; i < pointCount; i++)
        {
            Vector3 point = points[i];
            if (!_smokeTracker.IsBlockedBySmoke(
                    originX, originY, originZ,
                    point.X, point.Y, point.Z,
                    _config.AntiWallhack.SmokeBlockRadius,
                    currentTick,
                    _config.AntiWallhack.SmokeBlockDelayTicks,
                    _config.AntiWallhack.SmokeLifetimeTicks,
                    _config.AntiWallhack.SmokeGrowthStartFraction))
            {
                return false;
            }
        }

        return true;
    }

    private void AppendDebugPointsForObserver(
        int observerSlot,
        in PlayerSnapshot target,
        float originX,
        float originY,
        float originZ,
        int currentTick,
        int maxCheckPoints)
    {
        if (!_config.Debug.ShowTargetPoints || !FowConstants.IsValidSlot(observerSlot))
            return;

        int pointCount = _raycastEngine.FillVisibilityTestPoints(
            in target,
            originX,
            originY,
            originZ,
            _debugScratchPoints,
            _debugScratchFallbacks,
            currentTick,
            maxCheckPoints);
        AppendDebugPointsForObserver(observerSlot, _debugScratchPoints, _debugScratchFallbacks, pointCount);

        AppendDebugSkeletonLinesForObserver(observerSlot, in target, currentTick);
    }

    private void AppendDebugPointsForObserver(int observerSlot, ReadOnlySpan<Vector3> points, ReadOnlySpan<bool> fallbackFlags, int pointCount)
    {
        if (!_config.Debug.ShowTargetPoints || !FowConstants.IsValidSlot(observerSlot) || pointCount <= 0)
            return;

        int existingCount = _observerDebugPointCounts[observerSlot];
        int remainingCapacity = RaycastEngine.MaxDebugPointsPerObserver - existingCount;
        if (remainingCapacity <= 0)
            return;

        int copyCount = Math.Min(pointCount, remainingCapacity);
        int baseIndex = observerSlot * RaycastEngine.MaxDebugPointsPerObserver + existingCount;
        for (int i = 0; i < copyCount; i++)
        {
            _observerDebugPoints[baseIndex + i] = points[i];
            bool isFallback = i < fallbackFlags.Length && fallbackFlags[i];
            _observerDebugPointFallbacks[baseIndex + i] = isFallback;
            if (isFallback)
                _observerDebugFallbackPointCounts[observerSlot]++;
        }

        _observerDebugPointCounts[observerSlot] = existingCount + copyCount;
    }

    private void AppendDebugSkeletonLinesForObserver(int observerSlot, in PlayerSnapshot target, int currentTick)
    {
        if (!_config.Debug.ShowTargetPoints || !FowConstants.IsValidSlot(observerSlot))
            return;

        int existingCount = _observerDebugLineCounts[observerSlot];
        int remainingCapacity = RaycastEngine.MaxDebugLinesPerObserver - existingCount;
        if (remainingCapacity <= 0)
            return;

        int written = _raycastEngine.FillSkeletonGraphLines(
            in target,
            _observerDebugLineStarts.AsSpan(observerSlot * RaycastEngine.MaxDebugLinesPerObserver + existingCount, remainingCapacity),
            _observerDebugLineEnds.AsSpan(observerSlot * RaycastEngine.MaxDebugLinesPerObserver + existingCount, remainingCapacity),
            currentTick);

        _observerDebugLineCounts[observerSlot] = existingCount + written;
    }

    private bool TryGetCrosshairRevealSegment(
        in PlayerSnapshot observer,
        int currentTick,
        out float originX,
        out float originY,
        out float originZ,
        out float endX,
        out float endY,
        out float endZ)
    {
        int slot = observer.Slot;
        if (!FowConstants.IsValidSlot(slot))
        {
            originX = 0.0f;
            originY = 0.0f;
            originZ = 0.0f;
            endX = 0.0f;
            endY = 0.0f;
            endZ = 0.0f;
            return false;
        }

        // TryGetAimRaySegment already returns the origin, so there is no need to recompute it here.

        if (_aimRevealCacheTicks[slot] == currentTick)
        {
            RaycastMath.ComputeObserverRayOriginNoPrediction(in observer, _config, out originX, out originY, out originZ);
            endX = _aimRevealEndX[slot];
            endY = _aimRevealEndY[slot];
            endZ = _aimRevealEndZ[slot];
            return _aimRevealCacheValid[slot];
        }

        bool valid = _raycastEngine.TryGetAimRaySegment(
            in observer,
            out originX,
            out originY,
            out originZ,
            out endX,
            out endY,
            out endZ);
        _aimRevealCacheTicks[slot] = currentTick;
        _aimRevealCacheValid[slot] = valid;
        _aimRevealEndX[slot] = endX;
        _aimRevealEndY[slot] = endY;
        _aimRevealEndZ[slot] = endZ;
        if (valid)
            _observerAimTraceCounts[slot]++;
        return valid;
    }

    private bool DoesRaySegmentHitExpandedTargetBounds(
        float startX,
        float startY,
        float startZ,
        float endX,
        float endY,
        float endZ,
        in PlayerSnapshot target,
        float radiusUnits)
    {
        RaycastMath.ComputeTargetLeadPosition(
            in target,
            _config,
            _tickInterval,
            out float posX,
            out float posY,
            out float posZ,
            out _,
            out _,
            out _,
            out _);

        float minX = posX + target.MinsX - radiusUnits;
        float maxX = posX + target.MaxsX + radiusUnits;
        float minY = posY + target.MinsY - radiusUnits;
        float maxY = posY + target.MaxsY + radiusUnits;
        float minZ = posZ + target.MinsZ - radiusUnits;
        float maxZ = posZ + target.MaxsZ + radiusUnits;

        return SegmentIntersectsAabb(
            startX, startY, startZ,
            endX, endY, endZ,
            minX, minY, minZ,
            maxX, maxY, maxZ);
    }

    private static bool SegmentIntersectsAabb(
        float startX,
        float startY,
        float startZ,
        float endX,
        float endY,
        float endZ,
        float minX,
        float minY,
        float minZ,
        float maxX,
        float maxY,
        float maxZ)
    {
        float dirX = endX - startX;
        float dirY = endY - startY;
        float dirZ = endZ - startZ;

        float tMin = 0.0f;
        float tMax = 1.0f;

        if (!UpdateAxis(startX, dirX, minX, maxX, ref tMin, ref tMax))
            return false;
        if (!UpdateAxis(startY, dirY, minY, maxY, ref tMin, ref tMax))
            return false;
        if (!UpdateAxis(startZ, dirZ, minZ, maxZ, ref tMin, ref tMax))
            return false;

        return tMax >= tMin;
    }

    private static bool UpdateAxis(float start, float dir, float min, float max, ref float tMin, ref float tMax)
    {
        const float Epsilon = 1e-6f;
        if (MathF.Abs(dir) < Epsilon)
            return start >= min && start <= max;

        float inv = 1.0f / dir;
        float t1 = (min - start) * inv;
        float t2 = (max - start) * inv;
        if (t1 > t2)
        {
            float tmp = t1;
            t1 = t2;
            t2 = tmp;
        }

        if (t1 > tMin)
            tMin = t1;
        if (t2 < tMax)
            tMax = t2;

        return tMax >= tMin;
    }

    public void OnRoundStart(int currentTick)
    {
        _roundStartGraceUntilTick = currentTick + _config.General.RoundStartRevealDurationTicks;
        _visibilityCache.Clear();
        _smokeTracker.Clear();
        Array.Clear(_deathForceTransmitUntil);
        Array.Clear(_spawnForceTransmitUntil);
        Array.Clear(_wasHidden);
        Array.Clear(_needsFlexResync);
        Array.Clear(_aimRevealCacheTicks);
        Array.Clear(_aimRevealCacheValid);
    }

    public void SetRoundPhase(RoundPhase phase)
    {
        _currentRoundPhase = phase;
    }

    public bool ShouldBypassVisibilityWorkForCurrentPhase()
    {
        return _currentRoundPhase == RoundPhase.Warmup ||
               _currentRoundPhase == RoundPhase.RoundEnd;
    }

    public void OnPlayerDeath(int victimSlot, int currentTick)
    {
        if (FowConstants.IsValidSlot(victimSlot))
        {
            _deathForceTransmitUntil[victimSlot] = currentTick + _config.General.DeathVisibilityDurationTicks;
        }
    }

    public void OnPlayerSpawn(int slot, int currentTick)
    {
        if (!FowConstants.IsValidSlot(slot))
            return;

        _spawnForceTransmitUntil[slot] = currentTick + _config.General.RoundStartRevealDurationTicks;
        _deathForceTransmitUntil[slot] = 0;
        _needsFlexResync[slot] = false;
        _aimRevealCacheTicks[slot] = 0;
        _aimRevealCacheValid[slot] = false;
        _visibilityCache.ClearForPlayer(slot);

        for (int i = 0; i < FowConstants.MaxSlots; i++)
        {
            _wasHidden[slot * FowConstants.MaxSlots + i] = false;
            _wasHidden[i * FowConstants.MaxSlots + slot] = false;
        }
    }

    public void OnMapChange()
    {
        _visibilityCache.Clear();
        _smokeTracker.Clear();
        _qualityScaler.Reset();
        _currentRoundPhase = RoundPhase.Live;
        _roundStartGraceUntilTick = 0;
        _budgetFallbackCacheReuseCount = 0;
        _budgetFallbackOpenTransmitCount = 0;
        _budgetCacheOnlyHideCount = 0;
        _totalSmokePreFilterSkips = 0;
        Array.Clear(_deathForceTransmitUntil);
        Array.Clear(_spawnForceTransmitUntil);
        Array.Clear(_wasHidden);
        Array.Clear(_needsFlexResync);
        Array.Clear(_aimRevealCacheTicks);
        Array.Clear(_aimRevealCacheValid);
    }

    public void OnPlayerDisconnect(int slot)
    {
        _visibilityCache.ClearForPlayer(slot);
        if (FowConstants.IsValidSlot(slot))
        {
            _deathForceTransmitUntil[slot] = 0;
            _spawnForceTransmitUntil[slot] = 0;
            _needsFlexResync[slot] = false;
            _aimRevealCacheTicks[slot] = 0;
            _aimRevealCacheValid[slot] = false;
            // Clear all hidden states involving this slot
            for (int i = 0; i < FowConstants.MaxSlots; i++)
            {
                _wasHidden[slot * FowConstants.MaxSlots + i] = false;
                _wasHidden[i * FowConstants.MaxSlots + slot] = false;
            }
        }
    }

}
