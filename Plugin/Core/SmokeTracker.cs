using System.Runtime.InteropServices;
using S2FOW.Models;
using S2FOW.Util;

namespace S2FOW.Core;

/// <summary>
/// Tracks active smoke grenades and checks whether they block line of sight.
///
/// In CS2, smoke grenades create opaque clouds that block vision. This class
/// models each smoke as a sphere in the game world and checks whether the
/// straight line between an observer and a target passes through any of them.
///
/// Key behaviors:
///   - Smoke "bloom": When a smoke first detonates, it starts small and grows
///     to full size over a configurable period. During this growth phase, the
///     blocking radius is proportionally smaller.
///   - Smoke expiry: Smokes are removed when the engine reports them as expired,
///     OR when their configured blocking duration runs out.
///   - Pre-filtering: Before doing expensive per-ray smoke checks, we do a cheap
///     "is the line even near any smoke?" test to skip work when no smokes are nearby.
/// </summary>
public class SmokeTracker
{
    /// <summary>The list of currently active (blocking) smoke grenades.</summary>
    private readonly List<SmokeData> _activeSmokes = new(8);

    // ────────────────────────────────────────────────────────────────────────
    //  Smoke lifecycle
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records a newly detonated smoke grenade at the given world position.
    /// The smoke starts blocking immediately but at a reduced radius, growing
    /// to full size over 'smokeFormTicks' ticks.
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
    /// Removes a smoke when the engine says it has expired (dissipated).
    /// Matches by position with a small tolerance to handle floating-point drift.
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
    /// Removes smokes that have exceeded their maximum blocking duration.
    /// This is a safety net — normally the engine sends an "expired" event,
    /// but if that event is lost or delayed, this prevents stale smokes
    /// from blocking vision forever.
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

    // ────────────────────────────────────────────────────────────────────────
    //  Visibility blocking checks
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks whether any active smoke blocks the straight line from point A to point B.
    ///
    /// For each active smoke, we model it as a sphere with a radius that grows during
    /// the "bloom" phase. If the line from A to B passes through any smoke sphere,
    /// this returns true (the line of sight is blocked by smoke).
    ///
    /// smokeMinRadiusFraction comes from config (SmokeGrowthStartFraction, default 0.50).
    /// It is always passed explicitly — there is no default.
    /// </summary>
    public bool IsBlockedBySmoke(float fromX, float fromY, float fromZ,
        float toX, float toY, float toZ, float smokeRadius,
        int currentTick, int smokeFormTicks, int smokeBlockDurationTicks,
        float smokeMinRadiusFraction)
    {
        var span = CollectionsMarshal.AsSpan(_activeSmokes);
        for (int i = 0; i < span.Length; i++)
        {
            ref readonly var smoke = ref span[i];

            // Skip smokes that are not currently blocking (too early or expired).
            if (!IsVisuallyBlocking(in smoke, currentTick, smokeBlockDurationTicks))
                continue;

            // During bloom, the smoke's blocking radius is smaller than the full radius.
            float effectiveRadius = ComputeEffectiveRadius(in smoke, currentTick, smokeRadius, smokeMinRadiusFraction);

            // Does the line pass through this smoke sphere?
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

    /// <summary>
    /// Computes the effective blocking radius of a smoke during its bloom phase.
    ///
    /// When a smoke first detonates, it starts at 'minFraction' of its full radius
    /// (default 30%) and linearly grows to 100% over the bloom duration.
    /// After the bloom is complete, it always returns the full radius.
    /// </summary>
    private static float ComputeEffectiveRadius(in SmokeData smoke, int currentTick, float fullRadius, float minFraction)
    {
        // If min fraction is >= 1.0, there is no bloom — always use full radius.
        if (minFraction >= 1.0f || currentTick >= smoke.FullFormationTick)
            return fullRadius;

        int bloomDuration = smoke.FullFormationTick - smoke.DetonateTick;
        if (bloomDuration <= 0)
            return fullRadius;

        int elapsed = currentTick - smoke.DetonateTick;
        if (elapsed <= 0)
            return fullRadius * minFraction;

        // Linear interpolation from minFraction to 1.0 over the bloom duration.
        float progress = Math.Min(1.0f, (float)elapsed / bloomDuration);
        float fraction = minFraction + (1.0f - minFraction) * progress;
        return fullRadius * fraction;
    }

    /// <summary>
    /// Returns true if the smoke is currently in its "visually blocking" phase —
    /// after detonation and before its blocking lifetime expires.
    /// </summary>
    private static bool IsVisuallyBlocking(in SmokeData smoke, int currentTick, int smokeBlockDurationTicks)
    {
        if (currentTick < smoke.DetonateTick)
            return false;

        int blockingEndTick = GetCleanupTick(in smoke, smokeBlockDurationTicks);
        return currentTick < blockingEndTick;
    }

    /// <summary>Returns the tick at which this smoke should be fully removed.</summary>
    private static int GetCleanupTick(in SmokeData smoke, int smokeBlockDurationTicks)
    {
        return smoke.DetonateTick + Math.Max(0, smokeBlockDurationTicks);
    }

    /// <summary>Removes all tracked smokes. Called on map changes.</summary>
    public void Clear()
    {
        _activeSmokes.Clear();
    }

    /// <summary>How many smokes are currently active and blocking vision.</summary>
    public int ActiveCount => _activeSmokes.Count;

    // ────────────────────────────────────────────────────────────────────────
    //  Pre-filter (performance optimization)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Quick pre-check: is the line from A to B even close to any active smoke?
    ///
    /// This is much cheaper than the full sphere-intersection test because it uses
    /// point-to-segment distance (simpler math). If no smoke is near the line,
    /// we can skip all per-ray smoke checks for this observer-target pair.
    ///
    /// The targetExtentMargin accounts for the fact that check points are spread
    /// around the target center — a smoke near the center might still block a point
    /// at the edge of the body.
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
