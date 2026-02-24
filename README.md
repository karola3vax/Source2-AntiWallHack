<div align="center">

# 💎 S2AWH

## High-Performance Server-Authoritative Anti-Wallhack for CS2

[![Version](https://img.shields.io/badge/VERSION-3.0.0-ec4899?style=for-the-badge&logoColor=white)](https://github.com/karola3vax/Source2-AntiWallHack)
[![CounterStrikeSharp](https://img.shields.io/badge/CSSHARP-v1.0.362%2B-db2777?style=for-the-badge&logoColor=white)](https://github.com/roflmuffin/CounterStrikeSharp)
[![Ray-Trace](https://img.shields.io/badge/RAY--TRACE-v1.0.4-b0126f?style=for-the-badge&logoColor=white)](https://github.com/FUNPLAY-pro-CS2/Ray-Trace)
[![Build](https://img.shields.io/badge/BUILD-.NET%208-ad1457?style=for-the-badge&logoColor=white)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/LICENSE-MIT-10b981?style=for-the-badge&logoColor=white)](./LICENSE)

---

**S2AWH** neutralizes the effectiveness of wallhacks by up to **80%**. It achieves this by intelligently validating player visibility right on the server and withholding enemy data until they are truly visible to you.

[Requirements](#-requirements) • [Installation](#-installation) • [Performance Tuning](#-performance-tuning) • [Configuration Reference](#-configuration-reference)

</div>

---

> [!CAUTION]
> **Performance Impact:** Due to the intensive nature of real-time Ray-Tracing, S2AWH is performance-heavy. Servers with **20+ players** may experience noticeable "slow server frame" issues. Please refer to the [Performance Tuning](#-performance-tuning) section to optimize your settings.

## ✅ Requirements

- **[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)** `v1.0.362+`
- **[MetaMod:Source](https://www.sourcemm.net/downloads.php?branch=dev)** `1387+`
- **[Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace)** `v1.0.4` (Manual install required)

---

## 🔧 Installation

1. Stop your server.
2. Install **CounterStrikeSharp** and **MetaMod**.
3. Install the **Ray-Trace** version matching your OS (Windows or Linux).
   - *Note: Ray-Trace is mandatory for S2AWH to function.*

![Ray-Trace Installation Guide](https://raw.githubusercontent.com/karola3vax/server-assets/refs/heads/main/Screenshot_1.png)

4. Download the latest release from **Releases** and extract its contents. It should look like:
   `\addons\counterstrikesharp\plugins\S2AWH\S2AWH.dll`
5. Start the server. The config will generate automatically in
   `\addons\counterstrikesharp\configs\plugins\S2AWH\S2AWH.json`

---

## ⚙️ Performance Tuning

S2AWH is highly tunable. Use the following profiles to match your server capacity:

| Profile | `UpdateFrequencyTicks` | `RevealHoldSeconds` | Use Case |
| :--- | :---: | :---: | :--- |
| **Ultra-Fast** | `2` | `0.3` | 5v5 Competitive / Pro Matches |
| **Balanced** | `4` | `0.4` | 10v10 Casual / Community Servers |
| **Massive** | `10` | `0.5` | 32+ Player Deathmatch / Servers |

> [!TIP]
> Increasing `UpdateFrequencyTicks` spreads the work over a larger "stagger window," yielding much smoother server frame times on high-player-count servers.

---

## 📖 Configuration Reference

<details>
<summary><b>Click to View Full Settings Table</b></summary>

### 🛰️ Core & Trace

| Key | Default | Description |
| :--- | :--- | :--- |
| `Core.Enabled` | `true` | Global plugin toggle. |
| `Core.UpdateFrequencyTicks` | `10` | Size of the staggered work window. |
| `Trace.RayTracePoints` | `10` | Samples per AABB. (1-10, lower = better performance). |
| `Trace.UseFovCulling` | `true` | Skip checks for targets outside view cone. |
| `Trace.FovDegrees` | `200.0` | Total horizontal FOV check range. |

### 🚀 Prediction (Peek-Assist)

| Key | Default | Description |
| :--- | :--- | :--- |
| `Preload.PredictorDistance` | `150.0` | Forward prediction distance in units. |
| `Preload.PredictorMinSpeed` | `1.0` | Speed threshold to enable prediction. |
| `Preload.EnableViewerPeekAssist` | `true` | Predicts own movement for smoother peeks. |
| `Preload.ViewerPredictorDistanceFactor` | `0.85` | Multiplier for own-prediction strength. |
| `Preload.RevealHoldSeconds` | `0.3` | Time (s) to keep players visible after hiding. |

### 📦 AABB Scaling (Advanced)

| Key | Default | Description |
| :--- | :--- | :--- |
| `Aabb.HorizontalScale` | `3.0` | Base horizontal expansion of bounds. |
| `Aabb.VerticalScale` | `2.0` | Base vertical expansion of bounds. |
| `Aabb.EnableAdaptiveProfile` | `true` | Dynamically expands bounds at high speeds. |
| `Aabb.ProfileSpeedStart` | `40.0` | Speed where adaptive expansion starts. |
| `Aabb.ProfileSpeedFull` | `260.0` | Speed where max expansion is reached. |
| `Aabb.ProfileHorizontalMaxMultiplier` | `1.7` | Max horizontal scale limit. |
| `Aabb.ProfileVerticalMaxMultiplier` | `1.35` | Max vertical scale limit. |
| `Aabb.EnableDirectionalShift` | `true` | Shifts bounds towards movement direction. |
| `Aabb.DirectionalForwardShiftMaxUnits` | `34.0` | Max shift distance in units. |
| `Aabb.DirectionalPredictorShiftFactor` | `0.65` | Blend factor for movement-based shift. |

### 👥 Visibility Logic

| Key | Default | Description |
| :--- | :--- | :--- |
| `Visibility.IncludeTeammates` | `true` | Run visibility checks for teammates. |
| `Visibility.IncludeBots` | `true` | Run visibility checks for bot targets. |
| `Visibility.BotsDoLOS` | `true` | Run LOS logic for bot viewers (stress testing). |

### 🩺 Diagnostics

| Key | Default | Description |
| :--- | :--- | :--- |
| `Diagnostics.ShowDebugInfo` | `true` | Enable console log output. |
| `Diagnostics.DrawDebugTraceBeams` | `false` | Enable visual ray drawing (Testing only). |
| `Diagnostics.DrawDebugTraceBeamsForHumans` | `true` | Draw rays for human players. |
| `Diagnostics.DrawDebugTraceBeamsForBots` | `true` | Draw rays for bot players. |

</details>

---

## 🤝 Credits

- **[karola3vax](https://github.com/karola3vax)** - Lead Author
- **[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)** by **[roflmuffin](https://github.com/roflmuffin)**
- **[MetaMod:Source](https://www.metamodsource.net/downloads.php?branch=dev)** by **[AlliedModders](https://github.com/alliedmodders)**
- **[Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace)** by **[SlynxCZ](https://github.com/SlynxCZ)**

---

## ⚖️ License

Distributed under the **MIT License**. See `LICENSE` for more information.

<div align="center">
  <p><i>S2AWH: Because skill should be the only advantage.</i></p>
</div>
