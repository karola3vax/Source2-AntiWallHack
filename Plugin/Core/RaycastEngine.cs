using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Utils;
using S2FOW.Config;
using S2FOW.Models;
using Vector3 = System.Numerics.Vector3;

namespace S2FOW.Core;

/// <summary>
/// The raycast engine — the low-level workhorse that fires invisible "rays" (straight
/// lines) from an observer's eyes toward points on an enemy's body and checks whether
/// they hit a wall along the way.
///
/// Key responsibilities:
///   - Builds and caches the set of "visibility test points" for each enemy target.
///     These are specific 3D positions on the target's skeleton (head, shoulders, hips,
///     knees, weapon muzzle, etc.) plus 8 corners of the bounding box (AABB fallback).
///   - Fires rays from the observer to each test point via the RayTrace native API.
///   - Interprets the ray results: did the ray reach the target, or did it hit a wall?
///   - Tracks the per-frame raycast budget and stops early if exceeded (fail-open).
///   - Records debug rays for in-world visualization when debug mode is enabled.
///
/// Visibility checking is done in two tiers:
///   1. Skeleton points — precise body positions extracted from CS2 hitbox data.
///      If any skeleton ray reaches the target, the target is visible (early out).
///   2. AABB fallback points — the 8 corners of the padded bounding box.
///      Only checked if ALL skeleton rays were blocked. Acts as a safety net
///      so players peeking a tiny sliver are not incorrectly hidden.
/// </summary>

public class RaycastEngine
{
    /// <summary>
    /// The result of a full visibility check for one observer→target pair.
    /// Contains whether the target is visible, whether the budget was exceeded,
    /// and how many rays were cast in each tier.
    /// </summary>
    public readonly struct VisibilityResult
    {
        /// <summary>True if at least one ray reached the target (they are visible).</summary>
        public bool IsVisible { get; init; }

        /// <summary>True if the raycast budget ran out before all points could be checked.</summary>
        public bool BudgetExceeded { get; init; }

        /// <summary>How many rays were cast in each tier (skeleton vs AABB).</summary>
        public TraceCountBreakdown TraceCounts { get; init; }
    }

    /// <summary>Breakdown of how many rays were cast in each tier.</summary>
    public readonly struct TraceCountBreakdown
    {
        /// <summary>Rays cast to skeleton body points (head, shoulders, hips, etc.).</summary>
        public int Skeleton { get; init; }

        /// <summary>Rays cast to AABB fallback corners (bounding box).</summary>
        public int Aabb { get; init; }

        /// <summary>Total rays cast (skeleton + AABB).</summary>
        public int Total => Skeleton + Aabb;
    }

    /// <summary>Internal result of tracing a single primitive point.</summary>
    private enum PrimitiveTraceState
    {
        /// <summary>The ray hit a wall — this point is hidden.</summary>
        Hidden = 0,

        /// <summary>The ray reached the target — this point is visible.</summary>
        Visible = 1,

        /// <summary>The ray budget was exceeded — cannot determine visibility.</summary>
        BudgetExceeded = 2
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Constants
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Number of skeleton-based visibility primitives (body points extracted from CS2 hitboxes).</summary>
    public const int VisibilityPrimitiveCount = Cs2VisibilityPrimitiveLayout.PrimitiveCount;

    /// <summary>Total check points per target: skeleton primitives + 8 AABB corners.</summary>
    public const int MaxVisibilityTestPoints = Cs2VisibilityPrimitiveLayout.MaxVisibilityTestPoints;

    /// <summary>Maximum debug points across all targets for one observer (64 targets × 43 points each).</summary>
    public const int MaxDebugPointsPerObserver = FowConstants.MaxSlots * MaxVisibilityTestPoints;

    /// <summary>Maximum debug rays recorded per frame for visualization.</summary>
    public const int MaxDebugRays = 512;

    /// <summary>Number of AABB fallback corner points (8 corners of the bounding box).</summary>
    private const int AabbPointCount = Cs2VisibilityPrimitiveLayout.AabbPointCount;

    // ────────────────────────────────────────────────────────────────────────
    //  Dependencies and configuration
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>The native RayTrace API — performs the actual "shoot a line and see what it hits" computation.</summary>
    private readonly IRayTraceService _rayTrace;

    /// <summary>Active plugin configuration.</summary>
    private readonly S2FOWConfig _config;

    /// <summary>Options passed to each RayTrace call (world-only collision, no entity collision).</summary>
    private readonly TraceOptions _traceOptions;

