using System;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using SeamlessSwap.Core;
using UnityEngine;

namespace SeamlessSwap.Game
{
    /// <summary>
    /// Decides whether a drag is the one gesture this mod handles. Runs on every hover change, so it
    /// compares references and nothing else: no search, no simulation, no notification, no state change.
    /// </summary>
    internal static class Gesture
    {
        internal enum Match
        {
            None,
            Supported,
            /// <summary>It is the gesture, but this mod is standing aside because Fika is installed.</summary>
            DisabledByFika,
        }

        /// <param name="target">The slot under the drop, on either side.</param>
        /// <param name="mySlot">The player's own slot, whichever side the drag started from.</param>
        /// <param name="incoming">The body's container, whichever side the drag started from.</param>
        internal static Match Check(Slot target, Item dragged, ItemController controller, out SwapKind kind, out Slot mySlot, out CompoundItem incoming)
        {
            kind = SwapKind.Backpack;
            mySlot = null;
            incoming = null;

            if (dragged == null || target == null)
            {
                return Match.None;
            }

            if (target.ID == nameof(EquipmentSlot.Backpack))
            {
                kind = SwapKind.Backpack;
                if (!SeamlessSwapPlugin.EnableBackpackSwaps.Value)
                {
                    return Match.None;
                }
            }
            else if (target.ID == nameof(EquipmentSlot.TacticalVest))
            {
                kind = SwapKind.Rig;
                if (!SeamlessSwapPlugin.EnableRigSwaps.Value)
                {
                    return Match.None;
                }
            }
            else
            {
                // Belts in Pack 'n' Strap's ArmBand slot, and every other slot, are out of scope.
                return Match.None;
            }

            // Both sides of the loot screen hold the local player's controller (ItemsPanel.Show passes it
            // to the body's ComplexStashPanel too), so this says who is dragging, not whose slot it is.
            if (!(controller is Player.PlayerInventoryController player) || player.Player == null || !player.Player.IsYourPlayer)
            {
                return Match.None;
            }
            if (!Resolve(target, dragged, player.Inventory.Equipment, out mySlot, out incoming))
            {
                return Match.None;
            }

            // UI Fixes multi-select owns the drop when anything is selected: its prefixes on the same two
            // methods take over in exactly that case, so the two can never both act.
            if (UiFixesInterop.MultiSelectCount() > 0)
            {
                return Match.None;
            }

            if (IsBypassHeld())
            {
                return Match.None;
            }

            return SeamlessSwapPlugin.FikaInstalled ? Match.DisabledByFika : Match.Supported;
        }

        /// <summary>
        /// Works out the two sides of a drag between the player's slot and the same slot on a body, in
        /// either direction. The direction ends here: from this point on a swap is the same swap, so
        /// everything downstream (order, hotkeys, messages) can't differ between the two.
        /// A corpse always has a CorpseItemController (Corpse.Init), so a loot container, the ground or
        /// another live player can never match.
        /// </summary>
        internal static bool Resolve(Slot target, Item dragged, InventoryEquipment mine, out Slot mySlot, out CompoundItem incoming)
        {
            mySlot = null;
            incoming = null;

            if (!(dragged.Parent is SlotItemAddress from) || from.Slot.ID != target.ID)
            {
                return false;
            }

            Slot own;
            Slot theirs;
            if (target.ParentItem == mine)
            {
                own = target;
                theirs = from.Slot;
            }
            else if (from.Slot.ParentItem == mine)
            {
                own = from.Slot;
                theirs = target;
            }
            else
            {
                return false;
            }

            if (!(theirs.ParentItem is InventoryEquipment body) || !(body.Owner is CorpseItemController))
            {
                return false;
            }

            // An empty slot on either side isn't a swap: dropping into one is the game's normal move.
            if (!(own.ContainedItem is CompoundItem outgoing) || outgoing.Grids.Length == 0)
            {
                return false;
            }
            if (!(theirs.ContainedItem is CompoundItem bodyContainer) || bodyContainer.Grids.Length == 0)
            {
                return false;
            }

            mySlot = own;
            incoming = bodyContainer;
            return true;
        }

        private static bool IsBypassHeld()
        {
            switch (SeamlessSwapPlugin.Bypass.Value)
            {
                case BypassKey.Control:
                    return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                case BypassKey.Alt:
                    return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// UI Fixes' documented multi-select interop (MultiSelectInterop.cs in its source), reduced to the one
    /// call needed. By name only, so nothing loads or breaks when UI Fixes is absent.
    /// </summary>
    internal static class UiFixesInterop
    {
        private static bool _resolved;
        private static Func<int> _getCount;

        internal static int MultiSelectCount()
        {
            if (!_resolved)
            {
                _resolved = true;
                try
                {
                    var type = Type.GetType("UIFixes.MultiSelectController, Tyfon.UIFixes", false);
                    var method = type?.GetMethod("GetCount", BindingFlags.Public | BindingFlags.Static);
                    if (method != null)
                    {
                        _getCount = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), method);
                    }
                }
                catch (Exception ex)
                {
                    SeamlessSwapPlugin.Log.LogWarning("UI Fixes multi-select lookup failed: " + ex.Message);
                }
            }
            return _getCount?.Invoke() ?? 0;
        }
    }
}
