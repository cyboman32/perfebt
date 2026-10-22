# PerfeBT

Performance fixes and a built-in profiler for BattleTech with large mod packs (developed against RogueTech on ModTek 4.5.1, HarmonyX 2.16, BattleTech 1.9.1, Unity 2018.4 / Mono 5.11).

PerfeBT patches specific hot spots found by profiling. Every fix is meant to be **result-identical** to the code it replaces: same return values, same side effects, same order. Each fix has its own switch, and a fix whose target mod or method is missing or changed is skipped with a log line instead of failing.

Measured on a RogueTech career save (6.9k mechs, ~35k defs):

| Scenario | Before | After |
|---|---|---|
| Save load: defs loading window | ~73 s | 31.8 s |
| Save load: CustomComponents DefaultFixer | 13.8 s | 1.75 s |
| Startup: blocking internet check | 1.5–5 s | 0 |
| Contracts screen, first view of a map | up to 2.4 s | near instant |
| First mission start of a session | +2.4 s (portraits) | moved into the save-load screen |

Details and the remaining slow spots are in [docs/FINDINGS.md](docs/FINDINGS.md). Changes that would fit better in ModTek itself are in [docs/ModTek-upstream-proposals.md](docs/ModTek-upstream-proposals.md).

## Install

1. Build (see below) or take a release `PerfeBT.dll`.
2. Copy `PerfeBT.dll` and `mod.json` to `BATTLETECH/Mods/PerfeBT/`. Upgrading from RTPerf (the old name): copy your changed settings over, then disable the old mod by renaming `Mods/RTPerf/mod.json` to `mod.json.off` (or delete `Mods/RTPerf`). Both loaded at once would patch everything twice.
3. Defaults are for normal play: optimizations on, performance monitoring off.
4. Check the memory settings below against your PC's RAM.

To disable PerfeBT entirely, rename `mod.json` to `mod.json.off`.

All settings are in the `"Settings"` block of `Mods/PerfeBT/mod.json`. Edit them with the game closed. `true`/`false` switches a feature on or off; a setting missing from the file uses the default listed here.

## Master switches

| Setting | Default | What it does |
|---|---|---|
| `Optimizations` | `true` | `false` turns every fix off (everything under "Memory and garbage collection" and "Fixes"), e.g. to measure the game without PerfeBT. |
| `PerformanceMonitoring` | `false` | `true` turns the diagnostics on: profiler, reports in `Mods/PerfeBT/reports`, hitch log and the log lines listed under "Diagnostics". |

With a master switch on, the individual settings decide; with it off, they are ignored. For a "without / with optimizations" comparison, keep `PerformanceMonitoring` on and change only `Optimizations`. Without optimizations there are no `[gc]` lines (they come from `LoadGcPause`) and no `[load]` lines (they come from the save-load fix); collections still show in the profiler reports and hitch lines.

## Memory and garbage collection

The game's garbage collector (Boehm GC in Unity's Mono) cannot run in the background. Every collection stops the game and walks all live memory, which takes 1–5 s with RogueTech. The game normally runs one every few GB of allocations, so you get freezes in missions, on the Argo and in menus.

PerfeBT can't make a collection shorter, but it can move it. With `LoadGcPause` on:

- The collector is off during level transitions and runs once behind the loading screen.
- With `GcBudgetMb` above 0, it also stays off during play. It runs only after the game has used `GcBudgetMb` MB more than after the last collection, or when your PC runs low on memory.

**The tradeoff is fewer freezes for more RAM:** the game can use up to `GcBudgetMb` MB more than without PerfeBT. Game results don't change; unused memory is just freed later.