    /// <summary>Maximum raycasts allowed this frame. 0 = unlimited.</summary>
    private int _maxRaycastsPerFrame;

    /// <summary>A ray is considered "reaching the target" if its hit fraction exceeds this value.</summary>
    private readonly float _visibleHitFractionThreshold;

    /// <summary>A ray is considered "reaching the target" if the hit point is within this many units of the target.</summary>
    private readonly float _visibleHitDistanceUnits;

    /// <summary>Server tick interval in seconds (1/64 ≈ 0.015625 for 64-tick).</summary>
    private readonly float _tickInterval;

    // ────────────────────────────────────────────────────────────────────────
    //  Per-target geometry cache (rebuilt once per frame per target)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>The tick at which each target's geometry was last built. -1 = never.</summary>
    private readonly int[] _cachedGeometryTicks = new int[FowConstants.MaxSlots];

    /// <summary>Cached world-space positions of each target's skeleton visibility points.</summary>
    private readonly Vector3[] _cachedPrimitivePoints = new Vector3[FowConstants.MaxSlots * VisibilityPrimitiveCount];

    /// <summary>Cached world-space positions of each target's 8 AABB fallback corners.</summary>
    private readonly Vector3[] _cachedAabbPoints = new Vector3[FowConstants.MaxSlots * AabbPointCount];

    /// <summary>Debug rays recorded this frame for visualization.</summary>
    private readonly DebugRay[] _debugRays = new DebugRay[MaxDebugRays];

    /// <summary>Reusable Vector objects to avoid allocation in the hot path.</summary>
    private readonly Vector _reusableTraceOrigin = new(0, 0, 0);
    private readonly Vector _reusableTraceEnd = new(0, 0, 0);

    /// <summary>How many debug rays have been recorded this frame.</summary>
    private int _debugRayCount;

    // ────────────────────────────────────────────────────────────────────────
    //  Public read-only state
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>How many raycasts have been performed so far this frame.</summary>
    public int RaycastsThisFrame { get; private set; }

    /// <summary>The debug rays recorded this frame (for the debug beam renderer).</summary>
    internal ReadOnlySpan<DebugRay> DebugRays => _debugRays.AsSpan(0, _debugRayCount);

    /// <summary>The server's tick interval (seconds per tick).</summary>
    public float TickInterval => _tickInterval;

    // ────────────────────────────────────────────────────────────────────────
    //  Constructor
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new RaycastEngine bound to the given RayTrace API and config.
    /// Configures collision masks to only test against world geometry (walls, floors)
    /// and NOT against other entities (players, weapons, props).
    /// </summary>
    internal RaycastEngine(IRayTraceService rayTrace, S2FOWConfig config, float? tickIntervalOverride = null)
    {
        _rayTrace = rayTrace;
        _config = config;
        _maxRaycastsPerFrame = Math.Max(0, config.Performance.MaxRaycastsPerFrame);
        _visibleHitFractionThreshold = Math.Clamp(config.Performance.RayHitFractionThreshold, 0.0f, 1.0f);
        _visibleHitDistanceUnits = Math.Max(0.0f, config.Performance.RayHitDistanceThreshold);
        _traceOptions = new TraceOptions(
            InteractionLayers.MaskWorldOnly,
            InteractionLayers.None,
            false);

        _tickInterval = tickIntervalOverride ?? Server.TickInterval;
        Array.Fill(_cachedGeometryTicks, -1);
    }

    /// <summary>Resets the per-frame raycast counter and debug ray buffer. Called at the start of each frame.</summary>
    public void ResetFrameCounter()
    {
        RaycastsThisFrame = 0;
        _debugRayCount = 0;
    }

    /// <summary>Sets the maximum number of raycasts allowed this frame. 0 = unlimited.</summary>
    public void SetFrameBudget(int budget)
    {
        _maxRaycastsPerFrame = Math.Max(0, budget);
    }

