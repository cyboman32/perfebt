using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using BattleTech;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace PerfeBT
{
    // When a unit dies, TurnDirector.RebuildVisCaches calls Team.RebuildVisibilityCacheAllUnits for every team, and
    // that calls AbstractActor.UpdateVisibilityCacheForDeadUnit for every dead unit the team has ever had, which
    // rebuilds the visibility cache of every living unit against every combatant (buildings included). The same
    // unit-to-target visibility is computed again for each dead unit of each team: the work grows with the number of
    // dead units, and in urban maps (~700 building combatants, ~0.2 ms per LowVisibility check) a late kill froze the
    // game for 9-17 s, 80% of it repeating calls with identical arguments.
    //
    // Nothing moves and no stat changes while these methods run; they only write visibility caches. Inside the
    // outermost call, VisibilityCache.CalcVisValueToTarget for a cache/target pair already computed in that call
    // stores and returns the remembered value instead of recomputing it; the write into the cache is the same as the
    // original's. Every VerifyEvery-th reuse is computed for real and compared; a difference switches the memo off for
    // the session.
    internal static class VisibilityRebuildMemo
    {
        private struct Key : IEquatable<Key>
        {
            internal VisibilityCache Cache;
            internal ICombatant Target;

            public bool Equals(Key o) => ReferenceEquals(Cache, o.Cache) && ReferenceEquals(Target, o.Target);
            public override int GetHashCode() => RuntimeHelpers.GetHashCode(Cache) * 397 ^ RuntimeHelpers.GetHashCode(Target);
        }

        private const int Compute = 0, Reused = 1, Verify = 2;
        private const int LogMinCalls = 1000;

        private static readonly Dictionary<Key, VisibilityLevelAndAttribution> memo = new Dictionary<Key, VisibilityLevelAndAttribution>();
        private static int depth, verifyEvery;
        private static long windowStart, computed, reused, totalComputed, totalReused, verified;
        private static string windowName;
        private static bool disabled;

        internal static void Apply(Harmony harmony, int verifyEveryN)
        {
            verifyEvery = verifyEveryN;
            var calc = AccessTools.Method(typeof(VisibilityCache), nameof(VisibilityCache.CalcVisValueToTarget), new[] { typeof(ICombatant) })
                       ?? throw new MissingMethodException("VisibilityCache.CalcVisValueToTarget");
            var windows = new[]
            {
                AccessTools.Method(typeof(TurnDirector), nameof(TurnDirector.RebuildVisCaches), new[] { typeof(List<ICombatant>) }),
                AccessTools.Method(typeof(Team), nameof(Team.RebuildVisibilityCacheAllUnits), new[] { typeof(List<ICombatant>) }),
                AccessTools.Method(typeof(AbstractActor), nameof(AbstractActor.UpdateVisibilityCacheForDeadUnit), new[] { typeof(List<ICombatant>) }),
            };
            foreach (var w in windows)
            {
                if (w == null)
                    throw new MissingMethodException("visibility rebuild method");
                harmony.Patch(w,
                    prefix: new HarmonyMethod(typeof(VisibilityRebuildMemo), nameof(Enter)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(VisibilityRebuildMemo), nameof(Leave)) { priority = Priority.Last });
            }
            harmony.Patch(calc,
                prefix: new HarmonyMethod(typeof(VisibilityRebuildMemo), nameof(Prefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(VisibilityRebuildMemo), nameof(Postfix)) { priority = Priority.Last });
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (totalComputed + totalReused > 0)
                    Log.Write($"[vismemo] scene {scene.name}: {totalReused} of {totalComputed + totalReused} visibility checks in cache rebuilds reused; " +
                              $"{verified} verified{(disabled ? "; OFF" : "")}");
                totalComputed = totalReused = 0;
            };
        }

        private static void Enter(MethodBase __originalMethod)
        {
            if (depth++ > 0)
                return;
            windowName = __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name;
            windowStart = Stopwatch.GetTimestamp();
            computed = reused = 0;
        }

        private static Exception Leave(Exception __exception)
        {
            if (--depth > 0)
                return __exception;
            memo.Clear();
            totalComputed += computed;
            totalReused += reused;
            if (computed + reused >= LogMinCalls)
                Log.Write($"[vismemo] {windowName}: {computed} visibility checks computed, {reused} reused, " +
                          $"{(Stopwatch.GetTimestamp() - windowStart) * 1000.0 / Stopwatch.Frequency:F0} ms");
            return __exception;
        }

        private static bool Prefix(VisibilityCache __instance, ICombatant __0, ref VisibilityLevelAndAttribution __result, out int __state)
        {
            __state = Compute;
            if (depth == 0 || disabled || __0 == null)
                return true;
            if (!memo.TryGetValue(new Key { Cache = __instance, Target = __0 }, out var value))
                return true;
            reused++;
            if (verifyEvery > 0 && reused % verifyEvery == 0)
            {
                __state = Verify;
                return true;
            }
            __instance.VisLevelCache[__0.GUID] = value;
            __result = value;
            __state = Reused;
            return false;
        }

        private static void Postfix(VisibilityCache __instance, ICombatant __0, VisibilityLevelAndAttribution __result, int __state)
        {
            if (depth == 0 || disabled || __0 == null || __state == Reused)
                return;
            var key = new Key { Cache = __instance, Target = __0 };
            if (__state == Verify)
            {
                var value = memo[key];
                if (Same(value, __result))
                {
                    verified++;
                    return;
                }
                disabled = true;
                memo.Clear();
                var owner = __instance.OwningActor;
                Log.Write($"[vismemo] verification: {owner?.DisplayName} -> {__0.DisplayName} was {Describe(value)}, " +
                          $"computed again {Describe(__result)}; switched off for this session");
                return;
            }
            computed++;
            memo[key] = __result;
        }

        private static bool Same(VisibilityLevelAndAttribution a, VisibilityLevelAndAttribution b) =>
            a.VisibilityLevel == b.VisibilityLevel && a.LineOfFireLevel == b.LineOfFireLevel && a.LineOfFireCollision == b.LineOfFireCollision
            && a.SpottingTeamGUID == b.SpottingTeamGUID && a.SpottingLanceGUID == b.SpottingLanceGUID && a.SpottingUnitGUID == b.SpottingUnitGUID;

        private static string Describe(VisibilityLevelAndAttribution v) => $"{v.VisibilityLevel}/{v.LineOfFireLevel}/{v.LineOfFireCollision}";
    }
}
