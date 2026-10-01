#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Off-screen simulation, A-Life-lite (issue 4g-6, REDESIGN §5 phase 4g). An NPC with the `offscreen` part
// leaves the world with its cell and becomes an agent in the saved `offscreen` table; a step a game minute
// moves it toward its routine's anchor and settles fights between hostile agents; it comes back where it
// is when that scene and sector are live again.
//
// The valley streams by sector: the player starts in (0, 0); the worker sleeps at "home" there and works at
// "work" in (2, 0); three guards and three bandits stand within a few metres of each other in (5, 0).
public class OffscreenTests
{
    public OffscreenTests() { _ = TestEnv.UserRoot; }

    private const float S = Terrain.SectorSize;

    private const string Content = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "invulnerable": "state.invulnerable" },
         { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "tag", "id": "state.dead" },
         { "type": "tag", "id": "state.invulnerable" },
         { "type": "faction", "id": "guards", "relations": [ { "faction": "bandits", "stance": "Hostile" } ] },
         { "type": "faction", "id": "bandits" },
         { "type": "faction", "id": "traders" },
         { "type": "ai_schedule", "id": "keep", "tasks": ["MoveToAnchor", "StayAt"] },
         { "type": "routine", "id": "worker",
           "entries": [ { "from": 8, "to": 20, "schedule": "keep", "at": "work" },
                        { "from": 20, "to": 8, "schedule": "keep", "at": "home" } ] },
         { "type": "routine", "id": "cellar_keeper",
           "entries": [ { "from": 0, "to": 24, "schedule": "keep", "at": "barrel", "scene": "cellar" } ] },

         { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"] },
         { "type": "prefab", "id": "marker", "name": "marker" },
         { "type": "prefab", "id": "worker", "name": "worker",
           "parts": { "attributes": {}, "routine": "worker", "offscreen": { "speed": 2 } } },
         { "type": "prefab", "id": "guard", "name": "guard", "components": { "faction": { "id": "guards" } },
           "parts": { "attributes": {}, "offscreen": { "strength": 8 } } },
         { "type": "prefab", "id": "bandit", "name": "bandit", "components": { "faction": { "id": "bandits" } },
           "parts": { "attributes": {}, "offscreen": { "strength": 5, "corpse": false } } },
         { "type": "prefab", "id": "stone", "name": "stone" },

         { "type": "scene", "id": "valley", "streamed": true,
           "player": { "prefab": "hero", "at": [20, 1, 20] },
           "place": [ { "prefab": "marker", "at": [30, 0, 30], "name": "home" },
                      { "prefab": "worker", "at": [30, 0, 30], "name": "worker", "id": "worker" },
                      { "prefab": "stone", "at": [40, 0, 40], "name": "stone" },
                      { "prefab": "marker", "at": [2088, 0, 40], "name": "work" },
                      { "prefab": "guard", "at": [5140, 0, 30], "name": "guard", "id": "guard1" },
                      { "prefab": "guard", "at": [5142, 0, 32], "name": "guard", "id": "guard2" },
                      { "prefab": "guard", "at": [5144, 0, 30], "name": "guard", "id": "guard3" },
                      { "prefab": "bandit", "at": [5146, 0, 31], "name": "bandit", "id": "bandit1" },
                      { "prefab": "bandit", "at": [5148, 0, 30], "name": "bandit", "id": "bandit2" },
                      { "prefab": "bandit", "at": [5150, 0, 32], "name": "bandit", "id": "bandit3" } ] },
         { "type": "scene", "id": "outpost",
           "place": [ { "prefab": "marker", "at": [0, 0, 0], "name": "home" },
                      { "prefab": "marker", "at": [600, 0, 0], "name": "work" } ] },
         { "type": "scene", "id": "cellar", "place": [ { "prefab": "marker", "at": [3, -4, 2], "name": "barrel" } ] }]
        """;

    private static HeadlessApp Boot(string saves)
    {
        using var log = new CaptureSink();
        var app = HeadlessApp.Bare().With(new PhysicsModule(), new StreamingModule()).WithGameplay()
            .File("data/offscreen.json", Content).StartScene("sage:valley").Boot();
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", log.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message)));
        app.Engine.Saves.Root = saves;
        app.CVars.Execute("save_autosave 0");
        var clock = WorldClock.Of(app.World);
        clock.Scale = 0;          // the test passes the time
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

    private static Vector3 Absolute(World world, Entity entity) => world.Origin().ToAbsolute(world.Get<Transform>(entity).LocalPosition);

    // The player to absolute (x, z), and two ticks: the ring and the sectors move at the first's end, and
    // the off-screen system brings back what is in a sector placed then at the start of the second.
    private static void Walk(World world, float x, float z)
    {
        world.Get<Transform>(Hero(world)).LocalPosition = world.Origin().ToOrigin(new Vector3(x, 1, z));
        Tick(world, 2);
    }

    private static OffscreenAgents Table(World world) => world.Resources.Get<OffscreenAgents>();

    private static void AssertNoIdTwice(World world)
    {
        var ids = world.Query<Persistent>().Entities.ToEntityList().Select(e => e.GetComponent<Persistent>().Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        var away = Table(world).Agents.Select(a => a.Id).ToList();
        Assert.Equal(away.Count, away.Distinct().Count());
        Assert.DoesNotContain(away, id => !world.Resolve(PersistentId.TryParse(id, out var p) ? p : default).IsNull);
    }

    // What the squads' fight left: every agent's name, id and health, in id order.
    private static string Outcome(World world) =>
        string.Join("; ", Table(world).Agents.Where(a => a.Faction == Id("guards") || a.Faction == Id("bandits"))
            .OrderBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => $"{a.Name} {a.Id[..6]} {a.Health:0.###}{(a.Dead ? " dead" : "")}"));

    // The squads leave the world with their sector: the player visits (5, 0) and comes home.
    private static void SendTheSquadsOffscreen(World world)
    {
        Walk(world, 5 * S + 20, 20);
        Assert.Equal(3, Named(world, "guard").Length);
        Walk(world, 20, 20);
        Assert.Empty(Named(world, "guard"));
        Assert.Equal(6, Table(world).Agents.Count(a => a.Name is "guard" or "bandit"));
    }

    // The plan's first test: two hostile squads in an unloaded sector; two hours later one side is dead,
    // and the result is the same on two runs, and across a save and a load half way.
    [Xunit.Fact]
    public void TwoHostileSquadsOffscreenFightItOutTheSameWayEveryTime()
    {
        string Straight()
        {
            using var app = Boot(TestEnv.NewTempDir());
            var world = app.World;
            SendTheSquadsOffscreen(world);
            Assert.True(Time.Pass(world, 2, "wait"));
            Tick(world);
            return Outcome(world);
        }

        string first = Straight(), second = Straight();
        Assert.Equal(first, second);

        string saves = TestEnv.NewTempDir();
        using (var app = Boot(saves))
        {
            SendTheSquadsOffscreen(app.World);
            Assert.True(Time.Pass(app.World, 1, "wait"));
            Tick(app.World);
            Assert.True(app.Engine.Saves.Save("half"));
        }
        string loaded;
        using (var app = Boot(saves))
        {
            var world = app.World;
            Assert.True(app.Engine.Saves.Load("half"));
            Tick(world);
            Assert.Empty(Named(world, "guard"));            // still away: (5, 0) is not live
            Assert.True(Time.Pass(world, 1, "wait"));
            Tick(world);
            loaded = Outcome(world);

            // One side is dead: the bandits leave no corpse, so they are gone; the guards that fell are corpses.
            var squads = Table(world).Agents.Where(a => a.Name is "guard" or "bandit").ToList();
            bool guardsStand = squads.Any(a => a.Name == "guard" && !a.Dead), banditsStand = squads.Any(a => a.Name == "bandit" && !a.Dead);
            Assert.True(guardsStand ^ banditsStand, $"both sides or neither still stand: {loaded}");
            Assert.DoesNotContain(squads, a => a.Name == "bandit" && a.Dead);

            // And back in the world as the table has them: the living with what the fight left them, the
            // dead as corpses, the bandits nowhere.
            var standing = squads.Where(a => a.Name == "guard").ToDictionary(a => a.Id, a => (a.Health, a.Dead));
            Walk(world, 5 * S + 20, 20);
            Assert.Equal(standing.Count, Named(world, "guard").Length);
            Assert.Equal(squads.Count(a => a.Name == "bandit"), Named(world, "bandit").Length);
            var dead = Id("state.dead");
            foreach (var guard in Named(world, "guard"))
            {
                var (health, isDead) = standing[guard.GetComponent<Persistent>().Id.ToString()];
                Assert.Equal(health, world.Attribute(guard, Id("health")));
                Assert.Equal(isDead, world.HasTag(guard, dead));
                Assert.Equal(ContentIds.SectorSource(Id("valley"), new SectorCoord(5, 0)), world.Get<InCell>(guard).Source);
            }
            Assert.DoesNotContain(Table(world).Agents, a => a.Name is "guard" or "bandit");
            AssertNoIdTwice(world);
        }
        Assert.Equal(first, loaded);
    }

    // The plan's second test: an NPC walks from (0, 0) to (2, 0) while unloaded and appears in (2, 0), not
    // (0, 0) — once, at its work, a runtime spawn of the sector it walked into.
    [Xunit.Fact]
    public void AnNpcThatWalkedWhileUnloadedAppearsWhereItWalkedTo()
    {
        using var app = Boot(TestEnv.NewTempDir());
        var world = app.World;
        var worker = Assert.Single(Named(world, "worker"));
        var id = worker.GetComponent<Persistent>().Id;

        Walk(world, 5 * S + 20, 20);                          // (0, 0) and (2, 0) dormant: he is off-screen
        var agent = Assert.Single(Table(world).Agents, a => a.Name == "worker");
        Assert.Equal(Id("worker"), agent.Routine);
        Assert.Equal(30.0, agent.X, 3);

        Assert.True(Time.Pass(world, 3, "wait"));            // 10:00: at work since 08:00, 2 km away at 2 m/s
        Tick(world);
        Assert.Equal(2088.0, agent.X, 3);
        Assert.Equal(40.0, agent.Z, 3);

        Walk(world, 2 * S + 20, 20);                          // (2, 0) comes back, and he with it
        var back = Assert.Single(Named(world, "worker"));
        Assert.Equal(id, back.GetComponent<Persistent>().Id);
        var at = Absolute(world, back);
        Assert.True(Vector2.Distance(new Vector2(at.X, at.Z), new Vector2(2088, 40)) < 0.01f, $"he is at {at}");
        Assert.Equal(ContentIds.SectorSource(Id("valley"), new SectorCoord(2, 0)), world.Get<InCell>(back).Source);
        Assert.Null(Table(world).Find(id));
        Assert.Equal(100f, world.Attribute(back, Id("health")));

        Walk(world, 20, 20);                                  // home: (0, 0) placed again, without him
        Assert.Single(Named(world, "stone"));
        back = Assert.Single(Named(world, "worker"));         // still at work: (2, 0) is inside the ring's hysteresis
        Assert.Equal(2088f, Absolute(world, back).X, 2);
        AssertNoIdTwice(world);

        Walk(world, -3 * S + 20, 20);                         // and (2, 0) dormant again: he is away with it
        Assert.Empty(Named(world, "worker"));
        Assert.NotNull(Table(world).Find(id));
        AssertNoIdTwice(world);
    }

    // An anchor in another scene is reached through a door: to the door of its scene, out at the entry of
    // the other, and on to the anchor there.
    [Xunit.Fact]
    public void AnAgentChangesSceneThroughADoor()
    {
        using var app = Boot(TestEnv.NewTempDir());
        var world = app.World;
        var map = OffscreenMap.Of(world);
        Assert.True(map.TryAnchor(Id("cellar"), "barrel", out var barrel));
        Assert.Equal(new Vector3(3, -4, 2), barrel);
        map.AddDoor(Id("outpost"), new Vector3(100, 0, 0), Id("cellar"), new Vector3(0, -4, 0));

        var table = Table(world);
        table.Add(new OffscreenAgent { Id = PersistentId.FromName("keeper").ToString(), Name = "keeper", Scene = Id("outpost"),
                                       Routine = Id("cellar_keeper"), Speed = 1f });
        Tick(world);
        Assert.True(Time.Pass(world, 1, "wait"));             // 100 m to the door, then 3.6 m to the barrel
        Tick(world);
        var keeper = Assert.Single(table.Agents, a => a.Name == "keeper");
        Assert.Equal(Id("cellar"), keeper.Scene);
        Assert.Equal(3.0, keeper.X, 3);
        Assert.Equal(-4.0, keeper.Y, 3);
        Assert.Equal(2.0, keeper.Z, 3);
    }

    // The plan's third test: 500 agents step without allocating — moving by their routines, and the fight
    // sweep looking at every neighbour (traders and guards are not enemies).
    [Xunit.Collection(MeasurementsCollection.Name)]
    public class Measured
    {
        public Measured() { _ = TestEnv.UserRoot; }

        [Xunit.Fact]
        public void FiveHundredAgentsStepWithoutAllocating()
        {
            using var app = Boot(TestEnv.NewTempDir());
            var world = app.World;
            var table = Table(world);
            for (int i = 0; i < 500; i++)
                table.Add(new OffscreenAgent
                {
                    Id = PersistentId.FromName($"agent{i}").ToString(), Name = "agent", Scene = Id("outpost"),
                    X = i * 2.5, Z = (i % 7) * 3, Faction = Id(i % 2 == 0 ? "traders" : "guards"),
                    Routine = Id("worker"), Speed = 0.01f, Strength = 1, Health = 100,
                });
            table.Minute = 9 * 60;                            // 09:00: everyone walks to work
            var system = new OffscreenSystem(world, app.Records, budget: null);
            try
            {
                var map = OffscreenMap.Of(world);
                var calendar = Calendars.Of(world);
                double before = table.Agents[0].X;
                for (int i = 0; i < 20; i++) system.Step(world, table, map, calendar);   // warm
                AllocationProbe.AssertNone(200, () => system.Step(world, table, map, calendar));
                Assert.Equal(500, table.Agents.Count);
                Assert.True(table.Agents[0].X > before, "nobody walked");
                Assert.DoesNotContain(table.Agents, a => a.Dead);
            }
            finally { system.Dispose(); }
        }
    }
}
