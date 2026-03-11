# Changelog

## 3.0.5 - 2026-03-11

- Fixed release packaging so `scripts/package-release.ps1` always generates temporary `RELEASE_NOTES` content when a version-specific notes file and matching changelog section are missing.
- Improved changelog section matching in the release script to handle headings like `## x.y.z - yyyy-mm-dd` reliably.
- Updated README version badge to `3.0.5` to align docs with assembly/runtime metadata.
- Expanded linked-entity coverage beyond pawn+weapon ownership to include additional dependency-backed player/world links such as beams, particles, ropes, grenades/projectiles, sprites, flames, triggers, ambient sound sources, chickens, pings, physboxes, dogtags, planted C4, hostages, breakables, and instructor entities.
- Added explicit coverage for `logic_choreographed_scene` style `CSceneEntity` targets and `point_proximitysensor` target handles so more non-player world entities are pulled into the closure graph when they point at hidden actors.
- Reworked owned-entity bucket maintenance into an incremental/event-driven cache using entity lifecycle listeners, bounded post-spawn rescans, periodic safety full resyncs, and owned-cache debug telemetry (`full resyncs`, `dirty updates`, `post-spawn rescan marks`, `pending rescans`).
- Optimized hot paths by replacing repeated linear reverse-audit membership checks with a reusable scratch `HashSet<uint>` and caching viewer HUD ray-counter colors.
- Raised the documented and supported Ray-Trace floor to `v1.0.6+` because the runtime now consumes the expanded `TraceResult` fields added on that line.
- Integrated Ray-Trace's richer `trace_t` surface into the LOS/preload pipeline by preferring exact hit points (`HasExactHit` / `HitPoint`) for proximity checks and routing `AllSolid` traces through fail-open / uncertain visibility handling instead of hard-blocking players.
- Synchronized runtime/package/assembly metadata and refreshed GitHub release packaging so the release zip now includes `S2AWH.dll`, `S2AWH.deps.json`, optional `S2AWH.pdb`, docs, release notes, and SHA256 checksum output.

## 3.0.4 - 2026-03-06

- Moved all C# source files under `src/` and simplified project compile includes to `src\**\*.cs` for a cleaner project layout.
- Reworked the runtime around a safer visibility pipeline: current FOV gating, 4x4 LOS face probes, aim-ray proximity fallback, jump-assist prediction, and predictive preload.
- Removed stale legacy code paths from LOS/preload internals and aligned comments/config/docs with the code that actually runs.
- Hardened transmit closure against `Missing Client Entity` crashes:
  - `EffectEntity` and scene-parent/scene-descendant capture
  - raw `CGameSceneNode::m_hParent` fallback using Source2 layout
  - direct wearables capture
  - final reverse-reference transmit audit before hide
  - fail-open quarantine and richer closure telemetry

## 3.0.3 - 2026-03-05

- Removed redundant cache clears in `ClearVisibilityCache` that duplicated work already done by `InvalidateLivePlayersCache`.
- Removed redundant cleanup lines in `Unload` that duplicated work already done by `ClearVisibilityCache`.
- Removed redundant `Count <= 0` guard in the eligible target entity retention loop.
- Replaced per-call debug AABB corner buffer allocation with a static reusable buffer.
- Precomputed `HalfFovRadians` during config normalization so FOV culling avoids per-call trigonometry.
- Fixed the critical `CopyExistingEntity: missing client entity` client crash by collecting `CGameSceneNode.PParent` linked entities (wearables, bone-attached cosmetics) alongside gameplay-owned entities.
- Rewrote README for compact, layman-friendly presentation while preserving full configuration reference.

## 3.0.2 - 2026-03-03

