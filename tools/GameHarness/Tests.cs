using System.Linq;
using System.Threading.Tasks;
using EFT;
using EFT.InventoryLogic;
using SeamlessSwap.Core;
using SeamlessSwap.Game;

namespace SeamlessSwap.Harness
{
    internal static class Tests
    {
        private static Scene Packs(out Backpack mine, out Backpack theirs, (int, int)[] myGrids = null, (int, int)[] theirGrids = null)
        {
            var scene = Build.NewScene();
            mine = Build.Pack(myGrids ?? new[] { (4, 4) });
            theirs = Build.Pack(theirGrids ?? new[] { (6, 6) });
            Build.Equip(scene.MySlot(EquipmentSlot.Backpack), mine);
            Build.Equip(scene.TheirSlot(EquipmentSlot.Backpack), theirs);
            return scene;
        }

        private static Scene Rigs(out Vest mine, out Vest theirs, bool theirsArmored = false, (int, int)[] myGrids = null, (int, int)[] theirGrids = null, bool mineArmored = false)
        {
            var scene = Build.NewScene();
            mine = Build.Rig(mineArmored, myGrids ?? new[] { (2, 2), (1, 2) });
            theirs = Build.Rig(theirsArmored, theirGrids ?? new[] { (2, 2), (2, 2), (1, 2) });
            Build.Equip(scene.MySlot(EquipmentSlot.TacticalVest), mine);
            Build.Equip(scene.TheirSlot(EquipmentSlot.TacticalVest), theirs);
            return scene;
        }

        private static void AssertSwapped(Scene scene, EquipmentSlot slot, CompoundItem mine, CompoundItem theirs)
        {
            Assert.Equal<Item>(theirs, scene.MySlot(slot).ContainedItem, "incoming should be equipped");
            Assert.Equal<Item>(mine, scene.TheirSlot(slot).ContainedItem, "outgoing should be on the corpse");
            Assert.True(mine.IsEmpty, "outgoing should be empty");
            Assert.Equal(scene.Controller.ID, theirs.Owner.ID, "incoming owner");
            Assert.Equal(scene.Corpse.ID, mine.Owner.ID, "outgoing owner");
            Assert.GridsConsistent(theirs);
        }

        private static void AssertNothingChanged(Scene scene, EquipmentSlot slot, CompoundItem mine, CompoundItem theirs, string message)
        {
            Assert.Equal<Item>(mine, scene.MySlot(slot).ContainedItem, "my slot changed");
            Assert.Equal<Item>(theirs, scene.TheirSlot(slot).ContainedItem, "their slot changed");
            Assert.Equal(0, scene.Controller.Operations, "operations executed");
            Assert.Equal(1, scene.Env.Notifications.Count, "notifications");
            Assert.Equal(message, scene.Env.Notifications[0], "message");
        }

        public static async Task Backpack_PreservesEverything()
        {
            var scene = Packs(out var mine, out var theirs);
            var ammo = Build.Thing(1, 1);
            var meds = Build.Bindable(2, 1);
            var loot = Build.Thing(2, 2);
            var existing = Build.Thing(3, 3);
            ammo.SpawnedInSession = true;
            Build.Put(mine, 0, ammo, 0, 0);
            Build.Put(mine, 0, meds, 2, 0);
            Build.Put(mine, 0, loot, 0, 1);
            Build.Put(theirs, 0, existing, 0, 0);

            await scene.Swap(EquipmentSlot.Backpack, SwapKind.Backpack);

            AssertSwapped(scene, EquipmentSlot.Backpack, mine, theirs);
            Assert.ItemsExactlyOnce(mine, theirs, ammo, meds, loot, existing);
            foreach (var item in new[] { ammo, meds, loot, existing })
            {
                Assert.True(item.Parent.Container.ParentItem == theirs, item.Id + " is not in the incoming backpack");
            }
            Assert.True(ammo.SpawnedInSession, "found-in-raid was lost");
            Assert.Equal(0, scene.Env.Notifications.Count, "success must be silent");
            Assert.Equal("SwapResult", scene.Controller.OperationTypes[0], "first operation");
            Assert.True(scene.Controller.OperationTypes.Skip(1).All(t => t == "MoveResult"), "only moves follow the swap");
        }

        public static async Task NestedCase_MovesIntactWithDescendants()
        {
            var scene = Packs(out var mine, out var theirs);
            var pouch = Build.Case(2, 2, (2, 2));
            var inner = Build.Thing(1, 1);
            Build.Put(pouch, 0, inner, 1, 1);
            Build.Put(mine, 0, pouch, 0, 0);

            await scene.Swap(EquipmentSlot.Backpack, SwapKind.Backpack);

            AssertSwapped(scene, EquipmentSlot.Backpack, mine, theirs);
            Assert.True(inner.Parent.Container.ParentItem == pouch, "descendant left its case");
            Assert.Equal(1, inner.Parent is GridItemAddress at ? at.LocationInGrid.x : -1, "descendant position");
            Assert.ItemsExactlyOnce(mine, theirs, pouch, inner);
            Assert.Equal(1, scene.Controller.OperationTypes.Count(t => t == "MoveResult"), "the case is one move, its contents none");
        }

