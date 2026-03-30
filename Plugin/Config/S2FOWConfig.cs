using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;
using S2FOW.Core;

namespace S2FOW.Config;

/// <summary>
/// Main S2FOW config.
/// Sections are ordered from everyday settings to deeper tuning.
/// All values calibrated to real CS2 Source 2 engine mechanics:
/// Source provenance:
///   - Canonical hitbox layout comes from the checked-in local extraction data in
///     tools/cs2_player_hitboxes_canonical.json.
///   - Tick assumptions follow CounterStrikeSharp's fixed-tick integration.
///   - Smoke and radar behavior should stay tickrate-independent and smoke-blocked
///     in line with Valve's official release notes.
///   - Do not retune from third-party summaries; only change defaults when local
///     game assets or official Valve sources justify it.
///   - Tick rate: 64 ticks/s (15.625ms per tick)
///   - Player hull: ±16u wide/deep, 72u tall (standing), 54u (crouching)
///   - Eye height: 64u (standing), 46u (crouching)
///   - Max run speed: 250 u/s (knife), walk ~125 u/s, crouch ~83 u/s
///   - Per-tick travel at full run: 250/64 ≈ 3.9 u/tick
/// </summary>
public class S2FOWConfig : BasePluginConfig
{
    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = 22;

    public GeneralSettings General { get; set; } = new();
    public AntiWallhackSettings AntiWallhack { get; set; } = new();
    public MovementPredictionSettings MovementPrediction { get; set; } = new();
    public DebugSettings Debug { get; set; } = new();
    public PerformanceSettings Performance { get; set; } = new();

    public S2FOWConfig Clone()
    {
        return new S2FOWConfig
        {
            Version = Version,
            General = General.Clone(),
            AntiWallhack = AntiWallhack.Clone(),
            MovementPrediction = MovementPrediction.Clone(),
            Debug = Debug.Clone(),
            Performance = Performance.Clone()
        };
    }
}

/// <summary>
/// Main on/off behavior and the broad protection style.
/// Most server owners only need this section.
/// </summary>
public class GeneralSettings
{
    /// <summary>
    /// Turns S2FOW on or off.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Overall protection style.
    /// Strict hides more aggressively.
    /// Balanced and Compat stay more compatibility-friendly.
    /// </summary>
    public SecurityProfile SecurityProfile { get; set; } = SecurityProfile.Balanced;

    /// <summary>
    /// Keeps a just-killed player visible for a short moment.
    /// This smooths out the death transition instead of making the player vanish instantly.
    ///
    /// CS2 death camera durations (measured):
    ///   Body-shot death: ~1.8-2.2 seconds camera
    ///   Headshot death: ~0.5-1.0 seconds camera
    ///   DM instant-respawn: ~1.0-1.5 seconds camera
    /// At 64 ticks/s, 128 ticks = 2.0s covers the typical DM respawn and
    /// most competitive death-camera transitions without holding the death
    /// location visible longer than necessary.
    /// </summary>
    public int DeathVisibilityDurationTicks { get; set; } = 128;

    /// <summary>
    /// Auto-tuning profile for the server.
    /// When set to anything other than Custom, S2FOW detects the server game mode
    /// and automatically overrides specific config values at runtime for optimal
    /// performance and accuracy. The JSON on disk is never modified.
    ///
    /// Auto = detect game mode from convars and map prefix.
    /// Competitive5v5 / Wingman / Casual / Deathmatch / Retake = force a specific profile.
    /// Custom = no auto-tuning, use config values as-is.
    /// </summary>
    public GameModeProfile AutoProfile { get; set; } = GameModeProfile.Auto;

    /// <summary>
    /// Reveals everyone briefly at round start.
    /// This helps the plugin settle cleanly during spawn and freeze-time,
    /// and prevents CopyExistingEntity crashes caused by entity state
    /// transitions during the initial spawn phase.
    ///
    /// CS2 competitive freeze time is typically 7-15 seconds, but
    /// entity initialization completes within the first ~0.5s.
    /// 32 ticks = 0.5s at 64 tick/s.
    /// </summary>
    public int RoundStartRevealDurationTicks { get; set; } = 32;

    internal GeneralSettings Clone()
    {
        return (GeneralSettings)MemberwiseClone();
    }
}

