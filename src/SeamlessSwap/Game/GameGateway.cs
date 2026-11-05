using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using SeamlessSwap.Core;

namespace SeamlessSwap.Game
{
    /// <summary>Starts swaps, one at a time. A second drop during a swap is refused, never queued.</summary>
    internal static class SwapRunner
    {
        private static bool _active;

        internal static async Task Start(SlotView view, Slot target, Item dragged, Player.PlayerInventoryController controller, SwapKind kind)
        {
            if (_active)
            {
                NotificationManager.DisplayWarningNotification(Messages.Busy);
                return;
            }

            _active = true;
            try
            {
                var gateway = new GameGateway(target, (CompoundItem)dragged, controller, kind, new LiveEnvironment(view, controller));
                await new SwapCoordinator(gateway, kind).RunAsync();
            }
            catch (Exception ex)
            {
                SeamlessSwapPlugin.Log.LogError("Swap failed: " + ex);
            }
            finally
            {
                _active = false;
            }
        }
    }

    /// <summary>
    /// The little a swap needs from outside the inventory system. Kept apart so that the rest of the gateway
    /// can run against the game's real inventory rules in tools/GameHarness, where no UI exists.
    /// </summary>
    internal interface ISwapEnvironment
    {
        bool ScreenOpen { get; }
        bool PlayerCanAct { get; }
        void Notify(string message);
        void Log(string message);
        void LogError(string message);
    }

    internal sealed class LiveEnvironment : ISwapEnvironment
    {
        private readonly SlotView _view;
        private readonly Player.PlayerInventoryController _controller;

        public LiveEnvironment(SlotView view, Player.PlayerInventoryController controller)
        {
            _view = view;
            _controller = controller;
        }

        public bool ScreenOpen => _view != null && _view.isActiveAndEnabled;

        public bool PlayerCanAct
        {
            get
            {
                var player = _controller.Player;
                return player != null
                    && Singleton<GameWorld>.Instantiated
                    && player.HealthController != null
                    && player.HealthController.IsAlive;
            }
        }

        public void Notify(string message)
        {
            NotificationManager.DisplayWarningNotification(message);
        }

        public void Log(string message)
        {
            if (SeamlessSwapPlugin.DiagnosticLogging.Value)
            {
                SeamlessSwapPlugin.Log.LogInfo(message);
            }
        }

        public void LogError(string message)
        {
            SeamlessSwapPlugin.Log.LogError(message);
        }
    }

    /// <summary>
    /// The live-game side of a swap: reads state through the game's own checks, and acts only through its
    /// supported operations (Swap, Move, Bind, Unbind, SetTag). Runs on Unity's main thread, except that
    /// the solver gets a copied snapshot on a worker.
    /// </summary>
    internal sealed class GameGateway : ISwapGateway
    {
        private const int AckTimeoutMilliseconds = 10000;
        private const string ArmorSlotId = nameof(EquipmentSlot.ArmorVest);

        private readonly ISwapEnvironment _environment;
        private readonly Slot _target;
        private readonly Slot _source;
        private readonly CompoundItem _incoming;
        private readonly CompoundItem _outgoing;
        private readonly InventoryController _controller;
        private readonly SwapKind _kind;
        private readonly ItemAddress _targetAddress;
        private readonly ItemAddress _sourceAddress;

        // Captured once, before the first change. Key is the quick-use index; the item identity is the point.
        private List<KeyValuePair<EBoundItem, Item>> _bindings;
        private List<SavedTag> _tags;

        private readonly struct SavedTag
        {
            public readonly TagComponent Tag;
            public readonly string Name;
            public readonly int Color;

            public SavedTag(TagComponent tag)
            {
                Tag = tag;
                Name = tag.Name;
                Color = tag.Color;
            }
        }

        public GameGateway(Slot target, CompoundItem incoming, InventoryController controller, SwapKind kind, ISwapEnvironment environment)
        {
            _environment = environment;
            _target = target;
            _incoming = incoming;
            _outgoing = (CompoundItem)target.ContainedItem;
            _controller = controller;
            _kind = kind;
            _source = ((SlotItemAddress)incoming.Parent).Slot;
            _targetAddress = _target.CreateItemAddress();
            _sourceAddress = _source.CreateItemAddress();
        }

