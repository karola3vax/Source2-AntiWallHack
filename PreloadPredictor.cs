using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using RayTraceAPI;

namespace S2AWH;

internal sealed class PreloadPredictor
{
    private const int SlotCount = 65;
    private const int DirectedPreloadProbePointCount = 32; // 4x4 per face, up to 2 horizontal faces
    private const float DirectedPreloadDualFaceRatioThreshold = 1.35f;
    private const float StandingViewOffsetZ = 64.0f;
    private const float DirectedPreloadVerticalOffsetUnits = 1.0f;
    private const float MinStandUpLeadUnits = 1.0f;
    private const float StandUpHeadroomMultiplier = 2.0f;
    private const float MinViewerHorizontalPreloadLeadUnits = 18.0f;
    private const float MaxPreloadTargetHeadroomUnits = 12.0f;
    private const float MinJumpLeadUnits = 4.0f;
    private const float JumpAssistHeadroomUnits = 32.0f;
    private const float MinJumpAssistLeadUnits = 24.0f;
    private const float MaxJumpAssistLeadUnits = 64.0f;
    private const float JumpAssistGravityUnitsPerSecondSq = 800.0f;
    private const float DefaultJumpImpulseUnitsPerSecond = 301.0f;
    private const float MaxJumpAssistHorizontalLeadUnits = 32.0f;
    private static readonly float[] DirectedPreloadRowFactors =
    {
        0.97f,
        0.33f,
        -0.33f,
        -0.97f
    };
    private static readonly float[] DirectedPreloadColumnFactors =
    {
        -0.97f,
        -0.33f,
        0.33f,
        0.97f
    };

    private sealed class PredictorTargetCacheEntry
    {
        public int Tick = -1;
        public bool HasTargetLookahead;
        public Vector PredictedOrigin = new(0.0f, 0.0f, 0.0f);
    }

    private readonly CRayTraceInterface _rayTrace;
    private readonly Action<int, ViewerRayTraceStage>? _recordViewerTraceAttempt;
    private readonly Vector _viewerEyeBuffer = new(0.0f, 0.0f, 0.0f);
    private readonly Vector _currentTargetOrigin = new(0.0f, 0.0f, 0.0f);
    private readonly Vector _traceStart = new(0.0f, 0.0f, 0.0f);
    private readonly Vector _predictedViewerEye = new(0.0f, 0.0f, 0.0f);
    private readonly Vector _predictedViewerJumpEye = new(0.0f, 0.0f, 0.0f);
    private readonly Vector _predictedViewerHighEye = new(0.0f, 0.0f, 0.0f);
    private readonly Vector _surfaceTraceEnd = new(0.0f, 0.0f, 0.0f);
    private readonly Vector[] _directedPreloadProbePoints = CreateDirectedPreloadProbeBuffer();

    private readonly TraceOptions _cachedTraceOptions = VisibilityGeometry.GetVisibilityTraceOptions();
    private readonly PredictorTargetCacheEntry?[] _targetCacheBySlot = new PredictorTargetCacheEntry?[SlotCount];

    private int _cachedViewerSlot = -1;
    private int _cachedViewerTick = -1;
    private bool _cachedDrawDebugBeams;
    private int _activeViewerSlot = -1;

    public PreloadPredictor(CRayTraceInterface rayTrace, Action<int, ViewerRayTraceStage>? recordViewerTraceAttempt = null)
    {
        _rayTrace = rayTrace;
        _recordViewerTraceAttempt = recordViewerTraceAttempt;
    }

