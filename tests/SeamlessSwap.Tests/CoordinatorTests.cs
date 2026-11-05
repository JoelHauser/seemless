using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SeamlessSwap.Core;

namespace SeamlessSwap.Tests
{
    // A plain inventory model with the game's rules that matter here: one item per location, no overlap,
    // moves free their own cells first. It can refuse any operation, withhold acknowledgments, or change
    // state between steps, and it records every item so loss and duplication are detectable.
    internal sealed class FakeInventory : ISwapGateway
    {
        public sealed class Thing
        {
            public string Id;
            public int W, H;
            public Placement At;
            public bool[] AllowedIn;
        }

        public readonly List<Thing> Things = new List<Thing>();
        public GridSnapshot[] IncomingGrids;
        public GridSnapshot[] OutgoingGrids;
        public bool Swapped;
        public readonly List<string> Notifications = new List<string>();
        public readonly List<string> Calls = new List<string>();
        public string PreconditionError;
        public string DryRunError;
        public string SwapError;
        public int RefuseMoveNumber = -1;
        public int RefuseMoveCount = 1;
        public int StopContinuingAfterMoves = -1;
        public Action<int> BeforeMove;
        public Action AfterSolve;
        public TaskCompletionSource<OperationOutcome> HeldSwap;
        public bool AckTimesOutImmediately;
        public long SolverBudget = ArrangementSolver.DefaultNodeBudget;
        public int MovesExecuted;
        public int SwapsExecuted;
        public int RebindCalls;
        public bool ThrowInDryRun;

        public Thing AddOut(string id, int w, int h, int grid, int x, int y, bool rotated = false)
        {
            return Add(id, w, h, new Placement(ContainerSide.Outgoing, grid, x, y, rotated));
        }

        public Thing AddIn(string id, int w, int h, int grid, int x, int y, bool rotated = false)
        {
            return Add(id, w, h, new Placement(ContainerSide.Incoming, grid, x, y, rotated));
        }

        private Thing Add(string id, int w, int h, Placement at)
        {
            var t = new Thing { Id = id, W = w, H = h, At = at };
            Things.Add(t);
            return t;
        }

        public string ValidatePreconditions()
        {
            Calls.Add("validate");
            return PreconditionError;
        }

        public ArrangementRequest BuildRequest()
        {
            Calls.Add("snapshot");
            var items = Things.Select(t => new ItemSnapshot(t.Id, t.W, t.H, t.At,
                t.AllowedIn ?? Enumerable.Repeat(true, IncomingGrids.Length).ToArray(),
                Enumerable.Repeat(true, OutgoingGrids.Length).ToArray())).ToList();
            return new ArrangementRequest(IncomingGrids, OutgoingGrids, items);
        }

        public string Fingerprint()
        {
            return Swapped + "|" + string.Join(";", Things.OrderBy(t => t.Id, StringComparer.Ordinal).Select(t => t.Id + "@" + t.At));
        }

        public Task<ArrangementResult> SolveAsync(ArrangementRequest request)
        {
            Calls.Add("solve");
            var result = ArrangementSolver.Solve(request, SolverBudget);
            AfterSolve?.Invoke();
            return Task.FromResult(result);
        }

        public string DryRun(IReadOnlyList<MoveStep> steps)
        {
            Calls.Add("dryrun");
            if (ThrowInDryRun) throw new InvalidOperationException("boom");
            return DryRunError;
        }

        public Task<OperationOutcome> ExecuteSwapAsync()
        {
            Calls.Add("swap");
            SwapsExecuted++;
            if (HeldSwap != null)
            {
                return HeldSwap.Task;
            }
            if (SwapError != null)
            {
                return Task.FromResult(OperationOutcome.Failure(SwapError));
            }
            Swapped = true;
            return Task.FromResult(OperationOutcome.Success);
        }

        public bool IsSwapApplied() => Swapped;

        public bool IsStepValid(in MoveStep step)
        {
            string id = step.ItemId;
            var thing = Things.FirstOrDefault(t => t.Id == id);
            return thing != null && thing.At.SameAs(step.From) && FreeFor(thing, step.To);
        }

        private bool FreeFor(Thing thing, Placement to)
        {
            int w = to.Rotated ? thing.H : thing.W;
            int h = to.Rotated ? thing.W : thing.H;
            foreach (var other in Things)
            {
                if (other == thing || other.At.Side != to.Side || other.At.Grid != to.Grid) continue;
                int ow = other.At.Rotated ? other.H : other.W;
                int oh = other.At.Rotated ? other.W : other.H;
                if (to.X < other.At.X + ow && other.At.X < to.X + w && to.Y < other.At.Y + oh && other.At.Y < to.Y + h)
                {
                    return false;
                }
            }
            return true;
        }

