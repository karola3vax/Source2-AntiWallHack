using System.Globalization;
using System.Text;
using CounterStrikeSharp.API.Modules.Extensions;
using S2FOW.Util;

namespace S2FOW.Config;

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
            // Example config sync failure should never stop plugin startup.
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
            // If we cannot rewrite the config file, keep using the parsed config in memory.
        }
    }

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
        builder.AppendLine("// Tick assumptions follow CounterStrikeSharp's fixed-tick behavior, and smoke/radar policy");
        builder.AppendLine("// stays aligned with Valve's official smoke-blocking behavior notes. Avoid third-party retunes.");
        builder.AppendLine("// Start with General and AntiWallhack. Only change MovementPrediction or Performance when solving a specific issue.");
        builder.AppendLine("{");
        builder.AppendLine($"  \"ConfigVersion\": {config.Version},");
        builder.AppendLine();

        builder.AppendLine("  // Main plugin behavior.");
        builder.AppendLine("  \"General\": {");
        builder.AppendLine("    // Turns S2FOW on or off.");
        builder.AppendLine($"    \"Enabled\": {JsonBool(config.General.Enabled)},");
        builder.AppendLine("    // 0 = Strict (strongest protection), 1 = Balanced (recommended busy-server default), 2 = Compat (softest behavior).");
        builder.AppendLine($"    \"SecurityProfile\": {JsonInt((int)config.General.SecurityProfile)},");
        builder.AppendLine("    // Keeps a just-killed player visible briefly so death transitions feel cleaner without holding the death spot too long.");
        builder.AppendLine($"    \"DeathVisibilityDurationTicks\": {JsonInt(config.General.DeathVisibilityDurationTicks)},");
        builder.AppendLine("    // Auto profile: 0 = Auto-detect, 1 = Competitive5v5, 2 = Wingman, 3 = Casual, 4 = Deathmatch, 5 = Retake, 6 = Custom (no auto-tuning).");
        builder.AppendLine($"    \"AutoProfile\": {JsonInt((int)config.General.AutoProfile)},");
        builder.AppendLine("    // Reveals everyone briefly when the round begins so the plugin can settle cleanly.");
        builder.AppendLine($"    \"RoundStartRevealDurationTicks\": {JsonInt(config.General.RoundStartRevealDurationTicks)}");
        builder.AppendLine("  },");
        builder.AppendLine();

        builder.AppendLine("  // Main anti-ESP settings.");
        builder.AppendLine("  \"AntiWallhack\": {");
        builder.AppendLine("    // Hides enemy grenades and similar thrown objects when the thrower should stay hidden.");
        builder.AppendLine($"    \"BlockGrenadeESP\": {JsonBool(config.AntiWallhack.BlockGrenadeESP)},");
        builder.AppendLine("    // Stops hidden enemies from leaking through radar or minimap visibility.");
        builder.AppendLine($"    \"BlockRadarESP\": {JsonBool(config.AntiWallhack.BlockRadarESP)},");
        builder.AppendLine("    // Hides bullet impact effects from hidden enemies.");
        builder.AppendLine($"    \"BlockBulletImpactESP\": {JsonBool(config.AntiWallhack.BlockBulletImpactESP)},");
        builder.AppendLine("    // Keeps dropped weapons hidden briefly after death to reduce floor-weapon ESP leaks. 0 = off.");
        builder.AppendLine($"    \"BlockDroppedWeaponESPDurationTicks\": {JsonInt(config.AntiWallhack.BlockDroppedWeaponESPDurationTicks)},");
        builder.AppendLine("    // Reveals a recently dropped ground weapon again once the observer is close enough to loot it. 0 = always hide for full duration.");
        builder.AppendLine($"    \"DroppedWeaponRevealDistance\": {JsonFloat(config.AntiWallhack.DroppedWeaponRevealDistance)},");
        builder.AppendLine("    // Stops planted bomb radar or minimap leaks for CTs who cannot truly see the C4.");
        builder.AppendLine($"    \"BlockBombRadarESP\": {JsonBool(config.AntiWallhack.BlockBombRadarESP)},");
        builder.AppendLine("    // Hides the planted bomb world entity itself when the observer does not truly see it.");
        builder.AppendLine($"    \"HidePlantedBombEntityWhenNotVisible\": {JsonBool(config.AntiWallhack.HidePlantedBombEntityWhenNotVisible)},");
        builder.AppendLine("    // Lets smoke block visibility for S2FOW decisions.");
        builder.AppendLine($"    \"SmokeBlocksWallhack\": {JsonBool(config.AntiWallhack.SmokeBlocksWallhack)},");
        builder.AppendLine("    // Effective smoke size used by S2FOW.");
        builder.AppendLine($"    \"SmokeBlockRadius\": {JsonFloat(config.AntiWallhack.SmokeBlockRadius)},");
        builder.AppendLine("    // How long a smoke should be treated as active.");
        builder.AppendLine($"    \"SmokeLifetimeTicks\": {JsonInt(config.AntiWallhack.SmokeLifetimeTicks)},");
        builder.AppendLine("    // How long a new smoke takes to grow from its starting blocking size to full size. 48 ticks = 0.75 seconds.");
        builder.AppendLine($"    \"SmokeBlockDelayTicks\": {JsonInt(config.AntiWallhack.SmokeBlockDelayTicks)},");
        builder.AppendLine("    // Starting smoke size while it blooms. 0.5 means 50% of full size.");
        builder.AppendLine($"    \"SmokeGrowthStartFraction\": {JsonFloat(config.AntiWallhack.SmokeGrowthStartFraction)},");
        builder.AppendLine("    // Maximum range for crosshair-based reveal checks.");
        builder.AppendLine($"    \"CrosshairRevealDistance\": {JsonFloat(config.AntiWallhack.CrosshairRevealDistance)},");
        builder.AppendLine("    // Reveal radius around the crosshair. Larger feels more forgiving, but also reveals more often.");
        builder.AppendLine($"    \"CrosshairRevealRadius\": {JsonFloat(config.AntiWallhack.CrosshairRevealRadius)},");
        builder.AppendLine("    // Hard visibility distance limit. 0 = off.");
        builder.AppendLine($"    \"MaxVisibilityDistance\": {JsonFloat(config.AntiWallhack.MaxVisibilityDistance)},");
        builder.AppendLine("    // Keeps enemies visible briefly after a peek to reduce flicker.");
        builder.AppendLine($"    \"PeekGracePeriodTicks\": {JsonInt(config.AntiWallhack.PeekGracePeriodTicks)},");
        builder.AppendLine("    // Reveals a hidden enemy grenade once it gets very close. 0 = always hide.");
        builder.AppendLine($"    \"GrenadeRevealDistance\": {JsonFloat(config.AntiWallhack.GrenadeRevealDistance)}");
        builder.AppendLine("  },");
        builder.AppendLine();

        builder.AppendLine("  // Advanced movement prediction. Leave these alone unless visibility feels late or too aggressive.");
        builder.AppendLine("  \"MovementPrediction\": {");
        builder.AppendLine("    // Minimum speed before movement prediction starts.");
        builder.AppendLine($"    \"MinSpeed\": {JsonFloat(config.MovementPrediction.MinSpeed)},");
        builder.AppendLine("    // Enemy forward look-ahead.");
        builder.AppendLine($"    \"EnemyForwardLookaheadTicks\": {JsonFloat(config.MovementPrediction.EnemyForwardLookaheadTicks)},");
        builder.AppendLine("    // Enemy sideways look-ahead.");
        builder.AppendLine($"    \"EnemySidewaysLookaheadTicks\": {JsonFloat(config.MovementPrediction.EnemySidewaysLookaheadTicks)},");
        builder.AppendLine("    // Maximum lead distance for enemy prediction.");
        builder.AppendLine($"    \"EnemyMaxLeadDistance\": {JsonFloat(config.MovementPrediction.EnemyMaxLeadDistance)},");
        builder.AppendLine("    // Enemy jump and fall look-ahead.");
        builder.AppendLine($"    \"EnemyVerticalLookaheadTicks\": {JsonFloat(config.MovementPrediction.EnemyVerticalLookaheadTicks)},");
        builder.AppendLine("    // Maximum vertical lead for enemy prediction.");
        builder.AppendLine($"    \"EnemyVerticalMaxLeadDistance\": {JsonFloat(config.MovementPrediction.EnemyVerticalMaxLeadDistance)},");
        builder.AppendLine("    // Observer forward look-ahead.");
        builder.AppendLine($"    \"ViewerForwardLookaheadTicks\": {JsonFloat(config.MovementPrediction.ViewerForwardLookaheadTicks)},");
        builder.AppendLine("    // Observer jump anticipation.");
        builder.AppendLine($"    \"ViewerJumpAnticipationTicks\": {JsonFloat(config.MovementPrediction.ViewerJumpAnticipationTicks)},");
        builder.AppendLine("    // Observer strafe anticipation. Lower values reduce early reveals on wide ADAD movement.");
        builder.AppendLine($"    \"ViewerStrafeAnticipationTicks\": {JsonFloat(config.MovementPrediction.ViewerStrafeAnticipationTicks)},");
        builder.AppendLine("    // Maximum lead distance for observer prediction.");
        builder.AppendLine($"    \"ViewerMaxLeadDistance\": {JsonFloat(config.MovementPrediction.ViewerMaxLeadDistance)}");
        builder.AppendLine("  },");
        builder.AppendLine();

        builder.AppendLine("  // Debug visuals. Keep these off during normal live play and only enable them briefly while investigating.");
        builder.AppendLine("  \"Debug\": {");
        builder.AppendLine("    // Shows per-observer trace counters.");
        builder.AppendLine($"    \"ShowRayCount\": {JsonBool(config.Debug.ShowRayCount)},");
        builder.AppendLine("    // Draws the rays used by the solver.");
        builder.AppendLine($"    \"ShowRayLines\": {JsonBool(config.Debug.ShowRayLines)},");
        builder.AppendLine("    // Draws canonical hitbox primitive centers and AABB fallback corners.");
        builder.AppendLine($"    \"ShowTargetPoints\": {JsonBool(config.Debug.ShowTargetPoints)}");
        builder.AppendLine("  },");
        builder.AppendLine();

        builder.AppendLine("  // Advanced performance and engine behavior.");
        builder.AppendLine("  \"Performance\": {");
        builder.AppendLine("    // Hard cap for total raycasts per frame when adaptive budgeting is off. 0 = unlimited.");
        builder.AppendLine($"    \"MaxRaycastsPerFrame\": {JsonInt(config.Performance.MaxRaycastsPerFrame)},");
        builder.AppendLine("    // What to do when the frame ray budget runs out: 0 = CacheOnly, 1 = FailClosed, 2 = FailOpen.");
        builder.AppendLine($"    \"BudgetExceededPolicy\": {JsonInt((int)config.Performance.BudgetExceededPolicy)},");
        builder.AppendLine();
        builder.AppendLine("    // Distance-tiered evaluation thresholds (Hammer Units).");
        builder.AppendLine($"    \"CqbDistanceThreshold\": {JsonFloat(config.Performance.CqbDistanceThreshold)},");
        builder.AppendLine($"    \"MidDistanceThreshold\": {JsonFloat(config.Performance.MidDistanceThreshold)},");
        builder.AppendLine($"    \"FarDistanceThreshold\": {JsonFloat(config.Performance.FarDistanceThreshold)},");
        builder.AppendLine();
        builder.AppendLine("    // Horizontal FOV-based sampling. Full front cone = full checks, peripheral = half, rear = no LOS raycasts.");
        builder.AppendLine($"    \"FovSamplingEnabled\": {JsonBool(config.Performance.FovSamplingEnabled)},");
        builder.AppendLine($"    \"FullDetailFovHalfAngleDegrees\": {JsonFloat(config.Performance.FullDetailFovHalfAngleDegrees)},");
        builder.AppendLine($"    \"PeripheralFovHalfAngleDegrees\": {JsonFloat(config.Performance.PeripheralFovHalfAngleDegrees)},");
        builder.AppendLine();
        builder.AppendLine("    // Per-tier cache durations (ticks). Tuned for busy DM/retake servers while keeping close fights responsive.");
        builder.AppendLine($"    \"CqbHiddenCacheTicks\": {JsonInt(config.Performance.CqbHiddenCacheTicks)},");
        builder.AppendLine($"    \"CqbVisibleCacheTicks\": {JsonInt(config.Performance.CqbVisibleCacheTicks)},");
        builder.AppendLine($"    \"MidHiddenCacheTicks\": {JsonInt(config.Performance.MidHiddenCacheTicks)},");
        builder.AppendLine($"    \"MidVisibleCacheTicks\": {JsonInt(config.Performance.MidVisibleCacheTicks)},");
        builder.AppendLine($"    \"FarHiddenCacheTicks\": {JsonInt(config.Performance.FarHiddenCacheTicks)},");
        builder.AppendLine($"    \"FarVisibleCacheTicks\": {JsonInt(config.Performance.FarVisibleCacheTicks)},");
        builder.AppendLine($"    \"XFarHiddenCacheTicks\": {JsonInt(config.Performance.XFarHiddenCacheTicks)},");
        builder.AppendLine($"    \"XFarVisibleCacheTicks\": {JsonInt(config.Performance.XFarVisibleCacheTicks)},");
        builder.AppendLine();
        builder.AppendLine("    // Max CS2 hitbox primitives for far / extreme-far tiers. Lower = fewer long-range primitive checks.");
        builder.AppendLine($"    \"FarMaxCheckPoints\": {JsonInt(config.Performance.FarMaxCheckPoints)},");
        builder.AppendLine($"    \"XFarMaxCheckPoints\": {JsonInt(config.Performance.XFarMaxCheckPoints)},");
        builder.AppendLine();
        builder.AppendLine("    // Extends cache when both players are barely moving.");
        builder.AppendLine($"    \"VelocityCacheExtensionEnabled\": {JsonBool(config.Performance.VelocityCacheExtensionEnabled)},");
        builder.AppendLine($"    \"StationaryThresholdUnits\": {JsonFloat(config.Performance.StationaryThresholdUnits)},");
        builder.AppendLine();
        builder.AppendLine("    // Spreads cache expirations across ticks to prevent burst spikes.");
        builder.AppendLine($"    \"StaggeredCacheExpiryEnabled\": {JsonBool(config.Performance.StaggeredCacheExpiryEnabled)},");
        builder.AppendLine();
        builder.AppendLine("    // Scales ray budget automatically with alive player count.");
        builder.AppendLine($"    \"AdaptiveBudgetEnabled\": {JsonBool(config.Performance.AdaptiveBudgetEnabled)},");
        builder.AppendLine($"    \"BaseBudgetPerPlayer\": {JsonInt(config.Performance.BaseBudgetPerPlayer)},");
        builder.AppendLine($"    \"MaxAdaptiveBudget\": {JsonInt(config.Performance.MaxAdaptiveBudget)},");
        builder.AppendLine();
        builder.AppendLine("    // Ray hit interpretation.");
        builder.AppendLine($"    \"RayHitFractionThreshold\": {JsonFloat(config.Performance.RayHitFractionThreshold)},");
        builder.AppendLine($"    \"RayHitDistanceThreshold\": {JsonFloat(config.Performance.RayHitDistanceThreshold)},");
        builder.AppendLine($"    \"ViewerHeightOffset\": {JsonFloat(config.Performance.ViewerHeightOffset)},");
        builder.AppendLine($"    \"HitboxPaddingUp\": {JsonFloat(config.Performance.HitboxPaddingUp)},");
        builder.AppendLine($"    \"HitboxPaddingSide\": {JsonFloat(config.Performance.HitboxPaddingSide)},");
        builder.AppendLine($"    \"HitboxPaddingDown\": {JsonFloat(config.Performance.HitboxPaddingDown)},");
        builder.AppendLine("    // Reverse-link scanning. EntityRescanIntervalTicks is a light safety sweep, not the primary ownership tracker.");
        builder.AppendLine($"    \"EntityRescanIntervalTicks\": {JsonInt(config.Performance.EntityRescanIntervalTicks)},");
        builder.AppendLine($"    \"EntityRescanBudgetPerFrame\": {JsonInt(config.Performance.EntityRescanBudgetPerFrame)},");
        builder.AppendLine($"    \"MaxSceneTraversalDepth\": {JsonInt(config.Performance.MaxSceneTraversalDepth)},");
        builder.AppendLine($"    \"MaxSceneTraversalNodes\": {JsonInt(config.Performance.MaxSceneTraversalNodes)},");
        builder.AppendLine($"    \"HideUnresolvedEntitiesAfterTicks\": {JsonInt(config.Performance.HideUnresolvedEntitiesAfterTicks)}");
        builder.AppendLine("  }");
        builder.AppendLine("}");

        return builder.ToString();
    }

    private static string JsonBool(bool value) => value ? "true" : "false";
    private static string JsonInt(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string JsonFloat(float value) => value.ToString("0.0###", CultureInfo.InvariantCulture);
}
