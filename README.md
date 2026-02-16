# 🛡️ S2AW — Source 2 Anti-Wallhack

A server-side visibility plugin for Counter-Strike 2, built on CounterStrikeSharp.

---

S2AW prevents wallhack cheats by **controlling which player entities are transmitted** to each viewer. If a target cannot be seen through legitimate line-of-sight, their pawn entity is removed from the network snapshot — the cheat never receives the data in the first place.

> **How it works:** Each tick, S2AW casts rays from every viewer's eye position toward an expanded bounding box around each target. Only targets with at least one unobstructed ray are transmitted. A grace period ensures smooth transitions when players move between cover.

## ✨ Key Features

- 🔍 **Expanded AABB sampling** — Multi-point traces against scaled bounding boxes prevent pop-in
- ⚡ **Budgeted ray tracing** — Configurable per-tick trace limit protects server performance
- 🔄 **Priority viewer system** — Players who moved or turned are processed first
- 🕐 **Visibility grace period** — Brief delay before hiding prevents flicker at cover edges
- 🛡️ **Fail-open design** — On budget exhaustion or backend unavailability, targets remain visible (no false concealment)
- 🤖 **Bot support** — Configurable bot viewer/target processing

The release package includes **everything needed** — S2AW plugin and all Ray-Trace dependencies.

> **Prerequisite:** [CounterStrikeSharp](https://docs.cssharp.dev/) and [Metamod:Source](https://www.metamodsource.net/) must already be installed on your server.

## 📦 Installation

1. Download the latest release.
2. **Drag the `addons/` folder** into your server's `csgo/` (or `game/cs2/`) directory.
3. Restart the server.

That's it — one folder, everything included:

```text
addons/
├─ metamod/
│  └─ RayTrace.vdf              ← Metamod plugin descriptor
├─ RayTrace/
│  ├─ bin/win64/RayTrace.dll    ← Native ray-trace engine
│  └─ gamedata.json             ← Gamedata offsets
└─ counterstrikesharp/
   ├─ plugins/
   │  ├─ S2AW/                  ← Anti-wallhack plugin
   │  │  ├─ S2AW.dll
   │  │  └─ S2AW.deps.json
   │  └─ RayTraceImpl/          ← Managed ray-trace bridge
   │     ├─ RayTraceImpl.dll
   │     └─ (dependencies)
   └─ shared/
      └─ RayTraceApi/           ← Shared API contract
         └─ RayTraceApi.dll
```

## 🔬 Visibility Pipeline

```text
OnTick
 ├─ 1. Scan alive players → build active player list
 ├─ 2. Rebuild target snapshots (origin + expanded AABB per player)
 ├─ 3. Build viewer process order (priority: moved/turned viewers first)
 ├─ 4. For each viewer → for each target:
 │     ├─ Distance gate (closest point on AABB vs max_distance)
 │     ├─ FOV gate (AABB center vs viewer forward)
 │     ├─ Multi-sample ray traces against expanded AABB
 │     ├─ Any open trace → VISIBLE
 │     └─ Grace period check → may keep visible briefly
 └─ 5. Commit hidden pawn indices → applied in CheckTransmit
```

## ⚡ Performance & Safety

| Feature | Description |
| --- | --- |
| **Trace budget** | `max_traces_per_tick` caps total rays per tick to protect frame time |
| **Viewer batching** | `max_viewers_per_tick` limits how many viewers are evaluated per tick |
| **Fail-open on exhaustion** | Unprocessed viewers see all targets and are prioritized next tick |
| **Viewer pose cache** | Eye position + forward vector cached per tick (avoids recomputation) |
| **Bounds template cache** | AABB templates cached by collision bounds (cleared per round, capped at 64) |
| **Debug beam budget** | `debug_draw_max_beams` prevents entity exhaustion from debug visualization |

## 🎮 Commands

| Command | Description |
| --- | --- |
| `css_s2aw_selftest` | Prints a full diagnostic report: plugin state, config values, Ray-Trace backend status, active player count, bounds cache size, and hidden viewer count |
| `css_s2aw_stats` | Shows averaged performance metrics over recent ticks: traces used, viewers processed, budget exhaustion rate, aborted/fail-open viewer counts |
| `css_s2aw_stats_reset` | Clears the stats history buffer, starting fresh metric collection from the current tick |

## ⚙️ Configuration

S2AW auto-generates a JSON config file on first load. Key settings:

| Setting | Default | Description |
| --- | --- | --- |
| `enabled` | `true` | Master on/off switch |
| `max_distance` | `2800` | Maximum visibility check distance (units) |
| `max_traces_per_tick` | `3500` | Ray trace budget per server tick |
| `max_viewers_per_tick` | `64` | Max viewers to process per tick |
| `visibility_grace_ticks` | `4` | Ticks to keep a target visible after losing LOS |
| `expanded_box_scale_xy` | `3.0` | Horizontal AABB expansion multiplier |
| `expanded_box_scale_z` | `1.5` | Vertical AABB expansion multiplier |
| `sample_budget` | `12` | Max sample points per target AABB |
| `enforce_fov_check` | `true` | Skip targets outside viewer's FOV |
| `ignore_bots` | `false` | Whether to skip bots as targets |
| `hide_teammates` | `true` | Whether to evaluate teammates for hiding |

## 📜 Policy

- S2AW uses **CounterStrikeSharp + Ray-Trace** only.
- **CS2TraceRay** is not used.
- Pawn-index filtering only — controller, scoreboard, and weapon entities are never filtered.

---

<details>
<summary><b>🔨 Building from Source (Developers)</b></summary>

**Build-time requirement:** `S2AW/libs/RayTraceApi.dll` must be present.

```powershell
dotnet build S2AW/S2AW.csproj -c Release -warnaserror
```

Output is placed under `S2AW/release/addons/counterstrikesharp/plugins/S2AW/`.

</details>
