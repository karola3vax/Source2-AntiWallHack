# Changelog

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
