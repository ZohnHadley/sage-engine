#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// A load reconciles instead of rebuilding (REDESIGN §4.5, phase 4i issue 4i-3): everything content places
// has a stable id (scene placements, placements documents, `.map` entities, prefab children), a save
// writes tombstones for what the game destroyed, and a load places the *current* content again, removes
// the tombstoned, lays the saved state onto what matches by id and then spawns what the game made.
//
// Most of these span two runs of the game — a fresh HeadlessApp over the same saves folder — because the
// point is a save that outlives the engine that wrote it, and often the content it was written against.
public class ReconcileTests
{
    public ReconcileTests() { _ = TestEnv.UserRoot; }

    // A room with two loaves on the floor (one named, one not) and a counter with an authored id.
    private const string RoomMap = """
        {
        "classname" "worldspawn"
        }
        {
        "classname" "bread_on_floor"
        "origin" "32 0 16"
        "targetname" "bread"
        }
        {
        "classname" "levers"
        "origin" "0 32 16"
        "targetname" "levers"
        "id" "levers-1"
        }
        {
        "classname" "bread_on_floor"
        "origin" "-32 0 16"
        }
        """;

    private const string Content = """
    [
      { "type": "item", "id": "bread", "label": "bread", "weight": 1 },
      { "type": "prefab", "id": "rock", "name": "rock" },
      { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"], "parts": { "inventory": { "capacity": 10 } } },
      { "type": "prefab", "id": "bread_on_floor", "name": "loaf", "parts": { "pickup": { "item": "bread" } } },
      { "type": "prefab", "id": "levers", "name": "levers", "parts": { "logic_counter": { "min": 0, "max": 3 } } },
      { "type": "prefab", "id": "lid", "name": "lid", "parts": { "logic_counter": { "max": 5 } } },
      { "type": "prefab", "id": "chest", "name": "chest", "children": [ { "prefab": "lid", "name": "lid", "at": [0, 1, 0] } ] },
      { "type": "map", "id": "room", "file": "maps/room.map" },
      { "type": "placements", "id": "yard",
        "place": [ { "prefab": "rock", "at": [5, 0, 5], "name": "yard rock" }, { "prefab": "chest", "at": [6, 0, 6], "name": "yard chest" } ] },
      { "type": "scene", "id": "main",
        "player": { "prefab": "hero", "at": [0, 1, 0] },
        "maps": ["room"], "placements": ["yard"],
        "place": [ { "prefab": "rock", "at": [2, 0, 0], "name": "east" },
                   { "prefab": "rock", "at": [-2, 0, 0], "name": "west" },
                   { "prefab": "chest", "at": [0, 0, 4], "name": "chest" } ] }
    ]
    """;

    private static MountFixture Files(string content = Content, string map = RoomMap)
    {
        var files = new MountFixture();
        files.Write("game", "data/content.json", content);
        files.Write("game", "maps/room.map", map);
        files.Mount("game", "game");
        return files;
    }

    // One run of the game: booted into the scene, with a tick so the level's entities are in.
    private static HeadlessApp Run(MountFixture files, string saves)
    {
        var app = HeadlessApp.Gameplay().With(new MapModule()).Mount(files).StartScene("game:main").Boot();
        app.Engine.Saves.Root = saves;
        Tick(app.World);
        return app;
    }

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    private static Entity[] Named(World world, string name) =>
        world.QueryAll().Entities.ToEntityList().Where(e => e.Name == name).ToArray();

    private static Entity One(World world, string name) => Assert.Single(Named(world, name));

    private static RecordId Game(string name) => new("game", name);

