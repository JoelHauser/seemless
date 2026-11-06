using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SeamlessSwap.Core;

namespace SeamlessSwap.Tests
{
    internal static class SolverTests
    {
        // Builders. inc/outg place an item in the incoming or outgoing container; allow lists grid indexes.
        private static ItemSnapshot Inc(string id, int w, int h, int grid, int x, int y, int grids, bool rotated = false, bool isFixed = false, int[] allow = null, int outgoingGrids = 1)
        {
            return new ItemSnapshot(id, w, h, new Placement(ContainerSide.Incoming, grid, x, y, rotated), Mask(grids, allow), Mask(outgoingGrids, null), isFixed);
        }

        private static ItemSnapshot Out(string id, int w, int h, int grid, int x, int y, int grids, bool rotated = false, int[] allow = null, int outgoingGrids = 1, bool isFixed = false)
        {
            return new ItemSnapshot(id, w, h, new Placement(ContainerSide.Outgoing, grid, x, y, rotated), Mask(grids, allow), Mask(outgoingGrids, null), isFixed);
        }

        private static bool[] Mask(int count, int[] allow)
        {
            var mask = new bool[count];
            for (int i = 0; i < count; i++)
            {
                mask[i] = allow == null || allow.Contains(i);
            }
            return mask;
        }

        private static ArrangementRequest Request(GridSnapshot[] incoming, params ItemSnapshot[] items)
        {
            // Outgoing grid big enough for every test's outgoing placements.
            return new ArrangementRequest(incoming, new[] { new GridSnapshot(10, 10) }, items);
        }

        private static GridSnapshot[] Grids(params (int w, int h)[] sizes)
        {
            return sizes.Select(s => new GridSnapshot(s.w, s.h)).ToArray();
        }

        // Independent check of a solution: fully inside one permitted grid, no overlaps, nothing missing.
        private static void AssertValid(ArrangementRequest request, ArrangementResult result)
        {
            Check.Equal(ArrangementStatus.Found, result.Status, "status");
            Check.Equal(request.Items.Count, result.Placements.Count, "placed item count");

            var cells = new HashSet<(int, int, int)>();
            foreach (var item in request.Items)
            {
                var p = result.Placements[item.Id];
                Check.Equal(ContainerSide.Incoming, p.Side, item.Id + " side");
                var grid = request.IncomingGrids[p.Grid];
                if (!item.Fixed)
                {
                    Check.True(item.AllowedIncoming[p.Grid], $"{item.Id} is in forbidden grid {p.Grid}");
                }
                int w = p.Rotated ? item.Height : item.Width;
                int h = p.Rotated ? item.Width : item.Height;
                Check.True(p.X >= 0 && p.Y >= 0 && p.X + w <= grid.Width && p.Y + h <= grid.Height, $"{item.Id} leaves its grid: {p}");
                for (int y = p.Y; y < p.Y + h; y++)
                {
                    for (int x = p.X; x < p.X + w; x++)
                    {
                        Check.True(cells.Add((p.Grid, x, y)), $"{item.Id} overlaps at grid {p.Grid} ({x},{y})");
                    }
                }
            }
        }

