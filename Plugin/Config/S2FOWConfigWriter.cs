using System.Globalization;
using System.Text;
using CounterStrikeSharp.API.Modules.Extensions;
using S2FOW.Util;

namespace S2FOW.Config;

/// <summary>
/// Generates guided JSON config files with short comments for server owners.
/// CounterStrikeSharp accepts // comments in plugin config JSON.
/// </summary>
internal static class S2FOWConfigWriter
{
    private const string GuidedMarker = "S2FOW guided config";

    public static void EnsureGuidedJsonFiles(S2FOWConfig currentConfig)
    {
        string configPath = currentConfig.GetConfigPath();
        string? directoryPath = Path.GetDirectoryName(configPath);
        if (string.IsNullOrWhiteSpace(directoryPath))
            return;

        Directory.CreateDirectory(directoryPath);

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
        }

        if (!configPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            string existingContent = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
            if (!string.Equals(existingContent, guidedContent, StringComparison.Ordinal))
                File.WriteAllText(configPath, guidedContent);
        }
        catch
        {
            PluginDiagnostics.RecordConfigIoError();
        }
    }

    private static string BuildGuidedJson(S2FOWConfig config, bool includeGeneratedNotice)
    {
        StringBuilder builder = new();

        builder.AppendLine($"// {GuidedMarker}");
        builder.AppendLine(includeGeneratedNotice
            ? "// S2FOW rewrites this file with current setting names after it loads."
            : "// Example config with the default S2FOW settings.");
        builder.AppendLine("// Most servers only need Main and SmokeVisibility.");
        builder.AppendLine("// Tick examples assume a 64 tick server: 64 ticks = about 1 second.");
        builder.AppendLine("{");
        builder.AppendLine($"  \"ConfigVersion\": {JsonInt(config.Version)},");
        builder.AppendLine();

        builder.AppendLine("  // Main on/off behavior.");
        builder.AppendLine("  \"Main\": {");
        builder.AppendLine("    // Turn S2FOW protection on or off.");
        builder.AppendLine($"    \"ProtectionEnabled\": {JsonBool(config.General.Enabled)},");
        builder.AppendLine("    // Keep a killed player visible briefly so the death does not pop away. 128 ticks = about 2 seconds.");
        builder.AppendLine($"    \"KeepDeadPlayersVisibleTicks\": {JsonInt(config.General.DeathVisibilityDurationTicks)},");
        builder.AppendLine("    // Show everyone briefly when live play starts. 32 ticks = about 0.5 seconds.");
        builder.AppendLine($"    \"ShowEveryoneAtRoundStartTicks\": {JsonInt(config.General.RoundStartRevealDurationTicks)}");
        builder.AppendLine("  },");
        builder.AppendLine();

        builder.AppendLine("  // Smoke hiding behavior.");
        builder.AppendLine("  \"SmokeVisibility\": {");
        builder.AppendLine("    // Hide players when smoke blocks the viewer's sight.");
        builder.AppendLine($"    \"HidePlayersBehindSmoke\": {JsonBool(config.AntiWallhack.SmokeBlocksWallhack)},");
        builder.AppendLine("    // Approximate smoke size in Source 2 units.");
        builder.AppendLine($"    \"SmokeSizeUnits\": {JsonFloat(config.AntiWallhack.SmokeBlockRadius)},");
        builder.AppendLine("    // How long smoke can hide players. 1232 ticks = about 19.25 seconds.");
        builder.AppendLine($"    \"SmokeLastsTicks\": {JsonInt(config.AntiWallhack.SmokeLifetimeTicks)},");
        builder.AppendLine("    // How long smoke takes to grow. 192 ticks = about 3 seconds.");
        builder.AppendLine($"    \"SmokeGrowsTicks\": {JsonInt(config.AntiWallhack.SmokeBloomDurationTicks)},");
        builder.AppendLine("    // Starting smoke size as a fraction of full size. 0.25 means 25 percent.");
        builder.AppendLine($"    \"SmokeStartsAtSizeFraction\": {JsonFloat(config.AntiWallhack.SmokeGrowthStartFraction)}");
        builder.AppendLine("  },");
        builder.AppendLine();

        builder.AppendLine("  // Points on an enemy body that S2FOW checks for visibility.");
        builder.AppendLine("  \"EnemyCheckPoints\": {");
        builder.AppendLine("    // Use fewer body checks when an enemy is far outside the viewer's aim direction.");
        builder.AppendLine($"    \"UseFewerChecksOutsideView\": {JsonBool(config.TargetPoints.FovCullingEnabled)},");
        builder.AppendLine("    // Front view angle that still gets the full body check.");
        builder.AppendLine($"    \"FullCheckViewHalfAngleDegrees\": {JsonFloat(config.TargetPoints.FullLosHalfAngleDegrees)},");
        builder.AppendLine("    // Wider side view angle that gets a reduced body check.");
        builder.AppendLine($"    \"ReducedCheckViewHalfAngleDegrees\": {JsonFloat(config.TargetPoints.OriginalOnlyHalfAngleDegrees)},");
        builder.AppendLine("    // Use fewer body checks when an enemy is far away.");
        builder.AppendLine($"    \"UseFewerChecksFarAway\": {JsonBool(config.TargetPoints.DistanceTieringEnabled)},");
        builder.AppendLine("    // Distance for the full body check.");
        builder.AppendLine($"    \"FullCheckDistanceUnits\": {JsonFloat(config.TargetPoints.FullLosDistanceUnits)},");
        builder.AppendLine("    // Distance where S2FOW only checks the enemy's bounding box.");
        builder.AppendLine($"    \"BoxOnlyDistanceUnits\": {JsonFloat(config.TargetPoints.AabbOnlyDistanceUnits)},");
        builder.AppendLine("    // Predict enemy forward/back movement by this many ticks.");
        builder.AppendLine($"    \"EnemyForwardPredictionTicks\": {JsonInt(config.TargetPoints.ForwardLookAheadTicks)},");
        builder.AppendLine("    // Predict enemy left/right movement by this many ticks.");
        builder.AppendLine($"    \"EnemySidePredictionTicks\": {JsonInt(config.TargetPoints.SideLookAheadTicks)},");
        builder.AppendLine("    // Maximum enemy movement prediction distance.");
        builder.AppendLine($"    \"EnemyPredictionMaxUnits\": {JsonFloat(config.TargetPoints.MaxMoveUnits)},");
        builder.AppendLine("    // Predict enemy jump/fall movement by this many ticks.");
        builder.AppendLine($"    \"EnemyVerticalPredictionTicks\": {JsonInt(config.TargetPoints.UpDownLookAheadTicks)},");
        builder.AppendLine("    // Maximum enemy jump/fall prediction distance.");
        builder.AppendLine($"    \"EnemyVerticalPredictionMaxUnits\": {JsonFloat(config.TargetPoints.MaxUpDownUnits)}");
        builder.AppendLine("  },");
        builder.AppendLine();

        builder.AppendLine("  // Small viewer eye prediction so fast peeks do not hide visible players too early.");
        builder.AppendLine("  \"ViewerEyePrediction\": {");
        builder.AppendLine("    // Do not predict the viewer's eye position until they move at least this fast.");
        builder.AppendLine($"    \"MinimumSpeedForEyePrediction\": {JsonFloat(config.ViewerRays.StartAfterSpeed)},");
        builder.AppendLine("    // Predict viewer forward/back movement by this many ticks.");
        builder.AppendLine($"    \"EyeForwardPredictionTicks\": {JsonInt(config.ViewerRays.ForwardLookAheadTicks)},");
        builder.AppendLine("    // Predict viewer left/right movement by this many ticks.");
        builder.AppendLine($"    \"EyeSidePredictionTicks\": {JsonInt(config.ViewerRays.SideLookAheadTicks)},");
        builder.AppendLine("    // Predict viewer jump movement by this many ticks.");
        builder.AppendLine($"    \"EyeJumpPredictionTicks\": {JsonInt(config.ViewerRays.JumpLookAheadTicks)},");
        builder.AppendLine("    // Maximum viewer eye prediction distance.");
        builder.AppendLine($"    \"EyePredictionMaxUnits\": {JsonFloat(config.ViewerRays.MaxMoveUnits)}");
        builder.AppendLine("  },");
        builder.AppendLine();

        builder.AppendLine("  // Debug displays. Keep these off during normal play.");
        builder.AppendLine("  \"Debug\": {");
        builder.AppendLine("    // Show a small HUD with S2FOW workload and decisions.");
        builder.AppendLine($"    \"ShowDebugHud\": {JsonBool(config.Debug.ShowRayCount)},");
        builder.AppendLine("    // Draw the sight checks S2FOW sends to RayTrace.");
        builder.AppendLine($"    \"ShowDebugRays\": {JsonBool(config.Debug.ShowRayLines)},");
        builder.AppendLine("    // Draw the body points S2FOW is checking.");
        builder.AppendLine($"    \"ShowDebugPoints\": {JsonBool(config.Debug.ShowTargetPoints)}");
        builder.AppendLine("  },");
        builder.AppendLine();

        builder.AppendLine("  // Advanced settings. Change these only while solving a specific server issue.");
        builder.AppendLine("  \"Advanced\": {");
        builder.AppendLine("    // Maximum raycasts per frame. 0 = unlimited, which avoids delayed decisions.");
        builder.AppendLine($"    \"RaycastLimitPerFrame\": {JsonInt(config.Performance.MaxRaycastsPerFrame)},");
        builder.AppendLine("    // Skip expensive smoke checks when no smoke is near the viewer/enemy path.");
        builder.AppendLine($"    \"FastSmokePreCheck\": {JsonBool(config.Performance.SmokeBatchPreFilterEnabled)},");
        builder.AppendLine("    // Raise the viewer's eye check by this many units.");
        builder.AppendLine($"    \"EyeHeightOffsetUnits\": {JsonFloat(config.Performance.ViewerHeightOffset)},");
        builder.AppendLine("    // Expand enemy box checks upward by this many units.");
        builder.AppendLine($"    \"ExtraBoxHeightUpUnits\": {JsonFloat(config.Performance.HitboxPaddingUp)},");
        builder.AppendLine("    // Expand enemy box checks sideways by this many units.");
        builder.AppendLine($"    \"ExtraBoxWidthUnits\": {JsonFloat(config.Performance.HitboxPaddingSide)},");
        builder.AppendLine("    // Expand enemy box checks downward by this many units.");
        builder.AppendLine($"    \"ExtraBoxHeightDownUnits\": {JsonFloat(config.Performance.HitboxPaddingDown)},");
        builder.AppendLine("    // Reveal an enemy near where the viewer is aiming.");
        builder.AppendLine($"    \"AimRevealDistanceUnits\": {JsonFloat(config.Performance.AimRevealRadius)},");
        builder.AppendLine("    // Maximum distance for the aim reveal check.");
        builder.AppendLine($"    \"AimCheckDistanceUnits\": {JsonFloat(config.Performance.AimRayDistance)}");
        builder.AppendLine("  }");
        builder.AppendLine("}");

        return builder.ToString();
    }

    private static string JsonBool(bool value) => value ? "true" : "false";

    private static string JsonInt(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string JsonFloat(float value) => value.ToString("0.0###", CultureInfo.InvariantCulture);
}
