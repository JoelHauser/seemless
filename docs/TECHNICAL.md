# Seamless Swap: technical assessment

Inspected 2026-11-05 against SPT 4.1.6 (`spt-core` 4.1.6, server 4.1.6-RELEASE+731d7a2), EFT
0.16.9.40743, Unity 2022.3.43f1, BepInEx 5.4.23.5, HarmonyX 2.9.0, game assemblies on .NET Framework
4.7.2 profile (plugin targets net472). Sources: the SPT-patched `Assembly-CSharp` (decompiled),
UI Fixes 6.0.4 (tag `v6.0.4`, 3def5ab, and its release DLL), Pack 'n' Strap 2.1.1 (aa802d1 and its
release 7z), WTT-CommonLib main (75d7a2e) with release v3.0.6, Fika 2.4.6 (9572504).

Labels: **[verified]** read in code and exercised by a test against the real classes;
**[read]** read in code, not exercised; **[assumed]** reasoned, needs the game.

## 1. The gesture

- **[read]** `ItemView.OnEndDrag` → `ProcessItem` → `containerView.CanAccept(...)`, and only when that
  is true → `containerView.AcceptItem(...)`. Hover calls `CanAccept` many times (`DraggedItemView`,
  `HighlightItemViewPosition`); `AcceptItem` runs once per drop.
- **[read]** `SlotView.AcceptItem` is the only place a slot drop executes (`RunNetworkTransaction`).
  UI Fixes' slot swap lives inside `DragItemContext.CanAccept` (via `SlotView.CanAccept`) and is
  executed by that same original `AcceptItem`.
- **[read]** For an occupied slot, vanilla `DragItemContext.CanAccept` calls
  `ExecutePossibleAction(source, containedItem)`, which can mean "put their backpack inside yours".
  That is why the mod must take the drop even when vanilla says yes.

So: a postfix on `SlotView.CanAccept` answers yes for the exact gesture (reference checks only: no
search, no simulation, no notification); a prefix on `SlotView.AcceptItem` runs the swap and skips the
original. A refused swap therefore can't fall through to any other handling. Gesture
(`Game/Gesture.cs`): target is the local player's own `Backpack` or `TacticalVest` slot holding an
item with grids; source is the same slot ID on an `InventoryEquipment` owned by a
`CorpseItemController` (**[read]** `Corpse.Init` always creates one); UI Fixes multi-select empty;
bypass key up.

## 2. Operation order and why

No native operation exchanges two containers *and* their contents. **[read]** `SwapResult` exchanges
two items; `MoveResult` moves one. The sequence is therefore:

1. Validate, snapshot, solve (worker thread, plain data), plan moves, revalidate, **dry run**.
2. Native `Swap` (incoming → your slot, yours → the body's slot), contents still inside.
3. Single native `Move`s, one per frame, from your old container into the new one.
4. Repair hotkeys and tags that verifiably need it.

Reasons, each checked:

- **[verified]** Hotkeys: `ItemManipulator.Move` clears a binding only when the *source owner* is an
  `InventoryController` (`TryUnbindItems(parent.GetOwner(), ...)`). After step 2 the source is the
  corpse's controller, so hotkeys survive with no bind operations at all. The opposite order (contents
  first) loses them: `Fact_ContentsFirstLosesBinding`.
- **[verified]** Refusal is cheapest first: the swap is the step most likely to be refused (armor,
  filters, busy hands). Refused at step 2, nothing has changed.
- **[verified]** Interrupted at step 3, every remaining item is an ordinary item in an ordinary bag on
  the body, takeable by hand. The player is told how many.
- **[verified]** Known-item state is keyed by item *and address* (`ActiveSearchController`). The swap
  changes no address inside either bag, so the player's own items stay known after it. The outgoing
  container must count as searched, or the game refuses every move out of it
  (`TryFindChangedContainer`); the mod checks this first.

### Dry run

