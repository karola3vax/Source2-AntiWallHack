using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace S2AWH;

public sealed class S2AWHConfig : BasePluginConfig
{
    private float _fovDotThreshold = ComputeFovDotThreshold(200.0f);

    public sealed class CoreSettings
    {
        [JsonPropertyName("Enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("UpdateFrequencyTicks")]
        public int UpdateFrequencyTicks { get; set; } = 10;
    }

    public sealed class TraceSettings
    {
        [JsonPropertyName("RayTracePoints")]
        public int RayTracePoints { get; set; } = 10;

        [JsonPropertyName("UseFovCulling")]
        public bool UseFovCulling { get; set; } = true;

        [JsonPropertyName("FovDegrees")]
        public float FovDegrees { get; set; } = 200.0f;
    }

    public sealed class PreloadSettings
    {
        [JsonPropertyName("PredictorDistance")]
        public float PredictorDistance { get; set; } = 150.0f;

        [JsonPropertyName("PredictorMinSpeed")]
        public float PredictorMinSpeed { get; set; } = 1.0f;

        [JsonPropertyName("EnableViewerPeekAssist")]
        public bool EnableViewerPeekAssist { get; set; } = true;

        [JsonPropertyName("ViewerPredictorDistanceFactor")]
        public float ViewerPredictorDistanceFactor { get; set; } = 0.85f;

        [JsonPropertyName("RevealHoldSeconds")]
        public float RevealHoldSeconds { get; set; } = 0.30f;
    }

    public sealed class AabbSettings
    {
        [JsonPropertyName("HorizontalScale")]
        public float HorizontalScale { get; set; } = 3.0f;

        [JsonPropertyName("VerticalScale")]
        public float VerticalScale { get; set; } = 2.0f;

        [JsonPropertyName("EnableAdaptiveProfile")]
        public bool EnableAdaptiveProfile { get; set; } = true;

        [JsonPropertyName("ProfileSpeedStart")]
        public float ProfileSpeedStart { get; set; } = 40.0f;

        [JsonPropertyName("ProfileSpeedFull")]
        public float ProfileSpeedFull { get; set; } = 260.0f;

        [JsonPropertyName("ProfileHorizontalMaxMultiplier")]
        public float ProfileHorizontalMaxMultiplier { get; set; } = 1.70f;

        [JsonPropertyName("ProfileVerticalMaxMultiplier")]
        public float ProfileVerticalMaxMultiplier { get; set; } = 1.35f;

        [JsonPropertyName("EnableDirectionalShift")]
        public bool EnableDirectionalShift { get; set; } = true;

        [JsonPropertyName("DirectionalForwardShiftMaxUnits")]
        public float DirectionalForwardShiftMaxUnits { get; set; } = 34.0f;

        [JsonPropertyName("DirectionalPredictorShiftFactor")]
        public float DirectionalPredictorShiftFactor { get; set; } = 0.65f;
    }

    public sealed class VisibilitySettings
    {
        [JsonPropertyName("IncludeTeammates")]
        public bool IncludeTeammates { get; set; } = true;

        [JsonPropertyName("IncludeBots")]
        public bool IncludeBots { get; set; } = true;

        [JsonPropertyName("BotsDoLOS")]
        public bool BotsDoLOS { get; set; } = true;
    }

    public sealed class DiagnosticsSettings
    {
        [JsonPropertyName("ShowDebugInfo")]
        public bool ShowDebugInfo { get; set; } = true;

        [JsonPropertyName("DrawDebugTraceBeams")]
        public bool DrawDebugTraceBeams { get; set; } = false;

        [JsonPropertyName("DrawDebugTraceBeamsForHumans")]
        public bool DrawDebugTraceBeamsForHumans { get; set; } = true;

        [JsonPropertyName("DrawDebugTraceBeamsForBots")]
        public bool DrawDebugTraceBeamsForBots { get; set; } = true;
    }

    [JsonPropertyOrder(-100)]
    [JsonPropertyName("Core")]
    public CoreSettings Core { get; set; } = new();

    [JsonPropertyOrder(-90)]
    [JsonPropertyName("Trace")]
    public TraceSettings Trace { get; set; } = new();

    [JsonPropertyOrder(-80)]
    [JsonPropertyName("Preload")]
    public PreloadSettings Preload { get; set; } = new();

    [JsonPropertyOrder(-70)]
    [JsonPropertyName("Aabb")]
    public AabbSettings Aabb { get; set; } = new();

    [JsonPropertyOrder(-60)]
    [JsonPropertyName("Visibility")]
    public VisibilitySettings Visibility { get; set; } = new();

    [JsonPropertyOrder(-50)]
    [JsonPropertyName("Diagnostics")]
    public DiagnosticsSettings Diagnostics { get; set; } = new();

    [JsonIgnore]
    public float FovDotThreshold => _fovDotThreshold;

    public IReadOnlyList<string> Normalize()
    {
        List<string> warnings = new();

        if (Trace.RayTracePoints < 1)
        {
            int invalidValue = Trace.RayTracePoints;
            Trace.RayTracePoints = 1;
            warnings.Add($"Trace.RayTracePoints was {invalidValue}. Because the valid range is 1 to 10, the plugin now uses 1.");
        }
        else if (Trace.RayTracePoints > 10)
        {
            int invalidValue = Trace.RayTracePoints;
            Trace.RayTracePoints = 10;
            warnings.Add($"Trace.RayTracePoints was {invalidValue}. Because the valid range is 1 to 10, the plugin now uses 10.");
        }

        if (Trace.FovDegrees < 1.0f)
        {
            float invalidValue = Trace.FovDegrees;
            Trace.FovDegrees = 1.0f;
            warnings.Add($"Trace.FovDegrees was {invalidValue}. Because the minimum value is 1, the plugin now uses 1.");
        }
        else if (Trace.FovDegrees > 359.0f)
        {
            float invalidValue = Trace.FovDegrees;
            Trace.FovDegrees = 359.0f;
            warnings.Add($"Trace.FovDegrees was {invalidValue}. Because the maximum value is 359, the plugin now uses 359.");
        }

        if (Core.UpdateFrequencyTicks < 1)
        {
            int invalidValue = Core.UpdateFrequencyTicks;
            Core.UpdateFrequencyTicks = 1;
            warnings.Add($"Core.UpdateFrequencyTicks was {invalidValue}. Because the minimum value is 1, the plugin now uses 1.");
        }

        if (Preload.PredictorDistance < 0.0f)
        {
            float invalidValue = Preload.PredictorDistance;
            Preload.PredictorDistance = 0.0f;
            warnings.Add($"Preload.PredictorDistance was {invalidValue}. Because this value cannot be negative, the plugin now uses 0.");
        }

        if (Preload.PredictorMinSpeed < 0.0f)
        {
            float invalidValue = Preload.PredictorMinSpeed;
            Preload.PredictorMinSpeed = 0.0f;
            warnings.Add($"Preload.PredictorMinSpeed was {invalidValue}. Because the minimum value is 0, the plugin now uses 0.");
        }
        else if (Preload.PredictorMinSpeed > 100.0f)
        {
            float invalidValue = Preload.PredictorMinSpeed;
            Preload.PredictorMinSpeed = 100.0f;
            warnings.Add($"Preload.PredictorMinSpeed was {invalidValue}. Because the maximum value is 100, the plugin now uses 100.");
        }

        if (Preload.ViewerPredictorDistanceFactor < 0.0f)
        {
            float invalidValue = Preload.ViewerPredictorDistanceFactor;
            Preload.ViewerPredictorDistanceFactor = 0.0f;
            warnings.Add($"Preload.ViewerPredictorDistanceFactor was {invalidValue}. Because the minimum value is 0, the plugin now uses 0.");
        }
        else if (Preload.ViewerPredictorDistanceFactor > 2.0f)
        {
            float invalidValue = Preload.ViewerPredictorDistanceFactor;
            Preload.ViewerPredictorDistanceFactor = 2.0f;
            warnings.Add($"Preload.ViewerPredictorDistanceFactor was {invalidValue}. Because the maximum value is 2, the plugin now uses 2.");
        }

        if (Aabb.HorizontalScale < 1.0f)
        {
            float invalidValue = Aabb.HorizontalScale;
            Aabb.HorizontalScale = 1.0f;
            warnings.Add($"Aabb.HorizontalScale was {invalidValue}. Because the minimum value is 1, the plugin now uses 1.");
        }
        else if (Aabb.HorizontalScale > 10.0f)
        {
            float invalidValue = Aabb.HorizontalScale;
            Aabb.HorizontalScale = 10.0f;
            warnings.Add($"Aabb.HorizontalScale was {invalidValue}. Because the maximum value is 10, the plugin now uses 10.");
        }

        if (Aabb.VerticalScale < 1.0f)
        {
            float invalidValue = Aabb.VerticalScale;
            Aabb.VerticalScale = 1.0f;
            warnings.Add($"Aabb.VerticalScale was {invalidValue}. Because the minimum value is 1, the plugin now uses 1.");
        }
        else if (Aabb.VerticalScale > 10.0f)
        {
            float invalidValue = Aabb.VerticalScale;
            Aabb.VerticalScale = 10.0f;
            warnings.Add($"Aabb.VerticalScale was {invalidValue}. Because the maximum value is 10, the plugin now uses 10.");
        }

        if (Preload.RevealHoldSeconds < 0.0f)
        {
            float invalidValue = Preload.RevealHoldSeconds;
            Preload.RevealHoldSeconds = 0.0f;
            warnings.Add($"Preload.RevealHoldSeconds was {invalidValue}. Because the minimum value is 0, the plugin now uses 0.");
        }
        else if (Preload.RevealHoldSeconds > 1.0f)
        {
            float invalidValue = Preload.RevealHoldSeconds;
            Preload.RevealHoldSeconds = 1.0f;
            warnings.Add($"Preload.RevealHoldSeconds was {invalidValue}. Because the maximum value is 1, the plugin now uses 1.");
        }

        if (Aabb.ProfileSpeedStart < 0.0f)
        {
            float invalidValue = Aabb.ProfileSpeedStart;
            Aabb.ProfileSpeedStart = 0.0f;
            warnings.Add($"Aabb.ProfileSpeedStart was {invalidValue}. Because the minimum value is 0, the plugin now uses 0.");
        }

        float minProfileSpeedFull = Aabb.ProfileSpeedStart + 1.0f;
        if (Aabb.ProfileSpeedFull < minProfileSpeedFull)
        {
            float invalidValue = Aabb.ProfileSpeedFull;
            Aabb.ProfileSpeedFull = minProfileSpeedFull;
            warnings.Add($"Aabb.ProfileSpeedFull was {invalidValue}. Because it must be at least ProfileSpeedStart + 1, the plugin now uses {Aabb.ProfileSpeedFull}.");
        }

        if (Aabb.ProfileHorizontalMaxMultiplier < 1.0f)
        {
            float invalidValue = Aabb.ProfileHorizontalMaxMultiplier;
            Aabb.ProfileHorizontalMaxMultiplier = 1.0f;
            warnings.Add($"Aabb.ProfileHorizontalMaxMultiplier was {invalidValue}. Because the minimum value is 1, the plugin now uses 1.");
        }
        else if (Aabb.ProfileHorizontalMaxMultiplier > 3.0f)
        {
            float invalidValue = Aabb.ProfileHorizontalMaxMultiplier;
            Aabb.ProfileHorizontalMaxMultiplier = 3.0f;
            warnings.Add($"Aabb.ProfileHorizontalMaxMultiplier was {invalidValue}. Because the maximum value is 3, the plugin now uses 3.");
        }

        if (Aabb.ProfileVerticalMaxMultiplier < 1.0f)
        {
            float invalidValue = Aabb.ProfileVerticalMaxMultiplier;
            Aabb.ProfileVerticalMaxMultiplier = 1.0f;
            warnings.Add($"Aabb.ProfileVerticalMaxMultiplier was {invalidValue}. Because the minimum value is 1, the plugin now uses 1.");
        }
        else if (Aabb.ProfileVerticalMaxMultiplier > 3.0f)
        {
            float invalidValue = Aabb.ProfileVerticalMaxMultiplier;
            Aabb.ProfileVerticalMaxMultiplier = 3.0f;
            warnings.Add($"Aabb.ProfileVerticalMaxMultiplier was {invalidValue}. Because the maximum value is 3, the plugin now uses 3.");
        }

        if (Aabb.DirectionalForwardShiftMaxUnits < 0.0f)
        {
            float invalidValue = Aabb.DirectionalForwardShiftMaxUnits;
            Aabb.DirectionalForwardShiftMaxUnits = 0.0f;
            warnings.Add($"Aabb.DirectionalForwardShiftMaxUnits was {invalidValue}. Because the minimum value is 0, the plugin now uses 0.");
        }
        else if (Aabb.DirectionalForwardShiftMaxUnits > 128.0f)
        {
            float invalidValue = Aabb.DirectionalForwardShiftMaxUnits;
            Aabb.DirectionalForwardShiftMaxUnits = 128.0f;
            warnings.Add($"Aabb.DirectionalForwardShiftMaxUnits was {invalidValue}. Because the maximum value is 128, the plugin now uses 128.");
        }

        if (Aabb.DirectionalPredictorShiftFactor < 0.0f)
        {
            float invalidValue = Aabb.DirectionalPredictorShiftFactor;
            Aabb.DirectionalPredictorShiftFactor = 0.0f;
            warnings.Add($"Aabb.DirectionalPredictorShiftFactor was {invalidValue}. Because the minimum value is 0, the plugin now uses 0.");
        }
        else if (Aabb.DirectionalPredictorShiftFactor > 1.0f)
        {
            float invalidValue = Aabb.DirectionalPredictorShiftFactor;
            Aabb.DirectionalPredictorShiftFactor = 1.0f;
            warnings.Add($"Aabb.DirectionalPredictorShiftFactor was {invalidValue}. Because the maximum value is 1, the plugin now uses 1.");
        }

        _fovDotThreshold = ComputeFovDotThreshold(Trace.FovDegrees);
        return warnings;
    }

    private static float ComputeFovDotThreshold(float fovDegrees)
    {
        float clampedFov = Math.Clamp(fovDegrees, 1.0f, 359.0f);
        float halfFovRadians = (clampedFov * 0.5f) * MathF.PI / 180.0f;
        return MathF.Cos(halfFovRadians);
    }
}

public static class S2AWHState
{
    public static S2AWHConfig Current { get; set; } = new();
}