        // The move order must be legal when replayed on a plain model, and end exactly at the targets.
        private static void AssertPlanLegal(ArrangementRequest request, ArrangementResult result)
        {
            var steps = MovePlanner.Plan(request, result.Placements);
            Check.True(steps != null, "planner found no order");

            var now = request.Items.ToDictionary(i => i.Id, i => i.Current);
            var byId = request.Items.ToDictionary(i => i.Id);
            foreach (var step in steps)
            {
                Check.True(now[step.ItemId].SameAs(step.From), $"step source is stale: {step}");
                var item = byId[step.ItemId];
                int w = step.To.Rotated ? item.Height : item.Width;
                int h = step.To.Rotated ? item.Width : item.Height;
                var grid = step.To.Side == ContainerSide.Incoming ? request.IncomingGrids[step.To.Grid] : request.OutgoingGrids[step.To.Grid];
                Check.True(step.To.X + w <= grid.Width && step.To.Y + h <= grid.Height, $"step leaves grid: {step}");
                foreach (var other in request.Items.Where(o => o.Id != step.ItemId))
                {
                    var op = now[other.Id];
                    if (op.Side != step.To.Side || op.Grid != step.To.Grid) continue;
                    int ow = op.Rotated ? other.Height : other.Width;
                    int oh = op.Rotated ? other.Width : other.Height;
                    bool overlap = step.To.X < op.X + ow && op.X < step.To.X + w && step.To.Y < op.Y + oh && op.Y < step.To.Y + h;
                    Check.True(!overlap, $"step lands on {other.Id}: {step}");
                }
                now[step.ItemId] = step.To;
            }
            foreach (var pair in result.Placements)
            {
                Check.True(now[pair.Key].SameAs(pair.Value), $"{pair.Key} ended at {now[pair.Key]}, wanted {pair.Value}");
            }
        }

        [Test]
        private static void EmptyDestination_TakesEverything()
        {
            var req = Request(Grids((4, 4)), Out("a", 2, 2, 0, 0, 0, 1), Out("b", 1, 1, 0, 2, 0, 1));
            var res = ArrangementSolver.Solve(req);
            AssertValid(req, res);
            AssertPlanLegal(req, res);
        }

        [Test]
        private static void EmptyContainers_NothingToDo()
        {
            var req = Request(Grids((4, 4)));
            var res = ArrangementSolver.Solve(req);
            Check.Equal(ArrangementStatus.Found, res.Status, "status");
            Check.Equal(0, MovePlanner.Plan(req, res.Placements).Count, "steps");
        }

        [Test]
        private static void OccupiedDestination_ExistingItemsStayPut()
        {
            var req = Request(Grids((4, 4)), Inc("old", 2, 2, 0, 1, 1, 1), Out("new", 1, 1, 0, 0, 0, 1));
            var res = ArrangementSolver.Solve(req);
            AssertValid(req, res);
            Check.True(!res.RearrangedIncoming, "existing item should not move when it need not");
            Check.True(res.Placements["old"].SameAs(new Placement(ContainerSide.Incoming, 0, 1, 1, false)), "old moved");
        }

        // Every occupied cell, by grid, for looking at a layout's shape.
        private static bool[,] Occupied(GridSnapshot grid, int index, ArrangementRequest request, IReadOnlyDictionary<string, Placement> placements)
        {
            var cells = new bool[grid.Width, grid.Height];
            foreach (var item in request.Items)
            {
                var p = placements[item.Id];
                if (p.Grid != index)
                {
                    continue;
                }
                int w = p.Rotated ? item.Height : item.Width;
                int h = p.Rotated ? item.Width : item.Height;
                for (int y = p.Y; y < p.Y + h; y++)
                {
                    for (int x = p.X; x < p.X + w; x++)
                    {
                        Check.True(!cells[x, y], "organized layout overlaps at " + x + "," + y);
                        cells[x, y] = true;
                    }
                }
            }
            return cells;
        }

        // The layout from Joel's screenshot of a Pilgrim, 5 wide and 7 tall: holes and a near-empty row.
        private static ArrangementRequest ScatteredPilgrim()
        {
            return Request(Grids((5, 7)),
                Inc("orion", 2, 2, 0, 2, 0, 1),
                Inc("rs1", 1, 1, 0, 4, 0, 1), Inc("rs2", 1, 1, 0, 4, 1, 1),
                Inc("mb", 1, 1, 0, 0, 2, 1), Inc("ak103", 1, 1, 0, 1, 2, 1), Inc("ak74", 1, 1, 0, 2, 2, 1),
                Inc("korund", 1, 1, 0, 3, 2, 1), Inc("hg", 1, 1, 0, 4, 2, 1),
                Inc("krechet", 1, 1, 0, 0, 3, 1), Inc("qmk", 2, 1, 0, 1, 3, 1), Inc("fal", 2, 1, 0, 3, 3, 1),
                Inc("grip", 1, 1, 0, 0, 4, 1),
                Out("f2000", 2, 1, 0, 0, 0, 1), Out("falbel", 2, 1, 0, 2, 0, 1));
        }

