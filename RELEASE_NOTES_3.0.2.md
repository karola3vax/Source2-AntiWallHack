# S2AWH 3.0.2

## Summary
- Tightened LOS/FOV correctness for thin-angle and foot-level visibility.
- Restored and cleaned the preload predictor pipeline.
- Cleaned config/runtime drift and hardened legacy config compatibility.
- Improved release/build portability and repository hygiene.

## Key Changes
- Added conservative AABB-aware FOV culling to reduce early false hides.
- Added a low-origin LOS fallback for cases where feet are visible but eye-origin rays miss.
- Made micro-hull fallback more useful for first-visible extremities like arm, hand, leg, and foot.
- Tightened bounds normalization so surrounding bounds cannot shrink below the base collision box beyond a small tolerance.
- Re-enabled real preload prediction with point traces plus surface probing.
- Renamed the generated preload master switch to `Preload.EnablePreload` while keeping legacy aliases readable.
- Hardened legacy alias parsing so malformed old config values no longer silently disable preload.
- Moved plugin logs to CounterStrikeSharp's logger pipeline.
- Added a release packaging script and a commented example config for easier deployment.

## Default Changes
- `Preload.PredictorDistance`: `64.0`
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
