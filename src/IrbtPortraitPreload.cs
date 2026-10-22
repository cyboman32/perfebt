using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using BattleTech;
using BattleTech.Save;
using HarmonyLib;
using UnityEngine;

namespace PerfeBT
{
    // IRBTModUtils' CombatGameState._Init postfix (us.frostraptor.modUtils.CombatGameState__Init.Postfix) does
    // `SharedState.Combat = combat; if (ModState.Portraits.Count == 0) { load each Dialogue.Portraits PNG via
    // ImageUtils.LoadSprite and Add it }`. That is 19 PNGs (13 of them 2.6-4 MB), ~2.4 s of main-thread decoding
    // on the first mission of the session. ModState.Portraits is never cleared, so later missions skip the loop.
    //
    // This runs IRBT's own postfix earlier, while the save-load screen is up: postfix on
    // SimGameState.Rehydrate(GameInstanceSave), whose only caller is SimGameState.HandleSaveHydrate
    // (<- _OnHeadlessComplete <- OnStateBitsChanged, i.e. the sim init state machine during save load).
    // It never runs at the main menu or for a new career. The argument passed is the current
    // SharedState.Combat, so the postfix's only other effect (`stsfld SharedState::Combat`) rewrites the same
    // value. When the real combat _Init arrives, IRBT's postfix sets SharedState.Combat and sees Count != 0, so
    // it skips its loop, which is the same as on every later mission in vanilla.
    internal static class IrbtPortraitPreload
    {
        private static int mainThreadId;
        private static Action<CombatGameState> irbtInitPostfix;   // us.frostraptor.modUtils.CombatGameState__Init.Postfix
        private static FieldInfo sharedCombat;                     // IRBTModUtils.SharedState.Combat
        private static FieldInfo portraitsField;                   // IRBTModUtils.ModState.Portraits

        private static T Req<T>(T x, string what) where T : class
            => x ?? throw new MissingMemberException("PerfeBT.IrbtPortraitPreload: missing " + what);

        internal static void Apply(Harmony harmony)
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;

            var initPostfix = Req(AccessTools.Method("us.frostraptor.modUtils.CombatGameState__Init:Postfix", new[] { typeof(CombatGameState) }),
                "us.frostraptor.modUtils.CombatGameState__Init.Postfix(CombatGameState)");
            irbtInitPostfix = (Action<CombatGameState>)Delegate.CreateDelegate(typeof(Action<CombatGameState>), initPostfix);
            sharedCombat = Req(AccessTools.Field(Req(AccessTools.TypeByName("IRBTModUtils.SharedState"), "IRBTModUtils.SharedState"), "Combat"),
                "IRBTModUtils.SharedState.Combat");
            portraitsField = Req(AccessTools.Field(Req(AccessTools.TypeByName("IRBTModUtils.ModState"), "IRBTModUtils.ModState"), "Portraits"),
                "IRBTModUtils.ModState.Portraits");
            if (!sharedCombat.IsStatic || sharedCombat.FieldType != typeof(CombatGameState))
                throw new MissingMemberException("PerfeBT.IrbtPortraitPreload: SharedState.Combat has unexpected shape");
            if (!portraitsField.IsStatic || portraitsField.FieldType != typeof(Dictionary<string, Sprite>))
                throw new MissingMemberException("PerfeBT.IrbtPortraitPreload: ModState.Portraits has unexpected shape");

            var rehydrate = Req(AccessTools.Method(typeof(SimGameState), "Rehydrate", new[] { typeof(GameInstanceSave) }),
                "SimGameState.Rehydrate(GameInstanceSave)");
            harmony.Patch(rehydrate, postfix: new HarmonyMethod(AccessTools.Method(typeof(IrbtPortraitPreload), nameof(RehydratePostfix)), Priority.Last));
            Log.Write("IrbtPortraitPreload: IRBT portrait preload on SimGameState.Rehydrate applied");
        }

        private static void RehydratePostfix()
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId) return;   // Texture2D creation needs the main thread
            var portraits = (Dictionary<string, Sprite>)portraitsField.GetValue(null);
            if (portraits == null || portraits.Count != 0) return;              // IRBT's own condition; null -> leave its NRE to combat init

            var sw = Stopwatch.StartNew();
            try
            {
                // IRBT's own code path. SharedState.Combat is rewritten with its current value (no change).
                irbtInitPostfix((CombatGameState)sharedCombat.GetValue(null));
                Log.Write($"IrbtPortraitPreload: preloaded {portraits.Count} portraits in {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception e)
            {
                // The original would throw out of CombatGameState._Init at the same point. Restore the empty
                // dictionary so IRBT's postfix repeats the identical load (and failure) there.
                portraits.Clear();
                Log.Write($"IrbtPortraitPreload: preload failed after {sw.ElapsedMilliseconds} ms, deferred to IRBT at combat init: {e}");
            }
        }
    }
}