- Cleaned the generated config surface so new auto-created configs only contain live runtime keys.
- Removed `Preload.EnableSurfacePreload` from newly generated configs while keeping legacy read compatibility.
- Renamed the generated preload master switch to `Preload.EnablePreload` and kept `EnableProbePreload` / `EnableSurfacePreload` as legacy read aliases.
- Hardened legacy preload alias parsing so invalid non-boolean values no longer silently disable preload.
- Made the project file validate and resolve `Microsoft.Extensions.Logging.Abstractions.dll` through the same local dependency lookup flow as CounterStrikeSharp / Ray-Trace.
- Tightened bounds candidate normalization so surrounding/specified surrounding boxes must still contain the base collision box within a small tolerance.
- Made FOV culling more conservative by checking the target AABB bounding sphere before point-sample culling.
- Added a commented `S2AWH.example.json` written in plain language for easier setup.
- Moved plugin logs onto CounterStrikeSharp's logger pipeline instead of raw console writes.
- Added a per-tick debug beam budget so trace/AABB debug drawing cannot spam unlimited `env_beam` entities.
- Added per-viewer debug ray count HUD text in the center overlay to make live trace cost visible in-game.
- Added `Diagnostics.DrawAmountOfRayNumber` so the center HUD ray counter can be toggled independently from trace beam drawing.
- Added `Diagnostics.DrawOnlyPurpleAabb` so only the purple future predictor AABB can be drawn without LOS/current AABB clutter.
- Split preload control into `Preload.EnabledForPeekers` and `Preload.EnabledForHolders`, with peeker-only preload as the default.
- Reused same-tick visibility decisions during rebuild so transmit fallback work is not recomputed again in the same tick.
- Reworked micro-hull around slit-band and extremity-biased sampling so thin visible body parts are found earlier.
- Removed redundant LOS/preload face-grid probing after micro-hull took over that thin-slit role, cutting practical ray counts in real 1v1 tests.
- Reduced default LOS/preload probe density and changed surface-probe ordering to center-first sampling for better CPU efficiency without wasting the single-row fallback on corner-biased points.
- Added `Aabb.PredictorScaleStartSpeed` and `Aabb.PredictorScaleFullSpeed` so predictor AABB growth is controlled separately from preload look-ahead.
- Raised the default adaptive-profile speed band to stop the purple predictor AABB from hitting maximum size during normal walking.
- Capped predictor lead by target speed and update interval so the future AABB does not jump unrealistically far ahead.
- Restored real preload prediction: predictor eye/center/corner points are traced again and surface-probe preload is back in the runtime path.
- Simplified preload internals by removing stale helper paths that were cached but never used in `WillBeVisible`.
- Removed dead `Core.TransmitEntityRefreshTicks` config/docs; transmit entity lists are rebuilt per tick for safety.
- Updated README/config documentation to match the current LOS/preload/runtime behavior.
- Extracted shared AABB geometry helpers to reduce LOS/preload duplication.
- Tightened snapshot bounds normalization with scored local/world candidate selection.
- Stopped fully clearing snapshot arrays on every rebuild tick; live slots are overwritten and dead slots are already filtered out.
- Removed static mutable debug AABB corner state.
- Rebuilt missing `LosEvaluator` and aligned LOS/preload/transmit paths to slot-indexed snapshot flow.
- Reduced hot-path overhead by switching live-slot checks from `HashSet<int>` to fixed `bool[65]`.
- Moved live-slot flag population into the shared player-cache build so `OnCheckTransmit` no longer rebuilds the same slot bitmap again in the same tick.
- Removed stale/dead helper paths in `VisibilityGeometry` that were no longer used after snapshot migration.
- Simplified target cache refresh logic to avoid unreachable fallback allocation paths.
- Fixed entity-handle validity bounds in transmit filtering (`< Utilities.MaxEdicts`) to match CounterStrikeSharp handle rules.
- Cached viewer team once per viewer pass in rebuild loop to avoid repeated per-target interop reads.
- Narrowed internal helper visibility (`LosEvaluator`, `PreloadPredictor`, `TransmitFilter`, `S2AWHState`) to reduce unnecessary public API surface.
- Removed local build artifact logs from source tree and added `*.log` ignore rule for cleaner repository state.

## 3.0.1

- Added aim-ray proximity visibility checks with configurable radius, spread, count, and max distance.
- Reworked LOS around AABB surface probes plus micro-hull fallback to better handle thin-angle visibility.
- Added preload surface probing to reduce pop-in on narrow peeks.
- Removed legacy point-preload config knobs (`RayTracePoints`, `ProbePreloadOnly`, `PointPreloadFarDistance`) from the public config surface.
- Added `Preload.PredictorFullSpeed` for explicit look-ahead speed scaling.
- Simplified LOS micro-hull sampling to direct eye/center probes without array/point-count plumbing.
- Improved transmit filtering performance with cheaper entity-handle validation and periodic weapon-list refresh.
- Added and expanded debug diagnostics for trace beams, AABB boxes, and periodic runtime summaries.
- Expanded config validation and normalization with clearer warning messages for auto-corrected values.
- Updated README/config docs to match current behavior and defaults.

## 3.0.0

- Showcase overhaul focused on performance for 32+ player servers.
- Staggered rebuild engine to spread visibility work across ticks.
- Peek-assist predictor and stationary cache reuse improvements.
- Transmit entity sync improvements for active/last/saved/inventory weapon handles.
- Added LOS/predictor cache invalidation on disconnect and full cache clear.
- Preserved visibility behavior model (`Visible`, `Hidden`, `UnknownTransient`).
