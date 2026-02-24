using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using RayTraceAPI;

namespace S2AWH;

public class PreloadPredictor
{
    private sealed class PredictorTargetCacheEntry
    {
        public int Tick = -1;
        public int PawnIndex = -1;
        public bool HasTargetLookahead;
        public int PredictedPointCount;
        public Vector[] PredictedPoints = VisibilityGeometry.CreatePointBuffer();
        public bool CurrentPointsReady;
        public int CurrentPointCount;
        public Vector[] CurrentPoints = VisibilityGeometry.CreatePointBuffer();
    }

    private readonly CRayTraceInterface _rayTrace;
    private readonly Vector _viewerEyeBuffer = new(0.0f, 0.0f, 0.0f);
    private readonly Vector _predictedTargetOrigin = new(0.0f, 0.0f, 0.0f);
    private readonly Vector _predictedViewerEye = new(0.0f, 0.0f, 0.0f);
    private readonly Dictionary<int, PredictorTargetCacheEntry> _targetCacheBySlot = new(64);

    // Per-tick viewer eye cache (same pattern as LosEvaluator).
    private int _cachedViewerPawnIndex = -1;
    private int _cachedViewerTick = -1;

    public PreloadPredictor(CRayTraceInterface rayTrace)
    {
        _rayTrace = rayTrace;
    }

    /// <summary>
    /// Predicts if target will be visible to viewer in the near future (to prevent pop-in)
    /// </summary>
    public bool WillBeVisible(CCSPlayerController viewer, CCSPlayerController target, int nowTick)
    {
        var viewerPawn = viewer.PlayerPawn.Value;
        var targetPawn = target.PlayerPawn.Value;
        if (viewerPawn == null || targetPawn == null)
            return false;

        var targetOrigin = targetPawn.AbsOrigin;
        if (targetOrigin == null)
        {
            return false;
        }

        int viewerPawnIndex = (int)viewerPawn.Index;
        if (_cachedViewerPawnIndex != viewerPawnIndex || _cachedViewerTick != nowTick)
        {
            if (!VisibilityGeometry.TryFillEyePosition(viewerPawn, _viewerEyeBuffer))
            {
                return false;
            }
            _cachedViewerPawnIndex = viewerPawnIndex;
            _cachedViewerTick = nowTick;
        }

        var config = S2AWHState.Current;
        bool viewerIsBot = viewer.IsBot;
        var options = VisibilityGeometry.GetVisibilityTraceOptions();
        bool drawDebugBeams = VisibilityGeometry.ShouldDrawDebugTraceBeam(viewerIsBot);
        var targetCache = GetOrRefreshTargetCache(target, targetPawn, targetOrigin, config, nowTick);

        if (targetCache.HasTargetLookahead &&
            CanSeeAnyPoint(viewerPawn, targetPawn, _viewerEyeBuffer, targetCache.PredictedPoints, targetCache.PredictedPointCount, options, drawDebugBeams, viewerIsBot))
        {
            return true;
        }

        if (!config.Preload.EnableViewerPeekAssist)
        {
            return false;
        }

        float viewerPredictDistance = config.Preload.PredictorDistance * config.Preload.ViewerPredictorDistanceFactor;
        if (!TryGetLookahead(
                viewerPawn.AbsVelocity,
                config.Preload.PredictorMinSpeed,
                config.Aabb.ProfileSpeedFull,
                viewerPredictDistance,
                horizontalOnly: true,
                out float viewerLookaheadX,
                out float viewerLookaheadY,
                out float viewerLookaheadZ))
        {
            return false;
        }

        _predictedViewerEye.X = _viewerEyeBuffer.X + viewerLookaheadX;
        _predictedViewerEye.Y = _viewerEyeBuffer.Y + viewerLookaheadY;
        _predictedViewerEye.Z = _viewerEyeBuffer.Z + viewerLookaheadZ;

        EnsureCurrentPoints(targetCache, targetPawn, targetOrigin);
        if (targetCache.CurrentPointCount <= 0)
        {
            return false;
        }

        return CanSeeAnyPoint(viewerPawn, targetPawn, _predictedViewerEye, targetCache.CurrentPoints, targetCache.CurrentPointCount, options, drawDebugBeams, viewerIsBot);
    }

