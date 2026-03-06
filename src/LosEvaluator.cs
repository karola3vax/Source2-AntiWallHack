using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using RayTraceAPI;

namespace S2AWH;

internal sealed class LosEvaluator
{
    private const int SlotCount = 65;
    private const int MaxAimRayCount = 5;
    private const int MaxAabbSurfaceProbeRays = 64; // hard LOS cap per target evaluation
    private const int DebugLosProbeDrawIntervalTicks = 4;
    private const float DefaultAimRayDistance = 4096.0f;
    private const float MinAimRayDistance = 256.0f;
    private const float DegToRad = MathF.PI / 180.0f;
    private const float LosProbeVerticalOffsetUnits = 1.0f;
    private const float LosDualFaceRatioThreshold = 1.35f;
    private static readonly float[] LosBodyGridRowZFactors =
    {
        0.97f,
        0.33f,
        -0.33f,
        -0.97f
    };
    private static readonly float[] LosBodyGridColumnFactors =
    {
        -0.97f,
        -0.33f,
        0.33f,
        0.97f
    };
    private static readonly float[] LosBodyDualFaceColumnFactors =
    {
        -0.90f,
        -0.30f,
        0.30f,
        0.90f
    };
    private static readonly (float PitchFactor, float YawFactor)[] AimRayPattern =
    {
        (0.0f, 0.0f),
        (0.0f, 1.0f),
        (0.0f, -1.0f),
        (1.0f, 0.0f),
        (-1.0f, 0.0f)
    };
    private sealed class ViewerAimRayCacheEntry
    {
        public int Tick = -1;
        public int HitCount;
        public int AttemptedCount;
        public int SuccessfulCount;
        public Vector[] HitPoints = CreatePointBuffer(MaxAimRayCount);
    }

    private enum SurfaceProbeTraceOutcome : byte
    {
        TraceFailed = 0,
        Visible = 1,
        Blocked = 2
    }

    private readonly CRayTraceInterface _rayTrace;
    private readonly Action<int, ViewerRayTraceStage>? _recordViewerTraceAttempt;
    private readonly TraceOptions _cachedTraceOptions = VisibilityGeometry.GetVisibilityTraceOptions();
    private readonly ViewerAimRayCacheEntry?[] _viewerAimCacheBySlot = new ViewerAimRayCacheEntry?[SlotCount];
    private readonly Vector[] _losProbePoints = CreatePointBuffer(MaxAabbSurfaceProbeRays);
    private readonly Vector _traceStart = new(0.0f, 0.0f, 0.0f);
    private readonly Vector _traceEnd = new(0.0f, 0.0f, 0.0f);
    private readonly int[] _losDebugProbeDrawTicks = new int[SlotCount * SlotCount];
    private int _activeViewerSlot = -1;

    public LosEvaluator(CRayTraceInterface rayTrace, Action<int, ViewerRayTraceStage>? recordViewerTraceAttempt = null)
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
        bool drawLosDebugAabb = VisibilityGeometry.ShouldDrawDebugAabbBox(DebugAabbKind.Los);
        nint targetHandle = targetPawn.Handle;
        _activeViewerSlot = viewerSlot;

        SetVector(_traceStart, viewerSnapshot.EyeX, viewerSnapshot.EyeY, viewerSnapshot.EyeZ);
        if (drawLosDebugAabb)
        {
            DrawLosDebugAabb(ref targetSnapshot, config);
        }

        if (TryAabbSurfaceProbeLos(
            viewerPawn,
            targetHandle,
            viewerSlot,
            targetSlot,
            nowTick,
            ref viewerSnapshot,
            ref targetSnapshot,
            config,
            drawDebugBeams,
            drawLosDebugAabb,
            out _,
            ref hasAnyTraceAttempt,
            ref hasSuccessfulTraceCall))
        {
            return VisibilityEval.Visible;
        }

        int remainingRayBudget = MaxAimRayCount;

        bool triedAimRayEarly = ShouldTryAimRayEarly(ref viewerSnapshot, ref targetSnapshot, config);
        if (triedAimRayEarly &&
            TryAimRayProximityFallback(
                viewerPawn,
                viewerSlot,
                nowTick,
                ref viewerSnapshot,
                ref targetSnapshot,
                config,
                remainingRayBudget,
                drawDebugBeams,
                ref hasAnyTraceAttempt,
                ref hasSuccessfulTraceCall))
        {
            return VisibilityEval.Visible;
        }

