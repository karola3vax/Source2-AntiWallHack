# S2FOW - Source 2 Fog Of War

![S2FOW in action](https://raw.githubusercontent.com/karola3vax/server-assets/main/s2fow.gif)

S2FOW is a server-side visibility protection plugin for Counter-Strike 2.

It does one job: when a player should not be able to see an enemy, the server can stop sending that enemy to that player's game client. If the client never receives the hidden enemy, a client-side cheat has far less local player data to reveal.

This is not a ban system, not an anti-aim system, and not a sound/radar blocker. It is a practical fog-of-war layer for player visibility.

## Why Server Owners Use It

Most wallhack protection fails when the enemy data is already on the player's computer. S2FOW attacks that problem earlier.

Instead of only trusting the client, S2FOW checks visibility on the server every network frame. If an enemy is behind walls or fully blocked by smoke, S2FOW can remove that enemy's body and connected player objects from only that viewer's update.

The result is a cleaner, harder-to-abuse visibility pipeline:

| What matters | What S2FOW does |
|---|---|
| Hidden enemies | Stops sending hidden enemy player bodies to that viewer |
| Weapons and wearables | Hides them together with the player body |
| Smoke | Can hide enemies fully blocked by smoke |
| Safety | Shows players when S2FOW is unsure |
| Crash mitigation | Sends client refreshes after hide/show changes |
| Server control | Runs from the server, not from the player client |

## Real Behavior

Every network frame, S2FOW follows this flow:

1. Read current players, teams, positions, movement, weapons, body size, and connected player objects.
2. Skip hiding during warmup, freeze time, round end, and other moments where everyone should be visible.
3. Keep recently spawned and recently dead players visible for a short grace period.
4. Check smoke first. If smoke blocks every checked path to the enemy, the enemy can be hidden.
5. If the viewer is already aiming close to the enemy body, show the enemy to avoid a harsh pop-in.
6. Ask RayTrace whether walls block the checked body points.
7. Hide the enemy only when S2FOW completed a safe check and the enemy should not be visible.
8. Show the enemy whenever data is missing, RayTrace fails, or the configured raycast budget is exhausted.

That last rule is important: S2FOW is built to fail safe. When the plugin cannot prove a hide is safe, it shows the player.

## What Gets Hidden

S2FOW does not only remove the player body. It also removes the connected objects that can crash a client or leak the hidden player visually.

| Object | Covered |
|---|---|
| Player body | Yes |
| Inventory weapons | Yes |
| Active weapon | Yes |
| Previous weapon used for switching animations | Yes |
| Wearables | Yes |
| Attached scene objects | Yes |
| Carried hostage objects | Yes |
| Hostage carry prop | Yes |
| Scoreboard/controller data | Left visible |

This connected-object handling is part of the crash-mitigation design. A CS2 client can fail badly if it receives a weapon, wearable, or attached object that points to a player body it never received.

## Crash Recovery

S2FOW includes a full-update recovery path for the viewer whose visibility list changed.

When S2FOW hides or shows enemies for a viewer, it can request a client refresh for that viewer. Requests are combined to one refresh per viewer per frame and throttled to once every 32 ticks per viewer.

This requires the packaged file:

```text
gamedata/s2fow.gamedata.json
```

If full-update support is unavailable, S2FOW logs the problem and continues running. Visibility decisions still follow the safe-show rule.

## Smoke Visibility

S2FOW can hide enemies behind smoke when `SmokeVisibility.HidePlayersBehindSmoke` is enabled.

Smoke is modeled as a growing sphere:

- It starts smaller when it blooms.
- It grows to full size over the configured tick window.
- It stops blocking after the configured lifetime or when the engine reports the smoke expired.
- Fast pre-checking skips expensive smoke math when no smoke is near the viewer/enemy path.

Smoke hiding only happens when smoke blocks all checked paths for that enemy. A partly visible enemy is shown.

## Visibility Points

![Visibility point editor](https://raw.githubusercontent.com/karola3vax/server-assets/main/los-editor.png)

S2FOW checks real points on the CS2 player model:

- 35 model-based body and weapon points
- 8 backup box corners around the enemy
- Weapon-aware muzzle points for pistol, rifle, and sniper weapon classes
- Movement prediction for both the viewer's eyes and the enemy body

The included editor under `tools/los-point-editor/` is used to inspect and tune these points. The runtime layout is generated into:

```text
Plugin/Core/Cs2VisibilityPrimitiveLayout.cs
```

## What S2FOW Does Not Claim To Do

S2FOW is intentionally narrow. It does not claim unsupported behavior.

It does not:

- detect or ban cheaters
- hide footsteps or sound cues
- control radar or spotted-state behavior
- hide projectiles, grenades, dropped weapons, or map props
- replace server-side moderation, demos, or other anti-cheat tools
- control Source 2 PVS outside the player objects it removes from transmit

It focuses on player visibility data sent to each client.

## Requirements

- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp), API 276 or newer
- [Metamod:Source](https://www.sourcemm.net/downloads.php?branch=dev), dev build
- [Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace), with both RayTraceImpl and RayTraceApi installed
- .NET 8 for building

RayTrace is required. If RayTrace is not loaded, S2FOW stays idle and players are sent normally.

## Install

1. Install Metamod:Source and CounterStrikeSharp on the server.
2. Install Ray-Trace with both RayTraceImpl and RayTraceApi.
3. Build S2FOW or download a release.
4. Copy the release package's `addons/` folder into the server's `csgo/addons/` folder.
5. Make sure `s2fow.gamedata.json` lands in CounterStrikeSharp's global `gamedata` folder.
6. Restart the server.
7. Check the server console for the S2FOW startup banner and RayTrace connection message.

S2FOW generates its config automatically on first run.

## Build

```powershell
dotnet build S2FOW.sln -c Release
```

Build output:

```text
Plugin/bin/Release/net8.0/
```

Drop-in server package:

```text
Plugin/bin/Release/S2FOW-dropin/
  addons/
    counterstrikesharp/
      gamedata/
        s2fow.gamedata.json
      plugins/
        S2FOW/
          S2FOW.dll
          S2FOW.deps.json
```

For server installs, copy the `addons` folder from `S2FOW-dropin` into the server's `csgo/addons` folder. Do not put `s2fow.gamedata.json` inside the plugin folder; CounterStrikeSharp reads it from `addons/counterstrikesharp/gamedata`.

## Commands

All commands require `@css/root`.

| Command | What it does |
|---|---|
| `css_s2fow_status` | Shows protection status, workload, visibility decisions, crash protection counters, and warnings |
| `css_fow_stats` | Same status command, kept for older admin habits |
| `css_s2fow_toggle` | Turns S2FOW protection on or off |
| `css_fow_toggle` | Same toggle command, kept for older admin habits |

When protection is disabled with the toggle command, S2FOW sends players normally and requests full updates so clients refresh cleanly.

## Config

Config files are generated here:

```text
csgo/addons/counterstrikesharp/configs/plugins/S2FOW/
```

S2FOW writes a guided JSON config with plain-English comments. Current config schema: `33`.

Old v32 config files still load. S2FOW reads the old names once, then rewrites the file with the current plain-English names.

| Section | Plain meaning |
|---|---|
| `Main` | Main on/off behavior, death grace, and round-start visibility |
| `SmokeVisibility` | Smoke hiding, smoke size, smoke lifetime, and smoke growth |
| `EnemyCheckPoints` | Enemy body checks, distance rules, view-angle rules, and enemy movement prediction |
| `ViewerEyePrediction` | Small viewer eye prediction for fast movement and jumps |
| `Debug` | Optional HUD, ray lines, and body-point drawings |
| `Advanced` | Raycast budget, smoke pre-checking, box padding, aim reveal, and eye height offset |

Common settings:

| Setting | Default | Meaning |
|---|---:|---|
| `Main.ProtectionEnabled` | `true` | Main protection switch |
| `Main.KeepDeadPlayersVisibleTicks` | `128` | Keeps killed players visible briefly, about 2 seconds on 64 tick |
| `Main.ShowEveryoneAtRoundStartTicks` | `32` | Shows everyone briefly when live play starts, about 0.5 seconds on 64 tick |
| `SmokeVisibility.HidePlayersBehindSmoke` | `true` | Allows smoke to hide enemies |
| `SmokeVisibility.SmokeSizeUnits` | `130.0` | Approximate smoke blocking size in Source 2 units |
| `SmokeVisibility.SmokeLastsTicks` | `1232` | Smoke blocking lifetime, about 19.25 seconds on 64 tick |
| `SmokeVisibility.SmokeGrowsTicks` | `192` | Smoke growth time, about 3 seconds on 64 tick |
| `EnemyCheckPoints.UseFewerChecksFarAway` | `true` | Uses lighter checks for far enemies |
| `EnemyCheckPoints.UseFewerChecksOutsideView` | `false` | Optional lighter checks outside the viewer's aim direction |
| `Advanced.RaycastLimitPerFrame` | `0` | `0` means unlimited raycasts, which avoids delayed visibility decisions |
| `Advanced.FastSmokePreCheck` | `true` | Skips detailed smoke checks when smoke is nowhere near the viewer/enemy path |
| `Debug.ShowDebugHud` | `false` | Shows the in-game S2FOW debug HUD |
| `Debug.ShowDebugRays` | `false` | Draws the wall checks S2FOW sends to RayTrace |
| `Debug.ShowDebugPoints` | `false` | Draws the enemy body points S2FOW checks |

The old `RayHitFractionThreshold` and `RayHitDistanceThreshold` settings are no longer written. Current visibility behavior is strict: any world hit before the checked enemy point blocks that path.

## Status Output

Use:

```text
css_s2fow_status
```

The status output is grouped for server owners:

- `Status`: protection on/off, RayTrace readiness, round state, config schema
- `Work`: player checks, raycasts, average plugin time, average raycasts, peak raycasts
- `Visibility decisions`: smoke hides, blocked-sight hides, safe-show fallbacks
- `Crash protection`: full updates sent, throttled, failed, requested, and combined
- `Warnings`: config write failures, player-object read failures, incomplete child collection, RayTrace failures

## Debug Mode

Debug mode is for testing, not normal public play.

When enabled, S2FOW can draw:

- a small HUD with labels like `Enemies checked`, `Raycasts`, and `Why shown/hidden`
- yellow ray lines for clear checks
- blue ray lines for blocked checks
- white body points and blue backup box points around enemies

These debug drawings create real game objects, so keep them off during normal matches.

## Project Layout

```text
Plugin/
  S2FOWPlugin.cs              Plugin identity, shared fields, reading guide
  S2FOWPlugin.Lifecycle.cs    Loading, unloading, config setup, RayTrace connection
  S2FOWPlugin.Transmit.cs     Per-viewer player hiding in CheckTransmit
  S2FOWPlugin.Events.cs       Match events: spawn, death, smoke, bomb, map changes
  S2FOWPlugin.FullUpdate.cs   Viewer refresh queue and crash-recovery updates
  S2FOWPlugin.Commands.cs     Admin commands
  S2FOWPlugin.Debug.cs        Startup banner, status text, debug HUD
  Config/                     Config schema and guided config writer
  Core/                       Visibility decisions, ray checks, snapshots, smoke, full update bridge
  Models/                     Player snapshots, smoke data, debug drawing data
  Util/                       Text, diagnostics, performance counters, vector math
gamedata/
  s2fow.gamedata.json         Source copy for package building; installs to addons/counterstrikesharp/gamedata
tools/
  los-point-editor/           Browser editor for visibility points
  apply_los_points_to_layout.py
  extract_cs2_visibility_primitives.py
```

## Recommended Server Owner Flow

1. Install dependencies first: CounterStrikeSharp, Metamod:Source, and Ray-Trace.
2. Install S2FOW from the drop-in package so the plugin and gamedata land in the right folders.
3. Start the server and confirm RayTrace connects.
4. Leave defaults on the first test.
5. Use `css_s2fow_status` to watch workload and warnings.
6. Enable debug drawings only on a private test server.
7. Tune config only if your server has a specific visibility or performance issue.

## License

S2FOW is licensed under [AGPLv3](LICENSE).