/// <summary>
/// Core anti-ESP behavior.
/// These are the protections most server owners care about.
/// </summary>
public class AntiWallhackSettings
{
    // Start here: the protections most admins actually care about.

    /// <summary>
    /// Hides enemy grenades and similar thrown objects when the thrower should stay hidden.
    /// </summary>
    public bool BlockGrenadeESP { get; set; } = true;

    /// <summary>
    /// Stops hidden enemies from leaking through radar or minimap visibility.
    /// </summary>
    public bool BlockRadarESP { get; set; } = true;

    /// <summary>
    /// Hides bullet impact effects from hidden enemies so shots are harder to track through walls.
    /// </summary>
    public bool BlockBulletImpactESP { get; set; } = true;

    // Extra protections for bomb and death-location leaks.

    /// <summary>
    /// Keeps dropped weapons hidden for a short time after death.
    /// This helps stop floor-weapon ESP from revealing death locations.
    ///
    /// 128 ticks = 2 seconds at 64 tick/s.
    /// Long enough to smooth the death transition while reducing how long
    /// floor-weapon ESP can pin the death location on busy servers.
    /// Set to 0 to turn this off.
    /// </summary>
    public int BlockDroppedWeaponESPDurationTicks { get; set; } = 128;

    /// <summary>
    /// Reveals a recently dropped weapon again once the observer is close enough
    /// to realistically want to loot it.
    ///
    /// This only applies to ground weapons being hidden through temporal
    /// ownership after a death. Ragdolls and far-away death-location leaks stay hidden.
    /// 1000u â‰ˆ 25.4 meters.
    /// 192u â‰ˆ 4.9 meters.
    /// Set to 0 to always keep dropped weapons hidden for the full duration.
    /// </summary>
    public float DroppedWeaponRevealDistance { get; set; } = 192.0f;

    /// <summary>
    /// Stops planted bomb radar leaks for CTs who do not truly have line of sight to the C4.
    /// </summary>
    public bool BlockBombRadarESP { get; set; } = true;

    /// <summary>
    /// Hides the planted bomb world entity itself from observers who do not truly have line of sight.
    /// This is separate from radar scrubbing so servers can keep the world entity visible while
    /// still preventing minimap leaks.
    /// </summary>
    public bool HidePlantedBombEntityWhenNotVisible { get; set; } = true;

    // Smoke behavior. Leave these alone unless smoke edge-cases feel wrong in real play.

    /// <summary>
    /// Lets smoke block vision for S2FOW decisions.
    /// </summary>
    public bool SmokeBlocksWallhack { get; set; } = true;

    /// <summary>
    /// Effective smoke size used by the visibility system.
    ///
    /// CS2 smoke blocking sphere radius = 144u (288u diameter).
    /// This matches the documented CS2 smokegrenade radius and ensures
    /// no gap between the visual smoke edge and the S2FOW blocking boundary.
    /// Using a smaller radius would leave a ring where ESP can see through
    /// visually-opaque smoke edges — a security hole.
    /// </summary>
    public float SmokeBlockRadius { get; set; } = 144.0f;

    /// <summary>
    /// How long a smoke should be treated as active.
    ///
    /// CS2 smokegrenade_lifetime = 18 seconds (changed from CS:GO's 20s).
    /// Visual particle fadeout begins at ~17.5s and clears by ~19s.
    /// 1184 ticks = 18.5s: blocking ends just as the smoke becomes
    /// visually thin, preventing the window where players can see through
    /// fading haze but S2FOW still blocks.
    /// This is a safety-net timeout — the engine's SmokegrenadeExpired event
    /// is the primary removal trigger and typically fires at ~18s.
    /// </summary>
    public int SmokeLifetimeTicks { get; set; } = 1184;

    /// <summary>
    /// Time for a new smoke to reach full blocking radius.
    /// CS2 volumetric smoke reaches visual opacity faster than CS:GO
    /// (~0.5-0.75s vs ~1.0s). Blocking begins immediately at
    /// SmokeGrowthStartFraction of full radius and grows to 100%
    /// over this window.
    /// At 64 ticks/s, 48 ticks = 0.75 second.
    /// </summary>
    public int SmokeBlockDelayTicks { get; set; } = 48;

