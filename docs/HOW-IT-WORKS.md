# How PerfeBT works

Technical reference for every fix, diagnostic and the profiler. For what to install and which settings to change, see the [README](../README.md); for the measurements behind each fix and the remaining slow spots, see [FINDINGS.md](FINDINGS.md).

## Principles

- Every fix is meant to be **result-identical** to the code it replaces: same return values, same side effects, same order. Saves stay byte-identical.
- Each fix has its own setting. A fix whose target mod or method is missing or changed is skipped with a `[fix] failed …` line in `PerfeBT.log` instead of failing.
- Fixes that remember results check themselves: every N-th time (default 64, `0` = never) the original code runs as well and the results are compared. A difference switches that fix off for the rest of the session and is logged. N is set by `FindObjectMissCacheVerifyEvery`, `VisibilityRebuildMemoVerifyEvery`, `LineOfSightRayMemoVerifyEvery`, `LineOfSightFastTraceVerifyEvery`, `LowVisibilitySpotMemoVerifyEvery`, `SaveSerializerLookupsVerifyEvery` and `DelayedStartIndexVerifyEvery`.
- PerfeBT only patches vanilla game, Unity and Mono methods, or explicitly named methods of known mods. It never scans all assemblies at runtime: patching a type can trigger its static constructor early.

## Master switches

`Optimizations: false` turns off everything under "Memory and garbage collection" and "Fixes"; `PerformanceMonitoring: false` turns off everything under "Diagnostics" and the profiler. With a master switch on, the individual settings decide. Without optimizations there are no `[gc]` lines (they come from `LoadGcPause`) and no `[load]` lines (they come from the save-load fix); collections still show in the profiler reports and hitch lines.

## Memory and garbage collection

The game's garbage collector (Boehm GC in Unity's Mono) is non-incremental: every collection stops the game and walks all live memory, 1–5 s with RogueTech. It normally runs every few GB of allocation.

`LoadGcPause` disables the collector (`mono_unity_gc_disable`) during level transitions and collects once at the end of the loading screen (with a budget, only if the game grew at least `GcLoadCollectMinMb` since the last collection). With a `GcBudgetMb` the collector also stays off during play; a collection runs when the heap has grown `GcBudgetMb` since the last one, or when free RAM or free commit (RAM + page file) drops below `GcMinFreeMemoryMb` (then at most once per 1000 MB of growth). Without a budget, `GcPauseMaxGrowthMb` caps the growth of a single load. While the collector is off, `GC.Collect` calls from other mods do nothing. The collector is turned back on at quit: with it off, Mono's finalizer thread never woke and `mono_gc_cleanup` waited its 40 s timeout.

`GcBudgetMb`, `GcPauseMaxGrowthMb` and `GcMinFreeMemoryMb` at `0` are derived from installed RAM (`GetPhysicallyInstalledSystemMemory`, else what Windows reports): budget = RAM − 24000 MB rounded down to 1000, at most 14000, none at 24 GB or less; load cap = RAM / 4 rounded down to 1000, between 4000 and 12000; low-memory threshold 2000 MB under 24 GB, else 3000. A positive value overrides, a negative one switches the setting off. The chosen values are logged at startup (`[gc] RAM 32768 MB: budget 8000 MB (auto), …`).

Each collection writes a `[gc]` line: reason, duration, heap and working set before/after, page faults, scene, time since the previous one.

## Fixes

