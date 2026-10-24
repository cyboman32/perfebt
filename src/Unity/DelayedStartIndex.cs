using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using MonoMod.Core;
using UnityEngine.SceneManagement;

namespace PerfeBT
{
    // Salvage list refreshes spend 1-11 ms per item in GameObject.SetActive(false) after the list was built in the same
    // frame. Unity queues one delayed Start call per enabled script (MonoBehaviour::AddToManager -> CallDelayed with
    // DelayedStartCall) in DelayedCallManager's std::multiset, ordered by time. Disabling a script
    // (MonoBehaviour::RemoveFromManager) cancels its Start call with DelayedCallManager::CancelCallDelayed, which walks
    // the whole set, so building and hiding N UI components in one frame costs O(N^2) node visits (census: ~3000 pending
    // Start calls during a salvage refresh; a third of the hitch in CancelCallDelayed).
    //
    // This keeps an index instance ID -> pending Start-call node and answers CancelCallDelayed(id, DelayedStartCall,
    // no predicate) from it: it removes that node through DelayedCallManager::Remove, as the walk would, or returns when
    // there is none. Every other cancel runs the original. The index follows every path that adds or removes nodes of
    // this set (from the binary: _Insert_at is called by CallDelayed, Update and DestroyObjectDelayed; nodes are
    // extracted only by Remove, RemoveNoCleanup, CancelCallDelayed2 and MainThreadCleanup; _Erase destroys the set).
    // Same nodes removed in the same way; only the search differs.
    //
    // Safety: an object with more than one pending Start call, or one inserted while CancelCallDelayed2 or
    // MainThreadCleanup runs, goes through the original walk until that walk has removed its calls. MainThreadCleanup
    // drops the index; it is rebuilt by one walk on the next cancel. Every verifyEvery-th indexed cancel also walks
    // the set and compares; any mismatch, a call off the main thread or an exception switches the index off for the
    // session (the original walk runs from then on). Addresses are for this game's UnityPlayer.dll build only
    // (UnityPlayerBuild); the bytes of every hooked function are checked first.
    internal static class DelayedStartIndex
    {
        private const int RvaInsertAt = 0x6d53b0;          // std::_Tree<...DelayedCallManager::Callback...>::_Insert_at
        private const int RvaRemove = 0x6d8af0;            // DelayedCallManager::Remove(Callback&, iterator)
        private const int RvaRemoveNoCleanup = 0x6d8c80;   // DelayedCallManager::RemoveNoCleanup(Callback&, iterator)
        private const int RvaCancel = 0x6d6b50;            // DelayedCallManager::CancelCallDelayed(id, cb, pred, predData)
        private const int RvaCancel2 = 0x6d6910;           // DelayedCallManager::CancelCallDelayed2(id, cbA, cbB)
        private const int RvaMainThreadCleanup = 0x6d8800; // DelayedCallManager::MainThreadCleanup()
        private const int RvaErase = 0x6d9470;             // std::_Tree<...DelayedCallManager::Callback...>::_Erase(node)
        private const int RvaDelayedStartCall = 0x8e44e0;  // MonoBehaviour's delayed Start callback

        private static readonly byte[] InsertBytes = { 0x48, 0x89, 0x5c, 0x24, 0x08, 0x48, 0x89, 0x74, 0x24, 0x10, 0x48, 0x89, 0x7c, 0x24, 0x18, 0x41 };
        private static readonly byte[] RemoveBytes = { 0x48, 0x89, 0x5c, 0x24, 0x08, 0x57, 0x48, 0x83, 0xec, 0x20, 0x48, 0x8b, 0x5a, 0x28, 0x4c, 0x8b };
        private static readonly byte[] RemoveNoCleanupBytes = { 0x48, 0x83, 0xec, 0x28, 0x48, 0x8b, 0x51, 0x40, 0x4c, 0x8b, 0xc9, 0x49, 0x3b, 0xd0, 0x74, 0x6c };
        private static readonly byte[] CancelBytes = { 0x40, 0x56, 0x57, 0x41, 0x54, 0x41, 0x56, 0x41, 0x57, 0x48, 0x83, 0xec, 0x20, 0x48, 0x8b, 0x41 };
        private static readonly byte[] Cancel2Bytes = { 0x40, 0x55, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x57, 0x48, 0x83, 0xec, 0x20, 0x48, 0x8b, 0x41 };
        private static readonly byte[] MainThreadCleanupBytes = { 0x40, 0x56, 0x48, 0x83, 0xec, 0x40, 0x48, 0x83, 0x79, 0x38, 0x00, 0x48, 0x8b, 0xf1, 0x0f, 0x84 };
        private static readonly byte[] EraseBytes = { 0x48, 0x89, 0x5c, 0x24, 0x08, 0x48, 0x89, 0x74, 0x24, 0x10, 0x57, 0x48, 0x83, 0xec, 0x20, 0x80 };
        private static readonly byte[] DelayedStartCallBytes = { 0x48, 0x83, 0xec, 0x28, 0xe8, 0x27, 0x3f, 0x00, 0x00, 0xb8, 0x01, 0x00, 0x00, 0x00, 0x48, 0x83 };

