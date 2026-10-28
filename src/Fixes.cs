using System;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace PerfeBT
{
    // Targeted fixes, compiled against publicized game and mod assemblies. Fixes over mod types go through
    // ApplyGuarded and precompile their patch classes, so a missing or changed mod only switches that fix off.
    internal static class Fixes
    {
        internal static void Apply(Harmony harmony, Settings s)
        {
            if (s.FixTagSetIndex)
                Patch(harmony, typeof(BattleTech.Data.Tags_MDDExtenstions), nameof(BattleTech.Data.Tags_MDDExtenstions.GetTagsForSet), new HarmonyMethod(typeof(TagSetIndex), nameof(TagSetIndex.Prefix)));
            if (s.SkipInternetCheck)
                Patch(harmony, typeof(BattleTech.UnityGameInstance), nameof(BattleTech.UnityGameInstance.CheckInternetConnectivity), new HarmonyMethod(typeof(InternetCheck), nameof(InternetCheck.Prefix)));
            if (s.TimeLocalSettingsReads)
                ApplyGuarded("LocalSettingsReads", () =>
                {
                    Precompile(typeof(LocalSettingsReads));
                    Patch(harmony, typeof(CustomSettings.ModsLocalSettingsHelper), nameof(CustomSettings.ModsLocalSettingsHelper.Init), new HarmonyMethod(typeof(LocalSettingsReads), nameof(LocalSettingsReads.Prefix)));
                });
            if (s.TimeAutoFixers)
                ApplyGuarded("AutoFixers", () =>
                {
                    Precompile(typeof(AutoFixers));
                    Patch(harmony, typeof(BattleTech.SimGameState), nameof(BattleTech.SimGameState.RespondToDefsLoadComplete), new HarmonyMethod(typeof(AutoFixers), nameof(AutoFixers.Prefix)) { priority = Priority.First });
                });
            if (s.FixUnitTypeTags)
                ApplyGuarded("UnitTypeTags", () => UnitTypeTags.Apply(harmony));
            if (s.FixCategoryCache)
                ApplyGuarded("CategoryCache", () => CategoryCache.Apply(harmony));
            if (s.SaveLoadMaxLightLoads > 0)
                ApplyGuarded("SaveLoadThrottle", () => SaveLoadThrottle.Apply(harmony, s.SaveLoadMaxLightLoads, s.SaveLoadMaxHeavyLoads,
                    s.LoadFrameBudgetMs, s.LoadDiagnostics));
            if (s.FixDataLoaderMonitoring)
                ApplyGuarded("DataLoaderMonitoring", () => DataLoaderMonitoring.Apply(harmony));
            if (s.FixJsonReflection)
                ApplyGuarded("JsonReflection", () => JsonReflection.Apply(harmony));
            if (s.FixDefaultsChangeFast)
                ApplyGuarded("DefaultsChangeFast", () => DefaultsChangeFast.Apply(harmony));
            if (s.FixInventorySortFast)
                ApplyGuarded("InventorySortFast", () => InventorySortFast.Apply(harmony));
            if (s.FixPendingAdjustScan)
                ApplyGuarded("PendingAdjustScan", () => PendingAdjustScan.Apply(harmony));
            if (s.FixPrefabBundleScan)
            {
                PrefabBundleScanCache.Verify = s.VerifyPrefabBundleScan;
                ApplyGuarded("PrefabBundleScanCache", () => PrefabBundleScanCache.Apply(harmony));
            }
            if (s.FixMechLabIconPreload)
                ApplyGuarded("MechLabIconPreload", () => MechLabIconPreload.Apply(harmony));
            if (s.FixContractIntelMinimap)
                ApplyGuarded("ContractIntelMinimap", () => ContractIntelMinimapFix.Apply(harmony));
            if (s.PreloadIrbtPortraits)
                ApplyGuarded("IrbtPortraitPreload", () => IrbtPortraitPreload.Apply(harmony));
            if (s.FixSqliteInList)
                ApplyGuarded("UnitGenFix", () => UnitGenFix.Apply(harmony));
            if (s.HeapCensus)
            {
                HeapCensus.Hotkey = s.HeapCensusHotkey;
                HeapCensus.Scenes = s.HeapCensusScenes;
                HeapCensus.SceneDelaySeconds = s.HeapCensusSceneDelaySeconds;
                if (s.HeapCensusPathTypes != null)
                    HeapCensus.PathTypes = s.HeapCensusPathTypes;
                HeapCensus.PathShapes = s.HeapCensusPathShapes;
                ApplyGuarded("HeapCensus", () => HeapCensus.Apply(harmony));
            }
            if (s.GcStatsLog || s.GcDumpAfterUsedMb > 0)
                ApplyGuarded("GcDump", () =>
                {
                    GcDump.Apply(Path.Combine(Path.GetDirectoryName(typeof(GcDump).Assembly.Location) ?? ".", "reports"),
                        s.GcStatsLog, s.GcDumpAfterUsedMb);
                    LoadGcPause.AfterCollect = GcDump.AfterCollect;
                });
            if (s.LoadGcPause)
            {
                LoadGcPause.Configure(s.GcBudgetMb, s.GcPauseMaxGrowthMb, s.GcLoadCollectMinMb, s.GcMinFreeMemoryMb);
                ApplyGuarded("LoadGcPause", () => LoadGcPause.Apply(harmony));
            }
            if (s.UnloadDiagnostics || s.UnloadSkipSites.Count > 0 || s.UnloadDeferSites.Count > 0 || s.EmptySceneAdditive)
            {
                UnloadGate.Configure(s.UnloadDiagnostics, s.UnloadSkipSites, s.UnloadCoalesceFrames,
                    s.UnloadDeferSites, s.UnloadDeferFrames, s.EmptySceneAdditive);
                ApplyGuarded("UnloadGate", () =>
                {
                    CustomMapsUnloadSites.Register();
                    UnloadGate.Apply(harmony);
                });
            }
            if (s.LoadingScreenFix)
                ApplyGuarded("LoadingScreenFix", () => LoadingScreenFix.Apply(harmony));
            if (s.SaveProfile)
                ApplyGuarded("SaveProfile", () => SaveProfile.Apply(harmony));
            if (s.ReleaseCombatLeftovers)
                ApplyGuarded("ReleaseCombatLeftovers", CombatLeftovers.Apply);
            if (s.QuitWatch)
                ApplyGuarded("QuitWatch", () => QuitWatch.Apply(harmony));
            if (s.ExitFast)
                ApplyGuarded("ExitFast", () => ExitFast.Apply(harmony));
            if (s.LayoutFastPath)
                ApplyGuarded("LayoutFastPath", () => LayoutFastPath.Apply(harmony));
            if (s.SaveAlloc)
            {
                ApplyGuarded("SaveAlloc.Serialize", () => SaveAlloc.ApplySerialize(harmony));
                ApplyGuarded("SaveAlloc.Compress", () => SaveAlloc.ApplyCompress(harmony));
            }
            if (s.ReleaseSavedStream)
                ApplyGuarded("ReleaseSavedStream", () => SavedStreamRelease.Apply(harmony));
            if (s.SaveSerializerLookups)
                ApplyGuarded("SaveSerializerLookups", () => SaveSerializerLookups.Apply(harmony, s.SaveSerializerLookupsVerifyEvery));
            if (s.SaveSerializerCopies)
                ApplyGuarded("SaveSerializerCopies", () => SaveSerializerCopies.Apply(harmony));
            if (s.UiListPoolTrim)
                ApplyGuarded("UiListPoolTrim", () => UiListPoolTrim.Apply(harmony));
            if (s.TransitionWatch)
            {
                TransitionWatch.ShotSeconds = s.TransitionWatchShotSeconds;
                ApplyGuarded("TransitionWatch", () => TransitionWatch.Apply(harmony));
            }
            if (s.DelayedCallCensus)
                ApplyGuarded("DelayedCallCensus", () => DelayedCallCensus.Apply(harmony));
            if (s.FindObjectWatch)
                ApplyGuarded("FindObjectWatch", () => FindObjectWatch.Apply(harmony));
            if (s.VisibilityWatch)
                ApplyGuarded("VisibilityWatch", () => VisibilityWatch.Apply(harmony));
            if (s.LoopTimerSeconds > 0)
                ApplyGuarded("LoopTimer", () => LoopTimer.Apply(s.LoopTimerSeconds, s.LoopTimerTopN));
            if (s.FindObjectMissCache)
                ApplyGuarded("FindObjectMissCache", () => FindObjectMissCache.Apply(harmony, s.FindObjectMissCacheVerifyEvery));
            if (s.VisibilityRebuildMemo)
                ApplyGuarded("VisibilityRebuildMemo", () => VisibilityRebuildMemo.Apply(harmony, s.VisibilityRebuildMemoVerifyEvery));
            if (s.LineOfSightRayMemo)
                ApplyGuarded("LineOfSightRayMemo", () =>
                {
                    CacDesignMasks.Apply(harmony);
                    LineOfSightTrace.ApplyMemo(harmony, s.LineOfSightRayMemoVerifyEvery, s.LineOfSightRayMemoPersist);
                });
            if (s.LineOfSightFastTrace)
                ApplyGuarded("LineOfSightFastTrace", () => LineOfSightTrace.ApplyFastTrace(harmony, s.LineOfSightFastTraceVerifyEvery));
            if (s.LineOfFireRayMemo)
                ApplyGuarded("LineOfFireRayMemo", () => LineOfSightTrace.ApplyHeightMemo(harmony, s.LineOfFireRayMemoVerifyEvery, s.LineOfSightRayMemoPersist));
            if (s.LowVisibilityLineOfFire)
                ApplyGuarded("LowVisibilityLineOfFire", () => LowVisibilityLineOfFire.Apply(harmony, s.LowVisibilityLineOfFireVerifyEvery));
            if (s.LowVisibilitySpotMemo)
                ApplyGuarded("LowVisibilitySpotMemo", () => LowVisibilitySpotMemo.Apply(harmony, s.LowVisibilitySpotMemoVerifyEvery));
            // After the census: it checks the original bytes of CancelCallDelayed, which this detours.
            if (s.DelayedStartIndex)
                ApplyGuarded("DelayedStartIndex", () => DelayedStartIndex.Apply(s.DelayedStartIndexVerifyEvery));
        }

        // Fixes that reference mod types directly run behind a guard: a missing or changed mod surfaces here as a
        // type-load/missing-member error instead of breaking PerfeBT.
        private static void ApplyGuarded(string name, Action apply)
        {
            try
            {
                apply();
                Log.Write($"[fix] applied {name}");
            }
            catch (Exception e)
            {
                Log.Write($"[fix] failed {name}: {e}");
            }
        }

        // Patch bodies are compiled on their first call, which for mod members would move a missing-member error from
        // startup into play. A fix whose bodies use mod members directly calls this before patching, so the error
        // surfaces inside ApplyGuarded and the fix stays off. Generic methods are skipped (they need an instantiation).
        internal static void Precompile(params Type[] holders)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (var holder in holders)
                foreach (var m in holder.GetMethods(all))
                {
                    if (m.IsAbstract || m.ContainsGenericParameters)
                        continue;
                    RuntimeHelpers.PrepareMethod(m.MethodHandle);
                    m.MethodHandle.GetFunctionPointer();   // Mono compiles here
                }
        }

        private static void Patch(Harmony harmony, Type type, string method, HarmonyMethod prefix)
        {
            var target = $"{type.FullName}:{method}";
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    Log.Write($"[fix] {target} not found, skipped");
                    return;
                }
                harmony.Patch(original, prefix: prefix);
                Log.Write($"[fix] patched {target}");
            }
            catch (Exception e)
            {
                Log.Write($"[fix] failed {target}: {e}");
            }
        }

        // Diagnostic: CustomLocalSettings' SaveManager ctor postfix spent 2.5 s inside other mods' settings readers.
        // Wrap every registered reader with a timer so the slow mod is named in PerfeBT.log.
        internal static class LocalSettingsReads
        {
            private static bool wrapped;

            internal static void Prefix()
            {
                if (wrapped)
                    return;
                wrapped = true;
                try
                {
                    var actions = CustomSettings.ModsLocalSettingsHelper.resetActions;
                    if (actions == null)
                        return;
                    foreach (var e in actions)
                    {
                        var action = e.Value;
                        if (!(action?.read is Action<string> read))
                            continue;
                        string id = e.Key;
                        action.read = text =>
                        {
                            var sw = Stopwatch.StartNew();
                            try { read(text); }
                            finally { Log.Write($"[localsettings] read {id} ({text?.Length ?? 0} chars) {sw.ElapsedMilliseconds} ms"); }
                        };
                    }
                }
                catch (Exception e)
                {
                    Log.Write("[localsettings] wrap failed: " + e.Message);
                }
            }
        }

        // Diagnostic: CustomComponents' RespondToDefsLoadComplete prefix ran 13.8 s on save load, inside the
        // AutoFixer delegates registered by CC and MechEngineer. Wrap each delegate in CustomComponents.AutoFixer.Shared
        // with a timer, right before CC's prefix runs. Delegate wrapping avoids patching mod methods
        // (instrumenting them via Harmony crashed Mono at startup).
        internal static class AutoFixers
        {
            private static bool wrapped;

            internal static void Prefix()
            {
                if (wrapped)
                    return;
                wrapped = true;
                try
                {
                    var fixers = CustomComponents.AutoFixer.Shared?.fixers;
                    if (fixers == null)
                    {
                        Log.Write("[autofix] CustomComponents.AutoFixer.Shared.fixers not found");
                        return;
                    }
                    for (int i = 0; i < fixers.Count; i++)
                    {
                        var inner = fixers[i];
                        if (inner == null)
                            continue;
                        var name = $"{inner.Method.DeclaringType?.FullName}::{inner.Method.Name}";
                        fixers[i] = list =>
                        {
                            var sw = Stopwatch.StartNew();
                            try { inner(list); }
                            finally { Log.Write($"[autofix] {name} {sw.ElapsedMilliseconds} ms (items {list?.Count ?? -1})"); }
                        };
                    }
                    Log.Write($"[autofix] timing {fixers.Count} fixers");
                }
                catch (Exception e)
                {
                    Log.Write("[autofix] wrap failed: " + e.Message);
                }
            }
        }
    }
}
