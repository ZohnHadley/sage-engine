#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The navmesh (issue #264, docs/design/16 §3.4): baked from brush floors, terrain and static colliders,
// searched on a coarse graph of regions and then span by span.
//
// The bake and the search are tested on plain geometry first — boxes whose answer can be worked out by
// hand — and then through the real thing: a creature crossing a level of three rooms built from brushes,
// which straddles a sector border, and `sage validate` finding a marker nothing can walk to.
public class NavMeshTests
{
    public NavMeshTests() { _ = TestEnv.UserRoot; }

    // ---- plain geometry --------------------------------------------------------------------------

    private static NavGeometry Boxes(params (Vector3 Min, Vector3 Max)[] boxes)
    {
        var geometry = new NavGeometry();
        foreach (var (min, max) in boxes) geometry.Solids.Add(new NavSolid(min, max));
        return geometry;
    }

    private static (Vector3, Vector3) Box(float x0, float y0, float z0, float x1, float y1, float z1) =>
        (new Vector3(x0, y0, z0), new Vector3(x1, y1, z1));

    private static (Vector3, Vector3) Floor(float x0, float z0, float x1, float z1) => Box(x0, -0.5f, z0, x1, 0f, z1);

    // Every leg of a path is a straight walk over the mesh, or the path goes through something.
    private static void AssertWalkable(NavMesh mesh, Vector3 from, ReadOnlySpan<Vector3> corners)
    {
        int at = mesh.FindSpan(from);
        Assert.True(at >= 0, $"the start {from} is not on the mesh");
        foreach (var corner in corners)
        {
            int next = mesh.FindSpan(corner);
            Assert.True(next >= 0, $"corner {corner} is not on the mesh");
            Assert.True(mesh.LineIsClear(at, next) || at == next, $"no straight walk from {mesh.PositionOf(at)} to {corner}");
            at = next;
        }
    }

    private static NavMeshAnswer Plan(NavMesh mesh, NavGeometry geometry, Vector3 from, Vector3 to, Span<Vector3> corners,
                                      out int count, out bool reached, int maxNodes = 4096, long tick = 1)
    {
        mesh.BeginTick(tick);
        return mesh.Plan(from, to, geometry, maxNodes, corners, out count, out reached);
    }

    // A floor is somewhere to stand; a wall is not, and nor is the strip beside it a body cannot fit in.
    [Fact]
    public void AFloorIsWalkableAndAWallAndItsEdgeAreNot()
    {
        var geometry = Boxes(Floor(0, 0, 8, 8), Box(4, 0, 0, 4.5f, 2, 8));
        var mesh = new NavMesh(NavMeshAgent.Default);
        mesh.TryBake(0, 0, geometry, unbudgeted: true);

        int open = mesh.FindSpan(new Vector3(2, 0, 4), reach: 0);
        Assert.True(open >= 0, "nothing to stand on in the middle of a floor");
        Assert.Equal(0f, mesh.PositionOf(open).Y, 3);

        Assert.True(mesh.FindSpan(new Vector3(4.25f, 0, 4), reach: 0) < 0, "a wall's foot is walkable");
        // Its top is a floor of its own, two metres up: not the one a body at floor height is on.
        Assert.True(mesh.FindSpan(new Vector3(4.25f, 2, 4), reach: 0) < 0, "a wall half a metre thick has room on top for a body");
        // A cell's centre 12.5 cm from the wall: a 35 cm body does not fit there.
        Assert.True(mesh.FindSpan(new Vector3(3.875f, 0, 4), reach: 0) < 0, "the erosion did not take the strip beside the wall");
        Assert.True(mesh.FindSpan(new Vector3(3.875f, 0, 4)) >= 0, "a body pressed against the wall is not on the mesh at all");
    }

    // The case quarter-metre cells exist for: a doorway a metre wide stays open for a 0.35 m body.
    [Fact]
    public void ADoorwayAMetreWideIsWalkedThrough()
    {
        // A wall across the middle (x = 6..6.5) with a gap at z = 3.5..4.5.
        var geometry = Boxes(Floor(-2, -2, 14, 10), Box(6, 0, -2, 6.5f, 2, 3.5f), Box(6, 0, 4.5f, 6.5f, 2, 10));
        var mesh = new NavMesh(NavMeshAgent.Default);
        Span<Vector3> corners = stackalloc Vector3[NavPath.MaxCorners];

        var from = new Vector3(2, 0, 8);
        var to = new Vector3(11, 0, 0);
        var answer = Plan(mesh, geometry, from, to, corners, out int count, out bool reached);

        Assert.Equal(NavMeshAnswer.Found, answer);
        Assert.True(reached, "the goal is two tiles away, so the corridor should hold it");
        Assert.Equal(to, corners[count - 1]);
        AssertWalkable(mesh, from, corners[..count]);
        // Through the door, not round the end of a wall that has no end.
        Assert.Contains(corners[..count].ToArray(), c => c.X > 5.5f && c.X < 7f && c.Z > 3.5f && c.Z < 4.5f);
    }

