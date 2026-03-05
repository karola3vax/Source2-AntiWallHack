using CounterStrikeSharp.API.Modules.Utils;

namespace S2AWH;

internal static class AabbGeometry
{
    private const float ViewerShoulderLateralOffset = 14.0f;
    private const float ViewerShoulderVerticalDrop = 8.0f;
    private const float ViewerChestVerticalDrop = 14.0f;
    private const float ViewerShoulderLateralThreshold = 4.0f;
    private const float ViewerChestVerticalThreshold = -4.0f;

    internal static readonly (float AxisFactor, float ZFactor)[] SurfaceProbePattern =
    {
        (0.0f, 0.0f),
        (-0.5f, 0.5f),
        (0.5f, -0.5f)
    };

    internal static readonly (float XFactor, float YFactor)[] CapProbePattern =
    {
        (0.0f, 0.0f),
        (-0.5f, 1.0f),
        (1.0f, -0.5f)
    };

    internal static void GetClosestPointOnSurface(
        float pointX,
        float pointY,
        float pointZ,
        float minX,
        float minY,
        float minZ,
        float maxX,
        float maxY,
        float maxZ,
        out float closestX,
        out float closestY,
        out float closestZ)
    {
        closestX = Math.Clamp(pointX, minX, maxX);
        closestY = Math.Clamp(pointY, minY, maxY);
        closestZ = Math.Clamp(pointZ, minZ, maxZ);

        bool insideX = closestX > minX && closestX < maxX;
        bool insideY = closestY > minY && closestY < maxY;
        bool insideZ = closestZ > minZ && closestZ < maxZ;
        if (!insideX && !insideY && !insideZ)
        {
            return;
        }

        float distanceToMinX = MathF.Abs(pointX - minX);
        float distanceToMaxX = MathF.Abs(maxX - pointX);
        float distanceToMinY = MathF.Abs(pointY - minY);
        float distanceToMaxY = MathF.Abs(maxY - pointY);
        float distanceToMinZ = MathF.Abs(pointZ - minZ);
        float distanceToMaxZ = MathF.Abs(maxZ - pointZ);

        float bestDistance = distanceToMinX;
        int bestFace = 0; // 0=minX, 1=maxX, 2=minY, 3=maxY, 4=minZ, 5=maxZ

        if (distanceToMaxX < bestDistance)
        {
            bestDistance = distanceToMaxX;
            bestFace = 1;
        }

        if (distanceToMinY < bestDistance)
        {
            bestDistance = distanceToMinY;
            bestFace = 2;
        }

        if (distanceToMaxY < bestDistance)
        {
            bestDistance = distanceToMaxY;
            bestFace = 3;
        }

        if (distanceToMinZ < bestDistance)
        {
            bestDistance = distanceToMinZ;
            bestFace = 4;
        }

        if (distanceToMaxZ < bestDistance)
        {
            bestFace = 5;
        }

        // Project to the winning face, clamping the other two axes to stay on the surface.
        switch (bestFace)
        {
            case 0: // -X face
                closestX = minX;
                closestY = Math.Clamp(pointY, minY, maxY);
                closestZ = Math.Clamp(pointZ, minZ, maxZ);
                break;
            case 1: // +X face
                closestX = maxX;
                closestY = Math.Clamp(pointY, minY, maxY);
                closestZ = Math.Clamp(pointZ, minZ, maxZ);
                break;
            case 2: // -Y face
                closestX = Math.Clamp(pointX, minX, maxX);
                closestY = minY;
                closestZ = Math.Clamp(pointZ, minZ, maxZ);
                break;
            case 3: // +Y face
                closestX = Math.Clamp(pointX, minX, maxX);
                closestY = maxY;
                closestZ = Math.Clamp(pointZ, minZ, maxZ);
                break;
            case 4: // -Z face
                closestX = Math.Clamp(pointX, minX, maxX);
                closestY = Math.Clamp(pointY, minY, maxY);
                closestZ = minZ;
                break;
            default: // +Z face
                closestX = Math.Clamp(pointX, minX, maxX);
                closestY = Math.Clamp(pointY, minY, maxY);
                closestZ = maxZ;
                break;
        }
    }


