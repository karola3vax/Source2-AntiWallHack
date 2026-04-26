using System.Diagnostics;

namespace S2FOW.Util;

/// <summary>
/// Tracks the plugin's performance metrics across frames.
///
/// Every network frame (64 per second), the CheckTransmit hot path calls BeginFrame()
/// at the start and EndFrame() at the finish. This class measures:
///   - How much time the plugin itself uses per frame (self-time).
///   - How much time passes between frames (engine frame interval).
///   - How many raycasts are performed per frame (and the peak).
///   - How often the raycast budget is exceeded.
///
/// All metrics are exposed as rolling averages and lifetime totals, displayed by
/// the css_fow_stats admin command so operators can verify the plugin is not
/// consuming too much server CPU.
/// </summary>
public class PerformanceMonitor
{
    // ────────────────────────────────────────────────────────────────────────
    //  Internal counters (accumulated across frames, reset on map change)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>High-resolution timer for measuring how long each frame's work takes.</summary>
    private readonly Stopwatch _sw = new();

    /// <summary>The timestamp (in timer ticks) when BeginFrame() was last called. Used to measure frame intervals.</summary>
    private long _lastBeginFrameTimestamp;

    /// <summary>Total microseconds spent in the plugin's CheckTransmit logic across all frames.</summary>
    private long _totalMicroseconds;

    /// <summary>Total microseconds of time that passed between consecutive BeginFrame() calls.</summary>
    private long _totalFrameIntervalMicroseconds;

    /// <summary>How many frames have been processed since the last reset.</summary>
    private int _frameCount;

    /// <summary>Total number of raycasts (line-of-sight checks) performed across all frames.</summary>
    private long _totalRaycasts;

    /// <summary>Total number of visibility evaluations (observer→target pairs checked).</summary>
    private long _totalEvaluations;

    /// <summary>How many times a frame exceeded the raycast budget (safety limit).</summary>
    private long _totalBudgetExceeded;

    /// <summary>The highest number of raycasts performed in a single frame.</summary>
    private long _peakRaycastsPerFrame;

    /// <summary>How many live controlled targets were force-transmitted because their closure was unsafe.</summary>
    private long _unsafeHideSkipped;

    /// <summary>How many invalid-controller pawns were removed with their known closure.</summary>
    private long _invalidControllerPawnClears;

    /// <summary>How many dead or dying targets were force-transmitted.</summary>
    private long _deadForceTransmits;

    /// <summary>How many times associated entities were cleared because the pawn was already absent.</summary>
    private long _orphanClosureCleanups;

    // ────────────────────────────────────────────────────────────────────────
    //  Public read-only metrics (used by css_fow_stats and debug overlays)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Average microseconds the plugin spent per frame (lower = better).</summary>
    public double AvgFrameMicroseconds => _frameCount > 0 ? (double)_totalMicroseconds / _frameCount : 0;

    /// <summary>Average number of raycasts per frame.</summary>
    public double AvgRaycastsPerFrame => _frameCount > 0 ? (double)_totalRaycasts / _frameCount : 0;

    /// <summary>Average number of budget-exceeded events per frame. Should be near 0.</summary>
    public double AvgBudgetExceededPerFrame => _frameCount > 0 ? (double)_totalBudgetExceeded / _frameCount : 0;

    /// <summary>The highest number of raycasts ever performed in a single frame.</summary>
    public long PeakRaycastsPerFrame => _peakRaycastsPerFrame;

    /// <summary>Average microseconds between consecutive frames (engine tick interval, typically ~15,625 µs for 64 tick).</summary>
    public double AvgFrameIntervalMicroseconds => _frameCount > 0 ? (double)_totalFrameIntervalMicroseconds / _frameCount : 0;

    /// <summary>Total raycasts since last reset. Useful for seeing overall load.</summary>
    public long TotalRaycasts => _totalRaycasts;

    /// <summary>Total visibility evaluations since last reset.</summary>
    public long TotalEvaluations => _totalEvaluations;

    /// <summary>Total budget-exceeded events since last reset.</summary>
    public long TotalBudgetExceeded => _totalBudgetExceeded;

    /// <summary>Total live controlled targets transmitted because hiding was unsafe.</summary>
    public long UnsafeHideSkipped => _unsafeHideSkipped;

    /// <summary>Total invalid-controller pawns cleared from transmit with their known closure.</summary>
    public long InvalidControllerPawnClears => _invalidControllerPawnClears;