        private const int NodeLeft = UnityPlayerBuild.NodeLeft, NodeParent = UnityPlayerBuild.NodeParent;
        private const int NodeRight = UnityPlayerBuild.NodeRight, NodeIsNil = UnityPlayerBuild.NodeIsNil;
        private const int NodeValue = UnityPlayerBuild.NodeValue, NodeCallback = UnityPlayerBuild.NodeCallback;
        private const int NodeObjectId = UnityPlayerBuild.NodeObjectId;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr InsertFn(IntPtr tree, IntPtr result, byte addLeft, IntPtr where, IntPtr value, byte tag);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void RemoveFn(IntPtr manager, IntPtr value, IntPtr node);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CancelFn(IntPtr manager, int id, IntPtr callback, IntPtr predicate, IntPtr predicateData);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void Cancel2Fn(IntPtr manager, int id, IntPtr callbackA, IntPtr callbackB);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CleanupFn(IntPtr manager);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void EraseFn(IntPtr tree, IntPtr node);

        // Hook delegates and detours stay referenced for the whole session.
        private static readonly List<object> keepAlive = new List<object>();
        private static InsertFn origInsert;
        private static RemoveFn origRemove, origRemoveNoCleanup;
        private static CancelFn origCancel;
        private static Cancel2Fn origCancel2;
        private static CleanupFn origCleanup;
        private static EraseFn origErase;

        private static IntPtr manager, tree, startCall;
        private static int mainThreadId;
        private static int verifyEvery;

        private static bool disabled;
        private static bool valid;     // index matches the set (false until the first rebuild and after MainThreadCleanup)
        private static int busy;       // inside CancelCallDelayed2 / MainThreadCleanup, which extract nodes inline
        private static readonly Dictionary<int, IntPtr> index = new Dictionary<int, IntPtr>();
        private static readonly HashSet<int> multi = new HashSet<int>();   // ids that take the original walk
        private static readonly Stack<IntPtr> walk = new Stack<IntPtr>();

        private static long removed, absent, walked, rebuilds, verified, indexedCancels;
        private static int largest;

        internal static void Apply(int verifyEveryCancels)
        {
            var b = UnityPlayerBuild.Base();
            UnityPlayerBuild.Check(b, RvaInsertAt, InsertBytes, "Callback set _Insert_at");
            UnityPlayerBuild.Check(b, RvaRemove, RemoveBytes, "DelayedCallManager::Remove");
            UnityPlayerBuild.Check(b, RvaRemoveNoCleanup, RemoveNoCleanupBytes, "DelayedCallManager::RemoveNoCleanup");
            UnityPlayerBuild.Check(b, RvaCancel, CancelBytes, "DelayedCallManager::CancelCallDelayed");
            UnityPlayerBuild.Check(b, RvaCancel2, Cancel2Bytes, "DelayedCallManager::CancelCallDelayed2");
            UnityPlayerBuild.Check(b, RvaMainThreadCleanup, MainThreadCleanupBytes, "DelayedCallManager::MainThreadCleanup");
            UnityPlayerBuild.Check(b, RvaErase, EraseBytes, "Callback set _Erase");
            UnityPlayerBuild.Check(b, RvaDelayedStartCall, DelayedStartCallBytes, "DelayedStartCall");
            manager = UnityPlayerBuild.DelayedCallManager(b);
            if (manager == IntPtr.Zero)
                throw new InvalidOperationException("no DelayedCallManager yet");
            tree = manager + UnityPlayerBuild.ManagerHead;
            startCall = b + RvaDelayedStartCall;
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            verifyEvery = verifyEveryCancels;

            // Index maintenance first, the cancel shortcut last; the index is built on the first cancel.
            origInsert = Hook<InsertFn>(b + RvaInsertAt, new InsertFn(Insert));
            origRemove = Hook<RemoveFn>(b + RvaRemove, new RemoveFn(Remove));
            origRemoveNoCleanup = Hook<RemoveFn>(b + RvaRemoveNoCleanup, new RemoveFn(RemoveNoCleanup));
            origCancel2 = Hook<Cancel2Fn>(b + RvaCancel2, new Cancel2Fn(Cancel2));
            origCleanup = Hook<CleanupFn>(b + RvaMainThreadCleanup, new CleanupFn(MainThreadCleanup));
            origErase = Hook<EraseFn>(b + RvaErase, new EraseFn(Erase));
            origCancel = Hook<CancelFn>(b + RvaCancel, new CancelFn(Cancel));

            SceneManager.sceneLoaded += (scene, mode) => LogStats("scene " + scene.name);
        }

