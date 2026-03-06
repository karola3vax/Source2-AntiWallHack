<div align="center">

# S2AWH

### Server-side anti-wallhack for Counter-Strike 2

[![Version](https://img.shields.io/badge/VERSION-3.0.4-ec4899?style=for-the-badge&logoColor=white)](https://github.com/karola3vax/Source2-AntiWallHack/releases)
[![CounterStrikeSharp](https://img.shields.io/badge/CSSHARP-v1.0.362%2B-db2777?style=for-the-badge&logoColor=white)](https://github.com/roflmuffin/CounterStrikeSharp/releases)
[![Ray-Trace](https://img.shields.io/badge/RAY--TRACE-v1.0.4-b0126f?style=for-the-badge&logoColor=white)](https://github.com/FUNPLAY-pro-CS2/Ray-Trace/releases)
[![License](https://img.shields.io/badge/LICENSE-MIT-10b981?style=for-the-badge&logoColor=white)](./LICENSE)

**S2AWH keeps hidden enemy information off the client.**

</div>

---

S2AWH lives on the server. It decides whether a player should genuinely have access to an enemy, and if the answer is no, the server simply withholds that data from the viewer. That is the entire premise.

---

## Why It Matters

Most wallhack-style cheats depend on information that is already present on the client. S2AWH reduces that information at the source.

Not by dressing it up.  
Not by trying to deceive the client.  
By deciding whether it deserves to be transmitted at all.

---

## What It Does

S2AWH uses a layered visibility pipeline:

| Layer | Purpose |
|:--|:--|
| `FOV culling` | Skip clearly irrelevant targets early |
| `LOS probes` | Sample the target body surface |
| `Aim proximity` | Catch near-crosshair cases |
| `Jump assist` | Reduce pop-in during short jump peeks |
| `Predictive preload` | Look slightly ahead for moving viewer/target cases |
| `Transmit closure` | Hide linked entities together |
| `Reverse audit` | Refuse unsafe hides that could leave broken client references |

In practice:

- visible enemies remain untouched
- hidden enemies remain unsent
- linked entities are treated as a whole
- uncertain states fail open rather than risking client stability

---

## Install

### Requirements

| Dependency | Required |
|:--|:--|
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

### Project Layout

```text
S2AWH/
├─ src/        source code
├─ scripts/    release tooling
├─ configs/    example configuration
├─ README.md
├─ CHANGELOG.md
└─ S2AWH.csproj
```

### Paths

Plugin files go here:

```text
addons/counterstrikesharp/plugins/S2AWH/
```

Example config goes here:

```text
addons/counterstrikesharp/configs/plugins/S2AWH/
```

### First Start

1. Install CounterStrikeSharp, MetaMod, and Ray-Trace.
2. Copy the S2AWH release files into the paths above.
3. Start the server.
4. Look for `[S2AWH]` in console.

If you want a clean reset during an upgrade, remove your old `S2AWH.json` first.

---

## Recommended Baselines

| Server Type | `UpdateFrequencyTicks` | `RevealHoldSeconds` |
|:--|:--:|:--:|
| Competitive | `2` | `0.30` |
| Casual | `4` | `0.40` |
| Large server | `8` | `0.50` |
| High population | `16` | `1.00` |

> [!CAUTION]
> Lower `UpdateFrequencyTicks` means more server work.
> Start conservatively, then move lower only when the server clearly has the headroom.

---

## Settings That Matter First

### Core

| Key | Default | Meaning |
|:--|:--:|:--|
| `Core.Enabled` | `true` | Main switch |
| `Core.UpdateFrequencyTicks` | `16` | How often visibility work runs |

### Trace

| Key | Default | Meaning |
|:--|:--:|:--|
| `Trace.UseFovCulling` | `true` | Skip targets outside the viewer cone |
| `Trace.FovDegrees` | `240.0` | Viewer cone width |
| `Trace.AimRayHitRadius` | `100.0` | Aim-proximity tolerance |
| `Trace.AimRayCount` | `1` | Aim rays per viewer |
| `Trace.AimRayMaxDistance` | `3000.0` | Aim-ray range |

### Preload

| Key | Default | Meaning |
|:--|:--:|:--|
| `Preload.EnablePreload` | `true` | Master preload switch |
| `Preload.EnabledForPeekers` | `true` | Viewer prediction |
| `Preload.EnabledForHolders` | `false` | Target prediction |
| `Preload.PredictorDistance` | `160.0` | Look-ahead distance |
| `Preload.RevealHoldSeconds` | `0.10` | Short anti-pop hold time |

### Visibility

| Key | Default | Meaning |
|:--|:--:|:--|
| `Visibility.IncludeTeammates` | `false` | Apply logic to teammates too |
| `Visibility.IncludeBots` | `true` | Include bots |
| `Visibility.BotsDoLOS` | `true` | Let bots run LOS logic |

### Diagnostics

| Key | Default | Meaning |
|:--|:--:|:--|
| `Diagnostics.ShowDebugInfo` | `true` | Periodic console summary |
| `Diagnostics.DrawDebugTraceBeams` | `false` | In-game trace beams |
| `Diagnostics.DrawDebugAabbBoxes` | `false` | In-game AABB boxes |
| `Diagnostics.DrawAmountOfRayNumber` | `false` | In-game ray count HUD |

---

## What To Watch

If debug logging is enabled, the most useful lines are:

- `Safety checks`
- `Owned cache`
- `Closure offenders`

The `Owned cache` line is especially useful after the newer cache redesign: `full resyncs`, `dirty updates`, `post-spawn rescan marks`, and `pending rescans`.

Healthy pattern:

- low `full resyncs`
- noticeably higher `dirty updates`
- brief `pending rescans` spikes around entity creation or spawn

---

## Notes

**Does this run on the client?**  
No. It runs on the server.

**Do players need to install anything?**  
No.

**Is this just glow blocking or cosmetic anti-ESP?**  
No. It is visibility-driven transmit filtering at the server level.

**Does it stop every cheat?**  
No. Its purpose is more disciplined than that: reduce hidden enemy information reaching the client.

**Can it affect performance?**  
Yes. This is a real server-side visibility system. Tune `Core.UpdateFrequencyTicks` with care.

**Why does the plugin fail open sometimes?**  
Because sending more than necessary is still safer than sending a broken partial hide set that could destabilize clients.

**Do I need `S2AWH.deps.json`?**  
Yes. Ship it with the plugin.

---

## Development

```powershell
# Build
dotnet build .\S2AWH\S2AWH.csproj

# Package release
powershell -ExecutionPolicy Bypass -File .\S2AWH\scripts\package-release.ps1
```

---

## Credits

- **[karola3vax](https://github.com/karola3vax)** - Author
- **[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)** by [roflmuffin](https://github.com/roflmuffin)
- **[Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace)** by [SlynxCZ](https://github.com/SlynxCZ)
- **[MetaMod:Source](https://www.metamodsource.net/)** by [AlliedModders](https://github.com/alliedmodders)

## License

MIT - see [LICENSE](./LICENSE)

<div align="center">
<br>
<i>S2AWH keeps hidden information exactly where it belongs: on the server.</i>
<br><br>
</div>