    private static Entity Hero(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    private static List<PersistentId> Ids(World world) =>
        world.Query<Persistent>().Entities.ToEntityList().Select(e => e.GetComponent<Persistent>().Id).ToList();

    // The acceptance test for "a load throws away new content" (the plan's first gap): a placement added
    // to the scene and to a placements document after the save is there after the load, beside what the
    // save brought back, and nothing is doubled.
    [Xunit.Fact]
    public void AnEntityAddedToTheContentAfterASaveIsThereAfterTheLoad()
    {
        string saves = TestEnv.NewTempDir();
        using (var first = Run(Files(), saves))
        {
            first.World.Teleport(One(first.World, "west"), Transform.At(new Vector3(-7, 0, 0)));   // moved by the game
            Assert.True(first.Engine.Saves.Save("slot"));
        }

        string patched = Content
            .Replace("""{ "prefab": "rock", "at": [-2, 0, 0], "name": "west" },""",
                     """{ "prefab": "rock", "at": [-2, 0, 0], "name": "west" }, { "prefab": "rock", "at": [0, 0, -9], "name": "new" },""")
            .Replace("""{ "prefab": "rock", "at": [5, 0, 5], "name": "yard rock" },""",
                     """{ "prefab": "rock", "at": [5, 0, 5], "name": "yard rock" }, { "prefab": "rock", "at": [8, 0, 8], "name": "new in the yard" },""");
        using var second = Run(Files(patched), saves);
        var world = second.World;
        Assert.True(second.Engine.Saves.Load("slot"));

        Assert.Equal(new Vector3(0, 0, -9), One(world, "new").GetComponent<Transform>().LocalPosition);
        One(world, "new in the yard");
        Assert.Equal(new Vector3(-7, 0, 0), One(world, "west").GetComponent<Transform>().LocalPosition);   // the save's
        One(world, "east");
        One(world, "yard rock");
        Hero(world);
        Tick(world, 2);
        Assert.Equal(2, Named(world, "chest").Length + Named(world, "yard chest").Length);
    }

    // A killed placement stays dead in a new run of the game: the save's tombstone removes it from the
    // content placed again underneath. A prefab's child the game destroyed stays destroyed as well.
    [Xunit.Fact]
    public void AKilledPlacedEntityStaysDeadAcrossAReboot()
    {
        string saves = TestEnv.NewTempDir();
        using (var first = Run(Files(), saves))
        {
            var world = first.World;
            world.Destroy(One(world, "east"));
            world.Destroy(One(world, "chest").ChildEntities.Single());   // the scene chest's lid, torn off
            world.FlushCommands();
            Assert.True(first.Engine.Saves.Save("slot"));

            var file = JsonNode.Parse(File.ReadAllText(Path.Combine(saves, "slot", "world_main.json")))!;
            Assert.Equal(2, file["tombstones"]!["scene:game:main"]!.AsArray().Count);
        }

        using var second = Run(Files(), saves);
        Assert.Single(Named(second.World, "east"));   // the new run placed it, as content does
        Assert.True(second.Engine.Saves.Load("slot"));

        Assert.Empty(Named(second.World, "east"));
        One(second.World, "west");
        Assert.Empty(One(second.World, "chest").ChildEntities);
        Assert.Single(One(second.World, "yard chest").ChildEntities);
        Tick(second.World, 2);
        Assert.Empty(Named(second.World, "east"));
    }

    // The handoff gap ".map entities have no persistent id": a loaf the player picked up before the save
    // is not back on the floor after it — while it is in the pack — and the other loaf is still there.
    [Xunit.Fact]
    public void AMapPickupTakenBeforeTheSaveIsNotBack()
    {
        string saves = TestEnv.NewTempDir();
        using (var first = Run(Files(), saves))
        {
            var world = first.World;
            var loaf = One(world, "bread");
            Assert.True(world.Has<Persistent>(loaf), "a map entity has a persistent id");
            Assert.True(world.Give(Hero(world), Game("bread")));
            world.Destroy(loaf);
            world.FlushCommands();
            Assert.True(first.Engine.Saves.Save("slot"));
        }

        using var second = Run(Files(), saves);
        var w = second.World;
        One(w, "bread");
        Assert.True(second.Engine.Saves.Load("slot"));

        Assert.Empty(Named(w, "bread"));
        Assert.Single(w.Query<Pickup>().Entities.ToEntityList());   // the unnamed loaf
        Assert.Equal(1, w.CountOf(Hero(w), Game("bread")));
        Tick(w, 3);
        Assert.Empty(Named(w, "bread"));
    }

    // A map's logic keeps its count: a counter at 2 of 3 is at 2 after the load, and a third Add takes it
    // to its maximum from there.
    [Xunit.Fact]
    public void AMapLogicCounterAtTwoOfThreeIsAtTwoAfterTheLoad()
    {
        string saves = TestEnv.NewTempDir();
        using (var first = Run(Files(), saves))
        {
            var world = first.World;
            var levers = One(world, "levers");
            world.IO().FireInput(levers, "Add");
            world.IO().FireInput(levers, "Add");
            Tick(world, 2);
            Assert.Equal(2f, world.Get<LogicCounter>(levers).Value);
            Assert.True(first.Engine.Saves.Save("slot"));
        }

        using var second = Run(Files(), saves);
        var w = second.World;
        Assert.Equal(0f, w.Get<LogicCounter>(One(w, "levers")).Value);
        Assert.True(second.Engine.Saves.Load("slot"));

        var loaded = One(w, "levers");
        Assert.Equal(2f, w.Get<LogicCounter>(loaded).Value);
        Assert.Equal(3f, w.Get<LogicCounter>(loaded).Max);
        // Its id is its authored `id` key's, not its place in the file.
        Assert.Equal(ContentIds.Authored("map:game:room", "levers-1"), loaded.GetComponent<Persistent>().Id);

        w.IO().FireInput(loaded, "Add");
        Tick(w, 2);
        Assert.Equal(3f, w.Get<LogicCounter>(loaded).Value);
    }

    // Every id in the world is one entity's: scene placements, a document's, a map's (two loaves with no
    // name, one with), each chest's lid and the player — before a save, after a load in the same run and
    // after a load in a new one. And the set is the same each time: nothing doubled, nothing lost.
    [Xunit.Fact]
    public void NoPersistentIdIsDuplicated()
    {
        string saves = TestEnv.NewTempDir();
        using var log = new CaptureSink();
        List<PersistentId> before;
        using (var first = Run(Files(), saves))
        {
            before = Ids(first.World);
            Assert.Equal(before.Count, before.Distinct().Count());
            // east, west, chest and its lid; the yard's rock, chest and lid; three from the map; the player.
            Assert.Equal(11, before.Count);
            Assert.True(first.Engine.Saves.Save("slot"));

            Assert.True(first.Engine.Saves.Load("slot"));
            var again = Ids(first.World);
            Assert.Equal(before.OrderBy(i => i.Value), again.OrderBy(i => i.Value));
        }

        using var second = Run(Files(), saves);
        Assert.True(second.Engine.Saves.Load("slot"));
        Tick(second.World, 2);
        var after = Ids(second.World);
        Assert.Equal(before.OrderBy(i => i.Value), after.OrderBy(i => i.Value));
        Assert.Equal(2, Named(second.World, "lid").Length);
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("Duplicate PersistentId"));
    }

