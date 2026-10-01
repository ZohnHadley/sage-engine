#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Cells go dormant with their state (phase 4g issue 4g-1, the 4g plan's decision 2). A scene the player
// leaves keeps what happened in it — the wounded stay wounded, what was dropped stays on the floor — and
// the runtime spawns that belong to it leave the world with it. A dormant cell keeps the sector its
// positions are relative to, so a rebase in between, or a save and a load, moves nothing.
public class CellTests
{
    public CellTests() { _ = TestEnv.UserRoot; }

    private const string Content = """
    [
      { "type": "attribute", "id": "health", "max": 100, "start": 100 },
      { "type": "effect", "id": "hurt", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
      { "type": "item", "id": "sword", "label": "a sword", "weight": 3 },
      { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"], "parts": { "inventory": { "capacity": 10 } } },
      { "type": "prefab", "id": "goblin", "name": "goblin", "parts": { "attributes": {} } },
      { "type": "prefab", "id": "rock", "name": "rock" },
      { "type": "prefab", "id": "lid", "name": "lid", "parts": { "logic_counter": { "max": 5 } } },
      { "type": "prefab", "id": "chest", "name": "chest", "children": [ { "prefab": "lid", "name": "lid", "at": [0, 1, 0] } ] },
      { "type": "scene", "id": "camp",
        "player": { "prefab": "hero", "at": [0, 1, 0] },
        "place": [ { "prefab": "goblin", "at": [5, 0, 0], "name": "wounded", "id": "wounded" },
                   { "prefab": "goblin", "at": [-5, 0, 0], "name": "spared", "id": "spared" } ] },
      { "type": "scene", "id": "crypt",
        "player": { "prefab": "hero", "at": [100, 1, 100] },
        "place": [ { "prefab": "rock", "at": [100, 0, 104], "name": "altar" } ] }
    ]
    """;

    private static MountFixture Files(string content = Content)
    {
        var files = new MountFixture();
        files.Write("game", "data/content.json", content);
        files.Mount("game", "game");
        return files;
    }

    private static HeadlessApp Run(MountFixture files, string saves)
    {
        var app = HeadlessApp.Gameplay().Mount(files).StartScene("game:camp").Boot();
        app.Engine.Saves.Root = saves;
        app.World.RunFixed(1f / 60f);
        return app;
    }

    private static RecordId Game(string name) => new("game", name);

    private static Entity[] Named(World world, string name) =>
        world.QueryAll().Entities.ToEntityList().Where(e => e.Name == name).ToArray();

    private static Entity One(World world, string name) => Assert.Single(Named(world, name));

    private static Entity[] Pickups(World world) => world.Query<Pickup>().Entities.ToEntityList().ToArray();

