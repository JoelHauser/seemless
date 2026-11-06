using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.InventoryLogic;
using SeamlessSwap.Core;
using SeamlessSwap.Game;

namespace SeamlessSwap.Harness
{
    // Real game inventory classes, shipped gateway and coordinator, in the game's mono runtime.
    // What is NOT real here: the UI, hands controller, Player, network, and every Unity call.
    internal static class Program
    {
        private static int _passed;
        public static int CompatFailures;
        private static readonly List<string> Failures = new List<string>();

        private static int Main()
        {
            Console.WriteLine("Assembly-CSharp: " + typeof(Item).Assembly.Location);
            Setup();

            foreach (var test in typeof(Tests).GetMethods(BindingFlags.Static | BindingFlags.Public).OrderBy(m => m.MetadataToken))
            {
                try
                {
                    MainLoop.Run(() => test.Invoke(null, null) as Task ?? Task.CompletedTask);
                    _passed++;
                    Console.WriteLine("  ok    " + test.Name);
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException tie ? tie.InnerException : ex;
                    Failures.Add(test.Name);
                    Console.WriteLine("  FAIL  " + test.Name + ": " + inner.GetType().Name + ": " + inner.Message);
                    Console.WriteLine(inner.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("SeamlessSwap")) ?? inner.StackTrace);
                }
            }

            Console.WriteLine();
            Console.WriteLine($"{_passed} passed, {Failures.Count} failed");

            string dir = System.IO.Path.GetDirectoryName(typeof(Program).Assembly.Location);
            if (Compat.Available(dir))
            {
                try
                {
                    Compat.Run(dir);
                }
                catch (Exception ex)
                {
                    CompatFailures++;
                    Console.WriteLine("  FAIL  compatibility setup: " + ex);
                }
                Console.WriteLine($"compatibility: {CompatFailures} failed");
            }
            return Failures.Count == 0 && CompatFailures == 0 ? 0 : 1;
        }

