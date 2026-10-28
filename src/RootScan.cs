using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace PerfeBT
{
    // Diagnostic, run after GC_dump: what keeps the large live objects alive. The managed heap census reaches only a
    // couple of the ~290 MB save buffers the collector marks; Boehm also scans thread stacks, writable DLL data and
    // pointer-containing heap objects conservatively, where any word inside an object's range retains all of it.
    // Lists every marked heap object >= MinBytes, then reads all writable committed memory of the process
    // (ReadProcessMemory, so a region freed meanwhile only fails the read) and reports each word pointing into one:
    // inside a marked pointer-containing heap object (with its kind/size/class), a thread stack, a module image, or
    // other native memory. Read-only; the game keeps running on other threads while it scans.
    internal static class RootScan
    {
        private const long MinBytes = 32L << 20;
        private const int MaxHitsPerObject = 25;
        internal const int RvaGcArrays = 0x49dd40;
        internal const int OffHeapSects = 0x862e0; // struct { ptr start; size_t bytes; } GC_heap_sects[]
        internal const int RvaNHeapSects = 0x49a0e0;
        private const int RvaGcBase = 0x305170;
        private const int RvaGcFindHeader = 0x3088e8;
        private static readonly byte[] GcBasePrologue = { 0x48, 0x89, 0x5c, 0x24, 0x08, 0x48, 0x89, 0x6c };
        private static readonly byte[] FindHeaderPrologue = { 0x48, 0x8b, 0xd1, 0x4c, 0x8d, 0x05, 0x2e, 0x37 };

        // GC_find_header of this exact mono build (PE timestamp, image size and the function's first bytes checked).
        internal static PtrFn FindHeader(IntPtr monoBase)
        {
            int peOffset = Marshal.ReadInt32(monoBase, 0x3c);
            if (Marshal.ReadInt32(monoBase, peOffset + 8) != GcDump.TimeDateStamp || Marshal.ReadInt32(monoBase, peOffset + 0x50) != GcDump.SizeOfImage)
                throw new NotSupportedException("mono-2.0-bdwgc.dll is not the build the addresses are for");
            for (int i = 0; i < FindHeaderPrologue.Length; i++)
                if (Marshal.ReadByte(monoBase, RvaGcFindHeader + i) != FindHeaderPrologue[i])
                    throw new NotSupportedException("GC_find_header bytes differ");
            return (PtrFn)Marshal.GetDelegateForFunctionPointer(new IntPtr(monoBase.ToInt64() + RvaGcFindHeader), typeof(PtrFn));
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr PtrFn(IntPtr p);

        [StructLayout(LayoutKind.Sequential)]
        private struct MemInfo
        {
            public long BaseAddress;
            public long AllocationBase;
            public uint AllocationProtect;
            public uint Pad0;
            public long RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            public uint Pad1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ThreadBasicInformation
        {
            public int ExitStatus;
            public int Pad;
            public long TebBaseAddress;
            public long UniqueProcess;
            public long UniqueThread;
            public long AffinityMask;
            public int Priority;
            public int BasePriority;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr VirtualQuery(IntPtr address, out MemInfo info, IntPtr length);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] long[] buffer, IntPtr size, out IntPtr read);

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenThread(int access, bool inherit, int threadId);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern int GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "K32GetMappedFileNameW")]
        private static extern int GetMappedFileName(IntPtr process, IntPtr address, StringBuilder name, int size);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationThread(IntPtr thread, int infoClass, out ThreadBasicInformation info, int size, IntPtr returned);

        private sealed class Target
        {
            public long Start, Size;
            public int Kind;
            public string Type;
            public readonly List<string> Hits = new List<string>();
            public int HitCount;
        }

        private sealed class Stack
        {
            public int ThreadId;
            public long Low, High;
        }

        internal static void Run(IntPtr monoBase)
        {
            var findHeader = FindHeader(monoBase);
            for (int i = 0; i < GcBasePrologue.Length; i++)
                if (Marshal.ReadByte(monoBase, RvaGcBase + i) != GcBasePrologue[i])
                    throw new NotSupportedException("GC_base bytes differ");
            var gcBase = (PtrFn)Marshal.GetDelegateForFunctionPointer(new IntPtr(monoBase.ToInt64() + RvaGcBase), typeof(PtrFn));
            var sw = Stopwatch.StartNew();

            var arrayTypes = new Dictionary<long, string>
            {
                [VTable(new byte[1])] = "byte[]",
                [VTable(new char[1])] = "char[]",
                [VTable(new string('x', 3))] = "string",
                [VTable(new int[1])] = "int[]",
                [VTable(new long[1])] = "long[]",
                [VTable(new object[1])] = "object[]",
            };

            // 1. Marked heap blocks of at least MinBytes (large objects start their block).
            var targets = new List<Target>();
            long sects = Marshal.ReadInt64(monoBase, RvaNHeapSects);
            long heapLow = long.MaxValue, heapHigh = 0;
            for (long s = 0; s < sects; s++)
            {
                long start = Marshal.ReadInt64(monoBase, (int)(RvaGcArrays + OffHeapSects + s * 16));
                long bytes = Marshal.ReadInt64(monoBase, (int)(RvaGcArrays + OffHeapSects + s * 16 + 8));
                heapLow = Math.Min(heapLow, start);
                heapHigh = Math.Max(heapHigh, start + bytes);
                for (long h = start; h < start + bytes;)
                {
                    long hdr = findHeader(new IntPtr(h)).ToInt64();
                    if (hdr <= 0xfff)
                    {
                        h += 0x1000;
                        continue;
                    }
                    long sz = Marshal.ReadInt64(new IntPtr(hdr + 0x20));
                    long span = Math.Max(0x1000, (sz + 0xfff) & ~0xfffL);
                    bool free = (Marshal.ReadByte(new IntPtr(hdr + 0x19)) & 4) != 0;
                    if (!free && sz >= MinBytes && (Marshal.ReadInt64(new IntPtr(hdr + 0x40)) & 1) != 0)
                    {
                        long vt = Marshal.ReadInt64(new IntPtr(h));
                        targets.Add(new Target
                        {
                            Start = h,
                            Size = sz,
                            Kind = Marshal.ReadByte(new IntPtr(hdr + 0x18)),
                            Type = arrayTypes.TryGetValue(vt, out var name) ? name : $"vtable {vt:x}",
                        });
                    }
                    h += free || sz >= 0x1000 ? span : 0x1000;
                }
            }
            targets.Sort((a, b) => a.Start.CompareTo(b.Start));
            var starts = targets.Select(t => t.Start).ToArray();
            Log.Write($"[rootscan] {targets.Count} marked heap objects >= {MinBytes >> 20} MB, {targets.Sum(t => t.Size) / 1048576} MB, " +
                      $"in {sects} heap sections; listing took {sw.ElapsedMilliseconds} ms");
            if (targets.Count == 0)
                return;

            // Thread stacks: TEB StackBase (+8) / StackLimit (+0x10). Mono's Process.Threads is empty here; use Toolhelp.
            var stacks = new List<Stack>();
            foreach (int tid in ThreadIds())
            {
                var th = OpenThread(0x0040 /* THREAD_QUERY_INFORMATION */, false, tid);
                if (th == IntPtr.Zero)
                    continue;
                try
                {
                    if (NtQueryInformationThread(th, 0, out var tbi, Marshal.SizeOf(typeof(ThreadBasicInformation)), IntPtr.Zero) == 0 && tbi.TebBaseAddress != 0)
                        stacks.Add(new Stack
                        {
                            ThreadId = tid,
                            High = Marshal.ReadInt64(new IntPtr(tbi.TebBaseAddress + 8)),
                            Low = Marshal.ReadInt64(new IntPtr(tbi.TebBaseAddress + 0x10)),
                        });
                }
                finally
                {
                    CloseHandle(th);
                }
            }
            int self = GetCurrentThreadId();

            // 2. Every writable committed region, word by word.
            long minT = targets[0].Start, maxT = targets.Max(t => t.Start + t.Size);
            var buffer = new long[1 << 17]; // 1 MB
            var process = GetCurrentProcess();
            var moduleNames = new Dictionary<long, string>();
            long scanned = 0;
            int regions = 0;
            var info = default(MemInfo);
            var infoSize = new IntPtr(Marshal.SizeOf(typeof(MemInfo)));
            for (long addr = 0x10000; addr < 0x7fffffff0000L; addr = info.BaseAddress + info.RegionSize)
            {
                if (VirtualQuery(new IntPtr(addr), out info, infoSize) == IntPtr.Zero || info.RegionSize <= 0)
                    break;
                bool writable = (info.Protect & (0x04 | 0x08 | 0x40 | 0x80)) != 0 && (info.Protect & 0x100) == 0;
                if (info.State != 0x1000 || !writable)
                    continue;
                regions++;
                long regionEnd = info.BaseAddress + info.RegionSize;
                for (long chunk = info.BaseAddress; chunk < regionEnd; chunk += (long)buffer.Length * 8)
                {
                    // Skip the large objects themselves (their own contents do not retain anything).
                    int inside = Find(starts, targets, chunk);
                    if (inside >= 0)
                    {
                        chunk = ((targets[inside].Start + targets[inside].Size + 0xfff) & ~0xfffL) - (long)buffer.Length * 8;
                        continue;
                    }
                    long len = Math.Min((long)buffer.Length * 8, regionEnd - chunk);
                    if (!ReadProcessMemory(process, new IntPtr(chunk), buffer, new IntPtr(len), out var read))
                        continue;
                    long words = read.ToInt64() / 8;
                    scanned += read.ToInt64();
                    for (long w = 0; w < words; w++)
                    {
                        long v = buffer[w];
                        if (v < minT || v >= maxT)
                            continue;
                        int ti = Find(starts, targets, v);
                        if (ti < 0)
                            continue;
                        long loc = chunk + w * 8;
                        if (Find(starts, targets, loc) >= 0)
                            continue;
                        Record(targets[ti], loc, v, info, heapLow, heapHigh, gcBase, findHeader, stacks, self, moduleNames, process);
                    }
                }
            }
            Log.Write($"[rootscan] scanned {scanned / 1048576} MB in {regions} writable regions, {stacks.Count} thread stacks, {sw.ElapsedMilliseconds} ms");
            foreach (var t in targets)
            {
                Log.Write($"[rootscan] {t.Start:x} {t.Size / 1048576.0,8:F1} MB kind {t.Kind} {t.Type}: {t.HitCount} words point into it");
                foreach (var hit in t.Hits)
                    Log.Write("[rootscan]     " + hit);
            }
        }


        [StructLayout(LayoutKind.Sequential)]
        private struct ThreadEntry32
        {
            public int Size, Usage, ThreadId, OwnerProcessId, BasePri, DeltaPri, Flags;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr CreateToolhelp32Snapshot(int flags, int processId);

        [DllImport("kernel32.dll")]
        private static extern bool Thread32First(IntPtr snapshot, ref ThreadEntry32 entry);

        [DllImport("kernel32.dll")]
        private static extern bool Thread32Next(IntPtr snapshot, ref ThreadEntry32 entry);

        [DllImport("kernel32.dll")]
        private static extern int GetCurrentProcessId();

        internal static List<int> ThreadIds()
        {
            var ids = new List<int>();
            var snap = CreateToolhelp32Snapshot(4 /* TH32CS_SNAPTHREAD */, 0);
            if (snap == new IntPtr(-1))
                return ids;
            try
            {
                int pid = GetCurrentProcessId();
                var e = new ThreadEntry32 { Size = Marshal.SizeOf(typeof(ThreadEntry32)) };
                for (bool ok = Thread32First(snap, ref e); ok; ok = Thread32Next(snap, ref e))
                    if (e.OwnerProcessId == pid)
                        ids.Add(e.ThreadId);
            }
            finally
            {
                CloseHandle(snap);
            }
            return ids;
        }
        private static void Record(Target t, long loc, long value, MemInfo region, long heapLow, long heapHigh, PtrFn gcBase, PtrFn findHeader,
            List<Stack> stacks, int self, Dictionary<long, string> moduleNames, IntPtr process)
        {
            string where;
            bool inHeap = loc >= heapLow && loc < heapHigh && findHeader(new IntPtr(loc)) != IntPtr.Zero;
            long b = inHeap ? gcBase(new IntPtr(loc)).ToInt64() : 0;
            if (inHeap && b == 0)
                return; // free heap block: stale data, not scanned
            if (b != 0)
            {
                long hdr = findHeader(new IntPtr(b)).ToInt64();
                int kind = Marshal.ReadByte(new IntPtr(hdr + 0x18));
                long sz = Marshal.ReadInt64(new IntPtr(hdr + 0x20));
                long bit = sz >= 0x1000 ? 0 : ((b & 0xfff) >> 4);
                bool marked = ((Marshal.ReadInt64(new IntPtr(hdr + 0x40 + (bit >> 6) * 8)) >> (int)(bit & 63)) & 1) != 0;
                if (kind == 0 || !marked)
                    return; // pointer-free or dead objects are not scanned: no retention
                if (t.Hits.Count >= MaxHitsPerObject)
                {
                    t.HitCount++;
                    return;
                }
                where = $"heap object {b:x}+{loc - b:x} (kind {kind}, size {sz}, {ClassOf(b, kind)})";
            }
            else
            {
                var stack = stacks.FirstOrDefault(s => loc >= s.Low && loc < s.High);
                if (stack != null)
                    where = $"stack of thread {stack.ThreadId}{(stack.ThreadId == self ? " (this scan's thread)" : "")} at base-{stack.High - loc:x}";
                else if (region.Type == 0x1000000)
                {
                    if (!moduleNames.TryGetValue(region.AllocationBase, out var name))
                    {
                        var sb = new StringBuilder(520);
                        name = GetMappedFileName(process, new IntPtr(region.AllocationBase), sb, sb.Capacity) > 0 ? sb.ToString() : "?";
                        name = name.Substring(name.LastIndexOf('\\') + 1);
                        moduleNames[region.AllocationBase] = name;
                    }
                    where = $"image {name}+{loc - region.AllocationBase:x}";
                }
                else
                    where = $"{(region.Type == 0x20000 ? "private" : "mapped")} memory {loc:x} (region {region.BaseAddress:x}, {region.RegionSize / 1024} KB)";
            }
            t.HitCount++;
            if (t.Hits.Count < MaxHitsPerObject)
                t.Hits.Add($"{where} -> +{value - t.Start:x}");
        }

        [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr mono_class_get_name(IntPtr klass);

        [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr mono_class_get_namespace(IntPtr klass);

        // Typed (kind 3) blocks hold managed objects: vtable at +0, MonoClass* at vtable+0.
        private static string ClassOf(long obj, int kind)
        {
            if (kind != 3)
                return "untyped";
            long vt = Marshal.ReadInt64(new IntPtr(obj));
            if (vt == 0)
                return "null vtable";
            var klass = new IntPtr(Marshal.ReadInt64(new IntPtr(vt)));
            return Marshal.PtrToStringAnsi(mono_class_get_namespace(klass)) + "." + Marshal.PtrToStringAnsi(mono_class_get_name(klass));
        }

        private static int Find(long[] starts, List<Target> targets, long p)
        {
            int i = Array.BinarySearch(starts, p);
            if (i < 0)
                i = ~i - 1;
            return i >= 0 && p < targets[i].Start + targets[i].Size ? i : -1;
        }

        private static long VTable(object o)
        {
            var h = GCHandle.Alloc(o, GCHandleType.Pinned);
            try
            {
                // Arrays: data at +0x20; strings: chars at +0x14. Both start with the vtable pointer.
                long data = h.AddrOfPinnedObject().ToInt64();
                return Marshal.ReadInt64(new IntPtr(data - (o is string ? 0x14 : 0x20)));
            }
            finally
            {
                h.Free();
            }
        }
    }
}