    /// <summary>
    /// Starting size of a fresh smoke while it blooms.
    /// CS2 volumetric smokes expand from a wider visible base than CS:GO's
    /// particle-based smoke. 0.50 means the smoke starts at 50% of full
    /// radius and grows from there.
    /// At detonation: blocking radius = 0.50 × 144u = 72u.
    /// After SmokeBlockDelayTicks (48 ticks / 0.75s): grows to full 144u.
    /// </summary>
    public float SmokeGrowthStartFraction { get; set; } = 0.50f;

    // Visibility tuning. These shape how forgiving or strict the reveal logic feels.

    /// <summary>
    /// Maximum range for crosshair-based reveal checks.
    ///
    /// Longest competitive sightlines:
    ///   Dust2 Long A: ~2800u
    ///   Mirage Window to B short: ~2500u
    ///   Overpass: ~3200u
    /// 3200u covers the meaningful competitive sightlines while trimming
    /// expensive cross-map aim reveals that are rarely relevant in play.
    /// </summary>
    public float CrosshairRevealDistance { get; set; } = 3200.0f;

    /// <summary>
    /// How close an enemy must be to the crosshair to count as revealed.
    ///
    /// Player hull is 32u wide (±16u from center). At medium range this
    /// means ~16u radius would precisely match the hull edge.
    /// 64u provides generous forgiveness for crosshair-based reveal
    /// to avoid false negatives during fast crosshair movement.
    /// </summary>
    public float CrosshairRevealRadius { get; set; } = 64.0f;

    /// <summary>
    /// Hard distance limit for visibility.
    /// Players beyond this range are always hidden.
    ///
    /// Set to 0 to disable the distance limit (recommended).
    /// CS2 bullets despawn at 8192u, but real combat rarely exceeds 3500u.
    /// Disabling allows the distance-tiered system to handle falloff naturally.
    /// </summary>
    public float MaxVisibilityDistance { get; set; } = 0.0f;

    /// <summary>
    /// Keeps an enemy visible for a short time after a peek.
    /// This reduces flicker during fast corner fights and jiggles.
    ///
    /// CS2 jiggle peek exposure: typically &lt; 0.5s (~32 ticks).
    /// Human reaction time: ~200ms (~13 ticks).
    /// 12 ticks = 188ms: covers a realistic reaction + interpolation
    /// window so the enemy doesn't vanish mid-firefight.
    /// </summary>
    public int PeekGracePeriodTicks { get; set; } = 12;

    /// <summary>
    /// Reveals a hidden enemy grenade once it gets very close to the observer.
    /// This helps avoid "grenade from nowhere" moments at close range.
    ///
    /// 256u ≈ 6.5 meters. At this range the grenade is clearly audible
    /// and the player should see it regardless of thrower visibility.
    /// Set to 0 to always hide grenades from hidden throwers.
    /// </summary>
    public float GrenadeRevealDistance { get; set; } = 256.0f;

    internal AntiWallhackSettings Clone()
    {
        return (AntiWallhackSettings)MemberwiseClone();
    }
}

/// <summary>
/// Movement look-ahead used by visibility checks.
/// Tuned to real CS2 movement physics:
///   - Max run speed (knife): 250 u/s = 3.9 u/tick
///   - Max walk speed: ~125 u/s = 1.95 u/tick
///   - Crouch speed: ~83 u/s = 1.3 u/tick
///   - Jump height: ~56u, jump duration: ~0.8s (~51 ticks)
///   - Counter-strafe stop time: ~0.1-0.15s (~6-10 ticks)
/// </summary>
public class MovementPredictionSettings
{
    /// <summary>
    /// Minimum speed before movement prediction starts.
    ///
    /// Below this speed, the player is effectively stationary and no
    /// prediction offset is applied. Crouch-walking is ~83 u/s;
    /// a threshold of 15 u/s ensures very slow drift doesn't trigger prediction.
    /// </summary>
    public float MinSpeed { get; set; } = 15.0f;

    /// <summary>
    /// How far ahead to predict enemy forward motion.
    ///
    /// At 250 u/s knife speed: 8 ticks × 3.9 u/tick = 31.2u forward lead.
    /// This covers a full stride at maximum speed and accounts for
    /// client interpolation delay (~2-3 ticks).
    /// </summary>
    public float EnemyForwardLookaheadTicks { get; set; } = 8.0f;