        public string ValidatePreconditions()
        {
            // Stale references or a closed screen: the drop no longer means what it meant.
            if (!_environment.ScreenOpen)
            {
                return "The inventory screen closed before the swap could start.";
            }
            if (_target.ContainedItem != _outgoing || _source.ContainedItem != _incoming)
            {
                return Messages.InventoryChanged;
            }
            if (!CanContinue())
            {
                return "You can't swap equipment right now.";
            }
            if (_controller is Player.PlayerInventoryController owned && owned.HasDiscardLimits)
            {
                // Limits can destroy items as part of a move, which this mod never allows.
                return "Item limits are active, so an automatic swap isn't safe here.";
            }

            // Search state comes first: nothing else should reveal anything about hidden contents.
            // Both containers must count as searched: the game refuses any move out of a searchable grid
            // whose container isn't (TryFindChangedContainer), and the outgoing one is moved from too.
            // An unsearched nested case is fine; it travels closed, and its own state is untouched.
            var search = _controller.SearchController;
            foreach (var container in new[] { _incoming, _outgoing })
            {
                if (container is SearchableItem searchable)
                {
                    bool hasContents = DirectContents(container).Any();
                    if (search.ContainsUnknownItems(searchable) || (!search.IsSearched(searchable) && (hasContents || container == _incoming)))
                    {
                        return Messages.SearchRequired;
                    }
                }
            }

            var hands = _controller.ItemInHands;
            if (hands != null && _outgoing.GetAllItems().Contains(hands))
            {
                return "Put away the item in your hands before swapping.";
            }

            // Attached modules stay with their parent. Anything loose inside one would be left behind.
            foreach (var slot in _outgoing.AllSlots)
            {
                if (slot.ContainedItem is CompoundItem module && module.GetAllItems().Any(i => i != module && i.Parent is GridItemAddress))
                {
                    return "Empty the pouches attached to your current container before swapping.";
                }
            }

            foreach (var item in DirectContents(_outgoing))
            {
                var check = item.CheckAction(null);
                if (check.Failed)
                {
                    return Describe(check.Error);
                }
            }

            // The game's own verdict on both directions, including armor conflicts on each side. Simulated,
            // so it is the same check UI Fixes runs on hover.
            var swap = ItemManipulator.Swap(_incoming, _targetAddress, _outgoing, _sourceAddress, _controller, true);
            if (swap.Failed)
            {
                return Describe(swap.Error);
            }
            if (swap.Value.ItemsDestroyRequired)
            {
                return "This swap would destroy items, so it was cancelled.";
            }
            if (!swap.Value.CanExecute(_controller))
            {
                return "You can't swap equipment right now.";
            }
            return null;
        }

