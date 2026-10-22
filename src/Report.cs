using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PerfeBT
{
    internal static class Report
    {
        internal static string Hitch(double frameMs, int gcDelta, double frameAllocMb, int topN)
        {
            var ids = Prof.FrameTouched();
            ids.Sort((a, b) => Prof.FrameExcl[b].CompareTo(Prof.FrameExcl[a]));
            double rootMs = Prof.Ms(Prof.FrameRootTicks);

            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "HITCH {0:F0} ms | instrumented {1:F0} ms | uninstrumented {2:F0} ms | GCs {3}{4}\n",
                frameMs, rootMs, Math.Max(0, frameMs - rootMs), gcDelta,
                frameAllocMb >= 0 ? $" | allocated {frameAllocMb:F0} MB" : "");
            foreach (int id in ids.Take(topN))
            {
                double ms = Prof.Ms(Prof.FrameExcl[id]);
                if (ms < 1)
                    break;
                sb.AppendFormat(CultureInfo.InvariantCulture, "    {0,9:F1} ms {1,8} calls  {2,-28} {3}{4}\n",
                    ms, Prof.FrameCalls[id], Prof.Owners[id], Prof.Labels[id], FrameAllocNote(id));
            }
            return sb.ToString();
        }

        private static string FrameAllocNote(int id)
        {
            if (!Prof.TrackAlloc || Prof.FrameAlloc[id] < 1048576)
                return "";
            return string.Format(CultureInfo.InvariantCulture, "  [{0:F0} MB]", Prof.FrameAlloc[id] / 1048576.0);
        }

        internal static void Segment(string dir, string reason, SegmentStats seg, int topN)
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Sanitize(reason)}.txt");

            var ids = Enumerable.Range(0, Prof.Count).Where(i => Prof.Calls[i] > 0 || Prof.OffThreadCalls[i] > 0).ToList();
            var sb = new StringBuilder();
            var ic = CultureInfo.InvariantCulture;

            sb.AppendFormat(ic, "PerfeBT segment: {0}\n", reason);
            sb.AppendFormat(ic, "wall {0:F1} s | frames {1} | hitch frames {2} ({3:F1} s) | worst frame {4:F0} ms\n",
                seg.WallMs / 1000, seg.Frames, seg.HitchFrames, seg.HitchMs / 1000, seg.WorstFrameMs);
            sb.AppendFormat(ic, "instrumented main-thread {0:F1} s | uninstrumented in hitch frames {1:F1} s\n",
                seg.RootMs / 1000, seg.UninstrumentedHitchMs / 1000);
            sb.AppendFormat(ic, "GC collections {0} | mono heap used {1:F0} MB / reserved {2:F0} MB\n\n",
                seg.GcCount, Mem.UsedMb(), Mem.HeapMb());

            sb.Append("== Owners by exclusive time ==\n");
            foreach (var g in ids.GroupBy(i => Prof.Owners[i])
                         .Select(g => new { Owner = g.Key, Excl = g.Sum(i => Prof.Excl[i]), Calls = g.Sum(i => Prof.Calls[i]) })
                         .OrderByDescending(x => x.Excl).Take(topN))
            {
                if (Prof.Ms(g.Excl) < 1) break;
                sb.AppendFormat(ic, "  {0,10:F1} ms {1,10} calls  {2}\n", Prof.Ms(g.Excl), g.Calls, g.Owner);
            }

            Table(sb, "== Methods by exclusive time ==", ids, Prof.Excl, topN);
            Table(sb, "== Methods by inclusive time ==", ids, Prof.Incl, topN);
            if (Prof.TrackAlloc)
            {
                sb.AppendFormat(ic, "\n== Methods by allocated MB (exclusive, main thread; {0:F0} MB total) ==\n",
                    ids.Sum(i => Prof.AllocExcl[i]) / 1048576.0);
                foreach (int id in ids.OrderByDescending(i => Prof.AllocExcl[i]).Take(topN))
                {
                    double mb = Prof.AllocExcl[id] / 1048576.0;
                    if (mb < 1) break;
                    sb.AppendFormat(ic, "  {0,10:F1} MB {1,10} calls {2,10:F1} KB/call  {3,-28} {4}\n",
                        mb, Prof.Calls[id], Prof.AllocExcl[id] / 1024.0 / Math.Max(1, Prof.Calls[id]), Prof.Owners[id], Prof.Labels[id]);
                }
            }

            sb.Append("\n== Methods by call count ==\n");
            foreach (int id in ids.OrderByDescending(i => Prof.Calls[i]).Take(topN))
                sb.AppendFormat(ic, "  {0,10} calls {1,10:F1} ms incl  {2,-28} {3}\n",
                    Prof.Calls[id], Prof.Ms(Prof.Incl[id]), Prof.Owners[id], Prof.Labels[id]);

            sb.Append("\n== Off-main-thread calls (not timed) ==\n");
            foreach (int id in ids.Where(i => Prof.OffThreadCalls[i] > 0).OrderByDescending(i => Prof.OffThreadCalls[i]).Take(topN))
                sb.AppendFormat(ic, "  {0,10} calls  {1,-28} {2}\n", Prof.OffThreadCalls[id], Prof.Owners[id], Prof.Labels[id]);

            sb.Append("\n== Hitch log ==\n");
            sb.Append(seg.HitchLog);

            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
            Log.Write($"[segment] {reason}: wall {seg.WallMs / 1000:F1}s hitch {seg.HitchMs / 1000:F1}s -> {file}");
        }

        private static void Table(StringBuilder sb, string title, List<int> ids, long[] metric, int topN)
        {
            sb.Append('\n').Append(title).Append('\n');
            foreach (int id in ids.OrderByDescending(i => metric[i]).Take(topN))
            {
                if (Prof.Ms(metric[id]) < 1) break;
                sb.AppendFormat(CultureInfo.InvariantCulture, "  {0,10:F1} ms {1,10} calls {2,8:F3} ms/call  {3,-28} {4}\n",
                    Prof.Ms(metric[id]), Prof.Calls[id], Prof.Ms(metric[id]) / Math.Max(1, Prof.Calls[id]), Prof.Owners[id], Prof.Labels[id]);
            }
        }

        private static string Sanitize(string s)
        {
            var chars = s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
            return new string(chars, 0, Math.Min(chars.Length, 60));
        }
    }

    internal sealed class SegmentStats
    {
        internal double WallMs, HitchMs, WorstFrameMs, RootMs, UninstrumentedHitchMs;
        internal long Frames, HitchFrames;
        internal int GcCount;
        internal readonly StringBuilder HitchLog = new StringBuilder();
    }

    internal static class Mem
    {
        internal static double UsedMb() => UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / 1048576.0;
        internal static double HeapMb() => UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() / 1048576.0;
    }
}
