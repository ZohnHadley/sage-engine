#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Crowds, ground that costs and ground that is closed (issue #271, docs/design/16 §3.4): creatures steer
// round each other, a road is cheaper than a field, a faction keeps out of a camp, and a body plans on the
// navmesh of its own size.
public class CrowdTests
{
    public CrowdTests() { _ = TestEnv.UserRoot; }

    private const string Brains = """
        { "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "invulnerable": "state.invulnerable",
          "aiProfile": "default_ai", "schedules": { "idle": "idle", "chase": "chase", "meleeAttack": "idle", "castSpell": "idle", "holdGround": "idle" } },
        { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
        { "type": "tag", "id": "state.dead" },
        { "type": "tag", "id": "state.invulnerable" },
        { "type": "ai_profile", "id": "default_ai", "sightRange": 60, "meleeRange": 1.8, "thinkRate": 20, "memorySeconds": 30 },
        { "type": "ai_schedule", "id": "idle", "tasks": [{ "task": "Wait", "seconds": 1.5 }], "interrupts": ["SeeEnemy", "in_routine"] },
        { "type": "ai_schedule", "id": "chase", "tasks": [{ "task": "MoveToTarget", "distance": 1.6 }], "interrupts": ["LostEnemy", "NoEnemy"] },
        { "type": "ai_schedule", "id": "keep_routine", "tasks": ["MoveToAnchor", "StayAt"] }
        """;

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    private static Vector3 At(World world, Entity entity) => world.Get<Transform>(entity).LocalPosition;

    private static Entity Solid(World world, Vector3 min, Vector3 max, string surface = "")
    {
        var solid = world.Create(Transform.At((min + max) * 0.5f), "solid");
        var collider = Collider.Box(max - min);
        if (surface.Length > 0) collider.Surface = new RecordId("sage", surface);
        world.Add(solid, collider);
        return solid;
    }

    // ---- crowds -----------------------------------------------------------------------------------------

    // A corridor 24 m long and 4 m wide, walled both sides, and twenty villagers in it: ten at each end in
    // two files of five, each walking to the far end in its own file. Every one of them meets five others
    // nose to nose.
    private const float HalfLength = 12f, HalfWidth = 2f;

    private static (HeadlessApp App, Entity[] Walkers, Vector3[] Goals) Corridor(bool avoid)
    {
        var records = new StringBuilder("[" + Brains);
        for (int i = 0; i < 20; i++)
            records.Append($$""",{ "type": "routine", "id": "walk{{i}}", "entries": [ { "from": 0, "to": 24, "schedule": "keep_routine", "at": "goal{{i}}" } ] }""");
        records.Append(']');

        var app = HeadlessApp.Gameplay().File("data/crowd.json", records.ToString()).Boot("corridor");
        Assert.Equal(0, app.Records.ErrorCount);
        if (!avoid)
        {
            app.CVars.Execute("sv_cheats 1");
            app.CVars.Execute("nav_avoid 0");
            app.World.Resources.Get<Navigation>().Avoidance = false;
        }

        var world = app.World;
        Solid(world, new Vector3(-30, -1, -30), new Vector3(30, 0, 30));
        Solid(world, new Vector3(-HalfLength, 0, HalfWidth), new Vector3(HalfLength, 2, HalfWidth + 0.5f));
        Solid(world, new Vector3(-HalfLength, 0, -HalfWidth - 0.5f), new Vector3(HalfLength, 2, -HalfWidth));

        var walkers = new Entity[20];
        var goals = new Vector3[20];
        var layer = world.Resources.Get<IPhysicsWorld>().Layers.Enemy;
        for (int i = 0; i < 20; i++)
        {
            bool west = i < 10;
            int rank = (i % 10) / 2, file = i % 2;
            // The front of each group goes furthest, so nobody has to get past somebody already standing
            // at their place: everybody walks 16.2 m, half of it through the other group.
            float sign = west ? -1 : 1;
            float x = sign * (HalfLength - 1.5f - rank * 1.2f);
            float z = file == 0 ? -0.8f : 0.8f;
            var start = new Vector3(x, 0.05f, z);
            goals[i] = new Vector3(-sign * (HalfLength - 1.5f - (4 - rank) * 1.2f), 0, z);

            var goal = world.Create(Transform.At(goals[i]), "goal");
            goal.Name = $"goal{i}";

            var transform = Transform.At(start);
            transform.LocalRotation = SageMath.RotationFromYaw(SageMath.YawTo(start, goals[i]));
            var walker = world.Create(transform, $"walker{i}");
            world.AddCharacter(walker, layer);
            world.Add(walker, new AIState { Schedule = new RecordId("sage", "idle") });
            world.Add(walker, new Routine { Id = new RecordId("sage", $"walk{i}") });
            world.AddAttributes(walker);
            walkers[i] = walker;
        }
        Tick(world);
        return (app, walkers, goals);
    }

