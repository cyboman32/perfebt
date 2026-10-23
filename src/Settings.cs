using System.Collections.Generic;

namespace PerfeBT
{
    public sealed class Settings
    {
        // Master switches. Optimizations = false turns every fix below off (for "without optimizations" measurements);
        // PerformanceMonitoring = false turns every diagnostic off (profiler, reports, [load]/[unload]/[save]/[quit]/
        // [delayed]/[screen] lines, heap and GC dumps, verification runs). When a switch is true, the individual
        // settings decide. Applied once at startup by ApplyMasterSwitches.
        public bool Optimizations = true;
        public bool PerformanceMonitoring = false;

        // Profiler: instrumentation, hitch log and segment reports. Off = fixes only, no overhead.
        public bool Profile = true;

        // Fix: NOCASE index for Tags_MDDExtenstions.GetTagsForSet (full TagSetTag scan per call otherwise).
        public bool FixTagSetIndex = true;

        // Fix: skip the blocking startup HTTP connectivity check; the game is reported offline.
        public bool SkipInternetCheck = true;

        // Fix: CustomComponents TagUnitType.IsThisType rebuilds MechTags ∪ ChassisTags for every unit type; build it once
        // per UnitTypeDatabase.BuildUnitTypes call (~5 s on save load in RogueTech).
        public bool FixUnitTypeTags = true;

        // Fix: during CustomComponents DefaultFixer.FixMechs answer IsCategory(ref, id) from a per-def Category array
        // instead of re-enumerating the def's customs (~5.3M calls on save load in RogueTech).
        public bool FixCategoryCache = true;

        // Fix: during save load raise DataManager's light concurrent load limit (vanilla 10) so ~9.5k chassis dependency
        // loads are not trickled at ~10 per frame. 0 disables. Frames are bounded by LoadFrameBudgetMs.
        public int SaveLoadMaxLightLoads = 300;
        public int LoadFrameBudgetMs = 1000;
        // Heavy limit during save load (vanilla 1); 0 keeps the game's value.
        public int SaveLoadMaxHeavyLoads = 0;
        // Diagnostic: per-second load window stats ([load] lines).
        public bool LoadDiagnostics = true;

        // Fix: skip DataLoader's hot-reload file registration (an O(n) list scan per loaded file; its consumer never runs).
        public bool FixDataLoaderMonitoring = true;

        // Fix: cache per-Type reflection (GetInterfaces, GetGenericArguments, IsValueType) in the HBS JSON deserializer
        // that every def goes through (~4.5M member sets on save load).
        public bool FixJsonReflection = true;

        // Fixes: CustomComponents DefaultFixer on save load (allocation-free DoDefaultsChange, sorter key lookup once
        // per component, duplicate-adjust check without string building). Same changes, same order.
        public bool FixDefaultsChangeFast = true;
        public bool FixInventorySortFast = true;
        public bool FixPendingAdjustScan = true;

        // Fix: CustomUnits DataManager.Exists postfix scans every asset name of a bundle per call; index it per bundle.
        public bool FixPrefabBundleScan = true;
        // Verification: also run the original scan and log mismatches.
        public bool VerifyPrefabBundleScan = false;

        // Fix: CustomFilters mech lab opening re-parses icons already in the SVG cache.
        public bool FixMechLabIconPreload = true;

        // Fix: CustomUnits contract intel minimap is built (2+ s per new map) even when it will be hidden.
        public bool FixContractIntelMinimap = true;

        // Load IRBTModUtils' combat portraits (~2.4 s) during save load instead of at the first mission start.
        public bool PreloadIrbtPortraits = true;

        // Fix: Dapper expands `IN @list` into one parameter per value and Mono.Data.Sqlite matches parameter names with
        // linear scans, O(N²): ~620 ms per 1164-value lance query, 8× at launch with MissionControl. Long string lists are
        // written as SQL literals instead; same rows, same order.
        public bool FixSqliteInList = true;

