using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using RayTraceAPI;

namespace S2AWH;

internal static class VisibilityGeometry
{
    public const int MaxTracePoints = 10;
    // LOS needs AABB padding because the player visual model (arms, weapon,
    // shoulders) extends beyond the tight collision box, AND because rays need
    // to approach narrow gaps/slits at diverse enough angles to thread through.
    // 1.5x horizontal provides sufficient angle diversity without wall leaking.
    private const float LosHorizontalPadding = 1.5f;
    private const float LosVerticalPadding = 1.25f;
    private static readonly Vector DefaultViewOffset = new(0, 0, 64);
    private static readonly QAngle BeamRotationZero = new(0.0f, 0.0f, 0.0f);
    private static readonly Vector BeamVelocityZero = new(0.0f, 0.0f, 0.0f);
    private static readonly Color HumanDebugBeamColor = Color.FromArgb(255, 64, 160, 255);
    private static readonly Color BotDebugBeamColor = Color.FromArgb(255, 80, 220, 80);
    private const float DebugBeamWidth = 1.5f;
    private const float DebugBeamLifetimeSeconds = 0.08f;
    // LOS should be blocked by world geometry, not by other player models standing in front.
    private static readonly TraceOptions VisibilityTraceOptions = new(
        (InteractionLayers)0,
        InteractionLayers.MASK_WORLD_ONLY
    );

    public static TraceOptions GetVisibilityTraceOptions()
    {
        return VisibilityTraceOptions;
    }

    public static bool ShouldDrawDebugTraceBeam(bool viewerIsBot)
    {
        var diagnostics = S2AWHState.Current.Diagnostics;
        if (!diagnostics.DrawDebugTraceBeams)
        {
            return false;
        }

        return viewerIsBot
            ? diagnostics.DrawDebugTraceBeamsForBots
            : diagnostics.DrawDebugTraceBeamsForHumans;
    }

    public static void DrawDebugTraceBeam(Vector start, Vector intendedEnd, in TraceResult traceResult, bool viewerIsBot)
    {
        CBeam? beam = Utilities.CreateEntityByName<CBeam>("env_beam");
        if (beam == null || !beam.IsValid)
        {
            return;
        }

        beam.Render = viewerIsBot ? BotDebugBeamColor : HumanDebugBeamColor;
        beam.Width = DebugBeamWidth;
        beam.RenderMode = RenderMode_t.kRenderNormal;
        beam.RenderFX = RenderFx_t.kRenderFxNone;

        beam.Teleport(start, BeamRotationZero, BeamVelocityZero);

        if (traceResult.DidHit)
        {
            beam.EndPos.X = traceResult.EndPosX;
            beam.EndPos.Y = traceResult.EndPosY;
            beam.EndPos.Z = traceResult.EndPosZ;
        }
        else
        {
            beam.EndPos.X = intendedEnd.X;
            beam.EndPos.Y = intendedEnd.Y;
            beam.EndPos.Z = intendedEnd.Z;
        }

        beam.DispatchSpawn();
        beam.AddEntityIOEvent("Kill", beam, beam, delay: DebugBeamLifetimeSeconds);
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);

