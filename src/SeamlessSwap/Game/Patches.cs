using System.Threading.Tasks;
using Diz.LanguageExtensions;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using SeamlessSwap.Core;

namespace SeamlessSwap.Game
{
    // The game calls CanAccept on hover and again on drop, then AcceptItem only on drop
    // (ItemView.ProcessItem). So hover work stays in CanAccept, which must not plan, mutate or notify,
    // and the commit lives in AcceptItem.

    /// <summary>
    /// Makes the gesture a legal drop even when the game, or UI Fixes with swapping turned off, would say
    /// no. It also has to win when they would say yes, because the game's yes can mean "put their backpack
    /// inside yours" (ExecutePossibleAction). Whether the swap can really happen is decided at the drop,
    /// where one notification explains a refusal; a refused hover would not reach AcceptItem at all.
    /// </summary>
    [HarmonyPatch(typeof(SlotView), nameof(SlotView.CanAccept))]
    internal static class SlotViewCanAcceptPatch
    {
        [HarmonyPostfix]
        private static void Postfix(SlotView __instance, DragItemContext itemContext, ref OperationResult operation, ref bool __result, ItemController ___ItemController)
        {
            if (itemContext == null || __instance.Slot == null)
            {
                return;
            }
            if (Gesture.Check(__instance.Slot, itemContext.Item, ___ItemController, out _, out _, out _) == Gesture.Match.Supported)
            {
                // Clears any error the original produced, so no tooltip claims the drop will fail.
                operation = default;
                __result = true;
            }
        }
    }

    /// <summary>
    /// The commit. Returning false skips the original, so a refused swap cannot fall through into the
    /// game's or UI Fixes' own handling and move equipment anyway. Everything out of scope passes through.
    /// </summary>
    [HarmonyPatch(typeof(SlotView), nameof(SlotView.AcceptItem))]
    internal static class SlotViewAcceptItemPatch
    {
        private static bool _fikaNoticeShown;

        [HarmonyPrefix]
        private static bool Prefix(SlotView __instance, DragItemContext itemContext, ref Task __result, ItemController ___ItemController)
        {
            if (itemContext == null || __instance.Slot == null)
            {
                return true;
            }

            switch (Gesture.Check(__instance.Slot, itemContext.Item, ___ItemController, out SwapKind kind, out Slot mySlot, out CompoundItem incoming))
            {
                case Gesture.Match.Supported:
                    // The original calls this before anything else; the drag view depends on it.
                    itemContext.DragCancelled();
                    if (SeamlessSwapPlugin.DiagnosticLogging.Value)
                    {
                        SeamlessSwapPlugin.Log.LogInfo(__instance.Slot == mySlot ? "Drop: the body's container onto yours." : "Drop: your container onto the body's.");
                    }
                    __result = SwapRunner.Start(__instance, mySlot, incoming, (Player.PlayerInventoryController)___ItemController, kind);
                    return false;

                case Gesture.Match.DisabledByFika:
                    if (!_fikaNoticeShown)
                    {
                        _fikaNoticeShown = true;
                        NotificationManager.DisplayWarningNotification("Seamless Swap is off because Fika is installed. This drag uses normal behavior.");
                    }
                    return true;

                default:
                    return true;
            }
        }
    }
}