        public Task<OperationOutcome> ExecuteMoveAsync(MoveStep step)
        {
            int number = MovesExecuted++;
            BeforeMove?.Invoke(number);
            if (RefuseMoveNumber >= 0 && number >= RefuseMoveNumber && number < RefuseMoveNumber + RefuseMoveCount)
            {
                return Task.FromResult(OperationOutcome.Failure("refused by test"));
            }
            string id = step.ItemId;
            var thing = Things.First(t => t.Id == id);
            Check.True(FreeFor(thing, step.To), "executed a move onto an occupied location: " + step);
            thing.At = step.To;
            return Task.FromResult(OperationOutcome.Success);
        }

        public int CountOutgoingContents() => Things.Count(t => t.At.Side == ContainerSide.Outgoing);

        public bool CanContinue() => StopContinuingAfterMoves < 0 || MovesExecuted < StopContinuingAfterMoves;

        public Task NextFrame() => Task.CompletedTask;

        public Task AckTimeout() => AckTimesOutImmediately ? Task.CompletedTask : new TaskCompletionSource<bool>().Task;

        public void RestoreItemData() => RebindCalls++;

        public void Notify(string message) => Notifications.Add(message);

        public void Log(string message) { }

        public void LogError(string message) { }

        public void AssertEveryItemExactlyOnce(params string[] ids)
        {
            var have = Things.Select(t => t.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var want = ids.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Check.True(have.SequenceEqual(want), "item set changed: " + string.Join(",", have));
            foreach (var thing in Things)
            {
                Check.True(FreeFor(thing, thing.At), thing.Id + " overlaps another item");
            }
        }
    }

    internal static class CoordinatorTests
    {
        private static FakeInventory Standard()
        {
            var inv = new FakeInventory
            {
                IncomingGrids = new[] { new GridSnapshot(6, 6) },
                OutgoingGrids = new[] { new GridSnapshot(4, 4) },
            };
            inv.AddOut("ammo", 1, 1, 0, 0, 0);
            inv.AddOut("meds", 2, 1, 0, 1, 0);
            inv.AddOut("loot", 2, 2, 0, 0, 1);
            inv.AddIn("theirs", 2, 2, 0, 0, 0);
            return inv;
        }

        private static readonly string[] StandardIds = { "ammo", "meds", "loot", "theirs" };

        private static Task Run(FakeInventory inv, SwapKind kind = SwapKind.Backpack)
        {
            return new SwapCoordinator(inv, kind).RunAsync();
        }

        private static void AssertUnchangedAfterRejection(FakeInventory inv, string before, string message)
        {
            Check.Equal(before, inv.Fingerprint(), "inventory changed by a rejected swap");
            Check.Equal(0, inv.SwapsExecuted, "swap executed");
            Check.Equal(0, inv.MovesExecuted, "moves executed");
            Check.Equal(1, inv.Notifications.Count, "notification count");
            Check.Equal(message, inv.Notifications[0], "message");
        }

        [Test]
        private static async Task Success_FinalStateAndSilent()
        {
            var inv = Standard();
            await Run(inv);
            Check.True(inv.Swapped, "not swapped");
            Check.Equal(0, inv.CountOutgoingContents(), "left in outgoing");
            inv.AssertEveryItemExactlyOnce(StandardIds);
            Check.Equal(0, inv.Notifications.Count, "success must be silent");
            Check.Equal(1, inv.RebindCalls, "bindings reconciled");
        }

        [Test]
        private static async Task Order_ValidatesAndDryRunsBeforeTheSwap()
        {
            var inv = Standard();
            await Run(inv);
            int solve = inv.Calls.IndexOf("solve"), dry = inv.Calls.IndexOf("dryrun"), swap = inv.Calls.IndexOf("swap");
            Check.True(solve >= 0 && solve < dry && dry < swap, "order was " + string.Join(",", inv.Calls));
        }

        [Test]
        private static async Task Precondition_RejectsWithoutChange()
        {
            var inv = Standard();
            inv.PreconditionError = Messages.SearchRequired;
            string before = inv.Fingerprint();
            await Run(inv);
            AssertUnchangedAfterRejection(inv, before, Messages.SearchRequired);
        }

