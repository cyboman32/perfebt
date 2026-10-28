using System;
using System.Reflection;
using System.Security.Cryptography;
using BattleTech;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PerfeBT
{
    // LowVisibility replaces LineOfSight.GetLineOfFireUncached with its LineOfSight_GetLineOfFireUncached.Prefix. Every
    // call copies Combat.AllActors, sorts the copy by distance and filters it to the actors near the line, but uses
    // that list only when the target is an actor (or has no GUID). In urban maps 98% of the calls target buildings
    // (one per unit and building in every visibility rebuild: ~11,000 per death, 0.07 ms each with every ray cached).
    // For those targets this runs the rest of the prefix as written - the same calls in the same order, the same
    // float operations - and skips it. Only for the exact prefix it was written against (IL hash); every
    // VerifyEvery-th call runs LowVisibility's prefix as well and compares, a difference switches it off.
    internal static class LowVisibilityLineOfFire
    {
        // SHA-256 of the IL of LowVisibility's LineOfSight_GetLineOfFireUncached.Prefix as shipped with RogueTech.
        internal const string ExpectedIl = "ed70232794456042d0eb091afa67209b37def2bda7ff4b464dabd6f3c2c4a5f4";

        private struct State
        {
            internal bool Verify;
            internal LineOfFireLevel Level;
            internal Vector3 Collision;
        }

        private static MethodInfo target;

        // Uses LowVisibility members directly; only reached (and precompiled) when its assembly is loaded.
        private static class Lv
        {
            internal static MethodInfo Target() =>
                AccessTools.Method(typeof(LowVisibility.Patch.LineOfSight_GetLineOfFireUncached), nameof(LowVisibility.Patch.LineOfSight_GetLineOfFireUncached.Prefix));

            internal static bool TraceOn() => LowVisibility.Mod.Log.Trace.HasValue;
        }

        private static bool on, patchesChecked;
        private static int verifyEvery, sinceVerify;
        private static long computed, verified;

        internal static void Apply(Harmony h, int verify)
        {
            if (!ModAssemblies.Loaded("LowVisibility"))
                return;
            Fixes.Precompile(typeof(Lv));
            target = Lv.Target();
            var il = IlHash(target);
            if (il != ExpectedIl)
            {
                Log.Write($"[lof] {target.DeclaringType?.FullName}.Prefix is not the version this was written for (IL {il}); left alone");
                return;
            }
            verifyEvery = verify;
            h.Patch(target,
                prefix: new HarmonyMethod(typeof(LowVisibilityLineOfFire), nameof(Prefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(LowVisibilityLineOfFire), nameof(Postfix)) { priority = Priority.First });
            SceneManager.sceneLoaded += (scene, mode) => Summary(scene.name);
            on = true;
        }

        internal static string IlHash(MethodInfo method)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(method.GetMethodBody().GetILAsByteArray())).Replace("-", "").ToLowerInvariant();
        }

        // Other mods patch after PerfeBT loads, so the patches on LowVisibility's prefix are checked at the first call.
        private static void CheckPatches()
        {
            patchesChecked = true;
            var info = Harmony.GetPatchInfo(target);
            if (info == null)
                return;
            foreach (var p in info.Prefixes)
                if (!Ours(p.owner))
                    Off($"prefix {p.owner}");
            foreach (var p in info.Transpilers)
                if (!Ours(p.owner))
                    Off($"transpiler {p.owner}");
            foreach (var p in info.Postfixes)
                if (!Ours(p.owner))
                    Off($"postfix {p.owner}");
        }

        // The fixes and the profiler's timing patches.
        internal static bool Ours(string owner) => owner.StartsWith("io.perfebt.", StringComparison.Ordinal);

        private static void Off(string why)
        {
            if (!on)
                return;
            on = false;
            Log.Write($"[lof] LowVisibility line-of-fire prefix also has {why}; fast path off");
        }

        // Arguments as LowVisibility's prefix declares them: ref bool __runOriginal, LineOfSight __instance,
        // ref LineOfFireLevel __result, CombatGameState ___Combat, source, sourcePosition, target, targetPosition,
        // targetRotation, ref collisionWorldPos.
        private static bool Prefix(ref bool __0, LineOfSight __1, ref LineOfFireLevel __2, CombatGameState __3, AbstractActor __4,
            Vector3 __5, ICombatant __6, Vector3 __7, Quaternion __8, ref Vector3 __9, out State __state)
        {
            __state = default;
            if (!patchesChecked)
                CheckPatches();
            if (!on || !__0 || __6 is AbstractActor || Lv.TraceOn())
                return true;
            var guid = __6.GUID;
            if (guid == null)
                return true;
            var level = Compute(__1, __3, __4, __5, __6, __7, __8, guid, out var collision);
            if (verifyEvery > 0 && ++sinceVerify >= verifyEvery)
            {
                sinceVerify = 0;
                __state = new State { Verify = true, Level = level, Collision = collision };
                return true;
            }
            computed++;
            __2 = level;
            __9 = collision;
            __0 = false;
            return false;
        }

        private static void Postfix(bool __0, LineOfFireLevel __2, Vector3 __9, State __state)
        {
            if (!__state.Verify || !on)
                return;
            if (!__0 && __2 == __state.Level && __9.x.Equals(__state.Collision.x) && __9.y.Equals(__state.Collision.y) &&
                __9.z.Equals(__state.Collision.z))
            {
                verified++;
                return;
            }
            on = false;
            Log.Write($"[lof] verification: LowVisibility gave {__2} at {__9.ToString("F3")} (run original {__0}), fast path {__state.Level} at " +
                      $"{__state.Collision.ToString("F3")}; fast path switched off for this session");
        }

        // LowVisibility's prefix for a target that is not an actor and has a GUID: the actor list is never read.
        private static LineOfFireLevel Compute(LineOfSight los, CombatGameState combat, AbstractActor source, Vector3 sourcePosition,
            ICombatant target, Vector3 targetPosition, Quaternion targetRotation, string guid, out Vector3 collisionWorldPos)
        {
            var forward = targetPosition - sourcePosition;
            forward.y = 0f;
            var rotation = Quaternion.LookRotation(forward);
            var sourcePositions = source.GetLOSSourcePositions(sourcePosition, rotation);
            var targetPositions = target.GetLOSTargetPositions(targetPosition, targetRotation);
            float clear = 0f, rays = 0f, blocked = 0f;
            collisionWorldPos = targetPosition;
            var nearest = 999999.9f;
            var weapon = source.GetLongestRangeWeapon(false, false);
            var range = weapon == null ? 0f : weapon.MaxRange;
            var spotter = combat.LOS.GetAdjustedSpotterRange(source, null);
            var sensor = combat.LOS.GetAdjustedSensorRange(source, null);
            // Mathf.Max(range, sensor, spotter) without the params array.
            if (sensor > range)
                range = sensor;
            if (spotter > range)
                range = spotter;
            for (var i = 0; i < sourcePositions.Length; i++)
            {
                for (var j = 0; j < targetPositions.Length; j++)
                {
                    rays += 1f;
                    if (!(Vector3.Distance(sourcePositions[i], targetPositions[j]) <= range))
                        continue;
                    if (los.HasLineOfFire(sourcePositions[i], targetPositions[j], guid, range, out var collision))
                    {
                        clear += 1f;
                        break;
                    }
                    var distance = Vector3.Distance(collision, sourcePosition);
                    if (distance < nearest)
                    {
                        nearest = distance;
                        collisionWorldPos = collision;
                    }
                }
                if (clear > 0.5f)
                    break;
            }
            var ratio = clear;
            var visibility = combat.Constants.Visibility;
            var fromActors = Mathf.Min(blocked / rays, ratio - visibility.MinRatioFromActors);
            if (fromActors > 0.001f)
                ratio -= fromActors;
            if (ratio >= visibility.RatioFullVis)
                return LineOfFireLevel.LOFClear;
            if (ratio >= visibility.RatioObstructedVis)
                return LineOfFireLevel.LOFObstructed;
            return LineOfFireLevel.LOFBlocked;
        }

        private static void Summary(string scene)
        {
            if (computed + verified == 0)
                return;
            Log.Write($"[lof] scene {scene}: {computed} LowVisibility line-of-fire calls to buildings without the actor list, " +
                      $"{verified} verified{(on ? "" : " (off)")}");
            computed = verified = 0;
        }
    }
}
