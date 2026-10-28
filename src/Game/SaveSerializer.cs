using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BattleTech;
using BattleTech.Save;
using BattleTech.Serialization;
using BattleTech.Serialization.Factories;
using BattleTech.Serialization.Handlers;
using BattleTech.Serialization.Models;
using BattleTech.Serialization.Utility;
using Google.Protobuf;
using HarmonyLib;

namespace PerfeBT
{
    // Saves: ProtobufSerialization.SerializeClass, which builds every object's message (18,350 per Argo save), spent
    // 746 ms of a 2.1 s PreSerialization on its own, outside stat writing, protobuf writes and copies. Each class looks
    // up its contract, factory and type id by scanning the serializer's tables front to back with a LINQ predicate
    // (DataContractManager over every contract, ClassTypeResolver over every registered class, SerializationFactory-
    // Manager over every factory's handled types), and every list field finds Enumerable.ToArray by name and
    // instantiates it by reflection. The tables are filled once by Serializer.PrepareSerializer. Here each lookup
    // returns the first matching entry from a dictionary built in table order, so it is the entry the scan finds. A
    // table is rebuilt when its list or count changes, and is not cached when it holds a null entry (the scan would
    // throw). Lookups that find nothing, or of a null type, run the original (which logs or throws as before). Every
    // VerifyEvery-th lookup also scans the table and compares; a difference switches the caches off for the session.
    // Off if another mod patches one of the lookups.
    internal static class SaveSerializerLookups
    {
        private sealed class Table<T> where T : class
        {
            internal object List;
            internal int Count;
            internal bool Cacheable;
            internal readonly Dictionary<Type, T> ByType = new Dictionary<Type, T>();
        }

        private static readonly Table<DataContract> contracts = new Table<DataContract>();
        private static readonly Table<ClassReferenceMapping> classes = new Table<ClassReferenceMapping>();
        private static readonly Table<SerializationFactoryMapping> factories = new Table<SerializationFactoryMapping>();
        private static readonly Dictionary<KeyValuePair<Type, string>, MethodInfo> methods = new Dictionary<KeyValuePair<Type, string>, MethodInfo>();
        private static readonly Dictionary<KeyValuePair<MethodInfo, Type>, MethodInfo> generics = new Dictionary<KeyValuePair<MethodInfo, Type>, MethodInfo>();

        private static Harmony harmony;
        private static List<MethodBase> patched;
        private static bool on, checkedPatches;
        private static int verifyEvery, sinceVerify;
        private static long hits, verified;

        internal static void Apply(Harmony h, int verify)
        {
            harmony = h;
            verifyEvery = verify;
            var targets = new (MethodBase method, string prefix)[]
            {
                (AccessTools.Method(typeof(DataContractManager), nameof(DataContractManager.ContainsContract), new[] { typeof(Type) }), nameof(ContainsContract)),
                (AccessTools.Method(typeof(DataContractManager), nameof(DataContractManager.GetContract), new[] { typeof(Type) }), nameof(GetContract)),
                (AccessTools.Method(typeof(DataContractManager), nameof(DataContractManager.GetClassID), new[] { typeof(Type) }), nameof(GetClassID)),
                (AccessTools.Method(typeof(ClassTypeResolver), nameof(ClassTypeResolver.HasType), new[] { typeof(Type) }), nameof(HasType)),
                (AccessTools.Method(typeof(ClassTypeResolver), nameof(ClassTypeResolver.GetTypeIdentifier), new[] { typeof(Type) }), nameof(GetTypeIdentifier)),
                (AccessTools.Method(typeof(SerializationFactoryManager), nameof(SerializationFactoryManager.CanHandleType), new[] { typeof(Type) }), nameof(CanHandleType)),
                (AccessTools.Method(typeof(SerializationFactoryManager), nameof(SerializationFactoryManager.GetFactoryForType), new[] { typeof(Type) }), nameof(GetFactoryForType)),
                (AccessTools.Method(typeof(SerializationFactoryManager), nameof(SerializationFactoryManager.GetFactoryType), new[] { typeof(Type) }), nameof(GetFactoryType)),
            };
            var arraySerialize = AccessTools.Method(typeof(ArraySerializationHandler), nameof(ArraySerializationHandler.Serialize));
            if (targets.Any(t => t.method == null) || arraySerialize == null)
                throw new MissingMethodException("serializer lookups");
            patched = targets.Select(t => t.method).Append(arraySerialize).ToList();
            foreach (var (method, prefix) in targets)
                h.Patch(method, prefix: new HarmonyMethod(typeof(SaveSerializerLookups), prefix) { priority = Priority.First });
            h.Patch(arraySerialize, transpiler: new HarmonyMethod(typeof(SaveSerializerLookups), nameof(ArrayTranspiler)));
            h.Patch(AccessTools.Method(typeof(GameInstanceSave), nameof(GameInstanceSave.PreSerialization))
                    ?? throw new MissingMethodException("GameInstanceSave.PreSerialization"),
                postfix: new HarmonyMethod(typeof(SaveSerializerLookups), nameof(Report)));
            on = true;
        }

