using System.Collections.Generic;

namespace SeamlessSwap.Core
{
    // Plain copies of live inventory state. Nothing in Core may reference Unity or game types, so the
    // solver can run on a worker thread and the whole of Core can be tested without the game.

    /// <summary>Which container a grid belongs to.</summary>
    public enum ContainerSide
    {
        /// <summary>The container coming from the corpse; it holds everything once the swap is done.</summary>
        Incoming,
        /// <summary>The player's current container; it must end up empty.</summary>
        Outgoing,
    }

    public sealed class GridSnapshot
    {
        public int Width { get; }
        public int Height { get; }
        /// <summary>The grid's own limit on how many items it holds, or null when it has none.</summary>
        public int? MaxItems { get; }

        public GridSnapshot(int width, int height, int? maxItems = null)
        {
            Width = width;
            Height = height;
            MaxItems = maxItems;
        }

        public int Cells => Width * Height;
    }

    /// <summary>A position inside one grid of one container. Rotated means the game's vertical rotation.</summary>
    public readonly struct Placement
    {
        public readonly ContainerSide Side;
        public readonly int Grid;
        public readonly int X;
        public readonly int Y;
        public readonly bool Rotated;

        public Placement(ContainerSide side, int grid, int x, int y, bool rotated)
        {
            Side = side;
            Grid = grid;
            X = x;
            Y = y;
            Rotated = rotated;
        }

        public bool SameAs(in Placement other)
        {
            return Side == other.Side && Grid == other.Grid && X == other.X && Y == other.Y && Rotated == other.Rotated;
        }

        public override string ToString()
        {
            return $"{Side}[{Grid}]({X},{Y}{(Rotated ? ",r" : "")})";
        }
    }

    /// <summary>
    /// One direct content of either container. Nested items are never listed: a case is one item, with
    /// its current footprint, and its descendants travel with it.
    /// </summary>
    public sealed class ItemSnapshot
    {
        public string Id { get; }
        /// <summary>Footprint at the game's horizontal rotation, attachments and folding included.</summary>
        public int Width { get; }
        public int Height { get; }
        public Placement Current { get; }
        /// <summary>Per grid of the incoming container: do the game's rules allow this item there?</summary>
        public bool[] AllowedIncoming { get; }
        /// <summary>Same for the outgoing container; used only for scratch space while reordering.</summary>
        public bool[] AllowedOutgoing { get; }
        /// <summary>The item may not change position (a pinned or locked incoming item).</summary>
        public bool Fixed { get; }

        public ItemSnapshot(string id, int width, int height, Placement current, bool[] allowedIncoming, bool[] allowedOutgoing, bool isFixed = false)
        {
            Id = id;
            Width = width;
            Height = height;
            Current = current;
            AllowedIncoming = allowedIncoming;
            AllowedOutgoing = allowedOutgoing;
            Fixed = isFixed;
        }

        public int Area => Width * Height;
        public bool Square => Width == Height;
    }

    public sealed class ArrangementRequest
    {
        public IReadOnlyList<GridSnapshot> IncomingGrids { get; }
        public IReadOnlyList<GridSnapshot> OutgoingGrids { get; }
        public IReadOnlyList<ItemSnapshot> Items { get; }

        public ArrangementRequest(IReadOnlyList<GridSnapshot> incomingGrids, IReadOnlyList<GridSnapshot> outgoingGrids, IReadOnlyList<ItemSnapshot> items)
        {
            IncomingGrids = incomingGrids;
            OutgoingGrids = outgoingGrids;
            Items = items;
        }
    }

    public enum ArrangementStatus
    {
        /// <summary>A complete legal arrangement was found.</summary>
        Found,
        /// <summary>The search was exhaustive: no legal arrangement exists.</summary>
        Impossible,
        /// <summary>The search stopped at its limit, so it is unknown whether an arrangement exists.</summary>
        BudgetExhausted,
        /// <summary>An item's rules forbid every grid of the incoming container. Space is not the problem.</summary>
        Restricted,
    }

    public sealed class ArrangementResult
    {
        public ArrangementStatus Status { get; }
        /// <summary>Final position by item id, when Status is Found.</summary>
        public IReadOnlyDictionary<string, Placement> Placements { get; }
        /// <summary>The items that caused Status Restricted.</summary>
        public IReadOnlyList<string> RestrictedItems { get; }
        public long NodesVisited { get; }
        /// <summary>True when items already in the incoming container had to move.</summary>
        public bool RearrangedIncoming { get; }

        public ArrangementResult(ArrangementStatus status, IReadOnlyDictionary<string, Placement> placements, IReadOnlyList<string> restrictedItems, long nodesVisited, bool rearrangedIncoming)
        {
            Status = status;
            Placements = placements;
            RestrictedItems = restrictedItems;
            NodesVisited = nodesVisited;
            RearrangedIncoming = rearrangedIncoming;
        }
    }

    /// <summary>One ordered inventory move. ToScratch marks a temporary stop that a later step moves on from.</summary>
    public readonly struct MoveStep
    {
        public readonly string ItemId;
        public readonly Placement From;
        public readonly Placement To;
        public readonly bool ToScratch;

        public MoveStep(string itemId, Placement from, Placement to, bool toScratch)
        {
            ItemId = itemId;
            From = from;
            To = to;
            ToScratch = toScratch;
        }

        public override string ToString()
        {
            return $"{ItemId}: {From} -> {To}{(ToScratch ? " (scratch)" : "")}";
        }
    }
}
