using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace S2AWH;

public class TransmitFilter
{
    private readonly LosEvaluator _losEvaluator;
    private readonly PreloadPredictor _predictor;

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
            if (!IsFOV(viewerPawn, targetPawn, config.FovDotThreshold))
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

    private static bool IsFOV(CBasePlayerPawn viewerPawnBase, CBasePlayerPawn targetPawn, float fovDotThreshold)
    {
        var start = viewerPawnBase.AbsOrigin;
        if (start == null) return true; // Safety

        float startX = start.X;
        float startY = start.Y;
        float startZ = start.Z;

        var viewOffset = viewerPawnBase.ViewOffset;
        if (viewOffset != null)
        {
            startX += viewOffset.X;
            startY += viewOffset.Y;
            startZ += viewOffset.Z;
        }

        var end = targetPawn.AbsOrigin;
        if (end == null) return true;

        // Cast to CCSPlayerPawn to ensure we can get EyeAngles safely
        var viewerPawn = viewerPawnBase as CCSPlayerPawn;
        if (viewerPawn == null) return true;

        var angles = viewerPawn.EyeAngles;
        if (angles == null) return true;

        float pitch = angles.X * MathF.PI / 180.0f;
        float yaw = angles.Y * MathF.PI / 180.0f;

        (float sinPitch, float cosPitch) = MathF.SinCos(pitch);
        (float sinYaw, float cosYaw) = MathF.SinCos(yaw);
        float normalX = cosPitch * cosYaw;
        float normalY = cosPitch * sinYaw;
        float normalZ = -sinPitch;

        float planeX = end.X - startX;
        float planeY = end.Y - startY;
        float planeZ = end.Z - startZ;
        float distance = MathF.Sqrt((planeX * planeX) + (planeY * planeY) + (planeZ * planeZ));
        
        // If they are closer than 75 units, ALWAYS render them even if they are behind
        if (distance < 75.0f) return true;

        // Normalize plane
        float inverseDistance = 1.0f / distance;
        planeX *= inverseDistance;
        planeY *= inverseDistance;
        planeZ *= inverseDistance;

        // Dot product
        float dot = (planeX * normalX) + (planeY * normalY) + (planeZ * normalZ);
        
        // FOV culling threshold is derived from Trace.FovDegrees.
        return dot > fovDotThreshold;
    }
}
