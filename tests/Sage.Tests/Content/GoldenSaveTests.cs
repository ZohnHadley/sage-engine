#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using Friflo.Engine.ECS;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// When the format 1 golden save was written (main at 92c690a) this was
// `public struct GoldenLantern : IComponent { public float Brightness; public int Charges; }`, saved
// under its C# type name. Since then both the type and a field have been renamed: the former name says
// what the save called it, and the upgrader says what became of the field (issue #20).
[Component("test:lamp", Version = 2, FormerNames = new[] { "GoldenLantern" })]
public struct SaveLamp : IComponent
{
    public float Lumens;
    public int Charges;

    [Upgrade(1)] private static void From1(ref JsonObject o) => o.RenameField("Brightness", "Lumens");
}

// The same rename with no upgrader: what a game gets when it forgets one.
[Component("test:bare_lamp", Version = 2)]
public struct BareLamp : IComponent
{
    public float Lumens;
    public int Charges;
}

// Three versions, two steps: upgraders run oldest first, each on the last one's output.
[Component("test:chain", Version = 3)]
public struct ChainedUpgrades : IComponent
{
    public int Third;

    [Upgrade(2)] private static void From2(ref JsonObject o) => o.RenameField("Second", "Third");
    [Upgrade(1)] private static void From1(ref JsonObject o) => o.RenameField("First", "Second");
}

