# Compatibility

Patch owners and priorities below were read from Harmony at run time with the real DLLs loaded
(tools/GameHarness compatibility run), not inferred from source.

## Seamless Swap's patches

| Method | Kind | Owner | Purpose |
|---|---|---|---|
| `SlotView.CanAccept` | postfix | `com.joelhauser.seamlessswap` | Says yes to the gesture only. Reference checks; no mutation, no notification. |
| `SlotView.AcceptItem` | prefix | `com.joelhauser.seamlessswap` | Runs the swap and skips the original, for the gesture only. |

No priorities, no `before`/`after`: nothing in the overlap depends on order (below). Soft
dependencies on `com.tyfon.uifixes`, `com.wtt.packnstrap` and `com.fika.core` only order loading.
No type from another mod is referenced; UI Fixes is reached by name through its documented interop.

## UI Fixes 6.0.4 (`com.tyfon.uifixes`)

| UI Fixes patch | Same method | How both coexist |
|---|---|---|
| `MultiSelectPatches+SlotViewCanAcceptPatch` (prefix, owner `SlotViewCanAcceptPatch`) | `SlotView.CanAccept` | Acts only when a multi-selection exists. Seamless Swap stands aside whenever one exists (`MultiSelectController.GetCount`, UI Fixes' documented interop). Never both. |
| `MultiSelectPatches+SlotViewAcceptItemPatch` (prefix, owner `SlotViewAcceptItemPatch`) | `SlotView.AcceptItem` | Same gate. Exactly one of the two prefixes can act, so one drop gives one swap. |
| `SwapPatches+SlotCanAcceptSwapPatch` (prefix + postfix, owner `SlotCanAcceptSwapPatch`) | `DragItemContext.CanAccept`, called inside `SlotView.CanAccept` | On hover it may plan a plain UI Fixes swap (simulated, no change). Seamless Swap's postfix overrides the answer for the gesture only. UI Fixes' swap would only ever execute inside the original `AcceptItem`, which Seamless Swap skips, so the plain swap never also runs. |
| `SwapPatches+SwapOperationRaiseEventsPatch` | `SwapResult.RaiseEvents` | Runs on Seamless Swap's own swap: unbinds the two swapped containers if bound. Containers aren't bindable, so it does nothing here. Verified harmless in the harness run. |
| `ToggleForceSwapPatch`, `HighlightSwapSourcePatch` | drag view | Hover visuals only. |
| `LimitDragPatches` | drag events | Shift-drag is suppressed by UI Fixes; that's why Shift isn't a bypass option. |
| `NetworkTransactionWatcher` | `ItemController.RunNetworkTransaction` | Watches only when UI Fixes asks it to (multi-select). Not triggered here. |
| `OperationQueuePatch` | `ClientBackendSession.TrySendCommands` | Out of raid only. Unaffected. |
| `RebindConsumablesPatches` | `Discard`, grenade throw | Unrelated paths. |
| `ModifyUnsearchedContainerPatch` | `CanTransferTo` | Setting "add to unsearched containers": relaxes a rule the mod reads natively. The mod still requires both containers searched, so it never relies on it. |

**Bypass:** hold Control (default) while dropping and the drag is exactly game plus UI Fixes, including
UI Fixes' Force Swap if its key (default Alt) is also held. With the mod disabled, out of scope, or with
any multi-selection, the mod's patches return immediately.

## WTT - Pack 'n' Strap 2.1.1 (`com.wtt.packnstrap`) and WTT CommonLib 3.0.6

| Pack 'n' Strap behavior | Effect on swaps |
|---|---|
| Belts live in the `ArmBand` slot (`CustomBeltItemClass`) | Out of scope: only `Backpack` and `TacticalVest` slots trigger a swap. Belts are never read as space, never moved; their hotkeys are untouched. Verified. |
| `IsAtBindablePlacePatch` (postfix) — bindable inside cases in the rig, flares | Read through the game's own `IsAtBindablePlace`. With it applied, a hotkey on an item inside a case in the rig survives the swap. Verified with the real patch. |
| `Inventory.FastAccessSlots` / `BindAvailableSlotsExtended` replaced at load | Read live by the game; the mod never assumes vanilla slot counts and never rebuilds the hot bar. |
| `ContainerSlotsPatch`, `PaymentSlotsPatch`, `GrenadeThrowingSlotsPatch` | Not used by swaps. |
| Item filters, recursive-nesting filters, custom grids | Read through `Grid.CheckCompatibility` per item and per grid, so a forbidden nesting is a refusal before any change. |
| CommonLib client patches | None touch inventory moves, slots or bindings (checked by source). |

A custom item with grids isn't eligible just for having grids: only what sits in the `Backpack` or
`TacticalVest` slot is.

## Fika 2.4.6 (`com.fika.core`)

**[read]** Fika routes every client inventory operation to the host and applies it after the host
answers (`ClientInventoryController.RunClientOperation`, `ClientInventoryOperationHandler`). The body is
shared with other players while your items sit in your old bag on it. This hasn't been tested, so with
Fika installed the mod turns automatic swaps off, shows a one-time notice on the first such drag, and
leaves every drag to the game and UI Fixes. Host, client and headless installation requirements are
not established.
