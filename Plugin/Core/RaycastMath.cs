using S2FOW.Config;
using S2FOW.Models;
namespace S2FOW.Core;

/// <summary>
/// Pure math for computing where to start and end raycast checks.
///
/// The anti-wallhack system works by shooting invisible "rays" (straight lines)
/// from a player's eyes toward each enemy. If a ray reaches the enemy without
/// hitting a wall, the enemy is visible. If every ray hits a wall, the enemy is hidden.
///
/// This class handles two key computations:
///
/// 1. Where does the observer's ray START?
///    - From the observer's eye position, slightly adjusted forward/sideways based
///      on their movement speed. This "movement prediction" accounts for the fact
///      that by the time the server's decision reaches the client, the observer
///      may have moved slightly. Without this, fast-moving players could see enemies
///      "pop in" as they come around corners.
///
/// 2. Where does the target's check point END?
///    - At the target's body position, again with slight movement prediction.
///    - Airborne targets get a small visual offset so the check points stay
///      centered on the visible model rather than the feet.
///    - Gravity is modeled for jumping targets so the prediction follows a
///      realistic arc rather than a straight line upward.
/// </summary>
internal static class RaycastMath
{
    /// <summary>
    /// Source 2 gravity constant: 800 units per second squared.
    /// Used for predicting where a jumping player will be in a few ticks.
    /// </summary>
    private const float GravityUnitsPerSecondSq = 800.0f;

    /// <summary>
    /// Small upward offset applied to airborne (jumping/falling) targets.
    /// Without this, the check points would be at the feet, which may be
    /// below the visible player model during jumps.
    /// </summary>
    private const float AirborneTargetVisualOffsetZ = 8.0f;

    // ────────────────────────────────────────────────────────────────────────
    //  Direction calculations
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a yaw angle (compass heading) into forward and right direction vectors.
    ///
    /// Yaw is measured in degrees: 0° = East, 90° = North, 180° = West, 270° = South.
    /// The "forward" direction is where the player is facing.
    /// The "right" direction is 90° clockwise from forward (used for strafing).
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
    /// Converts pitch and yaw angles into a 3D direction vector.
    /// This is the direction the player's crosshair is pointing.
    ///
    /// Pitch: -90° = looking straight up, 0° = looking horizontal, +90° = looking straight down.
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

    // ────────────────────────────────────────────────────────────────────────
    //  Observer ray origin (where the "eye" ray starts)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes the starting point of the observer's ray, with movement prediction.
    ///
    /// The ray starts at the observer's eye position, then gets nudged forward and
    /// sideways based on their movement speed. This "lead" ensures that a player
    /// running around a corner will see the enemy slightly before they actually
    /// arrive, preventing jarring pop-in.
    ///
    /// The lead distance is clamped to MaxMoveUnits to prevent abuse — a player
    /// moving at extreme speeds (e.g., from an explosion boost) should not get
    /// an unreasonably large "preview" window.
    /// </summary>
    public static void ComputeObserverRayOrigin(
        in PlayerSnapshot observer,
        S2FOWConfig config,
        float tickInterval,
        out float originX,
        out float originY,
        out float originZ)
    {
        // Get the forward and right directions based on where the observer is facing.
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
            // How fast is the player moving forward (positive) or backward (negative)?
            float forwardSpeed = observer.VelX * forwardX + observer.VelY * forwardY;
            forwardSpeed = Math.Clamp(forwardSpeed, -weaponMaxSpeed, weaponMaxSpeed);
            forwardLead = forwardSpeed * tickInterval * config.ViewerRays.ForwardLookAheadTicks;

            // How fast is the player strafing left or right?
            float observedStrafeSpeed = observer.VelX * rightX + observer.VelY * rightY;
            observedStrafeSpeed = Math.Clamp(observedStrafeSpeed, -weaponMaxSpeed, weaponMaxSpeed);
            horizontalLead = observedStrafeSpeed * tickInterval * config.ViewerRays.SideLookAheadTicks;
        }