    // A prefab's children are spawned by their parent, once: a chest the game spawned (not content) comes
    // back from the save with its lid, and the lid's saved state is laid onto that lid rather than a second
    // one spawned from the save beside it.
    [Xunit.Fact]
    public void APrefabsChildrenAreNotSpawnedTwice()
    {
        string saves = TestEnv.NewTempDir();
        PersistentId id;
        using (var first = Run(Files(), saves))
        {
            var world = first.World;
            var chest = world.Spawn(Game("chest"), new Vector3(20, 0, 20));   // a runtime spawn has an id (4i-4)
            id = chest.GetComponent<Persistent>().Id;
            var lid = chest.ChildEntities.Single();
            Assert.Equal(ContentIds.Child(id, "name:lid"), lid.GetComponent<Persistent>().Id);
            world.Get<LogicCounter>(lid).Value = 4;
            Assert.True(first.Engine.Saves.Save("slot"));

            var file = JsonNode.Parse(File.ReadAllText(Path.Combine(saves, "slot", "world_main.json")))!;
            var savedLid = file["entities"]!.AsArray().Single(e => (string?)e!["id"] == lid.GetComponent<Persistent>().Id.ToString())!;
            Assert.Equal(id.ToString(), (string?)savedLid["parent"]);
        }

        using var second = Run(Files(), saves);
        var w = second.World;
        Assert.True(second.Engine.Saves.Load("slot"));
        var loaded = w.Resolve(id);
        Assert.False(loaded.IsNull);
        var loadedLid = Assert.Single(loaded.ChildEntities);
        Assert.Equal(4f, w.Get<LogicCounter>(loadedLid).Value);
        Assert.Equal(3, Named(w, "lid").Length);   // the scene's, the yard's and this one's: one each
    }

