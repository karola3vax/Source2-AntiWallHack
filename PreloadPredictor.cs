using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using RayTraceAPI;

namespace S2AWH;

internal sealed class PreloadPredictor
{
    private const int SlotCount = 65;
    private const int PredictorTracePointCount = 5; // eye + center + 3 boundaries
    private const int SurfaceProbePointCount = 18; // max: 3 probe rows * 6 faces
    private const float StandingViewOffsetZ = 64.0f;
    private const float MinStandUpLeadUnits = 1.0f;
    private const float StandUpHeadroomMultiplier = 2.0f;
    private const float MinViewerHorizontalPreloadLeadUnits = 18.0f;
    private const float MaxPreloadTargetHeadroomUnits = 12.0f;
    private const float MinJumpLeadUnits = 4.0f;
    private const float JumpAssistHeadroomUnits = 64.0f;
    private const float MinJumpAssistLeadUnits = 48.0f;
    private const float MaxJumpAssistLeadUnits = 96.0f;
    private const float JumpAssistGravityUnitsPerSecondSq = 800.0f;
    private const float DefaultJumpImpulseUnitsPerSecond = 301.0f;
    private const float MaxJumpAssistHorizontalLeadUnits = 96.0f;

    private sealed class PredictorTargetCacheEntry
    {
        public int Tick = -1;
        public bool HasTargetLookahead;
        public int PredictedPointCount;
        public Vector[] PredictedPoints = CreatePredictorPointBuffer();
        public int PredictedSurfacePointCount;
        public Vector[] PredictedSurfacePoints = CreateSurfaceProbePointBuffer();
        public bool CurrentPointsReady;
        public int CurrentPointCount;
        public Vector[] CurrentPoints = CreatePredictorPointBuffer();
        public bool CurrentSurfacePointsReady;
        public int CurrentSurfacePointCount;
        public Vector[] CurrentSurfacePoints = CreateSurfaceProbePointBuffer();
    }

