using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using RayTraceAPI;

namespace S2AWH;

public class LosEvaluator
{
    private const float MicroHullExtent = 5.0f;
    private const float AimRayLength = 8192.0f;
    private const int AimRayCount = 5;
    private static readonly sbyte[] AimRayPitchOffsetSigns = {
        0,
        1, 1,
        -1, -1
    };
    private static readonly sbyte[] AimRayYawOffsetSigns = {
        0,
        1, -1,
        1, -1
    };
    // How close (in source units) the gap-sweep ray's hit point must be to the
    // target's center to count as "reached the target area". Covers full player AABB.
    // Configurable via Trace.GapSweepProximity (default 72.0, range 20-200).
    // Gap-sweep fan: 8 probes at cardinal (+/-6 deg ~= 0.10472 rad) + diagonal (+/-4.2 deg ~= 0.07330 rad).
    // Uses direction-to-target as center (crosshair-independent).
    // Index 0 (center, 0 offset) is skipped because it duplicates the AABB center trace.
    // +/-6 deg at 300 units covers +/-31 units of lateral sweep, well beyond the AABB grid.
    private const int GapSweepFanCount = 9;
    private const int GapSweepFanStart = 1; // skip index 0 = redundant center ray
    // Pre-computed literal values (6 deg = 0.10471976f, 4.2 deg = 0.07330383f)
    private static readonly float[] GapSweepPitchOffsets = {
        0.0f,
        0.10471976f, -0.10471976f, 0.0f, 0.0f,
        0.07330383f, 0.07330383f, -0.07330383f, -0.07330383f
    };
    private static readonly float[] GapSweepYawOffsets = {
        0.0f,
        0.0f, 0.0f, 0.10471976f, -0.10471976f,
        0.07330383f, -0.07330383f, 0.07330383f, -0.07330383f
    };
    // Precomputed sin/cos of each offset angle for angle-addition in the gap-sweep loop.
    // Avoids calling MathF.SinCos 8 times per target (saves ~5820 trig calls/tick for 30 players).
    private static readonly float[] GapSweepSinPitch = Array.ConvertAll(GapSweepPitchOffsets, MathF.Sin);
    private static readonly float[] GapSweepCosPitch = Array.ConvertAll(GapSweepPitchOffsets, MathF.Cos);
    private static readonly float[] GapSweepSinYaw = Array.ConvertAll(GapSweepYawOffsets, MathF.Sin);
    private static readonly float[] GapSweepCosYaw = Array.ConvertAll(GapSweepYawOffsets, MathF.Cos);

    private sealed class TargetPointCacheEntry
    {
        public int Tick = -1;
        public int PawnIndex = -1;
        public int PointCount;
        public Vector[] Points = VisibilityGeometry.CreatePointBuffer();
    }

    private readonly CRayTraceInterface _rayTrace;
    private readonly Vector _viewerEyeBuffer = new(0.0f, 0.0f, 0.0f);
    private readonly Vector _microHullMins = new(-MicroHullExtent, -MicroHullExtent, -MicroHullExtent);
    private readonly Vector _microHullMaxs = new(MicroHullExtent, MicroHullExtent, MicroHullExtent);
    private readonly Vector _viewProbeEnd = new(0.0f, 0.0f, 0.0f);
    private readonly TraceOptions _cachedTraceOptions = VisibilityGeometry.GetVisibilityTraceOptions();
    private readonly Vector[] _aimRayHitPoints = CreateAimRayPointBuffer();
    private readonly bool[] _aimRayHitValid = new bool[AimRayCount];
    private readonly Dictionary<int, TargetPointCacheEntry> _targetPointCacheBySlot = new(64);

    // Per-tick viewer eye cache: avoids redundant TryFillEyePosition calls for the same viewer
    // across N target evaluations. For 30 players, this eliminates ~840 native property reads/snapshot.
    private int _cachedViewerPawnIndex = -1;
    private int _cachedViewerTick = -1;
    private bool _cachedViewerIsBot;
    private bool _cachedDrawDebugBeams;
    private int _cachedAimViewerPawnIndex = -1;
    private int _cachedAimTick = -1;
    private bool _cachedAimHasHitPoint;

