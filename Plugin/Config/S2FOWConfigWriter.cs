using System.Globalization;
using System.Text;
using CounterStrikeSharp.API.Modules.Extensions;
using S2FOW.Util;

namespace S2FOW.Config;

/// <summary>
/// Generates human-readable "guided" JSON config files with inline comments.
///
/// CounterStrikeSharp allows // comments in JSON config files (non-standard but
/// widely supported). This writer generates a config file where every setting has
/// a plain-English comment above it explaining what it does.
///
/// Two files are generated:
///   1. S2FOW.example.json — Always contains the default settings. Useful as a
///      reference if you want to see what the defaults are.
///   2. The real config file — Contains the current active settings with comments.
///      Only rewritten if the content has actually changed (avoids unnecessary disk I/O).
///
/// If writing fails (e.g., read-only filesystem), the error is silently counted
/// in PluginDiagnostics. The plugin continues running with its in-memory config.
/// </summary>
internal static class S2FOWConfigWriter
{
    /// <summary>Marker comment at the top of guided config files for identification.</summary>
    private const string GuidedMarker = "S2FOW guided config";

    /// <summary>
    /// Writes (or updates) the guided JSON config files to disk.
    /// Called every time the config is parsed or reloaded.
    /// </summary>
    public static void EnsureGuidedJsonFiles(S2FOWConfig currentConfig)
    {
        string configPath = currentConfig.GetConfigPath();
        string? directoryPath = Path.GetDirectoryName(configPath);
        if (string.IsNullOrWhiteSpace(directoryPath))
            return;

        Directory.CreateDirectory(directoryPath);

        // Write the example file (always contains defaults).
        string assemblyName = typeof(S2FOWConfig).Assembly.GetName().Name ?? "S2FOW";
        string examplePath = Path.Combine(directoryPath, $"{assemblyName}.example.json");
        string exampleContent = BuildGuidedJson(new S2FOWConfig(), includeGeneratedNotice: false);
        string guidedContent = BuildGuidedJson(currentConfig, includeGeneratedNotice: true);

        try
        {
            File.WriteAllText(examplePath, exampleContent);
        }
        catch
        {
            PluginDiagnostics.RecordConfigIoError();
            // Example config sync failure should never stop plugin startup.
        }

        if (!configPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return;

        // Only rewrite the real config if it has actually changed.
        try
        {
            string existingContent = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
            if (!string.Equals(existingContent, guidedContent, StringComparison.Ordinal))
                File.WriteAllText(configPath, guidedContent);
        }
        catch
        {
            PluginDiagnostics.RecordConfigIoError();
            // If we cannot rewrite the config file, keep using the parsed config in memory.
        }
    }

    /// <summary>
    /// Builds the full guided JSON string with inline comments explaining every setting.
    /// Each setting group (General, AntiWallhack, etc.) gets a section comment.
    /// </summary>
    private static string BuildGuidedJson(S2FOWConfig config, bool includeGeneratedNotice)
    {
        StringBuilder builder = new();

        builder.AppendLine($"// {GuidedMarker}");
        if (includeGeneratedNotice)
        {
            builder.AppendLine("// This file stays valid JSON because CounterStrikeSharp allows // comments.");
        }
        else
        {
            builder.AppendLine("// Example config copied when no real config file exists yet.");
        }

        builder.AppendLine("// Source-backed defaults: canonical hitboxes come from checked-in local CS2 extraction data.");
        builder.AppendLine("// Defaults use 64-friendly numbers where practical. TargetPoints movement lead is disabled by default.");
        builder.AppendLine("// Start with General and AntiWallhack. Only change TargetPoints, ViewerRays, or Performance when solving a specific issue.");
        builder.AppendLine("{");
        builder.AppendLine($"  \"ConfigVersion\": {config.Version},");
        builder.AppendLine();

        // General settings.
        builder.AppendLine("  // Main plugin behavior.");
        builder.AppendLine("  \"General\": {");
        builder.AppendLine("    // Turns S2FOW on or off.");
        builder.AppendLine($"    \"Enabled\": {JsonBool(config.General.Enabled)},");
        builder.AppendLine("    // Keeps a just-killed player visible briefly so death transitions feel cleaner without holding the death spot too long.");
        builder.AppendLine($"    \"DeathVisibilityDurationTicks\": {JsonInt(config.General.DeathVisibilityDurationTicks)},");
        builder.AppendLine("    // Reveals everyone briefly when the round begins so the plugin can settle cleanly.");
        builder.AppendLine($"    \"RoundStartRevealDurationTicks\": {JsonInt(config.General.RoundStartRevealDurationTicks)}");
        builder.AppendLine("  },");
        builder.AppendLine();

        // Anti-wallhack / smoke settings.
        builder.AppendLine("  // Main anti-ESP settings.");
        builder.AppendLine("  \"AntiWallhack\": {");
        builder.AppendLine("    // Lets smoke block visibility for S2FOW decisions.");
        builder.AppendLine($"    \"SmokeBlocksWallhack\": {JsonBool(config.AntiWallhack.SmokeBlocksWallhack)},");
        builder.AppendLine("    // Server-side sphere approximation of smoke size. CS2 smoke is volumetric (no fixed radius).");
        builder.AppendLine("    // 144 matches the CS2-scale smoke radius used by S2FOW. Tune if smokes feel too thin or too wide.");
        builder.AppendLine($"    \"SmokeBlockRadius\": {JsonFloat(config.AntiWallhack.SmokeBlockRadius)},");
        builder.AppendLine("    // How long a smoke blocks vision. CS2 smoke is about 20 seconds; S2FOW uses 19.25 seconds.");
        builder.AppendLine("    // At 64Hz: 1232 ticks = 19.25 seconds.");
        builder.AppendLine($"    \"SmokeLifetimeTicks\": {JsonInt(config.AntiWallhack.SmokeLifetimeTicks)},");
        builder.AppendLine("    // Smoke bloom duration: how many ticks the smoke takes to grow from its starting size to full size.");
        builder.AppendLine("    // Blocking starts immediately at detonation at SmokeGrowthStartFraction of full radius.");
        builder.AppendLine("    // 192 ticks = 3 seconds at 64Hz.");
        builder.AppendLine($"    \"SmokeBloomDurationTicks\": {JsonInt(config.AntiWallhack.SmokeBloomDurationTicks)},");
        builder.AppendLine("    // Starting blocking size as a fraction of full radius. 0.5 = starts at 50% (72 units) and grows to 100% (144 units).");
        builder.AppendLine($"    \"SmokeGrowthStartFraction\": {JsonFloat(config.AntiWallhack.SmokeGrowthStartFraction)}");
        builder.AppendLine("  },");
        builder.AppendLine();

        // Target point settings.
        builder.AppendLine("  // Enemy target points. These move the enemy LOS points a little forward.");
        builder.AppendLine("  \"TargetPoints\": {");
        builder.AppendLine("    // Uses front/side/back FOV tiers: front = full points, side = original 19, back = AABB only.");
        builder.AppendLine($"    \"FovCullingEnabled\": {JsonBool(config.TargetPoints.FovCullingEnabled)},");
        builder.AppendLine("    // Front half-angle for the full tuned LOS point set.");
        builder.AppendLine($"    \"FullLosHalfAngleDegrees\": {JsonFloat(config.TargetPoints.FullLosHalfAngleDegrees)},");
        builder.AppendLine("    // Side half-angle limit for original 19 points; targets behind this use AABB only.");
        builder.AppendLine($"    \"OriginalOnlyHalfAngleDegrees\": {JsonFloat(config.TargetPoints.OriginalOnlyHalfAngleDegrees)},");
        builder.AppendLine("    // Uses distance tiers: close = full points, mid = original 19, far = AABB only.");
        builder.AppendLine($"    \"DistanceTieringEnabled\": {JsonBool(config.TargetPoints.DistanceTieringEnabled)},");
        builder.AppendLine("    // Targets at or below this horizontal distance can use the full tuned LOS point set.");
        builder.AppendLine($"    \"FullLosDistanceUnits\": {JsonFloat(config.TargetPoints.FullLosDistanceUnits)},");
        builder.AppendLine("    // Targets at or above this horizontal distance use AABB only.");
        builder.AppendLine($"    \"AabbOnlyDistanceUnits\": {JsonFloat(config.TargetPoints.AabbOnlyDistanceUnits)},");
        builder.AppendLine("    // Enemy running forward: move target points this many ticks ahead.");
        builder.AppendLine($"    \"ForwardLookAheadTicks\": {JsonFloat(config.TargetPoints.ForwardLookAheadTicks)},");
        builder.AppendLine("    // Enemy moving left/right: move target points this many ticks ahead.");
        builder.AppendLine($"    \"SideLookAheadTicks\": {JsonFloat(config.TargetPoints.SideLookAheadTicks)},");
        builder.AppendLine("    // Never move enemy target points farther than this many units. Default is 0.0.");
        builder.AppendLine($"    \"MaxMoveUnits\": {JsonFloat(config.TargetPoints.MaxMoveUnits)},");
        builder.AppendLine("    // Enemy jumping/falling: move target points up/down this many ticks ahead.");
        builder.AppendLine($"    \"UpDownLookAheadTicks\": {JsonFloat(config.TargetPoints.UpDownLookAheadTicks)},");
        builder.AppendLine("    // Never move enemy target points up/down farther than this many units.");
        builder.AppendLine($"    \"MaxUpDownUnits\": {JsonFloat(config.TargetPoints.MaxUpDownUnits)}");
        builder.AppendLine("  },");
        builder.AppendLine();

        builder.AppendLine("  // Viewer rays. These move the ray origin from your eye a little when you move.");
        builder.AppendLine("  \"ViewerRays\": {");
        builder.AppendLine("    // Do not apply any prediction until the observer is moving at least this fast (units/sec).");
        builder.AppendLine($"    \"StartAfterSpeed\": {JsonFloat(config.ViewerRays.StartAfterSpeed)},");
        builder.AppendLine("    // Forward/back prediction in ticks. At 250 u/s: 8 x (250 x 0.015625) = 31.25 units.");
        builder.AppendLine($"    \"ForwardLookAheadTicks\": {JsonFloat(config.ViewerRays.ForwardLookAheadTicks)},");
        builder.AppendLine("    // Strafe prediction in ticks. Intentionally large (64) so MaxMoveUnits is always the binding clamp.");
        builder.AppendLine("    // Effective lead = min(speed x 0.015625 x 64, MaxMoveUnits). Gives all players full MaxMoveUnits prediction.");
        builder.AppendLine($"    \"SideLookAheadTicks\": {JsonFloat(config.ViewerRays.SideLookAheadTicks)},");
        builder.AppendLine("    // Jump prediction in ticks. Same clamp-saturation design as SideLookAheadTicks.");
        builder.AppendLine("    // sv_jump_impulse = 301.993 u/s. 64 ticks would predict 302 units -- always clamped to MaxMoveUnits.");
        builder.AppendLine($"    \"JumpLookAheadTicks\": {JsonFloat(config.ViewerRays.JumpLookAheadTicks)},");
        builder.AppendLine("    // Hard cap (units) on all prediction axes independently. 64 = one player-width.");
        builder.AppendLine($"    \"MaxMoveUnits\": {JsonFloat(config.ViewerRays.MaxMoveUnits)}");
        builder.AppendLine("  },");
        builder.AppendLine();

        // Debug settings.
        builder.AppendLine("  // Debug visuals. Keep these off during normal live play and only enable them briefly while investigating.");
        builder.AppendLine("  \"Debug\": {");
        builder.AppendLine("    // Shows per-observer trace counters.");
        builder.AppendLine($"    \"ShowRayCount\": {JsonBool(config.Debug.ShowRayCount)},");
        builder.AppendLine("    // Draws only rays actually submitted to RayTrace by the solver.");
        builder.AppendLine($"    \"ShowRayLines\": {JsonBool(config.Debug.ShowRayLines)},");
        builder.AppendLine("    // Draws the target point set S2FOW considers for LOS/smoke decisions.");
        builder.AppendLine($"    \"ShowTargetPoints\": {JsonBool(config.Debug.ShowTargetPoints)}");
        builder.AppendLine("  },");
        builder.AppendLine();

        // Performance settings.
        builder.AppendLine("  // Advanced performance and engine behavior.");
        builder.AppendLine("  \"Performance\": {");
        builder.AppendLine("    // Hard cap for total raycasts per frame. 0 = unlimited and is the recommended no-delay default.");
        builder.AppendLine($"    \"MaxRaycastsPerFrame\": {JsonInt(config.Performance.MaxRaycastsPerFrame)},");
        builder.AppendLine("    // Cheap smoke pre-filter. False only forces the full smoke check every time; it does not delay decisions.");
        builder.AppendLine($"    \"SmokeBatchPreFilterEnabled\": {JsonBool(config.Performance.SmokeBatchPreFilterEnabled)},");
        builder.AppendLine();
        builder.AppendLine("    // Ray hit interpretation. Fraction is 63/64. Near endpoint hits count as visible to reduce pop-in.");
        builder.AppendLine($"    \"RayHitFractionThreshold\": {JsonFloat(config.Performance.RayHitFractionThreshold)},");
        builder.AppendLine($"    \"RayHitDistanceThreshold\": {JsonFloat(config.Performance.RayHitDistanceThreshold)},");
        builder.AppendLine($"    \"ViewerHeightOffset\": {JsonFloat(config.Performance.ViewerHeightOffset)},");
        builder.AppendLine($"    \"HitboxPaddingUp\": {JsonFloat(config.Performance.HitboxPaddingUp)},");
        builder.AppendLine($"    \"HitboxPaddingSide\": {JsonFloat(config.Performance.HitboxPaddingSide)},");
        builder.AppendLine($"    \"HitboxPaddingDown\": {JsonFloat(config.Performance.HitboxPaddingDown)},");
        builder.AppendLine();
        builder.AppendLine("    // Aim assist reveal: the point where your aim ray stops reveals enemies within this radius.");
        builder.AppendLine($"    \"AimRevealRadius\": {JsonFloat(config.Performance.AimRevealRadius)},");
        builder.AppendLine($"    \"AimRayDistance\": {JsonFloat(config.Performance.AimRayDistance)}");
        builder.AppendLine("  }");
        builder.AppendLine("}");

        return builder.ToString();
    }

    // ────────────────────────────────────────────────────────────────────────
    //  JSON formatting helpers
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Formats a boolean as JSON ("true" or "false").</summary>
    private static string JsonBool(bool value) => value ? "true" : "false";

    /// <summary>Formats an integer as a culture-invariant JSON number.</summary>
    private static string JsonInt(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats a float as a culture-invariant JSON number with up to 4 decimals.</summary>
    private static string JsonFloat(float value) => value.ToString("0.0###", CultureInfo.InvariantCulture);
}