    /// <summary>
    /// How far ahead to predict enemy sideways motion.
    ///
    /// Strafing uses the same max speed as forward movement.
    /// 8 ticks = 31.2u at max speed. Slightly generous to catch
    /// ADAD strafe peekers.
    /// </summary>
    public float EnemySidewaysLookaheadTicks { get; set; } = 8.0f;

    /// <summary>
    /// Maximum lead distance allowed for enemy prediction.
    ///
    /// Caps how far the predicted target point can deviate from the actual position.
    /// 1u keeps enemy prediction nearly locked to the real pawn while still
    /// allowing a tiny amount of smoothing for interpolation jitter.
    /// </summary>
    public float EnemyMaxLeadDistance { get; set; } = 1.0f;

    /// <summary>
    /// How far ahead to predict enemy jump and fall motion.
    ///
    /// CS2 jump peak: ~56u height, ~51 ticks total (~0.8s).
    /// Upward phase ≈ 25 ticks. 6 ticks lookahead covers the critical
    /// take-off moment where vertical position changes fastest
    /// (initial velocity ~300 u/s → 4.7u/tick vertical).
    /// </summary>
    public float EnemyVerticalLookaheadTicks { get; set; } = 6.0f;

    /// <summary>
    /// Maximum vertical lead allowed for enemy prediction.
    ///
    /// Caps how far the predicted target point can move vertically.
    /// 16u ≈ 1/4 player hull height. Catches jump take-off without
    /// overshooting a full floor height (128u door height).
    /// </summary>
    public float EnemyVerticalMaxLeadDistance { get; set; } = 16.0f;

    /// <summary>
    /// How far ahead to predict the observer's forward peek movement.
    ///
    /// When the observer (camera holder) peeks a corner at full speed,
    /// their view position shifts 3.9u/tick. 4 ticks = ~15.6u lead.
    /// This pre-reveals enemies before the observer's pawn fully rounds
    /// the corner, preventing "pop-in" flicker.
    /// </summary>
    public float ViewerForwardLookaheadTicks { get; set; } = 4.0f;

    /// <summary>
    /// Extra jump anticipation for the observer.
    ///
    /// When the observer jumps, their eye height changes rapidly.
    /// 12 ticks × ~4.7u/tick = ~56u vertical anticipation at jump peak.
    /// This prevents enemies from popping in/out as the observer
    /// gains elevation over cover.
    /// </summary>
    public float ViewerJumpAnticipationTicks { get; set; } = 12.0f;

    /// <summary>
    /// Extra strafe anticipation for the observer.
    ///
    /// At 250 u/s, 24 ticks would predict ~94u of raw lateral travel,
    /// but ViewerMaxLeadDistance clamps the final lead to 64u.
    /// This still preloads normal jiggle peeks while reducing early reveals
    /// on wide ADAD motion for busy DM and retake servers.
    /// </summary>
    public float ViewerStrafeAnticipationTicks { get; set; } = 24.0f;

    /// <summary>
    /// Maximum lead distance allowed for observer prediction.
    ///
    /// Caps how far the observer prediction can place the probe point.
    /// 64u = 2 player hull widths = ~1.6 meters.
    /// This is enough to cover typical CS2 doorframes and tight corners
    /// without pushing the probe excessively deep into wall thickness.
    /// </summary>
    public float ViewerMaxLeadDistance { get; set; } = 64.0f;

    internal MovementPredictionSettings Clone()
    {
        return (MovementPredictionSettings)MemberwiseClone();
    }
}

/// <summary>
/// Troubleshooting visuals.
/// These are for debugging, not normal live play.
/// Keep them off on populated servers unless actively investigating behavior.
/// </summary>
public class DebugSettings
{
    /// <summary>
    /// Shows per-observer trace counters on screen.
    /// </summary>
    public bool ShowRayCount { get; set; } = false;

    /// <summary>
    /// Draws the actual rays used by the visibility system.
    /// </summary>
    public bool ShowRayLines { get; set; } = false;

    /// <summary>
    /// Draws canonical hitbox primitive centers plus AABB fallback corners.
    /// </summary>
    public bool ShowTargetPoints { get; set; } = false;

    internal DebugSettings Clone()
    {
        return (DebugSettings)MemberwiseClone();
    }
}

