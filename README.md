# S2FOW

S2FOW is a server-side anti-wallhack plugin for Counter-Strike 2 built on CounterStrikeSharp. It hides players and leak-prone world entities with visibility checks, ray tracing, radar scrubbing, smoke blocking, and transmit filtering.

## Highlights

- Server-authoritative anti-ESP logic in `CheckTransmit`
- Player, grenade, dropped-weapon, bullet-impact, radar, smoke, and planted-C4 leak protection
- Distance-tiered visibility evaluation with caching, adaptive budgeting, and FOV-based culling
- Runtime auto-profiles for competitive, wingman, casual, deathmatch, retake, and custom servers
- Guided JSON config generation with migration support for older config formats
- Built-in debug overlays for trace counts, target points, and ray lines

## Project Layout

- `Plugin/`
  - `Config/`: config model, guided config writer, auto-profile detection
  - `Core/`: visibility engine, caches, entity ownership, smoke/projectile/impact tracking
  - `Models/`: lightweight runtime structs
  - `Util/`: math, output formatting, diagnostics
- `Dependencies/`: local development dependencies used for building
- `tools/`: local extraction/reference utilities

## Requirements

- Counter-Strike 2 dedicated server
- CounterStrikeSharp API `1.0.364`
- .NET 8 SDK for local builds
- RayTrace plugin/runtime compatible with `RayTraceApi.dll`

## Installation

Build the project or use a release artifact, then copy the runtime files into your server:

```text
csgo/addons/counterstrikesharp/plugins/S2FOW/S2FOW.dll
csgo/addons/counterstrikesharp/plugins/S2FOW/S2FOW.deps.json
csgo/addons/counterstrikesharp/plugins/S2FOW/RayTraceApi.dll
```

S2FOW stores its config at:

```text
csgo/addons/counterstrikesharp/configs/plugins/S2FOW/S2FOW.json
```

On first successful config parse, S2FOW also writes a guided example file next to the live config:

```text
csgo/addons/counterstrikesharp/configs/plugins/S2FOW/S2FOW.example.json
```

## Configuration

The JSON is ordered from simple day-to-day settings to advanced tuning:

- `General`: enable/disable, security profile, auto-profile, round-start reveal, death visibility
- `AntiWallhack`: grenade/radar/impact/bomb/smoke protections and reveal distances
- `MovementPrediction`: forward/strafe/jump look-ahead tuning
- `Debug`: overlay and visual troubleshooting toggles
- `Performance`: budgets, cache timings, FOV sampling, reverse-link scanning, unresolved entity policy

Start with `General` and `AntiWallhack`. Most servers should leave `MovementPrediction` and `Performance` at defaults unless profiling a specific problem.

## Commands

- `css_fow_stats`: shows performance, quality, tracking, and suppressed-error counters
- `css_fow_toggle`: toggles protection on or off
- `css_fow_profile <auto|competitive|wingman|casual|deathmatch|retake|custom>`: overrides the active runtime profile

## Build

From the repository root:

```powershell
dotnet restore .\S2FOW.sln
dotnet build .\S2FOW.sln -c Release
```

Release binaries are produced in:

```text
Plugin/bin/Release/net8.0/
```

## Operational Notes

- Controller entities are intentionally left transmitted even when a hidden player pawn is removed. Hiding `CCSPlayerController` causes client-side `CopyExistingEntity` crashes; controllers do not carry reliable world-space coordinates, so this is the safe compatibility trade-off.
- `Dependencies/` is a development convenience folder. Do not ship the full source trees as release assets.
- The runtime stats command exposes suppressed catch counters so recurring entity-access failures are visible without crashing the server.

## CI

`.github/workflows/build.yml` restores, builds, and uploads a release artifact on GitHub Actions using Windows and .NET 8.

## License

The original S2FOW code in this repository is released under the MIT License. Third-party code inside `Dependencies/` keeps its own upstream licenses.
