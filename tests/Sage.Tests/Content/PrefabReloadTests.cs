#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

[Component("test:reload_stats")]
public struct ReloadStats : IComponent
{
    public float Speed;
    public float Reach;
}

// Not on the prefab until the edit adds it.
[Component("test:reload_glow")]
public struct ReloadGlow : IComponent
{
    public float Strength;
}

// Never saved, so it has no spawn baseline: unmodified means "still what the old prefab said".
[Transient]
[Component("test:reload_cache")]
public struct ReloadCache : IComponent
{
    public int Size;
}

[Tag("test:reload_wary")]
public struct ReloadWary : ITag { }

// Hot reload reaches live instances (issue #287): a `prefab` is `[Record(Reload = ReloadPolicy.Live)]`, so
// editing its component values while the game runs updates every entity spawned from it whose value the
// game, a save or a placement override did not change — field by field — and not only the next spawn.
public class PrefabReloadTests
{
    public PrefabReloadTests() { _ = TestEnv.UserRoot; }

    private const string Content = """
    [
      { "type": "prefab", "id": "goblin", "name": "goblin",
        "components": { "test:reload_stats": { "speed": 2, "reach": 1 }, "test:reload_cache": { "size": 4 } },
        "tags": ["test:reload_wary"] },
      { "type": "prefab", "id": "cart", "name": "cart",
        "children": [ { "prefab": "goblin", "name": "driver", "at": [0, 1, 0],
                        "overrides": { "components": { "test:reload_stats": { "reach": 3 } } } } ] },
      { "type": "scene", "id": "main", "place": [ { "prefab": "goblin", "at": [2, 0, 0], "name": "placed" } ] }
    ]
    """;

    // The edit: faster and longer-armed, a glow it did not have, a bigger cache, and no longer wary.
    private static readonly string Edited = Content
        .Replace("""{ "speed": 2, "reach": 1 }, "test:reload_cache": { "size": 4 } }""",
                 """{ "speed": 5, "reach": 2 }, "test:reload_cache": { "size": 8 }, "test:reload_glow": { "strength": 1 } }""")
        .Replace("""["test:reload_wary"]""", "[]");

    private static (HeadlessApp App, MountFixture Files) NewGame(string saves)
    {
        var files = new MountFixture();
        files.Write("game", "data/content.json", Content);
        files.Mount("game", "game");
        var app = HeadlessApp.Bare().Mount(files).StartScene("game:main").Boot();
        app.Engine.Saves.Root = saves;
        return (app, files);
    }

    private static RecordId Game(string name) => new("game", name);

    private static Entity One(World world, string name) =>
        Assert.Single(world.QueryAll().Entities.ToEntityList().Where(e => e.Name == name).ToArray());

    private static ReloadStats Stats(World world, string name) => world.Get<ReloadStats>(One(world, name));

    private static Entity Spawn(World world, string name, PrefabOverrides? overrides = null)
    {
        var entity = world.Spawn(Game("goblin"), new Vector3(9, 0, 9), 0f, overrides);
        entity.Name = name;
        return entity;
    }

