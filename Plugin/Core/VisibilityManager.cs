using System.Numerics;
using S2FOW.Config;
using S2FOW.Models;
using S2FOW.Util;

namespace S2FOW.Core;

/// <summary>
/// The current round state. S2FOW shows everyone during non-live states such as
/// warmup, freeze time, and round end.
/// </summary>
public enum RoundPhase
{
    /// <summary>Pre-game warmup: all players visible.</summary>
    Warmup = 0,
    /// <summary>Buy-time freeze: all players visible.</summary>
    FreezeTime = 1,
    /// <summary>Active gameplay: S2FOW may hide enemies.</summary>
    Live = 2,
    /// <summary>Bomb planted: S2FOW may hide enemies.</summary>
    PostPlant = 3,
    /// <summary>Round has ended: all players visible.</summary>
    RoundEnd = 4
}

/// <summary>
/// Decides whether one viewer should receive one enemy.
///
/// The code still uses the internal names "observer" for the viewer and "target"
/// for the enemy being checked. The actual decision order is:
///   1. Show everyone during warmup, freeze time, and round end.
///   2. Show everyone briefly at round start.
///   3. Show recently dead or freshly spawned players briefly.
///   4. Hide the enemy if smoke fully blocks all checked sight paths.
///   5. Show the enemy if the viewer is aiming directly at their body.
///   6. Ask RayTrace whether walls block every checked body point.
///   7. If S2FOW runs out of allowed checks or RayTrace fails, show the enemy.
///
/// This class also keeps the debug HUD counts that explain why enemies were shown
/// or hidden during the current frame.
/// </summary>
public class VisibilityManager
{
    /// <summary>
    /// Number of original body points used for reduced checks. Close enemies can
    /// use every detailed body point; far or off-angle enemies can use this smaller
    /// set before falling back to the simple box check.
    /// </summary>
    private const int OriginalLosPointCount = 19;

    /// <summary>Extra margin (units) added to the smoke pre-filter radius to catch edge cases.</summary>
    private const float SmokePreFilterMargin = 60.0f;

    private readonly RaycastEngine _raycastEngine;
    private readonly SmokeTracker _smokeTracker;
    private readonly S2FOWConfig _config;
    private readonly PerformanceMonitor? _perfMonitor;
    private readonly float _tickInterval;

    // ────────────────────────────────────────────────────────────────────────
    //  Per-player state tracking
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Tick until which each player's death animation should remain visible.</summary>
    private readonly int[] _deathForceTransmitUntil = new int[FowConstants.MaxSlots];

    /// <summary>Tick until which each freshly spawned player should remain visible.</summary>
    private readonly int[] _spawnForceTransmitUntil = new int[FowConstants.MaxSlots];

    /// <summary>For each viewer/enemy pair, remembers whether the enemy was hidden last frame.</summary>
    private readonly bool[] _wasHidden = new bool[FowConstants.MaxSlots * FowConstants.MaxSlots];

    /// <summary>Marks players that changed from hidden to visible and need a short visual refresh.</summary>
    private readonly bool[] _needsFlexResync = new bool[FowConstants.MaxSlots];

    /// <summary>Marks viewers that need a forced refresh after a hide/show change.</summary>
    private readonly bool[] _needsObserverFullUpdate = new bool[FowConstants.MaxSlots];

    // ────────────────────────────────────────────────────────────────────────
    //  Per-viewer debug counters for the HUD overlay
    // ────────────────────────────────────────────────────────────────────────

    private readonly int[] _observerSkeletonTraceCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerAabbTraceCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerTargetCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerRoundStartCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerDeathForceCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerSmokeBlockCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerLiveLosCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerBudgetFailOpenCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerDebugPointCounts = new int[FowConstants.MaxSlots];
    private readonly int[] _observerDebugFallbackPointCounts = new int[FowConstants.MaxSlots];
    private readonly Vector3[] _observerDebugPoints = new Vector3[FowConstants.MaxSlots * RaycastEngine.MaxDebugPointsPerObserver];
    private readonly bool[] _observerDebugPointFallbacks = new bool[FowConstants.MaxSlots * RaycastEngine.MaxDebugPointsPerObserver];
    private readonly int[] _observerAimEndpointTicks = new int[FowConstants.MaxSlots];
    private readonly bool[] _observerAimEndpointValid = new bool[FowConstants.MaxSlots];
    private readonly Vector3[] _observerAimEndpoints = new Vector3[FowConstants.MaxSlots];