    /// <summary>
    /// Predicts if target will be visible to viewer in the near future (to prevent pop-in).
    /// Uses directed preload surface probes with nearest-first and highest-point fallback.
    /// </summary>
    internal bool WillBeVisible(
        int viewerSlot,
        int targetSlot,
        bool viewerIsBot,
        int nowTick,
        PlayerTransformSnapshot[] transforms,
        CBasePlayerPawn?[] pawnsBySlot)
    {
        if ((uint)viewerSlot >= SlotCount || (uint)targetSlot >= SlotCount || viewerSlot == targetSlot)
        {
            return false;
        }

        var viewerPawn = pawnsBySlot[viewerSlot];
        var targetPawn = pawnsBySlot[targetSlot];
        if (viewerPawn == null || targetPawn == null)
        {
            return false;
        }

        ref var targetSnapshot = ref transforms[targetSlot];
        if (!targetSnapshot.IsValid)
        {
            return false;
        }

        ref var viewerSnapshot = ref transforms[viewerSlot];
        if (!viewerSnapshot.IsValid)
        {
            return false;
        }

        if (_cachedViewerSlot != viewerSlot || _cachedViewerTick != nowTick)
        {
            _viewerEyeBuffer.X = viewerSnapshot.EyeX;
            _viewerEyeBuffer.Y = viewerSnapshot.EyeY;
            _viewerEyeBuffer.Z = viewerSnapshot.EyeZ;
            _cachedViewerSlot = viewerSlot;
            _cachedViewerTick = nowTick;
            _cachedDrawDebugBeams = VisibilityGeometry.ShouldDrawDebugTraceBeam(viewerIsBot);
        }
        _activeViewerSlot = viewerSlot;

        var config = S2AWHState.Current;
        if (!config.Preload.EnablePreload)
        {
            return false;
        }

        _currentTargetOrigin.X = targetSnapshot.OriginX;
        _currentTargetOrigin.Y = targetSnapshot.OriginY;
        _currentTargetOrigin.Z = targetSnapshot.OriginZ;

        var targetCache = GetOrRefreshTargetCache(targetSlot, transforms, config, nowTick);
        float surfaceHitRadiusSq = config.Preload.SurfaceProbeHitRadius * config.Preload.SurfaceProbeHitRadius;
        float maxPreloadTargetPointZ = targetSnapshot.EyeZ + MaxPreloadTargetHeadroomUnits;
        bool drawDebugBeams = _cachedDrawDebugBeams;

        // Draw the current predictor AABB even when only the target-lookahead path runs.
        if (VisibilityGeometry.ShouldDrawDebugAabbBox(DebugAabbKind.PredictorCurrent) ||
            VisibilityGeometry.ShouldDrawDebugAabbBox(DebugAabbKind.PredictorPredicted))
        {
            DrawPredictorDebugAabb(ref targetSnapshot, _currentTargetOrigin, config, applyDirectionalShift: true, DebugAabbKind.PredictorCurrent);
        }

        // Preload uses up to two trace rays per evaluation (nearest + highest fallback).
        // Priority: peeker path (when viewer lookahead exists) -> holder path.
        if (config.Preload.EnabledForPeekers &&
            TryGetViewerPeekLookahead(
                ref viewerSnapshot,
                config,
                out float viewerLookaheadX,
                out float viewerLookaheadY,
                out float viewerLookaheadZ))
        {
            _predictedViewerEye.X = _viewerEyeBuffer.X + viewerLookaheadX;
            _predictedViewerEye.Y = _viewerEyeBuffer.Y + viewerLookaheadY;
            _predictedViewerEye.Z = _viewerEyeBuffer.Z + Math.Max(0.0f, viewerLookaheadZ);

            // Validate: the predicted eye must be reachable through world geometry.
            // If a wall blocks the path, clamp the predicted eye to just before the wall.
            ClampPredictedEyeToWorld(viewerPawn, _viewerEyeBuffer, _predictedViewerEye);

            Vector selectedEye = _predictedViewerEye;
            float highStandUpTargetEyeOffsetZ = GetViewerHighStandUpTargetEyeOffset(ref viewerSnapshot);
            float highStandUpTargetEyeZ = viewerSnapshot.OriginZ + highStandUpTargetEyeOffsetZ;
            if (highStandUpTargetEyeZ > _predictedViewerEye.Z)
            {
                _predictedViewerHighEye.X = _predictedViewerEye.X;
                _predictedViewerHighEye.Y = _predictedViewerEye.Y;
                _predictedViewerHighEye.Z = highStandUpTargetEyeZ;
                ClampPredictedEyeToWorld(viewerPawn, _viewerEyeBuffer, _predictedViewerHighEye);
                selectedEye = _predictedViewerHighEye;
            }

            return CanSeeNearestSurfaceProbe(
                viewerPawn,
                targetPawn,
                selectedEye,
                ref viewerSnapshot,
                ref targetSnapshot,
                _currentTargetOrigin,
                surfaceHitRadiusSq,
                maxPreloadTargetPointZ,
                DebugTraceKind.Preload,
                drawDebugBeams,
                config);
        }

        if (config.Preload.EnabledForHolders && targetCache.HasTargetLookahead)
        {
            return CanSeeNearestSurfaceProbe(
                viewerPawn,
                targetPawn,
                _viewerEyeBuffer,
                ref viewerSnapshot,
                ref targetSnapshot,
                targetCache.PredictedOrigin,
                surfaceHitRadiusSq,
                float.PositiveInfinity,
                DebugTraceKind.Preload,
                drawDebugBeams,
                config);
        }

        return false;
    }

    internal bool WillBeVisibleFromJumpPeek(
        int viewerSlot,
        int targetSlot,
        bool viewerIsBot,
        int nowTick,
        PlayerTransformSnapshot[] transforms,
        CBasePlayerPawn?[] pawnsBySlot)
    {
        if ((uint)viewerSlot >= SlotCount || (uint)targetSlot >= SlotCount || viewerSlot == targetSlot)
        {
            return false;
        }

        var viewerPawn = pawnsBySlot[viewerSlot];
        var targetPawn = pawnsBySlot[targetSlot];
        if (viewerPawn == null || targetPawn == null)
        {
            return false;
        }

        ref var targetSnapshot = ref transforms[targetSlot];
        ref var viewerSnapshot = ref transforms[viewerSlot];
        if (!targetSnapshot.IsValid || !viewerSnapshot.IsValid || !IsViewerInJumpAssistWindow(ref viewerSnapshot))
        {
            return false;
        }

        if (_cachedViewerSlot != viewerSlot || _cachedViewerTick != nowTick)
        {
            _viewerEyeBuffer.X = viewerSnapshot.EyeX;
            _viewerEyeBuffer.Y = viewerSnapshot.EyeY;
            _viewerEyeBuffer.Z = viewerSnapshot.EyeZ;
            _cachedViewerSlot = viewerSlot;
            _cachedViewerTick = nowTick;
            _cachedDrawDebugBeams = VisibilityGeometry.ShouldDrawDebugTraceBeam(viewerIsBot);
        }
        _activeViewerSlot = viewerSlot;

        var config = S2AWHState.Current;
        float jumpHitRadiusSq = config.Aabb.LosSurfaceProbeHitRadius * config.Aabb.LosSurfaceProbeHitRadius;
        bool drawDebugBeams = _cachedDrawDebugBeams;
        SetJumpAssistEyePosition(ref viewerSnapshot);

        // Validate: the predicted jump eye must be reachable through world geometry.
        ClampPredictedEyeToWorld(viewerPawn, _viewerEyeBuffer, _predictedViewerJumpEye);

        if (_predictedViewerJumpEye.Z <= viewerSnapshot.EyeZ)
        {
            return false;
        }

        return CanSeeJumpAssistSurfaceProbes(
            viewerPawn,
            targetPawn,
            _predictedViewerJumpEye,
            ref targetSnapshot,
            jumpHitRadiusSq,
            drawDebugBeams,
            config);
    }

