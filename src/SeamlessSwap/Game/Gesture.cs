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

        internal static Match Check(Slot target, Item dragged, ItemController controller, out SwapKind kind)
        {
            kind = SwapKind.Backpack;

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

            // Target: the local player's own occupied equipment slot.
            if (!(controller is Player.PlayerInventoryController player) || player.Player == null || !player.Player.IsYourPlayer)
            {
                return Match.None;
            }
            if (target.ParentItem != player.Inventory.Equipment)
            {
                return Match.None;
            }
            if (!(target.ContainedItem is CompoundItem outgoing) || outgoing.Grids.Length == 0)
            {
                return Match.None;
            }

            // Source: the same slot on a corpse. A corpse always has a CorpseItemController (Corpse.Init),
            // so a loot container, the ground or another live player can never match.
            if (!(dragged.Parent is SlotItemAddress from) || from.Slot.ID != target.ID || !(from.Slot.ParentItem is InventoryEquipment))
            {
                return Match.None;
            }
            if (!(from.GetOwnerOrNull() is CorpseItemController))
            {
                return Match.None;
            }
            if (!(dragged is CompoundItem incoming) || incoming.Grids.Length == 0)
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
