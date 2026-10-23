# Findings

These numbers were profiled on a RogueTech career save: about 6.9k MechDefs, 9.5k ChassisDefs and 35k DataLoader files. The machine runs Windows with Unity 2018.4 / Mono. All numbers are wall time on the main thread. Rows marked "fixed" are addressed by a PerfeBT setting; the others are open. How each fix works is in [HOW-IT-WORKS.md](HOW-IT-WORKS.md).

## Save load

| Spot | Cost | Status |
|---|---|---|
| DataManager trickles ~9.5k ChassisDef dependency loads at ~10 per frame (`MaxConcurrentLoadsLight`). Each frame first re-walks thousands of waiting batches. | ~43 s window | fixed: `SaveLoadMaxLightLoads` |
| `DataLoader.LoadResource(..., monitor: true)` scans a list of every loaded file per load. Its consumer, `CheckFiles`, is unreachable. | O(n²), seconds | fixed: `FixDataLoaderMonitoring` |
| HBS JSON deserializer: `Type.GetInterfaces`, `GetGenericArguments` and `Enumerable.Contains<Type>` per member, plus `MemberInfoHelper.IsValueType`. | 45.4 → 38.8 s window | fixed: `FixJsonReflection` |
| `Tags_MDDExtenstions.GetTagsForSet`: `COLLATE NOCASE` filter with only BINARY indexes, so a full scan of 432k `TagSetTag` rows per call. | ~11 ms per call | fixed: `FixTagSetIndex` |
| CustomComponents `TagUnitType.IsThisType` builds a new TagSet union per unit type per mech (243k unions). | ~5 s | fixed: `FixUnitTypeTags` |
| CustomComponents `DefaultFixer.FixMechs`: 5.3M `IsCategory` enumerations, allocating `DoDefaultsChange`, sort comparer lookups, string-building duplicate check. | 9.8 s → 1.75 s | fixed: `FixCategoryCache`, `FixDefaultsChangeFast`, `FixInventorySortFast`, `FixPendingAdjustScan` |
| CustomUnits ChassisDef prefix | ~3.5 s | open |
| `CheckRequestsComplete` / load tail | ~1–2 s | open |
| `CreateRef`, `CreateDefaultRecord`, CustomDeploy `UpdateRequests` | small per call, many calls | not worth a fix |

Overall defs window: ~73 s → 53.6 s (throttle) → 44.9 s (GC headroom env var) → 38.8 s (JSON reflection) → 31.8 s (DefaultFixer fixes; 0.7.0, profiler off).

## Startup

