# Changelog

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
