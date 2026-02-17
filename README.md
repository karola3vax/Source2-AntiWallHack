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
| `enabled` | `true` | Turns the entire plugin on or off |
| `hide_teammates` | `true` | Whether teammates are hidden from each other too. Turn this off for casual or deathmatch servers |
| `max_distance` | `5000` | How far away the plugin will check for enemies. Players beyond this distance are always visible |
| `max_traces_per_tick` | `3500` | Controls how many visibility checks can happen each tick. Higher values are more accurate but use more CPU |
| `peek_eye_offset` | `28.0` | Helps prevent the "pop-in" effect when peeking around corners. Set to `0` to turn this off |

### Fine-Tuning

| Key | Default | What it does |
| --- | ------- | ------------ |
| `tick_divider` | `1` | How frequently the plugin runs visibility checks. Set to `2` to check every other tick and save CPU |
| `max_viewers_per_tick` | `64` | The maximum number of players that get their visibility calculated each tick |
| `visibility_grace_ticks` | `4` | How many extra ticks a visible enemy stays visible. Prevents enemies from flickering in and out |
| `reveal_sync_ticks` | `12` | Extra time given when a hidden enemy first becomes visible, so they smoothly appear |
| `expanded_box_scale_xy` | `3.0` | How wide the area checked around each player is. Larger values mean fewer missed peeks but less hiding |
| `expanded_box_scale_z` | `1.5` | Same as above but controls the height of the checked area |
| `sample_budget` | `2` | How many points on each enemy are checked for visibility. More points are more accurate but cost more CPU |
| `first_pass_budget` | `1` | How many quick checks to do first. If any of them see the enemy, the rest are skipped |

### Other

| Key | Default | What it does |
| --- | ------- | ------------ |
| `ignore_bots` | `false` | When turned on, bots are always visible to everyone. Saves some CPU on bot-heavy servers |
| `process_bot_viewers` | `true` | When turned off, bots don't get their own visibility calculations. Saves CPU |
| `enforce_fov_check` | `true` | Skips checking enemies that are behind the player's back, since they can't see them anyway |
| `fov_dot_threshold` | `-0.20` | Controls how wide the field of view check is. The default covers roughly 192° in front of the player |
| `round_start_fail_open_ms` | `500` | After each round starts, everyone is visible for this many milliseconds to let the game stabilize |
| `raytrace_retry_ticks` | `128` | If the ray-trace system disconnects, how often the plugin tries to reconnect |

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
