# Seamless Swap: working notes for Claude

SPT client mod: drag a body's backpack or rig onto your own occupied slot; contents transfer and are
arranged. Brief from Joel, 2026-11-05: UI Fixes and Pack 'n' Strap compatibility is mandatory; verified
APIs only; never label it release-ready before in-game checks with both installed.

## State

0.0.1 built (dist/SeamlessSwap-0.0.1.zip, ignored by git), **never run in game**. Not a git repo yet:
nothing committed or published. Next step: Joel runs docs/TEST-PLAN.md on a test profile.

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
- `InGameStatus.InRaid` is obsolete in this build (compile error); a corpse-owned source implies a raid.

## Rules

- Release zips hold only `BepInEx/plugins/SeamlessSwap.dll`. Version in the csproj and the plugin.
- Hover (`CanAccept`) never plans, mutates or notifies. One notification per failed drop; success silent.
- No scope creep to belts or other slots without Joel asking.
