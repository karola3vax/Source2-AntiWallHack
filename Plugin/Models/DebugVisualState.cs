using CounterStrikeSharp.API.Core;
using S2FOW.Core;

namespace S2FOW.Models;

/// <summary>
/// Holds the debug visualization state for one observer (player).
///
/// When debug mode is active, each human player gets their own set of beam entities
/// that visualize the test points and bounding box edges used for visibility checks.
/// This state tracks those beams so we can update, hide, or remove them as needed.
/// </summary>
internal sealed class DebugVisualState
{
    /// <summary>
    /// Beam entities that mark each visibility test point as a short vertical line.
    /// White beams = skeleton hitbox points. Blue beams = AABB fallback points.
    /// </summary>
    public readonly CEnvBeam?[] PointBeams = new CEnvBeam[RaycastEngine.MaxDebugPointsPerObserver];

    /// <summary>
    /// Beam entities that draw the 12 edges of each target's axis-aligned bounding box.
    /// These form a wireframe box around the target player.
    /// </summary>
    public readonly CEnvBeam?[] AabbEdgeBeams = new CEnvBeam[FowConstants.MaxSlots * 12];

    /// <summary>How many point beams are currently active (visible in the world).</summary>
    public int ActivePointCount;

    /// <summary>How many AABB edge beams are currently active.</summary>
    public int ActiveAabbEdgeCount;

    /// <summary>The next server tick at which this observer's visuals should be updated.</summary>
    public int NextVisualUpdateTick;
}
