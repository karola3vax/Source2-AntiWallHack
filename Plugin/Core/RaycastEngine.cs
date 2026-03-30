using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Utils;
using RayTraceAPI;
using S2FOW.Config;
using S2FOW.Models;
using Vector3 = System.Numerics.Vector3;

namespace S2FOW.Core;

public class RaycastEngine
{
    public readonly struct VisibilityResult
    {
        public bool IsVisible { get; init; }
        public bool BudgetExceeded { get; init; }
        public TraceCountBreakdown TraceCounts { get; init; }
    }

    public readonly struct TraceCountBreakdown
    {
        public int Skeleton { get; init; }
        public int Aabb { get; init; }
        public int Total => Skeleton + Aabb;
    }

    private enum PrimitiveTraceState
    {
        Hidden = 0,
        Visible = 1,
        BudgetExceeded = 2
    }

    public const int VisibilityPrimitiveCount = Cs2VisibilityPrimitiveLayout.PrimitiveCount;
    public const int SkeletonGraphEdgeCount = VisibilityPrimitiveCount + 18;
    public const int SkeletonPointCount = VisibilityPrimitiveCount;
    public const int MaxBasePoints = Cs2VisibilityPrimitiveLayout.PrimitiveCount + Cs2VisibilityPrimitiveLayout.AabbPointCount;
    public const int MaxVisibilityTestPoints = Cs2VisibilityPrimitiveLayout.MaxVisibilityTestPoints;
    public const int MaxDebugPointsPerObserver = FowConstants.MaxSlots * MaxVisibilityTestPoints;
    public const int MaxDebugLinesPerObserver = FowConstants.MaxSlots * SkeletonGraphEdgeCount;
    public const int MaxDebugRays = 512;

    private const int AabbPointCount = Cs2VisibilityPrimitiveLayout.AabbPointCount;
    private const float ElevatedHeadTraceOffsetZ = 28.0f;
    private const float CandidateDedupDistanceSqr = 0.0625f;
    private const float StandingCombatPoseBlend = 0.96f;
    private const float MovingCombatPoseBlendPenalty = 0.18f;
    private const float ScopedCombatPoseBlendBonus = 0.18f;
    private const float DuckCombatPoseBlendBonus = 0.24f;
    private const float CrouchHeightDropUnits = 16.0f;

    public static bool IsHeadPointIndex(int pointIndex) => pointIndex == 0;

    private readonly CRayTraceInterface _rayTrace;
    private readonly S2FOWConfig _config;
    private readonly TraceOptions _traceOptions;
    private int _maxRaycastsPerFrame;
    private readonly float _visibleHitFractionThreshold;
    private readonly float _visibleHitDistanceUnits;
    private readonly float _tickInterval;

    private readonly int[] _cachedGeometryTicks = new int[FowConstants.MaxSlots];
    private readonly Vector3[] _cachedPrimitivePoint0 = new Vector3[FowConstants.MaxSlots * VisibilityPrimitiveCount];
    private readonly Vector3[] _cachedPrimitivePoint1 = new Vector3[FowConstants.MaxSlots * VisibilityPrimitiveCount];
    private readonly Vector3[] _cachedPrimitiveMid = new Vector3[FowConstants.MaxSlots * VisibilityPrimitiveCount];
    private readonly Vector3[] _cachedAabbPoints = new Vector3[FowConstants.MaxSlots * AabbPointCount];
    private readonly DebugRay[] _debugRays = new DebugRay[MaxDebugRays];
    private readonly Vector _reusableTraceOrigin = new(0, 0, 0);
    private readonly Vector _reusableTraceEnd = new(0, 0, 0);
    private int _debugRayCount;

    public int RaycastsThisFrame { get; private set; }
    internal ReadOnlySpan<DebugRay> DebugRays => _debugRays.AsSpan(0, _debugRayCount);

    public float TickInterval => _tickInterval;