    // Hot reload takes the same path: the scene is placed again from the new records, and what the game
    // destroyed is not put back by it.
    [Xunit.Fact]
    public void AHotReloadLeavesTheDeadDead()
    {
        using var app = Run(Files(), TestEnv.NewTempDir());
        var world = app.World;
        var west = One(world, "west");
        world.Destroy(One(world, "east"));
        world.Destroy(One(world, "levers"));
        world.FlushCommands();

        app.Records.Reload();
        Tick(world, 2);   // the level's entities come back at the next PrePhysics

        Assert.Empty(Named(world, "east"));
        Assert.Empty(Named(world, "levers"));
        Assert.NotEqual(west, One(world, "west"));   // placed again, not kept
        One(world, "bread");
    }

    // A format 2 save (no tombstones, no sources: it listed every persistent entity there was) still loads
    // over content placed again: what it lists is laid on by id, and a scene placement it does not list is
    // one the game had destroyed.
    [Xunit.Fact]
    public void AFormat2SaveOfASceneKeepsItsDeadDead()
    {
        string saves = TestEnv.NewTempDir();
        using (var first = Run(Files(), saves))
        {
            var world = first.World;
            world.Destroy(One(world, "east"));
            world.Teleport(One(world, "west"), Transform.At(new Vector3(-7, 0, 0)));
            world.FlushCommands();
            Assert.True(first.Engine.Saves.Save("slot"));
        }

        // What format 2 wrote: no scene, no tombstones, no sources, and only the scene's own entities and the
        // player had ids.
        string header = Path.Combine(saves, "slot", "header.json");
        File.WriteAllText(header, File.ReadAllText(header).Replace($"\"formatVersion\": {SaveSystem.FormatVersion}", "\"formatVersion\": 2"));
        string path = Path.Combine(saves, "slot", "world_main.json");
        var file = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        file.Remove("tombstones");
        file.Remove("scene");
        var entities = file["entities"]!.AsArray();
        foreach (var entity in entities.ToList())
        {
            string? source = (string?)entity!["source"];
            if (source != null && !source.StartsWith("scene:", StringComparison.Ordinal)) entities.Remove(entity);
            else if (entity["tags"] is JsonArray tags && tags.Any(t => (string?)t == "sage:from_parent_prefab")) entities.Remove(entity);
            entity.AsObject().Remove("source");
        }
        File.WriteAllText(path, file.ToJsonString());

        using var second = Run(Files(), saves);
        Assert.True(second.Engine.Saves.Load("slot"));
        Assert.Empty(Named(second.World, "east"));
        Assert.Equal(new Vector3(-7, 0, 0), One(second.World, "west").GetComponent<Transform>().LocalPosition);
        Assert.Single(One(second.World, "chest").ChildEntities);   // a child is not a tombstone by absence
        Hero(second.World);
    }

