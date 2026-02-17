# S2AW (Source 2 Anti-Wallhack)

S2AW is a CounterStrikeSharp plugin for CS2 servers.
It reduces wallhack advantage by filtering pawn transmit per viewer using Ray-Trace LOS checks.

- If target is visible for a viewer: pawn is transmitted.
- If target is not visible: target pawn index is removed for that viewer in `CheckTransmit`.
- S2AW does not directly remove controller/scoreboard/weapon entities.

## Runtime Requirements

S2AW uses the official Ray-Trace bridge path (no CS2TraceRay fallback):

- Metamod:Source
- CounterStrikeSharp
- Ray-Trace native module
- RayTraceImpl plugin
- RayTraceApi shared assembly

Expected server layout:

- `addons/metamod/RayTrace.vdf`
- `addons/RayTrace/bin/win64/RayTrace.dll`
- `addons/RayTrace/gamedata.json`
- `addons/counterstrikesharp/plugins/RayTraceImpl/*`
- `addons/counterstrikesharp/shared/RayTraceApi/*`
- `addons/counterstrikesharp/plugins/S2AW/S2AW.dll`

## Build

```bash
dotnet build S2AW/S2AW.csproj -c Release -warnaserror
```

## Core Runtime Flow

1. `OnTick` builds active players and target expanded AABB snapshots.
2. Viewers are processed with motion priority and adaptive per-tick limits.
3. Per viewer-target relation:
   - no-trace gates run first (team, cache, distance, FOV, static carry/stagger)
   - trace path runs only when needed
4. Hidden pawn list is committed per viewer.
5. `CheckTransmit` removes only hidden pawn indices for that viewer.

## Performance Model

S2AW is optimized to reduce trace pressure:

- Per-tick trace budget (`max_traces_per_tick`)
- Load-aware budget and distance shaping
  - mid load: ~75% budget, ~85% distance envelope
  - heavy load: ~60% budget, ~70% distance envelope
- Per-viewer fair-share trace budget
- Relation cache (`hidden`, `grace`, `next-evaluate`)
- Static relation carry and far/mid deterministic stagger
- Motion-priority effect limited by distance
- Small bounded LOS sample set (`base <= 3`)
- Assist limits
  - low-load only
  - hidden + priority relations only
  - one assist attempt per viewer per tick
  - strict global assist budget cap
- Round-start fail-open warmup for state stabilization

Design choice: when Ray-Trace is unavailable or budget is exhausted, S2AW fail-opens for gameplay stability.

## Main Config Keys

- `enabled`
- `ignore_bots`
- `process_bot_viewers`
- `hide_teammates`
- `tick_divider`
- `max_viewers_per_tick`
- `max_distance`
- `visibility_grace_ticks`
- `reveal_sync_ticks`
- `enforce_fov_check`
- `fov_dot_threshold`
- `max_traces_per_tick`
- `raytrace_retry_ticks`
- `expanded_box_scale_xy`
- `expanded_box_scale_z`
- `sample_budget`
- `first_pass_budget`
- `peek_eye_offset`
- `round_start_fail_open_ms`

## Current Defaults

- `enabled=true`
- `ignore_bots=false`
- `process_bot_viewers=true`
- `hide_teammates=true`
- `tick_divider=1`
- `max_viewers_per_tick=64`
- `max_distance=5000`
- `visibility_grace_ticks=4`
- `reveal_sync_ticks=12`
- `max_traces_per_tick=3500`
- `expanded_box_scale_xy=3.0`
- `expanded_box_scale_z=1.5`
- `sample_budget=2`
- `first_pass_budget=1`
- `peek_eye_offset=28.0`
- `round_start_fail_open_ms=500`

## Commands

- `css_s2aw_status`
- `css_s2aw_stats`
- `css_s2aw_stats_reset`

