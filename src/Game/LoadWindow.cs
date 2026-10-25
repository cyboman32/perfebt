using System.Collections.Generic;
using BattleTech;
using BattleTech.Data;
using HarmonyLib;
using UnityEngine;

namespace PerfeBT
{
    // Save load (SimGameState.RequestDataManagerResources → RespondToDefsLoadComplete) requests every MechDef in one
    // batch; each MechDef then opens its own dependency batch for its ChassisDef. DataManager.UpdateRequests (CustomDeploy
    // replaces it with a copy) stops a frame once MaxConcurrentLoadsLight (10) light loads are active, and every frame
    // re-walks thousands of waiting batches first: ~9.5k chassis load at 7-10 per frame (~43 s, ~1,100 frames).
    // During that window the light limit is raised; PerfeBT's own per-frame time budget bounds the resulting frames,
    // because ModTek's budget only applies inside a single batch.
    internal static class SaveLoadThrottle
    {
        private static readonly AccessTools.FieldRef<DataManager, List<LoadRequest>> ActiveBatches =
            AccessTools.FieldRefAccess<DataManager, List<LoadRequest>>("activeLoadBatches");

        private static int maxLightLoads;
        private static int maxHeavyLoads;
        private static float budgetSeconds;
        private static int savedLimit = -1;  // >= 0 while the window is open
        private static int savedHeavyLimit;
        private static bool inUpdate;
        private static float updateStart;
        private static float windowStart;
        private static int budgetCuts;
        private static bool diagnostics;

