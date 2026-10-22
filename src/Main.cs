using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace PerfeBT
{
    public static class Main
    {
        private static string dir;
        private static Settings settings;
        private static Harmony harmony;

        // ModTek entry point.
        public static void Init(string directory, string settingsJSON)
        {
            dir = directory;
            Log.Open(Path.Combine(dir, "PerfeBT.log"));
            try
            {
                settings = JsonConvert.DeserializeObject<Settings>(settingsJSON ?? "{}") ?? new Settings();
            }
            catch (Exception e)
            {
                Log.Write("[init] bad settings, using defaults: " + e.Message);
                settings = new Settings();
            }
            settings.ApplyMasterSwitches();
            Prof.MainThreadId = Thread.CurrentThread.ManagedThreadId;
            harmony = new Harmony("io.perfebt.profiler");
            Prof.TrackAlloc = settings.Profile && settings.ProfileAlloc;
            Log.Write($"[init] optimizations={settings.Optimizations}, performance monitoring={settings.PerformanceMonitoring}; " +
                      $"main thread {Prof.MainThreadId}, profile={settings.Profile}, alloc={Prof.TrackAlloc}");
            if (settings.Profile && settings.InitTypePatterns.Count > 0)
            {
                startupStart = System.Diagnostics.Stopwatch.GetTimestamp();
                try { Instrumenter.Run(harmony, settings, "init"); }
                catch (Exception e) { Log.Write("[init] instrument: " + e); }
            }
        }

        private static long startupStart;

        // Everything timed between Init and FinishedLoading, written before the regular instrumentation starts.
        private static void StartupReport()
        {
            if (startupStart == 0)
                return;
            var seg = new SegmentStats { WallMs = Prof.Ms(System.Diagnostics.Stopwatch.GetTimestamp() - startupStart), RootMs = Prof.Ms(Prof.FrameRootTicks) };
            Report.Segment(Path.Combine(dir, "reports"), "startup", seg, settings.TopN);
            Prof.ResetSegment();
            Prof.ResetFrame();
        }

        // ModTek calls this after every mod ran Init, i.e. after their Harmony patches exist and every mod
        // assembly is loaded (fix targets in mod assemblies resolve), but before UnityGameInstance.Awake.
        public static void FinishedLoading(List<string> loadOrder)
        {
            if (settings.Profile)
                StartupReport();
            Fixes.Apply(new Harmony("io.perfebt.fixes"), settings);
            if (!settings.Profile)
                return;
            try
            {
                Instrumenter.Run(harmony, settings, "initial");
                var monitor = Monitor.Create(settings, Path.Combine(dir, "reports"));
                monitor.StartCoroutine(Rescan());
            }
            catch (Exception e)
            {
                Log.Write("[init] " + e);
            }
        }

        private static IEnumerator Rescan()
        {
            yield return new WaitForSecondsRealtime(settings.RescanDelaySeconds);
            try { Instrumenter.Run(harmony, settings, "rescan"); }
            catch (Exception e) { Log.Write("[rescan] " + e); }
        }
    }
}
