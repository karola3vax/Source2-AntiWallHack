using System.Runtime.InteropServices;
using S2FOW.Models;
using S2FOW.Util;

namespace S2FOW.Core;

public class SmokeTracker
{
    private readonly List<SmokeData> _activeSmokes = new(8);

    /// <summary>
    /// Tracks a newly detonated smoke.
    /// </summary>
    public void OnSmokeDetonate(float x, float y, float z, int tick, int smokeFormTicks)
    {
        _activeSmokes.Add(new SmokeData
        {
            X = x, Y = y, Z = z,
            DetonateTick = tick,
            FullFormationTick = tick + Math.Max(0, smokeFormTicks)
        });
    }

    /// <summary>
    /// Removes a smoke by position when the engine reports that it expired.
    /// A small tolerance is used to account for floating-point drift.
    /// </summary>
    public void OnSmokeExpired(float x, float y, float z)
    {
        const float toleranceSqr = 1.0f;
        for (int i = _activeSmokes.Count - 1; i >= 0; i--)
        {
            ref readonly var smoke = ref CollectionsMarshal.AsSpan(_activeSmokes)[i];
            if (VectorMath.DistanceSquared(smoke.X, smoke.Y, smoke.Z, x, y, z) <= toleranceSqr)
            {
                _activeSmokes.RemoveAt(i);
                return;
            }
        }
    }

    /// <summary>
    /// Removes smokes that have passed their blocking lifetime.
    /// Call once per frame before visibility checks.
    /// </summary>
    public void CullExpired(int currentTick, int smokeBlockDurationTicks)
    {
        for (int i = _activeSmokes.Count - 1; i >= 0; i--)
        {
            ref readonly var smoke = ref CollectionsMarshal.AsSpan(_activeSmokes)[i];
            if (currentTick >= GetCleanupTick(in smoke, smokeBlockDurationTicks))
                _activeSmokes.RemoveAt(i);
        }
    }

    public bool IsBlockedBySmoke(float fromX, float fromY, float fromZ,
        float toX, float toY, float toZ, float smokeRadius,
        int currentTick, int smokeFormTicks, int smokeBlockDurationTicks,
        float smokeMinRadiusFraction = 0.3f)
    {
        var span = CollectionsMarshal.AsSpan(_activeSmokes);
        for (int i = 0; i < span.Length; i++)
        {
            ref readonly var smoke = ref span[i];
            if (!IsVisuallyBlocking(in smoke, currentTick, smokeBlockDurationTicks))
                continue;

            // Grow the blocking radius during the bloom phase instead of snapping to full size.
            float effectiveRadius = ComputeEffectiveRadius(in smoke, currentTick, smokeRadius, smokeMinRadiusFraction);

            if (VectorMath.LineIntersectsSphere(
                    fromX, fromY, fromZ,
                    toX, toY, toZ,
                    smoke.X, smoke.Y, smoke.Z, effectiveRadius))
            {
                return true;
            }
        }
        return false;
    }

    private static float ComputeEffectiveRadius(in SmokeData smoke, int currentTick, float fullRadius, float minFraction)
    {
        if (minFraction >= 1.0f || currentTick >= smoke.FullFormationTick)
            return fullRadius;

        int bloomDuration = smoke.FullFormationTick - smoke.DetonateTick;
        if (bloomDuration <= 0)
            return fullRadius;

        int elapsed = currentTick - smoke.DetonateTick;
        if (elapsed <= 0)
            return fullRadius * minFraction;

        float progress = Math.Min(1.0f, (float)elapsed / bloomDuration);
        float fraction = minFraction + (1.0f - minFraction) * progress;
        return fullRadius * fraction;
    }

    private static bool IsVisuallyBlocking(in SmokeData smoke, int currentTick, int smokeBlockDurationTicks)
    {
        // Block from detonation using the growth curve. ComputeEffectiveRadius
        // returns a reduced radius during the bloom phase, growing from
        // SmokeGrowthStartFraction to full over the formation window.
        if (currentTick < smoke.DetonateTick)
            return false;

        int blockingEndTick = GetCleanupTick(in smoke, smokeBlockDurationTicks);
        return currentTick < blockingEndTick;
    }

    private static int GetCleanupTick(in SmokeData smoke, int smokeBlockDurationTicks)
    {
        return smoke.DetonateTick + Math.Max(0, smokeBlockDurationTicks);
    }

    public void Clear()
    {
        _activeSmokes.Clear();
    }

    public int ActiveCount => _activeSmokes.Count;

    /// <summary>
    /// Cheap pre-filter: tests whether the straight line from observer to target
    /// center passes close enough to ANY active smoke to potentially block visibility.
    /// Uses point-to-segment distance instead of full sphere-line intersection.
    ///
    /// If this returns false, no smoke can fully block the target and the expensive
    /// per-point smoke check can be skipped entirely.
    ///
    /// The margin parameter accounts for the spread of check points around the target
    /// center (weapon muzzle tip is ~50u from center, AABB corners ~16u padding).
    /// </summary>
    public bool IsLineNearAnySmoke(
        float fromX, float fromY, float fromZ,
        float toX, float toY, float toZ,
        float smokeRadius, float targetExtentMargin,
        int currentTick, int smokeBlockDurationTicks)
    {
        float extendedRadius = smokeRadius + targetExtentMargin;
        float extendedRadiusSqr = extendedRadius * extendedRadius;

        var span = CollectionsMarshal.AsSpan(_activeSmokes);
        for (int i = 0; i < span.Length; i++)
        {
            ref readonly var smoke = ref span[i];
            if (!IsVisuallyBlocking(in smoke, currentTick, smokeBlockDurationTicks))
                continue;

            if (VectorMath.PointToSegmentDistanceSquared(
                    smoke.X, smoke.Y, smoke.Z,
                    fromX, fromY, fromZ,
                    toX, toY, toZ) <= extendedRadiusSqr)
                return true;
        }
        return false;
    }
}
