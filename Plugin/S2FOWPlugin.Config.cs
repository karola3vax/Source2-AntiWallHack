using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Utils;
using S2FOW.Config;
using S2FOW.Core;
using S2FOW.Util;
using System.Text.Json;

namespace S2FOW;

public partial class S2FOWPlugin
{
    private static void ApplyConfigMigrations(S2FOWConfig config)
    {
        const int PrimitiveLosConfigVersion = 20;
        const int BusyServerRetuneConfigVersion = 21;
        const int AutoConfigReworkConfigVersion = 22;

        if (config.Version < PrimitiveLosConfigVersion)
        {
            if (config.Performance.FarMaxCheckPoints == 18)
                config.Performance.FarMaxCheckPoints = 11;

            if (config.Performance.XFarMaxCheckPoints == 10)
                config.Performance.XFarMaxCheckPoints = 7;

            if (config.AntiWallhack.BlockDroppedWeaponESPDurationTicks == 256)
                config.AntiWallhack.BlockDroppedWeaponESPDurationTicks = 192;

            if (Math.Abs(config.AntiWallhack.DroppedWeaponRevealDistance - 1000.0f) < 0.001f ||
                Math.Abs(config.AntiWallhack.DroppedWeaponRevealDistance - 128.0f) < 0.001f)
                config.AntiWallhack.DroppedWeaponRevealDistance = 192.0f;

            if (Math.Abs(config.AntiWallhack.SmokeBlockRadius - 144.0f) < 0.001f)
                config.AntiWallhack.SmokeBlockRadius = 128.0f;

            if (config.AntiWallhack.SmokeBlockDelayTicks == 96)
                config.AntiWallhack.SmokeBlockDelayTicks = 80;

            if (Math.Abs(config.AntiWallhack.SmokeGrowthStartFraction - 0.3f) < 0.001f)
                config.AntiWallhack.SmokeGrowthStartFraction = 0.35f;

            if (Math.Abs(config.AntiWallhack.CrosshairRevealRadius - 72.0f) < 0.001f)
                config.AntiWallhack.CrosshairRevealRadius = 64.0f;

            if (config.Performance.FarHiddenCacheTicks == 10)
                config.Performance.FarHiddenCacheTicks = 12;

            if (config.Performance.FarVisibleCacheTicks == 3)
                config.Performance.FarVisibleCacheTicks = 4;

            if (config.Performance.XFarHiddenCacheTicks == 20)
                config.Performance.XFarHiddenCacheTicks = 24;

            if (config.Performance.XFarVisibleCacheTicks == 5)
                config.Performance.XFarVisibleCacheTicks = 6;

            if (config.Performance.BaseBudgetPerPlayer == 128)
                config.Performance.BaseBudgetPerPlayer = 96;

            if (config.Performance.MaxAdaptiveBudget == 6144)
                config.Performance.MaxAdaptiveBudget = 4096;

            if (Math.Abs(config.Performance.HitboxPaddingUp - 16.0f) < 0.001f)
                config.Performance.HitboxPaddingUp = 12.0f;

            if (Math.Abs(config.Performance.HitboxPaddingSide - 16.0f) < 0.001f)
                config.Performance.HitboxPaddingSide = 12.0f;
        }

        if (config.Version < BusyServerRetuneConfigVersion)
        {
            if (config.General.SecurityProfile == SecurityProfile.Strict)
                config.General.SecurityProfile = SecurityProfile.Balanced;

            if (config.General.DeathVisibilityDurationTicks == 160)
                config.General.DeathVisibilityDurationTicks = 128;

            if (config.AntiWallhack.BlockDroppedWeaponESPDurationTicks == 192)
                config.AntiWallhack.BlockDroppedWeaponESPDurationTicks = 128;

            if (config.AntiWallhack.SmokeBlockDelayTicks == 80)
                config.AntiWallhack.SmokeBlockDelayTicks = 64;

            if (Math.Abs(config.AntiWallhack.SmokeGrowthStartFraction - 0.35f) < 0.001f)
                config.AntiWallhack.SmokeGrowthStartFraction = 0.40f;

            if (Math.Abs(config.AntiWallhack.CrosshairRevealDistance - 3500.0f) < 0.001f)
                config.AntiWallhack.CrosshairRevealDistance = 3200.0f;

            if (config.AntiWallhack.PeekGracePeriodTicks == 16)
                config.AntiWallhack.PeekGracePeriodTicks = 12;

            if (Math.Abs(config.MovementPrediction.ViewerStrafeAnticipationTicks - 32.0f) < 0.001f)
                config.MovementPrediction.ViewerStrafeAnticipationTicks = 24.0f;

            if (config.Performance.MidHiddenCacheTicks == 5)
                config.Performance.MidHiddenCacheTicks = 6;

            if (config.Performance.MidVisibleCacheTicks == 3)
                config.Performance.MidVisibleCacheTicks = 4;

            if (config.Performance.FarHiddenCacheTicks == 12)
                config.Performance.FarHiddenCacheTicks = 14;

            if (config.Performance.FarVisibleCacheTicks == 4)
                config.Performance.FarVisibleCacheTicks = 5;

            if (config.Performance.XFarHiddenCacheTicks == 24)
                config.Performance.XFarHiddenCacheTicks = 28;

            if (config.Performance.XFarVisibleCacheTicks == 6)
                config.Performance.XFarVisibleCacheTicks = 8;

            if (config.Performance.ObserverPhaseSpreadTicks == 3)
                config.Performance.ObserverPhaseSpreadTicks = 4;

            if (config.Performance.EntityRescanIntervalTicks == 64)
                config.Performance.EntityRescanIntervalTicks = 96;
        }

        if (config.Version < AutoConfigReworkConfigVersion)
        {
            if (config.AntiWallhack.SmokeBlockDelayTicks == 64)
                config.AntiWallhack.SmokeBlockDelayTicks = 48;

            if (Math.Abs(config.AntiWallhack.SmokeGrowthStartFraction - 0.40f) < 0.001f)
                config.AntiWallhack.SmokeGrowthStartFraction = 0.50f;

            if (Math.Abs(config.Performance.FullDetailFovHalfAngleDegrees - 40.0f) < 0.001f)
                config.Performance.FullDetailFovHalfAngleDegrees = 45.0f;
        }

        config.Version = AutoConfigReworkConfigVersion;
    }

