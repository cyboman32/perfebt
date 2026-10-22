using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace PerfeBT
{
    // Main-thread call-stack timer. All mutable state is touched only on the Unity main thread,
    // except OffThreadCalls (Interlocked) and the id map (copy-on-write, read-only after publish).
    internal static class Prof
    {
        internal const int MaxDepth = 1024;
        internal static int MainThreadId;

        // Published id map; replaced wholesale by Register on the main thread.
        private static Dictionary<IntPtr, int> idByHandle = new Dictionary<IntPtr, int>();

        internal static readonly List<MethodBase> Methods = new List<MethodBase>();
        internal static readonly List<string> Labels = new List<string>();
        internal static readonly List<string> Owners = new List<string>();

        // Segment totals.
        internal static long[] Calls = new long[0];
        internal static long[] Incl = new long[0];
        internal static long[] Excl = new long[0];
        internal static long[] OffThreadCalls = new long[0];

        // Allocation tracking (Settings.ProfileAlloc): exclusive bytes by which the managed heap's used size grew
        // inside each method on the main thread. With the collector off (LoadGcPause budget mode) used size only grows
        // by allocation (in heap-block steps, so small methods are right on average). Activations during which a
        // collection ran are dropped. Other threads' allocations land on whatever ran at the time.
        internal static bool TrackAlloc;
        internal static long[] AllocExcl = new long[0];
        internal static long[] WindowAlloc = new long[0]; // since the last [alloc] window line
        internal static long[] FrameAlloc = new long[0];  // current frame, reset through the touched list

        // Current-frame accumulators, reset through the touched list.
        internal static long[] FrameExcl = new long[0];
        internal static long[] FrameCalls = new long[0];
        private static int[] touched = new int[256];
        private static int touchedCount;
        internal static long FrameRootTicks;

        // Recursion guard: inclusive time is only added for the outermost activation of a method.
        private static int[] activeDepth = new int[0];

        private static readonly int[] stackId = new int[MaxDepth];
        private static readonly long[] stackStart = new long[MaxDepth];
        private static readonly long[] stackChild = new long[MaxDepth];
        private static readonly long[] stackAlloc = new long[MaxDepth];
        private static readonly long[] stackChildAlloc = new long[MaxDepth];
        private static readonly int[] stackGc = new int[MaxDepth];
        private static int depth;

        internal static int Count => Methods.Count;

        internal static bool IsRegistered(MethodBase m) => idByHandle.ContainsKey(m.MethodHandle.Value);

        // Main thread only, before the method is patched.
        internal static int Register(MethodBase m, string label, string owner)
        {
            var handle = m.MethodHandle.Value;
            if (idByHandle.TryGetValue(handle, out var existing))
                return existing;

            int id = Methods.Count;
            Methods.Add(m);
            Labels.Add(label);
            Owners.Add(owner);
            EnsureCapacity(id + 1);

            var copy = new Dictionary<IntPtr, int>(idByHandle) { [handle] = id };
            Volatile.Write(ref idByHandle, copy);
            return id;
        }

        private static void EnsureCapacity(int n)
        {
            if (Calls.Length >= n)
                return;
            int size = Math.Max(n, Calls.Length * 2 + 64);
            Array.Resize(ref Calls, size);
            Array.Resize(ref Incl, size);
            Array.Resize(ref Excl, size);
            Array.Resize(ref FrameExcl, size);
            Array.Resize(ref FrameCalls, size);
            Array.Resize(ref activeDepth, size);
            Array.Resize(ref AllocExcl, size);
            Array.Resize(ref WindowAlloc, size);
            Array.Resize(ref FrameAlloc, size);
            var off = new long[size];
            Array.Copy(OffThreadCalls, off, OffThreadCalls.Length);
            Volatile.Write(ref OffThreadCalls, off);
        }

        // Returns the id to pass to Exit, or -1 when nothing was pushed.
        internal static int Enter(MethodBase original)
        {
            if (original == null || !idByHandle.TryGetValue(original.MethodHandle.Value, out int id))
                return -1;

            if (Thread.CurrentThread.ManagedThreadId != MainThreadId)
            {
                Interlocked.Increment(ref OffThreadCalls[id]);
                return -1;
            }

            if (depth >= MaxDepth)
                return -1;

            stackId[depth] = id;
            stackChild[depth] = 0;
            stackStart[depth] = Stopwatch.GetTimestamp();
            if (TrackAlloc)
            {
                stackChildAlloc[depth] = 0;
                stackGc[depth] = GC.CollectionCount(0);
                stackAlloc[depth] = GC.GetTotalMemory(false);
            }
            depth++;
            activeDepth[id]++;
            return id;
        }

        internal static void Exit(int id)
        {
            long now = Stopwatch.GetTimestamp();

            // Unwind frames that never reached their finalizer (should not happen, but keeps the stack sane).
            while (depth > 0 && stackId[depth - 1] != id)
                Pop(now);
            if (depth > 0)
                Pop(now);
        }

        private static void Pop(long now)
        {
            depth--;
            int id = stackId[depth];
            long incl = now - stackStart[depth];
            long excl = incl - stackChild[depth];

            activeDepth[id]--;
            Calls[id]++;
            Excl[id] += excl;
            if (activeDepth[id] == 0)
                Incl[id] += incl;

            if (FrameCalls[id] == 0)
            {
                if (touchedCount == touched.Length)
                    Array.Resize(ref touched, touched.Length * 2);
                touched[touchedCount++] = id;
            }
            FrameCalls[id]++;
            FrameExcl[id] += excl;

            if (depth > 0)
                stackChild[depth - 1] += incl;
            else
                FrameRootTicks += incl;

            if (TrackAlloc)
            {
                long allocIncl = GC.GetTotalMemory(false) - stackAlloc[depth];
                if (GC.CollectionCount(0) != stackGc[depth] || allocIncl < 0)
                    return; // a collection ran inside: unknown (the enclosing activations are dropped too)
                long allocExcl = Math.Max(0, allocIncl - stackChildAlloc[depth]);
                AllocExcl[id] += allocExcl;
                WindowAlloc[id] += allocExcl;
                FrameAlloc[id] += allocExcl;
                if (depth > 0)
                    stackChildAlloc[depth - 1] += allocIncl;
            }
        }

        internal static List<int> FrameTouched()
        {
            var list = new List<int>(touchedCount);
            for (int i = 0; i < touchedCount; i++)
                list.Add(touched[i]);
            return list;
        }

        internal static void ResetFrame()
        {
            for (int i = 0; i < touchedCount; i++)
            {
                int id = touched[i];
                FrameExcl[id] = 0;
                FrameCalls[id] = 0;
                FrameAlloc[id] = 0;
            }
            touchedCount = 0;
            FrameRootTicks = 0;
        }

        internal static void ResetSegment()
        {
            Array.Clear(Calls, 0, Calls.Length);
            Array.Clear(Incl, 0, Incl.Length);
            Array.Clear(Excl, 0, Excl.Length);
            Array.Clear(AllocExcl, 0, AllocExcl.Length);
            var off = OffThreadCalls;
            for (int i = 0; i < off.Length; i++)
                Interlocked.Exchange(ref off[i], 0);
        }

        internal static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }

    // Harmony hooks applied to every instrumented method.
    internal static class Hooks
    {
        public static void Prefix(MethodBase __originalMethod, out int __state)
        {
            __state = Prof.Enter(__originalMethod);
        }

        public static void Finalizer(int __state)
        {
            if (__state >= 0)
                Prof.Exit(__state);
        }
    }
}