        // Diagnostic: managed heap census (type histogram and first-reach bytes per root) to reports/<ts>_heap.txt on a
        // hotkey, and once per session for each listed scene. Freezes the game for 10-30 s while it runs.
        public bool HeapCensus = true;
        public string HeapCensusHotkey = "ctrl+shift+H";
        public List<string> HeapCensusScenes = new List<string>();
        public float HeapCensusSceneDelaySeconds = 30f;
        // Types whose shortest retention paths the census reports ("Full.Type.Name" or "Full.Type.Name>=bytes");
        // null = built-in list (combat map cells, Statistic, large byte arrays, actors, ...).
        public List<string> HeapCensusPathTypes = null;
        public int HeapCensusPathShapes = 10;

        // Garbage collection timing. Every Boehm collection marks the whole live heap (3-5.5 s in RogueTech) and cannot
        // be shortened, only moved. LoadGcPause: collector off during level transitions; one collection on the frame
        // after the target scene loads, behind the loading curtain (skipped when less than GcLoadCollectMinMb was
        // allocated since the last one). GcBudgetMb > 0: collector also off during play, collecting only after that
        // much growth (extra RAM up to about GcBudgetMb). GcBudgetMb = 0: normal collections in play; GcPauseMaxGrowthMb
        // caps growth during a load. GcMinFreeMemoryMb > 0: while the collector is off, also collect when the PC's
        // free RAM or free commit (RAM + page file) drops below that many MB. Memory timing only.
        public bool LoadGcPause = true;
        public long GcBudgetMb = 14000;
        public long GcLoadCollectMinMb = 2000;
        public long GcPauseMaxGrowthMb = 12000;
        public long GcMinFreeMemoryMb = 3000;

        // Diagnostic: during level transitions log curtain/camera/scene changes ([screen] lines) and save a screenshot
        // every TransitionWatchShotSeconds to reports/screens/<ts>/ (to locate the black screen).
        public bool TransitionWatch = true;
        public float TransitionWatchShotSeconds = 1f;

        // Fix (display only): keep the loading curtain visible while the "Loading" scene is up during level
        // transitions, instead of a black screen (its clear camera covered the UI, or nothing composited the UI texture).
        public bool LoadingScreenFix = true;

        // Diagnostic: per-type cost of the objects serialized by each save ([save] lines).
        public bool SaveProfile = true;

        // Fix (memory only): on the Argo / main menu, release the last mission's map grid, HUD and combat state that
        // CustomAmmoCategories and MissionControl keep in static fields ([leftovers] lines).
        public bool ReleaseCombatLeftovers = true;

        // Diagnostic: timeline of background save writes and quitting ([quit] lines).
        public bool QuitWatch = true;

        // Quit: after the managed shutdown (Unity quitting event, other mods' ProcessExit handlers, Steam shutdown) end
        // the process instead of Unity/Mono's native teardown of the heap. PlayerPrefs are saved first; skipped while a
        // background save request is still running.
        public bool ExitFast = true;

        // UI freezes: Unity UI's LayoutRebuilder.MarkLayoutForRebuild returns early for inactive rects under an
        // inactive parent, where the original marks nothing (called once per graphic when big panels are pooled).
        public bool LayoutFastPath = true;

        // Saves: the ~290 MB save message is written into a stream sized once instead of grown and copied, and the
        // gzip output stream starts at 1/32 of the input instead of its full size (~1.2 GB less per save; same bytes).
        public bool SaveAlloc = true;

        // UI 10-30x slower after a long mission (salvage, store, mech lab, mouse-over): empty lists in Unity UI's
        // shared ListPool<Component> keep the capacity of the largest masked panel, and every GetComponents into
        // them visits that whole capacity. Pooled lists above 128 slots drop their array after mask notifications.
        public bool UiListPoolTrim = true;

