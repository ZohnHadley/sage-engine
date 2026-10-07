#nullable enable
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Containers and bodies (issue #378): the base's `container` part (locked, key, respawn, owner) opened
// by Use into the kit's loot screen, a death that makes a body a container, and `Stolen` for what is
// taken from somebody else's.
public class ContainerTests
{
    public ContainerTests() { _ = TestEnv.UserRoot; }

    private const string Content = """
    [
      { "type": "item", "id": "cellar_key", "label": "cellar key", "weight": 0.1 },
      { "type": "faction", "id": "townsfolk", "label": "townsfolk" },
      { "type": "prefab", "id": "chest", "name": "chest",
        "parts": { "inventory": { "items": [ { "item": "bread", "count": 3 }, { "item": "coin", "count": 5 } ] },
                   "container": { "owner": "hermit", "respawn": 24 } } },
      { "type": "prefab", "id": "strongbox", "name": "strongbox",
        "parts": { "inventory": { "items": [ { "item": "coin", "count": 50 } ] },
                   "container": { "locked": true, "key": "cellar_key", "faction": "townsfolk" } } },
      { "type": "prefab", "id": "crate", "name": "crate", "parts": { "container": {} } }
    ]
    """;

    private static RecordId Id(string name) => new("sage", name);

    private static HeadlessApp Boot()
    {
        var app = HeadlessApp.Gameplay().WithEngineContent().With(new UiModule(), new RpgKitModule())
            .File("data/rpg_screens.json", RpgScreenTests.Records).File("data/containers.json", Content).Boot("rpg");
        Assert.Equal(0, app.Records.ErrorCount);
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        return app;
    }

    private static Entity Hero(World world)
    {
        var hero = world.Create(Transform.At(Vector3.Zero), "hero");
        world.AddInventory(hero, 50f);
        hero.AddTag<PlayerControlled>();
        return hero;
    }

    private static UiScreenStack Stack(World world)
    {
        var stack = world.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        stack.OpenSeconds = stack.CloseSeconds = 0f;
        return stack;
    }

    private static UiLayer Top(UiScreenStack stack) => Assert.Single(stack.Layers, l => l.Modal && !l.IsClosing);

    private static void Use(World world, Entity user, Entity target)
    {
        world.Events.Send(new Used(user, target));
        CameraRigTests.Step(world);
    }

    // Done (#378): a chest from content opens by Use into the loot screen about it, with no `use_screen`
    // part; taking from it is theft from its owner, `Stolen` says what and from whom, and the owner
    // himself takes freely.
    [Xunit.Fact]
    public void AnOwnedChestOpensByUse_AndTakingFromItIsStolen()
    {
        using var app = Boot();
        var world = app.World;
        var stack = Stack(world);
        var hero = Hero(world);
        var stolen = new EventProbe<Stolen>(world);
        var chest = world.Spawn(Id("chest"), new Vector3(0, 0, -1));
        Assert.True(chest.Tags.Has<Interactable>());
        Assert.Equal("hermit", world.Get<ItemContainer>(chest).Owner);

        Use(world, hero, chest);
        var loot = Top(stack);
        Assert.Equal(RpgKitModule.LootScreen, loot.Screen!.Id);
        Assert.Equal(chest, loot.Screen.Context.Other);
        var view = Assert.IsType<LootView>(loot.Screen.ViewModel);
        Assert.Equal(2, view.TakeAll(world, chest, hero));
        Assert.Equal(3, world.CountOf(hero, Id("bread")));

        CameraRigTests.Step(world);
        Assert.Equal(2, stolen.All.Count);
        var bread = Assert.Single(stolen.All, s => s.Item == Id("bread"));
        Assert.Equal((hero, chest, 3, "hermit"), (bread.Thief, bread.From, bread.Count, bread.Owner));

        // The hermit takes from his own chest: no theft.
        var hermit = world.Create(Transform.At(Vector3.Zero), "hermit");
        world.AddInventory(hermit);
        Assert.False(Containers.IsTheft(world, hermit, chest));
        Assert.True(Containers.IsTheft(world, hero, chest));
    }

    // A locked container stays shut, and says so, to Use without its key; with the key in the pack Use
    // unlocks it and opens it. A faction's member takes from its strongbox freely.
    [Xunit.Fact]
    public void ALockedContainerOpensOnlyForTheKeysCarrier()
    {
        using var app = Boot();
        var world = app.World;
        var stack = Stack(world);
        var hero = Hero(world);
        var box = world.Spawn(Id("strongbox"), new Vector3(0, 0, -1));
        Assert.True(Containers.IsLocked(world, box));

        Use(world, hero, box);
        Assert.Empty(stack.Layers);
        Assert.True(Containers.IsLocked(world, box));

        Assert.True(world.Give(hero, Id("cellar_key")));
        Use(world, hero, box);
        Assert.False(Containers.IsLocked(world, box));
        Assert.Equal(box, Top(stack).Screen!.Context.Other);

        Assert.True(Containers.IsTheft(world, hero, box));
        world.Add(hero, new Faction { Id = Id("townsfolk") });
        Assert.False(Containers.IsTheft(world, hero, box));
    }

    // Done (#378): a creature that dies with an inventory is a container, unowned, and Use on its body
    // opens the loot screen about it; taking from a body is not theft. The player's own body is not one.
    [Xunit.Fact]
    public void ABodyIsAContainer_LootedByUseWithoutTheft()
    {
        using var app = Boot();
        var world = app.World;
        var stack = Stack(world);
        var hero = Hero(world);
        var stolen = new EventProbe<Stolen>(world);
        var bandit = world.Create(Transform.At(new Vector3(0, 0, -1)), "bandit");
        world.AddInventory(bandit);
        Assert.True(world.Give(bandit, Id("sword")));
        Assert.False(world.Has<ItemContainer>(bandit));

        world.Events.Send(new Died(bandit, hero));
        world.Events.Send(new Died(hero, bandit));
        CameraRigTests.Step(world);
        Assert.True(world.Has<ItemContainer>(bandit));
        Assert.True(bandit.Tags.Has<Interactable>());
        Assert.False(world.Get<ItemContainer>(bandit).IsOwned);
        Assert.False(world.Has<ItemContainer>(hero));

        Use(world, hero, bandit);
        var loot = Top(stack);
        Assert.Equal(RpgKitModule.LootScreen, loot.Screen!.Id);
        Assert.Equal(1, Assert.IsType<LootView>(loot.Screen.ViewModel).TakeAll(world, bandit, hero));
        Assert.Equal(1, world.CountOf(hero, Id("sword")));
        CameraRigTests.Step(world);
        Assert.Empty(stolen.All);
    }

    // Respawn: game hours after it was first taken from, the next Use finds it refilled with what it held
    // when first opened — and the clock it waits on survives a save.
    [Xunit.Fact]
    public void AContainerRefillsRespawnHoursAfterItWasTakenFrom_AcrossASave()
    {
        using var app = Boot();
        var world = app.World;
        var stack = Stack(world);
        var hero = Hero(world);
        var clock = WorldClock.Of(world);
        clock.Scale = 0;
        var chest = world.Spawn(Id("chest"), new Vector3(0, 0, -1));
        var id = world.Get<Persistent>(chest).Id;

        Use(world, hero, chest);
        Assert.IsType<LootView>(Top(stack).Screen!.ViewModel).TakeAll(world, chest, hero);
        Assert.Equal(0, world.CountOf(chest, Id("bread")));
        stack.CloseAll();
        Assert.Equal(clock.Elapsed + 24, world.Get<ItemContainer>(chest).RestockAt, 3);

        Assert.True(app.Engine.Saves.Save("chest"));
        Assert.True(app.Engine.Saves.Load("chest"));
        chest = world.Resolve(id);
        Assert.False(chest.IsNull);
        hero = world.FindByName("hero");
        if (hero.IsNull) hero = Hero(world);
        clock = WorldClock.Of(world);
        clock.Scale = 0;

        Assert.Equal(12.0, clock.Hour);
        clock.Hour = 23;    // 11 hours on: still empty
        Use(world, hero, chest);
        Assert.Equal(0, world.CountOf(chest, Id("bread")));
        stack.CloseAll();

        clock.Day += 1;     // past the day: full again
        Use(world, hero, chest);
        Assert.Equal(3, world.CountOf(chest, Id("bread")));
        Assert.Equal(5, world.CountOf(chest, Id("coin")));
        Assert.Equal(0.0, world.Get<ItemContainer>(chest).RestockAt);
    }

    // The kit's grid moves (a drag out of the loot screen) say what was taken the way take-all does; a part
    // with no inventory before it gets an empty one.
    [Xunit.Fact]
    public void AStackDraggedOutOfAChestIsStolenToo()
    {
        using var app = Boot();
        var world = app.World;
        var hero = Hero(world);
        var stolen = new EventProbe<Stolen>(world);
        var crate = world.Spawn(Id("crate"), new Vector3(0, 0, -2));
        Assert.True(world.Has<Inventory>(crate));

        var chest = world.Spawn(Id("chest"), new Vector3(0, 0, -1));
        var from = new ItemGrid();
        from.Refresh(world, chest);
        var to = new ItemGrid();
        to.Refresh(world, hero);
        var coin = from.Items.First(i => i.Item == Id("coin"));
        Assert.True(ItemGrid.Transfer(world, coin, to, -1, -1, out _));
        CameraRigTests.Step(world);
        var theft = Assert.Single(stolen.All);
        Assert.Equal((Id("coin"), 5), (theft.Item, theft.Count));
    }
}