    // Ids and tombstones are kept per source (the plan's decision 8): a level that is not placed when the
    // save loads (here it stands on terrain this world has none of) keeps the save's state and tombstones
    // for it pending — written back by the next save, and laid onto its entities when they spawn.
    [Xunit.Fact]
    public void StateForALevelNotYetPlacedWaitsForItAndIsWrittenBack()
    {
        string saves = TestEnv.NewTempDir();
        using (var first = Run(Files(), saves))
        {
            var world = first.World;
            world.Get<LogicCounter>(One(world, "levers")).Value = 2;
            world.Destroy(One(world, "bread"));
            world.FlushCommands();
            Assert.True(first.Engine.Saves.Save("slot"));
        }

        string onTerrain = Content.Replace("""{ "type": "map", "id": "room", "file": "maps/room.map" }""",
                                           """{ "type": "map", "id": "room", "file": "maps/room.map", "onTerrain": true }""");
        using (var waiting = Run(Files(onTerrain), saves))
        {
            var world = waiting.World;
            Assert.True(waiting.Engine.Saves.Load("slot"));
            Assert.Empty(Named(world, "levers"));   // no ground, so the level has not spawned anything
            Assert.True(waiting.Engine.Saves.Save("again"));

            // Its entities arrive (as they would once the ground under it is generated): the save's state
            // is laid onto them then.
            MapLoader.EnsureEntities(world, world.Resources.Get<MapLevels>().Loaded.Single());
            Assert.Equal(2f, world.Get<LogicCounter>(One(world, "levers")).Value);
            Assert.Empty(Named(world, "bread"));
        }

        using var third = Run(Files(), saves);
        Assert.True(third.Engine.Saves.Load("again"));
        Assert.Equal(2f, third.World.Get<LogicCounter>(One(third.World, "levers")).Value);
        Assert.Empty(Named(third.World, "bread"));
        One(third.World, "west");
    }

    // An authored placement `id` wins over the derived one, so reordering the content does not move the
    // saved state to another entity; two placements with one id are a content error.
    [Xunit.Fact]
    public void AnAuthoredIdSurvivesTheContentBeingReordered()
    {
        const string Ordered = """
        [
          { "type": "prefab", "id": "levers", "name": "levers", "parts": { "logic_counter": { "min": 0, "max": 3 } } },
          { "type": "scene", "id": "main",
            "place": [ { "prefab": "levers", "name": "a", "id": "gate-a" }, { "prefab": "levers", "name": "b", "id": "gate-b" } ] }
        ]
        """;
        string saves = TestEnv.NewTempDir();
        using (var first = Run(Files(Ordered), saves))
        {
            first.World.Get<LogicCounter>(One(first.World, "a")).Value = 1;
            first.World.Get<LogicCounter>(One(first.World, "b")).Value = 2;
            Assert.True(first.Engine.Saves.Save("slot"));
        }

        string reordered = Ordered.Replace(
            """{ "prefab": "levers", "name": "a", "id": "gate-a" }, { "prefab": "levers", "name": "b", "id": "gate-b" }""",
            """{ "prefab": "levers", "name": "b", "id": "gate-b" }, { "prefab": "levers", "name": "a", "id": "gate-a" }""");
        using (var second = Run(Files(reordered), saves))
        {
            Assert.True(second.Engine.Saves.Load("slot"));
            Assert.Equal(1f, second.World.Get<LogicCounter>(One(second.World, "a")).Value);
            Assert.Equal(2f, second.World.Get<LogicCounter>(One(second.World, "b")).Value);
        }

        var twice = Files(Ordered.Replace("\"gate-b\"", "\"gate-a\""));
        using var log = new CaptureSink();
        using var app = HeadlessApp.Bare().Mount(twice).Build();
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("another placement already has the id 'gate-a'"));
    }
}
