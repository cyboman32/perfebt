using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BattleTech;
using System.Text;
using UnityEngine.Experimental.LowLevel;
using UnityEngine.SceneManagement;

namespace PerfeBT
{
    // Diagnostic: where the main thread's frame goes, by Unity engine phase (scripts' Update/LateUpdate, physics,
    // animation, UI canvas rebuild, rendering, waiting for the GPU, ...). A marker system is inserted before the first
    // and after every child system of Unity's player loop; the time between two markers belongs to the system between
    // them. "(between frames)" is the time from the end of one frame's loop to the start of the next.
    // Every Seconds, one [loop] line: frames, average/95th percentile/worst frame, and the systems that took most time
    // per frame (average, and their worst single frame). Uses the default player loop; no mod in RogueTech sets one.
    internal static class LoopTimer
    {
        private static string[] names;
        private static long[] ticks;          // per system, this window
        private static long[] worst;          // per system, worst single frame this window
        private static long[] frameTicks;     // per system, current frame
        private static long last, frameStart, windowStart;
        private static readonly List<float> frameMs = new List<float>(4096);
        private static float seconds;
        private static int topN;
        private static string scene = "?";

        internal static void Apply(float windowSeconds, int top)
        {
            seconds = windowSeconds;
            topN = top;
            var root = PlayerLoop.GetDefaultPlayerLoop();
            var nameList = new List<string> { "(between frames)" };
            var phases = root.subSystemList;
            for (int p = 0; p < phases.Length; p++)
            {
                var children = phases[p].subSystemList ?? Array.Empty<PlayerLoopSystem>();
                var list = new List<PlayerLoopSystem>(children.Length * 2 + 1);
                // The marker before the first child closes the previous span (between frames, or the previous phase's
                // last child); every marker after a child closes that child's span.
                list.Add(Marker(p == 0 ? 0 : -1, first: p == 0));
                foreach (var child in children)
                {
                    list.Add(child);
                    nameList.Add(phases[p].type.Name + "." + child.type.Name);
                    list.Add(Marker(nameList.Count - 1, first: false));
                }
                phases[p].subSystemList = list.ToArray();
            }
            root.subSystemList = phases;
            names = nameList.ToArray();
            ticks = new long[names.Length];
            worst = new long[names.Length];
            frameTicks = new long[names.Length];
            PlayerLoop.SetPlayerLoop(root);
            SceneManager.activeSceneChanged += (from, to) => scene = to.name;
            windowStart = Stopwatch.GetTimestamp();
        }

        // index: system whose span this marker closes (-1 = nothing, the previous phase's last child closed it).
        private static PlayerLoopSystem Marker(int index, bool first)
        {
            return new PlayerLoopSystem
            {
                type = typeof(LoopTimer),
                updateDelegate = () =>
                {
                    long now = Stopwatch.GetTimestamp();
                    if (first)
                        EndFrame(now);
                    if (index >= 0 && last != 0)
                        frameTicks[index] += now - last;
                    last = now;
                }
            };
        }

        private static void EndFrame(long now)
        {
            if (frameStart != 0)
            {
                // (between frames) is closed by this same marker after EndFrame, so add it to this frame first.
                frameTicks[0] += now - last;
                frameMs.Add((float)((now - frameStart) * 1000.0 / Stopwatch.Frequency));
                for (int i = 0; i < frameTicks.Length; i++)
                {
                    ticks[i] += frameTicks[i];
                    if (frameTicks[i] > worst[i])
                        worst[i] = frameTicks[i];
                    frameTicks[i] = 0;
                }
                last = now;   // already counted
            }
            frameStart = now;
            if ((now - windowStart) / (double)Stopwatch.Frequency >= seconds)
                Flush(now);
        }

        private static void Flush(long now)
        {
            int n = frameMs.Count;
            if (n > 0)
            {
                double f = 1000.0 / Stopwatch.Frequency;
                frameMs.Sort();
                var sb = new StringBuilder();
                sb.Append($"[loop] {(now - windowStart) / (double)Stopwatch.Frequency:F0} s, scene {scene}, combat {(UnityGameInstance.BattleTechGame?.Combat != null ? "yes" : "no")}: ");
                sb.Append($"{n} frames, avg {frameMs.Average():F1} ms, p95 {frameMs[Math.Min(n - 1, (int)(n * 0.95))]:F1} ms, worst {frameMs[n - 1]:F0} ms; per frame:");
                foreach (int i in Enumerable.Range(0, names.Length).OrderByDescending(i => ticks[i]).Take(topN))
                    sb.Append($"\n        {ticks[i] * f / n,7:F2} ms avg {worst[i] * f,8:F1} ms worst  {names[i]}");
                Log.Write(sb.ToString());
            }
            frameMs.Clear();
            Array.Clear(ticks, 0, ticks.Length);
            Array.Clear(worst, 0, worst.Length);
            windowStart = now;
        }
    }
}
