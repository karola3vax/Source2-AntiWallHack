using System.Numerics;

namespace S2FOW.Models;

/// <summary>
/// Stores one debug line drawn for a visibility check during this frame.
/// The debug renderer shows these lines in the game world.
///
/// Yellow means the check reached the enemy point, so that point was visible.
/// Blue means the check hit a wall first, so that point was blocked.
/// </summary>
internal struct DebugRay
{
    /// <summary>Where the check started: the viewer's eye position.</summary>
    public Vector3 Start;

    /// <summary>Where the check ended: the enemy point or the wall it hit first.</summary>
    public Vector3 End;

    /// <summary>True if this check reached the enemy point without hitting a wall.</summary>
    public bool Visible;
}
