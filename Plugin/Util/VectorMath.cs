using System.Numerics;
using System.Runtime.CompilerServices;

namespace S2FOW.Util;

/// <summary>
/// Geometry math utilities used for smoke grenade calculations.
///
/// These methods answer spatial questions like:
///   - How far apart are two points? (DistanceSquared)
///   - How close does a point come to a line segment? (PointToSegmentDistanceSquared)
///   - Does a line pass through a sphere? (LineIntersectsSphere)
///
/// All distance methods return SQUARED distances to avoid expensive square root
/// operations. When you only need to compare distances (is A closer than B?),
/// comparing squared values gives the same result but is much faster.
/// </summary>
public static class VectorMath
{
    /// <summary>
    /// Returns the squared distance between two 3D points.
    /// Used to check if a smoke grenade is near a certain position.
    ///
    /// Example: If point A is at (100, 200, 50) and point B is at (103, 204, 50),
    /// the squared distance is 3² + 4² + 0² = 25. The actual distance would be √25 = 5,
    /// but we skip the square root for speed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DistanceSquared(float x1, float y1, float z1, float x2, float y2, float z2)
    {
        return Vector3.DistanceSquared(
            new Vector3(x1, y1, z1),
            new Vector3(x2, y2, z2));
    }

    /// <summary>
    /// Returns the squared distance from a point to the nearest spot on a line segment.
    ///
    /// Imagine drawing a straight line from "start" to "end" (like a laser beam).
    /// This method finds the closest point on that line to "point", then returns
    /// how far away "point" is from that closest spot (squared).
    ///
    /// Used by the smoke tracker to check if a smoke grenade is close to a line
    /// of sight between two players, even when the smoke is not directly on the line.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float PointToSegmentDistanceSquared(
        float pointX, float pointY, float pointZ,
        float startX, float startY, float startZ,
        float endX, float endY, float endZ)
    {
        Vector3 segment = new(endX - startX, endY - startY, endZ - startZ);
        float segmentLengthSquared = segment.LengthSquared();

        // If the segment has zero length (start == end), just measure point-to-point.
        if (segmentLengthSquared <= 1e-6f)
            return DistanceSquared(pointX, pointY, pointZ, startX, startY, startZ);

        // Project the point onto the line, clamped to [0, 1] so it stays on the segment.
        Vector3 pointOffset = new(pointX - startX, pointY - startY, pointZ - startZ);
        float t = Math.Clamp(Vector3.Dot(pointOffset, segment) / segmentLengthSquared, 0.0f, 1.0f);

        // Find the closest point on the segment.
        Vector3 closestPoint = new(startX, startY, startZ);
        closestPoint += segment * t;

        // Return the squared distance from the original point to the closest point on the segment.
        Vector3 point = new(pointX, pointY, pointZ);
        return Vector3.DistanceSquared(point, closestPoint);
    }

    /// <summary>
    /// Tests whether a line segment passes through a sphere.
    ///
    /// Imagine a smoke cloud as a ball (sphere) in the world. Now draw a straight
    /// line from the viewer's eyes to the enemy being checked. This method checks if that
    /// line passes through the smoke ball at any point along its length.
    ///
    /// Uses the quadratic formula to find where the line intersects the sphere's
    /// surface. If either intersection point falls within the segment [0, 1], or
    /// if the line enters before the segment starts and exits after it ends
    /// (meaning the segment is entirely inside the smoke), it returns true.
    /// </summary>
    public static bool LineIntersectsSphere(
        float startX, float startY, float startZ,
        float endX, float endY, float endZ,
        float cx, float cy, float cz, float radius)
    {
        // Direction vector of the line segment.
        float dx = endX - startX;
        float dy = endY - startY;
        float dz = endZ - startZ;

        // Vector from sphere center to the line's start point.
        float fx = startX - cx;
        float fy = startY - cy;
        float fz = startZ - cz;

        // Quadratic equation coefficients: at² + bt + c = 0
        float a = dx * dx + dy * dy + dz * dz;
        if (a < 1e-8f) return false; // The line has zero length (degenerate).

        float b = 2.0f * (fx * dx + fy * dy + fz * dz);
        float c = fx * fx + fy * fy + fz * fz - radius * radius;

        // If discriminant < 0, the line misses the sphere entirely.
        float discriminant = b * b - 4.0f * a * c;
        if (discriminant < 0) return false;

        // Find the two intersection points (t1 and t2) along the line.
        // t=0 is the start of the segment, t=1 is the end.
        float sqrtDisc = MathF.Sqrt(discriminant);
        float inv2a = 1.0f / (2.0f * a);
        float t1 = (-b - sqrtDisc) * inv2a;
        float t2 = (-b + sqrtDisc) * inv2a;

        // The line passes through the sphere if either intersection point is
        // within the segment [0, 1], or if the segment is entirely inside the sphere.
        return (t1 >= 0f && t1 <= 1f) || (t2 >= 0f && t2 <= 1f) || (t1 < 0f && t2 > 1f);
    }
}
