# Changelog

## 0.9.0
- Master switches `Optimizations` (default `true`) and `PerformanceMonitoring` (default `false`): one setting turns all fixes or all diagnostics off. The shipped defaults are now normal play (fixes on, profiler and diagnostics off); set `PerformanceMonitoring` to `true` for reports.
- Renamed from RTPerf to PerfeBT: `Mods/PerfeBT/`, `PerfeBT.dll`, `PerfeBT.log`, Harmony ids `io.perfebt.*`, tag index `PerfeBT_TagSetTag_TagSetID_NoCase`. Disable an old `Mods/RTPerf` install. Settings are unchanged.
- Source split: generic game/engine fixes in `src/ModTek/` (TagSetIndex, InternetCheck, JsonReflection, SaveLoadThrottle, DataLoaderMonitoring, LoadGcPause, UnloadGate, LoadingScreenFix, ExitFast, LayoutFastPath, UiListPoolTrim, SaveAlloc, UnitGenFix), mod-specific fixes and diagnostics in `src/`. CustomMaps' `LoadCustom` site is registered from `src/` through `UnloadGate.AddSite`.
- `DelayedCallCensus` diagnostic: salvage refreshes after long missions cost 1–11 ms per item in `InventoryItemElement_NotListView.ClearEverything` (3–3.5 s hitches). A native sampler put a third of that time in `SetActive(false)` → `MonoBehaviour::RemoveFromManager` → `DelayedCallManager::CancelCallDelayed`, which walks every suspended coroutine and pending Invoke in the game once per disabled behaviour. The census lists that set by behaviour, coroutine and GameObject root when the salvage screen opens and on Ctrl+Shift+D.
- `VisibilityWatch` diagnostic: urban combat froze 1–17 s at a time (about 150 s in one mission, only two of the freezes were GCs) inside LowVisibility's replacement of `LineOfSight.GetVisibilityToTargetWithPositionsAndRotations`: ~0.15 ms per call, up to 106k calls in one frame at round start, 34M `MapMetaData.GetPriorityDesignMask` lookups. `[vis]` lines per frame with calls, time, repeated arguments and sample callers, to see whether a cache can avoid the repeats.
- `VisibilityRebuildMemo` (on): `VisibilityWatch` traced the biggest urban freezes (9–17 s, growing during the mission) to kills: `TurnDirector.RebuildVisCaches` → `Team.RebuildVisibilityCacheAllUnits` runs `UpdateVisibilityCacheForDeadUnit` for every dead unit of every team, and each of those rebuilds every living unit's visibility to every combatant (~700 with urban buildings); 80% of the checks in the worst frame repeated identical arguments. Inside the outermost of these calls, `VisibilityCache.CalcVisValueToTarget` reuses a result already computed for the same cache and target (same cache write, same return value); every 64th reuse is recomputed and compared, a difference switches it off.
- `LineOfSightRayMemo` (on) and `LineOfSightFastTrace` (on): with `VisibilityRebuildMemo` on, urban deaths still froze 2.5–18 s and movement paused ~0.5 s at points along the path. Nearly all of it is `LineOfSight.bresenhamVisionTest` walking sight lines to the ~560 buildings: per death a full rebuild (no repeats), and the auras the dead unit drops (CustomActivatableEquipment in `HandleDeath`) recompute each affected unit's visibility once per removed effect — 37,654 checks with 94% repeated arguments in one 18 s freeze; the movement pauses are CustomUnits' `OnPositionUpdate` → `UpdateVisibilityCache` (~1,100 building checks). The memo remembers a sight line's result inside the outermost `CombatGameState.Update`, `AbstractActor.HandleDeath` or visibility rebuild; it drops lines near the cells of a destroyed building (`ObstructionGameLogic.BuildingDestroyedUpdateCells`) and everything when cells are repainted, around `TurnDirector.EndCurrentRound` (CustomAmmoCategories ticks fire and temporary masks in its postfix) and around `ActorMovementSequence.Update` and the move-preview panels (where CustomAmmoCategories hides design masks). Other postfixes on the walk still run on remembered values. The fast trace is the same walk without the shared `List<Point>` and the helper calls, with the original's float operations. Each has its own verification (every 64th) that switches it off on a difference.
- `FindObjectMissCache` (on): `FindObjectWatch` showed `CameraControl.Instance` searching the whole scene (~19 ms, 170k behaviours sorted by instance ID) on every call after the combat map unloaded: the audio engine asks every frame, 15–25 s of every 30 s on the after-action and salvage screens, plus 3–5 s on loading screens; `BTCustomRenderer.btSunlight` the same during map loads (0.9 s). A miss for these two types is remembered until a scene load, `CameraControl.Create` or `BTSunlight.OnEnable`; searches always run while `Create` executes (it looks the camera up again right after instantiating it); every 64th skipped search runs anyway and a hit there switches the cache off.
- `LoopTimerSeconds` diagnostic: main-thread time per frame by Unity player-loop system (`[loop]` lines), to split urban combat frames into scripts, physics, animation, UI and rendering.
- `GcBudgetMb` default 8000 → 14000. Suggested values raised by 4 GB for 32 GB PCs (8000–10000) and by 6 GB for 64 GB or more (14000–18000): an urban warfare mission still collected twice in play (6.3 s and 4.0 s freezes) with 8000.
- `FindObjectWatch` diagnostic: the after-action and salvage screens spent 42% of the main thread in `Object::FindObjectsOfType` every frame (native sampler), under `CombatGameState.Update` with the mission still loaded. Times `Object.FindObjectOfType(Type)` by searched type with sampled callers (`[findobj]` lines).
- `DelayedStartIndex` (experimental, off): the census showed the salvage list's own UI scripts (~3,000 pending Start calls while it is built) behind the `ClearEverything` cost, not leftovers from combat. `MonoBehaviour::RemoveFromManager` cancels a script's pending Start call with `DelayedCallManager::CancelCallDelayed`, which walks every pending call. Native detours (MonoMod.Core) on that function and on every path that adds or removes calls of that set keep an index by object; the cancel removes the indexed call through `DelayedCallManager::Remove` as the walk would. Sampled verification against the walk (`DelayedStartIndexVerifyEvery`); any mismatch, off-main-thread use or exception switches it off for the session. Shared build checks moved to `src/ModTek/UnityPlayerBuild.cs`.
- `SaveAlloc` (on): the save message (~290 MB in RogueTech) is written into a stream sized once with `CalculateSize()` and returned without the `ToArray` copy; the gzip output stream of `SaveBlock<T>.CompressBytes` starts at 1/32 of the input instead of its full size. Same bytes, ~1.2 GB less allocated per save (was ~3.7 GB). Conservative GC scanning (untyped `Dictionary` entry arrays) kept many of those 290–580 MB buffers alive for the rest of the session (`RootScan`).
- `GcMinFreeMemoryMb` (3000): while `LoadGcPause` keeps the collector off, a collection also runs when free RAM or free commit (RAM + page file) drops below this, at most once per 1000 MB of growth. For PCs with less RAM than the `GcBudgetMb` headroom needs.
- README documents every setting, with suggested GC values by installed RAM.
- Fix: `EmptySceneAdditive` hung the loading screen when loading a save from the Argo (`SetActiveScene failed; invalid scene` in CustomMaps `LoadCustom`). When `LevelLoader.activeScene` is already `Empty`, the level loader unloads it next; loaded additively, that unload succeeded and left no Empty scene to activate. That case now loads Empty as the original does.
- `UiListPoolTrim` (on): after a long mission, Unity UI calls on the Argo ran 10–30× slower until restart (`ValidController` 3 → 57–92 µs, `Graphic.Raycast` 15 → 435–606 µs; salvage list, store, mech lab, every mouse-over). A native sampler showed `FillScriptingArrayFromUnityObjects` going from 2.5% to 32% of the main thread: `MaskUtilities.Notify2DMaskStateChanged`/`NotifyStencilStateChanged` fill a list from Unity UI's shared `ListPool<Component>` with `GetComponentsInChildren` of a whole masked panel, `List.Clear` keeps that capacity, and Unity's `GetComponents(…, list)` visits every slot up to the capacity. After those two methods, empty pooled lists above 128 slots drop their array; logged as `[listpool]`.
- `GcDumpAfterUsedMb` 0 in the shipped mod.json (Ctrl+Shift+G still dumps); `RootScan` finds thread stacks via Toolhelp.

