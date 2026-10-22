# Findings

These numbers were profiled on a RogueTech career save: about 6.9k MechDefs, 9.5k ChassisDefs and 35k DataLoader files. The machine runs Windows with Unity 2018.4 / Mono. All numbers are wall time on the main thread. Rows marked "fixed" are addressed by an PerfeBT setting; the others are open.

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
| Store tab switch: list reset and repaint; `FillInFullMech` called up to 5× per frame with the same mech | 0.15–0.34 s | open, see below |

**Store detail panel is not idempotent.** Every `SG_Shop_FullMechDetailPanel.FillInFullMech` call adds a new inactive `settingsImg` and an active `icon` child to every loadout row. Nothing removes them, so they accumulate with each refresh. A dedupe fix was tried and removed: verify mode showed that skipping changes the panel's child count, so it is not result-identical. Which mod adds the children was not identified.

## Mission start and save

| Spot | Cost | Status |
|---|---|---|
| IRBTModUtils loads 19 portrait PNGs in `CombatGameState._Init` postfix, first mission per session | 2.4 s | moved to save load: `PreloadIrbtPortraits` |
| `GameInstance.Save` before launch. The save is 303 MB uncompressed: ~150 MB of star-system stats and ~150 MB of JwTweaks zero padding. | 4.06 s | open |
| VMMWSGC working-set trim after save | ~0.8 s | intentionally left |
| ModTek custom sound banks: `LoadBankExternal` prefix, 6 banks × ~440 ms | 2.6 s at first combat | open |
| MissionControl encounter setup | 2.1 s | open |
| CustomUnits pathing-grid patches | 1.3 s | open |
| EnvironmentalDesignMasks mood | 0.72 s | open |

**Serialization primitives are not the save bottleneck.** A byte-identical fast path for `HBS.Util.Serialization.PutString` / `PutFloat` was verified over all 1,228,857 stat writes of a real save, but saved only ~10 ms under the game's Mono. The 3.7M calls themselves dominate, and these primitives are ~0.3 s of the 4.06 s. The rest is buffer copies, per-object serialization and the GC they trigger. Removing the save padding would change save bytes, so it was not done.

## Approaches that failed or were rejected

- **Hooking arbitrary mod methods by name** ("NamedMethods") crashed Mono (`0xc0000409`). Scanning all assemblies is also unsafe: patching a type can trigger its static constructor early. PerfeBT only instruments by explicit `^Assembly:Type` patterns, after the game has initialized, or types whose static constructor `CctorScan` judges side-effect free.
- **Profiling overhead.** Instrumenting the JSON deserializer or other per-member types adds seconds and distorts results; keep such patterns in `LateTypePatternsOff`.
- **`debugLog: false` in CustomUnits** causes a black screen: `Log.M` is null and is dereferenced unguarded.

## Notes for mod authors

- **CustomComponents:** `TagUnitType.IsThisType` and `DefaultFixer` are save-load hot paths across the whole MechDef set. The PerfeBT fixes in `UnitTypeTags.cs`, `CategoryCache.cs` and `DefaultFixerRest.cs` could be adopted directly.
- **CustomUnits:**
  - `LanceContractIntelWidget.Init` could skip `GetIntelMinimapSprite` when the minimap is hidden.
  - `DataManager_Exists.Postfix` could index bundle asset names once per bundle.
  - `Log.M` should not be null with `debugLog: false`.
- **IRBTModUtils:** portraits could be loaded lazily or at sim-game start.
- **JwTweaks:** save zero padding doubles save size and write time.
