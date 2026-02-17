# 🛡️ S2AW — Source 2 Anti-Wallhack

**Server-side wallhack protection for Counter-Strike 2.**

S2AW hides enemy players that are behind walls, floors, or obstacles — the data is removed before it reaches the client, so wallhack software has nothing to show.

> ⚠️ **Windows servers only**

---

## 📦 Installation

### What You Need

| # | Component | Link |
| - | --------- | ---- |
| 1 | **Metamod:Source** (Dev Build) | [Download](https://www.sourcemm.net/downloads.php?branch=dev) |
| 2 | **CounterStrikeSharp** | [GitHub](https://github.com/roflmuffin/CounterStrikeSharp) |
| 3 | **Ray-Trace** | [GitHub](https://github.com/FUNPLAY-pro-CS2/Ray-Trace) · *Included in S2AW releases* |

### Where Files Go

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

Restart server → look for `Ray-Trace capability connected.` in console.
Type **`css_s2aw_status`** to confirm everything is running.

---

## ⚙️ Configuration

Config file is auto-created on first run at:
`configs/plugins/S2AW/S2AW.json`

> 💡 **Most servers work great with defaults.** Only change these if you have a specific reason.

### Important Settings

| Key | Default | What it does |
| --- | ------- | ------------ |
| `enabled` | `true` | Turns the plugin on or off |
| `hide_teammates` | `true` | Also hides teammates from each other. Turn off for casual or deathmatch |
| `max_distance` | `5000` | How far (in game units) to check for enemies. Enemies beyond this are always visible |
| `max_traces_per_tick` | `3500` | How many ray-trace checks are allowed per tick. Higher = more accurate but more CPU |
| `peek_eye_offset` | `28.0` | Helps prevent enemies "popping in" when you peek corners. Set to `0` to disable |

### Fine-Tuning

| Key | Default | What it does |
| --- | ------- | ------------ |
| `tick_divider` | `1` | Run checks every Nth tick. Set to `2` if your server struggles with performance |
| `max_viewers_per_tick` | `64` | How many players get checked each tick. Lower = less CPU but slower updates |
| `visibility_grace_ticks` | `4` | After an enemy becomes visible, keep them visible for a few extra ticks to prevent flickering |
| `reveal_sync_ticks` | `12` | When a hidden enemy is revealed, give extra visibility time so they don't flash in and out |
| `expanded_box_scale_xy` | `3.0` | Makes the invisible zone around players smaller (wider check area = fewer missed peeks) |
| `expanded_box_scale_z` | `1.5` | Same as above but for height |
| `sample_budget` | `2` | How many points on the enemy model to check per target. More = more accurate, more CPU |
| `first_pass_budget` | `1` | Quick-check points before going deeper. If the first one hits, skip the rest |

### Other

| Key | Default | What it does |
| --- | ------- | ------------ |
| `ignore_bots` | `false` | If on, bots will always be visible (saves CPU) |
| `process_bot_viewers` | `true` | If off, bots won't get their own visibility checks (saves CPU) |
| `enforce_fov_check` | `true` | Skip checking enemies that are behind the viewer's back |
| `fov_dot_threshold` | `-0.20` | How wide the "behind your back" zone is. Default covers about 192° in front |
| `round_start_fail_open_ms` | `500` | Everyone is visible for this many milliseconds after round start |
| `raytrace_retry_ticks` | `128` | How often to try reconnecting if the ray-trace system goes down |

---

## 💻 Commands

| Command | What it does |
| ------- | ------------ |
| `css_s2aw_status` | Shows current status: config, player count, what's hidden |
| `css_s2aw_stats` | Shows performance numbers: how many traces per tick, health indicator |
| `css_s2aw_stats_reset` | Clears the stats history |

---

## 🏗️ How It Works

```text
Every tick:
 1. Find all alive players
 2. Build expanded hitboxes for each enemy
 3. Pick which viewers to check (moving/turning players go first)
 4. For each viewer × each enemy:
    → Can this viewer see this enemy? (ray-trace check)
    → If not visible → remove from transmit list
 5. CheckTransmit: hidden enemies are stripped from the network data
```

**Safety first:** If anything goes wrong (ray-trace down, budget exhausted, any error), S2AW makes everyone visible. A bug will never make a legitimate player invisible.

---

## 📈 Performance

S2AW automatically reduces work when the server is busy:

| Players | Effect |
| ------- | ------ |
| Under 22 | Full speed |
| 22–29 | Reduced range and budget |
| 30+ | Aggressive throttling |

Most checks are **free** — teammates, cached results, faraway enemies, and enemies behind you are all skipped without any ray-trace.

Typical 10v10: ~35% of the trace budget used.

---

## 🔧 Troubleshooting

**"Ray-Trace capability unavailable"**
→ Ray-Trace files are missing or not loaded. Check that all files are in the right folders and restart the server.

**"trace budget reached"**
→ Normal on busy servers. S2AW handles it automatically. If constant, increase `max_traces_per_tick`.

**Enemies pop in when peeking corners**
→ Increase `peek_eye_offset` (try 40–64). Also try increasing `visibility_grace_ticks`.

**High CPU usage**
→ Set `tick_divider` to `2` or lower `max_traces_per_tick`. Run `css_s2aw_stats` to check load.

---

## 🔨 Build from Source

```bash
dotnet build S2AW/S2AW.csproj -c Release -warnaserror
```

---

## ❓ FAQ

**Does this completely stop wallhacks?**
S2AW removes enemy model data from the network. Wallhacks can't show players that were never sent. Some indirect info (radar blips, sound) is outside S2AW's scope.

**Will legitimate players notice anything?**
No. The safety margins are generous, and any error defaults to "show the player."

**Works for 5v5?**
Yes, 5v5 is the easiest workload. Defaults work perfectly.
