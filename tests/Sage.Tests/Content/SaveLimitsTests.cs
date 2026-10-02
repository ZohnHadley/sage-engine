#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The saves' remaining limits (phase 4m issue 4m-4): a load takes away the runtime spawns no save names,
// and leaves what the engine and the game made for themselves; a value a part derives from its placement
// is compared with the entity's own, and content's entity still where it was placed is saved without a
// transform, so both follow the content; and a live entity's reference to one that went to sleep with
// its cell is kept by a save and finds it again when it wakes.
public class SaveLimitsTests
{
    public SaveLimitsTests() { _ = TestEnv.UserRoot; }

    private static RecordId Game(string name) => new("game", name);

    private static MountFixture Files(string content)
    {
        var files = new MountFixture();
        files.Write("game", "data/content.json", content);
        files.Mount("game", "game");
        return files;
    }

    private static HeadlessApp Run(string content, string saves, string scene)
    {
        var app = HeadlessApp.Gameplay().Mount(Files(content)).StartScene(scene).Boot();
        app.Engine.Saves.Root = saves;
        app.World.RunFixed(1f / 60f);
        return app;
    }

    private static Entity[] Named(World world, string name) =>
        world.QueryAll().Entities.ToEntityList().Where(e => e.Name == name).ToArray();

    private static Entity One(World world, string name) => Assert.Single(Named(world, name));

    private static int Count(World world, string prefab) =>
        world.Query<Sage.Simulation.FromPrefab>().Entities.Count(e => world.Get<Sage.Simulation.FromPrefab>(e).Prefab == Game(prefab));