    /// <summary>
    /// Traces from current eye to predicted eye. If a wall blocks the path,
    /// the predicted eye is clamped to just before the wall so preload traces
    /// never originate from inside world geometry.
    /// </summary>
    private void ClampPredictedEyeToWorld(CBasePlayerPawn viewerPawn, Vector currentEye, Vector predictedEye)
    {
        _surfaceTraceEnd.X = predictedEye.X;
        _surfaceTraceEnd.Y = predictedEye.Y;
        _surfaceTraceEnd.Z = predictedEye.Z;

        if (!_rayTrace.TraceEndShape(currentEye, _surfaceTraceEnd, viewerPawn, _cachedTraceOptions, out var result))
        {
            return; // Trace failed; keep predicted eye as-is (fail-open).
        }

        if (!result.DidHit)
        {
            return; // No wall between current and predicted — all clear.
        }

        // Pull back 2 units toward the viewer so the origin is safely in open space.
        const float pullbackUnits = 2.0f;
        float dx = currentEye.X - predictedEye.X;
        float dy = currentEye.Y - predictedEye.Y;
        float dz = currentEye.Z - predictedEye.Z;
        float distanceSq = (dx * dx) + (dy * dy) + (dz * dz);
        if (distanceSq > 0.0001f)
        {
            float inverseDistance = 1.0f / MathF.Sqrt(distanceSq);
            predictedEye.X = result.EndPosX + (dx * inverseDistance * pullbackUnits);
            predictedEye.Y = result.EndPosY + (dy * inverseDistance * pullbackUnits);
            predictedEye.Z = result.EndPosZ + (dz * inverseDistance * pullbackUnits);
        }
        else
        {
            predictedEye.X = currentEye.X;
            predictedEye.Y = currentEye.Y;
            predictedEye.Z = currentEye.Z;
        }
    }

    private void RecordActiveViewerTraceAttempt(ViewerRayTraceStage stage)
    {
        if (_activeViewerSlot >= 0)
        {
            _recordViewerTraceAttempt?.Invoke(_activeViewerSlot, stage);
        }
    }

    private bool CanSeeNearestSurfaceProbe(
        CBasePlayerPawn viewerPawn,
        CBasePlayerPawn targetPawn,
        Vector eyePosition,
        ref PlayerTransformSnapshot viewerSnapshot,
        ref PlayerTransformSnapshot targetSnapshot,
        Vector targetOrigin,
        float hitRadiusSq,
        float maxTargetPointZ,
        DebugTraceKind traceKind,
        bool drawDebugBeams,
        S2AWHConfig config)
    {
        // Preload probe candidates intentionally mirror LOS geometry.
        GetLosWorldBounds(ref targetSnapshot, targetOrigin, config, out float minX, out float minY, out float minZ, out float maxX, out float maxY, out float maxZ);
        int probeCount = FillDirectedPreloadProbePoints(
            eyePosition,
            minX,
            minY,
            minZ,
            maxX,
            maxY,
            maxZ,
            _directedPreloadProbePoints);
        if (probeCount <= 0)
        {
            return false;
        }

        int bestIndex = -1;
        float bestDistanceSq = float.MaxValue;
        int highestIndex = -1;
        float highestZ = float.MinValue;
        for (int i = 0; i < probeCount; i++)
        {
            Vector candidateProbe = _directedPreloadProbePoints[i];
            if (candidateProbe.Z > maxTargetPointZ)
            {
                continue;
            }

            float dx = candidateProbe.X - eyePosition.X;
            float dy = candidateProbe.Y - eyePosition.Y;
            float dz = candidateProbe.Z - eyePosition.Z;
            float distanceSq = (dx * dx) + (dy * dy) + (dz * dz);
            if (distanceSq < bestDistanceSq)
            {
                bestDistanceSq = distanceSq;
                bestIndex = i;
            }

            if (candidateProbe.Z > highestZ)
            {
                highestZ = candidateProbe.Z;
                highestIndex = i;
            }
        }

        if (bestIndex < 0)
        {
            return false;
        }

        // Try nearest probe first; if blocked, try highest (head area) as fallback.
        if (TraceSinglePreloadProbe(viewerPawn, targetPawn, eyePosition, bestIndex, hitRadiusSq, traceKind, drawDebugBeams))
        {
            return true;
        }

        if (highestIndex >= 0 && highestIndex != bestIndex)
        {
            return TraceSinglePreloadProbe(viewerPawn, targetPawn, eyePosition, highestIndex, hitRadiusSq, traceKind, drawDebugBeams);
        }

        return false;
    }

    private bool TraceSinglePreloadProbe(
        CBasePlayerPawn viewerPawn,
        CBasePlayerPawn targetPawn,
        Vector eyePosition,
        int probeIndex,
        float hitRadiusSq,
        DebugTraceKind traceKind,
        bool drawDebugBeams)
    {
        Vector probe = _directedPreloadProbePoints[probeIndex];
        float probeX = probe.X;
        float probeY = probe.Y;
        float probeZ = probe.Z;

        AabbGeometry.SetViewerOrigin(_traceStart, eyePosition);
        _surfaceTraceEnd.X = probeX;
        _surfaceTraceEnd.Y = probeY;
        _surfaceTraceEnd.Z = probeZ;

        RecordActiveViewerTraceAttempt(ViewerRayTraceStage.Preload);
        if (!_rayTrace.TraceEndShape(_traceStart, _surfaceTraceEnd, viewerPawn, _cachedTraceOptions, out var result))
        {
            return false;
        }

        if (drawDebugBeams)
        {
            VisibilityGeometry.DrawDebugTraceBeam(_traceStart, _surfaceTraceEnd, result, traceKind);
        }

        if (!result.DidHit || result.HitEntity == targetPawn.Handle)
        {
            return true;
        }

        return hitRadiusSq > 0.0f &&
               DistanceSquared(probeX, probeY, probeZ, result.EndPosX, result.EndPosY, result.EndPosZ) <= hitRadiusSq;
    }