        private string Describe(Error error)
        {
            if (error is Slot.ConflictingSlotBlockedError conflict && conflict.ConflictingSlot?.ID == ArmorSlotId)
            {
                return conflict.Slot == _target
                    ? Messages.BodyArmorConflict
                    : $"Your {KindName} can't go on this body: it conflicts with the armor the body is wearing.";
            }
            try
            {
                if (error is LocalizedError localized)
                {
                    string text = localized.GetLocalizedDescription();
                    if (!string.IsNullOrEmpty(text))
                    {
                        return text;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Could not localize " + error + ": " + ex.Message);
            }
            return Messages.Restricted;
        }

        private string KindName => _kind == SwapKind.Backpack ? "backpack" : "rig";

        private static IEnumerable<Item> DirectContents(CompoundItem container)
        {
            return container.Grids.SelectMany(g => g.Items);
        }

        public ArrangementRequest BuildRequest()
        {
            CaptureOnce();

            var items = new List<ItemSnapshot>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            AddItems(items, seen, _incoming, ContainerSide.Incoming);
            AddItems(items, seen, _outgoing, ContainerSide.Outgoing);

            return new ArrangementRequest(Copy(_incoming), Copy(_outgoing), items);
        }

        private static GridSnapshot[] Copy(CompoundItem container)
        {
            return container.Grids.Select(g => new GridSnapshot(g.GridWidth, g.GridHeight, g.MaxItemsCount)).ToArray();
        }

        private void AddItems(List<ItemSnapshot> items, HashSet<string> seen, CompoundItem container, ContainerSide side)
        {
            for (int g = 0; g < container.Grids.Length; g++)
            {
                var grid = container.Grids[g];
                foreach (var item in grid.Items.ToList())
                {
                    // An item is direct contents of one grid only. A repeat means state this mod can't trust.
                    if (!seen.Add(item.Id))
                    {
                        throw new InvalidOperationException("Item listed twice: " + item.Id);
                    }

                    var location = grid.GetItemLocation(item);
                    var size = item.CalculateCellSize();
                    var placement = new Placement(side, g, location.x, location.y, location.r == ItemRotation.Vertical);
                    var inIncoming = _incoming.Grids.Select(target => IsAllowed(item, target)).ToArray();
                    var inOutgoing = _outgoing.Grids.Select(target => IsAllowed(item, target)).ToArray();

                    // Already inside one of its grids counts as allowed, whatever filters say now.
                    if (side == ContainerSide.Incoming)
                    {
                        inIncoming[g] = true;
                    }

                    bool isFixed = item.PinLockState != EItemPinLockState.Free || item.CheckAction(null).Failed;
                    items.Add(new ItemSnapshot(item.Id, size.X, size.Y, placement, inIncoming, inOutgoing, isFixed));
                }
            }
        }

        // Every rule here is the game's own check. Nested containers and Pack 'n' Strap's recursion
        // filters are covered by CheckCompatibility, which runs the item's ParentRecursiveCheck.
        private bool IsAllowed(Item item, Grid grid)
        {
            if (!grid.CheckCompatibility(item))
            {
                return false;
            }
            if (item is IMoveCheckable moveCheckable && moveCheckable.CanBeMoved(grid).Failed)
            {
                return false;
            }

            var address = grid.CreateItemAddress(new LocationInGrid(0, 0, ItemRotation.Horizontal));
            if (ItemManipulator.DestinationCheck(item.Parent, address, _controller).Failed)
            {
                return false;
            }
            if (!ItemManipulator.CanTransferTo(address, _controller, out _))
            {
                return false;
            }

            int limit = Singleton<GlobalConfiguration>.Instance?.ItemsSettings?.MaxBackpackInserting ?? 0;
            return limit <= 0 || !ItemManipulator.GetBackpacksDeep(item, address, out int deep) || deep <= limit;
        }

        // Bindings and tags exactly as the player left them, before any operation can touch them.
        private void CaptureOnce()
        {
            if (_bindings != null)
            {
                return;
            }

            var carried = new HashSet<Item>(_outgoing.GetAllItems());
            _bindings = _controller.Inventory.FastAccess.BoundItems.Where(pair => pair.Value != null && carried.Contains(pair.Value)).ToList();

            // Move clears tags on any compound item that changes owner (ItemManipulator.ResetTags).
            _tags = DirectContents(_outgoing)
                .SelectMany(item => item.GetItemComponentsInChildren<TagComponent>(false))
                .Where(tag => !tag.IsEmpty)
                .Select(tag => new SavedTag(tag))
                .ToList();
        }

        public string Fingerprint()
        {
            var sb = new StringBuilder();
            sb.Append(_target.ContainedItem?.Id).Append('|').Append(_source.ContainedItem?.Id).Append('|');
            sb.Append(_controller.ItemInHands?.Id).Append('|');
            sb.Append(_controller.Inventory.Equipment.GetSlot(EquipmentSlot.ArmorVest)?.ContainedItem?.Id).Append('|');
            foreach (var container in new[] { _incoming, _outgoing })
            {
                for (int g = 0; g < container.Grids.Length; g++)
                {
                    var grid = container.Grids[g];
                    sb.Append(grid.GridWidth).Append('x').Append(grid.GridHeight).Append(':');
                    foreach (var item in grid.Items.OrderBy(i => i.Id, StringComparer.Ordinal))
                    {
                        var at = grid.GetItemLocation(item);
                        var size = item.CalculateCellSize();
                        sb.Append(item.Id).Append('@').Append(at.x).Append(',').Append(at.y).Append(',').Append((int)at.r)
                            .Append('#').Append(size.X).Append('x').Append(size.Y).Append(';');
                    }
                    sb.Append('/');
                }
            }
            return sb.ToString();
        }

        public Task<ArrangementResult> SolveAsync(ArrangementRequest request)
        {
            // The request is plain data with no references into the game, so this is safe off the main thread.
            return Task.Run(() => ArrangementSolver.Solve(request));
        }

        /// <summary>
        /// Runs the real moves without simulation and without raising events, then rolls them all back in
        /// the same frame. No frame, view or other actor can see it. A simulated move can't test a sequence,
        /// because each one would see the grid as it started; UI Fixes tests multi-item drops the same way.
        /// Bindings cleared on the way are restored by the rollback (UnbindResult.RollBack).
        ///
        /// Known-item state is keyed by address and only updates through events, which a dry run has none
        /// of. An item that moves twice (parked, then placed) would look unknown at its parking spot, so it
        /// is marked temporarily known here, which is what AddResult does at the start of a real move.
        /// </summary>
        public string DryRun(IReadOnlyList<MoveStep> steps)
        {
            var done = new Stack<MoveResult>();
            var search = _controller.SearchController as ActiveSearchController;
            var markedKnown = new List<Item>();
            try
            {
                foreach (var step in steps)
                {
                    var item = FindDirect(step.ItemId);
                    if (item == null)
                    {
                        return Messages.InventoryChanged;
                    }
                    var move = ItemManipulator.Move(item, AddressFor(step.To), _controller, false);
                    if (move.Failed)
                    {
                        Log($"Dry run refused {step}: {move.Error}");
                        return DescribeMoveRefusal(move.Error);
                    }
                    done.Push(move.Value);
                    if (search != null)
                    {
                        search.SetItemAsTemporaryKnown(item);
                        markedKnown.Add(item);
                    }
                    if (move.Value.ItemsDestroyRequired)
                    {
                        return "This swap would destroy items, so it was cancelled.";
                    }
                }
                return null;
            }
            finally
            {
                while (done.Count > 0)
                {
                    done.Pop().RollBack();
                }
                foreach (var item in markedKnown)
                {
                    search.RemoveItemFromTemporaryKnown(item);
                }
            }
        }

        private string DescribeMoveRefusal(Error error)
        {
            // The planner already proved the geometry, so a space error here means the plan and the game
            // disagree. "Not enough space" would be a false statement.
            if (error is Grid.NoFreeSpaceError || error is Grid.PlaceTakenByAnotherItemError || error is Grid.WontFitToGridError)
            {
                return Messages.InventoryChanged;
            }
            return Describe(error);
        }

        private Item FindDirect(string id)
        {
            foreach (var container in new[] { _incoming, _outgoing })
            {
                foreach (var grid in container.Grids)
                {
                    foreach (var item in grid.Items)
                    {
                        if (item.Id == id)
                        {
                            return item;
                        }
                    }
                }
            }
            return null;
        }

        private ItemAddress AddressFor(in Placement placement)
        {
            var container = placement.Side == ContainerSide.Incoming ? _incoming : _outgoing;
            var rotation = placement.Rotated ? ItemRotation.Vertical : ItemRotation.Horizontal;
            return container.Grids[placement.Grid].CreateItemAddress(new LocationInGrid(placement.X, placement.Y, rotation));
        }

        public async Task<OperationOutcome> ExecuteSwapAsync()
        {
            var swap = ItemManipulator.Swap(_incoming, _targetAddress, _outgoing, _sourceAddress, _controller, true);
            if (swap.Failed)
            {
                return OperationOutcome.Failure(Describe(swap.Error));
            }
            var result = await _controller.TryRunNetworkTransaction(swap);
            if (result.Succeed)
            {
                if (Singleton<GUISounds>.Instantiated)
                {
                    Singleton<GUISounds>.Instance.PlayItemSound(_incoming.ItemSound, EInventorySoundType.use);
                }
                return OperationOutcome.Success;
            }
            return OperationOutcome.Failure(DescribeResult(result));
        }

        private static string DescribeResult(IResult result)
        {
            return result.ErrorCode == ItemController.CANT_EXECUTE_ERROR_CODE
                ? "You're busy. Try the swap again in a moment."
                : "The swap was refused: " + result.Error;
        }

        public bool IsSwapApplied()
        {
            return _target.ContainedItem == _incoming && _source.ContainedItem == _outgoing;
        }

        public bool IsStepValid(in MoveStep step)
        {
            var item = FindDirect(step.ItemId);
            if (item == null || !(item.Parent is GridItemAddress at) || !IsAt(at, step.From))
            {
                return false;
            }
            // Simulated Move: the same answer the real call is about to give, freeing the item's own cells.
            return ItemManipulator.Move(item, AddressFor(step.To), _controller, true).Succeeded;
        }

        private bool IsAt(GridItemAddress address, in Placement placement)
        {
            var container = placement.Side == ContainerSide.Incoming ? _incoming : _outgoing;
            var location = address.LocationInGrid;
            return address.Grid == container.Grids[placement.Grid]
                && location.x == placement.X
                && location.y == placement.Y
                && (location.r == ItemRotation.Vertical) == placement.Rotated;
        }

        public async Task<OperationOutcome> ExecuteMoveAsync(MoveStep step)
        {
            var item = FindDirect(step.ItemId);
            if (item == null)
            {
                return OperationOutcome.Failure("Item is no longer available.");
            }
            var move = ItemManipulator.Move(item, AddressFor(step.To), _controller, true);
            if (move.Failed)
            {
                return OperationOutcome.Failure(move.Error.ToString());
            }
            if (move.Value.ItemsDestroyRequired)
            {
                return OperationOutcome.Failure("Move would destroy items.");
            }
            var result = await _controller.TryRunNetworkTransaction(move);
            return result.Succeed ? OperationOutcome.Success : OperationOutcome.Failure(DescribeResult(result));
        }

        public int CountOutgoingContents()
        {
            return _outgoing.Grids.Sum(g => g.Items.Count());
        }

        public bool CanContinue()
        {
            return _environment.PlayerCanAct && !_controller.IsInventoryBlocked();
        }

        public async Task NextFrame()
        {
            // Unity's synchronization context runs this on a later main-thread pass. Fika's own controllers
            // pace inventory operations the same way.
            await Task.Yield();
        }

        public Task AckTimeout()
        {
            return Task.Delay(AckTimeoutMilliseconds);
        }

        public void RestoreItemData()
        {
            RestoreTags();
            RestoreBindings();
        }

        private void RestoreTags()
        {
            if (_tags == null)
            {
                return;
            }
            foreach (var saved in _tags)
            {
                // Only what the move cleared. A tag someone has since set is theirs.
                if (saved.Tag.IsEmpty && saved.Tag.Item.Owner == _controller)
                {
                    var set = saved.Tag.Set(saved.Name, saved.Color, true);
                    _controller.TryRunNetworkTransaction(set);
                }
            }
        }

        // Native Move leaves these alone in this order of operations, so usually there is nothing to do.
        // This only repairs what is verifiably wrong, and goes through the game's own bind rules.
        private void RestoreBindings()
        {
            if (_bindings == null)
            {
                return;
            }
            var bound = _controller.Inventory.FastAccess.BoundItems;
            foreach (var pair in _bindings)
            {
                bound.TryGetValue(pair.Key, out var current);
                bool bindable = _controller.IsAtBindablePlace(pair.Value);

                if (current == pair.Value)
                {
                    if (!bindable)
                    {
                        // Left behind or no longer reachable. Keeping it would point a hotkey at a corpse.
                        // Unbind has no network operation (ConvertOperationResultToOperation rejects it),
                        // so it is applied directly and announced, which is how UI Fixes unbinds after a swap.
                        var unbind = UnbindResult.Run(_controller, pair.Value, pair.Key, false);
                        if (unbind.Succeeded)
                        {
                            unbind.Value.RaiseEvents(_controller, CommandStatus.Begin);
                            unbind.Value.RaiseEvents(_controller, CommandStatus.Succeed);
                        }
                    }
                }
                else if (current == null && bindable)
                {
                    _controller.TryRunNetworkTransaction(BindResult.Run(_controller, pair.Value, pair.Key, true));
                }
                // Anything else was rebound on purpose. Never overwritten, never substituted.
            }
        }

        public void Notify(string message)
        {
            _environment.Notify(message);
        }

        public void Log(string message)
        {
            _environment.Log(message);
        }

        public void LogError(string message)
        {
            _environment.LogError(message);
        }
    }
}
