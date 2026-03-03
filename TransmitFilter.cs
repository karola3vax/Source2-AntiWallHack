using CounterStrikeSharp.API.Core;

namespace S2AWH;

internal sealed class TransmitFilter
{
    private const int SlotCount = 65;
    // Very close targets should never be culled by FOV; 75 units is roughly two player widths.
    private const float NearbyAlwaysVisibleDistanceSq = 75.0f * 75.0f;
    private readonly LosEvaluator _losEvaluator;
    private readonly PreloadPredictor _predictor;
    private int _cachedFovViewerSlot = -1;
    private int _cachedFovTick = -1;
    private bool _cachedFovStateReady;
    private float _cachedFovStartX;
    private float _cachedFovStartY;
    private float _cachedFovStartZ;
    private float _cachedFovNormalX;
    private float _cachedFovNormalY;
    private float _cachedFovNormalZ;

    public TransmitFilter(LosEvaluator losEvaluator, PreloadPredictor predictor)
    {
        _losEvaluator = losEvaluator;
        _predictor = predictor;
    }

    /// <summary>
    /// Determines whether the target's data should be transmitted to the viewer
    /// </summary>
    internal VisibilityEval EvaluateVisibility(
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

        if (pawnsBySlot[viewerSlot] == null || pawnsBySlot[targetSlot] == null)
        {
            return VisibilityEval.Visible;
        }

        if (config.Trace.UseFovCulling)
        {
            if (!IsFov(viewerSlot, targetSlot, config, nowTick, transforms))
            {
                return VisibilityEval.Hidden;
            }
        }

        // 1. Check Line of Sight
        VisibilityEval losResult = _losEvaluator.EvaluateLineOfSight(
            viewerSlot,
            targetSlot,
            viewerIsBot,
            nowTick,
            config,
            transforms,
            pawnsBySlot);
        if (losResult == VisibilityEval.Visible)
        {
            return VisibilityEval.Visible;
        }

        if (losResult == VisibilityEval.UnknownTransient)
        {
            return VisibilityEval.UnknownTransient;
        }

        // 2. Check Predictive Visibility (Peek assist)
        bool willBeVisible = _predictor.WillBeVisible(viewerSlot, targetSlot, viewerIsBot, nowTick, transforms, pawnsBySlot);
        if (willBeVisible)
        {
            return VisibilityEval.Visible;
        }

        return VisibilityEval.Hidden;
    }

    private bool IsFov(int viewerSlot, int targetSlot, S2AWHConfig config, int nowTick, PlayerTransformSnapshot[] transforms)
    {
        float fovDotThreshold = config.FovDotThreshold;

        // Nearly full-circle FOV means culling does no practical work.
        if (fovDotThreshold <= -0.9998f)
        {
            return true;
        }

        if ((uint)viewerSlot >= SlotCount) return true;
        ref var viewerSnapshot = ref transforms[viewerSlot];
        if (!viewerSnapshot.IsValid) return true;

        if (_cachedFovViewerSlot != viewerSlot || _cachedFovTick != nowTick)
        {
            _cachedFovViewerSlot = viewerSlot;
            _cachedFovTick = nowTick;
            _cachedFovStateReady = false;

            _cachedFovStartX = viewerSnapshot.EyeX;
            _cachedFovStartY = viewerSnapshot.EyeY;
            _cachedFovStartZ = viewerSnapshot.EyeZ;
            _cachedFovNormalX = viewerSnapshot.FovNormalX;
            _cachedFovNormalY = viewerSnapshot.FovNormalY;
            _cachedFovNormalZ = viewerSnapshot.FovNormalZ;
            _cachedFovStateReady = true;
        }

        if (!_cachedFovStateReady)
        {
            return true;
        }

        if ((uint)targetSlot >= SlotCount) return true;
        ref var t = ref transforms[targetSlot];
        if (!t.IsValid) return true;

        // Fast pass: origin
        if (IsPointInFov(t.OriginX, t.OriginY, t.OriginZ, fovDotThreshold))
        {
            return true;
        }

        float centerX = t.OriginX + t.CenterX;
        float centerY = t.OriginY + t.CenterY;
        float centerZ = t.OriginZ + t.CenterZ;
        float halfX = (t.MaxsX - t.MinsX) * 0.5f * config.Aabb.LosHorizontalScale;
        float halfY = (t.MaxsY - t.MinsY) * 0.5f * config.Aabb.LosHorizontalScale;
        float halfZ = (t.MaxsZ - t.MinsZ) * 0.5f * config.Aabb.LosVerticalScale;

        float minX = centerX - halfX;
        float minY = centerY - halfY;
        float minZ = centerZ - halfZ;
        float maxX = centerX + halfX;
        float maxY = centerY + halfY;
        float maxZ = centerZ + halfZ;

        if (IsPointInFov(centerX, centerY, centerZ, fovDotThreshold))
        {
            return true;
        }

        if (IsPointInFov(t.EyeX, t.EyeY, t.EyeZ, fovDotThreshold))
        {
            return true;
        }

        // Corners
        if (IsPointInFov(minX, minY, minZ, fovDotThreshold) ||
            IsPointInFov(maxX, minY, minZ, fovDotThreshold) ||
            IsPointInFov(minX, maxY, minZ, fovDotThreshold) ||
            IsPointInFov(maxX, maxY, minZ, fovDotThreshold) ||
            IsPointInFov(minX, minY, maxZ, fovDotThreshold) ||
            IsPointInFov(maxX, minY, maxZ, fovDotThreshold) ||
            IsPointInFov(minX, maxY, maxZ, fovDotThreshold) ||
            IsPointInFov(maxX, maxY, maxZ, fovDotThreshold))
        {
            return true;
        }

        // Lateral face centers (captures thin visible strips where corners can miss).
        return IsPointInFov(minX, centerY, centerZ, fovDotThreshold) ||
               IsPointInFov(maxX, centerY, centerZ, fovDotThreshold) ||
               IsPointInFov(centerX, minY, centerZ, fovDotThreshold) ||
               IsPointInFov(centerX, maxY, centerZ, fovDotThreshold) ||
               IsPointInFov(centerX, centerY, minZ, fovDotThreshold) ||
               IsPointInFov(centerX, centerY, maxZ, fovDotThreshold);
    }

    private bool IsPointInFov(float pointX, float pointY, float pointZ, float fovDotThreshold)
    {
        float planeX = pointX - _cachedFovStartX;
        float planeY = pointY - _cachedFovStartY;
        float planeZ = pointZ - _cachedFovStartZ;
        float distanceSq = (planeX * planeX) + (planeY * planeY) + (planeZ * planeZ);

        // If they are closer than 75 units, always render even if behind.
        if (distanceSq < NearbyAlwaysVisibleDistanceSq)
        {
            return true;
        }

        if (distanceSq <= 0.0001f)
        {
            return true;
        }

        float dot = (planeX * _cachedFovNormalX) + (planeY * _cachedFovNormalY) + (planeZ * _cachedFovNormalZ);
        float thresholdSq = fovDotThreshold * fovDotThreshold * distanceSq;

        if (fovDotThreshold >= 0.0f)
        {
            if (dot <= 0.0f)
            {
                return false;
            }

            return (dot * dot) > thresholdSq;
        }

        // For negative thresholds:
        // dot >= 0 is always inside.
        // dot < 0 must satisfy dot > threshold * |plane|, which maps to dot^2 < threshold^2 * |plane|^2.
        if (dot >= 0.0f)
        {
            return true;
        }

        return (dot * dot) < thresholdSq;
    }
}