    /// <summary>Tick until which the round-start grace period is active.</summary>
    private int _roundStartGraceUntilTick;

    /// <summary>The last tick at which BeginFrame was called.</summary>
    private int _lastBeginFrameTick;

    /// <summary>Lifetime count of enemies shown because the raycast budget ran out.</summary>
    private long _budgetFallbackOpenTransmitCount;

    /// <summary>Current round phase (determines whether to bypass visibility checks).</summary>
    private RoundPhase _currentRoundPhase = RoundPhase.Live;

    public VisibilityManager(
        RaycastEngine raycastEngine,
        SmokeTracker smokeTracker,
        S2FOWConfig config,
        PerformanceMonitor? perfMonitor = null)
    {
        _raycastEngine = raycastEngine;
        _smokeTracker = smokeTracker;
        _config = config;
        _perfMonitor = perfMonitor;
        _tickInterval = raycastEngine.TickInterval;
    }

    /// <summary>How many enemies were shown because S2FOW could not safely finish checking them.</summary>
    public long BudgetFallbackOpenTransmitCount => _budgetFallbackOpenTransmitCount;

    /// <summary>Stores the current tick number so other methods can reference it.</summary>
    public void SetFrameTick(int tick)
    {
        _lastBeginFrameTick = tick;
    }

    /// <summary>Resets all per-frame counters and culls expired smokes. Called once at the start of each frame.</summary>
    public void BeginFrame()
    {
        Array.Clear(_needsFlexResync);
        Array.Clear(_needsObserverFullUpdate);
        Array.Clear(_observerSkeletonTraceCounts);
        Array.Clear(_observerAabbTraceCounts);
        Array.Clear(_observerTargetCounts);
        Array.Clear(_observerRoundStartCounts);
        Array.Clear(_observerDeathForceCounts);
        Array.Clear(_observerSmokeBlockCounts);
        Array.Clear(_observerLiveLosCounts);
        Array.Clear(_observerBudgetFailOpenCounts);
        Array.Clear(_observerDebugPointCounts);
        Array.Clear(_observerDebugFallbackPointCounts);
        Array.Clear(_observerAimEndpointValid);

        _smokeTracker.CullExpired(_lastBeginFrameTick, _config.AntiWallhack.SmokeLifetimeTicks);
    }

    /// <summary>Returns true when a player just changed from hidden to visible and needs a short visual refresh.</summary>
    public bool NeedsFlexResync(int targetSlot)
    {
        return FowConstants.IsValidSlot(targetSlot) && _needsFlexResync[targetSlot];
    }

    public bool NeedsObserverFullUpdate(int observerSlot)
    {
        return FowConstants.IsValidSlot(observerSlot) && _needsObserverFullUpdate[observerSlot];
    }

    /// <summary>Returns true if this enemy was hidden from this viewer before the current decision.</summary>
    public bool IsPairHidden(int observerSlot, int targetSlot)
    {
        if (!FowConstants.IsValidSlot(observerSlot) || !FowConstants.IsValidSlot(targetSlot))
            return false;

        return _wasHidden[observerSlot * FowConstants.MaxSlots + targetSlot];
    }

    /// <summary>
    /// Clears hidden state when another part of S2FOW decides an enemy must be
    /// shown for safety reasons.
    /// </summary>
    public void MarkForceVisible(int observerSlot, int targetSlot)
    {
        if (!FowConstants.IsValidSlot(observerSlot) || !FowConstants.IsValidSlot(targetSlot))
            return;

        int pairIndex = observerSlot * FowConstants.MaxSlots + targetSlot;
        bool previouslyHidden = _wasHidden[pairIndex];
        MarkPairVisible(pairIndex, observerSlot, targetSlot, previouslyHidden);
    }

    /// <summary>Gets the raycast breakdown for one viewer this frame, for the debug HUD.</summary>
    public void GetObserverTraceCounts(
        int observerSlot,
        out int skeleton,
        out int aabb,
        out int total,
        out int targets,
        out int debugPoints,
        out int debugFallbackPoints)
    {
        if (!FowConstants.IsValidSlot(observerSlot))
        {
            skeleton = 0;
            aabb = 0;
            total = 0;
            targets = 0;
            debugPoints = 0;
            debugFallbackPoints = 0;
            return;
        }

        skeleton = _observerSkeletonTraceCounts[observerSlot];
        aabb = _observerAabbTraceCounts[observerSlot];
        total = skeleton + aabb;
        targets = _observerTargetCounts[observerSlot];
        debugPoints = _observerDebugPointCounts[observerSlot];
        debugFallbackPoints = _observerDebugFallbackPointCounts[observerSlot];
    }

