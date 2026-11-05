using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SeamlessSwap.Core
{
    /// <summary>
    /// Finds a legal position for every direct content of both containers inside the incoming container.
    ///
    /// Pockets stay separate: an item must fit whole in one grid, and free cells in different grids never
    /// add up. The search is backtracking with most-constrained-item-first, so "this armor only fits the
    /// main compartment" is decided before small items can take its room. Two stages keep the player's
    /// layout where possible:
    ///   A. Items already in the incoming container stay put; only the outgoing items are placed.
    ///   B. Everything except fixed items may move; original positions are simply tried first.
    /// Only an exhausted stage B proves that no arrangement exists. Node counts, not wall-clock time, limit
    /// the search, so the same input always gives the same result.
    /// </summary>
    public static class ArrangementSolver
    {
        public const long DefaultNodeBudget = 150_000;

        private const int MaxGridWidth = 64;
        private const int MaxMemoEntries = 200_000;

        public static ArrangementResult Solve(ArrangementRequest request, long nodeBudget = DefaultNodeBudget)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var grids = request.IncomingGrids;
            // Sorted so that input order can never change the answer.
            var items = request.Items.OrderBy(i => i.Id, StringComparer.Ordinal).ToArray();

            if (grids.Any(g => g.Width > MaxGridWidth))
            {
                // Rows are 64-bit masks. No game grid is near this wide; refuse rather than misplace.
                return Final(ArrangementStatus.Restricted, null, Array.Empty<string>(), 0, false);
            }

            string[] restricted = items
                .Where(i => !i.AllowedIncoming.Take(grids.Count).Any(a => a) && !(i.Fixed && i.Current.Side == ContainerSide.Incoming))
                .Select(i => i.Id)
                .ToArray();
            if (restricted.Length > 0)
            {
                return Final(ArrangementStatus.Restricted, null, restricted, 0, false);
            }

            // A fixed outgoing item can never leave, so the swap cannot complete.
            restricted = items.Where(i => i.Fixed && i.Current.Side == ContainerSide.Outgoing).Select(i => i.Id).ToArray();
            if (restricted.Length > 0)
            {
                return Final(ArrangementStatus.Restricted, null, restricted, 0, false);
            }

            // Cheap early checks. Passing proves nothing: free cells do not mean the items fit.
            if (items.Sum(i => (long)i.Area) > grids.Sum(g => (long)g.Cells))
            {
                return Final(ArrangementStatus.Impossible, null, Array.Empty<string>(), 0, false);
            }
            if (items.Any(i => !i.Fixed && !FitsAnywhere(i, grids)))
            {
                return Final(ArrangementStatus.Impossible, null, Array.Empty<string>(), 0, false);
            }

            long stageABudget = Math.Max(1, nodeBudget / 4);

            bool stageAPossible = items.Where(i => i.Current.Side == ContainerSide.Incoming).All(i => Inside(i, grids));
            long used = 0;
            if (stageAPossible)
            {
                var stageA = new Search(grids, items, keepIncoming: true, stageABudget);
                var outcome = stageA.Run();
                used += stageA.Nodes;
                if (outcome == SearchOutcome.Found)
                {
                    return Final(ArrangementStatus.Found, stageA.Result, Array.Empty<string>(), used, false);
                }
            }

            var stageB = new Search(grids, items, keepIncoming: false, Math.Max(1, nodeBudget - used));
            var outcomeB = stageB.Run();
            used += stageB.Nodes;
            switch (outcomeB)
            {
                case SearchOutcome.Found:
                    bool moved = items.Any(i => i.Current.Side == ContainerSide.Incoming && !stageB.Result[i.Id].SameAs(i.Current));
                    return Final(ArrangementStatus.Found, stageB.Result, Array.Empty<string>(), used, moved);
                case SearchOutcome.NoSolution:
                    // Stage B covers everything stage A could do, so its proof stands even if A ran out.
                    return Final(ArrangementStatus.Impossible, null, Array.Empty<string>(), used, false);
                default:
                    return Final(ArrangementStatus.BudgetExhausted, null, Array.Empty<string>(), used, false);
            }
        }

        private static ArrangementResult Final(ArrangementStatus status, IReadOnlyDictionary<string, Placement> placements, IReadOnlyList<string> restricted, long nodes, bool rearranged)
        {
            return new ArrangementResult(status, placements, restricted, nodes, rearranged);
        }

        private static bool FitsAnywhere(ItemSnapshot item, IReadOnlyList<GridSnapshot> grids)
        {
            for (int g = 0; g < grids.Count; g++)
            {
                if (g >= item.AllowedIncoming.Length || !item.AllowedIncoming[g])
                {
                    continue;
                }
                if ((item.Width <= grids[g].Width && item.Height <= grids[g].Height) ||
                    (item.Height <= grids[g].Width && item.Width <= grids[g].Height))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Inside(ItemSnapshot item, IReadOnlyList<GridSnapshot> grids)
        {
            var p = item.Current;
            if (p.Grid < 0 || p.Grid >= grids.Count)
            {
                return false;
            }
            int w = p.Rotated ? item.Height : item.Width;
            int h = p.Rotated ? item.Width : item.Height;
            return p.X >= 0 && p.Y >= 0 && p.X + w <= grids[p.Grid].Width && p.Y + h <= grids[p.Grid].Height;
        }

        private enum SearchOutcome { Found, NoSolution, Exhausted }

        private sealed class Search
        {
            private readonly IReadOnlyList<GridSnapshot> _grids;
            private readonly ulong[][] _rows;
            private readonly int[] _counts;
            private readonly List<ItemSnapshot> _free = new List<ItemSnapshot>();
            private readonly long _budget;
            private readonly Dictionary<string, Placement> _placed = new Dictionary<string, Placement>(StringComparer.Ordinal);
            private readonly HashSet<string> _failedStates = new HashSet<string>(StringComparer.Ordinal);
            private readonly bool _impossibleFromStart;
            private readonly bool[] _assigned;
            private readonly string[] _classKeys;

            public long Nodes { get; private set; }
            public Dictionary<string, Placement> Result => _placed;

            public Search(IReadOnlyList<GridSnapshot> grids, IReadOnlyList<ItemSnapshot> items, bool keepIncoming, long budget)
            {
                _grids = grids;
                _budget = budget;
                _rows = grids.Select(g => new ulong[g.Height]).ToArray();
                _counts = new int[grids.Count];

                foreach (var item in items)
                {
                    bool stays = item.Fixed || (keepIncoming && item.Current.Side == ContainerSide.Incoming);
                    if (!stays)
                    {
                        _free.Add(item);
                        continue;
                    }

                    int w = item.Current.Rotated ? item.Height : item.Width;
                    int h = item.Current.Rotated ? item.Width : item.Height;
                    if (!Inside(item, grids) || !CanPut(item.Current.Grid, item.Current.X, item.Current.Y, w, h))
                    {
                        // Stale or overlapping input: no arrangement can honor it.
                        _impossibleFromStart = true;
                        continue;
                    }
                    Mark(item.Current.Grid, item.Current.X, item.Current.Y, w, h, true);
                    _counts[item.Current.Grid]++;
                    _placed[item.Id] = item.Current;
                }

                for (int g = 0; g < grids.Count; g++)
                {
                    if (grids[g].MaxItems.HasValue && _counts[g] > grids[g].MaxItems.Value)
                    {
                        _impossibleFromStart = true;
                    }
                }

                _assigned = new bool[_free.Count];
                _classKeys = _free.Select(ClassKey).ToArray();
            }

            public SearchOutcome Run()
            {
                if (_impossibleFromStart)
                {
                    return SearchOutcome.NoSolution;
                }
                return Recurse(_free.Count);
            }

            // Items that are interchangeable to the search: same size, same allowed grids.
            private static string ClassKey(ItemSnapshot item)
            {
                var sb = new StringBuilder();
                sb.Append(item.Width).Append('x').Append(item.Height).Append(':');
                foreach (bool a in item.AllowedIncoming)
                {
                    sb.Append(a ? '1' : '0');
                }
                return sb.ToString();
            }

            private bool CanPut(int grid, int x, int y, int w, int h)
            {
                ulong mask = w >= 64 ? ulong.MaxValue : ((1UL << w) - 1) << x;
                var rows = _rows[grid];
                for (int r = y; r < y + h; r++)
                {
                    if ((rows[r] & mask) != 0)
                    {
                        return false;
                    }
                }
                return true;
            }

            private void Mark(int grid, int x, int y, int w, int h, bool value)
            {
                ulong mask = w >= 64 ? ulong.MaxValue : ((1UL << w) - 1) << x;
                var rows = _rows[grid];
                for (int r = y; r < y + h; r++)
                {
                    if (value) rows[r] |= mask; else rows[r] &= ~mask;
                }
            }

            private SearchOutcome Recurse(int remaining)
            {
                if (remaining == 0)
                {
                    return SearchOutcome.Found;
                }
                if (++Nodes > _budget)
                {
                    return SearchOutcome.Exhausted;
                }

                string stateKey = StateKey();
                if (_failedStates.Contains(stateKey))
                {
                    return SearchOutcome.NoSolution;
                }

                // Count every unplaced item's legal positions, and note which free cells any item could
                // still cover. Cells no item can reach are wasted, which often proves a dead end early.
                int bestIndex = -1;
                int bestCount = int.MaxValue;
                long remainingArea = 0;
                ulong[][] coverage = _grids.Select(g => new ulong[g.Height]).ToArray();

                for (int i = 0; i < _free.Count; i++)
                {
                    if (_assigned[i])
                    {
                        continue;
                    }
                    var item = _free[i];
                    remainingArea += item.Area;
                    int count = CountPlacements(item, coverage);
                    if (count == 0)
                    {
                        Remember(stateKey);
                        return SearchOutcome.NoSolution;
                    }
                    if (count < bestCount ||
                        (count == bestCount && item.Area > _free[bestIndex].Area))
                    {
                        // _free is in ordinal id order, so an exact tie keeps the first id.
                        bestCount = count;
                        bestIndex = i;
                    }
                }

                long coverable = 0;
                for (int g = 0; g < coverage.Length; g++)
                {
                    for (int r = 0; r < coverage[g].Length; r++)
                    {
                        coverable += PopCount(coverage[g][r] & ~_rows[g][r]);
                    }
                }
                if (coverable < remainingArea)
                {
                    Remember(stateKey);
                    return SearchOutcome.NoSolution;
                }

                var chosen = _free[bestIndex];
                _assigned[bestIndex] = true;
                foreach (var p in OrderedPlacements(chosen))
                {
                    int w = p.Rotated ? chosen.Height : chosen.Width;
                    int h = p.Rotated ? chosen.Width : chosen.Height;
                    Mark(p.Grid, p.X, p.Y, w, h, true);
                    _counts[p.Grid]++;
                    _placed[chosen.Id] = p;

                    var outcome = Recurse(remaining - 1);
                    if (outcome == SearchOutcome.Found || outcome == SearchOutcome.Exhausted)
                    {
                        return outcome;
                    }

                    _placed.Remove(chosen.Id);
                    _counts[p.Grid]--;
                    Mark(p.Grid, p.X, p.Y, w, h, false);
                }
                _assigned[bestIndex] = false;

                Remember(stateKey);
                return SearchOutcome.NoSolution;
            }

            private void Remember(string key)
            {
                if (_failedStates.Count < MaxMemoEntries)
                {
                    _failedStates.Add(key);
                }
            }

            // Exact, not hashed: a collision here would wrongly report a fit as impossible.
            private string StateKey()
            {
                var sb = new StringBuilder();
                foreach (var grid in _rows)
                {
                    foreach (ulong row in grid)
                    {
                        sb.Append(row).Append(',');
                    }
                    sb.Append('|');
                }
                var classes = new List<string>();
                for (int i = 0; i < _free.Count; i++)
                {
                    if (!_assigned[i])
                    {
                        classes.Add(_classKeys[i]);
                    }
                }
                classes.Sort(StringComparer.Ordinal);
                foreach (string c in classes)
                {
                    sb.Append(c).Append(';');
                }
                return sb.ToString();
            }

            private int CountPlacements(ItemSnapshot item, ulong[][] coverage)
            {
                int count = 0;
                for (int g = 0; g < _grids.Count; g++)
                {
                    if (!Allowed(item, g))
                    {
                        continue;
                    }
                    count += CountInGrid(g, item.Width, item.Height, coverage);
                    if (!item.Square)
                    {
                        count += CountInGrid(g, item.Height, item.Width, coverage);
                    }
                }
                return count;
            }

            private int CountInGrid(int g, int w, int h, ulong[][] coverage)
            {
                var grid = _grids[g];
                if (w > grid.Width || h > grid.Height)
                {
                    return 0;
                }
                int count = 0;
                ulong widthMask = w >= 64 ? ulong.MaxValue : (1UL << w) - 1;
                for (int y = 0; y <= grid.Height - h; y++)
                {
                    for (int x = 0; x <= grid.Width - w; x++)
                    {
                        if (CanPut(g, x, y, w, h))
                        {
                            count++;
                            ulong mask = widthMask << x;
                            for (int r = y; r < y + h; r++)
                            {
                                coverage[g][r] |= mask;
                            }
                        }
                    }
                }
                return count;
            }

            private bool Allowed(ItemSnapshot item, int grid)
            {
                if (grid >= item.AllowedIncoming.Length || !item.AllowedIncoming[grid])
                {
                    return false;
                }
                var limit = _grids[grid].MaxItems;
                return !limit.HasValue || _counts[grid] < limit.Value;
            }

            // Value order, in priority: the item's current spot (same grid index when it comes from the
            // outgoing container), then the smallest pocket that holds it, so large pockets stay open.
            private List<Placement> OrderedPlacements(ItemSnapshot item)
            {
                var result = new List<Placement>();
                var cur = item.Current;
                if (cur.Grid >= 0 && cur.Grid < _grids.Count && Allowed(item, cur.Grid))
                {
                    int cw = cur.Rotated ? item.Height : item.Width;
                    int ch = cur.Rotated ? item.Width : item.Height;
                    var grid = _grids[cur.Grid];
                    if (cur.X >= 0 && cur.Y >= 0 && cur.X + cw <= grid.Width && cur.Y + ch <= grid.Height && CanPut(cur.Grid, cur.X, cur.Y, cw, ch))
                    {
                        result.Add(new Placement(ContainerSide.Incoming, cur.Grid, cur.X, cur.Y, cur.Rotated));
                    }
                }

                var order = Enumerable.Range(0, _grids.Count)
                    .Where(g => Allowed(item, g))
                    .OrderBy(g => _grids[g].Cells)
                    .ThenBy(g => g);
                foreach (int g in order)
                {
                    var grid = _grids[g];
                    for (int y = 0; y < grid.Height; y++)
                    {
                        for (int x = 0; x < grid.Width; x++)
                        {
                            AddIfFree(result, item, g, x, y, false);
                            if (!item.Square)
                            {
                                AddIfFree(result, item, g, x, y, true);
                            }
                        }
                    }
                }
                return result;
            }

            private void AddIfFree(List<Placement> list, ItemSnapshot item, int g, int x, int y, bool rotated)
            {
                int w = rotated ? item.Height : item.Width;
                int h = rotated ? item.Width : item.Height;
                var grid = _grids[g];
                if (x + w > grid.Width || y + h > grid.Height || !CanPut(g, x, y, w, h))
                {
                    return;
                }
                var p = new Placement(ContainerSide.Incoming, g, x, y, rotated);
                if (list.Count == 0 || !list[0].SameAs(p))
                {
                    list.Add(p);
                }
            }

            private static int PopCount(ulong value)
            {
                int count = 0;
                while (value != 0)
                {
                    value &= value - 1;
                    count++;
                }
                return count;
            }
        }
    }
}
