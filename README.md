<div align="center">

# S2AWH

### Server-side anti-wallhack for Counter-Strike 2

[![Version](https://img.shields.io/badge/VERSION-3.0.2-ec4899?style=for-the-badge&logoColor=white)](https://github.com/karola3vax/Source2-AntiWallHack/releases)
[![CounterStrikeSharp](https://img.shields.io/badge/CSSHARP-v1.0.362%2B-db2777?style=for-the-badge&logoColor=white)](https://github.com/roflmuffin/CounterStrikeSharp/releases)
[![Ray-Trace](https://img.shields.io/badge/RAY--TRACE-v1.0.4-b0126f?style=for-the-badge&logoColor=white)](https://github.com/FUNPLAY-pro-CS2/Ray-Trace/releases)
[![Build](https://img.shields.io/badge/BUILD-.NET%208-ad1457?style=for-the-badge&logoColor=white)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/LICENSE-MIT-10b981?style=for-the-badge&logoColor=white)](./LICENSE)

---

**S2AWH** makes wallhacks much less useful.
It runs on the server and only sends enemy data when that enemy should really be visible.

[How It Works](#how-it-works) | [Requirements](#requirements) | [Installation](#installation) | [Performance Tuning](#performance-tuning) | [Configuration](#configuration-reference)

</div>

---

> [!IMPORTANT]
> S2AWH runs entirely on the server — enemy data is stripped before it ever reaches the client.
> No cheat software can reveal what was never sent. Wallhacks see nothing.

> [!CAUTION]
>
> ### Performance Notice
>
> S2AWH does many real-time ray traces. This is CPU-heavy.
> It can run on 30+ player servers, but only with tuned settings and enough CPU headroom.
> Read the [Performance Tuning](#performance-tuning) section before production use.

---

## How It Works

S2AWH uses a 4-stage visibility check:

```
1. AABB Surface Probes  - Face probes with near-hit radius tolerance for thin visible slivers
2. Aim-Ray Proximity    - X pattern from crosshair, reveal targets near hit points
3. Micro-Hull Fallback  - Small hull traces for edge-case thin angles
4. Predictive Preload   - Point + surface look-ahead to reduce pop-in while peeking
```

If all 4 stages fail, the target is treated as hidden and their important entity data is removed from transmit.

> [!NOTE]
> Safety behavior is fail-open on transient engine/plugin errors. In uncertain error states, S2AWH prefers transmitting instead of wrongly hiding players.

---

## Requirements

| Dependency | Version | Link |
| :--- | :--- | :--- |
| **CounterStrikeSharp** | `v1.0.362+` | [Releases](https://github.com/roflmuffin/CounterStrikeSharp/releases) |
| **MetaMod:Source** | `1387+` | [Download](https://www.sourcemm.net/downloads.php?branch=dev) |
| **Ray-Trace** | `v1.0.4` | [Releases](https://github.com/FUNPLAY-pro-CS2/Ray-Trace/releases) |

---

## Installation

1. Stop your server.
2. Install CounterStrikeSharp and MetaMod if not already installed.
3. Install the Ray-Trace build that matches your OS (Windows/Linux).

[![Ray-Trace Installation Guide](https://raw.githubusercontent.com/karola3vax/server-assets/refs/heads/main/Screenshot_2.png)](https://github.com/FUNPLAY-pro-CS2/Ray-Trace/releases)

4. Download the latest S2AWH release and place the plugin file:

   ```
   \addons\counterstrikesharp\plugins\S2AWH\S2AWH.dll
   ```

5. If upgrading from an older version, delete the old config file first:

   ```
   \addons\counterstrikesharp\configs\plugins\S2AWH\S2AWH.json
   ```

6. Start the server. If `S2AWH.example.json` is present, CounterStrikeSharp copies it to `S2AWH.json` first.
7. If no example file is present, CounterStrikeSharp generates a plain default config automatically.
8. Check server console logs to confirm the plugin is initialized.

## Development

S2AWH builds against local `CounterStrikeSharp.API.dll`, `Microsoft.Extensions.Logging.Abstractions.dll`, and `RayTraceApi.dll` references.
The project validates these local dependency paths before compilation in [`S2AWH.csproj`](./S2AWH.csproj).

Default local lookup order:

1. `..\Dependencies\CounterStrikeSharp-main\managed\CounterStrikeSharp.API\bin\Release\net8.0\CounterStrikeSharp.API.dll`
2. `..\Dependencies\CounterStrikeSharp-main\managed\CounterStrikeSharp.API\bin\Release\net8.0\Microsoft.Extensions.Logging.Abstractions.dll`
3. `..\Dependencies\Ray-Trace-main\managed\RayTrace\RayTraceApi\bin\Release\net8.0\RayTraceApi.dll`

If your workspace layout is different, override `CssApiPath`, `LoggingAbstractionsPath`, and `RayTraceApiPath` at build time.

The repo also ships a commented example config at [`configs/plugins/S2AWH/S2AWH.example.json`](./configs/plugins/S2AWH/S2AWH.example.json).
It is written in plain language so server owners can understand each setting without reading source code.

---

## Performance Tuning

Use these as starting profiles, then benchmark on your own hardware.

| Profile | `UpdateFrequencyTicks` | `RevealHoldSeconds` | `Preload.SurfaceProbeRows` | Best For |
| :--- | :---: | :---: | :---: | :--- |
| **Competitive** | `2` | `0.30` | `3` | 5v5 / scrim |
| **Casual** | `4` | `0.40` | `2` | 10v10 community |
| **Large** | `8` | `0.50` | `2` | 20-24 players |
| **High Population** | `16` | `1.0` | `1` | 30+ players |

> [!TIP]
> The first lever for CPU is `Core.UpdateFrequencyTicks`. Higher value = fewer full visibility updates per second.

> [!TIP]
> If CPU is still high, lower `Preload.SurfaceProbeRows` and `Trace.AimRayCount`.

> [!TIP]
> For 64-player servers, start with `Preload.EnablePreload = false` and increase `Core.UpdateFrequencyTicks`.

---

## Configuration Reference

<details>
<summary><b>Click to expand full settings table</b></summary>

### Core & Trace

| Key | Default | Description |
| :--- | :---: | :--- |
| `Core.Enabled` | `true` | Master on/off switch for the plugin |
| `Core.UpdateFrequencyTicks` | `16` | How many ticks to spread viewer work across (higher = lower CPU, slower updates) |
| `Trace.UseFovCulling` | `true` | Skip expensive checks for targets outside viewer cone using conservative AABB-aware culling |
| `Trace.FovDegrees` | `220.0` | FOV cone size used by culling |
| `Trace.AimRayHitRadius` | `100.0` | Reveal radius around aim-ray hit points |
| `Trace.AimRaySpreadDegrees` | `1.0` | Angular spacing for the aim-ray X pattern |
| `Trace.AimRayCount` | `1` | Number of aim rays to cast per viewer (`1..5`) |
| `Trace.AimRayMaxDistance` | `2200.0` | Skip aim-ray proximity checks for targets farther than this distance (`0` disables) |

### Prediction & Peek-Assist

| Key | Default | Description |
| :--- | :---: | :--- |
| `Preload.EnablePreload` | `true` | Master switch for the whole preload system: predictor points, surface probes, and viewer peek assist |
| `Preload.SurfaceProbeHitRadius` | `64.0` | Accepts near-hit preload probes within this radius (`0..200`) |
| `Preload.SurfaceProbeRows` | `2` | Probe rows per predictor face for preload surface probing (`1..3`, total cached probes = rows x 6) |
| `Preload.PredictorDistance` | `64.0` | Maximum forward look-ahead distance for prediction. Real lead is also capped by target speed and update interval |
| `Preload.PredictorMinSpeed` | `1.0` | Minimum speed needed before prediction starts |
| `Preload.PredictorFullSpeed` | `100.0` | Speed where preload look-ahead reaches full configured distance |
| `Preload.EnableViewerPeekAssist` | `true` | Adds viewer movement prediction to reduce pop-in on peeks |
| `Preload.ViewerPredictorDistanceFactor` | `0.85` | Strength multiplier for viewer peek prediction |
| `Preload.RevealHoldSeconds` | `0.30` | Keep a target visible briefly after LOS is lost |

> [!NOTE]
> Older configs that still contain `Preload.EnableProbePreload` or `Preload.EnableSurfacePreload` are accepted as legacy aliases, but new auto-generated configs use `Preload.EnablePreload`.

### AABB Scaling

| Key | Default | Description |
| :--- | :---: | :--- |
| `Aabb.LosHorizontalScale` | `1.0` | Horizontal expansion used by LOS AABB sampling (orange box) |
| `Aabb.LosVerticalScale` | `1.0` | Vertical expansion used by LOS AABB sampling (orange box) |
| `Aabb.PredictorHorizontalScale` | `1.0` | Maximum horizontal expansion used by preload predictor AABB at full movement speed (green/purple boxes) |
| `Aabb.PredictorVerticalScale` | `1.0` | Maximum vertical expansion used by preload predictor AABB at full movement speed (green/purple boxes) |
| `Aabb.PredictorScaleStartSpeed` | `80.0` | Speed where predictor AABB starts growing from LOS size toward predictor size |
| `Aabb.PredictorScaleFullSpeed` | `200.0` | Speed where predictor AABB reaches full configured predictor size |
| `Aabb.EnableAdaptiveProfile` | `true` | Expands predictor AABB further as target speed increases |
| `Aabb.ProfileSpeedStart` | `140.0` | Speed where extra adaptive predictor growth begins |
| `Aabb.ProfileSpeedFull` | `260.0` | Speed where extra adaptive predictor growth reaches full multiplier |
| `Aabb.ProfileHorizontalMaxMultiplier` | `1.70` | Max horizontal multiplier applied by adaptive predictor profile |
| `Aabb.ProfileVerticalMaxMultiplier` | `1.35` | Max vertical multiplier applied by adaptive predictor profile |
| `Aabb.EnableDirectionalShift` | `true` | Shifts the current predictor AABB forward along movement direction without double-shifting the future predicted box |
| `Aabb.DirectionalForwardShiftMaxUnits` | `34.0` | Maximum forward shift used by predictor AABB |
| `Aabb.DirectionalPredictorShiftFactor` | `0.65` | Global multiplier for directional predictor shifting |
| `Aabb.LosSurfaceProbeHitRadius` | `64.0` | If a surface probe is blocked but ends within this radius of the probe point, count as visible |
| `Aabb.LosSurfaceProbeRows` | `2` | Probe rows per LOS face for surface probing (`1..3`, total cached probes = rows x 6) |
| `Aabb.MicroHullMaxDistance` | `2000.0` | Skip LOS micro-hull fallback when target is farther than this distance (`0` disables) |

### Visibility Logic

| Key | Default | Description |
| :--- | :---: | :--- |
| `Visibility.IncludeTeammates` | `false` | If `false`, teammates are always transmitted (no teammate LOS checks) |
| `Visibility.IncludeBots` | `true` | If `false`, bot targets are always transmitted |
| `Visibility.BotsDoLOS` | `true` | If `false`, bot viewers do not run LOS filtering |

### Diagnostics

| Key | Default | Description |
| :--- | :---: | :--- |
| `Diagnostics.ShowDebugInfo` | `true` | Enables periodic debug summary logs |
| `Diagnostics.DrawDebugTraceBeams` | `false` | Draw trace beams in-game (debug only, expensive if overused; S2AWH also caps debug beam spawns per tick) |
| `Diagnostics.DrawDebugAabbBoxes` | `false` | Draw LOS/predictor AABB wireframe boxes in-game (debug only, very expensive; uses the same per-tick debug beam cap) |
| `Diagnostics.DrawDebugTraceBeamsForHumans` | `true` | Beam drawing toggle for human viewers |
| `Diagnostics.DrawDebugTraceBeamsForBots` | `true` | Beam drawing toggle for bot viewers |

</details>

---

## Credits

- **[karola3vax](https://github.com/karola3vax)** - Lead author
- **[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp/releases)** by **[roflmuffin](https://github.com/roflmuffin)**
- **[MetaMod:Source](https://www.metamodsource.net/downloads.php?branch=dev)** by **[AlliedModders](https://github.com/alliedmodders)**
- **[Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace/releases)** by **[SlynxCZ](https://github.com/SlynxCZ)**

---

## License

Distributed under the **MIT License**. See [`LICENSE`](./LICENSE) for details.

<div align="center">
  <br>
  <i>S2AWH - Server-side visibility filtering for fair play.</i>
  <br><br>
</div>