    /// <summary>Total dead or dying targets force-transmitted.</summary>
    public long DeadForceTransmits => _deadForceTransmits;

    /// <summary>Total associated closure cleanups performed after a pawn was already absent.</summary>
    public long OrphanClosureCleanups => _orphanClosureCleanups;

    // ────────────────────────────────────────────────────────────────────────
    //  Frame lifecycle
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called at the very start of CheckTransmit. Starts the performance timer
    /// and records how much time passed since the previous frame.
    /// </summary>
    public void BeginFrame()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastBeginFrameTimestamp != 0)
        {
            long intervalMicroseconds = ConvertElapsedTicksToMicroseconds(now - _lastBeginFrameTimestamp);
            _totalFrameIntervalMicroseconds += intervalMicroseconds;
        }

        _lastBeginFrameTimestamp = now;
        _sw.Restart();
    }

    /// <summary>
    /// Called at the very end of CheckTransmit. Stops the timer and records
    /// how long the plugin's work took and how many raycasts were performed.
    /// </summary>
    public void EndFrame(int raycastsThisFrame)
    {
        _sw.Stop();
        long elapsedMicroseconds = ConvertElapsedTicksToMicroseconds(_sw.ElapsedTicks);
        _totalMicroseconds += elapsedMicroseconds;
        _totalRaycasts += raycastsThisFrame;
        if (raycastsThisFrame > _peakRaycastsPerFrame)
            _peakRaycastsPerFrame = raycastsThisFrame;
        _frameCount++;
    }

    /// <summary>Records that one visibility evaluation was performed (one observer→target pair).</summary>
    public void RecordEvaluation() => _totalEvaluations++;

    /// <summary>Records that the raycast budget was exceeded in the current frame.</summary>
    public void RecordBudgetExceeded() => _totalBudgetExceeded++;

    /// <summary>Records that a target stayed visible because its associated closure was unsafe.</summary>
    public void RecordUnsafeHideSkipped() => _unsafeHideSkipped++;

    /// <summary>Records that a pawn with an invalid controller was cleared with its known closure.</summary>
    public void RecordInvalidControllerPawnClear() => _invalidControllerPawnClears++;

    /// <summary>Records that a dead or dying target was intentionally kept transmitted.</summary>
    public void RecordDeadForceTransmit() => _deadForceTransmits++;

    /// <summary>Records that associated child entities were removed because their pawn was absent.</summary>
    public void RecordOrphanClosureCleanup() => _orphanClosureCleanups++;

    /// <summary>
    /// Resets all counters to zero. Called on map changes so metrics
    /// reflect only the current map's performance.
    /// </summary>
    public void Reset()
    {
        _totalMicroseconds = 0;
        _frameCount = 0;
        _totalRaycasts = 0;
        _totalEvaluations = 0;
        _totalBudgetExceeded = 0;
        _totalFrameIntervalMicroseconds = 0;
        _peakRaycastsPerFrame = 0;
        _unsafeHideSkipped = 0;
        _invalidControllerPawnClears = 0;
        _deadForceTransmits = 0;
        _orphanClosureCleanups = 0;
        _lastBeginFrameTimestamp = 0;
    }

    /// <summary>
    /// Builds a one-line summary string for the css_fow_stats command.
    /// Example: "[S2FOW] Runtime: 12345 frames | avg self 42.3us | avg 6.2 rays/frame (peak 48) | budget hits 0.01/frame"
    /// </summary>
    public string GetStatsString()
    {
        return PluginOutput.Prefix(
            $"Runtime: {_frameCount} frames | " +
            $"avg self {AvgFrameMicroseconds:F1}us | " +
            $"avg interval {AvgFrameIntervalMicroseconds:F1}us | " +
            $"avg {AvgRaycastsPerFrame:F1} rays/frame (peak {_peakRaycastsPerFrame}) | " +
            $"budget hits {AvgBudgetExceededPerFrame:F2}/frame");
    }

    /// <summary>
    /// Converts high-resolution timer ticks to microseconds.
    /// The conversion factor depends on the CPU's timer frequency (varies by hardware).
    /// </summary>
    internal static long ConvertElapsedTicksToMicroseconds(long elapsedTicks)
    {
        return elapsedTicks * 1_000_000L / Stopwatch.Frequency;
    }
}