    private static int FillDirectedPreloadProbePoints(
        Vector eyePosition,
        float minX,
        float minY,
        float minZ,
        float maxX,
        float maxY,
        float maxZ,
        Vector[] pointBuffer)
    {
        if (pointBuffer.Length < DirectedPreloadProbePointCount)
        {
            return 0;
        }

        float centerX = (minX + maxX) * 0.5f;
        float centerY = (minY + maxY) * 0.5f;
        float centerZ = (minZ + maxZ) * 0.5f;
        float halfX = (maxX - minX) * 0.5f;
        float halfY = (maxY - minY) * 0.5f;
        float halfZ = (maxZ - minZ) * 0.5f;
        float absDx = MathF.Abs(eyePosition.X - centerX);
        float absDy = MathF.Abs(eyePosition.Y - centerY);
        float maxAxis = MathF.Max(absDx, absDy);
        float minAxis = MathF.Min(absDx, absDy);
        bool includeBothHorizontalFaces = minAxis > 0.0001f && (maxAxis / minAxis) <= DirectedPreloadDualFaceRatioThreshold;
        float faceX = eyePosition.X <= centerX ? minX : maxX;
        float faceY = eyePosition.Y <= centerY ? minY : maxY;
        int pointIndex = 0;

        if (includeBothHorizontalFaces)
        {
            if (absDx >= absDy)
            {
                pointIndex = AppendDirectedXFaceProbePoints(pointBuffer, pointIndex, faceX, centerY, centerZ, halfY, halfZ, DirectedPreloadColumnFactors);
                pointIndex = AppendDirectedYFaceProbePoints(pointBuffer, pointIndex, faceY, centerX, centerZ, halfX, halfZ, DirectedPreloadColumnFactors);
            }
            else
            {
                pointIndex = AppendDirectedYFaceProbePoints(pointBuffer, pointIndex, faceY, centerX, centerZ, halfX, halfZ, DirectedPreloadColumnFactors);
                pointIndex = AppendDirectedXFaceProbePoints(pointBuffer, pointIndex, faceX, centerY, centerZ, halfY, halfZ, DirectedPreloadColumnFactors);
            }

            return pointIndex;
        }

        if (absDx >= absDy)
        {
            return AppendDirectedXFaceProbePoints(pointBuffer, pointIndex, faceX, centerY, centerZ, halfY, halfZ, DirectedPreloadColumnFactors);
        }

        return AppendDirectedYFaceProbePoints(pointBuffer, pointIndex, faceY, centerX, centerZ, halfX, halfZ, DirectedPreloadColumnFactors);
    }

    private static int AppendDirectedXFaceProbePoints(
        Vector[] pointBuffer,
        int pointIndex,
        float faceX,
        float centerY,
        float centerZ,
        float halfY,
        float halfZ,
        float[] columnFactors)
    {
        for (int row = 0; row < DirectedPreloadRowFactors.Length; row++)
        {
            float rowZ = centerZ + (halfZ * DirectedPreloadRowFactors[row]) + DirectedPreloadVerticalOffsetUnits;
            for (int col = 0; col < columnFactors.Length; col++)
            {
                if (pointIndex >= pointBuffer.Length)
                {
                    return pointIndex;
                }

                float columnY = centerY + (halfY * columnFactors[col]);
                SetPoint(pointBuffer, pointIndex++, faceX, columnY, rowZ);
            }
        }

        return pointIndex;
    }

    private static int AppendDirectedYFaceProbePoints(
        Vector[] pointBuffer,
        int pointIndex,
        float faceY,
        float centerX,
        float centerZ,
        float halfX,
        float halfZ,
        float[] columnFactors)
    {
        for (int row = 0; row < DirectedPreloadRowFactors.Length; row++)
        {
            float rowZ = centerZ + (halfZ * DirectedPreloadRowFactors[row]) + DirectedPreloadVerticalOffsetUnits;
            for (int col = 0; col < columnFactors.Length; col++)
            {
                if (pointIndex >= pointBuffer.Length)
                {
                    return pointIndex;
                }

                float columnX = centerX + (halfX * columnFactors[col]);
                SetPoint(pointBuffer, pointIndex++, columnX, faceY, rowZ);
            }
        }

        return pointIndex;
    }

    private PredictorTargetCacheEntry GetOrRefreshTargetCache(
        int targetSlot,
        PlayerTransformSnapshot[] transforms,
        S2AWHConfig config,
        int nowTick)
    {
        PredictorTargetCacheEntry? cache = _targetCacheBySlot[targetSlot];
        if (cache == null)
        {
            cache = new PredictorTargetCacheEntry();
            _targetCacheBySlot[targetSlot] = cache;
        }

        if (cache.Tick == nowTick)
        {
            return cache;
        }

        ref var targetSnapshot = ref transforms[targetSlot];

        cache.Tick = nowTick;
        cache.HasTargetLookahead = TryGetLookahead(
            targetSnapshot.VelocityX,
            targetSnapshot.VelocityY,
            targetSnapshot.VelocityZ,
            config.Preload.PredictorMinSpeed,
            config.Preload.PredictorFullSpeed,
            config.Preload.PredictorDistance,
            config.Core.UpdateFrequencyTicks,
            out float targetLookaheadX,
            out float targetLookaheadY,
            out float targetLookaheadZ);

        if (!cache.HasTargetLookahead)
        {
            return cache;
        }

        cache.PredictedOrigin.X = targetSnapshot.OriginX + targetLookaheadX;
        cache.PredictedOrigin.Y = targetSnapshot.OriginY + targetLookaheadY;
        cache.PredictedOrigin.Z = targetSnapshot.OriginZ + targetLookaheadZ;
        if (VisibilityGeometry.ShouldDrawDebugAabbBox(DebugAabbKind.PredictorPredicted))
        {
            DrawPredictorDebugAabb(
                ref targetSnapshot,
                cache.PredictedOrigin,
                config,
                applyDirectionalShift: false,
                DebugAabbKind.PredictorPredicted);
        }

        return cache;
    }

