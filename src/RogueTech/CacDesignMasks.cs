using System;
using System.Linq.Expressions;
using BattleTech;
using HarmonyLib;

namespace PerfeBT
{
    // CustomAmmoCategories' MapMetaData.GetPriorityDesignMask postfix returns a cell's temporary design mask (mines,
    // smoke, ...) or its burned-ground mask, and none at all while ActorMovementSequence_UpdateSticky's
    // bHIDE_DESIGN_MASK_FLAG is set (CAC sets it in the movement update and the move previews, CustomUnits in
    // AbstractActor.OnPositionUpdate for units unaffected by design masks). Those masks are written only by
    // MapTerrainDataCellEx.AddDesignMask, ReconstructTempDesignMask, burn and burnTick (and the cell constructor at map
    // load); after each of them the sight-line memo is dropped, and while the flag is set it is not used.
    internal static class CacDesignMasks
    {
        private const string CellType = "CustAmmoCategories.MapTerrainDataCellEx";
        private const string StickyType = "CustAmmoCategoriesPatches.ActorMovementSequence_UpdateSticky";

        internal static void Apply(Harmony harmony)
        {
            var cell = AccessTools.TypeByName(CellType);
            if (cell == null)
                return;
            var sticky = AccessTools.TypeByName(StickyType) ?? throw new TypeLoadException(StickyType);
            var flag = AccessTools.Field(sticky, "bHIDE_DESIGN_MASK_FLAG");
            if (flag == null || !flag.IsStatic || flag.FieldType != typeof(bool))
                throw new MissingFieldException(StickyType, "bHIDE_DESIGN_MASK_FLAG");
            var hidden = Expression.Lambda<Func<bool>>(Expression.Field(null, flag)).Compile();

            var writers = new[]
            {
                AccessTools.Method(cell, "AddDesignMask", new[] { typeof(DesignMaskDef), typeof(int) }),
                AccessTools.Method(cell, "ReconstructTempDesignMask", Type.EmptyTypes),
                AccessTools.Method(cell, "burn", new[] { typeof(Weapon), typeof(int), typeof(int) }),
                AccessTools.Method(cell, "burnTick", Type.EmptyTypes),
            };
            foreach (var w in writers)
                if (w == null)
                    throw new MissingMethodException(CellType, "design-mask writer");
            foreach (var w in writers)
                harmony.Patch(w, finalizer: new HarmonyMethod(typeof(LineOfSightTrace), nameof(LineOfSightTrace.ClearAfter)) { priority = Priority.Last });
            LineOfSightTrace.RegisterMaskSource(cell.Assembly.GetName().Name, hidden);
        }
    }
}