        [Test]
        private static void Organized_ClosesTheHoles()
        {
            var req = ScatteredPilgrim();
            var res = ArrangementSolver.Solve(req);
            AssertValid(req, res);
            Check.True(res.Organized != null, "no tidy layout");
            var cells = Occupied(req.IncomingGrids[0], 0, req, res.Organized);
            // 21 cells of items in rows of 5 fill five rows: anything lower is a hole somewhere above it.
            for (int y = 5; y < 7; y++)
            {
                for (int x = 0; x < 5; x++)
                {
                    Check.True(!cells[x, y], $"item left low in the bag at {x},{y}");
                }
            }
            for (int y = 1; y < 7; y++)
            {
                for (int x = 0; x < 5; x++)
                {
                    Check.True(!cells[x, y] || cells[x, y - 1], $"gap above an item at {x},{y}");
                }
            }
            Check.True(res.Organized["orion"].X == 0 && res.Organized["orion"].Y == 0, "largest item should lead");
            Check.True(req.Items.All(i => !res.Organized[i.Id].Rotated), "nothing here needs turning");
            Check.True(MovePlanner.Plan(req, res.Organized) != null, "tidy layout should be reachable");
        }

        [Test]
        private static void Organized_SmallPocketsFillBeforeTheMainCompartment()
        {
            var req = Request(Grids((5, 5), (1, 2)), Out("a", 2, 2, 0, 0, 0, 2), Out("mag", 1, 2, 0, 2, 0, 2));
            var res = ArrangementSolver.Solve(req);
            AssertValid(req, res);
            Check.Equal(1, res.Organized["mag"].Grid, "mag's pocket");
            Check.Equal(0, res.Organized["a"].Grid, "main compartment");
        }

        [Test]
        private static void Organized_FixedItemsStay()
        {
            var req = Request(Grids((4, 4)), Inc("pin", 1, 1, 0, 2, 2, 1, isFixed: true), Out("a", 1, 1, 0, 0, 0, 1));
            var res = ArrangementSolver.Solve(req);
            Check.True(res.Organized["pin"].SameAs(req.Items.First(i => i.Id == "pin").Current), "fixed item moved");
        }

        [Test]
        private static void Organized_RespectsFiltersAndItemLimits()
        {
            var limited = new[] { new GridSnapshot(3, 3, 1), new GridSnapshot(3, 3) };
            var req = Request(limited, Out("a", 1, 1, 0, 0, 0, 2), Out("b", 1, 1, 0, 1, 0, 2), Out("c", 1, 1, 0, 2, 0, 2, allow: new[] { 1 }));
            var res = ArrangementSolver.Solve(req);
            Check.Equal(1, res.Organized.Values.Count(p => p.Grid == 0), "grid item limit");
            Check.Equal(1, res.Organized["c"].Grid, "filter");
        }

        [Test]
        private static void Organized_NeverChangesWhetherASwapIsPossible()
        {
            var random = new Random(4242);
            for (int run = 0; run < 300; run++)
            {
                int w = random.Next(2, 7);
                int h = random.Next(2, 7);
                var items = new List<ItemSnapshot>();
                for (int i = 0; i < random.Next(1, 14); i++)
                {
                    items.Add(Out("o" + i, random.Next(1, 3), random.Next(1, 3), 0, i % 9, i / 9, 1));
                }
                var req = Request(Grids((w, h)), items.ToArray());
                var res = ArrangementSolver.Solve(req);
                if (res.Status != ArrangementStatus.Found)
                {
                    Check.True(res.Organized == null, "a tidy layout with no plain one");
                    continue;
                }
                AssertValid(req, res);
                if (res.Organized != null)
                {
                    AssertValid(req, new ArrangementResult(ArrangementStatus.Found, res.Organized, Array.Empty<string>(), 0, false));
                }
            }
        }

