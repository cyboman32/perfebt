using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace PerfeBT
{
    // Diagnostic: the collector's own view of the heap. Boehm GC internals are not exported from
    // mono-2.0-bdwgc.dll; their addresses come from Unity's symbol server PDB for this exact build (checked by PE
    // timestamp, image size and the first bytes of GC_dump, otherwise nothing is touched).
    //   - GcStatsLog: GC_print_stats = verbose with GC_LOG_FILE in reports/ (per collection: marking time, finalization
    //     and sweep time, finalizable object counts, heap growth).
    //   - GcDumpAfterUsedMb > 0: once per session, right after the first PerfeBT collection that leaves at least that
    //     much used, GC_dump(): registered roots, heap sections, free blocks and one line per heap block
    //     (kind,object size,marked objects,objects) - marked objects x size is the live data. Freezes for a while.
    //     Before it, a census of the objects held by normal and pinned GC handles (roots the heap census can't see).
    internal static class GcDump
    {
        private const string Module = "mono-2.0-bdwgc.dll";
        internal const int TimeDateStamp = 0x601cdd8a;
        internal const int SizeOfImage = 0x768000;
        private const int RvaGcDump = 0x3075e4;
        private const int RvaGcPrintStats = 0x49ab34;
        private const int RvaGcHandles = 0x490a10; // HandleData gc_handles[4]: bitmap, entries, uint size @0x10; 40 bytes each
        private static readonly byte[] GcDumpPrologue = { 0x48, 0x83, 0xec, 0x28, 0x83, 0x3d, 0xe1, 0x2a };

        internal static long DumpAfterUsedMb;
        internal static bool Forced;
        private static IntPtr baseAddress;
        private static bool dumped;
        private static string logPath;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void VoidFn();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern bool SetEnvironmentVariableA(string name, string value);

        [DllImport(Module, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr mono_gchandle_get_target(uint handle);

        [DllImport(Module, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr mono_object_get_class(IntPtr obj);

        [DllImport(Module, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr mono_class_get_name(IntPtr klass);

        [DllImport(Module, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr mono_class_get_namespace(IntPtr klass);

        [DllImport(Module, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint mono_object_get_size(IntPtr obj);

        internal static void Apply(string reportDir, bool statsLog, long dumpAfterUsedMb)
        {
            var b = GetModuleHandle(Module);
            if (b == IntPtr.Zero)
                throw new DllNotFoundException(Module + " not loaded");
            int peOffset = Marshal.ReadInt32(b, 0x3c);
            int stamp = Marshal.ReadInt32(b, peOffset + 8);
            int size = Marshal.ReadInt32(b, peOffset + 0x50);
            if (stamp != TimeDateStamp || size != SizeOfImage)
                throw new NotSupportedException($"{Module} build {stamp:x8}/{size:x} is not the one the addresses are for");
            for (int i = 0; i < GcDumpPrologue.Length; i++)
                if (Marshal.ReadByte(b, RvaGcDump + i) != GcDumpPrologue[i])
                    throw new NotSupportedException("GC_dump bytes differ");
            baseAddress = b;
            DumpAfterUsedMb = dumpAfterUsedMb;

            Directory.CreateDirectory(reportDir);
            logPath = Path.Combine(reportDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_gc.log");
            // Boehm opens its log lazily on first output, reading GC_LOG_FILE with GetEnvironmentVariableA.
            SetEnvironmentVariableA("GC_LOG_FILE", logPath);
            if (statsLog || dumpAfterUsedMb > 0)
                Marshal.WriteInt32(b, RvaGcPrintStats, 2); // VERBOSE; also keeps the log file (not delete-on-close)
            Log.Write($"[gcdump] Boehm log -> {logPath} (stats {(statsLog ? "on" : "off")}, dump after {dumpAfterUsedMb} MB used)");
        }

        // Called right after an PerfeBT collection, while the mark bits still describe the live heap.
        internal static void AfterCollect(long usedMb)
        {
            bool forced = Forced;
            Forced = false;
            if (baseAddress == IntPtr.Zero)
                return;
            if (!forced && (dumped || DumpAfterUsedMb <= 0 || usedMb < DumpAfterUsedMb))
                return;
            dumped = true;
            try
            {
                HandleCensus();
            }
            catch (Exception e)
            {
                Log.Write("[gchandles] census failed: " + e.Message);
            }
            var sw = Stopwatch.StartNew();
            try
            {
                var fn = (VoidFn)Marshal.GetDelegateForFunctionPointer(new IntPtr(baseAddress.ToInt64() + RvaGcDump), typeof(VoidFn));
                fn();
                Log.Write($"[gcdump] GC_dump at {usedMb} MB used took {sw.ElapsedMilliseconds} ms -> {logPath}");
            }
            catch (Exception e)
            {
                Log.Write("[gcdump] GC_dump failed: " + e.Message);
            }
            try
            {
                RootScan.Run(baseAddress);
            }
            catch (Exception e)
            {
                Log.Write("[rootscan] failed: " + e);
            }
        }

        // Normal (type 2) and pinned (type 3) handles keep their targets alive. Handle = slot << 3 | (type + 1);
        // mono_gchandle_get_target takes the handle lock and returns null for free slots.
        private static void HandleCensus()
        {
            var sw = Stopwatch.StartNew();
            var names = new Dictionary<IntPtr, string>();
            foreach (int type in new[] { 2, 3 })
            {
                uint size = (uint)Marshal.ReadInt32(baseAddress, RvaGcHandles + type * 40 + 0x10);
                var byClass = new Dictionary<string, long[]>();
                var largest = new List<KeyValuePair<long, string>>();
                long count = 0, bytes = 0;
                for (uint slot = 0; slot < size; slot++)
                {
                    var obj = mono_gchandle_get_target((slot << 3) | (uint)(type + 1));
                    if (obj == IntPtr.Zero)
                        continue;
                    var klass = mono_object_get_class(obj);
                    if (!names.TryGetValue(klass, out var name))
                        names[klass] = name = Marshal.PtrToStringAnsi(mono_class_get_namespace(klass)) + "." + Marshal.PtrToStringAnsi(mono_class_get_name(klass));
                    long sz = mono_object_get_size(obj);
                    count++;
                    bytes += sz;
                    if (!byClass.TryGetValue(name, out var agg))
                        byClass[name] = agg = new long[2];
                    agg[0]++;
                    agg[1] += sz;
                    if (sz >= 1 << 20)
                        largest.Add(new KeyValuePair<long, string>(sz, name));
                }
                Log.Write($"[gchandles] {(type == 2 ? "normal" : "pinned")}: {size} slots, {count} targets, {bytes / 1048576.0:F1} MB");
                foreach (var kv in byClass.OrderByDescending(kv => kv.Value[1]).Take(15))
                    Log.Write($"[gchandles]   {kv.Value[1] / 1048576.0,10:F1} MB {kv.Value[0],8}  {kv.Key}");
                foreach (var kv in largest.OrderByDescending(kv => kv.Key).Take(10))
                    Log.Write($"[gchandles]   largest {kv.Key / 1048576.0:F1} MB {kv.Value}");
            }
            Log.Write($"[gchandles] census took {sw.ElapsedMilliseconds} ms");
        }
    }
}
