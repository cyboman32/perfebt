using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BattleTech.Save;
using BattleTech.Save.Core;
using Google.Protobuf;
using HarmonyLib;

namespace PerfeBT
{
    // Saves: every Argo save allocated ~3.7 GB (0.8.9 profile), so roughly every second save ran a 3-4 s collection,
    // and the 290-580 MB buffers are what conservative scanning later keeps alive (RootScan: Dictionary entry arrays
    // are scanned untyped and their int pairs read as heap addresses). Two of the allocations only grow or copy a
    // stream, so the bytes written are the same with or without them:
    //   - ProtobufSerialization.SerializeClass writes each message into a new MemoryStream() and returns ToArray().
    //     For the ~290 MB GameInstanceSave message the stream grew to the message size, then doubled (580 MB), then
    //     ToArray copied it again. Here the stream is first sized to message.CalculateSize() (only for messages of
    //     1 MB or more; 16 KB with SaveSerializerCopies, which covers each star system's ~29 KB stat message), and
    //     when it is exactly full its buffer is the result instead of a copy (MemoryStream() always exposes its
    //     buffer; the stream is disposed right after and nothing else holds it).
    //   - SaveBlock<T>.CompressBytes gzips into a MemoryStream created with the uncompressed length as capacity
    //     (290 MB for a ~5 MB save file). The capacity becomes 1/32 of that (at least 64 KB); the stream grows if the
    //     output is larger. The returned bytes are copied out of the stream either way.
    internal static class SaveAlloc
    {
        private const int Threshold = 1 << 20;
        // Messages of this size or more are written into an exact-size stream (SaveSerializerCopies lowers it).
        internal static int PresizeAt = Threshold;
        private static int logged;

        internal static void ApplySerialize(Harmony harmony)
        {
            var serializeClass = AccessTools.Method(typeof(BattleTech.Serialization.ProtobufSerialization),
                                     nameof(BattleTech.Serialization.ProtobufSerialization.SerializeClass))
                                 ?? throw new MissingMethodException("ProtobufSerialization.SerializeClass");
            harmony.Patch(serializeClass, transpiler: new HarmonyMethod(typeof(SaveAlloc), nameof(SerializeClassTranspiler)));
        }

        // SaveBlock<T> is instantiated with reference types only, which share one compiled body.
        internal static void ApplyCompress(Harmony harmony)
        {
            var compress = AccessTools.Method(typeof(SaveBlock<GameInstanceSave>), nameof(SaveBlock<GameInstanceSave>.CompressBytes))
                           ?? throw new MissingMethodException("SaveBlock<T>.CompressBytes");
            harmony.Patch(compress, transpiler: new HarmonyMethod(typeof(SaveAlloc), nameof(CompressTranspiler)));
        }

        private static IEnumerable<CodeInstruction> SerializeClassTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var writeTo = AccessTools.Method(typeof(MessageExtensions), nameof(MessageExtensions.WriteTo), new[] { typeof(IMessage), typeof(Stream) });
            var toArray = AccessTools.Method(typeof(MemoryStream), nameof(MemoryStream.ToArray));
            var list = instructions.ToList();
            int writes = 0, arrays = 0;
            foreach (var ins in list)
            {
                if (ins.Calls(writeTo))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = AccessTools.Method(typeof(SaveAlloc), nameof(WriteTo));
                    writes++;
                }
                else if (ins.Calls(toArray))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = AccessTools.Method(typeof(SaveAlloc), nameof(ToArray));
                    arrays++;
                }
            }
            if (writes != 1 || arrays != 1)
                throw new InvalidOperationException($"SerializeClass shape changed: {writes} WriteTo, {arrays} ToArray");
            return list;
        }

        private static IEnumerable<CodeInstruction> CompressTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var ctor = AccessTools.Constructor(typeof(MemoryStream), new[] { typeof(int) });
            var list = instructions.ToList();
            int i = list.FindIndex(x => x.opcode == OpCodes.Newobj && Equals(x.operand, ctor));
            if (i < 0 || list.FindLastIndex(x => x.opcode == OpCodes.Newobj && Equals(x.operand, ctor)) != i)
                throw new InvalidOperationException("CompressBytes shape changed");
            list.Insert(i, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SaveAlloc), nameof(CompressCapacity))));
            return list;
        }

        private static int CompressCapacity(int uncompressed) => Math.Max(64 * 1024, uncompressed / 32);

        private static void WriteTo(IMessage message, Stream output)
        {
            if (output is MemoryStream ms && ms.Capacity == 0)
            {
                int size = message.CalculateSize();
                if (size >= PresizeAt)
                    ms.Capacity = size;
            }
            message.WriteTo(output);
        }

        private static byte[] ToArray(MemoryStream ms)
        {
            if (ms.Length < PresizeAt || ms.Capacity != ms.Length)
                return ms.ToArray();
            if (ms.Length < Threshold)
                return ms.GetBuffer();
            if (logged < 5)
            {
                logged++;
                Log.Write($"[savealloc] {ms.Length / 1048576.0:F1} MB message written into an exact-size buffer");
            }
            return ms.GetBuffer();
        }
    }
}