        public static async Task TaggedCase_KeepsItsTag()
        {
            var scene = Packs(out var mine, out var theirs);
            var pouch = Build.Case(2, 2, (2, 2));
            var innerCase = Build.Case(1, 1, (1, 1));
            pouch.Tag.Set("MEDS", 3, false);
            innerCase.Tag.Set("KEYS", 5, false);
            Build.Put(pouch, 0, innerCase, 0, 0);
            Build.Put(mine, 0, pouch, 0, 0);

            await scene.Swap(EquipmentSlot.Backpack, SwapKind.Backpack);
            await Task.Delay(50);

            AssertSwapped(scene, EquipmentSlot.Backpack, mine, theirs);
            Assert.Equal("MEDS", pouch.Tag.Name, "tag name");
            Assert.Equal(3, pouch.Tag.Color, "tag color");
            Assert.Equal("KEYS", innerCase.Tag.Name, "nested tag name");
        }

        // The evidence for the operation order: the same native Move clears a tag on a cross-owner move
        // whichever way it goes, which is why the restore step exists.
        public static void Fact_CrossOwnerMoveClearsTags()
        {
            var scene = Packs(out var mine, out var theirs);
            var pouch = Build.Case(2, 2, (2, 2));
            pouch.Tag.Set("MEDS", 3, false);
            Build.Put(mine, 0, pouch, 0, 0);
            scene.SearchEverything();
            var move = ItemManipulator.Move(pouch, theirs.Grids[0].CreateItemAddress(new LocationInGrid(0, 0, ItemRotation.Horizontal)), scene.Controller, false);
            Assert.True(move.Succeeded, "move");
            Assert.True(pouch.Tag.IsEmpty, "native move no longer clears tags; the restore step can go");
        }

        // The evidence for "swap first": moving a bound item out of the player's container onto a corpse
        // clears its binding in native code, so the contents-first order would lose every hotkey.
        public static void Fact_ContentsFirstLosesBinding()
        {
            var scene = Rigs(out var mine, out var theirs);
            var med = Build.Bindable();
            Build.Put(mine, 1, med, 0, 0);
            scene.Controller.Inventory.FastAccess.BoundItems[EBoundItem.Item4] = med;
            Assert.True(scene.Controller.IsAtBindablePlace(med), "setup: bound item should be bindable");
            scene.SearchEverything();

            var move = ItemManipulator.Move(med, theirs.Grids[0].CreateItemAddress(new LocationInGrid(0, 0, ItemRotation.Horizontal)), scene.Controller, false);
            Assert.True(move.Succeeded, "move");
            Assert.True(!scene.Controller.Inventory.FastAccess.BoundItems.ContainsKey(EBoundItem.Item4), "native move no longer clears this binding");
        }

        public static async Task Rig_HotkeySurvives_NoBindOperationsNeeded()
        {
            var scene = Rigs(out var mine, out var theirs);
            var med = Build.Bindable();
            var mag = Build.Thing(1, 2);
            var existing = Build.Thing(2, 2);
            Build.Put(mine, 1, med, 0, 0);
            Build.Put(mine, 0, mag, 0, 0);
            Build.Put(theirs, 0, existing, 0, 0);
            var bound = scene.Controller.Inventory.FastAccess.BoundItems;
            bound[EBoundItem.Item4] = med;

            await scene.Swap(EquipmentSlot.TacticalVest, SwapKind.Rig);

            AssertSwapped(scene, EquipmentSlot.TacticalVest, mine, theirs);
            Assert.ItemsExactlyOnce(mine, theirs, med, mag, existing);
            Assert.Equal<Item>(med, bound[EBoundItem.Item4], "same item on the same key");
            Assert.True(scene.Controller.IsAtBindablePlace(med), "binding is legal afterwards");
            Assert.True(!scene.Controller.OperationTypes.Any(t => t == "BindResult" || t == "UnbindResult"), "bindings should survive natively: " + string.Join(",", scene.Controller.OperationTypes));
            Assert.Equal(0, scene.Env.Notifications.Count, "silent");
            Assert.True(scene.Env.Errors.Count == 0, "errors: " + string.Join(" | ", scene.Env.Errors));
        }

