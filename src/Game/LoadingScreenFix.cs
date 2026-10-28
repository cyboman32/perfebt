using System;
using System.Linq;
using BattleTech.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PerfeBT
{
    // Black screen between the two loading screens of a level transition. Measured (TransitionWatch, 0.8.3): while
    // the "Loading" scene is loaded the LoadingCurtain is visible, but nothing shows it:
    //   - the Loading scene's "Clear Camera" (depth 100, clears to its black skybox) renders after the UI camera
    //     (depth 1) whenever the UI camera draws to the screen (from the main menu and the after-action screen);
    //   - from the sim game and combat the UI camera draws into the UI render texture, which only the outgoing
    //     scene's camera composited onto the screen, so nothing draws it at all.
    // While the Loading scene is loaded and the curtain is visible, this moves that Clear Camera below the UI camera
    // (depth -100: it still clears the screen first) and, when the UI camera targets a render texture, draws that
    // texture over the screen from OnGUI. Display only; the Loading scene and its camera are destroyed by the target
    // scene's Single load as before.
    internal static class LoadingScreenFix
    {
        private static LoadingScreenFixPainter painter;
        private static bool inTransition;

        internal static void Apply(Harmony harmony)
        {
            // Fails here (disabling the fix) if the curtain API changed.
            _ = LoadingCurtain.IsVisible;
            var prefix = new HarmonyMethod(typeof(LoadingScreenFix), nameof(LoadPrefix));
            harmony.Patch(AccessTools.Method(typeof(SceneManager), nameof(SceneManager.LoadSceneAsync), new[] { typeof(string) }), prefix: prefix);
            harmony.Patch(AccessTools.Method(typeof(SceneManager), nameof(SceneManager.LoadSceneAsync), new[] { typeof(string), typeof(LoadSceneMode) }), prefix: prefix);
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            var go = new GameObject("PerfeBT.LoadingScreenFix");
            UnityEngine.Object.DontDestroyOnLoad(go);
            painter = go.AddComponent<LoadingScreenFixPainter>();
            painter.enabled = false;
        }

        private static void LoadPrefix(string __0)
        {
            if (__0 == "Loading")
                inTransition = true;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!inTransition)
                return;
            if (scene.name == "Loading")
            {
                foreach (var cam in Camera.allCameras.Where(c => c.name == "Clear Camera" && c.gameObject.scene == scene && c.depth > -100))
                {
                    cam.depth = -100;
                    Log.Write("[loadscreen] Loading scene clear camera moved below the UI");
                }
                painter.enabled = true;
            }
            else if (scene.name != "Empty")
                Stop(); // interstitial (mission briefing) or target scene: their cameras take over
        }

        private static void OnSceneUnloaded(Scene scene)
        {
            if (scene.name == "Loading")
                Stop();
        }

        private static void Stop()
        {
            inTransition = false;
            if (painter != null)
                painter.enabled = false;
        }

        // The UI camera's render texture when the curtain is up and nothing else composites it, else null.
        internal static RenderTexture TextureToDraw()
        {
            if (!LoadingCurtain.IsVisible)
                return null;
            var ui = Camera.allCameras.FirstOrDefault(c => c.name == "UICam");
            return ui != null ? ui.targetTexture : null;
        }
    }

    internal sealed class LoadingScreenFixPainter : MonoBehaviour
    {
        private bool failed;

        private void OnGUI()
        {
            if (failed || Event.current.type != EventType.Repaint)
                return;
            try
            {
                var rt = LoadingScreenFix.TextureToDraw();
                if (rt != null)
                    GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), rt, ScaleMode.StretchToFill, false);
            }
            catch (Exception e)
            {
                failed = true;
                Log.Write("[loadscreen] draw failed, disabled: " + e.Message);
            }
        }
    }
}