    /// <summary>
    /// Fires a single ray along the observer's aim direction and returns where it lands.
    /// Used for the "aim reveal" feature: if the observer's crosshair lands near an enemy,
    /// that enemy is force-shown regardless of wall checks (they are directly aiming at them).
    /// Returns false if the budget is exceeded or the distance is invalid.
    /// </summary>
    public bool TryTraceAimEndpoint(
        float originX,
        float originY,
        float originZ,
        float directionX,
        float directionY,
        float directionZ,
        float distance,
        out Vector3 endpoint)
    {
        endpoint = default;
        if (distance <= 0.0f)
            return false;

        if (_maxRaycastsPerFrame > 0 && RaycastsThisFrame >= _maxRaycastsPerFrame)
            return false;

        float endX = originX + directionX * distance;
        float endY = originY + directionY * distance;
        float endZ = originZ + directionZ * distance;

        _reusableTraceOrigin.X = originX;
        _reusableTraceOrigin.Y = originY;
        _reusableTraceOrigin.Z = originZ;
        _reusableTraceEnd.X = endX;
        _reusableTraceEnd.Y = endY;
        _reusableTraceEnd.Z = endZ;

        bool success = _rayTrace.TraceEndShape(
            _reusableTraceOrigin,
            _reusableTraceEnd,
            null,
            _traceOptions,
            out TraceResult result);

        RaycastsThisFrame++;

        if (success && result.DidHit)
        {
            endpoint = new Vector3(result.EndPosX, result.EndPosY, result.EndPosZ);
            RecordDebugRay(originX, originY, originZ, endpoint.X, endpoint.Y, endpoint.Z, visible: true);
            return true;
        }

        endpoint = new Vector3(endX, endY, endZ);
        RecordDebugRay(originX, originY, originZ, endX, endY, endZ, visible: true);
        return success;
    }

    /// <summary>
    /// Performs the full two-tier visibility check for one target.
    ///
    /// Tier 1 (skeleton): Fires rays from the observer's eye to each skeleton body
    /// point on the target. If ANY ray reaches the target → visible (early return).
    ///
    /// Tier 2 (AABB fallback): If all skeleton rays hit walls, fires rays to the
    /// 8 corners of the target's padded bounding box. If ANY corner ray reaches
    /// the target → visible.
    ///
    /// If the budget runs out during checking, returns BudgetExceeded = true.
    /// The caller (VisibilityManager) then force-shows the target (fail-open).
    /// </summary>
    public VisibilityResult CheckVisibility(
        in PlayerSnapshot target,
        float eyeOriginX,
        float eyeOriginY,
        float eyeOriginZ,
        float fixedEyeOriginX,
        float fixedEyeOriginY,
        float fixedEyeOriginZ,
        int currentTick,
        int maxCheckPoints = VisibilityPrimitiveCount)
    {
        EnsureTargetGeometryBuilt(in target, currentTick);

        int maxPrimitiveCount = Math.Clamp(maxCheckPoints, 0, VisibilityPrimitiveCount);
        int primitiveBaseIndex = target.Slot * VisibilityPrimitiveCount;
        int aabbBaseIndex = target.Slot * AabbPointCount;
        int skeletonTraceCount = 0;
        int aabbTraceCount = 0;
        ReadOnlySpan<VisibilityPrimitive> primitives = Cs2VisibilityPrimitiveLayout.Primitives;

        for (int i = 0; i < maxPrimitiveCount; i++)
        {
            ref readonly var primitive = ref primitives[i];
            if (!ShouldUsePrimitiveForTarget(in primitive, in target))
                continue;

            Vector3 point = _cachedPrimitivePoints[primitiveBaseIndex + i];

            PrimitiveTraceState traceState = TracePrimitivePoint(
                in primitive,
                eyeOriginX,
                eyeOriginY,
                eyeOriginZ,
                fixedEyeOriginX,
                fixedEyeOriginY,
                fixedEyeOriginZ,
                point,
                ref skeletonTraceCount);

            switch (traceState)
            {
                case PrimitiveTraceState.Visible:
                    return new VisibilityResult
                    {
                        IsVisible = true,
                        BudgetExceeded = false,
                        TraceCounts = new TraceCountBreakdown { Skeleton = skeletonTraceCount, Aabb = aabbTraceCount }
                    };

                case PrimitiveTraceState.BudgetExceeded:
                    return new VisibilityResult
                    {
                        IsVisible = false,
                        BudgetExceeded = true,
                        TraceCounts = new TraceCountBreakdown { Skeleton = skeletonTraceCount, Aabb = aabbTraceCount }
                    };
            }
        }

        for (int i = 0; i < AabbPointCount; i++)
        {
            aabbTraceCount++;
            Vector3 corner = _cachedAabbPoints[aabbBaseIndex + i];
            bool? visibility = TraceIsVisible(eyeOriginX, eyeOriginY, eyeOriginZ, corner.X, corner.Y, corner.Z);
            if (visibility == null)
            {
                return new VisibilityResult
                {
                    IsVisible = false,
                    BudgetExceeded = true,
                    TraceCounts = new TraceCountBreakdown { Skeleton = skeletonTraceCount, Aabb = aabbTraceCount }
                };
            }

            if (visibility.Value)
            {
                return new VisibilityResult
                {
                    IsVisible = true,
                    BudgetExceeded = false,
                    TraceCounts = new TraceCountBreakdown { Skeleton = skeletonTraceCount, Aabb = aabbTraceCount }
                };
            }
        }

        return new VisibilityResult
        {
            IsVisible = false,
            BudgetExceeded = false,
            TraceCounts = new TraceCountBreakdown { Skeleton = skeletonTraceCount, Aabb = aabbTraceCount }
        };
    }