    private static void DrawPredictorDebugAabb(
        ref PlayerTransformSnapshot targetSnapshot,
        Vector targetOrigin,
        S2AWHConfig config,
        bool applyDirectionalShift,
        DebugAabbKind kind)
    {
        if (!VisibilityGeometry.ShouldDrawDebugAabbBox(kind))
        {
            return;
        }

        GetPredictorWorldBounds(
            ref targetSnapshot,
            targetOrigin,
            config,
            applyDirectionalShift,
            out float minX,
            out float minY,
            out float minZ,
            out float maxX,
            out float maxY,
            out float maxZ);
        VisibilityGeometry.DrawDebugAabbBox(minX, minY, minZ, maxX, maxY, maxZ, kind);
    }

    private static void GetPredictorWorldBounds(
        ref PlayerTransformSnapshot targetSnapshot,
        Vector targetOrigin,
        S2AWHConfig config,
        bool applyDirectionalShift,
        out float minX,
        out float minY,
        out float minZ,
        out float maxX,
        out float maxY,
        out float maxZ)
    {
        GetPredictorWorldBox(
            ref targetSnapshot,
            targetOrigin,
            config,
            applyDirectionalShift,
            out float centerX,
            out float centerY,
            out float centerZ,
            out float halfX,
            out float halfY,
            out float halfZ);

        minX = centerX - halfX;
        minY = centerY - halfY;
        minZ = centerZ - halfZ;
        maxX = centerX + halfX;
        maxY = centerY + halfY;
        maxZ = centerZ + halfZ;
    }

    private static void GetPredictorWorldBox(
        ref PlayerTransformSnapshot targetSnapshot,
        Vector targetOrigin,
        S2AWHConfig config,
        bool applyDirectionalShift,
        out float centerX,
        out float centerY,
        out float centerZ,
        out float halfX,
        out float halfY,
        out float halfZ)
    {
        float localCenterX = targetSnapshot.CenterX;
        float localCenterY = targetSnapshot.CenterY;
        float localCenterZ = targetSnapshot.CenterZ;
        float speed = GetSpeed(targetSnapshot.VelocityX, targetSnapshot.VelocityY, targetSnapshot.VelocityZ);
        float predictorScaleAlpha = GetPredictorScaleAlpha(speed, config);
        float horizontalScale = MathF.Max(
            config.Aabb.LosHorizontalScale,
            Lerp(config.Aabb.LosHorizontalScale, config.Aabb.PredictorHorizontalScale, predictorScaleAlpha));
        float verticalScale = MathF.Max(
            config.Aabb.LosVerticalScale,
            Lerp(config.Aabb.LosVerticalScale, config.Aabb.PredictorVerticalScale, predictorScaleAlpha));
        float alpha = GetProfileAlpha(speed, config);

        if (alpha > 0.0f)
        {
            horizontalScale *= Lerp(1.0f, config.Aabb.ProfileHorizontalMaxMultiplier, alpha);
            verticalScale *= Lerp(1.0f, config.Aabb.ProfileVerticalMaxMultiplier, alpha);
        }

        if (applyDirectionalShift &&
            config.Aabb.EnableDirectionalShift &&
            predictorScaleAlpha > 0.0f &&
            TryGetMovementDirection(ref targetSnapshot, out float movementDirX, out float movementDirY, out float movementDirZ))
        {
            float shiftUnits = config.Aabb.DirectionalForwardShiftMaxUnits * predictorScaleAlpha * config.Aabb.DirectionalPredictorShiftFactor;
            localCenterX += movementDirX * shiftUnits;
            localCenterY += movementDirY * shiftUnits;
            localCenterZ += movementDirZ * shiftUnits;
        }
        halfX = (targetSnapshot.MaxsX - targetSnapshot.MinsX) * 0.5f * horizontalScale;
        halfY = (targetSnapshot.MaxsY - targetSnapshot.MinsY) * 0.5f * horizontalScale;
        halfZ = (targetSnapshot.MaxsZ - targetSnapshot.MinsZ) * 0.5f * verticalScale;
        centerX = targetOrigin.X + localCenterX;
        centerY = targetOrigin.Y + localCenterY;
        centerZ = targetOrigin.Z + localCenterZ;
    }

