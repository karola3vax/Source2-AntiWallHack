using CounterStrikeSharp.API.Modules.Utils;

namespace S2AWH;

internal enum AabbFaceAxis : byte
{
    X = 0,
    Y = 1,
    Z = 2
}

internal static class AabbGeometry
{
    internal static readonly (float AxisFactor, float ZFactor)[] SurfaceProbePattern =
    {
        (-1.0f, 1.0f),
        (1.0f, -1.0f),
        (0.0f, 0.0f)
    };

    internal static readonly (float XFactor, float YFactor)[] CapProbePattern =
    {
        (-1.0f, 1.0f),
        (1.0f, -1.0f),
        (0.0f, 0.0f)
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

        float bestDistance = float.MaxValue;
        if (distanceToMinX < bestDistance)
        {
            bestDistance = distanceToMinX;
            closestX = minX;
        }

        if (distanceToMaxX < bestDistance)
        {
            bestDistance = distanceToMaxX;
            closestX = maxX;
        }

        if (distanceToMinY < bestDistance)
        {
            bestDistance = distanceToMinY;
            closestX = Math.Clamp(pointX, minX, maxX);
            closestY = minY;
            closestZ = Math.Clamp(pointZ, minZ, maxZ);
        }

        if (distanceToMaxY < bestDistance)
        {
            bestDistance = distanceToMaxY;
            closestX = Math.Clamp(pointX, minX, maxX);
            closestY = maxY;
            closestZ = Math.Clamp(pointZ, minZ, maxZ);
        }

        if (distanceToMinZ < bestDistance)
        {
            bestDistance = distanceToMinZ;
            closestX = Math.Clamp(pointX, minX, maxX);
            closestY = Math.Clamp(pointY, minY, maxY);
            closestZ = minZ;
        }

        if (distanceToMaxZ < bestDistance)
        {
            closestX = Math.Clamp(pointX, minX, maxX);
            closestY = Math.Clamp(pointY, minY, maxY);
            closestZ = maxZ;
        }
    }

    internal static void SetFacePoint(Vector point, float fixedAxisValue, float axis1, float axis2, AabbFaceAxis faceAxis)
    {
        switch (faceAxis)
        {
            case AabbFaceAxis.X:
                point.X = fixedAxisValue;
                point.Y = axis1;
                point.Z = axis2;
                break;
            case AabbFaceAxis.Y:
                point.X = axis1;
                point.Y = fixedAxisValue;
                point.Z = axis2;
                break;
            default:
                point.X = axis1;
                point.Y = axis2;
                point.Z = fixedAxisValue;
                break;
        }
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
}
