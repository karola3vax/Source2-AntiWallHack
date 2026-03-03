using CounterStrikeSharp.API.Core;

namespace S2AWH;

internal sealed class TransmitFilter
{
    private const int SlotCount = 65;
    // Very close targets should never be culled by FOV; 75 units is roughly two player widths.
    private const float NearbyAlwaysVisibleDistanceSq = 75.0f * 75.0f;
    private const float ViewerGroundProbeHeight = 8.0f;
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

        float groundStartX = viewerSnapshot.OriginX;
        float groundStartY = viewerSnapshot.OriginY;
        float groundStartZ = viewerSnapshot.OriginZ + ViewerGroundProbeHeight;

        // Fast pass: origin
        if (IsPointInFovFromAnyViewerStart(groundStartX, groundStartY, groundStartZ, t.OriginX, t.OriginY, t.OriginZ, fovDotThreshold))
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

        // Conservative sphere-vs-cone check: if the AABB's bounding sphere overlaps the FOV cone,
        // do not cull here. False positives only cost extra LOS work; false negatives hide players.
        float halfFovRadians = (config.Trace.FovDegrees * 0.5f) * MathF.PI / 180.0f;
        if (IsBoundsSphereInFov(_cachedFovStartX, _cachedFovStartY, _cachedFovStartZ, centerX, centerY, centerZ, halfX, halfY, halfZ, halfFovRadians, fovDotThreshold) ||
            IsBoundsSphereInFov(groundStartX, groundStartY, groundStartZ, centerX, centerY, centerZ, halfX, halfY, halfZ, halfFovRadians, fovDotThreshold))
        {
            return true;
        }

        if (IsPointInFov(_cachedFovStartX, _cachedFovStartY, _cachedFovStartZ, centerX, centerY, centerZ, fovDotThreshold) ||
            IsPointInFov(groundStartX, groundStartY, groundStartZ, centerX, centerY, centerZ, fovDotThreshold))
        {
            return true;
        }

        if (IsPointInFov(_cachedFovStartX, _cachedFovStartY, _cachedFovStartZ, t.EyeX, t.EyeY, t.EyeZ, fovDotThreshold) ||
            IsPointInFov(groundStartX, groundStartY, groundStartZ, t.EyeX, t.EyeY, t.EyeZ, fovDotThreshold))
        {
            return true;
        }

        // Corners
        if (IsPointInFovFromAnyViewerStart(groundStartX, groundStartY, groundStartZ, minX, minY, minZ, fovDotThreshold) ||
            IsPointInFovFromAnyViewerStart(groundStartX, groundStartY, groundStartZ, maxX, minY, minZ, fovDotThreshold) ||
            IsPointInFovFromAnyViewerStart(groundStartX, groundStartY, groundStartZ, minX, maxY, minZ, fovDotThreshold) ||
            IsPointInFovFromAnyViewerStart(groundStartX, groundStartY, groundStartZ, maxX, maxY, minZ, fovDotThreshold) ||
            IsPointInFovFromAnyViewerStart(groundStartX, groundStartY, groundStartZ, minX, minY, maxZ, fovDotThreshold) ||
            IsPointInFovFromAnyViewerStart(groundStartX, groundStartY, groundStartZ, maxX, minY, maxZ, fovDotThreshold) ||
            IsPointInFovFromAnyViewerStart(groundStartX, groundStartY, groundStartZ, minX, maxY, maxZ, fovDotThreshold) ||
            IsPointInFovFromAnyViewerStart(groundStartX, groundStartY, groundStartZ, maxX, maxY, maxZ, fovDotThreshold))
        {
            return true;
        }

        // Lateral face centers (captures thin visible strips where corners can miss).
        return IsPointInFovFromAnyViewerStart(groundStartX, groundStartY, groundStartZ, minX, centerY, centerZ, fovDotThreshold) ||
               IsPointInFovFromAnyViewerStart(groundStartX, groundStartY, groundStartZ, maxX, centerY, centerZ, fovDotThreshold) ||
               IsPointInFovFromAnyViewerStart(groundStartX, groundStartY, groundStartZ, centerX, minY, centerZ, fovDotThreshold) ||
               IsPointInFovFromAnyViewerStart(groundStartX, groundStartY, groundStartZ, centerX, maxY, centerZ, fovDotThreshold) ||
               IsPointInFovFromAnyViewerStart(groundStartX, groundStartY, groundStartZ, centerX, centerY, minZ, fovDotThreshold) ||
               IsPointInFovFromAnyViewerStart(groundStartX, groundStartY, groundStartZ, centerX, centerY, maxZ, fovDotThreshold);
    }

    private bool IsPointInFov(float startX, float startY, float startZ, float pointX, float pointY, float pointZ, float fovDotThreshold)
    {
        float planeX = pointX - startX;
        float planeY = pointY - startY;
        float planeZ = pointZ - startZ;
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

    private bool IsPointInFovFromAnyViewerStart(
        float groundStartX,
        float groundStartY,
        float groundStartZ,
        float pointX,
        float pointY,
        float pointZ,
        float fovDotThreshold)
    {
        return IsPointInFov(_cachedFovStartX, _cachedFovStartY, _cachedFovStartZ, pointX, pointY, pointZ, fovDotThreshold) ||
               IsPointInFov(groundStartX, groundStartY, groundStartZ, pointX, pointY, pointZ, fovDotThreshold);
    }

    private bool IsBoundsSphereInFov(
        float startX,
        float startY,
        float startZ,
        float centerX,
        float centerY,
        float centerZ,
        float halfX,
        float halfY,
        float halfZ,
        float halfFovRadians,
        float fovDotThreshold)
    {
        float planeX = centerX - startX;
        float planeY = centerY - startY;
        float planeZ = centerZ - startZ;
        float distanceSq = (planeX * planeX) + (planeY * planeY) + (planeZ * planeZ);
        if (distanceSq < NearbyAlwaysVisibleDistanceSq)
        {
            return true;
        }

        float radiusSq = (halfX * halfX) + (halfY * halfY) + (halfZ * halfZ);
        if (radiusSq <= 0.0001f)
        {
            return IsPointInFov(startX, startY, startZ, centerX, centerY, centerZ, fovDotThreshold);
        }

        if (distanceSq <= radiusSq)
        {
            return true;
        }

        float distance = MathF.Sqrt(distanceSq);
        float radius = MathF.Sqrt(radiusSq);
        float radiusAngle = MathF.Asin(Math.Min(1.0f, radius / distance));
        float expandedHalfFov = MathF.Min(MathF.PI - 0.0001f, halfFovRadians + radiusAngle);
        float expandedDotThreshold = MathF.Cos(expandedHalfFov);
        float dot = (planeX * _cachedFovNormalX) + (planeY * _cachedFovNormalY) + (planeZ * _cachedFovNormalZ);
        float thresholdSq = expandedDotThreshold * expandedDotThreshold * distanceSq;

        if (expandedDotThreshold >= 0.0f)
        {
            if (dot <= 0.0f)
            {
                return false;
            }

            return (dot * dot) >= thresholdSq;
        }

        if (dot >= 0.0f)
        {
            return true;
        }

        return (dot * dot) <= thresholdSq;
    }
}
