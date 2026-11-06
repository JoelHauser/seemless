using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using UnityEngine;

namespace SeamlessSwap.Game
{
    /// <summary>
    /// Makes the two containers' grids draw what they really hold, once a swap has finished. Joel saw two
    /// items drawn on top of each other after flipping rigs, while the only route to a real overlap
    /// (Grid.AddInternal, which logs "is taken by another item") had logged nothing, so an image was out of
    /// step with the data. The moment it goes stale is unknown, so this repairs the result and always logs
    /// what it found, diagnostics on or off, so the next occurrence names the cause.
    /// </summary>
    internal static class ViewCheck
    {
        private const float CellSize = 63f;

        private static readonly AccessTools.FieldRef<GridView, Dictionary<string, ItemView>> ItemViews =
            AccessTools.FieldRefAccess<GridView, Dictionary<string, ItemView>>("ItemViews");

        internal static async void AfterSwap(Transform root, CompoundItem first, CompoundItem second)
        {
            try
            {
                // The last operation's events and any view rebuilt by them have finished by now.
                await Task.Yield();
                await Task.Yield();
                if (root == null)
                {
                    return;
                }

                int checkedViews = 0;
                foreach (var view in root.GetComponentsInChildren<GridView>(false))
                {
                    var grid = view.Grid;
                    if (grid != null && !view.IsMagnified && (grid.ParentItem == first || grid.ParentItem == second))
                    {
                        Reconcile(view, grid);
                        checkedViews++;
                    }
                }
                if (SeamlessSwapPlugin.DiagnosticLogging.Value)
                {
                    SeamlessSwapPlugin.Log.LogInfo($"View check: {checkedViews} grids.");
                }
            }
            catch (Exception ex)
            {
                // Cosmetic: never let it disturb a swap that has already succeeded.
                SeamlessSwapPlugin.Log.LogError("View check failed: " + ex);
            }
        }

        private static void Reconcile(GridView view, Grid grid)
        {
            if (grid.OverlappingItems.Any())
            {
                SeamlessSwapPlugin.Log.LogError($"Real overlap in {Describe(grid)}: {string.Join(", ", grid.OverlappingItems.Select(i => i.Name))}");
            }

            var known = ItemViews(view);
            foreach (var drawn in view.GetComponentsInChildren<ItemView>(false))
            {
                var item = drawn.Item;
                if (drawn.BeingDragged)
                {
                    continue;
                }

                bool belongs = item != null && item.Parent?.Container == grid;
                bool tracked = item != null && known.TryGetValue(item.Id, out var current) && current == drawn;
                if (!belongs || !tracked)
                {
                    SeamlessSwapPlugin.Log.LogWarning($"Removed a stale image of {item?.Name ?? "nothing"} from {Describe(grid)} (in this grid: {belongs}, tracked: {tracked}).");
                    if (tracked)
                    {
                        known.Remove(item.Id);
                    }
                    drawn.Kill();
                    continue;
                }

                var at = grid.GetItemLocation(item);
                var wanted = new Vector2(at.x * CellSize, -at.y * CellSize);
                var position = ((RectTransform)drawn.transform).anchoredPosition;
                if ((position - wanted).sqrMagnitude > 1f)
                {
                    SeamlessSwapPlugin.Log.LogWarning($"Moved the image of {item.Name} in {Describe(grid)} from {position} to cell {at.x},{at.y}.");
                    view.SetItemViewPosition(drawn, at);
                }
            }

            foreach (var item in grid.Items.ToList())
            {
                if (!known.ContainsKey(item.Id))
                {
                    SeamlessSwapPlugin.Log.LogWarning($"Drew a missing image of {item.Name} in {Describe(grid)}.");
                    view.CreateItemView(item, grid.GetItemLocation(item), ItemUiContext.Instance);
                }
            }
        }

        private static string Describe(Grid grid)
        {
            return $"{grid.ParentItem?.Name} pocket {grid.ID} ({grid.GridWidth}x{grid.GridHeight})";
        }
    }
}