        // Combine forward and strafe leads into a single horizontal offset.
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

        // Final origin = eye position + horizontal lead.
        originX = observer.EyePosX + leadX;
        originY = observer.EyePosY + leadY;

        // Apply vertical lead for jumping observers (looking up while moving up).
        if (observer.VelZ > 0.0f)
        {
            verticalLead = observer.VelZ * tickInterval * config.ViewerRays.JumpLookAheadTicks;
            if (maxLeadUnits > 0.0f && verticalLead > maxLeadUnits)
                verticalLead = maxLeadUnits;
        }

        originZ = observer.EyePosZ + verticalLead + config.Performance.ViewerHeightOffset;
    }

    /// <summary>
    /// Computes the observer's ray origin WITHOUT movement prediction.
    /// Used for the "reverse" check (can the target see the observer?)
    /// where prediction is less important.
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

    // ────────────────────────────────────────────────────────────────────────
    //  Target lead position (where the check points are centered)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes where the target's body check points should be centered,
    /// accounting for movement prediction and gravity.
    ///
    /// Like the observer prediction above, this nudges the target's position
    /// slightly forward based on their movement. This prevents the situation
    /// where a fast-moving target "outruns" the check points and becomes
    /// invisible even though their model is visible on screen.
    ///
    /// For jumping targets, we model gravity: the vertical prediction follows
    /// a parabolic arc (up, slow down, fall) rather than a straight line up.
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

        // Compute horizontal lead (forward + strafe).
        float forwardSpeed = target.VelX * forwardX + target.VelY * forwardY;
        float observedStrafeSpeed = target.VelX * rightX + target.VelY * rightY;

        float pointLeadX =
            forwardX * (forwardSpeed * tickInterval * config.TargetPoints.ForwardLookAheadTicks) +
            rightX * (observedStrafeSpeed * tickInterval * config.TargetPoints.SideLookAheadTicks);
        float pointLeadY =
            forwardY * (forwardSpeed * tickInterval * config.TargetPoints.ForwardLookAheadTicks) +
            rightY * (observedStrafeSpeed * tickInterval * config.TargetPoints.SideLookAheadTicks);

        // Clamp horizontal lead to the configured maximum.
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

        // Compute vertical lead with gravity modeling.
        float pointLeadZ = ComputeTargetVerticalLead(target, tickInterval, config);
        float maxVerticalLead = config.TargetPoints.MaxUpDownUnits;
        if (maxVerticalLead > 0.0f)
        {
            pointLeadZ = Math.Clamp(pointLeadZ, -maxVerticalLead, maxVerticalLead);
        }

        // Airborne targets get a small upward offset to keep check points on the body.
        float visualOffsetZ = target.IsOnGround ? 0.0f : AirborneTargetVisualOffsetZ;

        // Final position = base position + all offsets.
        posX = target.PosX + pointLeadX;
        posY = target.PosY + pointLeadY;
        posZ = target.PosZ + pointLeadZ + visualOffsetZ;
    }

    /// <summary>
    /// Computes vertical prediction for a target, modeling gravity.
    ///
    /// For a jumping player (moving upward), we predict along a parabolic arc:
    /// the player rises, slows down, and eventually starts falling.
    ///
    /// For a grounded player not moving up, we return 0 (no vertical prediction).
    /// For a falling player, we predict continued falling with gravity acceleration.
    /// </summary>
    private static float ComputeTargetVerticalLead(in PlayerSnapshot target, float tickInterval, S2FOWConfig config)
    {
        float lookaheadSeconds = tickInterval * config.TargetPoints.UpDownLookAheadTicks;
        if (lookaheadSeconds <= 0.0f)
            return 0.0f;

        // Grounded players that are not jumping should not be pulled downward.
        if (target.IsOnGround && target.VelZ <= 0.0f)
            return 0.0f;

        // Jumping: predict the parabolic arc (velocity × time − ½ × gravity × time²).
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
