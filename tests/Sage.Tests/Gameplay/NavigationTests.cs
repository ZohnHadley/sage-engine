#nullable enable
using System;
using System.Numerics;
using Friflo.Engine.ECS;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Finding a way round things (docs/design/16 §3.4, TODO F23).
//
// The grid and the search are pure arithmetic, so a maze can be written out as text and the answer
// checked by hand. The creature that *follows* the answer is tested at the bottom, through the real
// modules with real physics, because "it goes round the wall" is the only claim that matters.
public class NavigationTests
{
    public NavigationTests() { _ = TestEnv.UserRoot; }

    // Reads a map where '#' is blocked, 'S' is the start and 'G' the goal. Row 0 is the *bottom* (+Z is
    // up in the strings' terms), so what you read is what you would see looking down at the world.
    private static (NavGrid Grid, Vector3 Start, Vector3 Goal) Map(params string[] rows)
    {
        var grid = new NavGrid();
        int height = rows.Length, width = rows[0].Length;

        // A window exactly the size of the map: from its corner to its far corner, with no padding.
        grid.Open(new Vector3(0, 0, 0), new Vector3(width - 1, 0, height - 1), 1f, padding: 0f);
        Assert.Equal(width, grid.Width);
        Assert.Equal(height, grid.Height);

        Vector3 start = Vector3.Zero, goal = new(width - 1, 0, height - 1);
        for (int y = 0; y < height; y++)
        {
            string row = rows[height - 1 - y];
            for (int x = 0; x < width; x++)
                switch (row[x])
                {
                    case '#': grid.Block(x, y); break;
                    case 'S': start = grid.CellCentre(x, y); break;
                    case 'G': goal = grid.CellCentre(x, y); break;
                }
        }
        return (grid, start, goal);
    }

    // Walking the corners a search returned, in a straight line between each pair: every cell it passes
    // through must be open, or the "path" goes through a wall.
    private static void AssertWalkable(NavGrid grid, Vector3 from, int count, Span<Vector3> corners)
    {
        Assert.True(count > 0, "there is no path at all");
        var at = from;
        for (int i = 0; i < count; i++)
        {
            Assert.True(grid.LineIsClear(at, corners[i]),
                $"corner {i} at {corners[i]} is not reachable in a straight line from {at}");
            at = corners[i];
        }
    }

    // Nothing in the way: the answer is "walk at it", one corner, and no search worth the name.
    [Fact]
    public void AnOpenFieldIsOneStraightLine()
    {
        var (grid, start, goal) = Map(
            ".....",
            ".....",
            "S...G");

        Span<Vector3> corners = stackalloc Vector3[NavPath.MaxCorners];
        int count = grid.Search(start, goal, corners);

        Assert.Equal(1, count);
        Assert.Equal(goal, corners[0]);
        Assert.True(grid.LineIsClear(start, goal));
    }

    // The thing F23 exists for: a wall between them, with a way round it.
    [Fact]
    public void AWallIsWalkedAround()
    {
        var (grid, start, goal) = Map(
            ".........",
            "....#....",
            "....#....",
            "S...#...G");

        Assert.False(grid.LineIsClear(start, goal), "the wall is not in the way, so this proves nothing");

        Span<Vector3> corners = stackalloc Vector3[NavPath.MaxCorners];
        int count = grid.Search(start, goal, corners);

        AssertWalkable(grid, start, count, corners);
        Assert.Equal(goal, corners[count - 1]);
        Assert.True(count <= 3, $"going round one wall took {count} corners; the path is not being straightened");
    }

    // A door is the interesting case: the way through is narrow, and the path has to aim at it.
    [Fact]
    public void ADoorwayIsFoundAndAimedAt()
    {
        var (grid, start, goal) = Map(
            "#########",
            "#...G...#",
            "####.####",
            "####.####",
            "####.####",
            "#...S...#",
            "#########");

        Span<Vector3> corners = stackalloc Vector3[NavPath.MaxCorners];
        int count = grid.Search(start, goal, corners);

        AssertWalkable(grid, start, count, corners);
        // Every corner has to be *at* the door's column or beyond it: the only gap is x = 4.
        Assert.Contains(corners[..count].ToArray(), c => MathF.Abs(c.X - 4f) < 0.01f);
    }

    // Sealed in: the search says so rather than searching for ever or pretending.
    [Fact]
    public void NoWayThroughIsAnAnswer()
    {
        var (grid, start, goal) = Map(
            ".........",
            "..#####..",
            "..#...#..",
            "..#.S.#..",
            "..#####..",
            "....G....");

        Span<Vector3> corners = stackalloc Vector3[NavPath.MaxCorners];
        int count = grid.Search(start, goal, corners);

        Assert.Equal(0, count);
        Assert.Equal(1, grid.Failures);
    }