// Golden saves (docs/REDESIGN.md §4.5, issue #20): a save written by each released format must still
// load. `Saves/format1` was written by main before components had stable ids — keyed by C# type name,
// with no versions — and `Saves/format2` by this format. Both are committed files, not written by the
// test, because the point is that a *file from the past* loads: a save written and read by the same
// build proves nothing about that.
//
// To add one for a new format: run this class with SAGE_WRITE_GOLDEN_SAVE=<folder> set, and commit
// what `WriteTheGoldenSaveWhenAsked` writes there as Saves/format<N>.
public class GoldenSaveTests
{
    public GoldenSaveTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
    [
      { "type": "attribute", "id": "health", "max": 100, "start": 100 },
      { "type": "attribute", "id": "armour", "max": 95 },
      { "type": "damage_type", "id": "physical", "resist": "armour" },
      { "type": "attack", "id": "fists", "damage": 5, "reach": 1.5 },
      { "type": "item", "id": "sword", "label": "a sword", "weight": 3, "slot": "MainHand", "attack": "fists" },
      { "type": "item", "id": "bread", "label": "bread", "weight": 1 },
      { "type": "tag", "id": "state.cursed" },
      { "type": "effect", "id": "hurt", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
      { "type": "effect", "id": "curse", "duration": "Infinite", "grantTags": ["state.cursed"],
        "modifiers": [ { "attribute": "armour", "op": "Add", "value": -10 } ] },
      { "type": "ability", "id": "heal", "targeting": "Self", "effects": ["hurt"], "magnitude": 1 },
      { "type": "prefab", "id": "hero", "name": "hero",
        "tags": ["player_controlled"],
        "parts": { "character": { "layer": "player" }, "attributes": {},
                   "melee": { "attack": "fists" }, "inventory": { "capacity": 40 } } },
      { "type": "prefab", "id": "goblin", "name": "goblin",
        "components": { "ai_state": { "schedule": "sage:idle" } },
        "parts": { "character": { "layer": "enemy" }, "attributes": {} } }
    ]
    """;

    private static RecordId Id(string name) => new("sage", name);

    private static string Golden(int format) =>
        Path.Combine(TestEnv.FolderAbove("Sage.sln"), "tests", "Sage.Tests", "Content", "Saves", $"format{format}");

    private static HeadlessApp NewApp(string savesRoot)
    {
        var app = HeadlessApp.Gameplay().File("data/golden.json", Records).Build();
        app.Engine.Saves.Root = savesRoot;
        return app;
    }

    // A copy, so a test that edits a golden file edits its own.
    private static string CopyOf(int format)
    {
        string root = TestEnv.NewTempDir();
        foreach (string file in Directory.EnumerateFiles(Golden(format), "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(root, Path.GetRelativePath(Golden(format), file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        return root;
    }

    // What the scene was when both golden saves were taken; each golden test checks all of it.
    private static void AssertTheGoldenWorld(World world)
    {
        var hero = world.Resolve(PersistentId.FromName("hero"));
        var goblin = world.Resolve(PersistentId.FromName("goblin"));
        var lamp = world.Resolve(PersistentId.FromName("lantern"));
        Assert.False(hero.IsNull);
        Assert.False(goblin.IsNull);
        Assert.False(lamp.IsNull);

        // The renamed component, under its new type and with its renamed field, values intact.
        Assert.Equal(0.75f, world.Get<SaveLamp>(lamp).Lumens);
        Assert.Equal(3, world.Get<SaveLamp>(lamp).Charges);
        Assert.Equal(new Vector3(1.5f, 2f, 4f), world.Get<Transform>(lamp).LocalPosition);

        Assert.Equal(new Vector3(3f, 0.085f, -7f), world.Get<Transform>(hero).LocalPosition);
        Assert.Equal(4, world.CountOf(hero, Id("bread")));
        Assert.Equal(Id("sword"), world.Get<Equipment>(hero).MainHand);
        Assert.True(world.Knows(hero, Id("heal")));
        Assert.Equal(65f, world.Attribute(hero, Id("health")));
        Assert.True(hero.Tags.Has<PlayerControlled>(), "the tag, by id now, by type name then");
        Assert.Equal(goblin, world.Get<ActiveEffects>(hero).Effects.Single(e => e.Record == Id("curse")).Source);
        Assert.Equal(Id("chase"), world.Get<AIState>(goblin).Schedule);

        Assert.Equal(-40f, world.Resources.Get<Reputation>().Of(new RecordId("sandbox", "townsfolk")), 1);
        Assert.Equal("hunt", world.Resources.Get<Journal>().Of(new RecordId("sandbox", "errand"))!.Stage);
        Assert.Equal(new RecordId("sandbox", "storm"), world.Resources.Get<Weather>().Target);
        Assert.Equal(0.25f, world.Resources.Get<Weather>().Blend, 2);
    }

    // The acceptance test of issue #20: a save from main, before stable ids, loads after them — every
    // component found by its old C# name, and the one renamed since (type and field) brought across by
    // its former name and its upgrader.
    [Fact]
    public void AGoldenSaveFromBeforeStableIdsStillLoads()
    {
        using var app = NewApp(Golden(1));
        var world = app.Engine.CreateWorld("main");
        using var log = new CaptureSink();

        Assert.True(app.Engine.Saves.Load("golden"));
        AssertTheGoldenWorld(world);
        // Every component and resource in the file was understood: nothing skipped, nothing unknown.
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warn && e.Message.Contains("golden/world_main"));
    }

    [Fact]
    public void AGoldenSaveInTheCurrentFormatLoads()
    {
        using var app = NewApp(Golden(2));
        var world = app.Engine.CreateWorld("main");
        using var log = new CaptureSink();

        Assert.True(app.Engine.Saves.Load("golden"));
        AssertTheGoldenWorld(world);
        // Every component and resource in the file was understood: nothing skipped, nothing unknown.
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warn && e.Message.Contains("golden/world_main"));
    }

    // The other half of the acceptance test: the same rename with no upgrader is an error that names
    // the component and the field, not a lamp that quietly comes back dark.
    [Fact]
    public void WithoutAnUpgraderTheErrorNamesTheComponentAndTheField()
    {
        string root = CopyOf(1);
        string file = Path.Combine(root, "golden", "world_main.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"GoldenLantern\"", "\"test:bare_lamp\""));

        using var app = NewApp(root);
        var world = app.Engine.CreateWorld("main");
        using var log = new CaptureSink();

        Assert.True(app.Engine.Saves.Load("golden"));   // the rest of the world still loads

        var lamp = world.Resolve(PersistentId.FromName("lantern"));
        Assert.False(world.Has<BareLamp>(lamp));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error
                                          && e.Message.Contains("'test:bare_lamp'")
                                          && e.Message.Contains("field 'Brightness'")
                                          && e.Message.Contains("[Upgrade(1)]"));
    }

    // What a save looks like now: components under their ids, each with its version; tags by id.
    [Fact]
    public void ASaveIsKeyedByStableIdsWithAVersionPerEntry()
    {
        string root = TestEnv.NewTempDir();
        using var app = NewApp(root);
        var world = app.Engine.CreateWorld("main");
        var hero = world.Spawn(Id("hero"), Vector3.Zero);
        world.Add(hero, new Persistent { Id = PersistentId.FromName("hero") });
        world.Add(hero, new SaveLamp { Lumens = 2f });
        world.FlushCommands();
        Assert.True(app.Engine.Saves.Save("ids"));

        var header = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "ids", "header.json")))!;
        Assert.Equal(SaveSystem.FormatVersion, (int)header["formatVersion"]!);

        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "ids", "world_main.json")))!["entities"]![0]!;
        var components = saved["components"]!.AsObject();
        Assert.Equal(1, (int)components["sage:transform"]!["version"]!);
        Assert.Equal(2, (int)components["test:lamp"]!["version"]!);
        Assert.Equal(2f, (float)components["test:lamp"]!["data"]!["Lumens"]!);
        Assert.DoesNotContain(components, c => c.Key == "Transform" || c.Key == "SaveLamp");
        Assert.DoesNotContain(components, c => c.Key == "sage:global_transform" || c.Key == "sage:physics_body");   // [Transient]
        Assert.Equal("sage:player_controlled", (string?)saved["tags"]![0]);
        Assert.Equal(1, (int)JsonNode.Parse(File.ReadAllText(Path.Combine(root, "ids", "world_main.json")))!["resources"]!["weather"]!["version"]!);
    }

    [Fact]
    public void ASaveFromANewerFormatIsRefused()
    {
        string root = CopyOf(2);
        string header = Path.Combine(root, "golden", "header.json");
        File.WriteAllText(header, File.ReadAllText(header).Replace($"\"formatVersion\": {SaveSystem.FormatVersion}", "\"formatVersion\": 99"));

        using var app = NewApp(root);
        app.Engine.CreateWorld("main");
        Assert.False(app.Engine.Saves.Load("golden"));
    }

    [Fact]
    public void UpgradersRunInOrderFromTheSavedVersion()
    {
        var fromOne = Upgraders.Run(typeof(ChainedUpgrades), new JsonObject { ["First"] = 7 }, saved: 1, current: 3);
        Assert.Equal(7, (int)fromOne["Third"]!);
        Assert.Single(fromOne);

        // Saved at 2: only the second step applies.
        var fromTwo = Upgraders.Run(typeof(ChainedUpgrades), new JsonObject { ["Second"] = 9 }, saved: 2, current: 3);
        Assert.Equal(9, (int)fromTwo["Third"]!);
    }

    [Fact]
    public void TheUpgradeHelpersRenameRemoveAndMove()
    {
        var o = new JsonObject { ["hp"] = 5, ["LastHit"] = 1.5, ["Speed"] = 3, ["Old"] = new JsonObject { ["Inner"] = true } };

        o.RenameField("HP", "Value")            // found ignoring case, as the save dialect reads
         .RemoveField("lasthit")
         .MoveField("Speed", "Movement.Speed")  // into an object made on the way
         .MoveField("Old.Inner", "Flag");       // and out of one

        Assert.Equal(5, (int)o["Value"]!);
        Assert.False(o.ContainsKey("hp"));
        Assert.False(o.ContainsKey("LastHit"));
        Assert.Equal(3, (int)o["Movement"]!["Speed"]!);
        Assert.True((bool)o["Flag"]!);
        Assert.Empty(o["Old"]!.AsObject());

        o.RenameField("Missing", "Whatever");   // an old save that never had the field: no error
        Assert.False(o.ContainsKey("Whatever"));
    }

    // Writes the golden save in the current format, only when asked (see the top of this class).
    [Fact]
    public void WriteTheGoldenSaveWhenAsked()
    {
        string? target = Environment.GetEnvironmentVariable("SAGE_WRITE_GOLDEN_SAVE");
        if (string.IsNullOrEmpty(target)) return;

        using var app = NewApp(target);
        var world = app.Engine.CreateWorld("main");
        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        world.Add(ground, Collider.Box(new Vector3(200, 1, 200)));

        var hero = world.Spawn(Id("hero"), new Vector3(3, 0.1f, -7));
        world.Add(hero, new Persistent { Id = PersistentId.FromName("hero") });
        var goblin = world.Spawn(Id("goblin"), new Vector3(0, 0.1f, -3));
        world.Add(goblin, new Persistent { Id = PersistentId.FromName("goblin") });
        var lantern = world.Create(Transform.At(new Vector3(1.5f, 2f, 4f)), "lantern");
        world.Add(lantern, new Persistent { Id = PersistentId.FromName("lantern") });
        world.Add(lantern, new SaveLamp { Lumens = 0.75f, Charges = 3 });
        world.FlushCommands();

        world.Give(hero, Id("sword"));
        world.Give(hero, Id("bread"), 4);
        world.Equip(hero, Id("sword"));
        world.Teach(hero, Id("heal"));
        Effects.Apply(world, hero, Id("hurt"), hero, 35f);
        Effects.Apply(world, hero, Id("curse"), goblin);
        world.Resources.Get<Reputation>().Set(new RecordId("sandbox", "townsfolk"), -40f);
        world.Resources.Get<Journal>().Entries.Add(new Journal.Entry
        {
            Quest = new RecordId("sandbox", "errand"), Stage = "hunt", Progress = { 1 },
        });
        var weather = world.Resources.Get<Weather>();
        weather.Set(new RecordId("sandbox", "storm"), seconds: 4f);
        weather.Advance(1f);
        for (int i = 0; i < 2; i++) world.RunFixed(1f / 60f);

        Assert.True(app.Engine.Saves.Save("golden"));
    }
}
