using S2FOW.Config;
using S2FOW.Models;
namespace S2FOW.Core;

/// <summary>
/// Pure math for deciding where visibility checks start and end.
///
/// S2FOW checks visibility by drawing invisible straight lines from the viewer's
/// eyes toward points on the enemy. If a line reaches the enemy without hitting
/// a wall, the enemy is visible. If every checked line hits a wall, the enemy can
/// be hidden from that viewer.
///
/// This class computes two positions:
///
/// 1. Where the viewer's check starts.
///    It starts at the viewer's eyes, then may be nudged slightly forward or
///    sideways based on movement speed. This movement prediction helps prevent
///    enemies from popping in late when a fast-moving player rounds a corner.
///
/// 2. Where the enemy's body point is checked.
///    It starts at the enemy's body position, then may be nudged slightly based
///    on enemy movement. Airborne enemies get a small height adjustment, and
///    jumping/falling uses gravity so the prediction follows the visible model.
/// </summary>
internal static class RaycastMath
{
    /// <summary>
    /// Source 2 gravity constant: 800 units per second squared.
    /// Used for predicting where a jumping player will be in a few ticks.
    /// </summary>
    private const float GravityUnitsPerSecondSq = 800.0f;

    /// <summary>
    /// Small upward offset applied to airborne enemies.
    /// Without this, the check points can sit too close to the feet during jumps.
    /// </summary>
    private const float AirborneTargetVisualOffsetZ = 8.0f;

    // Direction calculations

    /// <summary>
    /// Converts a yaw angle, meaning the player's compass heading, into forward
    /// and right direction vectors.
    ///
    /// Yaw is measured in degrees: 0 = East, 90 = North, 180 = West, 270 = South.
    /// The forward direction is where the player faces. The right direction is
    /// 90 degrees clockwise from forward and is used for sideways movement.
    /// </summary>
    public static void GetYawBasis(
        float yawDegrees,
        out float forwardX,
        out float forwardY,
        out float rightX,
        out float rightY)
    {
        float yawRad = yawDegrees * (MathF.PI / 180.0f);
        forwardX = MathF.Cos(yawRad);
        forwardY = MathF.Sin(yawRad);
        rightX = MathF.Sin(yawRad);
        rightY = -MathF.Cos(yawRad);
    }

    /// <summary>
    /// Converts pitch and yaw angles into the 3D direction the player's crosshair
    /// is pointing.
    ///
    /// Pitch: -90 = looking straight up, 0 = looking horizontal, +90 = looking straight down.
    /// Yaw: compass heading as described above.
    /// </summary>
    public static void GetAimDirection(
        float pitchDegrees,
        float yawDegrees,
        out float directionX,
        out float directionY,
        out float directionZ)
    {
        float pitchRad = pitchDegrees * (MathF.PI / 180.0f);
        float yawRad = yawDegrees * (MathF.PI / 180.0f);
        float pitchCos = MathF.Cos(pitchRad);

        directionX = pitchCos * MathF.Cos(yawRad);
        directionY = pitchCos * MathF.Sin(yawRad);
        directionZ = -MathF.Sin(pitchRad);
    }

    // Viewer check start position

    /// <summary>
    /// Computes the viewer eye position used for wall checks, including movement
    /// prediction.
    ///
    /// S2FOW starts at the viewer's eyes, then nudges that start point forward and
    /// sideways based on movement speed. The nudge is capped so unusual high speed
    /// cannot create a large preview around corners.
    /// </summary>
    public static void ComputeObserverRayOrigin(
        in PlayerSnapshot observer,
        S2FOWConfig config,
        float tickInterval,
        out float originX,
        out float originY,
        out float originZ)
    {
        // Get the forward and right directions based on where the viewer is facing.
        GetYawBasis(observer.Yaw, out float forwardX, out float forwardY, out float rightX, out float rightY);

        float forwardLead = 0.0f;
        float horizontalLead = 0.0f;
        float verticalLead = 0.0f;
        float minLeadSpeedSqr = config.ViewerRays.StartAfterSpeed * config.ViewerRays.StartAfterSpeed;
        float observerSpeed2DSqr = observer.VelX * observer.VelX + observer.VelY * observer.VelY;
        float maxLeadUnits = config.ViewerRays.MaxMoveUnits;
        float weaponMaxSpeed = observer.WeaponMaxSpeed > 0.0f ? observer.WeaponMaxSpeed : 250.0f;

        // Only apply movement prediction if the player is moving fast enough.
        // Standing still or moving very slowly does not need prediction.
        if (observerSpeed2DSqr >= minLeadSpeedSqr)
        {
            // How fast is the viewer moving forward or backward?
            float forwardSpeed = observer.VelX * forwardX + observer.VelY * forwardY;
            forwardSpeed = Math.Clamp(forwardSpeed, -weaponMaxSpeed, weaponMaxSpeed);
            forwardLead = forwardSpeed * tickInterval * config.ViewerRays.ForwardLookAheadTicks;

            // How fast is the viewer moving sideways?
            float observedStrafeSpeed = observer.VelX * rightX + observer.VelY * rightY;
            observedStrafeSpeed = Math.Clamp(observedStrafeSpeed, -weaponMaxSpeed, weaponMaxSpeed);
            horizontalLead = observedStrafeSpeed * tickInterval * config.ViewerRays.SideLookAheadTicks;
        }

        // Combine forward and sideways movement into one horizontal offset.
        float leadX = forwardX * forwardLead + rightX * horizontalLead;
        float leadY = forwardY * forwardLead + rightY * horizontalLead;

        // Clamp the total lead distance so it never exceeds the configured maximum.
        if (maxLeadUnits > 0.0f)
        {
            float leadLenSqr = leadX * leadX + leadY * leadY;
            float maxLeadSqr = maxLeadUnits * maxLeadUnits;
            if (leadLenSqr > maxLeadSqr)
            {
                float scale = maxLeadUnits / MathF.Sqrt(leadLenSqr);
                leadX *= scale;
                leadY *= scale;
            }
        }

        // Final start point = eye position + horizontal prediction.
        originX = observer.EyePosX + leadX;
        originY = observer.EyePosY + leadY;

        // Add vertical prediction while the viewer is moving upward.
        if (observer.VelZ > 0.0f)
        {
            verticalLead = observer.VelZ * tickInterval * config.ViewerRays.JumpLookAheadTicks;
            if (maxLeadUnits > 0.0f && verticalLead > maxLeadUnits)
                verticalLead = maxLeadUnits;
        }

        originZ = observer.EyePosZ + verticalLead + config.Performance.ViewerHeightOffset;
    }