/// <summary>
/// Advanced performance and engine-level tuning.
/// Most servers should leave these at default unless profiling a specific issue.
///
/// Distance tier design rationale (based on real CS2 map geometry):
///   CQB (&lt;768u / ~19m): Close-quarters combat. Site retakes, entries.
///   Mid (&lt;1800u / ~46m): Standard rifle engagement range.
///   Far (&lt;3200u / ~81m): Long angles (Dust2 Long, AWP duels).
///   XFar (&gt;3200u): Extreme sightlines. Sub-pixel target size.
/// </summary>
public class PerformanceSettings
{
    // Main safety limit.

    /// <summary>
    /// Hard cap for total raycasts per frame.
    ///
    /// For a 20v20 server (40 players):
    ///   - Worst case pairs: 20 × 20 = 400 enemy pairs
    ///   - At full point set (39 points): 400 × 39 = 15600 rays
    ///   - With distance tiering + caching: typically ~5-15% of worst case
    ///   - 2048 covers comfortable headroom for 20v20 with tiering
    /// 0 means unlimited, which is usually not ideal for live servers.
    /// </summary>
    // This flat cap is used directly when adaptive budgeting is disabled.
    public int MaxRaycastsPerFrame { get; set; } = 2048;

    /// <summary>
    /// What S2FOW should do after the frame ray budget runs out.
    /// CacheOnly reuses cache when possible, otherwise hides.
    /// FailClosed hides.
    /// FailOpen shows.
    /// </summary>
    public BudgetExceededPolicy BudgetExceededPolicy { get; set; } = BudgetExceededPolicy.CacheOnly;

    // Distance-tiered evaluation.
    // Distances control which trace-point set and cache duration are used
    // for each observer-target pair. All thresholds are in Hammer Units.

    /// <summary>
    /// Pairs closer than this are treated as close-quarters combat.
    /// Full point-set checks with the shortest cache.
    ///
    /// 768u ≈ 19.5 meters. Covers most site interiors and close angles.
    /// At this range, full body details are clearly visible.
    /// </summary>
    public float CqbDistanceThreshold { get; set; } = 768.0f;

    /// <summary>
    /// Pairs between CQB and this threshold are mid-range.
    /// Full point-set checks with a moderate cache.
    ///
    /// 1800u ≈ 46 meters. Standard rifle engagement range.
    /// Covers Dust2 B tunnels to site, Mirage A ramp to site, etc.
    /// </summary>
    public float MidDistanceThreshold { get; set; } = 1800.0f;

    /// <summary>
    /// Pairs between Mid and this threshold are far-range.
    /// Reduced point set with a longer cache.
    ///
    /// 3200u ≈ 81 meters. Covers all standard competitive AWP angles.
    /// Beyond this, targets are sub-pixel width.
    /// </summary>
    public float FarDistanceThreshold { get; set; } = 3200.0f;

    /// <summary>
    /// Reduces visibility work based on where the enemy sits inside the observer's
    /// horizontal field of view.
    /// Full cone = full primitive budget.
    /// Peripheral cone = half primitive budget.
    /// Rear cone = no LOS raycasts.
    /// </summary>
    public bool FovSamplingEnabled { get; set; } = true;

    /// <summary>
    /// Half-angle in degrees for the full-detail front cone.
    /// Targets inside this cone get the normal per-distance primitive budget.
    /// 45 degrees = 90-degree full-detail front wedge.
    /// Matches CS2 base FOV (90 deg on 4:3) so all on-screen targets get
    /// full primitive detail, preventing pop-in at screen edges.
    /// </summary>
    public float FullDetailFovHalfAngleDegrees { get; set; } = 45.0f;

    /// <summary>
    /// Half-angle in degrees for the reduced-detail peripheral cone.
    /// Targets outside FullDetail but inside this cone get half the primitive budget.
    /// Targets outside this cone are treated as rear targets and get no LOS raycasts.
    /// 160 degrees leaves a narrow 40-degree rear no-ray wedge.
    /// </summary>
    public float PeripheralFovHalfAngleDegrees { get; set; } = 160.0f;

