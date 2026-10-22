using System;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace PerfeBT
{
    // Diagnostic: timeline of quitting the game ([quit] lines): background save writes starting and finishing,
    // the game's ShutdownGame call, Unity's quitting event and the managed process exit. Read-only.
    internal static class QuitWatch
    {
        private static readonly Stopwatch sinceShutdown = new Stopwatch();

        internal static void Apply(Harmony harmony)
        {
            var request = AccessTools.TypeByName("BattleTech.Save.Core.ThreadedSaveManagerRequest")
                          ?? throw new TypeLoadException("ThreadedSaveManagerRequest not found");
            harmony.Patch(AccessTools.Method(request, "StartTask") ?? throw new MissingMethodException("StartTask"),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(QuitWatch), nameof(StartPostfix))));
            harmony.Patch(AccessTools.Method(request, "CompleteTask") ?? throw new MissingMethodException("CompleteTask"),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(QuitWatch), nameof(CompletePostfix))));
            harmony.Patch(AccessTools.Method(typeof(BattleTech.UnityGameInstance), "ShutdownGame"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(QuitWatch), nameof(ShutdownPrefix))));
            Application.quitting += () => Log.Write($"[quit] Unity quitting event, {Since()}");
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Log.Write($"[quit] process exit, {Since()}");
        }

        private static string Since() => sinceShutdown.IsRunning ? $"{sinceShutdown.ElapsedMilliseconds} ms after ShutdownGame" : "no ShutdownGame call";

        private static void StartPostfix(object __instance) =>
            Log.Write($"[quit] save request started: {__instance.GetType().Name}");

        private static void CompletePostfix(object __instance) =>
            Log.Write($"[quit] save request completed: {__instance.GetType().Name}");

        private static void ShutdownPrefix()
        {
            sinceShutdown.Start();
            Log.Write("[quit] ShutdownGame (Application.Quit)");
        }
    }
}
