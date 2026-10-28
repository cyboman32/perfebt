using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BattleTech;
using BattleTech.Save;
using BattleTech.Serialization;
using Google.Protobuf;
using HarmonyLib;
using SerializedReference = BattleTech.Save.Test.SerializableReferenceContainer.SerializedReference;

namespace PerfeBT
{
    // Diagnostic: a save spends 1.4-1.7 s in GameInstanceSave.PreSerialization, mostly serializing each object
    // registered in the SerializableReferenceContainer (SerializedReference.Serialize, which runs the object's own
    // Save). Times every Serialize by the source object's type and logs the top types after each PreSerialization
    // ([save] lines), and the self time (without the other timed stages inside) of the serializer's stages: contract
    // building (ProtobufSerialization.SerializeClass), stat collections (StatCollection.PreSerialization), reference
    // lookups (GetReferenceIdFor), protobuf writes and copies ([save] stage lines). Read-only.
    internal static class SaveProfile
    {
        private sealed class Row { public int Count; public long Ticks; public long Bytes; }

        // Serialize clears sourceObject, so the type is read before it runs.
        private struct Pending { public Type Type; public long Start; }

        private static readonly Dictionary<Type, Row> rows = new Dictionary<Type, Row>();
        private static long totalStart;

        private sealed class Stage { public string Name; public int Count; public long Self; }

        private static readonly Dictionary<MethodBase, Stage> stages = new Dictionary<MethodBase, Stage>();
        // Ticks spent in timed stages called by each running stage, innermost last.
        private static readonly List<long> childTicks = new List<long>();
        private static bool active;
        private static int saveThread;

        internal static void Apply(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(SerializedReference), nameof(SerializedReference.Serialize)),
                prefix: new HarmonyMethod(typeof(SaveProfile), nameof(SerializePrefix)),
                postfix: new HarmonyMethod(typeof(SaveProfile), nameof(SerializePostfix)));
            harmony.Patch(AccessTools.Method(typeof(GameInstanceSave), nameof(GameInstanceSave.PreSerialization)),
                prefix: new HarmonyMethod(typeof(SaveProfile), nameof(PrePrefix)),
                postfix: new HarmonyMethod(typeof(SaveProfile), nameof(PrePostfix)));
            var timed = new (MethodBase method, string name)[]
            {
                (AccessTools.Method(typeof(ProtobufSerialization), nameof(ProtobufSerialization.SerializeClass)), "contract building (SerializeClass)"),
                (AccessTools.Method(typeof(ProtobufSerialization), nameof(ProtobufSerialization.GetReferenceIdFor)), "reference lookups (GetReferenceIdFor)"),
                (AccessTools.Method(typeof(StatCollection), nameof(StatCollection.PreSerialization), Type.EmptyTypes), "stat collections (StatCollection.PreSerialization)"),
                (AccessTools.Method(typeof(ByteString), nameof(ByteString.CopyFrom), new[] { typeof(byte[]) }), "ByteString.CopyFrom"),
                (AccessTools.Method(typeof(SaveAlloc), "WriteTo"), "protobuf writes (WriteTo)"),
                (AccessTools.Method(typeof(SaveAlloc), "ToArray"), "stream copies (ToArray)"),
            };
            foreach (var (method, name) in timed)
            {
                if (method == null)
                    continue;
                stages[method] = new Stage { Name = name };
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(SaveProfile), nameof(StagePrefix)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(SaveProfile), nameof(StageFinalizer)) { priority = Priority.Last });
            }
        }

        private static void StagePrefix(out long __state)
        {
            if (!active || System.Threading.Thread.CurrentThread.ManagedThreadId != saveThread)
            {
                __state = 0;
                return;
            }
            childTicks.Add(0);
            __state = Stopwatch.GetTimestamp();
        }

        private static Exception StageFinalizer(MethodBase __originalMethod, long __state, Exception __exception)
        {
            if (__state == 0 || childTicks.Count == 0)
                return __exception;
            var elapsed = Stopwatch.GetTimestamp() - __state;
            var last = childTicks.Count - 1;
            var inner = childTicks[last];
            childTicks.RemoveAt(last);
            if (last > 0)
                childTicks[last - 1] += elapsed;
            if (stages.TryGetValue(__originalMethod, out var stage))
            {
                stage.Count++;
                stage.Self += elapsed - inner;
            }
            return __exception;
        }

        private static void PrePrefix()
        {
            rows.Clear();
            foreach (var s in stages.Values)
            {
                s.Count = 0;
                s.Self = 0;
            }
            childTicks.Clear();
            saveThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            active = true;
            totalStart = Stopwatch.GetTimestamp();
        }

        private static void SerializePrefix(SerializedReference __instance, out Pending __state) =>
            __state = new Pending { Type = __instance.sourceObject?.GetType() ?? typeof(void), Start = Stopwatch.GetTimestamp() };

        private static void SerializePostfix(SerializedReference __instance, Pending __state)
        {
            var ticks = Stopwatch.GetTimestamp() - __state.Start;
            if (!rows.TryGetValue(__state.Type, out var row))
                rows[__state.Type] = row = new Row();
            row.Count++;
            row.Ticks += ticks;
            row.Bytes += __instance.serializedData?.Length ?? 0;
        }

        private static void PrePostfix()
        {
            active = false;
            if (rows.Count == 0)
                return;
            double ms(long t) => t * 1000.0 / Stopwatch.Frequency;
            var total = Stopwatch.GetTimestamp() - totalStart;
            var serialized = rows.Values.Sum(r => r.Ticks);
            Log.Write($"[save] PreSerialization {ms(total):0} ms, of which object serialization {ms(serialized):0} ms " +
                      $"({rows.Values.Sum(r => r.Count)} objects, {rows.Values.Sum(r => r.Bytes) / 1024} KB); top types:");
            foreach (var kv in rows.OrderByDescending(k => k.Value.Ticks).Take(15))
                Log.Write($"[save]   {ms(kv.Value.Ticks),8:0.0} ms {kv.Value.Count,7} objects {kv.Value.Bytes / 1024,7} KB  {kv.Key.FullName}");
            foreach (var s in stages.Values.OrderByDescending(s => s.Self))
                if (s.Count > 0)
                    Log.Write($"[save] stage {ms(s.Self),8:0.0} ms self {s.Count,8} calls  {s.Name}");
            rows.Clear();
        }
    }
}
