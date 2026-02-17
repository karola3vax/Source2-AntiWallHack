# S2AW — Source 2 Anti-Wallhack

Server-side pawn transmit filter for **Counter-Strike 2**.  
S2AW hides enemy players that are not visible to each viewer using Ray-Trace line-of-sight checks — rendering wallhacks ineffective.

> **How it works:** Every tick, S2AW checks whether each enemy is geometrically visible to each viewer.
> Enemies that are behind walls, floors, or obstacles are removed from the viewer's transmit list
> before the data ever reaches the client. Wallhack software cannot display what was never sent.

---

## Table of Contents

- [Quick Start](#quick-start)
- [Requirements](#requirements)
- [Installation](#installation)
- [Build from Source](#build-from-source)
- [Configuration](#configuration)
- [Commands](#commands)
- [Architecture Overview](#architecture-overview)
- [Performance Model](#performance-model)
- [Troubleshooting](#troubleshooting)
- [FAQ](#faq)

---

## Quick Start

1. Install Metamod:Source + CounterStrikeSharp on your CS2 server
2. Install the Ray-Trace native module + RayTraceImpl plugin
3. Drop `S2AW.dll` into your plugins folder
4. Restart the server — S2AW auto-connects and starts filtering

---

## Requirements

| Component | Purpose |
|---|---|
| **Metamod:Source** | Plugin framework for Source 2 |
| **CounterStrikeSharp** | C# plugin API for CS2 |
| **Ray-Trace native module** | C++ ray-tracing against the BSP |
| **RayTraceImpl plugin** | Bridge between CounterStrikeSharp and the native module |
| **RayTraceApi assembly** | Shared API interface (placed in `shared/`) |

---

## Installation

### Step 1 — Prerequisites

Make sure Metamod:Source and CounterStrikeSharp are already installed and working.

### Step 2 — Ray-Trace Module

Copy the Ray-Trace files to your server:

```
csgo/
├── addons/
│   ├── metamod/
│   │   └── RayTrace.vdf
│   └── RayTrace/
│       ├── bin/
│       │   └── win64/
│       │       └── RayTrace.dll
│       └── gamedata.json
```

### Step 3 — RayTraceImpl Plugin

Copy the RayTraceImpl plugin and shared assembly:

```
csgo/
└── addons/
    └── counterstrikesharp/
        ├── plugins/
        │   └── RayTraceImpl/
        │       └── RayTraceImpl.dll        (+ dependencies)
        └── shared/
            └── RayTraceApi/
                └── RayTraceApi.dll
```

### Step 4 — S2AW Plugin

Copy the S2AW plugin:

```
csgo/
└── addons/
    └── counterstrikesharp/
        └── plugins/
            └── S2AW/
                └── S2AW.dll
```

### Step 5 — Verify

Restart the server and check the console for:

```
Ray-Trace capability connected.
```

Run `css_s2aw_status` in the server console to confirm everything is active.

> **Note:** S2AW currently supports **Windows servers only**.

---

## Build from Source

```bash
dotnet build S2AW/S2AW.csproj -c Release -warnaserror
```

Output: `S2AW/bin/Release/net8.0/S2AW.dll`

---

## Configuration

S2AW auto-generates a JSON config file on first load:  
`csgo/addons/counterstrikesharp/configs/plugins/S2AW/S2AW.json`

### Core Settings

| Key | Default | Range | Description |
|---|---|---|---|
| `enabled` | `true` | — | Master on/off switch |
| `ignore_bots` | `false` | — | If `true`, bot pawns are never hidden (saves traces) |
| `process_bot_viewers` | `true` | — | If `false`, bots don't get their own visibility checks |
| `hide_teammates` | `true` | — | Hide same-team players. Set `false` for casual/DM |

### Performance Tuning

| Key | Default | Range | Description |
|---|---|---|---|
| `tick_divider` | `1` | 1–16 | Only evaluate every Nth tick. `1` = every tick |
| `max_viewers_per_tick` | `64` | 1–64 | Max viewers to evaluate per tick |
| `max_distance` | `5000` | 300–5000 | Max distance (units) for LOS checks. Beyond this, always visible |
| `max_traces_per_tick` | `3500` | 128–20000 | Hard cap on ray-trace calls per tick |
| `raytrace_retry_ticks` | `128` | 16–1024 | How often to retry if Ray-Trace API is lost |

### Visibility

| Key | Default | Range | Description |
|---|---|---|---|
| `visibility_grace_ticks` | `4` | 0–32 | After confirmed visible, keep transmitting for N extra ticks |
| `reveal_sync_ticks` | `12` | 0–32 | Extra grace when transitioning from hidden→visible (prevents flicker) |
| `enforce_fov_check` | `true` | — | Skip traces for targets outside the viewer's FOV |
| `fov_dot_threshold` | `-0.20` | -1.0–1.0 | FOV dot-product cutoff. `-0.20` ≈ 192° arc. Lower = wider |

### AABB & Sampling

| Key | Default | Range | Description |
|---|---|---|---|
| `expanded_box_scale_xy` | `3.0` | 1.0–6.0 | Horizontal expansion of target hitbox for sampling |
| `expanded_box_scale_z` | `1.5` | 1.0–6.0 | Vertical expansion of target hitbox |
| `sample_budget` | `2` | 1–3 | Max sample points to trace per target |
| `first_pass_budget` | `1` | 1–3 | Traces in the initial pass (early exit on hit) |

### Peek Compensation

| Key | Default | Range | Description |
|---|---|---|---|
| `peek_eye_offset` | `28.0` | 0.0–64.0 | Offset (units) for shoulder-peek LOS. `0` = disabled |
| `round_start_fail_open_ms` | `500` | 0–5000 | Grace period after round start where all players are visible |

### Debug (Development Only)

| Key | Default | Description |
|---|---|---|
| `debug_draw_traces` | `false` | Draw trace beams in-game |
| `debug_draw_expanded_aabb` | `false` | Draw expanded hitbox edges |
| `debug_draw_interval_ms` | `1000` | Minimum ms between debug draws |
| `debug_draw_max_beams` | `256` | Max debug beams to prevent entity overflow |

---

## Commands

| Command | Description |
|---|---|
| `css_s2aw_status` | Shows current config, Ray-Trace status, active players, hidden counts |
| `css_s2aw_stats` | Shows average traces/tick, budget utilization, health indicator |
| `css_s2aw_stats_reset` | Resets the stats history buffer |

### Example Output

```
css_s2aw_status
S2AW status
Enabled=True, RayTrace=connected, ActivePlayers=10, WarmupActive=False
HiddenViewers=8, HiddenPawns=34, BoundsCache=2, DebugBeams=0/256
Distance=5000, Traces=3500/3500, ViewersPerTick=64, Sample/First=2/1, Grace=4, RevealSync=12
ScaleXY/Z=3.00/1.50, FovCheck=True (dot>=-0.20), PeekOffset=28.0

css_s2aw_stats
S2AW stats (120 samples): health=healthy, traces=1247.3/3500 (35.6%), viewers=10.0, priority=2.40
BudgetExhaust=0.0%, Aborted=0.00/tick, FailOpen=0.00/tick, LastTickTraces=1189
```

---

## Architecture Overview

```
Server Tick
    │
    ├─ OnTick()
    │   ├─ Build active player list
    │   ├─ Build target AABB snapshots (expanded hitboxes)
    │   ├─ Detect target movement
    │   ├─ Select viewers (priority-first ordering)
    │   │
    │   └─ For each viewer:
    │       └─ ProcessViewerVisibility()
    │           ├─ For each target:
    │           │   ├─ Skip: same team / cached result / out of range / out of FOV
    │           │   ├─ IsVisibleExpandedAabb() ─── Ray-Trace sample points
    │           │   └─ IsVisibleWithPeekAssist() ── Shoulder offset check
    │           └─ Commit hidden pawn list
    │
    └─ OnCheckTransmit()
        └─ Remove hidden pawn indices from each viewer's transmit set
```

**Key concept:** S2AW works **per viewer-target pair**. Each pair has its own cached state, grace timer, and re-evaluation schedule. This avoids redundant traces while keeping response times tight for active situations.

---

## Performance Model

S2AW uses a multi-layered performance system:

### Load-Shedding Tiers

| Player Count | Tier | Trace Budget | Distance | Effect |
|---|---|---|---|---|
| < 22 | Normal | 100% | 100% | Full evaluation |
| 22–29 | Medium | 75% | 85% | Reduced distance, fewer viewers |
| ≥ 30 | Heavy | 60% | 70% | Aggressive throttling |

### Trace Avoidance (Zero-Cost Gates)

Most viewer-target pairs **never consume a trace** on any given tick:

1. **Team check** — teammates are auto-visible (configurable)
2. **Relation cache** — if this pair was recently evaluated, skip until `nextEvaluateTick`
3. **Distance gate** — beyond `max_distance`, always visible
4. **FOV gate** — targets outside the viewer's field of view are hidden with no trace
5. **Static carry** — if neither viewer nor target moved, carry previous result
6. **Deterministic stagger** — far/mid targets are spread across ticks

### Peek Compensation

When a viewer is actively moving or turning, S2AW fires additional checks from shoulder offset positions. This prevents the "pop-in" effect where an enemy appears after peeking a corner. Peek assist is:

- Only active during viewer motion
- Only for previously hidden targets
- Distance and budget limited
- Completely disabled during load-shedding

### Safety: Fail-Open Design

S2AW always defaults to **visible** on any error:

- Ray-Trace API missing → all players visible
- Trace budget exhausted → remaining viewers fail-open
- Any unexpected error → visible (never accidentally hide a legitimate player)

---

## Troubleshooting

### "Ray-Trace capability unavailable"

**Cause:** The Ray-Trace native module or RayTraceImpl plugin is not loaded.

**Fix:**

1. Verify `RayTrace.vdf` is in `addons/metamod/`
2. Verify `RayTrace.dll` is in `addons/RayTrace/bin/win64/`
3. Verify `RayTraceImpl.dll` is in `addons/counterstrikesharp/plugins/RayTraceImpl/`
4. Verify `RayTraceApi.dll` is in `addons/counterstrikesharp/shared/RayTraceApi/`
5. Restart the server (hot-reload may not initialize the native module)

### "S2AW trace budget reached this tick"

**Cause:** The server has many active players and `max_traces_per_tick` is too low.

**Fix:** This is informational. S2AW automatically fail-opens remaining viewers and prioritizes them next tick. If this happens constantly, increase `max_traces_per_tick` or reduce `max_distance`.

### High CPU usage

**Possible causes:**

- `max_traces_per_tick` set too high
- `tick_divider` set to 1 on a very busy server

**Fix:** Increase `tick_divider` to `2` or lower `max_traces_per_tick`. Check `css_s2aw_stats` — if `health=high-load`, the auto load-shedding is already active.

### Players report "pop-in"

**Cause:** Enemies appear suddenly when peeking corners.

**Fix:**

1. Increase `peek_eye_offset` (default: 28, max: 64)
2. Increase `visibility_grace_ticks` (helps retain visibility slightly longer)
3. Increase `expanded_box_scale_xy` (makes the invisible zone smaller)

---

## FAQ

**Q: Does S2AW completely prevent wallhacks?**  
A: S2AW removes the enemy pawn data from the network stream before it reaches the client. Wallhack software cannot display what was never sent. However, S2AW intentionally uses conservative (generous) visibility to avoid hiding legitimate plays — a cheater may still see very rough position info from other game systems (scoreboard, radar, sound).

**Q: Does S2AW affect legitimate players?**  
A: No. S2AW uses expanded hitboxes (larger than the player model) and fail-open design. If there's any doubt about visibility, the player is transmitted. The grace tick system also prevents flickering.

**Q: What is the performance impact?**  
A: On a typical 10v10 server, S2AW uses ~1200 traces/tick out of the default 3500 budget (~35% utilization). The load-shedding system automatically reduces work as player count increases.

**Q: Can I use S2AW on a competitive 5v5 server?**  
A: Yes. 5v5 is the lightest workload. Default settings work well without any tuning.

**Q: Does S2AW work on Linux servers?**  
A: Not currently. S2AW only supports Windows servers at this time.
