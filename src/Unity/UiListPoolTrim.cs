using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace PerfeBT
{
    // Argo UI 10-30x slower for the rest of the session after a long mission (salvage list, store, mech lab, every
    // mouse-over): Unity UI's shared ListPool<Component> keeps every list it hands out. MaskUtilities.Notify2DMask-
    // StateChanged / NotifyStencilStateChanged fill one with GetComponentsInChildren of a whole masked panel (tens of
    // thousands of components with many mechs and items). List.Clear keeps that capacity, and Unity's
    // GetComponents(..., list) visits every slot of the list's array up to its capacity
    // (FillScriptingArrayFromUnityObjects), not just the components it returns. Graphic.Raycast, UpdateMaterial,
    // LayoutRebuilder and ExecuteEvents then pay for the largest panel ever masked on every call: 2.5% -> 32% of the
    // main thread on the Argo in the sampled session.
    //
    // After those two methods return their list to the pool, empty pooled lists above MaxCapacity get their backing
    // array dropped (Capacity = 0). Pooled lists are always empty (the pool clears them on release), so no caller
    // can observe the difference; the next large mask notification allocates a fresh array as on first use.
    internal static class UiListPoolTrim
    {
        private const int MaxCapacity = 128;
        private static Stack<List<Component>> pool;
        private static int trims;
        private static int largest;

        internal static void Apply(Harmony harmony)
        {
            pool = ListPool<Component>.s_ListPool.m_Stack;
            var postfix = new HarmonyMethod(typeof(UiListPoolTrim), nameof(Postfix));
            harmony.Patch(AccessTools.Method(typeof(MaskUtilities), nameof(MaskUtilities.Notify2DMaskStateChanged)), postfix: postfix);
            harmony.Patch(AccessTools.Method(typeof(MaskUtilities), nameof(MaskUtilities.NotifyStencilStateChanged)), postfix: postfix);
            Trim(null);
        }

        private static void Postfix(Component mask) => Trim(mask);

        private static void Trim(Component mask)
        {
            foreach (var list in pool)
            {
                int capacity = list.Capacity;
                if (capacity <= MaxCapacity || list.Count != 0)
                    continue;
                list.Capacity = 0;
                trims++;
                if (capacity > largest || trims <= 5 || trims % 1000 == 0)
                {
                    largest = Math.Max(largest, capacity);
                    Log.Write($"[listpool] pooled List<Component> capacity {capacity} released (trim {trims}, largest {largest}) after '{(mask != null ? mask.name : "startup")}'");
                }
            }
        }
    }
}
