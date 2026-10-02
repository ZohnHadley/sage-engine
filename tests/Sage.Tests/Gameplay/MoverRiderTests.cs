#nullable enable
using System;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Movers as kinematic bodies with a velocity (issue #261): what rests on a lift goes up with it, a crate
// carried by contact and friction, a character by the ground's velocity it reads under its feet.
public class MoverRiderTests
{
    public MoverRiderTests() { _ = TestEnv.UserRoot; }

    private const byte PlayerLayer = 1;

    private static void Tick(World world, int times = 1)
    {
        for (int i = 0; i < times; i++) world.RunFixed(1f / 60f);
    }

    // A two-metre square slab 0.2 thick whose top is at y = 0.2 when it is shut, as a level builds it.
    private static Entity Platform(World world, Vector3 open, float seconds)
    {
        var space = world.Resources.Get<IPhysicsWorld>();
        var closed = new Vector3(0, 0.1f, 0);
        var platform = world.Create(Transform.At(closed), "lift");
        var corners = new[]
        {
            new Vector3(-1, -0.1f, -1), new Vector3(1, -0.1f, -1), new Vector3(1, 0.1f, -1), new Vector3(-1, 0.1f, -1),
            new Vector3(-1, -0.1f, 1), new Vector3(1, -0.1f, 1), new Vector3(1, 0.1f, 1), new Vector3(-1, 0.1f, 1),
        };
        world.Add(platform, space.AddHull(platform, corners, closed));
        world.Add(platform, new Mover { OpenOffset = open, Seconds = seconds, Closed = closed });
        return platform;
    }

    private static (Engine, World) NewWorld()
    {
        var engine = HeadlessApp.Gameplay().Build().Engine;
        var world = engine.CreateWorld("riders");
        var floor = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "floor");
        world.Add(floor, Collider.Box(new Vector3(40, 1, 40)));
        return (engine, world);
    }

    private static Vector3 Position(World world, Entity entity) => world.Get<Transform>(entity).LocalPosition;

    [Fact]
    public void ACrateAndAPlayerRideALiftUpAndStayOnIt()
    {
        var (engine, world) = NewWorld();
        using var _ = engine;
        var lift = Platform(world, new Vector3(0, 3, 0), 3f);
        var crate = world.Create(Transform.At(new Vector3(0.5f, 0.45f, 0.5f)), "crate");
        world.Add(crate, Collider.Box(new Vector3(0.5f, 0.5f, 0.5f)));
        world.Add(crate, RigidBody.Dynamic(5f));
        var player = world.Create(Transform.At(new Vector3(-0.5f, 0.25f, -0.5f)), "player");
        world.AddCharacter(player, PlayerLayer);
        Tick(world, 60);   // settled on the shut lift

        world.IO().FireInput(lift, "Open");
        for (int i = 0; i < 60 * 4; i++)
        {
            Tick(world);
            float top = Position(world, lift).Y + 0.1f;
            Assert.True(Position(world, player).Y > top - 0.1f, $"tick {i}: the player sank into the lift ({Position(world, player).Y} under {top})");
        }

        Assert.Equal(1f, world.Get<Mover>(lift).Position);
        float roof = 3.2f;
        Assert.InRange(Position(world, player).Y, roof - 0.05f, roof + 0.1f);
        Assert.True(world.Get<CharacterController>(player).Grounded, "the player should be standing on the lift");
        Assert.InRange(Position(world, crate).Y, roof + 0.25f - 0.05f, roof + 0.25f + 0.1f);
        Assert.InRange(Position(world, crate).X, 0.3f, 0.7f);   // and it did not slide off
    }

    [Fact]
    public void APlayerStandingOnAMovingPlatformIsCarriedWithIt()
    {
        var (engine, world) = NewWorld();
        using var _ = engine;
        var platform = Platform(world, new Vector3(4, 0, 0), 2f);
        var player = world.Create(Transform.At(new Vector3(0, 0.25f, 0)), "player");
        world.AddCharacter(player, PlayerLayer);
        Tick(world, 30);
        float startX = Position(world, player).X;

        world.IO().FireInput(platform, "Open");
        Tick(world, 60 * 3);

        Assert.Equal(1f, world.Get<Mover>(platform).Position);
        Assert.InRange(Position(world, player).X - startX, 3.8f, 4.2f);
        Assert.True(world.Get<CharacterController>(player).Grounded);

        // Stopped: so is the player.
        float x = Position(world, player).X;
        Tick(world, 30);
        Assert.Equal(x, Position(world, player).X, 3);
        Assert.Equal(Vector3.Zero, world.Get<CharacterController>(player).GroundVelocity);
    }

    [Fact]
    public void AMoverIsAKinematicBodyMovingAtItsSpeedOnceItMoves()
    {
        var (engine, world) = NewWorld();
        using var _ = engine;
        var space = world.Resources.Get<IPhysicsWorld>();
        var lift = Platform(world, new Vector3(0, 3, 0), 3f);
        Assert.True(world.Get<PhysicsBody>(lift).IsStatic, "built as a static, like every brush");

        world.IO().FireInput(lift, "Open");
        Tick(world, 30);

        var body = world.Get<PhysicsBody>(lift);
        Assert.False(body.IsStatic);
        Assert.False(space.IsDynamic(body));
        Assert.InRange(space.VelocityOf(body).Y, 0.99f, 1.01f);   // three metres in three seconds

        Tick(world, 60 * 3);
        Assert.Equal(Vector3.Zero, space.VelocityOf(world.Get<PhysicsBody>(lift)));
        Assert.InRange(space.PoseOf(world.Get<PhysicsBody>(lift)).Position.Y, 3.09f, 3.11f);   // its centre, exactly there
    }
}