        [Test]
        private static async Task BodyArmor_RejectsWithoutChange()
        {
            var inv = Standard();
            inv.PreconditionError = Messages.BodyArmorConflict;
            string before = inv.Fingerprint();
            await Run(inv, SwapKind.Rig);
            AssertUnchangedAfterRejection(inv, before, Messages.BodyArmorConflict);
        }

        [Test]
        private static async Task NoSpace_Backpack_And_Rig_Wording()
        {
            foreach (var (kind, message) in new[] { (SwapKind.Backpack, Messages.BackpackNoSpace), (SwapKind.Rig, Messages.RigNoSpace) })
            {
                var inv = Standard();
                inv.IncomingGrids = new[] { new GridSnapshot(1, 1) };
                string before = inv.Fingerprint();
                await Run(inv, kind);
                AssertUnchangedAfterRejection(inv, before, message);
            }
        }

        [Test]
        private static async Task SearchLimit_HasItsOwnMessage()
        {
            // Plenty of cells, so only the budget can stop it.
            var inv = Standard();
            inv.SolverBudget = 1;
            string before = inv.Fingerprint();
            await Run(inv);
            AssertUnchangedAfterRejection(inv, before, Messages.SearchLimit);
        }

        [Test]
        private static async Task RestrictedItem_NotReportedAsSpace()
        {
            var inv = Standard();
            inv.Things[0].AllowedIn = new[] { false };
            string before = inv.Fingerprint();
            await Run(inv);
            AssertUnchangedAfterRejection(inv, before, Messages.Restricted);
        }

        [Test]
        private static async Task DryRunRefusal_RejectsWithoutChange()
        {
            var inv = Standard();
            inv.DryRunError = "The game refused this.";
            string before = inv.Fingerprint();
            await Run(inv);
            AssertUnchangedAfterRejection(inv, before, "The game refused this.");
        }

        [Test]
        private static async Task InventoryChangedDuringPlanning_RejectsWithoutChange()
        {
            var inv = Standard();
            inv.AfterSolve = () => inv.Things.First(t => t.Id == "theirs").At = new Placement(ContainerSide.Incoming, 0, 4, 4, false);
            await Run(inv);
            Check.Equal(0, inv.SwapsExecuted, "swap ran on stale data");
            Check.Equal(0, inv.MovesExecuted, "moves ran on stale data");
            Check.Equal(Messages.InventoryChanged, inv.Notifications.Single(), "message");
        }

        [Test]
        private static async Task SwapRefused_ChangesNothing()
        {
            var inv = Standard();
            inv.SwapError = "Refused by host";
            string before = inv.Fingerprint();
            await Run(inv);
            Check.Equal(before, inv.Fingerprint(), "state changed");
            Check.Equal(0, inv.MovesExecuted, "moves ran after a refused swap");
            Check.Equal("Refused by host", inv.Notifications.Single(), "message");
        }

        [Test]
        private static async Task EveryMoveFailurePoint_NoLossOrDuplicate_EitherRecoversOrReports()
        {
            // Inject a refusal at each move in turn, for one refusal and for all later moves too.
            for (int failAt = 0; failAt < 3; failAt++)
            {
                foreach (int count in new[] { 1, 99 })
                {
                    var inv = Standard();
                    inv.RefuseMoveNumber = failAt;
                    inv.RefuseMoveCount = count;
                    await Run(inv);
                    inv.AssertEveryItemExactlyOnce(StandardIds);
                    Check.True(inv.Swapped, "swap should stand");
                    int left = inv.CountOutgoingContents();
                    if (count == 1)
                    {
                        Check.Equal(0, left, $"a single refusal at {failAt} should recover by planning again");
                        Check.Equal(0, inv.Notifications.Count, "a recovered swap is silent");
                    }
                    else
                    {
                        Check.True(left > 0, "expected items to remain");
                        Check.Equal(Messages.Interrupted(SwapKind.Backpack, left), inv.Notifications.Single(), "must report the real count once");
                    }
                }
            }
        }

        [Test]
        private static async Task ReplanAttempts_AreBounded_NoEndlessReplay()
        {
            var inv = Standard();
            inv.RefuseMoveNumber = 0;
            inv.RefuseMoveCount = int.MaxValue / 2;
            await Run(inv);
            Check.True(inv.MovesExecuted <= 9, "too many attempts: " + inv.MovesExecuted);
        }

