#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Runtime spawns persist by default (phase 4i, issue 4i-4): a thing the game made while playing is part
// of the world a save keeps, unless its prefab says `"persist": false`; the children a prefab placed
// come back from their parent rather than from ids of their own.
public class RuntimeSpawnSaveTests
{
    public RuntimeSpawnSaveTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
    [
      { "type": "attribute", "id": "health", "max": 100, "start": 100 },
      { "type": "item", "id": "bread", "label": "bread", "weight": 1 },
      { "type": "effect", "id": "call", "executions": [{ "execution": "summon", "prefab": "imp", "count": 2 }] },
      { "type": "prefab", "id": "hero", "name": "hero",
        "parts": { "character": { "layer": "player" }, "attributes": {}, "inventory": { "capacity": 40 } } },
      { "type": "prefab", "id": "imp", "name": "imp", "parts": { "character": { "layer": "enemy" }, "attributes": {} } },
      { "type": "prefab", "id": "crate", "name": "crate", "components": { "sprite_renderer": { "size": [1.0, 1.0] } } },
      { "type": "prefab", "id": "spark", "name": "spark", "persist": false },
      { "type": "prefab", "id": "lamp", "name": "lamp", "parts": { "light": { "range": 4 } } },
      { "type": "prefab", "id": "cart", "name": "cart",
        "children": [ { "prefab": "lamp", "at": [0, 1, 0] }, { "prefab": "lamp", "at": [1, 1, 0] } ] }
    ]
    """;

    private static RecordId Id(string name) => new("sage", name);

    private static (HeadlessApp App, string Root) Boot()
    {
        var app = HeadlessApp.Gameplay().WithHands().File("data/spawns.json", Records).Boot("spawns");
        var root = TestEnv.NewTempDir();
        app.Engine.Saves.Root = Path.Combine(root, "saves");
        return (app, root);
    }

    private static void Finish(HeadlessApp app, string root)
    {
        app.Dispose();
        try { Directory.Delete(root, recursive: true); } catch { /* a leftover temp dir is not a failure */ }
    }

    private static int Count(World world, string prefab) =>
        world.Query<Sage.Simulation.FromPrefab>().Entities.Count(e => world.Get<Sage.Simulation.FromPrefab>(e).Prefab == Id(prefab));

    private static void SaveAndReload(HeadlessApp app)
    {
        Assert.True(app.Engine.Saves.Save("slot"));
        Assert.True(app.Engine.Saves.Load("slot"));
        app.World.RunFixed(1f / 60f);
    }

    // test: ADroppedItemSurvivesASave
    [Xunit.Fact]
    public void ADroppedItemSurvivesASave()
    {
        var (app, root) = Boot();
        try
        {
            var world = app.World;
            var hero = world.Spawn(Id("hero"), new Vector3(0, 0.1f, 0));
            world.Give(hero, Id("bread"), 3);
            var dropped = world.Drop(hero, Id("bread"), 2);
            Assert.True(world.Has<Persistent>(dropped));
            var at = world.Get<Transform>(dropped).LocalPosition;

            SaveAndReload(app);

            var back = Assert.Single(world.Query<Pickup>().Entities.ToEntityList());
            Assert.Equal(2, world.Get<Pickup>(back).Count);
            Assert.Equal(Id("bread"), world.Get<Pickup>(back).Item);
            Assert.Equal(at, world.Get<Transform>(back).LocalPosition);
        }
        finally { Finish(app, root); }
    }

    // test: ASummonedCreatureSurvivesASave
    [Xunit.Fact]
    public void ASummonedCreatureSurvivesASave()
    {
        var (app, root) = Boot();
        try
        {
            var world = app.World;
            var hero = world.Spawn(Id("hero"), new Vector3(0, 0.1f, 0));
            Effects.Apply(world, hero, Id("call"));
            world.RunFixed(1f / 60f);
            Assert.Equal(2, Count(world, "imp"));

            SaveAndReload(app);

            Assert.Equal(2, Count(world, "imp"));
            Assert.Equal(1, Count(world, "hero"));
        }
        finally { Finish(app, root); }
    }

    // test: APrefabThatSaysPersistFalseIsNotSaved
    [Xunit.Fact]
    public void APrefabThatSaysPersistFalseIsNotSaved()
    {
        var (app, root) = Boot();
        try
        {
            var world = app.World;
            var spark = world.Spawn(Id("spark"), Vector3.Zero);
            var crate = world.Spawn(Id("crate"), Vector3.Zero);
            Assert.False(world.Has<Persistent>(spark));
            Assert.True(world.Has<Persistent>(crate));

            Assert.True(app.Engine.Saves.Save("slot"));

            // What the file holds is what a load brings back: the crate, and not the spark.
            var file = File.ReadAllText(Path.Combine(app.Engine.Saves.Root, "slot", $"world_{world.Name}.json"));
            Assert.Contains("sage:crate", file);
            Assert.DoesNotContain("sage:spark", file);
        }
        finally { Finish(app, root); }
    }

    // test: AnEntSpawnedCrateSurvivesASave
    [Xunit.Fact]
    public void AnEntSpawnedCrateSurvivesASave()
    {
        var (app, root) = Boot();
        try
        {
            app.CVars.Execute("sv_cheats 1");
            app.CVars.Execute("ent_spawn sage:crate 4 0 -2");
            Assert.Equal(1, Count(app.World, "crate"));

            SaveAndReload(app);

            Assert.Equal(1, Count(app.World, "crate"));
        }
        finally { Finish(app, root); }
    }

    // test: APrefabsChildrenAreNotDuplicatedByASaveAndLoad
    [Xunit.Fact]
    public void APrefabsChildrenAreNotDuplicatedByASaveAndLoad()
    {
        var (app, root) = Boot();
        try
        {
            var world = app.World;
            var cart = world.Spawn(Id("cart"), new Vector3(2, 0, 2));
            Assert.True(world.Has<Persistent>(cart));
            // Never a random id of their own: one derived from the cart's and each child's place in it (4i-3).
            var cartId = world.Get<Persistent>(cart).Id;
            Assert.Equal(new[] { ContentIds.Child(cartId, "0:sage:lamp"), ContentIds.Child(cartId, "1:sage:lamp") },
                         cart.ChildEntities.Select(child => world.Get<Persistent>(child).Id).OrderBy(i => i == ContentIds.Child(cartId, "1:sage:lamp")));
            Assert.Equal(2, cart.ChildEntities.Count());

            SaveAndReload(app);
            SaveAndReload(app);

            Assert.Equal(1, Count(world, "cart"));
            Assert.Equal(2, Count(world, "lamp"));
            var loaded = Assert.Single(world.Query<Sage.Simulation.FromPrefab>().Entities.ToEntityList(),
                e => world.Get<Sage.Simulation.FromPrefab>(e).Prefab == Id("cart"));
            Assert.Equal(2, loaded.ChildEntities.Count());
        }
        finally { Finish(app, root); }
    }
}
