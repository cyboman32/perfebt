using BattleTech;
using CustomComponents;
using HarmonyLib;
using HBS.Collections;

namespace PerfeBT
{
    // CustomComponents.UnitTypeDatabase.BuildUnitTypes(mech) asks every registered unit type IsThisType(mech).
    // TagUnitType.IsThisType builds a fresh TagSet = MechTags ∪ ChassisTags on every call (27 unit types x 6.9k
    // mechs on save load: 243k unions, ~5 s). Within one BuildUnitTypes call the mech's tags cannot change, so the
    // union is built once per call and the original Required/Forbidden/Any logic is evaluated against it.
    // References CustomComponents types directly; Apply is only reached through a guarded call in Fixes.
    internal static class UnitTypeTags
    {
        private static MechDef scopeMech;
        private static TagSet scopeTags;

        internal static void Apply(Harmony harmony)
        {
            Fixes.Precompile(typeof(UnitTypeTags));
            harmony.Patch(AccessTools.Method(typeof(UnitTypeDatabase), nameof(UnitTypeDatabase.BuildUnitTypes), new[] { typeof(MechDef) }),
                prefix: new HarmonyMethod(typeof(UnitTypeTags), nameof(BuildPrefix)),
                finalizer: new HarmonyMethod(typeof(UnitTypeTags), nameof(BuildFinalizer)));
            harmony.Patch(AccessTools.Method(typeof(TagUnitType), nameof(TagUnitType.IsThisType)),
                prefix: new HarmonyMethod(typeof(UnitTypeTags), nameof(IsThisTypePrefix)) { priority = Priority.Last });
        }

        private static void BuildPrefix(MechDef __0)
        {
            scopeMech = __0;
            scopeTags = null;
        }

        private static void BuildFinalizer()
        {
            scopeMech = null;
            scopeTags = null;
        }

        private static bool IsThisTypePrefix(TagUnitType __instance, MechDef __0, ref bool __result)
        {
            if (__0 == null || !ReferenceEquals(__0, scopeMech) || __0.Chassis == null)
                return true;
            scopeTags ??= Union(__0);
            __result = Matches(__instance, scopeTags);
            return false;
        }

        // Same construction as TagUnitType.IsThisType.
        internal static TagSet Union(MechDef mech)
        {
            var tags = new TagSet();
            if (mech.MechTags != null)
                tags.UnionWith(mech.MechTags);
            if (mech.Chassis.ChassisTags != null)
                tags.UnionWith(mech.Chassis.ChassisTags);
            return tags;
        }

        // Same evaluation order and semantics as TagUnitType.IsThisType.
        internal static bool Matches(TagUnitType type, TagSet tags)
        {
            var required = type.RequiredTags;
            if (required != null && required.Length != 0)
                foreach (var tag in required)
                    if (!tags.Contains(tag))
                        return false;
            var forbidden = type.ForbiddenTags;
            if (forbidden != null && forbidden.Length != 0)
                foreach (var tag in forbidden)
                    if (tags.Contains(tag))
                        return false;
            var any = type.AnyTags;
            if (any == null || any.Length == 0)
                return true;
            foreach (var tag in any)
                if (tags.Contains(tag))
                    return true;
            return false;
        }
    }
}
