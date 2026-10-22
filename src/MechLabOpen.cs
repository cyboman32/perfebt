using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BattleTech;
using BattleTech.Data;
using HarmonyLib;
using UnityEngine;

namespace PerfeBT
{
    // CustomUnits.DataManager_Exists.Postfix: when Exists(Prefab, id) is false and the id's asset bundle is loaded, the
    // postfix calls bundle.GetAllAssetNames() and compares id against every file stem
    // (String.Compare(id, stem, ignoreCase: true, InvariantCulture)) only to choose between "found" and
    // "LogError + fallback"; __result becomes true either way. That scan runs about once per WeaponDef dependency gather
    // (~1.2k per mech-lab open, more at combat start). It is replaced by an exact cached equivalent: a loaded AssetBundle's
    // asset list is immutable, so per AssetBundle object the stems are computed once and each (bundle, id) answer is
    // memoised. __result and the logs are unchanged. Reloaded bundles are new objects (new keys); destroyed bundles never
    // reach the scan (the postfix's `bundle == null` branch comes first).
    internal static class PrefabBundleScanCache
    {
        private const string TargetType = "CustomUnits.DataManager_Exists";

        // Shadow mode: also run the original scan on every lookup and log any mismatch.
        public static bool Verify;

        private static readonly object Gate = new object();
        private static readonly ConditionalWeakTable<AssetBundle, BundleIndex> Indexes = new ConditionalWeakTable<AssetBundle, BundleIndex>();

        private sealed class BundleIndex
        {
            internal string[] Stems;                                   // null => a stem threw: always use the original loop
            internal Dictionary<string, int> FirstOrdinalIgnoreCase;   // candidate index for the fast positive path
            internal readonly Dictionary<string, bool> Results = new Dictionary<string, bool>(StringComparer.Ordinal);
        }

