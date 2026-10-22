using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace PerfeBT
{
    // Diagnostic: a save spends 1.4-1.7 s in GameInstanceSave.PreSerialization, mostly serializing each object
    // registered in the SerializableReferenceContainer (SerializedReference.Serialize, which runs the object's own
    // Save). Times every Serialize by the source object's type and logs the top types after each PreSerialization
    // ([save] lines). Read-only.
    internal static class SaveProfile
    {
        private sealed class Row { public int Count; public long Ticks; public long Bytes; }

        // Serialize clears sourceObject, so the type is read before it runs.
        private struct Pending { public Type Type; public long Start; }

        private static readonly Dictionary<Type, Row> rows = new Dictionary<Type, Row>();
        private static FieldInfo sourceField, dataField;
        private static long totalStart;

        internal static void Apply(Harmony harmony)
        {
            var refType = AccessTools.TypeByName("BattleTech.Save.Test.SerializableReferenceContainer+SerializedReference")
                          ?? throw new TypeLoadException("SerializedReference not found");
            sourceField = AccessTools.Field(refType, "sourceObject") ?? throw new MissingFieldException("sourceObject");
            dataField = AccessTools.Field(refType, "serializedData") ?? throw new MissingFieldException("serializedData");
            var serialize = AccessTools.Method(refType, "Serialize") ?? throw new MissingMethodException("Serialize");
            harmony.Patch(serialize,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(SaveProfile), nameof(SerializePrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(SaveProfile), nameof(SerializePostfix))));
            var pre = AccessTools.Method(typeof(BattleTech.Save.GameInstanceSave), "PreSerialization")
                      ?? throw new MissingMethodException("GameInstanceSave.PreSerialization");
            harmony.Patch(pre,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(SaveProfile), nameof(PrePrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(SaveProfile), nameof(PrePostfix))));
        }

        private static void PrePrefix()
        {
            rows.Clear();
            totalStart = Stopwatch.GetTimestamp();
        }

        private static void SerializePrefix(object __instance, out Pending __state) =>
            __state = new Pending { Type = sourceField.GetValue(__instance)?.GetType() ?? typeof(void), Start = Stopwatch.GetTimestamp() };

        private static void SerializePostfix(object __instance, Pending __state)
        {
            var ticks = Stopwatch.GetTimestamp() - __state.Start;
            if (!rows.TryGetValue(__state.Type, out var row))
                rows[__state.Type] = row = new Row();
            row.Count++;
            row.Ticks += ticks;
            row.Bytes += (dataField.GetValue(__instance) as byte[])?.Length ?? 0;
        }

        private static void PrePostfix()
        {
            if (rows.Count == 0)
                return;
            double ms(long t) => t * 1000.0 / Stopwatch.Frequency;
            var total = Stopwatch.GetTimestamp() - totalStart;
            var serialized = rows.Values.Sum(r => r.Ticks);
            Log.Write($"[save] PreSerialization {ms(total):0} ms, of which object serialization {ms(serialized):0} ms " +
                      $"({rows.Values.Sum(r => r.Count)} objects, {rows.Values.Sum(r => r.Bytes) / 1024} KB); top types:");
            foreach (var kv in rows.OrderByDescending(k => k.Value.Ticks).Take(15))
                Log.Write($"[save]   {ms(kv.Value.Ticks),8:0.0} ms {kv.Value.Count,7} objects {kv.Value.Bytes / 1024,7} KB  {kv.Key.FullName}");
            rows.Clear();
        }
    }
}