    /// <summary>Gets the show/hide reason counts for one viewer this frame, for the debug HUD.</summary>
    public void GetObserverDecisionCounts(
        int observerSlot,
        out int roundStart,
        out int deathForce,
        out int smokeBlocked,
        out int liveLos,
        out int budgetFailOpen)
    {
        if (!FowConstants.IsValidSlot(observerSlot))
        {
            roundStart = 0;
            deathForce = 0;
            smokeBlocked = 0;
            liveLos = 0;
            budgetFailOpen = 0;
        }
        else
        {
            roundStart = _observerRoundStartCounts[observerSlot];
            deathForce = _observerDeathForceCounts[observerSlot];
            smokeBlocked = _observerSmokeBlockCounts[observerSlot];
            liveLos = _observerLiveLosCounts[observerSlot];
            budgetFailOpen = _observerBudgetFailOpenCounts[observerSlot];
        }
    }

    /// <summary>Copies this viewer's debug point positions into output buffers used for beam drawing.</summary>
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

    /// <summary>
    /// The core decision method: should this viewer receive this enemy?
    /// Returns true to show the enemy and false to hide them from this viewer.
    /// </summary>
    public bool ShouldTransmit(in PlayerSnapshot observer, in PlayerSnapshot target, int currentTick)
    {
        int pairIndex = observer.Slot * FowConstants.MaxSlots + target.Slot;
        if (ShouldBypassVisibilityWorkForCurrentPhase())
        {
            bool wasHiddenDuringRestrictedPhase = _wasHidden[pairIndex];
            MarkPairVisible(pairIndex, observer.Slot, target.Slot, wasHiddenDuringRestrictedPhase);
            return true;
        }

        bool previouslyHidden = _wasHidden[pairIndex];
        if (FowConstants.IsValidSlot(observer.Slot))
            _observerTargetCounts[observer.Slot]++;

        if (currentTick < _roundStartGraceUntilTick)
        {
            if (FowConstants.IsValidSlot(observer.Slot))
                _observerRoundStartCounts[observer.Slot]++;
            MarkPairVisible(pairIndex, observer.Slot, target.Slot, previouslyHidden);
            return true;
        }

        if (currentTick < _deathForceTransmitUntil[target.Slot])
        {
            if (FowConstants.IsValidSlot(observer.Slot))
                _observerDeathForceCounts[observer.Slot]++;
            MarkPairVisible(pairIndex, observer.Slot, target.Slot, previouslyHidden);
            return true;
        }

        if (currentTick < _spawnForceTransmitUntil[target.Slot])
        {
            MarkPairVisible(pairIndex, observer.Slot, target.Slot, previouslyHidden);
            return true;
        }

        int maxCheckPoints = GetLimitedMaxCheckPoints(in observer, in target);
        RaycastMath.ComputeObserverRayOrigin(in observer, _config, _tickInterval,
            out float eyeOriginX, out float eyeOriginY, out float eyeOriginZ);
        RaycastMath.ComputeObserverRayOriginNoPrediction(in observer, _config,
            out float fixedEyeOriginX, out float fixedEyeOriginY, out float fixedEyeOriginZ);

        AppendDebugTargetPointsForObserver(observer.Slot, in target, eyeOriginX, eyeOriginY, eyeOriginZ, currentTick, maxCheckPoints);

        // Smoke hides the enemy only when every checked body/box point is blocked
        // from both the predicted viewer eye and the stable non-predicted eye.
        if (IsSmokeBlockingTarget(
                observer.Slot,
                in target,
                eyeOriginX, eyeOriginY, eyeOriginZ,
                fixedEyeOriginX, fixedEyeOriginY, fixedEyeOriginZ,
                currentTick,
                maxCheckPoints))
        {
            if (FowConstants.IsValidSlot(observer.Slot))
                _observerSmokeBlockCounts[observer.Slot]++;
            _perfMonitor?.RecordHiddenBySmoke();
            _wasHidden[pairIndex] = true;
            return false;
        }

        // Aim reveal is a safety path: if the viewer is aiming close enough to the
        // enemy body, show the enemy instead of risking a visible player being hidden.
        if (IsTargetNearAimEndpoint(in observer, in target, eyeOriginX, eyeOriginY, eyeOriginZ, currentTick, maxCheckPoints))
        {
            MarkPairVisible(pairIndex, observer.Slot, target.Slot, previouslyHidden);
            return true;
        }

        _perfMonitor?.RecordEvaluation();
        if (FowConstants.IsValidSlot(observer.Slot))
            _observerLiveLosCounts[observer.Slot]++;

        // Ask RayTrace whether all checked body/box points are blocked by world
        // geometry. Any point with a clear path means the enemy should be shown.
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
            // If S2FOW cannot finish checking within the configured budget, show
            // the enemy. The plugin must never hide someone it did not finish checking.
            _perfMonitor?.RecordBudgetExceeded();
            _budgetFallbackOpenTransmitCount++;
            if (FowConstants.IsValidSlot(observer.Slot))
                _observerBudgetFailOpenCounts[observer.Slot]++;
            MarkPairVisible(pairIndex, observer.Slot, target.Slot, previouslyHidden);
            return true;
        }

