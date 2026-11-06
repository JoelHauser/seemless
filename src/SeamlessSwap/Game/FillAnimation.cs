using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using UnityEngine;

namespace SeamlessSwap.Game
{
    /// <summary>
    /// The fill animation: like collecting a full mailbox, the items that moved appear one by one in their
    /// new cells, row by row across the screen. Purely on screen: by the time it starts every item is
    /// already in place (the swap lands in one frame, see Immediate), so nothing here can lose, move or
    /// block an item. An item's real view is only hidden until its turn; if anything goes wrong, or the
    /// inventory closes, every view is shown at once.
    ///
    /// Grid items only. Slot items, such as BackpackGunSlot's gun, have views other mods rebuild, so they
    /// are left alone.
    /// </summary>
    internal static class FillAnimation
    {
        private const float CellSize = 63f;
        private const float Step = 0.025f;
        private const float MaxTotal = 1.5f;

        private static readonly Dictionary<Item, Placement> Before = new Dictionary<Item, Placement>();

        private readonly struct Placement
        {
            public readonly Grid Grid;
            public readonly int X;
            public readonly int Y;

            public Placement(Grid grid, int x, int y)
            {
                Grid = grid;
                X = x;
                Y = y;
            }
        }

        /// <summary>Right before the swap: where every grid item of both containers sits.</summary>
        internal static void Capture(CompoundItem incoming, CompoundItem outgoing)
        {
            Before.Clear();
            if (!SeamlessSwapPlugin.FillAnimation.Value)
            {
                return;
            }
            foreach (var grid in incoming.Grids.Concat(outgoing.Grids))
            {
                foreach (var item in grid.Items)
                {
                    var at = grid.GetItemLocation(item);
                    Before[item] = new Placement(grid, at.x, at.y);
                }
            }
        }

        /// <summary>After the swap: hide every item that changed place, then reveal them in reading order.</summary>
        internal static void Play(Transform root, CompoundItem incoming)
        {
            try
            {
                if (root == null || Before.Count == 0 || !SeamlessSwapPlugin.FillAnimation.Value)
                {
                    return;
                }
                var views = root.GetComponentsInChildren<ItemView>(false);
                var arriving = new List<ItemView>();
                foreach (var pair in Before)
                {
                    var item = pair.Key;
                    if (!(item.Parent is GridItemAddress now) || now.Grid.ParentItem != incoming)
                    {
                        continue;
                    }
                    var was = pair.Value;
                    if (was.Grid == now.Grid && was.X == now.LocationInGrid.x && was.Y == now.LocationInGrid.y)
                    {
                        continue; // Came with its bag and stayed in its cell.
                    }
                    var view = views.FirstOrDefault(v => v.Item == item && v.isActiveAndEnabled && v.GetComponentInParent<GridView>() != null);
                    if (view != null)
                    {
                        arriving.Add(view);
                    }
                }
                if (arriving.Count == 0)
                {
                    return;
                }

                var canvas = arriving[0].GetComponentInParent<Canvas>()?.rootCanvas;
                if (canvas == null)
                {
                    return;
                }
                var runner = canvas.GetComponent<FillRunner>() ?? canvas.gameObject.AddComponent<FillRunner>();
                runner.FinishAll();

                // Reading order on screen: rows one cell high, top to bottom, then left to right.
                float cell = CellSize * Mathf.Max(0.0001f, canvas.transform.lossyScale.y);
                var ordered = arriving
                    .OrderBy(v => Mathf.Round(-v.transform.position.y / cell))
                    .ThenBy(v => v.transform.position.x)
                    .ToList();
                float speed = Mathf.Clamp(SeamlessSwapPlugin.FillSpeed.Value, 0.25f, 4f);
                float step = Mathf.Min(Step, MaxTotal / ordered.Count) / speed;
                for (int i = 0; i < ordered.Count; i++)
                {
                    runner.Add(ordered[i], (i + 1) * step);
                }
                if (SeamlessSwapPlugin.DiagnosticLogging.Value)
                {
                    SeamlessSwapPlugin.Log.LogInfo($"Fill animation: {ordered.Count} items, {step * 1000f:0} ms apart.");
                }
            }
            catch (Exception ex)
            {
                SeamlessSwapPlugin.Log.LogError("Fill animation failed to start: " + ex);
            }
            finally
            {
                Before.Clear();
            }
        }
    }

    /// <summary>Reveals the items on the inventory's root canvas. Lives as long as that canvas.</summary>
    internal sealed class FillRunner : MonoBehaviour
    {
        private sealed class Reveal
        {
            public ItemView View;
            public Item Item;
            public float At;
            public float Wanted;
        }

        // The game's own drop sound per item, but no closer together than this, or a fast fill is a buzz.
        private const float SoundGap = 0.06f;

        private readonly List<Reveal> _pending = new List<Reveal>();
        private float _lastSound = -1f;

        internal void Add(ItemView view, float delay)
        {
            var reveal = new Reveal
            {
                View = view,
                Item = view.Item,
                At = Time.unscaledTime + delay,
                Wanted = view.CanvasGroup != null ? view.CanvasGroup.alpha : 1f,
            };
            _pending.Add(reveal);
            Hide(reveal);
        }

        // The game sets this alpha itself for some states. Any value it sets while hidden is kept for later.
        private static void Hide(Reveal r)
        {
            var group = r.View.CanvasGroup;
            if (group == null)
            {
                return;
            }
            if (group.alpha > 0f)
            {
                r.Wanted = group.alpha;
            }
            group.alpha = 0f;
        }

        private static void Show(Reveal r)
        {
            if (r.View != null && r.View.CanvasGroup != null && r.View.CanvasGroup.alpha == 0f)
            {
                r.View.CanvasGroup.alpha = r.Wanted;
            }
        }

        private void Sound(Reveal r)
        {
            if (!SeamlessSwapPlugin.FillSounds.Value || Time.unscaledTime - _lastSound < SoundGap)
            {
                return;
            }
            try
            {
                if (Singleton<GUISounds>.Instantiated && !string.IsNullOrEmpty(r.Item?.ItemSound))
                {
                    Singleton<GUISounds>.Instance.PlayItemSound(r.Item.ItemSound, EInventorySoundType.drop);
                    _lastSound = Time.unscaledTime;
                }
            }
            catch (Exception ex)
            {
                SeamlessSwapPlugin.Log.LogError("Fill sound failed: " + ex.Message);
            }
        }

        internal void FinishAll()
        {
            foreach (var r in _pending)
            {
                Show(r);
            }
            _pending.Clear();
        }

        private void LateUpdate()
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var r = _pending[i];
                try
                {
                    // A view that was closed or reused for another item is no longer ours to hide.
                    bool alive = r.View != null && r.View.isActiveAndEnabled && r.View.Item == r.Item;
                    if (!alive || Time.unscaledTime >= r.At)
                    {
                        Show(r);
                        if (alive)
                        {
                            Sound(r);
                        }
                        _pending.RemoveAt(i);
                        continue;
                    }
                    Hide(r);
                }
                catch (Exception ex)
                {
                    Show(r);
                    _pending.RemoveAt(i);
                    SeamlessSwapPlugin.Log.LogError("Fill animation stopped: " + ex);
                }
            }
        }

        // Inventory closed or the canvas went away: every real view must be visible again.
        private void OnDisable()
        {
            FinishAll();
        }

        private void OnDestroy()
        {
            FinishAll();
        }
    }
}
