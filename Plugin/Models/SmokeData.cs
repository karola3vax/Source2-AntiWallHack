namespace S2FOW.Models;

public struct SmokeData
{
    public float X, Y, Z;
    public int DetonateTick;

    /// <summary>
    /// Tick at which the smoke reaches its full blocking radius.
    /// Before this tick, S2FOW treats the smoke as still blooming.
    /// </summary>
    public int FullFormationTick;
}
