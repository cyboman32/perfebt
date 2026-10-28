using System;
using BattleTech;
using HarmonyLib;

namespace PerfeBT
{
    // CustomAmmoCategories' MapMetaData.GetPriorityDesignMask postfix returns a cell's temporary design mask (mines,
    // smoke, ...) or its burned-ground mask, and none at all while ActorMovementSequence_UpdateSticky's
    // bHIDE_DESIGN_MASK_FLAG is set (CAC sets it in the movement update and the move previews, CustomUnits in
    // AbstractActor.OnPositionUpdate for units unaffected by design masks). Those masks are written only by
    // MapTerrainDataCellEx.AddDesignMask, ReconstructTempDesignMask, burn and burnTick (and the cell constructor at map
    // load), and each only changes its own cell. Most calls change nothing: burnTick counts down until the fire goes
    // out, ReconstructTempDesignMask runs every round for every smoke or mine cell, AddDesignMask on a present mask only
    // extends it. Dropping the whole sight-line memo after each call made the next rebuild trace every ray again
    // (4 s urban round starts and deaths instead of 0.5 s). The mask is a function of the cell's terrainMask,
    // tempDesignMask and CustomDesignMask (cachedHeight is never written here): they are compared before and after
    // each call, and only a cell whose mask inputs changed is reported. While the flag is set the memo is not used.
    internal static class CacDesignMasks
    {
        internal static void Apply(Harmony harmony)
        {
            if (!ModAssemblies.Loaded("CustomAmmoCategories"))
                return;
            Fixes.Precompile(typeof(Cac));
            Cac.Apply(harmony);
        }

        // Uses CustomAmmoCategories members directly; only reached (and precompiled) when its assembly is loaded.
        private static class Cac
        {
            internal static void Apply(Harmony harmony)
            {
                var cell = typeof(CustAmmoCategories.MapTerrainDataCellEx);
                var writers = new[]
                {
                    AccessTools.Method(cell, nameof(CustAmmoCategories.MapTerrainDataCellEx.AddDesignMask), new[] { typeof(DesignMaskDef), typeof(int) }),
                    AccessTools.Method(cell, nameof(CustAmmoCategories.MapTerrainDataCellEx.ReconstructTempDesignMask), Type.EmptyTypes),
                    AccessTools.Method(cell, nameof(CustAmmoCategories.MapTerrainDataCellEx.burn), new[] { typeof(Weapon), typeof(int), typeof(int) }),
                    AccessTools.Method(cell, nameof(CustAmmoCategories.MapTerrainDataCellEx.burnTick), Type.EmptyTypes),
                };
                // nameof checks the names only; a changed parameter list returns null. Patch all four or none.
                foreach (var w in writers)
                    if (w == null)
                        throw new MissingMethodException("CustAmmoCategories.MapTerrainDataCellEx", "design-mask writer");
                foreach (var w in writers)
                    harmony.Patch(w,
                        prefix: new HarmonyMethod(typeof(Cac), nameof(Before)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(Cac), nameof(After)) { priority = Priority.Last });
                LineOfSightTrace.RegisterMaskSource(cell.Assembly.GetName().Name, Hidden);
            }

            private struct MaskInputs
            {
                internal TerrainMaskFlags Terrain;
                internal DesignMaskDef Temp, Custom;
            }

            private static void Before(CustAmmoCategories.MapTerrainDataCellEx __instance, out MaskInputs __state)
            {
                __state = new MaskInputs { Terrain = __instance.terrainMask, Temp = __instance.tempDesignMask, Custom = __instance.CustomDesignMask };
            }

            // CAC indexes mapTerrainDataCells[x, y]: x is the row.
            private static Exception After(CustAmmoCategories.MapTerrainDataCellEx __instance, MaskInputs __state, Exception __exception)
            {
                if (__instance.terrainMask != __state.Terrain || !ReferenceEquals(__instance.tempDesignMask, __state.Temp) ||
                    !ReferenceEquals(__instance.CustomDesignMask, __state.Custom))
                    LineOfSightTrace.DesignMaskChanged(__instance, __instance.x, __instance.y);
                return __exception;
            }

            private static bool Hidden() => CustAmmoCategoriesPatches.ActorMovementSequence_UpdateSticky.bHIDE_DESIGN_MASK_FLAG;
        }
    }
}
