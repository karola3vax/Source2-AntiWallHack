using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using RayTraceAPI;

namespace S2AWH;

internal sealed class LosEvaluator
{
    private const int SlotCount = 65;
    private const int MaxAimRayCount = 5;
    private const int SurfaceProbePointCount = 18; // max: 3 probe rows * 6 faces
    private const float ViewerGroundProbeHeight = 16.0f;
    private const float DefaultAimRayDistance = 4096.0f;
    private const float MinAimRayDistance = 256.0f;
    private const float DegToRad = MathF.PI / 180.0f;
    private const float MicroHullHalfExtent = 2.0f;
    private static readonly (float LateralFactor, float VerticalFactor)[] MicroHullFaceExtremityPattern =
    {
        (-1.0f, 0.75f),  // upper-left shoulder/arm side
        (1.0f, 0.75f),   // upper-right shoulder/arm side
        (-1.0f, 0.0f),   // mid-left arm/torso side
        (1.0f, 0.0f),    // mid-right arm/torso side
        (-1.0f, -0.90f), // lower-left leg/foot side
        (1.0f, -0.90f)   // lower-right leg/foot side
    };
    private static readonly (float XFactor, float YFactor)[] MicroHullCapExtremityPattern =
    {
        (-0.85f, -0.85f),
        (0.85f, -0.85f),
        (-0.85f, 0.85f),
        (0.85f, 0.85f)
    };
    private static readonly float[] MicroHullSlitBandLateralPattern =
    {
        -0.95f,
        0.0f,
        0.95f
    };
    private static readonly float[] MicroHullSlitBandVerticalPattern =
    {
        0.55f,  // upper arm / shoulder band
        0.05f,  // forearm / chest / waist band
        -0.55f  // hip / knee / lower leg band
    };
    private static readonly (float PitchFactor, float YawFactor)[] AimRayPattern =
    {
        (0.0f, 0.0f),
        (1.0f, 1.0f),
        (-1.0f, -1.0f),
        (1.0f, -1.0f),
        (-1.0f, 1.0f)
    };

    private sealed class ViewerAimRayCacheEntry
    {
        public int Tick = -1;
        public int HitCount;
        public int AttemptedCount;
        public int SuccessfulCount;
        public Vector[] HitPoints = CreatePointBuffer(MaxAimRayCount);
    }

    private sealed class TargetSurfaceCacheEntry
    {
        public int Tick = -1;
        public int PointCount;
        public Vector[] SurfacePoints = CreatePointBuffer(SurfaceProbePointCount);
    }

    private readonly CRayTraceInterface _rayTrace;
    private readonly Action<int>? _recordViewerTraceAttempt;
    private readonly TraceOptions _cachedTraceOptions = VisibilityGeometry.GetVisibilityTraceOptions();
    private readonly ViewerAimRayCacheEntry?[] _viewerAimCacheBySlot = new ViewerAimRayCacheEntry?[SlotCount];
    private readonly TargetSurfaceCacheEntry?[] _targetSurfaceCacheBySlot = new TargetSurfaceCacheEntry?[SlotCount];
    private readonly Vector _traceStart = new(0.0f, 0.0f, 0.0f);
    private readonly Vector _traceEnd = new(0.0f, 0.0f, 0.0f);
    private readonly Vector _microHullMins = new(-MicroHullHalfExtent, -MicroHullHalfExtent, -MicroHullHalfExtent);
    private readonly Vector _microHullMaxs = new(MicroHullHalfExtent, MicroHullHalfExtent, MicroHullHalfExtent);
    private int _activeViewerSlot = -1;

    public LosEvaluator(CRayTraceInterface rayTrace, Action<int>? recordViewerTraceAttempt = null)
    {
        _rayTrace = rayTrace;
        _recordViewerTraceAttempt = recordViewerTraceAttempt;
    }

