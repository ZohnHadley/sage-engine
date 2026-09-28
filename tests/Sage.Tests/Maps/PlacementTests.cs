#nullable enable
using System.Numerics;
using Friflo.Engine.ECS;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// Placement documents (docs/design/15 §3, TODO F28): what stands where, in a form something other than
// the game can read and write.
//
// The property that matters is the round trip. An editor's whole job is to write a file a game will
// load, so "spawn it, move it, read it back" has to give the same document with the change in it — and
// the two conversions between absolute metres and origin space are where that goes wrong.
public class PlacementTests
{
    public PlacementTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
        [
          { "type": "prefab", "id": "post", "components": { "Transform": {} } },
          {
            "type": "placements",
            "id": "yard",
            "place": [
              { "prefab": "post", "at": [10, 0, -4], "yaw": 90, "name": "north post" },
              { "prefab": "post", "at": [12, 1, -4] }
            ]
          }
        ]
        """;

    private static Engine NewEngine()
    {
        var fixture = new MountFixture();
        fixture.Write("game", "data/yard.json", Records);
        fixture.Mount("game", "sandbox");

        return HeadlessApp.Bare().With(new PhysicsModule()).Mount(fixture).Build().Engine;
    }

    [Fact]
    public void ADocumentPutsItsPlacementsInTheWorld()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("edit");

        int spawned = world.SpawnPlacements(new RecordId("sandbox", "yard"));

        Assert.Equal(2, spawned);
        Assert.Equal(2, world.Query<FromPlacements>().Count);

        var named = default(Entity);
        foreach (var entity in world.Query<Transform, FromPlacements>().Entities)
            if (entity.Name.value == "north post") named = entity;

        Assert.False(named.IsNull);
        Assert.Equal(new Vector3(10, 0, -4), named.GetComponent<Transform>().LocalPosition);
    }

    [Fact]
    public void WhatTheWorldSaysIsWhatTheDocumentGetsBack()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("edit");
        var id = new RecordId("sandbox", "yard");
        world.SpawnPlacements(id);

        // Move one, the way an editor would.
        foreach (var entity in world.Query<Transform, FromPlacements>().Entities)
            if (entity.Name.value == "north post")
                entity.GetComponent<Transform>().LocalPosition = new Vector3(3, 2, 1);

        var saved = world.ReadPlacements(id);

        Assert.Equal(2, saved.Place.Count);
        var moved = saved.Place.Find(p => p.Name == "north post");
        Assert.NotNull(moved);
        Assert.Equal(new Vector3(3, 2, 1), moved!.At);
        Assert.Equal(new RecordId("sandbox", "post"), moved.Prefab);
    }

    [Fact]
    public void ADocumentCanBeClosedAndOpenedAgainWithoutDoubling()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("edit");
        var id = new RecordId("sandbox", "yard");

        world.SpawnPlacements(id);
        int removed = world.ClearPlacements(id);
        world.SpawnPlacements(id);

        // The editor opens, closes and reopens documents all day, and a file that leaves its last copy
        // behind is the bug F16's levels had: two of everything, in the same place, invisibly.
        Assert.Equal(2, removed);
        Assert.Equal(2, world.Query<FromPlacements>().Count);
    }

    [Fact]
    public void APlacementNamingAPrefabThatIsNotThereCostsItsOwnLine()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("edit");

        var document = new PlacementsRecord();
        document.Place.Add(new Placement { Prefab = new RecordId("sandbox", "post"), At = Vector3.Zero });
        document.Place.Add(new Placement { Prefab = new RecordId("sandbox", "ghost"), At = Vector3.One });

        int spawned = world.SpawnPlacements(new RecordId("sandbox", "ad_hoc"), document);

        Assert.Equal(1, spawned);
    }
}
