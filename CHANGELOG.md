# Changelog

## v1.0.3 - 2026-04-27

### Smoke Tuning
- Config version bumped to v32.
- Changed default `SmokeBlockRadius` to `144.0`.
- Changed default `SmokeLifetimeTicks` to `1232` ticks.
- Changed default `SmokeBloomDurationTicks` to `192` ticks.
- Fixed aim-ray force reveal bypassing smoke blocking when the aim endpoint landed near a target inside smoke.
- Existing configs using the v31 default smoke tuning migrate automatically.

## v1.0.2 - 2026-04-27

### Smoke Timing
- Config version bumped to v31.
- Changed default `SmokeLifetimeTicks` to `1216` ticks, counting CS2 smokes as 19 seconds at 64 tick to avoid over-hiding near smoke expiry.
- Changed default `SmokeBloomDurationTicks` to `256` ticks.
- Existing configs using the old default smoke timing migrate automatically.

## v1.0.1 - 2026-04-26

### Client Crash Hardening
- Expanded associated entity collection with scene-node child/sibling owner traversal so parented or bone-merged entities are hidden with the pawn.
- Added fail-open safety flags for incomplete dependency collection, associated entity capacity overflow, and live controlled pawns that cannot be hidden safely.
- Stopped hiding dead or dying pawns; death and ragdoll transitions now stay transmitted until the engine removes or respawns the pawn.
- Added an invalid-controller guard that clears abnormal live pawns only together with their known associated closure.
- Added per-frame orphan cleanup: if a pawn is already absent from a transmit set, S2FOW clears its known associated entities from that same set.
- Added `css_fow_stats` safety counters for unsafe hides skipped, closure overflow, scene children collected, invalid-controller clears, dead force-transmits, and orphan cleanups.
- Documented the CheckTransmit ordering risk: plugins that mutate transmit after S2FOW can still orphan entities.
- Release package now contains only S2FOW plugin files; CounterStrikeSharp, RayTraceImpl, and RayTraceApi are external server prerequisites.

## v1.0.0

### Entity Visibility Hardening
- Added `CollectWearableEntities()` — hides gloves, agent accessories, and charms (`m_hMyWearables`) alongside the pawn.
- Added `CollectHostageEntities()` — hides the hostage carry prop (`m_hCarriedHostageProp`) on hostage maps.
- Fixed `FATAL ERROR: CL_CopyExistingEntity: missing client entity` crash caused by orphaned wearable entities remaining in the transmit list when the pawn was hidden.
- All child entities (pawn + weapons + wearables + hostage prop) are now hidden atomically.

### Config
- Config version bumped to v30.
- Added distance tiering (`DistanceTieringEnabled`, `FullLosDistanceUnits`, `AabbOnlyDistanceUnits`).
- Tightened default hitbox padding: `8` up, `8` side, `0` down.
- `TargetPoints.MaxMoveUnits` default set to `2.0`.

### Codebase
- Fixed `MinimumApiVersion` attribute to use the `MinimumApiVersionRequired` constant.
- Updated all doc comments to reflect full entity hierarchy coverage.
- Coverage line (`css_fow_stats`) now reports `players, weapons, wearables`.
- Removed stale README references to "direct weapons only" design.

### Previous
- Updated for CounterStrikeSharp API `1.0.367`.
- Simplified transmit hiding to player pawns and direct weapon entities.
- Kept world geometry and smoke as the only visibility blockers.
- Removed stale systems: radar/spotted-state scrubbing, projectile hiding, bullet-impact hiding, dropped-weapon ownership, planted-C4 hiding, scene-node traversal, pair visibility caching, FOV/crosshair reveal policy, adaptive quality scaling.
- Renamed movement tuning into `TargetPoints` and `ViewerRays`.