    public RaycastEngine(CRayTraceInterface rayTrace, S2FOWConfig config, float? tickIntervalOverride = null)
    {
        _rayTrace = rayTrace;
        _config = config;
        _maxRaycastsPerFrame = Math.Max(0, config.Performance.MaxRaycastsPerFrame);
        _visibleHitFractionThreshold = Math.Clamp(config.Performance.RayHitFractionThreshold, 0.0f, 1.0f);
        _visibleHitDistanceUnits = Math.Max(0.0f, config.Performance.RayHitDistanceThreshold);
        _traceOptions = new TraceOptions(
            InteractionLayers.None,
            InteractionLayers.MASK_WORLD_ONLY,
            InteractionLayers.None,
            false);

        _tickInterval = tickIntervalOverride ?? Server.TickInterval;
        Array.Fill(_cachedGeometryTicks, -1);
    }

    public void ResetFrameCounter()
    {
        RaycastsThisFrame = 0;
        _debugRayCount = 0;
    }

    public void SetFrameBudget(int budget)
    {
        _maxRaycastsPerFrame = Math.Max(0, budget);
    }

    public bool TryGetAimRaySegment(
        in PlayerSnapshot observer,
        out float originX,
        out float originY,
        out float originZ,
        out float endX,
        out float endY,
        out float endZ)
    {
        float maxDistanceUnits = _config.AntiWallhack.CrosshairRevealDistance;
        if (maxDistanceUnits <= 0.0f)
        {
            originX = 0.0f;
            originY = 0.0f;
            originZ = 0.0f;
            endX = 0.0f;
            endY = 0.0f;
            endZ = 0.0f;
            return false;
        }

        RaycastMath.ComputeObserverRayOriginNoPrediction(in observer, _config, out originX, out originY, out originZ);
        RaycastMath.GetAimForwardVector(observer.Pitch, observer.Yaw, out float forwardX, out float forwardY, out float forwardZ);

        float intendedEndX = originX + forwardX * maxDistanceUnits;
        float intendedEndY = originY + forwardY * maxDistanceUnits;
        float intendedEndZ = originZ + forwardZ * maxDistanceUnits;

        if (_maxRaycastsPerFrame > 0 && RaycastsThisFrame >= _maxRaycastsPerFrame)
        {
            endX = intendedEndX;
            endY = intendedEndY;
            endZ = intendedEndZ;
            return false;
        }

        _reusableTraceOrigin.X = originX;
        _reusableTraceOrigin.Y = originY;
        _reusableTraceOrigin.Z = originZ;
        _reusableTraceEnd.X = intendedEndX;
        _reusableTraceEnd.Y = intendedEndY;
        _reusableTraceEnd.Z = intendedEndZ;

        bool success = _rayTrace.TraceEndShape(
            _reusableTraceOrigin,
            _reusableTraceEnd,
            null,
            _traceOptions,
            out TraceResult result);
        RaycastsThisFrame++;

        if (!success)
        {
            endX = intendedEndX;
            endY = intendedEndY;
            endZ = intendedEndZ;
            RecordDebugRay(originX, originY, originZ, intendedEndX, intendedEndY, intendedEndZ, visible: false, elevated: false, aim: true);
            return false;
        }

        if (result.DidHit)
        {
            endX = result.HitPointX;
            endY = result.HitPointY;
            endZ = result.HitPointZ;
            RecordDebugRay(originX, originY, originZ, endX, endY, endZ, visible: true, elevated: false, aim: true);
            return true;
        }

        endX = intendedEndX;
        endY = intendedEndY;
        endZ = intendedEndZ;
        RecordDebugRay(originX, originY, originZ, intendedEndX, intendedEndY, intendedEndZ, visible: true, elevated: false, aim: true);
        return true;
    }

    public bool? TraceLineVisibility(
        float originX,
        float originY,
        float originZ,
        float endX,
        float endY,
        float endZ,
        bool elevated = false)
    {
        return TraceIsVisible(originX, originY, originZ, endX, endY, endZ, elevated);
    }

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
        Vector3 observerOrigin = new(eyeOriginX, eyeOriginY, eyeOriginZ);
        Vector3 fixedObserverOrigin = new(fixedEyeOriginX, fixedEyeOriginY, fixedEyeOriginZ);
        int skeletonTraceCount = 0;
        int aabbTraceCount = 0;
        ReadOnlySpan<VisibilityPrimitive> primitives = Cs2VisibilityPrimitiveLayout.Primitives;

