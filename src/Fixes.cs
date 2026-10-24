using System;
using System.Collections;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace PerfeBT
{
    // Targeted fixes. Game-method fixes are bound by name; fixes over mod types (UnitTypeTags, CategoryCache) go through
    // ApplyGuarded.
    internal static class Fixes
    {
        internal static void Apply(Harmony harmony, Settings s)
        {
            if (s.FixTagSetIndex)
                Patch(harmony, "BattleTech.Data.Tags_MDDExtenstions:GetTagsForSet", nameof(TagSetIndex.Prefix), typeof(TagSetIndex));
            if (s.SkipInternetCheck)
                Patch(harmony, "BattleTech.UnityGameInstance:CheckInternetConnectivity", nameof(InternetCheck.Prefix), typeof(InternetCheck));
            if (s.TimeLocalSettingsReads)
                Patch(harmony, "CustomSettings.ModsLocalSettingsHelper:Init", nameof(LocalSettingsReads.Prefix), typeof(LocalSettingsReads));
            if (s.TimeAutoFixers)
                Patch(harmony, "BattleTech.SimGameState:RespondToDefsLoadComplete", nameof(AutoFixers.Prefix), typeof(AutoFixers), Priority.First);
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

        private static void Patch(Harmony harmony, string target, string prefix, Type holder, int priority = Priority.Normal)
        {
            try
            {
                var original = AccessTools.Method(target);
                if (original == null)
                {
                    Log.Write($"[fix] {target} not found, skipped");
                    return;
                }
                harmony.Patch(original, prefix: new HarmonyMethod(holder.GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)) { priority = priority });
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
                    var actions = AccessTools.Field("CustomSettings.ModsLocalSettingsHelper:resetActions")?.GetValue(null) as IDictionary;
                    if (actions == null)
                        return;
                    foreach (DictionaryEntry e in actions)
                    {
                        var action = e.Value;
                        var readProp = AccessTools.Property(action.GetType(), "read");
                        if (!(readProp?.GetValue(action, null) is Action<string> read))
                            continue;
                        string id = e.Key as string;
                        readProp.SetValue(action, (Action<string>)(text =>
                        {
                            var sw = Stopwatch.StartNew();
                            try { read(text); }
                            finally { Log.Write($"[localsettings] read {id} ({text?.Length ?? 0} chars) {sw.ElapsedMilliseconds} ms"); }
                        }), null);
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
                    var shared = AccessTools.Field("CustomComponents.AutoFixer:Shared")?.GetValue(null);
                    var fixers = shared == null ? null : AccessTools.Field(shared.GetType(), "fixers")?.GetValue(shared) as IList;
                    if (fixers == null)
                    {
                        Log.Write("[autofix] CustomComponents.AutoFixer.Shared.fixers not found");
                        return;
                    }
                    for (int i = 0; i < fixers.Count; i++)
                        if (fixers[i] is Delegate d)
                            fixers[i] = Timed.Wrap(d, $"{d.Method.DeclaringType?.FullName}::{d.Method.Name}");
                    Log.Write($"[autofix] timing {fixers.Count} fixers");
                }
                catch (Exception e)
                {
                    Log.Write("[autofix] wrap failed: " + e.Message);
                }
            }
        }
    }

    // Wraps a one-argument void delegate of any type (e.g. AutoFixerDelegate(List<MechDef>)) with a timer.
    internal sealed class Timed<T>
    {
        private readonly Delegate inner;
        private readonly string name;

        internal Timed(Delegate inner, string name)
        {
            this.inner = inner;
            this.name = name;
        }

        public void Run(T arg)
        {
            var sw = Stopwatch.StartNew();
            try { inner.DynamicInvoke(arg); }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            }
            finally
            {
                int n = arg is ICollection c ? c.Count : -1;
                Log.Write($"[autofix] {name} {sw.ElapsedMilliseconds} ms (items {n})");
            }
        }
    }

    internal static class Timed
    {
        internal static Delegate Wrap(Delegate d, string name)
        {
            var arg = d.GetType().GetMethod("Invoke").GetParameters()[0].ParameterType;
            var holder = Activator.CreateInstance(typeof(Timed<>).MakeGenericType(arg), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { d, name }, null);
            return Delegate.CreateDelegate(d.GetType(), holder, holder.GetType().GetMethod("Run"));
        }
    }
}
