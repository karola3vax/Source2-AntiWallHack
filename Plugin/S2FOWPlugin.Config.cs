using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using S2FOW.Config;
using S2FOW.Core;
using S2FOW.Util;

namespace S2FOW;

/// <summary>
/// Configuration management — handles config loading, version migrations, rebuilding
/// runtime state when settings change, and logging what changed between reloads.
/// </summary>
public partial class S2FOWPlugin
{
    // ────────────────────────────────────────────────────────────────────────
    //  Config version migration
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Updates old config values to match current defaults.
    /// When we change default values between versions, existing server configs
    /// would still have the old values. This method detects those old defaults
    /// and replaces them with the new ones, so server operators get improved
    /// behavior without manually editing their config files.
    /// </summary>
    private static void ApplyConfigMigrations(S2FOWConfig config)
    {
        const int TightHitboxPaddingConfigVersion = 27;
        const int TargetMaxMoveTwoConfigVersion = 28;
        const int SmokeTimingConfigVersion = 31;
        const int SmokeTuningConfigVersion = 32;

        // v27: Reduced hitbox padding for tighter visibility checks.
        if (config.Version < TightHitboxPaddingConfigVersion)
        {
            if (Math.Abs(config.Performance.HitboxPaddingUp - 16.0f) < 0.001f)
                config.Performance.HitboxPaddingUp = 8.0f;

            if (Math.Abs(config.Performance.HitboxPaddingDown - 4.0f) < 0.001f)
                config.Performance.HitboxPaddingDown = 0.0f;
        }

        // v28: Reduced target movement prediction cap.
        if (config.Version < TargetMaxMoveTwoConfigVersion)
        {
            if (Math.Abs(config.TargetPoints.MaxMoveUnits - 32.0f) < 0.001f)
                config.TargetPoints.MaxMoveUnits = 2.0f;
        }

        // v31: Match CS2's longer smoke duration but stop one second early to avoid over-hiding.
        if (config.Version < SmokeTimingConfigVersion)
        {
            if (config.AntiWallhack.SmokeLifetimeTicks == 1152)
                config.AntiWallhack.SmokeLifetimeTicks = 1216;

            if (config.AntiWallhack.SmokeBloomDurationTicks == 32)
                config.AntiWallhack.SmokeBloomDurationTicks = 256;
        }

        // v32: Tune smoke radius/timing and prevent aim reveal from seeing through smoke.
        if (config.Version < SmokeTuningConfigVersion)
        {
            if (Math.Abs(config.AntiWallhack.SmokeBlockRadius - 128.0f) < 0.001f)
                config.AntiWallhack.SmokeBlockRadius = 144.0f;

            if (config.AntiWallhack.SmokeLifetimeTicks == 1216)
                config.AntiWallhack.SmokeLifetimeTicks = 1232;

            if (config.AntiWallhack.SmokeBloomDurationTicks == 256)
                config.AntiWallhack.SmokeBloomDurationTicks = 192;
        }

        // v33 renamed the written config to plain-English sections and keys.
        // The in-memory property names stay stable; JSON compatibility aliases handle old files.
        config.Version = S2FOWConfig.CurrentConfigVersion;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Startup message builders
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Builds a one-line startup status string.</summary>
    private string BuildStartupConfigLine()
    {
        return PluginText.BuildStartupConfigLine(
            Config.General.Enabled,
            IsCrashRecoveryReady,
            Config.Version);
    }

    /// <summary>Builds a one-line summary of what S2FOW can hide.</summary>
    private string BuildStartupCoverageLine()
    {
        return PluginText.BuildCoverageLine(Config.AntiWallhack);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Runtime rebuilding
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds all engine components that depend on configuration values.
    /// Called when the config is parsed or reloaded. If the plugin was already
    /// running, optionally resets runtime state (smokes, debug visuals, etc.).
    /// </summary>
    private void RebuildRuntimeConfig(bool resetRuntimeState)
    {
        if (_initialized && _rayTrace != null)
        {
            _raycastEngine = new RaycastEngine(_rayTrace, Config, _perfMonitor);
            _visibilityManager = new VisibilityManager(
                _raycastEngine, _smokeTracker!,
                Config, _perfMonitor);
            _visibilityManager.SetRoundPhase(_currentRoundPhase);
            RebuildDebugRenderer();
            if (resetRuntimeState)
                ResetRuntimeState(logMapName: null);
        }
    }

    /// <summary>Creates a deep copy of a config so changes to one do not affect the other.</summary>
    private static S2FOWConfig CloneConfig(S2FOWConfig config)
    {
        return config.Clone();
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Game rules access
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tries to find the CS2 game rules entity, which tells us about the current
    /// round state (is it warmup? freeze time? is the bomb planted?).
    /// Returns false if the entity cannot be found (e.g., during map transitions).
    /// Exceptions are silently counted for diagnostics.
    /// </summary>
    private bool TryGetGameRules(out CCSGameRules? gameRules)
    {
        gameRules = null;

        try
        {
            foreach (var gameRulesProxy in Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules"))
            {
                if (gameRulesProxy == null || !gameRulesProxy.IsValid)
                    continue;

                gameRules = gameRulesProxy.GameRules;
                return gameRules != null;
            }

            return false;
        }
        catch
        {
            // Entity access can fail during map transitions.
            // Count it so operators can see if this happens repeatedly.
            _suppressedGameRulesErrors++;
            return false;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Config diff logging
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Compares the old and new config and logs any setting changes to the console.
    /// This helps server operators verify that a config reload had the intended effect.
    /// </summary>
    private void LogConfigDiff(S2FOWConfig previousConfig, S2FOWConfig currentConfig)
    {
        var changes = new List<string>(12);

        if (previousConfig.General.Enabled != currentConfig.General.Enabled)
            changes.Add($"ProtectionEnabled {previousConfig.General.Enabled}->{currentConfig.General.Enabled}");
        if (previousConfig.AntiWallhack.SmokeBlocksWallhack != currentConfig.AntiWallhack.SmokeBlocksWallhack)
            changes.Add($"HidePlayersBehindSmoke {previousConfig.AntiWallhack.SmokeBlocksWallhack}->{currentConfig.AntiWallhack.SmokeBlocksWallhack}");
        if (previousConfig.Debug.ShowTargetPoints != currentConfig.Debug.ShowTargetPoints)
            changes.Add($"ShowDebugPoints {previousConfig.Debug.ShowTargetPoints}->{currentConfig.Debug.ShowTargetPoints}");
        if (previousConfig.Debug.ShowRayLines != currentConfig.Debug.ShowRayLines)
            changes.Add($"ShowDebugRays {previousConfig.Debug.ShowRayLines}->{currentConfig.Debug.ShowRayLines}");
        if (previousConfig.Debug.ShowRayCount != currentConfig.Debug.ShowRayCount)
            changes.Add($"ShowDebugHud {previousConfig.Debug.ShowRayCount}->{currentConfig.Debug.ShowRayCount}");
        if (previousConfig.TargetPoints.FovCullingEnabled != currentConfig.TargetPoints.FovCullingEnabled)
            changes.Add($"UseFewerChecksOutsideView {previousConfig.TargetPoints.FovCullingEnabled}->{currentConfig.TargetPoints.FovCullingEnabled}");
        if (previousConfig.TargetPoints.DistanceTieringEnabled != currentConfig.TargetPoints.DistanceTieringEnabled)
            changes.Add($"UseFewerChecksFarAway {previousConfig.TargetPoints.DistanceTieringEnabled}->{currentConfig.TargetPoints.DistanceTieringEnabled}");

        if (changes.Count > 0)
            Log($"Settings updated: {string.Join(", ", changes)}");
    }
}