        [Test]
        private static void SameLayout_KeepsOriginalPositions()
        {
            var req = Request(Grids((1, 2), (1, 2), (2, 2)),
                Out("mag", 1, 2, 1, 0, 0, 3),
                Out("nade", 1, 1, 2, 1, 1, 3));
            var res = ArrangementSolver.Solve(req);
            AssertValid(req, res);
            Check.True(res.Placements["mag"].SameAs(new Placement(ContainerSide.Incoming, 1, 0, 0, false)), "mag left its pocket");
            Check.True(res.Placements["nade"].SameAs(new Placement(ContainerSide.Incoming, 2, 1, 1, false)), "nade left its pocket");
        }

        [Test]
        private static void ExactFit_SingleGrid()
        {
            var req = Request(Grids((4, 2)), Out("a", 2, 2, 0, 0, 0, 1), Inc("b", 2, 2, 0, 2, 0, 1));
            var res = ArrangementSolver.Solve(req);
            AssertValid(req, res);
        }

        [Test]
        private static void ExactFit_MultiPocket()
        {
            var req = Request(Grids((1, 2), (1, 2), (2, 2)),
                Out("a", 1, 2, 0, 0, 0, 3), Out("b", 1, 2, 0, 1, 0, 3), Out("c", 2, 2, 0, 2, 0, 3));
            AssertValid(req, ArrangementSolver.Solve(req));
        }

        [Test]
        private static void RotationDependentFit()
        {
            var req = Request(Grids((1, 3)), Out("long", 3, 1, 0, 0, 0, 1));
            var res = ArrangementSolver.Solve(req);
            AssertValid(req, res);
            Check.True(res.Placements["long"].Rotated, "only fits rotated");
        }

        [Test]
        private static void Rearrangement_MovesBothSets()
        {
            // Incoming item sits in the middle of the only pocket that can take the armor.
            var req = Request(Grids((2, 2), (3, 3)),
                Inc("small", 1, 1, 1, 1, 1, 2),
                Out("armor", 3, 3, 0, 0, 0, 2));
            var res = ArrangementSolver.Solve(req);
            AssertValid(req, res);
            AssertPlanLegal(req, res);
            Check.True(res.RearrangedIncoming, "the small item had to move");
            Check.Equal(1, res.Placements["armor"].Grid, "armor grid");
            Check.Equal(0, res.Placements["small"].Grid, "small grid");
        }

        [Test]
        private static void LargeItem_NoPocketFits_DespiteEnoughCells()
        {
            // 12 free cells in total; the 3x3 fits none of the 2x2 pockets.
            var req = Request(Grids((2, 2), (2, 2), (2, 2)), Out("armor", 3, 3, 0, 0, 0, 3));
            var res = ArrangementSolver.Solve(req);
            Check.Equal(ArrangementStatus.Impossible, res.Status, "status");
        }

        [Test]
        private static void EnoughCells_ButGeometryImpossible()
        {
            // Exactly 9 cells for 9 cells of items, yet two 2x2s cannot share a 3x3.
            var req = Request(Grids((3, 3)), Out("a", 2, 2, 0, 0, 0, 1), Out("b", 2, 2, 0, 2, 0, 1), Out("c", 1, 1, 0, 4, 0, 1));
            var res = ArrangementSolver.Solve(req);
            Check.Equal(ArrangementStatus.Impossible, res.Status, "status");
        }

        [Test]
        private static void TooManyCells_EarlyImpossible()
        {
            var req = Request(Grids((2, 2)), Out("a", 2, 2, 0, 0, 0, 1), Out("b", 1, 1, 0, 2, 0, 1));
            var res = ArrangementSolver.Solve(req);
            Check.Equal(ArrangementStatus.Impossible, res.Status, "status");
            Check.Equal(0L, res.NodesVisited, "should be decided before searching");
        }

