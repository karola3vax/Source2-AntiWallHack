# S2AWH 3.0.2

## Summary
- Tightened LOS/FOV correctness for thin-angle and foot-level visibility.
- Restored and cleaned the preload predictor pipeline.
- Cleaned config/runtime drift and hardened legacy config compatibility.
- Improved release/build portability and repository hygiene.
- Reduced real in-game ray cost by replacing redundant face-grid probing with stronger micro-hull slit sampling.

## Key Changes
- Added conservative AABB-aware FOV culling to reduce early false hides.
- Added a low-origin LOS fallback for cases where feet are visible but eye-origin rays miss.
- Reworked micro-hull fallback around nearest-surface, limb-biased, cap-biased, and slit-band sampling so thin visible body parts are found earlier.
- Removed redundant LOS/preload face-grid probing once micro-hull slit-band sampling took over that role.
- Tightened bounds normalization so surrounding bounds cannot shrink below the base collision box beyond a small tolerance.
- Re-enabled real preload prediction with point traces plus surface probing.
- Added same-tick visibility decision reuse so transmit fallback work is not recomputed again during the cache rebuild pass.
- Renamed the generated preload master switch to `Preload.EnablePreload` while keeping legacy aliases readable.
- Hardened legacy alias parsing so malformed old config values no longer silently disable preload.
- Moved plugin logs to CounterStrikeSharp's logger pipeline.
- Added a release packaging script and a commented example config for easier deployment.
- Added optional per-viewer ray count text in diagnostics to measure live cost in-game.

## Default Changes
- `Preload.PredictorDistance`: `96.0`
- `Preload.SurfaceProbeRows`: `1`
- `Aabb.LosSurfaceProbeRows`: `1`
- `Aabb.PredictorScaleStartSpeed`: `80.0`
- `Aabb.PredictorScaleFullSpeed`: `200.0`
- All default AABB scale values remain `1.0`

## Upgrade Notes
- Delete the old `S2AWH.json` if you want fresh defaults.
- Older configs using `Preload.EnableProbePreload` or `Preload.EnableSurfacePreload` still load, but new configs use `Preload.EnablePreload`.
- Release zip contains:
  - `addons/counterstrikesharp/plugins/S2AWH/S2AWH.dll`
  - `addons/counterstrikesharp/configs/plugins/S2AWH/S2AWH.example.json`

## Validation
- `dotnet build -c Release`
- `dotnet format --verify-no-changes`
