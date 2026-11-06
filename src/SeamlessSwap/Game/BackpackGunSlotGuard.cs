using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine.UI;

namespace SeamlessSwap.Game
{
    /// <summary>
    /// Works around a bug in BackpackGunSlot 1.1.1 that made the ON PACK gun vanish until the inventory was
    /// reopened. When the backpack changes, its BackpackSlotBinding destroys its old LayoutElement and adds a
    /// new one in the same frame, then caches the row's layout elements (_rowElements) while the old one,
    /// whose destruction Unity defers to the frame's end, is still listed. Next frame PlaceSlot reads
    /// .enabled on it, which throws (IL 0x3b2: its null check compares the C# reference, not Unity's), and
    /// the binding shuts itself off. Seen in Joel's log after swaps.
    ///
    /// The guard only drops a cache that holds a destroyed object, so that mod rebuilds it the way it
    /// already does when it's empty. Reached by name, no reference: absent or changed, it does nothing.
    /// </summary>
    internal static class BackpackGunSlotGuard
    {
        private static FieldInfo _rowElements;

        internal static void Apply(Harmony harmony)
        {
            try
            {
                var binding = AccessTools.TypeByName("BackpackGunSlot.BackpackSlotBinding");
                var place = binding != null ? AccessTools.Method(binding, "PlaceSlot") : null;
                _rowElements = binding != null ? AccessTools.Field(binding, "_rowElements") : null;
                if (place == null || _rowElements == null || _rowElements.FieldType != typeof(ILayoutElement[]))
                {
                    SeamlessSwapPlugin.Log.LogInfo("BackpackGunSlot layout guard not needed: its binding has changed.");
                    return;
                }
                harmony.Patch(place, prefix: new HarmonyMethod(typeof(BackpackGunSlotGuard), nameof(Prefix)));
                SeamlessSwapPlugin.Log.LogInfo("BackpackGunSlot layout guard on.");
            }
            catch (Exception ex)
            {
                SeamlessSwapPlugin.Log.LogWarning("BackpackGunSlot layout guard not applied: " + ex.Message);
            }
        }

        private static void Prefix(object __instance)
        {
            if (!(_rowElements.GetValue(__instance) is ILayoutElement[] cached))
            {
                return;
            }
            foreach (var element in cached)
            {
                // Unity's own equality: true for an object that has been destroyed.
                if (element is UnityEngine.Object unityObject && unityObject == null)
                {
                    _rowElements.SetValue(__instance, null);
                    return;
                }
            }
        }
    }
}