| Setting | Default | What it does |
|---|---|---|
| `LoadGcPause` | `true` | Master switch for everything in this table. `false` restores the game's normal collector behaviour. |
| `GcBudgetMb` | `14000` | Extra MB the game may grow during play before a collection runs. `0` = normal collections during play; only loading screens are covered. |
| `GcMinFreeMemoryMb` | `3000` | While the collector is off, run a collection when free RAM, or free RAM + page file, drops below this many MB. After that it waits for another 1000 MB of growth before collecting early again. `0` = off (not recommended). |
| `GcLoadCollectMinMb` | `2000` | At the end of a loading screen, collect only if the game has grown at least this many MB since the last collection. |
| `GcPauseMaxGrowthMb` | `12000` | Only with `GcBudgetMb` 0: the most a single load may grow before a collection runs anyway. |

Suggested values by installed RAM (RogueTech itself needs 10–16 GB):

| PC RAM | `GcBudgetMb` | `GcPauseMaxGrowthMb` | `GcMinFreeMemoryMb` |
|---|---|---|---|
| 16 GB | `0`, or `LoadGcPause: false` if loads fail | `4000` | `2000` |
| 32 GB | `8000`–`10000` | `8000` | `3000` |
| 64 GB or more | `14000`–`18000` | `12000` | `3000` |

Heavy maps (urban warfare) allocate the most: with an 8000 MB budget, a 17-minute urban mission still had two 4–6 s collections in play.

- A page file on an SSD avoids out-of-memory crashes when RAM runs short. With "System managed size", Windows grows it when needed.
- Lower `GcBudgetMb` if your system becomes sluggish during long missions or other programs start swapping.
- Raise it if you have spare RAM and still see 3–5 s freezes in missions.
- While the collector is off, other mods' forced collections do nothing.
- `PerfeBT.log` has one `[gc]` line per collection with its reason (end of load, growth, low memory), its duration and memory before/after.

`SaveAlloc` and `ReleaseCombatLeftovers` (below) also reduce memory use.

## Fixes

Every fix is on by default (with `Optimizations` on); set it to `false` to turn it off.