    // Runs until every walker is within `reach` of its goal or `seconds` pass. Returns how long it took (or
    // NaN) and the closest any two walkers came, centre to centre, while either was still on its way.
    private static (float Seconds, float Closest) Run(World world, Entity[] walkers, Vector3[] goals, float seconds, float reach = 1f)
    {
        float closest = float.MaxValue;
        var arrived = new bool[walkers.Length];
        for (int tick = 0; tick < seconds * 60; tick++)
        {
            Tick(world);
            var at = walkers.Select(w => At(world, w)).ToArray();
            for (int i = 0; i < at.Length; i++) arrived[i] |= SageMath.DistanceXZ(at[i], goals[i]) <= reach;
            if (arrived.All(a => a)) return ((tick + 1) / 60f, closest);
            for (int i = 0; i < at.Length; i++)
                for (int j = i + 1; j < at.Length; j++)
                    if (!arrived[i] || !arrived[j]) closest = MathF.Min(closest, SageMath.DistanceXZ(at[i], at[j]));
        }
        return (float.NaN, closest);
    }

    // The issue's "done when": twenty creatures converging in a corridor do not deadlock — every one gets to
    // where it is going, in not much more than twice the time a lone walker takes (16.2 m at 4.2 m/s is
    // four seconds) — and they get there round each other, not through each other: two bodies of 0.35 m
    // never close to much less than the 0.7 m that is touching.
    [Fact]
    public void TwentyCreaturesConvergingInACorridorAllGetThroughRoundEachOther()
    {
        var (app, walkers, goals) = Corridor(avoid: true);
        using (app)
        {
            var nav = app.World.Resources.Get<Navigation>();
            var (seconds, closest) = Run(app.World, walkers, goals, 40f);

            var stuck = walkers.Select((w, i) => (i, SageMath.DistanceXZ(At(app.World, w), goals[i]))).Where(p => p.Item2 > 1f).ToArray();
            Assert.False(float.IsNaN(seconds), $"{stuck.Length} walker(s) never arrived: " + string.Join(", ", stuck.Select(p => $"#{p.i} {p.Item2:F1} m short")));
            Assert.True(seconds < 20f, $"it took {seconds:F1} s");
            Assert.True(closest > 0.6f, $"two walkers came within {closest:F2} m of each other, centre to centre");
            Assert.True(nav.Crowd.Steered > 0, "nobody steered round anybody");
        }
    }

    // The A/B: with `nav_avoid 0` the same twenty walk straight through each other (they are on one physics
    // layer, which passes through itself). If this ever keeps them apart, the test above proves nothing.
    [Fact]
    public void WithoutAvoidanceTheSameCrowdWalksThroughItself()
    {
        var (app, walkers, goals) = Corridor(avoid: false);
        using (app)
        {
            var (_, closest) = Run(app.World, walkers, goals, 20f);
            Assert.True(closest < 0.2f, $"without avoidance the closest two came was {closest:F2} m");
            Assert.Equal(0, app.World.Resources.Get<Navigation>().Crowd.Steered);
        }
    }

    // ---- what the ground costs ----------------------------------------------------------------------------

    // A field of grass with a road round three sides of it, made of gravel: from one corner of the road to
    // the next is 24 m across the field or 45 m round by the road. Grass is a field (4 a metre), gravel a
    // road (1), both by what the ground is made of (#270) — nobody marks the road twice.
    private const string Ground = """
        { "type": "physics_material", "id": "grass" },
        { "type": "physics_material", "id": "gravel" }
        """;

    private const string GroundAreas = """
        ,{ "type": "nav_area", "id": "road", "cost": 1, "surfaces": ["gravel"] },
        { "type": "nav_area", "id": "field", "cost": 4, "surfaces": ["grass"] }
        """;

    private static readonly Vector3 RoadStart = new(0, 0.1f, 0), RoadEnd = new(24, 0.1f, 0);

