# In-game test plan

Use a **separate test profile**, never the main one. Record the exact versions in the table at the end.
Turn on F12 → Seamless Swap → Diagnostic logging. After each test, note the result and attach
`BepInEx/LogOutput.log` for any failure.

Setup for most tests: offline raid, any map, kill a PMC or Scav wearing a backpack and a rig. Use
the console or a second raid as needed. "Swap" below always means: open the body, drag its backpack
(or rig) from its equipment panel onto your own occupied backpack (or rig) slot, and release. Section D
covers the opposite direction.

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

## D. Reverse direction

19. **Mine onto theirs.** Repeat A1, A2, A4 and A7 dragging *your* backpack (or rig) onto the body's
    occupied slot. Expect the same results: theirs equipped on you with both sets of items, the same
    messages, and Control giving normal behavior.
20. **Unsearched, reverse.** Drag yours onto the body's unsearched backpack. Expect "Search both
    containers before swapping."; nothing changed; the search isn't started.
21. **Empty body slot.** Drag yours onto a body's empty slot. Expect the normal game move, contents
    staying in the bag.

22. **Organized.** Leave gaps in both bags, then swap. Expect one tidy block: largest items at the top
    left, small pockets filled first, nothing rotated that didn't need to be. Report how it looks.
23. **Almost full.** Swap into a bag with barely enough room. Expect the swap to happen even if the
    result is less tidy; it may keep the plain layout.

24. **Gun on your pack, same class.** With BackpackGunSlot, swap to a pack that accepts your on-pack gun.
    Expect the gun on the new pack's ON PACK slot, and the slot redrawn correctly.
25. **Gun too big for the new slot.** Swap a rifle on a big pack to a small one. Expect the rifle in the
    bag's grid, or "Not enough space…" and nothing changed if it won't fit.
26. **Body's gun.** Swap to a pack that already has a gun on it, with your own on yours. Expect theirs
    still on the pack and yours in the grid.

27. **Flip rigs repeatedly.** Swap a full rig back and forth on one body, 20 or more times, in pockets of
    mixed sizes. Expect no items drawn on top of each other. Attach the log either way: any "stale image"
    or "Moved the image" warning from Seamless Swap is what we need.

28. **No flash.** Swap a full backpack. Expect no glimpse of your items in the old bag on the body. Then
    interrupt a swap (close the inventory, or die) and expect what was left behind to be visible.

29. **Fill animation.** Swap a full backpack at Fill speed 0.25, 1 and 4. Expect items to pop in top to
    bottom, left to right, each with its drop sound. Close the inventory halfway through and reopen:
    expect every item visible. With Fill animation off, expect an instant swap.
30. **ON PACK gun.** With BackpackGunSlot, swap packs that both carry a gun, several times. Expect the ON
    PACK slot to stay drawn, and no "Backpack slot placement failed" in the log.

## E. Fika

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
| Seamless Swap | 1.0.0 |

Seamless Swap is not release-ready until section A and configuration 5 of section C pass.