        public static async Task Rig_BoundItemInsideNestedCase_LosesOnlyIfIllegal()
        {
            // Vanilla does not allow binding inside a case, so if such a binding existed it is unbound,
            // and nothing else is ever substituted for it.
            var scene = Rigs(out var mine, out var theirs);
            var pouch = Build.Case(1, 1, (1, 1));
            var med = Build.Bindable();
            Build.Put(pouch, 0, med, 0, 0);
            Build.Put(mine, 0, pouch, 0, 0);
            var bound = scene.Controller.Inventory.FastAccess.BoundItems;
            bound[EBoundItem.Item5] = med;

            await scene.Swap(EquipmentSlot.TacticalVest, SwapKind.Rig);
            await Task.Delay(50);

            AssertSwapped(scene, EquipmentSlot.TacticalVest, mine, theirs);
            Assert.True(med.Parent.Container.ParentItem == pouch, "descendant stayed in its case");
            // Vanilla: binding inside a case is not legal, so the key is released, never reassigned.
            Assert.True(!bound.ContainsKey(EBoundItem.Item5), "an illegal binding was kept");
            Assert.True(scene.Env.Errors.Count == 0, "errors: " + string.Join(" | ", scene.Env.Errors));
        }

        public static async Task Rig_ExistingHotkeyOnOtherSlotUntouched_NoSubstitution()
        {
            var scene = Rigs(out var mine, out var theirs);
            var med = Build.Bindable();
            var other = Build.Bindable();
            Build.Put(mine, 1, med, 0, 0);
            Build.Equip(scene.MySlot(EquipmentSlot.Pockets), PocketsWith(other));
            var bound = scene.Controller.Inventory.FastAccess.BoundItems;
            bound[EBoundItem.Item4] = med;
            bound[EBoundItem.Item5] = other;

            await scene.Swap(EquipmentSlot.TacticalVest, SwapKind.Rig);

            Assert.Equal<Item>(med, bound[EBoundItem.Item4], "key 4");
            Assert.Equal<Item>(other, bound[EBoundItem.Item5], "key 5");
        }

        private static CompoundItem PocketsWith(Item item)
        {
            var pockets = new CompoundItem(Build.Id(), new CompoundItemTemplate { CanPutIntoDuringTheRaid = true, StackObjectsCount = 1, StackMaxSize = 1, Grids = Build.Grids((1, 1)) });
            Build.Put(pockets, 0, item, 0, 0);
            return pockets;
        }

        public static async Task BodyArmor_ConflictRejected_NothingMoves()
        {
            var scene = Rigs(out var mine, out var theirs, theirsArmored: true);
            Build.Equip(scene.MySlot(EquipmentSlot.ArmorVest), new CompoundItem(Build.Id(), new CompoundItemTemplate { CanPutIntoDuringTheRaid = true, StackObjectsCount = 1, StackMaxSize = 1 }));
            var med = Build.Bindable();
            Build.Put(mine, 1, med, 0, 0);

            await scene.Swap(EquipmentSlot.TacticalVest, SwapKind.Rig);

            AssertNothingChanged(scene, EquipmentSlot.TacticalVest, mine, theirs, Messages.BodyArmorConflict);
            Assert.True(med.Parent.Container.ParentItem == mine, "item moved on a rejected swap");
        }

        public static async Task ArmoredRig_NoBodyArmor_Swaps()
        {
            var scene = Rigs(out var mine, out var theirs, theirsArmored: true);
            await scene.Swap(EquipmentSlot.TacticalVest, SwapKind.Rig);
            AssertSwapped(scene, EquipmentSlot.TacticalVest, mine, theirs);
        }

        public static async Task OutgoingArmoredRig_CorpseWearsArmor_Rejected()
        {
            var scene = Rigs(out var mine, out var theirs, mineArmored: true);
            Build.Equip(scene.TheirSlot(EquipmentSlot.ArmorVest), new CompoundItem(Build.Id(), new CompoundItemTemplate { CanPutIntoDuringTheRaid = true, StackObjectsCount = 1, StackMaxSize = 1 }));

            await scene.Swap(EquipmentSlot.TacticalVest, SwapKind.Rig);

            Assert.Equal<Item>(mine, scene.MySlot(EquipmentSlot.TacticalVest).ContainedItem, "my slot changed");
            Assert.Equal(0, scene.Controller.Operations, "operations executed");
            Assert.True(scene.Env.Notifications.Single() != Messages.BodyArmorConflict, "this is the body's armor, not the player's");
            Assert.True(scene.Env.Notifications.Single().Contains("rig"), "message: " + scene.Env.Notifications.Single());
        }

        public static async Task Unsearched_Rejected_WithExactMessage()
        {
            var scene = Packs(out var mine, out var theirs);
            scene.Controller.Search.UncoverContent(mine);
            await scene.Swap(EquipmentSlot.Backpack, SwapKind.Backpack, searchFirst: false);
            AssertNothingChanged(scene, EquipmentSlot.Backpack, mine, theirs, Messages.SearchRequired);
        }

