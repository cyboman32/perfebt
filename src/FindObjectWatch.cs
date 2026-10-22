using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace PerfeBT
{
    // Diagnostic: on the after-action and salvage screens 42% of the main thread went to Unity's
    // Object::FindObjectsOfType (whole-scene search, sorted by instance ID) every frame, called from managed code
    // under CombatGameState.Update while the finished mission is still loaded. Many game singletons
    // (CameraControl.Instance, combat reticles and indicators, ...) call FindObjectOfType<T>() whenever their cached
    // instance is null, which after the map is unloaded is every call.
    //
    // Times every Object.FindObjectOfType(Type) on the main thread (FindObjectOfType<T> goes through it) and logs
    // per searched type: calls, time, how many found nothing, and the calling methods of a sample of calls
    // ([findobj] lines, every LogSeconds while searches cost more than MinLogMs, and at each scene load).
    internal static class FindObjectWatch
    {
        private const float LogSeconds = 30f;
        private const double MinLogMs = 100;
        private const int StackEvery = 500;     // record the caller of the 1st and every 500th call per type
        private const int StackFrames = 6;
        private const int TopTypes = 10, TopStacks = 3;

        private sealed class Stat
        {
            internal long Calls, Ticks, Nulls;
            internal readonly Dictionary<string, int> Callers = new Dictionary<string, int>();
        }

        private static int mainThreadId;
        private static readonly Dictionary<Type, Stat> stats = new Dictionary<Type, Stat>();
        private static long windowStart;

        internal static void Apply(Harmony harmony)
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            var target = AccessTools.Method(typeof(UnityEngine.Object), nameof(UnityEngine.Object.FindObjectOfType), new[] { typeof(Type) })
                         ?? throw new MissingMethodException("UnityEngine.Object.FindObjectOfType(Type)");
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(FindObjectWatch), nameof(Prefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(FindObjectWatch), nameof(Postfix)) { priority = Priority.Last });
            windowStart = Stopwatch.GetTimestamp();
            SceneManager.sceneLoaded += (scene, mode) => Flush("before scene " + scene.name, 0);
        }

        private static void Prefix(out long __state)
        {
            __state = Thread.CurrentThread.ManagedThreadId == mainThreadId ? Stopwatch.GetTimestamp() : 0;
        }

        private static void Postfix(Type __0, UnityEngine.Object __result, long __state)
        {
            if (__state == 0)
                return;
            long now = Stopwatch.GetTimestamp();
            try
            {
                var key = __0 ?? typeof(void);
                if (!stats.TryGetValue(key, out var s))
                    stats[key] = s = new Stat();
                if (s.Calls++ % StackEvery == 0)
                {
                    string caller = Caller();
                    s.Callers.TryGetValue(caller, out int n);
                    s.Callers[caller] = n + 1;
                }
                if (__result == null)
                    s.Nulls++;
                s.Ticks += now - __state;
                if ((now - windowStart) / (double)Stopwatch.Frequency >= LogSeconds)
                    Flush($"last {LogSeconds:F0} s", MinLogMs);
            }
            catch (Exception e)
            {
                Log.Write("[findobj] " + e.Message);
            }
        }

        // Calling methods above the Harmony wrapper and FindObjectOfType<T>.
        private static string Caller()
        {
            var frames = new StackTrace(2, false).GetFrames() ?? Array.Empty<StackFrame>();
            var names = new List<string>(StackFrames);
            foreach (var f in frames)
            {
                var m = f.GetMethod();
                if (m == null || m.DeclaringType == typeof(UnityEngine.Object) || m.DeclaringType == typeof(FindObjectWatch))
                    continue;
                names.Add(m.DeclaringType?.Name + "." + m.Name);
                if (names.Count == StackFrames)
                    break;
            }
            return string.Join(" < ", names);
        }

        private static void Flush(string when, double minMs)
        {
            double totalMs = stats.Values.Sum(s => s.Ticks) * 1000.0 / Stopwatch.Frequency;
            if (stats.Count > 0 && totalMs >= minMs)
            {
                long calls = stats.Values.Sum(s => s.Calls);
                Log.Write($"[findobj] {when}: FindObjectOfType {calls} calls, {totalMs:F0} ms on the main thread");
                foreach (var kv in stats.OrderByDescending(kv => kv.Value.Ticks).Take(TopTypes))
                {
                    var s = kv.Value;
                    var sb = new StringBuilder();
                    sb.Append($"[findobj]   {s.Ticks * 1000.0 / Stopwatch.Frequency,8:F1} ms {s.Calls,7} calls ({s.Nulls} found nothing)  {kv.Key.FullName}");
                    foreach (var c in s.Callers.OrderByDescending(c => c.Value).Take(TopStacks))
                        sb.Append($"\n[findobj]       {c.Value}x {c.Key}");
                    Log.Write(sb.ToString());
                }
            }
            stats.Clear();
            windowStart = Stopwatch.GetTimestamp();
        }
    }
}
