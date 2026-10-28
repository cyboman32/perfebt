using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PerfeBT
{
    // Diagnostic: managed heap census. On a hotkey (and optionally once per configured scene) crawls the managed object
    // graph from explicit roots and writes reports/<ts>_heap.txt: a type histogram (count, shallow bytes), bytes per root
    // and the shortest retention paths of selected types (PathTypes).
    //
    // Roots: UnityGameInstance.BattleTechGame ("Game", split into named sub-roots GameDepth fields deep), every static
    // field of every loaded non-dynamic assembly, live MonoBehaviours (FindObjectsOfType, grouped by type), and the
    // statics of closed generic types met during the crawl. The crawl is one breadth-first search from all roots at once,
    // so every object is charged to the root with the shortest reference path to it (ties: earlier root). Objects only
    // reachable from thread stacks, GC handles (WeakReference, Unity's native->managed handles) or [ThreadStatic] fields
    // are not reached; the report shows that remainder as used minus crawled.
    //
    // Statics are read from Mono's raw static storage (mono_class_vtable + mono_vtable_get_static_field_data + field
    // offset), never through FieldInfo.GetValue or ldsfld, so no static constructor runs. A type whose cctor has not run
    // simply has all-null storage. mono_class_vtable may create a missing vtable (allocates the static block, runs no
    // managed code). The GC is disabled (mono_unity_gc_disable) during the crawl, so addresses identify objects.
    internal static class HeapCensus
    {
        // Configuration, assigned from Settings before Apply.
        internal static string Hotkey = "ctrl+shift+H";
        internal static List<string> Scenes = new List<string>();
        internal static float SceneDelaySeconds = 10f;
        internal static bool CollectFirst = true;
        internal static int GameDepth = 3;
        internal static int StaticDepth = 1;
        internal static int TopTypes = 80;
        internal static int TopRoots = 60;
        internal static int MaxSeconds = 300;
        // Types whose retention paths are reported: full type name (base classes match too), optionally ">=bytes".
        internal static List<string> PathTypes = DefaultPathTypes();
        internal static int PathShapes = 10;
        internal static string ReportDir;

        internal static List<string> DefaultPathTypes() => new List<string>
        {
            "CustAmmoCategories.MapTerrainDataCellEx",
            "BattleTech.MapEncounterLayerDataCell",
            "BattleTech.Statistic",
            "System.Byte[]>=1048576",
            "UnityEngine.Matrix4x4[]",
            "BattleTech.CombatGameState",
            "BattleTech.AbstractActor",
            "BattleTech.Mech",
        };

        private static KeyCode key;
        private static bool needCtrl, needShift, needAlt, hotkeyEnabled;
        private static readonly HashSet<string> scenesDone = new HashSet<string>();
        private static string pendingScene;
        private static float pendingAt;
        private static bool running;

        internal static void Apply(Harmony harmony)
        {
            var update = AccessTools.DeclaredMethod(typeof(BattleTech.UnityGameInstance), nameof(BattleTech.UnityGameInstance.Update), Type.EmptyTypes);
            if (update == null || update.ReturnType != typeof(void))
                throw new MissingMethodException("BattleTech.UnityGameInstance::Update() not found");
            if (Type.GetType("Mono.Runtime") == null || IntPtr.Size != 8)
                throw new PlatformNotSupportedException("HeapCensus needs the game's 64-bit Mono runtime");
            ParseHotkey(Hotkey);
            HeapWalker.ParseSpecs(PathTypes); // validate now; throws on a malformed entry
            if (string.IsNullOrEmpty(ReportDir))
                ReportDir = Path.Combine(Path.GetDirectoryName(typeof(HeapCensus).Assembly.Location) ?? ".", "reports");
            if (Scenes != null && Scenes.Count > 0)
                SceneManager.sceneLoaded += OnSceneLoaded;
            harmony.Patch(update, postfix: new HarmonyMethod(typeof(HeapCensus).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)));
            Log.Write($"[heap] census ready: hotkey={(hotkeyEnabled ? Hotkey : "off")}, scenes=[{string.Join(",", Scenes?.ToArray() ?? new string[0])}], path types=[{string.Join(", ", PathTypes?.ToArray() ?? new string[0])}]");
        }

        private static void ParseHotkey(string spec)
        {
            hotkeyEnabled = false;
            if (string.IsNullOrEmpty(spec))
                return;
            KeyCode? k = null;
            foreach (var raw in spec.Split('+'))
            {
                string t = raw.Trim();
                if (t.Equals("ctrl", StringComparison.OrdinalIgnoreCase) || t.Equals("control", StringComparison.OrdinalIgnoreCase)) needCtrl = true;
                else if (t.Equals("shift", StringComparison.OrdinalIgnoreCase)) needShift = true;
                else if (t.Equals("alt", StringComparison.OrdinalIgnoreCase)) needAlt = true;
                else k = (KeyCode)Enum.Parse(typeof(KeyCode), t, true);
            }
            if (k == null)
                throw new ArgumentException("HeapCensusHotkey has no key: " + spec);
            key = k.Value;
            hotkeyEnabled = true;
        }

        private static readonly Dictionary<string, int> sceneLoads = new Dictionary<string, int>();

        // Scenes entries: "Name" = first load of that scene, "Name#N" = its N-th load this session.
        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            sceneLoads.TryGetValue(scene.name, out var n);
            sceneLoads[scene.name] = ++n;
            var key = n == 1 && Scenes.Contains(scene.name) ? scene.name : scene.name + "#" + n;
            if (Scenes.Contains(key) && !scenesDone.Contains(key))
            {
                scenesDone.Add(key);
                pendingScene = key;
                pendingAt = Time.realtimeSinceStartup + SceneDelaySeconds;
            }
        }

        private static void Postfix()
        {
            if (running)
                return;
            string reason = null;
            if (hotkeyEnabled && Input.GetKeyDown(key)
                && (!needCtrl || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && (!needShift || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                && (!needAlt || Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)))
                reason = "hotkey";
            else if (pendingScene != null && Time.realtimeSinceStartup >= pendingAt)
            {
                reason = "scene_" + pendingScene;
                pendingScene = null;
            }
            if (reason == null)
                return;
            running = true;
            try { Run(reason); }
            catch (Exception e) { Log.Write("[heap] census failed: " + e); }
            finally { running = false; }
        }

        internal static void Run(string reason)
        {
            var total = Stopwatch.StartNew();
            string scene = SceneManager.GetActiveScene().name;
            Log.Write($"[heap] census start ({reason}, scene {scene})");
            long gcMs = 0;
            if (CollectFirst)
            {
                var sw = Stopwatch.StartNew();
                LoadGcPause.WithCollectorOn(() =>
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                });
                gcMs = sw.ElapsedMilliseconds;
            }
            long heapSize = MonoApi.mono_gc_get_heap_size(), usedSize = MonoApi.mono_gc_get_used_size();

            var setup = Stopwatch.StartNew();
            var statics = MonoStatics.Collect(out int staticTypes, out int vtableFailures);
            var behaviours = new List<KeyValuePair<Type, List<object>>>();
            try
            {
                var map = new Dictionary<Type, List<object>>();
                foreach (var mb in UnityEngine.Object.FindObjectsOfType(typeof(MonoBehaviour)))
                {
                    if ((object)mb == null) continue;
                    var t = mb.GetType();
                    if (!map.TryGetValue(t, out var list)) { map[t] = list = new List<object>(); behaviours.Add(new KeyValuePair<Type, List<object>>(t, list)); }
                    list.Add(mb);
                }
            }
            catch (Exception e) { Log.Write("[heap] FindObjectsOfType failed: " + e.Message); }
            object game = null;
            try { game = BattleTech.UnityGameInstance.BattleTechGame; }
            catch (Exception e) { Log.Write("[heap] BattleTechGame failed: " + e.Message); }
            long setupMs = setup.ElapsedMilliseconds;

            using (var walker = new HeapWalker(MonoApi.ObjectSize, MonoApi.ObjectSize) { MaxSeconds = MaxSeconds })
            {
                walker.SetPathSpecs(PathTypes);
                var genericSeen = new HashSet<Type>();
                var genericQueue = new Queue<Type>();
                walker.OnNewType = t =>
                {
                    for (var c = t; c != null && c != typeof(object); c = c.BaseType)
                        if (c.IsGenericType && !c.ContainsGenericParameters && genericSeen.Add(c))
                        {
                            genericQueue.Enqueue(c);
                            walker.HasPending = true;
                        }
                };
                walker.SeedPending = () =>
                {
                    while (genericQueue.Count > 0)
                        foreach (var s in MonoStatics.CollectType(genericQueue.Dequeue(), "G:"))
                            MonoStatics.Seed(walker, s, StaticDepth);
                };

                bool disabledHere = false;
                HeapUnreached.Result unreached = null;
                var crawl = Stopwatch.StartNew();
                try
                {
                    try
                    {
                        if (MonoApi.mono_unity_gc_is_disabled() == 0)
                        {
                            MonoApi.mono_unity_gc_disable();
                            disabledHere = true;
                        }
                    }
                    catch (Exception e) { Log.Write("[heap] GC disable failed, crawling with GC enabled: " + e.Message); }

                    if (game != null)
                        walker.SeedRoot("Game", "Game", GameDepth, game, false);
                    foreach (var s in statics)
                        MonoStatics.Seed(walker, s, StaticDepth);
                    foreach (var kv in behaviours)
                    {
                        int id = walker.AddRoot("MB:" + kv.Key.FullName, "MB:" + kv.Key.Assembly.GetName().Name, 0);
                        foreach (var mb in kv.Value)
                            walker.Seed(id, mb);
                        walker.DropIfEmpty(id);
                    }
                    walker.Run();
                    // Mark bits and addresses are only valid while the GC stays disabled.
                    if (disabledHere || MonoApi.mono_unity_gc_is_disabled() != 0)
                        unreached = HeapUnreached.Run(walker);
                }
                catch (Exception e)
                {
                    walker.Note("crawl aborted: " + e);
                }
                finally
                {
                    if (disabledHere)
                        MonoApi.mono_unity_gc_enable();
                }
                long crawlMs = crawl.ElapsedMilliseconds;

                // Path analysis reads only the recorded parent/field/type columns, never object memory.
                var paths = Stopwatch.StartNew();
                try { walker.AnalyzePaths(PathShapes); }
                catch (Exception e) { walker.Note("path analysis failed: " + e); }
                long pathsMs = paths.ElapsedMilliseconds;

                Directory.CreateDirectory(ReportDir);
                string file = Path.Combine(ReportDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_heap.txt");
                HeapWalker.Invariant(() =>
                {
                    using (var w = new StreamWriter(file, false, new UTF8Encoding(false)))
                    {
                        w.WriteLine($"PerfeBT heap census ({reason}) at {DateTime.Now:yyyy-MM-dd HH:mm:ss}, scene {scene}");
                        w.WriteLine($"mono heap {Mb(heapSize)} MB, used {Mb(usedSize)} MB{(CollectFirst ? $" after full GC ({gcMs} ms)" : " (no GC first: used includes garbage)")}");
                        w.WriteLine($"crawled {walker.Objects:N0} objects, {Mb(walker.Bytes)} MB shallow = {(usedSize > 0 ? 100.0 * walker.Bytes / usedSize : 0):F1}% of used; unreached {Mb(Math.Max(0, usedSize - walker.Bytes))} MB");
                        w.WriteLine($"setup {setupMs} ms ({statics.Count} static roots in {staticTypes} types, {vtableFailures} vtable failures, {behaviours.Sum(b => b.Value.Count)} MonoBehaviours), crawl {crawlMs} ms, paths {pathsMs} ms, total {total.ElapsedMilliseconds} ms, GC disabled during crawl: {disabledHere}");
                        walker.WriteReport(w, TopTypes, TopRoots);
                        if (unreached != null)
                            HeapUnreached.Write(w, unreached);
                    }
                    Log.Write($"[heap] census written to {file}: {walker.Objects:N0} objects, {Mb(walker.Bytes)} MB in {total.ElapsedMilliseconds} ms (crawl {crawlMs} ms, paths {pathsMs} ms){(walker.Aborted ? " ABORTED" : "")}");
                });
            }
        }

        private static string Mb(long b) => HeapWalker.Mb(b);
    }

    // Static roots read from Mono's raw static storage (see HeapCensus header).
    internal static class MonoStatics
    {
        internal sealed class Slot
        {
            internal FieldInfo Field;
            internal string Group; // assembly name, "G:" prefixed for statics of closed generic types
            internal IntPtr Address;
            internal Type FieldType;
            internal string Name => $"{Group}|{HeapWalker.TypeName(Field.DeclaringType)}::{Field.Name}";
        }

        // Static storage blocks are allocated once per class and never move, so slots are kept for later censuses and
        // only assemblies loaded since the last census are scanned.
        private static readonly HashSet<Assembly> scanned = new HashSet<Assembly>();
        private static readonly List<Slot> cache = new List<Slot>();
        private static int cachedTypes, cachedFailures;

        internal static List<Slot> Collect(out int typeCount, out int vtableFailures)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic || !scanned.Add(asm))
                    continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }
                foreach (var t in types)
                {
                    if (t == null || t.ContainsGenericParameters)
                        continue;
                    int before = cache.Count;
                    if (!AddType(t, "", cache)) cachedFailures++;
                    if (cache.Count > before) cachedTypes++;
                }
            }
            typeCount = cachedTypes;
            vtableFailures = cachedFailures;
            return cache;
        }

        internal static List<Slot> CollectType(Type t, string prefix)
        {
            var result = new List<Slot>();
            AddType(t, prefix, result);
            return result;
        }

        private static readonly IntPtr domain = MonoApi.mono_domain_get();

        private static readonly Dictionary<Assembly, string> groups = new Dictionary<Assembly, string>();

        // false = the type has static reference fields but its static storage could not be located.
        private static bool AddType(Type t, string prefix, List<Slot> result)
        {
            if (t == typeof(MonoStatics) || t == typeof(HeapWalker))
                return true; // the census' own caches
            List<FieldInfo> fields = null;
            try
            {
                foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.IsLiteral || f.IsDefined(typeof(ThreadStaticAttribute), false) || f.IsDefined(typeof(ContextStaticAttribute), false))
                        continue;
                    if (!HeapWalker.HoldsReferences(f.FieldType))
                        continue;
                    (fields ?? (fields = new List<FieldInfo>())).Add(f);
                }
            }
            catch { return true; }
            if (fields == null)
                return true;
            try
            {
                IntPtr klass = MonoApi.mono_class_from_mono_type(t.TypeHandle.Value);
                IntPtr vt = klass == IntPtr.Zero ? IntPtr.Zero : MonoApi.mono_class_vtable(domain, klass);
                IntPtr data = vt == IntPtr.Zero ? IntPtr.Zero : MonoApi.mono_vtable_get_static_field_data(vt);
                if (data == IntPtr.Zero)
                    return false;
                if (!groups.TryGetValue(t.Assembly, out var group))
                    groups[t.Assembly] = group = t.Assembly.GetName().Name;
                foreach (var f in fields)
                {
                    int off = MonoApi.mono_field_get_offset(f.FieldHandle.Value);
                    if (off < 0)
                        continue; // special (thread/context) static
                    result.Add(new Slot { Field = f, Group = prefix + group, Address = data + off, FieldType = f.FieldType });
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static void Seed(HeapWalker w, Slot s, int depth)
        {
            object v;
            bool copy = s.FieldType.IsValueType;
            try
            {
                v = copy ? HeapWalker.BoxFromAddress(s.FieldType, s.Address) : HeapWalker.ReadRef(s.Address);
            }
            catch (Exception e)
            {
                w.Note($"static {s.Name}: {e.Message}");
                return;
            }
            if (v != null)
                w.SeedRoot(s.Name, s.Group, copy ? 0 : depth, v, copy);
        }
    }

    // Unmanaged column of fixed-width values in 1M-entry chunks: crawl bookkeeping stays off the managed heap.
    internal sealed class NativeColumn : IDisposable
    {
        internal const int Shift = 20, ChunkLength = 1 << Shift, Mask = ChunkLength - 1;
        private readonly int width;
        private IntPtr[] chunks = new IntPtr[16];
        internal static long Allocated, Peak;

        internal NativeColumn(int width)
        {
            this.width = width;
        }

        internal void Ensure(int i)
        {
            int c = i >> Shift;
            if (c >= chunks.Length)
                Array.Resize(ref chunks, Math.Max(c + 1, chunks.Length * 2));
            if (chunks[c] != IntPtr.Zero)
                return;
            long bytes = (long)ChunkLength * width;
            chunks[c] = Marshal.AllocHGlobal((IntPtr)bytes);
            Allocated += bytes;
            if (Allocated > Peak) Peak = Allocated;
        }

        internal int GetInt(int i) => Marshal.ReadInt32(chunks[i >> Shift], (i & Mask) << 2);
        internal void SetInt(int i, int v) => Marshal.WriteInt32(chunks[i >> Shift], (i & Mask) << 2, v);
        internal long GetLong(int i) => Marshal.ReadInt64(chunks[i >> Shift], (i & Mask) << 3);
        internal void SetLong(int i, long v) => Marshal.WriteInt64(chunks[i >> Shift], (i & Mask) << 3, v);

        // Allocates and zeroes entries [0, count).
        internal void Zero(int count)
        {
            for (int c = 0; (long)c << Shift < count; c++)
            {
                Ensure(c << Shift);
                IntPtr p = chunks[c];
                int bytes = ChunkLength * width;
                for (int o = 0; o < bytes; o += 8)
                    Marshal.WriteInt64(p, o, 0);
            }
        }

        internal void Free(int chunk)
        {
            if (chunk < 0 || chunk >= chunks.Length || chunks[chunk] == IntPtr.Zero)
                return;
            Marshal.FreeHGlobal(chunks[chunk]);
            chunks[chunk] = IntPtr.Zero;
            Allocated -= (long)ChunkLength * width;
        }

        public void Dispose()
        {
            for (int c = 0; c < chunks.Length; c++)
                Free(c);
        }
    }

    // Breadth-first graph crawler. Pure managed code apart from the injected size functions, so it can be tested offline.
    // Objects are identified by address (valid while the GC cannot move or free objects: Boehm with GC disabled, or a
    // CLR no-GC region). Visited set: sparse bitmap, 1 bit per 16 bytes of address space (every managed object is at
    // least 16 bytes, so addr>>4 is unique), 4 MB chunks of address space per 32 KB bitmap.
    // Every discovered object gets a sequential index; native columns hold its address (the BFS queue, freed behind the
    // queue head), parent index, field id, bucket (root or sub-root) and type id. Parent + field give retention paths.
    internal sealed class HeapWalker : IDisposable
    {
        internal delegate void Visitor(object o, HeapWalker w);

        internal sealed class TypeStats
        {
            internal Type Type;
            internal int Id;
            internal long Count, Bytes, FixedSize = -1;
            internal Visitor Visit;
            internal int Errors;
            internal bool Expandable;
            internal int[] Specs;
        }

        // A root, or a sub-root ("Game.DataManager") that splits a root's reach by field near the root.
        internal sealed class Bucket
        {
            internal string Name, Group;
            internal int Parent = -1, Top, Depth, MaxDepth, HeadIndex = -1, Seeds;
            internal long Count, Bytes, InclCount, InclBytes;
        }

        internal sealed class PathSpec
        {
            internal string Text, TypeName;
            internal long MinBytes;
            internal long Count, Bytes;
            internal readonly List<int> Targets = new List<int>();
            internal readonly List<int> Sizes = new List<int>();
            internal int ShapeCount;
            internal List<KeyValuePair<string, long[]>> Shapes = new List<KeyValuePair<string, long[]>>(); // text -> {count, bytes}
        }

        private readonly Func<IntPtr, long> varSize, fixedSize;
        internal Action<Type> OnNewType;
        internal Action SeedPending;
        internal bool HasPending;
        internal int MaxSeconds = 300;
        internal int MaxTargetsPerSpec = 4000000;
        internal bool Aborted;
        internal long Objects, Bytes, Errors;

        private readonly LongMap<ulong[]> chunks = new LongMap<ulong[]>();
        private long lastChunkKey = long.MinValue;
        private ulong[] lastChunk;
        private readonly LongMap<TypeStats> typesByVtable = new LongMap<TypeStats>();
        private readonly Dictionary<Type, TypeStats> types = new Dictionary<Type, TypeStats>();
        private readonly List<TypeStats> typeList = new List<TypeStats>();
        private readonly List<Bucket> buckets = new List<Bucket>();
        private readonly LongMap<int> subBuckets = new LongMap<int>();
        private readonly List<PathSpec> specs = new List<PathSpec>();
        private readonly List<string> notes = new List<string>();
        private readonly Stopwatch clock = Stopwatch.StartNew();

        // (bucket, type) -> count/bytes
        private readonly LongMap<int> btIndex = new LongMap<int>();
        private int[] btBucket = new int[1024], btType = new int[1024];
        private long[] btCount = new long[1024], btBytes = new long[1024];
        private int btN;
        private long lastBtKey = long.MinValue;
        private int lastBt;

        private readonly NativeColumn addrCol = new NativeColumn(8), parentCol = new NativeColumn(4), fieldCol = new NativeColumn(4),
            bucketCol = new NativeColumn(4), typeCol = new NativeColumn(4);
        private int head, tail, peakQueue;
        private int curIndex = -1, curBucket;
        private bool curExpand;

        internal HeapWalker(Func<IntPtr, long> varSize, Func<IntPtr, long> fixedSize)
        {
            this.varSize = varSize;
            this.fixedSize = fixedSize;
            NativeColumn.Peak = NativeColumn.Allocated;
        }

        public void Dispose()
        {
            addrCol.Dispose();
            parentCol.Dispose();
            fieldCol.Dispose();
            bucketCol.Dispose();
            typeCol.Dispose();
        }

        internal IEnumerable<TypeStats> Types => types.Values;
        internal IReadOnlyList<Bucket> Roots => buckets;
        internal IReadOnlyList<PathSpec> Specs => specs;

        internal void Note(string s)
        {
            if (notes.Count < 200)
                notes.Add(s);
        }

        // ---- path type specs ----

        // "Full.Type.Name" or "Full.Type.Name>=bytes". Duplicates are dropped.
        internal static List<PathSpec> ParseSpecs(IEnumerable<string> list)
        {
            var result = new List<PathSpec>();
            if (list == null)
                return result;
            foreach (var raw in list)
            {
                if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(raw.Trim()) || result.Any(s => s.Text == raw.Trim()))
                    continue;
                string text = raw.Trim(), name = text;
                long min = 0;
                int ge = text.IndexOf(">=", StringComparison.Ordinal);
                if (ge >= 0)
                {
                    name = text.Substring(0, ge).Trim();
                    min = long.Parse(text.Substring(ge + 2).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                }
                if (name.Length == 0)
                    throw new FormatException("HeapCensusPathTypes entry without type name: " + raw);
                result.Add(new PathSpec { Text = text, TypeName = name, MinBytes = min });
            }
            return result;
        }

        internal void SetPathSpecs(IEnumerable<string> list)
        {
            specs.Clear();
            specs.AddRange(ParseSpecs(list));
        }

        private int[] MatchSpecs(Type t)
        {
            List<int> found = null;
            for (int k = 0; k < specs.Count; k++)
                for (var c = t; c != null; c = c.BaseType)
                    if (specs[k].TypeName == c.FullName || specs[k].TypeName == TypeName(c))
                    {
                        (found ?? (found = new List<int>())).Add(k);
                        break;
                    }
            return found?.ToArray();
        }

        // ---- roots ----

        internal int AddRoot(string name, string group, int expandDepth)
        {
            buckets.Add(new Bucket { Name = name, Group = group, Top = buckets.Count, MaxDepth = expandDepth });
            return buckets.Count - 1;
        }

        // Removes the root just added if none of its seeds was new.
        internal void DropIfEmpty(int id)
        {
            if (id == buckets.Count - 1 && buckets[id].Seeds == 0)
                buckets.RemoveAt(id);
        }

        // value: a heap object, or (copy = true) a boxed copy of a struct whose reference fields are the root's seeds.
        internal void SeedRoot(string name, string group, int expandDepth, object value, bool copy)
        {
            if (value == null || Aborted)
                return;
            int id = AddRoot(name, group, expandDepth);
            if (copy) SeedCopy(id, value);
            else Seed(id, value);
            DropIfEmpty(id);
        }

        internal void Seed(int root, object value)
        {
            if (value == null || Aborted)
                return;
            long a = (long)AddrOf(value);
            if (!TestAndSet(a))
                return;
            int i = Append(a, -1, RootField, root);
            if (buckets[root].HeadIndex < 0)
                buckets[root].HeadIndex = i;
        }

        internal void SeedCopy(int root, object box)
        {
            if (box == null || Aborted)
                return;
            int si = curIndex, sb = curBucket;
            bool se = curExpand;
            curIndex = -1;
            curBucket = root;
            curExpand = false;
            try { VisitBoxCopy(box); }
            finally
            {
                curIndex = si;
                curBucket = sb;
                curExpand = se;
            }
        }

        // ---- crawl ----

        private int Append(long a, int parent, int field, int bucket)
        {
            int i = tail;
            if ((i & NativeColumn.Mask) == 0)
            {
                addrCol.Ensure(i);
                parentCol.Ensure(i);
                fieldCol.Ensure(i);
                bucketCol.Ensure(i);
                typeCol.Ensure(i);
            }
            addrCol.SetLong(i, a);
            parentCol.SetInt(i, parent);
            fieldCol.SetInt(i, field);
            bucketCol.SetInt(i, bucket);
            typeCol.SetInt(i, -1);
            tail = i + 1;
            if (parent < 0)
                buckets[bucket].Seeds++;
            return i;
        }

        // Called from emitted visitors with the address of a non-null reference and the field id it was read from.
        internal void Push(IntPtr address, int field)
        {
            long a = (long)address;
            if (!TestAndSet(a))
                return;
            int b = curExpand ? SubBucket(curBucket, field, tail) : curBucket;
            Append(a, curIndex, field, b);
        }

        internal void PushObject(object o, int field)
        {
            if (o != null)
                Push(AddrOf(o), field);
        }

        private int SubBucket(int parent, int field, int headIndex)
        {
            long key = ((long)parent << 32) | (uint)field;
            if (subBuckets.TryGetValue(key, out int id))
                return id;
            var p = buckets[parent];
            buckets.Add(new Bucket { Name = p.Name + fieldTable[field].Name, Group = p.Group, Parent = parent, Top = p.Top, Depth = p.Depth + 1, MaxDepth = p.MaxDepth, HeadIndex = headIndex });
            id = buckets.Count - 1;
            subBuckets.Set(key, id);
            return id;
        }

        internal void Run()
        {
            while (!Aborted)
            {
                if (HasPending)
                {
                    HasPending = false;
                    try { SeedPending?.Invoke(); }
                    catch (Exception e) { Note("seeding generic statics: " + e.Message); }
                }
                if (head >= tail)
                    break;
                int i = head++;
                if ((i & NativeColumn.Mask) == 0 && i > 0)
                    addrCol.Free((i >> NativeColumn.Shift) - 1);
                if (tail - i > peakQueue)
                    peakQueue = tail - i;
                long a = addrCol.GetLong(i);
                int b = bucketCol.GetInt(i);
                object o = ObjAt((IntPtr)a);
                var ts = CountObject(o, a, b, i);
                if (ts?.Visit != null)
                {
                    curIndex = i;
                    curBucket = b;
                    var bk = buckets[b];
                    curExpand = ts.Expandable && bk.HeadIndex == i && bk.Depth < bk.MaxDepth;
                    try { ts.Visit(o, this); }
                    catch (Exception e)
                    {
                        ts.Errors++;
                        Errors++;
                        if (ts.Errors == 1) Note($"{TypeName(ts.Type)}: {e.GetType().Name} {e.Message}");
                    }
                }
                if ((i & 0xFFFF) == 0 && clock.Elapsed.TotalSeconds > MaxSeconds)
                {
                    Aborted = true;
                    Note($"aborted after {MaxSeconds} s; counts are partial ({tail - head:N0} objects still queued)");
                }
            }
            curIndex = -1;
            curExpand = false;
        }

        internal void VisitBoxCopy(object box)
        {
            var ts = GetStats(box.GetType());
            if (ts.Visit == null)
                return;
            try { ts.Visit(box, this); }
            catch (Exception e) { ts.Errors++; Errors++; Note($"{TypeName(ts.Type)} box: {e.Message}"); }
        }

        private TypeStats CountObject(object o, long a, int b, int i)
        {
            TypeStats ts;
            long size;
            try
            {
                long vt = (long)ReadPtr((IntPtr)a);
                if (!typesByVtable.TryGetValue(vt, out ts))
                {
                    ts = GetStats(o.GetType());
                    if (ts.FixedSize < 0 && !ts.Type.IsArray && ts.Type != typeof(string))
                        ts.FixedSize = fixedSize((IntPtr)a);
                    typesByVtable.Set(vt, ts);
                }
                size = ts.FixedSize >= 0 ? ts.FixedSize : varSize((IntPtr)a);
            }
            catch (Exception e)
            {
                Errors++;
                Note("count: " + e.Message);
                return null;
            }
            ts.Count++;
            ts.Bytes += size;
            Objects++;
            Bytes += size;
            var bk = buckets[b];
            bk.Count++;
            bk.Bytes += size;
            typeCol.SetInt(i, ts.Id);
            AddBucketType(b, ts.Id, size);
            if (ts.Specs != null)
                foreach (int k in ts.Specs)
                {
                    var sp = specs[k];
                    if (size < sp.MinBytes)
                        continue;
                    sp.Count++;
                    sp.Bytes += size;
                    if (sp.Targets.Count < MaxTargetsPerSpec)
                    {
                        sp.Targets.Add(i);
                        sp.Sizes.Add((int)Math.Min(size, int.MaxValue));
                    }
                }
            return ts;
        }

        private void AddBucketType(int b, int type, long size)
        {
            long key = ((long)b << 32) | (uint)type;
            int k;
            if (key == lastBtKey)
                k = lastBt;
            else
            {
                if (!btIndex.TryGetValue(key, out k))
                {
                    k = btN++;
                    if (k == btBucket.Length)
                    {
                        Array.Resize(ref btBucket, k * 2);
                        Array.Resize(ref btType, k * 2);
                        Array.Resize(ref btCount, k * 2);
                        Array.Resize(ref btBytes, k * 2);
                    }
                    btBucket[k] = b;
                    btType[k] = type;
                    btIndex.Set(key, k);
                }
                lastBtKey = key;
                lastBt = k;
            }
            btCount[k]++;
            btBytes[k] += size;
        }

        private TypeStats GetStats(Type t)
        {
            if (types.TryGetValue(t, out var ts))
                return ts;
            ts = new TypeStats
            {
                Type = t,
                Id = typeList.Count,
                Expandable = !t.IsArray && !t.IsValueType && t != typeof(string) && !(t.Namespace ?? "").StartsWith("System", StringComparison.Ordinal),
                Specs = MatchSpecs(t),
            };
            if (!visitorCache.TryGetValue(t, out ts.Visit))
            {
                try
                {
                    ts.Visit = Compile(t);
                    visitorCache[t] = ts.Visit;
                }
                catch (Exception e) { Note($"compile {TypeName(t)}: {e.Message}"); }
            }
            types[t] = ts;
            typeList.Add(ts);
            try { OnNewType?.Invoke(t); }
            catch { }
            return ts;
        }


        private bool TestAndSet(long a)
        {
            if (a == 0)
                return false;
            long key = a >> 22;
            ulong[] bits;
            if (key == lastChunkKey)
                bits = lastChunk;
            else
            {
                if (!chunks.TryGetValue(key, out bits))
                {
                    bits = new ulong[4096];
                    chunks.Set(key, bits);
                }
                lastChunkKey = key;
                lastChunk = bits;
            }
            int bit = (int)((a >> 4) & 0x3FFFF);
            ulong m = 1UL << (bit & 63);
            ref ulong word = ref bits[bit >> 6];
            if ((word & m) != 0)
                return false;
            word |= m;
            return true;
        }

        // Whether the crawl reached the object at address a (read-only).
        internal bool Visited(long a)
        {
            if (!chunks.TryGetValue(a >> 22, out var bits))
                return false;
            int bit = (int)((a >> 4) & 0x3FFFF);
            return (bits[bit >> 6] & (1UL << (bit & 63))) != 0;
        }

        // The type of the crawled objects with this vtable, null if the crawl met none.
        internal Type TypeOfVtable(long vt) => typesByVtable.TryGetValue(vt, out var ts) ? ts?.Type : null;

        internal long BitmapBytes => chunks.Count * 4096L * 8;

        // ---- retention paths ----

        private struct ShapeNode
        {
            internal int Parent; // node index, or -2 - root for the first hop
            internal int Field, Type;
            internal bool Repeat;
        }

        private struct ShapeKey : IEquatable<ShapeKey>
        {
            internal int Parent, Field, Type;
            internal bool Repeat;
            public bool Equals(ShapeKey o) => Parent == o.Parent && Field == o.Field && Type == o.Type && Repeat == o.Repeat;
            public override bool Equals(object o) => o is ShapeKey k && Equals(k);
            public override int GetHashCode() => ((Parent * 31 + Field) * 31 + Type) * 2 + (Repeat ? 1 : 0);
        }

        private List<ShapeNode> nodes;
        private Dictionary<ShapeKey, int> nodeIds;

        // Groups every recorded target of every spec by the shape of its BFS (shortest) path from its root.
        internal void AnalyzePaths(int topShapes)
        {
            if (specs.Count == 0 || head == 0)
                return;
            nodes = new List<ShapeNode>();
            nodeIds = new Dictionary<ShapeKey, int>();
            var rendered = new Dictionary<int, string>();
            var stack = new List<int>();
            using (var shapeCol = new NativeColumn(4))
            {
                shapeCol.Zero(head);
                foreach (var sp in specs)
                {
                    var byShape = new Dictionary<int, long[]>();
                    for (int t = 0; t < sp.Targets.Count; t++)
                    {
                        int s = ShapeOf(sp.Targets[t], shapeCol, stack);
                        if (!byShape.TryGetValue(s, out var agg))
                            byShape[s] = agg = new long[2];
                        agg[0]++;
                        agg[1] += sp.Sizes[t];
                    }
                    var byText = new Dictionary<string, long[]>();
                    foreach (var kv in byShape)
                    {
                        if (!rendered.TryGetValue(kv.Key, out var text))
                            rendered[kv.Key] = text = Render(kv.Key);
                        if (!byText.TryGetValue(text, out var agg))
                            byText[text] = agg = new long[2];
                        agg[0] += kv.Value[0];
                        agg[1] += kv.Value[1];
                    }
                    sp.ShapeCount = byText.Count;
                    sp.Shapes = byText.OrderByDescending(kv => kv.Value[1]).ThenByDescending(kv => kv.Value[0]).Take(topShapes).ToList();
                }
            }
            nodes = null;
            nodeIds = null;
        }

        private int ShapeOf(int i, NativeColumn shapeCol, List<int> stack)
        {
            stack.Clear();
            int s = -1;
            for (int j = i; ;)
            {
                int m = shapeCol.GetInt(j);
                if (m != 0)
                {
                    s = m - 1;
                    break;
                }
                stack.Add(j);
                int p = parentCol.GetInt(j);
                if (p < 0)
                    break;
                j = p;
            }
            for (int k = stack.Count - 1; k >= 0; k--)
            {
                int x = stack[k];
                int f = fieldCol.GetInt(x), ty = typeCol.GetInt(x);
                s = s < 0 ? Node(-2 - buckets[bucketCol.GetInt(x)].Top, f, ty, false) : Extend(s, f, ty);
                shapeCol.SetInt(x, s + 1);
            }
            return s;
        }

        // A hop identical to the previous one (linked chains) folds into one "(hop)+" node.
        private int Extend(int s, int field, int type)
        {
            var n = nodes[s];
            if (n.Parent >= 0 && n.Field == field && n.Type == type)
                return n.Repeat ? s : Node(n.Parent, field, type, true);
            return Node(s, field, type, false);
        }

        private int Node(int parent, int field, int type, bool repeat)
        {
            var key = new ShapeKey { Parent = parent, Field = field, Type = type, Repeat = repeat };
            if (nodeIds.TryGetValue(key, out int id))
                return id;
            nodes.Add(new ShapeNode { Parent = parent, Field = field, Type = type, Repeat = repeat });
            nodeIds[key] = nodes.Count - 1;
            return nodes.Count - 1;
        }

        // "Root:Type .field:Type []:Type ...". A collection's internal storage array (List._items, Dictionary.entries)
        // is left out, so its elements follow the collection directly as "[]" ("[key]" for dictionary keys).
        private string Render(int s)
        {
            var chain = new List<ShapeNode>();
            int x = s;
            while (true)
            {
                chain.Add(nodes[x]);
                if (nodes[x].Parent < 0) break;
                x = nodes[x].Parent;
            }
            chain.Reverse();
            var parts = new List<string>();
            var first = chain[0];
            parts.Add(buckets[-2 - first.Parent].Name + (first.Field != RootField ? fieldTable[first.Field].Name : "") + ":" + ShortType(first.Type));
            Type prev = TypeOf(first.Type);
            for (int k = 1; k < chain.Count; k++)
            {
                var n = chain[k];
                Type ty = TypeOf(n.Type);
                var fd = fieldTable[n.Field];
                if (fd.Kind == KindField && !n.Repeat && ty != null && ty.IsArray && IsCollection(prev))
                {
                    prev = ty;
                    continue;
                }
                string piece = fd.Name + ":" + ShortType(n.Type);
                parts.Add(n.Repeat ? "(" + piece + ")+" : piece);
                prev = ty;
            }
            if (parts.Count > 30)
                parts = parts.Take(12).Concat(new[] { $"...({parts.Count - 26} hops)..." }).Concat(parts.Skip(parts.Count - 14)).ToList();
            return string.Join(" ", parts.ToArray());
        }

        private static bool IsCollection(Type t) => t != null && (t.Namespace ?? "").StartsWith("System.Collections", StringComparison.Ordinal);

        private Type TypeOf(int id) => id >= 0 && id < typeList.Count ? typeList[id].Type : null;

        private string ShortType(int id)
        {
            var t = TypeOf(id);
            return t == null ? "?" : ShortName(t);
        }

        internal static string ShortName(Type t)
        {
            try
            {
                if (t.IsArray) return ShortName(t.GetElementType()) + (t.GetArrayRank() == 1 && t == t.GetElementType().MakeArrayType() ? "[]" : "[" + new string(',', t.GetArrayRank() - 1) + "]");
                if (!t.IsGenericType) return t.Name;
                string n = t.Name;
                int tick = n.IndexOf('`');
                if (tick >= 0) n = n.Substring(0, tick);
                return n + "<" + string.Join(",", t.GetGenericArguments().Select(ShortName).ToArray()) + ">";
            }
            catch { return t.Name; }
        }

        // ---- report ----

        internal static string Mb(long b) => (b / 1048576.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

        // Report text is culture-invariant (the game may run under e.g. ru-RU).
        internal static void Invariant(Action write)
        {
            var thread = System.Threading.Thread.CurrentThread;
            var culture = thread.CurrentCulture;
            thread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            try { write(); }
            finally { thread.CurrentCulture = culture; }
        }

        internal void WriteReport(TextWriter w, int topTypes, int topRoots) => Invariant(() => WriteReportBody(w, topTypes, topRoots));

        private void WriteReportBody(TextWriter w, int topTypes, int topRoots)
        {
            ComputeInclusive();
            w.WriteLine($"types {types.Count:N0}, roots+sub-roots {buckets.Count:N0}, visitor errors {Errors:N0}, bitmap {Mb(BitmapBytes)} MB, native records peak {Mb(NativeColumn.Peak)} MB, peak queue {peakQueue:N0}{(Aborted ? ", ABORTED (partial)" : "")}");
            w.WriteLine("shallow = mono_object_get_size (no allocator rounding). Attribution: one breadth-first crawl from all roots at once;");
            w.WriteLine("each object is charged to the root with the shortest reference path to it, ties to the earlier root (Game, statics,");
            w.WriteLine("MonoBehaviours, generic statics met during the crawl). Sub-roots (Root.field) split a root's reach by field near the root.");
            w.WriteLine();
            w.WriteLine($"== Types by shallow bytes (top {topTypes}) ==");
            w.WriteLine($"{"MB",9} {"count",12} {"avg B",9}  type");
            foreach (var t in types.Values.Where(t => t.Count > 0).OrderByDescending(t => t.Bytes).Take(topTypes))
                w.WriteLine($"{Mb(t.Bytes),9} {t.Count,12:N0} {t.Bytes / t.Count,9:N0}  {TypeName(t.Type)}");

            var top = Enumerable.Range(0, buckets.Count).Where(i => buckets[i].Parent < 0 && buckets[i].InclCount > 0).OrderByDescending(i => buckets[i].InclBytes).Take(topRoots).ToList();
            var own = Enumerable.Range(0, buckets.Count).Where(i => buckets[i].Count > 0).OrderByDescending(i => buckets[i].Bytes).Take(topRoots).ToList();
            var topTypesOf = TopTypesPerBucket(new HashSet<int>(top.Concat(own)), 6);
            w.WriteLine();
            w.WriteLine($"== Top-level roots by bytes incl. sub-roots (top {topRoots}) ==");
            w.WriteLine($"{"MB",9} {"objects",12}  root  [top types incl. sub-roots]");
            foreach (int i in top)
                w.WriteLine($"{Mb(buckets[i].InclBytes),9} {buckets[i].InclCount,12:N0}  {buckets[i].Name}{Brackets(topTypesOf, i)}");
            w.WriteLine();
            w.WriteLine($"== All roots and sub-roots by own bytes (top {topRoots}) ==");
            w.WriteLine($"{"MB",9} {"objects",12}  root  [top types]");
            foreach (int i in own)
                w.WriteLine($"{Mb(buckets[i].Bytes),9} {buckets[i].Count,12:N0}  {buckets[i].Name}{Brackets(topTypesOf, i, false)}");
            w.WriteLine();
            w.WriteLine("== Root groups (assembly owning the static field / MonoBehaviour type) ==");
            foreach (var g in buckets.Where(r => r.Parent < 0 && r.InclCount > 0).GroupBy(r => r.Group).Select(g => new { g.Key, B = g.Sum(r => r.InclBytes), C = g.Sum(r => r.InclCount), N = g.Count() }).OrderByDescending(g => g.B).Take(topRoots))
                w.WriteLine($"{Mb(g.B),9} {g.C,12:N0}  {g.Key} ({g.N} roots)");
            if (specs.Count > 0)
            {
                w.WriteLine();
                w.WriteLine("== Retention paths (shortest path from a root; grouped by shape) ==");
                w.WriteLine("Each hop is field:Type of the object reached. A collection's storage array is folded away, so its elements");
                w.WriteLine("appear as []:Type ([key]:Type for dictionary keys). (hop)+ = the same hop repeated (linked chains).");
                w.WriteLine("MB = shallow bytes of the listed objects only.");
                foreach (var sp in specs)
                {
                    w.WriteLine();
                    if (sp.Count == 0)
                    {
                        w.WriteLine($"-- {sp.Text}: none reached");
                        continue;
                    }
                    w.WriteLine($"-- {sp.Text}: {sp.Count:N0} objects, {Mb(sp.Bytes)} MB, {sp.ShapeCount:N0} path shapes{(sp.Targets.Count < sp.Count ? $" (paths for the first {sp.Targets.Count:N0} objects)" : "")}");
                    w.WriteLine($"{"MB",9} {"objects",12}  path");
                    foreach (var kv in sp.Shapes)
                        w.WriteLine($"{Mb(kv.Value[1]),9} {kv.Value[0],12:N0}  {kv.Key}");
                }
            }
            if (notes.Count > 0)
            {
                w.WriteLine();
                w.WriteLine("== Notes / errors ==");
                foreach (var n in notes)
                    w.WriteLine(n);
            }
        }

        private bool inclusiveDone;

        internal void ComputeInclusive()
        {
            if (inclusiveDone)
                return;
            inclusiveDone = true;
            for (int i = buckets.Count - 1; i >= 0; i--)
            {
                var r = buckets[i];
                r.InclCount += r.Count;
                r.InclBytes += r.Bytes;
                if (r.Parent >= 0)
                {
                    buckets[r.Parent].InclCount += r.InclCount;
                    buckets[r.Parent].InclBytes += r.InclBytes;
                }
            }
        }

        // Per bucket: own (bucket, type) rows and rows including sub-buckets.
        private Dictionary<int, List<long[]>[]> TopTypesPerBucket(HashSet<int> wanted, int n)
        {
            var own = new Dictionary<int, Dictionary<int, long[]>>();
            var incl = new Dictionary<int, Dictionary<int, long[]>>();
            for (int k = 0; k < btN; k++)
            {
                int b = btBucket[k];
                if (wanted.Contains(b))
                    Add(own, b, k);
                for (int a = b; a >= 0; a = buckets[a].Parent)
                    if (wanted.Contains(a))
                        Add(incl, a, k);
            }
            var result = new Dictionary<int, List<long[]>[]>();
            foreach (int b in wanted)
                result[b] = new[] { Top(own, b), Top(incl, b) };
            return result;

            void Add(Dictionary<int, Dictionary<int, long[]>> map, int b, int k)
            {
                if (!map.TryGetValue(b, out var byType))
                    map[b] = byType = new Dictionary<int, long[]>();
                if (!byType.TryGetValue(btType[k], out var row))
                    byType[btType[k]] = row = new long[] { btType[k], 0, 0 };
                row[1] += btCount[k];
                row[2] += btBytes[k];
            }

            List<long[]> Top(Dictionary<int, Dictionary<int, long[]>> map, int b) =>
                map.TryGetValue(b, out var byType) ? byType.Values.OrderByDescending(r => r[2]).Take(n).ToList() : new List<long[]>();
        }

        private string Brackets(Dictionary<int, List<long[]>[]> topTypesOf, int b, bool inclusive = true)
        {
            if (!topTypesOf.TryGetValue(b, out var rows))
                return "";
            var list = rows[inclusive ? 1 : 0];
            long bytes = inclusive ? buckets[b].InclBytes : buckets[b].Bytes;
            if (bytes < 65536 || list.Count == 0)
                return "";
            return "  [" + string.Join("; ", list.Select(r => $"{TypeName(typeList[(int)r[0]].Type)} {Mb(r[2])} MB/{r[1]}").ToArray()) + "]";
        }

        internal static string TypeName(Type t)
        {
            try
            {
                if (t.IsArray) return TypeName(t.GetElementType()) + (t.GetArrayRank() == 1 && t == t.GetElementType().MakeArrayType() ? "[]" : "[" + new string(',', t.GetArrayRank() - 1) + "]");
                if (!t.IsGenericType) return t.FullName ?? t.Name;
                string n = t.GetGenericTypeDefinition().FullName ?? t.Name;
                int tick = n.IndexOf('`');
                if (tick >= 0) n = n.Substring(0, tick);
                return n + "<" + string.Join(",", t.GetGenericArguments().Select(TypeName).ToArray()) + ">";
            }
            catch { return t.Name; }
        }

        // ---- field ids ----

        private const int KindRoot = 0, KindField = 1, KindElement = 2;

        private sealed class FieldDesc
        {
            internal string Name;
            internal int Kind;
        }

        internal const int RootField = 0, ElemField = 1;
        private static readonly List<FieldDesc> fieldTable = new List<FieldDesc>
        {
            new FieldDesc { Name = "", Kind = KindRoot },
            new FieldDesc { Name = "[]", Kind = KindElement },
        };
        private static readonly Dictionary<string, int> fieldIds = new Dictionary<string, int> { { "0:", RootField }, { "2:[]", ElemField } };

        private static int FieldId(int kind, string name)
        {
            string key = kind + ":" + name;
            if (fieldIds.TryGetValue(key, out int id))
                return id;
            fieldTable.Add(new FieldDesc { Name = name, Kind = kind });
            fieldIds[key] = fieldTable.Count - 1;
            return fieldTable.Count - 1;
        }

        // ".a.b" for a reference field reached through embedded struct fields.
        private static int FieldPathId(FieldInfo[] path) => FieldId(KindField, "." + string.Join(".", path.Select(FieldName).ToArray()));

        // Element of a struct array: BCL entry structs (Dictionary/Hashtable entries, KeyValuePair, HashSet slots) render
        // as "[]" or "[key]"; user structs keep their field path ("[].a.b").
        private static int ElementPathId(Type elem, FieldInfo[] path)
        {
            if ((elem.Namespace ?? "").StartsWith("System", StringComparison.Ordinal))
            {
                string last = path[path.Length - 1].Name;
                return FieldId(KindElement, last == "key" || last == "Key" || last == "_key" || last == "m_key" ? "[key]" : "[]");
            }
            return FieldId(KindElement, "[]." + string.Join(".", path.Select(FieldName).ToArray()));
        }

        private static string FieldName(FieldInfo f)
        {
            string n = f.Name;
            return n.Length > 17 && n[0] == '<' && n.EndsWith(">k__BackingField", StringComparison.Ordinal) ? n.Substring(1, n.Length - 17) : n;
        }

        // ---- visitor compilation ----

        private static readonly Dictionary<Type, FieldInfo[][]> structPaths = new Dictionary<Type, FieldInfo[][]>();
        private static readonly Dictionary<Type, Visitor> visitorCache = new Dictionary<Type, Visitor>();
        private static readonly MethodInfo PushMethod = typeof(HeapWalker).GetMethod(nameof(Push), BindingFlags.Instance | BindingFlags.NonPublic);
        private static Visitor refArrayVisitor;

        internal static bool HoldsReferences(Type ft)
        {
            if (ft.IsPointer || ft.IsByRef || ft.IsGenericParameter)
                return false;
            if (!ft.IsValueType)
                return true;
            return StructPaths(ft).Length > 0;
        }

        private static IEnumerable<FieldInfo> InstanceFields(Type t)
        {
            for (var c = t; c != null && c != typeof(object); c = c.BaseType)
                foreach (var f in c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    yield return f;
        }

        // Reference-field paths through embedded structs: each path is zero or more struct fields then one reference field.
        private static FieldInfo[][] StructPaths(Type vt)
        {
            if (structPaths.TryGetValue(vt, out var p))
                return p;
            structPaths[vt] = new FieldInfo[0][]; // recursion guard
            var list = new List<FieldInfo[]>();
            if (!vt.IsPrimitive && !vt.IsEnum && !vt.IsPointer && vt != typeof(IntPtr) && vt != typeof(UIntPtr) && vt != typeof(TypedReference))
                foreach (var f in vt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    AddPaths(f, list);
            p = list.ToArray();
            structPaths[vt] = p;
            return p;
        }

        private static void AddPaths(FieldInfo f, List<FieldInfo[]> list)
        {
            var ft = f.FieldType;
            if (ft.IsPointer || ft.IsByRef || ft.IsGenericParameter)
                return;
            if (!ft.IsValueType)
            {
                list.Add(new[] { f });
                return;
            }
            foreach (var sub in StructPaths(ft))
            {
                var path = new FieldInfo[sub.Length + 1];
                path[0] = f;
                Array.Copy(sub, 0, path, 1, sub.Length);
                list.Add(path);
            }
        }

        // Emitted visitors never keep a managed pointer in a local (Mono rejects byref locals of Nullable<T> in dynamic
        // methods); the base address is recomputed per field path instead.
        private static Visitor Compile(Type t)
        {
            if (t == typeof(string) || t.IsPointer)
                return null;
            if (t.IsArray)
            {
                var elem = t.GetElementType();
                if (!HoldsReferences(elem))
                    return null;
                if (t != elem.MakeArrayType())
                    return MultiDimVisitor(elem.IsValueType);
                if (!elem.IsValueType)
                    return refArrayVisitor ?? (refArrayVisitor = CompileRefArray());
                return CompileStructArray(t, elem, StructPaths(elem));
            }
            var paths = new List<FieldInfo[]>();
            if (t.IsValueType)
                paths.AddRange(StructPaths(t));
            else
                foreach (var f in InstanceFields(t))
                    AddPaths(f, paths);
            if (paths.Count == 0)
                return null;
            var dm = NewMethod("visit_" + t.Name);
            var il = dm.GetILGenerator();
            var tmp = il.DeclareLocal(typeof(object));
            Action loadBase;
            if (t.IsValueType)
                loadBase = () =>
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Unbox, t);
                };
            else
                loadBase = () => il.Emit(OpCodes.Ldarg_0);
            foreach (var p in paths)
                EmitPush(il, tmp, loadBase, p, FieldPathId(p));
            il.Emit(OpCodes.Ret);
            return (Visitor)dm.CreateDelegate(typeof(Visitor));
        }

        private static DynamicMethod NewMethod(string name) =>
            new DynamicMethod(name, typeof(void), new[] { typeof(object), typeof(HeapWalker) }, typeof(HeapWalker).Module, true);

        // base; ldflda path[0..n-2]; ldfld path[n-1]; if non-null: walker.Push(address-of-ref, fieldId)
        private static void EmitPush(ILGenerator il, LocalBuilder tmp, Action loadBase, FieldInfo[] path, int fieldId)
        {
            var skip = il.DefineLabel();
            loadBase();
            for (int i = 0; i < path.Length - 1; i++)
                il.Emit(OpCodes.Ldflda, path[i]);
            il.Emit(OpCodes.Ldfld, path[path.Length - 1]);
            EmitPushTop(il, tmp, skip, fieldId);
            il.MarkLabel(skip);
        }

        private static void EmitPushTop(ILGenerator il, LocalBuilder tmp, Label skip, int fieldId)
        {
            il.Emit(OpCodes.Stloc, tmp);
            il.Emit(OpCodes.Ldloc, tmp);
            il.Emit(OpCodes.Brfalse, skip);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldloca, tmp);
            il.Emit(OpCodes.Conv_U);
            il.Emit(OpCodes.Ldind_I);
            il.Emit(OpCodes.Ldc_I4, fieldId);
            il.Emit(OpCodes.Call, PushMethod);
        }

        // Any single-dimension zero-based array of references (array covariance: castclass object[] succeeds).
        private static Visitor CompileRefArray()
        {
            var dm = NewMethod("visit_refarray");
            var il = dm.GetILGenerator();
            var arr = il.DeclareLocal(typeof(object[]));
            var n = il.DeclareLocal(typeof(int));
            var i = il.DeclareLocal(typeof(int));
            var tmp = il.DeclareLocal(typeof(object));
            var cond = il.DefineLabel();
            var body = il.DefineLabel();
            var next = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, typeof(object[]));
            il.Emit(OpCodes.Stloc, arr);
            il.Emit(OpCodes.Ldloc, arr);
            il.Emit(OpCodes.Ldlen);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Stloc, n);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, i);
            il.Emit(OpCodes.Br, cond);
            il.MarkLabel(body);
            il.Emit(OpCodes.Ldloc, arr);
            il.Emit(OpCodes.Ldloc, i);
            il.Emit(OpCodes.Ldelem_Ref);
            EmitPushTop(il, tmp, next, ElemField);
            il.MarkLabel(next);
            il.Emit(OpCodes.Ldloc, i);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, i);
            il.MarkLabel(cond);
            il.Emit(OpCodes.Ldloc, i);
            il.Emit(OpCodes.Ldloc, n);
            il.Emit(OpCodes.Blt, body);
            il.Emit(OpCodes.Ret);
            return (Visitor)dm.CreateDelegate(typeof(Visitor));
        }

        // T[] with T a struct holding references (e.g. Dictionary entries): loop; per path ldelema then the struct path.
        private static Visitor CompileStructArray(Type arrayType, Type elem, FieldInfo[][] paths)
        {
            var dm = NewMethod("visit_" + elem.Name + "_array");
            var il = dm.GetILGenerator();
            var arr = il.DeclareLocal(arrayType);
            var n = il.DeclareLocal(typeof(int));
            var i = il.DeclareLocal(typeof(int));
            var tmp = il.DeclareLocal(typeof(object));
            var cond = il.DefineLabel();
            var body = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, arrayType);
            il.Emit(OpCodes.Stloc, arr);
            il.Emit(OpCodes.Ldloc, arr);
            il.Emit(OpCodes.Ldlen);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Stloc, n);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, i);
            il.Emit(OpCodes.Br, cond);
            il.MarkLabel(body);
            foreach (var path in paths)
                EmitPush(il, tmp, () =>
                {
                    il.Emit(OpCodes.Ldloc, arr);
                    il.Emit(OpCodes.Ldloc, i);
                    il.Emit(OpCodes.Ldelema, elem);
                }, path, ElementPathId(elem, path));
            il.Emit(OpCodes.Ldloc, i);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, i);
            il.MarkLabel(cond);
            il.Emit(OpCodes.Ldloc, i);
            il.Emit(OpCodes.Ldloc, n);
            il.Emit(OpCodes.Blt, body);
            il.Emit(OpCodes.Ret);
            return (Visitor)dm.CreateDelegate(typeof(Visitor));
        }

        // Rare: multi-dimensional or non-zero-based arrays. Elements are enumerated (structs arrive boxed).
        private static Visitor MultiDimVisitor(bool structs) => (o, w) =>
        {
            foreach (var e in (Array)o)
            {
                if (e == null) continue;
                if (structs) w.VisitBoxCopy(e);
                else w.PushObject(e, ElemField);
            }
        };

        // ---- raw memory helpers (DynamicMethod, so PerfeBT needs no unsafe code) ----

        private delegate IntPtr AddrFn(object o);
        private delegate IntPtr PtrFn(IntPtr p);
        private delegate object RefFn(IntPtr p);

        // ldarga 0; conv.u; ldind.i: the reference's bits, i.e. the object's address.
        private static readonly AddrFn AddrOfFn = Emit<AddrFn>("addr_of", typeof(IntPtr), typeof(object), il =>
        {
            il.Emit(OpCodes.Ldarga_S, (byte)0);
            il.Emit(OpCodes.Conv_U);
            il.Emit(OpCodes.Ldind_I);
        });

        // The inverse: reinterprets an address as a reference (only valid while objects cannot move or be freed).
        private static readonly RefFn ObjAtFn = Emit<RefFn>("obj_at", typeof(object), typeof(IntPtr), il =>
        {
            il.Emit(OpCodes.Ldarga_S, (byte)0);
            il.Emit(OpCodes.Conv_U);
            il.Emit(OpCodes.Ldind_Ref);
        });

        private static readonly PtrFn ReadPtrFn = Emit<PtrFn>("read_ptr", typeof(IntPtr), typeof(IntPtr), il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldind_I);
        });

        private static readonly RefFn ReadRefFn = Emit<RefFn>("read_ref", typeof(object), typeof(IntPtr), il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldind_Ref);
        });

        private static readonly Dictionary<Type, RefFn> boxers = new Dictionary<Type, RefFn>();

        internal static IntPtr AddrOf(object o) => AddrOfFn(o);
        internal static object ObjAt(IntPtr address) => ObjAtFn(address);
        internal static IntPtr ReadPtr(IntPtr p) => ReadPtrFn(p);
        internal static int ReadInt32(IntPtr p) => (int)(long)ReadPtrFn(p);
        internal static object ReadRef(IntPtr slot) => ReadRefFn(slot);

        // Boxed copy of a struct stored at address (ldobj T; box T).
        internal static object BoxFromAddress(Type t, IntPtr p)
        {
            if (!boxers.TryGetValue(t, out var fn))
            {
                fn = Emit<RefFn>("box_" + t.Name, typeof(object), typeof(IntPtr), il =>
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldobj, t);
                    il.Emit(OpCodes.Box, t);
                });
                boxers[t] = fn;
            }
            return fn(p);
        }

        private static T Emit<T>(string name, Type ret, Type arg, Action<ILGenerator> body) where T : class
        {
            var dm = new DynamicMethod(name, ret, new[] { arg }, typeof(HeapWalker).Module, true);
            var il = dm.GetILGenerator();
            body(il);
            il.Emit(OpCodes.Ret);
            return dm.CreateDelegate(typeof(T)) as T;
        }
    }

    // Open-addressing long -> T map keyed by addresses or address chunks; long.MinValue marks an empty slot.
    internal sealed class LongMap<T>
    {
        private const long Empty = long.MinValue;
        private long[] keys;
        private T[] values;
        private int mask;
        internal int Count;

        internal LongMap(int capacity = 1024)
        {
            keys = new long[capacity];
            values = new T[capacity];
            mask = capacity - 1;
            for (int i = 0; i < keys.Length; i++) keys[i] = Empty;
        }

        private static int Hash(long k) => (int)((ulong)(k * -7046029254386353131L) >> 32);

        // The empty marker is never a key: looking it up would match the first empty slot.
        internal bool TryGetValue(long k, out T v)
        {
            if (k == Empty) { v = default; return false; }
            int i = Hash(k) & mask;
            while (true)
            {
                long kk = keys[i];
                if (kk == k) { v = values[i]; return true; }
                if (kk == Empty) { v = default; return false; }
                i = (i + 1) & mask;
            }
        }

        internal void Set(long k, T v)
        {
            if (Count * 2 >= keys.Length)
                Grow();
            int i = Hash(k) & mask;
            while (keys[i] != Empty && keys[i] != k)
                i = (i + 1) & mask;
            if (keys[i] == Empty) Count++;
            keys[i] = k;
            values[i] = v;
        }

        private void Grow()
        {
            var ok = keys;
            var ov = values;
            keys = new long[ok.Length * 2];
            values = new T[ok.Length * 2];
            mask = keys.Length - 1;
            for (int i = 0; i < keys.Length; i++) keys[i] = Empty;
            Count = 0;
            for (int i = 0; i < ok.Length; i++)
                if (ok[i] != Empty)
                    Set(ok[i], ov[i]);
        }
    }
}