        [Test]
        private static void GreedyFails_ButBacktrackingSucceeds()
        {
            // Largest-first into the first spot puts the 2x1 flat at the top of the 2x3, stranding the
            // pieces below. Only one family of arrangements tiles this exactly.
            var req = Request(Grids((3, 3)),
                Out("a", 2, 1, 0, 0, 0, 1), Out("b", 2, 1, 0, 0, 1, 1), Out("c", 2, 1, 0, 0, 2, 1),
                Out("d", 2, 1, 0, 0, 3, 1), Out("e", 1, 1, 0, 0, 4, 1));
            var res = ArrangementSolver.Solve(req);
            AssertValid(req, res);
        }

        [Test]
        private static void PocketOrder_GreedyWouldWasteTheOnlyLongPocket()
        {
            // Filling the 1x3 with the 1x1s first would strand the 1x3 item.
            var req = Request(Grids((1, 3), (1, 1), (1, 1), (1, 1)),
                Out("n1", 1, 1, 0, 0, 0, 4), Out("n2", 1, 1, 0, 1, 0, 4), Out("n3", 1, 1, 0, 2, 0, 4),
                Out("rifle", 1, 3, 0, 3, 0, 4));
            var res = ArrangementSolver.Solve(req);
            AssertValid(req, res);
            Check.Equal(0, res.Placements["rifle"].Grid, "rifle pocket");
        }

        [Test]
        private static void RestrictedItem_ReportsRestriction_NotSpace()
        {
            var req = Request(Grids((5, 5)), Out("key", 1, 1, 0, 0, 0, 1, allow: new int[0]));
            var res = ArrangementSolver.Solve(req);
            Check.Equal(ArrangementStatus.Restricted, res.Status, "status");
            Check.Equal("key", res.RestrictedItems[0], "restricted item");
        }

        [Test]
        private static void FilterLimitsItemToOnePocket()
        {
            var req = Request(Grids((4, 4), (2, 2)),
                Out("mag", 1, 2, 0, 0, 0, 2, allow: new[] { 1 }),
                Out("other", 2, 2, 0, 2, 0, 2));
            var res = ArrangementSolver.Solve(req);
            AssertValid(req, res);
            Check.Equal(1, res.Placements["mag"].Grid, "mag grid");
        }

        [Test]
        private static void FilterMakesItUnfittable_IsImpossibleNotRestricted()
        {
            // Allowed only in a pocket too small for it: this is geometry, so a space message is right.
            var req = Request(Grids((4, 4), (1, 1)), Out("mag", 1, 2, 0, 0, 0, 2, allow: new[] { 1 }));
            Check.Equal(ArrangementStatus.Impossible, ArrangementSolver.Solve(req).Status, "status");
        }

        [Test]
        private static void FixedItem_NeverMoves_EvenIfThatMakesItImpossible()
        {
            var req = Request(Grids((3, 3)),
                Inc("pinned", 1, 1, 0, 1, 1, 1, isFixed: true),
                Out("armor", 3, 3, 0, 0, 0, 1));
            Check.Equal(ArrangementStatus.Impossible, ArrangementSolver.Solve(req).Status, "status");
        }

        [Test]
        private static void FixedItem_OthersRearrangeAroundIt()
        {
            var req = Request(Grids((4, 2)),
                Inc("pinned", 1, 1, 0, 0, 0, 1, isFixed: true),
                Inc("loose", 1, 1, 0, 2, 0, 1),
                Out("wide", 2, 2, 0, 0, 0, 1));
            var res = ArrangementSolver.Solve(req);
            AssertValid(req, res);
            AssertPlanLegal(req, res);
            Check.True(res.Placements["pinned"].SameAs(new Placement(ContainerSide.Incoming, 0, 0, 0, false)), "pinned moved");
        }

