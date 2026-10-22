using System;
using BattleTech;
using HarmonyLib;

namespace PerfeBT
{
    // CustomMaps replaces LevelLoader.Load with its own LoadCustom coroutine, which has the same two
    // Resources.UnloadUnusedAssets calls and the same LoadSceneAsync("Empty"). Registered with UnloadGate so its
    // skip/defer sites and EmptySceneAdditive cover it like the game's coroutine.
    internal static class CustomMapsUnloadSites
    {
        internal static void Register()
        {
            var customMaps = AccessTools.TypeByName("CustomMaps.Patches.LevelLoader_Start");
            if (customMaps == null)
            {
                Log.Write("UnloadGate: CustomMaps not loaded, site CustomMaps.LoadCustom not patched");
                return;
            }
            var loadCustom = AccessTools.Method(customMaps, "LoadCustom", new[] { typeof(LevelLoader) })
                             ?? throw new MissingMemberException("PerfeBT.UnloadGate: missing CustomMaps.Patches.LevelLoader_Start.LoadCustom(LevelLoader)");
            var moveNext = AccessTools.EnumeratorMoveNext(loadCustom)
                           ?? throw new MissingMemberException("PerfeBT.UnloadGate: missing CustomMaps LoadCustom MoveNext");
            UnloadGate.AddSite(moveNext, "CustomMaps.LoadCustom", 2, loadsEmpty: true);
        }
    }
}