    // The road's three straight pieces, as boxes on the grass (absolute, y = 0 to 0.1).
    private static readonly (Vector3 Min, Vector3 Max)[] Road =
    {
        (new Vector3(-1.5f, 0, -1.5f), new Vector3(1.5f, 0.1f, 12f)),
        (new Vector3(-1.5f, 0, 9f), new Vector3(25.5f, 0.1f, 12f)),
        (new Vector3(22.5f, 0, -1.5f), new Vector3(25.5f, 0.1f, 12f)),
    };

    private static bool OnRoad(Vector3 p, float slack) =>
        Road.Any(r => p.X >= r.Min.X - slack && p.X <= r.Max.X + slack && p.Z >= r.Min.Z - slack && p.Z <= r.Max.Z + slack);

    private static (HeadlessApp App, Entity Walker) RoadWorld(bool areas)
    {
        string records = "[" + Brains + "," + Ground + (areas ? GroundAreas : "") +
            """,{ "type": "routine", "id": "to_market", "entries": [ { "from": 0, "to": 24, "schedule": "keep_routine", "at": "market" } ] }]""";
        var app = HeadlessApp.Gameplay().File("data/road.json", records).Boot("road");
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        Solid(world, new Vector3(-20, -1, -20), new Vector3(45, 0, 30), "grass");
        foreach (var (min, max) in Road) Solid(world, min, max, "gravel");

        var market = world.Create(Transform.At(RoadEnd), "market");
        market.Name = "market";
        var transform = Transform.At(RoadStart);
        transform.LocalRotation = SageMath.RotationFromYaw(SageMath.YawTo(RoadStart, RoadEnd));
        var walker = world.Create(transform, "carter");
        world.AddCharacter(walker, world.Resources.Get<IPhysicsWorld>().Layers.Enemy);
        world.Add(walker, new AIState { Schedule = new RecordId("sage", "idle") });
        world.Add(walker, new Routine { Id = new RecordId("sage", "to_market") });
        world.AddAttributes(walker);
        Tick(world);
        return (app, walker);
    }

    // The issue's other "done when": a creature takes the road round the field rather than the shorter way
    // across it, staying on the road all the way to the market.
    [Fact]
    public void ACreatureTakesTheRoadRoundAFieldRatherThanTheShortWayAcrossIt()
    {
        var (app, walker) = RoadWorld(areas: true);
        using (app)
        {
            var world = app.World;
            float furthest = 0f;
            bool arrived = false;
            Vector3 off = default;
            for (int tick = 0; tick < 30 * 60 && !arrived; tick++)
            {
                Tick(world);
                var at = At(world, walker);
                furthest = MathF.Max(furthest, at.Z);
                if (!OnRoad(at, 1f) && off == default) off = at;
                arrived = SageMath.DistanceXZ(at, RoadEnd) < 1.5f;
            }

            Assert.True(arrived, $"it never got to the market (at {At(world, walker)})");
            Assert.True(off == default, $"it left the road for the field at {off}");
            Assert.True(furthest > 9f, $"it got no further round than z = {furthest:F1}, so it did not take the road");
            Assert.True(world.Resources.Get<Navigation>().MeshPlans > 0, "nothing was planned on the navmesh");
        }
    }

    // The A/B: the same ground with no nav_area records. Nothing costs more than anything else, the line is
    // clear, and it walks straight across the field.
    [Fact]
    public void WithoutAreasTheSameCreatureWalksStraightAcrossTheField()
    {
        var (app, walker) = RoadWorld(areas: false);
        using (app)
        {
            float furthest = 0f;
            for (int tick = 0; tick < 12 * 60; tick++)
            {
                Tick(app.World);
                furthest = MathF.Max(furthest, At(app.World, walker).Z);
            }
            Assert.True(SageMath.DistanceXZ(At(app.World, walker), RoadEnd) < 1.5f, $"it never got there (at {At(app.World, walker)})");
            Assert.True(furthest < 3f, $"it went round (z = {furthest:F1}) with no areas to make it");
        }
    }

