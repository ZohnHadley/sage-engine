#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

[Component("test:hit_points")]
public struct DiffHitPoints : IComponent
{
    public float Current;
    public float Max;
}

// On the prefab, so the game can take it off.
[Component("test:mark")]
public struct DiffMark : IComponent
{
    public int Kind;
}

// Not on the prefab, so the game can put it on.
[Component("test:glow")]
public struct DiffGlow : IComponent
{
    public float Strength;
}

[Tag("test:angry")]
public struct DiffAngry : ITag { }

// Save what changed (REDESIGN §4.5, phase 4i issue 4i-5): a prefab-spawned entity is saved as a diff
// against its prefab as spawned — overrides and a parent's children bodies included — and a load applies
// the *current* prefab first and the diff over it. So a rebalance of a prefab or a record reaches every
// entity the game never changed, and what the game did change is kept.
//
// Each spans two runs of the game over one saves folder, the second with the content patched, because
// that is what a rebalance is.
public class SaveDiffTests
{
    public SaveDiffTests() { _ = TestEnv.UserRoot; }

    private const string Content = """
    [
      { "type": "attribute", "id": "health", "max": 500, "start": 100 },
      { "type": "effect", "id": "hurt", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
      { "type": "prefab", "id": "goblin", "name": "goblin",
        "components": { "test:hit_points": { "current": 10, "max": 10 }, "test:mark": { "kind": 1 } },
        "tags": ["test:angry"],
        "parts": { "attributes": {} } },
      { "type": "prefab", "id": "cart", "name": "cart",
        "children": [ { "prefab": "goblin", "name": "driver", "at": [0, 1, 0],
                        "overrides": { "components": { "test:hit_points": { "max": 15 } } } } ] },
      { "type": "scene", "id": "main",
        "place": [ { "prefab": "goblin", "at": [2, 0, 0], "name": "calm" },
                   { "prefab": "goblin", "at": [-2, 0, 0], "name": "hurt" },
                   { "prefab": "goblin", "at": [0, 0, 5], "name": "tough",
                     "overrides": { "components": { "test:hit_points": { "max": 30 } } } },
                   { "prefab": "cart", "at": [0, 0, -5], "name": "cart" } ] }
    ]
    """;

    // The rebalance: the goblin's hit points doubled, and the health attribute every creature starts with.
    private static readonly string Rebalanced = Content
        .Replace("""{ "current": 10, "max": 10 }""", """{ "current": 20, "max": 20 }""")
        .Replace("""{ "type": "attribute", "id": "health", "max": 500, "start": 100 }""",
                 """{ "type": "attribute", "id": "health", "max": 500, "start": 150 }""");

    private static MountFixture Files(string content)
    {
        var files = new MountFixture();
        files.Write("game", "data/content.json", content);
        files.Mount("game", "game");
        return files;
    }

    private static HeadlessApp Run(string content, string saves)
    {
        var app = HeadlessApp.Gameplay().Mount(Files(content)).StartScene("game:main").Boot();
        app.Engine.Saves.Root = saves;
        app.World.RunFixed(1f / 60f);
        return app;
    }

    private static Entity One(World world, string name) =>
        Assert.Single(world.QueryAll().Entities.ToEntityList().Where(e => e.Name == name).ToArray());

    private static RecordId Game(string name) => new("game", name);

    private static DiffHitPoints HitPoints(World world, string name) => world.Get<DiffHitPoints>(One(world, name));

