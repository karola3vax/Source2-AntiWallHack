using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using S2FOW.Config;
using S2FOW.Core;
using S2FOW.Util;

namespace S2FOW;

public partial class S2FOWPlugin
{
    // Admin commands.
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

    [RequiresPermissions("@css/root")]
    private void OnFowToggle(CCSPlayerController? player, CommandInfo command)
    {
        if (_legacyConfigDetected)
        {
            Reply(command, "Cannot enable protection while an old config file is still in place. Update the config first.");
            return;
        }

        Config.General.Enabled = !Config.General.Enabled;
        ResetRuntimeState(logMapName: null);
        Reply(command, $"Protection {(Config.General.Enabled ? "enabled" : "disabled")}.");
    }

    [RequiresPermissions("@css/root")]
    private void OnFowProfile(CCSPlayerController? player, CommandInfo command)
    {
        if (_legacyConfigDetected)
        {
            Reply(command, "Profile controls are unavailable while a legacy config is still in place.");
            return;
        }

        if (command.ArgCount <= 1)
        {
            Reply(command,
                $"Configured auto-profile: {_baseConfig.General.AutoProfile} | " +
                $"active profile: {_activeProfile} | " +
                $"round phase: {_currentRoundPhase}");
            return;
        }

        if (!TryParseProfile(command.GetArg(1), out GameModeProfile requestedProfile))
        {
            Reply(command, "Unknown profile. Use: auto, competitive, wingman, casual, deathmatch, retake, custom.");
            return;
        }

        _baseConfig.General.AutoProfile = requestedProfile;
        ApplyRuntimeProfile(resetRuntimeState: true, logProfileChange: true);
        Reply(command,
            $"Configured auto-profile set to {_baseConfig.General.AutoProfile}. Active profile: {_activeProfile}.");
    }

    private IEnumerable<string> BuildStatsLines()
    {
        string qualityLevel = _visibilityManager?.CurrentQualityLevel.ToString() ?? "n/a";
        string qualityFrameMs = _visibilityManager != null
            ? _visibilityManager.AverageQualitySampleFrameTimeMs.ToString("F2")
            : "0.00";

        yield return _perfMonitor!.GetStatsString();

        yield return PluginOutput.Prefix(
            $"Profile: configured {_baseConfig.General.AutoProfile} | active {_activeProfile} | " +
            $"phase {_currentRoundPhase} | quality {qualityLevel} ({qualityFrameMs}ms avg)");

        yield return PluginOutput.Prefix(
            $"Timing: plugin self {_perfMonitor.AvgFrameMicroseconds / 1000.0:F3}ms avg | " +
            $"frame interval {_perfMonitor.AvgFrameIntervalMicroseconds / 1000.0:F3}ms avg");

        yield return PluginOutput.Prefix(
            $"Lifetime work: {_perfMonitor.TotalRaycasts} rays | " +
            $"{_perfMonitor.TotalEvaluations} visibility checks | " +
            $"{_perfMonitor.TotalCacheHits} cache hits | " +
            $"{_perfMonitor.TotalBudgetExceeded} budget events");

        yield return PluginOutput.Prefix(
            $"Fallback decisions: cache reuse {_visibilityManager?.BudgetFallbackCacheReuseCount ?? 0} | " +
            $"forced visible {_visibilityManager?.BudgetFallbackOpenTransmitCount ?? 0} | " +
            $"forced hidden {_visibilityManager?.BudgetCacheOnlyHideCount ?? 0}");

        yield return PluginOutput.Prefix(
            $"Hidden-link tracking: {_playerStateCache?.TrackedEntityCount ?? 0} tracked | " +
            $"{_playerStateCache?.DirtyEntityCount ?? 0} pending | " +
            $"{_playerStateCache?.UnresolvedParentCount ?? 0} unresolved | " +
            $"rescan running: {_playerStateCache?.FullReverseLinkRescanInProgress ?? false} | " +
            $"ctrl-hide: disabled (safe)");

        yield return PluginOutput.Prefix(
            $"Warning counters: {_playerStateCache?.SuppressedWarningCount ?? 0} suppressed | " +
            $"{_playerStateCache?.ReverseLinkDereferenceExceptionCount ?? 0} dereference faults | " +
            $"{_suppressedEntityLookupErrors} entity lookup | {_suppressedGameRulesErrors} gamerules");

        yield return PluginOutput.Prefix(
            $"Suppressed catches: spotted {_spottedStateScrubber?.SuppressedPawnStateErrors ?? 0}/{_spottedStateScrubber?.SuppressedPlantedC4StateErrors ?? 0}/{_spottedStateScrubber?.SuppressedBombBitErrors ?? 0} | " +
            $"projectiles {_projectileTracker?.EntityAccessFailureCount ?? 0}/{_projectileTracker?.OwnerResolveFailureCount ?? 0} | " +
            $"impacts {_impactTracker?.OwnerResolveFailureCount ?? 0} | " +
            $"config I/O {PluginDiagnostics.ConfigIoErrorCount} | auto-profile {PluginDiagnostics.AutoProfileProbeErrorCount}");

        yield return PluginOutput.Prefix(
            $"Live state: smokes {_smokeTracker?.ActiveCount ?? 0} | " +
            $"projectiles {_projectileTracker?.ActiveCount ?? 0} | " +
            $"ray tracing ready: {_initialized} | protection enabled: {Config.General.Enabled}");

        yield return PluginOutput.Prefix(BuildStartupCoverageLine());
    }
}