        public static async Task PartlyDiscovered_Rejected_WithExactMessage()
        {
            var scene = Packs(out var mine, out var theirs);
            scene.SearchEverything();
            // Searched once, then something new arrived that the player has not discovered.
            Build.Put(theirs, 0, Build.Thing(1, 1), 0, 0);
            await scene.Swap(EquipmentSlot.Backpack, SwapKind.Backpack, searchFirst: false);
            AssertNothingChanged(scene, EquipmentSlot.Backpack, mine, theirs, Messages.SearchRequired);
        }

        public static async Task UnsearchedNestedCase_MovesIntact_IsAllowed()
        {
            // Undiscovered contents inside a direct, known case do not block the swap.
            var scene = Packs(out var mine, out var theirs);
            var nested = Build.Pack((2, 2));
            Build.Put(nested, 0, Build.Thing(1, 1), 0, 0);
            Build.Put(theirs, 0, nested, 0, 0);

            // Search their backpack only: the nested bag is seen, its contents are not.
            var search = scene.Controller.Search;
            search.UncoverContent(mine);
            search.UncoverItem(theirs);
            search.DiscoverItem(nested);
            Assert.True(!search.IsSearched(nested), "setup: nested bag should be unsearched");

            await scene.Swap(EquipmentSlot.Backpack, SwapKind.Backpack, searchFirst: false);

            AssertSwapped(scene, EquipmentSlot.Backpack, mine, theirs);
            Assert.True(!search.IsSearched(nested), "search state must not change");
            Assert.True(search.ContainsUnknownItems(nested), "hidden contents must stay hidden");
        }

        public static async Task LargeArmor_RequiresRealRearrangement()
        {
            // The spec's example: armor fits only the main compartment, where a small item already sits.
            var scene = Packs(out var mine, out var theirs, theirGrids: new[] { (2, 2), (3, 3) });
            var armor = Build.Thing(3, 3);
            var small = Build.Thing(1, 1);
            Build.Put(mine, 0, armor, 0, 0);
            Build.Put(theirs, 1, small, 1, 1);

            await scene.Swap(EquipmentSlot.Backpack, SwapKind.Backpack);

            AssertSwapped(scene, EquipmentSlot.Backpack, mine, theirs);
            Assert.True(armor.Parent.Container == theirs.Grids[1], "armor belongs in the main compartment");
            Assert.True(small.Parent.Container == theirs.Grids[0], "small item should move to the side pocket");
            Assert.Equal(0, scene.Env.Notifications.Count, "silent");
        }

        public static async Task TooLargeForAnyPocket_ReportsSpace_DespiteFreeCells()
        {
            var scene = Packs(out var mine, out var theirs, theirGrids: new[] { (2, 2), (2, 2), (2, 2) });
            Build.Put(mine, 0, Build.Thing(3, 3), 0, 0);
            await scene.Swap(EquipmentSlot.Backpack, SwapKind.Backpack);
            AssertNothingChanged(scene, EquipmentSlot.Backpack, mine, theirs, Messages.BackpackNoSpace);
        }

        public static async Task Rig_NoSpace_HasRigWording()
        {
            var scene = Rigs(out var mine, out var theirs, theirGrids: new[] { (1, 1) });
            Build.Put(mine, 0, Build.Thing(2, 2), 0, 0);
            await scene.Swap(EquipmentSlot.TacticalVest, SwapKind.Rig);
            AssertNothingChanged(scene, EquipmentSlot.TacticalVest, mine, theirs, Messages.RigNoSpace);
        }

        public static async Task RealFilter_BlocksItem_ReportsRestrictionNotSpace()
        {
            var scene = Build.NewScene();
            var mine = Build.Pack((4, 4));
            var item = Build.Thing(1, 1);
            var filter = new ItemFilter { Filter = new MongoID[0], ExcludedFilter = new MongoID[] { item.TemplateId } };
            var theirs = new Backpack(Build.Id(), new BackpackTemplate { CanPutIntoDuringTheRaid = true, StackObjectsCount = 1, StackMaxSize = 1, Grids = new[] { new Grid("g", 6, 6, false, false, new[] { filter }, null) } });
            Build.Equip(scene.MySlot(EquipmentSlot.Backpack), mine);
            Build.Equip(scene.TheirSlot(EquipmentSlot.Backpack), theirs);
            Build.Put(mine, 0, item, 0, 0);

            await scene.Swap(EquipmentSlot.Backpack, SwapKind.Backpack);

            AssertNothingChanged(scene, EquipmentSlot.Backpack, mine, theirs, Messages.Restricted);
        }