    private static Entity Hero(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    private static List<PersistentId> Ids(World world) =>
        world.Query<Persistent>().Entities.ToEntityList().Select(e => e.GetComponent<Persistent>().Id).ToList();

    private static float Health(World world, Entity entity) => world.Attribute(entity, Game("health"));

    private static Vector3 Absolute(World world, Entity entity) => world.Origin().ToAbsolute(world.Get<Transform>(entity).LocalPosition);

    // In the camp: wound one goblin to 88, drop a sword at (3, 0, 1), and spawn a chest whose lid is at 4.
    private static (PersistentId Sword, PersistentId Chest) Play(World world)
    {
        var wounded = One(world, "wounded");
        Effects.Apply(world, wounded, Game("hurt"), wounded, 12f);
        var sword = world.SpawnPickup(Game("sword"), 1, new Vector3(3, 0, 1));
        var chest = world.Spawn(Game("chest"), new Vector3(-3, 0, 2));
        world.Get<LogicCounter>(chest.ChildEntities.Single()).Value = 4;
        world.FlushCommands();
        Assert.Equal(88f, Health(world, One(world, "wounded")));
        return (sword.GetComponent<Persistent>().Id, chest.GetComponent<Persistent>().Id);
    }

    // The plan's first test: leave a scene and come back, and the wounded goblin is still wounded and the
    // dropped sword is still on the floor. Twice, so a cell that has woken once goes dormant again as well.
    [Xunit.Fact]
    public void LeavingASceneAndComingBackKeepsTheWoundedWoundedAndTheSwordOnTheFloor()
    {
        using var app = Run(Files(), TestEnv.NewTempDir());
        var world = app.World;
        var scenes = app.Engine.Scenes;
        var (sword, chest) = Play(world);
        var hero = Hero(world);

        // A runtime spawn belongs to the cell it was made in; the player is not put to sleep with it.
        Assert.Equal("scene:game:camp", world.Get<InCell>(world.Resolve(sword)).Source);
        Assert.Equal("scene:game:camp", world.Get<InCell>(world.Resolve(chest)).Source);
        var before = Ids(world);

        for (int trip = 0; trip < 2; trip++)
        {
            Assert.True(scenes.Load(world, Game("crypt")));
            world.RunFixed(1f / 60f);
            Assert.Empty(Named(world, "wounded"));
            Assert.Empty(Pickups(world));   // the sword is asleep with the camp, not lying in the crypt
            Assert.True(world.Resolve(sword).IsNull);
            Assert.True(world.Resolve(chest).IsNull);
            Assert.Empty(Named(world, "lid"));
            Assert.Equal(hero, Hero(world));   // the player came along
            One(world, "altar");

            Assert.True(scenes.Load(world, Game("camp")));
            world.RunFixed(1f / 60f);
            Assert.Equal(88f, Health(world, One(world, "wounded")));
            Assert.Equal(100f, Health(world, One(world, "spared")));
            var floor = Assert.Single(Pickups(world));
            Assert.Equal(sword, floor.GetComponent<Persistent>().Id);
            Assert.Equal(new Vector3(3, 0, 1), world.Get<Transform>(floor).LocalPosition);
            Assert.Equal(Game("sword"), world.Get<Pickup>(floor).Item);
            var back = world.Resolve(chest);
            Assert.Equal(4f, world.Get<LogicCounter>(Assert.Single(back.ChildEntities)).Value);
            Assert.Equal("scene:game:camp", world.Get<InCell>(back).Source);
            Assert.Empty(Named(world, "altar"));
            Assert.Equal(hero, Hero(world));

            // Nothing doubled, nothing lost.
            var now = Ids(world);
            Assert.Equal(now.Count, now.Distinct().Count());
            Assert.Equal(before.OrderBy(i => i.Value), now.OrderBy(i => i.Value));
        }
    }

    // The dead stay dead as well (4i's tombstones, now beside the state): a goblin killed in the camp is not
    // back after a trip to the crypt.
    [Xunit.Fact]
    public void AKilledGoblinStaysDeadAcrossATripAndTheOtherKeepsItsState()
    {
        using var app = Run(Files(), TestEnv.NewTempDir());
        var world = app.World;
        Play(world);
        world.Destroy(One(world, "spared"));
        world.FlushCommands();

        app.Engine.Scenes.Load(world, Game("crypt"));
        app.Engine.Scenes.Load(world, Game("camp"));
        Assert.Empty(Named(world, "spared"));
        Assert.Equal(88f, Health(world, One(world, "wounded")));
    }

    // A dormant cell keeps the sector its positions are relative to: the world rebased while the camp is
    // dormant, and saved and loaded into a new run, still puts the sword where it was dropped.
    [Xunit.Fact]
    public void ADormantCellSurvivesARebaseAndASaveAndLoad()
    {
        string saves = TestEnv.NewTempDir();
        var dropped = new Vector3(3, 0, 1);   // absolute: the camp was played with the origin at (0, 0)
        PersistentId sword;
        using (var first = Run(Files(), saves))
        {
            var world = first.World;
            (sword, _) = Play(world);
            Assert.True(first.Engine.Scenes.Load(world, Game("crypt")));
            world.Rebase(new SectorCoord(2, -1));
            Assert.True(first.Engine.Saves.Save("slot"));

            // The file: the camp is dormant, with its sector, and its state is not among the live entities.
            var file = JsonNode.Parse(File.ReadAllText(Path.Combine(saves, "slot", "world_main.json")))!;
            Assert.Equal(2, (int)file["origin"]!["x"]!);
            var camp = file["dormant"]!["scene:game:camp"]!;
            Assert.Equal(0, (int)camp["sector"]!["x"]!);
            Assert.Equal(0, (int)camp["sector"]!["z"]!);
            Assert.Contains(camp["entities"]!.AsArray(), e => (string?)e!["id"] == sword.ToString());
            Assert.DoesNotContain(file["entities"]!.AsArray(), e => (string?)e!["id"] == sword.ToString());

            // In this run too: back to the camp after the rebase, and the sword is where it fell.
            Assert.True(first.Engine.Scenes.Load(world, Game("camp")));
            Assert.Equal(dropped, Absolute(world, world.Resolve(sword)));
            Assert.Equal(88f, Health(world, One(world, "wounded")));
        }

        using var second = Run(Files(), saves);
        var w = second.World;
        Assert.True(second.Engine.Saves.Load("slot"));
        Assert.Equal(new SectorCoord(2, -1), w.Origin().Sector);
        One(w, "altar");
        Assert.Empty(Pickups(w));
        Assert.Empty(Named(w, "wounded"));

        Assert.True(second.Engine.Scenes.Load(w, Game("camp")));
        var floor = w.Resolve(sword);
        Assert.False(floor.IsNull);
        Assert.Equal(dropped, Absolute(w, floor));
        Assert.Equal(88f, Health(w, One(w, "wounded")));
        Assert.Equal(new Vector3(5, 0, 0), Absolute(w, One(w, "wounded")));
        Assert.Single(Pickups(w));

        // And saving the camp live again writes it among the entities, with nothing left dormant for it.
        Assert.True(second.Engine.Saves.Save("again"));
        var again = JsonNode.Parse(File.ReadAllText(Path.Combine(saves, "again", "world_main.json")))!;
        Assert.Null(again["dormant"]?["scene:game:camp"]);
        Assert.NotNull(again["dormant"]?["scene:game:crypt"]);
    }

    // A hot reload is not a trip: the scene is placed again without the old state laid over the edit
    // (4i-3), and the scene's runtime spawns stay where they are rather than going to sleep.
    [Xunit.Fact]
    public void AHotReloadLeavesTheScenesRuntimeSpawnsInTheWorld()
    {
        using var app = Run(Files(), TestEnv.NewTempDir());
        var world = app.World;
        var (sword, _) = Play(world);
        var lying = world.Resolve(sword);

        app.Records.Reload();
        world.RunFixed(1f / 60f);

        Assert.Equal(lying, world.Resolve(sword));
        Assert.Equal(100f, Health(world, One(world, "wounded")));   // the edit shows, not the old state
        Assert.True(world.Resources.Get<ContentBaseline>().DormantCells.All(c => c.Cell.Entities.Count == 0));
    }

    // Format 3 → 4: a format 3 save had no cells, and everything the game spawned was in its scene (nothing
    // left a scene with its spawns before 4g-1). The upgrader gives those the save's scene's cell, so they
    // go to sleep when the player leaves it; the player does not.
    [Xunit.Fact]
    public void AFormat3SavesRuntimeSpawnsBelongToItsScene()
    {
        string saves = TestEnv.NewTempDir();
        PersistentId sword;
        using (var first = Run(Files(), saves))
        {
            (sword, _) = Play(first.World);
            Assert.True(first.Engine.Saves.Save("slot"));
        }

        // What format 3 wrote: the same, without a cell on anything.
        string header = Path.Combine(saves, "slot", "header.json");
        File.WriteAllText(header, File.ReadAllText(header).Replace($"\"formatVersion\": {SaveSystem.FormatVersion}", "\"formatVersion\": 3"));
        string path = Path.Combine(saves, "slot", "world_main.json");
        var file = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        foreach (var entity in file["entities"]!.AsArray())
            entity!["components"]!.AsObject().Remove("sage:cell");
        File.WriteAllText(path, file.ToJsonString());

        using var second = Run(Files(), saves);
        var world = second.World;
        Assert.True(second.Engine.Saves.Load("slot"));
        Assert.Equal("scene:game:camp", world.Get<InCell>(world.Resolve(sword)).Source);
        Assert.False(world.Has<InCell>(Hero(world)));

        second.Engine.Scenes.Load(world, Game("crypt"));
        Assert.Empty(Pickups(world));
        Hero(world);
        second.Engine.Scenes.Load(world, Game("camp"));
        Assert.Equal(sword, Assert.Single(Pickups(world)).GetComponent<Persistent>().Id);
    }
}
