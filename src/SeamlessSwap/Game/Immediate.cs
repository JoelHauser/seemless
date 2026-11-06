using Comfort.Common;
using EFT;
using AbstractOperation = EFT.InventoryLogic.Operations.AbstractOperation;
using HarmonyLib;

namespace SeamlessSwap.Game
{
    /// <summary>
    /// Lets this mod's own operations skip the one-frame wait the local player's controller puts in front of
    /// every operation (SinglePlayerInventoryController.ExecuteAsync yields, then calls base.Execute). With
    /// the wait, the swap lands in one frame and the moves in later ones, and the frames in between show a
    /// half-done swap: Joel saw it as a flash. Without it, nothing in the swap waits on a frame: OutProcess
    /// and InProcess complete on the spot when nothing is in hands, AsyncWorker runs completion inline on the
    /// main thread, and the callback follows. So the swap and every move land before the next render.
    ///
    /// One operation at a time: Next is consumed by the first Execute it reaches, so anything another mod
    /// or the game starts from inside an event takes the normal path.
    /// </summary>
    internal static class Immediate
    {
        internal static bool Next;

        [HarmonyPatch(typeof(Player.SinglePlayerInventoryController), nameof(Player.SinglePlayerInventoryController.Execute))]
        internal static class ExecutePatch
        {
            [HarmonyPrefix]
            private static bool Prefix(Player.SinglePlayerInventoryController __instance, AbstractOperation operation, Callback callback)
            {
                if (!Next)
                {
                    return true;
                }
                Next = false;
                // The compiler's name for base.Execute, called by ExecuteAsync after its yield: the same code.
                __instance.method_43(operation, callback);
                return false;
            }
        }
    }
}