        [Test]
        private static async Task AnotherActorTakesAnItem_MidTransfer()
        {
            var inv = Standard();
            inv.BeforeMove = n =>
            {
                if (n == 0)
                {
                    // Someone removes an item from the old bag before it can be moved.
                    inv.Things.RemoveAll(t => t.Id == "loot");
                }
            };
            await Run(inv);
            inv.AssertEveryItemExactlyOnce("ammo", "meds", "theirs");
            Check.Equal(0, inv.CountOutgoingContents(), "the rest should still arrive");
            Check.Equal(0, inv.Notifications.Count, "nothing of the player's is unaccounted for");
        }

        [Test]
        private static async Task DestinationTakenMidTransfer_ReplansAroundIt()
        {
            var inv = Standard();
            bool once = false;
            inv.BeforeMove = n =>
            {
                if (!once)
                {
                    once = true;
                    inv.AddIn("dropped", 6, 1, 0, 0, 5);
                }
            };
            await Run(inv);
            inv.AssertEveryItemExactlyOnce("ammo", "meds", "loot", "theirs", "dropped");
            Check.Equal(0, inv.CountOutgoingContents(), "left in outgoing");
        }

        [Test]
        private static async Task PlayerDies_StopsWithoutFurtherMoves_ReportsRemainder()
        {
            var inv = Standard();
            inv.StopContinuingAfterMoves = 1;
            await Run(inv);
            Check.Equal(1, inv.MovesExecuted, "kept moving after death");
            inv.AssertEveryItemExactlyOnce(StandardIds);
            Check.Equal(Messages.Interrupted(SwapKind.Backpack, 2), inv.Notifications.Single(), "message");
        }

        [Test]
        private static async Task SwapUnacknowledged_NoFalseFailureIfItLands()
        {
            var inv = Standard();
            inv.HeldSwap = new TaskCompletionSource<OperationOutcome>();
            inv.AckTimesOutImmediately = true;
            await Run(inv);
            Check.Equal(0, inv.MovesExecuted, "moved contents without a confirmed swap");
            Check.Equal(Messages.NoAcknowledgment, inv.Notifications.Single(), "message");

            // The answer arrives after all: the swap happened, so the player must be told what remains.
            inv.Swapped = true;
            inv.HeldSwap.SetResult(OperationOutcome.Success);
            await Task.Yield();
            Check.Equal(2, inv.Notifications.Count, "late landing was not reported");
            Check.Equal(Messages.Interrupted(SwapKind.Backpack, 3), inv.Notifications[1], "message");
            inv.AssertEveryItemExactlyOnce(StandardIds);
        }

        [Test]
        private static async Task SwapAckLostButApplied_Continues()
        {
            var inv = Standard();
            inv.HeldSwap = new TaskCompletionSource<OperationOutcome>();
            inv.AckTimesOutImmediately = true;
            inv.Swapped = true; // Applied, answer lost.
            await Run(inv);
            Check.Equal(0, inv.CountOutgoingContents(), "should finish from real state");
            Check.Equal(0, inv.Notifications.Count, "silent success");
        }

        [Test]
        private static async Task ExceptionBeforeSwap_ReportsAndChangesNothing()
        {
            var inv = Standard();
            inv.ThrowInDryRun = true;
            string before = inv.Fingerprint();
            await Run(inv);
            Check.Equal(before, inv.Fingerprint(), "state changed");
            Check.Equal(Messages.InternalError, inv.Notifications.Single(), "message");
        }

        [Test]
        private static async Task OneDrop_OneNotification_EvenOnLateFailure()
        {
            var inv = Standard();
            inv.RefuseMoveNumber = 1;
            inv.RefuseMoveCount = 99;
            await Run(inv);
            Check.Equal(1, inv.Notifications.Count, "notifications per drop");
        }

        [Test]
        private static async Task RearrangingBothSets_ThroughTheWholeFlow()
        {
            var inv = new FakeInventory
            {
                IncomingGrids = new[] { new GridSnapshot(2, 2), new GridSnapshot(3, 3) },
                OutgoingGrids = new[] { new GridSnapshot(3, 3) },
            };
            inv.AddOut("armor", 3, 3, 0, 0, 0);
            inv.AddIn("small", 1, 1, 1, 1, 1);
            await Run(inv);
            inv.AssertEveryItemExactlyOnce("armor", "small");
            Check.Equal(0, inv.CountOutgoingContents(), "left in outgoing");
            Check.Equal(1, inv.Things.First(t => t.Id == "armor").At.Grid, "armor grid");
        }
    }
}