        public static async Task PocketLimit_MaxItemsCount_IsRespected()
        {
            // Template grids lose their limit when cloned onto an item (Grid(Grid, parent)), so the
            // limit is set on the live grid, the only thing the game and this mod both read.
            var scene = Build.NewScene();
            var mine = Build.Pack((4, 4));
            var theirs = new Backpack(Build.Id(), new BackpackTemplate
            {
                CanPutIntoDuringTheRaid = true,
                StackObjectsCount = 1,
                StackMaxSize = 1,
                Grids = new[] { new Grid("a", 4, 4, false, false, new ItemFilter[0], null, 1), new Grid("b", 2, 2, false, false, new ItemFilter[0], null) },
            });
            Build.Equip(scene.MySlot(EquipmentSlot.Backpack), mine);
            Build.Equip(scene.TheirSlot(EquipmentSlot.Backpack), theirs);
            theirs.Grids[0].MaxItemsCount = 1;
            var one = Build.Thing(1, 1);
            var two = Build.Thing(1, 1);
            Build.Put(mine, 0, one, 0, 0);
            Build.Put(mine, 0, two, 1, 0);

            await scene.Swap(EquipmentSlot.Backpack, SwapKind.Backpack);

            AssertSwapped(scene, EquipmentSlot.Backpack, mine, theirs);
            Assert.True(theirs.Grids[0].Items.Count() <= 1, "grid limit ignored");
        }

        public static async Task IndependentBelt_Unchanged_NotUsedForSpace()
        {
            // Pack 'n' Strap equips belts in the ArmBand slot.
            var scene = Packs(out var mine, out var theirs, theirGrids: new[] { (1, 1) });
            var belt = Build.Rig(false, (4, 4));
            var beltMed = Build.Bindable();
            Build.Put(belt, 0, beltMed, 0, 0);
            Build.Equip(scene.MySlot(EquipmentSlot.ArmBand), belt);
            var bound = scene.Controller.Inventory.FastAccess.BoundItems;
            bound[EBoundItem.Item6] = beltMed;
            Build.Put(mine, 0, Build.Thing(2, 2), 0, 0); // Would fit the belt; must not go there.

            await scene.Swap(EquipmentSlot.Backpack, SwapKind.Backpack);

            AssertNothingChanged(scene, EquipmentSlot.Backpack, mine, theirs, Messages.BackpackNoSpace);
            Assert.Equal<Item>(belt, scene.MySlot(EquipmentSlot.ArmBand).ContainedItem, "belt slot");
            Assert.Equal<Item>(beltMed, bound[EBoundItem.Item6], "belt binding");
            Assert.Equal(1, belt.Grids[0].Items.Count(), "belt contents");
        }

        public static async Task IndependentBelt_SuccessfulSwapLeavesItAlone()
        {
            var scene = Packs(out var mine, out var theirs);
            var belt = Build.Rig(false, (2, 2));
            var beltMed = Build.Bindable();
            Build.Put(belt, 0, beltMed, 1, 1);
            Build.Equip(scene.MySlot(EquipmentSlot.ArmBand), belt);
            Build.Put(mine, 0, Build.Thing(1, 1), 0, 0);

            await scene.Swap(EquipmentSlot.Backpack, SwapKind.Backpack);

            AssertSwapped(scene, EquipmentSlot.Backpack, mine, theirs);
            Assert.Equal<Item>(belt, scene.MySlot(EquipmentSlot.ArmBand).ContainedItem, "belt slot");
            Assert.True(beltMed.Parent.Container.ParentItem == belt, "belt contents moved");
        }

        public static async Task ItemInHands_InOutgoing_Rejected()
        {
            var scene = Rigs(out var mine, out var theirs);
            var grenade = Build.Thing(1, 1);
            Build.Put(mine, 0, grenade, 0, 0);
            scene.Controller.Hands = grenade;
            await scene.Swap(EquipmentSlot.TacticalVest, SwapKind.Rig);
            Assert.Equal(0, scene.Controller.Operations, "operations");
            Assert.True(scene.Env.Notifications.Single().Contains("hands"), scene.Env.Notifications.Single());
        }

        public static async Task AttachedModuleWithContents_Rejected_NotAbandoned()
        {
            var scene = Build.NewScene();
            var module = new CompoundItem(Build.Id(), new CompoundItemTemplate { CanPutIntoDuringTheRaid = true, StackObjectsCount = 1, StackMaxSize = 1, Grids = Build.Grids((1, 1)) });
            var inside = Build.Thing(1, 1);
            Build.Put(module, 0, inside, 0, 0);
            var mine = new Backpack(Build.Id(), new BackpackTemplate
            {
                CanPutIntoDuringTheRaid = true,
                StackObjectsCount = 1,
                StackMaxSize = 1,
                Grids = Build.Grids((2, 2)),
                Slots = new[] { new Slot("mod_pouch", new ItemFilter[0], false, EParentMergeType.InheritFromItem) },
            });
            Build.Equip(mine.Slots[0], module);
            var theirs = Build.Pack((4, 4));
            Build.Equip(scene.MySlot(EquipmentSlot.Backpack), mine);
            Build.Equip(scene.TheirSlot(EquipmentSlot.Backpack), theirs);

            await scene.Swap(EquipmentSlot.Backpack, SwapKind.Backpack);

            Assert.Equal(0, scene.Controller.Operations, "operations");
            Assert.True(scene.Env.Notifications.Single().Contains("pouches"), scene.Env.Notifications.Single());
            Assert.True(inside.Parent.Container.ParentItem == module, "module contents moved");
        }