    private static JsonObject SavedEntity(string saves, string slot, World world, Entity entity)
    {
        string id = world.Get<Persistent>(entity).Id.ToString();
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(saves, slot, "world_main.json")))!;
        return root["entities"]!.AsArray().Select(e => e!.AsObject()).Single(e => (string?)e["id"] == id);
    }

    // The issue's acceptance test: after a rebalance an untouched goblin has the new prefab's hit points and
    // the new record's health, and a damaged one keeps what the game did to it — only that: the field the
    // game did not change (its max) follows the prefab too.
    [Xunit.Fact]
    public void AnUntouchedGoblinGetsTheRaisedPrefabHealthAndADamagedOneKeepsItsOwn()
    {
        string saves = TestEnv.NewTempDir();
        using (var first = Run(Content, saves))
        {
            var world = first.World;
            var hurt = One(world, "hurt");
            world.Get<DiffHitPoints>(hurt).Current = 4;
            Effects.Apply(world, hurt, Game("hurt"), hurt, 30f);
            // One the game spawned, untouched, and one it spawned and hurt: rebuilt from the prefab on load.
            world.Spawn(Game("goblin"), new Vector3(9, 0, 9)).Name = "summoned";
            var bitten = world.Spawn(Game("goblin"), new Vector3(-9, 0, 9));
            bitten.Name = "bitten";
            world.Get<DiffHitPoints>(bitten).Current = 7;
            world.FlushCommands();
            Assert.Equal(70f, world.Attribute(hurt, Game("health")));
            Assert.True(first.Engine.Saves.Save("slot"));
        }

        using var second = Run(Rebalanced, saves);
        var loaded = second.World;
        Assert.True(second.Engine.Saves.Load("slot"));

        Assert.Equal(20f, HitPoints(loaded, "calm").Current);
        Assert.Equal(20f, HitPoints(loaded, "calm").Max);
        Assert.Equal(150f, loaded.Attribute(One(loaded, "calm"), Game("health")));   // a record's rebalance

        Assert.Equal(4f, HitPoints(loaded, "hurt").Current);    // the game's
        Assert.Equal(20f, HitPoints(loaded, "hurt").Max);       // the prefab's, new
        Assert.Equal(70f, loaded.Attribute(One(loaded, "hurt"), Game("health")));

        Assert.Equal(20f, HitPoints(loaded, "summoned").Current);
        Assert.Equal(new Vector3(9, 0, 9), One(loaded, "summoned").GetComponent<Transform>().LocalPosition);
        Assert.Equal(7f, HitPoints(loaded, "bitten").Current);
        Assert.Equal(20f, HitPoints(loaded, "bitten").Max);
    }

    // What the file holds: nothing of a component the game left alone, and of one it changed only the
    // field that changed. A placed goblin still where content put it has no transform either (4m-4).
    [Xunit.Fact]
    public void ASaveWritesOnlyTheFieldsTheGameChanged()
    {
        string saves = TestEnv.NewTempDir();
        using var app = Run(Content, saves);
        var world = app.World;
        world.Get<DiffHitPoints>(One(world, "hurt")).Current = 4;
        Assert.True(app.Engine.Saves.Save("slot"));

        var calm = SavedEntity(saves, "slot", world, One(world, "calm"));
        Assert.True((bool)calm["diff"]!);
        var components = calm["components"]!.AsObject();
        Assert.False(components.ContainsKey("test:hit_points"));
        Assert.False(components.ContainsKey("test:mark"));
        Assert.False(components.ContainsKey("sage:attributes"));
        Assert.False(components.ContainsKey("sage:transform"));
        Assert.Null(calm["removed"]);

        var hurt = SavedEntity(saves, "slot", world, One(world, "hurt"));
        var data = hurt["components"]!["test:hit_points"]!["data"]!.AsObject();
        Assert.Equal(4f, (float)data["Current"]!);
        Assert.False(data.ContainsKey("Max"));
    }

    // "The prefab as it was spawned" includes a placement's overrides and a parent prefab's body for its
    // child: an overridden field the game left alone is not saved, keeps the override after a load (the
    // content still says it), and the fields the override does not touch follow the rebalanced prefab.
    [Xunit.Fact]
    public void OverridesAndAChildsBodyArePartOfWhatIsDiffedAgainst()
    {
        string saves = TestEnv.NewTempDir();
        using (var first = Run(Content, saves))
        {
            var world = first.World;
            Assert.Equal(30f, HitPoints(world, "tough").Max);
            Assert.Equal(15f, HitPoints(world, "driver").Max);
            Assert.True(first.Engine.Saves.Save("slot"));

            Assert.False(SavedEntity(saves, "slot", world, One(world, "tough"))["components"]!.AsObject().ContainsKey("test:hit_points"));
            Assert.False(SavedEntity(saves, "slot", world, One(world, "driver"))["components"]!.AsObject().ContainsKey("test:hit_points"));
        }

        using var second = Run(Rebalanced, saves);
        var loaded = second.World;
        Assert.True(second.Engine.Saves.Load("slot"));
        Assert.Equal(30f, HitPoints(loaded, "tough").Max);
        Assert.Equal(20f, HitPoints(loaded, "tough").Current);
        Assert.Equal(15f, HitPoints(loaded, "driver").Max);
        Assert.Equal(20f, HitPoints(loaded, "driver").Current);
    }

    // A component the game added (the prefab has none) and one it took off (the prefab has one) both come
    // back as the game left them, on a placed goblin and on one the game spawned; so does a tag taken off.
    [Xunit.Fact]
    public void AnAddedAndARemovedComponentRoundTrip()
    {
        string saves = TestEnv.NewTempDir();
        using (var first = Run(Content, saves))
        {
            var world = first.World;
            var placed = One(world, "calm");
            var spawned = world.Spawn(Game("goblin"), new Vector3(4, 0, 4));
            spawned.Name = "spawned";
            world.FlushCommands();
            foreach (var goblin in new[] { placed, spawned })
            {
                world.Add(goblin, new DiffGlow { Strength = 0.5f });
                world.Remove<DiffMark>(goblin);
                goblin.RemoveTag<DiffAngry>();
            }
            world.FlushCommands();
            Assert.True(first.Engine.Saves.Save("slot"));

            var saved = SavedEntity(saves, "slot", world, spawned);
            Assert.Equal("test:mark", (string?)Assert.Single(saved["removed"]!.AsArray()));
            Assert.Equal(0.5f, (float)saved["components"]!["test:glow"]!["data"]!["Strength"]!);
        }

        using var second = Run(Content, saves);
        var loaded = second.World;
        Assert.True(second.Engine.Saves.Load("slot"));
        foreach (var name in new[] { "calm", "spawned" })
        {
            var goblin = One(loaded, name);
            Assert.Equal(0.5f, loaded.Get<DiffGlow>(goblin).Strength);
            Assert.False(loaded.Has<DiffMark>(goblin), $"{name} got its mark back");
            Assert.False(goblin.Tags.Has<DiffAngry>(), $"{name} got its tag back");
            Assert.True(loaded.Has<DiffHitPoints>(goblin));   // what it kept, it has
        }
        // Untouched goblins beside them are as the prefab makes them.
        Assert.True(loaded.Has<DiffMark>(One(loaded, "hurt")));
        Assert.True(One(loaded, "hurt").Tags.Has<DiffAngry>());
    }

    // Saving what was loaded writes the same diff again: nothing the load laid on becomes "changed", so a
    // second rebalance still reaches the goblin the game never touched.
    [Xunit.Fact]
    public void ASaveOfALoadedGameStaysADiff()
    {
        string saves = TestEnv.NewTempDir();
        using (var first = Run(Content, saves))
            Assert.True(first.Engine.Saves.Save("slot"));

        using (var second = Run(Content, saves))
        {
            Assert.True(second.Engine.Saves.Load("slot"));
            Assert.True(second.Engine.Saves.Save("again"));
            Assert.False(SavedEntity(saves, "again", second.World, One(second.World, "calm"))["components"]!
                .AsObject().ContainsKey("test:hit_points"));
        }

        using var third = Run(Rebalanced, saves);
        Assert.True(third.Engine.Saves.Load("again"));
        Assert.Equal(20f, HitPoints(third.World, "calm").Current);
    }
}
