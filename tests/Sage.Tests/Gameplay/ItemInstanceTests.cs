#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Item instances (issue #383): a unit of an item with its own name, enchantments, condition and charges,
// held apart from the plain stack of its kind, equipped with its enchantments, split and dropped with the
// instance kept, and saved.
public class ItemInstanceTests
{
    public ItemInstanceTests() { _ = TestEnv.UserRoot; }

    private static readonly RecordId Sword = new("sage", "sword");
    private static readonly RecordId Arrow = new("sage", "arrow");
    private static readonly RecordId Anvil = new("sage", "anvil");
    private static readonly RecordId Armor = new("sage", "armor");
    private static readonly RecordId Warded = new("sage", "warded");
    private static readonly RecordId Keen = new("sage", "keen");

    private const string Records = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "invulnerable": "state.invulnerable", "damageType": "physical" },
         { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "attribute", "id": "armor",  "start": 0,   "min": 0, "max": 95 },
         { "type": "attribute", "id": "edge",   "start": 0,   "min": 0, "max": 100 },
         { "type": "tag", "id": "state.dead" },
         { "type": "tag", "id": "state.invulnerable" },
         { "type": "effect", "id": "damage", "duration": "Instant", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
         { "type": "effect", "id": "warded", "duration": "Infinite", "modifiers": [ { "attribute": "armor", "op": "Add", "value": 25 } ] },
         { "type": "effect", "id": "keen",   "duration": "Infinite", "modifiers": [ { "attribute": "edge", "op": "Add", "value": 10 } ] },
         { "type": "damage_type", "id": "physical", "resist": "armor", "effect": "damage" },
         { "type": "attack", "id": "fists",       "damage": 6,  "damageType": "physical", "reach": 1.8 },
         { "type": "attack", "id": "sword_swing", "damage": 24, "damageType": "physical", "reach": 2.4 },
         { "type": "item", "id": "sword",  "label": "iron sword", "slot": "MainHand", "attack": "sword_swing", "effects": ["keen"], "weight": 4 },
         { "type": "item", "id": "arrow",  "label": "arrow", "weight": 0.1, "maxStack": 20 },
         { "type": "item", "id": "anvil",  "label": "anvil", "weight": 90 },
         { "type": "prefab", "id": "champion", "name": "champion",
           "parts": { "attributes": {},
                      "inventory": { "capacity": 50, "items": [
                          { "item": "sword", "instance": { "name": "Dawnblade", "effects": ["warded"] } },
                          { "item": "sword", "instance": { "condition": 1 } } ] } } },
         { "type": "prefab", "id": "relic", "name": "relic", "parts": { "pickup": { "item": "sword", "instance": { "name": "Oathkeeper", "charges": 2 } } } }]
        """;

    private static HeadlessApp NewApp(string? savesRoot = null)
    {
        var app = HeadlessApp.Gameplay().WithHands().File("data/instances.json", Records).Build();
        if (savesRoot != null) app.Engine.Saves.Root = savesRoot;
        return app;
    }

    private static World NewWorld(HeadlessApp app)
    {
        var world = app.Engine.CreateWorld("main");
        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        world.Add(ground, Collider.Box(new Vector3(100, 1, 100)));
        return world;
    }

    private static Entity Carrier(World world, string name, float capacity = 50f)
    {
        var entity = world.Create(Transform.At(Vector3.Zero), name);
        world.AddCharacter(entity, world.Resources.Get<IPhysicsWorld>().Layers.Player);
        world.Add(entity, Melee.With(new RecordId("sage", "fists")));
        world.AddInventory(entity, capacity);
        world.AddAttributes(entity);
        world.Get<PawnIntent>(entity).Yaw = SageMath.YawTo(Vector3.Zero, new Vector3(0, 0, -2));
        return entity;
    }

    private static void Tick(World world, int ticks = 2)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    private static ItemInstance Named(string name, params RecordId[] enchantments)
    {
        var instance = new ItemInstance { Name = name };
        foreach (var e in enchantments) instance.Effects.Add(new RecordRef<EffectRecord>(e));
        return instance;
    }

    private static System.Collections.Generic.List<ItemStack> Stacks(World world, Entity e) => world.Get<Inventory>(e).Items;

    private static int IndexOf(World world, Entity e, string name) =>
        Stacks(world, e).FindIndex(s => s.Instance?.Name == name);

    [Fact]
    public void TwoDistinctInstancesOfOneRecordAreHeldApartAndPlainOnesStayPlain()
    {
        using var app = NewApp();
        var world = NewWorld(app);
        var hero = Carrier(world, "hero");

        Assert.True(world.Give(hero, Sword));
        Assert.True(world.Give(hero, Sword, Named("Dawnblade", Warded)));
        Assert.True(world.Give(hero, Sword, Named("Rustfang")));
        // An instance that differs in nothing is a plain item: no third kind of sword.
        Assert.True(world.Give(hero, Sword, new ItemInstance()));

        var stacks = Stacks(world, hero);
        Assert.Equal(4, stacks.Count);
        Assert.Equal(2, stacks.Count(s => s.Instance == null));
        Assert.Equal(4, world.CountOf(hero, Sword));
        var dawn = stacks.Single(s => s.Instance?.Name == "Dawnblade").Instance!;
        var rust = stacks.Single(s => s.Instance?.Name == "Rustfang").Instance!;
        Assert.NotEqual(dawn.Id, rust.Id);
        Assert.True(dawn.Id > 0 && rust.Id > 0);

        // Plain arrows stack as they always did; identical instances stack together, apart from the plain.
        var poisoned = new ItemInstance { Name = "poisoned arrow", Charges = 1 };
        Assert.True(world.Give(hero, Arrow, 5));
        Assert.True(world.Give(hero, Arrow, poisoned, 3));
        Assert.True(world.Give(hero, Arrow, poisoned, 4));
        Assert.True(world.Give(hero, Arrow, 2));
        Assert.Equal(7, stacks.Single(s => s.Item == Arrow && s.Instance == null).Count);
        Assert.Equal(7, stacks.Single(s => s.Item == Arrow && s.Instance != null).Count);
        Assert.NotSame(poisoned, stacks.Single(s => s.Item == Arrow && s.Instance != null).Instance);   // a copy went in
    }

    [Fact]
    public void EquippingAnInstanceAppliesItsEnchantmentsWithTheItemsOwnEffects()
    {
        using var app = NewApp();
        var world = NewWorld(app);
        var hero = Carrier(world, "hero");
        var edge = new RecordId("sage", "edge");
        world.Give(hero, Sword);
        world.Give(hero, Sword, Named("Dawnblade", Warded));

        Assert.True(world.EquipAt(hero, IndexOf(world, hero, "Dawnblade")));
        Tick(world);
        Assert.Equal(25f, world.Attribute(hero, Armor));     // the enchantment
        Assert.Equal(10f, world.Attribute(hero, edge));      // the record's own effect
        Assert.Equal("Dawnblade", world.WornInstance(hero, Hands.Main)!.Name);
        Assert.Equal(new RecordId("sage", "sword_swing"), world.Get<Melee>(hero).Attack);

        // The plain one in its place: the enchantment comes off, the record's effect stays on.
        Assert.True(world.Equip(hero, Sword));
        Assert.Null(world.WornInstance(hero, Hands.Main));
        Tick(world);
        Assert.Equal(0f, world.Attribute(hero, Armor));
        Assert.Equal(10f, world.Attribute(hero, edge));

        // Back to the named one, then it leaves the bag: off it comes, enchantment and all.
        Assert.True(world.EquipAt(hero, IndexOf(world, hero, "Dawnblade")));
        Assert.True(world.TakeAt(hero, IndexOf(world, hero, "Dawnblade"), 1, out var taken));
        Assert.Equal("Dawnblade", taken.Instance!.Name);
        Assert.True(world.Get<Equipment>(hero).In(Hands.Main).IsEmpty);
        Tick(world);
        Assert.Equal(0f, world.Attribute(hero, Armor));
        Assert.Equal(1, world.CountOf(hero, Sword));
    }

    [Fact]
    public void TakingByRecordTakesPlainUnitsFirstAndKeepsTheWornInstanceOn()
    {
        using var app = NewApp();
        var world = NewWorld(app);
        var hero = Carrier(world, "hero");
        world.Give(hero, Sword, Named("Dawnblade", Warded));
        world.Give(hero, Sword);
        Assert.True(world.EquipAt(hero, IndexOf(world, hero, "Dawnblade")));

        Assert.True(world.Take(hero, Sword));
        var left = Assert.Single(Stacks(world, hero));
        Assert.Equal("Dawnblade", left.Instance!.Name);
        Assert.Equal("Dawnblade", world.WornInstance(hero, Hands.Main)!.Name);
        Tick(world);
        Assert.Equal(25f, world.Attribute(hero, Armor));
    }

    [Fact]
    public void AStackSplitsOnDropAndThePickupGivesBackTheSameInstance()
    {
        using var app = NewApp();
        var world = NewWorld(app);
        var player = Carrier(world, "player");
        world.Give(player, Arrow, new ItemInstance { Name = "poisoned arrow", Charges = 1 }, 10);

        var dropped = world.DropAt(player, 0, 3);
        Assert.False(dropped.IsNull);
        Assert.Equal(7, world.CountOf(player, Arrow));
        var pickup = world.Get<Pickup>(dropped);
        Assert.Equal(3, pickup.Count);
        Assert.Equal("poisoned arrow", pickup.Instance!.Name);
        Assert.NotSame(Stacks(world, player)[0].Instance, pickup.Instance);

        var use = app.Engine.Actions.Get("Use");
        for (int i = 0; i < 2; i++) world.RunFixed(1f / 60f);
        world.Get<PawnIntent>(player).Pressed = new ActionMask().With(use);
        world.RunFixed(1f / 60f);

        var back = Assert.Single(Stacks(world, player));   // merged back into its own kind
        Assert.Equal(10, back.Count);
        Assert.Equal("poisoned arrow", back.Instance!.Name);
    }

    [Fact]
    public void DroppingByRecordKeepsAnInstanceInAPickupOfItsOwn()
    {
        using var app = NewApp();
        var world = NewWorld(app);
        var hero = Carrier(world, "hero");
        world.Give(hero, Sword);
        world.Give(hero, Sword, Named("Rustfang"));

        Assert.False(world.Drop(hero, Sword, 2).IsNull);
        Assert.Equal(0, world.CountOf(hero, Sword));
        var piles = world.Query<Pickup>().Entities.ToEntityList().Select(e => world.Get<Pickup>(e)).ToList();
        Assert.Equal(2, piles.Count);
        Assert.Contains(piles, p => p.Instance == null && p.Count == 1);
        Assert.Contains(piles, p => p.Instance?.Name == "Rustfang" && p.Count == 1);
    }

    [Fact]
    public void SplittingAndMovingKeepTheInstance()
    {
        using var app = NewApp();
        var world = NewWorld(app);
        var hero = Carrier(world, "hero", capacity: 200f);
        var chest = Carrier(world, "chest", capacity: 10f);
        world.Give(hero, Arrow, new ItemInstance { Name = "fire arrow" }, 10);
        world.Give(hero, Anvil);

        Assert.False(world.Split(hero, 0, 10));     // nothing would be left behind
        Assert.True(world.Split(hero, 0, 4));
        var stacks = Stacks(world, hero);
        Assert.Equal(6, stacks[0].Count);
        Assert.Equal(4, stacks[1].Count);
        Assert.Equal("fire arrow", stacks[1].Instance!.Name);
        Assert.NotEqual(stacks[0].Instance!.Id, stacks[1].Instance!.Id);
        Assert.Equal(10, world.CountOf(hero, Arrow));

        Assert.True(world.MoveTo(hero, 1, chest, 4));
        var moved = Assert.Single(Stacks(world, chest));
        Assert.Equal(4, moved.Count);
        Assert.Equal("fire arrow", moved.Instance!.Name);
        Assert.Equal(6, world.CountOf(hero, Arrow));

        // Too heavy for the chest: nothing moves.
        int anvil = Stacks(world, hero).FindIndex(s => s.Item == Anvil);
        Assert.False(world.MoveTo(hero, anvil, chest, 1));
        Assert.Equal(1, world.CountOf(hero, Anvil));
        Assert.Equal(0, world.CountOf(chest, Anvil));
    }

    [Fact]
    public void APrefabCanAuthorUniqueItems()
    {
        using var app = NewApp();
        var world = NewWorld(app);
        var champion = world.Spawn(new RecordId("sage", "champion"), Vector3.Zero);
        world.FlushCommands();
        var stacks = Stacks(world, champion);
        Assert.Equal(2, stacks.Count);
        Assert.Equal("Dawnblade", stacks[0].Instance!.Name);
        Assert.Equal(Warded, stacks[0].Instance!.Effects.Single().Id);
        Assert.Null(stacks[1].Instance);   // "condition": 1 is as made: a plain sword

        var relic = world.Spawn(new RecordId("sage", "relic"), new Vector3(3, 0, 3));
        world.FlushCommands();
        Assert.Equal("Oathkeeper", world.Get<Pickup>(relic).Instance!.Name);
        Assert.Equal(2, world.Get<Pickup>(relic).Instance!.Charges);
    }

    // The issue's acceptance: tests/Sage.Tests/Content/Saves/instances was written (format 4) by
    // WriteTheGoldenInstanceSaveWhenAsked — a hero carrying a plain sword and two distinct named ones, one of
    // them equipped with its enchantment on, a plain and a poisoned stack of arrows, and a named sword lying
    // on the ground. It must load with all of it, and the worn instance must still come off with its effect.
    [Fact]
    public void AGoldenSaveWithTwoDistinctInstancesLoads()
    {
        string golden = Path.Combine(TestEnv.FolderAbove("Sage.sln"), "tests", "Sage.Tests", "Content", "Saves", "instances");
        using var app = NewApp(golden);
        var world = NewWorld(app);
        using var log = new CaptureSink();
        int thread = Environment.CurrentManagedThreadId;

        Assert.True(app.Engine.Saves.Load("golden"));
        Assert.DoesNotContain(log.Entries, e => e.ThreadId == thread && e.Level >= LogLevel.Warn && e.Message.Contains("golden/world_main"));

        var hero = world.Resolve(PersistentId.FromName("hero"));
        Assert.False(hero.IsNull);
        var stacks = Stacks(world, hero);
        Assert.Equal(3, world.CountOf(hero, Sword));
        Assert.Single(stacks, s => s.Item == Sword && s.Instance == null);
        var dawn = stacks.Single(s => s.Instance?.Name == "Dawnblade").Instance!;
        var rust = stacks.Single(s => s.Instance?.Name == "Rustfang").Instance!;
        Assert.Equal(Warded, dawn.Effects.Single().Id);
        Assert.Equal(0.4f, rust.Condition);
        Assert.Equal(3, rust.Charges);
        Assert.NotEqual(dawn.Id, rust.Id);
        Assert.Equal(5, stacks.Single(s => s.Item == Arrow && s.Instance == null).Count);
        Assert.Equal(6, stacks.Single(s => s.Item == Arrow && s.Instance != null).Count);

        Assert.Same(dawn, world.WornInstance(hero, Hands.Main));
        Tick(world);
        Assert.Equal(25f, world.Attribute(hero, Armor));
        world.Unequip(hero, Hands.Main);
        Tick(world);
        Assert.Equal(0f, world.Attribute(hero, Armor));

        var relic = Assert.Single(world.Query<Pickup>().Entities.ToEntityList());
        Assert.Equal("Oathkeeper", world.Get<Pickup>(relic).Instance!.Name);

        // A plain stack is still saved as ids and counts.
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(golden, "golden", "world_main.json")))!["entities"]!.AsArray()
            .Single(e => (string?)e!["name"] == "hero")!["components"]!["sage:inventory"]!["data"]!["Items"]!.AsArray();
        Assert.Contains(saved, s => s!["Instance"] == null && (string?)s["Item"] == "sage:sword");
    }

    // Saved and loaded by this build: the worn instance is still the one worn.
    [Fact]
    public void AnInstanceRoundTripsThroughASaveInThisBuild()
    {
        string root = TestEnv.NewTempDir();
        using var app = NewApp(root);
        var world = NewWorld(app);
        var hero = Carrier(world, "hero");
        world.Add(hero, new Persistent { Id = PersistentId.FromName("hero") });
        world.Give(hero, Sword, Named("Dawnblade", Warded));
        world.Give(hero, Sword, Named("Rustfang"));
        Assert.True(world.EquipAt(hero, IndexOf(world, hero, "Rustfang")));
        world.FlushCommands();
        Assert.True(app.Engine.Saves.Save("trip"));
        Assert.True(app.Engine.Saves.Load("trip"));

        hero = world.Resolve(PersistentId.FromName("hero"));
        Assert.Equal("Rustfang", world.WornInstance(hero, Hands.Main)!.Name);
        Assert.Equal(2, Stacks(world, hero).Count(s => s.Instance != null));
    }

    // Writes the golden instance save, only when asked: run with SAGE_WRITE_GOLDEN_INSTANCE_SAVE=<folder> and
    // commit what it writes there as tests/Sage.Tests/Content/Saves/instances.
    [Fact]
    public void WriteTheGoldenInstanceSaveWhenAsked()
    {
        string? target = Environment.GetEnvironmentVariable("SAGE_WRITE_GOLDEN_INSTANCE_SAVE");
        if (string.IsNullOrEmpty(target)) return;
        using var app = NewApp(target);
        var world = NewWorld(app);
        app.CVars.Execute("save_autosave 0");

        var hero = Carrier(world, "hero");
        world.Add(hero, new Persistent { Id = PersistentId.FromName("hero") });
        world.Give(hero, Sword);
        world.Give(hero, Sword, Named("Dawnblade", Warded));
        world.Give(hero, Sword, new ItemInstance { Name = "Rustfang", Condition = 0.4f, Charges = 3 });
        world.Give(hero, Arrow, 5);
        world.Give(hero, Arrow, new ItemInstance { Name = "poisoned arrow", Charges = 1 }, 6);
        Assert.True(world.EquipAt(hero, IndexOf(world, hero, "Dawnblade")));
        world.SpawnPickup(new ItemStack { Item = Sword, Count = 1, Instance = new ItemInstance { Name = "Oathkeeper", Charges = 2 } },
                          new Vector3(4, 0, 6));
        world.FlushCommands();
        for (int i = 0; i < 2; i++) world.RunFixed(1f / 60f);

        Assert.True(app.Engine.Saves.Save("golden"));
    }
}