    // The acceptance test: the instances the game spawned are the same entities after the reload, and
    // each field follows the edit unless the game, or the override it was spawned with, made it its own.
    [Xunit.Fact]
    public void EditingAPrefabUpdatesTheFieldsNoOneChangedOnEveryLiveInstance()
    {
        var (app, files) = NewGame(TestEnv.NewTempDir());
        using (app)
        {
            var world = app.World;
            var untouched = Spawn(world, "untouched");
            var hurried = Spawn(world, "hurried");
            world.Get<ReloadStats>(hurried).Speed = 9;            // the game's: kept
            world.Get<ReloadCache>(hurried).Size = 1;
            var calm = Spawn(world, "calm");
            world.Remove<ReloadStats>(calm);                      // and then taken off: stays off
            Spawn(world, "long arm", new PrefabOverrides { Components = new JsonObject { ["test:reload_stats"] = new JsonObject { ["reach"] = 7 } } });
            world.Spawn(Game("cart"), new Vector3(0, 0, -5)).Name = "cart";
            world.FlushCommands();
            int before = world.QueryAll().Entities.Count;

            files.Write("game", "data/content.json", Edited);
            app.Records.Reload();

            Assert.Equal(before, world.QueryAll().Entities.Count);   // updated in place, nothing respawned
            Assert.True(world.IsAlive(untouched));

            Assert.Equal(5f, Stats(world, "untouched").Speed);
            Assert.Equal(2f, Stats(world, "untouched").Reach);
            Assert.Equal(8, world.Get<ReloadCache>(untouched).Size);                  // [Transient] too
            Assert.Equal(1f, world.Get<ReloadGlow>(untouched).Strength);              // a component it now has
            Assert.False(untouched.Tags.Has<ReloadWary>());                          // a tag it no longer has

            Assert.Equal(9f, Stats(world, "hurried").Speed);       // the game's value
            Assert.Equal(2f, Stats(world, "hurried").Reach);       // the prefab's, new
            Assert.Equal(1, world.Get<ReloadCache>(hurried).Size);

            Assert.False(world.Has<ReloadStats>(calm));            // taken off by the game, not put back
            Assert.True(world.Has<ReloadGlow>(calm));

            Assert.Equal(5f, Stats(world, "long arm").Speed);
            Assert.Equal(7f, Stats(world, "long arm").Reach);      // the override still wins
            Assert.Equal(5f, Stats(world, "driver").Speed);        // a prefab's child
            Assert.Equal(3f, Stats(world, "driver").Reach);        // with its parent's override

            // The scene's own placement is placed again (Scenes.Respawn), from the edit.
            Assert.Equal(5f, Stats(world, "placed").Speed);
        }
    }

    // A reload with nothing changed changes nothing, and the next edit is measured from the last one: a
    // field the game changed after the first reload is kept by the second, and one it left still follows.
    [Xunit.Fact]
    public void EachReloadFollowsFromTheLastOne()
    {
        var (app, files) = NewGame(TestEnv.NewTempDir());
        using (app)
        {
            var world = app.World;
            var goblin = Spawn(world, "goblin one");
            files.Write("game", "data/content.json", Edited);
            app.Records.Reload();
            Assert.Equal(5f, world.Get<ReloadStats>(goblin).Speed);

            world.Get<ReloadStats>(goblin).Reach = 6;               // changed after the first reload
            app.Records.Reload();                                   // the same content again
            Assert.Equal(5f, world.Get<ReloadStats>(goblin).Speed);
            Assert.Equal(6f, world.Get<ReloadStats>(goblin).Reach);

            files.Write("game", "data/content.json", Edited.Replace("\"speed\": 5, \"reach\": 2", "\"speed\": 6, \"reach\": 4"));
            app.Records.Reload();
            Assert.Equal(6f, world.Get<ReloadStats>(goblin).Speed);
            Assert.Equal(6f, world.Get<ReloadStats>(goblin).Reach);
        }
    }

