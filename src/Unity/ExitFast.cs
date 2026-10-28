using System;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using BattleTech.Save.Core;

namespace PerfeBT
{
    // Quit: once the managed shutdown is complete (Unity quitting event, scene objects destroyed, Steam shut down,
    // every AppDomain.ProcessExit handler registered before ours has run), end the process instead of letting
    // Unity and Mono tear down a 30-45 GB heap natively (several seconds, mostly paging in trimmed memory).
    // PlayerPrefs are saved first. Skipped while a background save request is still running.
    internal static class ExitFast
    {
        private static int pendingSaves;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        internal static void Apply(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(ThreadedSaveManagerRequest), nameof(ThreadedSaveManagerRequest.StartTask))
                          ?? throw new MissingMethodException("StartTask"),
                postfix: new HarmonyMethod(typeof(ExitFast), nameof(StartPostfix)));
            harmony.Patch(AccessTools.Method(typeof(ThreadedSaveManagerRequest), nameof(ThreadedSaveManagerRequest.CompleteTask))
                          ?? throw new MissingMethodException("CompleteTask"),
                postfix: new HarmonyMethod(typeof(ExitFast), nameof(CompletePostfix)));
            Application.quitting += OnQuitting;
        }

        private static void StartPostfix() => Interlocked.Increment(ref pendingSaves);

        private static void CompletePostfix() => Interlocked.Decrement(ref pendingSaves);

        private static void OnQuitting()
        {
            try
            {
                PlayerPrefs.Save();
            }
            catch (Exception e)
            {
                Log.Write("[quit] ExitFast: PlayerPrefs.Save failed, normal teardown: " + e.Message);
                return;
            }
            // Registered now, after every mod has loaded, so the other ProcessExit handlers run before this one.
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            var pending = Volatile.Read(ref pendingSaves);
            if (pending > 0)
            {
                Log.Write($"[quit] ExitFast: {pending} save request(s) still running, normal teardown");
                return;
            }
            Log.Write("[quit] ExitFast: managed shutdown complete, ending the process");
            TerminateProcess(GetCurrentProcess(), 0);
        }
    }
}
