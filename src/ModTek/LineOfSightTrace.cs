using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    // Memo (LineOfSightRayMemo): inside the outermost call of CombatGameState.Update, AbstractActor.HandleDeath or a
    // visibility cache rebuild, the result of the original method for the same arguments is remembered and returned
    // again. Postfixes of other mods still run on the remembered value. The memo is dropped when such a call ends and
    // whenever map state may change while it runs:
    //   - building destroyed (ObstructionGameLogic.BuildingDestroyedUpdateCells): only rays passing near its cells;
    //   - cells repainted (drop zones, dangerous regions): everything;
    //   - entering and leaving ActorMovementSequence.Update and the move-preview panels of CombatHUDStatusPanel, where
    //     CustomAmmoCategories hides design masks, and TurnDirector.EndCurrentRound, after which it ticks fire and
    //     temporary terrain masks; CustomAmmoCategories' other mask changes run in its own Update, outside the memo.
    // Every VerifyEvery-th reuse is traced again and compared; a difference switches the memo off for the session.
    //
    // Fast trace (LineOfSightFastTrace): the same walk as the original, with the same float operations in the same
    // order and the same calls into patchable methods (visCostOfCell, design masks), without filling the shared point
    // list first and walking it again. Every VerifyEvery-th trace runs the original instead and compares; a difference
    // switches the fast trace off for the session.
    //
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
        // A Bresenham line can step one cell past its end point on the minor axis; two cells of margin covers it.
        private const int RayMargin = 2;

        private static readonly AccessTools.FieldRef<LineOfSight, CombatGameState> combat =
            AccessTools.FieldRefAccess<LineOfSight, CombatGameState>("Combat");
        private static Func<LineOfSight, MapTerrainDataCell, float, float> visCostOfCell;

        private static Harmony harmony;
        private static MethodInfo target;
        private static bool patched, patchesChecked, memoOn, fastOn;
        private static int memoVerifyEvery, fastVerifyEvery, memoSinceVerify, fastSinceVerify;

        private static readonly Dictionary<RayKey, float> memo = new Dictionary<RayKey, float>();
        private static readonly HashSet<MapTerrainDataCell> pendingCells = new HashSet<MapTerrainDataCell>(new ByReference());
        private static readonly List<RayKey> dropKeys = new List<RayKey>();
        private static readonly List<int> changedX = new List<int>(), changedZ = new List<int>();
        private static int depth;
        private static string windowName;
        private static long windowStart, windowTraced, windowReused;
        private static long traced, reused, invalidated, cleared, memoVerified, fastTraces, fastVerified;

        internal static void ApplyMemo(Harmony h, int verifyEvery)
        {
            memoVerifyEvery = verifyEvery;
            EnsurePatched(h);
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

            var breakers = new List<MethodBase>
            {
                AccessTools.Method(typeof(ActorMovementSequence), "Update", Type.EmptyTypes),
                AccessTools.Method(typeof(TurnDirector), "EndCurrentRound", Type.EmptyTypes),
            };
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(CombatHUDStatusPanel)))
                if (m.Name == "ShowPreviewMoveIndicators" || m.Name == "ShowPreviewStatuses")
                    breakers.Add(m);
            foreach (var b in breakers)
                h.Patch(b ?? throw new MissingMethodException("line-of-sight memo breaker"),
                    prefix: new HarmonyMethod(typeof(LineOfSightTrace), nameof(Break)) { priority = Priority.Last },
                    finalizer: new HarmonyMethod(typeof(LineOfSightTrace), nameof(BreakAfter)) { priority = Priority.Last });

            var repaints = new[]
            {
                AccessTools.Method(typeof(DropshipGameLogic), "MarkDropshipLandingZone", new[] { typeof(bool) }),
                AccessTools.Method(typeof(RegionGameLogic), "MarkAssociatedCellsAsDangerous", new[] { typeof(bool) }),
                AccessTools.Method(typeof(UnitSpawnPointGameLogic), "PaintDangerousLocationForDropPod", new[] { typeof(bool) }),
                AccessTools.Method(typeof(MapMetaData), "PaintDangerousLocation", new[] { typeof(Rect), typeof(Rect) }),
            };
            foreach (var r in repaints)
                h.Patch(r ?? throw new MissingMethodException("line-of-sight memo repaint"),
                    finalizer: new HarmonyMethod(typeof(LineOfSightTrace), nameof(BreakAfter)) { priority = Priority.Last });

            h.Patch(AccessTools.Method(typeof(ObstructionGameLogic), "BuildingDestroyedUpdateCells", Type.EmptyTypes)
                    ?? throw new MissingMethodException("ObstructionGameLogic.BuildingDestroyedUpdateCells"),
                finalizer: new HarmonyMethod(typeof(LineOfSightTrace), nameof(BuildingDestroyed)) { priority = Priority.Last });
            memoOn = true;
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

        private static void Enter(MethodBase __originalMethod)
        {
            if (depth++ > 0)
                return;
            memo.Clear();
            pendingCells.Clear();
            windowName = __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name;
            windowStart = Stopwatch.GetTimestamp();
            windowTraced = windowReused = 0;
        }

        private static Exception Leave(Exception __exception)
        {
            if (--depth > 0)
                return __exception;
            memo.Clear();
            pendingCells.Clear();
            if (windowTraced + windowReused >= LogMinTraces)
                Log.Write($"[losray] {windowName}: {windowTraced} rays traced, {windowReused} reused, " +
                          $"{(Stopwatch.GetTimestamp() - windowStart) * 1000.0 / Stopwatch.Frequency:F0} ms");
            return __exception;
        }

        private static void Break()
        {
            if (depth == 0 || memo.Count == 0)
                return;
            memo.Clear();
            pendingCells.Clear();
            cleared++;
        }

        private static Exception BreakAfter(Exception __exception)
        {
            Break();
            return __exception;
        }

        private static Exception BuildingDestroyed(ObstructionGameLogic __instance, Exception __exception)
        {
            if (depth == 0 || memo.Count == 0 || __instance.occupiedCells == null)
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
            var unplaced = changedX.Count == 0;
            dropKeys.Clear();
            foreach (var key in memo.Keys)
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
                memo.Remove(key);
            Log.Write($"[losray] building destroyed during a trace window: {pendingCells.Count} cells, " +
                      $"{dropKeys.Count} of {memo.Count + dropKeys.Count} remembered rays dropped");
            invalidated += dropKeys.Count;
            dropKeys.Clear();
            pendingCells.Clear();
        }

        private static bool Prefix(LineOfSight __instance, Point __0, float __1, Point __2, float __3, Vector3 __4, string __5,
            ref float __result, out State __state)
        {
            __state = default;
            if (!patchesChecked)
                CheckPatches();
            if (memoOn && depth > 0)
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
            if (!memoOn || depth == 0)
                return;
            if ((state.Flags & Store) != 0)
            {
                memo[state.Key] = value;
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
            if (traced + reused + fastTraces == 0)
                return;
            Log.Write($"[losray] scene {scene}: memo {reused} of {traced + reused} rays reused, {invalidated} dropped for destroyed buildings, " +
                      $"{cleared} clears, {memoVerified} verified{(memoOn ? "" : " (off)")}; fast trace {fastTraces} rays, {fastVerified} verified{(fastOn ? "" : " (off)")}");
            traced = reused = invalidated = cleared = memoVerified = fastTraces = fastVerified = 0;
        }
    }
}