| Setting | Target | How it works |
|---|---|---|
| `FixTagSetIndex` | game MDDB | Adds a `NOCASE` index on `TagSetTag(TagSetID)`. `GetTagsForSet` filters with `COLLATE NOCASE` but only BINARY indexes exist, so it scanned 432k rows per call (~11 ms). |
| `FixSqliteInList` | game MDDB (MissionControl unit generation) | Dapper expands an `IN (...)` list into one SQL parameter per value (~620 ms per lance query). Long integer lists are written as literals instead; same rows, same order. Falls back to Dapper's own expansion for anything unexpected. |
| `SkipInternetCheck` | game | `UnityGameInstance.CheckInternetConnectivity` does a synchronous HTTP request with a 5 s timeout on the main thread. It reports offline immediately, as the game does on timeout. |
| `SaveLoadMaxLightLoads` (300), `LoadFrameBudgetMs` (1000), `SaveLoadMaxHeavyLoads` (0) | game DataManager | Between `RequestDataManagerResources` and `RespondToDefsLoadComplete`, the light concurrent-load limit goes from 10 to 300, and a `LoadRequest.PopPendingRequest` prefix stops popping once the frame budget is spent. `0` keeps the game's value. |
| `FixDataLoaderMonitoring` | game DataLoader | `LoadResource(..., monitor: true)` registered every file for hot reload with an O(n) list scan; the consumer (`CheckFiles`) never runs. Forces `monitor = false`. |
| `FixJsonReflection` | game JSON deserializer | Transpilers replace `Type.GetInterfaces` / `GetGenericArguments` / `Enumerable.Contains<Type>` in `RehydrateObjectFromDictionary` and `ConvertValue` with per-type cached arrays; `MemberInfoHelper.IsValueType` is memoized per type (~4.5M member sets per save load). |
| `FixUnitTypeTags` | CustomComponents | `TagUnitType.IsThisType` built `MechTags ∪ ChassisTags` per unit type per mech (243k unions); built once per `BuildUnitTypes` call. |
| `FixCategoryCache` | CustomComponents | During `DefaultFixer.FixMechs`, `IsCategory` is answered from a per-def array (~5.3M calls). |
| `FixDefaultsChangeFast` | CustomComponents | Allocation-free `DefaultFixer.DoDefaultsChange`: same `IsCategory`/`AddChange` calls in the same order. The original runs when trace logging is on. |
| `FixInventorySortFast` | CustomComponents | `SortMechDefInventory` looks each component's sorter up once per sort instead of twice per comparison. |
| `FixPendingAdjustScan` | CustomComponents | The duplicate check in `InventoryOperationState.DoChanges` compares category IDs instead of building two strings per element. |
| `FixPrefabBundleScan` (+ `VerifyPrefabBundleScan`) | CustomUnits | The `DataManager.Exists` postfix scanned every asset name of a bundle per call; it uses a per-bundle index. Verify mode also runs the original and logs `MISMATCH`. |
| `FixMechLabIconPreload` | CustomFilters | `PreloadIcons` no longer re-reads and re-parses icons already in the SVG cache. |
| `FixContractIntelMinimap` | CustomUnits | Skips `GetIntelMinimapSprite` (map metadata, terrain, a 512×512 texture) when `LanceContractIntelWidget.Init` is about to hide the minimap (no `Intel_Show_Minimap` company stat). Falls back to the original if unknown patches are present on map loading. |
| `PreloadIrbtPortraits` | IRBTModUtils | Runs IRBT's own portrait load after `SimGameState.Rehydrate` instead of in the first `CombatGameState._Init`. |
| `LayoutFastPath` | Unity UI | `LayoutRebuilder.MarkLayoutForRebuild` returns early for an inactive rect under an inactive parent, where the original marks nothing (25–50k calls, ~20 µs each, per mech lab enter/exit or store switch). |
| `UiListPoolTrim` | Unity UI | `MaskUtilities.Notify2DMaskStateChanged` / `NotifyStencilStateChanged` fill a list from Unity UI's shared `ListPool<Component>` with `GetComponentsInChildren` of a whole masked panel; `List.Clear` keeps that capacity, and Unity's `GetComponents(…, list)` visits every slot up to it. After those two methods, empty pooled lists above 128 slots drop their array (`[listpool]` lines). |
| `SaveAlloc` | game saves | The save message (~290 MB) is written into a stream sized once with `CalculateSize()` and returned without the `ToArray` copy; the gzip output stream of `SaveBlock<T>.CompressBytes` starts at 1/32 of the input. Same bytes. |
| `ReleaseSavedStream` | game saves | After a save is written, the slot's `GameInstanceSave` still holds the serialized reference stream (~290 MB per save in RogueTech) for the rest of the session. Only `PostDeserialization` reads it, and loading always reads the file again, so the stream of the slot just written is released once the save block is stored (`[savestream]` lines). |
| `SaveSerializerLookups` (+ `…VerifyEvery`) | game saves | The game's serializer finds each object's contract, factory and type id by scanning its tables front to back (`DataContractManager`, `ClassTypeResolver`, `SerializationFactoryManager`), and finds `Enumerable.ToArray` by name for every list field. The first matching entry comes from a dictionary built in table order instead; tables are rebuilt when their list or count changes. Lookups that find nothing, or tables with a null entry, run the original. Every N-th lookup is also scanned and compared; a difference switches the caches off (`[savelookups]` lines). Off if another mod patches a lookup. |
| `SaveSerializerCopies` | game saves | Each child message, array element and stat collection's bytes were copied again into the parent message (`ByteString.CopyFrom`). Arrays that are new and held by nothing else are wrapped instead (protobuf's own `AttachBytes`, checked at startup); with `SaveAlloc`, messages of 16 KB or more (each star system's) are written into an exact-size stream. Same bytes (`[savecopies]` lines). |
| `ReleaseCombatLeftovers` | game, CustomAmmoCategories, MissionControl | On the Argo and main menu, clears the last mission's map grid and trees, combat HUD, `CombatGameState`, actor lookups, floatie HUD, move-preview descriptions, AI pathing records and encounter cell grid that these mods keep in static fields, and the out-of-map cell's `mapMetaData` (vanilla sets it again before reading it; ~600 MB in one session). |
| `UnloadSkipSites`, `UnloadCoalesceFrames` (30) | game, CustomMaps | `Resources.UnloadUnusedAssets` from the listed call sites is skipped when one already ran within `UnloadCoalesceFrames` frames (~3 s each with RogueTech's heap). Empty list = off. |
| `UnloadDeferSites`, `UnloadDeferFrames` (3) | game | Unloads from the listed sites are postponed a few frames and dropped when a scene load, which unloads anyway, starts first. Empty list = off. |
| `EmptySceneAdditive` | game | Loads the intermediate `Empty` scene additively during level transitions: one asset unload less. When `LevelLoader.activeScene` is already `Empty`, it loads as the original does. |
| `LoadingScreenFix` | game | The Loading scene's clear camera drew over the UI, and from the Argo or combat nothing composited the UI render texture; the curtain now stays visible. Display only. |
| `ExitFast` | game | After the managed shutdown (Unity quitting event, other mods' `ProcessExit` handlers, Steam shutdown) and saving PlayerPrefs, the process ends instead of running Unity/Mono's native teardown of a 30–45 GB heap. Skipped while a background save is still running. |
| `FindObjectMissCache` (+ `…VerifyEvery`) | game | After combat, `CameraControl.Instance` (asked every frame by the audio engine) searched the whole scene (~19 ms, 170k behaviours) because the camera was unloaded; `BTCustomRenderer.btSunlight` did the same during map loads. A miss for these two types is remembered until a scene load, `CameraControl.Create` or `BTSunlight.OnEnable`; searches always run inside `Create` (`[misscache]` lines). |
| `VisibilityRebuildMemo` (+ `…VerifyEvery`) | game | On a kill, `TurnDirector.RebuildVisCaches` → `Team.RebuildVisibilityCacheAllUnits` runs `UpdateVisibilityCacheForDeadUnit` per dead unit of every team, each rebuilding every unit's visibility to every combatant. Inside the outermost of these calls, `VisibilityCache.CalcVisValueToTarget` reuses a result already computed for the same cache and target (same cache write, same return value) (`[vismemo]` lines). |
| `LineOfSightRayMemo` (+ `…VerifyEvery`, `…Persist`) | game | Remembers `LineOfSight.bresenhamVisionTest` results by all arguments. With `LineOfSightRayMemoPersist` (on) the results are kept until the map may change, so later deaths, round starts and actions in the same round reuse the rays the first one traced; without it they are kept only inside the outermost `CombatGameState.Update`, `AbstractActor.HandleDeath` or visibility rebuild. See "Sight-line memo" below (`[losray]` lines). |
| `LineOfSightFastTrace` (+ `…VerifyEvery`) | game | The same walk as `bresenhamVisionTest` with `getProjectedHeightAt` inlined and the Bresenham points in reused arrays instead of the shared `List<Point>`; same float operations in the same order, and the same calls to `visCostOfCell` (so design-mask patches of other mods still apply). Off if `BresenhamLine` or `getProjectedHeightAt` is patched. |
| `LineOfFireRayMemo` (+ `…VerifyEvery`) | game | The sight-line memo for `LineOfSight.bresenhamHeightTest`, the height-only walk behind line of fire (LowVisibility's `GetLineOfFireUncached` runs ~8 per unit and target in every visibility rebuild). Remembers the result and the collision point; reads only cell heights and building cells, so design-mask changes don't drop it. Kept across calls with `LineOfSightRayMemoPersist`. |
| `LowVisibilityLineOfFire` (+ `…VerifyEvery`) | LowVisibility | LowVisibility's `LineOfSight_GetLineOfFireUncached.Prefix` copies `Combat.AllActors`, sorts it by distance and filters blockers on every call, but reads that list only for actor targets. For building targets (98% of the calls in urban maps) a prefix on it runs the rest of LowVisibility's code as written (same calls and float operations in the same order) without the list. Only for the exact prefix IL it was written against (SHA-256), not while LowVisibility trace logging is on, off if another mod patches that prefix. `[lof]` lines. |
| `LowVisibilitySpotMemo` (+ `…VerifyEvery`) | LowVisibility | LowVisibility's `VisualLockHelper.CanSpotTarget` tries every pair of the source's and target's sight points with `LineOfSight.HasLineOfSight` (`bresenhamVisionTest(…) <= range`); with every ray remembered a building still cost ~0.05 ms, 0.8–1.1 s per urban death. For building targets the answer is remembered by spotter range, both point lists, building and `LineOfSight`, while `LineOfSightRayMemo` is kept across calls and has seen no map change (destroyed building, design mask, repaint, scene load); not while design masks are hidden or a StrategicOperations garrison squad looks. Only for the exact `CanSpotTarget` IL it was written against (SHA-256), not while LowVisibility trace logging is on, off if another mod patches it, `HasLineOfSight` or `bresenhamVisionTest`. `[spotmemo]` lines. |
| `DelayedStartIndex` (+ `…VerifyEvery`, experimental, off) | Unity engine | `MonoBehaviour::RemoveFromManager` cancels a script's pending `Start` with `DelayedCallManager::CancelCallDelayed`, which walks every pending call; building and hiding a long list is quadratic. Native detours (MonoMod.Core) on that function and every path that adds or removes those calls keep an index by object; the cancel removes the indexed call through `DelayedCallManager::Remove` as the walk would. Only for this game's `UnityPlayer.dll` build; off-main-thread use or any exception switches it off (`[startidx]` lines). |

### Sight-line memo

A sight line's result depends only on its arguments and map state: cell heights, terrain and design masks, building cells. Units rarely move between two deaths, so without the memo each death traces the same ~100,000 rays to the buildings again. The memo is dropped:

- at the end of the outermost update, death or rebuild, unless `LineOfSightRayMemoPersist` is on;
- for rays whose bounding box (end points ±2 cells) contains a cell of a destroyed building (`ObstructionGameLogic.BuildingDestroyedUpdateCells`), found lazily at the next lookup;
- for vision rays only, whose bounding box contains a cell whose `terrainMask` a repaint changed (`MarkDropshipLandingZone`, `MarkAssociatedCellsAsDangerous`, `PaintDangerousLocationForDropPod`, `MapMetaData.PaintDangerousLocation`; each only sets or clears terrain-mask flags). Every cell's `terrainMask` is compared before and after; line-of-fire rays read only heights and building cells and stay. Before, a repaint dropped both memos entirely: a reinforcement drop made the next death trace every ray again;
- entirely when a scene loads. At 300,000 rays the memo starts a new generation: rays reused from the previous one move over, the rest are dropped when the next one fills, so the rays of current unit positions stay;
- for vision rays only, whose bounding box contains a cell whose design mask CustomAmmoCategories changed: `MapTerrainDataCellEx.AddDesignMask`, `ReconstructTempDesignMask`, `burn`, `burnTick` (the only writers of the cell state its `GetPriorityDesignMask` postfix reads, each on its own cell). The cell's `terrainMask`, `tempDesignMask` and `CustomDesignMask` are compared before and after each call; calls that change none of them (a fire counting down, the per-round smoke reconstruction, a mask timer extended) drop nothing. Line-of-fire rays read no design masks and stay. Before, every call dropped the whole memo, so any fire or smoke on the map made the next round start or death trace every ray again (`[losray] design masks changed: …` lines; the scene summary counts them).

While CustomAmmoCategories' `bHIDE_DESIGN_MASK_FLAG` is set (its movement update and move previews, and CustomUnits' `AbstractActor.OnPositionUpdate` for units unaffected by design masks), rays are neither remembered nor reused. Any other mod patching `visCostOfCell`, `GetPriorityDesignMask` or `GetPriorityTerrainMaskFlags` switches the memo off. Only main-thread traces use the memo or the fast trace.

The memo stores the original's result before other postfixes; on a hit the original is skipped but later postfixes (StrategicOperations') still run. Both line-of-sight settings stand down if another mod adds a prefix or transpiler to `bresenhamVisionTest`; the memo also if another postfix runs before its own.

## Diagnostics

Only with `PerformanceMonitoring: true`.

| Setting | How it works |
|---|---|
| `ProfileAlloc`, `AllocWindowSeconds`, `AllocWindowTopN` | Per-method managed allocation (exclusive MB) in reports and hitch lines, plus an `[alloc]` line every `AllocWindowSeconds` with MB/s and the top allocating methods (`0` = off). Accurate while the collector is off. |
| `FindObjectWatch` | Times `Object.FindObjectOfType(Type)` by searched type with sampled callers (`[findobj]` lines every 30 s while over 100 ms, and at each scene load). |
| `VisibilityWatch` | Frames with 1000+ `LineOfSight.GetVisibilityToTargetWithPositionsAndRotations` calls (replaced by LowVisibility): calls, time, repeated arguments, sample callers (`[vis]` lines). |
| `LoopTimerSeconds` (30), `LoopTimerTopN` | Main-thread time per frame by Unity player-loop system: script `Update`/`LateUpdate`, physics, animation, UI canvases, rendering, GPU wait (`[loop]` lines with average, 95th percentile and worst frame). `0` = off. |
| `LoadDiagnostics` | One `[load]` line per second during save load: loads by type, batches walked, time in `UpdateRequests` and `CheckRequestsComplete`, GCs. |
| `TimeAutoFixers`, `TimeLocalSettingsReads` | Per-delegate timings of CustomComponents auto-fixers and CustomLocalSettings readers. |
| `DelayedCallCensus` | When the salvage screen opens (and on Ctrl+Shift+D with `Profile` on): Unity's pending coroutines and Invokes grouped by script, coroutine and GameObject root (`[delayed]` lines). Only for this game's `UnityPlayer.dll` build. |
| `SaveProfile` | Per-type serialization cost of each save (`[save]` lines). |
| `QuitWatch` | Timeline of background save writes and quitting (`[quit]` lines). |
| `UnloadDiagnostics` | Every asset unload request and the long frames after it (`[unload]` lines). |
| `TransitionWatch`, `TransitionWatchShotSeconds` | Loading-screen state (`[screen]` lines) and a screenshot every N seconds of a transition to `reports/screens/`. |
| `HeapCensus`, `HeapCensusHotkey` (Ctrl+Shift+H), `HeapCensusScenes`, `HeapCensusSceneDelaySeconds`, `HeapCensusPathTypes`, `HeapCensusPathShapes` | Type histogram and first-reach bytes per root to `reports/<time>_heap.txt`, on the hotkey or once per listed scene. A "collector view" section walks the collector's own heap blocks and mark bits: per kind marked, reached and kept-but-not-reached MB, the biggest unreached types and objects, and free slots by size. Freezes the game 10–30 s. |
| `GcStatsLog` | Boehm verbose statistics per collection to `reports/<time>_gc.log`. Ctrl+Shift+G runs a collection with a census of GC-handle targets (`[gchandles]`), a full `GC_dump` and `RootScan` (heap objects of 32 MB or more and every word pointing into them, `[rootscan]`). Addresses are for this exact `mono-2.0-bdwgc.dll` build and are checked before use. |
| `GcDumpAfterUsedMb` | Once per session, the same dump after the first collection that leaves at least this many MB in use. `0` = off. |

## Profiler

With `"Profile": true` PerfeBT times main-thread method calls with Harmony prefix/finalizer pairs.

- Every Harmony prefix/postfix/finalizer/transpiled method of every mod is wrapped (`InstrumentHarmonyPatches`), so cost is attributed per mod.
- Additional types are selected by regex:
  - `TypePatterns`: Assembly-CSharp full type names, instrumented at `FinishedLoading`. Types with a static constructor are skipped, because patching can trigger it early (unless `InstrumentSafeCctorTypes` and `CctorScan` judge it side-effect free).
  - `LateTypePatterns`: `"^Assembly:Full.Type.Name"`, instrumented by the main-menu rescan (`RescanDelaySeconds` later).
  - `InitTypePatterns`: same form, instrumented in PerfeBT's `Init` to time the rest of mod loading, for example ModTek's manifest processing. Written as the `startup` report.
  - `AssemblyPatterns`: whole assemblies. `ExcludePatterns`: excluded `Type::Method`.
  - Methods with an IL body under `MinIlBytes` (patch methods: `MinPatchIlBytes`) are skipped.
- Frames longer than `HitchMs` (150) are logged with a `HitchTopN` breakdown and the instrumented/uninstrumented split.
- A report is written to `reports/` per scene change and after each busy period (`AutoSegmentCalmSeconds` calm, at least `AutoSegmentMinBusyMs` of hitches): exclusive and inclusive time per method (`TopN` rows), call counts, owners, off-thread calls and the hitch log.

Overhead is roughly 1–2 µs per instrumented call. Patterns that hit per-frame or per-member methods inflate both wall time and the numbers. Never instrument `HBS.Util.Serialization::Put*`, `Statistic::Save` or `Variant::Save`, which run millions of times per save.

## Source layout

Folders are named after the code a fix patches, not where it might go. Fixes in the first three folders patch only vanilla code; that does not make them generic, since several only matter at RogueTech's scale (see [ModTek-upstream-proposals.md](ModTek-upstream-proposals.md)). They depend on nothing else in PerfeBT except `Log` (and each other within a folder); settings arrive as parameters from `Fixes.cs`, and extensions come in through hooks (`UnloadGate.AddSite`, `LoadGcPause.AfterCollect`, `LineOfSightTrace.RegisterMaskSource`).

- `src/Game/`: fixes for BattleTech's own code (`Assembly-CSharp`, HBS libraries, the game's SQLite access).
- `src/Unity/`: fixes for the Unity engine: Unity UI (`LayoutFastPath`, `UiListPoolTrim`), native `UnityPlayer.dll` internals (`DelayedStartIndex`, `UnityPlayerBuild`) and process shutdown (`ExitFast`).
- `src/Mono/`: the Mono runtime's garbage collector (`LoadGcPause`) and its native API (`MonoApi`), plus process memory counters (`ProcMem`).
- `src/RogueTech/`: fixes for specific RogueTech mods (CustomComponents, CustomUnits, CustomFilters, CustomMaps, CustomAmmoCategories, MissionControl, IRBTModUtils, LowVisibility). They use the mods' members directly and run only through `Fixes.ApplyGuarded`, after `Fixes.Precompile` has compiled their patch classes, so a missing or changed mod disables the fix instead of PerfeBT. Fixes that skip quietly when a mod is absent check `ModAssemblies.Loaded` first.
- `src/`: diagnostics, the profiler, settings and the wiring in `Fixes.cs`.

All code is compiled against publicized assemblies (see [BUILDING.md](BUILDING.md#publicizer)): private game and mod members are read and called directly. Reflection is left only where it is the point (heap census, static-constructor scan, the profiler's type patterns), for compiler-generated members without a C# name (iterator `MoveNext`, closure classes), for transpiler operands, for the LowVisibility IL hash checks, for the non-publicized Google.Protobuf (`ByteString.AttachBytes`), and for mods PerfeBT does not build against (CustomAmmoCategories' private assembly, StrategicOperations' garrison state).
