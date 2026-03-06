# S2AWH 3.0.4

## Highlights

- Safer transmit closure and reverse-reference audit to reduce `Missing Client Entity` crash risk.
- Broader linked-entity coverage for player-related world entities.
- Additional closure coverage for `logic_choreographed_scene` and `point_proximitysensor` style entity references.
- Incremental and event-driven owned-entity cache with live debug telemetry.
- Cleaner project structure with all C# sources moved under `src/`.
- Updated release packaging for GitHub releases.

## Required dependencies

- CounterStrikeSharp `v1.0.362+`
- MetaMod:Source `1387+`
- Ray-Trace `v1.0.4`

## Package contents

- `addons/counterstrikesharp/plugins/S2AWH/S2AWH.dll`
- `addons/counterstrikesharp/plugins/S2AWH/S2AWH.deps.json`
- `addons/counterstrikesharp/plugins/S2AWH/S2AWH.pdb`
- `addons/counterstrikesharp/configs/plugins/S2AWH/S2AWH.example.json`
- `README.md`
- `CHANGELOG.md`
- `RELEASE_NOTES.md`
- `LICENSE`

## Upgrade notes

- Replace both `S2AWH.dll` and `S2AWH.deps.json`.
- If you want fresh defaults, remove your old `S2AWH.json` first.
- Watch the debug summary after deploy:
  - `Owned cache: ...`
  - `Closure offenders: ...`

## GitHub release summary

This release rolls all work completed after `3.0.3` into the next public GitHub version, `3.0.4`.