        for (int i = 0; i < maxPrimitiveCount; i++)
        {
            ref readonly var primitive = ref primitives[i];
            Vector3 p0 = _cachedPrimitivePoint0[primitiveBaseIndex + i];
            Vector3 p1 = _cachedPrimitivePoint1[primitiveBaseIndex + i];
            Vector3 mid = _cachedPrimitiveMid[primitiveBaseIndex + i];

            PrimitiveTraceState traceState = primitive.Kind == VisibilityPrimitiveKind.Sphere
                ? TraceSpherePrimitive(
                    in primitive,
                    observerOrigin,
                    fixedObserverOrigin,
                    p0,
                    ref skeletonTraceCount)
                : TraceCapsulePrimitive(
                    in primitive,
                    observerOrigin,
                    fixedObserverOrigin,
                    p0,
                    p1,
                    mid,
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
            bool? visibility = TraceIsVisible(eyeOriginX, eyeOriginY, eyeOriginZ, corner.X, corner.Y, corner.Z, elevated: false);
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

    public int FillBaseCheckPoints(in PlayerSnapshot target, Span<Vector3> output, int currentTick)
    {
        return FillBaseCheckPoints(in target, output, Span<bool>.Empty, currentTick);
    }

    public int FillBaseCheckPoints(in PlayerSnapshot target, Span<Vector3> output, int currentTick, int maxCheckPoints)
    {
        return FillBaseCheckPoints(in target, output, Span<bool>.Empty, currentTick, maxCheckPoints);
    }

    public int FillBaseCheckPoints(in PlayerSnapshot target, Span<Vector3> output, Span<bool> validityOutput, int currentTick)
    {
        return FillBaseCheckPoints(in target, output, validityOutput, currentTick, VisibilityPrimitiveCount);
    }

    public int FillBaseCheckPoints(in PlayerSnapshot target, Span<Vector3> output, Span<bool> validityOutput, int currentTick, int maxCheckPoints)
    {
        EnsureTargetGeometryBuilt(in target, currentTick);

        int maxPrimitiveCount = Math.Clamp(maxCheckPoints, 0, VisibilityPrimitiveCount);
        int primitiveBaseIndex = target.Slot * VisibilityPrimitiveCount;
        int aabbBaseIndex = target.Slot * AabbPointCount;
        int written = 0;

        for (int i = 0; i < maxPrimitiveCount && written < output.Length; i++, written++)
        {
            output[written] = _cachedPrimitiveMid[primitiveBaseIndex + i];
            if (written < validityOutput.Length)
                validityOutput[written] = true;
        }

        for (int i = 0; i < AabbPointCount && written < output.Length; i++, written++)
        {
            output[written] = _cachedAabbPoints[aabbBaseIndex + i];
            if (written < validityOutput.Length)
                validityOutput[written] = true;
        }

        return written;
    }

    public int FillSkeletonGraphLines(in PlayerSnapshot target, Span<Vector3> startOutput, Span<Vector3> endOutput, int currentTick)
    {
        EnsureTargetGeometryBuilt(in target, currentTick);

        int count = Math.Min(SkeletonGraphEdgeCount, Math.Min(startOutput.Length, endOutput.Length));
        if (count <= 0)
            return 0;

        int primitiveBaseIndex = target.Slot * VisibilityPrimitiveCount;
        Vector3 Mid(int primitiveIndex) => _cachedPrimitiveMid[primitiveBaseIndex + primitiveIndex];

        Vector3 Proximal(int primitiveIndex)
        {
            VisibilityPrimitive primitive = Cs2VisibilityPrimitiveLayout.Primitives[primitiveIndex];
            Vector3 p0 = _cachedPrimitivePoint0[primitiveBaseIndex + primitiveIndex];
            Vector3 p1 = _cachedPrimitivePoint1[primitiveBaseIndex + primitiveIndex];
            return primitive.DistalEndpointIsPoint1 ? p0 : p1;
        }

        Vector3 Distal(int primitiveIndex)
        {
            VisibilityPrimitive primitive = Cs2VisibilityPrimitiveLayout.Primitives[primitiveIndex];
            Vector3 p0 = _cachedPrimitivePoint0[primitiveBaseIndex + primitiveIndex];
            Vector3 p1 = _cachedPrimitivePoint1[primitiveBaseIndex + primitiveIndex];
            return primitive.DistalEndpointIsPoint1 ? p1 : p0;
        }

        static Vector3 Avg(Vector3 a, Vector3 b) => (a + b) * 0.5f;

        Vector3 head = Mid(0);
        Vector3 neck = Mid(1);
        Vector3 spine3 = Mid(2);
        Vector3 spine2 = Mid(3);
        Vector3 spine1 = Mid(4);
        Vector3 spine0 = Mid(5);
        Vector3 pelvis = Mid(6);

        Vector3 leftShoulder = Proximal(11);
        Vector3 rightShoulder = Proximal(12);
        Vector3 leftElbow = Avg(Distal(11), Proximal(13));
        Vector3 rightElbow = Avg(Distal(12), Proximal(14));
        Vector3 leftHand = Distal(15);
        Vector3 rightHand = Distal(16);

        Vector3 leftHip = Proximal(17);
        Vector3 rightHip = Proximal(18);
        Vector3 leftKnee = Avg(Distal(17), Proximal(9));
        Vector3 rightKnee = Avg(Distal(18), Proximal(10));
        Vector3 leftFoot = Distal(7);
        Vector3 rightFoot = Distal(8);

        int written = 0;

        for (int primitiveIndex = 0; primitiveIndex < VisibilityPrimitiveCount; primitiveIndex++)
            written = WriteLine(startOutput, endOutput, written, count, Proximal(primitiveIndex), Distal(primitiveIndex));

        written = WriteLine(startOutput, endOutput, written, count, head, neck);
        written = WriteLine(startOutput, endOutput, written, count, neck, spine3);
        written = WriteLine(startOutput, endOutput, written, count, spine3, spine2);
        written = WriteLine(startOutput, endOutput, written, count, spine2, spine1);
        written = WriteLine(startOutput, endOutput, written, count, spine1, spine0);
        written = WriteLine(startOutput, endOutput, written, count, spine0, pelvis);
        written = WriteLine(startOutput, endOutput, written, count, spine3, leftShoulder);
        written = WriteLine(startOutput, endOutput, written, count, leftShoulder, leftElbow);
        written = WriteLine(startOutput, endOutput, written, count, leftElbow, leftHand);
        written = WriteLine(startOutput, endOutput, written, count, spine3, rightShoulder);
        written = WriteLine(startOutput, endOutput, written, count, rightShoulder, rightElbow);
        written = WriteLine(startOutput, endOutput, written, count, rightElbow, rightHand);
        written = WriteLine(startOutput, endOutput, written, count, pelvis, leftHip);
        written = WriteLine(startOutput, endOutput, written, count, leftHip, leftKnee);
        written = WriteLine(startOutput, endOutput, written, count, leftKnee, leftFoot);
        written = WriteLine(startOutput, endOutput, written, count, pelvis, rightHip);
        written = WriteLine(startOutput, endOutput, written, count, rightHip, rightKnee);
        written = WriteLine(startOutput, endOutput, written, count, rightKnee, rightFoot);

        return written;
    }

    private static int WriteLine(Span<Vector3> startOutput, Span<Vector3> endOutput, int index, int limit, Vector3 start, Vector3 end)
    {
        if (index >= limit)
            return index;

        startOutput[index] = start;
        endOutput[index] = end;
        return index + 1;
    }

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
        Vector3 observerOrigin = new(observerX, observerY, observerZ);
        ReadOnlySpan<VisibilityPrimitive> primitives = Cs2VisibilityPrimitiveLayout.Primitives;
        int written = 0;
        Span<Vector3> candidates = stackalloc Vector3[3];

        for (int i = 0; i < maxPrimitiveCount && written < output.Length; i++)
        {
            ref readonly var primitive = ref primitives[i];
            Vector3 p0 = _cachedPrimitivePoint0[primitiveBaseIndex + i];
            Vector3 p1 = _cachedPrimitivePoint1[primitiveBaseIndex + i];
            Vector3 mid = _cachedPrimitiveMid[primitiveBaseIndex + i];
            int candidateCount = primitive.Kind == VisibilityPrimitiveKind.Sphere
                ? BuildSphereCandidates(in primitive, observerOrigin, p0, candidates)
                : BuildCapsuleCandidates(in primitive, observerOrigin, p0, p1, mid, candidates);

            for (int c = 0; c < candidateCount && written < output.Length; c++)
            {
                output[written++] = candidates[c];
                if (written - 1 < isAabbFallbackOutput.Length)
                    isAabbFallbackOutput[written - 1] = false;
            }
        }

        for (int i = 0; i < AabbPointCount && written < output.Length; i++)
        {
            output[written++] = _cachedAabbPoints[aabbBaseIndex + i];
            if (written - 1 < isAabbFallbackOutput.Length)
                isAabbFallbackOutput[written - 1] = true;
        }

        return written;
    }

    private PrimitiveTraceState TraceSpherePrimitive(
        in VisibilityPrimitive primitive,
        Vector3 observerOrigin,
        Vector3 fixedObserverOrigin,
        Vector3 center,
        ref int traceCount)
    {
        Span<Vector3> candidates = stackalloc Vector3[3];
        int candidateCount = BuildSphereCandidates(in primitive, observerOrigin, center, candidates);
        return TracePrimitiveCandidates(in primitive, observerOrigin, fixedObserverOrigin, candidates[..candidateCount], ref traceCount);
    }

    private PrimitiveTraceState TraceCapsulePrimitive(
        in VisibilityPrimitive primitive,
        Vector3 observerOrigin,
        Vector3 fixedObserverOrigin,
        Vector3 p0,
        Vector3 p1,
        Vector3 mid,
        ref int traceCount)
    {
        Span<Vector3> candidates = stackalloc Vector3[3];
        int candidateCount = BuildCapsuleCandidates(in primitive, observerOrigin, p0, p1, mid, candidates);
        return TracePrimitiveCandidates(in primitive, observerOrigin, fixedObserverOrigin, candidates[..candidateCount], ref traceCount);
    }

    private PrimitiveTraceState TracePrimitiveCandidates(
        in VisibilityPrimitive primitive,
        Vector3 observerOrigin,
        Vector3 fixedObserverOrigin,
        ReadOnlySpan<Vector3> candidates,
        ref int traceCount)
    {
        float startX = primitive.UseFixedHeadOrigin ? fixedObserverOrigin.X : observerOrigin.X;
        float startY = primitive.UseFixedHeadOrigin ? fixedObserverOrigin.Y : observerOrigin.Y;
        float startZ = primitive.UseFixedHeadOrigin ? fixedObserverOrigin.Z : observerOrigin.Z;

        for (int i = 0; i < candidates.Length; i++)
        {
            traceCount++;
            Vector3 candidate = candidates[i];
            bool? visibility = TraceIsVisible(startX, startY, startZ, candidate.X, candidate.Y, candidate.Z, elevated: false);
            if (visibility == null)
                return PrimitiveTraceState.BudgetExceeded;

            if (visibility.Value)
                return PrimitiveTraceState.Visible;

            if (primitive.UseFixedHeadOrigin && i == 0)
            {
                traceCount++;
                float elevatedEyeOriginZ = fixedObserverOrigin.Z + ElevatedHeadTraceOffsetZ;
                visibility = TraceIsVisible(fixedObserverOrigin.X, fixedObserverOrigin.Y, elevatedEyeOriginZ, candidate.X, candidate.Y, candidate.Z, elevated: true);
                if (visibility == null)
                    return PrimitiveTraceState.BudgetExceeded;

                if (visibility.Value)
                    return PrimitiveTraceState.Visible;
            }
        }

        return PrimitiveTraceState.Hidden;
    }

    private static int BuildSphereCandidates(
        in VisibilityPrimitive primitive,
        Vector3 observerOrigin,
        Vector3 center,
        Span<Vector3> output)
    {
        int count = 0;
        TryAppendCandidate(output, ref count, ComputeSphereSupportPoint(center, primitive.Radius, observerOrigin));
        TryAppendCandidate(output, ref count, center);
        return count;
    }

    private static int BuildCapsuleCandidates(
        in VisibilityPrimitive primitive,
        Vector3 observerOrigin,
        Vector3 p0,
        Vector3 p1,
        Vector3 mid,
        Span<Vector3> output)
    {
        int count = 0;
        TryAppendCandidate(output, ref count, ComputeCapsuleSupportPoint(p0, p1, primitive.Radius, observerOrigin));

        if (primitive.Sampling == VisibilityPrimitiveSampling.SupportAndEndpoints)
        {
            TryAppendCandidate(output, ref count, p0);
            TryAppendCandidate(output, ref count, p1);
            return count;
        }

        TryAppendCandidate(output, ref count, mid);
        TryAppendCandidate(output, ref count, primitive.DistalEndpointIsPoint1 ? p1 : p0);
        return count;
    }

    private static Vector3 ComputeSphereSupportPoint(Vector3 center, float radius, Vector3 observerOrigin)
    {
        Vector3 dir = observerOrigin - center;
        float lenSqr = dir.LengthSquared();
        if (lenSqr <= float.Epsilon || radius <= 0.0f)
            return center;

        return center + (dir / MathF.Sqrt(lenSqr)) * radius;
    }

    private static Vector3 ComputeCapsuleSupportPoint(Vector3 p0, Vector3 p1, float radius, Vector3 observerOrigin)
    {
        Vector3 axis = p1 - p0;
        float axisLenSqr = axis.LengthSquared();
        Vector3 axisPoint;
        if (axisLenSqr <= float.Epsilon)
        {
            axisPoint = p0;
        }
        else
        {
            float t = Vector3.Dot(observerOrigin - p0, axis) / axisLenSqr;
            t = Math.Clamp(t, 0.0f, 1.0f);
            axisPoint = p0 + axis * t;
        }

        Vector3 toObserver = observerOrigin - axisPoint;
        float toObserverLenSqr = toObserver.LengthSquared();
        if (toObserverLenSqr <= float.Epsilon || radius <= 0.0f)
            return axisPoint;

        return axisPoint + (toObserver / MathF.Sqrt(toObserverLenSqr)) * radius;
    }

    private static void TryAppendCandidate(Span<Vector3> output, ref int count, Vector3 candidate)
    {
        for (int i = 0; i < count; i++)
        {
            if (Vector3.DistanceSquared(output[i], candidate) <= CandidateDedupDistanceSqr)
                return;
        }

        if (count < output.Length)
            output[count++] = candidate;
    }

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
            Vector3 p0 = TransformModelPoint(posX, posY, posZ, forwardX, forwardY, rightX, rightY, primitive.LocalPoint0);
            Vector3 p1 = primitive.Kind == VisibilityPrimitiveKind.Sphere
                ? p0
                : TransformModelPoint(
                    posX,
                    posY,
                    posZ,
                    forwardX,
                    forwardY,
                    rightX,
                    rightY,
                    primitive.LocalPoint1);

            _cachedPrimitivePoint0[primitiveBaseIndex + i] = p0;
            _cachedPrimitivePoint1[primitiveBaseIndex + i] = p1;
            _cachedPrimitiveMid[primitiveBaseIndex + i] = primitive.Kind == VisibilityPrimitiveKind.Sphere
                ? p0
                : (p0 + p1) * 0.5f;
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
            originX + forwardX * localPoint.X - rightX * localPoint.Y,
            originY + forwardY * localPoint.X - rightY * localPoint.Y,
            originZ + localPoint.Z);
    }

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

    private static float ComputeCombatPoseBlend(in PlayerSnapshot target, float crouchBlend)
    {
        float planarSpeed = MathF.Sqrt(target.VelX * target.VelX + target.VelY * target.VelY);
        float normalizedMoveSpeed = target.WeaponMaxSpeed > 1.0f
            ? Math.Clamp(planarSpeed / target.WeaponMaxSpeed, 0.0f, 1.0f)
            : 0.0f;

        float blend = StandingCombatPoseBlend;
        blend -= normalizedMoveSpeed * MovingCombatPoseBlendPenalty;
        blend += crouchBlend * DuckCombatPoseBlendBonus;
        if (target.IsScoped)
            blend += ScopedCombatPoseBlendBonus;

        return Math.Clamp(blend, 0.55f, 1.10f);
    }

    private static Vector3 ApplyCombatPoseOffset(
        int primitiveIndex,
        Vector3 localPoint,
        bool isPoint1,
        float combatPoseBlend,
        float crouchBlend)
    {
        Vector3 poseOffset = primitiveIndex switch
        {
            0 => ScaleOffset(new Vector3(2.65f, 0.00f, -1.10f), combatPoseBlend),
            1 => ScaleOffset(new Vector3(3.85f, 0.00f, -1.65f), combatPoseBlend),
            2 => ScaleOffset(new Vector3(4.35f, 0.00f, -1.95f), combatPoseBlend),
            3 => ScaleOffset(new Vector3(3.35f, 0.00f, -1.45f), combatPoseBlend),
            4 => ScaleOffset(new Vector3(2.05f, 0.00f, -0.75f), combatPoseBlend),
            5 => ScaleOffset(new Vector3(0.55f, 0.00f, -0.30f), combatPoseBlend),
            6 => ScaleOffset(new Vector3(-0.20f, 0.00f, -0.40f), combatPoseBlend),
            7 => ScaleOffset(isPoint1 ? new Vector3(2.40f, 3.70f, -1.25f) : new Vector3(1.60f, 2.80f, -0.95f), combatPoseBlend),
            8 => ScaleOffset(isPoint1 ? new Vector3(-6.00f, -5.00f, -1.00f) : new Vector3(-4.20f, -4.00f, -0.70f), combatPoseBlend),
            9 => ScaleOffset(isPoint1 ? new Vector3(3.10f, 4.40f, -3.00f) : new Vector3(6.70f, 3.10f, -0.40f), combatPoseBlend),
            10 => ScaleOffset(isPoint1 ? new Vector3(-6.80f, -5.80f, -1.80f) : new Vector3(1.80f, -4.10f, -1.10f), combatPoseBlend),
            11 => ScaleOffset(isPoint1 ? new Vector3(10.50f, -7.80f, 1.20f) : new Vector3(5.00f, -3.80f, -0.80f), combatPoseBlend),
            12 => ScaleOffset(isPoint1 ? new Vector3(5.50f, 6.00f, 1.00f) : new Vector3(2.00f, 2.00f, -0.80f), combatPoseBlend),
            13 => ScaleOffset(isPoint1 ? new Vector3(14.80f, -14.80f, 8.20f) : new Vector3(8.70f, -8.90f, 2.30f), combatPoseBlend),
            14 => ScaleOffset(isPoint1 ? new Vector3(7.00f, 8.00f, 6.20f) : new Vector3(3.00f, 3.20f, 1.10f), combatPoseBlend),
            15 => ScaleOffset(isPoint1 ? new Vector3(15.60f, -17.60f, 12.90f) : new Vector3(14.10f, -16.40f, 11.10f), combatPoseBlend),
            16 => ScaleOffset(isPoint1 ? new Vector3(9.50f, 11.00f, 10.30f) : new Vector3(8.00f, 9.40f, 8.20f), combatPoseBlend),
            17 => ScaleOffset(isPoint1 ? new Vector3(7.20f, 4.20f, -0.75f) : new Vector3(1.70f, 2.70f, -1.15f), combatPoseBlend),
            18 => ScaleOffset(isPoint1 ? new Vector3(0.80f, -5.10f, -1.80f) : new Vector3(-3.80f, -3.10f, -0.75f), combatPoseBlend),
            _ => Vector3.Zero
        };

        poseOffset += ScaleOffset(GetLeadSideStanceOffset(primitiveIndex, isPoint1), combatPoseBlend);

        if (crouchBlend > 0.0f)
        {
            float heightDropFactor = primitiveIndex switch
            {
                0 => 1.00f,
                1 => 0.95f,
                2 => 0.88f,
                3 => 0.76f,
                4 => 0.62f,
                5 => 0.45f,
                6 => 0.18f,
                11 or 12 or 13 or 14 or 15 or 16 => 0.82f,
                17 or 18 => 0.35f,
                9 or 10 => -0.10f,
                7 or 8 => -0.22f,
                _ => 0.0f
            };

            float forwardCrouchFactor = primitiveIndex switch
            {
                17 or 18 => 0.18f,
                9 or 10 => 0.28f,
                7 or 8 => 0.22f,
                2 or 3 or 4 or 11 or 12 or 13 or 14 or 15 or 16 => 0.12f,
                _ => 0.0f
            };

            poseOffset += new Vector3(
                CrouchHeightDropUnits * forwardCrouchFactor * crouchBlend,
                0.0f,
                -CrouchHeightDropUnits * heightDropFactor * crouchBlend);
        }

        return localPoint + poseOffset;
    }

    private static Vector3 ScaleOffset(Vector3 offset, float amount)
    {
        return offset * amount;
    }

    private static Vector3 GetLeadSideStanceOffset(int primitiveIndex, bool isPoint1)
    {
        Vector3 offset = primitiveIndex switch
        {
            0 => new Vector3(0.65f, 0.25f, -0.12f),
            1 => new Vector3(1.10f, 0.48f, -0.18f),
            2 => new Vector3(1.70f, 1.05f, -0.25f),
            3 => new Vector3(1.30f, 0.88f, -0.20f),
            4 => new Vector3(0.85f, 0.58f, -0.12f),
            5 => new Vector3(-0.10f, 0.40f, -0.08f),
            6 => new Vector3(-0.95f, 0.30f, 0.00f),
            7 => new Vector3(2.10f, 0.85f, -0.50f),
            8 => new Vector3(-5.70f, -1.50f, 0.25f),
            9 => new Vector3(2.60f, 1.00f, -1.10f),
            10 => new Vector3(-6.00f, -2.00f, 0.45f),
            11 => new Vector3(3.10f, -1.20f, 0.20f),
            12 => new Vector3(-5.50f, -0.30f, -0.80f),
            13 => new Vector3(3.70f, -4.50f, 0.30f),
            14 => new Vector3(-6.20f, 0.20f, -0.60f),
            15 => new Vector3(4.10f, -5.90f, 0.90f),
            16 => new Vector3(-5.50f, 1.20f, 0.20f),
            17 => new Vector3(2.60f, 0.95f, -0.80f),
            18 => new Vector3(-6.70f, -1.40f, 0.45f),
            _ => Vector3.Zero
        };

        if (isPoint1)
        {
            float distalScale = primitiveIndex switch
            {
                7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 => 1.15f,
                _ => 1.0f
            };

            offset *= distalScale;
        }

        return offset;
    }

    private bool? TraceIsVisible(float originX, float originY, float originZ, float endX, float endY, float endZ, bool elevated)
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
            RecordDebugRay(originX, originY, originZ, endX, endY, endZ, visible: true, elevated: elevated);
            return true;
        }

        if (success && result.DidHit)
        {
            if (IsTraceResultVisible(in result, _visibleHitFractionThreshold, _visibleHitDistanceUnits, originX, originY, originZ, endX, endY, endZ))
            {
                RecordDebugRay(originX, originY, originZ, endX, endY, endZ, visible: true, elevated: elevated);
                return true;
            }

            RecordDebugRay(originX, originY, originZ, result.HitPointX, result.HitPointY, result.HitPointZ, visible: false, elevated: elevated);
            return false;
        }

        RecordDebugRay(originX, originY, originZ, endX, endY, endZ, visible: false, elevated: elevated);
        return false;
    }

    public static bool IsTraceResultVisible(
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
            float dx = result.HitPointX - endX;
            float dy = result.HitPointY - endY;
            float dz = result.HitPointZ - endZ;
            float hitDistSqr = dx * dx + dy * dy + dz * dz;
            if (hitDistSqr <= visibleHitDistanceUnits * visibleHitDistanceUnits)
                return true;
        }

        return false;
    }

    private void RecordDebugRay(float startX, float startY, float startZ, float endX, float endY, float endZ, bool visible, bool elevated, bool aim = false)
    {
        if (!_config.Debug.ShowRayLines || _debugRayCount >= MaxDebugRays)
            return;

        _debugRays[_debugRayCount++] = new DebugRay
        {
            Aim = aim,
            Start = new Vector3(startX, startY, startZ),
            End = new Vector3(endX, endY, endZ),
            Visible = visible,
            Elevated = elevated
        };
    }
}
