<div align="center">

# S2AWH

### Server-side anti-wallhack for Counter-Strike 2

[![Version](https://img.shields.io/badge/VERSION-3.0.1-ec4899?style=for-the-badge&logoColor=white)](https://github.com/karola3vax/Source2-AntiWallHack/releases)
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
1. AABB Point Traces    - Multiple body-point checks against world geometry
2. Aim-Ray Proximity    - 5-ray X pattern from crosshair, reveal targets near hit points
3. Gap-Sweep Fan        - Extra angle rays to catch narrow slit visibility
4. Micro-Hull Fallback  - Small hull traces for edge-case thin angles
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

1. Download the latest S2AWH release and place the plugin file:

   ```
   \addons\counterstrikesharp\plugins\S2AWH\S2AWH.dll
   ```

2. If upgrading from an older version, delete the old config file first:

   ```
   \addons\counterstrikesharp\configs\plugins\S2AWH\S2AWH.json
   ```

3. Start the server. A new config file is generated automatically at the same path.
4. Check server console logs to confirm the plugin is initialized.

---

## Performance Tuning

Use these as starting profiles, then benchmark on your own hardware.

| Profile | `UpdateFrequencyTicks` | `RevealHoldSeconds` | `RayTracePoints` | Best For |
| :--- | :---: | :---: | :---: | :--- |
| **Competitive** | `2` | `0.30` | `10` | 5v5 / scrim |
| **Casual** | `4` | `0.40` | `8` | 10v10 community |
| **Large** | `8` | `0.50` | `6` | 20-24 players |
| **High Population** | `10` | `1.0` | `4` | 30+ players |

> [!TIP]
> The first lever for CPU is `Core.UpdateFrequencyTicks`. Higher value = fewer full visibility updates per second.

> [!TIP]
> If CPU is still high, lower `Trace.RayTracePoints` before disabling core visibility logic.

---

## Configuration Reference

<details>
<summary><b>Click to expand full settings table</b></summary>

### Core & Trace

| Key | Default | Description |
| :--- | :---: | :--- |
| `Core.Enabled` | `true` | Master on/off switch for the plugin |
| `Core.UpdateFrequencyTicks` | `10` | How many ticks to spread viewer work across (higher = lower CPU, slower updates) |
| `Trace.RayTracePoints` | `6` | Number of body sample points per target (`1..10`) |
| `Trace.UseFovCulling` | `true` | Skip expensive checks for targets outside viewer cone |
| `Trace.FovDegrees` | `220.0` | FOV cone size used by culling |
| `Trace.AimRayHitRadius` | `100.0` | Reveal radius around aim-ray hit points |
| `Trace.AimRaySpreadDegrees` | `1.0` | Angular spacing for 5-ray X aim pattern |
| `Trace.GapSweepProximity` | `72.0` | Max distance from target center for gap-sweep hit to count (`20..200`) |

### Prediction & Peek-Assist

| Key | Default | Description |
| :--- | :---: | :--- |
| `Preload.PredictorDistance` | `150.0` | Forward look-ahead distance for prediction |
| `Preload.PredictorMinSpeed` | `1.0` | Minimum speed needed before prediction starts |
| `Preload.EnableViewerPeekAssist` | `true` | Adds viewer movement prediction to reduce pop-in on peeks |
| `Preload.ViewerPredictorDistanceFactor` | `0.85` | Strength multiplier for viewer peek prediction |
| `Preload.RevealHoldSeconds` | `0.30` | Keep a target visible briefly after LOS is lost |

### AABB Scaling

| Key | Default | Description |
| :--- | :---: | :--- |
| `Aabb.HorizontalScale` | `3.0` | Base horizontal target expansion for predictor path |
| `Aabb.VerticalScale` | `2.0` | Base vertical target expansion for predictor path |
| `Aabb.EnableAdaptiveProfile` | `true` | Increase expansion at higher movement speeds |
| `Aabb.ProfileSpeedStart` | `80.0` | Speed where adaptive scaling starts |
| `Aabb.ProfileSpeedFull` | `100.0` | Speed where adaptive scaling reaches full strength |
| `Aabb.ProfileHorizontalMaxMultiplier` | `1.70` | Max horizontal multiplier at high speed |
| `Aabb.ProfileVerticalMaxMultiplier` | `1.35` | Max vertical multiplier at high speed |
| `Aabb.EnableDirectionalShift` | `true` | Shift predicted target volume toward movement direction |
| `Aabb.DirectionalForwardShiftMaxUnits` | `34.0` | Maximum forward shift in units |
| `Aabb.DirectionalPredictorShiftFactor` | `0.65` | Blend factor for directional shift |

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
| `Diagnostics.DrawDebugTraceBeams` | `false` | Draw trace beams in-game (debug only, expensive if overused) |
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
