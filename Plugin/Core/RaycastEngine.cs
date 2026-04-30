using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Utils;
using S2FOW.Config;
using S2FOW.Models;
using S2FOW.Util;
using Vector3 = System.Numerics.Vector3;

namespace S2FOW.Core;

/// <summary>
/// Performs the wall checks used by S2FOW.
///
/// A "raycast" is an invisible straight-line check from the viewer's eye to a point
/// on the enemy. RayTrace answers whether that line reaches the point or hits world
/// geometry such as a wall first.
///
/// This class checks two sets of points:
///   1. Detailed body points: head, shoulders, hips, knees, feet, and weapon tips.
///   2. Backup box points: the eight corners of a simple padded box around the enemy.
///
/// If any checked point is reachable, the enemy is visible. If every checked point is
/// blocked by the world, the enemy can be hidden. If the ray budget runs out or the
/// RayTrace call fails, the caller shows the enemy to stay safe.
/// </summary>

public class RaycastEngine
{
    /// <summary>
    /// The result of checking whether one viewer can see one enemy.
    /// </summary>
    public readonly struct VisibilityResult
    {
        /// <summary>True if at least one ray reached the enemy.</summary>
        public bool IsVisible { get; init; }

        /// <summary>True if S2FOW reached the configured per-frame raycast limit.</summary>
        public bool BudgetExceeded { get; init; }

        /// <summary>True if RayTrace failed; callers show the enemy when this happens.</summary>
        public bool TraceFailed { get; init; }

        /// <summary>How many detailed body checks and backup box checks were used.</summary>
        public TraceCountBreakdown TraceCounts { get; init; }
    }

    /// <summary>Breakdown of how many checks were used for detailed body points and backup box points.</summary>
    public readonly struct TraceCountBreakdown
    {
        /// <summary>Rays cast to detailed body points such as head, shoulders, and hips.</summary>
        public int Skeleton { get; init; }

        /// <summary>Rays cast to the eight backup box corners.</summary>
        public int Aabb { get; init; }

        /// <summary>Total rays cast.</summary>
        public int Total => Skeleton + Aabb;
    }

    /// <summary>Internal result of checking one body point.</summary>
    private enum PrimitiveTraceState
    {
        /// <summary>The ray hit a wall before reaching this point.</summary>
        Hidden = 0,

        /// <summary>The ray reached this point.</summary>
        Visible = 1,

        /// <summary>S2FOW reached the configured per-frame raycast limit.</summary>
        BudgetExceeded = 2,

        /// <summary>RayTrace failed, so the enemy must be shown by the caller.</summary>
        TraceFailed = 3
    }

    private enum TraceVisibilityState
    {
        Hidden = 0,
        Visible = 1,
        BudgetExceeded = 2,
        TraceFailed = 3
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Constants
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Number of detailed body points extracted from CS2 player model data.</summary>
    public const int VisibilityPrimitiveCount = Cs2VisibilityPrimitiveLayout.PrimitiveCount;

    /// <summary>Total check points per enemy: detailed body points plus eight backup box corners.</summary>
    public const int MaxVisibilityTestPoints = Cs2VisibilityPrimitiveLayout.MaxVisibilityTestPoints;

    /// <summary>Maximum debug points drawn for one viewer.</summary>
    public const int MaxDebugPointsPerObserver = 512;

    /// <summary>Maximum debug rays recorded per frame for visualization.</summary>
    public const int MaxDebugRays = 512;

    /// <summary>Number of backup box corner points.</summary>
    private const int AabbPointCount = Cs2VisibilityPrimitiveLayout.AabbPointCount;

    // ────────────────────────────────────────────────────────────────────────
    //  Dependencies and configuration
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>The RayTrace connection that performs the actual straight-line wall check.</summary>
    private readonly IRayTraceService _rayTrace;

    /// <summary>Active plugin configuration.</summary>
    private readonly S2FOWConfig _config;

    private readonly PerformanceMonitor? _perfMonitor;

    /// <summary>RayTrace options: check only the map/world, not players or other objects.</summary>
    private readonly TraceOptions _traceOptions;

    /// <summary>Maximum raycasts allowed this frame. 0 means unlimited.</summary>
    private int _maxRaycastsPerFrame;

    /// <summary>Server tick interval in seconds (1/64 ≈ 0.015625 for 64-tick).</summary>
    private readonly float _tickInterval;

    // ────────────────────────────────────────────────────────────────────────
    //  Per-enemy point cache, rebuilt once per frame per enemy
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>The tick when each enemy's body/box points were last built. -1 means never.</summary>
    private readonly int[] _cachedGeometryTicks = new int[FowConstants.MaxSlots];

    /// <summary>Cached world positions of each enemy's detailed body points.</summary>
    private readonly Vector3[] _cachedPrimitivePoints = new Vector3[FowConstants.MaxSlots * VisibilityPrimitiveCount];

    /// <summary>Cached world positions of each enemy's eight backup box corners.</summary>
    private readonly Vector3[] _cachedAabbPoints = new Vector3[FowConstants.MaxSlots * AabbPointCount];

    /// <summary>Debug rays recorded this frame for visualization.</summary>
    private readonly DebugRay[] _debugRays = new DebugRay[MaxDebugRays];

