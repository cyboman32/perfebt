using System;
using System.Collections;
using System.Linq;
using System.Reflection;
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
    internal static class CombatLeftovers
    {
        private sealed class Slot
        {
            public string Label;
            public FieldInfo Field;
            public bool Clear; // clear the collection instead of nulling the field
        }

        private static Slot[] slots;
        private static PropertyInfo mcInstanceLayer;
        private static FieldInfo mcInstance, layerCells;

        internal static void Apply()
        {
            layerCells = typeof(EncounterLayerData).GetField("mapEncounterLayerDataCells", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         ?? throw new MissingFieldException("EncounterLayerData.mapEncounterLayerDataCells");
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        // On the first sim game / main menu load, when every mod assembly is loaded.
        private static void Resolve()
        {
            var cac = Loaded("CustomAmmoCategories");
            var mc = Loaded("MissionControl");
            slots = new[]
            {
                Static(cac, "CustAmmoCategories.DynamicMapHelper", "mapMetaData", false),
                Static(cac, "CustAmmoCategories.DamageModifiersCache", "HUD", false),
                Static(cac, "CustAmmoCategories.AdvWeaponHitInfoRec", "Combat", false),
                Static(cac, "CustAmmoCategoriesPatches.MoveStatusPreview_DisplayPreviewStatus", "AdditionalTitles", true),
                Static(cac, "CustAmmoCategoriesPatches.AbstractActor_OnActivationBegin", "heatSinksReseted", true),
            }.Where(s => s != null).ToArray();

            var mcType = mc?.GetType("MissionControl.MissionControl");
            mcInstance = mcType?.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            mcInstanceLayer = mcType?.GetProperty("EncounterLayerData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (mc != null && (mcInstance == null || mcInstanceLayer == null))
                Log.Write("[leftovers] MissionControl EncounterLayerData not found, skipped");

            Log.Write($"[leftovers] watching {slots.Length} CustomAmmoCategories fields" + (mcInstanceLayer != null ? " + MissionControl encounter grid" : ""));
        }

        private static Assembly Loaded(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);

        private static Slot Static(Assembly asm, string type, string field, bool clear)
        {
            var f = asm?.GetType(type)?.GetField(field, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null)
            {
                Log.Write($"[leftovers] {type}.{field} not found, skipped");
                return null;
            }
            if (clear && !typeof(IEnumerable).IsAssignableFrom(f.FieldType))
                return null;
            return new Slot { Label = type.Substring(type.LastIndexOf('.') + 1) + "." + field, Field = f, Clear = clear };
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
            if (slots == null)
                Resolve();
            var released = new System.Collections.Generic.List<string>();
            foreach (var s in slots)
            {
                try
                {
                    var value = s.Field.GetValue(null);
                    if (value == null)
                        continue;
                    if (s.Clear)
                    {
                        var clear = value.GetType().GetMethod("Clear", Type.EmptyTypes);
                        if (clear == null || (value is ICollection c && c.Count == 0))
                            continue;
                        clear.Invoke(value, null);
                    }
                    else
                        s.Field.SetValue(null, null);
                    released.Add(s.Label);
                }
                catch (Exception e)
                {
                    Log.Write($"[leftovers] {s.Label} failed: {e.Message}");
                }
            }
            try
            {
                var instance = mcInstance?.GetValue(null);
                var layer = instance != null && mcInstanceLayer != null ? mcInstanceLayer.GetValue(instance, null) as EncounterLayerData : null;
                // Unity's == null is true for a destroyed object whose managed shell is still referenced.
                if ((object)layer != null && layer == null && layerCells.GetValue(layer) != null)
                {
                    layerCells.SetValue(layer, null);
                    released.Add("MissionControl EncounterLayerData cells");
                }
            }
            catch (Exception e)
            {
                Log.Write($"[leftovers] MissionControl failed: {e.Message}");
            }
            if (released.Count > 0)
                Log.Write($"[leftovers] {scene.name}: released {string.Join(", ", released)}");
        }
    }
}