    private static bool TryGetLookahead(
        float velX,
        float velY,
        float velZ,
        float minSpeed,
        float fullSpeedForMaxLookahead,
        float lookaheadDistance,
        int updateFrequencyTicks,
        out float lookaheadX,
        out float lookaheadY,
        out float lookaheadZ)
    {
        lookaheadX = 0.0f;
        lookaheadY = 0.0f;
        lookaheadZ = 0.0f;

        if (lookaheadDistance <= 0.0f)
        {
            return false;
        }

        float minSpeedSq = minSpeed * minSpeed;
        float speedSquared = (velX * velX) + (velY * velY) + (velZ * velZ);
        if (speedSquared <= 0.0001f || speedSquared < minSpeedSq)
        {
            return false;
        }

        float speed = MathF.Sqrt(speedSquared);
        float fullSpeed = MathF.Max(minSpeed + 1.0f, fullSpeedForMaxLookahead);
        float speedAlpha = Math.Clamp((speed - minSpeed) / (fullSpeed - minSpeed), 0.0f, 1.0f);
        float effectiveLookaheadDistance = lookaheadDistance * speedAlpha;
        float timeHorizonSeconds = MathF.Max(Server.TickInterval, Server.TickInterval * Math.Max(1, updateFrequencyTicks) * 1.25f);
        float physicallyReachableDistance = speed * timeHorizonSeconds;
        effectiveLookaheadDistance = MathF.Min(effectiveLookaheadDistance, physicallyReachableDistance);
        if (effectiveLookaheadDistance <= 0.001f)
        {
            return false;
        }

        float inverseSpeed = 1.0f / speed;
        lookaheadX = velX * inverseSpeed * effectiveLookaheadDistance;
        lookaheadY = velY * inverseSpeed * effectiveLookaheadDistance;
        lookaheadZ = velZ * inverseSpeed * effectiveLookaheadDistance;
        return true;
    }

    private static bool TryGetViewerPeekLookahead(
        ref PlayerTransformSnapshot viewerSnapshot,
        S2AWHConfig config,
        out float lookaheadX,
        out float lookaheadY,
        out float lookaheadZ)
    {
        float viewerPredictDistance = config.Preload.PredictorDistance * config.Preload.ViewerPredictorDistanceFactor;
        float upwardVelocity = MathF.Max(0.0f, viewerSnapshot.VelocityZ);
        bool hasVelocityLookahead = TryGetLookahead(
            viewerSnapshot.VelocityX,
            viewerSnapshot.VelocityY,
            upwardVelocity,
            config.Preload.PredictorMinSpeed,
            config.Preload.PredictorFullSpeed,
            viewerPredictDistance,
            config.Core.UpdateFrequencyTicks,
            out lookaheadX,
            out lookaheadY,
            out lookaheadZ);

        float horizontalSpeedSq =
            (viewerSnapshot.VelocityX * viewerSnapshot.VelocityX) +
            (viewerSnapshot.VelocityY * viewerSnapshot.VelocityY);
        float minHorizontalSpeed = Math.Max(1.0f, config.Preload.PredictorMinSpeed);
        if (horizontalSpeedSq >= (minHorizontalSpeed * minHorizontalSpeed))
        {
            float horizontalSpeed = MathF.Sqrt(horizontalSpeedSq);
            float inverseHorizontalSpeed = 1.0f / horizontalSpeed;
            float currentHorizontalLead = MathF.Sqrt((lookaheadX * lookaheadX) + (lookaheadY * lookaheadY));
            float desiredMinimumLead = MathF.Min(
                viewerPredictDistance,
                MathF.Max(MinViewerHorizontalPreloadLeadUnits, viewerPredictDistance * 0.35f));

            if (currentHorizontalLead < desiredMinimumLead)
            {
                // If we boost the horizontal lead, we must proportionally boost the vertical lead
                // so the trajectory angle is maintained (e.g. when slowly climbing stairs).
                float boostRatio = currentHorizontalLead > 0.001f ? desiredMinimumLead / currentHorizontalLead : 0.0f;
                
                lookaheadX = viewerSnapshot.VelocityX * inverseHorizontalSpeed * desiredMinimumLead;
                lookaheadY = viewerSnapshot.VelocityY * inverseHorizontalSpeed * desiredMinimumLead;
                
                if (boostRatio > 0.0f)
                {
                    lookaheadZ *= boostRatio;
                }
                
                hasVelocityLookahead = true;
            }
        }

        float standUpLead = GetViewerStandUpLead(ref viewerSnapshot);
        if (standUpLead > 0.0f)
        {
            lookaheadZ = MathF.Max(lookaheadZ, standUpLead);
            return true;
        }

        return hasVelocityLookahead;
    }

    private static float GetViewerStandUpLead(ref PlayerTransformSnapshot viewerSnapshot)
    {
        if (!IsViewerStandingUp(ref viewerSnapshot))
        {
            return 0.0f;
        }

        float remainingViewOffsetRise = StandingViewOffsetZ - viewerSnapshot.ViewOffsetZ;
        if (remainingViewOffsetRise <= MinStandUpLeadUnits)
        {
            return 0.0f;
        }

        return remainingViewOffsetRise * StandUpHeadroomMultiplier;
    }

    private static float GetViewerHighStandUpTargetEyeOffset(ref PlayerTransformSnapshot viewerSnapshot)
    {
        if (!IsViewerStandingUp(ref viewerSnapshot))
        {
            return 0.0f;
        }

        return MathF.Max(StandingViewOffsetZ, viewerSnapshot.ViewOffsetZ * 2.0f);
    }

    private static float GetViewerJumpTargetEyeZ(ref PlayerTransformSnapshot viewerSnapshot)
    {
        if (!IsViewerInJumpAssistWindow(ref viewerSnapshot))
        {
            return 0.0f;
        }

        float remainingJumpRise = GetEstimatedRemainingJumpRise(ref viewerSnapshot);

        float jumpRise = Math.Clamp(remainingJumpRise + JumpAssistHeadroomUnits, MinJumpAssistLeadUnits, MaxJumpAssistLeadUnits);
        if (jumpRise <= 0.0f)
        {
            return 0.0f;
        }

        return viewerSnapshot.EyeZ + jumpRise;
    }

    private static bool IsViewerInJumpAssistWindow(ref PlayerTransformSnapshot viewerSnapshot)
    {
        if (viewerSnapshot.IsGrounded)
        {
            return false;
        }

        return viewerSnapshot.JumpApexPending ||
               viewerSnapshot.VelocityZ > 0.0f ||
               viewerSnapshot.MaxJumpHeightThisJump > (viewerSnapshot.HeightAtJumpStart + MinJumpLeadUnits);
    }