    /// <summary>
    /// Evaluates LOS for one viewer->target pair using only snapshot transforms and cached pawn references.
    /// </summary>
    internal VisibilityEval EvaluateLineOfSight(
        int viewerSlot,
        int targetSlot,
        bool viewerIsBot,
        int nowTick,
        S2AWHConfig config,
        PlayerTransformSnapshot[] transforms,
        CBasePlayerPawn?[] pawnsBySlot)
    {
        if ((uint)viewerSlot >= SlotCount || (uint)targetSlot >= SlotCount || viewerSlot == targetSlot)
        {
            return VisibilityEval.Visible;
        }

        ref var viewerSnapshot = ref transforms[viewerSlot];
        ref var targetSnapshot = ref transforms[targetSlot];
        if (!viewerSnapshot.IsValid || !targetSnapshot.IsValid)
        {
            return VisibilityEval.Visible;
        }

        CBasePlayerPawn? viewerPawn = pawnsBySlot[viewerSlot];
        CBasePlayerPawn? targetPawn = pawnsBySlot[targetSlot];
        if (viewerPawn == null || targetPawn == null)
        {
            return VisibilityEval.Visible;
        }

        bool hasAnyTraceAttempt = false;
        bool hasSuccessfulTraceCall = false;
        bool drawDebugBeams = VisibilityGeometry.ShouldDrawDebugTraceBeam(viewerIsBot);
        nint targetHandle = targetPawn.Handle;
        _activeViewerSlot = viewerSlot;

        SetVector(_traceStart, viewerSnapshot.EyeX, viewerSnapshot.EyeY, viewerSnapshot.EyeZ);
        if (VisibilityGeometry.ShouldDrawDebugAabbBox())
        {
            DrawLosDebugAabb(ref targetSnapshot, config);
        }

        if (TryNearestSurfaceProbeLos(
            viewerPawn,
            targetHandle,
            ref viewerSnapshot,
            ref targetSnapshot,
            config,
            drawDebugBeams,
            ref hasAnyTraceAttempt,
            ref hasSuccessfulTraceCall))
        {
            return VisibilityEval.Visible;
        }

        if (TryNearGroundSurfaceProbeLos(
            viewerPawn,
            targetHandle,
            ref viewerSnapshot,
            ref targetSnapshot,
            config,
            drawDebugBeams,
            ref hasAnyTraceAttempt,
            ref hasSuccessfulTraceCall))
        {
            return VisibilityEval.Visible;
        }

        if (TryMicroHullFallback(
            viewerPawn,
            targetHandle,
            ref viewerSnapshot,
            ref targetSnapshot,
            config,
            drawDebugBeams,
            ref hasAnyTraceAttempt,
            ref hasSuccessfulTraceCall))
        {
            return VisibilityEval.Visible;
        }

        if (TryAabbSurfaceProbeLos(
            viewerPawn,
            targetHandle,
            targetSlot,
            nowTick,
            ref targetSnapshot,
            config,
            drawDebugBeams,
            ref hasAnyTraceAttempt,
            ref hasSuccessfulTraceCall))
        {
            return VisibilityEval.Visible;
        }

        if (TryAimRayProximityFallback(
            viewerPawn,
            viewerSlot,
            targetSlot,
            nowTick,
            ref viewerSnapshot,
            ref targetSnapshot,
            config,
            drawDebugBeams,
            ref hasAnyTraceAttempt,
            ref hasSuccessfulTraceCall))
        {
            return VisibilityEval.Visible;
        }

        if (hasAnyTraceAttempt && !hasSuccessfulTraceCall)
        {
            return VisibilityEval.UnknownTransient;
        }

        return VisibilityEval.Hidden;
    }

    private void RecordActiveViewerTraceAttempt()
    {
        if (_activeViewerSlot >= 0)
        {
            _recordViewerTraceAttempt?.Invoke(_activeViewerSlot);
        }
    }

    private bool TryNearestSurfaceProbeLos(
        CBasePlayerPawn viewerPawn,
        nint targetHandle,
        ref PlayerTransformSnapshot viewerSnapshot,
        ref PlayerTransformSnapshot targetSnapshot,
        S2AWHConfig config,
        bool drawDebugBeams,
        ref bool hasAnyTraceAttempt,
        ref bool hasSuccessfulTraceCall)
    {
        GetExpandedWorldBounds(ref targetSnapshot, config, out float minX, out float minY, out float minZ, out float maxX, out float maxY, out float maxZ);
        AabbGeometry.GetClosestPointOnSurface(
            viewerSnapshot.EyeX,
            viewerSnapshot.EyeY,
            viewerSnapshot.EyeZ,
            minX,
            minY,
            minZ,
            maxX,
            maxY,
            maxZ,
            out float probeX,
            out float probeY,
            out float probeZ);

        SetVector(_traceEnd, probeX, probeY, probeZ);
        hasAnyTraceAttempt = true;
        RecordActiveViewerTraceAttempt();
        if (!_rayTrace.TraceEndShape(_traceStart, _traceEnd, viewerPawn, _cachedTraceOptions, out var result))
        {
            return false;
        }

        hasSuccessfulTraceCall = true;
        if (drawDebugBeams)
        {
            VisibilityGeometry.DrawDebugTraceBeam(_traceStart, _traceEnd, result, DebugTraceKind.LosSurface);
        }

        if (!result.DidHit || result.HitEntity == targetHandle)
        {
            return true;
        }

        float hitRadius = config.Aabb.LosSurfaceProbeHitRadius;
        if (hitRadius <= 0.0f)
        {
            return false;
        }

        float dx = probeX - result.EndPosX;
        float dy = probeY - result.EndPosY;
        float dz = probeZ - result.EndPosZ;
        float hitRadiusSq = hitRadius * hitRadius;
        return (dx * dx) + (dy * dy) + (dz * dz) <= hitRadiusSq;
    }

