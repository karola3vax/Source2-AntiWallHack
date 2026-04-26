using System.Threading;

namespace S2FOW.Util;

/// <summary>
/// Thread-safe diagnostic counters for tracking rare internal errors.
///
/// Some operations (like writing config files) can fail due to file system issues.
/// Instead of crashing the plugin, we silently catch those errors and increment a
/// counter. Server operators can then check these counters via css_fow_stats to
/// see if something is consistently failing.
///
/// Uses Interlocked operations so the counters are safe to read/write from any thread,
/// even though the plugin currently runs single-threaded. This is defensive design.
/// </summary>
internal static class PluginDiagnostics
{
    /// <summary>How many times writing/reading the config file failed.</summary>
    private static long _configIoErrorCount;

    /// <summary>Returns the current count of config I/O errors (thread-safe read).</summary>
    public static long ConfigIoErrorCount => Interlocked.Read(ref _configIoErrorCount);

    /// <summary>Increments the config I/O error counter by one (thread-safe write).</summary>
    public static void RecordConfigIoError()
    {
        Interlocked.Increment(ref _configIoErrorCount);
    }
}