| Setting | Target | What it does |
|---|---|---|
| `FixTagSetIndex` | game MDDB | Adds a `NOCASE` index on `TagSetTag(TagSetID)`. `GetTagsForSet` otherwise scans 432k rows per call (~11 ms). |
| `FixSqliteInList` | game MDDB | Writes long `IN (...)` lists in database queries as literals instead of one parameter per value (~620 ms per lance query). Same rows, same order. |
| `SkipInternetCheck` | game | Skips the synchronous 5 s HTTP connectivity check at startup and reports offline, as the game does on timeout. |
| `SaveLoadMaxLightLoads` (300), `LoadFrameBudgetMs` (1000), `SaveLoadMaxHeavyLoads` (0) | game DataManager | During save load, raises the light concurrent-load limit (vanilla 10) and bounds each frame to `LoadFrameBudgetMs`. `0` keeps the game's value. |
| `FixDataLoaderMonitoring` | game DataLoader | Skips hot-reload registration. It was an O(n) list scan per loaded file whose consumer never runs. |
| `FixJsonReflection` | game JSON deserializer | Caches `Type.GetInterfaces` / `GetGenericArguments` and memoizes `MemberInfoHelper.IsValueType` per type. Every def load goes through these (~4.5M member sets per save load). |
| `FixUnitTypeTags` | CustomComponents | Builds `MechTags ∪ ChassisTags` once per `BuildUnitTypes` call instead of once per unit type (~5 s). |
| `FixCategoryCache` | CustomComponents | During `DefaultFixer.FixMechs`, answers `IsCategory` from a per-def array (~5.3M calls). |
| `FixDefaultsChangeFast` | CustomComponents | Allocation-free `DefaultFixer.DoDefaultsChange`. Same `IsCategory`/`AddChange` calls in the same order; the original runs when trace logging is on. |
| `FixInventorySortFast` | CustomComponents | `SortMechDefInventory` looks each component's sorter up once per sort instead of twice per comparison. |
| `FixPendingAdjustScan` | CustomComponents | The duplicate check in `InventoryOperationState.DoChanges` compares category IDs instead of building two strings per element. |
| `FixPrefabBundleScan` (+ `VerifyPrefabBundleScan`) | CustomUnits | The `DataManager.Exists` postfix scanned every asset name of a bundle per call; it now uses a per-bundle index. Verify mode also runs the original and logs `MISMATCH`. |
| `FixMechLabIconPreload` | CustomFilters | Mech lab opening no longer re-reads and re-parses filter icons that are already in the SVG cache. |
| `FixContractIntelMinimap` | CustomUnits | Skips building the contract intel minimap when `Init` is about to hide it (no `Intel_Show_Minimap` company stat). It falls back to the original if unknown patches are present on map loading. |
| `PreloadIrbtPortraits` | IRBTModUtils | Runs IRBT's own portrait load during save load (after `SimGameState.Rehydrate`) instead of at the first `CombatGameState._Init`. |
| `LayoutFastPath` | Unity UI | `LayoutRebuilder.MarkLayoutForRebuild` returns early for hidden UI elements, where the original marks nothing. The store buy/sell switch went from 2.2 s to 0.3 s, and entering/leaving the mech lab from ~2.4 s to ~0.5 s. |
| `UiListPoolTrim` | Unity UI | After a long mission the whole Argo UI (salvage, store, mech lab, mouse-over) got 10–30× slower for the rest of the session. A shared Unity UI list keeps the size of the largest masked panel, and every component lookup through it pays for that size. Empty pooled lists now drop oversized storage after mask updates. |
| `SaveAlloc` | game saves | Writes the save data (~290 MB before compression) into a buffer sized once instead of growing and copying it. The compression buffer starts small. Same file bytes, ~1.2 GB less memory churn per save. |
| `ReleaseCombatLeftovers` | CustomAmmoCategories, MissionControl | Memory only: on the Argo and main menu, releases the last mission's map grid, HUD and combat state these mods keep in static fields. |
| `UnloadSkipSites`, `UnloadCoalesceFrames` (30) | game, CustomMaps | Unity's unused-asset unload costs ~3 s with RogueTech's heap. Unloads from the listed call sites are skipped when one already ran within `UnloadCoalesceFrames` frames. Empty list = off. |
| `UnloadDeferSites`, `UnloadDeferFrames` (3) | game | Unloads from the listed sites are postponed a few frames and dropped when a scene load, which unloads anyway, starts first. Empty list = off. |
| `EmptySceneAdditive` | game | Loads the intermediate empty scene on top of the loading screen during level transitions: one asset unload less and no black screen between the two loading screens. |
| `LoadingScreenFix` | game | Keeps the loading curtain visible during level transitions instead of a black screen. Display only. |
| `ExitFast` | game | On quit, ends the process after the game and all mods have finished their shutdown work, skipping Unity's slow memory teardown. Skipped while a save is still being written. |
| `FindObjectMissCache` (+ `FindObjectMissCacheVerifyEvery`, 64) | game | After a mission, the after-action and salvage screens spent 50–80% of their time searching the whole scene for the combat camera, which was unloaded with the map (`CameraControl.Instance`, asked every frame by the audio engine; ~19 ms per search). The same searches added 3–5 s to loading screens. A search that found nothing (combat camera, map sunlight) is now remembered until a scene loads or the game creates that object. Every 64th skipped search runs anyway (`0` = never); if it finds something, the cache switches off for the session (`[misscache]` lines). |
| `VisibilityRebuildMemo` (+ `VisibilityRebuildMemoVerifyEvery`, 64) | game | When a unit dies, the game rebuilds every unit's visibility to every combatant (buildings included) once for each dead unit of each team, so kills late in a mission get slower; in urban maps with LowVisibility a kill froze the game for 9–17 s, 80% of it repeating identical checks. Within one rebuild, a unit-to-target result already computed is reused; nothing moves while it runs. Every 64th reuse is computed again and compared; a difference switches the memo off for the session (`[vismemo]` line). |
| `LineOfSightRayMemo` (+ `LineOfSightRayMemoVerifyEvery`, 64) | game | In urban maps each visibility check against a building walks the map grid cell by cell along about ten sight lines, and a unit death, the auras it drops or a mech moving runs thousands of such checks: freezes of 2–18 s on deaths, ~0.5 s pauses during movement. Up to 94% of those sight lines repeated one already walked in the same freeze. Within one game update, unit death or visibility rebuild, a sight line already walked returns the same result; lines near a destroyed building are walked again, and everything is forgotten around map changes (painted danger zones, end of round when fires and smoke tick, and while CustomAmmoCategories hides terrain effects during movement). Every 64th reuse is walked again and compared; a difference switches it off for the session (`[losray]` lines). |
| `LineOfSightFastTrace` (+ `LineOfSightFastTraceVerifyEvery`, 64) | game | The same sight-line walk with the same arithmetic and the same calls to other mods' hooks, without filling the game's shared list of cells and reading it back. Every 64th line runs the game's own walk and compares; a difference switches it off for the session. Both settings stand down if another mod adds its own prefix to the walk. |

