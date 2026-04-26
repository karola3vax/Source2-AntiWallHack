using System.Numerics;

namespace S2FOW.Models;

/// <summary>
/// Stores a single debug ray that was cast during this frame.
/// Used by the debug renderer to visualize rays as colored lines in the game world.
///
/// Visible rays are shown in yellow (hit open air — target is visible).
/// Blocked rays are shown in blue (hit a wall — target is hidden behind it).
/// </summary>
internal struct DebugRay
{
    /// <summary>Where the ray started (the observer's eye position).</summary>
    public Vector3 Start;

    /// <summary>Where the ray ended (either the target point or where it hit a wall).</summary>
    public Vector3 End;

    /// <summary>True if this ray reached its target without hitting anything (target is visible).</summary>
    public bool Visible;
}
