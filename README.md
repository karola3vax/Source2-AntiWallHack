<p align="center">
  <img src="https://img.shields.io/badge/CS2-CounterStrikeSharp-blue" alt="CounterStrikeSharp" />
  <img src="https://img.shields.io/badge/RayTrace-Required-orange" alt="RayTrace Required" />
  <img src="https://img.shields.io/badge/Version-Alpha%20Release%201.0-brightgreen" alt="Version" />
  <img src="https://img.shields.io/badge/Validation-Staging%20Recommended-yellow" alt="Validation Status" />
</p>

# ⚠️ Warning

This project is just in Alpha phase and yet to be fully released soon. Bugs and technical issues are expected. Use it with care.

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
- 📊 Adaptive trace budget and deferred pair evaluation for high-player servers
- 🚀 Optional FPS boost transmit filters (ragdolls, dropped weapons, projectiles, effects)
- 🧱 Optional `PlayerClip` ignore support (anti-rush invisible walls)
- 🕒 Wrap-safe tick/deadline handling for long uptimes
- 🔁 RayTrace failure cooldown + automatic retry (fail-open safety)

## ⚙️ Requirements

- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
- [Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace)
- Ray-Trace MetaMod plugin (`Ray-Trace-MM`) must be installed and running (backend provider)
- Ray-Trace CounterStrikeSharp bridge must be installed (`RayTraceImpl` + `RayTraceApi`)

## 📦 Installation

1. Download the latest plugin package from the **Releases** page.
2. Extract the release package.
3. Place the extracted `Source2-AntiWallHack` plugin folder into:
`addons/counterstrikesharp/plugins/Source2-AntiWallHack/`
4. Ensure Ray-Trace MetaMod plugin (`Ray-Trace-MM`) is installed on the server.
5. Ensure Ray-Trace CSSharp bridge components are installed:
`addons/counterstrikesharp/plugins/RayTraceImpl/RayTraceImpl.dll`
`addons/counterstrikesharp/shared/RayTraceApi/RayTraceApi.dll`
6. Restart server.
7. First run creates config file:

```txt
addons/counterstrikesharp/configs/plugins/Source2-AntiWallHack/Source2-AntiWallHack.json
```

## 🎮 Commands

- `css_source2awh_status`  
Shows full runtime diagnostics summary (trace budget/calls, cache hit rates, deferred reuse, preload probe stats, raytrace cooldown/failure state, entity readiness, and per-thread CheckTransmit allocation sample).

- `css_source2awh_reset`  
Clears cache without restarting the plugin.

- `css_source2awh_tracepair <viewerSlot> <targetSlot>`  
Arms a one-tick sampled decision trace for a single viewer->target pair. It logs one decision path and auto-disables.

## 📁 Configuration (Auto-Generated)

| Key | Default | User-friendly meaning |
|---|---:|---|
| `Enabled` | `true` | Turns Source2-AntiWallHack on/off. |
| `HideTeammates` | `true` | If true, teammates are also hidden when out of LOS. |
| `TeammateInfoPulseTicks` | `12` | Sends short teammate visibility pulses every N ticks so radar/name info can refresh while teammates stay mostly hidden out of LOS (`0` = disabled). |
| `IgnoreBots` | `false` | If true, bots are ignored by hide/show logic. |
| `BotsAlsoCastRays` | `true` | If true, bot viewers that appear in `CheckTransmit` are processed by LOS logic. No extra synthetic bot pass is created. Ignored when `IgnoreBots=true`. |
| `VisibleGraceTicks` | `4` | How long to keep a player visible after you just saw them. |
| `PreloadLookaheadTicks` | `14` | How far ahead movement is predicted for early visibility. |
| `PreloadHoldTicks` | `26` | How long preload visibility is kept once triggered. |
| `PreloadVelocityScale` | `1.35` | Prediction aggressiveness multiplier. |
| `PreloadMinSpeed` | `0` | Minimum movement speed to allow preload (`0` = always). |
| `CombatGraceTicks` | `32` | Force-visible ticks after damage events. |
| `DeathGraceTicks` | `64` | Force-visible ticks around death events. |
| `MaxTraceDistance` | `6000` | Max LOS check distance. |
| `SideProbeUnits` | `26` | Side sample width for visibility checks. |
| `TargetPaddingUnits` | `34` | Expands target sampling area to reduce pop-in. |
| `HorizontalPeekBonusUnits` | `28` | Extra sample expansion during fast horizontal peeks. |
| `HorizontalPeekSpeedForMaxBonus` | `100` | Speed needed to reach max horizontal bonus. |
| `VerticalPeekBonusUnits` | `18` | Extra vertical sampling expansion during up/down peeks (ramps, stairs, elevation changes). |
| `VerticalPeekSpeedForMaxBonus` | `120` | Relative vertical speed needed to reach max vertical peek bonus. |
| `DebugTraceBeams` | `false` | Draw LOS debug beams (keep off on production). |
| `AllowLosThroughPlayerClip` | `true` | Ignores `PlayerClip` blockers in LOS checks. Useful for invisible anti-rush walls. |
| `EnableFpsBoostFilters` | `true` | Enables optional non-player entity filtering for higher client FPS. |
| `HideRagdolls` | `true` | Hides ragdolls to reduce client render load. |
| `HideDroppedWeapons` | `false` | Hides dropped weapons (not weapons currently held by players). |
| `HideSmokeEffects` | `false` | Hides smoke-related entities. Can change gameplay readability. |
| `HideInfernoEffects` | `false` | Hides inferno/molotov fire entities. Can change gameplay readability. |
| `HideGrenadeProjectiles` | `false` | Hides grenade projectile entities while in flight. |
| `HidePhysicsProps` | `false` | Hides physics prop entities (map dependent). |
| `HideChickens` | `false` | Hides chickens and related small ambient entities. |
| `ConfigVersion` | `1` | CounterStrikeSharp config schema version value. |

## 💡 Practical Tuning

- For less pop-in: increase `TargetPaddingUnits` first.
- Then tune `HorizontalPeekBonusUnits` and `VerticalPeekBonusUnits`.
- If needed, raise `PreloadLookaheadTicks` slightly.
- For extra client FPS: keep `EnableFpsBoostFilters=true`, start with `HideRagdolls` + `HideDroppedWeapons`.
- Enable smoke/inferno/projectile filters only if your server rules allow that visual reduction.
- Keep `DebugTraceBeams=false` on live servers; use it only for short diagnostics because it can add noticeable overhead.

## 🛡️ Safety Fallbacks

- If EntitySystem is not ready, antiwallhack evaluation is delayed and safely retried.
- If RayTrace capability is missing or trace calls fail, plugin fails open (transmit) to avoid hiding visible players.
- RayTrace failures trigger cooldown-and-retry instead of permanent disable.
- Tick/deadline logic is wrap-safe, so long server uptimes do not break grace windows or cache deadlines.

## 🧪 Tests

- Unit tests are in `tests/Source2-AntiWallHack.Tests/`.
- Current tests cover wrap-safe tick math (`TickUtil`) and force/preload decision math (`RuntimeMath`) used by transmit visibility logic.
- Run with:

```txt
dotnet test tests/Source2-AntiWallHack.Tests/Source2-AntiWallHack.Tests.csproj -c Release
```

## ℹ️ Plugin Info

- Name: `Source2-AntiWallHack`
- Author: `karola3vax` on Discord
- Version: `Alpha Release 1.0`