        internal static void Apply(Harmony harmony, int maxLight, int maxHeavy, int frameBudgetMs, bool loadDiagnostics)
        {
            maxLightLoads = maxLight;
            maxHeavyLoads = maxHeavy;
            budgetSeconds = frameBudgetMs / 1000f;
            diagnostics = loadDiagnostics;
            harmony.Patch(AccessTools.Method(typeof(SimGameState), "RequestDataManagerResources"),
                prefix: new HarmonyMethod(typeof(SaveLoadThrottle), nameof(Open)));
            harmony.Patch(AccessTools.Method(typeof(SimGameState), "RespondToDefsLoadComplete"),
                prefix: new HarmonyMethod(typeof(SaveLoadThrottle), nameof(CloseOnDefsLoaded)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(DataManager), "UpdateRequests"),
                prefix: new HarmonyMethod(typeof(SaveLoadThrottle), nameof(UpdatePrefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(SaveLoadThrottle), nameof(UpdatePostfix)));
            harmony.Patch(AccessTools.Method(typeof(LoadRequest), "PopPendingRequest"),
                prefix: new HarmonyMethod(typeof(SaveLoadThrottle), nameof(PopPrefix)) { priority = Priority.First },
                postfix: diagnostics ? new HarmonyMethod(typeof(SaveLoadThrottle), nameof(PopPostfix)) { priority = Priority.Last } : null);
            if (diagnostics)
                harmony.Patch(AccessTools.Method(typeof(DataManager), "CheckRequestsComplete"),
                    prefix: new HarmonyMethod(typeof(SaveLoadThrottle), nameof(CheckPrefix)),
                    postfix: new HarmonyMethod(typeof(SaveLoadThrottle), nameof(CheckPostfix)));
        }

        private static void Open()
        {
            if (savedLimit >= 0)
                return;
            savedLimit = DataManager.MaxConcurrentLoadsLight;
            savedHeavyLimit = DataManager.MaxConcurrentLoadsHeavy;
            DataManager.SetMaxLightConcurrentLoads(maxLightLoads);
            if (maxHeavyLoads > 0)
                DataManager.SetMaxHeavyConcurrentLoads(maxHeavyLoads);
            windowStart = Time.realtimeSinceStartup;
            budgetCuts = 0;
            Log.Write($"[fix] save load: load limits light {savedLimit} -> {maxLightLoads}, heavy {savedHeavyLimit} -> {DataManager.MaxConcurrentLoadsHeavy}, frame budget {budgetSeconds * 1000:0} ms");
        }

        private static void Close(string why)
        {
            if (savedLimit < 0)
                return;
            DataManager.SetMaxLightConcurrentLoads(savedLimit);
            DataManager.SetMaxHeavyConcurrentLoads(savedHeavyLimit);
            Log.Write($"[fix] save load: load limits restored to light {savedLimit} heavy {savedHeavyLimit} ({why}) after {Time.realtimeSinceStartup - windowStart:0.0} s, {budgetCuts} budget cuts");
            savedLimit = -1;
            inUpdate = false;
        }

        private static void CloseOnDefsLoaded() => Close("defs loaded");

        private static void UpdatePrefix()
        {
            if (savedLimit < 0)
                return;
            inUpdate = true;
            updateStart = Time.realtimeSinceStartup;
        }

        // Postfixes run even though CustomDeploy's prefix skips the original UpdateRequests.
        private static void UpdatePostfix(DataManager __instance)
        {
            if (!inUpdate)
                return;
            inUpdate = false;
            var batches = ActiveBatches(__instance);
            if (diagnostics)
                Diag.EndFrame(batches, Time.realtimeSinceStartup - updateStart);
            if (batches.Count == 0)
                Close("no batches left");
        }

        // PopPendingRequest's only caller is UpdateRequests; a null result ends the current batch for this frame,
        // exactly as ModTek's own 2 s budget does.
        private static bool PopPrefix(ref DataManager.FileLoadRequest __result)
        {
            if (!inUpdate || Time.realtimeSinceStartup - updateStart < budgetSeconds)
                return true;
            budgetCuts++;
            __result = null;
            return false;
        }

        private static void PopPostfix(DataManager.FileLoadRequest __result)
        {
            if (inUpdate)
                Diag.Pop(__result);
        }

        private static void CheckPrefix(out float __state) => __state = savedLimit >= 0 ? Time.realtimeSinceStartup : -1f;

        private static void CheckPostfix(float __state)
        {
            if (__state >= 0f)
                Diag.checkSeconds += Time.realtimeSinceStartup - __state;
        }

        // Diagnostic: one log line per second of the window — frames, loads by weight class and type, batch states,
        // pop calls (batches walked), time in UpdateRequests and CheckRequestsComplete, GCs.
        private static class Diag
        {
            internal static float checkSeconds;
            private static float lastFlush, updateSeconds;
            private static int frames, popCalls, light, heavy, lastGc = -1, maxBatches, processing, awaiting;
            private static readonly Dictionary<string, int> heavyTypes = new Dictionary<string, int>();
            private static readonly Dictionary<string, int> types = new Dictionary<string, int>();

            internal static void Pop(DataManager.FileLoadRequest request)
            {
                popCalls++;
                if (request == null)
                    return;
                var type = request.ResourceType.ToString();
                types.TryGetValue(type, out var n);
                types[type] = n + 1;
                if (request.RequestWeight.AllowedWeight == 10)
                {
                    light++;
                    return;
                }
                heavy++;
                var key = $"{type}/w{request.RequestWeight.AllowedWeight}";
                heavyTypes.TryGetValue(key, out n);
                heavyTypes[key] = n + 1;
            }

            internal static void EndFrame(List<LoadRequest> batches, float seconds)
            {
                frames++;
                updateSeconds += seconds;
                if (batches.Count > maxBatches)
                    maxBatches = batches.Count;
                var now = Time.realtimeSinceStartup;
                if (now - lastFlush < 1f)
                    return;
                processing = awaiting = 0;
                foreach (var batch in batches)
                    if (batch.CurrentState == LoadRequest.State.Processing) processing++;
                    else awaiting++;
                int gc = System.GC.CollectionCount(0);
                Log.Write($"[load] {frames} frames, loads light {light} heavy {heavy}, pops {popCalls} ({popCalls / System.Math.Max(frames, 1)}/frame), " +
                          $"batches {batches.Count} (max {maxBatches}, processing {processing}, other {awaiting}), update {updateSeconds * 1000:0} ms, " +
                          $"check {checkSeconds * 1000:0} ms, GCs {(lastGc < 0 ? 0 : gc - lastGc)} | types {Top(types)} | heavy {Top(heavyTypes)}");
                lastGc = gc;
                lastFlush = now;
                frames = popCalls = light = heavy = maxBatches = 0;
                updateSeconds = checkSeconds = 0f;
                types.Clear();
                heavyTypes.Clear();
            }

            private static string Top(Dictionary<string, int> counts)
            {
                var list = new List<KeyValuePair<string, int>>(counts);
                list.Sort((a, b) => b.Value.CompareTo(a.Value));
                var parts = new List<string>();
                for (int i = 0; i < list.Count && i < 6; i++)
                    parts.Add($"{list[i].Key} {list[i].Value}");
                return string.Join(", ", parts.ToArray());
            }
        }
    }

    // Every DataLoader file load goes through LoadResource(path, handler, monitor=true), which registers the file for
    // hot-reload by scanning a List of all previously loaded files (O(n) per load, ~36k files). The only consumer,
    // DataLoader.CheckFiles, is unreachable (its callers Start/OnApplicationFocus are never invoked: DataLoader is a
    // plain object). With monitor=false the method just calls CallHandler, so loading is otherwise unchanged.
    internal static class DataLoaderMonitoring
    {
        internal static void Apply(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(HBS.Data.DataLoader), "LoadResource",
                    new[] { typeof(string), typeof(System.Action<string, System.IO.Stream>), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(DataLoaderMonitoring), nameof(Prefix)));
        }

        private static void Prefix(ref bool __2) => __2 = false;
    }
}
