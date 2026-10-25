using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace PerfeBT
{
    // HBS.Util.JSONSerializationUtility deserializes every def (~35k files, ~4.5M member sets on a RogueTech save load)
    // by reflection. Per member it calls Type.GetInterfaces() (a fresh array walk on Mono) + Enumerable.Contains, and
    // MemberInfoHelper.SetValue calls IsValueType(target type), which formats "{Assembly.FullName}.{Name}" (Mono
    // allocates FullName on every call) to look up a string-keyed cache.
    // Fixes, all result-identical:
    // - GetInterfaces/GetGenericArguments in RehydrateObjectFromDictionary and ConvertValue come from a per-Type cache
    //   (both callers only read the arrays: Contains, First, indexing).
    // - Enumerable.Contains<Type> there uses a plain loop over the array with the same default comparer.
    // - IsValueType results are memoized per Type. Vanilla stores the first computed value per string key and never
    //   changes it, so the value seen for a Type is fixed after its first call; the memo returns exactly that.
    internal static class JsonReflection
    {
        private static readonly ConcurrentDictionary<Type, Type[]> interfaces = new ConcurrentDictionary<Type, Type[]>();
        private static readonly ConcurrentDictionary<Type, Type[]> genericArguments = new ConcurrentDictionary<Type, Type[]>();
        private static readonly ConcurrentDictionary<Type, bool> isValueType = new ConcurrentDictionary<Type, bool>();
        private static readonly Func<Type, Type[]> getInterfaces = t => t.GetInterfaces();
        private static readonly Func<Type, Type[]> getGenericArguments = t => t.GetGenericArguments();

        private static readonly MethodInfo TypeGetInterfaces = AccessTools.Method(typeof(Type), nameof(Type.GetInterfaces));
        private static readonly MethodInfo TypeGetGenericArguments = AccessTools.Method(typeof(Type), nameof(Type.GetGenericArguments));
        private static readonly MethodInfo EnumerableContainsType = typeof(Enumerable).GetMethods()
            .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(Type));

        internal static void Apply(Harmony harmony)
        {
            var utility = AccessTools.TypeByName("HBS.Util.JSONSerializationUtility");
            var rehydrate = utility.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Single(m => m.Name == "RehydrateObjectFromDictionary" && m.GetParameters().Length == 7);
            var convert = AccessTools.Method(utility, "ConvertValue");
            var transpiler = new HarmonyMethod(typeof(JsonReflection), nameof(Transpiler));
            harmony.Patch(rehydrate, transpiler: transpiler);
            harmony.Patch(convert, transpiler: transpiler);

            var helper = AccessTools.TypeByName("HBS.Reflection.MemberInfoHelper");
            harmony.Patch(AccessTools.Method(helper, "IsValueType", new[] { typeof(Type) }),
                prefix: new HarmonyMethod(typeof(JsonReflection), nameof(IsValueTypePrefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(JsonReflection), nameof(IsValueTypePostfix)) { priority = Priority.Last });
        }

        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            int replaced = 0;
            foreach (var ins in instructions)
            {
                if ((ins.opcode == OpCodes.Callvirt || ins.opcode == OpCodes.Call) && ins.operand is MethodInfo m)
                {
                    MethodInfo with = m == TypeGetInterfaces ? AccessTools.Method(typeof(JsonReflection), nameof(GetInterfaces))
                        : m == TypeGetGenericArguments ? AccessTools.Method(typeof(JsonReflection), nameof(GetGenericArguments))
                        : m == EnumerableContainsType ? AccessTools.Method(typeof(JsonReflection), nameof(Contains))
                        : null;
                    if (with != null)
                    {
                        ins.opcode = OpCodes.Call;
                        ins.operand = with;
                        replaced++;
                    }
                }
                yield return ins;
            }
            Log.Write($"[fix] JsonReflection: {__originalMethod?.Name} {replaced} reflection calls cached");
        }

        public static Type[] GetInterfaces(Type type) => interfaces.GetOrAdd(type, getInterfaces);

        public static Type[] GetGenericArguments(Type type) => genericArguments.GetOrAdd(type, getGenericArguments);

        public static bool Contains(IEnumerable<Type> source, Type value)
        {
            if (!(source is Type[] array))
                return source.Contains(value);
            var comparer = EqualityComparer<Type>.Default;
            foreach (var t in array)
                if (comparer.Equals(t, value))
                    return true;
            return false;
        }

        private static bool IsValueTypePrefix(Type __0, ref bool __result) => __0 == null || !isValueType.TryGetValue(__0, out __result);

        private static void IsValueTypePostfix(Type __0, bool __result)
        {
            if (__0 != null)
                isValueType.TryAdd(__0, __result);
        }
    }
}