        // Other mods patch after PerfeBT loads, so the lookups are checked at their first use.
        private static bool Ready()
        {
            if (!checkedPatches)
            {
                checkedPatches = true;
                foreach (var m in patched)
                {
                    var info = Harmony.GetPatchInfo(m);
                    if (info == null || info.Owners.All(o => o == harmony.Id))
                        continue;
                    on = false;
                    Log.Write($"[savelookups] {m.DeclaringType?.Name}.{m.Name} is patched by {string.Join(", ", info.Owners)}; caches off");
                    break;
                }
            }
            return on;
        }

        // First entry per key, in list order: the entry FirstOrDefault returns. Not cacheable with a null entry.
        private static Table<T> Current<T>(Table<T> table, List<T> list, Func<T, IEnumerable<Type>> keys) where T : class
        {
            if (ReferenceEquals(table.List, list) && table.Count == list.Count)
                return table;
            table.List = list;
            table.Count = list.Count;
            table.ByType.Clear();
            table.Cacheable = true;
            foreach (var entry in list)
            {
                if (entry == null)
                {
                    table.Cacheable = false;
                    break;
                }
                var types = keys(entry);
                if (types == null)
                {
                    table.Cacheable = false;
                    break;
                }
                foreach (var t in types)
                    if (t != null && !table.ByType.ContainsKey(t))
                        table.ByType.Add(t, entry);
            }
            if (!table.Cacheable)
                table.ByType.Clear();
            return table;
        }

        private static IEnumerable<Type> ContractKey(DataContract dc) => new[] { dc.FunctionalityObjectType };
        private static IEnumerable<Type> ClassKey(ClassReferenceMapping m) => new[] { m.ClassType };
        private static IEnumerable<Type> FactoryKeys(SerializationFactoryMapping m) => m.HandledTypes;

        // The entry the original scan finds, or false to run the original. scanMatch is static (no closure per lookup).
        private static bool Find<T>(Table<T> table, Type type, Func<T, Type, bool> scanMatch, List<T> list, out T found) where T : class
        {
            found = null;
            if (type == null || !table.Cacheable)
                return false;
            table.ByType.TryGetValue(type, out found);
            hits++;
            if (verifyEvery > 0 && ++sinceVerify >= verifyEvery)
            {
                sinceVerify = 0;
                verified++;
                var scanned = list.FirstOrDefault(e => scanMatch(e, type));
                if (!ReferenceEquals(scanned, found))
                {
                    on = false;
                    Log.Write($"[savelookups] verification failed for {typeof(T).Name} of {type}: cached {found?.GetHashCode()}, scan {scanned?.GetHashCode()}; caches off");
                    found = scanned;
                }
            }
            return true;
        }

        private static bool FindContract(DataContractManager manager, Type type, out DataContract found)
        {
            found = null;
            if (!Ready())
                return false;
            var list = manager.dataContracts;
            return Find(Current(contracts, list, ContractKey), type, (dc, t) => dc.FunctionalityObjectType == t, list, out found);
        }

