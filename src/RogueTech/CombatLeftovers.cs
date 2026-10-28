using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using BattleTech;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PerfeBT
{
    // Memory only. Static fields in CustomAmmoCategories and MissionControl keep the last mission alive on the Argo
    // (heap census 2026-10-18 09:35, scene SimGame#2): CAC DynamicMapHelper.mapMetaData (terrain grid and trees,
    // 172 MB / 1.04M objects), CAC DamageModifiersCache.HUD (destroyed combat HUD, 57 MB),
    // AdvWeaponHitInfoRec.Combat (the old CombatGameState), the CAC actor collections AdditionalTitles and
    // heatSinksReseted, and MissionControl's EncounterLayerData cell grid (32 MB / 637k objects).
    // Every collection on the Argo scans them, and they stay alive while the next map loads.
    //
    // When the sim game or main menu scene loads with no combat running, they are released:
    //   - mapMetaData: both readers (TempTick, DynamicTreesHelper.OnInsert) null-check it; initHexGrid sets it
    //     again for the next map.
    //   - DamageModifiersCache.HUD: written only (Init), never read through the field.
    //   - AdvWeaponHitInfoRec.Combat: read only while resolving attacks, set again by initGenericAdvInfo.
    //   - AdditionalTitles / heatSinksReseted: cleared (combat-only lookups keyed by actors).
    //   - MissionControl EncounterLayerData: only when that Unity object is already destroyed; its cell array is
    //     dropped and InitSceneData assigns a new EncounterLayerData for the next mission.
    // Heap census 2026-10-28 14:05 (SimGame after a mission) found more:
    //   - MapMetaData.invalidCell.mapMetaData (the old map, 174 MB / 2.45M objects): the shared out-of-map cell is
    //     null there at game start; MapMetaData.GetPriorityDesignMask writes cell.mapMetaData = this before reading
    //     it. The other readers (MapTerrainDataCell.GetAudioSurfaceType, GetVFXNameModifier) see the same null as
    //     in the first mission after start instead of a destroyed map. Reset to the startup state.
    //   - CAC PersistentFloatieHelper.HUD (destroyed combat HUD, 89 MB): read only by CreateFloatie and PoolFloatie in
    //     combat; Init sets it again from CombatHUD.Init.
    //   - CAC MoveStatusPreview_DisplayPreviewStatus.AdditionalDescritions (29 MB): AdditionalTitles' twin, cleared.
    //   - CAC AIPathingLimiter.original_movementCandidateLocations (CustomAmmoCategoriesPrivate, keyed by the old
    //     BehaviorTrees, 13 MB and growing per mission): cleared; its users add a missing tree again.
    internal static class CombatLeftovers
    {
        private static bool resolved, cac, mc;
        private static Func<IDictionary> aiPathing;

        internal static void Apply()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        // On the first sim game / main menu load, when every mod assembly is loaded. A mod whose members changed is
        // skipped (its holder fails to precompile), the other one still runs.
        private static void Resolve()
        {
            resolved = true;
            cac = Precompiled("CustomAmmoCategories", typeof(Cac));
            mc = Precompiled("MissionControl", typeof(Mc));
            aiPathing = AiPathingCandidates();
            Log.Write($"[leftovers] watching {(cac ? 7 : 0)} CustomAmmoCategories fields" + (aiPathing != null ? " + AI pathing records" : "") +
                      (mc ? " + MissionControl encounter grid" : "") + " + the out-of-map cell");
        }

        // CustomAmmoCategoriesPrivate is not referenced: its private static dictionary is read once by reflection.
        private static Func<IDictionary> AiPathingCandidates()
        {
            if (!ModAssemblies.Loaded("CustomAmmoCategoriesPrivate"))
                return null;
            var property = AccessTools.Property(AccessTools.TypeByName("CustAmmoCategories.AIPathingLimiter"), "original_movementCandidateLocations");
            var getter = property?.GetGetMethod(true);
            if (getter == null || !getter.IsStatic || !typeof(IDictionary).IsAssignableFrom(getter.ReturnType))
            {
                Log.Write("[leftovers] AIPathingLimiter.original_movementCandidateLocations not found, skipped");
                return null;
            }
            return () => (IDictionary)getter.Invoke(null, null);
        }

        private static bool Precompiled(string assembly, Type holder)
        {
            if (!ModAssemblies.Loaded(assembly))
                return false;
            try
            {
                Fixes.Precompile(holder);
                return true;
            }
            catch (Exception e)
            {
                Log.Write($"[leftovers] {assembly} fields not found, skipped: {e.Message}");
                return false;
            }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single || (scene.name != "SimGame" && scene.name != "MainMenu"))
                return;
            if (UnityGameInstance.BattleTechGame?.Combat != null)
            {
                Log.Write("[leftovers] combat still running, nothing released");
                return;
            }
            if (!resolved)
                Resolve();
            var released = new List<string>();
            ReleaseInvalidCell(released);
            if (cac)
                Cac.Release(released);
            if (aiPathing != null)
            {
                try
                {
                    var candidates = aiPathing();
                    if (candidates != null && candidates.Count != 0)
                    {
                        candidates.Clear();
                        released.Add("AIPathingLimiter.original_movementCandidateLocations");
                    }
                }
                catch (Exception e) { Failed("AIPathingLimiter.original_movementCandidateLocations", e); }
            }
            if (mc)
            {
                try
                {
                    Mc.Release(released);
                }
                catch (Exception e)
                {
                    Log.Write($"[leftovers] MissionControl failed: {e.Message}");
                }
            }
            if (released.Count > 0)
                Log.Write($"[leftovers] {scene.name}: released {string.Join(", ", released)}");
        }

        private static void Failed(string label, Exception e) => Log.Write($"[leftovers] {label} failed: {e.Message}");

        private static void ReleaseInvalidCell(List<string> released)
        {
            try
            {
                var cell = MapMetaData.invalidCell;
                if (cell != null && cell.mapMetaData != null)
                {
                    cell.mapMetaData = null;
                    released.Add("MapMetaData.invalidCell.mapMetaData");
                }
            }
            catch (Exception e) { Failed("MapMetaData.invalidCell.mapMetaData", e); }
        }

        // Uses CustomAmmoCategories members directly. Each field: null -> skip; otherwise null it, or Clear() a collection
        // (a Dictionary only when not empty, as ICollection.Count was checked; HashSet is not an ICollection, so it is
        // cleared and reported whenever it is set, as before).
        private static class Cac
        {
            internal static void Release(List<string> released)
            {
                try
                {
                    if ((object)CustAmmoCategories.DynamicMapHelper.mapMetaData != null)
                    {
                        CustAmmoCategories.DynamicMapHelper.mapMetaData = null;
                        released.Add("DynamicMapHelper.mapMetaData");
                    }
                }
                catch (Exception e) { Failed("DynamicMapHelper.mapMetaData", e); }
                try
                {
                    if ((object)CustAmmoCategories.DamageModifiersCache.HUD != null)
                    {
                        CustAmmoCategories.DamageModifiersCache.HUD = null;
                        released.Add("DamageModifiersCache.HUD");
                    }
                }
                catch (Exception e) { Failed("DamageModifiersCache.HUD", e); }
                try
                {
                    if (CustAmmoCategories.AdvWeaponHitInfoRec.Combat != null)
                    {
                        CustAmmoCategories.AdvWeaponHitInfoRec.Combat = null;
                        released.Add("AdvWeaponHitInfoRec.Combat");
                    }
                }
                catch (Exception e) { Failed("AdvWeaponHitInfoRec.Combat", e); }
                try
                {
                    var titles = CustAmmoCategoriesPatches.MoveStatusPreview_DisplayPreviewStatus.AdditionalTitles;
                    if (titles != null && titles.Count != 0)
                    {
                        titles.Clear();
                        released.Add("MoveStatusPreview_DisplayPreviewStatus.AdditionalTitles");
                    }
                }
                catch (Exception e) { Failed("MoveStatusPreview_DisplayPreviewStatus.AdditionalTitles", e); }
                try
                {
                    var descriptions = CustAmmoCategoriesPatches.MoveStatusPreview_DisplayPreviewStatus.AdditionalDescritions;
                    if (descriptions != null && descriptions.Count != 0)
                    {
                        descriptions.Clear();
                        released.Add("MoveStatusPreview_DisplayPreviewStatus.AdditionalDescritions");
                    }
                }
                catch (Exception e) { Failed("MoveStatusPreview_DisplayPreviewStatus.AdditionalDescritions", e); }
                try
                {
                    if ((object)CustAmmoCategories.PersistentFloatieHelper.HUD != null)
                    {
                        CustAmmoCategories.PersistentFloatieHelper.HUD = null;
                        released.Add("PersistentFloatieHelper.HUD");
                    }
                }
                catch (Exception e) { Failed("PersistentFloatieHelper.HUD", e); }
                try
                {
                    var reseted = CustAmmoCategoriesPatches.AbstractActor_OnActivationBegin.heatSinksReseted;
                    if (reseted != null)
                    {
                        reseted.Clear();
                        released.Add("AbstractActor_OnActivationBegin.heatSinksReseted");
                    }
                }
                catch (Exception e) { Failed("AbstractActor_OnActivationBegin.heatSinksReseted", e); }
            }
        }

        // Uses MissionControl members directly.
        private static class Mc
        {
            internal static void Release(List<string> released)
            {
                var instance = global::MissionControl.MissionControl.instance;
                var layer = (object)instance != null ? instance.EncounterLayerData : null;
                // Unity's == null is true for a destroyed object whose managed shell is still referenced.
                if ((object)layer != null && layer == null && layer.mapEncounterLayerDataCells != null)
                {
                    layer.mapEncounterLayerDataCells = null;
                    released.Add("MissionControl EncounterLayerData cells");
                }
            }
        }
    }
}