    // Per-tier cache durations (in ticks at 64 tick/s).
    //
    // CQB: Fastest refresh. At 250 u/s, a player crosses 32u (full hull)
    //       in 8 ticks. Hidden cache=2 (31ms), Visible cache=2 (31ms).
    //
    // Mid: Moderate. Angular velocity of targets is lower.
    //      Hidden=6 (94ms), Visible=4 (63ms).
    //
    // Far: Targets move slowly in screen space.
    //      Hidden=14 (219ms), Visible=5 (78ms).
    //
    // XFar: Minimal visual change per tick.
    //       Hidden=28 (438ms), Visible=8 (125ms).

    public int CqbHiddenCacheTicks { get; set; } = 2;
    public int CqbVisibleCacheTicks { get; set; } = 2;
    public int MidHiddenCacheTicks { get; set; } = 6;
    public int MidVisibleCacheTicks { get; set; } = 4;
    public int FarHiddenCacheTicks { get; set; } = 14;
    public int FarVisibleCacheTicks { get; set; } = 5;
    public int XFarHiddenCacheTicks { get; set; } = 28;
    public int XFarVisibleCacheTicks { get; set; } = 8;

    /// <summary>
    /// Maximum visibility primitives for far-range targets.
    ///
    /// At 3200u, a full player model subtends ~0.6° visual angle.
    /// Primitive order follows the canonical CS2 hitbox layout:
    /// head, neck, torso chain, pelvis, ankles, then limbs.
    /// 11 keeps head/neck, the torso chain, pelvis, both ankles,
    /// and both lower legs for long-angle head and foot peeks.
    /// </summary>
    public int FarMaxCheckPoints { get; set; } = 11;

    /// <summary>
    /// Maximum visibility primitives for extreme-far-range targets.
    ///
    /// 7 keeps only the biggest and most important CS2 hitbox primitives:
    /// head, neck, the torso chain, and pelvis.
    /// </summary>
    public int XFarMaxCheckPoints { get; set; } = 7;

    // Velocity-aware caching.

    /// <summary>
    /// Extends cache TTL when both players are nearly stationary.
    /// </summary>
    public bool VelocityCacheExtensionEnabled { get; set; } = true;

    /// <summary>
    /// Below this movement distance since the last evaluation, a player is
    /// considered stationary for cache-extension purposes.
    ///
    /// At walking speed (125 u/s) over 2 ticks: 125/64 × 2 = 3.9u.
    /// 6u threshold ensures only truly stopped players get extended cache.
    /// Crouch-walking (~83 u/s) over 4 ticks = 5.2u, just under threshold.
    /// </summary>
    public float StationaryThresholdUnits { get; set; } = 6.0f;

    /// <summary>
    /// Movement threshold for "slow-moving" classification in the graduated
    /// velocity cache extension system. Players below this distance get a
    /// moderate cache extension (2x), while stationary players get 3x.
    ///
    /// At walking speed (125 u/s) over 4 ticks: 7.8u.
    /// At running speed (250 u/s) over 4 ticks: 15.6u.
    /// 20u captures walk-speed and slow crouch-walk players reliably.
    /// </summary>
    public float SlowMoveThresholdUnits { get; set; } = 20.0f;

    // Staggered cache expiry.

    /// <summary>
    /// Offsets cache expiry per pair so evaluations spread evenly across ticks
    /// instead of spiking on one tick.
    /// </summary>
    public bool StaggeredCacheExpiryEnabled { get; set; } = true;

    /// <summary>
    /// Spreads observer evaluations across ticks so cache expirations from
    /// different observers do not all hit the same frame. Each observer is
    /// assigned a phase based on its slot, and hidden cache TTLs are extended
    /// by a small offset on non-priority ticks.
    ///
    /// Critical for 20+ player servers where 40 observers can cause
    /// thundering-herd cache invalidation spikes.
    /// </summary>
    public bool ObserverPhaseSpreadEnabled { get; set; } = true;

    /// <summary>
    /// Number of ticks over which observer evaluations are spread.
    /// Higher values spread load more but increase worst-case staleness.
    ///
    /// At 64 tick/s, 4 ticks = 62.5ms spread. Human reaction time is ~200ms
    /// (13 ticks), so this is well within perceptual tolerance.
    /// Values above 4 are not recommended.
    /// </summary>
    public int ObserverPhaseSpreadTicks { get; set; } = 4;

    // Adaptive ray budget.

