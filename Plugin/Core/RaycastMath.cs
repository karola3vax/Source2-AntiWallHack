using S2FOW.Config;
using S2FOW.Models;
using Vector3 = System.Numerics.Vector3;

namespace S2FOW.Core;

internal static class RaycastMath
{
    private const float GravityUnitsPerSecondSq = 800.0f;
    private const float AirborneTargetVisualOffsetZ = 8.0f;

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

    public static void GetAimForwardVector(
        float pitchDegrees,
        float yawDegrees,
        out float forwardX,
        out float forwardY,
        out float forwardZ)
    {
        float pitchRad = pitchDegrees * (MathF.PI / 180.0f);
        float yawRad = yawDegrees * (MathF.PI / 180.0f);
        float cosPitch = MathF.Cos(pitchRad);

        forwardX = cosPitch * MathF.Cos(yawRad);
        forwardY = cosPitch * MathF.Sin(yawRad);
        forwardZ = -MathF.Sin(pitchRad);
    }

    internal static void ComputePlanarLeadFromBasis(
        float velX,
        float velY,
        float forwardX,
        float forwardY,
        float rightX,
        float rightY,
        float forwardLeadTicks,
        float horizontalLeadTicks,
        float tickInterval,
        out float leadX,
        out float leadY)
    {
        float forwardSpeed = velX * forwardX + velY * forwardY;
        float horizontalSpeed = velX * rightX + velY * rightY;

        float forwardLead = forwardSpeed * tickInterval * forwardLeadTicks;
        float horizontalLead = horizontalSpeed * tickInterval * horizontalLeadTicks;

        leadX = forwardX * forwardLead + rightX * horizontalLead;
        leadY = forwardY * forwardLead + rightY * horizontalLead;
    }

    public static void ComputeObserverRayOrigin(
        in PlayerSnapshot observer,
        S2FOWConfig config,
        float tickInterval,
        out float originX,
        out float originY,
        out float originZ)
    {
        GetYawBasis(observer.Yaw, out float forwardX, out float forwardY, out float rightX, out float rightY);

        float forwardLead = 0.0f;
        float horizontalLead = 0.0f;
        float verticalLead = 0.0f;
        float minLeadSpeedSqr = config.MovementPrediction.MinSpeed * config.MovementPrediction.MinSpeed;
        float observerSpeed2DSqr = observer.VelX * observer.VelX + observer.VelY * observer.VelY;
        float maxLeadUnits = config.MovementPrediction.ViewerMaxLeadDistance;
        float weaponMaxSpeed = observer.WeaponMaxSpeed > 0.0f ? observer.WeaponMaxSpeed : 250.0f;

        // Keep the speed threshold in one place so prediction stays easy to reason about.
        if (observerSpeed2DSqr >= minLeadSpeedSqr)
        {
            float forwardSpeed = observer.VelX * forwardX + observer.VelY * forwardY;
            forwardSpeed = Math.Clamp(forwardSpeed, -weaponMaxSpeed, weaponMaxSpeed);
            forwardLead = forwardSpeed * tickInterval * config.MovementPrediction.ViewerForwardLookaheadTicks;

            float observedStrafeSpeed = observer.VelX * rightX + observer.VelY * rightY;
            observedStrafeSpeed = Math.Clamp(observedStrafeSpeed, -weaponMaxSpeed, weaponMaxSpeed);
            horizontalLead = observedStrafeSpeed * tickInterval * config.MovementPrediction.ViewerStrafeAnticipationTicks;
        }

        float leadX = forwardX * forwardLead + rightX * horizontalLead;
        float leadY = forwardY * forwardLead + rightY * horizontalLead;

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

        originX = observer.EyePosX + leadX;
        originY = observer.EyePosY + leadY;

        // Clamp vertical lead so jump velocity cannot create excessive prediction.
        if (observer.VelZ > 0.0f)
        {
            verticalLead = observer.VelZ * tickInterval * config.MovementPrediction.ViewerJumpAnticipationTicks;
            if (maxLeadUnits > 0.0f && verticalLead > maxLeadUnits)
                verticalLead = maxLeadUnits;
        }

        originZ = observer.EyePosZ + verticalLead + config.Performance.ViewerHeightOffset;
    }

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

        float forwardSpeed = target.VelX * forwardX + target.VelY * forwardY;
        float observedStrafeSpeed = target.VelX * rightX + target.VelY * rightY;

        float pointLeadX =
            forwardX * (forwardSpeed * tickInterval * config.MovementPrediction.EnemyForwardLookaheadTicks) +
            rightX * (observedStrafeSpeed * tickInterval * config.MovementPrediction.EnemySidewaysLookaheadTicks);
        float pointLeadY =
            forwardY * (forwardSpeed * tickInterval * config.MovementPrediction.EnemyForwardLookaheadTicks) +
            rightY * (observedStrafeSpeed * tickInterval * config.MovementPrediction.EnemySidewaysLookaheadTicks);

        float maxTargetLeadUnits = config.MovementPrediction.EnemyMaxLeadDistance;
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

        float pointLeadZ = ComputeTargetVerticalLead(target, tickInterval, config);
        float maxVerticalLead = config.MovementPrediction.EnemyVerticalMaxLeadDistance;
        if (maxVerticalLead > 0.0f)
        {
            pointLeadZ = Math.Clamp(pointLeadZ, -maxVerticalLead, maxVerticalLead);
        }

        float visualOffsetZ = target.IsOnGround ? 0.0f : AirborneTargetVisualOffsetZ;

        posX = target.PosX + pointLeadX;
        posY = target.PosY + pointLeadY;
        posZ = target.PosZ + pointLeadZ + visualOffsetZ;
    }

    public static Vector3 ComputeTargetCenterPoint(in PlayerSnapshot target, S2FOWConfig config, float tickInterval)
    {
        ComputeTargetLeadPosition(
            in target,
            config,
            tickInterval,
            out float posX,
            out float posY,
            out float posZ,
            out _,
            out _,
            out _,
            out _);

        return new Vector3(
            posX,
            posY,
            posZ + (target.MaxsZ + target.MinsZ) * 0.5f);
    }

    private static float ComputeTargetVerticalLead(in PlayerSnapshot target, float tickInterval, S2FOWConfig config)
    {
        float lookaheadSeconds = tickInterval * config.MovementPrediction.EnemyVerticalLookaheadTicks;
        if (lookaheadSeconds <= 0.0f)
            return 0.0f;

        // Grounded targets should not be pulled downward by look-ahead.
        if (target.IsOnGround && target.VelZ <= 0.0f)
            return 0.0f;

        if (target.VelZ > 0.0f)
        {
            float timeToApex = target.VelZ / GravityUnitsPerSecondSq;
            float ascentSeconds = MathF.Min(lookaheadSeconds, timeToApex);
            return (target.VelZ * ascentSeconds) - (0.5f * GravityUnitsPerSecondSq * ascentSeconds * ascentSeconds);
        }

        return (target.VelZ * lookaheadSeconds) - (0.5f * GravityUnitsPerSecondSq * lookaheadSeconds * lookaheadSeconds);
    }
}