    // A search that cannot finish inside its budget gives up with an answer, not a frame spike: the
    // caller walks straight at the target, as it did before there was any pathfinding.
    [Fact]
    public void TheNodeBudgetIsRespected()
    {
        // A long corridor doubling back on itself: cheap to describe, expensive to search.
        var (grid, start, goal) = Map(
            "S.......#",
            "#######.#",
            "#.......#",
            "#.#######",
            "#.......#",
            "#######.#",
            "#......G#");

        Span<Vector3> corners = stackalloc Vector3[NavPath.MaxCorners];
        Assert.Equal(0, grid.Search(start, goal, corners, maxNodes: 8));
        Assert.True(grid.LastNodes <= 8, $"the search looked at {grid.LastNodes} cells with a budget of 8");

        // The same maze with a real budget is solvable, so the failure above was the budget and not the
        // maze — a test that cannot tell those apart is not testing the budget.
        Assert.True(grid.Search(start, goal, corners, maxNodes: 4096) > 0);
    }

    // A diagonal between two blocked cells is a gap a body does not fit through, whatever the geometry
    // of the grid says.
    [Fact]
    public void AShoulderDoesNotFitThroughADiagonalGap()
    {
        var (grid, start, goal) = Map(
            "..#G",
            "..#.",
            "S#..",
            "....");

        Span<Vector3> corners = stackalloc Vector3[NavPath.MaxCorners];
        int count = grid.Search(start, goal, corners);

        AssertWalkable(grid, start, count, corners);   // the checker refuses corner-cutting too
    }

    // A goal standing inside something — on a crate, in a doorway's frame — is answered with the nearest
    // place a body can stand, so a creature still walks up to it instead of giving up.
    [Fact]
    public void AGoalInsideAnObstacleIsApproachedAnyway()
    {
        var (grid, start, _) = Map(
            ".....",
            "..#..",
            "S....");
        var inside = grid.CellCentre(2, 1);

        Span<Vector3> corners = stackalloc Vector3[NavPath.MaxCorners];
        int count = grid.Search(start, inside, corners);

        Assert.True(count > 0, "a target standing in a wall made the creature give up");
        Assert.Equal(0, grid.Failures);
    }

    // The agent's own width is what makes a path walkable rather than merely open: a 0.35 m body needs
    // the cells either side of a wall marked too.
    [Fact]
    public void AnObstacleIsGrownByTheBodysRadius()
    {
        var grid = new NavGrid();
        grid.Open(new Vector3(0, 0, 0), new Vector3(10, 0, 0), 1f, padding: 2f);

        // A 1 m cube at x = 5: without the radius it blocks one column, with it, three.
        grid.BlockBox(new Vector3(4.5f, 0, -0.5f), new Vector3(5.5f, 0, 0.5f), grow: 0f);
        int narrow = grid.BlockedCells();

        grid.Open(new Vector3(0, 0, 0), new Vector3(10, 0, 0), 1f, padding: 2f);
        grid.BlockBox(new Vector3(4.5f, 0, -0.5f), new Vector3(5.5f, 0, 0.5f), grow: 0.35f);

        Assert.True(grid.BlockedCells() > narrow,
            "the body's radius did not widen the obstacle, so paths will clip its corners");
    }

    // Ground too steep to climb is not a path, whatever is standing on it. Sampled at the terrain's own
    // resolution rather than the grid's — a heightfield sample is 8 m apart, so asking per 1 m cell is
    // sixty-four questions with one answer between them.
    private sealed class Cliff : ITerrainGenerator
    {
        // Flat, then a wall of hillside across the middle: 40 m up over one 8 m step.
        public void Generate(SectorCoord coord, Heightfield heights, int seed)
        {
            for (int z = 0; z < heights.Resolution; z++)
                for (int x = 0; x < heights.Resolution; x++)
                    heights.Heights[z * heights.Resolution + x] = z >= heights.Resolution / 2 ? 40f : 0f;
        }
    }

    [Fact]
    public void GroundTooSteepToClimbIsNotAPath()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var terrain = new Terrain { Origin = world.Origin(), Generator = new Cliff() };
            world.Resources.Add(terrain);   // what sage.streaming would install
            terrain.Load(SectorCoord.Zero);

            var nav = world.Resources.Get<Navigation>();
            var grid = nav.Grid;