    // Water by its volume (#262): a pool across the middle of a floor, with a dry strip along one side. With
    // water dear the plan goes round by the strip and straightens only over dry ground; with no areas it
    // goes straight through, as it always did.
    [Fact]
    public void ThePlannerGoesRoundDearWaterAndStraightensOnlyOverCheapGround()
    {
        using var app = HeadlessApp.Gameplay().File("data/water.json",
            """[{ "type": "nav_area", "id": "water", "cost": 5, "water": true }]""").Build();
        Span<Vector3> corners = stackalloc Vector3[NavPath.MaxCorners];
        var from = new Vector3(2, 0, 0);
        var to = new Vector3(22, 0, 0);
        var pool = (Min: new Vector3(8, -1, -8), Max: new Vector3(16, 0.5f, 6));

        NavGeometry Geometry(bool areas)
        {
            var geometry = new NavGeometry();
            geometry.Solids.Add(new NavSolid(new Vector3(-2, -0.5f, -8), new Vector3(26, 0, 8)));
            if (areas)
            {
                geometry.Areas = NavAreas.From(app.Records);
                geometry.Volumes.Add(new NavAreaBox(pool.Min, pool.Max, geometry.Areas.Water, true));
            }
            return geometry;
        }

        var wet = Geometry(areas: true);
        Assert.Equal(1, wet.Areas.Count);
        var mesh = new NavMesh(NavMeshAgent.Default);
        mesh.BeginTick(1);
        Assert.Equal(NavMeshAnswer.Found, mesh.Plan(from, to, wet, null, 4096, corners, out int count, out bool reached, out _));
        Assert.True(reached && count >= 3, $"{count} corner(s): it went straight, or did not get there");
        for (int i = 0; i < count; i++)
        {
            var c = corners[i];
            bool inPool = c.X > pool.Min.X + 0.5f && c.X < pool.Max.X - 0.5f && c.Z < pool.Max.Z - 0.5f;
            Assert.False(inPool, $"corner {c} is in the water");
        }
        Assert.Contains(corners[..count].ToArray(), c => c.Z > pool.Max.Z - 0.3f);
        Assert.Equal(NavLine.Dearer, mesh.Line(from, to, wet, default, out _));

        var dry = Geometry(areas: false);
        var plain = new NavMesh(NavMeshAgent.Default);
        plain.BeginTick(1);
        Assert.Equal(NavMeshAnswer.Found, plain.Plan(from, to, dry, null, 4096, corners, out count, out reached, out _));
        Assert.Equal(1, count);
        Assert.Equal(NavLine.Best, plain.Line(from, to, dry, default, out _));
    }

    // What the ground is made of is its area, wherever the ground came from: a brush face's texture (through
    // the physics_material that names it, #270) and a terrain layer, as well as a collider's surface (above).
    private sealed class Painted : ITerrainGenerator
    {
        public void Generate(SectorCoord coord, Heightfield heights, int seed)
        {
            Array.Fill(heights.Heights, 2f);
            for (int z = 0; z < heights.Cells; z++) heights.SetLayer(1, z, 1);   // the second column of cells: the road
        }
    }

    [Fact]
    public void BrushTexturesAndTerrainLayersAreTheAreaOfWhatTheyAreMadeOf()
    {
        const string Records = """
            [{ "type": "physics_material", "id": "grass" },
             { "type": "physics_material", "id": "gravel", "textures": ["gravel*"] },
             { "type": "nav_area", "id": "road", "cost": 0.5, "surfaces": ["gravel"] },
             { "type": "map", "id": "yard", "file": "maps/yard.map", "at": [0, 0, 0], "surface": "grass" }]
            """;
        var fixture = new MountFixture();
        fixture.Write("game", "maps/yard.map", NavMeshTests.Map(new[]
        {
            NavMeshTests.Brush(-0.5f, -0.5f, -0.5f, 16.5f, 0, 8.5f, "floor"),
            NavMeshTests.Brush(4, 0, -0.5f, 6, 0.0625f, 8.5f, "gravel_wet"),
        }));
        fixture.Write("game", "data/yard.json", Records);
        fixture.Mount("game", "sage");
        using var app = HeadlessApp.Gameplay().With(new MapModule()).Mount(fixture).Build();
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.Engine.CreateWorld("yard");
        Assert.NotNull(MapLoader.Load(world, new RecordId("sage", "yard")));
        Tick(world);

        var nav = world.Resources.Get<Navigation>();
        var path = new NavPath();
        Assert.True(nav.Plan(world, world.Resources.Get<IPhysicsWorld>(), new Vector3(1, 0, 4), new Vector3(15, 0, 4), 0.35f, 0.45f, 50f, default, default, ref path));
        var road = nav.Areas.Of(new RecordId("sage", "road"));
        Assert.NotEqual(NavAreas.Ground, road);
        int onRoad = nav.Mesh.FindSpan(new Vector3(5, 0.0625f, 4), reach: 0);
        int onFloor = nav.Mesh.FindSpan(new Vector3(2, 0, 4), reach: 0);
        Assert.True(onRoad >= 0 && onFloor >= 0, "the yard was not baked");
        Assert.Equal(road, nav.Mesh.AreaOf(onRoad));
        Assert.Equal(NavAreas.Ground, nav.Mesh.AreaOf(onFloor));   // "floor" is grass, which is no area

        // Terrain: layer 1 is gravel, painted down one column of cells (8 m wide, x 8 to 16).
        var terrain = new Terrain { Generator = new Painted() };
        terrain.SurfaceLayers.Add(new RecordId("sage", "grass"));
        terrain.SurfaceLayers.Add(new RecordId("sage", "gravel"));
        terrain.Load(new SectorCoord(0, 0));
        var geometry = new NavGeometry { Terrain = terrain, Areas = nav.Areas };
        var mesh = new NavMesh(NavMeshAgent.Default);
        mesh.TryBake(1, 0, geometry, unbudgeted: true);
        mesh.TryBake(0, 0, geometry, unbudgeted: true);
        Assert.Equal(road, mesh.AreaOf(mesh.FindSpan(new Vector3(12, 2, 4), reach: 0)));
        Assert.Equal(NavAreas.Ground, mesh.AreaOf(mesh.FindSpan(new Vector3(4, 2, 4), reach: 0)));
    }

