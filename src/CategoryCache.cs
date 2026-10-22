using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BattleTech;
using CustomComponents;
using HarmonyLib;

namespace PerfeBT
{
    // CustomComponents.DefaultFixer.FixMechs runs DoDefaultsChange once per (mech, default category), and each run asks
    // CategoriesExtentions.IsCategory(ref, category) for every inventory item, twice (~5.3M calls for 6.9k mechs on save
    // load). Each call re-enumerates the def's custom list with a fresh iterator, closure and LINQ query.
    // During one FixMechs pass component defs' customs do not change, so each def's Category customs are collected once
    // (same order, via the same GetComponents<Category> API) and IsCategory is answered from that array.
    // References CustomComponents types directly; Apply is only reached through Fixes.ApplyGuarded.
    internal static class CategoryCache
    {
        private static Dictionary<MechComponentDef, Category[]> scope;

        // MechComponentRefExtensions.RefreshDef is internal to CustomComponents.
        private static readonly System.Action<BaseComponentRef> RefreshDef =
            AccessTools.MethodDelegate<System.Action<BaseComponentRef>>(AccessTools.Method(typeof(MechComponentRefExtensions), "RefreshDef"));

        internal static void Apply(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(DefaultFixer), "FixMechs"),
                prefix: new HarmonyMethod(typeof(CategoryCache), nameof(FixMechsPrefix)),
                finalizer: new HarmonyMethod(typeof(CategoryCache), nameof(FixMechsFinalizer)));
            harmony.Patch(AccessTools.Method(typeof(CategoriesExtentions), nameof(CategoriesExtentions.IsCategory),
                    new[] { typeof(MechComponentRef), typeof(string) }),
                prefix: new HarmonyMethod(typeof(CategoryCache), nameof(IsCategoryPrefix)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(CategoriesExtentions), nameof(CategoriesExtentions.IsCategory),
                    new[] { typeof(MechComponentRef), typeof(string), typeof(Category).MakeByRefType() }),
                prefix: new HarmonyMethod(typeof(CategoryCache), nameof(IsCategoryOutPrefix)) { priority = Priority.Last });
        }

        private static void FixMechsPrefix() => scope = new Dictionary<MechComponentDef, Category[]>(ReferenceComparer.Instance);

        private static void FixMechsFinalizer() => scope = null;

        private static bool IsCategoryPrefix(MechComponentRef __0, string __1, ref bool __result)
        {
            var cache = scope;
            if (cache == null || !TryCategories(cache, __0, out var categories))
                return true;
            __result = Find(categories, __1) != null;
            return false;
        }

        private static bool IsCategoryOutPrefix(MechComponentRef __0, string __1, ref Category __2, ref bool __result)
        {
            var cache = scope;
            if (cache == null || !TryCategories(cache, __0, out var categories))
                return true;
            __2 = Find(categories, __1);
            __result = __2 != null;
            return false;
        }

        // Mirrors MechComponentRefExtensions.GetComponents<Category>(ref): RefreshDef, then the def's customs.
        // A null ref or unresolved def is left to the original (which fails or behaves exactly as before).
        internal static bool TryCategories(Dictionary<MechComponentDef, Category[]> cache, MechComponentRef item, out Category[] categories)
        {
            categories = null;
            if (item == null)
                return false;
            RefreshDef(item);
            var def = item.Def;
            if (def == null)
                return false;
            if (!cache.TryGetValue(def, out categories))
                cache[def] = categories = def.GetComponents<Category>().ToArray();
            return true;
        }

        // Same predicate as IsCategory's lambda (CategoryID == id, ordinal string equality), first match wins.
        internal static Category Find(Category[] categories, string id)
        {
            foreach (var category in categories)
                if (category.CategoryID == id)
                    return category;
            return null;
        }

        private sealed class ReferenceComparer : IEqualityComparer<MechComponentDef>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(MechComponentDef x, MechComponentDef y) => ReferenceEquals(x, y);
            public int GetHashCode(MechComponentDef obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
