using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EFT;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using SeamlessSwap.Core;
using SeamlessSwap.Game;

namespace SeamlessSwap.Harness
{
    /// <summary>
    /// Loads the real UI Fixes and Pack 'n' Strap client DLLs next to this mod and applies their actual
    /// patches on the methods both mods touch. Runs only when those DLLs were copied in (see
    /// scripts\run-harness.ps1 -ExtraAssemblies). It proves coexistence at the patch level and runs swaps
    /// under the other mods' real inventory patches. It cannot prove UI behavior: there is no UI here.
    /// </summary>
    internal static class Compat
    {
        public static bool Available(string dir)
        {
            return File.Exists(Path.Combine(dir, "Tyfon.UIFixes.dll"));
        }

        public static void Run(string dir)
        {
            Console.WriteLine();
            Console.WriteLine("Compatibility run with the real mod DLLs:");

            var uiFixes = Assembly.LoadFrom(Path.Combine(dir, "Tyfon.UIFixes.dll"));
            Describe(uiFixes);
            Assembly packNStrap = null;
            string pnsPath = Path.Combine(dir, "WTT-PackNStrapClient.dll");
            if (File.Exists(pnsPath))
            {
                packNStrap = Assembly.LoadFrom(pnsPath);
                Describe(packNStrap);
            }

            // UI Fixes patches that sit on the same methods, or run on the operations this mod issues.
            foreach (string name in new[]
            {
                "UIFixes.MultiSelectPatches+SlotViewCanAcceptPatch",
                "UIFixes.MultiSelectPatches+SlotViewAcceptItemPatch",
                "UIFixes.SwapPatches+SlotCanAcceptSwapPatch",
                "UIFixes.SwapPatches+SwapOperationRaiseEventsPatch",
            })
            {
                Enable(uiFixes, name);
            }

            if (packNStrap != null)
            {
                Enable(packNStrap, "PackNStrap.Patches.IsAtBindablePlacePatch");
                Enable(packNStrap, "PackNStrap.Patches.IsAtReachablePlacePatch");
            }

            var harmony = new Harmony("com.joelhauser.seamlessswap.harness");
            harmony.PatchAll(typeof(SlotViewCanAcceptPatch));
            harmony.PatchAll(typeof(SlotViewAcceptItemPatch));

            foreach (var method in new MethodBase[]
            {
                AccessTools.Method(typeof(SlotView), nameof(SlotView.CanAccept)),
                AccessTools.Method(typeof(SlotView), nameof(SlotView.AcceptItem)),
                AccessTools.Method(typeof(DragItemContext), nameof(DragItemContext.CanAccept)),
                AccessTools.Method(typeof(InventoryController), nameof(InventoryController.IsAtBindablePlace)),
            })
            {
                var info = Harmony.GetPatchInfo(method);
                Console.WriteLine($"  {method.DeclaringType.Name}.{method.Name}:");
                Console.WriteLine("    prefixes:  " + string.Join(", ", info.Prefixes.Select(p => $"{p.PatchMethod.DeclaringType.FullName} [{p.owner}, priority {p.priority}]")));
                Console.WriteLine("    postfixes: " + string.Join(", ", info.Postfixes.Select(p => $"{p.PatchMethod.DeclaringType.FullName} [{p.owner}, priority {p.priority}]")));
            }

            Check("UI Fixes multi-select interop resolves and reads zero", () =>
            {
                int count = UiFixesInterop.MultiSelectCount();
                Assert.Equal(0, count, "selection count");
            });

            Check("rig swap under UI Fixes' swap event patch and Pack 'n' Strap's bind rules", () => MainLoop.Run(async () =>
            {
                var scene = Build.NewScene();
                var mine = Build.Rig(false, (2, 2), (1, 2));
                var theirs = Build.Rig(false, (2, 2), (2, 2), (1, 2));
                Build.Equip(scene.MySlot(EquipmentSlot.TacticalVest), mine);
                Build.Equip(scene.TheirSlot(EquipmentSlot.TacticalVest), theirs);
                var direct = Build.Bindable();
                var pouch = Build.Case(1, 1, (1, 1));
                var inPouch = Build.Bindable();
                Build.Put(pouch, 0, inPouch, 0, 0);
                Build.Put(mine, 1, direct, 0, 0);
                Build.Put(mine, 0, pouch, 0, 0);
                var belt = Build.Rig(false, (2, 2));
                var beltItem = Build.Bindable();
                Build.Put(belt, 0, beltItem, 0, 0);
                Build.Equip(scene.MySlot(EquipmentSlot.ArmBand), belt);

                var bound = scene.Controller.Inventory.FastAccess.BoundItems;
                bound[EBoundItem.Item4] = direct;
                bound[EBoundItem.Item7] = beltItem;
                bool pnsAllowsCase = scene.Controller.IsAtBindablePlace(inPouch);
                if (pnsAllowsCase)
                {
                    bound[EBoundItem.Item5] = inPouch;
                }
                Console.WriteLine("    item inside a case in the rig is bindable under these patches: " + pnsAllowsCase);

                await scene.Swap(EquipmentSlot.TacticalVest, SwapKind.Rig);

                Assert.Equal<Item>(theirs, scene.MySlot(EquipmentSlot.TacticalVest).ContainedItem, "incoming equipped");
                Assert.True(mine.IsEmpty, "outgoing empty");
                Assert.Equal<Item>(direct, bound[EBoundItem.Item4], "direct rig item keeps key 4");
                Assert.Equal<Item>(beltItem, bound[EBoundItem.Item7], "belt item keeps key 7");
                Assert.True(beltItem.Parent.Container.ParentItem == belt, "belt contents untouched");
                if (pnsAllowsCase)
                {
                    Assert.Equal<Item>(inPouch, bound[EBoundItem.Item5], "item in case keeps key 5 where Pack 'n' Strap allows it");
                }
                Assert.Equal(0, scene.Env.Notifications.Count, "silent");
                Assert.Equal(0, scene.Env.Errors.Count, "errors: " + string.Join(" | ", scene.Env.Errors));
            }));
        }

        private static void Describe(Assembly assembly)
        {
            foreach (var type in SafeTypes(assembly))
            {
                foreach (var attribute in type.GetCustomAttributes(typeof(BepInEx.BepInPlugin), false).Cast<BepInEx.BepInPlugin>())
                {
                    Console.WriteLine($"  {Path.GetFileName(assembly.Location)}: GUID {attribute.GUID}, version {attribute.Version}");
                }
            }
        }

        private static Type[] SafeTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null).ToArray();
            }
        }

        private static void Enable(Assembly assembly, string typeName)
        {
            var type = assembly.GetType(typeName, true);
            var patch = Activator.CreateInstance(type);
            type.GetMethod("Enable", BindingFlags.Public | BindingFlags.Instance).Invoke(patch, null);
            Console.WriteLine("  applied " + typeName);
        }

        private static void Check(string name, Action body)
        {
            try
            {
                body();
                Console.WriteLine("  ok    " + name);
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? tie.InnerException : ex;
                Program.CompatFailures++;
                Console.WriteLine("  FAIL  " + name + ": " + inner.GetType().Name + ": " + inner.Message);
            }
        }
    }
}
