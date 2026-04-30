using System.Diagnostics;
using S2FOW.Core;

namespace S2FOW.Util;

/// <summary>
/// Tracks the plugin's performance metrics across frames.
///
/// Every network frame, the player-hiding path calls BeginFrame() at the start and
/// EndFrame() at the finish. This class measures:
///   - How much time S2FOW uses per frame.
///   - How much time passes between frames.
///   - How many wall checks are performed per frame.
///   - How often S2FOW had to show players because it could not safely finish.
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

    /// <summary>Total microseconds spent in S2FOW's per-frame player-hiding work.</summary>
    private long _totalMicroseconds;

    /// <summary>Total microseconds of time that passed between consecutive BeginFrame() calls.</summary>
    private long _totalFrameIntervalMicroseconds;

    /// <summary>How many frames have been processed since the last reset.</summary>
    private int _frameCount;

    /// <summary>Total number of wall checks performed across all frames.</summary>
    private long _totalRaycasts;

    /// <summary>Total number of viewer/enemy pairs checked.</summary>
    private long _totalEvaluations;

    /// <summary>How many times a frame exceeded the raycast budget (safety limit).</summary>
    private long _totalBudgetExceeded;

    /// <summary>The highest number of raycasts performed in a single frame.</summary>
    private long _peakRaycastsPerFrame;

    /// <summary>How many live enemies stayed visible because connected-object data was incomplete.</summary>
    private long _unsafeHideSkipped;

    /// <summary>How many abnormal live bodies were removed with their known connected objects.</summary>
    private long _invalidControllerPawnClears;

    /// <summary>How many dead or dying enemies were intentionally kept visible.</summary>
    private long _deadForceTransmits;

    /// <summary>How many times connected objects were cleared because their player body was already absent.</summary>
    private long _orphanClosureCleanups;

    private long _rayTraceFailures;
    private long _fullUpdateRequested;
    private long _fullUpdateCoalesced;
    private long _fullUpdateSent;
    private long _fullUpdateThrottled;
    private long _fullUpdateFailed;
    private long _fullUpdateHideReasons;
    private long _fullUpdateUnhideReasons;
    private long _fullUpdateOrphanReasons;
    private long _fullUpdateSafetyReasons;
    private long _fullUpdatePhaseReasons;
    private long _fullUpdateToggleReasons;
    private long _hiddenBySmoke;
    private long _hiddenByLineOfSight;

    // ────────────────────────────────────────────────────────────────────────
    //  Public read-only metrics (used by css_fow_stats and debug overlays)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Average microseconds the plugin spent per frame (lower = better).</summary>
    public double AvgFrameMicroseconds => _frameCount > 0 ? (double)_totalMicroseconds / _frameCount : 0;

    /// <summary>Average number of wall checks per frame.</summary>
    public double AvgRaycastsPerFrame => _frameCount > 0 ? (double)_totalRaycasts / _frameCount : 0;

    /// <summary>Average number of budget-exceeded events per frame. Should be near 0.</summary>
    public double AvgBudgetExceededPerFrame => _frameCount > 0 ? (double)_totalBudgetExceeded / _frameCount : 0;

    /// <summary>The highest number of raycasts ever performed in a single frame.</summary>
    public long PeakRaycastsPerFrame => _peakRaycastsPerFrame;

    /// <summary>Average microseconds between consecutive frames (engine tick interval, typically ~15,625 µs for 64 tick).</summary>
    public double AvgFrameIntervalMicroseconds => _frameCount > 0 ? (double)_totalFrameIntervalMicroseconds / _frameCount : 0;

    /// <summary>Total wall checks since last reset. Useful for seeing overall load.</summary>
    public long TotalRaycasts => _totalRaycasts;

    /// <summary>Total viewer/enemy checks since last reset.</summary>
    public long TotalEvaluations => _totalEvaluations;

    /// <summary>Total budget-exceeded events since last reset.</summary>
    public long TotalBudgetExceeded => _totalBudgetExceeded;

    /// <summary>Total live enemies shown because hiding was unsafe.</summary>
    public long UnsafeHideSkipped => _unsafeHideSkipped;

    /// <summary>Total abnormal live bodies removed with their known connected objects.</summary>
    public long InvalidControllerPawnClears => _invalidControllerPawnClears;

    /// <summary>Total dead or dying enemies intentionally kept visible.</summary>
    public long DeadForceTransmits => _deadForceTransmits;

    /// <summary>Total connected-object cleanups after a player body was already absent.</summary>
    public long OrphanClosureCleanups => _orphanClosureCleanups;

    public long RayTraceFailures => _rayTraceFailures;
    public long FullUpdateRequested => _fullUpdateRequested;
    public long FullUpdateCoalesced => _fullUpdateCoalesced;
    public long FullUpdateSent => _fullUpdateSent;
    public long FullUpdateThrottled => _fullUpdateThrottled;
    public long FullUpdateFailed => _fullUpdateFailed;
    public long FullUpdateHideReasons => _fullUpdateHideReasons;
    public long FullUpdateUnhideReasons => _fullUpdateUnhideReasons;
    public long FullUpdateOrphanReasons => _fullUpdateOrphanReasons;
    public long FullUpdateSafetyReasons => _fullUpdateSafetyReasons;
    public long FullUpdatePhaseReasons => _fullUpdatePhaseReasons;
    public long FullUpdateToggleReasons => _fullUpdateToggleReasons;
    public long HiddenBySmoke => _hiddenBySmoke;
    public long HiddenByLineOfSight => _hiddenByLineOfSight;

    // ────────────────────────────────────────────────────────────────────────
    //  Frame lifecycle
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called at the very start of per-frame player hiding. Starts the performance timer
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
    /// Called at the very end of per-frame player hiding. Stops the timer and records
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

    /// <summary>Records that one viewer/enemy pair was checked.</summary>
    public void RecordEvaluation() => _totalEvaluations++;

    /// <summary>Records that the raycast budget was exceeded in the current frame.</summary>
    public void RecordBudgetExceeded() => _totalBudgetExceeded++;

    /// <summary>Records that an enemy stayed visible because connected-object data was incomplete.</summary>
    public void RecordUnsafeHideSkipped() => _unsafeHideSkipped++;

    /// <summary>Records that S2FOW hid a player because smoke blocked all checked sight paths.</summary>
    public void RecordHiddenBySmoke() => _hiddenBySmoke++;

    /// <summary>Records that S2FOW hid a player because walls blocked all checked sight paths.</summary>
    public void RecordHiddenByLineOfSight() => _hiddenByLineOfSight++;

    /// <summary>Records that an abnormal live body was cleared with its known connected objects.</summary>
    public void RecordInvalidControllerPawnClear() => _invalidControllerPawnClears++;

    /// <summary>Records that a dead or dying enemy was intentionally kept visible.</summary>
    public void RecordDeadForceTransmit() => _deadForceTransmits++;

    /// <summary>Records that connected objects were removed because their player body was absent.</summary>
    public void RecordOrphanClosureCleanup() => _orphanClosureCleanups++;

    public void RecordRayTraceFailure() => _rayTraceFailures++;

    public void RecordFullUpdateRequested(ObserverFullUpdateReason reason)
    {
        _fullUpdateRequested++;
        RecordFullUpdateReason(reason);
    }

    public void RecordFullUpdateCoalesced(ObserverFullUpdateReason reason)
    {
        _fullUpdateCoalesced++;
    }

    public void RecordFullUpdateSent(ObserverFullUpdateReason reason)
    {
        _fullUpdateSent++;
    }

    public void RecordFullUpdateThrottled(ObserverFullUpdateReason reason)
    {
        _fullUpdateThrottled++;
    }

    public void RecordFullUpdateFailed(ObserverFullUpdateReason reason)
    {
        _fullUpdateFailed++;
    }

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
        _rayTraceFailures = 0;
        _fullUpdateRequested = 0;
        _fullUpdateCoalesced = 0;
        _fullUpdateSent = 0;
        _fullUpdateThrottled = 0;
        _fullUpdateFailed = 0;
        _fullUpdateHideReasons = 0;
        _fullUpdateUnhideReasons = 0;
        _fullUpdateOrphanReasons = 0;
        _fullUpdateSafetyReasons = 0;
        _fullUpdatePhaseReasons = 0;
        _fullUpdateToggleReasons = 0;
        _hiddenBySmoke = 0;
        _hiddenByLineOfSight = 0;
        _lastBeginFrameTimestamp = 0;
    }

    /// <summary>
    /// Builds a one-line plain-English work summary.
    /// </summary>
    public string GetStatsString()
    {
        return PluginOutput.Prefix(
            $"Work: {_frameCount} frames checked | " +
            $"average plugin time {AvgFrameMicroseconds / 1000.0:F3} ms | " +
            $"average raycasts {AvgRaycastsPerFrame:F1}/frame | " +
            $"peak raycasts {_peakRaycastsPerFrame}");
    }

    /// <summary>
    /// Converts high-resolution timer ticks to microseconds.
    /// The conversion factor depends on the CPU's timer frequency (varies by hardware).
    /// </summary>
    internal static long ConvertElapsedTicksToMicroseconds(long elapsedTicks)
    {
        return elapsedTicks * 1_000_000L / Stopwatch.Frequency;
    }

    private void RecordFullUpdateReason(ObserverFullUpdateReason reason)
    {
        if ((reason & ObserverFullUpdateReason.Hide) != 0)
            _fullUpdateHideReasons++;
        if ((reason & ObserverFullUpdateReason.Unhide) != 0)
            _fullUpdateUnhideReasons++;
        if ((reason & ObserverFullUpdateReason.OrphanCleanup) != 0)
            _fullUpdateOrphanReasons++;
        if ((reason & ObserverFullUpdateReason.SafetyClear) != 0)
            _fullUpdateSafetyReasons++;
        if ((reason & ObserverFullUpdateReason.PhaseBypass) != 0)
            _fullUpdatePhaseReasons++;
        if ((reason & ObserverFullUpdateReason.Toggle) != 0)
            _fullUpdateToggleReasons++;
    }
}
