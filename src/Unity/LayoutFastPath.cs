using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace PerfeBT
{
    // UI freezes (mech lab enter/exit, store tab switch, salvage list, Argo rooms): Unity UI's
    // LayoutRebuilder.MarkLayoutForRebuild ran 25-50k times per freeze at ~20 us each (GetComponents with an
    // interface type on the parent, then on the rect itself in ValidController), 1-2 s per mech lab exit (0.8.8 profile).
    // Every Graphic calls it when it is disabled or re-parented, so pooling a large panel calls it once per graphic.
    //
    // When the rect is inactive in the hierarchy and its parent is too (or it has no RectTransform parent), the
    // original marks nothing: the parent walk stops at the first parent because none of its layout groups is
    // active and enabled, and ValidController(rect) finds no active and enabled layout controller on the inactive
    // rect. Those calls return before the two GetComponents lookups. Active rects, and inactive rects under an
    // active parent (whose layout group must rebuild), run the original unchanged.
    internal static class LayoutFastPath
    {
        private static long calls;
        private static long skipped;

        internal static void Apply(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(LayoutRebuilder), nameof(LayoutRebuilder.MarkLayoutForRebuild)),
                prefix: new HarmonyMethod(typeof(LayoutFastPath), nameof(Prefix)) { priority = Priority.First });
        }

        private static bool Prefix(RectTransform rect)
        {
            if (++calls % 100000 == 0)
                Log.Write($"[layout] MarkLayoutForRebuild {calls} calls, {skipped} skipped as inactive ({100.0 * skipped / calls:F0}%)");
            if (rect == null || rect.gameObject.activeInHierarchy)
                return true;
            var parent = rect.parent as RectTransform;
            if (parent != null && parent.gameObject.activeInHierarchy)
                return true;
            skipped++;
            return false;
        }
    }
}