    private bool CanSeeAnyPoint(
        CBasePlayerPawn viewerPawn,
        CCSPlayerPawn targetPawn,
        Vector eyePosition,
        Vector[] targetPoints,
        int targetPointCount,
        TraceOptions options,
        bool drawDebugBeams,
        bool viewerIsBot)
    {
        if (targetPointCount == 0)
        {
            return false;
        }

        var targetHandle = targetPawn.Handle;
        for (int i = 0; i < targetPointCount; i++)
        {
            var targetPoint = targetPoints[i];
            if (_rayTrace.TraceEndShape(eyePosition, targetPoint, viewerPawn, options, out var result))
            {
                if (drawDebugBeams)
                {
                    VisibilityGeometry.DrawDebugTraceBeam(eyePosition, targetPoint, result, viewerIsBot);
                }

                if (!result.DidHit || result.HitEntity == targetHandle)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private PredictorTargetCacheEntry GetOrRefreshTargetCache(
        CCSPlayerController target,
        CCSPlayerPawn targetPawn,
        Vector targetOrigin,
        S2AWHConfig config,
        int nowTick)
    {
        int targetSlot = target.Slot;
        if (!_targetCacheBySlot.TryGetValue(targetSlot, out var cache) || cache == null)
        {
            cache = new PredictorTargetCacheEntry();
            _targetCacheBySlot[targetSlot] = cache;
        }

        int pawnIndex = (int)targetPawn.Index;
        if (cache.Tick == nowTick && cache.PawnIndex == pawnIndex)
        {
            return cache;
        }

        cache.Tick = nowTick;
        cache.PawnIndex = pawnIndex;
        cache.CurrentPointsReady = false;
        cache.CurrentPointCount = 0;
        cache.HasTargetLookahead = TryGetLookahead(
            targetPawn.AbsVelocity,
            config.Preload.PredictorMinSpeed,
            config.Aabb.ProfileSpeedFull,
            config.Preload.PredictorDistance,
            horizontalOnly: false,
            out float targetLookaheadX,
            out float targetLookaheadY,
            out float targetLookaheadZ);

        if (cache.HasTargetLookahead)
        {
            _predictedTargetOrigin.X = targetOrigin.X + targetLookaheadX;
            _predictedTargetOrigin.Y = targetOrigin.Y + targetLookaheadY;
            _predictedTargetOrigin.Z = targetOrigin.Z + targetLookaheadZ;
            cache.PredictedPointCount = VisibilityGeometry.FillTargetPoints(targetPawn, cache.PredictedPoints, _predictedTargetOrigin, true);
        }
        else
        {
            cache.PredictedPointCount = 0;
        }

        return cache;
    }

    private static void EnsureCurrentPoints(PredictorTargetCacheEntry cache, CCSPlayerPawn targetPawn, Vector targetOrigin)
    {
        if (cache.CurrentPointsReady)
        {
            return;
        }

        cache.CurrentPointCount = VisibilityGeometry.FillTargetPoints(targetPawn, cache.CurrentPoints, targetOrigin, true);
        cache.CurrentPointsReady = true;
    }

    private static bool TryGetLookahead(
        Vector? velocity,
        float minimumSpeed,
        float fullSpeedForMaxLookahead,
        float lookaheadDistance,
        bool horizontalOnly,
        out float lookaheadX,
        out float lookaheadY,
        out float lookaheadZ)
    {
        lookaheadX = 0.0f;
        lookaheadY = 0.0f;
        lookaheadZ = 0.0f;

        if (velocity == null || lookaheadDistance <= 0.0f)
        {
            return false;
        }

        float speedSquared;
        if (horizontalOnly)
        {
            speedSquared =
                (velocity.X * velocity.X) +
                (velocity.Y * velocity.Y);
        }
        else
        {
            speedSquared =
                (velocity.X * velocity.X) +
                (velocity.Y * velocity.Y) +
                (velocity.Z * velocity.Z);
        }

        if (speedSquared <= 0.0001f)
        {
            return false;
        }

        float speed = MathF.Sqrt(speedSquared);
        float minSpeed = MathF.Max(0.0f, minimumSpeed);
        if (speed < minSpeed)
        {
            return false;
        }

        float fullSpeed = MathF.Max(minSpeed + 1.0f, fullSpeedForMaxLookahead);
        float speedAlpha = Math.Clamp((speed - minSpeed) / (fullSpeed - minSpeed), 0.0f, 1.0f);
        float effectiveLookaheadDistance = lookaheadDistance * speedAlpha;
        if (effectiveLookaheadDistance <= 0.001f)
        {
            return false;
        }

        float inverseSpeed = 1.0f / speed;
        lookaheadX = velocity.X * inverseSpeed * effectiveLookaheadDistance;
        lookaheadY = velocity.Y * inverseSpeed * effectiveLookaheadDistance;
        lookaheadZ = horizontalOnly
            ? 0.0f
            : velocity.Z * inverseSpeed * effectiveLookaheadDistance;
        return true;
    }

    internal void InvalidateTargetSlot(int targetSlot)
    {
        _targetCacheBySlot.Remove(targetSlot);
    }

    internal void ClearCaches()
    {
        _targetCacheBySlot.Clear();
        _cachedViewerPawnIndex = -1;
        _cachedViewerTick = -1;
    }
}
