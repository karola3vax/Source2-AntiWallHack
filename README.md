<p align="center">
  <img src="https://img.shields.io/badge/CS2-CounterStrikeSharp-blue" alt="CounterStrikeSharp" />
  <img src="https://img.shields.io/badge/RayTrace-Required-orange" alt="RayTrace Required" />
  <img src="https://img.shields.io/badge/Version-Alpha%20Release%201.0-brightgreen" alt="Version" />
  <img src="https://img.shields.io/badge/Ready_for-Live%20Servers-red" alt="Ready for Live Servers" />
</p>

# Source2-AntiWallHack

Source2-AntiWallHack is a CounterStrikeSharp plugin that reduces wallhack advantage by controlling who gets transmitted to each player. If a player is truly visible, they are shown normally. If they are behind cover and out of line-of-sight, they are hidden from network transmit for that viewer. The plugin also uses preload and grace logic to keep peeks smooth and reduce pop-in/flicker, while staying lightweight enough for busy servers.

## 🧭 How It Works (Simple)

1. The plugin checks line-of-sight between players using Ray-Trace.
2. If target is visible, target is transmitted.
3. If target is not visible, target is hidden for that viewer.
4. Small prediction and grace windows keep movement natural during peeks.

## 🚀 Features

- 🎯 LOS-based per-viewer visibility control
- ⚡ Predictive preload to reduce first-appearance pop-in
- 🛡️ Combat/death grace windows for stable visibility
- 🏃 Horizontal peek compensation for fast side peeks
- 🔫 Linked weapon sync handling when players hide/show
- 🧠 Runtime LOS/pair caching to lower trace cost
- 🧱 Optional `PlayerClip` ignore support (anti-rush invisible walls)

## ⚙️ Requirements

- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
- [Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace) (Ray-Trace-CSSharp)

## 📦 Installation

1. Download the latest plugin package from the **Releases** page.
2. Extract the release package.
3. Place the extracted `Source2-AntiWallHack` plugin folder into:
`addons/counterstrikesharp/plugins/Source2-AntiWallHack/`
4. Ensure Ray-Trace CSSharp components are installed:
`addons/counterstrikesharp/plugins/RayTraceImpl/RayTraceImpl.dll`
`addons/counterstrikesharp/shared/RayTraceApi/RayTraceApi.dll`
5. Restart server.
6. First run creates config file:

```txt
addons/counterstrikesharp/configs/plugins/Source2-AntiWallHack/Source2-AntiWallHack.json
```

## 🎮 Commands

- `css_source2awh_status`  
Shows current plugin summary.

- `css_source2awh_reset`  
Clears cache without restarting the plugin.

## 📁 Configuration (Auto-Generated)

| Key | Default | User-friendly meaning |
|---|---:|---|
| `Enabled` | `true` | Turns Source2-AntiWallHack on/off. |
| `HideTeammates` | `true` | If true, teammates are also hidden when out of LOS. |
| `IgnoreBots` | `false` | If true, bots are ignored by hide/show logic. |
| `VisibleGraceTicks` | `1` | How long to keep a player visible after you just saw them. |
| `PreloadLookaheadTicks` | `12` | How far ahead movement is predicted for early visibility. |
| `PreloadHoldTicks` | `22` | How long preload visibility is kept once triggered. |
| `PreloadVelocityScale` | `1.3` | Prediction aggressiveness multiplier. |
| `PreloadMinSpeed` | `0` | Minimum movement speed to allow preload (`0` = always). |
| `CombatGraceTicks` | `32` | Force-visible ticks after damage events. |
| `DeathGraceTicks` | `64` | Force-visible ticks around death events. |
| `MaxTraceDistance` | `4096` | Max LOS check distance. |
| `SideProbeUnits` | `24` | Side sample width for visibility checks. |
| `TargetPaddingUnits` | `30` | Expands target sampling area to reduce pop-in. |
| `HorizontalPeekBonusUnits` | `24` | Extra sample expansion during fast horizontal peeks. |
| `HorizontalPeekSpeedForMaxBonus` | `100` | Speed needed to reach max horizontal bonus. |
| `DebugTraceBeams` | `false` | Draw LOS debug beams (keep off on production). |
| `AllowLosThroughPlayerClip` | `true` | Ignores `PlayerClip` blockers in LOS checks. Useful for invisible anti-rush walls. |
| `ConfigVersion` | `1` | CounterStrikeSharp config schema version value. |

## 💡 Practical Tuning

- For less pop-in: increase `TargetPaddingUnits` first.
- Then tune `HorizontalPeekBonusUnits`.
- If needed, raise `PreloadLookaheadTicks` slightly.
- Keep `DebugTraceBeams=false` on live servers unless you wanna see rays. Crash is guaranteed.

## ℹ️ Plugin Info

- Name: `Source2-AntiWallHack`
- Author: `karola3vax` on Discord
- Version: `Alpha Release 1.0`
