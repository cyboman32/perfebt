using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;

namespace PerfeBT
{
    internal static class Instrumenter
    {
        private const BindingFlags AllDeclared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        private static HarmonyMethod prefix;
        private static HarmonyMethod finalizer;
        private static List<Regex> typeRx, lateTypeRx, initTypeRx, asmRx, excludeRx;
        private static bool allowSafeCctors;
        private static readonly HashSet<Type> loggedCctorTypes = new HashSet<Type>();

        internal static void Run(Harmony harmony, Settings s, string pass)
        {
            var sw = Stopwatch.StartNew();
            prefix ??= new HarmonyMethod(typeof(Hooks).GetMethod(nameof(Hooks.Prefix))) { priority = Priority.First };
            finalizer ??= new HarmonyMethod(typeof(Hooks).GetMethod(nameof(Hooks.Finalizer))) { priority = Priority.Last };
            allowSafeCctors = s.InstrumentSafeCctorTypes;
            typeRx ??= s.TypePatterns.Select(p => new Regex(p, RegexOptions.Compiled)).ToList();
            lateTypeRx ??= s.LateTypePatterns.Select(p => new Regex(p, RegexOptions.Compiled)).ToList();
            initTypeRx ??= s.InitTypePatterns.Select(p => new Regex(p, RegexOptions.Compiled)).ToList();
            asmRx ??= s.AssemblyPatterns.Select(p => new Regex(p, RegexOptions.Compiled)).ToList();
            excludeRx ??= s.ExcludePatterns.Select(p => new Regex(p, RegexOptions.Compiled)).ToList();

            var candidates = new List<(MethodBase method, string label, string owner, int minIl)>();
            int early;
            if (pass == "init")
            {
                // PerfeBT's Init: only the startup types (ModTek's manifest processing runs after every mod's Init).
                CollectTypeMethods(s, initTypeRx, candidates);
                early = candidates.Count;
            }
            else
            {
                if (s.InstrumentHarmonyPatches)
                    CollectPatchMethods(harmony.Id, s, candidates);
                CollectTypeMethods(s, typeRx, candidates);
                early = candidates.Count;
                // The rescan runs at the main menu: the game is initialized and these types' static ctors have run, so
                // patching them cannot trigger an early cctor.
                if (pass == "rescan")
                    CollectTypeMethods(s, lateTypeRx, candidates);
            }
            int ok = 0, failed = 0, skipped = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                var (method, label, owner, minIl) = candidates[i];
                if (Prof.IsRegistered(method) || !Eligible(method, minIl, checkCctor: i < early) || Excluded(label))
                {
                    skipped++;
                    continue;
                }
                Prof.Register(method, label, owner);
                try
                {
                    harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                    ok++;
                }
                catch (Exception e)
                {
                    failed++;
                    Log.Write($"[instrument] failed {label}: {e.GetType().Name}: {e.Message}");
                }
            }

            Log.Write($"[instrument:{pass}] patched={ok} failed={failed} skipped={skipped} total={Prof.Count} in {sw.ElapsedMilliseconds} ms");
        }

        private static void CollectPatchMethods(string ownId, Settings s, List<(MethodBase, string, string, int)> into)
        {
            foreach (var original in Harmony.GetAllPatchedMethods().ToList())
            {
                var info = Harmony.GetPatchInfo(original);
                if (info == null)
                    continue;
                string target = Describe(original);
                foreach (var p in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                {
                    if (p.owner == ownId || p.PatchMethod == null)
                        continue;
                    var m = p.PatchMethod;
                    string kind = info.Prefixes.Contains(p) ? "prefix" : info.Postfixes.Contains(p) ? "postfix" : "finalizer";
                    string label = $"{Describe(m)}  [{kind} -> {target}]";
                    into.Add((m, label, m.DeclaringType?.Assembly.GetName().Name ?? "?", s.MinPatchIlBytes));
                }
            }
        }

        // Early patterns match a type's full name in Assembly-CSharp (AssemblyPatterns add whole assemblies).
        // Late patterns match "AssemblyName:FullTypeName" and must start with "^AssemblyName:" (used to skip assemblies).
        private static void CollectTypeMethods(Settings s, List<Regex> patterns, List<(MethodBase, string, string, int)> into)
        {
            bool late = patterns == lateTypeRx || patterns == initTypeRx;
            if (patterns.Count == 0 && late)
                return;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic)
                    continue;
                string asmName = asm.GetName().Name;
                bool wholeAssembly = !late && asmRx.Any(r => r.IsMatch(asmName));
                bool gameAssembly = asmName == "Assembly-CSharp";
                if (!wholeAssembly && !late && !(gameAssembly && patterns.Count > 0))
                    continue;
                if (late && !patterns.Any(r => r.ToString().StartsWith("^" + asmName + ":")))
                    continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

                foreach (var t in types)
                {
                    if (t.ContainsGenericParameters || t.FullName == null)
                        continue;
                    string key = late ? asmName + ":" + t.FullName : t.FullName;
                    if (!wholeAssembly && !patterns.Any(r => r.IsMatch(key)))
                        continue;
                    foreach (var m in t.GetMethods(AllDeclared))
                        into.Add((m, Describe(m), asmName, s.MinIlBytes));
                }
            }
        }

        private static bool Eligible(MethodBase m, int minIl, bool checkCctor)
        {
            if (m.IsAbstract || m.ContainsGenericParameters || m.DeclaringType == null || m.DeclaringType.Assembly == typeof(Instrumenter).Assembly)
                return false;
            if (m.DeclaringType.Assembly.IsDynamic)
                return false;
            if ((m.MethodImplementationFlags & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime | MethodImplAttributes.Native)) != 0)
                return false;
            try
            {
                // Patching on Mono can run the declaring type's static constructor early (before UnityGameInstance
                // exists). A cctor that throws then poisons the type for the whole session
                // (e.g. SaveRejectListManager -> TypeInitializationException -> save loading breaks).
                // Types with a cctor are skipped unless InstrumentSafeCctorTypes is on and the cctor provably
                // touches nothing but the BCL and loggers.
                for (var t = m.DeclaringType; checkCctor && t != null; t = t.DeclaringType)
                {
                    if (t.TypeInitializer == null)
                        continue;
                    if (!allowSafeCctors || !CctorScan.IsSideEffectFree(t))
                        return false;
                    if (loggedCctorTypes.Add(t))
                        Log.Write($"[instrument] cctor type allowed: {t.FullName}");
                }
                var body = m.GetMethodBody();
                var il = body?.GetILAsByteArray();
                return il != null && il.Length >= minIl;
            }
            catch
            {
                return false;
            }
        }

        private static bool Excluded(string label) => excludeRx.Any(r => r.IsMatch(label));

        internal static string Describe(MethodBase m) => $"{m.DeclaringType?.FullName}::{m.Name}";
    }
}