        [Test]
        private static void FixedOutgoingItem_IsRestriction()
        {
            var req = Request(Grids((5, 5)), Out("locked", 1, 1, 0, 0, 0, 1, isFixed: true));
            Check.Equal(ArrangementStatus.Restricted, ArrangementSolver.Solve(req).Status, "status");
        }

        [Test]
        private static void NestedContainer_IsOneIntactItem()
        {
            // A case is listed once with its own footprint. Its contents are not items here, so a
            // case holding 50 items costs exactly its 2x2.
            var req = Request(Grids((2, 2)), Out("case", 2, 2, 0, 0, 0, 1));
            var res = ArrangementSolver.Solve(req);
            AssertValid(req, res);
            Check.Equal(1, res.Placements.Count, "one item, descendants not counted");
        }

        [Test]
        private static void GridItemLimit_IsRespected()
        {
            var grids = new[] { new GridSnapshot(4, 4, maxItems: 1), new GridSnapshot(1, 1) };
            var req = new ArrangementRequest(grids, new[] { new GridSnapshot(9, 9) }, new[]
            {
                Out("a", 1, 1, 0, 0, 0, 2), Out("b", 1, 1, 0, 1, 0, 2),
            });
            var res = ArrangementSolver.Solve(req);
            AssertValid(req, res);
            Check.True(res.Placements["a"].Grid != res.Placements["b"].Grid, "limit of one per grid ignored");

            var overLimit = new ArrangementRequest(grids, new[] { new GridSnapshot(9, 9) }, new[]
            {
                Out("a", 1, 1, 0, 0, 0, 2), Out("b", 1, 1, 0, 1, 0, 2), Out("c", 1, 1, 0, 2, 0, 2),
            });
            Check.Equal(ArrangementStatus.Impossible, ArrangementSolver.Solve(overLimit).Status, "over limit");
        }

        [Test]
        private static void SearchBudgetExhausted_IsReportedAsSuch()
        {
            // Hard to refute: many same-size bars in a grid where parity makes an exact tiling impossible.
            var items = new List<ItemSnapshot>();
            for (int i = 0; i < 24; i++)
            {
                items.Add(Out("bar" + i.ToString("00"), 3, 1, 0, 0, i % 10, 1));
            }
            var req = new ArrangementRequest(Grids((8, 9)), new[] { new GridSnapshot(40, 40) }, items);
            var res = ArrangementSolver.Solve(req, nodeBudget: 5);
            Check.Equal(ArrangementStatus.BudgetExhausted, res.Status, "status");
            Check.True(res.NodesVisited <= 12, "budget was not honored: " + res.NodesVisited);
        }

        [Test]
        private static void Deterministic_SameInputSameOutput()
        {
            var a = Sample(reverse: false);
            var b = Sample(reverse: false);
            var c = Sample(reverse: true); // Listing order must not matter either.
            var ra = ArrangementSolver.Solve(a);
            var rb = ArrangementSolver.Solve(b);
            var rc = ArrangementSolver.Solve(c);
            AssertValid(a, ra);
            foreach (var pair in ra.Placements)
            {
                Check.True(pair.Value.SameAs(rb.Placements[pair.Key]), "repeat run differs at " + pair.Key);
                Check.True(pair.Value.SameAs(rc.Placements[pair.Key]), "input order changed " + pair.Key);
            }
            Check.Equal(ra.NodesVisited, rb.NodesVisited, "nodes");
        }

        private static ArrangementRequest Sample(bool reverse)
        {
            var items = new List<ItemSnapshot>
            {
                Out("a", 2, 3, 0, 0, 0, 3), Out("b", 1, 2, 0, 3, 0, 3), Out("c", 1, 1, 0, 4, 0, 3),
                Out("d", 1, 2, 0, 5, 0, 3), Inc("e", 2, 2, 2, 0, 0, 3), Inc("f", 1, 1, 0, 0, 0, 3),
            };
            if (reverse) items.Reverse();
            return Request(Grids((1, 2), (2, 1), (4, 4)), items.ToArray());
        }

