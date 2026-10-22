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
- **Urban combat:** kills, aura changes and movement near many buildings froze the game for seconds at a time. The kill freezes are reduced; the latest fixes for deaths and movement are new and their in-game effect is still being measured.
- **Garbage collection freezes** (1–5 s) are moved behind loading screens, at the cost of more RAM (see below).
- **Saves** use about 1.2 GB less memory each; the last mission's leftovers are released on the Argo.

How each fix works is in [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md); the measurements and the slow spots still open are in [docs/FINDINGS.md](docs/FINDINGS.md).

## Install

1. Download `PerfeBT_<version>.zip` from the [Releases](https://github.com/cyboman32/perfebt/releases) page.
2. Extract it into `BATTLETECH/Mods/`. You get `Mods/PerfeBT/` with `PerfeBT.dll`, `mod.json` and this README.
3. Check the memory settings below against your PC's RAM.

The defaults are for normal play: all fixes on, performance monitoring off.

**Upgrading from RTPerf** (the old name): copy your changed settings over, then disable the old mod by renaming `Mods/RTPerf/mod.json` to `mod.json.off` (or delete `Mods/RTPerf`). Both at once would apply everything twice.

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

| Setting | Default | What it does |
|---|---|---|
| `LoadGcPause` | `true` | `false` restores the game's normal behaviour (and ignores the rest of this table). |
| `GcBudgetMb` | `14000` | How many MB the game may grow during play before it pauses to free memory. `0` = normal pauses during play; only loading screens are covered. |
| `GcMinFreeMemoryMb` | `3000` | Free memory early when your PC has less than this many MB free. `0` = off (not recommended). |
| `GcLoadCollectMinMb` | `2000` | Free memory at the end of a loading screen only if the game grew at least this much. |
| `GcPauseMaxGrowthMb` | `12000` | Only with `GcBudgetMb` 0: the most a single load may grow before memory is freed anyway. |

Suggested values by installed RAM (RogueTech itself needs 10–16 GB):

| PC RAM | `GcBudgetMb` | `GcPauseMaxGrowthMb` | `GcMinFreeMemoryMb` |
|---|---|---|---|
| 16 GB | `0`, or `LoadGcPause: false` if loads fail | `4000` | `2000` |
| 32 GB | `8000`–`10000` | `8000` | `3000` |
| 64 GB or more | `14000`–`18000` | `12000` | `3000` |

- Urban warfare maps use the most memory: with 8000 MB, a 17-minute urban mission still paused twice for 4–6 s.
- Have a page file on an SSD ("System managed size" is fine). It prevents out-of-memory crashes if RAM runs short.
- Lower `GcBudgetMb` if your PC gets sluggish during long missions or other programs start swapping.
- Raise it if you have spare RAM and still see 3–5 s freezes in missions.

## Troubleshooting

- **Something looks wrong:** set `Optimizations` to `false`. If the problem stays, it is not PerfeBT. If it goes away, turn fixes back on one by one (see [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md)) and report which one it is.
- **Log:** `Mods/PerfeBT/PerfeBT.log`. A fix that could not apply logs `[fix] failed …`; a fix that switched itself off after a difference says so in its own line. Please include this file in bug reports.

## Other tips for RogueTech

- **Faster save loading:** the Windows user environment variable `GC_FREE_SPACE_DIVISOR=1` lets the game grow its memory more eagerly (save load paused for memory ~15 times before, ~5 after). Undo with `reg delete HKCU\Environment /v GC_FREE_SPACE_DIVISOR /f`.
- **Less logging:** `debugLog: false` in CustomAmmoCategories (both `CustomAmmoCategoriesSettings.json` and the local override in `RogueTechSaves/local/modsettings/`) and in CustomUnitDecorator.
  - **Do not** set it in CustomUnits: the game then shows a black screen at startup.

## For mod authors and contributors

- [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md): every fix, diagnostic and the profiler in technical detail, plus the source layout.
- [docs/FINDINGS.md](docs/FINDINGS.md): profiling results, open slow spots, notes for other mods' authors.
- [docs/ModTek-upstream-proposals.md](docs/ModTek-upstream-proposals.md): fixes that would fit better in ModTek itself.
- [CHANGELOG.md](CHANGELOG.md)

**Building:** `PerfeBT.csproj` (net472) references the game's `Managed` assemblies, ModTek's `0Harmony.dll` and CustomComponents. Set `GameDir` in the csproj, then run `dotnet build -c Release`.

**Releasing:** the build needs the game's own files, so releases are built locally rather than on GitHub. With the game installed, a clean working tree and the [GitHub CLI](https://cli.github.com) logged in, run `powershell -ExecutionPolicy Bypass -File scripts/release.ps1 -Tag v1.2.3`. It builds, packs `dist/PerfeBT_v1.2.3.zip`, tags HEAD, pushes the tag and creates the GitHub release with the zip; the notes come from the `## 1.2.3` section of the CHANGELOG. `-DryRun` only builds and packs; `-GameDir` overrides the game folder.
