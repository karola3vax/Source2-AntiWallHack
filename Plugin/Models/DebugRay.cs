using System.Numerics;

namespace S2FOW.Models;

internal struct DebugRay
{
    public Vector3 Start;
    public Vector3 End;
    public bool Aim;
    public bool Visible;
    public bool Elevated;
}
