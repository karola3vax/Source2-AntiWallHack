using S2FOW.Config;
using S2FOW.Util;

namespace S2FOW.Core;

/// <summary>
/// Monitors server frame time and dynamically adjusts quality when the server is under pressure.
/// Integrates with VisibilityManager to scale effective cache TTLs and primitive budgets.
///
/// Quality levels:
///   Full    — all defaults, no scaling.
///   Reduced — 1.5× cache TTLs, −2 check points at Far/XFar.
///   Minimal — 2× cache TTLs, −4 check points, wider phase spread.
///
/// Thresholds are based on the observed wall-clock interval between visibility frames.
/// At a healthy 64 tick server this interval should stay near 15.625ms.
///   Downgrade trigger: >12ms average (75% utilization).
///   Upgrade trigger:   <8ms average (50% utilization).
///
/// Changes are smoothed over 64 ticks (1 second) to prevent oscillation.
/// </summary>
public class AdaptiveQualityScaler
{
    public enum QualityLevel
    {
        Full = 0,
        Reduced = 1,
        Minimal = 2
    }

    // Rolling average window: 64 ticks = 1 second at 64 Hz.
    private const int WindowSize = 64;

    // Thresholds in milliseconds relative to a 15.625ms tick budget.
    private const float DowngradeThresholdMs = 12.0f;  // 75% utilization
    private const float UpgradeThresholdMs = 8.0f;     // 50% utilization

    // Hysteresis: require sustained pressure before changing level.
    // Must exceed threshold for this many consecutive ticks to trigger a change.
    private const int HysteresisTicks = 64;

    private readonly float[] _frameTimes = new float[WindowSize];
    private int _writeIndex;
    private int _sampleCount;
    private float _rollingSum;

    private int _downgradeCounter;
    private int _upgradeCounter;

    public QualityLevel CurrentLevel { get; private set; } = QualityLevel.Full;
    public float AverageFrameTimeMs => _sampleCount > 0 ? _rollingSum / _sampleCount : 0.0f;

    /// <summary>
    /// Records a frame time sample and potentially adjusts quality level.
    /// Call once per frame from VisibilityManager.BeginFrame().
    /// </summary>
    /// <param name="frameTimeMs">Observed wall-clock frame interval in milliseconds.</param>
    public void RecordFrameTime(float frameTimeMs)
    {
        // Update rolling window.
        if (_sampleCount >= WindowSize)
            _rollingSum -= _frameTimes[_writeIndex];
        else
            _sampleCount++;

        _frameTimes[_writeIndex] = frameTimeMs;
        _rollingSum += frameTimeMs;
        _writeIndex = (_writeIndex + 1) % WindowSize;

        // Only evaluate after we have a full window.
        if (_sampleCount < WindowSize)
            return;

        float avg = _rollingSum / WindowSize;

        // Downgrade check.
        if (avg > DowngradeThresholdMs && CurrentLevel < QualityLevel.Minimal)
        {
            _downgradeCounter++;
            _upgradeCounter = 0;
            if (_downgradeCounter >= HysteresisTicks)
            {
                CurrentLevel = (QualityLevel)((int)CurrentLevel + 1);
                _downgradeCounter = 0;
            }
        }
        // Upgrade check.
        else if (avg < UpgradeThresholdMs && CurrentLevel > QualityLevel.Full)
        {
            _upgradeCounter++;
            _downgradeCounter = 0;
            if (_upgradeCounter >= HysteresisTicks)
            {
                CurrentLevel = (QualityLevel)((int)CurrentLevel - 1);
                _upgradeCounter = 0;
            }
        }
        else
        {
            // In the neutral zone — reset both counters.
            _downgradeCounter = 0;
            _upgradeCounter = 0;
        }
    }

    /// <summary>
    /// Applies quality scaling to a cache TTL value.
    /// </summary>
    public int ScaleCacheTTL(int baseTTL)
    {
        return CurrentLevel switch
        {
            QualityLevel.Reduced => (int)MathF.Ceiling(baseTTL * 1.5f),
            QualityLevel.Minimal => baseTTL * 2,
            _ => baseTTL
        };
    }

    /// <summary>
    /// Applies quality scaling to a max check point count.
    /// </summary>
    public int ScaleCheckPoints(int basePoints)
    {
        return CurrentLevel switch
        {
            QualityLevel.Reduced => Math.Max(3, basePoints - 2),
            QualityLevel.Minimal => Math.Max(3, basePoints - 4),
            _ => basePoints
        };
    }

    /// <summary>
    /// Returns additional phase spread ticks for the current quality level.
    /// </summary>
    public int ExtraPhaseSpreadTicks()
    {
        return CurrentLevel switch
        {
            QualityLevel.Minimal => 2,
            _ => 0
        };
    }

    /// <summary>
    /// Resets the scaler to Full quality. Call on map change or config reload.
    /// </summary>
    public void Reset()
    {
        CurrentLevel = QualityLevel.Full;
        Array.Clear(_frameTimes);
        _writeIndex = 0;
        _sampleCount = 0;
        _rollingSum = 0.0f;
        _downgradeCounter = 0;
        _upgradeCounter = 0;
    }
}
