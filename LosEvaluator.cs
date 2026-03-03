using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using RayTraceAPI;

namespace S2AWH;

internal sealed class LosEvaluator
{
    private const int SlotCount = 65;
    private const int MaxAimRayCount = 5;
    private const int SurfaceProbePointCount = 18; // max: 3 probe rows * 6 faces
    private const int ViewerFacingFaceProbeGridSize = 3;
    private const float DefaultAimRayDistance = 4096.0f;
    private const float MinAimRayDistance = 256.0f;
    private const float DegToRad = MathF.PI / 180.0f;
    private const float MicroHullHalfExtent = 2.0f;
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
    private readonly TraceOptions _cachedTraceOptions = VisibilityGeometry.GetVisibilityTraceOptions();
    private readonly ViewerAimRayCacheEntry?[] _viewerAimCacheBySlot = new ViewerAimRayCacheEntry?[SlotCount];
    private readonly TargetSurfaceCacheEntry?[] _targetSurfaceCacheBySlot = new TargetSurfaceCacheEntry?[SlotCount];
    private readonly Vector _traceStart = new(0.0f, 0.0f, 0.0f);
    private readonly Vector _traceEnd = new(0.0f, 0.0f, 0.0f);
    private readonly Vector _microHullMins = new(-MicroHullHalfExtent, -MicroHullHalfExtent, -MicroHullHalfExtent);
    private readonly Vector _microHullMaxs = new(MicroHullHalfExtent, MicroHullHalfExtent, MicroHullHalfExtent);

    public LosEvaluator(CRayTraceInterface rayTrace)
    {
        _rayTrace = rayTrace;
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

        if (TryViewerFacingFaceProbeLos(
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

        if (hasAnyTraceAttempt && !hasSuccessfulTraceCall)
        {
            return VisibilityEval.UnknownTransient;
        }

        return VisibilityEval.Hidden;
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

    private bool TryViewerFacingFaceProbeLos(
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

        float centerX = (minX + maxX) * 0.5f;
        float centerY = (minY + maxY) * 0.5f;
        float centerZ = (minZ + maxZ) * 0.5f;

        float faceX = viewerSnapshot.EyeX <= centerX ? minX : maxX;
        float faceY = viewerSnapshot.EyeY <= centerY ? minY : maxY;
        float faceZ = viewerSnapshot.EyeZ <= centerZ ? minZ : maxZ;
        float hitRadiusSq = config.Aabb.LosSurfaceProbeHitRadius * config.Aabb.LosSurfaceProbeHitRadius;

        return TraceFaceGrid(viewerPawn, targetHandle, faceX, minY, maxY, minZ, maxZ, AabbFaceAxis.X, hitRadiusSq, drawDebugBeams, ref hasAnyTraceAttempt, ref hasSuccessfulTraceCall) ||
               TraceFaceGrid(viewerPawn, targetHandle, faceY, minX, maxX, minZ, maxZ, AabbFaceAxis.Y, hitRadiusSq, drawDebugBeams, ref hasAnyTraceAttempt, ref hasSuccessfulTraceCall) ||
               TraceFaceGrid(viewerPawn, targetHandle, faceZ, minX, maxX, minY, maxY, AabbFaceAxis.Z, hitRadiusSq, drawDebugBeams, ref hasAnyTraceAttempt, ref hasSuccessfulTraceCall);
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

    private bool TraceFaceGrid(
        CBasePlayerPawn viewerPawn,
        nint targetHandle,
        float fixedAxisValue,
        float range1Min,
        float range1Max,
        float range2Min,
        float range2Max,
        AabbFaceAxis faceAxis,
        float hitRadiusSq,
        bool drawDebugBeams,
        ref bool hasAnyTraceAttempt,
        ref bool hasSuccessfulTraceCall)
    {
        float step1 = ViewerFacingFaceProbeGridSize > 1 ? (range1Max - range1Min) / (ViewerFacingFaceProbeGridSize - 1) : 0.0f;
        float step2 = ViewerFacingFaceProbeGridSize > 1 ? (range2Max - range2Min) / (ViewerFacingFaceProbeGridSize - 1) : 0.0f;

        for (int i = 0; i < ViewerFacingFaceProbeGridSize; i++)
        {
            float axis1 = range1Min + (step1 * i);
            for (int j = 0; j < ViewerFacingFaceProbeGridSize; j++)
            {
                float axis2 = range2Min + (step2 * j);
                AabbGeometry.SetFacePoint(_traceEnd, fixedAxisValue, axis1, axis2, faceAxis);

                hasAnyTraceAttempt = true;
                if (!_rayTrace.TraceEndShape(_traceStart, _traceEnd, viewerPawn, _cachedTraceOptions, out var result))
                {
                    continue;
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

                if (hitRadiusSq > 0.0f)
                {
                    float dx = _traceEnd.X - result.EndPosX;
                    float dy = _traceEnd.Y - result.EndPosY;
                    float dz = _traceEnd.Z - result.EndPosZ;
                    if ((dx * dx) + (dy * dy) + (dz * dz) <= hitRadiusSq)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
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

        if (TryMicroHullTrace(viewerPawn, targetHandle, targetCenterX, targetCenterY, targetCenterZ, drawDebugBeams, ref hasAnyTraceAttempt, ref hasSuccessfulTraceCall))
        {
            return true;
        }

        return TryMicroHullTrace(viewerPawn, targetHandle, targetSnapshot.EyeX, targetSnapshot.EyeY, targetSnapshot.EyeZ, drawDebugBeams, ref hasAnyTraceAttempt, ref hasSuccessfulTraceCall);
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
