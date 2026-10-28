using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PerfeBT
{
    // Diagnostic, part of the heap census: what are the bytes the collector keeps but the census crawl never reached?
    // Census 2026-10-28 14:05: used 10,042 MB after a full GC, crawled 3,962 MB, "unreached" 6,080 MB. Boehm's "used"
    // also counts free slots of partly used small-object blocks, so the difference mixes fragmentation with objects the
    // collector retains without a managed path (conservative words, GC handles, native roots).
    //
    // Runs right after the crawl while the GC is still disabled: the mark bits are those of the census's own full
    // collection, objects allocated since then are unmarked, and no address has been reused. Walks every heap block
    // (GC_heap_sects + GC_find_header, the same verified addresses as RootScan) and every object slot in it:
    //   marked + crawled = reached; marked + not crawled = retained without a managed path (by type); unmarked = free
    //   or garbage in a used block (fragmentation, by object size).
    // hblkhdr (bdwgc, MARK_BIT_PER_GRANULE, 64-bit): kind @0x18, flags @0x19 (4 = free), hb_sz @0x20, hb_n_marks @0x38,
    // hb_marks @0x40, one mark per 16-byte granule. Whether marks are bytes or bits is chosen by comparing both with
    // hb_n_marks on the first blocks; the scan stands down when neither fits.
    internal static class HeapUnreached
    {
        private const int Granule = 16, Block = 0x1000, TopTypes = 60, TopObjects = 25;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] long[] buffer, IntPtr size, out IntPtr read);

        internal sealed class KindStats
        {
            internal long Blocks, MarkedBytes, ReachedBytes, UnmarkedBytes;
        }

        internal sealed class Result
        {
            internal string Error;
            internal string Layout;
            internal long Ms, FreeBlockBytes, LayoutMatches, LayoutSamples;
            internal readonly SortedDictionary<int, KindStats> Kinds = new SortedDictionary<int, KindStats>();
            internal List<KeyValuePair<string, long[]>> Types = new List<KeyValuePair<string, long[]>>();
            internal List<KeyValuePair<long, long[]>> FreeBySize = new List<KeyValuePair<long, long[]>>();
            internal List<string> Largest = new List<string>();
        }

        // Where the scan was, for the report when it fails: on Mono a bad native read surfaces as NullReferenceException.
        private static string phase;
        private static long atBlock, atHeader, atSize, atSlot;

        // Runs only with the GC disabled (the caller holds it off for the crawl).
        internal static Result Run(HeapWalker walker)
        {
            var result = new Result();
            var sw = Stopwatch.StartNew();
            phase = "start";
            atBlock = atHeader = atSize = atSlot = 0;
            try
            {
                Scan(walker, result);
            }
            catch (Exception e)
            {
                result.Error = $"{e.GetType().Name}: {e.Message} in {phase} (block {atBlock:x}, header {atHeader:x}, size {atSize}, slot {atSlot:x})" +
                               Environment.NewLine + e.StackTrace;
            }
            result.Ms = sw.ElapsedMilliseconds;
            return result;
        }

        private static void Scan(HeapWalker walker, Result result)
        {
            phase = "find header";
            var monoBase = GetModuleHandle("mono-2.0-bdwgc.dll");
            if (monoBase == IntPtr.Zero)
                throw new DllNotFoundException("mono-2.0-bdwgc.dll not loaded");
            var findHeader = RootScan.FindHeader(monoBase);
            long sects = Marshal.ReadInt64(monoBase, RootScan.RvaNHeapSects);

            // 1. Mark layout: bytes or bits per granule, whichever matches hb_n_marks on the first used small blocks.
            bool? bytes = null;
            int byteHits = 0, bitHits = 0, samples = 0;
            phase = "layout sample";
            ForEachBlock(monoBase, findHeader, sects, (h, hdr, sz, free) =>
            {
                if (free || sz >= Block / 2 || samples >= 4000)
                    return samples < 4000;
                long n = Marshal.ReadInt64(new IntPtr(hdr + 0x38));
                long nb = 0, nbit = 0;
                for (long off = 0; off + sz <= Block; off += sz)
                {
                    long g = off / Granule;
                    if (Marshal.ReadByte(new IntPtr(hdr + 0x40 + g)) != 0) nb++;
                    if ((Marshal.ReadByte(new IntPtr(hdr + 0x40 + (g >> 3))) & (1 << (int)(g & 7))) != 0) nbit++;
                }
                if (nb == n) byteHits++;
                if (nbit == n) bitHits++;
                samples++;
                return true;
            });
            result.LayoutSamples = samples;
            if (samples == 0)
                throw new InvalidOperationException("no used small-object block found");
            if (byteHits >= samples * 9 / 10 && byteHits > bitHits)
                bytes = true;
            else if (bitHits >= samples * 9 / 10 && bitHits > byteHits)
                bytes = false;
            result.LayoutMatches = Math.Max(byteHits, bitHits);
            if (bytes == null)
                throw new NotSupportedException($"mark layout unknown: hb_n_marks matched mark bytes in {byteHits}, mark bits in {bitHits} of {samples} blocks");
            result.Layout = bytes.Value ? "mark bytes" : "mark bits";

            // 2. Every slot of every used block.
            var byVtable = new Dictionary<long, long[]>(); // vtable -> {count, bytes}
            var freeBySize = new Dictionary<long, long[]>(); // slot size -> {count, bytes}
            var largest = new List<KeyValuePair<long, long>>(); // (size, address)
            long freeBlockBytes = 0;
            bool useBytes = bytes.Value;
            phase = "slot walk";
            ForEachBlock(monoBase, findHeader, sects, (h, hdr, sz, free) =>
            {
                if (free)
                {
                    freeBlockBytes += Math.Max(Block, (sz + Block - 1) & ~(long)(Block - 1));
                    return true;
                }
                int kind = Marshal.ReadByte(new IntPtr(hdr + 0x18));
                if (!result.Kinds.TryGetValue(kind, out var ks))
                    result.Kinds[kind] = ks = new KindStats();
                ks.Blocks++;
                long limit = Math.Max(Block, sz);
                for (long off = 0; off + sz <= limit; off += sz)
                {
                    long g = off / Granule;
                    bool marked = useBytes
                        ? Marshal.ReadByte(new IntPtr(hdr + 0x40 + g)) != 0
                        : (Marshal.ReadByte(new IntPtr(hdr + 0x40 + (g >> 3))) & (1 << (int)(g & 7))) != 0;
                    if (!marked)
                    {
                        ks.UnmarkedBytes += sz;
                        if (!freeBySize.TryGetValue(sz, out var f))
                            freeBySize[sz] = f = new long[2];
                        f[0]++;
                        f[1] += sz;
                        continue;
                    }
                    ks.MarkedBytes += sz;
                    long a = h + off;
                    atSlot = a;
                    if (walker.Visited(a))
                    {
                        ks.ReachedBytes += sz;
                        continue;
                    }
                    long vt = Marshal.ReadInt64(new IntPtr(a));
                    if (!byVtable.TryGetValue(vt, out var c))
                        byVtable[vt] = c = new long[2];
                    c[0]++;
                    c[1] += sz;
                    if (sz >= 1 << 20)
                        largest.Add(new KeyValuePair<long, long>(sz, a));
                }
                return true;
            });

            // 3. Names: the crawl's own vtables, else the MonoClass read through ReadProcessMemory.
            phase = "names";
            atBlock = atHeader = atSize = atSlot = 0;
            var names = new Namer(walker);
            var types = new Dictionary<string, long[]>();
            foreach (var kv in byVtable)
            {
                var name = names.Name(kv.Key);
                if (!types.TryGetValue(name, out var t))
                    types[name] = t = new long[2];
                t[0] += kv.Value[0];
                t[1] += kv.Value[1];
            }
            result.Types = types.OrderByDescending(kv => kv.Value[1]).Take(TopTypes).ToList();
            result.FreeBySize = freeBySize.OrderByDescending(kv => kv.Value[1]).Take(20).ToList();
            phase = "largest";
            result.Largest = largest.OrderByDescending(kv => kv.Key).Take(TopObjects)
                .Select(kv => $"{kv.Key / 1048576.0,9:F1} MB  {names.Name(Marshal.ReadInt64(new IntPtr(kv.Value)))}  @{kv.Value:x}").ToList();
            result.FreeBlockBytes = freeBlockBytes;
        }

        // Calls visit(blockStart, header, hb_sz, free) for each block start; stops when visit returns false.
        private static void ForEachBlock(IntPtr monoBase, RootScan.PtrFn findHeader, long sects, Func<long, long, long, bool, bool> visit)
        {
            for (long s = 0; s < sects; s++)
            {
                long start = Marshal.ReadInt64(monoBase, (int)(RootScan.RvaGcArrays + RootScan.OffHeapSects + s * 16));
                long bytes = Marshal.ReadInt64(monoBase, (int)(RootScan.RvaGcArrays + RootScan.OffHeapSects + s * 16 + 8));
                for (long h = start; h < start + bytes;)
                {
                    atBlock = h;
                    atHeader = 0;
                    long hdr = findHeader(new IntPtr(h)).ToInt64();
                    atHeader = hdr;
                    if (hdr <= 0xfff)
                    {
                        h += Block;
                        continue;
                    }
                    long sz = Marshal.ReadInt64(new IntPtr(hdr + 0x20));
                    atSize = sz;
                    bool free = (Marshal.ReadByte(new IntPtr(hdr + 0x19)) & 4) != 0;
                    if (sz <= 0)
                    {
                        h += Block;
                        continue;
                    }
                    if (!visit(h, hdr, sz, free))
                        return;
                    long span = Math.Max(Block, (sz + Block - 1) & ~(long)(Block - 1));
                    h += free || sz >= Block ? span : Block;
                }
            }
        }

        // vtable -> type name. MonoVTable starts with its MonoClass*; MonoClass starts with element_class, and rank is
        // the byte at RankOffset (checked on byte[] and string before use). Unknown classes keep their addresses.
        private sealed class Namer
        {
            private const int RankOffset = 0x1a;
            private readonly HeapWalker walker;
            private readonly Dictionary<long, string> byClass = new Dictionary<long, string>();
            private readonly Dictionary<long, string> cache = new Dictionary<long, string>();
            private readonly bool arraysOk;
            private readonly long[] buffer = new long[1];

            internal Namer(HeapWalker walker)
            {
                this.walker = walker;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] all;
                    try { all = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { all = e.Types; }
                    catch { continue; }
                    foreach (var t in all)
                    {
                        if (t == null)
                            continue;
                        try
                        {
                            long klass = MonoApi.mono_class_from_mono_type(t.TypeHandle.Value).ToInt64();
                            if (klass != 0 && !byClass.ContainsKey(klass))
                                byClass[klass] = HeapWalker.TypeName(t);
                        }
                        catch { }
                    }
                }
                long byteArray = MonoApi.mono_class_from_mono_type(typeof(byte[]).TypeHandle.Value).ToInt64();
                long byteClass = MonoApi.mono_class_from_mono_type(typeof(byte).TypeHandle.Value).ToInt64();
                long stringClass = MonoApi.mono_class_from_mono_type(typeof(string).TypeHandle.Value).ToInt64();
                arraysOk = Read(byteArray) == byteClass && Rank(byteArray) == 1 && Read(stringClass) == stringClass && Rank(stringClass) == 0;
            }

            internal string Name(long vt)
            {
                if (cache.TryGetValue(vt, out var name))
                    return name;
                var t = walker.TypeOfVtable(vt);
                if (t != null)
                    name = HeapWalker.TypeName(t);
                else
                {
                    long klass = Read(vt);
                    if (klass != 0 && byClass.TryGetValue(klass, out name))
                        name += " (not crawled)";
                    else if (klass != 0 && arraysOk && Rank(klass) == 1 && byClass.TryGetValue(Read(klass), out var element))
                        name = element + "[] (not crawled)";
                    else
                        name = $"unknown vtable {vt:x}" + (klass != 0 ? $" class {klass:x}" : "");
                }
                cache[vt] = name;
                return name;
            }

            private long Read(long address)
            {
                if (address == 0)
                    return 0;
                return ReadProcessMemory(GetCurrentProcess(), new IntPtr(address), buffer, new IntPtr(8), out var read) && read.ToInt64() == 8 ? buffer[0] : 0;
            }

            private int Rank(long klass) => klass == 0 ? -1 : (int)((ulong)Read(klass + (RankOffset & ~7)) >> (8 * (RankOffset & 7)) & 0xff);
        }

        internal static void Write(TextWriter w, Result r)
        {
            w.WriteLine();
            w.WriteLine("== Collector view: every heap block after the census GC (marked = kept by the collector) ==");
            if (r.Error != null)
            {
                w.WriteLine($"skipped: {r.Error}");
                return;
            }
            w.WriteLine($"{r.Layout} ({r.LayoutMatches} of {r.LayoutSamples} sample blocks match hb_n_marks), walk {r.Ms} ms, free blocks {HeapWalker.Mb(r.FreeBlockBytes)} MB");
            w.WriteLine("kind      blocks   marked MB   reached MB   kept, not reached MB   unmarked slots MB");
            long m = 0, rb = 0, u = 0;
            foreach (var kv in r.Kinds)
            {
                var k = kv.Value;
                string kind = kv.Key == 0 ? "ptrfree" : kv.Key == 1 ? "normal" : kv.Key == 2 ? "uncoll" : "kind " + kv.Key;
                w.WriteLine($"{kind,-8}{k.Blocks,9:N0} {HeapWalker.Mb(k.MarkedBytes),11} {HeapWalker.Mb(k.ReachedBytes),12} {HeapWalker.Mb(k.MarkedBytes - k.ReachedBytes),22} {HeapWalker.Mb(k.UnmarkedBytes),19}");
                m += k.MarkedBytes;
                rb += k.ReachedBytes;
                u += k.UnmarkedBytes;
            }
            w.WriteLine($"total   {"",9} {HeapWalker.Mb(m),11} {HeapWalker.Mb(rb),12} {HeapWalker.Mb(m - rb),22} {HeapWalker.Mb(u),19}");
            w.WriteLine("(allocator sizes: slot size of the block, so slightly above the census's shallow sizes)");
            w.WriteLine();
            w.WriteLine($"== Kept by the collector, not reached by the crawl: types by bytes (top {TopTypes}) ==");
            w.WriteLine("       MB        count  type");
            foreach (var kv in r.Types)
                w.WriteLine($"{HeapWalker.Mb(kv.Value[1]),9} {kv.Value[0],12:N0}  {kv.Key}");
            w.WriteLine();
            w.WriteLine($"== Largest kept, not reached objects (>= 1 MB, top {TopObjects}) ==");
            foreach (var line in r.Largest)
                w.WriteLine(line);
            w.WriteLine();
            w.WriteLine("== Unmarked slots in used blocks by slot size (free or garbage: fragmentation) ==");
            w.WriteLine("    bytes        slots        MB");
            foreach (var kv in r.FreeBySize)
                w.WriteLine($"{kv.Key,9:N0} {kv.Value[0],12:N0} {HeapWalker.Mb(kv.Value[1]),9}");
        }
    }
}
