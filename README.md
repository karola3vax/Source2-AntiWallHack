# 🛡️ S2AW — Source 2 Anti-Wallhack

**Server-side wallhack protection for Counter-Strike 2.**

S2AW filters enemy player data before it reaches the client — wallhack software cannot display what was never sent.

Each tick, S2AW checks line-of-sight from every viewer to every enemy using ray-tracing. Enemies behind walls are removed from the network transmit. Simple, effective, zero client-side changes.

> ⚠️ **Windows servers only** · Requires [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)

---

## 📦 Installation

### Prerequisites

| # | Component | Link |
| - | --------- | ---- |
| 1 | **Metamod:Source** (Dev Build) | [sourcemm.net](https://www.sourcemm.net/downloads.php?branch=dev) |
| 2 | **CounterStrikeSharp** | [GitHub](https://github.com/roflmuffin/CounterStrikeSharp) |
| 3 | **Ray-Trace** native module | Included in release package |
| 4 | **RayTraceImpl** plugin | Included in release package |

### Server File Layout

```text
csgo/addons/
├── metamod/
│   └── RayTrace.vdf
├── RayTrace/
│   ├── bin/win64/RayTrace.dll
│   └── gamedata.json
└── counterstrikesharp/
    ├── plugins/
    │   ├── RayTraceImpl/
    │   │   └── RayTraceImpl.dll
    │   └── S2AW/
    │       └── S2AW.dll
    └── shared/
        └── RayTraceApi/
            └── RayTraceApi.dll
```

### Verify

Restart server → check console for `Ray-Trace capability connected.`
Run **`css_s2aw_status`** to confirm.

---

## ⚙️ Configuration

Auto-generated at first load: `configs/plugins/S2AW/S2AW.json`

### Essential

| Key | Default | Description |
| --- | ------- | ----------- |
| `enabled` | `true` | Master switch |
| `hide_teammates` | `true` | Hide same-team players (set `false` for casual/DM) |
| `max_distance` | `5000` | LOS check range in units (300–5000) |
| `max_traces_per_tick` | `3500` | Trace budget per tick (128–20000) |
| `peek_eye_offset` | `28.0` | Shoulder-peek compensation in units (0–64, `0` = off) |

### Tuning

| Key | Default | Description |
| --- | ------- | ----------- |
| `tick_divider` | `1` | Evaluate every Nth tick (1–16) |
| `max_viewers_per_tick` | `64` | Max viewers per tick (1–64) |
| `visibility_grace_ticks` | `4` | Keep visible for N extra ticks after LOS confirmed (0–32) |
| `reveal_sync_ticks` | `12` | Extra grace on hidden→visible transition (0–32) |
| `expanded_box_scale_xy` | `3.0` | Horizontal hitbox expansion (1.0–6.0) |
| `expanded_box_scale_z` | `1.5` | Vertical hitbox expansion (1.0–6.0) |
| `sample_budget` | `2` | Max sample points per target (1–3) |
| `first_pass_budget` | `1` | Traces before early-exit (1–3) |

### Other

| Key | Default | Description |
| --- | ------- | ----------- |
| `ignore_bots` | `false` | Never hide bot pawns |
| `process_bot_viewers` | `true` | Run visibility for bot viewers |
| `enforce_fov_check` | `true` | Skip traces for out-of-FOV targets |
| `fov_dot_threshold` | `-0.20` | FOV cutoff, `-0.20` ≈ 192° (-1.0–1.0) |
| `round_start_fail_open_ms` | `500` | Grace period after round start (0–5000 ms) |
| `raytrace_retry_ticks` | `128` | API reconnect interval (16–1024) |

---

## 💻 Commands

| Command | What it does |
| ------- | ------------ |
| `css_s2aw_status` | Config, Ray-Trace status, active players, hidden counts |
| `css_s2aw_stats` | Avg traces/tick, budget utilization, health |
| `css_s2aw_stats_reset` | Reset stats buffer |

---

## 🏗️ How It Works

```text
OnTick
 ├─ Build player lists + target AABB snapshots
 ├─ Detect movement (viewer turning/moving, target moving)
 ├─ Select viewers (priority-first: moving/turning viewers go first)
 │
 └─ Per viewer × per target:
     ├─ Skip: teammate / cached result / out of range / out of FOV
     ├─ IsVisibleExpandedAabb() → ray-trace sample points
     └─ IsVisibleWithPeekAssist() → shoulder offset check
         └─ Commit hidden list

OnCheckTransmit
 └─ Remove hidden pawn indices from viewer's transmit set
```

**Safety:** Every error path defaults to **visible** (fail-open). A bug will never hide a legitimate player.

---

## 📈 Performance

S2AW auto-scales based on player count:

| Players | Trace Budget | Distance | Notes |
| ------- | ------------ | -------- | ----- |
| < 22 | 100% | 100% | Full evaluation |
| 22–29 | 75% | 85% | Medium load-shedding |
| ≥ 30 | 60% | 70% | Heavy load-shedding |

Most viewer-target pairs **skip traces entirely** via:
team check → relation cache → distance gate → FOV gate → static carry → deterministic stagger

Typical 10v10: ~1200 traces/tick out of 3500 budget (~35% utilization).

---

## 🔧 Troubleshooting

**"Ray-Trace capability unavailable"**
→ RayTrace module or RayTraceImpl plugin not loaded. Verify all files are in place and restart the server.

**"trace budget reached this tick"**
→ Informational. S2AW auto-recovers by fail-opening remaining viewers. If constant, increase `max_traces_per_tick`.

**Players report pop-in**
→ Increase `peek_eye_offset` (max 64), `visibility_grace_ticks`, or `expanded_box_scale_xy`.

**High CPU usage**
→ Increase `tick_divider` to 2, or lower `max_traces_per_tick`. Check `css_s2aw_stats`.

---

## 🔨 Build

```bash
dotnet build S2AW/S2AW.csproj -c Release -warnaserror
```

---

## ❓ FAQ

**Does S2AW completely block wallhacks?**
S2AW removes enemy pawn data from the network stream. Wallhack software can't show what wasn't sent. Some indirect info (radar, sound) is outside S2AW's scope.

**Does it affect legitimate players?**
No. Expanded hitboxes + fail-open design + grace ticks ensure no legitimate play is affected.

**Works on 5v5 competitive?**
Yes. 5v5 is the lightest workload, default config works perfectly.
