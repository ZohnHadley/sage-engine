#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Off-screen simulation, the rest of it (issue 4m-10): an agent walks round walls by the content's coarse
// graph (doors and nav links are ways through), an NPC in a sector never placed is in the table from the
// start and at its routine's place, a `.map`'s targetnames are anchors, and a live NPC whose anchor is not in
// the world walks to where the content has it — through the door, when it is in another scene.
public class OffscreenRoutesTests
{
    public OffscreenRoutesTests() { _ = TestEnv.UserRoot; }

    private const float S = Terrain.SectorSize;

    // A walled town 40 m square round (0, 0) of the scene `town`, its one gate in the south wall; a well in
    // the middle. A shed from a `.map` stands at (100, 0, 0), a targetname at its door. The valley streams:
    // the player starts in (0, 0); a farmer is placed in (6, 0), which nobody has visited, and works a field
    // 300 m from where he was put.
    private const string Content = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "invulnerable": "state.invulnerable" },
         { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "tag", "id": "state.dead" },
         { "type": "tag", "id": "state.invulnerable" },
         { "type": "ai_schedule", "id": "keep", "tasks": ["MoveToAnchor", "StayAt"] },
         { "type": "routine", "id": "to_the_well", "entries": [ { "from": 0, "to": 24, "schedule": "keep", "at": "well" } ] },
         { "type": "routine", "id": "farmer", "entries": [ { "from": 0, "to": 24, "schedule": "keep", "at": "field" } ] },

         { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"] },
         { "type": "prefab", "id": "marker", "name": "marker" },
         { "type": "prefab", "id": "wall_ns", "name": "wall", "parts": { "body": { "size": [40, 3, 1] } } },
         { "type": "prefab", "id": "wall_ew", "name": "wall", "parts": { "body": { "size": [1, 3, 40] } } },
         { "type": "prefab", "id": "wall_half", "name": "wall", "parts": { "body": { "size": [19, 3, 1] } } },
         { "type": "prefab", "id": "gate", "name": "gate", "parts": { "body": { "size": [2, 3, 0.3] }, "mover": { "open": [0, 3, 0], "seconds": 2 } } },
         { "type": "prefab", "id": "farmer", "name": "farmer",
           "parts": { "attributes": {}, "routine": "farmer", "offscreen": { "speed": 2 } } },
         { "type": "map", "id": "shed", "file": "maps/shed.map", "at": [100, 0, 0] },

         { "type": "scene", "id": "valley", "streamed": true,
           "player": { "prefab": "hero", "at": [20, 1, 20] },
           "place": [ { "prefab": "marker", "at": [30, 0, 30], "name": "home" },
                      { "prefab": "farmer", "at": [6154, 0, 10], "name": "farmer", "id": "farmer" },
                      { "prefab": "marker", "at": [6444, 0, 200], "name": "field" } ] },
         { "type": "scene", "id": "town", "maps": ["shed"],
           "place": [ { "prefab": "wall_ns", "at": [0, 1.5, 20] },
                      { "prefab": "wall_half", "at": [-10.5, 1.5, -20] },
                      { "prefab": "wall_half", "at": [10.5, 1.5, -20] },
                      { "prefab": "gate", "at": [0, 1.5, -20], "name": "gate" },
                      { "prefab": "wall_ew", "at": [20, 1.5, 0] },
                      { "prefab": "wall_ew", "at": [-20, 1.5, 0] },
                      { "prefab": "marker", "at": [0, 0, 0], "name": "well" } ] }]
        """;

    // The shed: a box 4 m by 3 m by 6 m high-ish from (-2, 0, -2) to (2, 3, 2) of the map, and a target at its door.
    private static string Shed() =>
        NavMeshTests.Map(new[] { NavMeshTests.Brush(-2, 0, -2, 2, 3, 2) }, NavMeshTests.Point("info_target", new Vector3(0, 0, 3), "shed_door"));

    private static HeadlessApp Boot(string saves)
    {
        using var log = new CaptureSink();
        var app = HeadlessApp.Bare().With(new PhysicsModule(), new StreamingModule(), new MapModule()).WithGameplay()
            .File("data/routes.json", Content).File("maps/shed.map", Shed()).StartScene("sage:valley").Boot();
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", log.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message)));
        app.Engine.Saves.Root = saves;
        app.CVars.Execute("save_autosave 0");
        var clock = WorldClock.Of(app.World);
        clock.Scale = 0;
        clock.Hour = 7;
        Tick(app.World);
        return app;
    }

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    private static RecordId Id(string name) => new("sage", name);

    private static Entity Hero(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    private static Entity[] Named(World world, string name) =>
        world.QueryAll().Entities.ToEntityList().Where(e => e.Name == name).ToArray();

    private static void Walk(World world, float x, float z)
    {
        world.Get<Transform>(Hero(world)).LocalPosition = world.Origin().ToOrigin(new Vector3(x, 1, z));
        Tick(world, 2);
    }

    private static OffscreenAgents Table(World world) => world.Resources.Get<OffscreenAgents>();

    private static float FlatDistance(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));

    // The done criterion's first half: an agent outside the north wall of a walled town whose anchor is the
    // well inside walks round the wall and in through the gate, never through a wall; and gets there.
    [Xunit.Fact]
    public void AnAgentCrossesAWalledTownByItsGate()
    {
        using var app = Boot(TestEnv.NewTempDir());
        var world = app.World;
        var map = OffscreenMap.Of(world);
        var town = Id("town");
        Assert.True(map.TryAnchor(town, "well", out var well));
        var start = new Vector3(0, 0, 60);
        Assert.True(map.Blocked(town, start, well), "the north wall is not in the way");
        Assert.False(map.Blocked(town, new Vector3(0, 0, -40), well), "the gate is a wall");

        var table = Table(world);
        var agent = new OffscreenAgent { Id = PersistentId.FromName("walker").ToString(), Name = "walker", Scene = town,
                                         X = start.X, Y = start.Y, Z = start.Z, Routine = Id("to_the_well"), Speed = 1.4f };
        table.Add(agent);
        table.Minute = 12 * 60;
        var system = new OffscreenSystem(world, app.Records, budget: null);
        try
        {
            var calendar = Calendars.Of(world);
            system.Step(world, table, map, calendar);
            Assert.NotEmpty(agent.Route);
            var route = new List<Vector3> { start };
            route.AddRange(agent.Route);
            Assert.Equal(well, route[^1]);

            var walked = new List<Vector3> { start, new((float)agent.X, (float)agent.Y, (float)agent.Z) };
            for (int i = 1; i < route.Count; i++)
                Assert.False(map.Blocked(town, i == 1 ? walked[1] : route[i - 1], route[i]), $"leg {i} of {string.Join(" ", route)} goes through a wall");
            // In through the gate: the one leg that crosses the south wall's line inside the town's width
            // crosses it in the gateway.
            var crossings = new List<float>();
            for (int i = 1; i < route.Count; i++)
            {
                var (a, b) = (i == 1 ? walked[1] : route[i - 1], route[i]);
                if ((a.Z + 20f) * (b.Z + 20f) < 0f && MathF.Abs(a.X + (b.X - a.X) * (-20f - a.Z) / (b.Z - a.Z)) < 20.5f) crossings.Add(a.X + (b.X - a.X) * (-20f - a.Z) / (b.Z - a.Z));
            }
            float x = Assert.Single(crossings);
            Assert.True(MathF.Abs(x) < 1f, $"it crossed the south wall at x = {x}");

            for (int i = 0; i < 10 && FlatDistance(new Vector3((float)agent.X, 0, (float)agent.Z), well) > 0.01f; i++)
                system.Step(world, table, map, calendar);
            Assert.True(FlatDistance(new Vector3((float)agent.X, 0, (float)agent.Z), well) < 0.01f, $"it is at {agent}");
            Assert.Empty(agent.Route);
        }
        finally { system.Dispose(); }
    }

    // A `.map` a scene places: its targetnames are anchors and its brushes walls, though it is not loaded.
    [Xunit.Fact]
    public void AMapsTargetnamesAreAnchorsAndItsBrushesWalls()
    {
        using var app = Boot(TestEnv.NewTempDir());
        var map = OffscreenMap.Of(app.World);
        Assert.True(map.TryAnchor(Id("town"), "shed_door", out var door));
        Assert.True(FlatDistance(door, new Vector3(100, 0, 3)) < 0.01f, $"the shed's door is at {door}");
        Assert.True(map.Blocked(Id("town"), new Vector3(95, 0, 0), new Vector3(105, 0, 0)), "the shed is not in the way");
        Assert.False(map.Blocked(Id("town"), new Vector3(95, 0, 5), new Vector3(105, 0, 5)), "beside the shed is not clear");
    }

    // The done criterion's second half: the farmer in a sector the player has never been near is in the table
    // from the start, at his field; walking there finds him at it, once, and never twice — across a walk away
    // and back, and a save and a load.
    [Xunit.Fact]
    public void ANeverVisitedSectorsNpcAppearsAtItsRoutinePosition()
    {
        string saves = TestEnv.NewTempDir();
        using var app = Boot(saves);
        var world = app.World;
        var field = new Vector3(6444, 0, 200);
        Tick(world);                                          // the ring is known at the first tick's end

        var agent = Assert.Single(Table(world).Agents, a => a.Name == "farmer");
        Assert.Equal(6444.0, agent.X, 3);
        Assert.Equal(200.0, agent.Z, 3);
        Assert.Empty(Named(world, "farmer"));
        string id = agent.Id;

        Walk(world, 6 * S + 20, 20);
        var farmer = Assert.Single(Named(world, "farmer"));
        Assert.Equal(id, farmer.GetComponent<Persistent>().Id.ToString());
        Assert.True(FlatDistance(world.Origin().ToAbsolute(world.Get<Transform>(farmer).LocalPosition), field) < 0.01f);
        Assert.Equal(ContentIds.SectorSource(Id("valley"), new SectorCoord(6, 0)), world.Get<InCell>(farmer).Source);
        Assert.DoesNotContain(Table(world).Agents, a => a.Name == "farmer");

        Walk(world, 20, 20);
        Assert.Empty(Named(world, "farmer"));
        Assert.Single(Table(world).Agents, a => a.Name == "farmer");
        Assert.True(app.Engine.Saves.Save("home"));

        Walk(world, 6 * S + 20, 20);
        Assert.Single(Named(world, "farmer"));
        Assert.True(app.Engine.Saves.Load("home"));
        Tick(world);
        Assert.Empty(Named(world, "farmer"));
        Assert.Single(Table(world).Agents, a => a.Name == "farmer");
        Walk(world, 6 * S + 20, 20);
        farmer = Assert.Single(Named(world, "farmer"));
        Assert.True(FlatDistance(world.Origin().ToAbsolute(world.Get<Transform>(farmer).LocalPosition), field) < 0.01f);
        Assert.DoesNotContain(Table(world).Agents, a => a.Name == "farmer");
    }
}

// A live NPC whose anchor is not in the world (4m-10): one that is a map's targetname with no prefab is
// walked to where the map has it; one in another scene is walked to the door there, and through it.
public class FarAnchorTests
{
    public FarAnchorTests() { _ = TestEnv.UserRoot; }

    private const string Content = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "invulnerable": "state.invulnerable",
           "aiProfile": "default_ai", "schedules": { "idle": "idle", "chase": "chase", "meleeAttack": "chase", "castSpell": "idle", "holdGround": "idle" } },
         { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "tag", "id": "state.dead" },
         { "type": "tag", "id": "state.invulnerable" },
         { "type": "ai_profile", "id": "default_ai", "sightRange": 25, "meleeRange": 1.8, "thinkRate": 20 },
         { "type": "ai_schedule", "id": "idle", "tasks": [{ "task": "Wait", "seconds": 1.5 }], "interrupts": ["SeeEnemy", "in_routine"] },
         { "type": "ai_schedule", "id": "chase", "tasks": [{ "task": "MoveToTarget", "distance": 1.6 }], "interrupts": ["LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "keep_routine", "tasks": ["MoveToAnchor", "FaceAnchor", "StayAt"], "interrupts": ["SeeEnemy"] },

         { "type": "routine", "id": "watch", "entries": [ { "from": 0, "to": 24, "schedule": "keep_routine", "at": "lookout" } ] },
         { "type": "routine", "id": "cellar_keeper",
           "entries": [ { "from": 0, "to": 24, "schedule": "keep_routine", "at": "barrel", "scene": "cellar" } ] },

         { "type": "prefab", "id": "ground", "name": "ground", "parts": { "body": { "size": [400, 1, 400] } } },
         { "type": "prefab", "id": "marker", "name": "marker" },
         { "type": "prefab", "id": "cellar_door", "name": "cellar_door", "parts": { "load_door": { "scene": "cellar", "entry": "barrel" } } },
         { "type": "prefab", "id": "watchman", "name": "watchman", "components": { "ai_state": {} },
           "parts": { "character": { "layer": "enemy" }, "routine": "watch" } },
         { "type": "prefab", "id": "keeper", "name": "keeper", "components": { "ai_state": {} },
           "parts": { "character": { "layer": "enemy" }, "attributes": {}, "routine": "cellar_keeper", "offscreen": {} } },
         { "type": "map", "id": "yard", "file": "maps/yard.map" },

         { "type": "scene", "id": "town", "maps": ["yard"],
           "place": [ { "prefab": "ground", "at": [0, -0.5, 0], "name": "ground" },
                      { "prefab": "cellar_door", "at": [5, 0, 10], "name": "cellar_door" },
                      { "prefab": "watchman", "at": [-5, 0.1, -5], "name": "watchman", "id": "watchman" },
                      { "prefab": "keeper", "at": [5, 0.1, 20], "name": "keeper", "id": "keeper" } ] },
         { "type": "scene", "id": "cellar", "place": [ { "prefab": "marker", "at": [3, -4, 2], "name": "barrel" } ] }]
        """;

    private static readonly Vector3 Lookout = new(-5, 0, -15);

    private static HeadlessApp Boot()
    {
        using var log = new CaptureSink();
        var app = HeadlessApp.Gameplay().With(new MapModule()).File("data/far.json", Content)
            .File("maps/yard.map", NavMeshTests.Map(Array.Empty<string>(), NavMeshTests.Point("info_target", Lookout, "lookout")))
            .StartScene("sage:town").Boot("town");
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", log.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message)));
        var clock = WorldClock.Of(app.World);
        clock.Scale = 0;
        clock.Hour = 7;
        return app;
    }

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    // The lookout is a targetname with no prefab: nothing of it is in the world, and he walks to it anyway.
    [Xunit.Fact]
    public void ALiveNpcWalksToAnAnchorThatIsOnlyInTheContent()
    {
        using var app = Boot();
        var world = app.World;
        Assert.True(world.FindByName("lookout").IsNull);
        var watchman = world.FindByName("watchman");
        Tick(world, 60 * 15);

        var state = world.Get<AIState>(watchman);
        Assert.True(((AICondition)state.Conditions).HasFlag(AICondition.InRoutine));
        Assert.True(state.AnchorFar);
        float off = SageMath.DistanceXZ(world.Get<Transform>(watchman).LocalPosition, world.Origin().ToOrigin(Lookout));
        Assert.True(off < 1.5f, $"he is {off:F1} m from the lookout");
    }

    // The barrel is in the cellar: he walks to the cellar door in view, and goes through it — out of this
    // world and into the off-screen table on the other side, at the door's entry.
    [Xunit.Fact]
    public void ALiveNpcWhoseAnchorIsInAnotherSceneGoesThroughTheDoor()
    {
        using var app = Boot();
        var world = app.World;
        var keeper = world.FindByName("keeper");
        var id = keeper.GetComponent<Persistent>().Id;
        for (int i = 0; i < 60 * 20 && world.IsAlive(keeper); i++) Tick(world);

        Assert.False(world.IsAlive(keeper), "he never went through the door");
        Assert.True(world.FindByName("keeper").IsNull);
        var agent = Assert.Single(world.Resources.Get<OffscreenAgents>().Agents, a => a.Name == "keeper");
        Assert.Equal(id.ToString(), agent.Id);
        Assert.Equal(new RecordId("sage", "cellar"), agent.Scene);
        Assert.Equal(3.0, agent.X, 3);
        Assert.Equal(-4.0, agent.Y, 3);
        Assert.Equal(2.0, agent.Z, 3);
    }
}