    /// <summary>
    /// Scales the ray budget with player count instead of using a flat cap.
    /// </summary>
    // When enabled, the effective budget is BaseBudgetPerPlayer * alive players, capped by MaxAdaptiveBudget.
    public bool AdaptiveBudgetEnabled { get; set; } = true;

    /// <summary>
    /// Budget added per alive player.
    ///
    /// For 20v20 (40 alive): 40 x 96 = 3840 rays budget.
    /// For 5v5 (10 alive): 10 x 96 = 960 rays budget.
    /// This scales linearly with server load.
    /// </summary>
    public int BaseBudgetPerPlayer { get; set; } = 96;

    /// <summary>
    /// Hard ceiling even when adaptive scaling is active.
    ///
    /// 4096 rays is a comfortable ceiling for a dedicated server CPU.
    /// Prevents runaway budget in 32v32 or higher scenarios.
    /// </summary>
    public int MaxAdaptiveBudget { get; set; } = 4096;

    /// <summary>
    /// Distributes the ray budget fairly across observers so no single
    /// observer can starve others by consuming the entire frame budget.
    /// Without this, the first observers processed in slot order may use
    /// all available rays, leaving later observers with only cache fallback.
    ///
    /// Critical for 20+ player servers. On a 40-player DM server,
    /// this ensures all 40 observers get meaningful ray trace results
    /// instead of only the first 5-10.
    /// </summary>
    public bool PerObserverBudgetFairnessEnabled { get; set; } = true;

    /// <summary>
    /// Minimum fraction of the total frame budget reserved per observer.
    /// This floor prevents starvation even when budget is scarce.
    ///
    /// At 40 players with 5120 budget: 0.03 × 5120 = 154 rays minimum.
    /// 154 rays allows full CQB evaluation (40 rays) for ~3 close enemies
    /// per observer, ensuring close-quarters accuracy is always maintained.
    /// </summary>
    public float MinObserverBudgetShare { get; set; } = 0.03f;

    /// <summary>
    /// Pre-filters smoke blocking checks using a cheap line-segment distance
    /// test before running expensive per-point sphere intersections.
    /// Pairs whose observer-target line is far from all active smokes skip
    /// the full smoke check entirely.
    ///
    /// Most effective when smokes are clustered at one site and many pairs
    /// are fighting elsewhere (common in deathmatch and retake scenarios).
    /// </summary>
    public bool SmokeBatchPreFilterEnabled { get; set; } = true;

    // Ray hit interpretation.

    /// <summary>
    /// If a ray reaches this fraction of its full path, treat it as visible.
    ///
    /// 0.985 = if 98.5% of the ray path is clear, the target is considered visible.
    /// This tighter threshold reduces long-range thin-wall false positives,
    /// while the distance fallback below still catches genuine edge clipping
    /// right at the target surface.
    /// </summary>
    public float RayHitFractionThreshold { get; set; } = 0.985f;

    /// <summary>
    /// Extra distance forgiveness for near-miss ray hits.
    ///
    /// If the ray hit point is within this distance of the target point,
    /// treat it as visible even if fraction threshold wasn't met.
    /// 16u = half a player hull width (±16u). Catches rays that clip
    /// the edge of thin-wall cover right at the target.
    /// </summary>
    public float RayHitDistanceThreshold { get; set; } = 16.0f;

    // Ray start and target box shaping.

    /// <summary>
    /// Vertical offset added to the observer ray start.
    ///
    /// 0 = rays start from exact eye position (64u standing, 46u crouching).
    /// Adjusting this can compensate for viewmodel vs. hitbox alignment
    /// mismatches, but 0 is correct for CS2.
    /// </summary>
    public float ViewerHeightOffset { get; set; } = 0.0f;

    /// <summary>
    /// Extra padding added above the target box.
    ///
    /// CS2 head hitbox capsule overshoots collision hull top by ~6-8u
    /// (head bone at 62u + 4u capsule radius = 66u, hull top = 72u).
    /// 12u keeps the AABB fallback generous for crouch-to-stand transitions
    /// and jump poses without adding more trace points.
    /// </summary>
    public float HitboxPaddingUp { get; set; } = 12.0f;

