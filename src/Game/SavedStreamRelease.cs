using BattleTech.Save.SaveGameStructure;
using HarmonyLib;

namespace PerfeBT
{
    // Memory only. Every save keeps its whole reference stream (GameInstanceSave.referencesDataStream, ~293 MB in a
    // RogueTech career) alive in the new slot: heap census 2026-10-28 14:05 found two of them (586 MB) through
    // SaveGameStructure.instances and MostRecentCareerSave.
    //
    // The stream is written by GameInstanceSave.PreSerialization and read only by PostDeserialization of a loaded save.
    // After SaveGameStructure.OnBlockSaved the file is written; the slot's GameInstanceSave is then read only for
    // metadata (SlotModel getters), loading a slot reads the file again (Load -> CloudLoadBlock(FileID)), and slots
    // rebuilt by Refresh carry metadata only. So the saved slot's stream is dropped there.
    internal static class SavedStreamRelease
    {
        private static long released, bytes;

        internal static void Apply(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(SaveGameStructure), nameof(SaveGameStructure.OnBlockSaved)),
                prefix: new HarmonyMethod(typeof(SavedStreamRelease), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(SavedStreamRelease), nameof(Postfix)));
        }

        private static void Prefix(SaveGameStructure __instance, out Slot __state) => __state = __instance.pendingSaveSlot;

        // Only when OnBlockSaved consumed the slot (another request's message leaves pendingSaveSlot in place).
        private static void Postfix(SaveGameStructure __instance, Slot __state)
        {
            var save = __state?.gameInstanceSave;
            if (save == null || ReferenceEquals(__instance.pendingSaveSlot, __state) || save.referencesDataStream == null)
                return;
            released++;
            bytes += save.referencesDataStream.Length;
            Log.Write($"[savestream] saved slot's reference stream released ({save.referencesDataStream.Length / 1048576.0:F0} MB); " +
                      $"{released} saves, {bytes / 1048576.0:F0} MB in total");
            save.referencesDataStream = null;
        }
    }
}