        internal static void Apply(Harmony harmony)
        {
            var type = AccessTools.TypeByName(TargetType) ?? throw new MissingMemberException(TargetType);
            var postfix = AccessTools.Method(type, "Postfix") ?? throw new MissingMethodException(TargetType, "Postfix");
            harmony.Patch(postfix, transpiler: new HarmonyMethod(typeof(PrefabBundleScanCache), nameof(Transpiler)));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var ps = original.GetParameters();
            if (ps.Length != 4 || ps[2].ParameterType != typeof(string))
                throw new InvalidOperationException("DataManager_Exists.Postfix signature changed");

            var codes = instructions.ToList();
            var getAll = AccessTools.Method(typeof(AssetBundle), nameof(AssetBundle.GetAllAssetNames));
            var stemOf = AccessTools.Method(typeof(Path), nameof(Path.GetFileNameWithoutExtension), new[] { typeof(string) });
            var compare = AccessTools.Method(typeof(string), nameof(string.Compare), new[] { typeof(string), typeof(string), typeof(bool), typeof(CultureInfo) });

            int call = codes.FindIndex(c => c.Calls(getAll));
            if (call < 1 || !codes[call - 1].IsLdloc())
                throw new InvalidOperationException("GetAllAssetNames scan not found");
            int start = call - 1;                                   // ldloc bundle; carries the label of the `bundle != null` branch
            int end = codes.FindIndex(call, c => c.opcode == OpCodes.Blt || c.opcode == OpCodes.Blt_S);
            if (end < 0 || end + 1 >= codes.Count || !codes[end + 1].IsLdloc())
                throw new InvalidOperationException("scan loop end not found");
            var removed = codes.GetRange(start, end - start + 1);
            if (!removed.Any(c => c.Calls(stemOf)) || !removed.Any(c => c.Calls(compare)))
                throw new InvalidOperationException("scan loop shape changed");
            // Labels inside the removed range (except its first instruction) must only be targeted from inside it.
            var inner = new HashSet<Label>(removed.Skip(1).SelectMany(c => c.labels));
            var remaining = codes.Take(start).Concat(codes.Skip(end + 1));
            if (remaining.Any(c => (c.operand is Label l && inner.Contains(l))
                                || (c.operand is Label[] ls && ls.Any(inner.Contains))))
                throw new InvalidOperationException("external branch into scan loop");
            if (codes.Skip(end + 1).Any(c => c.Calls(getAll)))
                throw new InvalidOperationException("more than one GetAllAssetNames scan");
            var foundLoad = codes[end + 1];                         // `ldloc found` right after the loop

            var bundleLoad = codes[start];
            codes.RemoveRange(start, end - start + 1);
            codes.InsertRange(start, new[]
            {
                bundleLoad,
                new CodeInstruction(OpCodes.Ldarg_2),               // id
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PrefabBundleScanCache), nameof(Contains))),
                MechLabOpenIL.StoreTo(foundLoad),
            });
            return codes;
        }

        // == original loop: exists name in bundle.GetAllAssetNames() with
        //    String.Compare(id, Path.GetFileNameWithoutExtension(name), true, InvariantCulture) == 0
        internal static bool Contains(AssetBundle bundle, string id)
        {
            if (id == null)
                return ScanOriginal(bundle, id);
            BundleIndex index;
            lock (Gate)
            {
                if (!Indexes.TryGetValue(bundle, out index))
                {
                    index = Build(bundle);
                    Indexes.Add(bundle, index);
                }
                if (index.Stems != null && index.Results.TryGetValue(id, out var known))
                    return Checked(bundle, id, known);
            }
            if (index.Stems == null)
                return ScanOriginal(bundle, id);
            bool found = Lookup(index, id);
            lock (Gate)
                index.Results[id] = found;
            return Checked(bundle, id, found);
        }

        private static BundleIndex Build(AssetBundle bundle)
        {
            var index = new BundleIndex();
            var names = bundle.GetAllAssetNames();
            var stems = new string[names.Length];
            try
            {
                for (int i = 0; i < names.Length; i++)
                    stems[i] = Path.GetFileNameWithoutExtension(names[i]);
            }
            catch (ArgumentException)
            {
                return index;                                       // Stems stays null: exact fallback (original may throw)
            }
            var first = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < stems.Length; i++)
                if (stems[i] != null && !first.ContainsKey(stems[i]))
                    first.Add(stems[i], i);
            index.FirstOrdinalIgnoreCase = first;
            index.Stems = stems;
            return index;
        }

        private static bool Lookup(BundleIndex index, string id)
        {
            // Fast positive: an ordinal-ignore-case candidate confirmed by the original comparison.
            if (index.FirstOrdinalIgnoreCase.TryGetValue(id, out int i)
                && string.Compare(id, index.Stems[i], true, CultureInfo.InvariantCulture) == 0)
                return true;
            // Otherwise the original comparison over every stem (once per (bundle, id)).
            var stems = index.Stems;
            for (int k = 0; k < stems.Length; k++)
                if (string.Compare(id, stems[k], true, CultureInfo.InvariantCulture) == 0)
                    return true;
            return false;
        }

        // Verbatim original loop (used whenever caching is not provably equivalent, and by Verify).
        private static bool ScanOriginal(AssetBundle bundle, string id)
        {
            foreach (var name in bundle.GetAllAssetNames())
                if (string.Compare(id, Path.GetFileNameWithoutExtension(name), true, CultureInfo.InvariantCulture) == 0)
                    return true;
            return false;
        }

        private static bool Checked(AssetBundle bundle, string id, bool result)
        {
            if (Verify && ScanOriginal(bundle, id) != result)
                Log.Write($"[fix] PrefabBundleScanCache MISMATCH {bundle.name} {id} cached={result}");
            return result;
        }
    }

    // CustomFilters UIHandler.PreloadIcons reads and parses (File.ReadAllText + SVGAsset.Load) every mech-lab filter icon
    // synchronously (49 parses, 40 unique, in RogueTechTabs.json) and then calls SVGCache.AddSVGAsset, which ignores ids
    // already cached. For ids already in the SVGCache the read+parse is skipped. The manifest-null check (which aborts the
    // tab) still runs first, so control flow and cache contents (keys and object identities) are unchanged.
    // SVGAsset.Load only writes import scratch statics that every Load re-initialises before use, so a skipped
    // discarded Load cannot affect later Loads. Only the Debug "Success load SVG" line / errors for cached icons vanish.
    internal static class MechLabIconPreload
    {
        private const string UIHandlerType = "CustomFilters.MechLabFiltering.UIHandler";
        private const string ButtonInfoType = "CustomFilters.MechLabFiltering.TabConfig.ButtonInfo";

        internal static void Apply(Harmony harmony)
        {
            var handler = AccessTools.TypeByName(UIHandlerType) ?? throw new MissingMemberException(UIHandlerType);
            var target = AccessTools.Method(handler, "PreloadIcons") ?? throw new MissingMethodException(UIHandlerType, "PreloadIcons");
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(MechLabIconPreload), nameof(Transpiler)));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il, MethodBase original)
        {
            var codes = instructions.ToList();
            var iconCache = AccessTools.Field(original.DeclaringType, "_iconCache") ?? throw new MissingFieldException(UIHandlerType, "_iconCache");
            var buttonInfo = AccessTools.TypeByName(ButtonInfoType) ?? throw new MissingMemberException(ButtonInfoType);
            var buttonIcon = AccessTools.Field(buttonInfo, "Icon") ?? throw new MissingFieldException(ButtonInfoType, "Icon");
            var getFilePath = AccessTools.PropertyGetter(typeof(VersionManifestEntry), nameof(VersionManifestEntry.FilePath));
            var fileExists = AccessTools.Method(typeof(File), nameof(File.Exists), new[] { typeof(string) });
            var contains = AccessTools.Method(typeof(SVGCache), nameof(SVGCache.Contains), new[] { typeof(string) });
            if (iconCache.FieldType != typeof(SVGCache))
                throw new InvalidOperationException("UIHandler._iconCache type changed");

            int iconLoad = codes.FindIndex(c => c.LoadsField(buttonIcon));
            if (iconLoad < 0 || iconLoad + 1 >= codes.Count || !codes[iconLoad + 1].IsStloc())
                throw new InvalidOperationException("icon local not found");
            var iconStore = codes[iconLoad + 1];

            int at = -1;                                            // `ldloc entry; callvirt get_FilePath` (first use after the null check)
            for (int i = iconLoad; i + 1 < codes.Count; i++)
                if (codes[i].IsLdloc() && codes[i + 1].Calls(getFilePath)) { at = i; break; }
            if (at < 0)
                throw new InvalidOperationException("FilePath use not found");
            int exists = codes.FindIndex(at, c => c.Calls(fileExists));
            int cont = exists < 0 ? -1 : codes.FindIndex(exists, c => c.opcode == OpCodes.Leave || c.opcode == OpCodes.Leave_S);
            if (cont < 0)
                throw new InvalidOperationException("continue-leave not found");
            var next = (Label)codes[cont].operand;                  // loop increment (same target as `!File.Exists` -> continue)

            var load = il.DefineLabel();
            var first = new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(codes[at]);
            codes[at].labels.Add(load);
            codes.InsertRange(at, new[]
            {
                first,
                new CodeInstruction(OpCodes.Ldfld, iconCache),
                MechLabOpenIL.LoadFrom(iconStore),
                new CodeInstruction(OpCodes.Callvirt, contains),
                new CodeInstruction(OpCodes.Brfalse, load),
                new CodeInstruction(OpCodes.Leave, next),
            });
            return codes;
        }
    }

    // Harmony hands short-form local accesses (ldloc.s/stloc.s) over with a LocalBuilder operand, for which
    // CodeInstructionExtensions.LocalIndex() throws in this HarmonyX build; mirror the access with the same operand.
    internal static class MechLabOpenIL
    {
        internal static CodeInstruction StoreTo(CodeInstruction load)
            => load.operand is LocalBuilder lb ? new CodeInstruction(OpCodes.Stloc, lb) : CodeInstruction.StoreLocal(load.LocalIndex());

        internal static CodeInstruction LoadFrom(CodeInstruction store)
            => store.operand is LocalBuilder lb ? new CodeInstruction(OpCodes.Ldloc, lb) : CodeInstruction.LoadLocal(store.LocalIndex());
    }
}