    internal static void SetDistributedViewerOrigin(
        Vector origin,
        ref PlayerTransformSnapshot viewerSnapshot,
        Vector baseEye,
        float targetX,
        float targetY,
        float targetZ)
    {
        float verticalDelta = targetZ - baseEye.Z;
        if (verticalDelta <= ViewerChestVerticalThreshold)
        {
            origin.X = baseEye.X;
            origin.Y = baseEye.Y;
            origin.Z = baseEye.Z - ViewerChestVerticalDrop;
            return;
        }

        GetHorizontalRight(ref viewerSnapshot, out float rightX, out float rightY);
        float deltaX = targetX - baseEye.X;
        float deltaY = targetY - baseEye.Y;
        float lateral = (deltaX * rightX) + (deltaY * rightY);

        if (lateral >= ViewerShoulderLateralThreshold)
        {
            origin.X = baseEye.X + (rightX * ViewerShoulderLateralOffset);
            origin.Y = baseEye.Y + (rightY * ViewerShoulderLateralOffset);
            origin.Z = baseEye.Z - ViewerShoulderVerticalDrop;
            return;
        }

        if (lateral <= -ViewerShoulderLateralThreshold)
        {
            origin.X = baseEye.X - (rightX * ViewerShoulderLateralOffset);
            origin.Y = baseEye.Y - (rightY * ViewerShoulderLateralOffset);
            origin.Z = baseEye.Z - ViewerShoulderVerticalDrop;
            return;
        }

        origin.X = baseEye.X;
        origin.Y = baseEye.Y;
        origin.Z = baseEye.Z;
    }

    internal static int FillSurfaceProbePoints(
        Vector[] pointBuffer,
        int probeRows,
        float centerX,
        float centerY,
        float centerZ,
        float halfX,
        float halfY,
        float halfZ)
    {
        int rowCount = Math.Clamp(probeRows, 1, SurfaceProbePattern.Length);
        int pointIndex = 0;
        for (int row = 0; row < rowCount; row++)
        {
            float axisFactor = SurfaceProbePattern[row].AxisFactor;
            float zFactor = SurfaceProbePattern[row].ZFactor;
            float capXFactor = CapProbePattern[row].XFactor;
            float capYFactor = CapProbePattern[row].YFactor;
            float z = centerZ + (halfZ * zFactor);
            float y = centerY + (halfY * axisFactor);
            float x = centerX + (halfX * axisFactor);

            SetPoint(pointBuffer[pointIndex++], centerX + halfX, y, z);
            SetPoint(pointBuffer[pointIndex++], centerX - halfX, y, z);
            SetPoint(pointBuffer[pointIndex++], x, centerY + halfY, z);
            SetPoint(pointBuffer[pointIndex++], x, centerY - halfY, z);
            SetPoint(pointBuffer[pointIndex++], centerX + (halfX * capXFactor), centerY + (halfY * capYFactor), centerZ + halfZ);
            SetPoint(pointBuffer[pointIndex++], centerX + (halfX * capXFactor), centerY + (halfY * capYFactor), centerZ - halfZ);
        }

        return pointIndex;
    }

    private static void SetPoint(Vector point, float x, float y, float z)
    {
        point.X = x;
        point.Y = y;
        point.Z = z;
    }

    private static void GetHorizontalRight(
        ref PlayerTransformSnapshot viewerSnapshot,
        out float rightX,
        out float rightY)
    {
        float forwardX = viewerSnapshot.FovNormalX;
        float forwardY = viewerSnapshot.FovNormalY;
        float horizontalLengthSq = (forwardX * forwardX) + (forwardY * forwardY);
        if (horizontalLengthSq <= 0.0001f)
        {
            rightX = 0.0f;
            rightY = 1.0f;
            return;
        }

        float inverseHorizontalLength = 1.0f / MathF.Sqrt(horizontalLengthSq);
        forwardX *= inverseHorizontalLength;
        forwardY *= inverseHorizontalLength;
        rightX = -forwardY;
        rightY = forwardX;
    }
}