        if (!triedAimRayEarly &&
            TryAimRayProximityFallback(
                viewerPawn,
                viewerSlot,
                nowTick,
                ref viewerSnapshot,
                ref targetSnapshot,
                config,
                remainingRayBudget,
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

    private static bool ShouldTryAimRayEarly(
        ref PlayerTransformSnapshot viewerSnapshot,
        ref PlayerTransformSnapshot targetSnapshot,
        S2AWHConfig config)
    {
        float hitRadius = config.Trace.AimRayHitRadius;
        if (hitRadius <= 0.0f)
        {
            return false;
        }

        float centerX = targetSnapshot.OriginX + targetSnapshot.CenterX;
        float centerY = targetSnapshot.OriginY + targetSnapshot.CenterY;
        float centerZ = targetSnapshot.OriginZ + targetSnapshot.CenterZ;
        float toTargetX = centerX - viewerSnapshot.EyeX;
        float toTargetY = centerY - viewerSnapshot.EyeY;
        float toTargetZ = centerZ - viewerSnapshot.EyeZ;
        float distanceSq = (toTargetX * toTargetX) + (toTargetY * toTargetY) + (toTargetZ * toTargetZ);
        if (distanceSq <= 0.0001f)
        {
            return true;
        }

        float maxDistance = config.Trace.AimRayMaxDistance;
        if (maxDistance > 0.0f && distanceSq > (maxDistance * maxDistance))
        {
            return false;
        }

        float distance = MathF.Sqrt(distanceSq);
        float halfX = (targetSnapshot.MaxsX - targetSnapshot.MinsX) * 0.5f * config.Aabb.LosHorizontalScale;
        float halfY = (targetSnapshot.MaxsY - targetSnapshot.MinsY) * 0.5f * config.Aabb.LosHorizontalScale;
        float halfZ = (targetSnapshot.MaxsZ - targetSnapshot.MinsZ) * 0.5f * config.Aabb.LosVerticalScale;
        float effectiveRadius = MathF.Sqrt((halfX * halfX) + (halfY * halfY) + (halfZ * halfZ)) + hitRadius;
        float angularAllowance = MathF.Asin(Math.Min(1.0f, effectiveRadius / distance)) + (config.Trace.AimRaySpreadDegrees * DegToRad);
        float dotThreshold = MathF.Cos(MathF.Min(MathF.PI, angularAllowance));
        float inverseDistance = 1.0f / distance;
        float dirX = toTargetX * inverseDistance;
        float dirY = toTargetY * inverseDistance;
        float dirZ = toTargetZ * inverseDistance;
        float alignment =
            (dirX * viewerSnapshot.FovNormalX) +
            (dirY * viewerSnapshot.FovNormalY) +
            (dirZ * viewerSnapshot.FovNormalZ);
        return alignment >= dotThreshold;
    }

    private void RecordActiveViewerTraceAttempt(ViewerRayTraceStage stage)
    {
        if (_activeViewerSlot >= 0)
        {
            _recordViewerTraceAttempt?.Invoke(_activeViewerSlot, stage);
        }
    }

    private bool TryAabbSurfaceProbeLos(
        CBasePlayerPawn viewerPawn,
        nint targetHandle,
        int viewerSlot,
        int targetSlot,
        int nowTick,
        ref PlayerTransformSnapshot viewerSnapshot,
        ref PlayerTransformSnapshot targetSnapshot,
        S2AWHConfig config,
        bool drawDebugBeams,
        bool drawDebugLosProbePoints,
        out int attemptedLosRays,
        ref bool hasAnyTraceAttempt,
        ref bool hasSuccessfulTraceCall)
    {
        attemptedLosRays = 0;
        int pointCount = FillDirectedLosProbePoints(ref viewerSnapshot, ref targetSnapshot, config, _losProbePoints);
        if (pointCount <= 0)
        {
            return false;
        }

        bool drawProbePointsThisTick =
            drawDebugLosProbePoints &&
            ShouldDrawLosDebugProbePoints(viewerSlot, targetSlot, nowTick);
        if (drawProbePointsThisTick)
        {
            for (int i = 0; i < pointCount; i++)
            {
                Vector point = _losProbePoints[i];
                VisibilityGeometry.DrawDebugAabbProbePoint(point.X, point.Y, point.Z, DebugAabbKind.Los);
            }
        }

        float hitRadiusSq = config.Aabb.LosSurfaceProbeHitRadius * config.Aabb.LosSurfaceProbeHitRadius;
        for (int i = 0; i < pointCount; i++)
        {
            Vector point = _losProbePoints[i];
            attemptedLosRays++;
            SurfaceProbeTraceOutcome probeOutcome = TraceSurfaceProbePoint(
                viewerPawn,
                targetHandle,
                ref viewerSnapshot,
                point,
                hitRadiusSq,
                drawDebugBeams,
                ref hasAnyTraceAttempt,
                ref hasSuccessfulTraceCall);
            if (probeOutcome == SurfaceProbeTraceOutcome.Visible)
            {
                return true;
            }
        }

        return false;
    }

    private bool ShouldDrawLosDebugProbePoints(int viewerSlot, int targetSlot, int nowTick)
    {
        if ((uint)viewerSlot >= SlotCount || (uint)targetSlot >= SlotCount)
        {
            return false;
        }

        int pairIndex = (viewerSlot * SlotCount) + targetSlot;
        int lastDrawTick = _losDebugProbeDrawTicks[pairIndex];
        if (lastDrawTick > 0)
        {
            int ageTicks = nowTick - lastDrawTick;
            if (ageTicks >= 0 && ageTicks < DebugLosProbeDrawIntervalTicks)
            {
                return false;
            }
        }

        _losDebugProbeDrawTicks[pairIndex] = nowTick;
        return true;
    }

    private static int FillDirectedLosProbePoints(
        ref PlayerTransformSnapshot viewerSnapshot,
        ref PlayerTransformSnapshot targetSnapshot,
        S2AWHConfig config,
        Vector[] pointBuffer)
    {
        if (pointBuffer.Length < MaxAabbSurfaceProbeRays)
        {
            return 0;
        }

        GetExpandedWorldBounds(ref targetSnapshot, config, out float minX, out float minY, out float minZ, out float maxX, out float maxY, out float maxZ);
        float centerX = (minX + maxX) * 0.5f;
        float centerY = (minY + maxY) * 0.5f;
        float centerZ = (minZ + maxZ) * 0.5f;
        float halfX = (maxX - minX) * 0.5f;
        float halfY = (maxY - minY) * 0.5f;
        float halfZ = (maxZ - minZ) * 0.5f;
        float absDx = MathF.Abs(viewerSnapshot.EyeX - centerX);
        float absDy = MathF.Abs(viewerSnapshot.EyeY - centerY);
        float maxAxis = MathF.Max(absDx, absDy);
        float minAxis = MathF.Min(absDx, absDy);
        bool includeBothHorizontalFaces = minAxis > 0.0001f && (maxAxis / minAxis) <= LosDualFaceRatioThreshold;
        float faceX = viewerSnapshot.EyeX <= centerX ? minX : maxX;
        float faceY = viewerSnapshot.EyeY <= centerY ? minY : maxY;
        int pointIndex = 0;

        if (includeBothHorizontalFaces)
        {
            if (absDx >= absDy)
            {
                pointIndex = AppendXFaceProbePoints(pointBuffer, pointIndex, faceX, centerY, centerZ, halfY, halfZ, LosBodyDualFaceColumnFactors);
                pointIndex = AppendYFaceProbePoints(pointBuffer, pointIndex, faceY, centerX, centerZ, halfX, halfZ, LosBodyDualFaceColumnFactors);
            }
            else
            {
                pointIndex = AppendYFaceProbePoints(pointBuffer, pointIndex, faceY, centerX, centerZ, halfX, halfZ, LosBodyDualFaceColumnFactors);
                pointIndex = AppendXFaceProbePoints(pointBuffer, pointIndex, faceX, centerY, centerZ, halfY, halfZ, LosBodyDualFaceColumnFactors);
            }

            return pointIndex;
        }

        if (absDx >= absDy)
        {
            return AppendXFaceProbePoints(pointBuffer, pointIndex, faceX, centerY, centerZ, halfY, halfZ, LosBodyGridColumnFactors);
        }

        return AppendYFaceProbePoints(pointBuffer, pointIndex, faceY, centerX, centerZ, halfX, halfZ, LosBodyGridColumnFactors);
    }

    private static int AppendXFaceProbePoints(
        Vector[] pointBuffer,
        int pointIndex,
        float faceX,
        float centerY,
        float centerZ,
        float halfY,
        float halfZ,
        float[] columnFactors)
    {
        for (int row = 0; row < LosBodyGridRowZFactors.Length; row++)
        {
            float rowZ = centerZ + (halfZ * LosBodyGridRowZFactors[row]) + LosProbeVerticalOffsetUnits;
            for (int col = 0; col < columnFactors.Length; col++)
            {
                if (pointIndex >= pointBuffer.Length)
                {
                    return pointIndex;
                }

                float columnY = centerY + (halfY * columnFactors[col]);
                SetVector(pointBuffer[pointIndex++], faceX, columnY, rowZ);
            }
        }

        return pointIndex;
    }

    private static int AppendYFaceProbePoints(
        Vector[] pointBuffer,
        int pointIndex,
        float faceY,
        float centerX,
        float centerZ,
        float halfX,
        float halfZ,
        float[] columnFactors)
    {
        for (int row = 0; row < LosBodyGridRowZFactors.Length; row++)
        {
            float rowZ = centerZ + (halfZ * LosBodyGridRowZFactors[row]) + LosProbeVerticalOffsetUnits;
            for (int col = 0; col < columnFactors.Length; col++)
            {
                if (pointIndex >= pointBuffer.Length)
                {
                    return pointIndex;
                }

                float columnX = centerX + (halfX * columnFactors[col]);
                SetVector(pointBuffer[pointIndex++], columnX, faceY, rowZ);
            }
        }

        return pointIndex;
    }

    private SurfaceProbeTraceOutcome TraceSurfaceProbePoint(
        CBasePlayerPawn viewerPawn,
        nint targetHandle,
        ref PlayerTransformSnapshot viewerSnapshot,
        Vector point,
        float hitRadiusSq,
        bool drawDebugBeams,
        ref bool hasAnyTraceAttempt,
        ref bool hasSuccessfulTraceCall)
    {
        SetVector(_traceStart, viewerSnapshot.EyeX, viewerSnapshot.EyeY, viewerSnapshot.EyeZ);
        hasAnyTraceAttempt = true;
        RecordActiveViewerTraceAttempt(ViewerRayTraceStage.Los);
        if (!_rayTrace.TraceEndShape(_traceStart, point, viewerPawn, _cachedTraceOptions, out var result))
        {
            return SurfaceProbeTraceOutcome.TraceFailed;
        }

        hasSuccessfulTraceCall = true;
        if (drawDebugBeams)
        {
            VisibilityGeometry.DrawDebugTraceBeam(_traceStart, point, result, DebugTraceKind.LosSurface);
        }

        if (!result.DidHit || result.HitEntity == targetHandle)
        {
            return SurfaceProbeTraceOutcome.Visible;
        }

        if (hitRadiusSq <= 0.0f)
        {
            return SurfaceProbeTraceOutcome.Blocked;
        }

        float dx = point.X - result.EndPosX;
        float dy = point.Y - result.EndPosY;
        float dz = point.Z - result.EndPosZ;
        return (dx * dx) + (dy * dy) + (dz * dz) <= hitRadiusSq
            ? SurfaceProbeTraceOutcome.Visible
            : SurfaceProbeTraceOutcome.Blocked;
    }

    private bool TryAimRayProximityFallback(
        CBasePlayerPawn viewerPawn,
        int viewerSlot,
        int nowTick,
        ref PlayerTransformSnapshot viewerSnapshot,
        ref PlayerTransformSnapshot targetSnapshot,
        S2AWHConfig config,
        int maxAimRaysBudget,
        bool drawDebugBeams,
        ref bool hasAnyTraceAttempt,
        ref bool hasSuccessfulTraceCall)
    {
        if (maxAimRaysBudget <= 0)
        {
            return false;
        }

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
            maxAimRaysBudget,
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

    private void EnsureAimRayCache(
        int viewerSlot,
        int nowTick,
        ref PlayerTransformSnapshot viewerSnapshot,
        CBasePlayerPawn viewerPawn,
        S2AWHConfig config,
        int maxRayBudget,
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

        int rayCount = Math.Clamp(config.Trace.AimRayCount, 1, Math.Min(MaxAimRayCount, maxRayBudget));
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
            RecordActiveViewerTraceAttempt(ViewerRayTraceStage.Aim);
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
            _viewerAimCacheBySlot[targetSlot] = null;
        }
    }

    /// <summary>
    /// Clears all LOS caches.
    /// </summary>
    internal void ClearCaches()
    {
        Array.Clear(_viewerAimCacheBySlot, 0, _viewerAimCacheBySlot.Length);
        Array.Clear(_losDebugProbeDrawTicks, 0, _losDebugProbeDrawTicks.Length);
    }
}
