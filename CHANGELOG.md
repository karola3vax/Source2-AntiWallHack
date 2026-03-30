# Changelog

All notable changes to this project will be documented in this file.

The format is based on Keep a Changelog and this project follows Semantic Versioning where practical.

## [Unreleased]

### Added

- Repository metadata for publishing: `README.md`, `LICENSE`, `CHANGELOG.md`, `.editorconfig`, and GitHub Actions build workflow
- Suppressed-catch counters for spotted-state, projectile, impact, config I/O, and auto-profile probe failures
- Explicit controller-entity security documentation in the transmit path

### Changed

- Completed the remaining `S2FOWPlugin` partial split by moving lifecycle and config responsibilities into dedicated partial files
- Replaced config deep-clone JSON roundtrips with explicit clone methods on config sections
- Added SIMD-backed vector helpers and reused them in smoke and impact hot paths
- Reworked `TryGetGameRules` to avoid LINQ allocation and read the entity list directly

### Fixed

- Corrected missing using/import issues that prevented the solution from building cleanly
- Preserved visibility fallback behavior while making suppressed entity-access failures observable in stats output

## [1.0.0] - 2026-03-30

### Added

- Initial S2FOW server-side anti-wallhack implementation for CS2
- Visibility, smoke, projectile, impact, radar, and planted-C4 protection systems
- Guided config generation, migrations, auto-profile detection, and debug tooling
