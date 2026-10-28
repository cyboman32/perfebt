using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;

namespace PerfeBT
{
    // Diagnostic: salvage list refreshes after long missions spend 1-11 ms per item in
    // InventoryItemElement_NotListView.ClearEverything (3-3.5 s hitches on "disassemble all"). A native sampler put
    // the time in GameObject.SetActive(false) -> MonoBehaviour::RemoveFromManager -> DelayedCallManager::CancelCallDelayed,
    // which walks Unity's whole set of delayed calls (every suspended coroutine and pending Invoke in the game) once per
    // disabled behaviour. This census lists what that set holds: total, then groups by behaviour type and coroutine
    // (enumerator type) or callback, and by GameObject root ([delayed] lines). Read-only; main thread only.
    //
    // DelayedCallManager is not exported from UnityPlayer.dll; the addresses are for this exact build (UnityPlayerBuild
    // checks it and the bytes of the functions they were read from, otherwise nothing is touched).
    // Triggers: AAR_SalvageScreen.CalculateAndAddAvailableSalvage (salvage screen opening, combat still loaded) and
    // Ctrl+Shift+D (with Profile on).
    internal static class DelayedCallCensus
    {
        private const string Module = UnityPlayerBuild.Module;
        private const int RvaCancelCallDelayed = 0x6d6b50;
        private const int RvaContinueCoroutine = 0x8dfb20;
        private static readonly byte[] CancelBytes = { 0x40, 0x56, 0x57, 0x41, 0x54, 0x41, 0x56, 0x41, 0x57, 0x48, 0x83, 0xec, 0x20, 0x48, 0x8b, 0x41 };
        private static readonly byte[] ContinueBytes = { 0x4c, 0x8b, 0xdc, 0x48, 0x83, 0xec, 0x78, 0x48, 0x8b, 0xc2, 0x48, 0x39, 0x4a, 0x40, 0x74, 0x52 };

        private const int ManagerHead = UnityPlayerBuild.ManagerHead, ManagerSize = UnityPlayerBuild.ManagerSize;
        private const int NodeLeft = UnityPlayerBuild.NodeLeft, NodeParent = UnityPlayerBuild.NodeParent;
        private const int NodeRight = UnityPlayerBuild.NodeRight, NodeIsNil = UnityPlayerBuild.NodeIsNil;
        private const int NodeUserData = UnityPlayerBuild.NodeUserData, NodeCallback = UnityPlayerBuild.NodeCallback;
        private const int NodeObjectId = UnityPlayerBuild.NodeObjectId;
        // Coroutine (userData of ContinueCoroutine): enumerator GC handle +0x10, or the object at +0x20 when +0x18 == 2;
        // behaviour +0x40 (Coroutine::Run).
        private const int CoHandle = 0x10, CoKind = 0x18, CoObject = 0x20;

        private const int TopGroups = 40, TopRoots = 15;
        private static IntPtr baseAddress;