        // Salvage list refreshes: hiding a script that was enabled in the same frame cancels its pending Start call,
        // and Unity finds it by walking every pending delayed call (O(N^2) when a list of N components is built and
        // hidden at once). An index by object finds that call directly; same calls removed in the same way. Native
        // hooks into this game's UnityPlayer.dll build only. Every DelayedStartIndexVerifyEvery-th indexed cancel is
        // also checked against the full walk (0 = never); any mismatch switches the index off for the session.
        public bool DelayedStartIndex = false;
        public int DelayedStartIndexVerifyEvery = 64;

        // CameraControl.Instance and BTCustomRenderer.btSunlight search the whole scene on every access while their
        // object does not exist (after a mission: every frame, ~19 ms each). A miss is remembered until a scene loads,
        // CameraControl.Create runs or a BTSunlight is enabled. Every FindObjectMissCacheVerifyEvery-th skipped
        // search runs anyway (0 = never); finding an object there switches the cache off for the session.
        public bool FindObjectMissCache = true;
        public int FindObjectMissCacheVerifyEvery = 64;

        // Visibility cache rebuilds after a unit dies recompute the same unit-to-target visibility once per dead unit
        // per team (urban maps: 9-17 s freezes late in a mission). Within one rebuild call, a pair already computed is
        // reused. Every VisibilityRebuildMemoVerifyEvery-th reuse is computed again and compared (0 = never); a
        // difference switches the memo off for the session.
        public bool VisibilityRebuildMemo = true;
        public int VisibilityRebuildMemoVerifyEvery = 64;

        // Line-of-sight rays (LineOfSight.bresenhamVisionTest, a cell-by-cell walk over the map grid) traced again with
        // the same arguments while the map has not changed: within one CombatGameState.Update, unit death or visibility
        // rebuild, the result is remembered (urban maps: 94% of the rays in an 18 s death freeze were repeats). Dropped
        // for rays near a destroyed building and around map or design-mask changes. Every
        // LineOfSightRayMemoVerifyEvery-th reuse is traced again and compared (0 = never); a difference switches the
        // memo off for the session.
        public bool LineOfSightRayMemo = true;
        public int LineOfSightRayMemoVerifyEvery = 64;

        // Keeps the line-of-sight memo across calls until the map may change (building destroyed, cells repainted,
        // CustomAmmoCategories design-mask change, round end) instead of dropping it after each update, death or
        // rebuild: units rarely move between two deaths, so each death traced the same ~100,000 rays to the buildings
        // again. Only with LineOfSightRayMemo.
        public bool LineOfSightRayMemoPersist = true;

        // The same memo for LineOfSight.bresenhamHeightTest, the height-only walk behind line of fire: after the sight
        // lines were remembered, LowVisibility's GetLineOfFireUncached (0.13 ms per call, ~8 of these walks each, one
        // call per unit and target in every death rebuild) was most of a death freeze. Reads only cell heights and
        // building cells, so it is dropped only for destroyed buildings, repaints and scene loads; kept across calls
        // with LineOfSightRayMemoPersist. Every LineOfFireRayMemoVerifyEvery-th reuse is traced again and compared.
        public bool LineOfFireRayMemo = true;
        public int LineOfFireRayMemoVerifyEvery = 64;

        // LowVisibility's GetLineOfFireUncached prefix copies and sorts the whole actor list on every call but reads it
        // only for actor targets; with every ray cached that was still ~0.07 ms per call, ~0.8 s per urban death (one
        // call per unit and building). For building targets the rest of that prefix runs here without the list. Only
        // for the LowVisibility build it was written against; every LowVisibilityLineOfFireVerifyEvery-th call is
        // compared with LowVisibility's own result.
        public bool LowVisibilityLineOfFire = true;
        public int LowVisibilityLineOfFireVerifyEvery = 64;

        // The same ray walk without building the shared point list and walking it again, with the original's float
        // operations and calls. Every LineOfSightFastTraceVerifyEvery-th ray runs the original and compares (0 =
        // never); a difference switches it off for the session.
        public bool LineOfSightFastTrace = true;
        public int LineOfSightFastTraceVerifyEvery = 64;

