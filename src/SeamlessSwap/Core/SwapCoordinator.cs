using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SeamlessSwap.Core
{
    public enum SwapKind { Backpack, Rig }

    /// <summary>The player-facing messages. Exact wording is part of the mod's specification.</summary>
    public static class Messages
    {
        public const string BackpackNoSpace = "Not enough space to transfer your backpack contents.";
        public const string RigNoSpace = "Not enough space to transfer your rig contents.";
        public const string SearchLimit = "Could not arrange items automatically. Rearrange and try again.";
        public const string SearchRequired = "Search both containers before swapping.";
        public const string BodyArmorConflict = "Remove your body armor before equipping this armored rig.";
        public const string Restricted = "An item's restrictions prevent this swap.";
        public const string InventoryChanged = "Your inventory changed before the swap could start. Try again.";
        public const string NoAcknowledgment = "The swap has not been confirmed. Check your equipment before moving on.";
        public const string Busy = "A swap is already in progress.";
        public const string InternalError = "Could not swap: an unexpected error occurred. Nothing was moved.";

        public static string NoSpace(SwapKind kind)
        {
            return kind == SwapKind.Backpack ? BackpackNoSpace : RigNoSpace;
        }

        public static string Interrupted(SwapKind kind, int leftInOld)
        {
            string container = kind == SwapKind.Backpack ? "backpack" : "rig";
            return leftInOld == 1
                ? $"Swap interrupted: 1 item is still in your old {container} on the body."
                : $"Swap interrupted: {leftInOld} items are still in your old {container} on the body.";
        }
    }

    public sealed class OperationOutcome
    {
        public bool Succeeded { get; }
        public string Error { get; }

        private OperationOutcome(bool succeeded, string error)
        {
            Succeeded = succeeded;
            Error = error;
        }

        public static readonly OperationOutcome Success = new OperationOutcome(true, null);
        public static OperationOutcome Failure(string error) => new OperationOutcome(false, error);
    }

    /// <summary>
    /// Everything the coordinator needs from the live game. The game implementation is in Game/; tests
    /// use a model that can reject any operation, delay any acknowledgment or change state between steps.
    /// </summary>
    public interface ISwapGateway
    {
        /// <summary>Checks equipment, search, hands and module rules. Null when the swap may go ahead.</summary>
        string ValidatePreconditions();

        ArrangementRequest BuildRequest();

        /// <summary>Changes whenever any state the plan depends on changes.</summary>
        string Fingerprint();

        Task<ArrangementResult> SolveAsync(ArrangementRequest request);

        /// <summary>Checks the whole plan with the game's own rules and leaves inventory exactly as it was. Null means legal.</summary>
        string DryRun(IReadOnlyList<MoveStep> steps);

        Task<OperationOutcome> ExecuteSwapAsync();
        bool IsSwapApplied();

        /// <summary>True while the item is where the step expects and the destination is free.</summary>
        bool IsStepValid(in MoveStep step);
        Task<OperationOutcome> ExecuteMoveAsync(MoveStep step);

        /// <summary>Items the player owned or looted that are still in the outgoing container.</summary>
        int CountOutgoingContents();

        /// <summary>False once the player is dead, or the raid or game world is gone.</summary>
        bool CanContinue();

        /// <summary>Lets one frame pass. Inventory operations run at most one per frame.</summary>
        Task NextFrame();

        /// <summary>Completes after the time limit for a single acknowledgment.</summary>
        Task AckTimeout();

        /// <summary>
        /// Repairs what the moves disturbed: hotkeys that are still valid, and tags the game clears when
        /// an item changes owner. Run once the moves are done.
        /// </summary>
        void RestoreItemData();

        void Notify(string message);
        void Log(string message);
        void LogError(string message);
    }

    /// <summary>
    /// Runs one swap from the drop to the final state.
    ///
    /// Order: everything is validated, solved and dry-run before the first change; the native Swap comes
    /// first; contents follow as single moves. No native atomic operation covers the whole exchange, so
    /// this order is chosen for what an interruption leaves behind:
    ///   - The swap is the step most likely to be refused. Doing it first means a refusal changes nothing.
    ///   - After the swap, every remaining item is a normal item in a normal bag on the body. Nothing is
    ///     limbo, and the player can still take anything by hand.
    ///   - Items leave the old container with a corpse as their source owner, and the game clears bindings
    ///     only for an InventoryController source (ItemManipulator.Move), so hotkeys survive natively.
    /// A failure after the swap never replays: state is read again and a fresh plan is made, a bounded
    /// number of times. Whatever then remains is reported, once, with the actual count.
    /// </summary>
    public sealed class SwapCoordinator
    {
        // One attempt, plus re-plans from fresh state.
        private const int MaxTransferAttempts = 3;

        private readonly ISwapGateway _gateway;
        private readonly SwapKind _kind;

        public SwapCoordinator(ISwapGateway gateway, SwapKind kind)
        {
            _gateway = gateway;
            _kind = kind;
        }

        public async Task RunAsync()
        {
            try
            {
                await RunInternal();
            }
            catch (Exception ex)
            {
                _gateway.LogError("Swap failed with an exception: " + ex);
                if (SafeIsSwapApplied())
                {
                    Finish();
                }
                else
                {
                    _gateway.Notify(Messages.InternalError);
                }
            }
        }

        private bool SafeIsSwapApplied()
        {
            try
            {
                return _gateway.IsSwapApplied();
            }
            catch (Exception ex)
            {
                _gateway.LogError("State check failed: " + ex);
                return true;
            }
        }

        private async Task RunInternal()
        {
            string error = _gateway.ValidatePreconditions();
            if (error != null)
            {
                _gateway.Notify(error);
                return;
            }

            var request = _gateway.BuildRequest();
            string fingerprint = _gateway.Fingerprint();

            var arrangement = await _gateway.SolveAsync(request);
            _gateway.Log($"Solver: {arrangement.Status} after {arrangement.NodesVisited} nodes, rearranged={arrangement.RearrangedIncoming}");

            error = MessageFor(arrangement);
            if (error != null)
            {
                _gateway.Notify(error);
                return;
            }

            var steps = MovePlanner.Plan(request, arrangement.Placements);
            if (steps == null)
            {
                _gateway.Log("A legal arrangement exists but no space allows reaching it step by step.");
                _gateway.Notify(Messages.SearchLimit);
                return;
            }

            // The solver may have run across frames. Anything it assumed may be out of date.
            if (_gateway.Fingerprint() != fingerprint)
            {
                _gateway.Notify(Messages.InventoryChanged);
                return;
            }
            error = _gateway.ValidatePreconditions();
            if (error != null)
            {
                _gateway.Notify(error);
                return;
            }

            error = _gateway.DryRun(steps);
            if (error != null)
            {
                _gateway.Log("Dry run refused the plan: " + error);
                _gateway.Notify(error);
                return;
            }

            Task<OperationOutcome> swapTask = _gateway.ExecuteSwapAsync();
            var swapOutcome = await WithTimeout(swapTask);
            if (!swapOutcome.Succeeded && !_gateway.IsSwapApplied() && !swapTask.IsCompleted)
            {
                // No answer yet, and nothing has happened so far. The swap may still land, so it cannot
                // be reported as refused until it has either answered or used up a second time limit.
                swapOutcome = await WithTimeout(swapTask);
            }

            if (!_gateway.IsSwapApplied())
            {
                _gateway.Log("Swap not applied: " + swapOutcome.Error);
                if (swapTask.IsCompleted)
                {
                    _gateway.Notify(swapOutcome.Error ?? Messages.InternalError);
                }
                else
                {
                    _gateway.Notify(Messages.NoAcknowledgment);
                    WatchLateSwap(swapTask);
                }
                return;
            }

            await Transfer(steps);
            Finish();
        }

        private string MessageFor(ArrangementResult arrangement)
        {
            switch (arrangement.Status)
            {
                case ArrangementStatus.Found:
                    return null;
                case ArrangementStatus.Impossible:
                    return Messages.NoSpace(_kind);
                case ArrangementStatus.BudgetExhausted:
                    return Messages.SearchLimit;
                default:
                    return Messages.Restricted;
            }
        }

        private async Task Transfer(List<MoveStep> firstSteps)
        {
            var steps = firstSteps;
            for (int attempt = 1; attempt <= MaxTransferAttempts; attempt++)
            {
                bool clean = await RunSteps(steps);
                if (!_gateway.CanContinue())
                {
                    _gateway.Log("Stopping: the player or raid can no longer continue.");
                    return;
                }
                if (clean && _gateway.CountOutgoingContents() == 0)
                {
                    return;
                }
                if (attempt == MaxTransferAttempts)
                {
                    return;
                }

                // Never replay. Read the world again; it may have changed for a good reason.
                _gateway.Log($"Attempt {attempt} did not finish; planning again from current state.");
                await _gateway.NextFrame();
                if (!_gateway.CanContinue())
                {
                    return;
                }
                var request = _gateway.BuildRequest();
                var arrangement = await _gateway.SolveAsync(request);
                if (arrangement.Status != ArrangementStatus.Found)
                {
                    _gateway.Log("Re-plan found no arrangement: " + arrangement.Status);
                    return;
                }
                steps = MovePlanner.Plan(request, arrangement.Placements);
                if (steps == null)
                {
                    _gateway.Log("Re-plan found no move order.");
                    return;
                }
                string error = _gateway.DryRun(steps);
                if (error != null)
                {
                    _gateway.Log("Re-plan refused by dry run: " + error);
                    return;
                }
            }
        }

        private async Task<bool> RunSteps(IReadOnlyList<MoveStep> steps)
        {
            foreach (var step in steps)
            {
                // Operations are spaced one per frame; UI Fixes documents bugs from running several in one.
                await _gateway.NextFrame();
                if (!_gateway.CanContinue())
                {
                    return false;
                }
                if (!_gateway.IsStepValid(step))
                {
                    _gateway.Log("Stale step: " + step);
                    return false;
                }
                var outcome = await WithTimeout(_gateway.ExecuteMoveAsync(step));
                if (!outcome.Succeeded)
                {
                    _gateway.Log($"Move refused: {step} - {outcome.Error}");
                    return false;
                }
            }
            return true;
        }

        private async Task<OperationOutcome> WithTimeout(Task<OperationOutcome> operation)
        {
            var finished = await Task.WhenAny(operation, _gateway.AckTimeout());
            if (finished == operation)
            {
                return await operation;
            }
            // The operation may still land. Callers go on to read real state, never assume either way.
            return OperationOutcome.Failure("No acknowledgment from the inventory system.");
        }

        // A swap with no answer may land after the player was already told it did not confirm. If it
        // does, the contents are still in the old container, and the player has to be told that too.
        private async void WatchLateSwap(Task<OperationOutcome> swapTask)
        {
            try
            {
                await swapTask;
                if (_gateway.IsSwapApplied())
                {
                    _gateway.Log("The unconfirmed swap landed late.");
                    Finish();
                }
            }
            catch (Exception ex)
            {
                _gateway.LogError("Late swap failed: " + ex);
            }
        }

        private void Finish()
        {
            try
            {
                _gateway.RestoreItemData();
            }
            catch (Exception ex)
            {
                _gateway.LogError("Could not restore hotkeys and tags: " + ex);
            }

            int left;
            try
            {
                left = _gateway.CountOutgoingContents();
            }
            catch (Exception ex)
            {
                _gateway.LogError("Could not count remaining items: " + ex);
                return;
            }
            if (left > 0)
            {
                _gateway.Notify(Messages.Interrupted(_kind, left));
            }
        }
    }
}
