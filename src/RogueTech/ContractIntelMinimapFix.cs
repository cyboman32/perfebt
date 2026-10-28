using System;
using System.Reflection;
using BattleTech;
using BattleTech.Data;
using HarmonyLib;
using UnityEngine;

namespace PerfeBT
{
    // CustomUnits.LanceContractIntelWidget.Init assigns GetIntelMinimapSprite(contract) to the minimap image and,
    // a few instructions later, deactivates that image unless CUSettings.IntelShowMiniMap is true or the company
    // stat CUSettings.IntelCompanyStatShowMiniMap is true (RogueTech: false / granted by an Argo upgrade).
    // On a cache miss GetIntelMinimapSprite runs MapMetaData.LoadMapMetaData (262k terrain cells, ~786k objects
    // with CAC) and builds a 512x512 texture — up to 2.4 s observed — only for it to be hidden.
    // This prefix evaluates Init's visibility predicate first and, when the minimap will be hidden, returns null
    // without building. Every other case (cache hit, shown, unknown patches, combat running) runs the original.
    internal static class ContractIntelMinimapFix
    {
        static MethodBase[] guardedMethods;
        static bool cacLoaded;

        static int gate; // 0 = unchecked, 1 = ok, -1 = disabled (main thread only)

        // Patches whose effects on the skipped LoadMapMetaData path were analysed (all are skippable outside combat):
        static readonly string[] AllowedPatchOwners =
        {
            "CustomMaps.Patches.MapMetaData_LoadMapMetaData",   // picks file, restores contract.mapName in postfix
            "CustomAmmoCategoriesPatches.MapMetaData_Load",     // cell ctor swap; postfix no-op w/o Terrain.activeTerrain
            "CustomAmmoCategoriesPatches.MapMetaData_LoadData", // DataManager design-mask load request (cache warm-up)
            "EnvironmentalDesignMasks.MapMetaData_Load",        // returns immediately when Combat == null
        };

        // Uses CustomUnits (and, when loaded, CustomAmmoCategories) members directly: the bodies are precompiled first.
        internal static void Apply(Harmony harmony)
        {
            cacLoaded = ModAssemblies.Loaded("CustomAmmoCategories");
            Fixes.Precompile(typeof(ContractIntelMinimapFix));
            if (cacLoaded)
                Fixes.Precompile(typeof(CacPostfixes));
            var target = Req(AccessTools.Method(typeof(CustomUnits.LanceContractIntelWidget), nameof(CustomUnits.LanceContractIntelWidget.GetIntelMinimapSprite),
                new[] { typeof(Contract) }), "GetIntelMinimapSprite(Contract)");

            var ss = typeof(HBS.Util.SerializationStream);
            guardedMethods = new MethodBase[]
            {
                Req(AccessTools.Method(typeof(MapMetaData), nameof(MapMetaData.LoadMapMetaData), new[] { typeof(Contract), typeof(DataManager) }), "MapMetaData.LoadMapMetaData"),
                Req(AccessTools.Method(typeof(MapMetaData), nameof(MapMetaData.Load), new[] { ss }), "MapMetaData.Load(ss)"),
                Req(AccessTools.Method(typeof(MapMetaData), nameof(MapMetaData.Load), new[] { ss, typeof(DataManager) }), "MapMetaData.Load(ss,dm)"),
                Req(AccessTools.Method(typeof(MapMetaData), nameof(MapMetaData.RefreshIsWaterIce)), "MapMetaData.RefreshIsWaterIce"),
                Req(AccessTools.Method(typeof(MapMetaData), nameof(MapMetaData.GetPriorityTerrainMaskFlags), new[] { typeof(TerrainMaskFlags) }), "MapMetaData.GetPriorityTerrainMaskFlags"),
                Req(AccessTools.Method(typeof(MapTerrainDataCell), nameof(MapTerrainDataCell.Load), new[] { ss }), "MapTerrainDataCell.Load"),
                Req(AccessTools.Method(typeof(EncounterLayerIdentifier), nameof(EncounterLayerIdentifier.Load), new[] { ss }), "EncounterLayerIdentifier.Load"),
                target,
            };

            harmony.Patch(target, prefix: new HarmonyMethod(typeof(ContractIntelMinimapFix), nameof(Prefix)) { priority = Priority.First });
        }