    /// <summary>
    /// Extra padding added to each side of the target box.
    ///
    /// CS2 collision hull: ±16u (32u wide).
    /// Arm/shoulder hitbox capsules overshoot hull sides by ~8-12u.
    /// Weapon models (especially rifles) extend ~20-30u forward.
    /// 12u padding catches most limb and weapon overshoot without making
    /// the fallback box feel too sticky around thin cover.
    /// </summary>
    public float HitboxPaddingSide { get; set; } = 12.0f;

    /// <summary>
    /// Extra padding added below the target box.
    ///
    /// Foot hitbox capsules sit at ankle height (~4u above ground).
    /// Collision hull bottom is at Z=0. Small upward padding
    /// catches foot visibility on ramps and stairs.
    /// </summary>
    public float HitboxPaddingDown { get; set; } = 2.0f;

    // Reverse-link scanning for attached and owned entities.

    /// <summary>
    /// How often to run the wider ownership rescan.
    ///
    /// 96 ticks = 1.5 seconds. Full entity system scan is expensive.
    /// Most entity ownership changes are caught immediately by dirty tracking;
    /// this is a safety net sweep.
    /// </summary>
    public int EntityRescanIntervalTicks { get; set; } = 96;

    /// <summary>
    /// How many entities the wider rescan may process in one frame.
    ///
    /// 1024 entities per frame ÷ 64 tick interval = full sweep for
    /// servers with up to ~65,000 entity indices (well above CS2 limits).
    /// </summary>
    public int EntityRescanBudgetPerFrame { get; set; } = 1024;

    /// <summary>
    /// Maximum depth when walking scene-node children.
    /// </summary>
    public int MaxSceneTraversalDepth { get; set; } = 24;

    /// <summary>
    /// Maximum scene nodes to visit while collecting child entities.
    /// </summary>
    public int MaxSceneTraversalNodes { get; set; } = 2048;

    /// <summary>
    /// In Strict mode unresolved linked entities are hidden immediately.
    /// In softer modes this is the fallback timeout before forcing a hide.
    ///
    /// 64 ticks = 1 second. Gives newly spawned entities time to be
    /// claimed by a player before falling back to hide.
    /// Set to 0 to disable the non-Strict timeout.
    /// </summary>
    public int HideUnresolvedEntitiesAfterTicks { get; set; } = 64;

    internal PerformanceSettings Clone()
    {
        return (PerformanceSettings)MemberwiseClone();
    }
}

/// <summary>
/// Global safety posture.
/// </summary>
public enum SecurityProfile
{
    /// <summary>
    /// Most secure behavior. Prefers hiding over risking a leak.
    /// </summary>
    Strict = 0,

    /// <summary>
    /// Middle ground between security and compatibility.
    /// </summary>
    Balanced = 1,

    /// <summary>
    /// Least intrusive behavior. Use only if another plugin/setup needs it.
    /// </summary>
    Compat = 2
}

/// <summary>
/// Fallback action when the frame ray budget runs out.
/// </summary>
public enum BudgetExceededPolicy
{
    /// <summary>
    /// Reuse cached results when possible. If there is no cache, hide.
    /// </summary>
    CacheOnly = 0,

    /// <summary>
    /// Always hide when over budget.
    /// </summary>
    FailClosed = 1,

    /// <summary>
    /// Always show when over budget.
    /// </summary>
    FailOpen = 2
}

/// <summary>
/// Auto-config profile that tunes parameters for a specific game mode.
/// </summary>
public enum GameModeProfile
{
    /// <summary>
    /// Detect game mode from server convars and map prefix.
    /// Falls back to Casual if detection is inconclusive.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// 5v5 competitive (10 players). Highest accuracy, tightest security.
    /// </summary>
    Competitive5v5 = 1,

    /// <summary>
    /// 2v2 wingman (4 players). Similar to competitive with even more budget headroom.
    /// </summary>
    Wingman = 2,

    /// <summary>
    /// 10v10 casual (20 players). Balanced defaults — the config is already tuned for this.
    /// </summary>
    Casual = 3,

    /// <summary>
    /// Deathmatch or free-for-all (20-64 players). Performance-focused with relaxed caching.
    /// </summary>
    Deathmatch = 4,

    /// <summary>
    /// Retake mode (10-20 players). Post-plant focused with C4 visibility priority.
    /// </summary>
    Retake = 5,

    /// <summary>
    /// No auto-tuning. Use config values exactly as written.
    /// </summary>
    Custom = 6
}
