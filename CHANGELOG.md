# Changelog

## v1.0.7 - 2026-04-30

### Crash Safety
- Fixed a false pause where one temporary full-update send failure, such as `network client not found for slot 0` during a round or connection transition, could turn protection off for the rest of the map.
- Startup status now says crash recovery is "not checked yet" during early config parsing instead of incorrectly saying protection is paused before the bridge has been created.

## v1.0.6 - 2026-04-30

### Crash Safety
- Paused protection when full-update crash recovery is unavailable or fails, so S2FOW does not hide players without the recovery path that prevents `CopyExistingEntity: missing client entity` crashes.
- Updated startup, toggle, and status output to say plainly when protection is paused because crash recovery is not ready.

## v1.0.5 - 2026-04-30

### Plain-English Output And Config
- Config schema bumped to v33.
- Renamed the generated config to layman-friendly sections: `Main`, `SmokeVisibility`, `EnemyCheckPoints`, `ViewerEyePrediction`, `Advanced`, and `Debug`.
- Renamed generated config keys to describe real behavior, such as `ProtectionEnabled`, `HidePlayersBehindSmoke`, `RaycastLimitPerFrame`, `ShowDebugHud`, `ShowDebugRays`, and `ShowDebugPoints`.
- Kept old v32 section and key names readable for migration, then rewrote configs in the new shape.
- Stopped writing removed ray-hit threshold settings because strict world-only visibility no longer uses them.
- Reworked startup logs, command replies, status output, debug HUD labels, README text, and tool messages for clearer server-owner language.
- Added clearer command aliases: `css_s2fow_status` and `css_s2fow_toggle`; existing `css_fow_stats` and `css_fow_toggle` remain supported.

## v1.0.4 - 2026-04-30

### Crash Mitigation
- Relicensed the project to AGPLv3.
- Added observer-side `ForceFullUpdate` recovery after CheckTransmit hide/unhide changes, coalesced per observer per frame and throttled to once every 32 ticks per observer.
- Packaged S2FOW gamedata for `INetworkServerService_GetIGameServer`, `INetworkGameServer_Slots`, and `CServerSideClient_m_nForceWaitForTick`.
- Added `css_fow_stats` counters for full-update requests, coalescing, sends, throttling, failures, and request reasons.
- Kept hidden-to-visible NOINTERP resync and added same-angle pawn teleport after full updates.
- Hardened dependent entity closure traversal and isolated per-player snapshot failures so one bad entity cannot abort the frame.

### Visibility And Tooling
- Changed world-only LOS so any world hit before the target blocks visibility; aim reveal behavior is unchanged.
- Made RayTrace call failures fail open and count in stats.
- Reduced debug beam caps so debug rendering cannot create unbounded entity counts.
- Fixed LOS layout generation checks, hitbox verification skip behavior when `resourceinfo.exe` is unavailable, and VPK extraction temp-file cleanup.
- Tightened generated-artifact ignore rules for build outputs and Python cache files.

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
