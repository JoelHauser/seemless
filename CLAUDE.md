# Seamless Swap: working notes for Claude

SPT client mod: drag a body's backpack or rig onto your own occupied slot, or yours onto theirs; contents transfer and are
arranged. Brief from Joel, 2026-11-05: UI Fixes and Pack 'n' Strap compatibility is mandatory; verified
APIs only; never label it release-ready before in-game checks with both installed.

## State

1.0.0 released on GitHub (v1.0.0). Run in raids with UI Fixes, Pack 'n' Strap and BackpackGunSlot installed; the BackpackGunSlot guard and fill animation were the last changes before release. Repo https://github.com/JoelHauser/seemless.git (public).
Remaining in-game checks: docs/TEST-PLAN.md (gun on pack, rig flips, fill animation).

## Layout

- `src/SeamlessSwap/Core` — solver, move planner, coordinator. No Unity or game references, ever.
- `src/SeamlessSwap/Game` — gesture, two Harmony patches, `GameGateway` (all live-game reads and ops).
- `tests/SeamlessSwap.Tests` — plain .NET, `dotnet run -c Release`.
- `tools/GameHarness` — the shipped Core + Game code against the game's real inventory classes in the
  game's mono runtime, hosted by `tools/MonoHost`. Optional compatibility run with real mod DLLs.
- docs/TECHNICAL.md (what is verified and how), docs/COMPATIBILITY.md, docs/TEST-PLAN.md.

## Building on the work machine

`C:\HUH` is unpatched and must stay untouched. Build a mirror (same as FirstShotSmoothness):
copy `C:\HUH\BepInEx` and `C:\HUH\EscapeFromTarkov_Data\Managed` into `<mirror>`, keep
`Assembly-CSharp.dll` as `.spt-bak`, and apply
`C:\HUH\SPT_Runtime\SPT_Data\Launcher\Patches\SPT-core\EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll.delta`
with SharpHDiffPatch.Core. Then from PowerShell:

```
scripts\pack.ps1 -SPTPath <mirror>
scripts\run-harness.ps1 -SPTPath <mirror> -Mono C:\HUH\MonoBleedingEdge -Work <empty dir> [-ExtraAssemblies <dlls>]
```

For the compatibility run pass `-ExtraAssemblies`: every DLL under `<mirror>\BepInEx\plugins\spt`,
`Tyfon.UIFixes.dll`, `Tyfon.UIFixes.Net.dll`, `WTT-PackNStrapClient.dll`, `WTT-ClientCommonLib.dll`
(from the GitHub releases). The "mono runtime and class libraries are out of sync" lines are harness
noise.

## Things learned the hard way

- Move clears hotkeys only when the source owner is an InventoryController, so the swap must come
  first. Contents-first loses every hotkey (harness `Fact_ContentsFirstLosesBinding`).
- Move resets tags (cases, magazines) on any owner change; the gateway saves and restores them.
- `UnbindResult` can't go through `TryRunNetworkTransaction` (`ConvertOperationResultToOperation`
  throws). Unbind directly and raise events, as UI Fixes does.
- Known-item state is per item *and address* and changes only through events. A dry run must mark
  moved items temporarily known, or a parked item looks unknown at its parking spot.
- The outgoing bag must count as searched, or every move out of it is refused after the swap.
- Template grids lose `MaxItemsCount` when cloned onto an item (`Grid(Grid, parent)`); read live grids.
- Harness: without a single-thread synchronization context, continuations race the game's async
  operation pipeline. `MainLoop` stands in for Unity's. Harness items need `StackObjectsCount` and
  `StackMaxSize` 1, `CanPutIntoDuringTheRaid` true, empty (not null) filter arrays, 24-hex ids, and a
  real `ActiveSearchController` (built uninitialized, fields set by hand).
- Every body-slot placement logs "Cannot put X ... because it contains X" in EFT's inventory.log, after success
  (`CorpseItemController.InProcess` adds it again). A game quirk, and one line per swap. The harness has no
  `ContainerCollectionView`, so it never shows there.
- An awaited operation can resume inside its own completion callback. Never issue the next one from there;
  wait a frame first.
- The swap and its moves must land in one frame, or the frame between shows a half-done swap (Joel saw it as a
  flash, twice). `Immediate` skips the controller's per-operation yield for this mod's operations only; the
  harness asserts one frame. Hiding views (`Curtain`) alone did not fix it.
- Moves run in rounds on purpose: one per frame still looks like items streaming across. UI Fixes' multi-move
  also hands the game a row of moves. The earlier "one per frame" rule had no basis in its source.
- Measure transfers with both bags holding items: an empty target bag hides the chains tidying creates.
- `ItemManipulator.Sort` would be ideal but `IsAllowedToSort` is false in raid. That's a game rule; leave it.
- Tidying (`ArrangementResult.Organized`) is best effort: `PlanOrganized` falls back to `Placements` when the tidy
  layout has no move order or the dry run refuses it. It must never be the reason a swap is refused.
- Hotkeys only exist on items in rigs and pockets; a binding on a backpack item is illegal and gets dropped.
- BackpackGunSlot's gun lives in a *slot* of the bag, so it isn't a grid item: `CarriedGuns` finds it, and
  `BuildRequest` gives a gun with no new slot a stand-in outgoing grid so Core needs no changes.
- Joel saw two items drawn on top of each other after flipping rigs (2026-11-05). No "is taken by another item"
  in Player.log, the only route to a real overlap, so it was the drawing. Cause unknown; `ViewCheck` repairs and
  always logs it, so read those warnings before theorizing. It can't run in the harness: there's no UI.
- BackpackGunSlot 1.1.1 has its own bug: after a backpack change, `PlaceSlot` reads `.enabled` on a LayoutElement it
  destroyed (cached in `_rowElements`), throws, and hides ON PACK until the inventory reopens.
  `BackpackGunSlotGuard` clears that cache when it holds a destroyed object. Report it upstream.
- Fill animation is Rows only (Joel removed Fly: too floaty). Grid items only; slot items are other mods' views.
- `InGameStatus.InRaid` is obsolete in this build (compile error); a corpse-owned source implies a raid.

## Rules

- Release zips hold only `BepInEx/plugins/SeamlessSwap.dll`. Version in the csproj and the plugin.
- Hover (`CanAccept`) never plans, mutates or notifies. One notification per failed drop; success silent.
- No scope creep to belts or other slots without Joel asking.