    // ---- ground closed to a faction ---------------------------------------------------------------------------

    private const string Camp = """
        { "type": "faction", "id": "wolves", "default": "Hostile" },
        { "type": "faction", "id": "villagers" },
        { "type": "nav_area", "id": "camp", "forbidden": ["wolves"] }
        """;

    private static readonly (Vector3 Min, Vector3 Max) CampBox = (new Vector3(-4, -1, -4), new Vector3(4, 1, 4));

    private static World CampWorld(HeadlessApp app)
    {
        var world = app.World;
        Solid(world, new Vector3(-30, -1, -30), new Vector3(30, 0, 30));
        var camp = world.Create(Transform.At((CampBox.Min + CampBox.Max) * 0.5f), "camp");
        world.Add(camp, new NavArea { Area = new RecordId("sage", "camp"), Size = CampBox.Max - CampBox.Min });
        Tick(world);
        return world;
    }

    private static bool InCamp(Vector3 p, float grow = 0f) =>
        p.X > CampBox.Min.X - grow && p.X < CampBox.Max.X + grow && p.Z > CampBox.Min.Z - grow && p.Z < CampBox.Max.Z + grow;

    // A nav_area volume closed to wolves: a wolf's plan goes round it, a villager's straight through, and a
    // wolf's plan to somewhere inside it is no plan at all.
    [Fact]
    public void GroundClosedToAFactionIsPlannedRoundByItAndCrossedByOthers()
    {
        using var app = HeadlessApp.Gameplay().File("data/camp.json", "[" + Camp + "]").Boot("camp");
        Assert.Equal(0, app.Records.ErrorCount);
        var world = CampWorld(app);
        var nav = world.Resources.Get<Navigation>();
        var space = world.Resources.Get<IPhysicsWorld>();
        var from = new Vector3(-10, 0, 0.5f);
        var to = new Vector3(10, 0, 0.5f);
        var wolves = new RecordId("sage", "wolves");

        var path = new NavPath();
        Assert.True(nav.Plan(world, space, from, to, 0.35f, 1.8f, wolves, 0.45f, 50f, default, default, ref path));
        Assert.True(path.Count >= 2, "the wolf's plan went straight through the camp");
        var previous = from;
        for (int i = 0; i < path.Count; i++)
        {
            Assert.False(InCamp(path[i]), $"corner {path[i]} is in the camp");
            // Every leg too, sampled: a leg between two corners outside can still cut a corner of it.
            for (float t = 0.05f; t < 1f; t += 0.05f)
                Assert.False(InCamp(Vector3.Lerp(previous, path[i], t)), $"the leg from {previous} to {path[i]} cuts across the camp");
            previous = path[i];
        }

        path = new NavPath();
        Assert.True(nav.Plan(world, space, from, to, 0.35f, 1.8f, new RecordId("sage", "villagers"), 0.45f, 50f, default, default, ref path));
        Assert.Equal(1, path.Count);

        path = new NavPath();
        Assert.False(nav.Plan(world, space, from, Vector3.Zero, 0.35f, 1.8f, wolves, 0.45f, 50f, default, default, ref path));
        Assert.True(path.NoWayThrough);
    }