### Experimental (off by default)

| Setting | Target | What it does |
|---|---|---|
| `DelayedStartIndex` (+ `DelayedStartIndexVerifyEvery`, 64) | Unity engine | Salvage list refreshes. Hiding a UI element that was shown in the same frame makes Unity search its whole list of pending calls once per script, so building and hiding a long list costs time that grows with the square of its length (0.6–1.3 s per salvage list build measured). An index by object finds the pending call directly; the same calls are removed in the same way. Hooks native code in this game's `UnityPlayer.dll` build only; on any other build it logs a failure and stays off. Every 64th lookup is also checked against Unity's own search (`0` = never); a mismatch or anything unexpected switches it off for the session (`[startidx]` lines in `PerfeBT.log`). |

## Diagnostics

These only log, and only with `PerformanceMonitoring` set to `true`.

| Setting | What it does |
|---|---|
| `Profile` | Profiler (see below). `false` = fixes only, no overhead. |
| `ProfileAlloc`, `AllocWindowSeconds`, `AllocWindowTopN` | Memory allocated per method in profiler reports, plus an `[alloc]` line every `AllocWindowSeconds` (0 = off). |
| `FindObjectWatch` | Time spent in Unity's `FindObjectOfType` (a whole-scene search) by searched type, with sample callers (`[findobj]` lines every 30 s while it costs over 100 ms, and at each scene load). |
| `VisibilityWatch` | Frames with 1000+ visibility checks between units (`LineOfSight.GetVisibilityToTargetWithPositionsAndRotations`, replaced by LowVisibility): calls, time, how many repeated an earlier call's arguments in the same frame, sample callers (`[vis]` lines). |
| `LoopTimerSeconds` (30), `LoopTimerTopN` | Main-thread time per frame by Unity engine phase: script `Update`/`LateUpdate`, physics, animation, UI canvases, rendering, waiting for the GPU (`[loop]` lines with average, 95th percentile and worst frame). `0` = off. |
| `LoadDiagnostics` | One `[load]` line per second during save load. |
| `TimeAutoFixers`, `TimeLocalSettingsReads` | Per-delegate timings of CustomComponents auto-fixers and CustomLocalSettings readers. |
| `DelayedCallCensus` | When the salvage screen opens (and on Ctrl+Shift+D with `Profile` on): Unity's pending coroutines and Invokes, grouped by script, coroutine and object (`[delayed]` lines). Hiding any UI element walks this whole list, so a long one makes salvage refreshes slow. Only for this game build's `UnityPlayer.dll`; otherwise it logs a failure and stays off. |
| `SaveProfile` | Per-type cost of each save (`[save]` lines). |
| `QuitWatch` | Timeline of background save writes and quitting (`[quit]` lines). |
| `UnloadDiagnostics` | Every asset unload request and the long frames after it (`[unload]` lines). |
| `TransitionWatch`, `TransitionWatchShotSeconds` | Logs loading-screen state and saves a screenshot every N seconds of a level transition to `reports/screens/`. |
| `HeapCensus`, `HeapCensusHotkey` (Ctrl+Shift+H), `HeapCensusScenes`, `HeapCensusSceneDelaySeconds`, `HeapCensusPathTypes`, `HeapCensusPathShapes` | Memory census by type to `reports/<time>_heap.txt` on the hotkey or once per listed scene. Freezes the game 10–30 s. |
| `GcStatsLog` | GC statistics per collection to `reports/<time>_gc.log`. Ctrl+Shift+G then runs a collection with a full heap dump. |
| `GcDumpAfterUsedMb` | Once per session, a full heap dump after the first collection that leaves at least this many MB in use. Freezes the game for tens of seconds. `0` = off. |

