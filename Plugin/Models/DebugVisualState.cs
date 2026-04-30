using CounterStrikeSharp.API.Core;
using S2FOW.Core;

namespace S2FOW.Models;

/// <summary>
/// Holds the debug drawing objects for one viewer.
///
/// When debug mode is active, each human player gets their own beam objects that
/// show the body points, backup box corners, and backup box edges used by S2FOW.
/// This state tracks those beams so S2FOW can update, hide, or remove them.
/// </summary>
internal sealed class DebugVisualState
{
    /// <summary>
    /// Beam objects that mark visibility points as short vertical lines.
    /// White beams are detailed body points. Blue beams are backup box points.
    /// </summary>
    public readonly CEnvBeam?[] PointBeams = new CEnvBeam[RaycastEngine.MaxDebugPointsPerObserver];

    /// <summary>
    /// Beam objects that draw the 12 edges of each enemy's backup box.
    /// These form a wireframe box around the enemy being checked.
    /// </summary>
    public readonly CEnvBeam?[] AabbEdgeBeams = new CEnvBeam[FowConstants.MaxSlots * 12];

    /// <summary>How many point beams are currently visible in the world.</summary>
    public int ActivePointCount;

    /// <summary>How many backup-box edge beams are currently visible in the world.</summary>
    public int ActiveAabbEdgeCount;

    /// <summary>The next server tick when this viewer's debug drawing should update.</summary>
    public int NextVisualUpdateTick;
}
