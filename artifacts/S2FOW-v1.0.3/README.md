# S2FOW

Server-side anti-wallhack for Counter-Strike 2. Hides enemy players that the server cannot confirm are visible.

No client-side data means no wallhack can reveal them — the information simply does not exist on their machine.

## How It Works

Every network frame, S2FOW:

1. Snapshots every player's position, bounds, speed, and weapon
2. For each human player, checks every enemy with RayTrace line-of-sight
3. Blocks visibility through smoke grenades when enabled
4. Removes hidden enemies from the transmit list - pawn, weapons, wearables, hostage carry props, and scene-node child owners
5. Applies NOINTERP on reveal transitions to prevent rubber-banding
6. Fails open if the ray budget or entity-closure safety checks cannot complete

**Safety windows** during warmup, freeze time, round start, spawns, deaths, and round end ensure players are always visible when gameplay demands it. Dead and dying player pawns are never hidden by S2FOW.

## Entity Coverage

| Entity Type | Source | Handled |
|---|---|---|
| Player pawn | `C_CSPlayerPawn` | ✅ |
| Weapons | `WeaponServices.MyWeapons / ActiveWeapon / LastWeapon` | ✅ |
| Wearables | `C_BaseCombatCharacter.m_hMyWearables` (gloves, accessories) | ✅ |
| Scene-node children | `CGameSceneNode.Child / NextSibling / Owner` closure | ✅ |
| Carried hostage | `HostageServices.CarriedHostage` | ✅ |
| Hostage carry prop | `HostageServices.CarriedHostageProp` | ✅ |
| Player controller | Scoreboard only — left transmitted | ✅ |

Hiding all child entities atomically prevents the `FATAL ERROR: CL_CopyExistingEntity: missing client entity` crash. If S2FOW cannot prove the associated entity closure is complete for a live controlled pawn, it transmits that pawn instead of hiding it.

S2FOW also enforces an orphan-prevention invariant every CheckTransmit frame: if a pawn is already absent from a client's transmit set, S2FOW removes the known associated child entities from that same set. Plugins that mutate CheckTransmit after S2FOW can still violate this invariant.

## Requirements

- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) (API >= 276)
- RayTraceImpl and RayTraceApi installed on the server
- .NET 8

CounterStrikeSharp, RayTraceImpl, and RayTraceApi are external server prerequisites. They are not bundled in the S2FOW release package.

## Build

```
dotnet build S2FOW.sln -c Release
```

Output: `Plugin/bin/Release/net8.0/`

## Install

1. Build or download the release
2. Install CounterStrikeSharp on the server
3. Install RayTraceImpl and RayTraceApi on the server
4. Merge the release ZIP's `addons/` folder into your server's `csgo/addons/` folder
5. If installing manually, copy only `S2FOW.dll` and `S2FOW.deps.json` to `csgo/addons/counterstrikesharp/plugins/S2FOW/`
6. Restart the server - config generates automatically on first run

## Commands

| Command | Permission | Description |
|---|---|---|
| `css_fow_stats` | `@css/root` | Performance and safety summary: rays, budget, timing, warnings, smokes, closure counters |
| `css_fow_toggle` | `@css/root` | Toggle protection on/off at runtime |

## Config

Auto-generated at `csgo/addons/counterstrikesharp/configs/plugins/S2FOW/`. A guided version with inline comments is also written.

| Section | Controls |
|---|---|
| `General` | Master switch, death/spawn grace durations |
| `AntiWallhack` | Smoke blocking radius, lifetime, bloom duration |
| `TargetPoints` | Enemy LOS check points, FOV culling, distance tiering |
| `ViewerRays` | Observer ray origin prediction for moving players |
| `Performance` | Ray budget, hitbox padding, hit thresholds |
| `Debug` | In-game beam visuals and HUD overlay |

Default smoke blocking uses `SmokeBlockRadius = 144.0`, counts CS2 smokes as 19.25 seconds for S2FOW visibility (`SmokeLifetimeTicks = 1232` at 64 tick), and uses a 3 second bloom (`SmokeBloomDurationTicks = 192`) to avoid excessive hiding. Aim-ray force reveal stays enabled, but it no longer overrides smoke-blocked LOS.

## Debug

Enable in config for testing only:

- `Debug.ShowRayCount` — HUD overlay with per-enemy decision breakdown
- `Debug.ShowRayLines` — colored beams showing each raycast (yellow = visible, blue = blocked)
- `Debug.ShowTargetPoints` — marks visibility test points on enemy models

## Source Layout

```
Plugin/
  S2FOWPlugin.cs              Shared fields, helpers, plugin identity
  S2FOWPlugin.Lifecycle.cs    Load, unload, RayTrace connection
  S2FOWPlugin.Transmit.cs     CheckTransmit hot path
  S2FOWPlugin.Events.cs       Game event handlers
  S2FOWPlugin.Config.cs       Config loading, migration, diff logging
  S2FOWPlugin.Commands.cs     Admin commands
  S2FOWPlugin.Debug.cs        HUD overlay, startup banner
  S2FOWPlugin.Helpers.cs      Round phase, state resets, NOINTERP
  Config/                     Config schema and guided JSON writer
  Core/                       RaycastEngine, VisibilityManager, PlayerStateCache, SmokeTracker
  Models/                     PlayerSnapshot, SmokeData, DebugRay
  Util/                       PerformanceMonitor, VectorMath, diagnostics
tools/
  los-point-editor/           3D editor for tuning visibility test points
  apply_los_points_to_layout.py
```

## License

[MIT](LICENSE)
