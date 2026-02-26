# Changelog

## 3.0.1

- **Better visibility in tricky angles:** Enemies are now less likely to be missed through tiny gaps and narrow peeks.
- **More stable on busy servers:** Keeps safe behavior during temporary engine issues and stays optimized for 30+ player servers.
- **Cleaner output:** Debug logs are easier to read in console.
- **Clearer docs:** README and release text were rewritten to be easier to understand.
- **New config options:** Added `Trace.AimRayHitRadius` (default `100.0`), `Trace.AimRaySpreadDegrees` (default `1.0`), and `Trace.GapSweepProximity` (default `72.0`).
- **Lowered default `RayTracePoints` from `10` to `8`:** ~20% fewer ray traces with negligible accuracy impact, better suited for 30+ player servers.
- **Fixed `RemoveTargetPlayerAndWeapons` return value:** Debug-off fast path now correctly tracks whether entities were actually removed instead of always returning `true`.
- **Changed default `IncludeTeammates` to `false`:** Teammates are always transmitted by default, skipping unnecessary LOS checks for same-team players.

## 3.0.0

- **Showcase Overhaul:** Complete performance optimization for 32+ player servers.
- **Staggered Rebuild Engine:** Spreads visibility logic across multiple ticks to eliminate frame spikes.
- **Peek-Assist Technology:** Zero-latency predictor for smoother corner peeks.
- **Cache Reuse:** Intelligent stationary-visible pair caching to save CPU cycles.
- **MathF Optimization:** Native hardware acceleration for trigonometric calculations.
- **Professional README:** Comprehensive documentation with installation guides and config reference.
- **Full Credits:** Proper attribution for all upstream dependencies.
- **MIT License:** Standard open-source licensing.
- Improved transmit entity sync set to include active/last/saved/inventory weapon handles.
- Relaxed transient owner-resolution failures to reduce weapon desync side effects.
- Standardized entity index validation with `Utilities.MaxEdicts`.
- Added LOS/predictor target cache invalidation on disconnect and full cache clear.
- Improved thin-gap LOS robustness with two-plane non-predictor sampling.
- Added hidden-only micro-hull fallback with 2 strategic probes.
- Preserved plugin behavior model (`Visible/Hidden/UnknownTransient` semantics unchanged).
