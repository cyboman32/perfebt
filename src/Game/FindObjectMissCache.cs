using System;
using System.Collections.Generic;
using System.Threading;
using BattleTech;
using BattleTech.Rendering;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace PerfeBT
{
    // CameraControl.Instance and BTCustomRenderer.btSunlight cache what Object.FindObjectOfType<T>() returns, but a
    // miss is not cached: while no such object exists, every access searches the whole scene again (sorting every
    // loaded MonoBehaviour by instance ID, ~19 ms with RogueTech's ~170k behaviours). After a mission the combat
    // camera is gone with the map, and the audio engine asks CameraControl.HasInstance every frame: 50-80% of the
    // main thread on the after-action and salvage screens, plus 3-5 s of loading screens.
    //
    // After a search for one of these types found nothing, further searches return null without searching until an
    // object of that type can have appeared: any scene load, BTSunlight.OnEnable (called for every sunlight that
    // becomes active), and CameraControl.Create (the game's only way to make the combat camera). Create looks up
    // the camera, instantiates it when missing and looks it up again, so searches always run while it executes.
    // Every VerifyEvery-th skipped search runs the real one; if that finds an object, the cache is switched off for
    // the session.
    internal static class FindObjectMissCache
    {
        private static readonly HashSet<Type> types = new HashSet<Type> { typeof(CameraControl), typeof(BTSunlight) };
        private static readonly HashSet<Type> missing = new HashSet<Type>();
        private static readonly Dictionary<Type, long> skipped = new Dictionary<Type, long>();
        private static int mainThreadId, verifyEvery;
        private static long verified;
        private static bool disabled;
        private static int inCreate;

        internal static void Apply(Harmony harmony, int verifyEveryN)
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            verifyEvery = verifyEveryN;
            var find = AccessTools.Method(typeof(UnityEngine.Object), nameof(UnityEngine.Object.FindObjectOfType), new[] { typeof(Type) })
                       ?? throw new MissingMethodException("UnityEngine.Object.FindObjectOfType(Type)");
            var create = AccessTools.Method(typeof(CameraControl), nameof(CameraControl.Create))
                         ?? throw new MissingMethodException("CameraControl.Create");
            var sunEnable = AccessTools.Method(typeof(BTSunlight), "OnEnable")
                            ?? throw new MissingMethodException("BTSunlight.OnEnable");
            harmony.Patch(create,
                prefix: new HarmonyMethod(typeof(FindObjectMissCache), nameof(CreateBegin)),
                finalizer: new HarmonyMethod(typeof(FindObjectMissCache), nameof(CreateEnd)));
            harmony.Patch(sunEnable, prefix: new HarmonyMethod(typeof(FindObjectMissCache), nameof(SunlightEnabled)));
            harmony.Patch(find,
                prefix: new HarmonyMethod(typeof(FindObjectMissCache), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(FindObjectMissCache), nameof(Postfix)));
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                missing.Clear();
                LogStats("scene " + scene.name);
            };
        }

        private static void CreateBegin()
        {
            inCreate++;
            missing.Remove(typeof(CameraControl));
        }

        private static void CreateEnd()
        {
            inCreate--;
            missing.Remove(typeof(CameraControl));
        }

        private static void SunlightEnabled() => missing.Remove(typeof(BTSunlight));

        private static bool Prefix(Type __0, ref UnityEngine.Object __result, out bool __state)
        {
            __state = false;
            if (disabled || inCreate > 0 || __0 == null || !missing.Contains(__0) || Thread.CurrentThread.ManagedThreadId != mainThreadId)
                return true;
            skipped.TryGetValue(__0, out long n);
            skipped[__0] = ++n;
            if (verifyEvery > 0 && n % verifyEvery == 0)
            {
                __state = true;   // run the real search and compare in the postfix
                return true;
            }
            __result = null;
            return false;
        }

        private static void Postfix(Type __0, UnityEngine.Object __result, bool __state)
        {
            if (disabled || __0 == null || !types.Contains(__0) || Thread.CurrentThread.ManagedThreadId != mainThreadId)
                return;
            if (__result == null)
            {
                missing.Add(__0);
                if (__state)
                    verified++;
                return;
            }
            missing.Remove(__0);
            if (__state)
            {
                disabled = true;
                missing.Clear();
                Log.Write($"[misscache] verification found a {__0.FullName} while cached as missing; switched off for this session");
            }
        }

        private static void LogStats(string when)
        {
            if (skipped.Count == 0)
                return;
            var parts = new List<string>();
            foreach (var kv in skipped)
                parts.Add($"{kv.Key.Name} {kv.Value}");
            Log.Write($"[misscache] {when}: searches skipped {string.Join(", ", parts)}; {verified} verified{(disabled ? "; OFF" : "")}");
        }
    }
}