    // No way through is an answer, and costs no more than the budget.
    [Fact]
    public void ASealedRoomIsNoRouteWithinTheBudget()
    {
        var geometry = Boxes(Floor(-4, -4, 12, 12),
                             Box(2, 0, 2, 6, 2, 2.5f), Box(2, 0, 5.5f, 6, 2, 6), Box(2, 0, 2, 2.5f, 2, 6), Box(5.5f, 0, 2, 6, 2, 6));
        var mesh = new NavMesh(NavMeshAgent.Default);
        Span<Vector3> corners = stackalloc Vector3[NavPath.MaxCorners];

        var answer = Plan(mesh, geometry, new Vector3(4, 0, 4), new Vector3(10, 0, 10), corners, out int count, out _, maxNodes: 512);

        Assert.Equal(NavMeshAnswer.NoRoute, answer);
        Assert.Equal(0, count);
        Assert.True(mesh.LastNodes <= 512, $"{mesh.LastNodes} nodes with a budget of 512");
    }

    // A step is climbed and a ledge is not: the character controller's 0.45 m.
    [Fact]
    public void AStepIsClimbedAndALedgeIsNot()
    {
        Span<Vector3> corners = stackalloc Vector3[NavPath.MaxCorners];
        foreach (var (height, climbable) in new[] { (0.3f, true), (0.8f, false) })
        {
            var geometry = Boxes(Floor(0, 0, 8, 8), Box(4, 0, 0, 8, height, 8));
            var mesh = new NavMesh(NavMeshAgent.Default);
            var answer = Plan(mesh, geometry, new Vector3(1, 0, 4), new Vector3(6, height, 4), corners, out _, out _);
            Assert.Equal(climbable ? NavMeshAnswer.Found : NavMeshAnswer.NoRoute, answer);
        }
    }

    // A bake is a function of the geometry: the same input, the same spans and links, every time.
    [Fact]
    public void BakingIsDeterministic()
    {
        var geometry = Boxes(Floor(-1, -1, 9, 9), Box(3, 0, 1, 3.4f, 1.5f, 6), Box(5, 0, 2, 6, 0.3f, 7));
        var a = new NavMesh(NavMeshAgent.Default).Bake(0, 0, geometry);
        var b = new NavMesh(NavMeshAgent.Default).Bake(0, 0, geometry);

        Assert.True(a.SpanCount > 500, $"only {a.SpanCount} spans: the test floor is not there");
        Assert.Equal(a.Floor, b.Floor);
        Assert.Equal(a.Links, b.Links);
        Assert.Equal(a.Region, b.Region);
    }

    // A long way, over a sector border: a hundred and eighty metres along a wall and round its end. The
    // coarse graph plans the whole of it without baking it (a tile not baked yet is one optimistic node),
    // the fine search only the next stretch, and walking the corners and planning again gets there —
    // every plan inside the node budget and a small bake budget.
    [Fact]
    public void ALongWayAcrossASectorBorderIsPlannedAStretchAtATime()
    {
        // Floor from x = 950 to 1150; a wall along z = 10 from x = 940 to 1140, open at the east end.
        var geometry = Boxes(Box(940, -0.5f, -4, 1160, 0, 24), Box(940, 0, 9.75f, 1140, 2, 10.25f));
        var mesh = new NavMesh(NavMeshAgent.Default) { BakesPerTick = 6 };
        Span<Vector3> corners = stackalloc Vector3[NavPath.MaxCorners];

        var at = new Vector3(960, 0, 5);
        var goal = new Vector3(960, 0, 15);
        bool crossed = false, arrived = false;
        int plans = 0;
        for (long tick = 1; tick < 400 && !arrived; tick++)
        {
            var answer = Plan(mesh, geometry, at, goal, corners, out int count, out bool reached, maxNodes: 4096, tick: tick);
            if (answer == NavMeshAnswer.CannotAnswer) continue;   // its tile not baked yet: next tick
            plans++;
            Assert.Equal(NavMeshAnswer.Found, answer);
            Assert.True(mesh.LastNodes <= 4096, $"a plan looked at {mesh.LastNodes} nodes");
            AssertWalkable(mesh, at, corners[..count]);

            // Walk it: to the last corner of this stretch, as a creature would before asking again.
            at = corners[count - 1];
            crossed |= at.X > 1024f;
            arrived = reached && Vector3.Distance(at, goal) < 0.01f;
        }

        Assert.True(arrived, $"it never got round the wall: stopped at {at} after {plans} plans");
        Assert.True(crossed, "the way never crossed x = 1024, so this proves nothing about sector borders");
        Assert.True(plans < 60, $"{plans} plans for 400 m: each plan is covering too little");
    }

    // Terrain is baked too, by its heights and its slope: a ridge too steep to climb runs across the
    // border between sectors (0, 0) and (1, 0), and the way over the border is round its end.
    private sealed class Ridge : ITerrainGenerator
    {
        public void Generate(SectorCoord coord, Heightfield heights, int seed)
        {
            var corner = coord.Origin(Terrain.SectorSize);
            for (int z = 0; z < heights.Resolution; z++)
                for (int x = 0; x < heights.Resolution; x++)
                {
                    float wx = corner.X + x * heights.Spacing, wz = corner.Z + z * heights.Spacing;
                    heights.Heights[z * heights.Resolution + x] = wx >= 1016f && wx <= 1032f && wz < 40f ? 40f : 5f;
                }
        }
    }