        private static bool FindClass(ClassTypeResolver resolver, Type type, out ClassReferenceMapping found)
        {
            found = null;
            var list = resolver.ClassMappingTable;
            if (!Ready() || list == null)
                return false;
            return Find(Current(classes, list, ClassKey), type, (m, t) => m.ClassType == t, list, out found);
        }

        private static bool FindFactory(SerializationFactoryManager manager, Type type, out SerializationFactoryMapping found)
        {
            found = null;
            if (!Ready())
                return false;
            var list = manager.mappings;
            return Find(Current(factories, list, FactoryKeys), type, (m, t) => m.HandledTypes.Contains(t), list, out found);
        }

        private static bool ContainsContract(DataContractManager __instance, Type __0, ref bool __result)
        {
            if (!FindContract(__instance, __0, out var dc))
                return true;
            __result = dc != null;
            return false;
        }

        private static bool GetContract(DataContractManager __instance, Type __0, ref DataContract __result)
        {
            if (!FindContract(__instance, __0, out var dc))
                return true;
            __result = dc;
            return false;
        }

        private static bool GetClassID(DataContractManager __instance, Type __0, ref uint __result)
        {
            if (!FindContract(__instance, __0, out var dc) || dc == null)
                return true;
            __result = dc.ID;
            return false;
        }

        private static bool HasType(ClassTypeResolver __instance, Type __0, ref bool __result)
        {
            if (!FindClass(__instance, __0, out var m))
                return true;
            __result = m != null;
            return false;
        }

        private static bool GetTypeIdentifier(ClassTypeResolver __instance, Type __0, ref uint __result)
        {
            if (!FindClass(__instance, __0, out var m) || m == null)
                return true;
            __result = m.ID;
            return false;
        }

        private static bool CanHandleType(SerializationFactoryManager __instance, Type __0, ref bool __result)
        {
            if (!FindFactory(__instance, __0, out var m))
                return true;
            __result = m != null;
            return false;
        }

        private static bool GetFactoryForType(SerializationFactoryManager __instance, Type __0, ref ISerializationFactory __result)
        {
            if (!FindFactory(__instance, __0, out var m))
                return true;
            __result = m?.Constructor(null);
            return false;
        }

        private static bool GetFactoryType(SerializationFactoryManager __instance, Type __0, ref Type __result)
        {
            if (!FindFactory(__instance, __0, out var m))
                return true;
            __result = m?.Type;
            return false;
        }