    /// <summary>Reusable Vector objects to avoid creating garbage while checking many enemies.</summary>
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
    /// Configures RayTrace to test only against world geometry such as walls and
    /// floors, not against players, weapons, or props.
    /// </summary>
    internal RaycastEngine(
        IRayTraceService rayTrace,
        S2FOWConfig config,
        PerformanceMonitor? perfMonitor = null,
        float? tickIntervalOverride = null)
    {
        _rayTrace = rayTrace;
        _config = config;
        _perfMonitor = perfMonitor;
        _maxRaycastsPerFrame = Math.Max(0, config.Performance.MaxRaycastsPerFrame);
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
    /// Checks where the viewer is aiming. If the aim point lands near an enemy's body,
    /// S2FOW shows that enemy because the viewer is directly aiming at them.
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

        if (!success)
            _perfMonitor?.RecordRayTraceFailure();

        endpoint = new Vector3(endX, endY, endZ);
        RecordDebugRay(originX, originY, originZ, endX, endY, endZ, visible: true);
        return success;
    }

    /// <summary>
    /// Checks whether the viewer can see the enemy.
    ///
    /// First, S2FOW checks detailed body points. If any point is clear, the enemy
    /// is visible. If all detailed body points are blocked, S2FOW checks the eight
    /// corners of a padded backup box around the enemy.
    ///
    /// If S2FOW cannot finish because the raycast limit is reached, the caller
    /// shows the enemy.
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

                case PrimitiveTraceState.TraceFailed:
                    return new VisibilityResult
                    {
                        IsVisible = false,
                        BudgetExceeded = false,
                        TraceFailed = true,
                        TraceCounts = new TraceCountBreakdown { Skeleton = skeletonTraceCount, Aabb = aabbTraceCount }
                    };
            }
        }

        for (int i = 0; i < AabbPointCount; i++)
        {
            aabbTraceCount++;
            Vector3 corner = _cachedAabbPoints[aabbBaseIndex + i];
            TraceVisibilityState visibility = TraceIsVisible(eyeOriginX, eyeOriginY, eyeOriginZ, corner.X, corner.Y, corner.Z);
            if (visibility == TraceVisibilityState.BudgetExceeded)
            {
                return new VisibilityResult
                {
                    IsVisible = false,
                    BudgetExceeded = true,
                    TraceCounts = new TraceCountBreakdown { Skeleton = skeletonTraceCount, Aabb = aabbTraceCount }
                };
            }

            if (visibility == TraceVisibilityState.TraceFailed)
            {
                return new VisibilityResult
                {
                    IsVisible = false,
                    BudgetExceeded = false,
                    TraceFailed = true,
                    TraceCounts = new TraceCountBreakdown { Skeleton = skeletonTraceCount, Aabb = aabbTraceCount }
                };
            }

            if (visibility == TraceVisibilityState.Visible)
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
    /// Fills an output buffer with all body/box points for an enemy. Smoke checks
    /// and debug drawing use these same points.
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
    /// Fills output buffers with body/box points and marks whether each point came
    /// from the backup box.
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
    /// Checks whether a body point applies to the enemy's current weapon.
    /// Weapon-tip points only apply when the enemy is holding that weapon class.
    /// </summary>
    private static bool ShouldUsePrimitiveForTarget(in VisibilityPrimitive primitive, in PlayerSnapshot target)
    {
        return primitive.RequiredWeaponClass == WeaponLosClass.None ||
               primitive.RequiredWeaponClass == target.ActiveWeaponLosClass;
    }

    /// <summary>
    /// Checks one detailed body point. Some body points use the stable eye position
    /// instead of the movement-predicted eye position to reduce jitter.
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
        TraceVisibilityState visibility = TraceIsVisible(startX, startY, startZ, point.X, point.Y, point.Z);
        if (visibility == TraceVisibilityState.BudgetExceeded)
            return PrimitiveTraceState.BudgetExceeded;

        if (visibility == TraceVisibilityState.TraceFailed)
            return PrimitiveTraceState.TraceFailed;

        if (visibility == TraceVisibilityState.Visible)
            return PrimitiveTraceState.Visible;

        return PrimitiveTraceState.Hidden;
    }

    /// <summary>
    /// Ensures the enemy's body/box points have been computed for this frame.
    /// The points are reused if the same enemy is checked again in the same tick.
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
    /// Builds world positions for all body points and the eight backup box corners
    /// around the enemy.
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
    /// Converts a body point from player-local coordinates into world coordinates,
    /// using the player's facing direction.
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
    /// Converts a backup box corner from player-local coordinates into world coordinates.
    /// The Z coordinate is already in world coordinates.
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
    /// Checks whether one straight path is clear. Any world hit before the enemy
    /// point means the path is blocked. RayTrace failures are reported separately
    /// so callers can show the enemy instead of hiding on missing data.
    /// </summary>
    private TraceVisibilityState TraceIsVisible(float originX, float originY, float originZ, float endX, float endY, float endZ)
    {
        if (_maxRaycastsPerFrame > 0 && RaycastsThisFrame >= _maxRaycastsPerFrame)
            return TraceVisibilityState.BudgetExceeded;

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
            return TraceVisibilityState.Visible;
        }

        if (success && result.DidHit)
        {
            RecordDebugRay(originX, originY, originZ, result.EndPosX, result.EndPosY, result.EndPosZ, visible: false);
            return TraceVisibilityState.Hidden;
        }

        _perfMonitor?.RecordRayTraceFailure();
        RecordDebugRay(originX, originY, originZ, endX, endY, endZ, visible: true);
        return TraceVisibilityState.TraceFailed;
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
