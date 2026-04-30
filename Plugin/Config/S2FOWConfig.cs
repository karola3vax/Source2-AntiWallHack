using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace S2FOW.Config;

public sealed class S2FOWConfig : BasePluginConfig
{
    public const int CurrentConfigVersion = 33;

    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = CurrentConfigVersion;

    [JsonPropertyName("Main")]
    public GeneralSettings General { get; set; } = new();

    [JsonPropertyName("SmokeVisibility")]
    public AntiWallhackSettings AntiWallhack { get; set; } = new();

    [JsonPropertyName("EnemyCheckPoints")]
    public TargetPointSettings TargetPoints { get; set; } = new();

    [JsonPropertyName("ViewerEyePrediction")]
    public ViewerRaySettings ViewerRays { get; set; } = new();

    public DebugSettings Debug { get; set; } = new();

    [JsonPropertyName("Advanced")]
    public PerformanceSettings Performance { get; set; } = new();

    [JsonPropertyName("General")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeneralSettings? LegacyGeneral
    {
        get => null;
        set
        {
            if (value is not null)
            {
                General = value;
            }
        }
    }

    [JsonPropertyName("AntiWallhack")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AntiWallhackSettings? LegacyAntiWallhack
    {
        get => null;
        set
        {
            if (value is not null)
            {
                AntiWallhack = value;
            }
        }
    }

    [JsonPropertyName("TargetPoints")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TargetPointSettings? LegacyTargetPoints
    {
        get => null;
        set
        {
            if (value is not null)
            {
                TargetPoints = value;
            }
        }
    }

    [JsonPropertyName("ViewerRays")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ViewerRaySettings? LegacyViewerRays
    {
        get => null;
        set
        {
            if (value is not null)
            {
                ViewerRays = value;
            }
        }
    }

    [JsonPropertyName("Performance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PerformanceSettings? LegacyPerformance
    {
        get => null;
        set
        {
            if (value is not null)
            {
                Performance = value;
            }
        }
    }

    public S2FOWConfig Clone()
    {
        return new S2FOWConfig
        {
            Version = Version,
            General = new GeneralSettings
            {
                Enabled = General.Enabled,
                DeathVisibilityDurationTicks = General.DeathVisibilityDurationTicks,
                RoundStartRevealDurationTicks = General.RoundStartRevealDurationTicks
            },
            AntiWallhack = new AntiWallhackSettings
            {
                SmokeBlocksWallhack = AntiWallhack.SmokeBlocksWallhack,
                SmokeBlockRadius = AntiWallhack.SmokeBlockRadius,
                SmokeLifetimeTicks = AntiWallhack.SmokeLifetimeTicks,
                SmokeBloomDurationTicks = AntiWallhack.SmokeBloomDurationTicks,
                SmokeGrowthStartFraction = AntiWallhack.SmokeGrowthStartFraction
            },
            TargetPoints = new TargetPointSettings
            {
                FovCullingEnabled = TargetPoints.FovCullingEnabled,
                DistanceTieringEnabled = TargetPoints.DistanceTieringEnabled,
                FullLosHalfAngleDegrees = TargetPoints.FullLosHalfAngleDegrees,
                OriginalOnlyHalfAngleDegrees = TargetPoints.OriginalOnlyHalfAngleDegrees,
                FullLosDistanceUnits = TargetPoints.FullLosDistanceUnits,
                AabbOnlyDistanceUnits = TargetPoints.AabbOnlyDistanceUnits,
                ForwardLookAheadTicks = TargetPoints.ForwardLookAheadTicks,
                SideLookAheadTicks = TargetPoints.SideLookAheadTicks,
                MaxMoveUnits = TargetPoints.MaxMoveUnits,
                UpDownLookAheadTicks = TargetPoints.UpDownLookAheadTicks,
                MaxUpDownUnits = TargetPoints.MaxUpDownUnits
            },
            ViewerRays = new ViewerRaySettings
            {
                StartAfterSpeed = ViewerRays.StartAfterSpeed,
                ForwardLookAheadTicks = ViewerRays.ForwardLookAheadTicks,
                SideLookAheadTicks = ViewerRays.SideLookAheadTicks,
                JumpLookAheadTicks = ViewerRays.JumpLookAheadTicks,
                MaxMoveUnits = ViewerRays.MaxMoveUnits
            },
            Debug = new DebugSettings
            {
                ShowRayCount = Debug.ShowRayCount,
                ShowRayLines = Debug.ShowRayLines,
                ShowTargetPoints = Debug.ShowTargetPoints
            },
            Performance = new PerformanceSettings
            {
                MaxRaycastsPerFrame = Performance.MaxRaycastsPerFrame,
                ViewerHeightOffset = Performance.ViewerHeightOffset,
                HitboxPaddingUp = Performance.HitboxPaddingUp,
                HitboxPaddingSide = Performance.HitboxPaddingSide,
                HitboxPaddingDown = Performance.HitboxPaddingDown,
                AimRevealRadius = Performance.AimRevealRadius,
                AimRayDistance = Performance.AimRayDistance,
                SmokeBatchPreFilterEnabled = Performance.SmokeBatchPreFilterEnabled
            }
        };
    }
}

public sealed class GeneralSettings
{
    [JsonPropertyName("ProtectionEnabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("KeepDeadPlayersVisibleTicks")]
    public int DeathVisibilityDurationTicks { get; set; } = 128;

    [JsonPropertyName("ShowEveryoneAtRoundStartTicks")]
    public int RoundStartRevealDurationTicks { get; set; } = 32;

    [JsonPropertyName("Enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyEnabled
    {
        get => null;
        set { if (value.HasValue) Enabled = value.Value; }
    }

    [JsonPropertyName("DeathVisibilityDurationTicks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyDeathVisibilityDurationTicks
    {
        get => null;
        set { if (value.HasValue) DeathVisibilityDurationTicks = value.Value; }
    }

    [JsonPropertyName("RoundStartRevealDurationTicks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyRoundStartRevealDurationTicks
    {
        get => null;
        set { if (value.HasValue) RoundStartRevealDurationTicks = value.Value; }
    }
}

public sealed class AntiWallhackSettings
{
    [JsonPropertyName("HidePlayersBehindSmoke")]
    public bool SmokeBlocksWallhack { get; set; } = true;

    [JsonPropertyName("SmokeSizeUnits")]
    public float SmokeBlockRadius { get; set; } = 130f;

    [JsonPropertyName("SmokeLastsTicks")]
    public int SmokeLifetimeTicks { get; set; } = 1232;

    [JsonPropertyName("SmokeGrowsTicks")]
    public int SmokeBloomDurationTicks { get; set; } = 192;

    [JsonPropertyName("SmokeStartsAtSizeFraction")]
    public float SmokeGrowthStartFraction { get; set; } = 0.25f;

    [JsonPropertyName("SmokeBlocksWallhack")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacySmokeBlocksWallhack
    {
        get => null;
        set { if (value.HasValue) SmokeBlocksWallhack = value.Value; }
    }

    [JsonPropertyName("SmokeBlockRadius")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? LegacySmokeBlockRadius
    {
        get => null;
        set { if (value.HasValue) SmokeBlockRadius = value.Value; }
    }

    [JsonPropertyName("SmokeLifetimeTicks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacySmokeLifetimeTicks
    {
        get => null;
        set { if (value.HasValue) SmokeLifetimeTicks = value.Value; }
    }

    [JsonPropertyName("SmokeBloomDurationTicks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacySmokeBloomDurationTicks
    {
        get => null;
        set { if (value.HasValue) SmokeBloomDurationTicks = value.Value; }
    }

    [JsonPropertyName("SmokeGrowthStartFraction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? LegacySmokeGrowthStartFraction
    {
        get => null;
        set { if (value.HasValue) SmokeGrowthStartFraction = value.Value; }
    }
}

public sealed class TargetPointSettings
{
    [JsonPropertyName("UseFewerChecksOutsideView")]
    public bool FovCullingEnabled { get; set; } = false;

    [JsonPropertyName("UseFewerChecksFarAway")]
    public bool DistanceTieringEnabled { get; set; } = true;

    [JsonPropertyName("FullCheckViewHalfAngleDegrees")]
    public float FullLosHalfAngleDegrees { get; set; } = 62f;

    [JsonPropertyName("ReducedCheckViewHalfAngleDegrees")]
    public float OriginalOnlyHalfAngleDegrees { get; set; } = 100f;

    [JsonPropertyName("FullCheckDistanceUnits")]
    public float FullLosDistanceUnits { get; set; } = 3200f;

    [JsonPropertyName("BoxOnlyDistanceUnits")]
    public float AabbOnlyDistanceUnits { get; set; } = 6400f;

    [JsonPropertyName("EnemyForwardPredictionTicks")]
    public int ForwardLookAheadTicks { get; set; } = 4;

    [JsonPropertyName("EnemySidePredictionTicks")]
    public int SideLookAheadTicks { get; set; } = 3;

    [JsonPropertyName("EnemyPredictionMaxUnits")]
    public float MaxMoveUnits { get; set; } = 48f;

    [JsonPropertyName("EnemyVerticalPredictionTicks")]
    public int UpDownLookAheadTicks { get; set; } = 2;

    [JsonPropertyName("EnemyVerticalPredictionMaxUnits")]
    public float MaxUpDownUnits { get; set; } = 32f;

    [JsonPropertyName("FovCullingEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyFovCullingEnabled
    {
        get => null;
        set { if (value.HasValue) FovCullingEnabled = value.Value; }
    }

    [JsonPropertyName("DistanceTieringEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyDistanceTieringEnabled
    {
        get => null;
        set { if (value.HasValue) DistanceTieringEnabled = value.Value; }
    }

    [JsonPropertyName("FullLosHalfAngleDegrees")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? LegacyFullLosHalfAngleDegrees
    {
        get => null;
        set { if (value.HasValue) FullLosHalfAngleDegrees = value.Value; }
    }

    [JsonPropertyName("OriginalOnlyHalfAngleDegrees")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? LegacyOriginalOnlyHalfAngleDegrees
    {
        get => null;
        set { if (value.HasValue) OriginalOnlyHalfAngleDegrees = value.Value; }
    }

    [JsonPropertyName("FullLosDistanceUnits")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? LegacyFullLosDistanceUnits
    {
        get => null;
        set { if (value.HasValue) FullLosDistanceUnits = value.Value; }
    }

    [JsonPropertyName("AabbOnlyDistanceUnits")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? LegacyAabbOnlyDistanceUnits
    {
        get => null;
        set { if (value.HasValue) AabbOnlyDistanceUnits = value.Value; }
    }

    [JsonPropertyName("ForwardLookAheadTicks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyForwardLookAheadTicks
    {
        get => null;
        set { if (value.HasValue) ForwardLookAheadTicks = value.Value; }
    }

    [JsonPropertyName("SideLookAheadTicks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacySideLookAheadTicks
    {
        get => null;
        set { if (value.HasValue) SideLookAheadTicks = value.Value; }
    }

    [JsonPropertyName("MaxMoveUnits")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? LegacyMaxMoveUnits
    {
        get => null;
        set { if (value.HasValue) MaxMoveUnits = value.Value; }
    }

    [JsonPropertyName("UpDownLookAheadTicks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyUpDownLookAheadTicks
    {
        get => null;
        set { if (value.HasValue) UpDownLookAheadTicks = value.Value; }
    }

    [JsonPropertyName("MaxUpDownUnits")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? LegacyMaxUpDownUnits
    {
        get => null;
        set { if (value.HasValue) MaxUpDownUnits = value.Value; }
    }
}

public sealed class ViewerRaySettings
{
    [JsonPropertyName("MinimumSpeedForEyePrediction")]
    public float StartAfterSpeed { get; set; } = 25f;

    [JsonPropertyName("EyeForwardPredictionTicks")]
    public int ForwardLookAheadTicks { get; set; } = 3;

    [JsonPropertyName("EyeSidePredictionTicks")]
    public int SideLookAheadTicks { get; set; } = 2;

    [JsonPropertyName("EyeJumpPredictionTicks")]
    public int JumpLookAheadTicks { get; set; } = 4;

    [JsonPropertyName("EyePredictionMaxUnits")]
    public float MaxMoveUnits { get; set; } = 40f;

    [JsonPropertyName("StartAfterSpeed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? LegacyStartAfterSpeed
    {
        get => null;
        set { if (value.HasValue) StartAfterSpeed = value.Value; }
    }

    [JsonPropertyName("ForwardLookAheadTicks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyForwardLookAheadTicks
    {
        get => null;
        set { if (value.HasValue) ForwardLookAheadTicks = value.Value; }
    }

    [JsonPropertyName("SideLookAheadTicks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacySideLookAheadTicks
    {
        get => null;
        set { if (value.HasValue) SideLookAheadTicks = value.Value; }
    }

    [JsonPropertyName("JumpLookAheadTicks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyJumpLookAheadTicks
    {
        get => null;
        set { if (value.HasValue) JumpLookAheadTicks = value.Value; }
    }

    [JsonPropertyName("MaxMoveUnits")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? LegacyMaxMoveUnits
    {
        get => null;
        set { if (value.HasValue) MaxMoveUnits = value.Value; }
    }
}

public sealed class DebugSettings
{
    [JsonPropertyName("ShowDebugHud")]
    public bool ShowRayCount { get; set; } = false;

    [JsonPropertyName("ShowDebugRays")]
    public bool ShowRayLines { get; set; } = false;

    [JsonPropertyName("ShowDebugPoints")]
    public bool ShowTargetPoints { get; set; } = false;

    [JsonPropertyName("ShowRayCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyShowRayCount
    {
        get => null;
        set { if (value.HasValue) ShowRayCount = value.Value; }
    }

    [JsonPropertyName("ShowRayLines")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyShowRayLines
    {
        get => null;
        set { if (value.HasValue) ShowRayLines = value.Value; }
    }

    [JsonPropertyName("ShowTargetPoints")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyShowTargetPoints
    {
        get => null;
        set { if (value.HasValue) ShowTargetPoints = value.Value; }
    }
}

public sealed class PerformanceSettings
{
    [JsonPropertyName("RaycastLimitPerFrame")]
    public int MaxRaycastsPerFrame { get; set; } = 0;

    [JsonPropertyName("EyeHeightOffsetUnits")]
    public float ViewerHeightOffset { get; set; } = 6f;

    [JsonPropertyName("ExtraBoxHeightUpUnits")]
    public float HitboxPaddingUp { get; set; } = 12f;

    [JsonPropertyName("ExtraBoxWidthUnits")]
    public float HitboxPaddingSide { get; set; } = 10f;

    [JsonPropertyName("ExtraBoxHeightDownUnits")]
    public float HitboxPaddingDown { get; set; } = 8f;

    [JsonPropertyName("AimRevealDistanceUnits")]
    public float AimRevealRadius { get; set; } = 2.5f;

    [JsonPropertyName("AimCheckDistanceUnits")]
    public float AimRayDistance { get; set; } = 8192f;

    [JsonPropertyName("FastSmokePreCheck")]
    public bool SmokeBatchPreFilterEnabled { get; set; } = true;

    [JsonPropertyName("MaxRaycastsPerFrame")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyMaxRaycastsPerFrame
    {
        get => null;
        set { if (value.HasValue) MaxRaycastsPerFrame = value.Value; }
    }

    [JsonPropertyName("ViewerHeightOffset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? LegacyViewerHeightOffset
    {
        get => null;
        set { if (value.HasValue) ViewerHeightOffset = value.Value; }
    }

    [JsonPropertyName("HitboxPaddingUp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? LegacyHitboxPaddingUp
    {
        get => null;
        set { if (value.HasValue) HitboxPaddingUp = value.Value; }
    }

    [JsonPropertyName("HitboxPaddingSide")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? LegacyHitboxPaddingSide
    {
        get => null;
        set { if (value.HasValue) HitboxPaddingSide = value.Value; }
    }

    [JsonPropertyName("HitboxPaddingDown")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? LegacyHitboxPaddingDown
    {
        get => null;
        set { if (value.HasValue) HitboxPaddingDown = value.Value; }
    }

    [JsonPropertyName("AimRevealRadius")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? LegacyAimRevealRadius
    {
        get => null;
        set { if (value.HasValue) AimRevealRadius = value.Value; }
    }

    [JsonPropertyName("AimRayDistance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? LegacyAimRayDistance
    {
        get => null;
        set { if (value.HasValue) AimRayDistance = value.Value; }
    }

    [JsonPropertyName("SmokeBatchPreFilterEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacySmokeBatchPreFilterEnabled
    {
        get => null;
        set { if (value.HasValue) SmokeBatchPreFilterEnabled = value.Value; }
    }

    [JsonPropertyName("RayHitFractionThreshold")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? LegacyRayHitFractionThreshold
    {
        get => null;
        set { }
    }

    [JsonPropertyName("RayHitDistanceThreshold")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? LegacyRayHitDistanceThreshold
    {
        get => null;
        set { }
    }
}