    /// <summary>
    /// Computes the viewer eye position without movement prediction.
    /// Used for reverse checks where prediction is less important.
    /// </summary>
    public static void ComputeObserverRayOriginNoPrediction(
        in PlayerSnapshot observer,
        S2FOWConfig config,
        out float originX,
        out float originY,
        out float originZ)
    {
        originX = observer.EyePosX;
        originY = observer.EyePosY;
        originZ = observer.EyePosZ + config.Performance.ViewerHeightOffset;
    }

    // Enemy check point position

    /// <summary>
    /// Computes where the enemy's body check points should be centered, including
    /// movement prediction and gravity.
    ///
    /// This nudges the enemy position slightly based on movement so fast-moving
    /// enemies do not outrun their own check points and become hidden while their
    /// model is still visible.
    /// </summary>
    public static void ComputeTargetLeadPosition(
        in PlayerSnapshot target,
        S2FOWConfig config,
        float tickInterval,
        out float posX,
        out float posY,
        out float posZ,
        out float forwardX,
        out float forwardY,
        out float rightX,
        out float rightY)
    {
        GetYawBasis(target.Yaw, out forwardX, out forwardY, out rightX, out rightY);

        // Compute horizontal lead (forward + sideways movement).
        float forwardSpeed = target.VelX * forwardX + target.VelY * forwardY;
        float observedStrafeSpeed = target.VelX * rightX + target.VelY * rightY;

        float pointLeadX =
            forwardX * (forwardSpeed * tickInterval * config.TargetPoints.ForwardLookAheadTicks) +
            rightX * (observedStrafeSpeed * tickInterval * config.TargetPoints.SideLookAheadTicks);
        float pointLeadY =
            forwardY * (forwardSpeed * tickInterval * config.TargetPoints.ForwardLookAheadTicks) +
            rightY * (observedStrafeSpeed * tickInterval * config.TargetPoints.SideLookAheadTicks);

        // Clamp horizontal prediction to the configured maximum.
        float maxTargetLeadUnits = config.TargetPoints.MaxMoveUnits;
        if (maxTargetLeadUnits > 0.0f)
        {
            float pointLeadLenSqr = pointLeadX * pointLeadX + pointLeadY * pointLeadY;
            float targetLeadMaxSqr = maxTargetLeadUnits * maxTargetLeadUnits;
            if (pointLeadLenSqr > targetLeadMaxSqr)
            {
                float scale = maxTargetLeadUnits / MathF.Sqrt(pointLeadLenSqr);
                pointLeadX *= scale;
                pointLeadY *= scale;
            }
        }

        // Compute vertical prediction with gravity modeling.
        float pointLeadZ = ComputeTargetVerticalLead(target, tickInterval, config);
        float maxVerticalLead = config.TargetPoints.MaxUpDownUnits;
        if (maxVerticalLead > 0.0f)
        {
            pointLeadZ = Math.Clamp(pointLeadZ, -maxVerticalLead, maxVerticalLead);
        }

        // Airborne enemies get a small upward offset to keep check points on the body.
        float visualOffsetZ = target.IsOnGround ? 0.0f : AirborneTargetVisualOffsetZ;

        // Final position = current enemy position + prediction offsets.
        posX = target.PosX + pointLeadX;
        posY = target.PosY + pointLeadY;
        posZ = target.PosZ + pointLeadZ + visualOffsetZ;
    }

    /// <summary>
    /// Computes vertical prediction for an enemy, modeling gravity.
    ///
    /// For a jumping player, this predicts a curved path: rise, slow down, then fall.
    /// For a grounded player not moving up, it returns 0. For a falling player, it
    /// predicts continued falling with gravity acceleration.
    /// </summary>
    private static float ComputeTargetVerticalLead(in PlayerSnapshot target, float tickInterval, S2FOWConfig config)
    {
        float lookaheadSeconds = tickInterval * config.TargetPoints.UpDownLookAheadTicks;
        if (lookaheadSeconds <= 0.0f)
            return 0.0f;

        // Grounded players that are not jumping should not be pulled downward.
        if (target.IsOnGround && target.VelZ <= 0.0f)
            return 0.0f;

        // Jumping: predict the curved path using velocity, time, and gravity.
        if (target.VelZ > 0.0f)
        {
            float timeToApex = target.VelZ / GravityUnitsPerSecondSq;
            float ascentSeconds = MathF.Min(lookaheadSeconds, timeToApex);
            return (target.VelZ * ascentSeconds) - (0.5f * GravityUnitsPerSecondSq * ascentSeconds * ascentSeconds);
        }

        // Falling: predict continued descent with gravity.
        return (target.VelZ * lookaheadSeconds) - (0.5f * GravityUnitsPerSecondSq * lookaheadSeconds * lookaheadSeconds);
    }
}
