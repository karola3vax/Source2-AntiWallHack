using System.Numerics;
using System.Runtime.CompilerServices;
using S2FOW.Models;

namespace S2FOW.Util;

public static class VectorMath
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DistanceSquared(in PlayerSnapshot a, in PlayerSnapshot b)
    {
        return DistanceSquared(
            a.EyePosX, a.EyePosY, a.EyePosZ,
            b.PosX, b.PosY, b.PosZ);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DistanceSquared(float x1, float y1, float z1, float x2, float y2, float z2)
    {
        return Vector3.DistanceSquared(
            new Vector3(x1, y1, z1),
            new Vector3(x2, y2, z2));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DistanceSquared(in Vector3 a, in Vector3 b)
    {
        return Vector3.DistanceSquared(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float PointToSegmentDistanceSquared(
        float pointX, float pointY, float pointZ,
        float startX, float startY, float startZ,
        float endX, float endY, float endZ)
    {
        Vector3 segment = new(endX - startX, endY - startY, endZ - startZ);
        float segmentLengthSquared = segment.LengthSquared();
        if (segmentLengthSquared <= 1e-6f)
            return DistanceSquared(pointX, pointY, pointZ, startX, startY, startZ);

        Vector3 pointOffset = new(pointX - startX, pointY - startY, pointZ - startZ);
        float t = Math.Clamp(Vector3.Dot(pointOffset, segment) / segmentLengthSquared, 0.0f, 1.0f);
        Vector3 closestPoint = new(startX, startY, startZ);
        closestPoint += segment * t;
        Vector3 point = new(pointX, pointY, pointZ);
        return Vector3.DistanceSquared(point, closestPoint);
    }

    /// <summary>
    /// Line-sphere intersection test. Returns true if the line segment from
    /// (startX,startY,startZ) to (endX,endY,endZ) passes through the sphere
    /// at (cx,cy,cz) with the given radius.
    /// </summary>
    public static bool LineIntersectsSphere(
        float startX, float startY, float startZ,
        float endX, float endY, float endZ,
        float cx, float cy, float cz, float radius)
    {
        float dx = endX - startX;
        float dy = endY - startY;
        float dz = endZ - startZ;
        float fx = startX - cx;
        float fy = startY - cy;
        float fz = startZ - cz;

        float a = dx * dx + dy * dy + dz * dz;
        if (a < 1e-8f) return false; // degenerate segment

        float b = 2.0f * (fx * dx + fy * dy + fz * dz);
        float c = fx * fx + fy * fy + fz * fz - radius * radius;

        float discriminant = b * b - 4.0f * a * c;
        if (discriminant < 0) return false;

        float sqrtDisc = MathF.Sqrt(discriminant);
        float inv2a = 1.0f / (2.0f * a);
        float t1 = (-b - sqrtDisc) * inv2a;
        float t2 = (-b + sqrtDisc) * inv2a;

        // Check if intersection is within segment [0, 1]
        return (t1 >= 0f && t1 <= 1f) || (t2 >= 0f && t2 <= 1f) || (t1 < 0f && t2 > 1f);
    }
}
