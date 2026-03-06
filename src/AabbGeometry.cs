using CounterStrikeSharp.API.Modules.Utils;

namespace S2AWH;

internal static class AabbGeometry
{
    internal static void SetViewerOrigin(
        Vector origin,
        Vector baseEye)
    {
        origin.X = baseEye.X;
        origin.Y = baseEye.Y;
        origin.Z = baseEye.Z;
    }

}
