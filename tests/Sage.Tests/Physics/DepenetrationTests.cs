#nullable enable
using System;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The narrow-phase overlap and the character controller's depenetration (issue #259, TODO bug 61): a
// capsule that ends up inside geometry (a door closing on it, a teleport, a spawn) is pushed out over a
// few ticks and walks on, instead of being stuck with every sweep starting inside and seeing nothing.
public class DepenetrationTests
{
    public DepenetrationTests() { _ = TestEnv.UserRoot; }

    private const byte PlayerLayer = 1;

    private static Engine NewEngine() => HeadlessApp.Gameplay().Build().Engine;

    private static Entity Box(World world, Vector3 center, Vector3 size, string name = "box")
    {
        var entity = world.Create(Transform.At(center), name);
        world.Add(entity, Collider.Box(size));
        return entity;
    }

    private static Entity Character(World world, Vector3 feet)
    {
        var entity = world.Create(Transform.At(feet), "character");
        world.AddCharacter(entity, PlayerLayer);
        return entity;
    }

    private static void Walk(World world, Entity character, Vector2 move, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            world.Get<PawnIntent>(character).Move = move;
            world.RunFixed(1f / 60f);
        }
    }

    private static Vector3 Position(World world, Entity entity) => world.Get<Transform>(entity).LocalPosition;

    private static int OverlapsOf(World world, Entity character, Span<OverlapHit> hits)
    {
        var physics = world.Resources.Get<IPhysicsWorld>();
        var controller = world.Get<CharacterController>(character);
        var shape = Collider.Standing(world.Get<Collider>(character).Size.X, controller.Height);
        var pose = new Pose { Position = Position(world, character), Rotation = Quaternion.Identity, Scale = Vector3.One };
        return physics.Overlap(shape, pose, hits, LayerMask.All.Except(PlayerLayer));
    }

    [Fact]
    public void OverlapTestsShapesNotBoundsAndSaysWhichWayIsOut()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("overlap");
        var wall = Box(world, new Vector3(0, 1, 0), new Vector3(1, 2, 1), "wall");   // x from -0.5 to 0.5
        world.RunFixed(1f / 60f);
        var physics = world.Resources.Get<IPhysicsWorld>();
        Span<OverlapHit> hits = new OverlapHit[4];

        // A sphere of radius 0.3 centred at x = 0.7 reaches 0.1 m into the wall's +X face.
        var ball = Collider.Sphere(0.3f);
        int count = physics.Overlap(ball, new Pose { Position = new Vector3(0.7f, 1, 0), Rotation = Quaternion.Identity, Scale = Vector3.One }, hits);
        Assert.Equal(1, count);
        Assert.Equal(wall, hits[0].Entity);
        Assert.InRange(hits[0].Depth, 0.09f, 0.11f);
        Assert.True(Vector3.Dot(hits[0].Normal, Vector3.UnitX) > 0.99f, $"the way out is +X, not {hits[0].Normal}");

        // Near the corner the bounds overlap but the shapes don't: the broad phase would say yes.
        count = physics.Overlap(ball, new Pose { Position = new Vector3(0.75f, 2.2f, 0.75f), Rotation = Quaternion.Identity, Scale = Vector3.One }, hits);
        Assert.Equal(0, count);
        Assert.Equal(1, physics.OverlapBox(new Vector3(0.75f, 2.2f, 0.75f), new Vector3(0.3f), new Entity[4]));

        // Clear of it, nothing; and the asker can leave something out.
        Assert.Equal(0, physics.Overlap(ball, new Pose { Position = new Vector3(2, 1, 0), Rotation = Quaternion.Identity, Scale = Vector3.One }, hits));
        Assert.Equal(0, physics.Overlap(ball, new Pose { Position = new Vector3(0.7f, 1, 0), Rotation = Quaternion.Identity, Scale = Vector3.One }, hits, ignore: wall));
    }

    [Fact]
    public void OverlapReportsTheDeepestFirstAndTruncatesToTheDeepest()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("overlap");
        var shallow = Box(world, new Vector3(-0.6f, 1, 0), new Vector3(1, 2, 1), "shallow");   // its +X face at x = -0.1
        var deep = Box(world, new Vector3(0.4f, 1, 0), new Vector3(1, 2, 1), "deep");          // its -X face at x = -0.1
        world.RunFixed(1f / 60f);
        var physics = world.Resources.Get<IPhysicsWorld>();
        var pose = new Pose { Position = new Vector3(0f, 1, 0), Rotation = Quaternion.Identity, Scale = Vector3.One };

        Span<OverlapHit> two = new OverlapHit[2];
        Assert.Equal(2, physics.Overlap(Collider.Sphere(0.25f), pose, two));
        Assert.Equal(deep, two[0].Entity);
        Assert.Equal(shallow, two[1].Entity);
        Assert.True(two[0].Depth > two[1].Depth);

        Span<OverlapHit> one = new OverlapHit[1];
        Assert.Equal(1, physics.Overlap(Collider.Sphere(0.25f), pose, one));
        Assert.Equal(deep, one[0].Entity);
    }

    [Fact]
    public void ACharacterStandingOnTheFloorOverlapsNothing()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("kcc");
        Box(world, new Vector3(0, -0.5f, 0), new Vector3(20, 1, 20), "floor");
        var character = Character(world, Vector3.Zero);
        Walk(world, character, Vector2.Zero, 30);

        // Resting a skin width above the floor is touching, not overlapping: depenetration must leave a
        // standing character alone, or it would jitter on every floor.
        Assert.Equal(0, OverlapsOf(world, character, new OverlapHit[4]));
        Assert.True(world.Get<CharacterController>(character).Grounded);
        float y = Position(world, character).Y;
        Walk(world, character, Vector2.Zero, 30);
        Assert.Equal(y, Position(world, character).Y, 4);
    }

    // The issue's exit: a character placed inside a box ends up clear of it and able to walk.
    [Fact]
    public void ACharacterPlacedInsideABoxIsPushedOutAndWalksOn()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("kcc");
        Box(world, new Vector3(0, -0.5f, 0), new Vector3(20, 1, 20), "floor");
        Box(world, new Vector3(0, 1, 0), new Vector3(1, 2, 1), "pillar");   // x from -0.5 to 0.5
        world.RunFixed(1f / 60f);                                            // the statics are in the space
        var character = Character(world, new Vector3(0.6f, 0, 0));          // its capsule reaches into the pillar
        Assert.True(OverlapsOf(world, character, new OverlapHit[4]) > 0, "the test needs it to start inside");

        Walk(world, character, Vector2.Zero, 5);

        Assert.Equal(0, OverlapsOf(world, character, new OverlapHit[4]));
        Assert.True(Position(world, character).X > 0.5f, "pushed out of the +X face, the nearest way out");
        Assert.True(world.Get<CharacterController>(character).Grounded);

        // And it walks: away from the pillar along +X.
        float x = Position(world, character).X;
        world.Get<PawnIntent>(character).Yaw = SageMath.YawOf(Quaternion.Identity);
        var forward = SageMath.ForwardFromYaw(world.Get<PawnIntent>(character).Yaw);
        var away = new Vector2(Vector3.Dot(new Vector3(-forward.Z, 0, forward.X), Vector3.UnitX), Vector3.Dot(forward, Vector3.UnitX));
        Walk(world, character, away, 60);
        Assert.True(Position(world, character).X > x + 1f, $"it should have walked on, but went from {x} to {Position(world, character).X}");
    }

    // Deep inside, it comes out over a few ticks rather than in one jump, and never through the far side.
    [Fact]
    public void ACharacterDeepInsideComesOutAFewTicksLaterNeverMoreThanARadiusATick()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("kcc");
        Box(world, new Vector3(0, -0.5f, 0), new Vector3(20, 1, 20), "floor");
        Box(world, new Vector3(0, 1, 0), new Vector3(3, 2, 1), "wall");   // z from -0.5 to 0.5
        world.RunFixed(1f / 60f);
        var character = Character(world, new Vector3(0, 0, 0.1f));         // nearer the +Z face
        float radius = world.Get<Collider>(character).Size.X;

        Vector3 last = Position(world, character);
        int ticks = 0;
        while (OverlapsOf(world, character, new OverlapHit[4]) > 0 && ticks < 30)
        {
            Walk(world, character, Vector2.Zero, 1);
            Vector3 now = Position(world, character);
            Assert.True(Vector3.Distance(last, now) <= radius + 0.03f, $"tick {ticks}: moved {Vector3.Distance(last, now)} m");
            last = now;
            ticks++;
        }

        Assert.InRange(ticks, 2, 10);
        Assert.True(Position(world, character).Z > 0.5f, $"out through the near (+Z) face, at {Position(world, character)}");
    }
}
