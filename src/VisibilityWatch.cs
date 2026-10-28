using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using BattleTech;
using HarmonyLib;
using UnityEngine;

namespace PerfeBT
{
    // Diagnostic: urban missions froze for 1-17 s at a time inside LineOfSight.GetVisibilityToTargetWithPositionsAndRotations
    // (replaced by LowVisibility; 0.15 ms per call, up to 106k calls in one frame). For every frame with at least
    // MinCalls calls, one [vis] line: calls, time, how many had exactly the same arguments as an earlier call in that
    // frame (what a per-frame cache could save), and the callers of a sample of calls.
    internal static class VisibilityWatch
    {
        private const int MinCalls = 1000, StackEvery = 2000, StackFrames = 10, TopStacks = 4;

        private struct Key : IEquatable<Key>
        {
            internal int Source, Target;
            internal Vector3 SourcePos, TargetPos;
            internal Quaternion TargetRot;

            public bool Equals(Key o) => Source == o.Source && Target == o.Target && SourcePos == o.SourcePos
                                         && TargetPos == o.TargetPos && TargetRot == o.TargetRot;
            public override int GetHashCode() => Source * 397 ^ Target * 31 ^ SourcePos.GetHashCode() ^ TargetPos.GetHashCode() * 7;
        }

        private static readonly HashSet<Key> seen = new HashSet<Key>();
        private static readonly HashSet<int> sources = new HashSet<int>(), targets = new HashSet<int>();
        private static readonly Dictionary<string, int> callers = new Dictionary<string, int>();
        private static int frame = -1, calls, repeats, depth, unitCalls, unitSeen, otherSeen;
        private static long ticks, unitTicks;
        private static bool currentIsUnit;

        internal static void Apply(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(LineOfSight), nameof(LineOfSight.GetVisibilityToTargetWithPositionsAndRotations),
                             new[] { typeof(AbstractActor), typeof(Vector3), typeof(ICombatant), typeof(Vector3), typeof(Quaternion) })
                         ?? throw new MissingMethodException("LineOfSight.GetVisibilityToTargetWithPositionsAndRotations");
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(VisibilityWatch), nameof(Prefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(VisibilityWatch), nameof(Postfix)) { priority = Priority.Last });
        }

        private static void Prefix(AbstractActor __0, Vector3 __1, ICombatant __2, Vector3 __3, Quaternion __4, out long __state)
        {
            __state = 0;
            if (depth++ > 0)
                return;
            int f = Time.frameCount;
            if (f != frame)
            {
                Flush();
                frame = f;
            }
            calls++;
            currentIsUnit = __2 is AbstractActor;
            if (currentIsUnit)
                unitCalls++;
            int s = RuntimeHelpers.GetHashCode(__0), t = __2 == null ? 0 : RuntimeHelpers.GetHashCode(__2);
            sources.Add(s);
            targets.Add(t);
            if (!seen.Add(new Key { Source = s, Target = t, SourcePos = __1, TargetPos = __3, TargetRot = __4 }))
                repeats++;
            if (calls % StackEvery == 1)
            {
                string c = Caller();
                callers.TryGetValue(c, out int n);
                callers[c] = n + 1;
            }
            __state = Stopwatch.GetTimestamp();
        }

        private static void Postfix(long __state, VisibilityLevel __result)
        {
            depth--;
            if (__state == 0)
                return;
            long t = Stopwatch.GetTimestamp() - __state;
            ticks += t;
            if (currentIsUnit)
            {
                unitTicks += t;
                if (__result != VisibilityLevel.None)
                    unitSeen++;
            }
            else if (__result != VisibilityLevel.None)
                otherSeen++;
        }

        private static string Caller()
        {
            var frames = new StackTrace(2, false).GetFrames() ?? Array.Empty<StackFrame>();
            var names = new List<string>(StackFrames);
            foreach (var f in frames)
            {
                var m = f.GetMethod();
                if (m == null || m.DeclaringType == typeof(VisibilityWatch) || m.DeclaringType == typeof(VisibilityRebuildMemo)
                    || m.Name.Contains("GetVisibilityToTargetWithPositionsAndRotations") || m.Name.Contains("SyncProxy")
                    || m.DeclaringType?.Name == "Glue")
                    continue;
                string n = m.Name;
                int dmd = n.LastIndexOf("::", StringComparison.Ordinal);   // Harmony replacement "DMD<...::Type::Method>"
                names.Add(dmd >= 0 ? n.Substring(dmd + 2).TrimEnd('>') : m.DeclaringType?.Name + "." + n);
                if (names.Count == StackFrames)
                    break;
            }
            return string.Join(" < ", names);
        }

        private static void Flush()
        {
            if (calls >= MinCalls)
            {
                var lines = string.Join("", callers.OrderByDescending(c => c.Value).Take(TopStacks).Select(c => $"\n[vis]       {c.Value}x {c.Key}"));
                Log.Write($"[vis] frame {frame}: {calls} calls in {ticks * 1000.0 / Stopwatch.Frequency:F0} ms, {repeats} repeated arguments " +
                          $"({100.0 * repeats / calls:F0}%), {sources.Count} sources x {targets.Count} targets; units as targets " +
                          $"{unitCalls} calls {unitTicks * 1000.0 / Stopwatch.Frequency:F0} ms ({unitSeen} not None), other targets " +
                          $"{calls - unitCalls} calls {(ticks - unitTicks) * 1000.0 / Stopwatch.Frequency:F0} ms ({otherSeen} not None){lines}");
            }
            calls = repeats = unitCalls = unitSeen = otherSeen = 0;
            ticks = unitTicks = 0;
            seen.Clear();
            sources.Clear();
            targets.Clear();
            callers.Clear();
        }
    }
}