## Profiler

With `"Profile": true` PerfeBT times method calls on the main thread with Harmony prefix/finalizer pairs and writes reports.

- Every Harmony prefix/postfix/finalizer/transpiled method of every mod is wrapped (`InstrumentHarmonyPatches`), so cost is attributed per mod.
- Additional types are selected by regex:
  - `TypePatterns`: Assembly-CSharp full type names, instrumented at `FinishedLoading`. Types with a static constructor are skipped, because patching can trigger it early.
  - `LateTypePatterns`: `"^Assembly:Full.Type.Name"`, instrumented by the main-menu rescan.
  - `InitTypePatterns`: same form, instrumented in PerfeBT's `Init` to time the rest of mod loading, for example ModTek's manifest processing. Written as the `startup` report.
  - `AssemblyPatterns`: whole assemblies.
  - `ExcludePatterns`: excluded `Type::Method`.
- Frames longer than `HitchMs` are logged to `PerfeBT.log` with a top-N breakdown and the instrumented/uninstrumented split.
- A report is written to `reports/` per scene change and per busy period. It lists exclusive and inclusive time per method, call counts, owners (assemblies), off-thread calls and the hitch log.
- `LoadDiagnostics` writes one `[load]` line per second during save load: loads by type, batches walked, time in `UpdateRequests` and `CheckRequestsComplete`, and GCs.
- `TimeAutoFixers` and `TimeLocalSettingsReads` log per-delegate timings for CustomComponents auto-fixers and CustomLocalSettings readers.

Overhead is roughly 1–2 µs per instrumented call. Patterns that hit hot types (per-frame or per-member methods) inflate both wall time and the numbers.

Never instrument `HBS.Util.Serialization::Put*`, `Statistic::Save` or `Variant::Save`, which run millions of times per save.

## Building

`PerfeBT.csproj` (net472) references the game's `Managed` assemblies, ModTek's `0Harmony.dll` and CustomComponents with `Private=false`. Set `GameDir` in the csproj, then run `dotnet build -c Release`.

Source layout:
- `src/ModTek/`: fixes for vanilla BattleTech, Unity and Mono code only, meant to move into ModTek. They depend on nothing else in PerfeBT except `Log`; settings arrive as parameters from `Fixes.cs`, and extensions come in through hooks (`UnloadGate.AddSite`, `LoadGcPause.AfterCollect`).
- `src/`: fixes for specific mods (CustomComponents, CustomUnits, CustomFilters, CustomMaps, CAC, MissionControl, IRBTModUtils), diagnostics, the profiler, settings and the wiring in `Fixes.cs`.

## Settings outside PerfeBT that helped

- **GC heap headroom:** the user environment variable `GC_FREE_SPACE_DIVISOR=1` makes Mono's Boehm GC grow the heap more eagerly. GCs in the save-load window dropped from ~15 to ~5. Undo with `reg delete HKCU\Environment /v GC_FREE_SPACE_DIVISOR /f`.
- **`debugLog: false`** in CustomAmmoCategories (both `CustomAmmoCategoriesSettings.json` and the local override in `RogueTechSaves/local/modsettings/`) and in CustomUnitDecorator.
  - **Do not** set it in CustomUnits: its `Log.M` returns null when debug logging is off, and several unguarded uses then throw at startup (black screen).