    private static bool IsViewerStandingUp(ref PlayerTransformSnapshot viewerSnapshot)
    {
        bool isCrouchedOrTransitioning =
            viewerSnapshot.IsDucked ||
            viewerSnapshot.IsDucking ||
            viewerSnapshot.DuckAmount > 0.01f ||
            viewerSnapshot.ViewOffsetZ < (StandingViewOffsetZ - MinStandUpLeadUnits);

        return isCrouchedOrTransitioning && viewerSnapshot.DuckReleasedThisTick;
    }

    private void SetJumpAssistEyePosition(ref PlayerTransformSnapshot viewerSnapshot)
    {
        _predictedViewerJumpEye.X = viewerSnapshot.EyeX;
        _predictedViewerJumpEye.Y = viewerSnapshot.EyeY;
        _predictedViewerJumpEye.Z = GetViewerJumpTargetEyeZ(ref viewerSnapshot);

        float upwardVelocity = GetJumpAssistUpwardVelocity(ref viewerSnapshot);
        if (upwardVelocity <= 0.0f)
        {
            return;
        }

        float timeToApexSeconds = upwardVelocity / JumpAssistGravityUnitsPerSecondSq;
        if (timeToApexSeconds <= 0.0f)
        {
            return;
        }

        float horizontalSpeedSq =
            (viewerSnapshot.VelocityX * viewerSnapshot.VelocityX) +
            (viewerSnapshot.VelocityY * viewerSnapshot.VelocityY);
        if (horizontalSpeedSq <= 0.0001f)
        {
            return;
        }

        float horizontalSpeed = MathF.Sqrt(horizontalSpeedSq);
        float leadDistance = MathF.Min(MaxJumpAssistHorizontalLeadUnits, horizontalSpeed * timeToApexSeconds);
        if (leadDistance <= 0.001f)
        {
            return;
        }

        float inverseSpeed = 1.0f / horizontalSpeed;
        _predictedViewerJumpEye.X += viewerSnapshot.VelocityX * inverseSpeed * leadDistance;
        _predictedViewerJumpEye.Y += viewerSnapshot.VelocityY * inverseSpeed * leadDistance;
    }

    private static float GetEstimatedRemainingJumpRise(ref PlayerTransformSnapshot viewerSnapshot)
    {
        float predictedByMovementService = viewerSnapshot.MaxJumpHeightThisJump - viewerSnapshot.OriginZ;
        float upwardVelocity = GetJumpAssistUpwardVelocity(ref viewerSnapshot);
        float predictedByBallistics = upwardVelocity > 0.0f
            ? (upwardVelocity * upwardVelocity) / (2.0f * JumpAssistGravityUnitsPerSecondSq)
            : 0.0f;

        return Math.Max(predictedByMovementService, predictedByBallistics);
    }

    private static float GetJumpAssistUpwardVelocity(ref PlayerTransformSnapshot viewerSnapshot)
    {
        if (viewerSnapshot.VelocityZ > 0.0f)
        {
            return viewerSnapshot.VelocityZ;
        }

        if (!viewerSnapshot.IsGrounded && viewerSnapshot.OnGroundLastTick)
        {
            return DefaultJumpImpulseUnitsPerSecond;
        }

        return 0.0f;
    }



    private bool CanSeeJumpAssistSurfaceProbes(
        CBasePlayerPawn viewerPawn,
        CBasePlayerPawn targetPawn,
        Vector eyePosition,
        ref PlayerTransformSnapshot targetSnapshot,
        float hitRadiusSq,
        bool drawDebugBeams,
        S2AWHConfig config)
    {
        GetJumpAssistWorldBounds(ref targetSnapshot, config, out float minX, out float minY, out float minZ, out float maxX, out float maxY, out float maxZ);
        int probeCount = FillDirectedPreloadProbePoints(
            eyePosition,
            minX,
            minY,
            minZ,
            maxX,
            maxY,
            maxZ,
            _directedPreloadProbePoints);
        if (probeCount <= 0)
        {
            return false;
        }

        int bestIndex = -1;
        float bestDistanceSq = float.MaxValue;
        int highestIndex = -1;
        float highestZ = float.MinValue;
        for (int i = 0; i < probeCount; i++)
        {
            Vector probe = _directedPreloadProbePoints[i];
            float dx = probe.X - eyePosition.X;
            float dy = probe.Y - eyePosition.Y;
            float dz = probe.Z - eyePosition.Z;
            float distanceSq = (dx * dx) + (dy * dy) + (dz * dz);
            
            if (distanceSq < bestDistanceSq)
            {
                bestDistanceSq = distanceSq;
                bestIndex = i;
            }

            if (probe.Z > highestZ)
            {
                highestZ = probe.Z;
                highestIndex = i;
            }
        }

        if (bestIndex < 0)
        {
            return false;
        }

        // Try nearest probe first; if blocked, try highest (head area) as fallback.
        if (TraceSingleJumpAssistProbe(viewerPawn, targetPawn, eyePosition, bestIndex, hitRadiusSq, drawDebugBeams))
        {
            return true;
        }

        if (highestIndex >= 0 && highestIndex != bestIndex)
        {
            return TraceSingleJumpAssistProbe(viewerPawn, targetPawn, eyePosition, highestIndex, hitRadiusSq, drawDebugBeams);
        }

        return false;
    }

