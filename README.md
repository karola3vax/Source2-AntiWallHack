# 🛡️ S2AW — Source 2 Anti-Wallhack

A server-side anti-wallhack plugin for Counter-Strike 2, powered by CounterStrikeSharp and Ray-Trace.

> ⚠️ **Windows only** — S2AW and its Ray-Trace dependency currently support Windows dedicated servers only. Linux is not supported.

---

## 💡 What Does It Do?

Wallhack cheats work by reading enemy positions from the game's network data. S2AW stops this at the source: if a player **can't physically see** an enemy through walls or obstacles, their entity data is **never sent** over the network. The cheat has nothing to read.

Every server tick, S2AW:

1. Builds an **expanded bounding box** around each player (larger than their actual model to account for peeking).
2. Casts multiple **ray traces** from each viewer's eye to sample points on those boxes.
3. If **no ray reaches** the target → the target's pawn is **removed from the network snapshot** for that viewer.
4. A short **grace period** keeps players visible briefly after breaking line-of-sight, preventing jarring pop-in.

## ✨ Key Features

- 🔍 **Expanded AABB sampling** — Multi-point traces against scaled bounding boxes prevent pop-in when players peek corners
- ⚡ **Budgeted ray tracing** — Per-tick trace limit keeps server performance stable, even with many players
- 🔄 **Priority viewer system** — Players who are actively moving or turning their camera are processed first
- 🕐 **Visibility grace period** — Seconds of grace after losing sight prevent flicker at cover transitions
- 🛡️ **Fail-open design** — If the system runs out of budget or the backend is unavailable, all players remain visible (no false concealment, no gameplay disruption)
- 🤖 **Bot support** — Choose whether bots are tracked as targets, viewers, or both

---

## 📋 Prerequisites

You need these installed on your Windows dedicated server **before** adding S2AW:

