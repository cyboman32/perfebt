using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using BattleTech.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PerfeBT
{
    // Diagnostic for the black screen during level transitions: from each "Loading" scene request until
    // TransitionWatchTailSeconds after the target scene's Single load, logs ([screen] lines) every change of what
    // decides the picture (loading curtain visible, active scene, enabled cameras with depth/clear flags), every
    // LoadingCurtain show/hide with its caller, and saves a screenshot to reports/screens/<ts>/ every
    // TransitionWatchShotSeconds. Read-only.
    internal static class TransitionWatch
    {
        internal static float TailSeconds = 20f;
        internal static float ShotSeconds = 1f;
        internal static int MaxShots = 60;
        internal static string ReportDir;

        private static bool active;
        private static float endAt = float.MaxValue;
        private static string shotDir;
        private static int shots;
        private static float nextShot;
        private static string lastState;
        private static float start;

        internal static void Apply(Harmony harmony)
        {
            if (string.IsNullOrEmpty(ReportDir))
                ReportDir = Path.Combine(Path.GetDirectoryName(typeof(TransitionWatch).Assembly.Location) ?? ".", "reports");
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(TransitionWatch), nameof(LoadPrefix)));
            harmony.Patch(AccessTools.Method(typeof(SceneManager), "LoadSceneAsync", new[] { typeof(string) }), prefix: prefix);
            harmony.Patch(AccessTools.Method(typeof(SceneManager), "LoadSceneAsync", new[] { typeof(string), typeof(LoadSceneMode) }), prefix: prefix);
            var curtain = new HarmonyMethod(AccessTools.Method(typeof(TransitionWatch), nameof(CurtainPostfix)));
            harmony.Patch(AccessTools.Method(typeof(LoadingCurtain), nameof(LoadingCurtain.ShowUntil)), postfix: curtain);
            harmony.Patch(AccessTools.Method(typeof(LoadingCurtain), nameof(LoadingCurtain.ShowPopupUntil)), postfix: curtain);
            harmony.Patch(AccessTools.Method(typeof(LoadingCurtain), nameof(LoadingCurtain.Hide)), postfix: curtain);
            SceneManager.sceneLoaded += OnSceneLoaded;
            var go = new GameObject("PerfeBT.TransitionWatch");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<TransitionWatchClock>();
        }

        private static void LoadPrefix(string __0)
        {
            if (__0 == "Loading" && !active)
            {
                active = true;
                endAt = float.MaxValue;
                start = Time.realtimeSinceStartup;
                shots = 0;
                nextShot = 0;
                lastState = null;
                shotDir = Path.Combine(Path.Combine(ReportDir, "screens"), DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                Directory.CreateDirectory(shotDir);
                Log.Write($"[screen] transition start; screenshots in {shotDir}");
            }
            if (active)
                Log.Write($"[screen] {Stamp()} LoadSceneAsync('{__0}')");
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!active)
                return;
            Log.Write($"[screen] {Stamp()} sceneLoaded '{scene.name}' {mode}");
            if (mode == LoadSceneMode.Single && scene.name != "Loading" && scene.name != "Empty")
                endAt = Time.realtimeSinceStartup + TailSeconds;
        }

        private static void CurtainPostfix(MethodBase __originalMethod)
        {
            if (!active)
                return;
            var caller = new StackFrame(2, false).GetMethod();
            Log.Write($"[screen] {Stamp()} LoadingCurtain.{__originalMethod.Name} from {caller?.DeclaringType?.FullName}::{caller?.Name}; visible={SafeVisible()}");
        }

        internal static void EndOfFrame()
        {
            if (!active)
                return;
            var now = Time.realtimeSinceStartup;
            var state = State();
            if (state != lastState)
            {
                Log.Write($"[screen] {Stamp()} {state}");
                lastState = state;
            }
            if (shots < MaxShots && now >= nextShot)
            {
                shots++;
                nextShot = now + ShotSeconds;
                var name = $"{now - start:000.0}s_{SceneManager.GetActiveScene().name}.png";
                try { ScreenCapture.CaptureScreenshot(Path.Combine(shotDir, name)); }
                catch (Exception e) { Log.Write("[screen] screenshot failed: " + e.Message); shots = MaxShots; }
            }
            if (now >= endAt)
            {
                active = false;
                Log.Write($"[screen] {Stamp()} transition watch end ({shots} screenshots)");
            }
        }

        private static string Stamp() => $"+{Time.realtimeSinceStartup - start:0.00}s f{Time.frameCount}";

        private static string SafeVisible()
        {
            try { return LoadingCurtain.IsVisible.ToString(); }
            catch (Exception e) { return "?" + e.GetType().Name; }
        }

        private static string State()
        {
            var cams = Camera.allCameras
                .OrderBy(c => c.depth)
                .Select(c => $"{c.name}(d{c.depth},{c.clearFlags},{(c.targetTexture != null ? "rt" : "screen")},{c.gameObject.scene.name})");
            return $"curtain={SafeVisible()} active={SceneManager.GetActiveScene().name} cams=[{string.Join(" ", cams.ToArray())}]";
        }
    }

    internal sealed class TransitionWatchClock : MonoBehaviour
    {
        private IEnumerator Start()
        {
            var wait = new WaitForEndOfFrame();
            while (true)
            {
                yield return wait;
                try { TransitionWatch.EndOfFrame(); }
                catch (Exception e) { Log.Write("[screen] watch failed: " + e.Message); }
            }
        }
    }
}
