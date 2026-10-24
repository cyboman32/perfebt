using System;

namespace PerfeBT
{
    // Process working set, private bytes and page-fault count (GetProcessMemoryInfo).
    internal static class ProcMem
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Counters
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
        private static extern bool GetProcessMemoryInfo(IntPtr process, ref Counters counters, uint size);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        internal struct Sample
        {
            public uint Faults;
            public long WorkingSetMb, PrivateMb;
        }

        internal static Sample Read()
        {
            var c = new Counters { cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Counters)) };
            if (!GetProcessMemoryInfo(GetCurrentProcess(), ref c, c.cb))
                return default(Sample);
            return new Sample
            {
                Faults = c.PageFaultCount,
                WorkingSetMb = (long)(c.WorkingSetSize.ToUInt64() >> 20),
                PrivateMb = (long)(c.PrivateUsage.ToUInt64() >> 20),
            };
        }

        // Page faults from a to b (the counter is 32-bit and wraps).
        internal static uint FaultsBetween(Sample a, Sample b) => unchecked(b.Faults - a.Faults);
    }
}
