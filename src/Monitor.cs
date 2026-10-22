using System;
using System.Collections;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PerfeBT
{
    // Frame clock: detects hitches, segments busy periods, writes reports.
    internal sealed class Monitor : MonoBehaviour
    {
        private Settings s;
        private string reportDir;
        private SegmentStats seg = new SegmentStats();
        private long segStart, lastFrameEnd, lastHitchAt;
        private int gcAtSegmentStart, gcAtFrameStart;
        private long usedAtFrameEnd;

        // [alloc] window: managed allocation (used-size growth in frames without a collection) per AllocWindowSeconds.
        private long windowStart, windowAllocBytes, windowFrames, windowUnknownFrames;
        private double windowWorstFrameMs;

        // [ws] working-set trims by other software (e.g. a working-set cleaner) make the next collection page the heap back in.
        private long lastWsCheck;
        private ProcMem.Sample lastWs;
        private bool busy;
        private string pendingReason;

        internal static Monitor Create(Settings settings, string reportDir)
        {
            var go = new GameObject("PerfeBT.Monitor");
            DontDestroyOnLoad(go);
            var m = go.AddComponent<Monitor>();
            m.s = settings;
            m.reportDir = reportDir;
            return m;
        }

        private void Awake()
        {
            segStart = lastFrameEnd = Stopwatch.GetTimestamp();
            gcAtSegmentStart = gcAtFrameStart = GC.CollectionCount(0);
            usedAtFrameEnd = GC.GetTotalMemory(false);
            windowStart = lastWsCheck = segStart;
            lastWs = ProcMem.Read();
            SceneManager.sceneLoaded += (scene, _) => pendingReason = "scene_" + scene.name;
        }

        private IEnumerator Start()
        {
            var wait = new WaitForEndOfFrame();
            while (true)
            {
                yield return wait;
                try { EndFrame(); }
                catch (Exception e) { Log.Write("[monitor] " + e); }
            }
        }

        private void Update()
        {
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (ctrl && shift && Input.GetKeyDown(KeyCode.P))
                pendingReason = "hotkey";
            if (ctrl && shift && Input.GetKeyDown(KeyCode.G))
            {
                GcDump.Forced = true;
                LoadGcPause.CollectNow("hotkey dump");
            }
            if (ctrl && shift && Input.GetKeyDown(KeyCode.D))
                DelayedCallCensus.Run("hotkey");
        }

        private void EndFrame()
        {
            long now = Stopwatch.GetTimestamp();
            double frameMs = Prof.Ms(now - lastFrameEnd);
            lastFrameEnd = now;

            int gcNow = GC.CollectionCount(0);
            int gcDelta = gcNow - gcAtFrameStart;
            gcAtFrameStart = gcNow;

            long used = GC.GetTotalMemory(false);
            double frameAllocMb = -1;
            if (gcDelta == 0 && used >= usedAtFrameEnd)
            {
                frameAllocMb = (used - usedAtFrameEnd) / 1048576.0;
                windowAllocBytes += used - usedAtFrameEnd;
            }
            else
                windowUnknownFrames++;
            usedAtFrameEnd = used;
            windowFrames++;
            if (frameMs > windowWorstFrameMs)
                windowWorstFrameMs = frameMs;
            if (s.AllocWindowSeconds > 0 && Prof.Ms(now - windowStart) >= s.AllocWindowSeconds * 1000)
                WriteAllocWindow(now, used);
            if (Prof.Ms(now - lastWsCheck) >= 1000)
                CheckWorkingSet(now);

            double rootMs = Prof.Ms(Prof.FrameRootTicks);
            seg.Frames++;
            seg.RootMs += rootMs;
            if (frameMs > seg.WorstFrameMs)
                seg.WorstFrameMs = frameMs;

            if (frameMs >= s.HitchMs)
            {
                seg.HitchFrames++;
                seg.HitchMs += frameMs;
                seg.UninstrumentedHitchMs += Math.Max(0, frameMs - rootMs);
                string text = Report.Hitch(frameMs, gcDelta, frameAllocMb, s.HitchTopN);
                seg.HitchLog.Append($"[{DateTime.Now:HH:mm:ss.fff}] ").Append(text);
                if (frameMs >= 1000)
                    Log.Write(text.TrimEnd());
                busy = true;
                lastHitchAt = now;
            }
            Prof.ResetFrame();

            if (pendingReason == null && busy && Prof.Ms(now - lastHitchAt) >= s.AutoSegmentCalmSeconds * 1000)
            {
                busy = false;
                if (seg.HitchMs >= s.AutoSegmentMinBusyMs)
                    pendingReason = "auto";
            }

            if (pendingReason != null)
            {
                seg.WallMs = Prof.Ms(now - segStart);
                seg.GcCount = gcNow - gcAtSegmentStart;
                Report.Segment(reportDir, pendingReason, seg, s.TopN);
                pendingReason = null;
                seg = new SegmentStats();
                segStart = Stopwatch.GetTimestamp();
                gcAtSegmentStart = GC.CollectionCount(0);
                busy = false;
                Prof.ResetSegment();
            }
        }

        private void WriteAllocWindow(long now, long used)
        {
            double secs = Prof.Ms(now - windowStart) / 1000;
            double mb = windowAllocBytes / 1048576.0;
            var sb = new System.Text.StringBuilder();
            sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                "[alloc] {0:F0} s: {1:F0} MB allocated ({2:F1} MB/s), {3} frames (worst {4:F0} ms), {5} frames unmeasured (GC), used {6} MB, scene {7}, combat {8}",
                secs, mb, mb / Math.Max(0.001, secs), windowFrames, windowWorstFrameMs, windowUnknownFrames, used >> 20,
                SceneManager.GetActiveScene().name, InCombat() ? "yes" : "no");
            if (Prof.TrackAlloc)
            {
                var ids = new System.Collections.Generic.List<int>();
                for (int i = 0; i < Prof.Count; i++)
                    if (Prof.WindowAlloc[i] >= 1048576)
                        ids.Add(i);
                ids.Sort((a, b) => Prof.WindowAlloc[b].CompareTo(Prof.WindowAlloc[a]));
                long tracked = 0;
                for (int i = 0; i < Prof.Count; i++)
                    tracked += Prof.WindowAlloc[i];
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "; instrumented {0:F0} MB", tracked / 1048576.0);
                for (int k = 0; k < ids.Count && k < s.AllocWindowTopN; k++)
                    sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "\n    {0,8:F1} MB  {1,-28} {2}",
                        Prof.WindowAlloc[ids[k]] / 1048576.0, Prof.Owners[ids[k]], Prof.Labels[ids[k]]);
                Array.Clear(Prof.WindowAlloc, 0, Prof.WindowAlloc.Length);
            }
            Log.Write(sb.ToString());
            windowStart = now;
            windowAllocBytes = windowFrames = windowUnknownFrames = 0;
            windowWorstFrameMs = 0;
        }

        private void CheckWorkingSet(long now)
        {
            lastWsCheck = now;
            var ws = ProcMem.Read();
            if (ws.WorkingSetMb < lastWs.WorkingSetMb / 2 && lastWs.WorkingSetMb - ws.WorkingSetMb >= 1024)
                Log.Write($"[ws] working set trimmed {lastWs.WorkingSetMb} -> {ws.WorkingSetMb} MB (private {ws.PrivateMb} MB)");
            lastWs = ws;
        }

        private static bool InCombat()
        {
            try { return BattleTech.UnityGameInstance.BattleTechGame?.Combat != null; }
            catch { return false; }
        }
    }
}
