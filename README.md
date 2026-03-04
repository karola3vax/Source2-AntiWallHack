<div align="center">

# S2AWH

### Server-side anti-wallhack for Counter-Strike 2

[![Version](https://img.shields.io/badge/VERSION-3.0.2-ec4899?style=for-the-badge&logoColor=white)](https://github.com/karola3vax/Source2-AntiWallHack/releases)
[![CounterStrikeSharp](https://img.shields.io/badge/CSSHARP-v1.0.362%2B-db2777?style=for-the-badge&logoColor=white)](https://github.com/roflmuffin/CounterStrikeSharp/releases)
[![Ray-Trace](https://img.shields.io/badge/RAY--TRACE-v1.0.4-b0126f?style=for-the-badge&logoColor=white)](https://github.com/FUNPLAY-pro-CS2/Ray-Trace/releases)
[![License](https://img.shields.io/badge/LICENSE-MIT-10b981?style=for-the-badge&logoColor=white)](./LICENSE)

---

**S2AWH** stops wallhacks at the source.
It runs on the server and hides enemy data that shouldn't be visible — before it ever reaches the client.

</div>

---

> [!IMPORTANT]
> Enemy data is stripped **server-side**. No cheat can reveal what was never sent.

## How It Works

Every tick, S2AWH checks if each enemy is actually visible to each player using a multi-stage pipeline:

| Stage | What It Does |
|:---:|:---|
| **1** | Surface ray probes — fast closest-point checks |
| **2** | Micro-hull traces — catches thin slits, limbs, edge peeks |
| **3** | Aim-ray proximity — checks near the player's crosshair |
| **4** | Predictive preload — look-ahead to prevent pop-in while peeking |

If all stages say "hidden", the enemy's entities are removed from transmit. The cheat sees nothing.

---

## Quick Start

### Requirements

| Dependency | Min Version |
|:---|:---:|
| [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp/releases) | `v1.0.362+` |
| [MetaMod:Source](https://www.sourcemm.net/downloads.php?branch=dev) | `1387+` |
| [Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace/releases) | `v1.0.4` |

### Install

1. Install **CounterStrikeSharp** + **MetaMod** + **Ray-Trace** (match your OS).
2. Drop `S2AWH.dll` into:

   ```
   addons/counterstrikesharp/plugins/S2AWH/
   ```

3. Start your server. A default config is auto-generated.
4. Check console for `[S2AWH]` — you're live.

> [!TIP]
> Upgrading? Delete `S2AWH.json` first to get fresh defaults.

---

## Performance Profiles

| Profile | `UpdateFrequencyTicks` | `RevealHoldSeconds` | Best For |
|:---|:---:|:---:|:---|
| **Competitive** | `2` | `0.30` | 5v5 scrims |
| **Casual** | `4` | `0.40` | 10v10 community |
| **Large** | `8` | `0.50` | 20-24 players |
| **High Pop** | `16` | `1.0` | 30+ players |

> [!CAUTION]
> S2AWH is CPU-intensive. Higher `UpdateFrequencyTicks` = lower CPU cost. Start high, tune down.

---

<details>
<summary><b>⚙️ Full Configuration Reference</b></summary>

### Core

| Key | Default | What It Does |
|:---|:---:|:---|
| `Core.Enabled` | `true` | Master switch |
| `Core.UpdateFrequencyTicks` | `16` | Spread work across N ticks (higher = less CPU) |

### Trace

| Key | Default | What It Does |
|:---|:---:|:---|
| `Trace.UseFovCulling` | `true` | Skip targets outside the viewer's cone |
| `Trace.FovDegrees` | `240.0` | FOV cone width |
| `Trace.AimRayHitRadius` | `100.0` | Reveal radius around aim-ray hits |
| `Trace.AimRayCount` | `1` | Aim rays per viewer (1–5) |
| `Trace.AimRayMaxDistance` | `2200.0` | Max aim-ray range (0 = off) |

### Prediction

| Key | Default | What It Does |
|:---|:---:|:---|
| `Preload.EnablePreload` | `true` | Master preload switch |
| `Preload.PredictorDistance` | `160.0` | Look-ahead distance |
| `Preload.EnabledForPeekers` | `true` | Viewer peek prediction |
| `Preload.EnabledForHolders` | `false` | Target movement prediction |
| `Preload.RevealHoldSeconds` | `0.10` | Keep visible briefly after LOS lost |
| `Preload.SurfaceProbeRows` | `1` | Preload probe density (1–3) |

### AABB

| Key | Default | What It Does |
|:---|:---:|:---|
| `Aabb.LosHorizontalScale` | `1.0` | LOS box width multiplier |
| `Aabb.LosVerticalScale` | `1.0` | LOS box height multiplier |
| `Aabb.LosSurfaceProbeRows` | `1` | LOS probe density (1–3) |
| `Aabb.MicroHullMaxDistance` | `2000.0` | Max range for micro-hull fallback |
| `Aabb.EnableAdaptiveProfile` | `true` | Grow predictor box with speed |

### Visibility

| Key | Default | What It Does |
|:---|:---:|:---|
| `Visibility.IncludeTeammates` | `false` | Run LOS on teammates too |
| `Visibility.IncludeBots` | `true` | Hide bots from wallhacks |
| `Visibility.BotsDoLOS` | `true` | Bots run visibility checks |

### Diagnostics

| Key | Default | What It Does |
|:---|:---:|:---|
| `Diagnostics.ShowDebugInfo` | `true` | Periodic summary in console |
| `Diagnostics.DrawDebugTraceBeams` | `false` | Draw trace beams in-game |
| `Diagnostics.DrawDebugAabbBoxes` | `false` | Draw AABB wireframes |
| `Diagnostics.DrawAmountOfRayNumber` | `false` | HUD ray counter overlay |

</details>

---

## Development

```powershell
# Build
dotnet build S2AWH/S2AWH.csproj

# Package release
powershell -ExecutionPolicy Bypass -File .\scripts\package-release.ps1
```

Local dependency paths are auto-resolved. Override with `-p:CssApiPath=...` if needed.

---

## Credits

- **[karola3vax](https://github.com/karola3vax)** — Author
- **[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)** by [roflmuffin](https://github.com/roflmuffin)
- **[Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace)** by [SlynxCZ](https://github.com/SlynxCZ)
- **[MetaMod:Source](https://www.metamodsource.net/)** by [AlliedModders](https://github.com/alliedmodders)

## License

MIT — see [`LICENSE`](./LICENSE).

<div align="center">
  <br>
  <i>S2AWH — server-side visibility filtering for fair play.</i>
  <br><br>
</div>