        static T Req<T>(T v, string what) where T : class
            => v ?? throw new MissingMemberException("ContractIntelMinimapFix: " + what + " not found");

        // Evaluated once, lazily on first use (after every mod has applied its patches).
        static bool GateOk()
        {
            if (gate != 0) return gate > 0;
            gate = -1;
            try
            {
                foreach (var m in guardedMethods)
                {
                    var info = Harmony.GetPatchInfo(m);
                    if (info == null) continue;
                    foreach (var list in new[] { info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers })
                        foreach (var p in list)
                        {
                            var owner = p.PatchMethod.DeclaringType;
                            if (owner == typeof(ContractIntelMinimapFix)) continue;
                            if (owner?.Namespace != null && owner.Namespace.StartsWith("PerfeBT", StringComparison.Ordinal)) continue; // profiler hooks
                            if (owner == null || Array.IndexOf(AllowedPatchOwners, owner.FullName) < 0)
                            {
                                Log.Write("[ContractIntelMinimapFix] disabled: unknown patch " + owner?.FullName + "." + p.PatchMethod.Name + " on " + m.DeclaringType?.Name + "." + m.Name);
                                return false;
                            }
                        }
                }
                // CAC's MapMetaData_Load postfix invokes CACMain.Core.MapMetadata_Load_Postfixes; require none registered.
                var cacCount = cacLoaded ? CacPostfixes.Count() : 0;
                if (cacCount != 0)
                {
                    Log.Write("[ContractIntelMinimapFix] disabled: CACMain.Core.MapMetadata_Load_Postfixes has " + cacCount + " entries");
                    return false;
                }
                gate = 1;
                Log.Write("[ContractIntelMinimapFix] active");
                return true;
            }
            catch (Exception e)
            {
                Log.Write("[ContractIntelMinimapFix] disabled: " + e);
                return false;
            }
        }

        // Returns true => original GetIntelMinimapSprite runs unchanged.
        static bool Prefix(Contract __0, ref Sprite __result)
        {
            try
            {
                var contract = __0;
                if (contract == null || contract.mapName == null) return true;
                var cache = CustomUnits.LanceContractIntelWidget.intelMinimaps;
                if (cache == null || cache.ContainsKey(contract.mapName)) return true; // original cache-hit path
                if (!GateOk()) return true;
                var game = UnityGameInstance.BattleTechGame;
                if (game == null || game.Combat != null) return true;                  // EDM postfix would act
                if (UnityEngine.Terrain.activeTerrain != null) return true;        // CAC postfix would act
                if (!MiniMapWillBeHidden(game)) return true;

                // Same first log line as the original cache-miss path.
                var lf = CustomUnits.Log.M;
                if (lf != null) lf.TWL(0, "GetIntelMinimapSprite:" + contract.mapName, false);
                __result = null;
                return false;
            }
            catch (Exception e)
            {
                Log.Write("[ContractIntelMinimapFix] prefix error, running original: " + e.Message);
                return true;
            }
        }

        // Mirrors LanceContractIntelWidget.Init IL_04ef..IL_05c3 (evaluated there right after the sprite assignment).
        // Any exception => treated as "not hidden" (original runs).
        static bool MiniMapWillBeHidden(GameInstance game)
        {
            try
            {
                var settings = CustomUnits.Core.Settings;
                if (settings == null) return false;                       // Init would NRE; keep original behaviour
                if (settings.IntelShowMiniMap) return false;
                var sim = game.Simulation;
                if (sim == null) return true;                             // IL_0517: SetActive(false)
                var stats = sim.CompanyStats;
                if (stats == null) return false;
                var stat = stats.GetStatistic(settings.IntelCompanyStatShowMiniMap);
                if (stat == null) return true;                            // IL_0559: SetActive(false)
                return !stat.Value<bool>();                               // IL_057f: SetActive(false) when false
            }
            catch
            {
                return false;
            }
        }

        // CustomAmmoCategories is optional here: only reached (and precompiled) when its assembly is loaded.
        static class CacPostfixes
        {
            internal static int Count() => CACMain.Core.MapMetadata_Load_Postfixes?.Count ?? 0;
        }
    }
}