    private static bool DetectLegacyConfig(S2FOWConfig config)
    {
        if (config.Version < 10)
            return true;

        string configPath = config.GetConfigPath();
        string raw;
        try
        {
            if (!File.Exists(configPath))
                return false;

            raw = File.ReadAllText(configPath);
        }
        catch
        {
            PluginDiagnostics.RecordConfigIoError();
            return false;
        }

        ReadOnlySpan<string> legacyKeys =
        [
            "TargetPointLeadTicks",
            "TargetPointHorizontalLeadTicks",
            "TargetPointLeadMax",
            "TargetPointVerticalLeadTicks",
            "TargetPointVerticalLeadMax",
            "ObserverPeekLeadTicks",
            "ObserverPeekHorizontalLeadTicks",
            "ObserverPeekLeadMax",
            "ObserverPeekLeadMinSpeed",
            "VisibleCacheTTL",
            "HiddenCacheTTL",
            "GracePeriodTicks",
            "MaxRelevanceDistanceSqr",
            "SmokeBlockingEnabled",
            "SmokeRadius",
            "SmokeFormTicks",
            "SmokeBlockDurationTicks",
            "DrawAABB",
            "DrawRays",
            "DrawRayAmount",
            "AabbExpandXY",
            "AabbExpandDown",
            "AabbExpandUp",
            "RaycastPointCount",
            "RoundStartGraceTicks",
            "DeathForceTicks",
            "Basics",
            "Movement",
            "Visibility",
            "World",
            "DeathRevealTicks",
            "RoundStartRevealTicks",
            "PeekGraceTicks",
            "MaxDistanceUnits",
            "BlockThroughSmoke",
            "SmokeRadiusUnits",
            "SmokeWarmupTicks",
            "SmokeDurationTicks",
            "TargetForwardLeadTicks",
            "ObserverForwardLeadTicks",
            "DrawTraceLines",
            "DrawTargetPoints",
            "ShowTraceStats",
            "BoundsExpandSideUnits",
            "ObserverRayStartUpUnits",
            "MaxCheckPoints",
            "EnemyMaxPredictionDistanceUnits",
            "EnemySidePredictionTicks",
            "EnemyVerticalPredictionLimitUnits",
            "EnemyVerticalPredictionTicks",
            "StartViewerPredictionAtSpeedUnits",
            "ViewerForwardPredictionTicks",
            "ViewerMaxPredictionDistanceUnits",
            "ViewerSidePredictionTicks",
            "CatchupIsEnabledAtThisSpeed",
            "LOSPointsForwardFutureTicks",
            "PeekAssistRaysJumpFutureTicks",
            "PeekAssistRaysSidewaysFutureTicks",
            "LOSPointsForwardCatchup",
            "LOSPointsMaxCatchupDistanceUnits",
            "LOSPointsForwardCatchupLeadUnits",
            "LOSPointsForwardCatchupTicks",
            "LOSPointsSideCatchupTicks",
            "LOSPointsPlanarCatchupLeadUnits",
            "LOSPointsVerticalCatchupTicks",
            "LOSPointsVerticalCatchupLimitUnits",
            "ViewerRayForwardCatchupTicks",
            "ViewerRaySideCatchupTicks",
            "ViewerRayMaxCatchupDistanceUnits",
            "Vision",
            "Prediction",
            "Advanced",
            "HideEnemyProjectiles",
            "ProjectileRevealDistanceFromObserverUnits",
            "ScrubRadarSpottedState",
            "HideDroppedWeaponsAfterDeathForTicks",
            "HideImpactDecalsFromHiddenPlayers",
            "ScrubPlantedC4SpottedState",
            "SmokeMinRadiusFraction",
            "HideControllerWhenHidden",
            "CrosshairRevealDistanceUnits",
            "CrosshairRevealRadiusUnits",
            "HideBeyondDistanceUnits",
            "KeepEnemyVisibleAfterPeekForTicks",
            "SmokeBlockRadiusUnits",
            "SmokeStartsBlockingAfterTicks",
            "SmokeBlocksVision",
            "KeepKilledPlayerVisibleForTicks",
            "RevealEveryoneAtRoundStartForTicks",
            "MinSpeedForPrediction",
            "EnemyMovementPredictionTicks",
            "LOSPointsSidewaysFutureTicks",
            "LOSPointsLeadUnits",
            "LOSPointsJumpAndFallingFutureTicks",
            "LOSPointsJumpAndFallingLeadUnits",
            "PeekAssistRaysForwardFutureTicks",
            "JumpAnticipationTicks",
            "StrafeAnticipationTicks",
            "PeekAssistRaysLeadUnits",
            "EntityLinkFullRescanTicks",
            "EntityLinkRescanBudgetPerFrame",
            "VisibleResultCacheTicks",
            "HiddenResultCacheTicks",
            "VisibleRayHitFractionThreshold",
            "VisibleRayHitDistanceUnits",
            "ViewerRayStartHeightOffsetUnits",
            "ExtraUpHitboxUnits",
            "ExtraSideHitboxUnits",
            "ExtraDownHitboxUnits",
            "VisibleCacheDurationTicks",
            "HiddenCacheDurationTicks"
        ];

        for (int i = 0; i < legacyKeys.Length; i++)
        {
            string jsonToken = $"\"{legacyKeys[i]}\"";
            string tomlToken = $"{legacyKeys[i]} =";
            if (raw.Contains(jsonToken, StringComparison.Ordinal) ||
                raw.Contains(tomlToken, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static void LogLegacyConfigWarning()
    {
        string[] lines = PluginText.BuildLegacyConfigWarning(new S2FOWConfig().GetConfigPath());
        for (int i = 0; i < lines.Length; i++)
            Log(lines[i]);
    }

    private string BuildStartupProfileLine()
    {
        string baseLine = PluginText.BuildStartupProfileLine(
            Config.General.Enabled,
            Config.General.SecurityProfile,
            Config.Version);
        return $"{baseLine} | auto-profile {_baseConfig.General.AutoProfile} -> {_activeProfile}";
    }

    private string BuildStartupCoverageLine()
    {
        return PluginText.BuildCoverageLine(
            Config.AntiWallhack,
            ShouldBlockBombRadarESP(),
            ShouldHidePlantedBombEntity());
    }

    private void ApplyRuntimeProfile(bool resetRuntimeState, bool logProfileChange)
    {
        if (_legacyConfigDetected)
            return;

        GameModeProfile configuredProfile = _baseConfig.General.AutoProfile;
        GameModeProfile resolvedProfile = ResolveConfiguredProfile(configuredProfile);

        S2FOWConfig effectiveConfig = CloneConfig(_baseConfig);
        if (resolvedProfile != GameModeProfile.Custom)
            AutoConfigProfile.Apply(effectiveConfig, resolvedProfile);

        bool configChanged = !string.Equals(
            SerializeConfig(Config),
            SerializeConfig(effectiveConfig),
            StringComparison.Ordinal);
        bool profileChanged = resolvedProfile != _activeProfile;

        Config = effectiveConfig;
        _activeProfile = resolvedProfile;

        _playerStateCache?.Configure(Config);
        _spottedStateScrubber = new SpottedStateScrubber(Config);

        if (_initialized && _rayTrace != null && configChanged)
        {
            _raycastEngine = new RaycastEngine(_rayTrace, Config);
            _visibilityManager = new VisibilityManager(
                _raycastEngine, _visibilityCache!, _smokeTracker!,
                Config, _perfMonitor, Log);
            _visibilityManager.SetRoundPhase(_currentRoundPhase);
            RebuildDebugRenderer();
            if (resetRuntimeState)
                ResetRuntimeState(logMapName: null);
        }

        if (logProfileChange && (configChanged || profileChanged))
        {
            string profileLabel = configuredProfile == GameModeProfile.Auto
                ? $"{configuredProfile} -> {resolvedProfile}"
                : resolvedProfile.ToString();
            Log($"Auto-profile applied: {profileLabel}.");
        }
    }

    private GameModeProfile ResolveConfiguredProfile(GameModeProfile configuredProfile)
    {
        if (configuredProfile == GameModeProfile.Auto)
            return AutoConfigProfiler.Detect();

        return configuredProfile;
    }

    private static bool TryParseProfile(string rawValue, out GameModeProfile profile)
    {
        string normalized = rawValue.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "0":
            case "auto":
                profile = GameModeProfile.Auto;
                return true;
            case "1":
            case "competitive":
            case "competitive5v5":
            case "comp":
            case "5v5":
                profile = GameModeProfile.Competitive5v5;
                return true;
            case "2":
            case "wingman":
            case "2v2":
                profile = GameModeProfile.Wingman;
                return true;
            case "3":
            case "casual":
                profile = GameModeProfile.Casual;
                return true;
            case "4":
            case "deathmatch":
            case "dm":
                profile = GameModeProfile.Deathmatch;
                return true;
            case "5":
            case "retake":
            case "retakes":
                profile = GameModeProfile.Retake;
                return true;
            case "6":
            case "custom":
            case "manual":
                profile = GameModeProfile.Custom;
                return true;
            default:
                profile = GameModeProfile.Custom;
                return false;
        }
    }

    private static S2FOWConfig CloneConfig(S2FOWConfig config)
    {
        return config.Clone();
    }

    private static string SerializeConfig(S2FOWConfig config)
    {
        return JsonSerializer.Serialize(config);
    }

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
            _suppressedGameRulesErrors++;
            return false;
        }
    }

    private void LogConfigDiff(S2FOWConfig previousConfig, S2FOWConfig currentConfig)
    {
        var changes = new List<string>(12);

        if (previousConfig.General.Enabled != currentConfig.General.Enabled)
            changes.Add($"Enabled={previousConfig.General.Enabled}->{currentConfig.General.Enabled}");
        if (previousConfig.General.SecurityProfile != currentConfig.General.SecurityProfile)
            changes.Add($"SecurityProfile={previousConfig.General.SecurityProfile}->{currentConfig.General.SecurityProfile}");
        if (previousConfig.Performance.BudgetExceededPolicy != currentConfig.Performance.BudgetExceededPolicy)
            changes.Add($"BudgetExceededPolicy={previousConfig.Performance.BudgetExceededPolicy}->{currentConfig.Performance.BudgetExceededPolicy}");
        if (previousConfig.AntiWallhack.PeekGracePeriodTicks != currentConfig.AntiWallhack.PeekGracePeriodTicks)
            changes.Add($"PeekGracePeriodTicks={previousConfig.AntiWallhack.PeekGracePeriodTicks}->{currentConfig.AntiWallhack.PeekGracePeriodTicks}");
        if (previousConfig.AntiWallhack.SmokeBlocksWallhack != currentConfig.AntiWallhack.SmokeBlocksWallhack)
            changes.Add($"SmokeBlocksWallhack={previousConfig.AntiWallhack.SmokeBlocksWallhack}->{currentConfig.AntiWallhack.SmokeBlocksWallhack}");
        if (previousConfig.Debug.ShowTargetPoints != currentConfig.Debug.ShowTargetPoints)
            changes.Add($"ShowTargetPoints={previousConfig.Debug.ShowTargetPoints}->{currentConfig.Debug.ShowTargetPoints}");
        if (previousConfig.Debug.ShowRayLines != currentConfig.Debug.ShowRayLines)
            changes.Add($"ShowRayLines={previousConfig.Debug.ShowRayLines}->{currentConfig.Debug.ShowRayLines}");
        if (previousConfig.Debug.ShowRayCount != currentConfig.Debug.ShowRayCount)
            changes.Add($"ShowRayCount={previousConfig.Debug.ShowRayCount}->{currentConfig.Debug.ShowRayCount}");

        if (changes.Count > 0)
            Log($"Settings updated: {string.Join(", ", changes)}");
    }
}