    /// <summary>
    /// Fills an output buffer with the world-space positions of all visibility test
    /// points for a target. Used by smoke blocking checks and the debug renderer.
    /// Overload without the fallback-flag output.
    /// </summary>
    public int FillVisibilityTestPoints(
        in PlayerSnapshot target,
        float observerX,
        float observerY,
        float observerZ,
        Span<Vector3> output,
        int currentTick,
        int maxCheckPoints = VisibilityPrimitiveCount)
    {
        return FillVisibilityTestPoints(in target, observerX, observerY, observerZ, output, Span<bool>.Empty, currentTick, maxCheckPoints);
    }

    /// <summary>
    /// Fills output buffers with visibility test point positions and a flag indicating
    /// whether each point is an AABB fallback point (true) or a skeleton point (false).
    /// </summary>
    public int FillVisibilityTestPoints(
        in PlayerSnapshot target,
        float observerX,
        float observerY,
        float observerZ,
        Span<Vector3> output,
        Span<bool> isAabbFallbackOutput,
        int currentTick,
        int maxCheckPoints = VisibilityPrimitiveCount)
    {
        EnsureTargetGeometryBuilt(in target, currentTick);

        int maxPrimitiveCount = Math.Clamp(maxCheckPoints, 0, VisibilityPrimitiveCount);
        int primitiveBaseIndex = target.Slot * VisibilityPrimitiveCount;
        int aabbBaseIndex = target.Slot * AabbPointCount;
        int written = 0;

        for (int i = 0; i < maxPrimitiveCount && written < output.Length; i++)
        {
            ref readonly var primitive = ref Cs2VisibilityPrimitiveLayout.Primitives[i];
            if (!ShouldUsePrimitiveForTarget(in primitive, in target))
                continue;

            output[written++] = _cachedPrimitivePoints[primitiveBaseIndex + i];
            if (written - 1 < isAabbFallbackOutput.Length)
                isAabbFallbackOutput[written - 1] = false;
        }

        for (int i = 0; i < AabbPointCount && written < output.Length; i++)
        {
            output[written++] = _cachedAabbPoints[aabbBaseIndex + i];
            if (written - 1 < isAabbFallbackOutput.Length)
                isAabbFallbackOutput[written - 1] = true;
        }

        return written;
    }

    /// <summary>
    /// Checks if a visibility primitive applies to this target's current weapon.
    /// Some points (weapon muzzle tip) only exist for specific weapon classes.
    /// A primitive with RequiredWeaponClass = None applies to all targets.
    /// </summary>
    private static bool ShouldUsePrimitiveForTarget(in VisibilityPrimitive primitive, in PlayerSnapshot target)
    {
        return primitive.RequiredWeaponClass == WeaponLosClass.None ||
               primitive.RequiredWeaponClass == target.ActiveWeaponLosClass;
    }

    /// <summary>
    /// Fires a single ray from the observer to one primitive point on the target.
    /// Some primitives use the "fixed" (no-prediction) origin for stability.
    /// Returns Hidden, Visible, or BudgetExceeded.
    /// </summary>
    private PrimitiveTraceState TracePrimitivePoint(
        in VisibilityPrimitive primitive,
        float eyeOriginX,
        float eyeOriginY,
        float eyeOriginZ,
        float fixedEyeOriginX,
        float fixedEyeOriginY,
        float fixedEyeOriginZ,
        Vector3 point,
        ref int traceCount)
    {
        float startX = primitive.UseFixedHeadOrigin ? fixedEyeOriginX : eyeOriginX;
        float startY = primitive.UseFixedHeadOrigin ? fixedEyeOriginY : eyeOriginY;
        float startZ = primitive.UseFixedHeadOrigin ? fixedEyeOriginZ : eyeOriginZ;

        traceCount++;
        bool? visibility = TraceIsVisible(startX, startY, startZ, point.X, point.Y, point.Z);
        if (visibility == null)
            return PrimitiveTraceState.BudgetExceeded;

        if (visibility.Value)
            return PrimitiveTraceState.Visible;

        return PrimitiveTraceState.Hidden;
    }

