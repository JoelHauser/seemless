# In-game test plan

Use a **separate test profile**, never the main one. Record the exact versions in the table at the end.
Turn on F12 → Seamless Swap → Diagnostic logging. After each test, note the result and attach
`BepInEx/LogOutput.log` for any failure.

Setup for most tests: offline raid, any map, kill a PMC or Scav wearing a backpack and a rig. Use
the console or a second raid as needed. "Swap" below always means: open the body, drag its backpack
(or rig) from its equipment panel onto your own occupied backpack (or rig) slot, and release.

## A. Core

1. **Backpack, normal.** Your backpack holds ammo, meds, loot; theirs is bigger and holds loot. Swap.
   Expect: theirs equipped holding both sets; yours empty on the body; no notification; no lag.
2. **Rig, hotkey.** Bind a medkit in your rig to 4. Swap rigs. Expect: same medkit still on 4, and
   pressing 4 uses it.
3. **Rearrangement.** Put a 3x3 (armor) in your backpack. Their backpack has a main compartment that
   fits it with a small item in it, plus a side pocket. Swap. Expect the small item moved aside.
4. **No space.** Their backpack too small. Expect "Not enough space to transfer your backpack
   contents." once; nothing changed. Repeat with rigs for the rig wording.
5. **Unsearched.** Drag their backpack before searching it. Expect "Search both containers before
   swapping."; nothing changed; the search isn't started.
6. **Body armor.** Wear body armor, swap onto an armored rig from the body. Expect "Remove your body
   armor before equipping this armored rig."; nothing changed.
7. **Bypass.** Hold Control while dropping. Expect normal game behavior (plain swap with UI Fixes).
8. **Repeat drop.** Drop twice quickly. Expect one swap only.
9. **Hover.** Drag over your slot and back several times without dropping. Expect no notifications.
10. **Extract and reload.** After 1 and 2, extract, restart the game. Expect the new backpack and rig
    with all items and the hotkey.

## B. Interruption

11. Swap a full backpack, then close the inventory at once. Expect the swap to finish anyway.
12. Swap with a grenade from the rig in your hands. Expect a refusal mentioning your hands.
13. Have many items (20+) and swap; watch for stutter. Report how long it takes.

## C. Compatibility matrix

Run A1, A2, A4, A7 in each configuration. Record pass or fail per cell.

| # | Configuration |
|---|---|
| 1 | Seamless Swap only |
| 2 | UI Fixes only (baseline; record its normal behavior for A1 and A7) |
| 3 | Seamless Swap + UI Fixes |
| 4 | Seamless Swap + Pack 'n' Strap + CommonLib |
| 5 | All four |
| 6 | All four, UI Fixes "Swap items" off, then "Force swap key" set to Control |
| 7 | All four, Seamless Swap's backpack and rig swaps both off (expect configuration 2 or 4 behavior exactly) |

With all four (configuration 5), also:

14. Wear a Pack 'n' Strap belt with items and a hotkey on one. Swap backpacks and rigs. Expect the
    belt, its items and its hotkey untouched, and the belt never used as space (try a case that only
    the belt could hold: expect "Not enough space…").
15. Put a Pack 'n' Strap case (with items) in your backpack. Swap. Expect the case moved whole with its
    items, and its tag kept.
16. Try a nesting Pack 'n' Strap forbids (for example a belt inside the incoming backpack). Expect a
    refusal and nothing changed.
17. Bind an item inside a pouch in your rig (Pack 'n' Strap allows this). Swap rigs. Expect the hotkey
    kept.
18. Open your backpack's window and the body's backpack window, then swap. Expect no stale or ghost
    items; report what the windows show.

## D. Fika

With Fika installed, drag a body's backpack onto yours. Expect one notice that Seamless Swap is off, and
normal behavior. Nothing else is supported.

## Versions tested

| Component | Version |
|---|---|
| SPT | |
| EFT | |
| UI Fixes | |
| Pack 'n' Strap | |
| WTT CommonLib | |
| Seamless Swap | 0.0.1 |

Seamless Swap is not release-ready until section A and configuration 5 of section C pass.
