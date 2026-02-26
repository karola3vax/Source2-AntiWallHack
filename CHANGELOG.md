# Changelog

## 3.0.1

- **Improved detection accuracy:** Enemies peeking through narrow gaps, thin slits, and tight corners are now detected more reliably thanks to refined aim-ray and gap-sweep probes.
- **Better performance on large servers:** Default ray trace points lowered from 10 to 6, teammate visibility checks are skipped by default, and adaptive profile speed range tightened (`ProfileSpeedStart: 80`, `ProfileSpeedFull: 100`) for faster peek-assist response — all reducing CPU usage on 30+ player servers.
- **New tuning options:** Added `AimRayHitRadius`, `AimRaySpreadDegrees`, and `GapSweepProximity` settings to give server operators finer control over detection sensitivity and performance balance.
- **Bug fix:** Corrected an internal tracking issue where hidden entity removal could be misreported, improving diagnostic accuracy when debug counters are enabled.
- **Cleaner logs and docs:** Debug console output is now easier to read, and the README has been rewritten with clearer installation steps and configuration guidance.

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
