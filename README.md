# S2AW (Source 2 Anti-Wallhack)

S2AW is a CounterStrikeSharp plugin for CS2 servers.
It reduces wallhack advantage by filtering **pawn transmit per viewer** with Ray-Trace LOS.

- Visible target for viewer: transmitted
- Not visible target for viewer: target pawn index removed in `CheckTransmit`
- Scoreboard/controller/weapon entities are not directly filtered by S2AW

## Runtime Requirements

S2AW uses the official Ray-Trace bridge path (no CS2TraceRay fallback):

- Metamod:Source
- CounterStrikeSharp
- Ray-Trace native module
- RayTraceImpl plugin
- RayTraceApi shared assembly

Expected server-side layout:

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

1. `OnTick` builds active player/viewer lists and target expanded AABB snapshots.
2. Viewers are processed in adaptive batches with priority for motion/turn deltas.
3. For each viewer-target relation:
   - no-trace gates run first (team rules, cached next-eval, distance/FOV, static carry)
   - if needed, LOS traces run against expanded AABB samples
4. Hidden pawn index list is committed per viewer.
5. `CheckTransmit` removes only hidden pawn indices for that viewer.

## Performance Model (Current)

S2AW is optimized to minimize trace pressure on busy servers:

- Per-tick trace budget (`max_traces_per_tick`)
- Load-aware effective budget and distance envelope
  - medium load: budget ~75%, distance ~85%
  - heavy load: budget ~60%, distance ~70%
- Per-viewer fair-share trace budget
- Distance/FOV no-trace rejection before LOS
- Relation cache:
  - hidden/visible state
  - grace ticks
  - next evaluate tick
- Static relation carry (no trace) for non-priority viewers
- Far/mid deterministic stagger for non-priority relations
- Motion-priority influence is distance-limited:
  - far relations are not re-evaluated as full priority
- Far/mid relation throttling is more aggressive:
  - static hidden/visible relations are carried longer before recheck
  - far relations are spread over wider stagger windows
- Base LOS sample cap is small and bounded
  - runtime hard cap: `base <= 3`
- Assist pressure controls:
  - global assist budget per tick
  - assist only for currently hidden + priority relations
  - assist disabled in load-shedding mode
  - assist limited to one attempt per viewer per tick
  - assist and priority influence are both distance-limited
- Round-start warmup fail-open for state stabilization

Design choice: when trace backend/budget is unavailable, S2AW fail-opens for gameplay stability.

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
- `max_traces_per_tick`
- `expanded_box_scale_xy`
- `expanded_box_scale_z`
- `sample_budget`
- `first_pass_budget`
- `peek_eye_offset`
- `round_start_fail_open_ms`

## Default Profile (Current)

- `ignore_bots=false`
- `process_bot_viewers=true`
- `hide_teammates=true`
- `tick_divider=1`
- `max_viewers_per_tick=64`
- `max_traces_per_tick=3500`
- `sample_budget=2`
- `first_pass_budget=1`
- `expanded_box_scale_xy=3.0`
- `expanded_box_scale_z=1.5`
- `peek_eye_offset=28`
- `round_start_fail_open_ms=500`

## Commands

- `css_s2aw_status`
- `css_s2aw_stats`
- `css_s2aw_stats_reset`
