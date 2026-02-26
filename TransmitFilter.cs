using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace S2AWH;

public class TransmitFilter
{
    private const float NearbyAlwaysVisibleDistanceSq = 75.0f * 75.0f;
    private readonly LosEvaluator _losEvaluator;
    private readonly PreloadPredictor _predictor;
    private int _cachedFovViewerPawnIndex = -1;
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
    internal VisibilityEval EvaluateVisibility(CCSPlayerController viewer, CCSPlayerController target, int nowTick, S2AWHConfig config)
    {
        if (!config.Core.Enabled)
        {
            return VisibilityEval.Visible;
        }

        bool viewerAlive = viewer.PawnIsAlive;
        bool targetAlive = target.PawnIsAlive;

        if (viewer.TeamNum == target.TeamNum && !config.Visibility.IncludeTeammates) return VisibilityEval.Visible; // Always transmit teammates if IncludeTeammates is false
        if (!targetAlive) return VisibilityEval.Visible; // Keep dead targets fail-open for spectator/ragdoll safety.

        if (target.IsBot && !config.Visibility.IncludeBots) return VisibilityEval.Visible; // Transmit bots to everyone if IncludeBots is false (don't hide bots)
        
        var viewerPawn = viewer.PlayerPawn.Value;
        var targetPawn = target.PlayerPawn.Value;

        if (viewerPawn == null || targetPawn == null) return VisibilityEval.Visible;

        if (config.Trace.UseFovCulling && viewerAlive)
        {
            if (!IsFOV(viewerPawn, targetPawn, config.FovDotThreshold, nowTick))
            {
                return VisibilityEval.Hidden;
            }
        }

        // 1. Check Line of Sight
        VisibilityEval losResult = _losEvaluator.EvaluateLineOfSight(viewer, target, nowTick);
        if (losResult == VisibilityEval.Visible)
        {
            return VisibilityEval.Visible;
        }

        if (losResult == VisibilityEval.UnknownTransient)
        {
            return VisibilityEval.UnknownTransient;
        }

        // Spectator/dead viewers should not get predictive preload from tiny movement events.
        if (!viewerAlive)
        {
            return VisibilityEval.Hidden;
        }

        // 2. Check Predictive Visibility (Peek assist)
        bool willBeVisible = _predictor.WillBeVisible(viewer, target, nowTick);
        if (willBeVisible)
        {
            return VisibilityEval.Visible;
        }

        return VisibilityEval.Hidden;
    }

    private bool IsFOV(CCSPlayerPawn viewerPawn, CBasePlayerPawn targetPawn, float fovDotThreshold, int nowTick)
    {
        // Nearly full-circle FOV means culling does no practical work.
        if (fovDotThreshold <= -0.9998f)
        {
            return true;
        }

        int viewerPawnIndex = (int)viewerPawn.Index;
        if (_cachedFovViewerPawnIndex != viewerPawnIndex || _cachedFovTick != nowTick)
        {
            _cachedFovViewerPawnIndex = viewerPawnIndex;
            _cachedFovTick = nowTick;
            _cachedFovStateReady = false;

            var start = viewerPawn.AbsOrigin;
            if (start == null)
            {
                return true; // Safety
            }

            _cachedFovStartX = start.X;
            _cachedFovStartY = start.Y;
            _cachedFovStartZ = start.Z;

            var viewOffset = viewerPawn.ViewOffset;
            if (viewOffset != null)
            {
                _cachedFovStartX += viewOffset.X;
                _cachedFovStartY += viewOffset.Y;
                _cachedFovStartZ += viewOffset.Z;
            }

            var angles = viewerPawn.EyeAngles;
            if (angles == null)
            {
                return true; // Safety
            }

            float pitch = angles.X * MathF.PI / 180.0f;
            float yaw = angles.Y * MathF.PI / 180.0f;

            (float sinPitch, float cosPitch) = MathF.SinCos(pitch);
            (float sinYaw, float cosYaw) = MathF.SinCos(yaw);
            _cachedFovNormalX = cosPitch * cosYaw;
            _cachedFovNormalY = cosPitch * sinYaw;
            _cachedFovNormalZ = -sinPitch;
            _cachedFovStateReady = true;
        }

        if (!_cachedFovStateReady)
        {
            return true;
        }

        var end = targetPawn.AbsOrigin;
        if (end == null)
        {
            return true;
        }

        float planeX = end.X - _cachedFovStartX;
        float planeY = end.Y - _cachedFovStartY;
        float planeZ = end.Z - _cachedFovStartZ;
        float distanceSq = (planeX * planeX) + (planeY * planeY) + (planeZ * planeZ);

        // If they are closer than 75 units, always render even if behind.
        if (distanceSq < NearbyAlwaysVisibleDistanceSq)
        {
            return true;
        }

        float inverseDistance = 1.0f / MathF.Sqrt(distanceSq);
        planeX *= inverseDistance;
        planeY *= inverseDistance;
        planeZ *= inverseDistance;

        float dot = (planeX * _cachedFovNormalX) + (planeY * _cachedFovNormalY) + (planeZ * _cachedFovNormalZ);
        
        // FOV culling threshold is derived from Trace.FovDegrees.
        return dot > fovDotThreshold;
    }
}