    private static float GetSpeed(Vector? velocity)
    {
        if (velocity == null)
        {
            return 0.0f;
        }

        return MathF.Sqrt(
            (velocity.X * velocity.X) +
            (velocity.Y * velocity.Y) +
            (velocity.Z * velocity.Z)
        );
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

    private static bool TryGetMovementDirection(
        Vector? velocity,
        out float directionX,
        out float directionY,
        out float directionZ)
    {
        directionX = 0.0f;
        directionY = 0.0f;
        directionZ = 0.0f;
        if (velocity == null)
        {
            return false;
        }

        float horizontalLengthSquared = (velocity.X * velocity.X) + (velocity.Y * velocity.Y);
        if (horizontalLengthSquared > 0.0001f)
        {
            float invLength = 1.0f / MathF.Sqrt(horizontalLengthSquared);
            directionX = velocity.X * invLength;
            directionY = velocity.Y * invLength;
            return true;
        }

        float fullLengthSquared = horizontalLengthSquared + (velocity.Z * velocity.Z);
        if (fullLengthSquared > 0.0001f)
        {
            float invLength = 1.0f / MathF.Sqrt(fullLengthSquared);
            directionX = velocity.X * invLength;
            directionY = velocity.Y * invLength;
            directionZ = velocity.Z * invLength;
            return true;
        }

        return false;
    }

    public static Vector[] CreatePointBuffer()
    {
        Vector[] buffer = new Vector[MaxTracePoints];
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

    public static bool TryFillEyePosition(CBasePlayerPawn pawn, Vector eyePosition)
    {
        var origin = pawn.AbsOrigin;
        if (origin == null)
        {
            return false;
        }

        var viewOffset = pawn.ViewOffset;
        if (viewOffset != null)
        {
            eyePosition.X = origin.X + viewOffset.X;
            eyePosition.Y = origin.Y + viewOffset.Y;
            eyePosition.Z = origin.Z + viewOffset.Z;
            return true;
        }

        eyePosition.X = origin.X + DefaultViewOffset.X;
        eyePosition.Y = origin.Y + DefaultViewOffset.Y;
        eyePosition.Z = origin.Z + DefaultViewOffset.Z;
        return true;
    }

    public static int FillTargetPoints(
        CBasePlayerPawn pawn,
        Vector[] pointBuffer,
        Vector? originOverride = null,
        bool isPredictorPath = false,
        bool applyConfiguredLimit = true)
    {
        if (pointBuffer.Length < MaxTracePoints)
        {
            throw new ArgumentException($"pointBuffer length must be at least {MaxTracePoints}.", nameof(pointBuffer));
        }

        var config = S2AWHState.Current;
        Vector? origin = originOverride ?? pawn.AbsOrigin;

        if (origin == null)
        {
            return 0;
        }

        int pointCount = 0;
        var viewOffset = pawn.ViewOffset;
        float viewOffsetX = viewOffset?.X ?? DefaultViewOffset.X;
        float viewOffsetY = viewOffset?.Y ?? DefaultViewOffset.Y;
        float viewOffsetZ = viewOffset?.Z ?? DefaultViewOffset.Z;

        SetPoint(pointBuffer, pointCount++, origin.X + viewOffsetX, origin.Y + viewOffsetY, origin.Z + viewOffsetZ);

        var collision = pawn.Collision;
        if (collision != null && collision.Mins != null && collision.Maxs != null)
        {
            var mins = collision.Mins;
            var maxs = collision.Maxs;
            float horizontalScale = isPredictorPath ? 1.0f : LosHorizontalPadding;
            float verticalScale = isPredictorPath ? 1.0f : LosVerticalPadding;

            float centerX = (mins.X + maxs.X) * 0.5f;
            float centerY = (mins.Y + maxs.Y) * 0.5f;
            float centerZ = (mins.Z + maxs.Z) * 0.5f;

            if (isPredictorPath)
            {
                var velocity = pawn.AbsVelocity;
                float speed = GetSpeed(velocity);
                float alpha = GetProfileAlpha(speed, config);

                float profileHorizontalMultiplier = Lerp(1.0f, config.Aabb.ProfileHorizontalMaxMultiplier, alpha);
                float profileVerticalMultiplier = Lerp(1.0f, config.Aabb.ProfileVerticalMaxMultiplier, alpha);
                horizontalScale = config.Aabb.HorizontalScale * profileHorizontalMultiplier;
                verticalScale = config.Aabb.VerticalScale * profileVerticalMultiplier;

                if (config.Aabb.EnableDirectionalShift &&
                    alpha > 0.0f &&
                    TryGetMovementDirection(velocity, out float movementDirX, out float movementDirY, out float movementDirZ))
                {
                    float shiftUnits = config.Aabb.DirectionalForwardShiftMaxUnits * alpha;
                    shiftUnits *= config.Aabb.DirectionalPredictorShiftFactor;

                    centerX += movementDirX * shiftUnits;
                    centerY += movementDirY * shiftUnits;
                    centerZ += movementDirZ * shiftUnits;
                }
            }

            float halfX = (maxs.X - mins.X) * 0.5f * horizontalScale;
            float halfY = (maxs.Y - mins.Y) * 0.5f * horizontalScale;
            float halfZ = (maxs.Z - mins.Z) * 0.5f * verticalScale;

            float expandedMinX = centerX - halfX;
            float expandedMaxX = centerX + halfX;
            float expandedMinY = centerY - halfY;
            float expandedMaxY = centerY + halfY;
            float expandedMinZ = centerZ - halfZ;
            float expandedMaxZ = centerZ + halfZ;

            SetPoint(pointBuffer, pointCount++, origin.X + centerX, origin.Y + centerY, origin.Z + centerZ);

            if (isPredictorPath)
            {
                SetPoint(pointBuffer, pointCount++, origin.X + expandedMinX, origin.Y + expandedMinY, origin.Z + expandedMinZ);
                SetPoint(pointBuffer, pointCount++, origin.X + expandedMaxX, origin.Y + expandedMinY, origin.Z + expandedMinZ);
                SetPoint(pointBuffer, pointCount++, origin.X + expandedMinX, origin.Y + expandedMaxY, origin.Z + expandedMinZ);
                SetPoint(pointBuffer, pointCount++, origin.X + expandedMaxX, origin.Y + expandedMaxY, origin.Z + expandedMinZ);

                SetPoint(pointBuffer, pointCount++, origin.X + expandedMinX, origin.Y + expandedMinY, origin.Z + expandedMaxZ);
                SetPoint(pointBuffer, pointCount++, origin.X + expandedMaxX, origin.Y + expandedMinY, origin.Z + expandedMaxZ);
                SetPoint(pointBuffer, pointCount++, origin.X + expandedMinX, origin.Y + expandedMaxY, origin.Z + expandedMaxZ);
                SetPoint(pointBuffer, pointCount++, origin.X + expandedMaxX, origin.Y + expandedMaxY, origin.Z + expandedMaxZ);
            }
            else
            {
                // LOS path: use full 8-corner AABB sampling to cover diagonal/oblique angles.
                // Z levels are spread wide apart (75% below / 85% above center) so that rays
                // approach from maximally different angles. This is critical for narrow gaps
                // where only one specific angle can thread through the opening.
                float lowerZ = Math.Clamp(centerZ - (halfZ * 0.75f), expandedMinZ, expandedMaxZ);
                float upperZ = Math.Clamp(centerZ + (halfZ * 0.85f), expandedMinZ, expandedMaxZ);

                // Lower ring (4 corners)
                SetPoint(pointBuffer, pointCount++, origin.X + centerX + halfX, origin.Y + centerY + halfY, origin.Z + lowerZ);
                SetPoint(pointBuffer, pointCount++, origin.X + centerX - halfX, origin.Y + centerY + halfY, origin.Z + lowerZ);
                SetPoint(pointBuffer, pointCount++, origin.X + centerX + halfX, origin.Y + centerY - halfY, origin.Z + lowerZ);
                SetPoint(pointBuffer, pointCount++, origin.X + centerX - halfX, origin.Y + centerY - halfY, origin.Z + lowerZ);

                // Upper ring (4 corners)
                SetPoint(pointBuffer, pointCount++, origin.X + centerX + halfX, origin.Y + centerY + halfY, origin.Z + upperZ);
                SetPoint(pointBuffer, pointCount++, origin.X + centerX - halfX, origin.Y + centerY + halfY, origin.Z + upperZ);
                SetPoint(pointBuffer, pointCount++, origin.X + centerX + halfX, origin.Y + centerY - halfY, origin.Z + upperZ);
                SetPoint(pointBuffer, pointCount++, origin.X + centerX - halfX, origin.Y + centerY - halfY, origin.Z + upperZ);
            }
        }
        else
        {
            SetPoint(pointBuffer, pointCount++, origin.X, origin.Y, origin.Z + (viewOffsetZ / 2));
        }

        if (!applyConfiguredLimit)
        {
            return pointCount;
        }

        int configuredPointCount = Math.Clamp(config.Trace.RayTracePoints, 1, MaxTracePoints);
        return Math.Min(pointCount, configuredPointCount);
    }
}