        // Diagnostic: Boehm GC verbose statistics per collection in reports/<ts>_gc.log (marking, finalization, sweep).
        public bool GcStatsLog = false;

        // Diagnostic: once per session, right after the first PerfeBT collection leaving at least this much used,
        // GC_dump() into the same log: roots, heap sections and one line per heap block with its marked (live)
        // objects. Freezes the game for some seconds and writes a large file. 0 = off.
        public long GcDumpAfterUsedMb = 0;

        // Unity Resources.UnloadUnusedAssets costs ~3 s per call with a multi-GB managed heap. Diagnostic: log every
        // explicit request (by call site), scene load/unload and long frames after them ([unload] lines).
        public bool UnloadDiagnostics = true;
        // Explicit call sites (ids as logged) that are skipped when an unload already ran within UnloadCoalesceFrames.
        // Memory timing only: unreferenced assets are released by the next unload instead.
        public List<string> UnloadSkipSites = new List<string>
            { "CustomMaps.LoadCustom#1", "CustomMaps.LoadCustom#2", "LevelLoader.Load#1", "LevelLoader.Load#2" };
        public int UnloadCoalesceFrames = 30;
        // Sites whose unload is postponed UnloadDeferFrames frames and dropped if a Single scene load (which unloads
        // itself) starts first. DataManager.Clear is followed by the 'Loading' Single load in the same frame.
        public List<string> UnloadDeferSites = new List<string> { "DataManager.Clear" };
        public int UnloadDeferFrames = 3;
        // Load the intermediate "Empty" scene additively during level transitions instead of replacing the 'Loading'
        // scene with it: removes one asset unload per transition and the black screen between the two loading screens.
        public bool EmptySceneAdditive = true;

        // Diagnostic: log time spent in each CustomLocalSettings reader at startup.
        public bool TimeLocalSettingsReads = true;

        // Diagnostic: log time spent in each CustomComponents AutoFixer delegate on save load.
        public bool TimeAutoFixers = true;

        // Diagnostic: when the salvage screen opens (and on Ctrl+Shift+D with Profile on), list Unity's delayed calls
        // (suspended coroutines, pending Invokes) by behaviour, coroutine and GameObject root ([delayed] lines). Every
        // disabled behaviour walks this whole set, so a large set slows every SetActive(false).
        public bool DelayedCallCensus = true;

        // Diagnostic: time every Object.FindObjectOfType on the main thread by searched type, with sampled callers
        // ([findobj] lines). Singletons whose instance is gone search the whole scene on every access.
        public bool FindObjectWatch = true;

        // Diagnostic: frames with 1000+ LineOfSight.GetVisibilityToTargetWithPositionsAndRotations calls (LowVisibility
        // replaces it; urban missions froze up to 17 s in it): calls, time, repeated arguments, sample callers ([vis] lines).
        public bool VisibilityWatch = true;

        // Diagnostic: main-thread time per frame by Unity engine phase (script Update/LateUpdate, physics, animation,
        // UI canvases, rendering, GPU wait, ...): one [loop] line every LoopTimerSeconds (0 = off).
        public float LoopTimerSeconds = 30;
        public int LoopTimerTopN = 10;

        // Wrap every prefix/postfix/finalizer registered through Harmony by any mod, so cost is attributed per mod.
        public bool InstrumentHarmonyPatches = true;

        // Regexes on full type names (Assembly-CSharp only) whose methods are instrumented.
        public List<string> TypePatterns = new List<string>();

        // Regexes on "AssemblyName:FullTypeName", each starting with "^AssemblyName:", for types instrumented by the
        // main-menu rescan only, when the game is initialized and their static ctors have run (so the static ctor rule
        // is skipped). Diagnostic; hot types add overhead.
        public List<string> LateTypePatterns = new List<string>();

