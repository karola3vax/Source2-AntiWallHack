# S2AWH 3.0.3

## Summary

Code housekeeping and stability release: fixed the critical `missing client entity` crash, eliminated redundant cache operations, reduced GC pressure in debug paths, precomputed hot-path values, and rewrote the README for clarity.

## Changes

- **CRITICAL FIX**: Fixed the `CopyExistingEntity: missing client entity` crash that occurred when player pawns were hidden but their scene-graph parented cosmetics (gloves, agent models) remained transmitted.
- Removed 10+ redundant cache-clear and cleanup lines across `ClearVisibilityCache` and `Unload`.
- Removed a redundant `Count <= 0` guard in the transmit entity retention loop.
- Made the debug AABB corner buffer static to avoid 8-Vector allocation per draw call.
- Precomputed `HalfFovRadians` so FOV culling skips per-call trigonometry.
- Rewrote README as a compact, layman-friendly document with collapsible config reference.

## Upgrade Notes

- Drop-in replacement for 3.0.2 — no config changes required.
- Release zip contains:
  - `addons/counterstrikesharp/plugins/S2AWH/S2AWH.dll`
  - `addons/counterstrikesharp/configs/plugins/S2AWH/S2AWH.example.json`

## Validation

- `dotnet build -c Release`
- `dotnet build -c Release -p:EnableNETAnalyzers=true -p:AnalysisMode=AllEnabledByDefault -p:TreatWarningsAsErrors=true`
