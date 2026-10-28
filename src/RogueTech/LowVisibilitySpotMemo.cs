using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BattleTech;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PerfeBT
{
    // LowVisibility's VisualLockHelper.CanSpotTarget tries every pair of the source's sight points and the target's
    // until LineOfSight.HasLineOfSight passes. Every visibility rebuild after a death asks it for every unit and building
    // (~18,000 checks in urban maps, 98% buildings); with every ray remembered that was still ~0.05 ms per building,
    // 0.8-1.1 s per death. For a building the answer depends only on the spotter range, the two point lists, the
    // building and the map, and HasLineOfSight is bresenhamVisionTest(...) <= range: so the answer is remembered by
    // exactly those, kept while the sight-line memo sees no map change (LineOfSightTrace.Epoch) and forgotten
    // otherwise. Only for the exact CanSpotTarget it was written against (IL hash), only while no other mod patches
    // the sight-line path (StrategicOperations' garrison postfix is honoured by not remembering while a garrison
    // squad looks); every VerifyEvery-th reuse runs LowVisibility's code as well and compares, a difference
    // switches it off.
    internal static class LowVisibilitySpotMemo
    {
        // SHA-256 of the IL of LowVisibility's VisualLockHelper.CanSpotTarget as shipped with RogueTech.
        internal const string ExpectedIl = "71f8013013db0903039008d774f68bb0793a15cc7c6627abe9d342a73d98ac34";
        private const int MaxEntries = 400000;

        private sealed class Key : IEquatable<Key>
        {
            internal LineOfSight Los;
            internal ICombatant Target;
            internal float Range;
            internal Vector3[] Source, Points;
            internal int Hash;

            internal void Rehash()
            {
                var h = Identity(Los) * 31 + Identity(Target);
                h = h * 31 + Range.GetHashCode();
                h = Mix(h, Source);
                Hash = Mix(h, Points);
            }

            private static int Identity(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);

            private static int Mix(int h, Vector3[] points)
            {
                h = h * 31 + points.Length;
                foreach (var p in points)
                    h = ((h * 31 + p.x.GetHashCode()) * 31 + p.y.GetHashCode()) * 31 + p.z.GetHashCode();
                return h;
            }

            public bool Equals(Key o) =>
                o != null && Hash == o.Hash && ReferenceEquals(Los, o.Los) && ReferenceEquals(Target, o.Target) && Range.Equals(o.Range) &&
                Same(Source, o.Source) && Same(Points, o.Points);

            private static bool Same(Vector3[] a, Vector3[] b)
            {
                if (a.Length != b.Length)
                    return false;
                for (var i = 0; i < a.Length; i++)
                    if (!a[i].x.Equals(b[i].x) || !a[i].y.Equals(b[i].y) || !a[i].z.Equals(b[i].z))
                        return false;
                return true;
            }

            public override bool Equals(object obj) => Equals(obj as Key);
            public override int GetHashCode() => Hash;
        }

        private struct State
        {
            internal bool Verify, Remembered;
            internal int Epoch;
            internal Key Key;
        }

        private static MethodInfo target;

        // Uses LowVisibility members directly; only reached (and precompiled) when its assembly is loaded.
        private static class Lv
        {
            internal static MethodInfo Target() =>
                AccessTools.Method(typeof(LowVisibility.Helper.VisualLockHelper), nameof(LowVisibility.Helper.VisualLockHelper.CanSpotTarget));

            internal static float SpotterRange(AbstractActor source, ICombatant target) =>
                LowVisibility.Helper.VisualLockHelper.GetAdjustedSpotterRange(source, target);

            internal static bool TraceOn() => LowVisibility.Mod.Log.Trace.HasValue;
        }

        private static Dictionary<Key, bool> memo = new Dictionary<Key, bool>();
        private static readonly Key probe = new Key();
        private static Func<bool> garrisonLooking;
        private static bool on, patchesChecked;
        private static int verifyEvery, sinceVerify, epoch;
        private static long reused, computed, verified, forgotten;

        internal static void Apply(Harmony h, int verify)
        {
            if (!ModAssemblies.Loaded("LowVisibility"))
                return;
            Fixes.Precompile(typeof(Lv));
            target = Lv.Target();
            var il = LowVisibilityLineOfFire.IlHash(target);
            if (il != ExpectedIl)
            {
                Log.Write($"[spotmemo] LowVisibility's VisualLockHelper.CanSpotTarget is not the version this was written for (IL {il}); left alone");
                return;
            }
            verifyEvery = verify;
            h.Patch(target,
                prefix: new HarmonyMethod(typeof(LowVisibilitySpotMemo), nameof(Prefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(LowVisibilitySpotMemo), nameof(Postfix)) { priority = Priority.First });
            SceneManager.sceneLoaded += (scene, mode) => Summary(scene.name);
            on = true;
        }

        // Other mods patch after PerfeBT loads, so the sight-line path is checked at the first call.
        private static void CheckPatches()
        {
            patchesChecked = true;
            foreach (var p in Foreign(target))
                Off($"CanSpotTarget is patched by {p.owner}");
            foreach (var method in new MethodBase[]
                     {
                         AccessTools.Method(typeof(LineOfSight), nameof(LineOfSight.HasLineOfSight), new[] { typeof(Vector3), typeof(Vector3), typeof(float), typeof(string) }),
                         AccessTools.Method(typeof(MapMetaData), nameof(MapMetaData.GetXIndex)),
                         AccessTools.Method(typeof(MapMetaData), nameof(MapMetaData.GetZIndex)),
                     })
            {
                if (method == null)
                {
                    Off("a sight-line method is missing");
                    continue;
                }
                foreach (var p in Foreign(method))
                    Off($"{method.Name} is patched by {p.owner}");
            }
            var vision = AccessTools.Method(typeof(LineOfSight), nameof(LineOfSight.bresenhamVisionTest),
                new[] { typeof(Point), typeof(float), typeof(Point), typeof(float), typeof(Vector3), typeof(string) });
            foreach (var p in Foreign(vision))
            {
                if (p.PatchMethod.DeclaringType?.Assembly.GetName().Name != "StrategicOperations")
                    Off($"bresenhamVisionTest is patched by {p.owner}");
                else if (garrisonLooking == null)
                    GarrisonGate();
            }
        }

        private static IEnumerable<Patch> Foreign(MethodBase method)
        {
            var info = method == null ? null : Harmony.GetPatchInfo(method);
            if (info == null)
                return Enumerable.Empty<Patch>();
            return info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Where(p => !LowVisibilityLineOfFire.Ours(p.owner));
        }

        // StrategicOperations changes vision rays only while ModState.CurrentGarrisonSquadForLOS is set.
        private static void GarrisonGate()
        {
            var type = AccessTools.TypeByName("StrategicOperations.Framework.ModState");
            var field = type == null ? null : AccessTools.Field(type, "CurrentGarrisonSquadForLOS");
            if (field != null && field.IsStatic)
                garrisonLooking = () => field.GetValue(null) != null;
            else
                Off("StrategicOperations patches bresenhamVisionTest but its garrison state was not found");
        }

        private static void Off(string why)
        {
            if (!on)
                return;
            on = false;
            memo.Clear();
            Log.Write($"[spotmemo] {why}; building spotting memo off");
        }

        private static bool Prefix(AbstractActor source, Vector3 sourcePos, ICombatant target, Vector3 targetPos, Quaternion targetRot,
            LineOfSight los, ref bool __result, out State __state)
        {
            __state = default;
            if (!patchesChecked)
                CheckPatches();
            if (!on || !(target is BattleTech.Building) || los == null || Lv.TraceOn() || !LineOfSightTrace.VisionMemoSteady() ||
                garrisonLooking != null && garrisonLooking())
                return true;
            if (epoch != LineOfSightTrace.Epoch)
            {
                forgotten += memo.Count;
                memo.Clear();
                epoch = LineOfSightTrace.Epoch;
            }
            // CanSpotTarget's own steps up to the sight-line loop.
            var range = Lv.SpotterRange(source, target);
            if (Vector3.Distance(sourcePos, targetPos) > range)
                return true;
            var vector = targetPos - sourcePos;
            vector.y = 0f;
            var rotation = vector != Vector3.zero ? Quaternion.LookRotation(vector) : source.CurrentRotation;
            probe.Los = los;
            probe.Target = target;
            probe.Range = range;
            probe.Source = source.GetLOSSourcePositions(sourcePos, rotation);
            probe.Points = target.GetLOSTargetPositions(targetPos, targetRot);
            if (probe.Source == null || probe.Points == null)
                return true;
            probe.Rehash();
            if (memo.TryGetValue(probe, out var remembered))
            {
                if (verifyEvery > 0 && ++sinceVerify >= verifyEvery)
                {
                    sinceVerify = 0;
                    __state = new State { Verify = true, Remembered = remembered };
                    return true;
                }
                reused++;
                __result = remembered;
                return false;
            }
            if (memo.Count >= MaxEntries)
            {
                forgotten += memo.Count;
                memo.Clear();
            }
            var key = new Key
            {
                Los = los, Target = target, Range = range, Source = (Vector3[])probe.Source.Clone(), Points = (Vector3[])probe.Points.Clone(),
                Hash = probe.Hash,
            };
            __state = new State { Epoch = epoch, Key = key };
            return true;
        }

        private static void Postfix(bool __result, State __state)
        {
            if (!on)
                return;
            if (__state.Key != null)
            {
                // Remembered only if the map did not change while LowVisibility traced.
                if (__state.Epoch == LineOfSightTrace.Epoch && LineOfSightTrace.VisionMemoSteady())
                {
                    memo[__state.Key] = __result;
                    computed++;
                }
                return;
            }
            if (!__state.Verify)
                return;
            if (__result == __state.Remembered)
            {
                verified++;
                return;
            }
            on = false;
            memo.Clear();
            Log.Write($"[spotmemo] verification: LowVisibility gave {__result}, remembered {__state.Remembered}; building spotting memo switched off for this session");
        }

        private static void Summary(string scene)
        {
            // A new table: Clear keeps the capacity (and the last mission's buildings in the probe).
            memo = new Dictionary<Key, bool>();
            probe.Los = null;
            probe.Target = null;
            probe.Source = probe.Points = null;
            if (reused + computed + verified == 0)
                return;
            Log.Write($"[spotmemo] scene {scene}: {reused} building spotting checks reused, {computed} computed, {verified} verified, " +
                      $"{forgotten} forgotten for map changes{(on ? "" : " (off)")}");
            reused = computed = verified = forgotten = 0;
        }
    }
}
