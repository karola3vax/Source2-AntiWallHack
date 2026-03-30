using S2FOW.Core;

namespace S2FOW.Config;

/// <summary>
/// Defines per-profile parameter overrides for each game mode.
/// These overrides are applied at runtime and never written to disk.
/// </summary>
internal static class AutoConfigProfile
{
    /// <summary>
    /// Applies profile-specific overrides to the given config.
    /// Only modifies values that differ from the profile's optimal tuning.
    /// Returns the resolved profile that was applied.
    /// </summary>
    public static GameModeProfile Apply(S2FOWConfig config, GameModeProfile profile)
    {
        switch (profile)
        {
            case GameModeProfile.Competitive5v5:
                ApplyCompetitive5v5(config);
                break;
            case GameModeProfile.Wingman:
                ApplyWingman(config);
                break;
            case GameModeProfile.Casual:
                // Defaults are already tuned for casual. No overrides needed.
                break;
            case GameModeProfile.Deathmatch:
                ApplyDeathmatch(config);
                break;
            case GameModeProfile.Retake:
                ApplyRetake(config);
                break;
            case GameModeProfile.Custom:
                // No auto-tuning.
                break;
        }

        return profile;
    }

    /// <summary>
    /// Competitive 5v5 (10 players): Highest accuracy, tightest security.
    /// With only 10 players the ray budget is generous — spend it on precision.
    ///
    /// Max enemy pairs per observer: 5
    /// Worst-case rays/frame (5 obs × 5 tgt × 19 pts): 475
    /// Budget headroom: 4096 / 475 = 8.6× overhead
    /// </summary>
    private static void ApplyCompetitive5v5(S2FOWConfig config)
    {
        // Security: Strict in competitive — prefer hiding over leaking.
        config.General.SecurityProfile = SecurityProfile.Strict;

        // Budget: double per-player for maximum accuracy with 10 players.
        // 10 × 192 = 1920 adaptive budget, well within 4096 cap.
        config.Performance.MaxRaycastsPerFrame = 4096;
        config.Performance.BaseBudgetPerPlayer = 192;
        config.Performance.MaxAdaptiveBudget = 4096;

        // Cache: maximum responsiveness at all tiers.
        // With only 25 enemy pairs max, every pair can be fresh.
        config.Performance.CqbHiddenCacheTicks = 2;
        config.Performance.CqbVisibleCacheTicks = 2;
        config.Performance.MidHiddenCacheTicks = 4;
        config.Performance.MidVisibleCacheTicks = 3;
        config.Performance.FarHiddenCacheTicks = 10;
        config.Performance.FarVisibleCacheTicks = 4;
        config.Performance.XFarHiddenCacheTicks = 20;
        config.Performance.XFarVisibleCacheTicks = 6;

        // Prediction: tighter strafe anticipation (less ADAD spam in competitive).
        config.MovementPrediction.ViewerStrafeAnticipationTicks = 16.0f;

        // Peek grace: tight — competitive players expect precise timing.
        config.AntiWallhack.PeekGracePeriodTicks = 12;
    }

    /// <summary>
    /// Wingman 2v2 (4 players): Similar to competitive with even more headroom.
    /// Only 2 enemy pairs per observer — virtually unlimited ray budget.
    /// </summary>
    private static void ApplyWingman(S2FOWConfig config)
    {
        // Start from competitive profile.
        ApplyCompetitive5v5(config);

        // Even more generous per-player budget.
        // 4 × 256 = 1024 adaptive budget — trivial load.
        config.Performance.BaseBudgetPerPlayer = 256;

        // Tighter cache for the few pairs that exist.
        config.Performance.MidHiddenCacheTicks = 3;
        config.Performance.FarHiddenCacheTicks = 8;
        config.Performance.XFarHiddenCacheTicks = 16;
    }

    /// <summary>
    /// Deathmatch (20-64 players): Performance-focused with relaxed caching.
    ///
    /// Key challenge: N observers × N/2 enemies = up to 32×32 = 1024 pairs.
    /// Must rely heavily on caching and distance tiering to stay within budget.
    ///
    /// Typical DM frame budget at 40 alive: 40 × 64 = 2560, capped at 6144.
    /// Worst case at 64 alive: 64 × 64 = 4096, capped at 6144.
    /// </summary>
    private static void ApplyDeathmatch(S2FOWConfig config)
    {
        // Security: Balanced — DM prioritizes performance over leak-proof hiding.
        config.General.SecurityProfile = SecurityProfile.Balanced;

        // Death visibility: shorter for fast DM respawns (1.0s instead of 2.0s).
        config.General.DeathVisibilityDurationTicks = 64;

        // Round start: minimal reveal (0.25s instead of 0.5s).
        config.General.RoundStartRevealDurationTicks = 16;

        // Budget: reduced per-player but higher ceiling for large servers.
        config.Performance.MaxRaycastsPerFrame = 2048;
        config.Performance.BaseBudgetPerPlayer = 64;
        config.Performance.MaxAdaptiveBudget = 6144;

        // Cache: relaxed for performance. DM is chaotic — small staleness is acceptable.
        config.Performance.CqbHiddenCacheTicks = 3;
        config.Performance.CqbVisibleCacheTicks = 3;
        config.Performance.MidHiddenCacheTicks = 8;
        config.Performance.MidVisibleCacheTicks = 6;
        config.Performance.FarHiddenCacheTicks = 18;
        config.Performance.FarVisibleCacheTicks = 8;
        config.Performance.XFarHiddenCacheTicks = 36;
        config.Performance.XFarVisibleCacheTicks = 12;

        // Reduced check points for far distances — sub-pixel targets in DM don't need precision.
        config.Performance.FarMaxCheckPoints = 9;
        config.Performance.XFarMaxCheckPoints = 5;

        // Wider phase spread to absorb thundering-herd cache invalidation.
        config.Performance.ObserverPhaseSpreadTicks = 6;

        // Prediction: wider strafe anticipation for chaotic DM movement.
        config.MovementPrediction.ViewerStrafeAnticipationTicks = 32.0f;
        config.MovementPrediction.ViewerMaxLeadDistance = 80.0f;

        // Peek grace: shorter for fast-paced gameplay (125ms instead of 188ms).
        config.AntiWallhack.PeekGracePeriodTicks = 8;
    }

    /// <summary>
    /// Retake (10-20 players): Post-plant focused, bomb visibility critical.
    ///
    /// Retake rounds are short and intense, focused on site fights.
    /// CQB encounters dominate, and C4 visibility is the most important feature.
    /// </summary>
    private static void ApplyRetake(S2FOWConfig config)
    {
        // Security: Balanced — retake benefits from accuracy but can't afford false hides.
        config.General.SecurityProfile = SecurityProfile.Balanced;

        // Budget: generous per-player for 10-20 player retake servers.
        // 16 × 128 = 2048, 20 × 128 = 2560 — comfortable.
        config.Performance.BaseBudgetPerPlayer = 128;
        config.Performance.MaxAdaptiveBudget = 4096;

        // Cache: tight CQB for site fights, standard elsewhere.
        config.Performance.CqbHiddenCacheTicks = 2;
        config.Performance.CqbVisibleCacheTicks = 2;

        // Bomb visibility: always enforced in retake.
        config.AntiWallhack.BlockBombRadarESP = true;
        config.AntiWallhack.HidePlantedBombEntityWhenNotVisible = true;

        // Peek grace: slightly longer for site holds (219ms).
        config.AntiWallhack.PeekGracePeriodTicks = 14;
    }
}
