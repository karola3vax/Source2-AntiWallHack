using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace S2AWH;

#pragma warning disable CA1034 // Nested settings types intentionally mirror JSON sections.
public sealed class S2AWHConfig : BasePluginConfig
{
    private float _fovDotThreshold = ComputeFovDotThreshold(240.0f);

    public sealed class CoreSettings
    {
        [JsonPropertyName("Enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("UpdateFrequencyTicks")]
        public int UpdateFrequencyTicks { get; set; } = 16;
    }

    public sealed class TraceSettings
    {
        [JsonPropertyName("UseFovCulling")]
        public bool UseFovCulling { get; set; } = true;

        [JsonPropertyName("FovDegrees")]
        public float FovDegrees { get; set; } = 240.0f;

        [JsonPropertyName("AimRayHitRadius")]
        public float AimRayHitRadius { get; set; } = 100.0f;

        [JsonPropertyName("AimRaySpreadDegrees")]
        public float AimRaySpreadDegrees { get; set; } = 1.0f;

        [JsonPropertyName("AimRayCount")]
        public int AimRayCount { get; set; } = 1;

        [JsonPropertyName("AimRayMaxDistance")]
        public float AimRayMaxDistance { get; set; } = 2200.0f;
    }

    public sealed class PreloadSettings
    {
        [JsonPropertyName("EnablePreload")]
        public bool EnablePreload { get; set; } = true;

        [JsonPropertyName("SurfaceProbeHitRadius")]
        public float SurfaceProbeHitRadius { get; set; } = 64.0f;

        [JsonPropertyName("SurfaceProbeRows")]
        public int SurfaceProbeRows { get; set; } = 1;

        [JsonPropertyName("PredictorDistance")]
        public float PredictorDistance { get; set; } = 160.0f;

        [JsonPropertyName("PredictorMinSpeed")]
        public float PredictorMinSpeed { get; set; } = 60.0f;

        [JsonPropertyName("PredictorFullSpeed")]
        public float PredictorFullSpeed { get; set; } = 120.0f;

        [JsonPropertyName("EnabledForPeekers")]
        public bool EnabledForPeekers { get; set; } = true;

        [JsonPropertyName("EnabledForHolders")]
        public bool EnabledForHolders { get; set; } = false;

        [JsonPropertyName("ViewerPredictorDistanceFactor")]
        public float ViewerPredictorDistanceFactor { get; set; } = 1.0f;

        [JsonPropertyName("RevealHoldSeconds")]
        public float RevealHoldSeconds { get; set; } = 0.10f;

        [SuppressMessage(
            "Design",
            "CA2227:Collection properties should be read only",
            Justification = "System.Text.Json extension-data capture needs a mutable dictionary property so legacy config aliases survive deserialization.")]
        [JsonExtensionData]
        public Dictionary<string, System.Text.Json.JsonElement>? ExtraJson { get; set; }

        public bool TryConsumeLegacyPreloadAlias(out bool enabled, out string aliasName, out string? warning)
        {
            aliasName = string.Empty;
            enabled = false;
            warning = null;
            if (ExtraJson == null)
            {
                return false;
            }

            if (ExtraJson.TryGetValue("EnableProbePreload", out var probeValue))
            {
                aliasName = "Preload.EnableProbePreload";
                if (TryReadLegacyBoolValue(probeValue, out enabled))
                {
                    return true;
                }

                warning = $"{aliasName} exists but is not a valid boolean. The plugin ignores it and keeps Preload.EnablePreload unchanged.";
                return false;
            }

            if (!ExtraJson.TryGetValue("EnableSurfacePreload", out var value))
            {
                return false;
            }

            aliasName = "Preload.EnableSurfacePreload";
            if (TryReadLegacyBoolValue(value, out enabled))
            {
                return true;
            }

            warning = $"{aliasName} exists but is not a valid boolean. The plugin ignores it and keeps Preload.EnablePreload unchanged.";
            return false;
        }

        public bool TryConsumeLegacyPeekersAlias(out bool enabled, out string aliasName, out string? warning)
        {
            aliasName = string.Empty;
            enabled = false;
            warning = null;
            if (ExtraJson == null)
            {
                return false;
            }

            if (!ExtraJson.TryGetValue("EnableViewerPeekAssist", out var value))
            {
                return false;
            }

            aliasName = "Preload.EnableViewerPeekAssist";
            if (TryReadLegacyBoolValue(value, out enabled))
            {
                return true;
            }

            warning = $"{aliasName} exists but is not a valid boolean. The plugin ignores it and keeps Preload.EnabledForPeekers unchanged.";
            return false;
        }

        private static bool TryReadLegacyBoolValue(System.Text.Json.JsonElement value, out bool enabled)
        {
            enabled = false;
            if (value.ValueKind == System.Text.Json.JsonValueKind.True)
            {
                enabled = true;
                return true;
            }

            if (value.ValueKind == System.Text.Json.JsonValueKind.False)
            {
                enabled = false;
                return true;
            }

            if (value.ValueKind == System.Text.Json.JsonValueKind.String &&
                bool.TryParse(value.GetString(), out bool parsed))
            {
                enabled = parsed;
                return true;
            }

            return false;
        }
    }

    public sealed class AabbSettings
    {
        [JsonPropertyName("LosHorizontalScale")]
        public float LosHorizontalScale { get; set; } = 1.0f;

        [JsonPropertyName("LosVerticalScale")]
        public float LosVerticalScale { get; set; } = 1.0f;

        [JsonPropertyName("PredictorHorizontalScale")]
        public float PredictorHorizontalScale { get; set; } = 1.0f;

        [JsonPropertyName("PredictorVerticalScale")]
        public float PredictorVerticalScale { get; set; } = 1.0f;

        [JsonPropertyName("PredictorScaleStartSpeed")]
        public float PredictorScaleStartSpeed { get; set; } = 60.0f;

        [JsonPropertyName("PredictorScaleFullSpeed")]
        public float PredictorScaleFullSpeed { get; set; } = 120.0f;

        [JsonPropertyName("EnableAdaptiveProfile")]
        public bool EnableAdaptiveProfile { get; set; } = true;

        [JsonPropertyName("ProfileSpeedStart")]
        public float ProfileSpeedStart { get; set; } = 60.0f;

        [JsonPropertyName("ProfileSpeedFull")]
        public float ProfileSpeedFull { get; set; } = 120.0f;

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

        [JsonPropertyName("LosSurfaceProbeHitRadius")]
        public float LosSurfaceProbeHitRadius { get; set; } = 64.0f;

        [JsonPropertyName("LosSurfaceProbeRows")]
        public int LosSurfaceProbeRows { get; set; } = 1;

        [JsonPropertyName("MicroHullMaxDistance")]
        public float MicroHullMaxDistance { get; set; } = 2000.0f;

        [JsonPropertyName("MicroHullOverheadZOffset")]
        public float MicroHullOverheadZOffset { get; set; } = 32.0f;
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

        [JsonPropertyName("DrawDebugAabbBoxes")]
        public bool DrawDebugAabbBoxes { get; set; } = false;

        [JsonPropertyName("DrawOnlyPurpleAabb")]
        public bool DrawOnlyPurpleAabb { get; set; } = false;

        [JsonPropertyName("DrawAmountOfRayNumber")]
        public bool DrawAmountOfRayNumber { get; set; } = false;

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
        float fovDegrees = Trace.FovDegrees;
        ClampWithWarning(ref fovDegrees, 1.0f, 359.0f, "Trace.FovDegrees", warnings);
        Trace.FovDegrees = fovDegrees;

        float aimRayHitRadius = Trace.AimRayHitRadius;
        ClampWithWarning(ref aimRayHitRadius, 0.0f, 500.0f, "Trace.AimRayHitRadius", warnings);
        Trace.AimRayHitRadius = aimRayHitRadius;

        float aimRaySpreadDegrees = Trace.AimRaySpreadDegrees;
        ClampWithWarning(ref aimRaySpreadDegrees, 0.0f, 5.0f, "Trace.AimRaySpreadDegrees", warnings);
        Trace.AimRaySpreadDegrees = aimRaySpreadDegrees;

        int aimRayCount = Trace.AimRayCount;
        ClampWithWarning(ref aimRayCount, 1, 5, "Trace.AimRayCount", warnings);
        Trace.AimRayCount = aimRayCount;

        float aimRayMaxDistance = Trace.AimRayMaxDistance;
        ClampWithWarning(ref aimRayMaxDistance, 0.0f, 8192.0f, "Trace.AimRayMaxDistance", warnings);
        Trace.AimRayMaxDistance = aimRayMaxDistance;

        // --- Core ---
        int updateFrequencyTicks = Core.UpdateFrequencyTicks;
        ClampWithWarning(ref updateFrequencyTicks, 1, int.MaxValue, "Core.UpdateFrequencyTicks", warnings);
        Core.UpdateFrequencyTicks = updateFrequencyTicks;

        // --- Preload ---
        float predictorDistance = Preload.PredictorDistance;
        ClampWithWarning(ref predictorDistance, 0.0f, float.MaxValue, "Preload.PredictorDistance", warnings);
        Preload.PredictorDistance = predictorDistance;

        float surfaceProbeHitRadius = Preload.SurfaceProbeHitRadius;
        ClampWithWarning(ref surfaceProbeHitRadius, 0.0f, 200.0f, "Preload.SurfaceProbeHitRadius", warnings);
        Preload.SurfaceProbeHitRadius = surfaceProbeHitRadius;

        int surfaceProbeRows = Preload.SurfaceProbeRows;
        ClampWithWarning(ref surfaceProbeRows, 1, 3, "Preload.SurfaceProbeRows", warnings);
        Preload.SurfaceProbeRows = surfaceProbeRows;

        float predictorMinSpeed = Preload.PredictorMinSpeed;
        ClampWithWarning(ref predictorMinSpeed, 0.0f, 100.0f, "Preload.PredictorMinSpeed", warnings);
        Preload.PredictorMinSpeed = predictorMinSpeed;

        float minPredictorFullSpeed = Preload.PredictorMinSpeed + 1.0f;
        if (Preload.PredictorFullSpeed < minPredictorFullSpeed)
        {
            float invalidValue = Preload.PredictorFullSpeed;
            Preload.PredictorFullSpeed = minPredictorFullSpeed;
            warnings.Add($"Preload.PredictorFullSpeed was {invalidValue}. Because it must be at least PredictorMinSpeed + 1, the plugin now uses {Preload.PredictorFullSpeed}.");
        }

        float viewerPredictorDistanceFactor = Preload.ViewerPredictorDistanceFactor;
        ClampWithWarning(ref viewerPredictorDistanceFactor, 0.0f, 2.0f, "Preload.ViewerPredictorDistanceFactor", warnings);
        Preload.ViewerPredictorDistanceFactor = viewerPredictorDistanceFactor;

        float revealHoldSeconds = Preload.RevealHoldSeconds;
        ClampWithWarning(ref revealHoldSeconds, 0.0f, 1.0f, "Preload.RevealHoldSeconds", warnings);
        Preload.RevealHoldSeconds = revealHoldSeconds;

        // Legacy compatibility: accept old key, but keep generated config surface clean.
        if (Preload.TryConsumeLegacyPreloadAlias(out bool enablePreloadAlias, out string preloadAliasName, out string? preloadAliasWarning))
        {
            Preload.EnablePreload = enablePreloadAlias;
            warnings.Add($"{preloadAliasName} is a legacy key. The plugin maps it to Preload.EnablePreload, but new auto-generated configs no longer include the old name.");
        }
        else if (!string.IsNullOrWhiteSpace(preloadAliasWarning))
        {
            warnings.Add(preloadAliasWarning);
        }

        if (Preload.TryConsumeLegacyPeekersAlias(out bool enablePeekersAlias, out string peekersAliasName, out string? peekersAliasWarning))
        {
            Preload.EnabledForPeekers = enablePeekersAlias;
            warnings.Add($"{peekersAliasName} is a legacy key. The plugin maps it to Preload.EnabledForPeekers, but new auto-generated configs no longer include the old name.");
        }
        else if (!string.IsNullOrWhiteSpace(peekersAliasWarning))
        {
            warnings.Add(peekersAliasWarning);
        }

        // --- Aabb ---
        float losHorizontalScale = Aabb.LosHorizontalScale;
        ClampWithWarning(ref losHorizontalScale, 1.0f, 10.0f, "Aabb.LosHorizontalScale", warnings);
        Aabb.LosHorizontalScale = losHorizontalScale;

        float losVerticalScale = Aabb.LosVerticalScale;
        ClampWithWarning(ref losVerticalScale, 1.0f, 10.0f, "Aabb.LosVerticalScale", warnings);
        Aabb.LosVerticalScale = losVerticalScale;

        float predictorHorizontalScale = Aabb.PredictorHorizontalScale;
        ClampWithWarning(ref predictorHorizontalScale, 1.0f, 10.0f, "Aabb.PredictorHorizontalScale", warnings);
        Aabb.PredictorHorizontalScale = predictorHorizontalScale;

        float predictorVerticalScale = Aabb.PredictorVerticalScale;
        ClampWithWarning(ref predictorVerticalScale, 1.0f, 10.0f, "Aabb.PredictorVerticalScale", warnings);
        Aabb.PredictorVerticalScale = predictorVerticalScale;

        float predictorScaleStartSpeed = Aabb.PredictorScaleStartSpeed;
        ClampWithWarning(ref predictorScaleStartSpeed, 0.0f, float.MaxValue, "Aabb.PredictorScaleStartSpeed", warnings);
        Aabb.PredictorScaleStartSpeed = predictorScaleStartSpeed;

        float minPredictorScaleFullSpeed = Aabb.PredictorScaleStartSpeed + 1.0f;
        if (Aabb.PredictorScaleFullSpeed < minPredictorScaleFullSpeed)
        {
            float invalidValue = Aabb.PredictorScaleFullSpeed;
            Aabb.PredictorScaleFullSpeed = minPredictorScaleFullSpeed;
            warnings.Add($"Aabb.PredictorScaleFullSpeed was {invalidValue}. Because it must be at least PredictorScaleStartSpeed + 1, the plugin now uses {Aabb.PredictorScaleFullSpeed}.");
        }

        float profileSpeedStart = Aabb.ProfileSpeedStart;
        ClampWithWarning(ref profileSpeedStart, 0.0f, float.MaxValue, "Aabb.ProfileSpeedStart", warnings);
        Aabb.ProfileSpeedStart = profileSpeedStart;

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
        ClampWithWarning(ref directionalPredictorShiftFactor, 0.0f, 2.0f, "Aabb.DirectionalPredictorShiftFactor", warnings);
        Aabb.DirectionalPredictorShiftFactor = directionalPredictorShiftFactor;

        float losSurfaceProbeHitRadius = Aabb.LosSurfaceProbeHitRadius;
        ClampWithWarning(ref losSurfaceProbeHitRadius, 0.0f, 200.0f, "Aabb.LosSurfaceProbeHitRadius", warnings);
        Aabb.LosSurfaceProbeHitRadius = losSurfaceProbeHitRadius;

        int losSurfaceProbeRows = Aabb.LosSurfaceProbeRows;
        ClampWithWarning(ref losSurfaceProbeRows, 1, 3, "Aabb.LosSurfaceProbeRows", warnings);
        Aabb.LosSurfaceProbeRows = losSurfaceProbeRows;

        float microHullMaxDistance = Aabb.MicroHullMaxDistance;
        ClampWithWarning(ref microHullMaxDistance, 0.0f, 8192.0f, "Aabb.MicroHullMaxDistance", warnings);
        Aabb.MicroHullMaxDistance = microHullMaxDistance;

        float microHullOverheadZOffset = Aabb.MicroHullOverheadZOffset;
        ClampWithWarning(ref microHullOverheadZOffset, 0.0f, 128.0f, "Aabb.MicroHullOverheadZOffset", warnings);
        Aabb.MicroHullOverheadZOffset = microHullOverheadZOffset;

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
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            float invalid = value;
            value = min;
            warnings.Add($"{paramName} was {invalid} (Not a Number or Infinity). The plugin has reset it to {min}.");
        }
        else if (value < min)
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
#pragma warning restore CA1034

internal static class S2AWHState
{
    public static S2AWHConfig Current { get; set; } = new();
}
