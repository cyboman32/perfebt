# PerfeBT

The name means "Perfect BattleTech": a play on the word *perfect*, with its "c" swapped for the "B" of BattleTech.

I started this work because of the enormous slowdowns in modded BattleTech with the RogueTech and BTAU mod packs. Both are great, but even on a beefy PC they run slow as hell: long loads, sluggish menus and multi-second freezes in combat.

All of it was developed with [OMP](https://omp.sh) using Anthropic models: profiling, analysis, fixes and these docs.

PerfeBT is a ModTek mod that makes BattleTech with large mod packs faster: shorter loads, quicker menus on the Argo, fewer and shorter freezes in combat. It is built and tested with RogueTech (BattleTech 1.9.1, ModTek 4.5.1).

**It does not change the game.** Every fix produces exactly the same results as the code it speeds up, and saves are identical. Fixes that remember results double-check themselves while you play and switch off on their own if anything ever differs. If a mod a fix depends on is missing or different, that fix simply stays off.

## What gets faster

Measured on a RogueTech career save:

| What | Before | After |
|---|---|---|
| Loading a save | ~73 s | ~32 s |
| Game startup (internet check) | 1.5–5 s | 0 |
| Contracts screen, first view of a map | up to 2.4 s | near instant |
| First mission of a session | +2.4 s | moved into the save load |
| Mission start (unit generation) | 5.5 s | 0.02 s |
| Store buy/sell switch | 2.2 s | 0.3 s |
| Entering or leaving the mech lab | ~2.4 s | ~0.5 s |
| Quitting the game | 50 s | 9 s |

Also:

- **Argo after long missions:** salvage, store, mech lab and mouse-overs no longer get 10–30× slower for the rest of the session.
- **After-action and salvage screens** no longer spend most of their time searching for the unloaded combat camera.
- **Urban combat:** kills, aura changes and movement near many buildings froze the game for 3–5 s at a time. In a recent urban mission, 16 kills took 0.2–2.2 s each (half of them under 0.4 s); movement stalls are reduced by the same fixes.
- **Garbage collection freezes** (1–5 s) are moved behind loading screens, at the cost of more RAM (see below).
- **Saves** use about 1.2 GB less memory each; the last mission's leftovers are released on the Argo.

How each fix works is in [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md); the measurements and the slow spots still open are in [docs/FINDINGS.md](docs/FINDINGS.md).

## Install

> **Online competitions:** in RogueTech and BTAU, adding PerfeBT to your install disqualifies you from their online competition play. Don't install it, or remove it, if you take part.

1. Download `PerfeBT_<version>.zip` from the [Releases](https://github.com/cyboman32/perfebt/releases) page.
2. Extract it into `BATTLETECH/Mods/`. You get `Mods/PerfeBT/` with `PerfeBT.dll`, `mod.json` and this README.
3. Nothing to tune for memory: the memory settings follow your PC's installed RAM (see below).

The defaults are for normal play: all fixes on, performance monitoring off.

**To turn PerfeBT off**, rename `Mods/PerfeBT/mod.json` to `mod.json.off`.

## Settings

All settings are in the `"Settings"` block of `Mods/PerfeBT/mod.json`. Edit them with the game closed. `true`/`false` switches something on or off; a setting missing from the file uses its default.

| Setting | Default | What it does |
|---|---|---|
| `Optimizations` | `true` | `false` turns every fix off, e.g. to compare the game with and without PerfeBT. |
| `PerformanceMonitoring` | `false` | `true` turns on the profiler and diagnostics: reports in `Mods/PerfeBT/reports` and extra lines in `PerfeBT.log`. Slows the game a little; only for measuring. |

To compare "without" and "with" PerfeBT, keep `PerformanceMonitoring` on and change only `Optimizations`.

Each fix also has its own setting (listed in [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md)). You normally don't need them; set one to `false` only if you suspect that fix.

## Memory

The game pauses completely while it frees unused memory ("garbage collection"). With RogueTech each pause takes 1–5 s and happens every few GB, so you get freezes in missions, on the Argo and in menus.

PerfeBT moves these pauses behind loading screens and lets the game grow by up to `GcBudgetMb` MB during play before pausing. **The tradeoff is fewer freezes for more RAM use.** If your PC runs low on free memory, PerfeBT frees memory early anyway.

The three RAM-dependent settings default to `0` = **automatic**: PerfeBT reads your installed RAM at startup and writes the values it picked to `PerfeBT.log` (`[gc] RAM … MB: budget … (auto)`). Set a number to override one, or a negative number (`-1`) to switch it off.

| Setting | Default | What it does |
|---|---|---|
| `LoadGcPause` | `true` | `false` restores the game's normal behaviour (and ignores the rest of this table). |
| `GcBudgetMb` | `0` (auto) | How many MB the game may grow during play before it pauses to free memory. `-1` = normal pauses during play; only loading screens are covered. |
| `GcMinFreeMemoryMb` | `0` (auto) | Free memory early when your PC has less than this many MB free. `-1` = off (not recommended). |
| `GcPauseMaxGrowthMb` | `0` (auto) | Only without a `GcBudgetMb`: the most a single load may grow before memory is freed anyway. `-1` = no limit. |
| `GcLoadCollectMinMb` | `2000` | Free memory at the end of a loading screen only if the game grew at least this much. |

Automatic values (RogueTech itself needs 10–16 GB):

| PC RAM | `GcBudgetMb` | `GcPauseMaxGrowthMb` | `GcMinFreeMemoryMb` |
|---|---|---|---|
| 16 GB | off (normal pauses during play) | `4000` | `2000` |
| 24 GB | off | `6000` | `3000` |
| 32 GB | `8000` | `8000` | `3000` |
| 48 GB or more | `14000` | `12000` | `3000` |

The budget is the RAM above 24,000 MB (rounded down to 1000, at most 14000); the load limit is a quarter of the RAM, between 4000 and 12000.

- Urban warfare maps use the most memory: with 8000 MB, a 17-minute urban mission still paused twice for 4–6 s.
- Have a page file on an SSD ("System managed size" is fine). It prevents out-of-memory crashes if RAM runs short.
- Lower `GcBudgetMb` if your PC gets sluggish during long missions or other programs start swapping.
- Raise it if you have spare RAM and still see 3–5 s freezes in missions.
- Turn off working-set cleaners (RogueTech's optional `VMMWSGC`, memory "optimizer" tools). They push the game's memory to the page file, and the next pause reads all of it back (a 2 s pause became 94 s on a 32 GB PC).

## Troubleshooting

- **Something looks wrong:** set `Optimizations` to `false`. If the problem stays, it is not PerfeBT. If it goes away, turn fixes back on one by one (see [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md)) and report which one it is.
- **Log:** `Mods/PerfeBT/PerfeBT.log`. A fix that could not apply logs `[fix] failed …`; a fix that switched itself off after a difference says so in its own line. Please include this file in bug reports.

## Other tips for RogueTech

- **Faster save loading:** the Windows user environment variable `GC_FREE_SPACE_DIVISOR=1` lets the game grow its memory more eagerly (save load paused for memory ~15 times before, ~5 after). Undo with `reg delete HKCU\Environment /v GC_FREE_SPACE_DIVISOR /f`.
- **Less logging:** `debugLog: false` in CustomAmmoCategories (both `CustomAmmoCategoriesSettings.json` and the local override in `RogueTechSaves/local/modsettings/`) and in CustomUnitDecorator.
  - **Do not** set it in CustomUnits: the game then shows a black screen at startup.

## For mod authors and contributors

- [docs/BUILDING.md](docs/BUILDING.md): what you need to build the mod from source, and how.
- [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md): every fix, diagnostic and the profiler in technical detail, plus the source layout.
- [docs/FINDINGS.md](docs/FINDINGS.md): profiling results, open slow spots, notes for other mods' authors.
- [docs/ModTek-upstream-proposals.md](docs/ModTek-upstream-proposals.md): findings that touch ModTek's code, with how well each is measured.
- [CHANGELOG.md](CHANGELOG.md)

**Building:** needs a game install with ModTek and CustomComponents, the .NET SDK and the .NET Framework 4.7.2 Developer Pack; then run `dotnet build -c Release -p:GameDir=<your BATTLETECH folder>`. Details in [docs/BUILDING.md](docs/BUILDING.md).

**Releasing:** the build needs the game's own files, so releases are built locally rather than on GitHub. With the game installed, a clean working tree and the [GitHub CLI](https://cli.github.com) logged in, run `powershell -ExecutionPolicy Bypass -File scripts/release.ps1 -Tag v1.2.3`. It builds, packs `dist/PerfeBT_v1.2.3.zip`, tags HEAD, pushes the tag and creates the GitHub release with the zip; the notes come from the `## 1.2.3` section of the CHANGELOG. `-DryRun` only builds and packs; `-GameDir` overrides the game folder.