`DryRun` executes the planned moves for real (`simulate: false`, no events, no network) and rolls them
all back in the same call. A simulated move can't test a *sequence*. UI Fixes tests multi-item drops
the same way. **[verified]** state, bindings, tags and grids are byte-identical afterwards
(`DryRun_RestoresExactState`). An item parked and moved again is marked temporarily known for the
duration, which is what a real move's `AddResult` does (`PlanWithParking_DryRunAcceptsItAndItRuns`).
The swap itself is checked by the game's simulated `Swap`, not executed, because slot changes fire
`OnAddOrRemoveItem` to visual subscribers.

### Failure handling

- Each operation waits for its acknowledgment (10 s limit). A missing acknowledgment is never taken as
  success or failure: real state is read. A swap that answers late is watched and, if it lands, reported.
- A refused or stale step stops the run; state is snapshotted again, re-solved and re-dry-run, at most
  three attempts. Nothing is replayed. **[verified]** a refusal at any operation loses or duplicates
  nothing (`FaultAtEveryOperation_NothingLostOrDuplicated`, plain and real-class versions).
- Player dead, game world gone or inventory blocked: stop, report what remains.
- UI closed after the swap started: continues, the moves need no UI (**[verified]** in the harness).
- One swap at a time; a second drop meanwhile is refused with a notice.

## 3. Arrangement

`Core/ArrangementSolver.cs`: backtracking, most-constrained item first (fewest legal positions, then
largest, then id), each grid separate, rotation where the item isn't square, grid item limits, fixed
items. Pruning: per-item zero options; cells no remaining item can reach; exact memo of failed states.
Value order: the item's current position, then the smallest pocket that holds it. Stage A keeps every
item already in the incoming container in place; stage B lets them move. Results: Found / Impossible
(only from an exhaustive stage B) / BudgetExhausted (150 000 nodes, deterministic) / Restricted.
A hard infeasible case (twelve 2x2s in a 7x7) is refuted in about 30 000 nodes, 0.2 s, on a worker
thread. `Core/MovePlanner.cs` orders the moves and parks an item in free space when items block each
other in a cycle; with no free space anywhere, the swap is refused before any change.

Per-item, per-grid rules come only from the game: `Grid.CheckCompatibility` (filters, Pack 'n' Strap's
recursive-nesting filters via `ParentRecursiveCheck`, `ForbidNonEmptyContainers`),
`IMoveCheckable.CanBeMoved`, `ItemManipulator.DestinationCheck` (safe-container rules),
`ItemManipulator.CanTransferTo`, and the backpack-in-backpack depth limit. **[verified]** the
`CantPutIntoDuringRaid` rule surfaces correctly.

## 4. Data preserved

- **[verified]** Same item objects and ids throughout; found-in-raid, descendants and positions inside
  cases unchanged. Durability, resources, ammo, attachments and plates are fields on items that are
  moved whole, never touched. **[read]** no operation used here edits them.
- **[verified]** Tags: native `Move` clears every `TagComponent` under a compound item that changes owner
  (`ResetTags`), which includes cases and magazines. The mod saves them before and puts back only those
  still cleared, through the native tag operation (`Fact_CrossOwnerMoveClearsTags`,
  `TaggedCase_KeepsItsTag`).
- **[verified]** Hotkeys: kept natively. A captured binding that is no longer legal is released (direct
  unbind plus events, the pattern UI Fixes uses: `UnbindResult` has no network operation, and
  `ConvertOperationResultToOperation` throws on it, a bug the harness caught). A key someone else
  now holds is never overwritten; nothing is substituted.
- **[read]** `BaseStatisticsManager.OnGrabLoot` counts items entering the player's inventory once per
  item id per raid. Your own items re-entering from the body may count once as looted. This is the
  same as moving them out and back by hand.

## 5. Not verified (needs the game)

- The drop gesture, highlight colour and native notification appearance and position.
- That Unity's synchronization context paces `Task.Yield` one frame at a time as assumed (Fika relies
  on the same).
- Open container windows for either bag during the swap.
- The hands controller's `CanExecute` for these operations mid-animation.
- Profile save after extraction and reload.
- Real Pack 'n' Strap item classes and grid layouts (the harness uses its patches on vanilla classes).