    private bool TryNearGroundSurfaceProbeLos(
        CBasePlayerPawn viewerPawn,
        nint targetHandle,
        ref PlayerTransformSnapshot viewerSnapshot,
        ref PlayerTransformSnapshot targetSnapshot,
        S2AWHConfig config,
        bool drawDebugBeams,
        ref bool hasAnyTraceAttempt,
        ref bool hasSuccessfulTraceCall)
    {
        float groundStartX = viewerSnapshot.OriginX;
        float groundStartY = viewerSnapshot.OriginY;
        float groundStartZ = viewerSnapshot.OriginZ + ViewerGroundProbeHeight;

        GetExpandedWorldBounds(ref targetSnapshot, config, out float minX, out float minY, out float minZ, out float maxX, out float maxY, out float maxZ);
        AabbGeometry.GetClosestPointOnSurface(
            groundStartX,
            groundStartY,
            groundStartZ,
            minX,
            minY,
            minZ,
            maxX,
            maxY,
            maxZ,
            out float probeX,
            out float probeY,
            out float probeZ);

        SetVector(_traceStart, groundStartX, groundStartY, groundStartZ);
        SetVector(_traceEnd, probeX, probeY, probeZ);
        hasAnyTraceAttempt = true;
        RecordActiveViewerTraceAttempt();
        if (!_rayTrace.TraceEndShape(_traceStart, _traceEnd, viewerPawn, _cachedTraceOptions, out var result))
        {
            SetVector(_traceStart, viewerSnapshot.EyeX, viewerSnapshot.EyeY, viewerSnapshot.EyeZ);
            return false;
        }

        hasSuccessfulTraceCall = true;
        if (drawDebugBeams)
        {
            VisibilityGeometry.DrawDebugTraceBeam(_traceStart, _traceEnd, result, DebugTraceKind.LosSurface);
        }

        SetVector(_traceStart, viewerSnapshot.EyeX, viewerSnapshot.EyeY, viewerSnapshot.EyeZ);

        if (!result.DidHit || result.HitEntity == targetHandle)
        {
            return true;
        }

        float hitRadius = config.Aabb.LosSurfaceProbeHitRadius;
        if (hitRadius <= 0.0f)
        {
            return false;
        }

        float dx = probeX - result.EndPosX;
        float dy = probeY - result.EndPosY;
        float dz = probeZ - result.EndPosZ;
        float hitRadiusSq = hitRadius * hitRadius;
        return (dx * dx) + (dy * dy) + (dz * dz) <= hitRadiusSq;
    }

