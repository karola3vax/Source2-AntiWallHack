namespace S2FOW.Models;

/// <summary>
/// Stores the position and timing of a single active smoke grenade.
///
/// When a smoke detonates, we record where it landed and when. This lets us:
///   - Check if a line of sight passes through the smoke cloud.
///   - Model the smoke "blooming" (growing from small to full size over time).
///   - Automatically expire the smoke after its lifetime runs out.
/// </summary>
public struct SmokeData
{
    /// <summary>The world-space position where the smoke detonated (X, Y, Z coordinates).</summary>
    public float X, Y, Z;

    /// <summary>The server tick when the smoke detonated (started emitting smoke).</summary>
    public int DetonateTick;

    /// <summary>
    /// The tick at which the smoke reaches its full blocking radius.
    /// Before this tick, the smoke is still "blooming" — its effective radius
    /// is smaller than the full size, growing linearly from 50% to 100%.
    /// </summary>
    public int FullFormationTick;
}