        public static async Task AttachedEmptyModule_StaysOnItsParent()
        {
            var scene = Build.NewScene();
            var plate = new CompoundItem(Build.Id(), new CompoundItemTemplate { CanPutIntoDuringTheRaid = true, StackObjectsCount = 1, StackMaxSize = 1 });
            var mine = new Vest(Build.Id(), new VestTemplate
            {
                CanPutIntoDuringTheRaid = true,
                StackObjectsCount = 1,
                StackMaxSize = 1,
                Grids = Build.Grids((2, 2)),
                Slots = new[] { new Slot("front_plate", new ItemFilter[0], false, EParentMergeType.InheritFromItem) },
            });
            Build.Equip(mine.Slots[0], plate);
            var theirs = Build.Rig(false, (4, 4));
            Build.Equip(scene.MySlot(EquipmentSlot.TacticalVest), mine);
            Build.Equip(scene.TheirSlot(EquipmentSlot.TacticalVest), theirs);

            await scene.Swap(EquipmentSlot.TacticalVest, SwapKind.Rig);

            AssertSwapped(scene, EquipmentSlot.TacticalVest, mine, theirs);
            Assert.True(plate.Parent.Container.ParentItem == mine, "the plate belongs to its rig");
        }

        public static async Task ScreenClosedBeforeStart_Rejected()
        {
            var scene = Packs(out var mine, out var theirs);
            scene.Env.ScreenOpen = false;
            await scene.Swap(EquipmentSlot.Backpack, SwapKind.Backpack);
            Assert.Equal(0, scene.Controller.Operations, "operations");
            Assert.Equal(1, scene.Env.Notifications.Count, "notifications");
        }

        public static async Task ScreenClosesMidSwap_StillFinishes()
        {
            // Closing the UI after the swap must not strand items: the moves need no UI.
            var scene = Packs(out var mine, out var theirs);
            var a = Build.Thing(1, 1);
            var b = Build.Thing(1, 1);
            Build.Put(mine, 0, a, 0, 0);
            Build.Put(mine, 0, b, 1, 0);
            scene.SearchEverything();
            var gateway = new GameGateway(scene.MySlot(EquipmentSlot.Backpack), theirs, scene.Controller, SwapKind.Backpack, scene.Env);
            var closing = new ClosingGateway(gateway, scene.Env);
            await new SwapCoordinator(closing, SwapKind.Backpack).RunAsync();
            AssertSwapped(scene, EquipmentSlot.Backpack, mine, theirs);
            Assert.Equal(0, scene.Env.Notifications.Count, "silent");
        }

        public static async Task RefusedSwapOperation_NothingChanges()
        {
            var scene = Packs(out var mine, out var theirs);
            var item = Build.Thing(1, 1);
            Build.Put(mine, 0, item, 0, 0);
            scene.Controller.RefuseFromOperation = 0;
            await scene.Swap(EquipmentSlot.Backpack, SwapKind.Backpack);
            Assert.Equal<Item>(mine, scene.MySlot(EquipmentSlot.Backpack).ContainedItem, "my slot");
            Assert.Equal<Item>(theirs, scene.TheirSlot(EquipmentSlot.Backpack).ContainedItem, "their slot");
            Assert.True(item.Parent.Container.ParentItem == mine, "item");
            Assert.Equal(1, scene.Env.Notifications.Count, "notifications");
        }

        public static async Task FaultAtEveryOperation_NothingLostOrDuplicated()
        {
            for (int failAt = 1; failAt <= 4; failAt++)
            {
                foreach (int count in new[] { 1, 100 })
                {
                    var scene = Packs(out var mine, out var theirs);
                    var items = new[] { Build.Thing(1, 1), Build.Thing(2, 1), Build.Thing(2, 2), Build.Bindable() };
                    Build.Put(mine, 0, items[0], 0, 0);
                    Build.Put(mine, 0, items[1], 1, 0);
                    Build.Put(mine, 0, items[2], 0, 1);
                    Build.Put(mine, 0, items[3], 3, 0);
                    scene.Controller.RefuseFromOperation = failAt;
                    scene.Controller.RefuseCount = count;

                    await scene.Swap(EquipmentSlot.Backpack, SwapKind.Backpack);

                    Assert.ItemsExactlyOnce(mine, theirs, items);
                    Assert.GridsConsistent(mine);
                    Assert.GridsConsistent(theirs);
                    Assert.Equal<Item>(theirs, scene.MySlot(EquipmentSlot.Backpack).ContainedItem, "swap stands");
                    int left = mine.Grids[0].Items.Count();
                    if (count == 1)
                    {
                        Assert.Equal(0, left, $"one refusal at {failAt} should recover");
                        Assert.Equal(0, scene.Env.Notifications.Count, "recovered swap is silent");
                    }
                    else
                    {
                        Assert.True(left > 0, "expected leftovers");
                        Assert.Equal(Messages.Interrupted(SwapKind.Backpack, left), scene.Env.Notifications.Single(), "real count, once");
                    }
                }
            }
        }

