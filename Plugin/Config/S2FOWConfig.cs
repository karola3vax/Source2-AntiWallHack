using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace S2FOW.Config;

/// <summary>
/// The complete configuration schema for S2FOW.
///
/// This class defines every setting that server operators can tune in the JSON
/// config file. Settings are organized into logical groups:
///   - General:        Master on/off switch and grace-period durations.
///   - AntiWallhack:   Smoke grenade blocking parameters.
///   - TargetPoints:   How many and which body points to check on enemies.
///   - ViewerRays:     How the observer's ray origin is predicted for moving players.
///   - Debug:          In-game visual debugging options (beam lines, HUD overlay).
///   - Performance:    Raycast budget, hit thresholds, and hitbox padding.
///
/// The config is serialized to/from JSON by CounterStrikeSharp's config system.
/// A "guided" version with inline comments is also generated for human readability.
/// </summary>
public class S2FOWConfig : BasePluginConfig
{
    /// <summary>
    /// The config schema version. Incremented when default values change,
    /// so the migration system can detect and update old configs.
    /// </summary>
    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = 32;

    /// <summary>General settings: master enable switch, death/spawn grace durations.</summary>
    public GeneralSettings General { get; set; } = new();

    /// <summary>Anti-wallhack settings: smoke blocking parameters.</summary>
    public AntiWallhackSettings AntiWallhack { get; set; } = new();

    /// <summary>Target point settings: FOV culling, distance tiering, movement prediction for targets.</summary>
    public TargetPointSettings TargetPoints { get; set; } = new();

    /// <summary>Viewer ray settings: movement prediction for the observer's ray origin.</summary>
    public ViewerRaySettings ViewerRays { get; set; } = new();

    /// <summary>Debug settings: toggle in-game visual aids for development and tuning.</summary>
    public DebugSettings Debug { get; set; } = new();

    /// <summary>Performance settings: raycast budget, hit thresholds, hitbox padding.</summary>
    public PerformanceSettings Performance { get; set; } = new();

    /// <summary>Creates a deep copy of this config (changes to the copy do not affect the original).</summary>
    public S2FOWConfig Clone()
    {
        return new S2FOWConfig
        {
            Version = Version,
            General = General.Clone(),
            AntiWallhack = AntiWallhack.Clone(),
            TargetPoints = TargetPoints.Clone(),
            ViewerRays = ViewerRays.Clone(),
            Debug = Debug.Clone(),
            Performance = Performance.Clone()
        };
    }
}

/// <summary>
/// General plugin settings.
/// </summary>
public class GeneralSettings
{
    /// <summary>Master switch: set to false to disable all anti-wallhack protection.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How many ticks (64 ticks = 1 second) to keep a dead player visible after death.
    /// This lets the death animation play out naturally instead of the body vanishing instantly.
    /// Default: 128 ticks ≈ 2 seconds.
    /// </summary>
    public int DeathVisibilityDurationTicks { get; set; } = 128;

    /// <summary>
    /// How many ticks to keep all players visible at the start of each round.
    /// This prevents glitches during the spawn animation. Default: 32 ticks ≈ 0.5 seconds.
    /// </summary>
    public int RoundStartRevealDurationTicks { get; set; } = 32;

    internal GeneralSettings Clone() => (GeneralSettings)MemberwiseClone();
}

/// <summary>
/// Smoke grenade anti-wallhack settings.
/// Controls whether and how smoke grenades block line-of-sight checks.
/// </summary>
public class AntiWallhackSettings
{
    /// <summary>If true, smoke grenades block visibility (enemies behind smoke are hidden).</summary>
    public bool SmokeBlocksWallhack { get; set; } = true;

    /// <summary>
    /// Server-side approximation of the smoke blocking sphere radius (game units).
    /// CS2 uses volumetric smoke that conforms to geometry — there is no actual fixed
    /// sphere in the game. This value is used by S2FOW for its server-side LOS check
    /// and should be tuned to match the visual extent of smokes on your map geometry.
    /// CS:GO used radius ~144 units. Default: 144.
    /// </summary>
    public float SmokeBlockRadius { get; set; } = 144.0f;

    /// <summary>
    /// How many ticks a smoke blocks visibility after detonation.
    /// CS2 smoke lasts about 20 seconds. S2FOW stops short of that so smoke
    /// does not keep hiding players after the client smoke is effectively gone.
    /// Default: 1232 ticks (19.25 seconds at 64 Hz).
    /// </summary>
    public int SmokeLifetimeTicks { get; set; } = 1232;

