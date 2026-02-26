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
        public int RayTracePoints { get; set; } = 6;

        [JsonPropertyName("UseFovCulling")]
        public bool UseFovCulling { get; set; } = true;

        [JsonPropertyName("FovDegrees")]
        public float FovDegrees { get; set; } = 220.0f;

        [JsonPropertyName("AimRayHitRadius")]
        public float AimRayHitRadius { get; set; } = 100.0f;

        [JsonPropertyName("AimRaySpreadDegrees")]
        public float AimRaySpreadDegrees { get; set; } = 1.0f;

        [JsonPropertyName("GapSweepProximity")]
        public float GapSweepProximity { get; set; } = 72.0f;
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
        public float ProfileSpeedStart { get; set; } = 80.0f;

        [JsonPropertyName("ProfileSpeedFull")]
        public float ProfileSpeedFull { get; set; } = 100.0f;

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
        public bool IncludeTeammates { get; set; } = false;

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

    /// <summary>
    /// Validates and clamps all config values to their allowed ranges, returning a list of
    /// human-readable warnings for any values that were auto-corrected.
    /// </summary>
    public IReadOnlyList<string> Normalize()
    {
        List<string> warnings = new();

        // --- Trace ---
        int rayTracePoints = Trace.RayTracePoints;
        ClampWithWarning(ref rayTracePoints, 1, 10, "Trace.RayTracePoints", warnings);
        Trace.RayTracePoints = rayTracePoints;

        float fovDegrees = Trace.FovDegrees;
        ClampWithWarning(ref fovDegrees, 1.0f, 359.0f, "Trace.FovDegrees", warnings);
        Trace.FovDegrees = fovDegrees;

        float aimRayHitRadius = Trace.AimRayHitRadius;
        ClampWithWarning(ref aimRayHitRadius, 0.0f, 500.0f, "Trace.AimRayHitRadius", warnings);
        Trace.AimRayHitRadius = aimRayHitRadius;

        float aimRaySpreadDegrees = Trace.AimRaySpreadDegrees;
        ClampWithWarning(ref aimRaySpreadDegrees, 0.0f, 5.0f, "Trace.AimRaySpreadDegrees", warnings);
        Trace.AimRaySpreadDegrees = aimRaySpreadDegrees;

        float gapSweepProximity = Trace.GapSweepProximity;
        ClampWithWarning(ref gapSweepProximity, 20.0f, 200.0f, "Trace.GapSweepProximity", warnings);
        Trace.GapSweepProximity = gapSweepProximity;

        // --- Core ---
        int updateFrequencyTicks = Core.UpdateFrequencyTicks;
        ClampWithWarning(ref updateFrequencyTicks, 1, int.MaxValue, "Core.UpdateFrequencyTicks", warnings);
        Core.UpdateFrequencyTicks = updateFrequencyTicks;

        // --- Preload ---
        float predictorDistance = Preload.PredictorDistance;
        ClampWithWarning(ref predictorDistance, 0.0f, float.MaxValue, "Preload.PredictorDistance", warnings);
        Preload.PredictorDistance = predictorDistance;

        float predictorMinSpeed = Preload.PredictorMinSpeed;
        ClampWithWarning(ref predictorMinSpeed, 0.0f, 100.0f, "Preload.PredictorMinSpeed", warnings);
        Preload.PredictorMinSpeed = predictorMinSpeed;

        float viewerPredictorDistanceFactor = Preload.ViewerPredictorDistanceFactor;
        ClampWithWarning(ref viewerPredictorDistanceFactor, 0.0f, 2.0f, "Preload.ViewerPredictorDistanceFactor", warnings);
        Preload.ViewerPredictorDistanceFactor = viewerPredictorDistanceFactor;

        float revealHoldSeconds = Preload.RevealHoldSeconds;
        ClampWithWarning(ref revealHoldSeconds, 0.0f, 1.0f, "Preload.RevealHoldSeconds", warnings);
        Preload.RevealHoldSeconds = revealHoldSeconds;

        // --- Aabb ---
        float horizontalScale = Aabb.HorizontalScale;
        ClampWithWarning(ref horizontalScale, 1.0f, 10.0f, "Aabb.HorizontalScale", warnings);
        Aabb.HorizontalScale = horizontalScale;

        float verticalScale = Aabb.VerticalScale;
        ClampWithWarning(ref verticalScale, 1.0f, 10.0f, "Aabb.VerticalScale", warnings);
        Aabb.VerticalScale = verticalScale;

        float profileSpeedStart = Aabb.ProfileSpeedStart;
        ClampWithWarning(ref profileSpeedStart, 0.0f, float.MaxValue, "Aabb.ProfileSpeedStart", warnings);
        Aabb.ProfileSpeedStart = profileSpeedStart;

        // ProfileSpeedFull must be at least ProfileSpeedStart + 1 (special logic).
        float minProfileSpeedFull = Aabb.ProfileSpeedStart + 1.0f;
        if (Aabb.ProfileSpeedFull < minProfileSpeedFull)
        {
            float invalidValue = Aabb.ProfileSpeedFull;
            Aabb.ProfileSpeedFull = minProfileSpeedFull;
            warnings.Add($"Aabb.ProfileSpeedFull was {invalidValue}. Because it must be at least ProfileSpeedStart + 1, the plugin now uses {Aabb.ProfileSpeedFull}.");
        }

        float profileHorizontalMaxMultiplier = Aabb.ProfileHorizontalMaxMultiplier;
        ClampWithWarning(ref profileHorizontalMaxMultiplier, 1.0f, 3.0f, "Aabb.ProfileHorizontalMaxMultiplier", warnings);
        Aabb.ProfileHorizontalMaxMultiplier = profileHorizontalMaxMultiplier;

        float profileVerticalMaxMultiplier = Aabb.ProfileVerticalMaxMultiplier;
        ClampWithWarning(ref profileVerticalMaxMultiplier, 1.0f, 3.0f, "Aabb.ProfileVerticalMaxMultiplier", warnings);
        Aabb.ProfileVerticalMaxMultiplier = profileVerticalMaxMultiplier;

        float directionalForwardShiftMaxUnits = Aabb.DirectionalForwardShiftMaxUnits;
        ClampWithWarning(ref directionalForwardShiftMaxUnits, 0.0f, 128.0f, "Aabb.DirectionalForwardShiftMaxUnits", warnings);
        Aabb.DirectionalForwardShiftMaxUnits = directionalForwardShiftMaxUnits;

        float directionalPredictorShiftFactor = Aabb.DirectionalPredictorShiftFactor;
        ClampWithWarning(ref directionalPredictorShiftFactor, 0.0f, 1.0f, "Aabb.DirectionalPredictorShiftFactor", warnings);
        Aabb.DirectionalPredictorShiftFactor = directionalPredictorShiftFactor;

        _fovDotThreshold = ComputeFovDotThreshold(Trace.FovDegrees);
        return warnings;
    }

    private static void ClampWithWarning(ref int value, int min, int max, string paramName, List<string> warnings)
    {
        if (value < min)
        {
            int invalid = value;
            value = min;
            warnings.Add($"{paramName} was {invalid}. Because the minimum value is {min}, the plugin now uses {min}.");
        }
        else if (max != int.MaxValue && value > max)
        {
            int invalid = value;
            value = max;
            warnings.Add($"{paramName} was {invalid}. Because the maximum value is {max}, the plugin now uses {max}.");
        }
    }

    private static void ClampWithWarning(ref float value, float min, float max, string paramName, List<string> warnings)
    {
        if (value < min)
        {
            float invalid = value;
            value = min;
            warnings.Add($"{paramName} was {invalid}. Because the minimum value is {min}, the plugin now uses {min}.");
        }
        else if (max != float.MaxValue && value > max)
        {
            float invalid = value;
            value = max;
            warnings.Add($"{paramName} was {invalid}. Because the maximum value is {max}, the plugin now uses {max}.");
        }
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