        // Same "^AssemblyName:FullTypeName" form, instrumented in PerfeBT's Init (static ctor rule applies) to time the
        // rest of mod loading, e.g. ModTek's manifest processing. Reported as the "startup" segment.
        public List<string> InitTypePatterns = new List<string>();

        // Regexes on assembly simple names whose types are instrumented completely (mod internals).
        public List<string> AssemblyPatterns = new List<string>();

        // Instrument types whose static ctor is judged side-effect free (CctorScan). Off by default: not yet verified
        // in game; the 0.3.0 startup crash was reproduced without it (NamedMethods), but it was enabled then too.
        public bool InstrumentSafeCctorTypes = false;

        // Regexes on "Type::Method" that are never instrumented.
        public List<string> ExcludePatterns = new List<string>();

        // Methods whose IL body is smaller than this are skipped (getters, trivial wrappers); keeps overhead low.
        public int MinIlBytes = 24;

        // Harmony patch methods are often tiny but still interesting; separate lower threshold.
        public int MinPatchIlBytes = 8;

        // A frame longer than this is logged as a hitch with its own top-N breakdown.
        public float HitchMs = 150f;

        // After a busy period (hitches), this many calm seconds trigger an automatic segment report.
        public float AutoSegmentCalmSeconds = 4f;

        // Busy periods shorter than this in total hitch time are not worth an automatic report.
        public float AutoSegmentMinBusyMs = 1000f;

        // Rows in each report table.
        public int TopN = 60;

        // Rows in each hitch breakdown.
        public int HitchTopN = 12;

        // Second instrumentation pass to catch patches applied after FinishedLoading.
        public float RescanDelaySeconds = 20f;

        // Profiler: per-method managed allocation (MB in segment reports, hitch lines and [alloc] windows). Costs two
        // more runtime calls per instrumented call. Accurate while the collector is off (LoadGcPause budget mode).
        public bool ProfileAlloc = true;

        // Every this many seconds an [alloc] line: MB allocated, MB/s, scene, combat, top allocating methods. 0 = off.
        public float AllocWindowSeconds = 60f;
        public int AllocWindowTopN = 8;

        // Turns off the individual settings covered by a master switch that is false.
        internal void ApplyMasterSwitches()
        {
            if (!Optimizations)
            {
                FixTagSetIndex = SkipInternetCheck = FixUnitTypeTags = FixCategoryCache = false;
                FixDataLoaderMonitoring = FixJsonReflection = false;
                FixDefaultsChangeFast = FixInventorySortFast = FixPendingAdjustScan = false;
                FixPrefabBundleScan = FixMechLabIconPreload = FixContractIntelMinimap = PreloadIrbtPortraits = false;
                FixSqliteInList = false;
                SaveLoadMaxLightLoads = SaveLoadMaxHeavyLoads = 0;
                LoadGcPause = false;
                LoadingScreenFix = ReleaseCombatLeftovers = ExitFast = LayoutFastPath = SaveAlloc = false;
                UiListPoolTrim = DelayedStartIndex = FindObjectMissCache = VisibilityRebuildMemo = false;
                LineOfSightRayMemo = LineOfSightRayMemoPersist = LineOfSightFastTrace = LineOfFireRayMemo = LowVisibilityLineOfFire = false;
                UnloadSkipSites = new List<string>();
                UnloadDeferSites = new List<string>();
                EmptySceneAdditive = false;
            }
            if (!PerformanceMonitoring)
            {
                Profile = ProfileAlloc = false;
                LoadDiagnostics = UnloadDiagnostics = false;
                HeapCensus = TransitionWatch = SaveProfile = QuitWatch = false;
                GcStatsLog = false;
                GcDumpAfterUsedMb = 0;
                TimeLocalSettingsReads = TimeAutoFixers = DelayedCallCensus = FindObjectWatch = VisibilityWatch = false;
                LoopTimerSeconds = 0;
                InstrumentHarmonyPatches = false;
                VerifyPrefabBundleScan = false;
            }
        }
    }
}
