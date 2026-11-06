# Compatibility

Patch owners and priorities below were read from Harmony at run time with the real DLLs loaded
(tools/GameHarness compatibility run), not inferred from source.

## Seamless Swap's patches

| Method | Kind | Owner | Purpose |
|---|---|---|---|
| `SlotView.CanAccept` | postfix | `com.joelhauser.seamlessswap` | Says yes to the gesture only. Reference checks; no mutation, no notification. |
| `SlotView.AcceptItem` | prefix | `com.joelhauser.seamlessswap` | Runs the swap and skips the original, for the gesture only. |
| `Player.SinglePlayerInventoryController.Execute` | prefix | `com.joelhauser.seamlessswap` | For this mod's own operations only (one flag, consumed by the first call): runs them without the controller's one-frame wait, via the same base call, so a swap lands in one frame. Every other operation passes through. |
| `SearchableItemView.Show` | postfix | `com.joelhauser.seamlessswap` | During a swap only: hides the old container's contents on the body until they've moved. Changes nothing else. |

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

## BackpackGunSlot 1.1.1 (`com.20fpsguy.backpackgunslot`)

Its server half adds a slot (default `mod_backpack_weapon`) to each backpack template, and which guns a
pack accepts depends on the pack's size. Its client half only draws that slot: its patches
(`SlotView.SetupItemView`, `SlotView.ScaleItem`, `SearchableSlotView.CreateSlots`, `ContainersPanel`,
`GridWindow`, `ContainedGridsView`, `EquipItemWindow`, `ItemSpecificationPanel`, `ObjectsFactory`) are read,
and none overlaps Seamless Swap's two.

| Situation | Result |
|---|---|
| New pack's slot is free and accepts the gun | The gun moves to it |
| Slot taken, refuses the gun, or the new pack has no slot | The gun is packed into the new pack's grid with everything else |
| No room in the grid either | "Not enough space…", nothing moves |
| The body's pack has a gun on it | It comes with that pack |

**Their bug, guarded:** when the backpack changes, BackpackSlotBinding caches its row's layout elements while its
old `LayoutElement` is still pending destruction, then reads `.enabled` on it next frame (IL 0x3b2 in `PlaceSlot`),
throws, and shuts its ON PACK slot off until the inventory is reopened. Seamless Swap prefixes `PlaceSlot`, found
by name, and drops that cache only when it holds a destroyed object, so the mod rebuilds it as it already does.
Worth reporting upstream.

The gun is found by type (an `IWeapon` in a slot of the bag), not by slot name, which is a setting. It is
counted in what was left behind, so an interrupted swap reports it. No type from the mod is referenced.

## Fika 2.4.6 (`com.fika.core`)

**[read]** Fika routes every client inventory operation to the host and applies it after the host
answers (`ClientInventoryController.RunClientOperation`, `ClientInventoryOperationHandler`). The body is
shared with other players while your items sit in your old bag on it. This hasn't been tested, so with
Fika installed the mod turns automatic swaps off, shows a one-time notice on the first such drag, and
leaves every drag to the game and UI Fixes. Host, client and headless installation requirements are
not established.
