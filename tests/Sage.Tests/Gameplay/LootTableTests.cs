#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Loot tables and leveled lists (issue #379): a creature prefab names a `loot_table`, what it drops is
// rolled into its inventory from the world's seeded stream, conditions are the engine's own vocabulary,
// and a table that names nothing real or nests itself is a load error.
public class LootTableTests
{
    public LootTableTests() { _ = TestEnv.UserRoot; }

    private static readonly RecordId Gold = new("sage", "gold");
    private static readonly RecordId Ear = new("sage", "ear");
    private static readonly RecordId Gem = new("sage", "gem");
    private static readonly RecordId Ring = new("sage", "ring");
    private static readonly RecordId Sword = new("sage", "steel_sword");
    private static readonly RecordId Bone = new("sage", "bone");

    private const string Base = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "damageType": "cut" },
         { "type": "attribute", "id": "health", "start": 20, "min": 0, "max": 20 },
         { "type": "attribute", "id": "level",  "start": 1,  "min": 1, "max": 100 },
         { "type": "tag", "id": "state.dead" },
         { "type": "tag", "id": "undead" },
         { "type": "effect", "id": "wound", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
         { "type": "damage_type", "id": "cut", "effect": "wound" },

         { "type": "item", "id": "gold", "maxStack": 1000, "weight": 0 },
         { "type": "item", "id": "ear" },
         { "type": "item", "id": "gem", "maxStack": 100 },
         { "type": "item", "id": "ring", "maxStack": 100 },
         { "type": "item", "id": "steel_sword" },
         { "type": "item", "id": "bone", "maxStack": 100 },
        """;

    private const string Tables = """
         { "type": "loot_table", "id": "trinkets", "entries": [ { "item": "gem", "weight": 1 }, { "item": "ring", "weight": 1 } ] },
         { "type": "loot_table", "id": "goblin_loot", "rolls": 1, "maxRolls": 3,
           "entries": [ { "item": "gold", "weight": 4, "min": 2, "max": 9 },
                        { "table": "trinkets", "weight": 2 },
                        { "weight": 2 },
                        { "item": "ear", "always": true } ] },
         { "type": "loot_table", "id": "leveled", "rolls": 1,
           "entries": [ { "item": "gold", "requires": { "attribute": "level", "max": 4 } },
                        { "item": "steel_sword", "requires": { "attribute": "level", "min": 5 } },
                        { "item": "bone", "always": true, "requires": { "has_tag": "undead", "entity": "!other" } } ] },
         { "type": "prefab", "id": "goblin", "parts": { "attributes": {}, "loot": "goblin_loot" } },
         { "type": "prefab", "id": "packrat", "parts": { "loot": { "table": "goblin_loot", "when": "spawn" } } }
        ]
        """;

    private static HeadlessApp Game(string world = "loot", string? records = null) =>
        HeadlessApp.Gameplay().File("data/loot.json", records ?? Base + Tables).Boot(world);

    private static string Carried(World world, Entity entity) =>
        string.Join(",", world.Get<Inventory>(entity).Items.Select(s => $"{s.Item.Name}x{s.Count}"));

    // The Done line: a creature prefab names a table, and its death rolls it into the body's inventory —
    // the `always` ear every time, and the rest within the counts the table gives.
    [Fact]
    public void ACreaturePrefabDropsItsLootTableWhenItDies()
    {
        using var app = Game();
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;

        var goblin = world.Spawn(new RecordId("sage", "goblin"));
        Assert.True(world.Has<Inventory>(goblin));
        Assert.Empty(world.Get<Inventory>(goblin).Items);          // nothing until it dies
        Assert.False(world.Get<Loot>(goblin).Rolled);

        Combat.ApplyDamage(world, new DamageInfo(default, goblin, default, 50f, Vector3.Zero, Vector3.UnitZ));
        world.RunFixed(1f / 60f);

        Assert.True(world.Get<Loot>(goblin).Rolled);
        Assert.Equal(1, world.CountOf(goblin, Ear));
        int gold = world.CountOf(goblin, Gold);
        Assert.True(gold == 0 || gold is >= 2 and <= 27, $"gold {gold}: 2-9 a pick, at most 3 picks");
        int picked = world.Get<Inventory>(goblin).Items.Count;

        // A body that is told it died again drops nothing more.
        world.Events.Send(new Died(goblin, default));
        world.RunFixed(1f / 60f);
        Assert.Equal(picked, world.Get<Inventory>(goblin).Items.Count);
        Assert.Equal(1, world.CountOf(goblin, Ear));
    }

    // Drops come from the world's seed: the same world rolls the same loot on every run, a world of another
    // name rolls other loot, and a loaded game rolls what it would have rolled.
    [Fact]
    public void LootIsDeterministicFromTheWorldSeed()
    {
        string Roll(HeadlessApp app, int n) => string.Join("|", Enumerable.Range(0, n)
            .Select(_ => Carried(app.World, app.World.Spawn(new RecordId("sage", "packrat")))));

        using var first = Game("crypt");
        using var second = Game("crypt");
        using var other = Game("barrow");
        string a = Roll(first, 30);
        Assert.Equal(a, Roll(second, 30));
        Assert.NotEqual(a, Roll(other, 30));
        Assert.Contains("gem", a);                        // the nested table was rolled
        Assert.Contains("gold", a);
        Assert.All(a.Split('|'), carried => Assert.Contains("earx1", carried));

        first.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        Assert.True(first.Engine.Saves.Save("loot"));
        string after = Roll(first, 20);
        Assert.True(first.Engine.Saves.Load("loot"));
        Assert.Equal(after, Roll(first, 20));
    }

    // A leveled list is the engine's conditions on its entries: the player's level picks gold or a sword,
    // and the creature's own tags (`!other`) add a bone.
    [Fact]
    public void LeveledEntriesAskTheEnginesConditions()
    {
        using var app = Game();
        var world = app.World;
        var player = world.Create(Transform.At(Vector3.Zero), "player");
        player.AddTag<PlayerControlled>();
        world.AddAttributes(player);

        var low = world.Create(Transform.At(Vector3.One), "low");
        world.AddInventory(low);
        world.AddAttributes(low);
        Assert.Equal(1, LootTables.Roll(world, low, new RecordId("sage", "leveled")));
        Assert.Equal("goldx1", Carried(world, low));

        var level = world.Resources.Get<GameplayRegistries>().Attribute(new RecordId("sage", "level"));
        world.Get<Attributes>(player).Values.SetBase(level, 7);
        var skeleton = world.Create(Transform.At(Vector3.One), "skeleton");
        world.AddInventory(skeleton);
        world.AddAttributes(skeleton);
        world.AddTag(skeleton, new RecordId("sage", "undead"));
        Assert.Equal(2, LootTables.Roll(world, skeleton, new RecordId("sage", "leveled")));
        Assert.Equal(1, world.CountOf(skeleton, Sword));
        Assert.Equal(1, world.CountOf(skeleton, Bone));
        Assert.Equal(0, world.CountOf(skeleton, Gold));
    }

    // Content mistakes are load errors: an entry naming an item or a table nobody wrote, and tables that
    // nest each other in a circle (each table in it says so).
    [Fact]
    public void ALootTableWithAMissingReferenceOrACycleIsALoadError()
    {
        using (var missing = Game(records: Base + """
            { "type": "loot_table", "id": "broken", "entries": [ { "item": "no_such_item" }, { "table": "no_such_table" } ] }]
            """))
        {
            Assert.Equal(2, missing.Records.ErrorCount);
            Assert.Contains(missing.Records.LoadErrors, e => e.Contains("no_such_item"));
            Assert.Contains(missing.Records.LoadErrors, e => e.Contains("no_such_table"));
        }

        using (var cycle = Game(records: Base + """
            { "type": "loot_table", "id": "a", "entries": [ { "table": "b" } ] },
            { "type": "loot_table", "id": "b", "entries": [ { "item": "gold" }, { "table": "c" } ] },
            { "type": "loot_table", "id": "c", "entries": [ { "table": "a" } ] },
            { "type": "loot_table", "id": "fine", "entries": [ { "table": "a" } ] }]
            """))
        {
            Assert.Equal(3, cycle.Records.ErrorCount);
            Assert.Contains(cycle.Records.LoadErrors, e => e.Contains("circle") && e.Contains("sage:a -> sage:b -> sage:c -> sage:a"));
        }

        using var both = Game(records: Base + """
            { "type": "loot_table", "id": "greedy", "entries": [ { "item": "gold", "table": "greedy", "min": 3, "max": 1 } ] }]
            """);
        Assert.Equal(3, both.Records.ErrorCount);   // both an item and a table; max below min; itself
    }
}