        public static void DryRun_RestoresExactState()
        {
            // Real moves and a real rollback, including a binding that the pre-swap move clears.
            var scene = Rigs(out var mine, out var theirs);
            var med = Build.Bindable();
            var mag = Build.Thing(1, 2);
            var existing = Build.Thing(2, 2);
            var pouch = Build.Case(1, 1, (1, 1));
            pouch.Tag.Set("TAG", 2, false);
            Build.Put(mine, 1, med, 0, 0);
            Build.Put(mine, 0, mag, 0, 0);
            Build.Put(mine, 0, pouch, 1, 0);
            Build.Put(theirs, 0, existing, 0, 0);
            var bound = scene.Controller.Inventory.FastAccess.BoundItems;
            bound[EBoundItem.Item4] = med;
            scene.SearchEverything();

            var gateway = new GameGateway(scene.MySlot(EquipmentSlot.TacticalVest), theirs, scene.Controller, SwapKind.Rig, scene.Env);
            var request = gateway.BuildRequest();
            var arrangement = ArrangementSolver.Solve(request);
            Assert.Equal(ArrangementStatus.Found, arrangement.Status, "solver");
            var steps = MovePlanner.Plan(request, arrangement.Placements);
            string before = gateway.Fingerprint();

            string error = gateway.DryRun(steps);

            Assert.True(error == null, "dry run refused a legal plan: " + error);
            Assert.Equal(before, gateway.Fingerprint(), "fingerprint after rollback");
            Assert.Equal<Item>(med, bound[EBoundItem.Item4], "binding after rollback");
            Assert.Equal("TAG", pouch.Tag.Name, "tag after rollback");
            Assert.GridsConsistent(mine);
            Assert.GridsConsistent(theirs);
            Assert.Equal(0, scene.Controller.Operations, "a dry run must send nothing");
            Assert.Equal(0, scene.Corpse.Views.Count, "no views");
        }

        public static async Task PlanWithParking_DryRunAcceptsItAndItRuns()
        {
            // Two incoming items that must trade pockets: only parking one makes the plan legal, so it
            // visits an address the search controller has never seen until the move really runs.
            var scene = Build.NewScene();
            var mine = Build.Pack((2, 2));
            var theirs = Build.Pack((2, 1), (1, 2), (3, 3));
            Build.Equip(scene.MySlot(EquipmentSlot.Backpack), mine);
            Build.Equip(scene.TheirSlot(EquipmentSlot.Backpack), theirs);
            var a = Build.Thing(2, 1);
            var b = Build.Thing(1, 2);
            Build.Put(theirs, 0, a, 0, 0);
            Build.Put(theirs, 1, b, 0, 0);
            scene.SearchEverything();

            var gateway = new GameGateway(scene.MySlot(EquipmentSlot.Backpack), theirs, scene.Controller, SwapKind.Backpack, scene.Env);
            var request = gateway.BuildRequest();
            var targets = new System.Collections.Generic.Dictionary<string, Placement>
            {
                [a.Id] = new Placement(ContainerSide.Incoming, 1, 0, 0, true),
                [b.Id] = new Placement(ContainerSide.Incoming, 0, 0, 0, true),
            };
            var steps = MovePlanner.Plan(request, targets);
            Assert.True(steps != null && steps.Any(s => s.ToScratch), "this plan should need parking");
            string before = gateway.Fingerprint();

            string error = gateway.DryRun(steps);

            Assert.True(error == null, "dry run refused a legal parking plan: " + error);
            Assert.Equal(before, gateway.Fingerprint(), "state after dry run");
            Assert.True(!scene.Controller.Search._temporaryKnownItems.Any(), "temporary known state must be released");

            foreach (var step in steps)
            {
                Assert.True(gateway.IsStepValid(step), "step should be valid live: " + step);
                var outcome = await gateway.ExecuteMoveAsync(step);
                Assert.True(outcome.Succeeded, "live step refused: " + outcome.Error);
            }
            Assert.True(a.Parent.Container == theirs.Grids[1], "a landed in the wrong pocket");
            Assert.True(b.Parent.Container == theirs.Grids[0], "b landed in the wrong pocket");
        }