    /// <summary>
    /// Ensures the target's geometry (skeleton points + AABB corners) has been
    /// computed for this frame. Only rebuilds if the target hasn't been processed yet.
    /// </summary>
    private void EnsureTargetGeometryBuilt(in PlayerSnapshot target, int currentTick)
    {
        int slot = target.Slot;
        if (!FowConstants.IsValidSlot(slot))
            return;

        if (_cachedGeometryTicks[slot] == currentTick)
            return;

        BuildTargetGeometry(in target, currentTick);
        _cachedGeometryTicks[slot] = currentTick;
    }

    /// <summary>
    /// Builds the world-space positions of all visibility test points for a target.
    /// Transforms the local-space skeleton points by the target's facing direction,
    /// and constructs the 8 corners of the padded axis-aligned bounding box (AABB).
    /// </summary>
    private void BuildTargetGeometry(in PlayerSnapshot target, int currentTick)
    {
        RaycastMath.ComputeTargetLeadPosition(
            in target,
            _config,
            _tickInterval,
            out float posX,
            out float posY,
            out float posZ,
            out float forwardX,
            out float forwardY,
            out float rightX,
            out float rightY);

        int primitiveBaseIndex = target.Slot * VisibilityPrimitiveCount;
        int aabbBaseIndex = target.Slot * AabbPointCount;
        ReadOnlySpan<VisibilityPrimitive> primitives = Cs2VisibilityPrimitiveLayout.Primitives;

        for (int i = 0; i < VisibilityPrimitiveCount; i++)
        {
            ref readonly var primitive = ref primitives[i];
            _cachedPrimitivePoints[primitiveBaseIndex + i] = TransformModelPoint(
                posX,
                posY,
                posZ,
                forwardX,
                forwardY,
                rightX,
                rightY,
                primitive.LocalPoint);
        }

        float minLocalX = target.MinsX - _config.Performance.HitboxPaddingSide;
        float maxLocalX = target.MaxsX + _config.Performance.HitboxPaddingSide;
        float minLocalY = target.MinsY - _config.Performance.HitboxPaddingSide;
        float maxLocalY = target.MaxsY + _config.Performance.HitboxPaddingSide;
        float minZ = posZ + target.MinsZ - _config.Performance.HitboxPaddingDown;
        float maxZ = posZ + target.MaxsZ + _config.Performance.HitboxPaddingUp;

        _cachedAabbPoints[aabbBaseIndex + 0] = TransformBoundsCorner(posX, posY, minZ, forwardX, forwardY, rightX, rightY, maxLocalX, minLocalY);
        _cachedAabbPoints[aabbBaseIndex + 1] = TransformBoundsCorner(posX, posY, minZ, forwardX, forwardY, rightX, rightY, maxLocalX, maxLocalY);
        _cachedAabbPoints[aabbBaseIndex + 2] = TransformBoundsCorner(posX, posY, minZ, forwardX, forwardY, rightX, rightY, minLocalX, minLocalY);
        _cachedAabbPoints[aabbBaseIndex + 3] = TransformBoundsCorner(posX, posY, minZ, forwardX, forwardY, rightX, rightY, minLocalX, maxLocalY);
        _cachedAabbPoints[aabbBaseIndex + 4] = TransformBoundsCorner(posX, posY, maxZ, forwardX, forwardY, rightX, rightY, maxLocalX, minLocalY);
        _cachedAabbPoints[aabbBaseIndex + 5] = TransformBoundsCorner(posX, posY, maxZ, forwardX, forwardY, rightX, rightY, maxLocalX, maxLocalY);
        _cachedAabbPoints[aabbBaseIndex + 6] = TransformBoundsCorner(posX, posY, maxZ, forwardX, forwardY, rightX, rightY, minLocalX, minLocalY);
        _cachedAabbPoints[aabbBaseIndex + 7] = TransformBoundsCorner(posX, posY, maxZ, forwardX, forwardY, rightX, rightY, minLocalX, maxLocalY);
    }

