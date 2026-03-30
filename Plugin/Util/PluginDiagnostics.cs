using System.Threading;

namespace S2FOW.Util;

internal static class PluginDiagnostics
{
    private static long _configIoErrorCount;
    private static long _autoProfileProbeErrorCount;

    public static long ConfigIoErrorCount => Interlocked.Read(ref _configIoErrorCount);
    public static long AutoProfileProbeErrorCount => Interlocked.Read(ref _autoProfileProbeErrorCount);

    public static void RecordConfigIoError()
    {
        Interlocked.Increment(ref _configIoErrorCount);
    }

    public static void RecordAutoProfileProbeError()
    {
        Interlocked.Increment(ref _autoProfileProbeErrorCount);
    }
}