    /// <summary>
    /// How many ticks the smoke takes to grow from its starting blocking radius
    /// to its full blocking radius (the bloom duration).
    /// Blocking starts immediately at detonation at SmokeGrowthStartFraction of full size;
    /// it grows linearly to full size over this many ticks.
    /// Default: 192 ticks = 3 seconds at 64 Hz.
    /// </summary>
    public int SmokeBloomDurationTicks { get; set; } = 192;

    /// <summary>
    /// The starting blocking radius as a fraction of the full SmokeBlockRadius (0.0 to 1.0).
    /// At detonation, the effective blocking radius = SmokeGrowthStartFraction × SmokeBlockRadius.
    /// It grows linearly to 100% over SmokeBloomDurationTicks. Default: 0.50 (50% = 72 units).
    /// </summary>
    public float SmokeGrowthStartFraction { get; set; } = 0.50f;

    internal AntiWallhackSettings Clone() => (AntiWallhackSettings)MemberwiseClone();
}

/// <summary>
/// Settings for the check points placed on enemy targets.
/// These control how many points are checked, how they are filtered by FOV
/// and distance, and how target movement is predicted.
/// </summary>
public class TargetPointSettings
{
    /// <summary>
    /// If true, reduce the number of check points based on the observer's field of view.
    /// Enemies directly ahead get more points (thorough checking).
    /// Enemies to the side or behind get fewer points (faster, since they are less likely to be seen).
    /// </summary>
    public bool FovCullingEnabled { get; set; } = true;

    /// <summary>
    /// Enemies within this many degrees of the observer's crosshair get the full set of
    /// check points (all skeleton + AABB points). Default: 60° (120° total cone).
    /// </summary>
    public float FullLosHalfAngleDegrees { get; set; } = 60.0f;

    /// <summary>
    /// Enemies within this many degrees get the original (reduced) set of check points.
    /// Beyond this angle, only AABB fallback points are used. Default: 120° (240° total cone).
    /// </summary>
    public float OriginalOnlyHalfAngleDegrees { get; set; } = 120.0f;

    /// <summary>
    /// If true, reduce check point count based on distance to the target.
    /// Nearby enemies get the full set. Far-away enemies get fewer points.
    /// </summary>
    public bool DistanceTieringEnabled { get; set; } = true;

    /// <summary>Enemies closer than this many units get the full LOS point set. Default: 1000.</summary>
    public float FullLosDistanceUnits { get; set; } = 1000.0f;

    /// <summary>Enemies further than this many units get only AABB fallback points. Default: 3000.</summary>
    public float AabbOnlyDistanceUnits { get; set; } = 3000.0f;

    /// <summary>How many ticks of forward movement prediction for target points. Default: 0 (none).</summary>
    public float ForwardLookAheadTicks { get; set; } = 0.0f;

    /// <summary>How many ticks of sideways movement prediction for target points. Default: 0 (none).</summary>
    public float SideLookAheadTicks { get; set; } = 0.0f;

    /// <summary>Maximum units the target point can be offset by movement prediction. Default: 0 (none).</summary>
    public float MaxMoveUnits { get; set; } = 0.0f;

    /// <summary>How many ticks of vertical (jump/fall) prediction for target points. Default: 8.</summary>
    public float UpDownLookAheadTicks { get; set; } = 8.0f;

    /// <summary>Maximum vertical prediction offset in units. Default: 16.</summary>
    public float MaxUpDownUnits { get; set; } = 16.0f;

    internal TargetPointSettings Clone() => (TargetPointSettings)MemberwiseClone();
}

/// <summary>
/// Settings for the observer's ray origin prediction.
/// These control how the observer's "eye" position is predicted forward based on movement.
/// </summary>
public class ViewerRaySettings
{
    /// <summary>Minimum horizontal speed (units/sec) before movement prediction activates. Default: 1.</summary>
    public float StartAfterSpeed { get; set; } = 1.0f;

    /// <summary>
    /// Forward movement prediction lookahead in ticks. Default: 8.
    /// At 250 u/s running speed: 8 × (250 × 0.015625) = 31.25 units forward lead.
    /// Kept below MaxMoveUnits=64 so it applies naturally without clamping.
    /// </summary>
    public float ForwardLookAheadTicks { get; set; } = 8.0f;