    /// <summary>
    /// Converts a skeleton point from the player's local coordinate space
    /// to the world coordinate space, using the player's facing direction.
    /// </summary>
    private static Vector3 TransformModelPoint(
        float originX,
        float originY,
        float originZ,
        float forwardX,
        float forwardY,
        float rightX,
        float rightY,
        Vector3 localPoint)
    {
        return new Vector3(
            originX - forwardX * localPoint.X - rightX * localPoint.Y,
            originY - forwardY * localPoint.X - rightY * localPoint.Y,
            originZ + localPoint.Z);
    }

    /// <summary>
    /// Converts an AABB corner from the player's local space to world space.
    /// The Z coordinate is passed directly (already in world space).
    /// </summary>
    private static Vector3 TransformBoundsCorner(
        float originX,
        float originY,
        float z,
        float forwardX,
        float forwardY,
        float rightX,
        float rightY,
        float localX,
        float localY)
    {
        return new Vector3(
            originX + forwardX * localX - rightX * localY,
            originY + forwardY * localX - rightY * localY,
            z);
    }

    /// <summary>
    /// Fires a single ray from origin to end and determines if the path is clear.
    /// Returns true (visible), false (blocked by wall), or null (budget exceeded).
    /// Also applies the "near hit" threshold: if the ray hit a wall but landed
    /// very close to the target point, it still counts as visible (prevents pop-in
    /// caused by the target's own collision geometry).
    /// </summary>
    private bool? TraceIsVisible(float originX, float originY, float originZ, float endX, float endY, float endZ)
    {
        if (_maxRaycastsPerFrame > 0 && RaycastsThisFrame >= _maxRaycastsPerFrame)
            return null;

        _reusableTraceOrigin.X = originX;
        _reusableTraceOrigin.Y = originY;
        _reusableTraceOrigin.Z = originZ;
        _reusableTraceEnd.X = endX;
        _reusableTraceEnd.Y = endY;
        _reusableTraceEnd.Z = endZ;

        bool success = _rayTrace.TraceEndShape(
            _reusableTraceOrigin,
            _reusableTraceEnd,
            null,
            _traceOptions,
            out TraceResult result);

        RaycastsThisFrame++;

        if (success && !result.DidHit)
        {
            RecordDebugRay(originX, originY, originZ, endX, endY, endZ, visible: true);
            return true;
        }

        if (success && result.DidHit)
        {
            if (IsTraceResultVisible(in result, _visibleHitFractionThreshold, _visibleHitDistanceUnits, originX, originY, originZ, endX, endY, endZ))
            {
                RecordDebugRay(originX, originY, originZ, endX, endY, endZ, visible: true);
                return true;
            }

            RecordDebugRay(originX, originY, originZ, result.EndPosX, result.EndPosY, result.EndPosZ, visible: false);
            return false;
        }

        RecordDebugRay(originX, originY, originZ, endX, endY, endZ, visible: false);
        return false;
    }

    /// <summary>
    /// Interprets a ray trace result: did the ray reach the target?
    /// A ray is "visible" if:
    ///   - It did not hit anything (fraction = 1.0), OR
    ///   - It hit something, but the fraction is above the threshold (very close to the target), OR
    ///   - The hit point is within RayHitDistanceThreshold units of the target point.
    /// This tolerance prevents false "hidden" results caused by the target's own body
    /// blocking the last tiny segment of the ray.
    /// </summary>
    private static bool IsTraceResultVisible(
        in TraceResult result,
        float visibleHitFractionThreshold,
        float visibleHitDistanceUnits,
        float originX,
        float originY,
        float originZ,
        float endX,
        float endY,
        float endZ)
    {
        if (!result.DidHit)
            return true;

        if (result.Fraction >= visibleHitFractionThreshold)
            return true;

        if (visibleHitDistanceUnits > 0.0f)
        {
            float dx = result.EndPosX - endX;
            float dy = result.EndPosY - endY;
            float dz = result.EndPosZ - endZ;
            float hitDistSqr = dx * dx + dy * dy + dz * dz;
            if (hitDistSqr <= visibleHitDistanceUnits * visibleHitDistanceUnits)
                return true;
        }

        return false;
    }

    /// <summary>Records a ray for debug visualization (only when ShowRayLines is enabled).</summary>
    private void RecordDebugRay(float startX, float startY, float startZ, float endX, float endY, float endZ, bool visible)
    {
        if (!_config.Debug.ShowRayLines || _debugRayCount >= MaxDebugRays)
            return;

        _debugRays[_debugRayCount++] = new DebugRay
        {
            Start = new Vector3(startX, startY, startZ),
            End = new Vector3(endX, endY, endZ),
            Visible = visible
        };
    }
}
