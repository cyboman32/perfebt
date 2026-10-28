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
            if (!ModAssemblies.Loaded("CustomMaps"))
            {
                Log.Write("UnloadGate: CustomMaps not loaded, site CustomMaps.LoadCustom not patched");
                return;
            }
            // A changed CustomMaps must only drop this site, not the whole UnloadGate fix that calls Register.
            try
            {
                Fixes.Precompile(typeof(CustomMapsSite));
                CustomMapsSite.Register();
            }
            catch (Exception e)
            {
                Log.Write("UnloadGate: site CustomMaps.LoadCustom not patched: " + e.Message);
            }
        }

        // Uses CustomMaps members directly; only reached (and precompiled) when its assembly is loaded.
        private static class CustomMapsSite
        {
            internal static void Register()
            {
                var loadCustom = AccessTools.Method(typeof(CustomMaps.Patches.LevelLoader_Start), nameof(CustomMaps.Patches.LevelLoader_Start.LoadCustom),
                    new[] { typeof(LevelLoader) });
                // The coroutine's state machine is compiler-generated (no C# name): found through Harmony.
                var moveNext = AccessTools.EnumeratorMoveNext(loadCustom)
                               ?? throw new MissingMemberException("PerfeBT.UnloadGate: missing CustomMaps LoadCustom MoveNext");
                UnloadGate.AddSite(moveNext, "CustomMaps.LoadCustom", 2, loadsEmpty: true);
            }
        }
    }
}