        // ArraySerializationHandler.Serialize: typeof(Enumerable).GetMethod("ToArray").MakeGenericMethod(type) per list
        // field. Both results depend only on their arguments.
        private static IEnumerable<CodeInstruction> ArrayTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var getMethod = AccessTools.Method(typeof(Type), nameof(Type.GetMethod), new[] { typeof(string) });
            var makeGeneric = AccessTools.Method(typeof(MethodInfo), nameof(MethodInfo.MakeGenericMethod), new[] { typeof(Type[]) });
            var list = instructions.ToList();
            int gets = 0, makes = 0;
            foreach (var ins in list)
            {
                if (ins.Calls(getMethod))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = AccessTools.Method(typeof(SaveSerializerLookups), nameof(CachedGetMethod));
                    gets++;
                }
                else if (ins.Calls(makeGeneric))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = AccessTools.Method(typeof(SaveSerializerLookups), nameof(CachedMakeGeneric));
                    makes++;
                }
            }
            if (gets != 1 || makes != 1)
                throw new InvalidOperationException($"ArraySerializationHandler.Serialize shape changed: {gets} GetMethod, {makes} MakeGenericMethod");
            return list;
        }

        private static MethodInfo CachedGetMethod(Type type, string name)
        {
            if (!on || type == null || name == null)
                return type.GetMethod(name);
            var key = new KeyValuePair<Type, string>(type, name);
            if (!methods.TryGetValue(key, out var method))
                methods[key] = method = type.GetMethod(name);
            return method;
        }

        private static MethodInfo CachedMakeGeneric(MethodInfo method, Type[] args)
        {
            if (!on || method == null || args == null || args.Length != 1 || args[0] == null)
                return method.MakeGenericMethod(args);
            var key = new KeyValuePair<MethodInfo, Type>(method, args[0]);
            if (!generics.TryGetValue(key, out var generic))
                generics[key] = generic = method.MakeGenericMethod(args);
            return generic;
        }

        private static void Report()
        {
            if (hits == 0)
                return;
            Log.Write($"[savelookups] {hits} serializer table lookups from caches, {verified} verified{(on ? "" : " (off)")}");
            hits = verified = 0;
        }
    }

    // Saves: every message the serializer builds is copied again on its way into its parent: SerializeNestedClass and
    // ClassSerializationHandler wrap each child's bytes with ByteString.CopyFrom, ArraySerializationHandler copies each
    // element's freshly converted bytes, and ByteSerializationHandler copies each stat collection's bytes (~140 MB per
    // save; 80,468 CopyFrom calls took 160 ms and are a large part of the 2.5 GB a save allocates). Where the array is
    // new and nothing else keeps it, it is wrapped instead (ByteString.AttachBytes, protobuf's own no-copy wrap):
    //   - a child's ClassData: SerializeClass's fresh result, not stored anywhere else;
    //   - ArraySerializationHandler.ToBytes results (BitConverter, Encoding and ToByteArray return new arrays), only
    //     for the handler's own ToBytes, not a subclass's;
    //   - StatCollection.serializationStreamBytes: created by the PreSerialization just before, nulled by
    //     PostSerialization right after; wrapped only if it is that same array.
    // Same bytes. With SaveAlloc on, messages of 16 KB or more are also written into an exact-size stream whose buffer
    // is the result (the ~29 KB star system messages grew by doubling and were copied by ToArray).
    internal static class SaveSerializerCopies
    {
        private static byte[] statBytes;
        private static long wrapped, wrappedBytes;
        // Off: every site copies as before (the smoke test compares both).
        internal static bool On = true;

        // protobuf's internal no-copy wrap, bound and tested once: a call that threw inside a field handler would be
        // caught by SerializeClass and the field left out of the save, so the fix is not applied unless it works.
        private static Func<byte[], ByteString> attach;

        internal static void Apply(Harmony h)
        {
            attach = AccessTools.MethodDelegate<Func<byte[], ByteString>>(
                AccessTools.Method(typeof(ByteString), "AttachBytes", new[] { typeof(byte[]) })
                ?? throw new MissingMethodException("ByteString.AttachBytes"));
            var probe = new byte[] { 1, 2, 3 };
            var wrappedProbe = attach(probe);
            probe[0] = 9; // shares the array: the change shows
            if (wrappedProbe.Length != 3 || wrappedProbe[0] != 9 || wrappedProbe[2] != 3)
                throw new InvalidOperationException("ByteString.AttachBytes does not wrap the array");
            var nested = AccessTools.Method(typeof(ProtobufSerialization), nameof(ProtobufSerialization.SerializeNestedClass));
            var classField = AccessTools.Method(typeof(ClassSerializationHandler), nameof(ClassSerializationHandler.Serialize));
            var arrayField = AccessTools.Method(typeof(ArraySerializationHandler), nameof(ArraySerializationHandler.Serialize));
            var byteField = AccessTools.Method(typeof(ByteSerializationHandler), nameof(ByteSerializationHandler.Serialize));
            var pre = AccessTools.Method(typeof(StatCollection), nameof(StatCollection.PreSerialization), Type.EmptyTypes);
            var post = AccessTools.Method(typeof(StatCollection), nameof(StatCollection.PostSerialization), Type.EmptyTypes);
            if (nested == null || classField == null || arrayField == null || byteField == null || pre == null || post == null)
                throw new MissingMethodException("serializer copy sites");
            h.Patch(nested, transpiler: new HarmonyMethod(typeof(SaveSerializerCopies), nameof(ChildTranspiler)));
            h.Patch(classField, transpiler: new HarmonyMethod(typeof(SaveSerializerCopies), nameof(ChildTranspiler)));
            h.Patch(arrayField, transpiler: new HarmonyMethod(typeof(SaveSerializerCopies), nameof(ArrayTranspiler)));
            h.Patch(byteField, transpiler: new HarmonyMethod(typeof(SaveSerializerCopies), nameof(ByteTranspiler)));
            h.Patch(pre, postfix: new HarmonyMethod(typeof(SaveSerializerCopies), nameof(StatsWritten)) { priority = Priority.Last });
            h.Patch(post, prefix: new HarmonyMethod(typeof(SaveSerializerCopies), nameof(StatsReleased)));
            h.Patch(AccessTools.Method(typeof(GameInstanceSave), nameof(GameInstanceSave.PreSerialization))
                    ?? throw new MissingMethodException("GameInstanceSave.PreSerialization"),
                postfix: new HarmonyMethod(typeof(SaveSerializerCopies), nameof(Report)));
            // With SaveAlloc: each star system's message (~29 KB of stats) is written into an exact-size stream too.
            SaveAlloc.PresizeAt = 16 * 1024;
        }

        private static readonly MethodInfo copyFrom = AccessTools.Method(typeof(ByteString), nameof(ByteString.CopyFrom), new[] { typeof(byte[]) });

        private static List<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions, string name, int expected, Func<CodeInstruction[]> with)
        {
            var result = new List<CodeInstruction>();
            var count = 0;
            foreach (var ins in instructions)
            {
                if (ins.Calls(copyFrom))
                {
                    count++;
                    var replacement = with();
                    replacement[0].labels.AddRange(ins.labels);
                    replacement[0].blocks.AddRange(ins.blocks);
                    result.AddRange(replacement);
                }
                else
                    result.Add(ins);
            }
            if (count != expected)
                throw new InvalidOperationException($"{name} shape changed: {count} ByteString.CopyFrom");
            return result;
        }

        private static IEnumerable<CodeInstruction> ChildTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            Replace(instructions, __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name, 1,
                () => new[] { new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SaveSerializerCopies), nameof(Wrap))) });

        private static IEnumerable<CodeInstruction> ArrayTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Replace(instructions, "ArraySerializationHandler.Serialize", 1, () => new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SaveSerializerCopies), nameof(WrapElement))),
            });

        // Two calls: the byte[] field, and the one-byte array of a byte field (never the stat array).
        private static IEnumerable<CodeInstruction> ByteTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Replace(instructions, "ByteSerializationHandler.Serialize", 2,
                () => new[] { new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SaveSerializerCopies), nameof(WrapStats))) });

        private static ByteString Wrap(byte[] bytes)
        {
            if (bytes == null || !On)
                return ByteString.CopyFrom(bytes);
            wrapped++;
            wrappedBytes += bytes.Length;
            return attach(bytes);
        }

        private static ByteString WrapElement(byte[] bytes, ArraySerializationHandler handler) =>
            handler.GetType() == typeof(ArraySerializationHandler) ? Wrap(bytes) : ByteString.CopyFrom(bytes);

        private static ByteString WrapStats(byte[] bytes)
        {
            if (bytes == null || !ReferenceEquals(bytes, statBytes))
                return ByteString.CopyFrom(bytes);
            statBytes = null;
            return Wrap(bytes);
        }

        private static void StatsWritten(StatCollection __instance) => statBytes = __instance.serializationStreamBytes;

        private static void StatsReleased() => statBytes = null;

        private static void Report()
        {
            if (wrapped == 0)
                return;
            Log.Write($"[savecopies] {wrapped} arrays ({wrappedBytes / 1048576.0:F1} MB) wrapped instead of copied");
            wrapped = wrappedBytes = 0;
        }
    }
}