        [Test]
        private static void MoveOrder_ResolvesSwapCycle()
        {
            // Force two incoming items to trade places by handing the planner that target directly.
            var a = Inc("a", 1, 1, 0, 0, 0, 1);
            var b = Inc("b", 1, 1, 0, 1, 0, 1);
            var req = new ArrangementRequest(Grids((2, 1)), new[] { new GridSnapshot(1, 1) }, new[] { a, b });
            var targets = new Dictionary<string, Placement>
            {
                ["a"] = new Placement(ContainerSide.Incoming, 0, 1, 0, false),
                ["b"] = new Placement(ContainerSide.Incoming, 0, 0, 0, false),
            };
            var result = new ArrangementResult(ArrangementStatus.Found, targets, Array.Empty<string>(), 0, true);
            AssertPlanLegal(req, result);
            var steps = MovePlanner.Plan(req, targets);
            Check.True(steps.Any(s => s.ToScratch && s.To.Side == ContainerSide.Outgoing), "expected parking in the outgoing container");
        }

        [Test]
        private static void MoveOrder_NoScratchAnywhere_ReturnsNull()
        {
            var a = Inc("a", 1, 1, 0, 0, 0, 1, outgoingGrids: 0);
            var b = Inc("b", 1, 1, 0, 1, 0, 1, outgoingGrids: 0);
            var req = new ArrangementRequest(Grids((2, 1)), new GridSnapshot[0], new[] { a, b });
            var targets = new Dictionary<string, Placement>
            {
                ["a"] = new Placement(ContainerSide.Incoming, 0, 1, 0, false),
                ["b"] = new Placement(ContainerSide.Incoming, 0, 0, 0, false),
            };
            Check.True(MovePlanner.Plan(req, targets) == null, "must refuse rather than overlap");
        }

        [Test]
        private static void MoveOrder_SlideOntoOwnCells()
        {
            var a = Inc("a", 2, 1, 0, 0, 0, 1);
            var req = Request(Grids((3, 1)), a);
            var targets = new Dictionary<string, Placement> { ["a"] = new Placement(ContainerSide.Incoming, 0, 1, 0, false) };
            var steps = MovePlanner.Plan(req, targets);
            Check.Equal(1, steps.Count, "one direct step, no parking");
        }

        [Test]
        private static void RandomizedScenarios_AllResultsVerified()
        {
            // Every Found is checked independently and its move order replayed. Each scenario starts
            // from a real legal tiling, so a feasible instance reported Impossible is caught too.
            var rng = new Random(20261105);
            int found = 0;
            for (int run = 0; run < 400; run++)
            {
                var req = RandomScenario(rng, out bool knownFeasible);
                var res = ArrangementSolver.Solve(req);
                if (knownFeasible)
                {
                    Check.True(res.Status != ArrangementStatus.Impossible, $"run {run}: a feasible case was called impossible");
                }
                if (res.Status == ArrangementStatus.Found)
                {
                    found++;
                    AssertValid(req, res);
                    AssertPlanLegal(req, res);
                }
            }
            Check.True(found > 100, "too few solved scenarios to mean anything: " + found);
        }