    [Fact]
    public void TerrainIsBakedAndARidgeAcrossASectorBorderIsWalkedRound()
    {
        var terrain = new Terrain { Generator = new Ridge() };
        terrain.Load(new SectorCoord(0, 0));
        terrain.Load(new SectorCoord(1, 0));
        var geometry = new NavGeometry { Terrain = terrain };
        var mesh = new NavMesh(NavMeshAgent.Default);
        Span<Vector3> corners = stackalloc Vector3[NavPath.MaxCorners];

        var at = new Vector3(1000, 5, 16);
        var goal = new Vector3(1050, 5, 16);
        Assert.Equal(5f, mesh.Bake(NavMesh.TileOf(at.X), NavMesh.TileOf(at.Z), geometry).Floor[0], 3);
        bool round = false, arrived = false;
        for (long tick = 1; tick < 200 && !arrived; tick++)
        {
            var answer = Plan(mesh, geometry, at, goal, corners, out int count, out bool reached, tick: tick);
            if (answer == NavMeshAnswer.CannotAnswer) continue;
            Assert.Equal(NavMeshAnswer.Found, answer);
            AssertWalkable(mesh, at, corners[..count]);
            foreach (var corner in corners[..count])
            {
                Assert.True(corner.Y < 6f, $"a corner at {corner} is up on the ridge");
                round |= corner.Z > 32f;
            }
            at = corners[count - 1];
            arrived = reached;
        }

        Assert.True(arrived, $"it never got over the border: stopped at {at}");
        Assert.True(round, "no corner went round the end of the ridge");
    }

    // ---- through the world -------------------------------------------------------------------------