| Spot | Cost | Status |
|---|---|---|
| `UnityGameInstance.CheckInternetConnectivity`: synchronous HTTP check with a 5 s timeout | 1.5–5 s | fixed: `SkipInternetCheck` |
| Mod `Init`s total ~16 s: CustomUnits 1.9, CustomAmmoCategories 1.7, MissionControl 1.5 s | | open |
| ModTek manifest processing | ~9 s | open; see [ModTek-upstream-proposals.md](ModTek-upstream-proposals.md) |
| CustomLocalSettings `SaveManager` postfix (time spent in other mods' readers) | 2.5 s | diagnosed (`TimeLocalSettingsReads`) |

## Sim game UI

| Spot | Cost | Status |
|---|---|---|
| Contracts screen: CustomUnits `GetIntelMinimapSprite` loads map metadata and terrain and builds a 512×512 texture, even when the minimap will be hidden. | up to 2.4 s per new map | fixed: `FixContractIntelMinimap` |
| Mech lab open: CustomUnits `DataManager.Exists` postfix scans all asset names of a bundle per lookup | ~0.3 s | fixed: `FixPrefabBundleScan` |
| Mech lab open: CustomFilters `PreloadIcons` re-parses cached SVGs | 0.06–0.35 s | fixed: `FixMechLabIconPreload` |
| Unity UI `LayoutRebuilder.MarkLayoutForRebuild` and `ValidController` on hidden elements: 25–50k calls, ~20 µs each, on every mech lab enter/exit, store switch and Argo room change | store switch 2.2 → 0.3 s, mech lab ~2.4 → ~0.5 s | fixed: `LayoutFastPath` |
| After a long mission all Argo UI ran 10–30× slower until restart (`ValidController` 3 → 57–92 µs, `Graphic.Raycast` 15 → 435–606 µs). Unity UI's shared `ListPool<Component>` keeps the capacity of the largest masked panel, and `GetComponents(…, list)` visits every slot. | whole session | fixed: `UiListPoolTrim` |
| After-action and salvage screens: `CameraControl.Instance` searches the whole scene (~19 ms) every frame after the combat camera was unloaded; the audio engine asks every frame. Same for `BTSunlight` during map loads. | 50–80% of those screens' time, 3–5 s per loading screen | fixed: `FindObjectMissCache` |
| Salvage list refresh: hiding each list element cancels its pending `Start` with Unity's `DelayedCallManager::CancelCallDelayed`, which walks every pending call (~3,000 while the list is built). | 0.6–1.3 s per list build | experimental: `DelayedStartIndex` (off) |
| Store tab switch: list reset and repaint; `FillInFullMech` called up to 5× per frame with the same mech | 0.15–0.34 s | open, see below |
| `MechBayMechInfoWidget.Awake` | 16 × ~15 ms | open |
| `AAR_UnitStatusWidget.FillInData` | | open |

**Store detail panel is not idempotent.** Every `SG_Shop_FullMechDetailPanel.FillInFullMech` call adds a new inactive `settingsImg` and an active `icon` child to every loadout row. Nothing removes them, so they accumulate with each refresh. A dedupe fix was tried and removed: verify mode showed that skipping changes the panel's child count, so it is not result-identical. Which mod adds the children was not identified.

## Mission start and save

| Spot | Cost | Status |
|---|---|---|
| IRBTModUtils loads 19 portrait PNGs in `CombatGameState._Init` postfix, first mission per session | 2.4 s | moved to save load: `PreloadIrbtPortraits` |
| MissionControl unit generation: Dapper expands long `IN (...)` lists into one parameter per value | 5.5 s → 20 ms | fixed: `FixSqliteInList` |
| `GameInstance.Save` before launch. The save is 303 MB uncompressed: ~150 MB of star-system stats and ~150 MB of JwTweaks zero padding. | 4.06 s | open |
| Save buffers: the ~290 MB message grows by doubling and is copied by `ToArray`; the gzip stream starts at the full input size. Conservative GC scanning kept many of these buffers alive for the session. | ~3.7 GB allocated per save | fixed: `SaveAlloc` (~1.2 GB less, same bytes) |
| `CombatGameState` / `PreSerialization` part of the in-mission save | 2.2–4.4 s | open |
| VMMWSGC working-set trim after save | ~0.8 s | intentionally left |
| ModTek custom sound banks: `LoadBankExternal` prefix, 6 banks × ~440 ms | 2.6 s at first combat | open |
| MissionControl encounter setup | 2.1 s | open |
| CustomUnits pathing-grid patches | 1.3 s | open |
| EnvironmentalDesignMasks mood | 0.72 s | open |
| CustomDeploy `DataManager_UpdateRequests::Prefix` | 15 mid-mission hitches | open |

**Serialization primitives are not the save bottleneck.** A byte-identical fast path for `HBS.Util.Serialization.PutString` / `PutFloat` was verified over all 1,228,857 stat writes of a real save, but saved only ~10 ms under the game's Mono. The 3.7M calls themselves dominate, and these primitives are ~0.3 s of the 4.06 s. The rest is buffer copies, per-object serialization and the GC they trigger. Removing the save padding would change save bytes, so it was not done.

## Urban combat

Urban maps (about 560 destructible buildings) froze for 1–17 s at a time; about 150 s in one mission, only two of the freezes were GCs. Almost all of it is visibility: LowVisibility's `GetVisibilityToTargetWithPositionsAndRotations` (~0.15 ms per call) and, below it, `LineOfSight.bresenhamVisionTest` walking sight lines cell by cell to every building (~55% of the time) and `visCostOfCell` with its design-mask lookups (~35%). Visibility of units as targets costs only 5–50 ms.

| Spot | Cost | Status |
|---|---|---|
| Kills: `TurnDirector.RebuildVisCaches` → `Team.RebuildVisibilityCacheAllUnits` runs `UpdateVisibilityCacheForDeadUnit` once per dead unit of every team, each rebuilding every unit's visibility to every combatant; grows during the mission. 80% of the checks in the worst frame repeated identical arguments. | 9–17 s per kill | fixed: `VisibilityRebuildMemo` |
| Death rebuilds still left after that memo (no repeats within one rebuild) | 16 frames, 100 s in one mission (2.5–10 s each) | fixed: `LineOfSightRayMemo` + `LineOfSightRayMemoPersist`, `LineOfFireRayMemo`, `LowVisibilityLineOfFire`. One urban mission: 16 deaths took 0.17–2.2 s each, half under 0.4 s |
| LowVisibility `GetLineOfFireUncached` during death rebuilds (actor list copied and sorted per call, ~8 height walks per call) | 1.7 s of a 2.2 s death | fixed: `LineOfFireRayMemo`, `LowVisibilityLineOfFire` |
| Aura churn on death: CustomActivatableEquipment's `AbstractActor.HandleDeath` postfix (`CAEAuraHelper.RemoveAllAuras`) removes and reapplies auras on every unit; each `OnEffectEnd` runs `UpdateCacheReciprocal`. | 95 frames, 123 s in one mission; worst 18.4 s, 37,654 checks, 94% repeated | reduced by `LineOfSightRayMemo` (included in the death timings above); the churn itself is open |
| Movement stutter: CustomUnits `CustomMech.OnPositionUpdate` → `UpdateVisibilityCache` → `UpdateCacheReciprocal`, ~1,100 building checks | 350–690 ms per stall | reduced by `LineOfSightRayMemo`, `LineOfSightFastTrace`; not measured separately |
| Deploy: CustomUnits `DEPLOY` placeholder death | 10.1 s | partly covered by the line-of-sight fixes; open |
| Deploy: StrategicOperations `TurnDirector_StartFirstRound::Postfix`, `OnMovePhaseComplete` | 2.5 s, 3 s | open |

Why the sight-line memo gives identical results: see "Sight-line memo" in [HOW-IT-WORKS.md](HOW-IT-WORKS.md#sight-line-memo).

## Memory and exit

| Spot | Cost | Status |
|---|---|---|
| Boehm GC stops the game for 1–5 s every few GB of allocation | freezes in missions, Argo and menus | moved behind loading screens: `LoadGcPause`, `GcBudgetMb` (uses more RAM) |
| Quitting with the collector off: the finalizer thread never woke and `mono_gc_cleanup` waited its 40 s timeout | 50 s → 9 s | fixed in `LoadGcPause` |
| Unity/Mono native teardown of a 30–45 GB heap on quit | seconds | fixed: `ExitFast` |
| Redundant `UnloadUnusedAssets` during level transitions | ~3 s each | `UnloadSkipSites`, `UnloadDeferSites`, `EmptySceneAdditive` |
| Last mission's map grid, HUD and combat state kept in CustomAmmoCategories and MissionControl static fields | ~260 MB / 1.7M objects | fixed: `ReleaseCombatLeftovers` |

## Approaches that failed or were rejected

- **Hooking arbitrary mod methods by name** ("NamedMethods") crashed Mono (`0xc0000409`). Scanning all assemblies is also unsafe: patching a type can trigger its static constructor early. PerfeBT only instruments by explicit `^Assembly:Type` patterns, after the game has initialized, or types whose static constructor `CctorScan` judges side-effect free.
- **Profiling overhead.** Instrumenting the JSON deserializer or other per-member types adds seconds and distorts results; keep such patterns in `LateTypePatternsOff`. Instrumenting `LineOfSight` and `LOFCache` in combat inflated the urban freezes roughly 1.5–2×.
- **`debugLog: false` in CustomUnits** causes a black screen: `Log.M` is null and is dereferenced unguarded.
- **Store detail-panel dedupe** (`StoreTabsFix`): removed, see "Sim game UI".

## Notes for mod authors

- **CustomComponents:** `TagUnitType.IsThisType` and `DefaultFixer` are save-load hot paths across the whole MechDef set. The PerfeBT fixes in `UnitTypeTags.cs`, `CategoryCache.cs` and `DefaultFixerRest.cs` could be adopted directly.
- **CustomUnits:**
  - `LanceContractIntelWidget.Init` could skip `GetIntelMinimapSprite` when the minimap is hidden.
  - `DataManager_Exists.Postfix` could index bundle asset names once per bundle.
  - `Log.M` should not be null with `debugLog: false`.
  - `CustomMech.OnPositionUpdate` rebuilds visibility at points along every move; in urban maps each costs 350–690 ms.
- **CustomActivatableEquipment:** removing all auras in the `HandleDeath` postfix recomputes visibility once per removed effect for every affected unit (up to 18 s in one death); batching the removal and recomputing once would avoid it.
- **IRBTModUtils:** portraits could be loaded lazily or at sim-game start.
- **JwTweaks:** save zero padding doubles save size and write time.
