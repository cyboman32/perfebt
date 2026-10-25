using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BattleTech;
using BattleTech.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PerfeBT
{
    // LineOfSight.bresenhamVisionTest walks the map grid cell by cell between two points and sums the vision cost of
    // each cell (terrain height, design mask, buildings in the way). In urban maps every visibility check against a
    // building traces about ten such rays, and a unit death or an aura change runs tens of thousands of checks: one
    // death froze the game for 18 s, 94% of the checks repeating arguments already traced in the same freeze.
    //
    // A trace reads only its arguments and map state: cell heights, terrain masks, design masks, building cells. Two
    // independent parts speed it up:
    //
    // Memo (LineOfSightRayMemo): the result of the original method for the same arguments is remembered and returned
    // again. Postfixes of other mods still run on the remembered value. Units rarely move between two deaths, so the
    // same rays to the same buildings are traced again and again; with LineOfSightRayMemoPersist the memo lives across
    // calls until map state may change, otherwise only inside the outermost call of CombatGameState.Update,
    // AbstractActor.HandleDeath or a visibility cache rebuild. It is dropped:
    //   - building destroyed (ObstructionGameLogic.BuildingDestroyedUpdateCells): only rays passing near its cells;
    //   - cells repainted (drop zones, dangerous regions), scene load: everything;
    //   - design masks changed by a registered mask source (RegisterMaskSource, ClearAfter): everything.
    // While a mask source hides design masks (CustomAmmoCategories does inside the movement update, the move previews
    // and CustomUnits' AbstractActor.OnPositionUpdate for units unaffected by masks) rays are neither remembered nor
    // reused. Any other mod patching the design-mask lookups switches the memo off. Only main-thread traces use it.
    // Every VerifyEvery-th reuse is traced again and compared; a difference switches the memo off for the session.
    //
    // Fast trace (LineOfSightFastTrace): the same walk as the original, with the same float operations in the same
    // order and the same calls into patchable methods (visCostOfCell, design masks), without filling the shared point
    // list first and walking it again. Every VerifyEvery-th trace runs the original instead and compares; a difference
    // switches the fast trace off for the session.
    //
    // Line-of-fire memo (LineOfFireRayMemo): the same memo for bresenhamHeightTest, which LowVisibility's line-of-fire
    // replacement runs about eight times per unit and target in every visibility rebuild. It reads only cell heights
    // and building cells and returns a result plus the collision cell; design masks don't affect it.
    // Both stand down if another mod adds a prefix or transpiler to bresenhamVisionTest; the fast trace also if the
    // line or projected-height helpers it reproduces are patched.
    internal static class LineOfSightTrace
    {
        private struct RayKey : IEquatable<RayKey>
        {
            internal LineOfSight Los;
            internal int X0, Z0, X1, Z1;
            internal float H0, H1, Dx, Dz;
            internal string Target;

            public bool Equals(RayKey o) =>
                X0 == o.X0 && Z0 == o.Z0 && X1 == o.X1 && Z1 == o.Z1 && H0 == o.H0 && H1 == o.H1 && Dx == o.Dx && Dz == o.Dz
                && ReferenceEquals(Los, o.Los) && string.Equals(Target, o.Target);

            public override int GetHashCode()
            {
                unchecked
                {
                    var h = X0 * 397 ^ Z0;
                    h = h * 397 ^ X1;
                    h = h * 397 ^ Z1;
                    h = h * 397 ^ H0.GetHashCode();
                    h = h * 397 ^ H1.GetHashCode();
                    h = h * 397 ^ Dx.GetHashCode();
                    h = h * 397 ^ Dz.GetHashCode();
                    return h * 397 ^ (Target == null ? 0 : Target.GetHashCode());
                }
            }
        }

        private struct State
        {
            internal int Flags;
            internal float MemoValue, FastValue;
            internal RayKey Key;
        }

        private sealed class ByReference : IEqualityComparer<MapTerrainDataCell>
        {
            public bool Equals(MapTerrainDataCell a, MapTerrainDataCell b) => ReferenceEquals(a, b);
            public int GetHashCode(MapTerrainDataCell c) => RuntimeHelpers.GetHashCode(c);
        }

        private const int Store = 1, VerifyMemo = 2, VerifyFast = 4, Done = 8;
        private const int LogMinTraces = 5000;
        // About 80 bytes per remembered ray; one urban death rebuild traces ~110,000 distinct rays. Per generation.
        private const int MaxRays = 300000;

        // Two generations: when the current one is full it becomes the old one and the previous old one is dropped;
        // a ray found in the old generation moves back to the current one. The rays of the current unit positions
        // stay remembered, those of positions long left fall out.
        private sealed class RayMemo<T>
        {
            internal Dictionary<RayKey, T> Current = new Dictionary<RayKey, T>(), Old = new Dictionary<RayKey, T>();

            internal int Count => Current.Count + Old.Count;

            internal bool TryGetValue(RayKey key, out T value)
            {
                if (Current.TryGetValue(key, out value))
                    return true;
                if (!Old.TryGetValue(key, out value))
                    return false;
                Old.Remove(key);
                Set(key, value);
                return true;
            }

            internal void Set(RayKey key, T value)
            {
                if (Current.Count >= MaxRays)
                {
                    var dropped = Old;
                    Old = Current;
                    dropped.Clear();
                    Current = dropped;
                    capped++;
                }
                Current[key] = value;
            }

            internal void Clear()
            {
                Current.Clear();
                Old.Clear();
            }
        }

        private struct HeightResult
        {
            internal bool Clear;
            internal Point Collision;
        }

        private struct HeightState
        {
            internal int Flags;
            internal HeightResult Remembered;
            internal RayKey Key;
        }

        // A Bresenham line can step one cell past its end point on the minor axis; two cells of margin covers it.
        private const int RayMargin = 2;

        private static readonly AccessTools.FieldRef<LineOfSight, CombatGameState> combat =
            AccessTools.FieldRefAccess<LineOfSight, CombatGameState>("Combat");
        private static Func<LineOfSight, MapTerrainDataCell, float, float> visCostOfCell;

        private static Harmony harmony;
        private static MethodInfo target, heightTarget;
        private static bool patched, hooked, patchesChecked, memoOn, fastOn, heightOn, persist;
        private static int memoVerifyEvery, fastVerifyEvery, heightVerifyEvery, memoSinceVerify, fastSinceVerify, heightSinceVerify, mainThread;
        private static readonly Dictionary<string, Func<bool>> maskSources = new Dictionary<string, Func<bool>>();
        private static Func<bool> masksHidden;

        private static readonly RayMemo<float> memo = new RayMemo<float>();
        private static readonly RayMemo<HeightResult> heightMemo = new RayMemo<HeightResult>();
        private static readonly HashSet<MapTerrainDataCell> pendingCells = new HashSet<MapTerrainDataCell>(new ByReference());
        private static readonly List<RayKey> dropKeys = new List<RayKey>();
        private static readonly List<int> changedX = new List<int>(), changedZ = new List<int>();
        private static int depth;
        private static string windowName;
        private static long windowStart, windowTraced, windowReused, windowHeightTraced, windowHeightReused;
        private static long traced, reused, invalidated, cleared, capped, hiddenTraces, memoVerified, fastTraces, fastVerified;
        private static long heightTraced, heightReused, heightVerified;

        internal static void ApplyMemo(Harmony h, int verifyEvery, bool persistent)
        {
            memoVerifyEvery = verifyEvery;
            persist = persistent;
            EnsurePatched(h);
            EnsureHooks(h);
            memoOn = true;
        }

        // LineOfSight.bresenhamHeightTest (line of fire): the same memo for the height-only walk, which reads only cell
        // heights and building cells, not design masks.
        internal static void ApplyHeightMemo(Harmony h, int verifyEvery, bool persistent)
        {
            heightVerifyEvery = verifyEvery;
            persist = persistent;
            EnsurePatched(h);
            EnsureHooks(h);
            heightTarget = AccessTools.Method(typeof(LineOfSight), "bresenhamHeightTest",
                               new[] { typeof(Point), typeof(float), typeof(Point), typeof(float), typeof(string), typeof(Point).MakeByRefType() })
                           ?? throw new MissingMethodException("LineOfSight.bresenhamHeightTest");
            h.Patch(heightTarget,
                prefix: new HarmonyMethod(typeof(LineOfSightTrace), nameof(HeightPrefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(LineOfSightTrace), nameof(HeightPostfix)) { priority = Priority.First });
            heightOn = true;
        }

        private static void EnsureHooks(Harmony h)
        {
            if (hooked)
                return;
            var windows = new[]
            {
                AccessTools.Method(typeof(CombatGameState), "Update", Type.EmptyTypes),
                AccessTools.Method(typeof(AbstractActor), "HandleDeath", new[] { typeof(string) }),
                AccessTools.Method(typeof(TurnDirector), "RebuildVisCaches", new[] { typeof(List<ICombatant>) }),
                AccessTools.Method(typeof(Team), "RebuildVisibilityCacheAllUnits", new[] { typeof(List<ICombatant>) }),
                AccessTools.Method(typeof(AbstractActor), "UpdateVisibilityCacheForDeadUnit", new[] { typeof(List<ICombatant>) }),
            };
            foreach (var w in windows)
                h.Patch(w ?? throw new MissingMethodException("line-of-sight memo window"),
                    prefix: new HarmonyMethod(typeof(LineOfSightTrace), nameof(Enter)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(LineOfSightTrace), nameof(Leave)) { priority = Priority.Last });

            var repaints = new[]
            {
                AccessTools.Method(typeof(DropshipGameLogic), "MarkDropshipLandingZone", new[] { typeof(bool) }),
                AccessTools.Method(typeof(RegionGameLogic), "MarkAssociatedCellsAsDangerous", new[] { typeof(bool) }),
                AccessTools.Method(typeof(UnitSpawnPointGameLogic), "PaintDangerousLocationForDropPod", new[] { typeof(bool) }),
                AccessTools.Method(typeof(MapMetaData), "PaintDangerousLocation", new[] { typeof(Rect), typeof(Rect) }),
            };
            foreach (var r in repaints)
                h.Patch(r ?? throw new MissingMethodException("line-of-sight memo repaint"),
                    finalizer: new HarmonyMethod(typeof(LineOfSightTrace), nameof(ClearAfter)) { priority = Priority.Last });

            h.Patch(AccessTools.Method(typeof(ObstructionGameLogic), "BuildingDestroyedUpdateCells", Type.EmptyTypes)
                    ?? throw new MissingMethodException("ObstructionGameLogic.BuildingDestroyedUpdateCells"),
                finalizer: new HarmonyMethod(typeof(LineOfSightTrace), nameof(BuildingDestroyed)) { priority = Priority.Last });
            hooked = true;
        }

        // A mod whose patches on the design-mask lookups make design masks change at runtime. It must clear the memo
        // (ClearAfter as finalizer) after every change; hidden reports whether it currently hides design masks (null: never).
        internal static void RegisterMaskSource(string assembly, Func<bool> hidden)
        {
            maskSources[assembly] = hidden;
        }

        internal static void ApplyFastTrace(Harmony h, int verifyEvery)
        {
            fastVerifyEvery = verifyEvery;
            var cost = AccessTools.Method(typeof(LineOfSight), "visCostOfCell", new[] { typeof(MapTerrainDataCell), typeof(float) })
                       ?? throw new MissingMethodException("LineOfSight.visCostOfCell");
            visCostOfCell = AccessTools.MethodDelegate<Func<LineOfSight, MapTerrainDataCell, float, float>>(cost);
            EnsurePatched(h);
            fastOn = true;
        }

        private static void EnsurePatched(Harmony h)
        {
            if (patched)
                return;
            harmony = h;
            target = AccessTools.Method(typeof(LineOfSight), "bresenhamVisionTest",
                         new[] { typeof(Point), typeof(float), typeof(Point), typeof(float), typeof(Vector3), typeof(string) })
                     ?? throw new MissingMethodException("LineOfSight.bresenhamVisionTest");
            mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            h.Patch(target,
                prefix: new HarmonyMethod(typeof(LineOfSightTrace), nameof(Prefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(LineOfSightTrace), nameof(Postfix)) { priority = Priority.First });
            SceneManager.sceneLoaded += (scene, mode) => Summary(scene.name);
            patched = true;
        }

        // Other mods patch after PerfeBT loads, so their patches are checked at the first trace.
        private static void CheckPatches()
        {
            patchesChecked = true;
            if (heightOn && heightTarget != null)
                foreach (var note in ForeignPatches(Harmony.GetPatchInfo(heightTarget)))
                {
                    heightOn = false;
                    Log.Write($"[losray] bresenhamHeightTest also has {note}; line-of-fire memo off");
                    break;
                }
            var info = Harmony.GetPatchInfo(target);
            if (info == null)
                return;
            var notes = new List<string>();
            foreach (var p in info.Prefixes)
                if (p.owner != harmony.Id)
                    notes.Add($"prefix {p.owner}");
            foreach (var p in info.Transpilers)
                if (p.owner != harmony.Id)
                    notes.Add($"transpiler {p.owner}");
            if (notes.Count > 0)
            {
                memoOn = fastOn = false;
                Log.Write($"[losray] bresenhamVisionTest also has {string.Join(", ", notes)}; memo and fast trace off");
                return;
            }
            foreach (var p in info.Postfixes)
                if (p.owner != harmony.Id && p.priority >= Priority.First && memoOn)
                {
                    memoOn = false;
                    Log.Write($"[losray] postfix {p.owner} runs before the memo's; memo off");
                }
            if (memoOn)
                CheckMaskSources();
            if (!fastOn)
                return;
            foreach (var reproduced in new[]
                     {
                         AccessTools.Method(typeof(LineOfSight), "getProjectedHeightAt"),
                         AccessTools.Method(typeof(BresenhamLineUtil), "BresenhamLine"),
                     })
            {
                var r = reproduced == null ? null : Harmony.GetPatchInfo(reproduced);
                if (reproduced != null && (r == null || r.Owners.Count == 0))
                    continue;
                fastOn = false;
                Log.Write($"[losray] {reproduced?.Name ?? "a reproduced helper"} is missing or patched ({(r == null ? "" : string.Join(", ", r.Owners))}); fast trace off");
                return;
            }
        }

        // Patches of other mods that would see or change the original's result before the memo's postfix.
        private static IEnumerable<string> ForeignPatches(Patches info)
        {
            if (info == null)
                yield break;
            foreach (var p in info.Prefixes.Concat(info.Transpilers))
                if (p.owner != harmony.Id)
                    yield return $"{p.owner} ({p.PatchMethod.Name})";
            foreach (var p in info.Postfixes)
                if (p.owner != harmony.Id && p.priority >= Priority.First)
                    yield return $"postfix {p.owner} running first";
        }

        // The memo is only safe if every patch on the design-mask lookups comes from a registered mask source.
        private static void CheckMaskSources()
        {
            var own = typeof(LineOfSightTrace).Assembly;
            var hidden = new List<Func<bool>>();
            foreach (var lookup in new MethodBase[]
                     {
                         AccessTools.Method(typeof(LineOfSight), "visCostOfCell", new[] { typeof(MapTerrainDataCell), typeof(float) }),
                         AccessTools.Method(typeof(MapMetaData), "GetPriorityDesignMask", new[] { typeof(MapTerrainDataCell) }),
                         AccessTools.Method(typeof(MapMetaData), "GetPriorityTerrainMaskFlags", new[] { typeof(MapTerrainDataCell) }),
                     })
            {
                var info = lookup == null ? null : Harmony.GetPatchInfo(lookup);
                if (info == null)
                    continue;
                foreach (var p in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
                {
                    var asm = p.PatchMethod.DeclaringType?.Assembly;
                    if (asm == own)
                        continue;
                    var name = asm?.GetName().Name ?? p.owner;
                    if (!maskSources.TryGetValue(name, out var h))
                    {
                        memoOn = false;
                        Log.Write($"[losray] {lookup.Name} is patched by {name}, which is not a known design-mask source; memo off");
                        return;
                    }
                    if (h != null && !hidden.Contains(h))
                        hidden.Add(h);
                }
            }
            if (hidden.Count == 1)
                masksHidden = hidden[0];
            else if (hidden.Count > 1)
                masksHidden = () => hidden.Exists(f => f());
        }

        private static void Enter(MethodBase __originalMethod)
        {
            if (depth++ > 0)
                return;
            if (!persist)
            {
                memo.Clear();
                heightMemo.Clear();
                pendingCells.Clear();
            }
            windowName = __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name;
            windowStart = Stopwatch.GetTimestamp();
            windowTraced = windowReused = windowHeightTraced = windowHeightReused = 0;
        }

        private static Exception Leave(Exception __exception)
        {
            if (--depth > 0)
                return __exception;
            if (!persist)
            {
                memo.Clear();
                heightMemo.Clear();
                pendingCells.Clear();
            }
            if (windowTraced + windowReused + windowHeightTraced + windowHeightReused >= LogMinTraces)
                Log.Write($"[losray] {windowName}: {windowTraced} rays traced, {windowReused} reused; line of fire {windowHeightTraced} traced, " +
                          $"{windowHeightReused} reused; {(Stopwatch.GetTimestamp() - windowStart) * 1000.0 / Stopwatch.Frequency:F0} ms");
            return __exception;
        }

        // Drops every remembered ray; for mask sources after they change design masks.
        internal static void Clear()
        {
            if (memo.Count == 0 && heightMemo.Count == 0)
                return;
            memo.Clear();
            heightMemo.Clear();
            pendingCells.Clear();
            cleared++;
        }

        internal static Exception ClearAfter(Exception __exception)
        {
            Clear();
            return __exception;
        }

        private static Exception BuildingDestroyed(ObstructionGameLogic __instance, Exception __exception)
        {
            if (memo.Count + heightMemo.Count == 0 || __instance.occupiedCells == null)
                return __exception;
            foreach (var cell in __instance.occupiedCells)
                if (cell?.relatedTerrainCell != null)
                    pendingCells.Add(cell.relatedTerrainCell);
            return __exception;
        }

        // Drops the remembered rays whose cells may include a changed one.
        private static void DropPendingRays(LineOfSight los)
        {
            var cells = combat(los)?.MapMetaData?.mapTerrainDataCells;
            changedX.Clear();
            changedZ.Clear();
            if (cells != null)
            {
                int zn = cells.GetLength(0), xn = cells.GetLength(1);
                for (var z = 0; z < zn; z++)
                    for (var x = 0; x < xn; x++)
                        if (pendingCells.Contains(cells[z, x]))
                        {
                            changedX.Add(x);
                            changedZ.Add(z);
                        }
            }
            var dropped = DropNear(memo, los) + DropNear(heightMemo, los);
            Log.Write($"[losray] building destroyed: {pendingCells.Count} cells, " +
                      $"{dropped} of {memo.Count + heightMemo.Count + dropped} remembered rays dropped");
            invalidated += dropped;
            pendingCells.Clear();
        }

        private static int DropNear<T>(RayMemo<T> rays, LineOfSight los) => DropNear(rays.Current, los) + DropNear(rays.Old, los);

        private static int DropNear<T>(Dictionary<RayKey, T> rays, LineOfSight los)
        {
            var unplaced = changedX.Count == 0;
            dropKeys.Clear();
            foreach (var key in rays.Keys)
            {
                if (unplaced || !ReferenceEquals(key.Los, los))
                {
                    dropKeys.Add(key);
                    continue;
                }
                int minX = Math.Min(key.X0, key.X1) - RayMargin, maxX = Math.Max(key.X0, key.X1) + RayMargin;
                int minZ = Math.Min(key.Z0, key.Z1) - RayMargin, maxZ = Math.Max(key.Z0, key.Z1) + RayMargin;
                for (var i = 0; i < changedX.Count; i++)
                    if (changedX[i] >= minX && changedX[i] <= maxX && changedZ[i] >= minZ && changedZ[i] <= maxZ)
                    {
                        dropKeys.Add(key);
                        break;
                    }
            }
            foreach (var key in dropKeys)
                rays.Remove(key);
            var count = dropKeys.Count;
            dropKeys.Clear();
            return count;
        }

        private static bool HeightPrefix(LineOfSight __instance, Point __0, float __1, Point __2, float __3, string __4,
            ref Point __5, ref bool __result, out HeightState __state)
        {
            __state = default;
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != mainThread)
                return true;
            if (!patchesChecked)
                CheckPatches();
            if (!heightOn || !persist && depth == 0)
                return true;
            if (pendingCells.Count > 0)
                DropPendingRays(__instance);
            __state.Key = new RayKey { Los = __instance, X0 = __0.X, Z0 = __0.Z, X1 = __2.X, Z1 = __2.Z, H0 = __1, H1 = __3, Target = __4 };
            if (!heightMemo.TryGetValue(__state.Key, out var remembered))
            {
                __state.Flags = Store;
                return true;
            }
            if (heightVerifyEvery > 0 && ++heightSinceVerify >= heightVerifyEvery)
            {
                heightSinceVerify = 0;
                __state.Flags = VerifyMemo;
                __state.Remembered = remembered;
                return true;
            }
            heightReused++;
            windowHeightReused++;
            __result = remembered.Clear;
            __5 = remembered.Collision;
            __state.Flags = Done;
            return false;
        }

        private static void HeightPostfix(bool __result, Point __5, HeightState __state)
        {
            if (!heightOn || (__state.Flags & Done) != 0)
                return;
            var value = new HeightResult { Clear = __result, Collision = __5 };
            if ((__state.Flags & Store) != 0)
            {
                heightMemo.Set(__state.Key, value);
                heightTraced++;
                windowHeightTraced++;
            }
            else if ((__state.Flags & VerifyMemo) != 0)
            {
                var r = __state.Remembered;
                if (r.Clear == value.Clear && r.Collision.X == value.Collision.X && r.Collision.Z == value.Collision.Z)
                {
                    heightVerified++;
                    return;
                }
                heightOn = false;
                heightMemo.Clear();
                var k = __state.Key;
                Log.Write($"[losray] line-of-fire memo verification: ({k.X0},{k.Z0}) -> ({k.X1},{k.Z1}) target {k.Target}: remembered " +
                          $"{r.Clear} at ({r.Collision.X},{r.Collision.Z}), traced again {value.Clear} at ({value.Collision.X},{value.Collision.Z}); " +
                          "line-of-fire memo switched off for this session");
            }
        }

        private static bool Prefix(LineOfSight __instance, Point __0, float __1, Point __2, float __3, Vector3 __4, string __5,
            ref float __result, out State __state)
        {
            __state = default;
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != mainThread)
                return true;
            if (!patchesChecked)
                CheckPatches();
            if (memoOn && (persist || depth > 0) && masksHidden != null && masksHidden())
                hiddenTraces++;
            else if (memoOn && (persist || depth > 0))
            {
                if (pendingCells.Count > 0)
                    DropPendingRays(__instance);
                __state.Key = new RayKey
                {
                    Los = __instance, X0 = __0.X, Z0 = __0.Z, X1 = __2.X, Z1 = __2.Z, H0 = __1, H1 = __3, Dx = __4.x, Dz = __4.z, Target = __5,
                };
                if (memo.TryGetValue(__state.Key, out var remembered))
                {
                    if (memoVerifyEvery > 0 && ++memoSinceVerify >= memoVerifyEvery)
                    {
                        memoSinceVerify = 0;
                        __state.Flags = VerifyMemo;
                        __state.MemoValue = remembered;
                    }
                    else
                    {
                        reused++;
                        windowReused++;
                        __result = remembered;
                        __state.Flags = Done;
                        return false;
                    }
                }
                else
                    __state.Flags = Store;
            }
            if (!fastOn)
                return true;
            var value = Trace(__instance, __0, __1, __2, __3, __4, __5);
            fastTraces++;
            if (fastVerifyEvery > 0 && ++fastSinceVerify >= fastVerifyEvery)
            {
                fastSinceVerify = 0;
                __state.Flags |= VerifyFast;
                __state.FastValue = value;
                return true;
            }
            Finish(ref __state, value);
            __result = value;
            __state.Flags |= Done;
            return false;
        }

        private static void Postfix(float __result, State __state, Point __0, Point __2, string __5)
        {
            if ((__state.Flags & Done) != 0)
                return;
            if ((__state.Flags & VerifyFast) != 0 && fastOn)
            {
                if (Same(__state.FastValue, __result))
                    fastVerified++;
                else
                {
                    fastOn = false;
                    Log.Write($"[losray] fast trace verification: ({__0.X},{__0.Z}) -> ({__2.X},{__2.Z}) target {__5}: fast {__state.FastValue:R}, " +
                              $"original {__result:R}; fast trace switched off for this session");
                }
            }
            Finish(ref __state, __result);
        }

        private static void Finish(ref State state, float value)
        {
            if (!memoOn)
                return;
            if ((state.Flags & Store) != 0)
            {
                memo.Set(state.Key, value);
                traced++;
                windowTraced++;
            }
            else if ((state.Flags & VerifyMemo) != 0)
            {
                if (Same(state.MemoValue, value))
                {
                    memoVerified++;
                    return;
                }
                memoOn = false;
                memo.Clear();
                pendingCells.Clear();
                var k = state.Key;
                Log.Write($"[losray] memo verification in {windowName}: ({k.X0},{k.Z0}) -> ({k.X1},{k.Z1}) target {k.Target}: " +
                          $"remembered {state.MemoValue:R}, traced again {value:R}; memo switched off for this session");
            }
        }

        private static bool Same(float a, float b) => a == b || float.IsNaN(a) && float.IsNaN(b);

        // LineOfSight.bresenhamVisionTest with getProjectedHeightAt inlined and the line points in reused arrays instead
        // of BresenhamLineUtil's shared List<Point>: the same float operations in the same order, explicit (float)
        // where the original passes a value through a call.
        private static float Trace(LineOfSight los, Point p0, float height0, Point p1, float height1, Vector3 unitDelta, string targetGuid)
        {
            if (p0.X == p1.X && p0.Z == p1.Z)
                return 0f;
            var map = combat(los).MapMetaData;
            if (!map.IsWithinBounds(p0) || !map.IsWithinBounds(p1))
                return float.MaxValue;
            var costX = Mathf.Abs(unitDelta.x) * (float)MapMetaDataExporter.cellSize;
            var costZ = Mathf.Abs(unitDelta.z) * (float)MapMetaDataExporter.cellSize;
            var count = Line(p0, p1);
            var spanX = (float)(p1.X - p0.X);
            var spanZ = (float)(p1.Z - p0.Z);
            var steps = Mathf.Max(Mathf.Abs(spanX), Mathf.Abs(spanZ));
            var stepHeight = (float)((height1 - height0) / steps);
            var sum = 0f;
            var cells = map.mapTerrainDataCells;
            for (var i = 1; i < count; i++)
            {
                var weight = lineX[i] != lineX[i - 1] ? costX : costZ;
                var projected = (float)(height0 + stepHeight * (float)Mathf.Max(Mathf.Abs(lineX[i] - p0.X), Mathf.Abs(lineZ[i] - p0.Z)));
                var cell = cells[lineZ[i], lineX[i]];
                if (cell.cachedHeight > projected)
                {
                    var layer = cell.MapEncounterLayerDataCell;
                    if (layer.HasBuilding)
                    {
                        var buildings = layer.buildingList;
                        for (var b = 0; b < buildings.Count; b++)
                            if (ObstructionGameLogic.GuidsMatchObjectOrRep(buildings[b].buildingGuid, targetGuid))
                                return sum;
                    }
                    return float.MaxValue;
                }
                sum = sum + visCostOfCell(los, cell, projected) * weight;
            }
            return sum;
        }

        private static int[] lineX = new int[256], lineZ = new int[256];
        private static int lineCount;

        private static void AddPoint(int x, int z)
        {
            if (lineCount == lineX.Length)
            {
                Array.Resize(ref lineX, lineCount * 2);
                Array.Resize(ref lineZ, lineCount * 2);
            }
            lineX[lineCount] = x;
            lineZ[lineCount] = z;
            lineCount++;
        }

        // BresenhamLineUtil.BresenhamLine into lineX/lineZ; returns the point count. Only called with p0 != p1.
        private static int Line(Point p0, Point p1)
        {
            lineCount = 0;
            var dx = (float)(p1.X - p0.X);
            var dz = (float)(p1.Z - p0.Z);
            var steep = Mathf.Abs(dz) > Mathf.Abs(dx);
            var error = 0f;
            if (steep)
            {
                var slope = Mathf.Abs(dx / dz);
                var stepX = p1.X > p0.X ? 1 : -1;
                var x = p0.X;
                var z = p0.Z;
                while (p1.Z > p0.Z ? z <= p1.Z : z >= p1.Z)
                {
                    AddPoint(x, z);
                    error = error + slope;
                    while (error >= 0.5f)
                    {
                        x = x + stepX;
                        error = error - 1f;
                        AddPoint(x, z);
                    }
                    z = z + (p1.Z > p0.Z ? 1 : -1);
                }
            }
            else
            {
                var slope = Mathf.Abs(dz / dx);
                var stepZ = p1.Z > p0.Z ? 1 : -1;
                var z = p0.Z;
                var x = p0.X;
                while (p1.X > p0.X ? x <= p1.X : x >= p1.X)
                {
                    AddPoint(x, z);
                    error = error + slope;
                    while (error >= 0.5f)
                    {
                        z = z + stepZ;
                        error = error - 1f;
                        AddPoint(x, z);
                    }
                    x = x + (p1.X > p0.X ? 1 : -1);
                }
            }
            return lineCount;
        }

        private static void Summary(string scene)
        {
            memo.Clear();
            heightMemo.Clear();
            pendingCells.Clear();
            if (traced + reused + fastTraces + heightTraced + heightReused == 0)
                return;
            Log.Write($"[losray] scene {scene}: memo {reused} of {traced + reused} rays reused, {invalidated} dropped for destroyed buildings, " +
                      $"{cleared} clears, {capped} size limits, {hiddenTraces} traces with design masks hidden, {memoVerified} verified{(memoOn ? "" : " (off)")}; " +
                      $"fast trace {fastTraces} rays, {fastVerified} verified{(fastOn ? "" : " (off)")}; " +
                      $"line of fire {heightReused} of {heightTraced + heightReused} reused, {heightVerified} verified{(heightOn ? "" : " (off)")}");
            traced = reused = invalidated = cleared = capped = hiddenTraces = memoVerified = fastTraces = fastVerified = 0;
            heightTraced = heightReused = heightVerified = 0;
        }
    }
}
