<div align="center">

# S2AWH

### Server-side anti-wallhack for Counter-Strike 2

[![Version](https://img.shields.io/badge/Version-3.0.4-ec4899?style=for-the-badge&logoColor=white)](https://github.com/karola3vax/Source2-AntiWallHack/releases)
[![CounterStrikeSharp](https://img.shields.io/badge/CounterStrikeSharp-v1.0.362%2B-db2777?style=for-the-badge&logoColor=white)](https://github.com/roflmuffin/CounterStrikeSharp/releases)
[![Ray-Trace](https://img.shields.io/badge/Ray--Trace-v1.0.4-b0126f?style=for-the-badge&logoColor=white)](https://github.com/FUNPLAY-pro-CS2/Ray-Trace/releases)
[![License](https://img.shields.io/badge/License-MIT-10b981?style=for-the-badge&logoColor=white)](./LICENSE)

**S2AWH keeps hidden enemy information off the client.**

</div>

---

S2AWH runs on the server and answers one question, over and over:

> Should this player truly be able to see that enemy right now?

If the answer is yes, the game behaves normally.  
If the answer is no, the server withholds that data from the client.

That is the entire idea.

## What This Means

Wallhack-style cheats are strongest when the client already knows where everyone is.  
S2AWH reduces that knowledge at the source.

It does not try to decorate the screen.  
It does not try to fool the cheat.  
It decides whether the information should be sent at all.

## How It Works

S2AWH uses a layered visibility pipeline:

| Layer | Purpose |
| :-- | :-- |
| `FOV culling` | Skip clearly irrelevant targets early |
| `LOS probes` | Sample the target body surface |
| `Aim proximity` | Catch near-crosshair cases |
| `Jump assist` | Reduce pop-in during short jump peeks |
| `Predictive preload` | Look slightly ahead for moving viewer or target cases |
| `Transmit closure` | Hide linked entities together |
| `Reverse audit` | Refuse unsafe hides that could leave broken client references |

In practice:

- visible enemies stay normal
- hidden enemies stay unsent
- linked entities are handled together
- uncertain states fail open instead of risking client stability

## Install

### Requirements

| Dependency | Required |
| :-- | :-- |
| [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp/releases) | `v1.0.362+` |
| [MetaMod:Source](https://www.sourcemm.net/downloads.php?branch=dev) | `1387+` |
| [Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace/releases) | `v1.0.4` |

### Release Package

The GitHub release includes:

- `S2AWH.dll`
- `S2AWH.deps.json`
- `S2AWH.pdb`
- `S2AWH.example.json`
- `README.md`
- `CHANGELOG.md`
- `RELEASE_NOTES.md`
- `LICENSE`

The archive is versioned, for example `S2AWH-3.0.4.zip`.

### Paths

Plugin files:

```text
addons/counterstrikesharp/plugins/S2AWH/
```

Example config:

```text
addons/counterstrikesharp/configs/plugins/S2AWH/
```

### First Start

1. Install CounterStrikeSharp, MetaMod, and Ray-Trace.
2. Download the latest S2AWH release archive.
3. Extract it so the files land in the paths above.
4. Start the server.
5. Look for `[S2AWH]` in console.

If you want a clean reset during an upgrade, remove your old `S2AWH.json` first.

## Recommended Baselines

| Server Type | `UpdateFrequencyTicks` | `RevealHoldSeconds` |
| :-- | :--: | :--: |
| Competitive | `2` | `0.30` |
| Casual | `4` | `0.40` |
| Large server | `8` | `0.50` |
| High population | `16` | `1.00` |

Lower `UpdateFrequencyTicks` means more server work. Start conservatively, then move lower only when the server clearly has headroom.

## Settings That Matter First

### Core

| Key | Default | Meaning |
| :-- | :--: | :-- |
| `Core.Enabled` | `true` | Main switch |
| `Core.UpdateFrequencyTicks` | `16` | How often visibility work runs |

### Trace

| Key | Default | Meaning |
| :-- | :--: | :-- |
| `Trace.UseFovCulling` | `true` | Skip targets outside the viewer cone |
| `Trace.FovDegrees` | `240.0` | Viewer cone width |
| `Trace.AimRayHitRadius` | `100.0` | Aim-proximity tolerance |
| `Trace.AimRayCount` | `1` | Aim rays per viewer |
| `Trace.AimRayMaxDistance` | `3000.0` | Aim-ray range |

### Preload

| Key | Default | Meaning |
| :-- | :--: | :-- |
| `Preload.EnablePreload` | `true` | Master preload switch |
| `Preload.EnabledForPeekers` | `true` | Viewer prediction |
| `Preload.EnabledForHolders` | `false` | Target prediction |
| `Preload.PredictorDistance` | `160.0` | Look-ahead distance |
| `Preload.RevealHoldSeconds` | `0.10` | Short anti-pop hold time |

### Visibility

| Key | Default | Meaning |
| :-- | :--: | :-- |
| `Visibility.IncludeTeammates` | `false` | Apply logic to teammates too |
| `Visibility.IncludeBots` | `true` | Include bots |
| `Visibility.BotsDoLOS` | `true` | Let bots run LOS logic |

### Diagnostics

| Key | Default | Meaning |
| :-- | :--: | :-- |
| `Diagnostics.ShowDebugInfo` | `true` | Periodic console summary |
| `Diagnostics.DrawDebugTraceBeams` | `false` | In-game trace beams |
| `Diagnostics.DrawDebugAabbBoxes` | `false` | In-game AABB boxes |
| `Diagnostics.DrawAmountOfRayNumber` | `false` | In-game ray count HUD |

## What To Watch

If debug logging is enabled, the most useful lines are:

- `Safety checks`
- `Owned cache`
- `Closure offenders`

The `Owned cache` line is especially useful after the cache redesign:

- `full resyncs`
- `dirty updates`
- `post-spawn rescan marks`
- `pending rescans`

Healthy pattern:

- low `full resyncs`
- higher `dirty updates`
- brief `pending rescans` spikes around entity creation or spawn

## Notes

**Does this run on the client?**  
No. It runs on the server.

**Do players need to install anything?**  
No.

**Is this just glow blocking or cosmetic anti-ESP?**  
No. It is visibility-driven transmit filtering at the server level.

**Does it stop every cheat?**  
No. Its purpose is narrower than that: reduce hidden enemy information reaching the client.

**Can it affect performance?**  
Yes. This is a real server-side visibility system. Tune `Core.UpdateFrequencyTicks` with care.

**Why does the plugin fail open sometimes?**  
Because sending more than necessary is safer than sending a broken partial hide set that could destabilize clients.

**Do I need `S2AWH.deps.json`?**  
Yes. Ship it with the plugin.

## Project Layout

```text
S2AWH/
├─ src/        source code
├─ scripts/    release tooling
├─ configs/    example configuration
├─ README.md
├─ CHANGELOG.md
├─ RELEASE_NOTES_3.0.4.md
└─ S2AWH.csproj
```

## Development

```powershell
dotnet build .\S2AWH\S2AWH.csproj
powershell -ExecutionPolicy Bypass -File .\S2AWH\scripts\package-release.ps1
```

## Credits

- **[karola3vax](https://github.com/karola3vax)** - Author
- **[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)** by [roflmuffin](https://github.com/roflmuffin)
- **[Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace)** by [SlynxCZ](https://github.com/SlynxCZ)
- **[MetaMod:Source](https://www.metamodsource.net/)** by [AlliedModders](https://github.com/alliedmodders)

## License

MIT - see [LICENSE](./LICENSE)

<div align="center">
<br>
<i>S2AWH keeps hidden information where it belongs: on the server.</i>
<br><br>
</div>