    // And a wolf chasing a player who stands in the camp walks up to its edge and stops there.
    [Fact]
    public void AWolfChasingIntoGroundClosedToItStopsAtTheEdge()
    {
        using var app = HeadlessApp.Gameplay().File("data/camp.json", "[" + Brains + "," + Camp + "]").Boot("camp");
        Assert.Equal(0, app.Records.ErrorCount);
        var world = CampWorld(app);

        var player = world.Create(Transform.At(new Vector3(0, 0, 0)), "player");
        world.Add(player, Collider.Standing(0.35f, 1.8f, world.Resources.Get<IPhysicsWorld>().Layers.Player));
        world.Add(player, RigidBody.Kinematic());
        world.AddAttributes(player);
        world.Add(player, new Faction { Id = new RecordId("sage", "villagers") });
        player.AddTag<PlayerControlled>();

        var start = new Vector3(-12, 0, 0);
        var transform = Transform.At(start);
        transform.LocalRotation = SageMath.RotationFromYaw(SageMath.YawTo(start, Vector3.Zero));
        var wolf = world.Create(transform, "wolf");
        world.AddCharacter(wolf, world.Resources.Get<IPhysicsWorld>().Layers.Enemy);
        world.Add(wolf, new AIState { Schedule = new RecordId("sage", "idle") });
        world.Add(wolf, new Faction { Id = new RecordId("sage", "wolves") });
        world.AddAttributes(wolf);

        bool entered = false;
        for (int tick = 0; tick < 10 * 60; tick++)
        {
            Tick(world);
            entered |= InCamp(At(world, wolf));
        }

        var at = At(world, wolf);
        Assert.Equal(new RecordId("sage", "chase"), world.Get<AIState>(wolf).Schedule);
        Assert.False(entered, $"the wolf walked into the camp (now at {at})");
        Assert.True(at.X > CampBox.Min.X - 1.5f, $"it stopped {CampBox.Min.X - at.X:F1} m short of the camp, not at its edge");
    }

    // ---- sizes -------------------------------------------------------------------------------------------

    // A body takes the smallest size class it fits, and each class has the mesh of its own size: a rat goes
    // under a beam a person cannot, and an ogre does not fit a door a person does.
    [Fact]
    public void EachBodyPlansOnTheMeshOfItsOwnSize()
    {
        Assert.Equal(0, Navigation.SizeClassOf(0.2f, 0.6f));
        Assert.Equal(Navigation.PersonClass, Navigation.SizeClassOf(0.35f, 1.8f));
        Assert.Equal(Navigation.PersonClass, Navigation.SizeClassOf(0.3f, 1.2f));
        Assert.Equal(2, Navigation.SizeClassOf(0.7f, 2.5f));
        Assert.Equal(2, Navigation.SizeClassOf(2f, 5f));

        var from = new Vector3(2, 0, 4);
        var to = new Vector3(11, 0, 4);
        NavMeshAnswer PlanFor(int size, NavGeometry geometry)
        {
            var mesh = new NavMesh(Navigation.SizeClasses[size]);
            mesh.BeginTick(1);
            return mesh.Plan(from, to, geometry, null, 4096, new Vector3[NavPath.MaxCorners], out _, out _, out _);
        }

        // A wall across the floor with a door 1.2 m wide in it: a person fits, an ogre does not.
        var door = new NavGeometry();
        door.Solids.Add(new NavSolid(new Vector3(-2, -0.5f, -2), new Vector3(14, 0, 10)));
        door.Solids.Add(new NavSolid(new Vector3(6, 0, -2), new Vector3(6.5f, 3.5f, 3.4f)));
        door.Solids.Add(new NavSolid(new Vector3(6, 0, 4.6f), new Vector3(6.5f, 3.5f, 10)));
        Assert.Equal(NavMeshAnswer.Found, PlanFor(0, door));
        Assert.Equal(NavMeshAnswer.Found, PlanFor(Navigation.PersonClass, door));
        Assert.Equal(NavMeshAnswer.NoRoute, PlanFor(2, door));

        // The same door with a beam across it 1.2 m up: only the rat goes under.
        door.Solids.Add(new NavSolid(new Vector3(5.5f, 1.2f, 3.2f), new Vector3(7f, 3.5f, 4.8f)));
        Assert.Equal(NavMeshAnswer.Found, PlanFor(0, door));
        Assert.Equal(NavMeshAnswer.NoRoute, PlanFor(Navigation.PersonClass, door));
    }
}
