using CounterStrikeSharp.API.Core;
using S2FOW.Core;
using System.Numerics;

namespace S2FOW.Models;

internal sealed class DebugVisualState
{
    public readonly CEnvBeam?[] PointBeams = new CEnvBeam[RaycastEngine.MaxDebugPointsPerObserver];
    public readonly CEnvBeam?[] LineBeams = new CEnvBeam[RaycastEngine.MaxDebugLinesPerObserver];
    public readonly Vector3[] LastPointCenters = new Vector3[RaycastEngine.MaxDebugPointsPerObserver];
    public readonly bool[] PointInitialized = new bool[RaycastEngine.MaxDebugPointsPerObserver];
    public int NextVisualUpdateTick;
}
