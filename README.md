<div align="center">

# S2FOW

Server-side anti-wallhack for Counter-Strike 2

[![CounterStrikeSharp](https://img.shields.io/badge/CounterStrikeSharp-1.0.364-0f172a?style=for-the-badge)](https://github.com/roflmuffin/CounterStrikeSharp)
[![.NET](https://img.shields.io/badge/.NET-8.0-1d4ed8?style=for-the-badge)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
[![License](https://img.shields.io/badge/License-MIT-111827?style=for-the-badge)](LICENSE)

Reduce hidden-state visibility leaks by withholding useful entity data until a player is truly visible.

</div>

## Overview

S2FOW hooks `CheckTransmit` on every server tick, uses the Ray-Trace API for line-of-sight checks against 19 skeleton primitives and 8 AABB fallback corners per target, and removes entity data from the transmission list before it reaches the client.

### What it blocks

| Category | How |
| :--- | :--- |
| **Hidden player pawns** | Ray-traced multi-point LOS with distance-tiered caching and movement prediction |
| **Radar / spotted-state** | Scrubs `m_bSpottedByMask` bits per observer-target pair |
| **Smoke vision** | Spherical bloom-phase blocking (144u radius, 18.5s lifetime) |
| **Grenade trajectory ESP** | Hides projectiles from hidden throwers, reveals within 256u proximity |
| **Bullet impact ESP** | Hides impact decals from hidden shooters |
| **Dropped weapon ESP** | Temporal ownership tracking for 2s after death, proximity reveal at 192u |
| **Planted C4 radar + world** | Scrubs spotted bits and hides entity for CTs without true LOS |

### How visibility is decided

```text
CheckTransmit (each server tick)
  |-- Build player snapshots             [PlayerStateCache]
  |-- Update projectile positions         [ProjectileTracker]
  |-- For each observer (alive, human, playing):
  |     |-- For each enemy target:
  |     |     |-- Distance cull / FOV classification
  |     |     |-- Smoke blocking check    [SmokeTracker]
  |     |     |-- Crosshair reveal check
  |     |     |-- Visibility cache lookup [VisibilityCache]
  |     |     |-- Multi-point ray trace   [RaycastEngine]
  |     |     |     |-- 19 skeleton primitives (head -> ankles)
  |     |     |     +-- 8 AABB corners (fallback)
  |     |     |-- Peek grace window
  |     |     +-- If hidden: remove pawn + linked entities
  |     |-- Hide grenades from hidden throwers
  |     +-- Hide impact decals from hidden shooters
  |-- Scrub spotted-state bits            [SpottedStateScrubber]
  |-- Block planted C4 radar/world
  +-- Force flex state resync on transitions
```

## Requirements

- [Counter-Strike 2 Dedicated Server](https://developer.valvesoftware.com/wiki/Counter-Strike_2/Dedicated_Servers)
- [Metamod:Source](https://www.sourcemm.net/downloads.php?branch=dev)
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) >= v1.0.364
- [Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace) (both CSS API and MM packages)

S2FOW stays inactive until Ray-Trace is installed and loaded. The `css_fow_stats` command reports ray tracing readiness.

## Install

**Step 1** — Install Metamod:Source and CounterStrikeSharp.

**Step 2** — Install Ray-Trace (CSS API package + the MM package for your OS):

```text
RayTrace-CSS-API-v1.0.7.tar.gz
RayTrace-MM-v1.0.7-linux.tar.gz    (Linux)
RayTrace-MM-v1.0.7-windows.tar.gz  (Windows)
```

**Step 3** — Copy plugin files:

```text
csgo/addons/counterstrikesharp/plugins/S2FOW/
  S2FOW.dll
  S2FOW.deps.json
  RayTraceApi.dll
```

**Step 4** — Start the server once to generate config files:

```text
csgo/addons/counterstrikesharp/configs/plugins/S2FOW/S2FOW.json
csgo/addons/counterstrikesharp/configs/plugins/S2FOW/S2FOW.example.json
```

**Step 5** — Edit `S2FOW.json` to taste. `S2FOW.example.json` is an auto-generated defaults snapshot for reference.

**Step 6** — Verify with `css_fow_stats`.

## Profiles

S2FOW auto-detects the server game mode and applies a tuning profile at runtime. The JSON on disk is never modified by profile overrides.

| Profile | Players | Security | Cache TTL | Budget | Use case |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `auto` | varies | varies | varies | varies | Recommended. Detects mode from convars and map prefix. |
| `competitive` | 5v5 | Strict | 2-20 ticks | 4096 / 192 per player | Tightest protection posture. |
| `wingman` | 2v2 | Strict | 2-16 ticks | 4096 / 256 per player | Like competitive with more headroom. |
| `casual` | 10v10 | Balanced | 2-28 ticks | 4096 / 96 per player | Balanced defaults (config as-is). |
| `deathmatch` | 20-64 | Balanced | 3-36 ticks | 6144 / 64 per player | Performance-first for high counts. |
| `retake` | 10-20 | Balanced | 2-28 ticks | 4096 / 128 per player | Post-plant focus with C4 priority. |
| `custom` | any | any | any | any | No auto-tuning, config values as written. |

### Auto-detection order

1. `game_type` / `game_mode` convars (competitive, wingman, DM, casual)
2. Retake plugin convars (`sm_retakes_enabled`, `css_retakes_enabled`)
3. Map prefix (`retake_*`, `retakes_*`, `dm_*`, `aim_*`, `ffa_*`, `hs_*`)
4. `sv_maxplayers` heuristic (<=4 wingman, <=12 competitive, >24 DM)
5. Fallback: casual

Runtime override:

```text
css_fow_profile <auto|competitive|wingman|casual|deathmatch|retake|custom>
```

## Commands

All commands require `@css/root` permission.

| Command | Purpose |
| :--- | :--- |
| `css_fow_stats` | Live runtime diagnostics: raycasts, cache hits, budget events, entity tracking, error counters, smoke/projectile state, quality level. |
| `css_fow_toggle` | Enable or disable protection instantly. Resets runtime state. |
| `css_fow_profile` | Show or override the active runtime profile. |

## Configuration

Config version: **22**. S2FOW auto-migrates older configs.

All values are validated on load. Invalid values are clamped to safe ranges.

### General

| Setting | Default | Description |
| :--- | :--- | :--- |
| `Enabled` | `true` | Master on/off switch. |
| `SecurityProfile` | `Balanced` | `Strict` hides aggressively, `Balanced` is the middle ground, `Compat` is least intrusive. |
| `DeathVisibilityDurationTicks` | `128` | Keep dead players visible for this long (128 = 2s at 64 tick). |
| `AutoProfile` | `Auto` | Auto-detect game mode or force a specific profile. |
| `RoundStartRevealDurationTicks` | `32` | Reveal everyone briefly at round start (32 = 0.5s). |

### AntiWallhack

| Setting | Default | Description |
| :--- | :--- | :--- |
| `BlockGrenadeESP` | `true` | Hide grenades from hidden throwers. |
| `BlockRadarESP` | `true` | Scrub spotted-state bits for hidden enemies. |
| `BlockBulletImpactESP` | `true` | Hide bullet impact decals from hidden shooters. |
| `BlockDroppedWeaponESPDurationTicks` | `128` | Temporal ownership duration for dropped weapons after death (0 = off). |
| `DroppedWeaponRevealDistance` | `192.0` | Reveal dropped weapons when observer is this close (0 = always hidden). |
| `BlockBombRadarESP` | `true` | Scrub planted C4 spotted bits for CTs without LOS. |
| `HidePlantedBombEntityWhenNotVisible` | `true` | Hide planted C4 world entity from CTs without LOS. |
| `SmokeBlocksWallhack` | `true` | Use smoke as a visibility blocker. |
| `SmokeBlockRadius` | `144.0` | CS2 smoke sphere radius in Hammer units. |
| `SmokeLifetimeTicks` | `1184` | Smoke active duration (1184 = 18.5s). Safety-net; engine event is primary removal. |
| `SmokeBlockDelayTicks` | `48` | Bloom time for new smokes to reach full radius (48 = 0.75s). |
| `SmokeGrowthStartFraction` | `0.50` | Starting fraction of full radius during bloom phase. |
| `CrosshairRevealDistance` | `3200.0` | Max range for crosshair-based reveal checks. |
| `CrosshairRevealRadius` | `64.0` | Aim proximity radius for crosshair reveal. |
| `MaxVisibilityDistance` | `0.0` | Hard distance cutoff (0 = disabled, recommended). |
| `PeekGracePeriodTicks` | `12` | Keep enemy visible briefly after peek (12 = 188ms). Invalidated if target moves >64u. |
| `GrenadeRevealDistance` | `256.0` | Reveal hidden-thrower grenades within this proximity. |

### MovementPrediction

| Setting | Default | Description |
| :--- | :--- | :--- |
| `MinSpeed` | `15.0` | Minimum speed before prediction activates. |
| `EnemyForwardLookaheadTicks` | `8.0` | Target forward prediction (8 ticks = 31u at full speed). |
| `EnemySidewaysLookaheadTicks` | `8.0` | Target strafe prediction. |
| `EnemyMaxLeadDistance` | `1.0` | Cap on target lead offset. |
| `EnemyVerticalLookaheadTicks` | `6.0` | Target jump/fall prediction. |
| `EnemyVerticalMaxLeadDistance` | `16.0` | Cap on vertical target lead. |
| `ViewerForwardLookaheadTicks` | `4.0` | Observer forward peek prediction (4 ticks = 15.6u). |
| `ViewerJumpAnticipationTicks` | `12.0` | Observer jump anticipation. |
| `ViewerStrafeAnticipationTicks` | `24.0` | Observer strafe anticipation. |
| `ViewerMaxLeadDistance` | `64.0` | Cap on observer lead offset (64u = 2 hull widths). |

### Performance

| Setting | Default | Description |
| :--- | :--- | :--- |
| `MaxRaycastsPerFrame` | `2048` | Hard cap on total raycasts per frame. |
| `BudgetExceededPolicy` | `CacheOnly` | `CacheOnly` reuses cache then hides, `FailClosed` always hides, `FailOpen` shows (rate-limited to 3/frame then falls closed). |
| `AdaptiveBudgetEnabled` | `true` | Scale budget with alive player count. |
| `BaseBudgetPerPlayer` | `96` | Rays added per alive player. |
| `MaxAdaptiveBudget` | `4096` | Hard ceiling for adaptive budget. |
| `PerObserverBudgetFairnessEnabled` | `true` | Distribute budget fairly across observers. |
| `MinObserverBudgetShare` | `0.03` | Floor fraction per observer to prevent starvation. |
| `FovSamplingEnabled` | `true` | Reduce work for peripheral/rear targets. |
| `FullDetailFovHalfAngleDegrees` | `45.0` | Full cone = full primitive budget. |
| `PeripheralFovHalfAngleDegrees` | `160.0` | Peripheral cone = half primitives. Rear = no raycasts. |
| `VelocityCacheExtensionEnabled` | `true` | Extend cache for stationary pairs (up to 3x). |
| `StaggeredCacheExpiryEnabled` | `true` | Spread cache expirations across ticks. |
| `ObserverPhaseSpreadEnabled` | `true` | Prevent thundering-herd cache invalidation spikes. |
| `ObserverPhaseSpreadTicks` | `4` | Ticks over which observer evaluations are spread. |
| `SmokeBatchPreFilterEnabled` | `true` | Skip smoke checks for pairs far from all smokes. |

#### Distance tiers

| Tier | Threshold | Hidden cache | Visible cache | Check points |
| :--- | :--- | :--- | :--- | :--- |
| CQB | < 768u (~19m) | 2 ticks | 2 ticks | 19 (full) |
| Mid | < 1800u (~46m) | 6 ticks | 4 ticks | 19 (full) |
| Far | < 3200u (~81m) | 14 ticks | 5 ticks | 11 (reduced) |
| XFar | >= 3200u | 28 ticks | 8 ticks | 7 (minimal) |

#### Velocity cache extension

| Movement state | TTL multiplier |
| :--- | :--- |
| Both stationary (< 6u) | 3x |
| One stationary + one slow (< 20u) | 3x |
| One stationary OR both slow | 2x |
| Both moving | 1x (no extension) |

### Debug

| Setting | Default | Description |
| :--- | :--- | :--- |
| `ShowRayCount` | `false` | Per-observer trace counters on screen. |
| `ShowRayLines` | `false` | Draw actual visibility rays. |
| `ShowTargetPoints` | `false` | Draw hitbox primitive centers and AABB corners. |

## Architecture

### Source layout

```text
Plugin/
  S2FOWPlugin.cs                     Main entry point, partial class root
  S2FOWPlugin.Commands.cs            css_fow_* command handlers
  S2FOWPlugin.Config.cs              Config migrations, profile resolution
  S2FOWPlugin.Debug.cs               Debug overlay rendering
  S2FOWPlugin.Events.cs              Game event handlers (round, player, bomb, smoke)
  S2FOWPlugin.Helpers.cs             Bomb tracking, round phase utilities
  S2FOWPlugin.Lifecycle.cs           Load, Unload, InitializeRayTrace
  S2FOWPlugin.Transmit.cs            CheckTransmit hot path
  Config/
    S2FOWConfig.cs                   5-section config with validation (v22)
    S2FOWConfigWriter.cs             Guided JSON generation with atomic write
    AutoConfigProfile.cs             Per-profile runtime overrides
    AutoConfigProfiler.cs            Game mode auto-detection
  Core/
    Constants.cs                     MaxSlots=64, MaxEntityIndex=16384
    VisibilityManager.cs             Core visibility decision orchestrator
    VisibilityCache.cs               Observer-target pair cache with TTLs
    RaycastEngine.cs                 Ray-Trace API interface, geometry caching
    RaycastMath.cs                   Movement prediction, vector math
    PlayerStateCache.cs              Per-frame snapshots, entity ownership
    EntityOwnershipResolver.cs       Handle/scene-graph ownership resolution
    SmokeTracker.cs                  Smoke bloom + sphere blocking
    ProjectileTracker.cs             Grenade-to-player mapping
    ImpactTracker.cs                 Bullet impact association
    SpottedStateScrubber.cs          Radar bit scrubbing
    AdaptiveQualityScaler.cs         Frame-time monitoring, quality scaling
    SceneNodeTraverser.cs            Entity hierarchy traversal
    Cs2VisibilityPrimitiveLayout.cs  19 canonical hitbox primitives
    DebugAabbRenderer.cs             Debug visualization
  Models/
    PlayerSnapshot.cs                Per-frame player state struct
    SmokeData.cs                     Smoke position and timing
    DebugRay.cs                      Debug ray recording
    DebugVisualState.cs              Debug overlay state
  Util/
    PerformanceMonitor.cs            Frame timing and raycast statistics
    VectorMath.cs                    SIMD distance and intersection helpers
    PluginDiagnostics.cs             Thread-safe error counters
    PluginOutput.cs                  Console output formatter
    PluginText.cs                    UI text constants
```

### Key design decisions

- **CCSPlayerController is never hidden.** Removing it from transmission causes `CL_CopyExistingEntity` client crashes during delta encoding. Only pawn-linked entities are filtered.

- **Temporal ownership.** Dropped weapons retain their dead owner's association for a configurable duration to prevent floor-weapon ESP from revealing death locations.

- **Adaptive quality scaler.** Monitors server frame time with a rolling 64-frame window. When frame time exceeds thresholds, cache TTLs and check point counts scale automatically with hysteresis to prevent oscillation.

- **Per-observer budget fairness.** The ray budget is distributed across observers so no single player can starve others in slot order. A minimum share floor prevents starvation even under extreme budget pressure.

- **FailOpen rate limiting.** When `BudgetExceededPolicy` is `FailOpen`, a per-frame cap of 3 uncached reveals prevents a starved raycast engine from exposing all hidden players.

- **Peek grace with movement check.** The grace window that keeps a recently-visible enemy transmitted is invalidated if the target moves more than 64 units, preventing abuse through peek-and-reposition.

- **Config validation.** All 70+ config fields are clamped to safe ranges on load. Negative budgets, zero cache TTLs, and invalid distance orderings are impossible at runtime.

## Build

```powershell
dotnet restore .\S2FOW.sln
dotnet build .\S2FOW.sln -c Release
```

Output:

```text
Plugin/bin/Release/net8.0/
  S2FOW.dll          (~167 KB)
  S2FOW.deps.json
  RayTraceApi.dll    (~13 KB)
```

Requires .NET SDK 8.0. The GitHub Actions workflow (`.github/workflows/build.yml`) builds on `windows-latest` and uploads a ready-to-deploy artifact.

## Notes

- Config groups: `General`, `AntiWallhack`, `MovementPrediction`, `Debug`, `Performance`.
- Config is auto-migrated through versions 20 -> 21 -> 22. Legacy flat-key configs are detected and protection is disabled until the config is updated.
- The `Dependencies/` directory contains third-party source trees used locally during development. They are not part of the deployed plugin.
- Smoke blocking uses a bloom phase model: radius grows from `SmokeGrowthStartFraction` (50%) to 100% over `SmokeBlockDelayTicks` (48 ticks / 0.75s).
- All visibility primitives are extracted from canonical CS2 hitbox data (`tools/cs2_player_hitboxes_canonical.json`).

## License

Licensed under **MIT**. See [LICENSE](LICENSE) for details.