    private bool TryAabbSurfaceProbeLos(
        CBasePlayerPawn viewerPawn,
        nint targetHandle,
        int targetSlot,
        int nowTick,
        ref PlayerTransformSnapshot targetSnapshot,
        S2AWHConfig config,
        bool drawDebugBeams,
        ref bool hasAnyTraceAttempt,
        ref bool hasSuccessfulTraceCall)
    {
        int pointCount = GetOrRefreshSurfaceProbeCache(targetSlot, nowTick, ref targetSnapshot, config, out Vector[] points);
        if (pointCount <= 0)
        {
            return false;
        }

        float hitRadiusSq = config.Aabb.LosSurfaceProbeHitRadius * config.Aabb.LosSurfaceProbeHitRadius;
        for (int i = 0; i < pointCount; i++)
        {
            Vector point = points[i];
            hasAnyTraceAttempt = true;
            RecordActiveViewerTraceAttempt();
            if (!_rayTrace.TraceEndShape(_traceStart, point, viewerPawn, _cachedTraceOptions, out var result))
            {
                continue;
            }

            hasSuccessfulTraceCall = true;
            if (drawDebugBeams)
            {
                VisibilityGeometry.DrawDebugTraceBeam(_traceStart, point, result, DebugTraceKind.LosSurface);
            }

            if (!result.DidHit || result.HitEntity == targetHandle)
            {
                return true;
            }

            if (hitRadiusSq > 0.0f)
            {
                float dx = point.X - result.EndPosX;
                float dy = point.Y - result.EndPosY;
                float dz = point.Z - result.EndPosZ;
                if ((dx * dx) + (dy * dy) + (dz * dz) <= hitRadiusSq)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryAimRayProximityFallback(
        CBasePlayerPawn viewerPawn,
        int viewerSlot,
        int targetSlot,
        int nowTick,
        ref PlayerTransformSnapshot viewerSnapshot,
        ref PlayerTransformSnapshot targetSnapshot,
        S2AWHConfig config,
        bool drawDebugBeams,
        ref bool hasAnyTraceAttempt,
        ref bool hasSuccessfulTraceCall)
    {
        float maxDistance = config.Trace.AimRayMaxDistance;
        if (maxDistance > 0.0f)
        {
            float targetCenterX = targetSnapshot.OriginX + targetSnapshot.CenterX;
            float targetCenterY = targetSnapshot.OriginY + targetSnapshot.CenterY;
            float targetCenterZ = targetSnapshot.OriginZ + targetSnapshot.CenterZ;
            float dx = targetCenterX - viewerSnapshot.EyeX;
            float dy = targetCenterY - viewerSnapshot.EyeY;
            float dz = targetCenterZ - viewerSnapshot.EyeZ;
            float distanceSq = (dx * dx) + (dy * dy) + (dz * dz);
            float maxDistanceSq = maxDistance * maxDistance;
            if (distanceSq > maxDistanceSq)
            {
                return false;
            }
        }

        EnsureAimRayCache(
            viewerSlot,
            nowTick,
            ref viewerSnapshot,
            viewerPawn,
            config,
            drawDebugBeams,
            out var aimCache);

        if (aimCache.AttemptedCount > 0)
        {
            hasAnyTraceAttempt = true;
        }

        if (aimCache.SuccessfulCount > 0)
        {
            hasSuccessfulTraceCall = true;
        }

        if (aimCache.HitCount <= 0)
        {
            return false;
        }

        float hitRadius = config.Trace.AimRayHitRadius;
        if (hitRadius <= 0.0f)
        {
            return false;
        }

        float hitRadiusSq = hitRadius * hitRadius;
        GetExpandedWorldBounds(ref targetSnapshot, config, out float minX, out float minY, out float minZ, out float maxX, out float maxY, out float maxZ);

        for (int i = 0; i < aimCache.HitCount; i++)
        {
            Vector hitPoint = aimCache.HitPoints[i];
            if (SegmentIntersectsAabb(
                viewerSnapshot.EyeX,
                viewerSnapshot.EyeY,
                viewerSnapshot.EyeZ,
                hitPoint.X,
                hitPoint.Y,
                hitPoint.Z,
                minX,
                minY,
                minZ,
                maxX,
                maxY,
                maxZ))
            {
                return true;
            }

            float closestX = Math.Clamp(hitPoint.X, minX, maxX);
            float closestY = Math.Clamp(hitPoint.Y, minY, maxY);
            float closestZ = Math.Clamp(hitPoint.Z, minZ, maxZ);
            float dx = hitPoint.X - closestX;
            float dy = hitPoint.Y - closestY;
            float dz = hitPoint.Z - closestZ;
            if ((dx * dx) + (dy * dy) + (dz * dz) <= hitRadiusSq)
            {
                return true;
            }
        }

        return false;
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
        float tMin = 0.0f;
        float tMax = 1.0f;
        float dirX = endX - startX;
        float dirY = endY - startY;
        float dirZ = endZ - startZ;

        return ClipSegmentAxis(startX, dirX, minX, maxX, ref tMin, ref tMax) &&
               ClipSegmentAxis(startY, dirY, minY, maxY, ref tMin, ref tMax) &&
               ClipSegmentAxis(startZ, dirZ, minZ, maxZ, ref tMin, ref tMax);
    }

    private static bool ClipSegmentAxis(
        float start,
        float direction,
        float min,
        float max,
        ref float tMin,
        ref float tMax)
    {
        const float epsilon = 0.0001f;
        if (MathF.Abs(direction) <= epsilon)
        {
            return start >= min && start <= max;
        }

        float inverseDirection = 1.0f / direction;
        float t1 = (min - start) * inverseDirection;
        float t2 = (max - start) * inverseDirection;
        if (t1 > t2)
        {
            (t1, t2) = (t2, t1);
        }

        if (t1 > tMin)
        {
            tMin = t1;
        }

        if (t2 < tMax)
        {
            tMax = t2;
        }

        return tMin <= tMax && tMax >= 0.0f && tMin <= 1.0f;
    }

    private bool TryMicroHullFallback(
        CBasePlayerPawn viewerPawn,
        nint targetHandle,
        ref PlayerTransformSnapshot viewerSnapshot,
        ref PlayerTransformSnapshot targetSnapshot,
        S2AWHConfig config,
        bool drawDebugBeams,
        ref bool hasAnyTraceAttempt,
        ref bool hasSuccessfulTraceCall)
    {
        float microHullMaxDistance = config.Aabb.MicroHullMaxDistance;
        if (microHullMaxDistance <= 0.0f)
        {
            return false;
        }

        float targetCenterX = targetSnapshot.OriginX + targetSnapshot.CenterX;
        float targetCenterY = targetSnapshot.OriginY + targetSnapshot.CenterY;
        float targetCenterZ = targetSnapshot.OriginZ + targetSnapshot.CenterZ;
        float dx = targetCenterX - viewerSnapshot.EyeX;
        float dy = targetCenterY - viewerSnapshot.EyeY;
        float dz = targetCenterZ - viewerSnapshot.EyeZ;
        float distanceSq = (dx * dx) + (dy * dy) + (dz * dz);
        float maxDistanceSq = microHullMaxDistance * microHullMaxDistance;
        if (distanceSq > maxDistanceSq)
        {
            return false;
        }

        GetExpandedWorldBounds(ref targetSnapshot, config, out float minX, out float minY, out float minZ, out float maxX, out float maxY, out float maxZ);
        AabbGeometry.GetClosestPointOnSurface(
            viewerSnapshot.EyeX,
            viewerSnapshot.EyeY,
            viewerSnapshot.EyeZ,
            minX,
            minY,
            minZ,
            maxX,
            maxY,
            maxZ,
            out float nearestX,
            out float nearestY,
            out float nearestZ);

        if (TryMicroHullTrace(viewerPawn, targetHandle, nearestX, nearestY, nearestZ, drawDebugBeams, ref hasAnyTraceAttempt, ref hasSuccessfulTraceCall))
        {
            return true;
        }

        if (TryMicroHullSlitBandFallback(viewerPawn, targetHandle, ref viewerSnapshot, minX, minY, minZ, maxX, maxY, maxZ, drawDebugBeams, ref hasAnyTraceAttempt, ref hasSuccessfulTraceCall))
        {
            return true;
        }

        if (TryMicroHullExtremityFallback(viewerPawn, targetHandle, ref viewerSnapshot, minX, minY, minZ, maxX, maxY, maxZ, drawDebugBeams, ref hasAnyTraceAttempt, ref hasSuccessfulTraceCall))
        {
            return true;
        }

        if (TryMicroHullCapExtremityFallback(viewerPawn, targetHandle, ref viewerSnapshot, minX, minY, minZ, maxX, maxY, maxZ, drawDebugBeams, ref hasAnyTraceAttempt, ref hasSuccessfulTraceCall))
        {
            return true;
        }

        if (TryMicroHullTrace(viewerPawn, targetHandle, targetCenterX, targetCenterY, targetCenterZ, drawDebugBeams, ref hasAnyTraceAttempt, ref hasSuccessfulTraceCall))
        {
            return true;
        }

        return TryMicroHullTrace(viewerPawn, targetHandle, targetSnapshot.EyeX, targetSnapshot.EyeY, targetSnapshot.EyeZ, drawDebugBeams, ref hasAnyTraceAttempt, ref hasSuccessfulTraceCall);
    }

    private bool TryMicroHullExtremityFallback(
        CBasePlayerPawn viewerPawn,
        nint targetHandle,
        ref PlayerTransformSnapshot viewerSnapshot,
        float minX,
        float minY,
        float minZ,
        float maxX,
        float maxY,
        float maxZ,
        bool drawDebugBeams,
        ref bool hasAnyTraceAttempt,
        ref bool hasSuccessfulTraceCall)
    {
        float centerX = (minX + maxX) * 0.5f;
        float centerY = (minY + maxY) * 0.5f;
        float centerZ = (minZ + maxZ) * 0.5f;
        float halfY = (maxY - minY) * 0.5f;
        float halfZ = (maxZ - minZ) * 0.5f;
        float halfX = (maxX - minX) * 0.5f;
        float deltaX = viewerSnapshot.EyeX - centerX;
        float deltaY = viewerSnapshot.EyeY - centerY;

        if (MathF.Abs(deltaX) >= MathF.Abs(deltaY))
        {
            float faceX = deltaX <= 0.0f ? minX : maxX;
            for (int i = 0; i < MicroHullFaceExtremityPattern.Length; i++)
            {
                float probeY = centerY + (halfY * MicroHullFaceExtremityPattern[i].LateralFactor);
                float probeZ = centerZ + (halfZ * MicroHullFaceExtremityPattern[i].VerticalFactor);
                if (TryMicroHullTrace(viewerPawn, targetHandle, faceX, probeY, probeZ, drawDebugBeams, ref hasAnyTraceAttempt, ref hasSuccessfulTraceCall))
                {
                    return true;
                }
            }

            return false;
        }

        float faceY = deltaY <= 0.0f ? minY : maxY;
        for (int i = 0; i < MicroHullFaceExtremityPattern.Length; i++)
        {
            float probeX = centerX + (halfX * MicroHullFaceExtremityPattern[i].LateralFactor);
            float probeZ = centerZ + (halfZ * MicroHullFaceExtremityPattern[i].VerticalFactor);
            if (TryMicroHullTrace(viewerPawn, targetHandle, probeX, faceY, probeZ, drawDebugBeams, ref hasAnyTraceAttempt, ref hasSuccessfulTraceCall))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryMicroHullSlitBandFallback(
        CBasePlayerPawn viewerPawn,
        nint targetHandle,
        ref PlayerTransformSnapshot viewerSnapshot,
        float minX,
        float minY,
        float minZ,
        float maxX,
        float maxY,
        float maxZ,
        bool drawDebugBeams,
        ref bool hasAnyTraceAttempt,
        ref bool hasSuccessfulTraceCall)
    {
        float centerX = (minX + maxX) * 0.5f;
        float centerY = (minY + maxY) * 0.5f;
        float centerZ = (minZ + maxZ) * 0.5f;
        float halfX = (maxX - minX) * 0.5f;
        float halfY = (maxY - minY) * 0.5f;
        float halfZ = (maxZ - minZ) * 0.5f;
        float deltaX = viewerSnapshot.EyeX - centerX;
        float deltaY = viewerSnapshot.EyeY - centerY;

        if (MathF.Abs(deltaX) >= MathF.Abs(deltaY))
        {
            float faceX = deltaX <= 0.0f ? minX : maxX;
            for (int bandIndex = 0; bandIndex < MicroHullSlitBandVerticalPattern.Length; bandIndex++)
            {
                float probeZ = centerZ + (halfZ * MicroHullSlitBandVerticalPattern[bandIndex]);
                for (int lateralIndex = 0; lateralIndex < MicroHullSlitBandLateralPattern.Length; lateralIndex++)
                {
                    float probeY = centerY + (halfY * MicroHullSlitBandLateralPattern[lateralIndex]);
                    if (TryMicroHullTrace(viewerPawn, targetHandle, faceX, probeY, probeZ, drawDebugBeams, ref hasAnyTraceAttempt, ref hasSuccessfulTraceCall))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        float faceY = deltaY <= 0.0f ? minY : maxY;
        for (int bandIndex = 0; bandIndex < MicroHullSlitBandVerticalPattern.Length; bandIndex++)
        {
            float probeZ = centerZ + (halfZ * MicroHullSlitBandVerticalPattern[bandIndex]);
            for (int lateralIndex = 0; lateralIndex < MicroHullSlitBandLateralPattern.Length; lateralIndex++)
            {
                float probeX = centerX + (halfX * MicroHullSlitBandLateralPattern[lateralIndex]);
                if (TryMicroHullTrace(viewerPawn, targetHandle, probeX, faceY, probeZ, drawDebugBeams, ref hasAnyTraceAttempt, ref hasSuccessfulTraceCall))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryMicroHullCapExtremityFallback(
        CBasePlayerPawn viewerPawn,
        nint targetHandle,
        ref PlayerTransformSnapshot viewerSnapshot,
        float minX,
        float minY,
        float minZ,
        float maxX,
        float maxY,
        float maxZ,
        bool drawDebugBeams,
        ref bool hasAnyTraceAttempt,
        ref bool hasSuccessfulTraceCall)
    {
        float centerX = (minX + maxX) * 0.5f;
        float centerY = (minY + maxY) * 0.5f;
        float centerZ = (minZ + maxZ) * 0.5f;
        float halfX = (maxX - minX) * 0.5f;
        float halfY = (maxY - minY) * 0.5f;
        float capZ = viewerSnapshot.EyeZ <= centerZ ? minZ : maxZ;

        for (int i = 0; i < MicroHullCapExtremityPattern.Length; i++)
        {
            float probeX = centerX + (halfX * MicroHullCapExtremityPattern[i].XFactor);
            float probeY = centerY + (halfY * MicroHullCapExtremityPattern[i].YFactor);
            if (TryMicroHullTrace(viewerPawn, targetHandle, probeX, probeY, capZ, drawDebugBeams, ref hasAnyTraceAttempt, ref hasSuccessfulTraceCall))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryMicroHullTrace(
        CBasePlayerPawn viewerPawn,
        nint targetHandle,
        float targetX,
        float targetY,
        float targetZ,
        bool drawDebugBeams,
        ref bool hasAnyTraceAttempt,
        ref bool hasSuccessfulTraceCall)
    {
        SetVector(_traceEnd, targetX, targetY, targetZ);
        hasAnyTraceAttempt = true;
        RecordActiveViewerTraceAttempt();
        if (!_rayTrace.TraceHullShape(_traceStart, _traceEnd, _microHullMins, _microHullMaxs, viewerPawn, _cachedTraceOptions, out var result))
        {
            return false;
        }

        hasSuccessfulTraceCall = true;
        if (drawDebugBeams)
        {
            VisibilityGeometry.DrawDebugTraceBeam(_traceStart, _traceEnd, result, DebugTraceKind.MicroHull);
        }

        return !result.DidHit || result.HitEntity == targetHandle;
    }

    private void EnsureAimRayCache(
        int viewerSlot,
        int nowTick,
        ref PlayerTransformSnapshot viewerSnapshot,
        CBasePlayerPawn viewerPawn,
        S2AWHConfig config,
        bool drawDebugBeams,
        out ViewerAimRayCacheEntry cache)
    {
        cache = _viewerAimCacheBySlot[viewerSlot] ??= new ViewerAimRayCacheEntry();
        if (cache.Tick == nowTick)
        {
            return;
        }

        cache.Tick = nowTick;
        cache.HitCount = 0;
        cache.AttemptedCount = 0;
        cache.SuccessfulCount = 0;

        int rayCount = Math.Clamp(config.Trace.AimRayCount, 1, MaxAimRayCount);
        float spreadDegrees = Math.Max(0.0f, config.Trace.AimRaySpreadDegrees);
        float rayDistance = config.Trace.AimRayMaxDistance > 0.0f
            ? Math.Max(MinAimRayDistance, config.Trace.AimRayMaxDistance)
            : DefaultAimRayDistance;
        float basePitch = viewerSnapshot.EyeAnglesPitch;
        float baseYaw = viewerSnapshot.EyeAnglesYaw;

        SetVector(_traceStart, viewerSnapshot.EyeX, viewerSnapshot.EyeY, viewerSnapshot.EyeZ);

        for (int i = 0; i < rayCount; i++)
        {
            float pitch = basePitch + (spreadDegrees * AimRayPattern[i].PitchFactor);
            float yaw = baseYaw + (spreadDegrees * AimRayPattern[i].YawFactor);
            float pitchRad = pitch * DegToRad;
            float yawRad = yaw * DegToRad;
            (float sinPitch, float cosPitch) = MathF.SinCos(pitchRad);
            (float sinYaw, float cosYaw) = MathF.SinCos(yawRad);

            float dirX = cosPitch * cosYaw;
            float dirY = cosPitch * sinYaw;
            float dirZ = -sinPitch;

            SetVector(
                _traceEnd,
                _traceStart.X + (dirX * rayDistance),
                _traceStart.Y + (dirY * rayDistance),
                _traceStart.Z + (dirZ * rayDistance));

            cache.AttemptedCount++;
            RecordActiveViewerTraceAttempt();
            if (!_rayTrace.TraceEndShape(_traceStart, _traceEnd, viewerPawn, _cachedTraceOptions, out var result))
            {
                continue;
            }

            cache.SuccessfulCount++;
            if (drawDebugBeams)
            {
                VisibilityGeometry.DrawDebugTraceBeam(_traceStart, _traceEnd, result, DebugTraceKind.AimRay);
            }

            Vector hitPoint = cache.HitPoints[cache.HitCount++];
            if (result.DidHit)
            {
                hitPoint.X = result.EndPosX;
                hitPoint.Y = result.EndPosY;
                hitPoint.Z = result.EndPosZ;
            }
            else
            {
                hitPoint.X = _traceEnd.X;
                hitPoint.Y = _traceEnd.Y;
                hitPoint.Z = _traceEnd.Z;
            }
        }
    }

    private int GetOrRefreshSurfaceProbeCache(
        int targetSlot,
        int nowTick,
        ref PlayerTransformSnapshot targetSnapshot,
        S2AWHConfig config,
        out Vector[] points)
    {
        var cache = _targetSurfaceCacheBySlot[targetSlot] ??= new TargetSurfaceCacheEntry();
        if (cache.Tick != nowTick)
        {
            cache.Tick = nowTick;
            cache.PointCount = FillSurfaceProbePoints(ref targetSnapshot, config, cache.SurfacePoints);
        }

        points = cache.SurfacePoints;
        return cache.PointCount;
    }

    private static int FillSurfaceProbePoints(
        ref PlayerTransformSnapshot targetSnapshot,
        S2AWHConfig config,
        Vector[] pointBuffer)
    {
        if (pointBuffer.Length < SurfaceProbePointCount)
        {
            return 0;
        }

        float centerX = targetSnapshot.CenterX;
        float centerY = targetSnapshot.CenterY;
        float centerZ = targetSnapshot.CenterZ;
        float halfX = (targetSnapshot.MaxsX - targetSnapshot.MinsX) * 0.5f * config.Aabb.LosHorizontalScale;
        float halfY = (targetSnapshot.MaxsY - targetSnapshot.MinsY) * 0.5f * config.Aabb.LosHorizontalScale;
        float halfZ = (targetSnapshot.MaxsZ - targetSnapshot.MinsZ) * 0.5f * config.Aabb.LosVerticalScale;

        return AabbGeometry.FillSurfaceProbePoints(
            pointBuffer,
            config.Aabb.LosSurfaceProbeRows,
            targetSnapshot.OriginX + centerX,
            targetSnapshot.OriginY + centerY,
            targetSnapshot.OriginZ + centerZ,
            halfX,
            halfY,
            halfZ);
    }

    private static void DrawLosDebugAabb(ref PlayerTransformSnapshot targetSnapshot, S2AWHConfig config)
    {
        GetExpandedWorldBounds(ref targetSnapshot, config, out float minX, out float minY, out float minZ, out float maxX, out float maxY, out float maxZ);
        VisibilityGeometry.DrawDebugAabbBox(
            minX,
            minY,
            minZ,
            maxX,
            maxY,
            maxZ,
            DebugAabbKind.Los);
    }

    private static void GetExpandedWorldBounds(
        ref PlayerTransformSnapshot targetSnapshot,
        S2AWHConfig config,
        out float minX,
        out float minY,
        out float minZ,
        out float maxX,
        out float maxY,
        out float maxZ)
    {
        float centerX = targetSnapshot.OriginX + targetSnapshot.CenterX;
        float centerY = targetSnapshot.OriginY + targetSnapshot.CenterY;
        float centerZ = targetSnapshot.OriginZ + targetSnapshot.CenterZ;
        float halfX = (targetSnapshot.MaxsX - targetSnapshot.MinsX) * 0.5f * config.Aabb.LosHorizontalScale;
        float halfY = (targetSnapshot.MaxsY - targetSnapshot.MinsY) * 0.5f * config.Aabb.LosHorizontalScale;
        float halfZ = (targetSnapshot.MaxsZ - targetSnapshot.MinsZ) * 0.5f * config.Aabb.LosVerticalScale;

        minX = centerX - halfX;
        minY = centerY - halfY;
        minZ = centerZ - halfZ;
        maxX = centerX + halfX;
        maxY = centerY + halfY;
        maxZ = centerZ + halfZ;
    }

    private static void SetVector(Vector vector, float x, float y, float z)
    {
        vector.X = x;
        vector.Y = y;
        vector.Z = z;
    }

    private static Vector[] CreatePointBuffer(int count)
    {
        Vector[] points = new Vector[count];
        for (int i = 0; i < count; i++)
        {
            points[i] = new Vector(0.0f, 0.0f, 0.0f);
        }

        return points;
    }

    /// <summary>
    /// Clears cached target surface data for one slot.
    /// </summary>
    internal void InvalidateTargetSlot(int targetSlot)
    {
        if ((uint)targetSlot < SlotCount)
        {
            _targetSurfaceCacheBySlot[targetSlot] = null;
            _viewerAimCacheBySlot[targetSlot] = null;
        }
    }

    /// <summary>
    /// Clears all LOS caches.
    /// </summary>
    internal void ClearCaches()
    {
        Array.Clear(_viewerAimCacheBySlot, 0, _viewerAimCacheBySlot.Length);
        Array.Clear(_targetSurfaceCacheBySlot, 0, _targetSurfaceCacheBySlot.Length);
    }
}
