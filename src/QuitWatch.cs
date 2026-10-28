using System;
using System.Diagnostics;
using HarmonyLib;
using BattleTech.Save.Core;
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
            harmony.Patch(AccessTools.Method(typeof(ThreadedSaveManagerRequest), nameof(ThreadedSaveManagerRequest.StartTask)),
                postfix: new HarmonyMethod(typeof(QuitWatch), nameof(StartPostfix)));
            harmony.Patch(AccessTools.Method(typeof(ThreadedSaveManagerRequest), nameof(ThreadedSaveManagerRequest.CompleteTask)),
                postfix: new HarmonyMethod(typeof(QuitWatch), nameof(CompletePostfix)));
            harmony.Patch(AccessTools.Method(typeof(BattleTech.UnityGameInstance), nameof(BattleTech.UnityGameInstance.ShutdownGame)),
                prefix: new HarmonyMethod(typeof(QuitWatch), nameof(ShutdownPrefix)));
            Application.quitting += () => Log.Write($"[quit] Unity quitting event, {Since()}");
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Log.Write($"[quit] process exit, {Since()}");
        }

        private static string Since() => sinceShutdown.IsRunning ? $"{sinceShutdown.ElapsedMilliseconds} ms after ShutdownGame" : "no ShutdownGame call";

        private static void StartPostfix(ThreadedSaveManagerRequest __instance) =>
            Log.Write($"[quit] save request started: {__instance.GetType().Name}");

        private static void CompletePostfix(ThreadedSaveManagerRequest __instance) =>
            Log.Write($"[quit] save request completed: {__instance.GetType().Name}");

        private static void ShutdownPrefix()
        {
            sinceShutdown.Start();
            Log.Write("[quit] ShutdownGame (Application.Quit)");
        }
    }
}
