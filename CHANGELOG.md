# Changelog

## 3.0.1

- **Better performance on large servers:** Reduced default body trace points from 10 to 6, teammates are no longer checked by default, and peek-assist now reacts faster at lower speeds — all saving CPU on 30+ player servers.
- **Jump-peek support:** Players jumping to peek over walls now see enemies immediately — no more delayed pop-in during jump peeks. Falling does not leak info.
- **Improved detection accuracy:** Enemies peeking through narrow gaps, thin slits, and tight corners are now caught more reliably.
- **Wider FOV culling cone:** Default FOV increased from 200° to 220° to reduce edge cases where enemies at the screen border were briefly hidden.
- **New tuning options:** The plugin now traces rays where your crosshair is pointing — if a ray hits a wall and an enemy is nearby that hit point, they're revealed. Added `AimRayHitRadius`, `AimRaySpreadDegrees`, and `GapSweepProximity` to control how this works.
- **Bug fix:** Fixed an internal tracking issue that could misreport entity removal when debug counters were off.
- **Cleaner logs and docs:** Simplified debug output and rewrote the README with clearer instructions.

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