            // A window on flat ground either side of the step, at the sector's middle.
            var flat = new Vector3(100, 0, Terrain.SectorSize / 2 - 40);
            var beyond = new Vector3(100, 0, Terrain.SectorSize / 2 + 40);

            var path = new NavPath();
            bool planned = nav.Plan(world, world.Resources.Get<IPhysicsWorld>(), flat, beyond,
                                    radius: 0.35f, stepHeight: 0.45f, maxSlopeDegrees: 50f,
                                    self: default, target: default, path: ref path);

            Assert.False(planned, "a creature planned a way up a forty-metre cliff");
            Assert.True(grid.BlockedCells() > 0, "the cliff blocked no cells at all");
            Assert.Equal(1, nav.NoRoute);
        }
    }

    // ---- through the real thing -----------------------------------------------------------------

    private const string Records = """
        [{ "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "tag", "id": "state.dead" },
         { "type": "ai_profile", "id": "default_ai", "sightRange": 60, "meleeRange": 1.8, "thinkRate": 20 },
         { "type": "ai_schedule", "id": "idle", "tasks": [{ "task": "Wait", "seconds": 1.5 }], "interrupts": ["SeeEnemy"] },
         { "type": "ai_schedule", "id": "chase", "tasks": [{ "task": "MoveToTarget", "distance": 1.6 }], "interrupts": ["EnemyInMeleeRange", "LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "melee_attack", "tasks": ["FaceTarget", { "task": "Wait", "seconds": 0.4 }], "interrupts": ["LostEnemy", "NoEnemy"] }]
        """;

    private static (Engine, World) NewWorld()
    {
        var engine = HeadlessApp.Gameplay().File("data/nav.json", Records).Build().Engine;

        var world = engine.CreateWorld("nav");
        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        world.Add(ground, Collider.Box(new Vector3(400, 1, 400)));
        return (engine, world);
    }

    // A fence: 12 m wide, waist high, with the player on the other side of the middle of it.
    //
    // **Waist high on purpose.** A creature sees over it (perception is a ray at eye height, 16 §3.4) so
    // it gives chase, but it cannot walk through it (a body is stopped by anything taller than its step
    // height). That is the case F23 is for, and the case a tall wall cannot test: a creature that cannot
    // see the player never chases it at all, so nothing would move for either reason.
    private static void Wall(World world, Vector3 centre, float width, float height = 1.2f)
    {
        var wall = world.Create(Transform.At(centre), "wall");
        world.Add(wall, Collider.Box(new Vector3(width, height, 1f)));
    }

    private static Entity Creature(World world, Vector3 position)
    {
        var entity = world.Create(Transform.At(position), "creature");
        world.AddCharacter(entity, world.Resources.Get<IPhysicsWorld>().Layers.Enemy);
        world.Add(entity, new AIState { Schedule = AIThinkSystem.Schedules.Idle });
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

    private static float Run(World world, Entity creature, Entity player, int seconds)
    {
        float closest = float.MaxValue;
        for (int i = 0; i < seconds * 60; i++)
        {
            world.RunFixed(1f / 60f);
            closest = MathF.Min(closest, SageMath.DistanceXZ(world.Get<Transform>(creature).LocalPosition,
                                                             world.Get<Transform>(player).LocalPosition));
        }
        return closest;
    }

    [Fact]
    public void ACreatureWalksRoundAWallToReachThePlayer()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            Wall(world, new Vector3(0, 0.6f, 0), width: 12f);
            var creature = Creature(world, new Vector3(0, 0, 6));
            var player = Player(world, new Vector3(0, 0, -6));

            float closest = Run(world, creature, player, seconds: 20);

            Assert.True(closest < 2.5f,
                $"the creature got no closer than {closest:F1} m: it is still leaning on the wall");
        }
    }

    // Three walls in a U, open away from the player, with the creature inside it.
    //
    // **Concave is the point.** A fence can be escaped by sliding along it, which raycast steering does
    // by accident, so a fence cannot tell pathfinding and steering apart. A pen cannot: steering walks
    // into the back wall, turns along it, meets a side wall and stops in the corner. Getting out means
    // going the *wrong way first*, which is the thing only a plan does.
    private static void Pen(World world, Vector3 centre, float half = 4f)
    {
        Wall(world, centre + new Vector3(0, 0.6f, -half), width: half * 2f + 1f);          // back
        var left = world.Create(Transform.At(centre + new Vector3(-half, 0.6f, 0)), "pen_left");
        world.Add(left, Collider.Box(new Vector3(1f, 1.2f, half * 2f)));
        var right = world.Create(Transform.At(centre + new Vector3(half, 0.6f, 0)), "pen_right");
        world.Add(right, Collider.Box(new Vector3(1f, 1.2f, half * 2f)));
    }

    [Fact]
    public void ACreatureWalksOutOfAPenToReachThePlayer()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            Pen(world, Vector3.Zero);
            var creature = Creature(world, new Vector3(0, 0, 0));
            var player = Player(world, new Vector3(0, 0, -12));

            float closest = Run(world, creature, player, seconds: 25);

            Assert.True(closest < 2.5f, $"the creature got no closer than {closest:F1} m: it is still in the pen");
        }
    }

    // The A/B, and the reason `nav_enabled` exists: with navigation off, the same creature in the same
    // pen does what it used to — walks at the wall between it and the player and stays there. If this
    // ever fails, the test above is passing for some other reason and needs a harder obstacle.
    [Fact]
    public void WithoutNavigationTheSameCreatureStaysInThePen()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            // Through the cvar, because that is how anybody turns it off — and it is a cheat, so cheats
            // have to be on first, exactly as in the game.
            engine.CVars.Execute("sv_cheats 1");
            engine.CVars.Execute("nav_enabled 0");
            Pen(world, Vector3.Zero);
            var creature = Creature(world, new Vector3(0, 0, 0));
            var player = Player(world, new Vector3(0, 0, -12));

            float closest = Run(world, creature, player, seconds: 25);

            Assert.True(closest > 6f,
                $"with navigation off the creature reached {closest:F1} m, so the pen is not holding it");
        }
    }

    // A path is a list of positions in origin space, and the origin moves (R6). Nothing else shifts a
    // creature's corners or the place it last saw somebody, so the AI has to — and before it did, a
    // creature a kilometre and a half from where it started walked at a corner 1024 m away.
    [Fact]
    public void APathMovesWithTheWorld()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            Pen(world, Vector3.Zero);
            var creature = Creature(world, new Vector3(0, 0, 0));
            var player = Player(world, new Vector3(0, 0, -12));

            // Long enough to have seen the player, planned a way out and started walking it.
            for (int i = 0; i < 60; i++) world.RunFixed(1f / 60f);

            var before = world.Get<AIState>(creature);
            Assert.True(before.Path.Count > 0, "no path to rebase, so this proves nothing");
            var corner = before.Path[before.Path.Step];
            var lastSeen = before.LastSeen;

            var offset = world.Rebase(new SectorCoord(1, 0));
            Assert.NotEqual(Vector3.Zero, offset);

            var after = world.Get<AIState>(creature);
            Assert.Equal(corner + offset, after.Path[after.Path.Step]);
            Assert.Equal(lastSeen + offset, after.LastSeen);

            // And it is still the same distance from the creature, which is the point: a rebase must be
            // invisible to anything that only ever asks "how far".
            Assert.Equal(SageMath.DistanceXZ(world.Get<Transform>(creature).LocalPosition, corner + offset),
                         SageMath.DistanceXZ(world.Get<Transform>(creature).LocalPosition, after.Path[after.Path.Step]), 3);
        }
    }

    // Planning is budgeted, so a crowd cannot cost a hundred searches in one tick. The rest keep last
    // tick's path and ask again next tick, which nobody can see.
    //
    // Measured over a couple of seconds rather than the first two ticks: a creature's first look at the
    // world is taken in the Commands phase, before the statics of that same tick exist (PrePhysics), so
    // the opening answer to "is the way clear?" is always yes. The crowd settles into asking together,
    // because they all plan at the same moment and all wait the same half second.
    [Fact]
    public void PlanningIsBudgetedPerTick()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var nav = world.Resources.Get<Navigation>();
            nav.PlansPerTick = 2;

            Pen(world, Vector3.Zero);
            Player(world, new Vector3(0, 0, -12));
            for (int i = 0; i < 6; i++) Creature(world, new Vector3(-5 + i * 2, 0, 6));

            int busiestTick = 0;
            for (int t = 0; t < 120; t++)
            {
                int before = nav.Plans;
                world.RunFixed(1f / 60f);
                busiestTick = Math.Max(busiestTick, nav.Plans - before);
            }

            Assert.True(busiestTick <= 2, $"{busiestTick} creatures planned in one tick with a budget of 2");
            Assert.True(nav.Refused > 0, "nothing was ever held over, so the budget was never reached");
            Assert.True(nav.Plans > 2, "nobody planned at all, so the budget proves nothing");
        }
    }
}
