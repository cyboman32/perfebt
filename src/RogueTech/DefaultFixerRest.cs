using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BattleTech;
using CustomComponents;
using CustomComponents.Changes;
using HarmonyLib;

namespace PerfeBT
{
    // CustomComponents.DefaultFixer.DoDefaultsChange runs once per (mech, adjusted category) during FixMechs (~150k calls
    // on save load). Its main branch builds usage/free records with LINQ, allocates a closure + delegate per inventory
    // item and boxes twice per Enum.HasFlag (~100 objects per call). This prefix performs the same computation with
    // reusable arrays: same IsCategory calls (same items, same order, same public overload, so CategoryCache still
    // applies) and same AddChange calls (same order and arguments). Trace logging on, the "clearing" branch, or any null
    // input -> CC's original runs unchanged. Nothing is cached across calls.
    internal static class DefaultsChangeFast
    {
        private static Func<bool> traceOn; // CustomComponents.Log.DefaultHandle.Trace != null, read live on every call

        [ThreadStatic] private static Scratch pooled;

        internal static void Apply(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(DefaultFixer), "DoDefaultsChange",
                             new[] { typeof(InventoryOperationState), typeof(string) })
                         ?? throw new MissingMethodException("CustomComponents.DefaultFixer", "DoDefaultsChange");
            var log = AccessTools.TypeByName("CustomComponents.Log")
                      ?? throw new TypeLoadException("CustomComponents.Log");
            var handle = AccessTools.Field(log, "DefaultHandle")
                         ?? throw new MissingFieldException("CustomComponents.Log", "DefaultHandle");
            var trace = AccessTools.PropertyGetter(handle.FieldType, "Trace")
                        ?? throw new MissingMethodException(handle.FieldType.FullName, "get_Trace");

            // Same IL as CC's log sites: ldsfld DefaultHandle; callvirt get_Trace; != null.
            var dm = new DynamicMethod("PerfeBT_DefaultHandleTraceOn", typeof(bool), Type.EmptyTypes, typeof(DefaultFixer), true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldsfld, handle);
            il.Emit(OpCodes.Callvirt, trace);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Cgt_Un);
            il.Emit(OpCodes.Ret);
            traceOn = (Func<bool>)dm.CreateDelegate(typeof(Func<bool>));