    private const string Records = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "aiProfile": "default_ai", "schedules": { "idle": "idle", "chase": "chase", "meleeAttack": "melee_attack" } },
         { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "tag", "id": "state.dead" },
         { "type": "ai_profile", "id": "default_ai", "sightRange": 60, "meleeRange": 1.8, "thinkRate": 20, "memorySeconds": 30 },
         { "type": "ai_schedule", "id": "idle", "tasks": [{ "task": "Wait", "seconds": 1.5 }], "interrupts": ["SeeEnemy"] },
         { "type": "ai_schedule", "id": "chase", "tasks": [{ "task": "MoveToTarget", "distance": 1.6 }], "interrupts": ["EnemyInMeleeRange", "LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "melee_attack", "tasks": ["FaceTarget", { "task": "Wait", "seconds": 0.4 }], "interrupts": ["LostEnemy", "NoEnemy"] }]
        """;

    // A brush in the canonical Quake form, from a box in engine metres (map units are 1/32 m, map Y is
    // engine -Z and map Z is engine Y).
    internal static string Brush(float x0, float y0, float z0, float x1, float y1, float z1, string texture = "wall")
    {
        int ax = (int)MathF.Round(x0 * 32), bx = (int)MathF.Round(x1 * 32);
        int ay = (int)MathF.Round(-z1 * 32), by = (int)MathF.Round(-z0 * 32);
        int az = (int)MathF.Round(y0 * 32), bz = (int)MathF.Round(y1 * 32);
        var s = new StringBuilder();
        s.Append("{\n");
        s.Append($"( {ax} {ay} {az} ) ( {ax} {ay + 1} {az} ) ( {ax} {ay} {az + 1} ) {texture} 0 0 0 1 1\n");
        s.Append($"( {ax} {ay} {az} ) ( {ax} {ay} {az + 1} ) ( {ax + 1} {ay} {az} ) {texture} 0 0 0 1 1\n");
        s.Append($"( {ax} {ay} {az} ) ( {ax + 1} {ay} {az} ) ( {ax} {ay + 1} {az} ) {texture} 0 0 0 1 1\n");
        s.Append($"( {bx} {by} {bz} ) ( {bx} {by + 1} {bz} ) ( {bx + 1} {by} {bz} ) {texture} 0 0 0 1 1\n");
        s.Append($"( {bx} {by} {bz} ) ( {bx + 1} {by} {bz} ) ( {bx} {by} {bz + 1} ) {texture} 0 0 0 1 1\n");
        s.Append($"( {bx} {by} {bz} ) ( {bx} {by} {bz + 1} ) ( {bx} {by + 1} {bz} ) {texture} 0 0 0 1 1\n");
        s.Append("}\n");
        return s.ToString();
    }

    internal static string Point(string classname, Vector3 at, string? targetname = null) =>
        "{\n" + $"\"classname\" \"{classname}\"\n" +
        (targetname != null ? $"\"targetname\" \"{targetname}\"\n" : "") +
        $"\"origin\" \"{(int)MathF.Round(at.X * 32)} {(int)MathF.Round(-at.Z * 32)} {(int)MathF.Round(at.Y * 32)}\"\n" + "}\n";

    internal static string Map(IEnumerable<string> brushes, params string[] points) =>
        "{\n\"classname\" \"worldspawn\"\n" + string.Concat(brushes) + "}\n" + string.Concat(points);

    // Three rooms in a row, 8 m square, with walls 0.9 m high (a creature sees over them, it cannot walk
    // over them: the same reason NavigationTests' fence is waist high). The doors zig-zag — the first at
    // the far side, the second at the near side — so the straight line between the end rooms crosses two
    // walls and the way round is not a slide along either.
    //
    // With `pen`, the first room also has a pen in it, open to the west, away from everything: the
    // creature starts inside it. Concave on purpose (NavigationTests' pen, for the same reason): steering
    // slides along a wall into a door by luck, and only a plan walks the wrong way first.
    internal static IEnumerable<string> ThreeRooms(float wall = 0.9f, bool pen = false)
    {
        if (pen)
        {
            yield return Brush(5.5f, 0, 2, 6, wall, 6);             // its east side, toward the player
            yield return Brush(2, 0, 6, 6, wall, 6.5f);             // north
            yield return Brush(2, 0, 1.5f, 6, wall, 2);             // south
        }
        yield return Brush(-0.5f, -0.5f, -0.5f, 24.5f, 0, 8.5f, "floor");
        yield return Brush(-0.5f, 0, -0.5f, 24.5f, wall, 0);        // south
        yield return Brush(-0.5f, 0, 8, 24.5f, wall, 8.5f);         // north
        yield return Brush(-0.5f, 0, 0, 0, wall, 8);                // west
        yield return Brush(24, 0, 0, 24.5f, wall, 8);               // east
        yield return Brush(7.75f, 0, 0, 8.25f, wall, 6);            // A|B, door at z 6..7.25
        yield return Brush(7.75f, 0, 7.25f, 8.25f, wall, 8);
        yield return Brush(15.75f, 0, 0, 16.25f, wall, 0.75f);      // B|C, door at z 0.75..2
        yield return Brush(15.75f, 0, 2, 16.25f, wall, 8);
    }

    // Placed so the middle room straddles x = 1024, the border between sectors (0, 0) and (1, 0).
    private static readonly Vector3 LevelAt = new(1012, 0, 100);

    private static (Engine, World) LevelWorld(string map, bool meshOff = false)
    {
        var fixture = new MountFixture();
        fixture.Write("game", "maps/rooms.map", map);
        fixture.Write("game", "data/nav.json", Records);
        fixture.Write("game", "data/level.json",
            $$"""[{ "type": "map", "id": "rooms", "file": "maps/rooms.map", "at": [{{LevelAt.X}}, {{LevelAt.Y}}, {{LevelAt.Z}}] }]""");
        fixture.Mount("game", "sage");
        var engine = HeadlessApp.Gameplay().With(new MapModule()).Mount(fixture).Build().Engine;
        if (meshOff)
        {
            // Through the cvar, as anybody would, and before the world exists so its first plan obeys it.
            engine.CVars.Execute("sv_cheats 1");
            engine.CVars.Execute("nav_mesh 0");
        }
        var world = engine.CreateWorld("rooms");
        Assert.NotNull(MapLoader.Load(world, new RecordId("sage", "rooms")));
        world.RunFixed(1f / 60f);   // placed, and its hulls in physics
        return (engine, world);
    }

    // Facing +X, down the row of rooms: its sight is a cone, and the player is in the far room.
    private static Entity Creature(World world, Vector3 position)
    {
        var transform = Transform.At(position);
        transform.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2f);
        var entity = world.Create(transform, "creature");
        world.AddCharacter(entity, world.Resources.Get<IPhysicsWorld>().Layers.Enemy);
        world.Add(entity, new AIState { Schedule = Conventional.Idle });
        world.AddAttributes(entity);
        return entity;
    }

    private static Entity Player(World world, Vector3 position)
    {
        var entity = world.Create(Transform.At(position), "player");
        world.Add(entity, Collider.Standing(0.35f, 1.8f, world.Resources.Get<IPhysicsWorld>().Layers.Player));
        world.Add(entity, RigidBody.Kinematic());
        world.AddAttributes(entity);
        entity.AddTag<PlayerControlled>();
        return entity;
    }

    // The issue's "done when": a creature paths through a level of rooms built from brushes and across a
    // sector border, inside `nav_maxnodes` and `nav_plans`. The local grid cannot do this at all — a
    // brush is a hull in physics and not a `Collider`, so the grid never saw a wall — which the A/B below
    // shows.
    [Fact]
    public void ACreatureCrossesALevelOfRoomsAndASectorBorderOnTheNavmesh()
    {
        var (engine, world) = LevelWorld(Map(ThreeRooms(pen: true)));
        using (engine)
        {
            var nav = world.Resources.Get<Navigation>();
            var creature = Creature(world, LevelAt + new Vector3(4, 0, 4));
            var player = Player(world, LevelAt + new Vector3(22, 0, 6));
            Assert.True(world.Get<Transform>(creature).LocalPosition.X < 1024f && world.Get<Transform>(player).LocalPosition.X > 1024f,
                "the two are not in different sectors");

            float closest = float.MaxValue;
            int busiestTick = 0, mostNodes = 0;
            for (int i = 0; i < 40 * 60 && closest >= 2.5f; i++)
            {
                int before = nav.Plans;
                world.RunFixed(1f / 60f);
                busiestTick = Math.Max(busiestTick, nav.Plans - before);
                mostNodes = Math.Max(mostNodes, nav.Mesh.LastNodes);
                closest = MathF.Min(closest, SageMath.DistanceXZ(world.Get<Transform>(creature).LocalPosition,
                                                                 world.Get<Transform>(player).LocalPosition));
            }

            Assert.True(closest < 2.5f, $"the creature got no closer than {closest:F1} m (at {world.Get<Transform>(creature).LocalPosition}; " +
                                        $"{nav.MeshPlans} plans on the navmesh, {nav.GridPlans} on the grid, {nav.NoRoute} with no way through)");
            Assert.True(nav.MeshPlans > 0, "nothing was planned on the navmesh, so the grid did this");
            Assert.True(busiestTick <= nav.PlansPerTick, $"{busiestTick} plans in one tick with a budget of {nav.PlansPerTick}");
            Assert.True(mostNodes <= nav.MaxNodes, $"a search looked at {mostNodes} nodes with a budget of {nav.MaxNodes}");
        }
    }

    // The A/B: the same level with the navmesh off. The grid sees no brushes, so the creature leans on
    // the first wall. If this ever reaches the player, the test above proves nothing and needs a harder
    // level.
    [Fact]
    public void WithoutTheNavmeshTheSameCreatureIsStuckInTheFirstRoom()
    {
        var (engine, world) = LevelWorld(Map(ThreeRooms(pen: true)), meshOff: true);
        using (engine)
        {
            var creature = Creature(world, LevelAt + new Vector3(4, 0, 4));
            var player = Player(world, LevelAt + new Vector3(22, 0, 6));

            for (int i = 0; i < 25 * 60; i++) world.RunFixed(1f / 60f);

            Assert.True(world.Get<Transform>(creature).LocalPosition.X < LevelAt.X + 8f,
                $"with the navmesh off the creature still left the first room (at {world.Get<Transform>(creature).LocalPosition})");
            Assert.Equal(0, world.Resources.Get<Navigation>().MeshPlans);
        }
    }

    // A tile is kept, so it must be dropped when what it was baked from changes: a static collider put
    // down after the bake is in the next plan's way.
    [Fact]
    public void AStaticColliderAddedAfterTheBakeIsInTheNextPlan()
    {
        var engine = HeadlessApp.Gameplay().File("data/nav.json", Records).Build().Engine;
        using (engine)
        {
            var world = engine.CreateWorld("nav");
            var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
            world.Add(ground, Collider.Box(new Vector3(60, 1, 60)));
            world.RunFixed(1f / 60f);

            var nav = world.Resources.Get<Navigation>();
            var space = world.Resources.Get<IPhysicsWorld>();
            var from = new Vector3(0, 0, -6);
            var to = new Vector3(0, 0, 6);
            var path = new NavPath();
            Assert.True(nav.Plan(world, space, from, to, 0.35f, 0.45f, 50f, default, default, ref path));
            Assert.Equal(1, path.Count);   // nothing in the way: one corner, the goal
            Assert.Equal(1, nav.MeshPlans);

            var wall = world.Create(Transform.At(new Vector3(0, 1, 0)), "wall");
            world.Add(wall, Collider.Box(new Vector3(12, 2, 1)));
            world.RunFixed(1f / 60f);

            Assert.True(nav.Plan(world, space, from, to, 0.35f, 0.45f, 50f, default, default, ref path));
            Assert.True(path.Count >= 2, "the plan went straight through a wall put down after the tiles were baked");
            for (int i = 0; i < path.Count - 1; i++)
                Assert.True(MathF.Abs(path[i].X) > 6f, $"corner {path[i]} is not round the end of the wall");
            Assert.Equal(2, nav.MeshPlans);
        }
    }

    // ---- doors, off-mesh links and things that move (#265) ---------------------------------------------

    private static NavMeshAnswer Plan(NavMesh mesh, NavGeometry geometry, NavCrossings crossings, Vector3 from, Vector3 to,
                                      Span<Vector3> corners, out int count, out NavMeshCrossing crossing, long tick = 1)
    {
        mesh.BeginTick(tick);
        return mesh.Plan(from, to, geometry, crossings, 4096, corners, out count, out _, out crossing);
    }

    // A platform three metres up: the way down is a drop link, and it goes one way unless it says
    // otherwise. The path stops at the link's far end, so the creature crosses it and plans again.
    [Fact]
    public void ALinkIsAnEdgeOneWayUnlessItSaysTwoWay()
    {
        var geometry = Boxes(Floor(-8, -8, 16, 16), Box(0, 0, 0, 6, 3, 6));
        Span<Vector3> corners = stackalloc Vector3[NavPath.MaxCorners];
        var top = new Vector3(3, 3, 3);
        var ground = new Vector3(-4, 0, 3);
        var link = new NavLinkSpan(default, new Vector3(0.3f, 3, 3), new Vector3(-1.5f, 0, 3), NavLinkKind.Drop, false, 0f);

        var none = new NavCrossings();
        Assert.Equal(NavMeshAnswer.NoRoute, Plan(new NavMesh(NavMeshAgent.Default), geometry, none, top, ground, corners, out _, out _));

        var crossings = new NavCrossings();
        crossings.Links.Add(link);
        var mesh = new NavMesh(NavMeshAgent.Default);
        Assert.Equal(NavMeshAnswer.Found, Plan(mesh, geometry, crossings, top, ground, corners, out int count, out var crossing));
        Assert.Equal(NavCrossing.Drop, crossing.Kind);
        Assert.Equal(0, crossing.Index);
        Assert.Equal(count - 2, crossing.Corner);
        Assert.Equal(link.Start, corners[crossing.Corner]);
        Assert.Equal(link.End, corners[crossing.Corner + 1]);
        AssertWalkable(mesh, top, corners[..(count - 1)]);

        // Not back up it.
        Assert.Equal(NavMeshAnswer.NoRoute, Plan(mesh, geometry, crossings, ground, top, corners, out _, out _, tick: 2));
        crossings.Links[0] = link with { TwoWay = true };
        Assert.Equal(NavMeshAnswer.Found, Plan(mesh, geometry, crossings, ground, top, corners, out count, out crossing, tick: 3));
        Assert.Equal(link.End, corners[crossing.Corner]);
        Assert.Equal(link.Start, corners[crossing.Corner + 1]);
    }

    // A door in a doorway is baked as if it were not there, and the plan through it stops at the far
    // side with the door as its crossing: the near corner is short of it, the far one past it.
    [Fact]
    public void ADoorIsACrossingAndThePathStopsOnItsFarSide()
    {
        var geometry = Boxes(Floor(-2, -2, 14, 10), Box(6, 0, -2, 6.5f, 2, 3.4f), Box(6, 0, 4.6f, 6.5f, 2, 10));
        var crossings = new NavCrossings();
        crossings.Doors.Add(new NavDoorSpan(default, new Vector3(6.2f, 0, 3.4f), new Vector3(6.3f, 2, 4.6f), Open: false, Locked: false));
        var mesh = new NavMesh(NavMeshAgent.Default);
        Span<Vector3> corners = stackalloc Vector3[NavPath.MaxCorners];

        var from = new Vector3(2, 0, 8);
        Assert.Equal(NavMeshAnswer.Found, Plan(mesh, geometry, crossings, from, new Vector3(11, 0, 0), corners, out int count, out var crossing));
        Assert.Equal(NavCrossing.Door, crossing.Kind);
        Assert.Equal(count - 2, crossing.Corner);
        var near = corners[crossing.Corner];
        var far = corners[crossing.Corner + 1];
        Assert.True(near.X < 6.2f - 0.3f && near.Z > 3.4f && near.Z < 4.6f, $"the near side {near} is not in front of the door");
        Assert.True(far.X > 6.3f + 0.3f && far.Z > 3.4f && far.Z < 4.6f, $"the far side {far} is not past the door");
        AssertWalkable(mesh, from, corners[..count]);
    }

    private static (Engine, World) BoxWorld(params (Vector3 Min, Vector3 Max)[] solids)
    {
        var engine = HeadlessApp.Gameplay().File("data/nav.json", Records).Build().Engine;
        var world = engine.CreateWorld("links");
        foreach (var (min, max) in solids)
        {
            var solid = world.Create(Transform.At((min + max) * 0.5f), "solid");
            world.Add(solid, Collider.Box(max - min));
        }
        return (engine, world);
    }

    private static (Vector3, Vector3) Solid(float x0, float y0, float z0, float x1, float y1, float z1) =>
        (new Vector3(x0, y0, z0), new Vector3(x1, y1, z1));

    // Runs until the creature is within `reach` of the player, or `seconds` pass. Returns how close it got.
    private static float Chase(World world, Entity creature, Entity player, float seconds, float reach = 2.5f, Action<int>? each = null)
    {
        float closest = float.MaxValue;
        for (int i = 0; i < seconds * 60 && closest >= reach; i++)
        {
            world.RunFixed(1f / 60f);
            each?.Invoke(i);
            closest = MathF.Min(closest, Vector3.Distance(world.Get<Transform>(creature).LocalPosition, world.Get<Transform>(player).LocalPosition));
        }
        return closest;
    }

    // Two rooms with a waist-high wall between them (a creature sees over it, it cannot walk over it) and
    // one doorway, shut by a door of the same height that slides into the wall beside it.
    private static (Engine, World, Entity Door) DoorWorld(bool locked)
    {
        var (engine, world) = BoxWorld(
            Solid(-2, -1, -4, 18, 0, 12),                // floor, exactly as deep as the wall: no way round
            Solid(7.9f, 0, -4, 8.1f, 0.9f, 3.4f),
            Solid(7.9f, 0, 4.6f, 8.1f, 0.9f, 12));
        var closed = new Vector3(8, 0.45f, 4);
        var door = world.Create(Transform.At(closed), "door");
        door.Name = "door";
        world.Add(door, Collider.Box(new Vector3(0.1f, 0.9f, 1.2f)));
        world.Add(door, new Mover { OpenOffset = new Vector3(0, 0, 1.4f), Seconds = 1f, Closed = closed });
        if (locked) world.Add(door, new NavDoor { Locked = true });
        return (engine, world, door);
    }

    // The issue's first "done when": a creature whose way is through a closed door walks up to it, opens
    // it (fires `Open`) and waits, then walks through — the door is not a wall to the planner.
    [Fact]
    public void ACreatureOpensAClosedDoorAndWalksThroughIt()
    {
        var (engine, world, door) = DoorWorld(locked: false);
        using (engine)
        {
            float opened = 0f;   // nothing else in this world opens it
            var creature = Creature(world, new Vector3(3, 0, 0));
            var player = Player(world, new Vector3(13, 0, 8));
            var nav = world.Resources.Get<Navigation>();
            bool waited = false;

            float closest = Chase(world, creature, player, 30f, each: _ =>
            {
                ref readonly var path = ref world.Get<AIState>(creature).Path;
                if (path.InCrossing && path.Crossing == NavCrossing.Door && world.Get<Mover>(door).Position < 1f)
                    waited = true;
                opened = MathF.Max(opened, world.Get<Mover>(door).Position);
            });

            Assert.True(closest < 2.5f, $"the creature got no closer than {closest:F1} m (at {world.Get<Transform>(creature).LocalPosition}, " +
                                        $"door at {world.Get<Mover>(door).Position:F2}; {nav.MeshPlans} plans on the navmesh, {nav.NoRoute} with no way through)");
            Assert.True(world.Get<Transform>(creature).LocalPosition.X > 8.1f, "it did not go through the doorway");
            Assert.True(waited, "it never stood at the door waiting for it to open");
            Assert.Equal(1f, opened);
        }
    }

    // The A/B: a door creatures may not open is a wall to the planner while it is shut, so the same
    // creature never asks it to open and never leaves its room.
    [Fact]
    public void ALockedDoorIsAWallToThePlanner()
    {
        var (engine, world, door) = DoorWorld(locked: true);
        using (engine)
        {
            var creature = Creature(world, new Vector3(3, 0, 0));
            var player = Player(world, new Vector3(13, 0, 8));

            Chase(world, creature, player, 12f);

            Assert.True(world.Get<Transform>(creature).LocalPosition.X < 7.9f, $"it got past a locked door (at {world.Get<Transform>(creature).LocalPosition})");
            Assert.Equal(0f, world.Get<Mover>(door).Position);
            Assert.True(world.Resources.Get<Navigation>().NoRoute > 0, "the planner found a way through a locked door");
        }
    }

    // A platform three metres up with a parapet round it, open on the far side from the player: the way
    // down is a drop link from the opening to the ground.
    private static (Engine, World) LedgeWorld(bool link)
    {
        var (engine, world) = BoxWorld(
            Solid(-12, -1, -10, 30, 0, 16),               // the ground
            Solid(0, 0, 0, 6, 3, 6),                      // the platform
            Solid(5.75f, 3, 0, 6, 3.7f, 6),               // parapet: east, toward the player
            Solid(0, 3, 0, 6, 3.7f, 0.25f),               // south
            Solid(0, 3, 5.75f, 6, 3.7f, 6),               // north
            Solid(0, 3, 0, 0.25f, 3.7f, 2.25f),           // west, either side of the opening
            Solid(0, 3, 3.75f, 0.25f, 3.7f, 6));
        if (link)
        {
            var at = world.Create(Transform.At(new Vector3(0.3f, 3, 3)), "drop");
            world.Add(at, new NavLink { End = new Vector3(-1.8f, -3, 0), Kind = NavLinkKind.Drop });
        }
        return (engine, world);
    }

    // The issue's second "done when": a creature on a ledge walks away from its target to the opening,
    // drops off it by the link, and comes round to the player on the ground.
    [Fact]
    public void ACreatureDropsFromALedgeByALink()
    {
        var (engine, world) = LedgeWorld(link: true);
        using (engine)
        {
            var creature = Creature(world, new Vector3(3, 3, 3));
            var player = Player(world, new Vector3(20, 0, 3));
            bool dropped = false;

            float closest = Chase(world, creature, player, 40f, each: _ =>
            {
                var at = world.Get<Transform>(creature).LocalPosition;
                if (at.X < 0f && at.Y < 0.5f) dropped = true;
            });

            Assert.True(dropped, $"it never came down on the west side of the platform (at {world.Get<Transform>(creature).LocalPosition})");
            Assert.True(closest < 2.5f, $"the creature got no closer than {closest:F1} m (at {world.Get<Transform>(creature).LocalPosition})");
        }
    }

    // The A/B: without the link there is no way down, and the creature stays up there.
    [Fact]
    public void WithoutTheLinkTheCreatureStaysOnTheLedge()
    {
        var (engine, world) = LedgeWorld(link: false);
        using (engine)
        {
            var creature = Creature(world, new Vector3(3, 3, 3));
            var player = Player(world, new Vector3(20, 0, 3));

            Chase(world, creature, player, 15f);

            Assert.True(world.Get<Transform>(creature).LocalPosition.Y > 2.5f, $"it got down without a link (at {world.Get<Transform>(creature).LocalPosition})");
            Assert.True(world.Resources.Get<Navigation>().NoRoute > 0);
        }
    }

    // The issue's third "done when": a crate comes to rest in the doorway a creature is walking to, and it
    // plans again — through the other doorway — without being told. A dynamic body is baked once it lies
    // still, and the change moves `Navigation.Version`, which a path planned before it is checked against.
    [Fact]
    public void ACreatureReplansWhenABlockerComesToRestInItsWay()
    {
        var (engine, world) = BoxWorld(
            Solid(-2, -1, -4, 18, 0, 14),
            Solid(7.9f, 0, -4, 8.1f, 0.9f, 1),            // a wall with a doorway at z 1..2.5, near the line
            Solid(7.9f, 0, 2.5f, 8.1f, 0.9f, 9),          // and another at z 9..10.5, far from it
            Solid(7.9f, 0, 10.5f, 8.1f, 0.9f, 14));
        using (engine)
        {
            var creature = Creature(world, new Vector3(3, 0, -1));
            var player = Player(world, new Vector3(13, 0, -2));
            var nav = world.Resources.Get<Navigation>();

            // Until it has a plan through the near doorway.
            bool Through(float z0, float z1)
            {
                ref readonly var path = ref world.Get<AIState>(creature).Path;
                for (int i = 0; i < path.Count; i++)
                    if (MathF.Abs(path[i].X - 8f) < 1.5f && path[i].Z > z0 && path[i].Z < z1) return true;
                return false;
            }
            int ticks = 0;
            while (!Through(0.5f, 3f) && ticks++ < 5 * 60) world.RunFixed(1f / 60f);
            Assert.True(ticks < 5 * 60, "it never planned through the near doorway");
            int version = nav.Version;

            var crate = world.Create(Transform.At(new Vector3(8, 0.45f, 1.75f)), "crate");
            world.Add(crate, Collider.Box(new Vector3(0.6f, 0.9f, 1.6f)));
            world.Add(crate, RigidBody.Dynamic(200f));

            int replanned = -1;
            float closest = Chase(world, creature, player, 40f, each: i =>
            {
                if (replanned < 0 && Through(8.5f, 11f)) replanned = i;
            });

            Assert.True(nav.Version > version, "the crate coming to rest changed nothing the planner knows about");
            Assert.True(replanned >= 0, "it never planned through the far doorway");
            Assert.True(replanned < 45, $"it took {replanned} ticks to plan again after the crate landed");
            Assert.True(closest < 2.5f, $"the creature got no closer than {closest:F1} m (at {world.Get<Transform>(creature).LocalPosition})");
            Assert.True(MathF.Abs(world.Get<Transform>(crate).LocalPosition.Z - 1.75f) < 0.3f, "the creature pushed the crate out of the way instead");
        }
    }

    // Every other kind of link, crossed the way its kind says: a jump over a gap and a teleport across it
    // (from one platform to another two metres up, with no way down), and a ladder up the side of one.
    [Theory]
    [InlineData(NavLinkKind.Jump)]
    [InlineData(NavLinkKind.Teleport)]
    [InlineData(NavLinkKind.Ladder)]
    public void EachKindOfLinkIsCrossed(NavLinkKind kind)
    {
        var (engine, world) = BoxWorld(
            Solid(-12, -1, -6, 24, 0, 12),                // the ground
            Solid(0, 0, 0, 6, 2, 6),                      // platform A
            Solid(5.75f, 2, 0, 6, 2.7f, 2.25f),           // A's east parapet, open at z 2.25..3.75
            Solid(5.75f, 2, 3.75f, 6, 2.7f, 6),
            Solid(8.5f, 0, 0, 14.5f, 2, 6));              // platform B, 2.5 m across the gap
        using (engine)
        {
            Entity creature, player;
            var link = world.Create(Transform.At(kind == NavLinkKind.Ladder ? new Vector3(-0.4f, 0, 3) : new Vector3(5.6f, 2, 3)), "link");
            if (kind == NavLinkKind.Ladder)
            {
                world.Add(link, new NavLink { End = new Vector3(1.2f, 2, 0), Kind = kind });
                creature = Creature(world, new Vector3(-6, 0, 1));
                player = Player(world, new Vector3(4, 2, 1));
            }
            else
            {
                world.Add(link, new NavLink { End = new Vector3(3.4f, 0, 0), Kind = kind });
                creature = Creature(world, new Vector3(2, 2, 1));
                player = Player(world, new Vector3(12, 2, 1));
            }
            bool crossing = false;

            var expected = NavCrossings.Of(kind);
            float closest = Chase(world, creature, player, 30f, each: _ =>
                crossing |= world.Get<AIState>(creature).Path.Crossing == expected);

            var at = world.Get<Transform>(creature).LocalPosition;
            Assert.True(crossing, $"it never planned over the {kind} link (at {at})");
            Assert.True(closest < 2.5f, $"the creature got no closer than {closest:F1} m (at {at})");
            Assert.True(at.Y > 1.5f, $"it is not up on the platform (at {at})");
        }
    }

    // ---- sage validate --------------------------------------------------------------------------------

    private static string Repo => TestEnv.FolderAbove("Sage.sln");

    // A marker in a room the doors do not reach, and one inside a pillar: each said, at its line, and the
    // level still loads (warnings, not errors). The Sandbox's own levels say nothing (StrictContentTests'
    // Validate_TheSandboxAndHelloHaveNoErrors runs the same check on them).
    [Fact]
    public void ValidateNamesAMarkerNothingCanWalkTo()
    {
        var rooms = ThreeRooms().ToList();
        rooms.Add(Brush(15.75f, 0, 0.75f, 16.25f, 1.2f, 2));   // the second door bricked up
        rooms.Add(Brush(10.5f, 0, 4.5f, 13.5f, 1.2f, 7.5f));   // a pillar in the middle room
        string map = Map(rooms,
            Point("info_player_start", new Vector3(2, 0.25f, 2)),
            Point("info_target", new Vector3(12, 0.25f, 2), "middle"),
            Point("info_target", new Vector3(20, 0.25f, 4), "sealed"),
            Point("info_target", new Vector3(12, 0.25f, 6), "in_pillar"),
            Point("light", new Vector3(20, 3, 4)));

        var mod = TestEnv.NewTempDir();
        Directory.CreateDirectory(Path.Combine(mod, "data"));
        Directory.CreateDirectory(Path.Combine(mod, "maps"));
        File.WriteAllText(Path.Combine(mod, "maps", "rooms.map"), map);
        File.WriteAllText(Path.Combine(mod, "data", "level.json"), """[{ "type": "map", "id": "rooms", "file": "maps/rooms.map" }]""");

        var report = ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = Path.Combine(Repo, "games", "Sandbox"),
            EngineContentDirectory = Path.Combine(Repo, "engine_content"),
            AvailablePlugins = BasePlugins.All(),
            GameModule = new Sandbox.SandboxModule(),
            Mounts = new[] { (mod, "navmod") },
        });

        Assert.True(report.Ok, string.Join("\n", report.Errors));
        var mine = report.Warnings.Where(w => w.Contains("navmesh, #264")).ToList();
        Assert.Equal(2, mine.Count);
        string name = Path.GetFileName(mod);
        // An entity's line is its opening brace's, two above its targetname (1-based).
        int Line(string target) => map.Split('\n').ToList().FindIndex(l => l == $"\"targetname\" \"{target}\"") - 1;
        Assert.Contains(mine, w => w.Contains($"{name}:maps/rooms.map:{Line("sealed")}: 'info_target' 'sealed' cannot be reached from {name}:maps/rooms.map:")
                                   && w.Contains("'info_player_start'"));
        Assert.Contains(mine, w => w.Contains($"{name}:maps/rooms.map:{Line("in_pillar")}: 'info_target' 'in_pillar' stands on nothing a body can walk on"));
        Assert.DoesNotContain(mine, w => w.Contains("'middle'"));
    }
}