    private readonly CRayTraceInterface _rayTrace;
    private readonly Action<int, ViewerRayTraceStage>? _recordViewerTraceAttempt;
    private readonly Vector _viewerEyeBuffer = new(0.0f, 0.0f, 0.0f);
    private readonly Vector _predictedTargetOrigin = new(0.0f, 0.0f, 0.0f);
    private readonly Vector _currentTargetOrigin = new(0.0f, 0.0f, 0.0f);
    private readonly Vector _traceStart = new(0.0f, 0.0f, 0.0f);
    private readonly Vector _predictedViewerEye = new(0.0f, 0.0f, 0.0f);
    private readonly Vector _predictedViewerJumpEye = new(0.0f, 0.0f, 0.0f);
    private readonly Vector _predictedViewerHighEye = new(0.0f, 0.0f, 0.0f);
    private readonly Vector _surfaceTraceEnd = new(0.0f, 0.0f, 0.0f);

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
    /// Uses both predictor points and predictor surface probes.
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
            EnsureCurrentPoints(targetCache, ref targetSnapshot, _currentTargetOrigin, config);
        }

        if (config.Preload.EnabledForHolders &&
            targetCache.HasTargetLookahead &&
            CanSeePreloadPath(
                viewerPawn,
                targetPawn,
                _viewerEyeBuffer,
                ref viewerSnapshot,
                ref targetSnapshot,
                _predictedTargetOrigin,
                applyDirectionalShift: false,
                targetCache.PredictedPoints,
                targetCache.PredictedPointCount,
                targetCache.PredictedSurfacePoints,
                targetCache.PredictedSurfacePointCount,
                surfaceHitRadiusSq,
                float.PositiveInfinity,
                DebugTraceKind.Preload,
                drawDebugBeams,
                config))
        {
            return true;
        }

        if (!config.Preload.EnabledForPeekers)
        {
            return false;
        }

        if (!TryGetViewerPeekLookahead(
                ref viewerSnapshot,
                config,
                out float viewerLookaheadX,
                out float viewerLookaheadY,
                out float viewerLookaheadZ))
        {
            return false;
        }

        _predictedViewerEye.X = _viewerEyeBuffer.X + viewerLookaheadX;
        _predictedViewerEye.Y = _viewerEyeBuffer.Y + viewerLookaheadY;
        _predictedViewerEye.Z = _viewerEyeBuffer.Z + Math.Max(0.0f, viewerLookaheadZ);

        EnsureCurrentPoints(targetCache, ref targetSnapshot, _currentTargetOrigin, config);
        EnsureCurrentSurfacePoints(targetCache, ref targetSnapshot, _currentTargetOrigin, config);

        if (CanSeePreloadPath(
            viewerPawn,
            targetPawn,
            _predictedViewerEye,
            ref viewerSnapshot,
            ref targetSnapshot,
            _currentTargetOrigin,
            applyDirectionalShift: true,
            targetCache.CurrentPoints,
            targetCache.CurrentPointCount,
            targetCache.CurrentSurfacePoints,
            targetCache.CurrentSurfacePointCount,
            surfaceHitRadiusSq,
            maxPreloadTargetPointZ,
            DebugTraceKind.Preload,
            drawDebugBeams,
            config))
        {
            return true;
        }

        float highStandUpTargetEyeOffsetZ = GetViewerHighStandUpTargetEyeOffset(ref viewerSnapshot);
        float highStandUpTargetEyeZ = viewerSnapshot.OriginZ + highStandUpTargetEyeOffsetZ;
        if (highStandUpTargetEyeZ <= _predictedViewerEye.Z)
        {
            return false;
        }

        _predictedViewerHighEye.X = _viewerEyeBuffer.X + viewerLookaheadX;
        _predictedViewerHighEye.Y = _viewerEyeBuffer.Y + viewerLookaheadY;
        _predictedViewerHighEye.Z = highStandUpTargetEyeZ;

        return CanSeePreloadPath(
            viewerPawn,
            targetPawn,
            _predictedViewerHighEye,
            ref viewerSnapshot,
            ref targetSnapshot,
            _currentTargetOrigin,
            applyDirectionalShift: true,
            targetCache.CurrentPoints,
            targetCache.CurrentPointCount,
            targetCache.CurrentSurfacePoints,
            targetCache.CurrentSurfacePointCount,
            surfaceHitRadiusSq,
            maxPreloadTargetPointZ,
            DebugTraceKind.Preload,
            drawDebugBeams,
            config);
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
        if (_predictedViewerJumpEye.Z <= viewerSnapshot.EyeZ)
        {
            return false;
        }

        return CanSeeNearestJumpAssistSurfaceProbe(
            viewerPawn,
            targetPawn,
            _predictedViewerJumpEye,
            ref targetSnapshot,
            jumpHitRadiusSq,
            drawDebugBeams,
            config);
    }

    private bool CanSeePreloadPath(
        CBasePlayerPawn viewerPawn,
        CBasePlayerPawn targetPawn,
        Vector eyePosition,
        ref PlayerTransformSnapshot viewerSnapshot,
        ref PlayerTransformSnapshot targetSnapshot,
        Vector targetOrigin,
        bool applyDirectionalShift,
        Vector[] predictorPoints,
        int predictorPointCount,
        Vector[] surfacePoints,
        int surfacePointCount,
        float hitRadiusSq,
        float maxTargetPointZ,
        DebugTraceKind traceKind,
        bool drawDebugBeams,
        S2AWHConfig config)
    {
        if (CanSeeNearestSurfaceProbe(
                viewerPawn,
                targetPawn,
                eyePosition,
                ref viewerSnapshot,
                ref targetSnapshot,
                targetOrigin,
                applyDirectionalShift,
                hitRadiusSq,
                maxTargetPointZ,
                traceKind,
                drawDebugBeams,
                config))
        {
            return true;
        }

        if (predictorPointCount > 0 &&
            CanSeeAnyTargetPoint(
                viewerPawn,
                targetPawn,
                eyePosition,
                ref viewerSnapshot,
                predictorPoints,
                predictorPointCount,
                maxTargetPointZ,
                traceKind,
                drawDebugBeams))
        {
            return true;
        }

        return surfacePointCount > 0 &&
               CanSeeAnySurfaceProbe(
                   viewerPawn,
                   targetPawn,
                   eyePosition,
                   ref viewerSnapshot,
                   surfacePoints,
                   surfacePointCount,
                   hitRadiusSq,
                   maxTargetPointZ,
                   traceKind,
                   drawDebugBeams);
    }

    private void RecordActiveViewerTraceAttempt(ViewerRayTraceStage stage)
    {
        if (_activeViewerSlot >= 0)
        {
            _recordViewerTraceAttempt?.Invoke(_activeViewerSlot, stage);
        }
    }

    private bool CanSeeAnyTargetPoint(
        CBasePlayerPawn viewerPawn,
        CBasePlayerPawn targetPawn,
        Vector eyePosition,
        ref PlayerTransformSnapshot viewerSnapshot,
        Vector[] targetPoints,
        int targetPointCount,
        float maxTargetPointZ,
        DebugTraceKind traceKind,
        bool drawDebugBeams)
    {
        nint targetHandle = targetPawn.Handle;
        for (int i = 0; i < targetPointCount; i++)
        {
            Vector targetPoint = targetPoints[i];
            if (targetPoint.Z > maxTargetPointZ)
            {
                continue;
            }

            AabbGeometry.SetDistributedViewerOrigin(_traceStart, ref viewerSnapshot, eyePosition, targetPoint.X, targetPoint.Y, targetPoint.Z);
            RecordActiveViewerTraceAttempt(ViewerRayTraceStage.Preload);
            if (!_rayTrace.TraceEndShape(_traceStart, targetPoint, viewerPawn, _cachedTraceOptions, out var result))
            {
                continue;
            }

            if (drawDebugBeams)
            {
                VisibilityGeometry.DrawDebugTraceBeam(_traceStart, targetPoint, result, traceKind);
            }

            if (!result.DidHit || result.HitEntity == targetHandle)
            {
                return true;
            }
        }

        return false;
    }

    private bool CanSeeNearestSurfaceProbe(
        CBasePlayerPawn viewerPawn,
        CBasePlayerPawn targetPawn,
        Vector eyePosition,
        ref PlayerTransformSnapshot viewerSnapshot,
        ref PlayerTransformSnapshot targetSnapshot,
        Vector targetOrigin,
        bool applyDirectionalShift,
        float hitRadiusSq,
        float maxTargetPointZ,
        DebugTraceKind traceKind,
        bool drawDebugBeams,
        S2AWHConfig config)
    {
        GetPredictorWorldBounds(ref targetSnapshot, targetOrigin, config, applyDirectionalShift, out float minX, out float minY, out float minZ, out float maxX, out float maxY, out float maxZ);
        AabbGeometry.GetClosestPointOnSurface(
            eyePosition.X,
            eyePosition.Y,
            eyePosition.Z,
            minX,
            minY,
            minZ,
            maxX,
            maxY,
            maxZ,
            out float probeX,
            out float probeY,
            out float probeZ);

        if (probeZ > maxTargetPointZ)
        {
            probeZ = maxTargetPointZ;
        }

        AabbGeometry.SetDistributedViewerOrigin(_traceStart, ref viewerSnapshot, eyePosition, probeX, probeY, probeZ);
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

    private bool CanSeeAnySurfaceProbe(
        CBasePlayerPawn viewerPawn,
        CBasePlayerPawn targetPawn,
        Vector eyePosition,
        ref PlayerTransformSnapshot viewerSnapshot,
        Vector[] surfacePoints,
        int surfacePointCount,
        float hitRadiusSq,
        float maxTargetPointZ,
        DebugTraceKind traceKind,
        bool drawDebugBeams)
    {
        nint targetHandle = targetPawn.Handle;
        for (int i = 0; i < surfacePointCount; i++)
        {
            Vector probePoint = surfacePoints[i];
            if (probePoint.Z > maxTargetPointZ)
            {
                continue;
            }

            AabbGeometry.SetDistributedViewerOrigin(_traceStart, ref viewerSnapshot, eyePosition, probePoint.X, probePoint.Y, probePoint.Z);
            RecordActiveViewerTraceAttempt(ViewerRayTraceStage.Preload);
            if (!_rayTrace.TraceEndShape(_traceStart, probePoint, viewerPawn, _cachedTraceOptions, out var result))
            {
                continue;
            }

            if (drawDebugBeams)
            {
                VisibilityGeometry.DrawDebugTraceBeam(_traceStart, probePoint, result, traceKind);
            }

            if (!result.DidHit || result.HitEntity == targetHandle)
            {
                return true;
            }

            if (hitRadiusSq > 0.0f &&
                DistanceSquared(probePoint.X, probePoint.Y, probePoint.Z, result.EndPosX, result.EndPosY, result.EndPosZ) <= hitRadiusSq)
            {
                return true;
            }
        }

        return false;
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
        cache.CurrentPointsReady = false;
        cache.CurrentPointCount = 0;
        cache.CurrentSurfacePointsReady = false;
        cache.CurrentSurfacePointCount = 0;
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
            cache.PredictedPointCount = 0;
            cache.PredictedSurfacePointCount = 0;
            return cache;
        }

        _predictedTargetOrigin.X = targetSnapshot.OriginX + targetLookaheadX;
        _predictedTargetOrigin.Y = targetSnapshot.OriginY + targetLookaheadY;
        _predictedTargetOrigin.Z = targetSnapshot.OriginZ + targetLookaheadZ;

        cache.PredictedPointCount = FillPredictorTargetPoints(
            ref targetSnapshot,
            _predictedTargetOrigin,
            cache.PredictedPoints,
            config,
            applyDirectionalShift: false,
            DebugAabbKind.PredictorPredicted);
        cache.PredictedSurfacePointCount = FillSurfaceProbePoints(
            ref targetSnapshot,
            _predictedTargetOrigin,
            cache.PredictedSurfacePoints,
            config,
            applyDirectionalShift: false);

        return cache;
    }

    private static void EnsureCurrentPoints(
        PredictorTargetCacheEntry cache,
        ref PlayerTransformSnapshot targetSnapshot,
        Vector targetOrigin,
        S2AWHConfig config)
    {
        if (cache.CurrentPointsReady)
        {
            return;
        }

        cache.CurrentPointCount = FillPredictorTargetPoints(
            ref targetSnapshot,
            targetOrigin,
            cache.CurrentPoints,
            config,
            applyDirectionalShift: true,
            DebugAabbKind.PredictorCurrent);
        cache.CurrentPointsReady = true;
    }

    private static void EnsureCurrentSurfacePoints(
        PredictorTargetCacheEntry cache,
        ref PlayerTransformSnapshot targetSnapshot,
        Vector targetOrigin,
        S2AWHConfig config)
    {
        if (cache.CurrentSurfacePointsReady)
        {
            return;
        }

        cache.CurrentSurfacePointCount = FillSurfaceProbePoints(
            ref targetSnapshot,
            targetOrigin,
            cache.CurrentSurfacePoints,
            config,
            applyDirectionalShift: true);
        cache.CurrentSurfacePointsReady = true;
    }

    private static int FillPredictorTargetPoints(
        ref PlayerTransformSnapshot targetSnapshot,
        Vector targetOrigin,
        Vector[] pointBuffer,
        S2AWHConfig config,
        bool applyDirectionalShift,
        DebugAabbKind debugAabbKind)
    {
        if (pointBuffer.Length < PredictorTracePointCount || !targetSnapshot.IsValid)
        {
            return 0;
        }

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

        SetPoint(pointBuffer, 0, targetOrigin.X + targetSnapshot.ViewOffsetX, targetOrigin.Y + targetSnapshot.ViewOffsetY, targetOrigin.Z + targetSnapshot.ViewOffsetZ);
        SetPoint(pointBuffer, 1, centerX, centerY, centerZ);
        SetPoint(pointBuffer, 2, centerX - halfX, centerY - halfY, centerZ - halfZ);
        SetPoint(pointBuffer, 3, centerX + halfX, centerY - halfY, centerZ - halfZ);
        SetPoint(pointBuffer, 4, centerX + halfX, centerY + halfY, centerZ + halfZ);

        VisibilityGeometry.DrawDebugAabbBox(
            centerX - halfX,
            centerY - halfY,
            centerZ - halfZ,
            centerX + halfX,
            centerY + halfY,
            centerZ + halfZ,
            debugAabbKind);

        return PredictorTracePointCount;
    }

    private static int FillSurfaceProbePoints(
        ref PlayerTransformSnapshot targetSnapshot,
        Vector targetOrigin,
        Vector[] pointBuffer,
        S2AWHConfig config,
        bool applyDirectionalShift)
    {
        if (pointBuffer.Length < SurfaceProbePointCount || !targetSnapshot.IsValid)
        {
            return 0;
        }

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

        return AabbGeometry.FillSurfaceProbePoints(
            pointBuffer,
            config.Preload.SurfaceProbeRows,
            centerX,
            centerY,
            centerZ,
            halfX,
            halfY,
            halfZ);
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
        bool hasVelocityLookahead = TryGetLookahead(
            viewerSnapshot.VelocityX,
            viewerSnapshot.VelocityY,
            0.0f,
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
                lookaheadX = viewerSnapshot.VelocityX * inverseHorizontalSpeed * desiredMinimumLead;
                lookaheadY = viewerSnapshot.VelocityY * inverseHorizontalSpeed * desiredMinimumLead;
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



    private bool CanSeeNearestJumpAssistSurfaceProbe(
        CBasePlayerPawn viewerPawn,
        CBasePlayerPawn targetPawn,
        Vector eyePosition,
        ref PlayerTransformSnapshot targetSnapshot,
        float hitRadiusSq,
        bool drawDebugBeams,
        S2AWHConfig config)
    {
        GetJumpAssistWorldBounds(ref targetSnapshot, config, out float minX, out float minY, out float minZ, out float maxX, out float maxY, out float maxZ);
        AabbGeometry.GetClosestPointOnSurface(
            eyePosition.X,
            eyePosition.Y,
            eyePosition.Z,
            minX,
            minY,
            minZ,
            maxX,
            maxY,
            maxZ,
            out float probeX,
            out float probeY,
            out float probeZ);

        _traceStart.X = eyePosition.X;
        _traceStart.Y = eyePosition.Y;
        _traceStart.Z = eyePosition.Z;
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

    private static Vector[] CreatePredictorPointBuffer()
    {
        Vector[] buffer = new Vector[PredictorTracePointCount];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = new Vector(0.0f, 0.0f, 0.0f);
        }

        return buffer;
    }

    private static Vector[] CreateSurfaceProbePointBuffer()
    {
        Vector[] buffer = new Vector[SurfaceProbePointCount];
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
