using System;
using System.Collections.Generic;
using System.Linq;

namespace SeamlessSwap.Core
{
    /// <summary>
    /// Turns a final arrangement into an ordered list of single moves that are each legal when they run.
    ///
    /// A game move frees the item's own cells first, so an item may slide onto cells it already covers.
    /// A cycle of items that all want each other's cells has no legal first move; one of them is parked
    /// in free space (the outgoing container counts), and a later step takes it to its target. If no
    /// space exists anywhere the planner returns null and the swap is rejected before any change.
    /// </summary>
    public static class MovePlanner
    {
        public static List<MoveStep> Plan(ArrangementRequest request, IReadOnlyDictionary<string, Placement> targets)
        {
            var state = new State(request);
            var items = request.Items.OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
            var current = items.ToDictionary(i => i.Id, i => i.Current, StringComparer.Ordinal);
            foreach (var item in items)
            {
                state.Occupy(item, item.Current);
            }

            // Items already where they belong need no step.
            var pending = items.Where(i => !current[i.Id].SameAs(targets[i.Id])).ToList();
            // The player's own items first wherever their target is free, so that an interruption leaves
            // as few as possible in the bag on the body. Incoming items move as they come free to.
            pending = pending.OrderBy(i => i.Current.Side == ContainerSide.Outgoing ? 0 : 1)
                .ThenBy(i => i.Id, StringComparer.Ordinal)
                .ToList();

            var steps = new List<MoveStep>();
            var parked = new HashSet<string>(StringComparer.Ordinal);
            int guard = (pending.Count + 1) * (pending.Count + 1) * 2 + 8;

            while (pending.Count > 0)
            {
                if (--guard < 0)
                {
                    return null;
                }

                ItemSnapshot ready = pending.FirstOrDefault(i => state.CanMove(i, current[i.Id], targets[i.Id]));
                if (ready != null)
                {
                    steps.Add(new MoveStep(ready.Id, current[ready.Id], targets[ready.Id], false));
                    state.Vacate(ready, current[ready.Id]);
                    state.Occupy(ready, targets[ready.Id]);
                    current[ready.Id] = targets[ready.Id];
                    pending.Remove(ready);
                    continue;
                }

                // Deadlock. Park the first item that is still in the incoming container and not yet parked.
                ItemSnapshot victim = pending.FirstOrDefault(i => current[i.Id].Side == ContainerSide.Incoming && !parked.Contains(i.Id))
                    ?? pending.FirstOrDefault(i => !parked.Contains(i.Id));
                if (victim == null)
                {
                    return null;
                }

                if (!state.TryFindScratch(victim, current[victim.Id], targets, pending, out Placement scratch))
                {
                    return null;
                }

                steps.Add(new MoveStep(victim.Id, current[victim.Id], scratch, true));
                state.Vacate(victim, current[victim.Id]);
                state.Occupy(victim, scratch);
                current[victim.Id] = scratch;
                parked.Add(victim.Id);
            }

            return steps;
        }

        private sealed class State
        {
            private readonly bool[][,] _incoming;
            private readonly bool[][,] _outgoing;
            private readonly ArrangementRequest _request;

            public State(ArrangementRequest request)
            {
                _request = request;
                _incoming = request.IncomingGrids.Select(g => new bool[g.Width, g.Height]).ToArray();
                _outgoing = request.OutgoingGrids.Select(g => new bool[g.Width, g.Height]).ToArray();
            }

            private bool[,] Cells(in Placement p)
            {
                return p.Side == ContainerSide.Incoming ? _incoming[p.Grid] : _outgoing[p.Grid];
            }

            private static void Size(ItemSnapshot item, in Placement p, out int w, out int h)
            {
                w = p.Rotated ? item.Height : item.Width;
                h = p.Rotated ? item.Width : item.Height;
            }

            public void Occupy(ItemSnapshot item, in Placement p)
            {
                Set(item, p, true);
            }

            public void Vacate(ItemSnapshot item, in Placement p)
            {
                Set(item, p, false);
            }

            private void Set(ItemSnapshot item, in Placement p, bool value)
            {
                Size(item, p, out int w, out int h);
                var cells = Cells(p);
                for (int y = p.Y; y < p.Y + h; y++)
                {
                    for (int x = p.X; x < p.X + w; x++)
                    {
                        cells[x, y] = value;
                    }
                }
            }

            // Free apart from the item's own current cells, which the game releases before adding.
            public bool CanMove(ItemSnapshot item, in Placement from, in Placement to)
            {
                Vacate(item, from);
                bool free = IsFree(item, to);
                Occupy(item, from);
                return free;
            }

            private bool IsFree(ItemSnapshot item, in Placement p)
            {
                Size(item, p, out int w, out int h);
                var cells = Cells(p);
                if (p.X < 0 || p.Y < 0 || p.X + w > cells.GetLength(0) || p.Y + h > cells.GetLength(1))
                {
                    return false;
                }
                for (int y = p.Y; y < p.Y + h; y++)
                {
                    for (int x = p.X; x < p.X + w; x++)
                    {
                        if (cells[x, y])
                        {
                            return false;
                        }
                    }
                }
                return true;
            }

            public bool TryFindScratch(ItemSnapshot item, in Placement from, IReadOnlyDictionary<string, Placement> targets, List<ItemSnapshot> pending, out Placement scratch)
            {
                // First choice avoids every target still waiting, so parking does not create a new block.
                // The outgoing container is tried first: it empties anyway.
                foreach (bool avoidTargets in new[] { true, false })
                {
                    foreach (var candidate in Candidates(item, from))
                    {
                        if (avoidTargets && OverlapsPendingTarget(item, candidate, targets, pending))
                        {
                            continue;
                        }
                        scratch = candidate;
                        return true;
                    }
                }
                scratch = default;
                return false;
            }

            private IEnumerable<Placement> Candidates(ItemSnapshot item, Placement from)
            {
                foreach (var side in new[] { ContainerSide.Outgoing, ContainerSide.Incoming })
                {
                    var grids = side == ContainerSide.Outgoing ? _request.OutgoingGrids : _request.IncomingGrids;
                    var allowed = side == ContainerSide.Outgoing ? item.AllowedOutgoing : item.AllowedIncoming;
                    for (int g = 0; g < grids.Count; g++)
                    {
                        if (g >= allowed.Length || !allowed[g])
                        {
                            continue;
                        }
                        for (int y = 0; y < grids[g].Height; y++)
                        {
                            for (int x = 0; x < grids[g].Width; x++)
                            {
                                foreach (bool rotated in item.Square ? new[] { false } : new[] { false, true })
                                {
                                    var p = new Placement(side, g, x, y, rotated);
                                    if (!p.SameAs(from) && IsFree(item, p))
                                    {
                                        yield return p;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            private static bool OverlapsPendingTarget(ItemSnapshot item, in Placement candidate, IReadOnlyDictionary<string, Placement> targets, List<ItemSnapshot> pending)
            {
                Size(item, candidate, out int w, out int h);
                foreach (var other in pending)
                {
                    var t = targets[other.Id];
                    if (t.Side != candidate.Side || t.Grid != candidate.Grid)
                    {
                        continue;
                    }
                    Size(other, t, out int ow, out int oh);
                    if (candidate.X < t.X + ow && t.X < candidate.X + w && candidate.Y < t.Y + oh && t.Y < candidate.Y + h)
                    {
                        return true;
                    }
                }
                return false;
            }
        }
    }
}
