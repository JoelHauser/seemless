using System.Collections.Generic;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using UnityEngine;

namespace SeamlessSwap.Game
{
    /// <summary>
    /// Hides the contents of the player's old container while they move out of it. The swap comes first
    /// (hotkeys depend on it), so the old container lands on the body still full, and the game draws it
    /// that way for a frame or two before the moves land: a visible flash of the player's items on the
    /// body. Only the contents are hidden, not the container, and only for the length of one swap.
    /// </summary>
    internal static class Curtain
    {
        private static readonly AccessTools.FieldRef<SearchableItemView, ContainedGridsView> GridsOf =
            AccessTools.FieldRefAccess<SearchableItemView, ContainedGridsView>("_containedGridsView");

        private static CompoundItem _hidden;
        private static readonly List<CanvasGroup> Drawn = new List<CanvasGroup>();

        internal static void Draw(CompoundItem container)
        {
            _hidden = container;
        }

        /// <summary>Always called when a swap ends, however it ends: anything left behind must be seen.</summary>
        internal static void Lift()
        {
            _hidden = null;
            foreach (var group in Drawn)
            {
                if (group != null)
                {
                    group.alpha = 1f;
                }
            }
            Drawn.Clear();
        }

        // Runs as the game builds a container's contents, in the same frame, before anything is rendered.
        [HarmonyPatch(typeof(SearchableItemView), nameof(SearchableItemView.Show))]
        internal static class SearchableItemViewShowPatch
        {
            [HarmonyPostfix]
            private static void Postfix(SearchableItemView __instance, CompoundItem compoundItem)
            {
                if (_hidden == null || compoundItem != _hidden)
                {
                    return;
                }
                var grids = GridsOf(__instance);
                if (grids == null)
                {
                    return;
                }
                var group = grids.GetComponent<CanvasGroup>() ?? grids.gameObject.AddComponent<CanvasGroup>();
                group.alpha = 0f;
                Drawn.Add(group);
            }
        }
    }
}
