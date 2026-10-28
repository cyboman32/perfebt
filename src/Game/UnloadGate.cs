using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using BattleTech;
using BattleTech.Data;
using BattleTech.Portraits;
using BattleTech.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PerfeBT
{
    // Unity's "Unloading N unused Assets" costs 2-4 s each with a multi-GB managed heap (MarkObjects walks the heap).
    // A level transition ran up to six: DataManager.Clear's explicit call, the Single load of 'Loading', the Single
    // load of 'Empty', LevelLoader's explicit call after it, the Single load of the target and LevelLoader's explicit
    // call after that. Each one right after another frees almost nothing.
    //
    // Every explicit Resources.UnloadUnusedAssets() call in the game, plus sites other code registers with AddSite
    // before Apply (CustomMapsUnloadSites; verified offline: no other mod calls it), is transpiled into
    // UnloadGate.Request("<site>"). Every site either pops the result or yields it from a
    // coroutine (`yield return null` = wait one frame), so Request may return null:
    //  - UnloadSkipSites: skipped when an unload (explicit, or a Single-mode scene load observed via sceneLoaded) ran
    //    within the last UnloadCoalesceFrames frames.
    //  - UnloadDeferSites: the unload runs UnloadDeferFrames frames later instead, and is dropped if a Single-mode
    //    scene load is requested (or already in flight) before then, or any other unload ran in between; that unload
    //    covers it. Meant for DataManager.Clear (void; pops the result), which runs right before
    //    LevelLoader.LoadScene's Single load of 'Loading' (SimGameState._OnBeginAttachUX: Clear, then LoadScene).
    // Both are memory-timing-only: unreferenced assets stay loaded until the next unload instead of being released
    // now. Game state is unaffected (only Resources.FindObjectsOfTypeAll could observe the difference).
    //
    // EmptySceneAdditive: LevelLoader.Load and replacements registered with loadsEmpty (CustomMaps' LoadCustom) load 'Empty' with
    // LoadSceneAsync("Empty") (Single) right after 'Loading' was loaded Single. That unloads 'Loading' (its only
    // content: the 'Clear Camera' tagged ClearCamera, depth 100, black clear; the LevelLoader object, already moved to
    // DontDestroyOnLoad in LevelLoader.Start; a SystemSceneTag nobody queries) and runs one more full unused-asset
    // unload. Loaded Additive instead, 'Loading' and its clear camera stay until the coroutine's own
    // CleanupClearCameras (after the interstitial loads) or the target Single load, which unloads Loading, Empty and
    // the interstitial together. Everything downstream is unchanged: SetActiveScene(Empty) finds Empty loaded, the
    // "Unloading Empty"/interstitial/'Loading' UnloadSceneAsync branches run after the target Single load as before
    // (nothing left to unload). Other 'Empty' loads (LoadTransitioning/SceneTransitioner, loadTarget == "Empty") do
    // not take this branch and are untouched. Exception: when LevelLoader.activeScene is "Empty" (the previous load
    // targeted Empty, e.g. loading a save from the Argo), the coroutine next unloads activeScene. With Single, Empty is
    // the last loaded scene, Unity refuses that unload and SetActiveScene(Empty) succeeds; with Additive the unload
    // succeeds, SetActiveScene throws and the coroutine dies (endless loading screen). That case keeps Single.
    //
    // Diagnostics ([unload] lines, UnloadDiagnostics): requests, completion of each explicit op, every SceneManager
    // load/unload call (outermost public overload only) and its completion, sceneLoaded/sceneUnloaded, and frames
    // > 1000 ms within 10 frames after one of these events (attributes Single-load auto-unloads). Skip, defer, drop
    // and deferred-run lines are written regardless.
    internal static class UnloadGate
    {
        private const double LongFrameMs = 1000;
        private const int AdjacentFrames = 10;
        private const int NoFrame = -1000000;              // "never"; frame - NoFrame stays positive without overflow
        private const float SingleLoadInFlightMaxSeconds = 120;   // stale guard if a Single load never reports back

        private static bool diagnostics = true;
        private static HashSet<string> skipSites = new HashSet<string>(StringComparer.Ordinal);
        private static int coalesceFrames = 30;
        private static HashSet<string> deferSites = new HashSet<string>(StringComparer.Ordinal);
        private static int deferFrames = 3;
        private static bool emptySceneAdditive;

        // Main-thread state.
        private static int lastUnloadFrame = NoFrame;      // last explicit unload or Single-mode sceneLoaded
        private static string lastUnloadSource = "none";
        private static int lastEventFrame = NoFrame;       // last diagnostic event, for long-frame attribution
        private static string lastEvent = "none";
        private static string singleLoadInFlight;          // Single-mode load requested, sceneLoaded not seen yet
        private static float singleLoadRequestedAt;
        private static string deferredSite;                // pending deferred unload
        private static int deferredRequestFrame, deferredDueFrame;
        private static int sceneCallDepth;

        private static MethodInfo unloadUnusedAssets, loadSceneAsyncName, loadSceneAsyncNameMode;
        private static readonly Dictionary<MethodBase, Site> sites = new Dictionary<MethodBase, Site>();

        private sealed class Site
        {
            internal readonly string Id;
            internal readonly int Calls;
            internal Site(string id, int calls) { Id = id; Calls = calls; }
        }

        private static readonly List<KeyValuePair<MethodBase, Site>> extraTargets = new List<KeyValuePair<MethodBase, Site>>();
        private static readonly List<MethodBase> extraEmptyTargets = new List<MethodBase>();

        // Registers another UnloadUnusedAssets call site (a mod's replacement of a game coroutine). Call before Apply.
        // loadsEmpty: the method also contains LevelLoader.Load's LoadSceneAsync("Empty") for EmptySceneAdditive.
        internal static void AddSite(MethodBase method, string id, int calls, bool loadsEmpty)
        {
            extraTargets.Add(Target(method, id, calls));
            if (loadsEmpty)
                extraEmptyTargets.Add(method);
        }

        internal static void Configure(bool unloadDiagnostics, IEnumerable<string> unloadSkipSites, int unloadCoalesceFrames,
            IEnumerable<string> unloadDeferSites, int unloadDeferFrames, bool emptyAdditive)
        {
            diagnostics = unloadDiagnostics;
            skipSites = new HashSet<string>(unloadSkipSites ?? Array.Empty<string>(), StringComparer.Ordinal);
            coalesceFrames = unloadCoalesceFrames;
            deferSites = new HashSet<string>(unloadDeferSites ?? Array.Empty<string>(), StringComparer.Ordinal);
            deferFrames = Math.Max(1, unloadDeferFrames);
            emptySceneAdditive = emptyAdditive;
        }

        private static T Req<T>(T x, string what) where T : class
            => x ?? throw new MissingMemberException("PerfeBT.UnloadGate: missing " + what);

        internal static void Apply(Harmony harmony)
        {
            unloadUnusedAssets = Req(AccessTools.Method(typeof(Resources), nameof(Resources.UnloadUnusedAssets), Type.EmptyTypes),
                "Resources.UnloadUnusedAssets()");
            loadSceneAsyncName = Req(AccessTools.Method(typeof(SceneManager), nameof(SceneManager.LoadSceneAsync), new[] { typeof(string) }),
                "SceneManager.LoadSceneAsync(string)");
            loadSceneAsyncNameMode = Req(AccessTools.Method(typeof(SceneManager), nameof(SceneManager.LoadSceneAsync), new[] { typeof(string), typeof(LoadSceneMode) }),
                "SceneManager.LoadSceneAsync(string, LoadSceneMode)");

            // Resolve every target before patching anything, so a missing game target patches nothing.
            var levelLoaderMoveNext = Req(AccessTools.EnumeratorMoveNext(Req(AccessTools.Method(typeof(LevelLoader), nameof(LevelLoader.Load), Type.EmptyTypes),
                "LevelLoader.Load()")), "LevelLoader.Load MoveNext");
            var targets = new List<KeyValuePair<MethodBase, Site>>
            {
                Target(levelLoaderMoveNext, "LevelLoader.Load", 2),
                Target(AccessTools.Method(typeof(DataManager), nameof(DataManager.Clear), new[] { typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool) }),
                    "DataManager.Clear", 1),
                Target(AccessTools.Method(typeof(PortraitManager), nameof(PortraitManager.ClearLargePortraits), Type.EmptyTypes),
                    "PortraitManager.ClearLargePortraits", 1),
                Target(AccessTools.Method(typeof(SGRoomController_CptQuarters), nameof(SGRoomController_CptQuarters.OnHeraldryClosed), new[] { typeof(HeraldryDef) }),
                    "CptQuarters.OnHeraldryClosed", 1),
                Target(AccessTools.Method(typeof(StaticsFinder), nameof(StaticsFinder.Update), Type.EmptyTypes),
                    "StaticsFinder.Update", 2),
            };
            var emptyTargets = new List<MethodBase> { levelLoaderMoveNext };

            targets.AddRange(extraTargets);
            emptyTargets.AddRange(extraEmptyTargets);

            var transpiler = new HarmonyMethod(typeof(UnloadGate), nameof(Transpiler));
            foreach (var t in targets)
            {
                sites[t.Key] = t.Value;
                harmony.Patch(t.Key, transpiler: transpiler);
            }

            if (emptySceneAdditive)
            {
                var emptyTranspiler = new HarmonyMethod(typeof(UnloadGate), nameof(EmptyAdditiveTranspiler));
                foreach (var m in emptyTargets)
                    harmony.Patch(m, transpiler: emptyTranspiler);
                Log.Write($"UnloadGate: 'Empty' loaded Additive in {emptyTargets.Count} level loader coroutine(s)");
            }

            // Single-mode sceneLoaded counts as an unload for the coalesce/defer policies.
            SceneManager.sceneLoaded += OnSceneLoaded;
            if (diagnostics)
                SceneManager.sceneUnloaded += OnSceneUnloaded;
            // SceneManager calls: logged with diagnostics, and Single-load requests cancel deferred unloads.
            if (diagnostics || deferSites.Count != 0)
            {
                PatchSceneManager(harmony);
                UnloadGateFrameWatch.Create();
            }

            Log.Write($"UnloadGate: {targets.Count} UnloadUnusedAssets call sites gated; diagnostics {(diagnostics ? "on" : "off")}; " +
                      $"skip [{Join(skipSites)}] when an unload ran within {coalesceFrames} frames; " +
                      $"defer [{Join(deferSites)}] by {deferFrames} frames; EmptySceneAdditive {emptySceneAdditive}");
        }

        private static string Join(HashSet<string> set) => string.Join(", ", new List<string>(set).ToArray());

        private static KeyValuePair<MethodBase, Site> Target(MethodBase method, string id, int calls)
            => new KeyValuePair<MethodBase, Site>(Req(method, id), new Site(id, calls));

        // Replaces `call Resources::UnloadUnusedAssets()` with `ldstr <site>; call UnloadGate::Request(string)`.
        // The original call instruction is rewritten in place to keep its labels and exception blocks (DataManager.Clear
        // branches to it). Stack shape is identical: () -> AsyncOperation.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var site = sites[__originalMethod];
            var request = AccessTools.Method(typeof(UnloadGate), nameof(Request));
            var result = new List<CodeInstruction>();
            var found = 0;
            foreach (var ins in instructions)
            {
                if ((ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt) && ins.operand is MethodInfo m && m == unloadUnusedAssets)
                {
                    found++;
                    ins.opcode = OpCodes.Ldstr;
                    ins.operand = site.Calls == 1 ? site.Id : site.Id + "#" + found;
                    result.Add(ins);
                    result.Add(new CodeInstruction(OpCodes.Call, request));
                }
                else
                    result.Add(ins);
            }
            if (found != site.Calls)
                throw new InvalidOperationException($"PerfeBT.UnloadGate: {site.Id} has {found} UnloadUnusedAssets calls, expected {site.Calls}");
            return result;
        }

        // Replaces the single `ldstr "Empty"; call SceneManager::LoadSceneAsync(string)` (Single) with
        // `ldstr "Empty"; ldc.i4.1; call SceneManager::LoadSceneAsync(string, LoadSceneMode)` (Additive).
        // The call is rewritten in place (it is not a branch target; its labels, if any, stay on it).
        private static IEnumerable<CodeInstruction> EmptyAdditiveTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var result = new List<CodeInstruction>();
            var found = 0;
            foreach (var ins in instructions)
            {
                if (ins.opcode == OpCodes.Call && ins.operand is MethodInfo m && m == loadSceneAsyncName)
                {
                    var prev = result.Count != 0 ? result[result.Count - 1] : null;
                    if (prev == null || prev.opcode != OpCodes.Ldstr || !"Empty".Equals(prev.operand))
                        throw new InvalidOperationException($"PerfeBT.UnloadGate: {__originalMethod.DeclaringType?.FullName} loads a scene other than 'Empty' with LoadSceneAsync(string)");
                    found++;
                    ins.opcode = OpCodes.Call;
                    ins.operand = AccessTools.Method(typeof(UnloadGate), nameof(EmptyLoadMode));
                    result.Add(ins);
                    result.Add(new CodeInstruction(OpCodes.Call, loadSceneAsyncNameMode));
                }
                else
                    result.Add(ins);
            }
            if (found != 1)
                throw new InvalidOperationException($"PerfeBT.UnloadGate: {__originalMethod.DeclaringType?.FullName} has {found} LoadSceneAsync(\"Empty\") calls, expected 1");
            return result;
        }

        public static LoadSceneMode EmptyLoadMode()
        {
            if (LevelLoader.activeScene != "Empty")
                return LoadSceneMode.Additive;
            Log.Write("[unload] 'Empty' loaded Single: LevelLoader.activeScene is 'Empty'");
            return LoadSceneMode.Single;
        }

        // Called from the transpiled sites on the main thread.
        public static AsyncOperation Request(string site)
        {
            var frame = Time.frameCount;
            if (skipSites.Count != 0 && skipSites.Contains(site) && frame - lastUnloadFrame <= coalesceFrames)
            {
                Log.Write($"[unload] skipped {site} frame {frame} t={Time.realtimeSinceStartup:0.000}s scene={ActiveScene()} " +
                          $"(last unload {lastUnloadSource} at frame {lastUnloadFrame}, {frame - lastUnloadFrame} frames ago)");
                Event("skipped " + site);
                return null;
            }

            if (deferSites.Count != 0 && deferSites.Contains(site))
            {
                var inFlight = SingleLoadInFlight();
                if (inFlight != null)
                    Log.Write($"[unload] dropped {site} frame {frame}, covered by Single load {inFlight} (in flight)");
                else if (deferredSite != null)
                    Log.Write($"[unload] dropped {site} frame {frame}, merged into deferred {deferredSite} (due frame {deferredDueFrame})");
                else
                {
                    deferredSite = site;
                    deferredRequestFrame = frame;
                    deferredDueFrame = frame + deferFrames;
                    Log.Write($"[unload] deferred {site} frame {frame} to frame {deferredDueFrame} scene={ActiveScene()}");
                }
                Event("deferred " + site);
                return null;
            }

            return Run(site, frame);
        }

        private static AsyncOperation Run(string site, int frame)
        {
            lastUnloadFrame = frame;
            lastUnloadSource = site;
            if (!diagnostics) return Resources.UnloadUnusedAssets();

            Log.Write($"[unload] request {site} frame {frame} t={Time.realtimeSinceStartup:0.000}s scene={ActiveScene()} " +
                      $"heap={GC.GetTotalMemory(false) >> 20} MB");
            Event("request " + site);
            var start = Stopwatch.GetTimestamp();
            var op = Resources.UnloadUnusedAssets();
            if (op != null)
            {
                op.completed += _ =>
                {
                    var done = Time.frameCount;
                    lastUnloadFrame = done;
                    lastUnloadSource = site;
                    Log.Write($"[unload] done {site} frame {done} +{Ms(start):0} ms ({done - frame} frames) heap={GC.GetTotalMemory(false) >> 20} MB");
                    Event("done " + site);
                };
            }
            return op;
        }

        private static string SingleLoadInFlight()
        {
            if (singleLoadInFlight != null && Time.realtimeSinceStartup - singleLoadRequestedAt > SingleLoadInFlightMaxSeconds)
                singleLoadInFlight = null;
            return singleLoadInFlight;
        }

        private static void OnSingleLoadRequested(string scene)
        {
            singleLoadInFlight = scene;
            singleLoadRequestedAt = Time.realtimeSinceStartup;
            if (deferredSite == null) return;
            Log.Write($"[unload] dropped {deferredSite}, covered by Single load {scene} (requested frame {Time.frameCount}, deferred at frame {deferredRequestFrame})");
            deferredSite = null;
        }

        private static void RunDeferred(int frame)
        {
            var site = deferredSite;
            deferredSite = null;
            if (lastUnloadFrame >= deferredRequestFrame)
            {
                Log.Write($"[unload] dropped {site} frame {frame}, covered by {lastUnloadSource} at frame {lastUnloadFrame}");
                return;
            }
            Log.Write($"[unload] deferred {site} runs at frame {frame} (requested frame {deferredRequestFrame}) scene={ActiveScene()}");
            Run(site, frame);
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                var frame = Time.frameCount;
                if (mode == LoadSceneMode.Single)
                {
                    lastUnloadFrame = frame;
                    lastUnloadSource = "Single load " + scene.name;
                    singleLoadInFlight = null;
                }
                if (!diagnostics) return;
                Log.Write($"[unload] sceneLoaded '{scene.name}' {mode} frame {frame} t={Time.realtimeSinceStartup:0.000}s");
                Event($"sceneLoaded {scene.name} {mode}");
            }
            catch (Exception e)
            {
                Log.Write("[unload] sceneLoaded handler failed: " + e);
            }
        }

        private static void OnSceneUnloaded(Scene scene)
        {
            try
            {
                Log.Write($"[unload] sceneUnloaded '{scene.name}' frame {Time.frameCount} t={Time.realtimeSinceStartup:0.000}s");
                Event("sceneUnloaded " + scene.name);
            }
            catch (Exception e)
            {
                Log.Write("[unload] sceneUnloaded handler failed: " + e);
            }
        }

        // Every public SceneManager.LoadScene/LoadSceneAsync/UnloadSceneAsync/UnloadScene overload. Overloads delegate
        // to each other, so only the outermost call on the stack is handled.
        private static void PatchSceneManager(Harmony harmony)
        {
            var prefix = new HarmonyMethod(typeof(UnloadGate), nameof(ScenePrefix));
            var asyncPostfix = new HarmonyMethod(typeof(UnloadGate), nameof(SceneAsyncPostfix));
            var finalizer = new HarmonyMethod(typeof(UnloadGate), nameof(SceneFinalizer));
            var names = new HashSet<string>
            {
                nameof(SceneManager.LoadScene), nameof(SceneManager.LoadSceneAsync), nameof(SceneManager.UnloadSceneAsync), nameof(SceneManager.UnloadScene)
            };

            // Overload the game calls besides the two resolved in Apply; its absence means a different Unity version.
            Req(AccessTools.Method(typeof(SceneManager), nameof(SceneManager.UnloadSceneAsync), new[] { typeof(string) }), "SceneManager.UnloadSceneAsync(string)");

            var count = 0;
            foreach (var m in typeof(SceneManager).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (!names.Contains(m.Name) || m.IsGenericMethod) continue;
                harmony.Patch(m, prefix: prefix, finalizer: finalizer,
                    postfix: m.ReturnType == typeof(AsyncOperation) ? asyncPostfix : null);
                count++;
            }
            Log.Write($"UnloadGate: {count} SceneManager load/unload overloads hooked");
        }

        // Single-mode load request -> scene name; otherwise null.
        private static string SingleLoadTarget(MethodBase method, object[] args)
        {
            if (!method.Name.StartsWith("LoadScene", StringComparison.Ordinal) || args.Length == 0) return null;
            var single = args.Length == 1
                         || (args[1] is LoadSceneMode mode && mode == LoadSceneMode.Single)
                         || (args[1] is LoadSceneParameters p && p.loadSceneMode == LoadSceneMode.Single);
            if (!single) return null;
            return args[0] is string s ? s : "#" + args[0];
        }

        private static void ScenePrefix(MethodBase __originalMethod, object[] __args)
        {
            if (sceneCallDepth++ != 0) return;
            try
            {
                var single = SingleLoadTarget(__originalMethod, __args);
                if (single != null) OnSingleLoadRequested(single);
                if (!diagnostics) return;
                var frame = Time.frameCount;
                var desc = SceneCall(__originalMethod, __args);
                Log.Write($"[unload] {desc} frame {frame} t={Time.realtimeSinceStartup:0.000}s scene={ActiveScene()}");
                Event(desc);
            }
            catch (Exception e)
            {
                Log.Write("[unload] scene call hook failed: " + e);
            }
        }

        private static void SceneAsyncPostfix(MethodBase __originalMethod, object[] __args, AsyncOperation __result)
        {
            if (sceneCallDepth != 1 || __result == null) return;
            try
            {
                var single = SingleLoadTarget(__originalMethod, __args);
                if (single == null && !diagnostics) return;
                var desc = diagnostics ? SceneCall(__originalMethod, __args) : null;
                var frame = Time.frameCount;
                var start = Stopwatch.GetTimestamp();
                __result.completed += _ =>
                {
                    if (single != null && singleLoadInFlight == single) singleLoadInFlight = null;
                    if (desc == null) return;
                    var done = Time.frameCount;
                    Log.Write($"[unload] done {desc} frame {done} +{Ms(start):0} ms ({done - frame} frames)");
                    Event("done " + desc);
                };
            }
            catch (Exception e)
            {
                Log.Write("[unload] scene op hook failed: " + e);
            }
        }

        private static void SceneFinalizer() => sceneCallDepth--;

        private static string SceneCall(MethodBase method, object[] args)
        {
            var parts = new List<string>(args.Length);
            foreach (var a in args)
            {
                switch (a)
                {
                    case string s: parts.Add("'" + s + "'"); break;
                    case Scene sc: parts.Add("'" + sc.name + "'"); break;
                    case LoadSceneParameters p: parts.Add(p.loadSceneMode.ToString()); break;
                    default: parts.Add(a?.ToString() ?? "null"); break;
                }
            }
            return $"{method.Name}({string.Join(", ", parts.ToArray())})";
        }

        private static string ActiveScene()
        {
            try { return SceneManager.GetActiveScene().name; }
            catch { return "?"; }
        }

        private static double Ms(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

        private static void Event(string what)
        {
            lastEvent = what;
            lastEventFrame = Time.frameCount;
        }

        // Called once per frame at end of frame by UnloadGateFrameWatch.
        internal static void EndFrame(int frame, double ms, int gcs)
        {
            if (deferredSite != null && frame >= deferredDueFrame)
                RunDeferred(frame);
            if (!diagnostics || ms <= LongFrameMs) return;
            var after = frame - lastEventFrame;
            if (after < 0 || after > AdjacentFrames) return;
            Log.Write($"[unload] frame {frame} took {ms:0} ms after {lastEvent} ({after} frames later, {gcs} GCs)");
        }
    }

    // End-of-frame clock for UnloadGate: runs due deferred unloads and attributes long frames.
    internal sealed class UnloadGateFrameWatch : MonoBehaviour
    {
        internal static void Create()
        {
            var go = new GameObject("PerfeBT.UnloadGate");
            DontDestroyOnLoad(go);
            go.AddComponent<UnloadGateFrameWatch>();
        }

        private IEnumerator Start()
        {
            var wait = new WaitForEndOfFrame();
            var last = Stopwatch.GetTimestamp();
            var gcs = GC.CollectionCount(0);
            while (true)
            {
                yield return wait;
                var now = Stopwatch.GetTimestamp();
                var nowGcs = GC.CollectionCount(0);
                try
                {
                    UnloadGate.EndFrame(Time.frameCount, (now - last) * 1000.0 / Stopwatch.Frequency, nowGcs - gcs);
                }
                catch (Exception e)
                {
                    Log.Write("[unload] frame watch failed: " + e);
                }
                last = now;
                gcs = nowGcs;
            }
        }
    }
}