        public static async Task OutgoingUnsearched_WithContents_Rejected()
        {
            // A bag equipped without searching it. The game would refuse every move out of it.
            var scene = Packs(out var mine, out var theirs);
            var hidden = Build.Thing(1, 1);
            Build.Put(mine, 0, hidden, 0, 0);
            scene.Controller.Search.UncoverContent(theirs);
            await scene.Swap(EquipmentSlot.Backpack, SwapKind.Backpack, searchFirst: false);
            AssertNothingChanged(scene, EquipmentSlot.Backpack, mine, theirs, Messages.SearchRequired);
        }

        public static void DryRun_RefusalAlsoRollsBack()
        {
            var scene = Packs(out var mine, out var theirs);
            var a = Build.Thing(1, 1);
            var b = Build.Thing(1, 1);
            Build.Put(mine, 0, a, 0, 0);
            Build.Put(mine, 0, b, 1, 0);
            scene.SearchEverything();
            var gateway = new GameGateway(scene.MySlot(EquipmentSlot.Backpack), theirs, scene.Controller, SwapKind.Backpack, scene.Env);
            string before = gateway.Fingerprint();
            var steps = new[]
            {
                new MoveStep(a.Id, new Placement(ContainerSide.Outgoing, 0, 0, 0, false), new Placement(ContainerSide.Incoming, 0, 0, 0, false), false),
                // Same target as the step before: the game must refuse it here.
                new MoveStep(b.Id, new Placement(ContainerSide.Outgoing, 0, 1, 0, false), new Placement(ContainerSide.Incoming, 0, 0, 0, false), false),
            };
            string error = gateway.DryRun(steps);
            Assert.True(error != null, "an overlapping plan must be refused");
            Assert.True(error == Messages.InventoryChanged, "must not claim no space: " + error);
            Assert.Equal(before, gateway.Fingerprint(), "state after a refused dry run");
        }

        public static async Task Rotation_IsApplied_ByTheRealGrid()
        {
            var scene = Packs(out var mine, out var theirs, theirGrids: new[] { (1, 3) });
            var rifle = Build.Thing(3, 1);
            Build.Put(mine, 0, rifle, 0, 0);
            await scene.Swap(EquipmentSlot.Backpack, SwapKind.Backpack);
            AssertSwapped(scene, EquipmentSlot.Backpack, mine, theirs);
            Assert.Equal(ItemRotation.Vertical, ((GridItemAddress)rifle.Parent).LocationInGrid.r, "rotation");
        }

        public static async Task RepeatedSwaps_RoundTrip()
        {
            var scene = Packs(out var mine, out var theirs);
            var item = Build.Thing(1, 1);
            Build.Put(mine, 0, item, 0, 0);
            await scene.Swap(EquipmentSlot.Backpack, SwapKind.Backpack);
            AssertSwapped(scene, EquipmentSlot.Backpack, mine, theirs);
            await scene.Swap(EquipmentSlot.Backpack, SwapKind.Backpack);
            AssertSwapped(scene, EquipmentSlot.Backpack, theirs, mine);
            Assert.True(item.Parent.Container.ParentItem == mine, "the item followed both swaps");
            Assert.ItemsExactlyOnce(mine, theirs, item);
        }
    }

    // Reports the screen closed as soon as the swap has happened.
    internal sealed class ClosingGateway : ISwapGateway
    {
        private readonly ISwapGateway _inner;
        private readonly TestEnvironment _env;

        public ClosingGateway(ISwapGateway inner, TestEnvironment env)
        {
            _inner = inner;
            _env = env;
        }

        public string ValidatePreconditions() => _inner.ValidatePreconditions();
        public ArrangementRequest BuildRequest() => _inner.BuildRequest();
        public string Fingerprint() => _inner.Fingerprint();
        public Task<ArrangementResult> SolveAsync(ArrangementRequest request) => _inner.SolveAsync(request);
        public string DryRun(System.Collections.Generic.IReadOnlyList<MoveStep> steps) => _inner.DryRun(steps);

        public async Task<OperationOutcome> ExecuteSwapAsync()
        {
            var outcome = await _inner.ExecuteSwapAsync();
            _env.ScreenOpen = false;
            return outcome;
        }

        public bool IsSwapApplied() => _inner.IsSwapApplied();
        public bool IsStepValid(in MoveStep step) => _inner.IsStepValid(step);
        public Task<OperationOutcome> ExecuteMoveAsync(MoveStep step) => _inner.ExecuteMoveAsync(step);
        public int CountOutgoingContents() => _inner.CountOutgoingContents();
        public bool CanContinue() => _inner.CanContinue();
        public Task NextFrame() => _inner.NextFrame();
        public Task AckTimeout() => _inner.AckTimeout();
        public void RestoreItemData() => _inner.RestoreItemData();
        public void Notify(string message) => _inner.Notify(message);
        public void Log(string message) => _inner.Log(message);
        public void LogError(string message) => _inner.LogError(message);
    }
}