        private static ArrangementRequest RandomScenario(Random rng, out bool knownFeasible)
        {
            int gridCount = rng.Next(1, 5);
            var grids = new GridSnapshot[gridCount];
            var occupied = new List<bool[,]>();
            for (int g = 0; g < gridCount; g++)
            {
                grids[g] = new GridSnapshot(rng.Next(1, 7), rng.Next(1, 7));
                occupied.Add(new bool[grids[g].Width, grids[g].Height]);
            }

            // Cut a legal solution first, then scatter its items into the outgoing container or leave
            // them in place in the incoming one.
            var items = new List<ItemSnapshot>();
            var outgoing = new GridSnapshot(30, 30);
            int nextRow = 0;
            int count = rng.Next(1, 16);
            for (int i = 0; i < count; i++)
            {
                int w = rng.Next(1, 4), h = rng.Next(1, 4), g = rng.Next(gridCount);
                if (w > grids[g].Width || h > grids[g].Height) continue;
                int x = rng.Next(grids[g].Width - w + 1), y = rng.Next(grids[g].Height - h + 1);
                bool free = true;
                for (int yy = y; yy < y + h; yy++) for (int xx = x; xx < x + w; xx++) free &= !occupied[g][xx, yy];
                if (!free) continue;
                for (int yy = y; yy < y + h; yy++) for (int xx = x; xx < x + w; xx++) occupied[g][xx, yy] = true;

                string id = "i" + i.ToString("00");
                bool stays = rng.Next(3) == 0;
                bool rotateSize = rng.Next(2) == 0 && w != h;
                int iw = rotateSize ? h : w, ih = rotateSize ? w : h;
                var allowed = Enumerable.Repeat(true, gridCount).ToArray();
                if (stays)
                {
                    items.Add(new ItemSnapshot(id, iw, ih, new Placement(ContainerSide.Incoming, g, x, y, rotateSize), allowed, new[] { true }));
                }
                else
                {
                    items.Add(new ItemSnapshot(id, iw, ih, new Placement(ContainerSide.Outgoing, 0, 0, nextRow, false), allowed, new[] { true }));
                    nextRow += ih;
                }
            }
            knownFeasible = true;
            return new ArrangementRequest(grids, new[] { outgoing }, items);
        }

        [Test]
        private static void Performance_WorstCaseBudgetStaysBounded()
        {
            // Passes every cheap check: 48 cells of items in 49 cells, yet a 7x7 holds only nine 2x2s.
            // Refuting this needs real search, unlike a case the area check rejects at once.
            var hard = new List<ItemSnapshot>();
            for (int i = 0; i < 12; i++)
            {
                hard.Add(Out("sq" + i.ToString("00"), 2, 2, 0, 0, i * 2, 1));
            }
            var hardReq = new ArrangementRequest(Grids((7, 7)), new[] { new GridSnapshot(40, 40) }, hard);
            var watch = Stopwatch.StartNew();
            var hardRes = ArrangementSolver.Solve(hardReq);
            watch.Stop();
            Console.WriteLine($"        identical 2x2s: {hardRes.Status}, {hardRes.NodesVisited} nodes, {watch.ElapsedMilliseconds} ms");
            Check.True(hardRes.Status != ArrangementStatus.Found, "this cannot fit");

            // Mixed sizes give the memo little to share, which is the slowest shape for the search.
            var mixed = new List<ItemSnapshot>();
            int[][] sizes = { new[] { 5, 3 }, new[] { 4, 3 }, new[] { 4, 2 }, new[] { 3, 3 }, new[] { 3, 2 }, new[] { 3, 1 }, new[] { 2, 2 }, new[] { 2, 1 } };
            for (int i = 0; i < 10; i++)
            {
                var size = sizes[i % sizes.Length];
                mixed.Add(Out("mx" + i.ToString("00"), size[0], size[1], 0, 0, i * 5, 1));
            }
            var mixedReq = new ArrangementRequest(Grids((5, 5), (5, 5), (4, 5), (4, 5)), new[] { new GridSnapshot(60, 90) }, mixed);
            watch.Restart();
            var mixedRes = ArrangementSolver.Solve(mixedReq);
            watch.Stop();
            Console.WriteLine($"        mixed sizes in four pockets: {mixedRes.Status}, {mixedRes.NodesVisited} nodes, {watch.ElapsedMilliseconds} ms");

            Check.True(hardRes.NodesVisited <= ArrangementSolver.DefaultNodeBudget + 1, "over budget");
            Check.True(mixedRes.NodesVisited <= ArrangementSolver.DefaultNodeBudget + 1, "over budget");
            Check.True(watch.ElapsedMilliseconds < 15000, "too slow even for a worker thread: " + watch.ElapsedMilliseconds);
        }
    }
}