        private static T Hook<T>(IntPtr target, T hook) where T : Delegate
        {
            keepAlive.Add(hook);
            var detour = DetourFactory.Current.CreateNativeDetour(target, Marshal.GetFunctionPointerForDelegate(hook));
            keepAlive.Add(detour);
            if (!detour.HasOrigEntrypoint)
                throw new NotSupportedException("native detour without original entry point");
            return (T)(object)Marshal.GetDelegateForFunctionPointer(detour.OrigEntrypoint, typeof(T));
        }

        private static void LogStats(string when)
        {
            if (indexedCancels == 0 && walked == 0)
                return;
            Log.Write($"[startidx] {when}: {removed} Start calls removed via index, {absent} cancels with none pending, " +
                      $"{walked} walked (several pending), {rebuilds} rebuilds, {verified} verified; index {index.Count} " +
                      $"(largest {largest}){(disabled ? ", OFF" : "")}");
        }

        private static void Disable(string reason)
        {
            if (disabled)
                return;
            disabled = true;
            index.Clear();
            multi.Clear();
            Log.Write("[startidx] switched off for this session: " + reason);
            LogStats("at switch-off");
        }

        private static bool OnMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId == mainThreadId)
                return true;
            Disable("delayed call set used off the main thread");
            return false;
        }

        private static IntPtr Insert(IntPtr t, IntPtr result, byte addLeft, IntPtr where, IntPtr value, byte tag)
        {
            var r = origInsert(t, result, addLeft, where, value, tag);
            if (disabled || t != tree || !valid)
                return r;
            try
            {
                if (!OnMainThread())
                    return r;
                var node = Marshal.ReadIntPtr(result);
                if (Marshal.ReadIntPtr(node, NodeCallback) == startCall)
                    Add(Marshal.ReadInt32(node, NodeObjectId), node);
            }
            catch (Exception e)
            {
                Disable(e.ToString());
            }
            return r;
        }

        private static void Add(int id, IntPtr node)
        {
            if (busy > 0 || index.ContainsKey(id))
                multi.Add(id);
            else
            {
                index[id] = node;
                if (index.Count > largest)
                    largest = index.Count;
            }
        }

        private static void Forget(IntPtr m, IntPtr node)
        {
            if (disabled || m != manager)
                return;
            try
            {
                if (!OnMainThread())
                    return;
                int id = Marshal.ReadInt32(node, NodeObjectId);
                if (index.TryGetValue(id, out var indexed) && indexed == node)
                    index.Remove(id);
            }
            catch (Exception e)
            {
                Disable(e.ToString());
            }
        }

        private static void Remove(IntPtr m, IntPtr value, IntPtr node)
        {
            Forget(m, node);
            origRemove(m, value, node);
        }

        private static void RemoveNoCleanup(IntPtr m, IntPtr value, IntPtr node)
        {
            Forget(m, node);
            origRemoveNoCleanup(m, value, node);
        }

        // Removes every node of id whose callback is callbackA or callbackB, extracting them inline.
        private static void Cancel2(IntPtr m, int id, IntPtr callbackA, IntPtr callbackB)
        {
            busy++;
            try
            {
                origCancel2(m, id, callbackA, callbackB);
            }
            finally
            {
                busy--;
            }
            if (disabled || m != manager || (callbackA != startCall && callbackB != startCall))
                return;
            // Ids in multi keep their (possibly stale) entry until their next cancel walks; it is never dereferenced.
            if (!multi.Contains(id))
                index.Remove(id);
        }

        // Empties the whole set (shutdown).
        private static void MainThreadCleanup(IntPtr m)
        {
            busy++;
            try
            {
                origCleanup(m);
            }
            finally
            {
                busy--;
            }
            valid = false;
            index.Clear();
            multi.Clear();
        }

        private static void Erase(IntPtr t, IntPtr node)
        {
            if (!disabled && t == tree)
                Disable("delayed call set destroyed");
            origErase(t, node);
        }

        private static void Cancel(IntPtr m, int id, IntPtr callback, IntPtr predicate, IntPtr predicateData)
        {
            if (disabled || callback != startCall || predicate != IntPtr.Zero || m != manager || busy > 0)
            {
                origCancel(m, id, callback, predicate, predicateData);
                return;
            }
            try
            {
                if (!OnMainThread() || (!valid && !Rebuild()))
                {
                    origCancel(m, id, callback, predicate, predicateData);
                    return;
                }
                if (multi.Contains(id))
                {
                    // Removes every pending Start call of id (no predicate, Start calls have no cleanup).
                    walked++;
                    origCancel(m, id, callback, predicate, predicateData);
                    index.Remove(id);
                    multi.Remove(id);
                    return;
                }
                bool has = index.TryGetValue(id, out var node);
                indexedCancels++;
                if (verifyEvery > 0 && indexedCancels % verifyEvery == 0 && !Verify(id, has, node))
                {
                    origCancel(m, id, callback, predicate, predicateData);
                    return;
                }
                if (!has)
                {
                    absent++;
                    return;
                }
                if (Marshal.ReadInt32(node, NodeObjectId) != id || Marshal.ReadIntPtr(node, NodeCallback) != startCall)
                {
                    Disable($"index entry for id {id} points at another call");
                    origCancel(m, id, callback, predicate, predicateData);
                    return;
                }
                index.Remove(id);
                removed++;
                origRemove(m, node + NodeValue, node);
            }
            catch (Exception e)
            {
                Disable(e.ToString());
                origCancel(m, id, callback, predicate, predicateData);
            }
        }

        // Visits every node of the set; false (and switched off) when the walk does not end within the set's size.
        private static bool ForEachNode(Action<IntPtr> visit)
        {
            var head = Marshal.ReadIntPtr(tree);
            long limit = Marshal.ReadInt64(manager, UnityPlayerBuild.ManagerSize) + 16;
            walk.Clear();
            var root = Marshal.ReadIntPtr(head, NodeParent);
            if (Marshal.ReadByte(root, NodeIsNil) == 0)
                walk.Push(root);
            long seen = 0;
            while (walk.Count > 0)
            {
                if (++seen > limit)
                {
                    walk.Clear();
                    Disable("delayed call set walk exceeded its size");
                    return false;
                }
                var n = walk.Pop();
                visit(n);
                var l = Marshal.ReadIntPtr(n, NodeLeft);
                var r = Marshal.ReadIntPtr(n, NodeRight);
                if (Marshal.ReadByte(l, NodeIsNil) == 0) walk.Push(l);
                if (Marshal.ReadByte(r, NodeIsNil) == 0) walk.Push(r);
            }
            return true;
        }

        private static bool Rebuild()
        {
            index.Clear();
            multi.Clear();
            if (!ForEachNode(n =>
                {
                    if (Marshal.ReadIntPtr(n, NodeCallback) == startCall)
                        Add(Marshal.ReadInt32(n, NodeObjectId), n);
                }))
                return false;
            rebuilds++;
            valid = true;
            return true;
        }

        // The walk's answer for id must equal the index's: no pending Start call, or exactly the indexed node.
        private static bool Verify(int id, bool has, IntPtr node)
        {
            int count = 0;
            bool found = false;
            if (!ForEachNode(n =>
                {
                    if (Marshal.ReadInt32(n, NodeObjectId) == id && Marshal.ReadIntPtr(n, NodeCallback) == startCall)
                    {
                        count++;
                        found |= n == node;
                    }
                }))
                return false;
            if (has ? count == 1 && found : count == 0)
            {
                verified++;
                return true;
            }
            Disable($"verification failed for id {id}: index {(has ? "has a node" : "has none")}, set has {count}" +
                    (has ? (found ? " including it" : " not including it") : ""));
            return false;
        }
    }
}
