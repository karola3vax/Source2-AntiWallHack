using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using S2FOW.Core;
using S2FOW.Util;

namespace S2FOW;

/// <summary>
/// Admin console commands for checking status and turning protection on/off.
/// Both commands require @css/root permissions.
/// </summary>
public partial class S2FOWPlugin
{
    [RequiresPermissions("@css/root")]
    private void OnFowStats(CCSPlayerController? player, CommandInfo command)
    {
        if (_perfMonitor == null)
        {
            Reply(command, "Status is not ready yet.");
            return;
        }

        ReplyMany(command, BuildStatsLines());
    }

    [RequiresPermissions("@css/root")]
    private void OnFowToggle(CCSPlayerController? player, CommandInfo command)
    {
        Config.General.Enabled = !Config.General.Enabled;
        ResetRuntimeState(logMapName: null);
        if (!Config.General.Enabled)
            ForceFullUpdateAllObserversNow(ObserverFullUpdateReason.Unhide | ObserverFullUpdateReason.Toggle);

        Reply(command, Config.General.Enabled
            ? "Protection enabled. S2FOW may hide enemies the player cannot see."
            : "Protection disabled. All players are sent normally; full updates were sent to clients.");
    }

    private IEnumerable<string> BuildStatsLines()
    {
        yield return PluginOutput.Prefix(
            $"Status: protection {(Config.General.Enabled ? "on" : "off")} | " +
            $"RayTrace {(_initialized ? "ready" : "not ready")} | " +
            $"round state {FriendlyRoundPhase(_currentRoundPhase)} | " +
            $"config schema v{Config.Version}");

        yield return PluginOutput.Prefix(
            $"Work: {_perfMonitor!.TotalEvaluations} player checks | " +
            $"{_perfMonitor.TotalRaycasts} raycasts | " +
            $"average plugin time {_perfMonitor.AvgFrameMicroseconds / 1000.0:F3} ms | " +
            $"average raycasts {_perfMonitor.AvgRaycastsPerFrame:F1}/frame | " +
            $"peak raycasts {_perfMonitor.PeakRaycastsPerFrame}");

        yield return PluginOutput.Prefix(
            $"Visibility decisions: players checked {_perfMonitor.TotalEvaluations} | " +
            $"hidden by smoke {_perfMonitor.HiddenBySmoke} | " +
            $"hidden by blocked sight {_perfMonitor.HiddenByLineOfSight} | " +
            $"shown to stay safe {_visibilityManager?.BudgetFallbackOpenTransmitCount ?? 0}");

        yield return PluginOutput.Prefix(
            $"Crash protection: full updates sent {_perfMonitor.FullUpdateSent} | " +
            $"throttled {_perfMonitor.FullUpdateThrottled} | " +
            $"failed {_perfMonitor.FullUpdateFailed} | " +
            $"requested {_perfMonitor.FullUpdateRequested} | " +
            $"combined {_perfMonitor.FullUpdateCoalesced}");

        yield return PluginOutput.Prefix(
            $"Crash protection reasons: hide {_perfMonitor.FullUpdateHideReasons} | " +
            $"show again {_perfMonitor.FullUpdateUnhideReasons} | " +
            $"leftover child cleanup {_perfMonitor.FullUpdateOrphanReasons} | " +
            $"safe fallback {_perfMonitor.FullUpdateSafetyReasons} | " +
            $"round state {_perfMonitor.FullUpdatePhaseReasons} | " +
            $"toggle {_perfMonitor.FullUpdateToggleReasons}");

        yield return PluginOutput.Prefix(
            $"Safety cleanup: unsafe hides skipped {_perfMonitor.UnsafeHideSkipped} | " +
            $"leftover child cleanup {_perfMonitor.OrphanClosureCleanups} | " +
            $"missing-controller cleanup {_perfMonitor.InvalidControllerPawnClears} | " +
            $"dead-player safe shows {_perfMonitor.DeadForceTransmits} | " +
            $"child list too large {_playerStateCache?.AssociatedEntityOverflowCount ?? 0}");

        yield return PluginOutput.Prefix(
            $"Warnings: config write failures {PluginDiagnostics.ConfigIoErrorCount} | " +
            $"entity read failures {_playerStateCache?.SuppressedWarningCount ?? 0} | " +
            $"incomplete child collection {_playerStateCache?.DependentEntityCollectionFailureCount ?? 0} | " +
            $"round-state read failures {_suppressedGameRulesErrors} | " +
            $"RayTrace failures {_perfMonitor.RayTraceFailures}");

        yield return PluginOutput.Prefix(
            $"Live map state: active smokes {_smokeTracker?.ActiveCount ?? 0} | " +
            $"frame interval {_perfMonitor.AvgFrameIntervalMicroseconds / 1000.0:F3} ms");

        yield return PluginOutput.Prefix(BuildStartupCoverageLine());
    }
}
