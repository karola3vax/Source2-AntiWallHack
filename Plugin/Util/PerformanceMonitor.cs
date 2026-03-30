using System.Diagnostics;

namespace S2FOW.Util;

public class PerformanceMonitor
{
    private readonly Stopwatch _sw = new();
    private long _lastBeginFrameTimestamp;
    private long _totalMicroseconds;
    private long _totalFrameIntervalMicroseconds;
    private int _frameCount;
    private long _totalRaycasts;
    private long _totalCacheHits;
    private long _totalEvaluations;
    private long _totalBudgetExceeded;
    private long _totalVelocityCacheExtensions;
    private long _totalSmokePreFilterSkips;
    private long _peakRaycastsPerFrame;
    private long _minRaycastsPerFrame = long.MaxValue;

    // Rolling averages for the current runtime session.
    public double AvgFrameMicroseconds => _frameCount > 0 ? (double)_totalMicroseconds / _frameCount : 0;
    public double AvgRaycastsPerFrame => _frameCount > 0 ? (double)_totalRaycasts / _frameCount : 0;
    public double CacheHitRate => _totalEvaluations > 0 ? (double)_totalCacheHits / _totalEvaluations * 100 : 0;
    public double AvgBudgetExceededPerFrame => _frameCount > 0 ? (double)_totalBudgetExceeded / _frameCount : 0;
    public double VelocityExtensionRate => _totalEvaluations > 0 ? (double)_totalVelocityCacheExtensions / _totalEvaluations * 100 : 0;
    public long PeakRaycastsPerFrame => _peakRaycastsPerFrame;
    public long MinRaycastsPerFrame => _frameCount > 0 ? _minRaycastsPerFrame : 0;
    public long TotalSmokePreFilterSkips => _totalSmokePreFilterSkips;
    public float LastFrameMilliseconds { get; private set; }
    public float LastFrameIntervalMilliseconds { get; private set; }
    public double AvgFrameIntervalMicroseconds => _frameCount > 0 ? (double)_totalFrameIntervalMicroseconds / _frameCount : 0;
    public int TotalFrames => _frameCount;
    public long TotalRaycasts => _totalRaycasts;
    public long TotalCacheHits => _totalCacheHits;
    public long TotalEvaluations => _totalEvaluations;
    public long TotalBudgetExceeded => _totalBudgetExceeded;

    public void BeginFrame()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastBeginFrameTimestamp != 0)
        {
            long intervalMicroseconds = ConvertElapsedTicksToMicroseconds(now - _lastBeginFrameTimestamp);
            LastFrameIntervalMilliseconds = intervalMicroseconds / 1000.0f;
            _totalFrameIntervalMicroseconds += intervalMicroseconds;
        }

        _lastBeginFrameTimestamp = now;
        _sw.Restart();
    }

    public void EndFrame(int raycastsThisFrame)
    {
        _sw.Stop();
        long elapsedMicroseconds = ConvertElapsedTicksToMicroseconds(_sw.ElapsedTicks);
        LastFrameMilliseconds = elapsedMicroseconds / 1000.0f;
        _totalMicroseconds += elapsedMicroseconds;
        _totalRaycasts += raycastsThisFrame;
        if (raycastsThisFrame < _minRaycastsPerFrame)
            _minRaycastsPerFrame = raycastsThisFrame;
        if (raycastsThisFrame > _peakRaycastsPerFrame)
            _peakRaycastsPerFrame = raycastsThisFrame;
        _frameCount++;
    }

    public void RecordCacheHit() => _totalCacheHits++;
    public void RecordEvaluation() => _totalEvaluations++;
    public void RecordBudgetExceeded() => _totalBudgetExceeded++;
    public void RecordVelocityCacheExtension() => _totalVelocityCacheExtensions++;
    public void RecordSmokePreFilterSkip() => _totalSmokePreFilterSkips++;

    public void Reset()
    {
        _totalMicroseconds = 0;
        _frameCount = 0;
        _totalRaycasts = 0;
        _totalCacheHits = 0;
        _totalEvaluations = 0;
        _totalBudgetExceeded = 0;
        _totalVelocityCacheExtensions = 0;
        _totalSmokePreFilterSkips = 0;
        _totalFrameIntervalMicroseconds = 0;
        _peakRaycastsPerFrame = 0;
        _minRaycastsPerFrame = long.MaxValue;
        _lastBeginFrameTimestamp = 0;
        LastFrameMilliseconds = 0.0f;
        LastFrameIntervalMilliseconds = 0.0f;
    }

    public string GetStatsString()
    {
        return PluginOutput.Prefix(
            $"Runtime: {_frameCount} frames | " +
            $"avg self {AvgFrameMicroseconds:F1}us | " +
            $"avg interval {AvgFrameIntervalMicroseconds:F1}us | " +
            $"avg {AvgRaycastsPerFrame:F1} rays/frame (peak {_peakRaycastsPerFrame}) | " +
            $"cache hit {CacheHitRate:F1}% | " +
            $"vel ext {VelocityExtensionRate:F1}% | " +
            $"budget hits {AvgBudgetExceededPerFrame:F2}/frame | " +
            $"smoke skip {_totalSmokePreFilterSkips}");
    }

    internal static long ConvertElapsedTicksToMicroseconds(long elapsedTicks)
    {
        return elapsedTicks * 1_000_000L / Stopwatch.Frequency;
    }
}