    private bool TraceSingleJumpAssistProbe(
        CBasePlayerPawn viewerPawn,
        CBasePlayerPawn targetPawn,
        Vector eyePosition,
        int probeIndex,
        float hitRadiusSq,
        bool drawDebugBeams)
    {
        Vector probe = _directedPreloadProbePoints[probeIndex];
        float probeX = probe.X;
        float probeY = probe.Y;
        float probeZ = probe.Z;

        AabbGeometry.SetViewerOrigin(_traceStart, eyePosition);
        _surfaceTraceEnd.X = probeX;
        _surfaceTraceEnd.Y = probeY;
        _surfaceTraceEnd.Z = probeZ;

        RecordActiveViewerTraceAttempt(ViewerRayTraceStage.Jump);
        if (!_rayTrace.TraceEndShape(_traceStart, _surfaceTraceEnd, viewerPawn, _cachedTraceOptions, out var result))
        {
            return false;
        }

        if (drawDebugBeams)
        {
            VisibilityGeometry.DrawDebugTraceBeam(_traceStart, _surfaceTraceEnd, result, DebugTraceKind.JumpAssist);
        }

        if (!result.DidHit || result.HitEntity == targetPawn.Handle)
        {
            return true;
        }

        return hitRadiusSq > 0.0f &&
               DistanceSquared(probeX, probeY, probeZ, result.EndPosX, result.EndPosY, result.EndPosZ) <= hitRadiusSq;
    }

    private static void GetJumpAssistWorldBounds(
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

    private static void GetLosWorldBounds(
        ref PlayerTransformSnapshot targetSnapshot,
        Vector targetOrigin,
        S2AWHConfig config,
        out float minX,
        out float minY,
        out float minZ,
        out float maxX,
        out float maxY,
        out float maxZ)
    {
        float centerX = targetOrigin.X + targetSnapshot.CenterX;
        float centerY = targetOrigin.Y + targetSnapshot.CenterY;
        float centerZ = targetOrigin.Z + targetSnapshot.CenterZ;
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

    private static float GetSpeed(float velX, float velY, float velZ)
    {
        return MathF.Sqrt((velX * velX) + (velY * velY) + (velZ * velZ));
    }

    private static float GetProfileAlpha(float speed, S2AWHConfig config)
    {
        if (!config.Aabb.EnableAdaptiveProfile)
        {
            return 0.0f;
        }

        float start = Math.Max(0.0f, config.Aabb.ProfileSpeedStart);
        float full = Math.Max(start + 1.0f, config.Aabb.ProfileSpeedFull);

        if (speed <= start)
        {
            return 0.0f;
        }

        if (speed >= full)
        {
            return 1.0f;
        }

        return (speed - start) / (full - start);
    }

    private static float GetPredictorScaleAlpha(float speed, S2AWHConfig config)
    {
        float start = Math.Max(0.0f, config.Aabb.PredictorScaleStartSpeed);
        float full = Math.Max(start + 1.0f, config.Aabb.PredictorScaleFullSpeed);

        if (speed <= start)
        {
            return 0.0f;
        }

        if (speed >= full)
        {
            return 1.0f;
        }

        return (speed - start) / (full - start);
    }

    private static bool TryGetMovementDirection(
        ref PlayerTransformSnapshot targetSnapshot,
        out float directionX,
        out float directionY,
        out float directionZ)
    {
        directionX = 0.0f;
        directionY = 0.0f;
        directionZ = 0.0f;

        float horizontalLengthSquared = (targetSnapshot.VelocityX * targetSnapshot.VelocityX) + (targetSnapshot.VelocityY * targetSnapshot.VelocityY);
        if (horizontalLengthSquared > 0.0001f)
        {
            float invLength = 1.0f / MathF.Sqrt(horizontalLengthSquared);
            directionX = targetSnapshot.VelocityX * invLength;
            directionY = targetSnapshot.VelocityY * invLength;
            return true;
        }

        float fullLengthSquared = horizontalLengthSquared + (targetSnapshot.VelocityZ * targetSnapshot.VelocityZ);
        if (fullLengthSquared > 0.0001f)
        {
            float invLength = 1.0f / MathF.Sqrt(fullLengthSquared);
            directionX = targetSnapshot.VelocityX * invLength;
            directionY = targetSnapshot.VelocityY * invLength;
            directionZ = targetSnapshot.VelocityZ * invLength;
            return true;
        }

        return false;
    }

    private static float Lerp(float start, float end, float alpha)
    {
        return start + ((end - start) * alpha);
    }

    private static float DistanceSquared(
        float x1,
        float y1,
        float z1,
        float x2,
        float y2,
        float z2)
    {
        float dx = x1 - x2;
        float dy = y1 - y2;
        float dz = z1 - z2;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private static Vector[] CreateDirectedPreloadProbeBuffer()
    {
        Vector[] buffer = new Vector[DirectedPreloadProbePointCount];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = new Vector(0.0f, 0.0f, 0.0f);
        }

        return buffer;
    }

    private static void SetPoint(Vector[] pointBuffer, int index, float x, float y, float z)
    {
        Vector point = pointBuffer[index];
        point.X = x;
        point.Y = y;
        point.Z = z;
    }

    /// <summary>
    /// Clears cached predictor data for the given target slot.
    /// </summary>
    internal void InvalidateTargetSlot(int targetSlot)
    {
        if ((uint)targetSlot < SlotCount)
        {
            _targetCacheBySlot[targetSlot] = null;
            if (_cachedViewerSlot == targetSlot)
            {
                _cachedViewerSlot = -1;
                _cachedViewerTick = -1;
            }
        }
    }

    /// <summary>
    /// Clears all predictor caches.
    /// </summary>
    internal void ClearCaches()
    {
        Array.Clear(_targetCacheBySlot, 0, _targetCacheBySlot.Length);
        _cachedViewerSlot = -1;
        _cachedViewerTick = -1;
    }
}
