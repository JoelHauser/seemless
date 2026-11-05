# Seamless Swap

Drag a body's backpack or rig onto your own, and your belongings come with you.

One normal drag and drop, while looting a body, onto your own occupied backpack or rig slot:

- the body's container is equipped on you;
- everything that was directly in yours moves into it, alongside what it already held;
- items are arranged across its real pockets, rotating and rearranging where needed;
- your emptied container goes onto the body in its place;
- quick-use hotkeys stay on the same items wherever the game still allows them.

No menu, button, dialog or spare inventory space. A successful swap is silent. When a swap can't
happen, nothing moves and one notification in the bottom-right corner says why.

**Status: 0.0.1, pre-release. It has never been run in the game.** Its logic is tested against the
game's real inventory code outside the game (see "Testing"), but drag and drop, the UI and real raids
are untested. Don't use it on a profile you care about until the in-game checks in
[docs/TEST-PLAN.md](docs/TEST-PLAN.md) pass.

## Requirements

- SPT 4.1.x (built and tested against SPT 4.1.6, Escape from Tarkov 0.16.9.40743).
- Optional and supported: [UI Fixes](https://github.com/tyfon7/UIFixes) 6.0.4,
  [WTT - Pack 'n' Strap](https://sp-mod.com/mod/1278/wtt-pack-n-strap) 2.1.1 with WTT CommonLib.
  Neither is required.
- Not supported: Fika. With Fika installed, automatic swaps stay off and drags keep their normal behavior.

## Install

Unzip `SeamlessSwap-0.0.1.zip` over your SPT folder. It contains only
`BepInEx/plugins/SeamlessSwap.dll`.

## Remove

Delete `BepInEx/plugins/SeamlessSwap.dll`, and optionally `BepInEx/config/com.joelhauser.seamlessswap.cfg`.
The mod stores nothing in your profile.

## Settings (F12)

| Setting | Default | Effect |
|---|---|---|
| Enable backpack swaps | on | Backpack drags transfer contents. |
| Enable rig swaps | on | Rig drags transfer contents. |
| Bypass modifier | Control | Hold it while dropping to get the normal game behavior for that drag, including UI Fixes. `Alt` and `None` are also offered. |
| Diagnostic logging | off | Writes planning details to the BepInEx log. |

All four take effect immediately; each one is read at the moment of the drop. Shift isn't offered as a
bypass because UI Fixes uses it to suppress drags. Alt is UI Fixes' default Force Swap key, so Control
is the default here.

## What it refuses, and says

| Situation | Message |
|---|---|
| Not enough room, or an item fits no single pocket | Not enough space to transfer your backpack contents. / …rig contents. |
| The arrangement search hit its limit | Could not arrange items automatically. Rearrange and try again. |
| Either container not fully searched | Search both containers before swapping. |
| Incoming armored rig, but you wear body armor | Remove your body armor before equipping this armored rig. |
| An item's filters forbid it in the new container | An item's restrictions prevent this swap. |

Also refused: an item from your container in your hands, pouches attached to your container that hold
items, and an armored rig of yours that the body's own armor blocks.

## What it never does

Fold weapons, strip attachments, unload or merge ammo, unpack cases, start a search, remove your armor,
move plates, use your belt or any other container as spare room, or touch anything outside the two
containers being swapped. Belts in Pack 'n' Strap's slot are left exactly as they are.

## Compatibility

See [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md) for how it sits next to UI Fixes, Pack 'n' Strap,
CommonLib and Fika, method by method.

## Testing

- `tests/` — 48 tests of the arrangement solver and the swap sequence, on plain .NET.
- `tools/GameHarness` — 34 tests running this mod's real code against the game's real inventory classes
  in the game's own runtime, plus a run with the real UI Fixes and Pack 'n' Strap patches applied.
- [docs/TEST-PLAN.md](docs/TEST-PLAN.md) — what only the game can show, step by step.