            harmony.Patch(target, prefix: new HarmonyMethod(typeof(DefaultsChangeFast), nameof(Prefix)));
        }

        private static bool Prefix(InventoryOperationState __0, string __1)
        {
            if (traceOn()) return true; // keep CC's trace output identical

            var state = __0;
            var categoryId = __1;
            if (state == null || categoryId == null) return true;

            var info = DefaultsDatabase.Instance[state.Mech];
            var byCategory = info?.Defaults;
            if (byCategory == null || !byCategory.TryGetValue(categoryId, out var catDefault) || catDefault == null)
                return true; // clearing branch, or an input CC would throw on
            var records = catDefault.Defaults;
            var limits = catDefault.CategoryRecord?.LocationLimits;
            var inventory = state.Inventory;
            if (records == null || limits == null || inventory == null) return true;

            // Validate everything the fast path dereferences before any side effect; otherwise the original runs, so
            // exception behaviour stays exactly CC's.
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                if (r?.Item?.Description == null || r.Category == null) return true;
            }
            for (int i = 0; i < inventory.Count; i++)
                if (inventory[i]?.Item == null) return true;

            var s = pooled;
            if (s == null || s.Busy) s = new Scratch(); // re-entrancy safe
            s.Busy = true;
            try
            {
                Run(state, categoryId, records, limits, inventory, s);
            }
            finally
            {
                s.Release(records.Count);
                pooled = s;
            }
            return false;
        }

        private static void Run(InventoryOperationState state, string categoryId, List<CategoryDefaultRecord> records,
            Dictionary<ChassisLocations, CategoryLimit> limits, List<InvItem> inventory, Scratch s)
        {
            int u = records.Count;
            s.Reserve(u, limits.Count);
            var rec = s.Rec;         // usage_record.crecord
            var used = s.Used;       // usage_record.item (used_now == item != null)
            var freeLoc = s.FreeLoc; // free_record.location
            var free = s.Free;       // free_record.free
            var pendIdx = s.PendIdx;
            var pendW = s.PendW;

            // usage = Defaults.Select(r => new usage_record(r)).ToList()
            for (int i = 0; i < u; i++)
            {
                rec[i] = records[i];
                used[i] = null;
            }

            // free = LocationLimits.Where(kv => kv.Value.Limited).Select(kv => (kv.Key, kv.Value.Limit)).ToList()
            int f = 0;
            foreach (var kv in limits)
            {
                var limit = kv.Value;
                if (limit.Limited)
                {
                    freeLoc[f] = kv.Key;
                    free[f] = limit.Limit;
                    f++;
                }
            }

            // Loop 1: claim matching inventory items; subtract the category weight of the others from matching free records.
            foreach (var invItem in inventory)
            {
                var item = invItem.Item;
                var id = item.ComponentDefID;
                var location = invItem.Location;
                int match = -1;
                for (int i = 0; i < u; i++) // usage.FirstOrDefault(b__3)
                {
                    var r = rec[i];
                    if (r.Item.Description.Id == id && r.Location == location && used[i] == null)
                    {
                        match = i;
                        break;
                    }
                }
                if (match >= 0)
                {
                    used[match] = item;
                    continue;
                }
                if (CategoriesExtentions.IsCategory(item, categoryId, out Category category))
                    for (int j = 0; j < f; j++)
                        if ((freeLoc[j] & location) == location) // == Enum.HasFlag (b__4)
                            free[j] -= category.Weight;
            }

            // Loop 2: used_after per usage record; same changes, same order.
            for (int i = 0; i < u; i++)
            {
                var r = rec[i];
                int pending = 0;
                bool fit = true;
                for (int j = 0; j < f; j++)
                {
                    var location = r.Location;
                    if ((freeLoc[j] & location) != location) continue; // b__5
                    if (free[j] >= r.Category.Weight)
                    {
                        pendIdx[pending] = j;
                        pendW[pending] = r.Category.Weight;
                        pending++;
                    }
                    else
                    {
                        fit = false;
                        break;
                    }
                }
                fit = fit && pending > 0;
                if (fit)
                    for (int p = 0; p < pending; p++)
                        free[pendIdx[p]] -= pendW[p];

                if (fit != (used[i] != null))
                {
                    if (fit) state.AddChange(new Change_Add(r.Item.Description.Id, r.Item.ComponentType, r.Location));
                    else state.AddChange(new Change_Remove(r.Item.Description.Id, r.Location));
                }
            }
        }

        private sealed class Scratch
        {
            internal bool Busy;
            internal CategoryDefaultRecord[] Rec = new CategoryDefaultRecord[8];
            internal MechComponentRef[] Used = new MechComponentRef[8];
            internal ChassisLocations[] FreeLoc = new ChassisLocations[8];
            internal int[] Free = new int[8];
            internal int[] PendIdx = new int[8];
            internal int[] PendW = new int[8];

            internal void Reserve(int usage, int limits)
            {
                if (Rec.Length < usage)
                {
                    Rec = new CategoryDefaultRecord[usage];
                    Used = new MechComponentRef[usage];
                }
                if (Free.Length < limits)
                {
                    FreeLoc = new ChassisLocations[limits];
                    Free = new int[limits];
                    PendIdx = new int[limits];
                    PendW = new int[limits];
                }
            }

            internal void Release(int usage)
            {
                Array.Clear(Rec, 0, Math.Min(usage, Rec.Length)); // don't keep defs/refs alive between calls
                Array.Clear(Used, 0, Math.Min(usage, Used.Length));
                Busy = false;
            }
        }
    }

    // CustomComponents sorts every MechDef.SetInventory array with SorterUtils.InventoryComparer, which calls
    // def.GetComponent<ISorter>() (a linear isinst scan of the def's customs) twice per comparison (~3.4M calls on save
    // load). This comparer returns exactly the same integers (same Order arithmetic, same live Index scan over the array
    // being sorted), but evaluates GetComponent<ISorter>()?.Order once per distinct def per sort. The memo lives for one
    // Array.Sort call, during which only the comparer runs.
    internal static class InventorySortFast
    {
        internal static void Apply(Harmony harmony)
        {
            var target = AccessTools.Method("CustomComponents.SorterUtils:SortMechDefInventory", new[] { typeof(MechComponentRef[]) })
                         ?? throw new MissingMethodException("CustomComponents.SorterUtils", "SortMechDefInventory");
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(InventorySortFast), nameof(Prefix)));
        }

        private static bool Prefix(MechComponentRef[] __0)
        {
            Sort(__0);
            return false;
        }

        internal static void Sort(MechComponentRef[] inventory) => Array.Sort(inventory, new Comparer(inventory));

        private sealed class Comparer : IComparer<MechComponentRef>
        {
            private readonly MechComponentRef[] originalOrder; // the array being sorted, as in CC
            private readonly Dictionary<MechComponentDef, int?> sorterOrder =
                new Dictionary<MechComponentDef, int?>(ReferenceComparer.Instance);

            internal Comparer(MechComponentRef[] array) { originalOrder = array; }

            public int Compare(MechComponentRef x, MechComponentRef y)
            {
                int result = Order(x?.Def) - Order(y?.Def);
                return result != 0 ? result : Index(x) - Index(y);
            }

            private int Order(MechComponentDef def)
            {
                int? value = null;
                if (def != null && !sorterOrder.TryGetValue(def, out value))
                {
                    var sorter = def.GetComponent<ISorter>();
                    value = sorter == null ? (int?)null : sorter.Order;
                    sorterOrder.Add(def, value);
                }
                return value ?? Control.Settings.SorterMechInventoryDefault; // read live, as CC does
            }

            private int Index(MechComponentRef item) // live scan, reference equality, -1 if absent
            {
                var array = originalOrder;
                for (int i = 0; i < array.Length; i++)
                    if ((object)array[i] == item) return i;
                return -1;
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<MechComponentDef>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(MechComponentDef x, MechComponentDef y) => ReferenceEquals(x, y);
            public int GetHashCode(MechComponentDef obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }

    // InventoryOperationState.DoChanges skips a dequeued adjust change when
    //   pending_changes.Any(c => c is IChange_Adjust a && a.ChangeID == adj.ChangeID)
    // and Change_CategoryAdjust.ChangeID is "Category_" + CategoryID, so every element test builds two strings (~5.5M on
    // save load). The transpiler swaps the closure delegate + Enumerable.Any for AnySameChangeId(pending_changes, adj),
    // which compares CategoryIDs directly when both sides are exactly Change_CategoryAdjust and evaluates the original
    // lambda body otherwise. Same elements, same order, same short-circuit.
    internal static class PendingAdjustScan
    {
        private static Type closureType;
        private static FieldInfo adjField;

        internal static void Apply(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(InventoryOperationState), "DoChanges")
                         ?? throw new MissingMethodException("CustomComponents.InventoryOperationState", "DoChanges");
            closureType = AccessTools.Inner(typeof(InventoryOperationState), "<>c__DisplayClass11_0")
                          ?? throw new TypeLoadException("CustomComponents.InventoryOperationState+<>c__DisplayClass11_0");
            adjField = AccessTools.Field(closureType, "adj")
                       ?? throw new MissingFieldException(closureType.FullName, "adj");

            // The direct CategoryID comparison is only equivalent while ChangeID is CC's own "Category_" + CategoryID.
            var changeId = AccessTools.PropertyGetter(typeof(Change_CategoryAdjust), nameof(Change_CategoryAdjust.ChangeID))
                           ?? throw new MissingMethodException(typeof(Change_CategoryAdjust).FullName, "get_ChangeID");
            var patches = Harmony.GetPatchInfo(changeId);
            if (patches != null && (patches.Prefixes.Count > 0 || patches.Postfixes.Count > 0 || patches.Transpilers.Count > 0
                                    || patches.Finalizers.Count > 0))
                throw new InvalidOperationException("Change_CategoryAdjust.get_ChangeID is patched by " +
                                                    string.Join(", ", patches.Owners.ToArray()));

            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(PendingAdjustScan), nameof(Transpiler)));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var helper = AccessTools.Method(typeof(PendingAdjustScan), nameof(AnySameChangeId));
            int hits = 0;
            for (int i = 2; i < code.Count; i++)
            {
                // ldfld pending_changes; ldloc closure; ldftn <DoChanges>b__0; newobj Func; call Enumerable.Any<IChange>
                if (code[i - 2].opcode == OpCodes.Ldftn && code[i - 2].operand is MethodInfo lambda
                    && lambda.DeclaringType == closureType && lambda.Name == "<DoChanges>b__0"
                    && code[i - 1].opcode == OpCodes.Newobj
                    && code[i].opcode == OpCodes.Call && code[i].operand is MethodInfo any
                    && any.DeclaringType == typeof(Enumerable) && any.Name == nameof(Enumerable.Any)
                    && any.GetParameters().Length == 2)
                {
                    code[i - 2].opcode = OpCodes.Ldfld; // closure -> closure.adj
                    code[i - 2].operand = adjField;
                    code[i - 1].opcode = OpCodes.Nop;
                    code[i - 1].operand = null;
                    code[i].operand = helper; // AnySameChangeId(pending_changes, adj)
                    hits++;
                }
            }
            if (hits != 1)
                throw new InvalidOperationException("PerfeBT PendingAdjustScan: expected 1 Any(<DoChanges>b__0) site in DoChanges, found " + hits);
            return code;
        }

        // Exactly Enumerable.Any(source, c => c is IChange_Adjust a && a.ChangeID == adj.ChangeID); DoChanges only calls
        // it with adj != null.
        public static bool AnySameChangeId(IEnumerable<IChange> source, IChange_Adjust adj)
        {
            if (source == null) throw new ArgumentNullException(nameof(source)); // Enumerable.Any contract
            bool fast = adj.GetType() == typeof(Change_CategoryAdjust);
            string key = fast ? ((Change_CategoryAdjust)adj).CategoryID ?? string.Empty : null;
            if (source is Queue<IChange> queue)
            {
                foreach (var c in queue) // struct enumerator, no boxing
                    if (Matches(c, adj, fast, key)) return true;
                return false;
            }
            foreach (var c in source)
                if (Matches(c, adj, fast, key)) return true;
            return false;
        }

        private static bool Matches(IChange c, IChange_Adjust adj, bool fast, string key)
        {
            if (!(c is IChange_Adjust a)) return false;
            if (fast && a.GetType() == typeof(Change_CategoryAdjust))
                // "Category_" + x == "Category_" + y  <=>  (x ?? "") == (y ?? "")  (ordinal; Concat treats null as "")
                return string.Equals(((Change_CategoryAdjust)a).CategoryID ?? string.Empty, key);
            return a.ChangeID == adj.ChangeID; // the original lambda body for every other pairing
        }
    }
}