    public LosEvaluator(CRayTraceInterface rayTrace)
    {
        _rayTrace = rayTrace;
    }

    private static Vector[] CreateAimRayPointBuffer()
    {
        Vector[] buffer = new Vector[AimRayCount];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = new Vector(0.0f, 0.0f, 0.0f);
        }

        return buffer;
    }

    /// <summary>
    /// Checks if viewer can see target. Uses 10-point AABB bounds,
    /// a gap-sweep fan probe for narrow gaps, and a micro-hull fallback.
    /// </summary>
    internal VisibilityEval EvaluateLineOfSight(CCSPlayerController viewer, CCSPlayerController target, int nowTick)
    {
        var viewerPawn = viewer.PlayerPawn.Value;
        var targetPawn = target.PlayerPawn.Value;
        if (viewerPawn == null || targetPawn == null)
        {
            return VisibilityEval.UnknownTransient;
        }

        int viewerPawnIndex = (int)viewerPawn.Index;
        if (_cachedViewerPawnIndex != viewerPawnIndex || _cachedViewerTick != nowTick)
        {
            if (!VisibilityGeometry.TryFillEyePosition(viewerPawn, _viewerEyeBuffer))
            {
                return VisibilityEval.UnknownTransient;
            }
            _cachedViewerPawnIndex = viewerPawnIndex;
            _cachedViewerTick = nowTick;
            _cachedViewerIsBot = viewer.IsBot;
            _cachedDrawDebugBeams = VisibilityGeometry.ShouldDrawDebugTraceBeam(_cachedViewerIsBot);
        }

        if (!TryGetTargetPoints(target.Slot, targetPawn, nowTick, out var targetPoints, out int targetPointCount))
        {
            return VisibilityEval.UnknownTransient;
        }

        var options = _cachedTraceOptions;
        bool drawDebugBeams = _cachedDrawDebugBeams;
        bool viewerIsBot = _cachedViewerIsBot;
        var targetHandle = targetPawn.Handle;
        bool hasSuccessfulTrace = false;

        for (int i = 0; i < targetPointCount; i++)
        {
            var tPoint = targetPoints[i];
            if (_rayTrace.TraceEndShape(_viewerEyeBuffer, tPoint, viewerPawn, options, out var result))
            {
                hasSuccessfulTrace = true;
                if (drawDebugBeams)
                {
                    VisibilityGeometry.DrawDebugTraceBeam(_viewerEyeBuffer, tPoint, result, viewerIsBot);
                }

                // If the trace didn't hit anything, or it hit the target, line of sight is clear
                if (!result.DidHit || result.HitEntity == targetHandle)
                {
                    return VisibilityEval.Visible;
                }
            }
        }

        if (!hasSuccessfulTrace)
        {
            return VisibilityEval.UnknownTransient;
        }

        // SLAYER method (expanded): 5 aim rays (center + X pattern) per viewer tick,
        // then reveal targets near any hit point.
        float aimRayHitRadius = S2AWHState.Current.Trace.AimRayHitRadius;
        if (aimRayHitRadius > 0.0f &&
            TryCacheAimRayHitPoints(viewerPawn, nowTick, options, drawDebugBeams, viewerIsBot) &&
            IsTargetWithinRadiusOfAnyAimHitPoint(targetPawn, aimRayHitRadius))
        {
            return VisibilityEval.Visible;
        }

        // Gap-sweep probe: trace a fan of 9 rays centered on direction-to-target.
        // These rays extend past the target at +/-6 deg angular offsets, creating spatial lines
        // through different parts of the gap/wall geometry. Crosshair-independent.
        if (TryGapSweepProbe(viewerPawn, targetPawn, options, drawDebugBeams, viewerIsBot))
        {
            return VisibilityEval.Visible;
        }

        // Micro-hull fallback for thin-angle slits where point samples can miss.
        if (TryMicroHullFallback(viewerPawn, targetHandle, targetPoints, targetPointCount, options, drawDebugBeams, viewerIsBot))
        {
            return VisibilityEval.Visible;
        }

        return VisibilityEval.Hidden;
    }

    /// <summary>
    /// Sweeps a fan of 9 rays centered on the direction from viewer eye to target center.
    /// Unlike the AABB traces (which target fixed body points), these rays extend PAST the
    /// target at angular offsets (+/-6 deg), creating spatial lines through different parts of
    /// the gap/wall geometry. Crosshair-independent and works regardless of look direction.
    /// </summary>
    private bool TryGapSweepProbe(
        CCSPlayerPawn viewerPawn,
        CCSPlayerPawn targetPawn,
        TraceOptions options,
        bool drawDebugBeams,
        bool viewerIsBot)
    {
        var targetOrigin = targetPawn.AbsOrigin;
        if (targetOrigin == null)
        {
            return false;
        }

        // Estimate the target's vertical center (origin is at feet).
        float targetCenterZ = targetOrigin.Z;
        var targetCollision = targetPawn.Collision;
        if (targetCollision?.Mins != null && targetCollision?.Maxs != null)
        {
            targetCenterZ += (targetCollision.Mins.Z + targetCollision.Maxs.Z) * 0.5f;
        }
        else
        {
            targetCenterZ += 36.0f;
        }

        // Direction from viewer eye to target center.
        float toTargetX = targetOrigin.X - _viewerEyeBuffer.X;
        float toTargetY = targetOrigin.Y - _viewerEyeBuffer.Y;
        float toTargetZ = targetCenterZ - _viewerEyeBuffer.Z;
        float distSq = (toTargetX * toTargetX) + (toTargetY * toTargetY) + (toTargetZ * toTargetZ);
        if (distSq <= 1.0f)
        {
            return false;
        }

        float dist = MathF.Sqrt(distSq);
        float invDist = 1.0f / dist;

        // Derive pitch/yaw from the geometric direction to target (NOT from EyeAngles).
        // This makes the fan crosshair-independent.
        float dirX = toTargetX * invDist;
        float dirY = toTargetY * invDist;
        float dirZ = toTargetZ * invDist;

        float basePitchRad = -MathF.Asin(dirZ);
        float baseYawRad = MathF.Atan2(dirY, dirX);

        float traceLen = dist * 1.15f;
        float gapProximity = S2AWHState.Current.Trace.GapSweepProximity;
        float gapProximitySq = gapProximity * gapProximity;

        // Precompute base sin/cos once for angle-addition in the fan loop.
        (float baseSinPitch, float baseCosPitch) = MathF.SinCos(basePitchRad);
        (float baseSinYaw, float baseCosYaw) = MathF.SinCos(baseYawRad);

        // Fan pattern: 8 offset rays around the direction to target (index 0 skipped = AABB center duplicate).
        for (int fanIndex = GapSweepFanStart; fanIndex < GapSweepFanCount; fanIndex++)
        {
            // Angle-addition: sin(a+b) = sin(a)cos(b) + cos(a)sin(b)
            //                 cos(a+b) = cos(a)cos(b) - sin(a)sin(b)
            float sinPitch = baseSinPitch * GapSweepCosPitch[fanIndex] + baseCosPitch * GapSweepSinPitch[fanIndex];
            float cosPitch = baseCosPitch * GapSweepCosPitch[fanIndex] - baseSinPitch * GapSweepSinPitch[fanIndex];
            float sinYaw = baseSinYaw * GapSweepCosYaw[fanIndex] + baseCosYaw * GapSweepSinYaw[fanIndex];
            float cosYaw = baseCosYaw * GapSweepCosYaw[fanIndex] - baseSinYaw * GapSweepSinYaw[fanIndex];
            float fwdX = cosPitch * cosYaw;
            float fwdY = cosPitch * sinYaw;
            float fwdZ = -sinPitch;

            _viewProbeEnd.X = _viewerEyeBuffer.X + (fwdX * traceLen);
            _viewProbeEnd.Y = _viewerEyeBuffer.Y + (fwdY * traceLen);
            _viewProbeEnd.Z = _viewerEyeBuffer.Z + (fwdZ * traceLen);

            if (!_rayTrace.TraceEndShape(_viewerEyeBuffer, _viewProbeEnd, viewerPawn, options, out var result))
            {
                continue;
            }

            if (drawDebugBeams)
            {
                VisibilityGeometry.DrawDebugTraceBeam(_viewerEyeBuffer, _viewProbeEnd, result, viewerIsBot);
            }

            if (IsGapSweepHitNearTarget(result, traceLen, dist, _viewProbeEnd, targetOrigin.X, targetOrigin.Y, targetCenterZ, gapProximitySq))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryCacheAimRayHitPoints(
        CCSPlayerPawn viewerPawn,
        int nowTick,
        TraceOptions options,
        bool drawDebugBeams,
        bool viewerIsBot)
    {
        int viewerPawnIndex = (int)viewerPawn.Index;
        if (_cachedAimViewerPawnIndex != viewerPawnIndex || _cachedAimTick != nowTick)
        {
            _cachedAimViewerPawnIndex = viewerPawnIndex;
            _cachedAimTick = nowTick;
            _cachedAimHasHitPoint = false;
            Array.Clear(_aimRayHitValid, 0, _aimRayHitValid.Length);

            var eyeAngles = viewerPawn.EyeAngles;
            if (eyeAngles == null)
            {
                return false;
            }

            float basePitchRad = eyeAngles.X * (MathF.PI / 180.0f);
            float baseYawRad = eyeAngles.Y * (MathF.PI / 180.0f);
            float aimRaySpreadDegrees = Math.Clamp(S2AWHState.Current.Trace.AimRaySpreadDegrees, 0.0f, 5.0f);
            (float baseSinPitch, float baseCosPitch) = MathF.SinCos(basePitchRad);
            (float baseSinYaw, float baseCosYaw) = MathF.SinCos(baseYawRad);

            // Spread zero means all 5 rays collapse to one center ray.
            if (aimRaySpreadDegrees <= 0.0001f)
            {
                TraceAimRay(
                    viewerPawn,
                    options,
                    drawDebugBeams,
                    viewerIsBot,
                    baseSinPitch,
                    baseCosPitch,
                    baseSinYaw,
                    baseCosYaw,
                    rayIndex: 0);
            }
            else
            {
                float aimRaySpreadRad = aimRaySpreadDegrees * (MathF.PI / 180.0f);
                (float spreadSin, float spreadCos) = MathF.SinCos(aimRaySpreadRad);

                for (int i = 0; i < AimRayCount; i++)
                {
                    ApplySignedOffset(
                        baseSinPitch,
                        baseCosPitch,
                        spreadSin,
                        spreadCos,
                        AimRayPitchOffsetSigns[i],
                        out float sinPitch,
                        out float cosPitch);
                    ApplySignedOffset(
                        baseSinYaw,
                        baseCosYaw,
                        spreadSin,
                        spreadCos,
                        AimRayYawOffsetSigns[i],
                        out float sinYaw,
                        out float cosYaw);

                    TraceAimRay(
                        viewerPawn,
                        options,
                        drawDebugBeams,
                        viewerIsBot,
                        sinPitch,
                        cosPitch,
                        sinYaw,
                        cosYaw,
                        i);
                }
            }
        }

        return _cachedAimHasHitPoint;
    }

    private static void ApplySignedOffset(
        float baseSin,
        float baseCos,
        float spreadSin,
        float spreadCos,
        sbyte sign,
        out float resultSin,
        out float resultCos)
    {
        if (sign == 0)
        {
            resultSin = baseSin;
            resultCos = baseCos;
            return;
        }

        if (sign > 0)
        {
            resultSin = (baseSin * spreadCos) + (baseCos * spreadSin);
            resultCos = (baseCos * spreadCos) - (baseSin * spreadSin);
            return;
        }

        // a - b
        resultSin = (baseSin * spreadCos) - (baseCos * spreadSin);
        resultCos = (baseCos * spreadCos) + (baseSin * spreadSin);
    }

    private void TraceAimRay(
        CCSPlayerPawn viewerPawn,
        TraceOptions options,
        bool drawDebugBeams,
        bool viewerIsBot,
        float sinPitch,
        float cosPitch,
        float sinYaw,
        float cosYaw,
        int rayIndex)
    {
        float dirX = cosPitch * cosYaw;
        float dirY = cosPitch * sinYaw;
        float dirZ = -sinPitch;

        _viewProbeEnd.X = _viewerEyeBuffer.X + (dirX * AimRayLength);
        _viewProbeEnd.Y = _viewerEyeBuffer.Y + (dirY * AimRayLength);
        _viewProbeEnd.Z = _viewerEyeBuffer.Z + (dirZ * AimRayLength);

        if (!_rayTrace.TraceEndShape(_viewerEyeBuffer, _viewProbeEnd, viewerPawn, options, out var result))
        {
            return;
        }

        if (drawDebugBeams)
        {
            VisibilityGeometry.DrawDebugTraceBeam(_viewerEyeBuffer, _viewProbeEnd, result, viewerIsBot);
        }

        if (!result.DidHit)
        {
            return;
        }

        _aimRayHitPoints[rayIndex].X = result.EndPosX;
        _aimRayHitPoints[rayIndex].Y = result.EndPosY;
        _aimRayHitPoints[rayIndex].Z = result.EndPosZ;
        _aimRayHitValid[rayIndex] = true;
        _cachedAimHasHitPoint = true;
    }

    private bool IsTargetWithinRadiusOfAnyAimHitPoint(
        CCSPlayerPawn targetPawn,
        float radius)
    {
        var targetOrigin = targetPawn.AbsOrigin;
        if (targetOrigin == null)
        {
            return false;
        }

        float centerX = targetOrigin.X;
        float centerY = targetOrigin.Y;
        float centerZ = targetOrigin.Z;

        var targetCollision = targetPawn.Collision;
        if (targetCollision?.Mins != null && targetCollision?.Maxs != null)
        {
            centerX += (targetCollision.Mins.X + targetCollision.Maxs.X) * 0.5f;
            centerY += (targetCollision.Mins.Y + targetCollision.Maxs.Y) * 0.5f;
            centerZ += (targetCollision.Mins.Z + targetCollision.Maxs.Z) * 0.5f;
        }
        else
        {
            centerZ += 36.0f;
        }

        float radiusSq = radius * radius;
        for (int i = 0; i < AimRayCount; i++)
        {
            if (!_aimRayHitValid[i])
            {
                continue;
            }

            Vector hitPoint = _aimRayHitPoints[i];
            float dx = centerX - hitPoint.X;
            float dy = centerY - hitPoint.Y;
            float dz = centerZ - hitPoint.Z;
            float distanceSq = (dx * dx) + (dy * dy) + (dz * dz);
            if (distanceSq <= radiusSq)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGapSweepHitNearTarget(
        in TraceResult result,
        float traceLen,
        float distToTarget,
        Vector probeEnd,
        float targetX,
        float targetY,
        float targetCenterZ,
        float gapProximitySq)
    {
        // If the ray hit a wall before reaching the target's distance, verify proximity.
        if (result.DidHit)
        {
            float hitTravelDist = result.Fraction * traceLen;
            if (hitTravelDist >= distToTarget)
            {
                return true;
            }
        }

        // Check if the hit/end point is near the target's body.
        float endX = result.DidHit ? result.EndPosX : probeEnd.X;
        float endY = result.DidHit ? result.EndPosY : probeEnd.Y;
        float endZ = result.DidHit ? result.EndPosZ : probeEnd.Z;

        float hx = endX - targetX;
        float hy = endY - targetY;
        float hz = endZ - targetCenterZ;
        float hitToTargetDistSq = (hx * hx) + (hy * hy) + (hz * hz);
        return hitToTargetDistSq < gapProximitySq;
    }

    private bool TryMicroHullFallback(
        CCSPlayerPawn viewerPawn,
        nint targetHandle,
        Vector[] targetPoints,
        int targetPointCount,
        TraceOptions options,
        bool drawDebugBeams,
        bool viewerIsBot)
    {
        if (targetPointCount <= 0)
        {
            return false;
        }

        // 1. Center point probe (index 1 = AABB center)
        int centerPointIndex = targetPointCount > 1 ? 1 : 0;
        if (TryMicroHullProbe(viewerPawn, targetHandle, targetPoints[centerPointIndex], options, drawDebugBeams, viewerIsBot))
        {
            return true;
        }

        // 2. Eye point probe (index 0)
        if (centerPointIndex != 0 && TryMicroHullProbe(viewerPawn, targetHandle, targetPoints[0], options, drawDebugBeams, viewerIsBot))
        {
            return true;
        }

        // 3. Sample a subset of corner points to catch narrow diagonal slivers
        //    where thin-angle ray samples all missed but a small hull can still reach.
        int probeLimit = Math.Min(targetPointCount, 6);
        for (int i = 2; i < probeLimit; i++)
        {
            if (TryMicroHullProbe(viewerPawn, targetHandle, targetPoints[i], options, drawDebugBeams, viewerIsBot))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryMicroHullProbe(
        CCSPlayerPawn viewerPawn,
        nint targetHandle,
        Vector probeEnd,
        TraceOptions options,
        bool drawDebugBeams,
        bool viewerIsBot)
    {
        if (!_rayTrace.TraceHullShape(_viewerEyeBuffer, probeEnd, _microHullMins, _microHullMaxs, viewerPawn, options, out var hullResult))
        {
            return false;
        }

        if (drawDebugBeams)
        {
            VisibilityGeometry.DrawDebugTraceBeam(_viewerEyeBuffer, probeEnd, hullResult, viewerIsBot);
        }

        return !hullResult.DidHit || hullResult.HitEntity == targetHandle;
    }

    private bool TryGetTargetPoints(int targetSlot, CCSPlayerPawn targetPawn, int nowTick, out Vector[] targetPoints, out int pointCount)
    {
        targetPoints = Array.Empty<Vector>();
        pointCount = 0;

        if (!_targetPointCacheBySlot.TryGetValue(targetSlot, out var cacheEntry) || cacheEntry == null)
        {
            cacheEntry = new TargetPointCacheEntry();
            _targetPointCacheBySlot[targetSlot] = cacheEntry;
        }

        int pawnIndex = (int)targetPawn.Index;
        if (cacheEntry.Tick != nowTick || cacheEntry.PawnIndex != pawnIndex)
        {
            cacheEntry.PointCount = VisibilityGeometry.FillTargetPoints(targetPawn, cacheEntry.Points, null, false);
            cacheEntry.Tick = nowTick;
            cacheEntry.PawnIndex = pawnIndex;
        }

        if (cacheEntry.PointCount <= 0)
        {
            return false;
        }

        targetPoints = cacheEntry.Points;
        pointCount = cacheEntry.PointCount;
        return true;
    }

    /// <summary>
    /// Clears cached target-point data for the given slot.
    /// </summary>
    internal void InvalidateTargetSlot(int targetSlot)
    {
        _targetPointCacheBySlot.Remove(targetSlot);
    }

    /// <summary>
    /// Clears all LOS evaluation caches.
    /// </summary>
    internal void ClearCaches()
    {
        _targetPointCacheBySlot.Clear();
        _cachedViewerPawnIndex = -1;
        _cachedViewerTick = -1;
        _cachedAimViewerPawnIndex = -1;
        _cachedAimTick = -1;
        _cachedAimHasHitPoint = false;
        Array.Clear(_aimRayHitValid, 0, _aimRayHitValid.Length);
    }
}