| Dependency | Where to get it |
| --- | --- |
| **Metamod:Source** | [Download (Dev Build)](https://www.metamodsource.net/downloads.php/?branch=master) |
| **CounterStrikeSharp** | [GitHub releases](https://github.com/roflmuffin/CounterStrikeSharp) |

## 📦 Installation

The release package includes **S2AW + all Ray-Trace files** — nothing else to download.

1. **Download** the latest S2AW release.
2. **Drag the entire `addons/` folder** into your server's `game/csgo/` directory. If prompted, merge with the existing `addons/` folder.
3. **Restart** the server.

Done! The folder structure you're installing looks like this:

```text
addons/
├─ metamod/
│  └─ RayTrace.vdf              ← Registers RayTrace with Metamod
├─ RayTrace/
│  ├─ bin/win64/RayTrace.dll    ← Native ray-trace engine
│  └─ gamedata.json             ← Engine offset data
└─ counterstrikesharp/
   ├─ plugins/
   │  ├─ S2AW/                  ← The anti-wallhack plugin
   │  │  ├─ S2AW.dll
   │  │  └─ S2AW.deps.json
   │  └─ RayTraceImpl/          ← Managed bridge to the native engine
   │     ├─ RayTraceImpl.dll
   │     └─ (dependencies)
   └─ shared/
      └─ RayTraceApi/           ← Shared API used by both plugins
         └─ RayTraceApi.dll
```

### ✅ Verifying It Works

After the server starts, open the server console and run:

```text
css_s2aw_status
```

You should see a report showing `enabled=True` and `raytrace_ready=True`. If `raytrace_ready` is `False`, the Ray-Trace files may not be installed correctly.

---

## 🎮 Console Commands

All commands can be run from the **server console** or by an admin **in-game** (requires appropriate permissions):

| Command | What it does |
| --- | --- |
| `css_s2aw_status` | Full diagnostic report — shows whether the plugin is active, Ray-Trace is connected, current config values, how many players are being tracked, and the bounds cache size |
| `css_s2aw_stats` | Performance dashboard — averaged over recent ticks, shows ray traces per tick, viewers processed per tick, how often the budget ran out, and how many viewers had to fail-open |
| `css_s2aw_stats_reset` | Resets the stats counters to zero so you can start measuring from a clean slate |

---

## ⚙️ Configuration

S2AW **auto-generates** a JSON config file the first time it loads. You'll find it at:

```text
addons/counterstrikesharp/configs/plugins/S2AW/S2AW.json
```

Edit the file, then restart the server (or reload the plugin) for changes to take effect.

### Core Settings

| Setting | Default | What it controls |
| --- | --- | --- |
| `enabled` | `true` | Master switch — set to `false` to disable all visibility filtering |
| `max_distance` | `5000` | Players farther than this (in game units) are always visible — saves traces on large maps |
| `max_traces_per_tick` | `3500` | Total ray traces allowed per server tick — higher = more accurate but costs more CPU |
| `max_viewers_per_tick` | `64` | How many viewers to evaluate per tick — on 64+ player servers you may want to tune this |
| `visibility_grace_ticks` | `4` | After losing line-of-sight, keep the target visible for this many ticks to prevent pop-in |
| `expanded_box_scale_xy` | `3.0` | How much to expand the target's bounding box horizontally (wider = more generous visibility) |
| `expanded_box_scale_z` | `1.5` | How much to expand vertically |
| `sample_budget` | `12` | Max sample points checked per target — more points = better coverage but more traces used |
| `first_pass_budget` | `4` | Samples checked in the fast first pass before falling back to full budget |
| `enforce_fov_check` | `true` | Skip targets behind the viewer's back — saves traces with minimal risk |
| `fov_dot_threshold` | `-0.20` | FOV cutoff (dot product). `-0.20` is very wide (~100°+ each side). Set lower to be more generous |
| `tick_divider` | `1` | Process visibility every N-th tick. `1` = every tick, `2` = every other tick, etc. |

### Player Filtering

| Setting | Default | What it controls |
| --- | --- | --- |
| `ignore_bots` | `true` | Skip bot players as targets — they don't need wallhack protection |
| `process_bot_viewers` | `true` | Whether bots get their own visibility evaluation (disable to save traces) |
| `hide_teammates` | `true` | Whether teammates are candidates for hiding (disable if teammates should always be visible) |

### Debug & Visualization

These settings let you **see the plugin working** in-game. Useful for tuning or verifying on a test server.

| Setting | Default | What it controls |
| --- | --- | --- |
| `debug_draw_traces` | `false` | Render ray-trace beams as visible laser lines in the game world |
| `debug_draw_expanded_aabb` | `false` | Render the expanded bounding boxes around each player |
| `debug_draw_interval_ms` | `1000` | How often debug visuals update (in milliseconds) — lower = more frequent updates |
| `debug_draw_max_beams` | `256` | Max beam entities per round — prevents crashing the server with too many debug draws |

> 💡 **Tip:** To visualize everything at once, set both `debug_draw_traces` and `debug_draw_expanded_aabb` to `true`, and lower `debug_draw_interval_ms` to `100` for real-time feedback. Remember to turn them off in production!

---

## 🔬 How It Works (Technical)

For those who want to understand the internals:

```text
Every Server Tick:
 ├─ 1. Scan all alive players → build active player list
 ├─ 2. Rebuild target snapshots
 │     Each player gets an expanded axis-aligned bounding box (AABB)
 │     based on their collision bounds × scale config
 ├─ 3. Build viewer processing order
 │     Priority goes to players who moved or turned since last tick
 ├─ 4. For each viewer → for each enemy target:
 │     ├─ Distance check     — skip if target is beyond max_distance
 │     ├─ FOV check          — skip if target is behind the viewer
 │     ├─ Sample ray traces  — cast up to sample_budget rays at the
 │     │                       expanded AABB (closest point first,
 │     │                       then corners sorted by distance)
 │     ├─ Any ray hits open air → target is VISIBLE
 │     └─ All rays blocked → check grace period before hiding
 └─ 5. Commit hidden pawn indices
       Applied in CheckTransmit — hidden pawn entities are removed
       from the network snapshot for that specific viewer
```

The system is designed to be **conservative** — when in doubt, targets stay visible. This prevents false concealment that players would notice as enemies appearing out of thin air.
