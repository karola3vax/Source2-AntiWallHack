# S2FOW (Source2 Fog Of War)

![S2FOW Action](https://raw.githubusercontent.com/karola3vax/server-assets/main/s2fow.gif)

S2FOW is a server-side Counter-Strike 2 visibility plugin. It hides enemy players from a client when the server cannot confirm that the player should see them.

If the server does not send an enemy to a client, a client-side cheat cannot reveal that enemy from local game data.

## How It Works

Every network frame, S2FOW:

1. Reads each player's position, body size, movement, weapon, and connected child entities.
2. Checks whether each human player can see each enemy with RayTrace.
3. Hides players behind smoke when smoke hiding is enabled.
4. Removes the hidden player's body, weapons, wearables, carried hostage props, and scene children from that viewer's update.
5. Sends a crash-recovery full update after hide/show changes so clients resync cleanly.
6. Shows the player instead of hiding them whenever S2FOW cannot complete a safe check.

S2FOW always shows players during warmup, freeze time, round start, spawn grace, death grace, and round end. Dead and dying player bodies are not hidden.

## Entity Coverage

| Entity Type | Source | Handled |
|---|---|---|
| Player body | `C_CSPlayerPawn` | Yes |
| Weapons | `WeaponServices.MyWeapons / ActiveWeapon / LastWeapon` | Yes |
| Wearables | `C_BaseCombatCharacter.m_hMyWearables` | Yes |
| Scene children | `CGameSceneNode.Child / NextSibling / Owner` | Yes |
| Carried hostage | `HostageServices.CarriedHostage` | Yes |
| Hostage carry prop | `HostageServices.CarriedHostageProp` | Yes |
| Player controller | Scoreboard/controller data is left visible | Yes |

![Visibility Point Editor](https://raw.githubusercontent.com/karola3vax/server-assets/main/los-editor.png)

S2FOW hides connected player entities together. This prevents the `FATAL ERROR: CL_CopyExistingEntity: missing client entity` crash that can happen when a client loses a player body but still receives one of that player's child entities.

S2FOW also sends a full client update after hide/show changes. This crash-recovery path is active when the packaged `gamedata/s2fow.gamedata.json` is installed with the plugin.

## Requirements

- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) (API >= 276)
- [Metamod:Source](https://www.sourcemm.net/downloads.php?branch=dev) (Dev build)
- [Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace) (RayTraceImpl and RayTraceApi installed on the server)
- .NET 8

CounterStrikeSharp, Metamod:Source, and Ray-Trace are external server prerequisites. They are not bundled in the S2FOW release package.

## Build

```powershell
dotnet build S2FOW.sln -c Release
```

Output: `Plugin/bin/Release/net8.0/`

## Install

1. Build or download the release.
2. Install Metamod:Source and CounterStrikeSharp on the server.
3. Install Ray-Trace with both RayTraceImpl and RayTraceApi.
4. Merge the release ZIP's `addons/` folder into the server's `csgo/addons/` folder.
5. For manual installs, copy `S2FOW.dll`, `S2FOW.deps.json`, and `gamedata/s2fow.gamedata.json` into the S2FOW plugin folder layout.
6. Restart the server. The config is generated automatically on first run.

## Commands

| Command | Permission | Description |
|---|---|---|
| `css_s2fow_status` | `@css/root` | Show status, workload, warnings, and crash protection counters |
| `css_fow_stats` | `@css/root` | Same as `css_s2fow_status`; kept for existing admin habits |
| `css_s2fow_toggle` | `@css/root` | Turn S2FOW player hiding on or off |
| `css_fow_toggle` | `@css/root` | Same as `css_s2fow_toggle`; kept for existing admin habits |

## Config

Auto-generated at `csgo/addons/counterstrikesharp/configs/plugins/S2FOW/`. S2FOW writes a guided config with short comments. Old v32 config files still load, then S2FOW rewrites them with the new plain-English names.

| Section | Controls |
|---|---|
| `Main` | `ProtectionEnabled`, death grace, and round-start visibility |
| `SmokeVisibility` | Smoke hiding on/off, smoke size, smoke lifetime, and smoke growth |
| `EnemyCheckPoints` | Body points checked on enemies and reduced-check distance/view rules |
| `ViewerEyePrediction` | Small viewer eye prediction for fast movement |
| `Advanced` | Raycast limit, fast smoke pre-check, box padding, and aim reveal distance |
| `Debug` | Debug HUD, debug rays, and debug body points |

Common settings:

| Setting | Plain meaning |
|---|---|
| `Main.ProtectionEnabled` | Main on/off switch |
| `Main.KeepDeadPlayersVisibleTicks` | Keeps dead players visible briefly; 128 ticks is about 2 seconds on a 64 tick server |
| `Main.ShowEveryoneAtRoundStartTicks` | Shows everyone at live round start; 32 ticks is about 0.5 seconds |
| `SmokeVisibility.HidePlayersBehindSmoke` | Lets smoke hide players from viewers |
| `SmokeVisibility.SmokeSizeUnits` | Approximate smoke size in Source 2 units |
| `SmokeVisibility.SmokeLastsTicks` | How long smoke can hide players; 1232 ticks is about 19.25 seconds |
| `SmokeVisibility.SmokeGrowsTicks` | How long smoke takes to reach full size; 192 ticks is about 3 seconds |

The old `RayHitFractionThreshold` and `RayHitDistanceThreshold` settings are no longer written. Visibility now treats any world hit before the target point as blocked.

## Debug

Enable debug settings only while testing:

- `Debug.ShowDebugHud` shows the in-game S2FOW HUD with plain labels such as `Enemies checked`, `Raycasts`, and `Why shown/hidden`.
- `Debug.ShowDebugRays` draws sight checks sent to RayTrace.
- `Debug.ShowDebugPoints` marks the enemy body points S2FOW checks.

## Source Layout

```text
Plugin/
  S2FOWPlugin.cs              Shared fields, helpers, plugin identity
  S2FOWPlugin.Lifecycle.cs    Load, unload, RayTrace connection
  S2FOWPlugin.Transmit.cs     CheckTransmit player hiding
  S2FOWPlugin.Events.cs       Game event handlers
  S2FOWPlugin.Config.cs       Config loading, migration, diff logging
  S2FOWPlugin.Commands.cs     Admin commands
  S2FOWPlugin.Debug.cs        HUD overlay, startup banner
  S2FOWPlugin.Helpers.cs      Round state, resets, NOINTERP
  Config/                     Config schema and guided JSON writer
  Core/                       RaycastEngine, VisibilityManager, PlayerStateCache, SmokeTracker
  Models/                     PlayerSnapshot, SmokeData, DebugRay
  Util/                       PerformanceMonitor, VectorMath, diagnostics
gamedata/
  s2fow.gamedata.json         Full-update crash-recovery offsets
tools/
  los-point-editor/           3D editor for tuning visibility points
  apply_los_points_to_layout.py
```

## License

[AGPLv3](LICENSE)