## 0.8.9
- `LayoutFastPath` (on): Unity UI's `LayoutRebuilder.MarkLayoutForRebuild` returns early for an inactive rect under an inactive parent, where the original marks nothing. The 0.8.8 profile put 1–2 s of every mech lab enter/exit, store switch and Argo room change in this method and `ValidController` (25–50k calls, ~20 µs each).
- `GcDump` adds a census of objects held by normal and pinned GC handles (`[gchandles]` lines). `GcDumpAfterUsedMb` 9000 in the shipped mod.json; Ctrl+Shift+G runs a collection plus census and dump on demand.
- `RootScan` (runs after `GC_dump`): lists every marked heap object of 32 MB or more and every word in writable process memory that points into one — inside a live pointer-containing heap object, a thread stack, a module image or other native memory (`[rootscan]` lines). Read-only.

## 0.8.8
- `ExitFast` (on): after the managed shutdown (Unity quitting event, other mods' `ProcessExit` handlers, Steam shutdown) the process ends instead of Unity/Mono's native teardown of a 30–45 GB heap. PlayerPrefs are saved first; skipped while a background save request is still running.
- Diagnostics: `GcStatsLog` (Boehm verbose statistics per collection in `reports/<ts>_gc.log`) and `GcDumpAfterUsedMb` (one `GC_dump` per session: roots, heap sections, per-block live objects). Addresses are for this exact `mono-2.0-bdwgc.dll` build and are checked before use.
- Profiling patterns add `UIModule`, `UIManager`, Unity UI layout/graphic and TextMeshPro types, to split the mech lab, shop and salvage freezes.

## 0.8.7
- Profiler `ProfileAlloc`: per-method managed allocation (exclusive MB) in segment reports and hitch lines, plus an `[alloc]` line every `AllocWindowSeconds` with MB/s, scene, combat and the top allocating methods.
- `[gc] collected` lines add heap size, working set before/after, page faults during the collection, private bytes, scene, combat and time since the previous collection. `[ws]` lines log external working-set trims.

## 0.8.6
- `LoadGcPause` turns the collector back on when the game quits. With it left off, Mono's finalizer thread never woke at shutdown and `mono_gc_cleanup` waited its full 40 s timeout. Exit 50 s → 9 s.

## 0.8.5
- `ReleaseCombatLeftovers` (memory only): on the Argo / main menu, clears the last mission's data held in static fields by CustomAmmoCategories (map grid and trees, combat HUD, CombatGameState, actor lookups) and MissionControl (encounter cell grid of the destroyed scene). About 260 MB / 1.7M objects.
- Diagnostics: `SaveProfile` (`[save]` per-type serialization cost; 95% is the 5,115 `StarSystem` objects), `QuitWatch` (`[quit]` timeline).
- `TransitionWatch` off by default; automatic heap census off (`HeapCensusScenes = []`).

## 0.8.4
- `LoadingScreenFix` (display only): the loading curtain stays visible during level transitions instead of a black screen. The Loading scene's clear camera drew over the UI, and from the Argo/combat nothing composited the UI render texture.

## 0.8.0 – 0.8.3
- `UnitGenFix`: MissionControl unit generation prefix 5.5 s → 20 ms.
- `UnloadSkipSites` / `UnloadDeferSites` / `EmptySceneAdditive`: fewer redundant `UnloadUnusedAssets` calls during transitions (memory timing only).
- `LoadGcPause` with `GcBudgetMb`: collections run behind loading screens and after `GcBudgetMb` of growth instead of every ~5 GB during play (memory timing only; uses more RAM).
- Diagnostics: `HeapCensus` (retention paths), `TransitionWatch` (`[screen]` lines, screenshots).

## 0.7.1
- Removed the store detail-panel dedupe (`StoreTabsFix`). The original `FillInFullMech` is not idempotent, so skipping repeat calls changed the panel.
- Profiler `LateTypePatterns` no longer instrument `Mech`, `Vehicle` and `Turret`, to keep combat overhead low.

## 0.7.0
- `FixDefaultsChangeFast`, `FixInventorySortFast`, `FixPendingAdjustScan` (CustomComponents DefaultFixer). FixMechs 5.66 s → 1.75 s.
- `FixPrefabBundleScan` (+ `VerifyPrefabBundleScan`) and `FixMechLabIconPreload` for opening the mech lab.
- `FixContractIntelMinimap`: no minimap build when it is hidden. Contracts screen up to 2.4 s → near instant.
- `PreloadIrbtPortraits`: IRBT portraits load during save load instead of at the first mission.

## 0.6.x
- `FixJsonReflection`: cached reflection in the HBS JSON deserializer. Load window 45.4 → 38.8 s.
- Profiler:
  - `LateTypePatterns` accept `^Assembly:Type` for any assembly.
  - `InitTypePatterns` and a `startup` report.

## 0.5.x
- `SaveLoadMaxLightLoads` / `LoadFrameBudgetMs` save-load throttle, `FixDataLoaderMonitoring`, `[load]` diagnostics.
- `FixCategoryCache`.

## 0.4.x and earlier
- Profiler: Harmony patch instrumentation, hitch log, segment reports, `CctorScan`.
- `FixTagSetIndex`, `SkipInternetCheck`, `FixUnitTypeTags`, `TimeAutoFixers`, `TimeLocalSettingsReads`.