    /// <summary>
    /// Sideways (strafe) movement prediction lookahead in ticks. Default: 64.
    /// This intentionally large value ensures that at any running speed up to 250 u/s,
    /// the predicted offset saturates the MaxMoveUnits clamp (64 units), giving all
    /// moving players the maximum strafe prediction regardless of exact speed.
    /// Effective lead = min(speed × tickInterval × 64, MaxMoveUnits).
    /// </summary>
    public float SideLookAheadTicks { get; set; } = 64.0f;

    /// <summary>
    /// Vertical prediction lookahead when jumping, in ticks. Default: 64.
    /// Like SideLookAheadTicks, this is intentionally large to saturate MaxMoveUnits=64.
    /// At jump impulse 301.993 u/s: 64 ticks would predict 302 units up — always clamped
    /// to MaxMoveUnits so the actual lookahead is always bounded.
    /// Effective vertical lead = min(velZ × tickInterval × 64, MaxMoveUnits).
    /// </summary>
    public float JumpLookAheadTicks { get; set; } = 64.0f;

    /// <summary>
    /// Hard cap on total prediction offset in game units. Default: 64 (one player-width).
    /// All ForwardLookAheadTicks, SideLookAheadTicks, and JumpLookAheadTicks leads
    /// are clamped to this value independently.
    /// </summary>
    public float MaxMoveUnits { get; set; } = 64.0f;

    internal ViewerRaySettings Clone() => (ViewerRaySettings)MemberwiseClone();
}

/// <summary>
/// Debug visualization settings. Enable these during development to see the
/// visibility checks happening in real-time in the game world.
/// Keep all disabled in production for performance.
/// </summary>
public class DebugSettings
{
    /// <summary>Show a HUD overlay with ray count and decision breakdown per observer.</summary>
    public bool ShowRayCount { get; set; } = false;

    /// <summary>Show colored beam lines for each raycast (yellow = visible, blue = blocked).</summary>
    public bool ShowRayLines { get; set; } = false;

    /// <summary>Show marker beams at each check point on enemy targets.</summary>
    public bool ShowTargetPoints { get; set; } = false;

    internal DebugSettings Clone() => (DebugSettings)MemberwiseClone();
}

/// <summary>
/// Performance tuning settings. These control the computational budget
/// and the accuracy thresholds for ray hit detection.
/// </summary>
public class PerformanceSettings
{
    /// <summary>
    /// Maximum raycasts the plugin may perform per frame across ALL observers.
    /// Set to 0 for unlimited. When the budget is exceeded, remaining targets are
    /// force-shown (fail-open) to prevent hiding players without checking them.
    /// </summary>
    public int MaxRaycastsPerFrame { get; set; } = 0;

    /// <summary>If true, do a quick pre-check before per-ray smoke blocking. Default: true.</summary>
    public bool SmokeBatchPreFilterEnabled { get; set; } = true;

    /// <summary>
    /// A ray is considered "reaching the target" if its hit fraction is above this value.
    /// 1.0 = hit nothing. Values slightly below 1.0 account for the target's own collision.
    /// Default: 0.984375 (≈1/64th of the ray length from the endpoint).
    /// </summary>
    public float RayHitFractionThreshold { get; set; } = 0.984375f;

    /// <summary>
    /// Alternative hit check: if the ray endpoint is within this many units of the
    /// target point, it is considered a hit regardless of fraction. Default: 32.
    /// </summary>
    public float RayHitDistanceThreshold { get; set; } = 32.0f;

    /// <summary>Additional vertical offset added to the observer's eye position. Default: 0.</summary>
    public float ViewerHeightOffset { get; set; } = 0.0f;

    /// <summary>How much to expand the AABB upward for fallback check points. Default: 8 units.</summary>
    public float HitboxPaddingUp { get; set; } = 8.0f;

    /// <summary>How much to expand the AABB sideways for fallback check points. Default: 16 units.</summary>
    public float HitboxPaddingSide { get; set; } = 16.0f;

    /// <summary>How much to expand the AABB downward for fallback check points. Default: 0 units.</summary>
    public float HitboxPaddingDown { get; set; } = 0.0f;

    /// <summary>
    /// If the observer's crosshair aim-point is within this many units of a target,
    /// the target is always shown (they are directly aiming at them). Default: 64 units.
    /// </summary>
    public float AimRevealRadius { get; set; } = 64.0f;

    /// <summary>How far ahead (in units) to trace the observer's aim ray. Default: 8192.</summary>
    public float AimRayDistance { get; set; } = 8192.0f;

    internal PerformanceSettings Clone() => (PerformanceSettings)MemberwiseClone();
}
