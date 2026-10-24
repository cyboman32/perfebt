using System;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PerfeBT
{
    // The Boehm collector in this Mono has no incremental or parallel mode: every collection marks the whole live heap
    // (3-5.5 s in RogueTech), and one runs each time ~5 GB has been allocated. A collection cannot be made shorter,
    // only moved, so this controls when they happen (mono_unity_gc_disable/enable is a nesting counter):
    //   - From the "Loading" scene request the collector is off; one collection runs on the frame after the target
    //     scene's Single load completes, while the loading curtain is still up.
    //   - With a play budget (GcBudgetMb) the collector also stays off during play; a collection runs only when used
    //     memory has grown that much since the last one. Without one, normal collection resumes after the end-of-load
    //     collection, and GcPauseMaxGrowthMb caps growth during a load the same way.
    //   - With a low-memory threshold (GcMinFreeMemoryMb), while the collector is off, a collection also runs when the
    //     PC's free RAM or free commit (RAM + page file) drops below it, at most once per 1000 MB of further growth.
    // Settings: 0 = derived from the PC's installed RAM (Auto), a negative value = off, a positive value = used as is.
    // Memory timing only: objects become collectable at the same points; they are only freed later. While the
    // collector is off, explicit GC.Collect calls from other mods do nothing.
    internal static class LoadGcPause
    {
        // Resolved values: BudgetMb <= 0 = normal collections in play, MaxGrowthMb <= 0 = no cap during a load,
        // MinFreeMemoryMb <= 0 = no low-memory check.
        internal static long BudgetMb;
        internal static long MaxGrowthMb;
        internal static long LoadCollectMinMb;
        internal static long MinFreeMemoryMb;
        private static string configLog = "";

        internal struct GcValues
        {
            public long BudgetMb, MaxGrowthMb, MinFreeMemoryMb;
        }

        // Values by installed RAM (the module size, e.g. 32768 for 32 GB; Windows sees a little less). RogueTech itself
        // takes 10-16 GB and Windows plus other programs a few more, so the play budget is what is left above 24000 MB,
        // capped at 14000 (an urban mission collected twice in play with 8000). At 24 GB or less there is no room for a
        // budget: normal collections in play. A single load may grow by a quarter of RAM (4000-12000). The low-memory
        // threshold is 2000 MB under 24 GB, else 3000. totalRamMb <= 0 (unknown) gets the smallest PC's values.
        internal static GcValues Auto(long totalRamMb)
        {
            if (totalRamMb <= 0)
                totalRamMb = 16 * 1024;
            return new GcValues
            {
                BudgetMb = Math.Min(Math.Max(totalRamMb - 24000, 0), 14000) / 1000 * 1000,
                MaxGrowthMb = Math.Min(Math.Max(totalRamMb / 4 / 1000 * 1000, 4000), 12000),
                MinFreeMemoryMb = totalRamMb < 24 * 1024 ? 2000 : 3000,
            };
        }

        internal static void Configure(long budgetMb, long maxGrowthMb, long loadCollectMinMb, long minFreeMemoryMb)
        {
            var ramMb = TotalRamMb();
            var auto = Auto(ramMb);
            BudgetMb = Pick(budgetMb, auto.BudgetMb);
            MaxGrowthMb = Pick(maxGrowthMb, auto.MaxGrowthMb);
            LoadCollectMinMb = loadCollectMinMb;
            MinFreeMemoryMb = Pick(minFreeMemoryMb, auto.MinFreeMemoryMb);
            configLog = $"[gc] RAM {(ramMb > 0 ? ramMb + " MB" : "unknown")}: budget {Describe(BudgetMb, budgetMb)}, " +
                        $"load growth cap {Describe(MaxGrowthMb, maxGrowthMb)}, low-memory threshold {Describe(MinFreeMemoryMb, minFreeMemoryMb)}";
        }

        private static long Pick(long configured, long auto) => configured == 0 ? auto : Math.Max(configured, 0);

        private static string Describe(long value, long configured) =>
            (value > 0 ? value + " MB" : "off") + (configured == 0 ? " (auto)" : " (set)");

        private static float nextMemoryCheck;
        private static long lowMemoryUsedMb = -1; // used after the last low-memory collection

        private static bool off;          // our disable is in effect
        private static bool inLoad;
        private static int collectFrame = -1;
        private static string loadTarget = "?";
        private static long baseUsedMb;   // used after our last collection (or when the collector went off)
        private static float loadStart;

        internal static void Apply(Harmony harmony)
        {
            // Throws if the runtime lacks the Unity GC switch (DllNotFound/EntryPointNotFound), before anything is patched.
            MonoApi.mono_unity_gc_is_disabled();

            var prefix = new HarmonyMethod(AccessTools.Method(typeof(LoadGcPause), nameof(LoadPrefix)));
            var single = AccessTools.Method(typeof(SceneManager), "LoadSceneAsync", new[] { typeof(string) })
                         ?? throw new MissingMethodException("SceneManager.LoadSceneAsync(string)");
            var withMode = AccessTools.Method(typeof(SceneManager), "LoadSceneAsync", new[] { typeof(string), typeof(LoadSceneMode) })
                           ?? throw new MissingMethodException("SceneManager.LoadSceneAsync(string, LoadSceneMode)");
            harmony.Patch(single, prefix: prefix);
            harmony.Patch(withMode, prefix: prefix);

            SceneManager.sceneLoaded += OnSceneLoaded;
            var go = new GameObject("PerfeBT.LoadGcPause");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<LoadGcPauseClock>();

            Log.Write(configLog);
            if (BudgetMb > 0)
            {
                Off();
                Log.Write($"[gc] collector off during play; collections behind loading screens, every {BudgetMb} MB of growth " +
                          $"and below {MinFreeMemoryMb} MB free RAM/commit (used {baseUsedMb} MB)");
            }

            // Exit hung 40 s in mono_gc_cleanup: Mono's finalizer thread never woke from its semaphore while the
            // collector was left off. Turn it back on before the runtime shuts down.
            Application.quitting += () =>
            {
                inLoad = false;
                if (off)
                    On();
            };
        }

        private static void LoadPrefix(string __0)
        {
            try
            {
                if (__0 != "Loading" || inLoad)
                    return;
                inLoad = true;
                collectFrame = -1;
                loadTarget = "?";
                loadStart = Time.realtimeSinceStartup;
                if (!off)
                    Off();
                Log.Write($"[gc] level transition: collector off (used {UsedMb()} MB)");
            }
            catch (Exception e)
            {
                Log.Write("[gc] pause failed: " + e.Message);
            }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!inLoad || mode != LoadSceneMode.Single || scene.name == "Loading" || scene.name == "Empty")
                return;
            loadTarget = scene.name;
            collectFrame = Time.frameCount + 1;
        }

        internal static void Tick()
        {
            if (inLoad && collectFrame >= 0 && Time.frameCount >= collectFrame)
            {
                inLoad = false;
                collectFrame = -1;
                var grown = UsedMb() - baseUsedMb;
                if (BudgetMb <= 0 || grown >= LoadCollectMinMb)
                    Collect($"end of load '{loadTarget}' after {Time.realtimeSinceStartup - loadStart:0.0} s");
                else
                    Log.Write($"[gc] end of load '{loadTarget}': no collection, grown only {grown} MB since last");
                if (BudgetMb <= 0)
                    On();
                return;
            }
            if (!off)
                return;
            var cap = inLoad && BudgetMb <= 0 ? MaxGrowthMb : BudgetMb;
            var used = UsedMb();
            if (cap > 0 && used - baseUsedMb > cap)
                Collect($"growth {used - baseUsedMb} MB > {cap} MB");
            else if (MinFreeMemoryMb > 0 && Time.realtimeSinceStartup >= nextMemoryCheck)
            {
                nextMemoryCheck = Time.realtimeSinceStartup + 1f;
                if (lowMemoryUsedMb >= 0 && used - lowMemoryUsedMb < 1000)
                    return;
                FreeMemory(out var ramMb, out var commitMb);
                if (Math.Min(ramMb, commitMb) >= MinFreeMemoryMb)
                    return;
                Collect($"low memory: {ramMb} MB RAM, {commitMb} MB commit free < {MinFreeMemoryMb} MB");
                lowMemoryUsedMb = UsedMb();
            }
            else
                return;
            if (BudgetMb <= 0)
            {
                inLoad = false;
                On();
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalKb);

        // Installed RAM from the firmware tables; what Windows can use (TotalPhys) where those are unavailable (VMs).
        internal static long TotalRamMb()
        {
            try
            {
                if (GetPhysicallyInstalledSystemMemory(out var kb) && kb > 0)
                    return (long)(kb / 1024);
            }
            catch (EntryPointNotFoundException)
            {
            }
            var s = new MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MemoryStatusEx)) };
            return GlobalMemoryStatusEx(ref s) ? (long)(s.TotalPhys / 1048576) : 0;
        }

        private static void FreeMemory(out long ramMb, out long commitMb)
        {
            var s = new MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MemoryStatusEx)) };
            if (!GlobalMemoryStatusEx(ref s))
            {
                ramMb = commitMb = long.MaxValue;
                return;
            }
            ramMb = (long)(s.AvailPhys / 1048576);
            commitMb = (long)(s.AvailPageFile / 1048576);
        }

        private static void Collect(string reason)
        {
            var before = UsedMb();
            var heapBefore = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() / 1048576;
            var memBefore = ProcMem.Read();
            var sw = Stopwatch.StartNew();
            try
            {
                if (off)
                    MonoApi.mono_unity_gc_enable();
                GC.Collect();
                if (off)
                    MonoApi.mono_unity_gc_disable();
            }
            catch (Exception e)
            {
                Log.Write("[gc] collect failed: " + e.Message);
                return;
            }
            var ms = sw.ElapsedMilliseconds;
            var memAfter = ProcMem.Read();
            baseUsedMb = UsedMb();
            var sinceLast = lastCollectAt > 0 ? $"{Time.realtimeSinceStartup - lastCollectAt:0} s" : "n/a";
            lastCollectAt = Time.realtimeSinceStartup;
            bool combat;
            try { combat = BattleTech.UnityGameInstance.BattleTechGame?.Combat != null; }
            catch { combat = false; }
            // Page faults during the collection show how much of the heap had been trimmed out of the working set.
            Log.Write($"[gc] collected ({reason}) in {ms} ms; used {before} -> {baseUsedMb} MB, heap {heapBefore} MB; " +
                      $"working set {memBefore.WorkingSetMb} -> {memAfter.WorkingSetMb} MB, page faults {ProcMem.FaultsBetween(memBefore, memAfter)}, " +
                      $"private {memAfter.PrivateMb} MB; scene {SceneManager.GetActiveScene().name}, combat {(combat ? "yes" : "no")}, {sinceLast} since last");
            AfterCollect?.Invoke(baseUsedMb);
        }

        // Called after every collection with the used MB, while the mark bits still describe the live heap (GcDump).
        internal static Action<long> AfterCollect;

        // Full collection now, also while the collector is off (Ctrl+Shift+G heap dump).
        internal static void CollectNow(string reason) => Collect(reason);

        private static float lastCollectAt;

        // Runs action with the collector temporarily on (explicit collections by PerfeBT diagnostics).
        internal static void WithCollectorOn(Action action)
        {
            var wasOff = off;
            if (wasOff)
                MonoApi.mono_unity_gc_enable();
            try
            {
                action();
            }
            finally
            {
                if (wasOff)
                {
                    MonoApi.mono_unity_gc_disable();
                    baseUsedMb = UsedMb();
                }
            }
        }

        private static void Off()
        {
            MonoApi.mono_unity_gc_disable();
            off = true;
            baseUsedMb = UsedMb();
        }

        private static void On()
        {
            off = false;
            MonoApi.mono_unity_gc_enable();
            Log.Write($"[gc] collector back on (used {UsedMb()} MB)");
        }

        private static long UsedMb() => UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / 1048576;
    }

    internal sealed class LoadGcPauseClock : MonoBehaviour
    {
        private void Update() => LoadGcPause.Tick();
    }
}