        if (visibilityResult.TraceFailed)
        {
            // If RayTrace fails, show the enemy. Missing visibility data must not
            // become a hidden player.
            _budgetFallbackOpenTransmitCount++;
            if (FowConstants.IsValidSlot(observer.Slot))
                _observerBudgetFailOpenCounts[observer.Slot]++;
            MarkPairVisible(pairIndex, observer.Slot, target.Slot, previouslyHidden);
            return true;
        }

        if (visibilityResult.IsVisible)
        {
            MarkPairVisible(pairIndex, observer.Slot, target.Slot, previouslyHidden);
            return true;
        }

        _wasHidden[pairIndex] = true;
        _perfMonitor?.RecordHiddenByLineOfSight();
        return false;
    }

    /// <summary>Called when a new round starts. Sets the grace timer and clears all tracking state.</summary>
    public void OnRoundStart(int currentTick)
    {
        _roundStartGraceUntilTick = currentTick + Math.Max(0, _config.General.RoundStartRevealDurationTicks);
        Array.Clear(_deathForceTransmitUntil);
        Array.Clear(_spawnForceTransmitUntil);
        Array.Clear(_wasHidden);
        Array.Clear(_needsFlexResync);
        Array.Clear(_needsObserverFullUpdate);
    }

    /// <summary>Updates the current round state and clears hidden records when everyone should be visible.</summary>
    public void SetRoundPhase(RoundPhase phase)
    {
        if (_currentRoundPhase == phase)
            return;

        _currentRoundPhase = phase;
        if (ShouldBypassVisibilityWorkForCurrentPhase())
        {
            Array.Clear(_wasHidden);
            Array.Clear(_needsFlexResync);
            Array.Clear(_needsObserverFullUpdate);
        }
    }

    /// <summary>Returns true during warmup, freeze time, or round end (all players visible).</summary>
    public bool ShouldBypassVisibilityWorkForCurrentPhase()
    {
        return _currentRoundPhase is RoundPhase.Warmup or RoundPhase.FreezeTime or RoundPhase.RoundEnd;
    }

    /// <summary>Sets the death grace timer so the death animation plays out visibly.</summary>
    public void OnPlayerDeath(int slot, int currentTick)
    {
        if (!FowConstants.IsValidSlot(slot))
            return;

        _deathForceTransmitUntil[slot] = currentTick + Math.Max(0, _config.General.DeathVisibilityDurationTicks);
    }

    /// <summary>Sets the spawn grace timer and marks the player for a short visual refresh.</summary>
    public void OnPlayerSpawn(int slot, int currentTick)
    {
        if (!FowConstants.IsValidSlot(slot))
            return;

        _spawnForceTransmitUntil[slot] = currentTick + Math.Min(16, Math.Max(0, _config.General.RoundStartRevealDurationTicks));
        MarkFlexResync(slot);
    }

    /// <summary>Resets all state for a new map.</summary>
    public void OnMapChange()
    {
        _roundStartGraceUntilTick = 0;
        Array.Clear(_deathForceTransmitUntil);
        Array.Clear(_spawnForceTransmitUntil);
        Array.Clear(_wasHidden);
        Array.Clear(_needsFlexResync);
        Array.Clear(_needsObserverFullUpdate);
        _budgetFallbackOpenTransmitCount = 0;
    }

    /// <summary>Clears all state for a disconnecting player.</summary>
    public void OnPlayerDisconnect(int slot)
    {
        if (!FowConstants.IsValidSlot(slot))
            return;

        _deathForceTransmitUntil[slot] = 0;
        _spawnForceTransmitUntil[slot] = 0;
        _needsFlexResync[slot] = false;
        _needsObserverFullUpdate[slot] = false;

        for (int i = 0; i < FowConstants.MaxSlots; i++)
        {
            _wasHidden[slot * FowConstants.MaxSlots + i] = false;
            _wasHidden[i * FowConstants.MaxSlots + slot] = false;
        }
    }

    private void MarkFlexResync(int targetSlot)
    {
        if (FowConstants.IsValidSlot(targetSlot))
            _needsFlexResync[targetSlot] = true;
    }

    private void MarkObserverFullUpdate(int observerSlot)
    {
        if (FowConstants.IsValidSlot(observerSlot))
            _needsObserverFullUpdate[observerSlot] = true;
    }

    private void MarkPairVisible(int pairIndex, int observerSlot, int targetSlot, bool previouslyHidden)
    {
        _wasHidden[pairIndex] = false;
        if (!previouslyHidden)
            return;

        MarkFlexResync(targetSlot);
        MarkObserverFullUpdate(observerSlot);
    }

    private int GetLimitedMaxCheckPoints(in PlayerSnapshot observer, in PlayerSnapshot target)
    {
        float toTargetX = target.PosX - observer.EyePosX;
        float toTargetY = target.PosY - observer.EyePosY;
        float distanceSqr = toTargetX * toTargetX + toTargetY * toTargetY;

        int fovLimit = GetFovLimitedMaxCheckPoints(in observer, toTargetX, toTargetY, distanceSqr);
        int distanceLimit = GetDistanceLimitedMaxCheckPoints(distanceSqr);
        return Math.Min(fovLimit, distanceLimit);
    }

    private int GetFovLimitedMaxCheckPoints(
        in PlayerSnapshot observer,
        float toTargetX,
        float toTargetY,
        float distanceSqr)
    {
        if (!_config.TargetPoints.FovCullingEnabled)
            return RaycastEngine.VisibilityPrimitiveCount;

        if (distanceSqr <= 0.0001f)
            return RaycastEngine.VisibilityPrimitiveCount;

        RaycastMath.GetYawBasis(observer.Yaw, out float forwardX, out float forwardY, out _, out _);
        float invDistance = 1.0f / MathF.Sqrt(distanceSqr);
        float dot = Math.Clamp((forwardX * toTargetX + forwardY * toTargetY) * invDistance, -1.0f, 1.0f);
        float angleDegrees = MathF.Acos(dot) * (180.0f / MathF.PI);

        float fullHalfAngle = Math.Clamp(_config.TargetPoints.FullLosHalfAngleDegrees, 0.0f, 180.0f);
        if (angleDegrees <= fullHalfAngle)
            return RaycastEngine.VisibilityPrimitiveCount;

        float originalOnlyHalfAngle = Math.Clamp(_config.TargetPoints.OriginalOnlyHalfAngleDegrees, fullHalfAngle, 180.0f);
        if (angleDegrees <= originalOnlyHalfAngle)
            return Math.Min(OriginalLosPointCount, RaycastEngine.VisibilityPrimitiveCount);

        return 0;
    }

    private int GetDistanceLimitedMaxCheckPoints(float distanceSqr)
    {
        if (!_config.TargetPoints.DistanceTieringEnabled)
            return RaycastEngine.VisibilityPrimitiveCount;

        float fullDistance = Math.Max(0.0f, _config.TargetPoints.FullLosDistanceUnits);
        float aabbOnlyDistance = Math.Max(fullDistance, _config.TargetPoints.AabbOnlyDistanceUnits);
        float fullDistanceSqr = fullDistance * fullDistance;
        float aabbOnlyDistanceSqr = aabbOnlyDistance * aabbOnlyDistance;

        if (distanceSqr <= fullDistanceSqr)
            return RaycastEngine.VisibilityPrimitiveCount;

        if (distanceSqr >= aabbOnlyDistanceSqr)
            return 0;

        return Math.Min(OriginalLosPointCount, RaycastEngine.VisibilityPrimitiveCount);
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
                    _config.AntiWallhack.SmokeBloomDurationTicks,
                    _config.AntiWallhack.SmokeLifetimeTicks,
                    _config.AntiWallhack.SmokeGrowthStartFraction))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsSmokeBlockingTarget(
        int observerSlot,
        in PlayerSnapshot target,
        float eyeOriginX,
        float eyeOriginY,
        float eyeOriginZ,
        float fixedEyeOriginX,
        float fixedEyeOriginY,
        float fixedEyeOriginZ,
        int currentTick,
        int maxCheckPoints)
    {
        if (!_config.AntiWallhack.SmokeBlocksWallhack || _smokeTracker.ActiveCount <= 0)
            return false;

        if (_config.Performance.SmokeBatchPreFilterEnabled &&
            !_smokeTracker.IsLineNearAnySmoke(
                eyeOriginX, eyeOriginY, eyeOriginZ,
                target.PosX, target.PosY, target.PosZ + target.ViewOffsetZ * 0.5f,
                _config.AntiWallhack.SmokeBlockRadius, SmokePreFilterMargin,
                currentTick, _config.AntiWallhack.SmokeLifetimeTicks))
        {
            return false;
        }

        if (!IsFullyBlockedBySmoke(observerSlot, in target, eyeOriginX, eyeOriginY, eyeOriginZ, currentTick, maxCheckPoints))
            return false;

        return IsSamePoint(eyeOriginX, eyeOriginY, eyeOriginZ, fixedEyeOriginX, fixedEyeOriginY, fixedEyeOriginZ) ||
               IsFullyBlockedBySmoke(observerSlot, in target, fixedEyeOriginX, fixedEyeOriginY, fixedEyeOriginZ, currentTick, maxCheckPoints);
    }

    private static bool IsSamePoint(float ax, float ay, float az, float bx, float by, float bz)
    {
        const float Epsilon = 0.001f;
        return MathF.Abs(ax - bx) <= Epsilon &&
               MathF.Abs(ay - by) <= Epsilon &&
               MathF.Abs(az - bz) <= Epsilon;
    }

    private bool IsTargetNearAimEndpoint(
        in PlayerSnapshot observer,
        in PlayerSnapshot target,
        float originX,
        float originY,
        float originZ,
        int currentTick,
        int maxCheckPoints)
    {
        float radius = _config.Performance.AimRevealRadius;
        if (radius <= 0.0f)
            return false;

        if (!TryGetAimEndpoint(in observer, originX, originY, originZ, currentTick, out Vector3 endpoint))
            return false;

        Span<Vector3> points = stackalloc Vector3[RaycastEngine.MaxVisibilityTestPoints];
        int pointCount = _raycastEngine.FillVisibilityTestPoints(
            in target,
            originX,
            originY,
            originZ,
            points,
            currentTick,
            maxCheckPoints);

        float radiusSqr = radius * radius;
        for (int i = 0; i < pointCount; i++)
        {
            if (Vector3.DistanceSquared(endpoint, points[i]) <= radiusSqr)
                return true;
        }

        return false;
    }

    private bool TryGetAimEndpoint(
        in PlayerSnapshot observer,
        float originX,
        float originY,
        float originZ,
        int currentTick,
        out Vector3 endpoint)
    {
        endpoint = default;
        int slot = observer.Slot;
        if (!FowConstants.IsValidSlot(slot))
            return false;

        if (_observerAimEndpointTicks[slot] == currentTick)
        {
            endpoint = _observerAimEndpoints[slot];
            return _observerAimEndpointValid[slot];
        }

        _observerAimEndpointTicks[slot] = currentTick;
        RaycastMath.GetAimDirection(observer.Pitch, observer.Yaw, out float directionX, out float directionY, out float directionZ);
        bool valid = _raycastEngine.TryTraceAimEndpoint(
            originX,
            originY,
            originZ,
            directionX,
            directionY,
            directionZ,
            _config.Performance.AimRayDistance,
            out endpoint);

        _observerAimEndpointValid[slot] = valid;
        _observerAimEndpoints[slot] = endpoint;
        return valid;
    }

    private void AppendDebugTargetPointsForObserver(
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
        AppendDebugPointsForObserver(observerSlot, points, fallbackFlags, pointCount);
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

}
