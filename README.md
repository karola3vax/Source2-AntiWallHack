<div align="center">

# 💎 S2AWH

### Server-Sided Anti-Wallhack for Counter-Strike 2

[![Version](https://img.shields.io/badge/VERSION-3.0.1-ec4899?style=for-the-badge&logoColor=white)](https://github.com/karola3vax/Source2-AntiWallHack/releases)
[![CounterStrikeSharp](https://img.shields.io/badge/CSSHARP-v1.0.362%2B-db2777?style=for-the-badge&logoColor=white)](https://github.com/roflmuffin/CounterStrikeSharp/releases)
[![Ray-Trace](https://img.shields.io/badge/RAY--TRACE-v1.0.4-b0126f?style=for-the-badge&logoColor=white)](https://github.com/FUNPLAY-pro-CS2/Ray-Trace/releases)
[![Build](https://img.shields.io/badge/BUILD-.NET%208-ad1457?style=for-the-badge&logoColor=white)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/LICENSE-MIT-10b981?style=for-the-badge&logoColor=white)](./LICENSE)

---

**S2AWH** neutralizes the effectiveness of wallhacks by up to **80%**.
It validates player visibility in real-time on the server and withholds enemy data until they are truly visible — making wallhacks see nothing but air.

[How It Works](#-how-it-works) • [Requirements](#-requirements) • [Installation](#-installation) • [Performance Tuning](#-performance-tuning) • [Configuration](#-configuration-reference)

</div>

---

> [!CAUTION]
>
> ### ⚠️ Performance Notice
>
> S2AWH performs **real-time ray-tracing** on every server tick. This is computationally expensive.
> **Using S2AWH on servers with 20+ players is NOT recommended** — it will cause noticeable server frame-time degradation.
> Please review the [Performance Tuning](#-performance-tuning) section before deploying.

---

## 🧠 How It Works

S2AWH uses a **4-stage visibility cascade** powered by real-time ray-tracing to determine whether each player should receive another player's data:

```
1. AABB Point Traces    — 10-point body sampling against world geometry
2. Gap-Sweep Fan        — 8 angular rays to catch narrow slit visibility
3. Aim-Ray AABB Probe   — Scope/crosshair-aligned slab intersection check
4. Micro-Hull Fallback  — Small hull traces for the thinnest angles
```

If **none** of the stages detect visibility → **enemy data is withheld entirely**.
The client never receives the position, model, or weapon data of hidden players.

> [!NOTE]
> Unlike client-sided anti-cheats, S2AWH operates entirely on the server. There is nothing for cheat software to bypass on the client because the data simply never arrives.

---

## ✅ Requirements

| Dependency | Version | Link |
| :--- | :--- | :--- |
| **CounterStrikeSharp** | `v1.0.362+` | [Releases](https://github.com/roflmuffin/CounterStrikeSharp/releases) |
| **MetaMod:Source** | `1387+` | [Download](https://www.sourcemm.net/downloads.php?branch=dev) |
| **Ray-Trace** | `v1.0.4` | [Releases](https://github.com/FUNPLAY-pro-CS2/Ray-Trace/releases) |

---

## 🔧 Installation

1. **Stop** your server.
2. Install **CounterStrikeSharp** and **MetaMod** if not already installed.
3. Install the **Ray-Trace** build matching your OS (Windows or Linux).

[![Ray-Trace Installation Guide](https://raw.githubusercontent.com/karola3vax/server-assets/refs/heads/main/Screenshot_2.png)](https://github.com/FUNPLAY-pro-CS2/Ray-Trace/releases)

1. Download the latest release from the **[Releases](https://github.com/karola3vax/Source2-AntiWallHack/releases)** page and extract its contents:

   ```
   \addons\counterstrikesharp\plugins\S2AWH\S2AWH.dll
   ```

2. If upgrading from a previous version, **delete the old `S2AWH.json`** config file.
3. Start the server. A fresh config will generate automatically at:

   ```
   \addons\counterstrikesharp\configs\plugins\S2AWH\S2AWH.json
   ```

4. Check the server console for S2AWH startup logs to confirm everything is running.

---

## ⚙️ Performance Tuning

Use the following profiles to match your server's player capacity:

| Profile | `UpdateFrequencyTicks` | `RevealHoldSeconds` | Best For |
| :--- | :---: | :---: | :--- |
| **Competitive** | `2` | `0.3` | 5v5 matches, pro servers |
| **Casual** | `4` | `0.4` | 10v10 casual & community servers |
| **Large** | `10` | `0.5` | 20+ player DM / public servers |

> [!TIP]
> `UpdateFrequencyTicks` controls the stagger window — higher values spread ray-trace work across more ticks, dramatically reducing per-frame CPU cost at the expense of slightly delayed visibility updates.

---

## 📖 Configuration Reference

<details>
<summary><b>Click to expand full settings table</b></summary>

### 🛰️ Core & Trace

| Key | Default | Description |
| :--- | :---: | :--- |
| `Core.Enabled` | `true` | Global plugin toggle |
| `Core.UpdateFrequencyTicks` | `10` | Stagger window size (higher = smoother, slower updates) |
| `Trace.RayTracePoints` | `10` | Sample points per target AABB (1–10, lower = faster) |
| `Trace.UseFovCulling` | `true` | Skip checks for targets outside the view cone |
| `Trace.FovDegrees` | `200.0` | Total horizontal FOV for culling |
| `Trace.AimRayHitRadius` | `100.0` | Radius (units) around aim-ray hit point to reveal nearby targets |
| `Trace.AimRaySpreadDegrees` | `1.0` | Angular spread (degrees) for the 5-ray X pattern around crosshair |

### 🚀 Prediction & Peek-Assist

| Key | Default | Description |
| :--- | :---: | :--- |
| `Preload.PredictorDistance` | `150.0` | Forward prediction distance (units) |
| `Preload.PredictorMinSpeed` | `1.0` | Minimum speed to activate prediction |
| `Preload.EnableViewerPeekAssist` | `true` | Predict viewer's own movement for smoother peeks |
| `Preload.ViewerPredictorDistanceFactor` | `0.85` | Multiplier for viewer prediction strength |
| `Preload.RevealHoldSeconds` | `0.3` | Time (s) to keep targets visible after LOS loss |

### 📦 AABB Scaling

| Key | Default | Description |
| :--- | :---: | :--- |
| `Aabb.HorizontalScale` | `3.0` | Base horizontal expansion of collision bounds |
| `Aabb.VerticalScale` | `2.0` | Base vertical expansion of collision bounds |
| `Aabb.EnableAdaptiveProfile` | `true` | Dynamically expand bounds at higher speeds |
| `Aabb.ProfileSpeedStart` | `40.0` | Speed where adaptive expansion begins |
| `Aabb.ProfileSpeedFull` | `260.0` | Speed where maximum expansion is reached |
| `Aabb.ProfileHorizontalMaxMultiplier` | `1.7` | Maximum horizontal scale limit |
| `Aabb.ProfileVerticalMaxMultiplier` | `1.35` | Maximum vertical scale limit |
| `Aabb.EnableDirectionalShift` | `true` | Shift bounds toward movement direction |
| `Aabb.DirectionalForwardShiftMaxUnits` | `34.0` | Maximum directional shift distance (units) |
| `Aabb.DirectionalPredictorShiftFactor` | `0.65` | Blend factor for movement-based shift |

### 👥 Visibility Logic

| Key | Default | Description |
| :--- | :---: | :--- |
| `Visibility.IncludeTeammates` | `true` | Run visibility checks for teammates |
| `Visibility.IncludeBots` | `true` | Run visibility checks for bot targets |
| `Visibility.BotsDoLOS` | `true` | Run LOS evaluations for bot viewers |

### 🩺 Diagnostics

| Key | Default | Description |
| :--- | :---: | :--- |
| `Diagnostics.ShowDebugInfo` | `true` | Enable detailed console output |
| `Diagnostics.DrawDebugTraceBeams` | `false` | Visualize rays in-game (debug only) |
| `Diagnostics.DrawDebugTraceBeamsForHumans` | `true` | Draw beams for human player traces |
| `Diagnostics.DrawDebugTraceBeamsForBots` | `true` | Draw beams for bot player traces |

</details>

---

## 🤝 Credits

- **[karola3vax](https://github.com/karola3vax)** — Lead Author
- **[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp/releases)** by **[roflmuffin](https://github.com/roflmuffin)**
- **[MetaMod:Source](https://www.metamodsource.net/downloads.php?branch=dev)** by **[AlliedModders](https://github.com/alliedmodders)**
- **[Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace/releases)** by **[SlynxCZ](https://github.com/SlynxCZ)**

---

## ⚖️ License

Distributed under the **MIT License**. See [`LICENSE`](./LICENSE) for details.

<div align="center">
  <br>
  <i>S2AWH — Because skill should be the only advantage.</i>
  <br><br>
</div>