    private static JsonObject SavedEntity(string saves, World world, Entity entity)
    {
        string id = world.Get<Persistent>(entity).Id.ToString();
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(saves, "slot", $"world_{world.Name}.json")))!;
        return root["entities"]!.AsArray().Select(e => e!.AsObject()).Single(e => (string?)e["id"] == id);
    }

    // ---- a load removes what the save does not name ------------------------------------------------

    private const string Spawns = """
    [
      { "type": "attribute", "id": "health", "max": 100, "start": 100 },
      { "type": "effect", "id": "sparkle", "executions": [{ "execution": "summon", "prefab": "spark", "count": 2 }] },
      { "type": "prefab", "id": "hero", "name": "hero", "parts": { "attributes": {} } },
      { "type": "prefab", "id": "crate", "name": "crate" },
      { "type": "prefab", "id": "spark", "name": "spark", "persist": false,
        "children": [ { "prefab": "ember", "name": "ember", "at": [0, 1, 0] } ] },
      { "type": "prefab", "id": "ember", "name": "ember" },
      { "type": "scene", "id": "yard", "place": [ { "prefab": "crate", "at": [1, 0, 1], "name": "placed crate" } ] }
    ]
    """;

    // The issue's leak: an effect's and a cue's `"persist": false` spawns were still in the world after a
    // load, beside the world the save brought back. Those made before the save and after it both go, with
    // their children; what the save names is back once, content's and the game's.
    // test: ALoadRemovesTheUnsavedSpawnsItDoesNotName
    [Xunit.Fact]
    public void ALoadRemovesTheUnsavedSpawnsItDoesNotName()
    {
        using var app = Run(Spawns, TestEnv.NewTempDir(), "game:yard");
        var world = app.World;
        var hero = world.Spawn(Game("hero"), Vector3.Zero);
        world.Spawn(Game("crate"), new Vector3(4, 0, 0));
        Effects.Apply(world, hero, Game("sparkle"));   // two sparks, from an effect
        world.RunFixed(1f / 60f);
        world.Spawn(Game("spark"), new Vector3(2, 0, 0));   // and one a cue spawned
        world.FlushCommands();
        Assert.Equal(3, Count(world, "spark"));
        Assert.All(world.Query<Sage.Simulation.FromPrefab>().Entities.ToEntityList().Where(e => e.Name == "spark"),
                   s => Assert.True(s.Tags.Has<Unsaved>()));
        Assert.True(app.Engine.Saves.Save("slot"));

        world.Spawn(Game("spark"), new Vector3(3, 0, 0));   // after the save
        world.FlushCommands();
        Assert.True(app.Engine.Saves.Load("slot"));
        world.RunFixed(1f / 60f);

        Assert.Equal(0, Count(world, "spark"));
        Assert.Equal(0, Count(world, "ember"));   // a prefab's children go with it
        Assert.Equal(2, Count(world, "crate"));   // the placed one and the spawned one, once each
        Assert.Equal(1, Count(world, "hero"));
    }

    // The other side: what is not saved by design and is not the game's to lose is left alone — a bare
    // `Create` (a camera, a tool's marker), a spawn made without an id (the player camera's way), and a
    // `"persist": false` spawn the game then made persistent, which the save names and brings back once.
    // A game's own throwaway entity says `Unsaved` and goes.
    // test: ALoadLeavesWhatTheEngineAndTheGameMadeForThemselves
    [Xunit.Fact]
    public void ALoadLeavesWhatTheEngineAndTheGameMadeForThemselves()
    {
        using var app = Run(Spawns, TestEnv.NewTempDir(), "game:yard");
        var world = app.World;
        var camera = world.Create(Transform.At(new Vector3(0, 5, 0)), "a camera");
        var marker = world.Spawn(Game("crate"), new Vector3(9, 0, 9));
        world.Remove<Persistent>(marker);   // like a spawn made without an id
        marker.Name = "marker";
        var kept = world.Spawn(Game("spark"), new Vector3(6, 0, 0));
        kept.Name = "kept spark";
        world.MakePersistent(kept);
        Assert.False(kept.Tags.Has<Unsaved>());
        var throwaway = world.Create(Transform.Identity, "throwaway");
        throwaway.AddTag<Unsaved>();
        world.FlushCommands();
        Assert.True(app.Engine.Saves.Save("slot"));
        Assert.True(app.Engine.Saves.Load("slot"));
        world.RunFixed(1f / 60f);

        Assert.True(world.IsAlive(camera));
        Assert.Equal(new Vector3(0, 5, 0), world.Get<Transform>(camera).LocalPosition);
        Assert.True(world.IsAlive(marker));
        Assert.Equal(new Vector3(6, 0, 0), world.Get<Transform>(One(world, "kept spark")).LocalPosition);
        Assert.Empty(Named(world, "throwaway"));
    }

    // ---- derived values and placements follow the content --------------------------------------------

    private const string Doors = """
    [
      { "type": "prefab", "id": "door", "name": "door", "parts": { "mover": { "open": [0, 3, 0], "seconds": 2 } } },
      { "type": "scene", "id": "hall",
        "place": [ { "prefab": "door", "at": [0, 0, 0], "name": "first", "id": "first" },
                   { "prefab": "door", "at": [10, 0, 0], "name": "second", "id": "second" },
                   { "prefab": "door", "at": [20, 0, 0], "name": "pushed", "id": "pushed" } ] }
    ]
    """;

    // The content after the save: the second door's placement moved, the third's too, and every door slower.
    private static readonly string Moved = Doors
        .Replace("\"at\": [10, 0, 0]", "\"at\": [10, 0, 4]")
        .Replace("\"at\": [20, 0, 0]", "\"at\": [20, 0, 4]")
        .Replace("\"seconds\": 2", "\"seconds\": 5");

    // A mover's closed position is its placement. The baseline is shared by every door, so the second
    // door's used to differ from the first's and be written, and the door came back where it was saved
    // whatever the content said. Now an untouched door writes nothing — no mover, no transform — and after
    // the content moves its placement it stands at the new one and shuts there, at the new speed; a door
    // the game moved keeps where the game put it.
    // test: AnUntouchedDoorFollowsItsPlacementAndARebalance
    [Xunit.Fact]
    public void AnUntouchedDoorFollowsItsPlacementAndARebalance()
    {
        string saves = TestEnv.NewTempDir();
        using (var first = Run(Doors, saves, "game:hall"))
        {
            var world = first.World;
            var pushed = One(world, "pushed");
            world.Get<Transform>(pushed).LocalPosition = new Vector3(21, 0, 0);
            Assert.True(first.Engine.Saves.Save("slot"));

            var second = SavedEntity(saves, world, One(world, "second"))["components"]!.AsObject();
            Assert.False(second.ContainsKey("sage:mover"));
            Assert.False(second.ContainsKey("sage:transform"));
            var moved = SavedEntity(saves, world, pushed)["components"]!.AsObject();
            Assert.True(moved.ContainsKey("sage:transform"));
            Assert.False(moved.ContainsKey("sage:mover"));
        }

        using var after = Run(Moved, saves, "game:hall");
        var loaded = after.World;
        Assert.True(after.Engine.Saves.Load("slot"));
        loaded.RunFixed(1f / 60f);

        var door = One(loaded, "second");
        Assert.Equal(new Vector3(10, 0, 4), loaded.Get<Transform>(door).LocalPosition);
        Assert.Equal(new Vector3(10, 0, 4), loaded.Get<Mover>(door).Closed);
        Assert.Equal(5f, loaded.Get<Mover>(door).Seconds);
        Assert.Equal(new Vector3(0, 0, 0), loaded.Get<Mover>(One(loaded, "first")).Closed);
        Assert.Equal(new Vector3(21, 0, 0), loaded.Get<Transform>(One(loaded, "pushed")).LocalPosition);
        Assert.Equal(5f, loaded.Get<Mover>(One(loaded, "pushed")).Seconds);
    }

    // ---- references to what sleeps ------------------------------------------------------------------

    private const string Cells = """
    [
      { "type": "attribute", "id": "armour", "max": 95, "start": 50 },
      { "type": "effect", "id": "curse", "duration": "Infinite", "modifiers": [ { "attribute": "armour", "op": "Add", "value": -10 } ] },
      { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"], "parts": { "attributes": {} } },
      { "type": "prefab", "id": "goblin", "name": "goblin", "parts": { "attributes": {} } },
      { "type": "prefab", "id": "rock", "name": "rock" },
      { "type": "scene", "id": "camp",
        "player": { "prefab": "hero", "at": [0, 1, 0] },
        "place": [ { "prefab": "goblin", "at": [5, 0, 0], "name": "witch", "id": "witch" } ] },
      { "type": "scene", "id": "crypt",
        "player": { "prefab": "hero", "at": [100, 1, 100] },
        "place": [ { "prefab": "rock", "at": [100, 0, 104], "name": "altar" } ] }
    ]
    """;

    private static Entity Hero(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    private static Entity CurseSource(World world) =>
        world.Get<ActiveEffects>(Hero(world)).Effects.Single(e => e.Record == Game("curse")).Source;

    // The 4g-1 limit: the hero carries a curse from the witch, and the witch sleeps with the camp while the
    // hero is in the crypt. The reference does not resolve while she sleeps, a save made there keeps it
    // (it used to write null), and it finds her when the camp wakes — in the same run, and after a load of
    // that save into a fresh game, where it was read while she still slept.
    // test: AReferenceToASleepingEntityIsKeptAndResolvesWhenItWakes
    [Xunit.Fact]
    public void AReferenceToASleepingEntityIsKeptAndResolvesWhenItWakes()
    {
        string saves = TestEnv.NewTempDir();
        PersistentId witchId;
        using (var app = Run(Cells, saves, "game:camp"))
        {
            var world = app.World;
            var witch = One(world, "witch");
            witchId = world.Get<Persistent>(witch).Id;
            Assert.True(Effects.Apply(world, Hero(world), Game("curse"), witch));
            world.RunFixed(1f / 60f);

            Assert.True(app.Engine.Scenes.Load(world, Game("crypt")));
            world.RunFixed(1f / 60f);
            Assert.Empty(Named(world, "witch"));
            Assert.True(world.Resolve(CurseSource(world)).IsNull);   // asleep

            Assert.True(app.Engine.Saves.Save("slot"));
            var hero = SavedEntity(saves, world, Hero(world));
            Assert.Contains(witchId.ToString(), hero.ToJsonString());

            Assert.True(app.Engine.Scenes.Load(world, Game("camp")));
            world.RunFixed(1f / 60f);
            Assert.Equal(One(world, "witch"), world.Resolve(CurseSource(world)));
            Assert.Equal(One(world, "witch"), CurseSource(world));   // the effect follows the woken handle
        }

        using var again = Run(Cells, saves, "game:camp");
        var loaded = again.World;
        Assert.True(again.Engine.Saves.Load("slot"));   // in the crypt, the witch asleep
        loaded.RunFixed(1f / 60f);
        Assert.Empty(Named(loaded, "witch"));
        var source = CurseSource(loaded);
        Assert.True(loaded.Resolve(source).IsNull);
        Assert.True(again.Engine.Saves.Save("slot2"));   // and a save of the loaded game still names her
        Assert.Contains(witchId.ToString(), File.ReadAllText(Path.Combine(saves, "slot2", $"world_{loaded.Name}.json")));

        Assert.True(again.Engine.Scenes.Load(loaded, Game("camp")));
        loaded.RunFixed(1f / 60f);
        Assert.Equal(witchId, loaded.Get<Persistent>(loaded.Resolve(source)).Id);
    }
}