    // After a reload the instance's baseline is the new prefab: a save writes nothing of what still
    // follows it, and what the game changed is written and comes back over the prefab on load.
    [Xunit.Fact]
    public void ASaveAfterAReloadDiffsAgainstTheNewPrefab()
    {
        string saves = TestEnv.NewTempDir();
        var (app, files) = NewGame(saves);
        using (app)
        {
            var world = app.World;
            var untouched = Spawn(world, "untouched");
            Spawn(world, "hurried");
            world.Get<ReloadStats>(One(world, "hurried")).Speed = 9;
            files.Write("game", "data/content.json", Edited);
            app.Records.Reload();
            Assert.Equal(5f, world.Get<ReloadStats>(untouched).Speed);   // it followed: a stale baseline would write it
            Assert.True(app.Engine.Saves.Save("slot"));

            string id = world.Get<Persistent>(untouched).Id.ToString();
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(saves, "slot", "world_main.json")))!;
            var saved = root["entities"]!.AsArray().Select(e => e!.AsObject()).Single(e => (string?)e["id"] == id);
            var components = saved["components"]!.AsObject();
            Assert.False(components.ContainsKey("test:reload_stats"), components.ToJsonString());
            Assert.False(components.ContainsKey("test:reload_glow"), components.ToJsonString());
            Assert.Null(saved["removed"]);
        }

        var (second, secondFiles) = NewGame(saves);
        using (second)
        {
            secondFiles.Write("game", "data/content.json", Edited);
            second.Records.Reload();
            Assert.True(second.Engine.Saves.Load("slot"));
            Assert.Equal(5f, Stats(second.World, "untouched").Speed);
            Assert.Equal(9f, Stats(second.World, "hurried").Speed);
            Assert.Equal(2f, Stats(second.World, "hurried").Reach);
        }
    }

    // What a load laid back is the save's, so a reload after the load keeps it, field by field.
    [Xunit.Fact]
    public void AValueALoadPutBackIsKeptByAReload()
    {
        string saves = TestEnv.NewTempDir();
        var (app, _) = NewGame(saves);
        using (app)
        {
            Spawn(app.World, "hurried");
            app.World.Get<ReloadStats>(One(app.World, "hurried")).Speed = 9;
            app.World.FlushCommands();
            Assert.True(app.Engine.Saves.Save("slot"));
        }

        var (second, files) = NewGame(saves);
        using (second)
        {
            Assert.True(second.Engine.Saves.Load("slot"));
            Assert.Equal(9f, Stats(second.World, "hurried").Speed);

            files.Write("game", "data/content.json", Edited);
            second.Records.Reload();
            Assert.Equal(9f, Stats(second.World, "hurried").Speed);
            Assert.Equal(2f, Stats(second.World, "hurried").Reach);
        }
    }

    // The policy is declared on the record type: prefabs and scenes are live, anything else waits for its
    // next spawn unless its type says otherwise.
    [Xunit.Fact]
    public void RecordTypesDeclareTheirReloadPolicy()
    {
        using var app = HeadlessApp.Bare().Build();
        Assert.Equal(ReloadPolicy.Live, app.Records.ReloadPolicyOf("prefab"));
        Assert.Equal(ReloadPolicy.Live, app.Records.ReloadPolicyOf("scene"));
        Assert.Equal(ReloadPolicy.NextSpawn, app.Records.ReloadPolicyOf("placements"));
        Assert.Equal(ReloadPolicy.NextSpawn, app.Records.ReloadPolicyOf("no_such_type"));
    }

    // The issue's done criterion, in the Sandbox: a prefab's component values edited (here by a patch in a
    // mount over the Sandbox's own content, the file a modder or the editor saves) reach what is already in
    // the running world — the player the rules spawned, and campfires spawned at run time — except a value
    // the game changed.
    [Xunit.Fact]
    public void InTheSandboxEditingAPrefabUpdatesItsUnmodifiedInstances()
    {
        const string Before = """
        [ { "type": "prefab", "id": "sandbox:player", "patch": true, "components": { "test:reload_stats": { "speed": 2 } } },
          { "type": "prefab", "id": "sandbox:campfire", "patch": true, "components": { "map_marker": { "label": "fire" } } } ]
        """;
        var files = new MountFixture();
        files.Write("tune", "data/tune.json", Before);
        files.Mount("tune", "tune");
        using var app = HeadlessApp.ForGame(Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox"), new global::Sandbox.SandboxModule())
            .WithEngineContent().Mount(files).Boot();
        var world = app.World;
        var player = Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList().ToArray());
        Assert.Equal(2f, world.Get<ReloadStats>(player).Speed);
        var lit = world.Spawn(new RecordId("sandbox", "campfire"), new Vector3(3, 0, 3));
        var named = world.Spawn(new RecordId("sandbox", "campfire"), new Vector3(-3, 0, 3));
        world.Get<Sage.Kits.Rpg.MapMarker>(named).Label = "my fire";   // the game's
        world.FlushCommands();

        files.Write("tune", "data/tune.json", Before.Replace("\"speed\": 2", "\"speed\": 4").Replace("\"label\": \"fire\"", "\"label\": \"embers\""));
        app.Records.Reload();

        Assert.Equal(player, Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList().ToArray()));
        Assert.Equal(4f, world.Get<ReloadStats>(player).Speed);
        Assert.Equal("embers", world.Get<Sage.Kits.Rpg.MapMarker>(lit).Label);
        Assert.Equal("my fire", world.Get<Sage.Kits.Rpg.MapMarker>(named).Label);
        Assert.Equal("sandbox:map_place", world.Get<Sage.Kits.Rpg.MapMarker>(lit).Style);   // what the edit left alone
    }
}
