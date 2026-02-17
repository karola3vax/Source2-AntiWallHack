# S2AW — Source 2 Anti-Wallhack

<div align="center">

![Counter-Strike 2](https://img.shields.io/badge/Counter--Strike%202-Released-orange?style=for-the-badge&logo=counter-strike)
![Platform](https://img.shields.io/badge/Platform-Windows-blue?style=for-the-badge&logo=windows)
![License](https://img.shields.io/badge/License-MIT-green?style=for-the-badge)

**High-performance server-side visibility culling for CS2.**  
*Blocks wallhacks by removing enemy data from the network stream before it reaches the client.*

[Installation](#-installation) • [Configuration](#-configuration) • [How It Works](#-how-it-works) • [Support](#-troubleshooting)

</div>

---

## ✨ Features

- **True Anti-Wallhack:** Enemies behind walls are **physically removed** from the packet. Cheats cannot see what isn't there.
- **Fail-Open Safety:** Defaults to "Visible" on any error or budget overflow. Legit gameplay is never compromised.
- **Smart Performance:**
  - 🧠 **Caches** visibility results to skip redundant checks.
  - 📉 **Load-Shedding** automatically reduces range/precision when server FPS drops or player count spikes.
  - 🏎️ **Optimized** for 64-player servers (adaptive batching).
- **Peek Compensation:** Special logic to prevent "pop-in" when enemies peek corners.

---

## 📦 Installation

### 1. Requirements

Ensure you have the following installed on your **Windows** server:

| Component | Link |
| :--- | :--- |
| **Metamod:Source** (Dev) | [Download (Dev Branch)](https://www.sourcemm.net/downloads.php?branch=dev) |
| **CounterStrikeSharp** | [Download (GitHub)](https://github.com/roflmuffin/CounterStrikeSharp/releases) |
| **Ray-Trace** | [Download (GitHub)](https://github.com/FUNPLAY-pro-CS2/Ray-Trace) *(Included in S2AW release)* |

### 2. File Structure

Drag and drop the `addons` folder into your `game/csgo/` directory.

```text
csgo/addons/
├── metamod/
│   └── RayTrace.vdf                 <-- Metamod loader
├── RayTrace/
│   ├── bin/win64/RayTrace.dll       <-- Native ray-tracing module
│   └── gamedata.json                <-- Offsets
└── counterstrikesharp/
    ├── plugins/
    │   ├── RayTraceImpl/
    │   │   └── RayTraceImpl.dll     <-- C# Bridge
    │   └── S2AW/
    │       └── S2AW.dll             <-- The Plugin
    └── shared/
        └── RayTraceApi/
            └── RayTraceApi.dll      <-- Shared Interface
```

### 3. Verification

Restart your server and check the console. You should see:
> `[RayTrace] Ray-Trace capability connected.`

Type `css_s2aw_status` in the server console to verify S2AW is running.

---

## 🏗️ How It Works (The Logic Flow)

Every tick, S2AW decides if Player A can see Player B. The decision happens in this **exact order**:

### 1. 🏁 Round Start (Global Sync)
>
> **Is the round just starting?**  
> If YES (`round_start_fail_open_ms`), everyone is **VISIBLE**.
> *Why? Ensures all player and weapon data is fully synced to clients before we start hiding things.*

### 2. 🤝 Game Rules
>
> **Are they teammates?**  
> If YES (`hide_teammates` = false), they are **VISIBLE**.

### 3. 🧠 Smart Caching (Zero Cost)
>
> **Did we check this recently?**  
> If YES, use the cached result (Visible/Hidden) without running a new trace.

### 4. 📐 Spatial Gates (Cheap Checks)
>
> **Is the target too far?** (`max_distance`) → **VISIBLE** (Safety).  
> **Is the target behind us?** (`enforce_fov_check`) → **HIDDEN**.

### 5. 🔦 Ray-Trace (Expensive)
>
> **Is there a wall in the way?**  
> We trace a line from Viewer Eye → Target Body.
>
> - **BLOCKED:** Player is **HIDDEN**.
> - **CLEAR:** Player is **VISIBLE**.
>   - *Transition Logic:*
>     - **Hidden ➔ Visible:** We hold them visible for `reveal_sync_ticks` (e.g. 12 ticks) to let their weapon model sync.
>     - **Visible ➔ Visible:** We refresh the `visibility_grace_ticks` (e.g. 4 ticks) to prevent flickering if they briefly go behind a thin object.

---

## ⚙️ Configuration

The config file is generated automatically at:  
`addons/counterstrikesharp/configs/plugins/S2AW/S2AW.json`

### 🔹 Essentials

| Setting | Default | Description |
| :--- | :--- | :--- |
| `enabled` | `true` | Master switch for the plugin. |
| `hide_teammates` | `true` | Set `false` for Casual/DM servers where teammates should see each other. |
| `max_distance` | `5000` | Max distance (units) to process. Enemies further away are always visible. |
| `max_traces_per_tick` | `3500` | **Performance Cap:** Higher = more accuracy, Lower = less CPU usage. |
| `peek_eye_offset` | `28.0` | **Anti-Pop-in:** Checks visibility from a "virtual shoulder" to catch peeks early. |

### 🔹 Tuning & Performance

| Setting | Default | Description |
| :--- | :--- | :--- |
| `tick_divider` | `1` | Run checks every N ticks. Set to `2` on weak CPU servers. |
| `max_viewers_per_tick` | `64` | Max players processed per tick. |
| `expanded_box_scale_xy` | `3.0` | **Safety Margin:** Makes the target hitbox wider effectively "shrinking" walls. |
| `visibility_grace_ticks` | `4` | **Anti-Flicker:** Keeps a player visible for X ticks after losing LOS. |
| `reveal_sync_ticks` | `12` | **Weapon Sync:** Keeps a newly revealed player visible longer so their weapon doesn't float. |
| `round_start_fail_open_ms`| `500` | **Warmup:** Time (ms) after round start where everyone is visible. |

---

## 📊 Commands

| Command | Usage | Description |
| :--- | :--- | :--- |
| `css_s2aw_status` | Console | Shows current status, config summary, and active player counts. |
| `css_s2aw_stats` | Console | Displays real-time performance metrics (Trace usage, CPU load). |
| `css_s2aw_stats_reset`| Console | Resets the statistics history buffer. |

---

## 🔧 Troubleshooting

#### ❌ "Ray-Trace capability unavailable"
>
> **Cause:** The native module isn't loaded.  
> **Fix:** Check that `RayTrace.dll` is in the correct folder and `RayTrace.vdf` is in `addons/metamod`. Restart server.

#### ⚠️ "Trace budget reached this tick"
>
> **Cause:** Server is very busy (many players looking at many enemies).  
> **Fix:** Normal behavior. S2AW "fails open" (shows players) when budget is hit to preserve FPS. If frequent, increase `max_traces_per_tick` or reduce `max_distance`.

#### 👻 Enemies "pop in" when peeking
>
> **Fix:** Increase `peek_eye_offset` (try 40.0) or `expanded_box_scale_xy`.

---

## 📈 Performance Scaling

S2AW adapts to server load dynamically:

| Player Count | Mode | Effect |
| :--- | :--- | :--- |
| **< 22** | **Full** | Max precision, full distance checks. |
| **22–29** | **Eco** | Reduces max distance by 15%, trace budget by 25%. |
| **30+** | **Turbo** | Aggressive culling, reduced distance, focuses on near enemies. |

*Designed for high-performance 128-tick simulations.*