        private const string Mono = "mono-2.0-bdwgc";
        [DllImport(Mono, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mono_gchandle_get_target(uint handle);
        [DllImport(Mono, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mono_object_get_class(IntPtr obj);
        [DllImport(Mono, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mono_class_get_name(IntPtr klass);
        [DllImport(Mono, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mono_class_get_nesting_type(IntPtr klass);

        internal static void Apply(Harmony harmony)
        {
            var b = UnityPlayerBuild.Base();
            UnityPlayerBuild.Check(b, RvaCancelCallDelayed, CancelBytes, "DelayedCallManager::CancelCallDelayed");
            UnityPlayerBuild.Check(b, RvaContinueCoroutine, ContinueBytes, "Coroutine::ContinueCoroutine");
            baseAddress = b;

            var target = AccessTools.Method(typeof(BattleTech.UI.AAR_SalvageScreen), nameof(BattleTech.UI.AAR_SalvageScreen.CalculateAndAddAvailableSalvage))
                         ?? throw new MissingMethodException("AAR_SalvageScreen.CalculateAndAddAvailableSalvage");
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(DelayedCallCensus), nameof(SalvagePrefix)) { priority = Priority.First });
        }

        private static void SalvagePrefix() => Run("salvage screen");

        internal static void Run(string reason)
        {
            if (baseAddress == IntPtr.Zero)
                return;
            try
            {
                Census(reason);
            }
            catch (Exception e)
            {
                Log.Write("[delayed] census failed: " + e);
            }
        }

        private struct Entry
        {
            internal int ObjectId;
            internal long CallbackRva;
            internal IntPtr UserData;
        }

        private static void Census(string reason)
        {
            var sw = Stopwatch.StartNew();
            var manager = UnityPlayerBuild.DelayedCallManager(baseAddress);
            if (manager == IntPtr.Zero)
            {
                Log.Write("[delayed] no DelayedCallManager");
                return;
            }
            var head = Marshal.ReadIntPtr(manager, ManagerHead);
            long count = Marshal.ReadInt64(manager, ManagerSize);
            long baseLong = baseAddress.ToInt64();

            // Depth-first over the red-black tree from the root (head->parent); nil nodes end a branch.
            var entries = new List<Entry>((int)Math.Min(count, 1 << 20));
            var stack = new Stack<IntPtr>();
            var root = Marshal.ReadIntPtr(head, NodeParent);
            if (Marshal.ReadByte(root, NodeIsNil) == 0)
                stack.Push(root);
            long limit = count + 16;
            while (stack.Count > 0 && entries.Count <= limit)
            {
                var n = stack.Pop();
                entries.Add(new Entry
                {
                    ObjectId = Marshal.ReadInt32(n, NodeObjectId),
                    CallbackRva = Marshal.ReadIntPtr(n, NodeCallback).ToInt64() - baseLong,
                    UserData = Marshal.ReadIntPtr(n, NodeUserData),
                });
                var l = Marshal.ReadIntPtr(n, NodeLeft);
                var r = Marshal.ReadIntPtr(n, NodeRight);
                if (Marshal.ReadByte(l, NodeIsNil) == 0) stack.Push(l);
                if (Marshal.ReadByte(r, NodeIsNil) == 0) stack.Push(r);
            }
            long walkMs = sw.ElapsedMilliseconds;

            // Instance ID -> behaviour type and GameObject.
            var byId = new Dictionary<int, MonoBehaviour>();
            foreach (var mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
                if (mb != null)
                    byId[mb.GetInstanceID()] = mb;

            var groups = new Dictionary<string, int>();
            var roots = new Dictionary<string, int>();
            int coroutines = 0, unknownObjects = 0;
            foreach (var e in entries)
            {
                byId.TryGetValue(e.ObjectId, out var mb);
                string owner = mb != null ? mb.GetType().FullName : "id " + e.ObjectId + " (not a loaded MonoBehaviour)";
                if (mb == null)
                    unknownObjects++;
                string what;
                if (e.CallbackRva == RvaContinueCoroutine)
                {
                    coroutines++;
                    what = "coroutine " + EnumeratorName(e.UserData);
                }
                else
                    what = $"callback {Module}+0x{e.CallbackRva:x}";
                Add(groups, owner + " | " + what);
                if (mb != null)
                {
                    var t = mb.transform.root;
                    Add(roots, $"{t.name} (scene {t.gameObject.scene.name}, active {mb.isActiveAndEnabled})");
                }
            }

            Log.Write($"[delayed] census ({reason}): {count} delayed calls (walked {entries.Count}), {coroutines} coroutines, " +
                      $"{groups.Count} groups, {unknownObjects} without a loaded MonoBehaviour; {byId.Count} behaviours; " +
                      $"walk {walkMs} ms, total {sw.ElapsedMilliseconds} ms");
            foreach (var g in groups.OrderByDescending(kv => kv.Value).Take(TopGroups))
                Log.Write($"[delayed]   {g.Value,7}  {g.Key}");
            Log.Write("[delayed] by GameObject root:");
            foreach (var g in roots.OrderByDescending(kv => kv.Value).Take(TopRoots))
                Log.Write($"[delayed]   {g.Value,7}  {g.Key}");
        }

        private static void Add(Dictionary<string, int> d, string key)
        {
            d.TryGetValue(key, out var v);
            d[key] = v + 1;
        }

        // Enumerator class of a suspended coroutine, e.g. "Mech.<DoDeath>d__12" (declaring type + iterator).
        private static string EnumeratorName(IntPtr coroutine)
        {
            if (coroutine == IntPtr.Zero)
                return "?";
            IntPtr obj;
            if (Marshal.ReadInt32(coroutine, CoKind) == 2)
                obj = Marshal.ReadIntPtr(coroutine, CoObject);
            else
            {
                long handle = Marshal.ReadInt64(coroutine, CoHandle);
                obj = handle == -1 ? IntPtr.Zero : mono_gchandle_get_target((uint)handle);
            }
            if (obj == IntPtr.Zero)
                return "(no enumerator)";
            var klass = mono_object_get_class(obj);
            var name = Marshal.PtrToStringAnsi(mono_class_get_name(klass));
            var outer = mono_class_get_nesting_type(klass);
            return outer != IntPtr.Zero ? Marshal.PtrToStringAnsi(mono_class_get_name(outer)) + "." + name : name;
        }
    }
}
