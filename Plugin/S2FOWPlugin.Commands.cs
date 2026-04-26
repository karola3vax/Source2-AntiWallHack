using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using S2FOW.Config;
using S2FOW.Core;
using S2FOW.Util;

namespace S2FOW;

/// <summary>
/// Admin console commands — provides server operators with tools to monitor
/// and control the plugin at runtime.
///
/// Commands:
///   css_fow_stats  → Prints a detailed health summary (ray counts, timing, errors).
///   css_fow_toggle → Turns the anti-wallhack protection on or off instantly.
///
/// Both commands require @css/root (server administrator) permissions.
/// </summary>
public partial class S2FOWPlugin
{
    /// <summary>
    /// Prints a comprehensive health summary to the admin's console.
    /// Includes performance metrics, decision breakdowns, and error counters.
    /// </summary>
    [RequiresPermissions("@css/root")]
    private void OnFowStats(CCSPlayerController? player, CommandInfo command)
    {
        if (_perfMonitor == null)
        {
            Reply(command, "Stats are not ready yet.");
            return;
        }

        ReplyMany(command, BuildStatsLines());
    }

    /// <summary>
    /// Toggles the anti-wallhack protection on or off.
    /// When disabled, all entities are transmitted normally (no hiding).
    /// </summary>
    [RequiresPermissions("@css/root")]
    private void OnFowToggle(CCSPlayerController? player, CommandInfo command)
    {
        Config.General.Enabled = !Config.General.Enabled;
        ResetRuntimeState(logMapName: null);
        Reply(command, $"Protection {(Config.General.Enabled ? "enabled" : "disabled")}.");
    }

    /// <summary>
    /// Builds the multi-line stats output shown by css_fow_stats.
    /// Each line covers a different aspect of the plugin's health.
    /// </summary>
    private IEnumerable<string> BuildStatsLines()
    {
        // Line 1: Overall performance averages (frame time, rays per frame, budget hits).
        yield return _perfMonitor!.GetStatsString();

        // Line 2: Config version and current round phase.
        yield return PluginOutput.Prefix(
            $"Config v{Config.Version} | phase {_currentRoundPhase}");

        // Line 3: Detailed timing — how much time the plugin uses vs frame interval.
        yield return PluginOutput.Prefix(
            $"Timing: plugin self {_perfMonitor.AvgFrameMicroseconds / 1000.0:F3}ms avg | " +
            $"frame interval {_perfMonitor.AvgFrameIntervalMicroseconds / 1000.0:F3}ms avg");

        // Line 4: Lifetime totals — total rays, visibility checks, and budget events since last reset.
        yield return PluginOutput.Prefix(
            $"Lifetime work: {_perfMonitor.TotalRaycasts} rays | " +
            $"{_perfMonitor.TotalEvaluations} visibility checks | " +
            $"{_perfMonitor.TotalBudgetExceeded} budget events");

        // Line 5: How many times a target was force-shown because the ray budget ran out.
        yield return PluginOutput.Prefix(
            $"Fallback decisions: forced visible {_visibilityManager?.BudgetFallbackOpenTransmitCount ?? 0}");

        // Crash-prevention paths and entity closure coverage.
        yield return PluginOutput.Prefix(
            $"Safety: unsafe hides skipped {_perfMonitor.UnsafeHideSkipped} | " +
            $"closure overflows {_playerStateCache?.AssociatedEntityOverflowCount ?? 0} | " +
            $"scene children {_playerStateCache?.SceneChildEntitiesCollected ?? 0} | " +
            $"invalid-controller clears {_perfMonitor.InvalidControllerPawnClears} | " +
            $"dead force-transmits {_perfMonitor.DeadForceTransmits} | " +
            $"orphan cleanups {_perfMonitor.OrphanClosureCleanups}");

        // Line 6: Suppressed warning counters (entity access failures that were caught silently).
        yield return PluginOutput.Prefix(
            $"Warning counters: {_playerStateCache?.SuppressedWarningCount ?? 0} suppressed | " +
            $"{_playerStateCache?.DependentEntityCollectionFailureCount ?? 0} collection failures | " +
            $"{_suppressedGameRulesErrors} gamerules");

        // Line 7: Config I/O error count (failed config file writes).
        yield return PluginOutput.Prefix(
            $"Suppressed catches: config I/O {PluginDiagnostics.ConfigIoErrorCount}");

        // Line 8: Live state — active smokes, RayTrace status, and protection toggle.
        yield return PluginOutput.Prefix(
            $"Live state: smokes {_smokeTracker?.ActiveCount ?? 0} | " +
            $"ray tracing ready: {_initialized} | protection enabled: {Config.General.Enabled}");

        // Line 9: What entity types are currently being protected.
        yield return PluginOutput.Prefix(BuildStartupCoverageLine());
    }
}