        private static void Setup()
        {
            // The controller constructor reads this singleton. An empty one means no raid restrictions,
            // no backpack-depth limit and no discard limits, which is what SPT ships unless configured.
            var config = (GlobalConfiguration)FormatterServices.GetUninitializedObject(typeof(GlobalConfiguration));
            Singleton<GlobalConfiguration>.Create(config);
#pragma warning disable CS0618
            InGameStatus.InRaid = true;
#pragma warning restore CS0618
        }
    }

    /// <summary>
    /// Stands in for Unity's synchronization context: one thread runs everything, and anything posted
    /// (an await continuation, Task.Yield) runs on a later pass of the loop, the way Unity runs posted
    /// work once per frame. Without it, continuations land on pool threads and race the game's own
    /// async operation pipeline, which can never happen in the game. A pass drains only what was queued
    /// when it began, so Frame counts what Unity's would.
    /// </summary>
    internal sealed class MainLoop : System.Threading.SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<KeyValuePair<System.Threading.SendOrPostCallback, object>> _queue =
            new System.Collections.Concurrent.BlockingCollection<KeyValuePair<System.Threading.SendOrPostCallback, object>>();

        public static int Frame;

        public override void Post(System.Threading.SendOrPostCallback d, object state)
        {
            _queue.Add(new KeyValuePair<System.Threading.SendOrPostCallback, object>(d, state));
        }

        public override void Send(System.Threading.SendOrPostCallback d, object state)
        {
            throw new NotSupportedException();
        }

        public static void Run(Func<Task> body)
        {
            var previous = Current;
            var loop = new MainLoop();
            SetSynchronizationContext(loop);
            try
            {
                Task task = body();
                task.ContinueWith(_ => loop._queue.CompleteAdding(), TaskScheduler.Default);
                // A test that never finishes must fail, not hang the run.
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (!loop._queue.IsCompleted)
                {
                    if (loop._queue.TryTake(out var work, 100))
                    {
                        int rest = loop._queue.Count;
                        work.Key(work.Value);
                        for (int i = 0; i < rest && loop._queue.TryTake(out work); i++)
                        {
                            work.Key(work.Value);
                        }
                        Frame++;
                    }
                    if (DateTime.UtcNow > deadline)
                    {
                        throw new TimeoutException("test did not finish within 60 s");
                    }
                }
                task.GetAwaiter().GetResult();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }

    internal sealed class ProfileStub : IInventoryProfileInfo
    {
        public Inventory InventoryInfo { get; set; }
        public string ProfileId => "aaaaaaaaaaaaaaaaaaaaaaaa";
        public EPlayerSide Side => EPlayerSide.Usec;
        public string Nickname => "Harness";
        public IInventoryProfileSkillInfo SkillsInfo => null;
        public int Experience { get; set; }
        public int MagDrillsMastering => 0;
        public Dictionary<MongoID, Profile.TraderInfo> TradersInfo { get; } = new Dictionary<MongoID, Profile.TraderInfo>();
        public bool ForgetInEncyclopedia(MongoID itemId) => false;
        public void LearnInEncyclopedia(MongoID itemId) { }
        public bool IsCheckedChambers(MongoID weaponId) => true;
        public void CheckChamber(MongoID weaponId) { }
        public void UnCheckChamber(MongoID weaponId) { }
        public bool IsCheckedMagazines(MongoID magazineId) => true;
        public void CheckMagazines(MongoID magazineId, int value) { }
        public void UnCheckMagazines(MongoID magazineId) { }
        public int CheckMagazinesValue(MongoID magazineId) => 0;
        public void AddToCarriedQuestItems(string itemTemplateId) { }
        public bool Examined(MongoID templateId) => true;
    }

    internal sealed class TestController : InventoryController
    {
        // The game's real search logic. Its constructor needs a live Player, so its fields are set by hand.
        public readonly ActiveSearchController Search = NewSearchController();
        public Item Hands;
        public int RefuseFromOperation = -1;
        public int RefuseCount = 1;
        public int Operations;
        public readonly List<string> OperationTypes = new List<string>();

        public TestController(IInventoryProfileInfo profile) : base(profile, true) { }

        private static ActiveSearchController NewSearchController()
        {
            var search = (ActiveSearchController)FormatterServices.GetUninitializedObject(typeof(ActiveSearchController));
            search._knownItems = new Dictionary<Item, ItemAddress>();
            search._temporaryKnownItems = new HashSet<Item>();
            search._discoveredItems = new HashSet<Item>();
            typeof(PlayerSearchController).GetField("_searchedItems", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SetValue(search, new HashSet<SearchableItem>());
            return search;
        }

        public override ISearchController SearchController => Search;

        public readonly List<string> FailedOperations = new List<string>();

        /// <summary>The frame each operation really ran in.</summary>
        public readonly List<int> OperationFrames = new List<int>();

        // SinglePlayerInventoryController.ExecuteAsync: what a raid's local player does before every operation.
        // Immediate.ExecutePatch does the same to the game's controller.
        public override void Execute(EFT.InventoryLogic.Operations.AbstractOperation operation, Callback callback)
        {
            if (Immediate.Next)
            {
                Immediate.Next = false;
                RunNow(operation, callback);
                return;
            }
            ExecuteAfterYield(operation, callback);
        }

        private async void ExecuteAfterYield(EFT.InventoryLogic.Operations.AbstractOperation operation, Callback callback)
        {
            await Task.Yield();
            RunNow(operation, callback);
        }

        private void RunNow(EFT.InventoryLogic.Operations.AbstractOperation operation, Callback callback)
        {
            OperationFrames.Add(MainLoop.Frame);

            // ItemController.Execute, minus its failure log: that reads Time.frameCount, a Unity call this
            // process doesn't have, which would hide the operation's real error.
            if (!CanExecute(operation))
            {
                operation.Dispose();
                callback?.Fail("Can't execute " + operation, 1);
                return;
            }
            operation.Execute(result =>
            {
                if (!result.Succeed)
                {
                    FailedOperations.Add(operation + ": " + result.Error);
                    if (TestEnvironment.Verbose) Console.WriteLine("    operation failed: " + operation + ": " + result.Error);
                }
                callback?.Invoke(result);
            });
        }

        public override Item ItemInHands => Hands;

        public override Task<IResult> TryRunNetworkTransaction(OperationResult operationResult, Callback callback = null)
        {
            int number = Operations++;
            OperationTypes.Add(operationResult.Value?.GetType().Name ?? "failed");
            if (RefuseFromOperation >= 0 && number >= RefuseFromOperation && number < RefuseFromOperation + RefuseCount)
            {
                // A rejection with no state change, exactly what an authoritative refusal looks like.
                return Task.FromResult<IResult>(new FailedResult("refused by harness", 0));
            }
            return base.TryRunNetworkTransaction(operationResult, callback);
        }
    }

    internal sealed class TestEnvironment : ISwapEnvironment
    {
        public readonly List<string> Notifications = new List<string>();
        public bool ScreenOpen { get; set; } = true;
        public bool PlayerCanAct { get; set; } = true;
        public void BeforeSwap(CompoundItem incoming, CompoundItem outgoing, IEnumerable<Item> extras) { }
        public void Settled(CompoundItem first, CompoundItem second) { }
        public void Notify(string message) => Notifications.Add(message);
        public static bool Verbose = System.Environment.GetEnvironmentVariable("HARNESS_VERBOSE") == "1";
        public void Log(string message)
        {
            if (Verbose) System.Console.WriteLine("    log: " + message);
        }
        public readonly List<string> Errors = new List<string>();
        public void LogError(string message)
        {
            Errors.Add(message);
            System.Console.WriteLine("    ERROR: " + message);
        }
    }

    internal sealed class Scene
    {
        public InventoryEquipment Mine;
        public InventoryEquipment Theirs;
        public TestController Controller;
        public CorpseItemController Corpse;
        public TestEnvironment Env = new TestEnvironment();

        public Slot MySlot(EquipmentSlot s) => Mine.GetSlot(s);
        public Slot TheirSlot(EquipmentSlot s) => Theirs.GetSlot(s);

        /// <summary>What the game does at raid start, then what searching a body does.</summary>
        public void SearchEverything()
        {
            Controller.Search.UncoverContent(Mine);
            Controller.Search.UncoverContent(Theirs);
        }

        public Task Swap(EquipmentSlot slot, SwapKind kind, bool searchFirst = true)
        {
            if (searchFirst)
            {
                SearchEverything();
            }
            var incoming = (CompoundItem)TheirSlot(slot).ContainedItem;
            var gateway = new GameGateway(MySlot(slot), incoming, Controller, kind, Env);
            return new SwapCoordinator(gateway, kind).RunAsync();
        }

        /// <summary>A real drop: the gesture decides the sides, then the swap runs from what it decided.</summary>
        public Task Drop(Slot target, Item dragged, SwapKind kind)
        {
            SearchEverything();
            Assert.True(Gesture.Resolve(target, dragged, Mine, out var mySlot, out var incoming), "the drop should be the gesture");
            var gateway = new GameGateway(mySlot, incoming, Controller, kind, Env);
            return new SwapCoordinator(gateway, kind).RunAsync();
        }
    }

    /// <summary>
    /// The game's Weapon constructor crashes this process, so guns are this: a real item that says it's a
    /// weapon the only way the mod asks. Everything else about it is ordinary item behavior.
    /// </summary>
    internal sealed class StandInGun : CompoundItem, IWeapon
    {
        public StandInGun(string id, CompoundItemTemplate template) : base(id, template)
        {
        }

        float IWeapon.TotalWeight => 0;
        float IWeapon.RecoilForceBack => 0;
        float IWeapon.RecoilBase => 0;
        float IWeapon.SpeedFactor => 0;
        bool IWeapon.IsUnderbarrelWeapon => false;
        float IWeapon.RecoilDelta => 0;
        Weapon.MalfunctionState IWeapon.MalfState => null;
        Item IWeapon.Item => this;
        WeaponTemplate IWeapon.WeaponTemplate => null;
        Slot[] IWeapon.Chambers => new Slot[0];
        int IWeapon.GetCurrentMagazineCount() => 0;
    }

    internal static class Build
    {
        private static int _next = 1;

        // Ids must be valid MongoIDs: 24 hex characters.
        public static string Id()
        {
            return (_next++).ToString("x24");
        }

        public static Scene NewScene()
        {
            var scene = new Scene
            {
                Mine = Equipment(),
                Theirs = Equipment(),
            };
            var inventory = new Inventory(scene.Mine, null, null, null, null, null, new Dictionary<EAreaType, CompoundItem>(),
                new Dictionary<EBoundItem, MongoID>(), new Dictionary<MongoID, int>(), new List<MongoID>(), new List<Error>(), false);
            var profile = new ProfileStub { InventoryInfo = inventory };
            scene.Controller = new TestController(profile);
            scene.Corpse = new CorpseItemController(scene.Theirs, EPlayerSide.Bear, scene.Theirs.Id, "victim");
            return scene;
        }

        private static InventoryEquipment Equipment()
        {
            var names = Enum.GetNames(typeof(EquipmentSlot));
            var template = new InventoryEquipmentTemplate
            {
                CanPutIntoDuringTheRaid = true,
                Slots = names.Select(n => new Slot(n, new ItemFilter[0], false, EParentMergeType.InheritFromItem)).ToArray(),
            };
            return new InventoryEquipment(Id(), template);
        }

        public static Grid[] Grids(params (int w, int h)[] sizes)
        {
            int i = 0;
            return sizes.Select(s => new Grid("g" + (i++), s.w, s.h, false, false, new ItemFilter[0], null)).ToArray();
        }

        public static Backpack Pack(params (int w, int h)[] grids)
        {
            return new Backpack(Id(), new BackpackTemplate { CanPutIntoDuringTheRaid = true, StackObjectsCount = 1, StackMaxSize = 1, Width = 5, Height = 5, Grids = Grids(grids) });
        }

        // A pack as BackpackGunSlot's server half leaves it: its template gains one weapon slot.
        public static Backpack GunPack(ItemFilter[] filters, params (int w, int h)[] grids)
        {
            return new Backpack(Id(), new BackpackTemplate
            {
                CanPutIntoDuringTheRaid = true, StackObjectsCount = 1, StackMaxSize = 1, Width = 5, Height = 5, Grids = Grids(grids),
                Slots = new[] { new Slot("mod_backpack_weapon", filters ?? new ItemFilter[0], false, EParentMergeType.InheritFromItem) },
            });
        }

        public static Item Gun(int w = 4, int h = 2)
        {
            return new StandInGun(Id(), new CompoundItemTemplate { CanPutIntoDuringTheRaid = true, StackObjectsCount = 1, StackMaxSize = 1, Width = w, Height = h, Slots = new Slot[0], Grids = new Grid[0] });
        }

        public static Vest Rig(bool blocksArmor, params (int w, int h)[] grids)
        {
            return new Vest(Id(), new VestTemplate { CanPutIntoDuringTheRaid = true, StackObjectsCount = 1, StackMaxSize = 1, Width = 3, Height = 3, Grids = Grids(grids), BlocksArmorVest = blocksArmor });
        }

        public static Item Thing(int w, int h)
        {
            return new Item(Id(), new ItemTemplate { Width = w, Height = h, StackObjectsCount = 1, StackMaxSize = 1 });
        }

        public static Item Bindable(int w = 1, int h = 1)
        {
            // A compass: the game treats it as bindable, and its constructor needs no medical data.
            return new Compass(Id(), new CompassTemplate { Width = w, Height = h, StackObjectsCount = 1, StackMaxSize = 1 });
        }

        public static SimpleContainer Case(int w, int h, params (int w, int h)[] grids)
        {
            return new SimpleContainer(Id(), new SimpleContainerTemplate { CanPutIntoDuringTheRaid = true, Width = w, Height = h, Grids = Grids(grids), StackObjectsCount = 1, StackMaxSize = 1 });
        }

        public static void Put(CompoundItem container, int grid, Item item, int x, int y, bool rotated = false)
        {
            var location = new LocationInGrid(x, y, rotated ? ItemRotation.Vertical : ItemRotation.Horizontal);
            var result = container.Grids[grid].AddItemWithoutRestrictions(item, location);
            if (result.Failed)
            {
                throw new InvalidOperationException("Setup could not place an item: " + result.Error);
            }
        }

        public static void Equip(Slot slot, Item item)
        {
            var result = slot.AddWithoutRestrictions(item);
            if (result.Failed)
            {
                throw new InvalidOperationException("Setup could not equip an item: " + result.Error);
            }
        }
    }

    internal static class Assert
    {
        public static void True(bool value, string message)
        {
            if (!value) throw new Exception(message);
        }

        public static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new Exception($"{what}: expected '{expected}', got '{actual}'");
            }
        }

        // Every id exactly once anywhere under either container.
        public static void ItemsExactlyOnce(CompoundItem a, CompoundItem b, params Item[] items)
        {
            var seen = new List<Item>();
            seen.AddRange(a.GetAllItems());
            seen.AddRange(b.GetAllItems());
            foreach (var item in items)
            {
                Equal(1, seen.Count(i => i == item), "copies of item " + item.Id);
                Equal(1, seen.Count(i => i.Id == item.Id), "copies of id " + item.Id);
            }
        }

        // Asks the game's grids directly whether each location is consistent.
        public static void GridsConsistent(CompoundItem container)
        {
            foreach (var grid in container.Grids)
            {
                True(!grid.OverlappingItems.Any(), "overlap reported by the game's grid");
                True(!grid.OutOfBoundsItems.Any(), "out of bounds reported by the game's grid");
                foreach (var item in grid.Items)
                {
                    True(grid.LiesWithinGrid(item, grid.GetItemLocation(item)), "item outside its grid");
                    Equal(grid, item.Parent.Container as Grid, "item's parent grid");
                }
            }
        }
    }
}
