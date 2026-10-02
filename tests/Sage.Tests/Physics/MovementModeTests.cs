#nullable enable
using System;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Movement modes in the character controller (issue #267, docs/design/10 "As built (movement modes)"):
// a smooth crouch whose Collider shrinks with it, GoldSrc air-strafing, and Fly / Noclip for editors and
// debugging, chosen by the movement_profile's `mode` or the character's own.
public class MovementModeTests
{
    public MovementModeTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;
    private const byte PlayerLayer = 1;

    private const string Profiles = """
        [
          { "type": "movement_profile", "id": "test_bhop", "mode": "AirStrafe" },
          { "type": "movement_profile", "id": "test_fly", "mode": "Fly" }
        ]
        """;

    private static Engine NewEngine() => HeadlessApp.Gameplay().File("data/movement_modes.json", Profiles).Build().Engine;

    private static Entity Box(World world, Vector3 center, Vector3 size, string name)
    {
        var entity = world.Create(Transform.At(center), name);
        world.Add(entity, Collider.Box(size));
        return entity;
    }

    private static World Ground(Engine engine, string name)
    {
        var world = engine.CreateWorld(name);
        Box(world, new Vector3(0, -0.5f, 0), new Vector3(200, 1, 200), "floor");
        world.RunFixed(Dt);
        return world;
    }

    private static Entity Character(World world, Vector3 feet, string? profile = null)
    {
        var entity = world.Create(Transform.At(feet), "mover");
        world.AddCharacter(entity, PlayerLayer, profile == null ? default : new RecordId("sage", profile));
        return entity;
    }

    private static void Drive(World world, Entity character, Vector2 move, int ticks, bool crouch = false, bool jump = false, bool run = false)
    {
        var actions = world.Engine!.Actions;
        for (int i = 0; i < ticks; i++)
        {
            ref var intent = ref world.Get<PawnIntent>(character);
            intent.Move = move;
            var held = default(ActionMask);
            if (crouch) held = held.With(actions.Get("Crouch"));
            if (jump) held = held.With(actions.Get("Jump"));
            if (run) held = held.With(actions.Get("Run"));
            intent.Held = held;
            intent.Pressed = default;
            world.RunFixed(Dt);
        }
    }

    private static Vector3 Feet(World world, Entity entity) => world.Get<Transform>(entity).LocalPosition;
    private static CharacterController Controller(World world, Entity entity) => world.Get<CharacterController>(entity);
    private static Pose At(Vector3 position) => new() { Position = position, Rotation = Quaternion.Identity, Scale = Vector3.One };
    private static float HeightOf(in Collider collider) => collider.Center.Y * 2f;

    // The issue's first exit: a crouched character fits under a low lintel, and what another entity casts at
    // standing head height passes over its collider instead of stopping at a capsule it no longer has.
    [Fact]
    public void ACrouchedCharacterFitsUnderALintelAndHeadHeightSweepsPassOverIt()
    {
        using var engine = NewEngine();
        var world = Ground(engine, "lintel");
        var physics = world.Resources.Get<IPhysicsWorld>();
        // A doorway two metres wide whose lintel's underside is at 1.3 m, across the way at z = -3.
        Box(world, new Vector3(-1.5f, 1.5f, -3), new Vector3(1, 3, 0.4f), "post_left");
        Box(world, new Vector3(1.5f, 1.5f, -3), new Vector3(1, 3, 0.4f), "post_right");
        Box(world, new Vector3(0, 2.15f, -3), new Vector3(2, 1.7f, 0.4f), "lintel");
        world.RunFixed(Dt);
        var character = Character(world, Vector3.Zero);   // yaw 0 looks at -Z: at the doorway
        Drive(world, character, Vector2.Zero, 5);
        var body = world.Get<PhysicsBody>(character);
        var profile = MovementProfileRecord.Fallback;

        // Standing, it walks into the lintel and stops in front of it.
        Drive(world, character, new Vector2(0, 1), 90);
        Assert.True(Feet(world, character).Z > -2.6f, $"a standing character should not fit under a 1.3 m lintel, at {Feet(world, character)}");

        // Crouching is smooth: part of the way down after a few ticks, and its collider follows.
        Drive(world, character, Vector2.Zero, 4, crouch: true);
        float partway = Controller(world, character).Height;
        Assert.True(Controller(world, character).Crouching);
        Assert.InRange(partway, profile.CrouchHeight + 0.05f, profile.StandHeight - 0.05f);
        Assert.Equal(partway, HeightOf(world.Get<Collider>(character)), 3);
        Drive(world, character, Vector2.Zero, 20, crouch: true);
        Assert.Equal(profile.CrouchHeight, Controller(world, character).Height, 3);
        Assert.Equal(profile.CrouchHeight, HeightOf(world.Get<Collider>(character)), 3);
        Assert.Equal(body, world.Get<PhysicsBody>(character));   // the same body, reshaped in place

        // Crouched, it goes under the lintel and comes out the other side.
        Drive(world, character, new Vector2(0, 1), 150, crouch: true);
        var feet = Feet(world, character);
        Assert.True(feet.Z < -3.6f, $"a crouched character should fit under the lintel, at {feet}");

        // In the open past it, an enemy's sweep and ray at standing head height pass over it; at its knees they hit.
        // (From its side, across the open floor: the doorway is behind it.)
        var enemy = new Vector3(feet.X - 5f, 1.6f, feet.Z);
        var head = physics.Sweep(Collider.Sphere(0.1f), At(enemy), Vector3.UnitX, 10f);
        Assert.False(head.Hit, $"a head-height sweep should pass over a crouched character, but hit {World.Describe(head.Entity)}");
        Assert.False(physics.Raycast(enemy, Vector3.UnitX, 10f).Hit);
        var knee = physics.Sweep(Collider.Sphere(0.1f), At(enemy with { Y = 0.5f }), Vector3.UnitX, 10f);
        Assert.True(knee.Hit);
        Assert.Equal(character, knee.Entity);

        // Letting go of Crouch stands it up over CrouchTime, and the head-height ray hits it again.
        Drive(world, character, Vector2.Zero, 30);
        Assert.False(Controller(world, character).Crouching);
        Assert.Equal(profile.StandHeight, Controller(world, character).Height, 3);
        Assert.Equal(profile.StandHeight, HeightOf(world.Get<Collider>(character)), 3);
        var hit = physics.Raycast(enemy, Vector3.UnitX, 10f);
        Assert.True(hit.Hit);
        Assert.Equal(character, hit.Entity);
    }

    // Under the lintel there is no room to stand: letting go of Crouch keeps it crouched, collider and all,
    // until it has walked out.
    [Fact]
    public void ACrouchedCharacterUnderALintelStaysDownUntilItHasRoom()
    {
        using var engine = NewEngine();
        var world = Ground(engine, "low");
        Box(world, new Vector3(0, 2.15f, -3), new Vector3(4, 1.7f, 2f), "lintel");   // underside at 1.3 m, z -2..-4
        world.RunFixed(Dt);
        var character = Character(world, new Vector3(0, 0, -3));
        Drive(world, character, Vector2.Zero, 30, crouch: true);   // crouched where it stands
        Assert.Equal(MovementProfileRecord.Fallback.CrouchHeight, Controller(world, character).Height, 3);

        Drive(world, character, Vector2.Zero, 30);
        Assert.True(Controller(world, character).Crouching, "no headroom: it should stay crouched");
        Assert.Equal(MovementProfileRecord.Fallback.CrouchHeight, HeightOf(world.Get<Collider>(character)), 3);

        // Walks out (crouched speed, with Crouch released) and stands up in the open.
        Drive(world, character, new Vector2(0, 1), 120);
        Assert.True(Feet(world, character).Z < -4.4f, $"should have walked out, at {Feet(world, character)}");
        Assert.False(Controller(world, character).Crouching);
        Assert.Equal(MovementProfileRecord.Fallback.StandHeight, Controller(world, character).Height, 3);
    }

    // The issue's second exit: an air-strafe profile record gains speed by strafing in the air, past the
    // ground's top speed, bunny-hopping; the walk profile doing the same never passes it.
    [Fact]
    public void AnAirStrafeProfileBunnyHopsPastTheGroundMaximum()
    {
        using var engine = NewEngine();
        var profile = engine.Records.Get<MovementProfileRecord>(new RecordId("sage", "test_bhop"));
        Assert.Equal(MovementMode.AirStrafe, profile.Mode);

        float strafed = BunnyHop(engine, "test_bhop");
        float walked = BunnyHop(engine, null);
        Assert.True(strafed > profile.RunSpeed * 1.3f, $"air-strafing should beat {profile.RunSpeed} m/s on the ground, reached {strafed}");
        Assert.True(walked <= MovementProfileRecord.Fallback.RunSpeed + 0.05f, $"the walk profile should not gain speed in the air, reached {walked}");
    }

    // Runs up to speed, then jumps on every landing and, in the air, holds Run and wishes at right angles
    // to its velocity — what holding a strafe key while turning the mouse toward it does. Returns the
    // fastest horizontal speed it reached after the run-up.
    private static float BunnyHop(Engine engine, string? profile)
    {
        var world = Ground(engine, profile ?? "walk");
        var character = Character(world, Vector3.Zero, profile);
        Drive(world, character, new Vector2(0, 1), 90, run: true);
        var actions = engine.Actions;
        var forward = SageMath.ForwardFromYaw(0f);
        var right = new Vector3(-forward.Z, 0, forward.X);
        float fastest = 0f;
        int jumps = 0;
        for (int i = 0; i < 600; i++)
        {
            var controller = Controller(world, character);
            var velocity = controller.Velocity with { Y = 0 };
            var sideways = velocity.LengthSquared() > 1e-4f ? Vector3.Normalize(new Vector3(-velocity.Z, 0, velocity.X)) : right;
            ref var intent = ref world.Get<PawnIntent>(character);
            intent.Yaw = 0f;
            intent.Move = new Vector2(Vector3.Dot(sideways, right), Vector3.Dot(sideways, forward));
            intent.Held = default(ActionMask).With(actions.Get("Run"));
            intent.Pressed = controller.Grounded ? default(ActionMask).With(actions.Get("Jump")) : default;
            if (controller.Grounded) jumps++;
            world.RunFixed(Dt);
            fastest = MathF.Max(fastest, (Controller(world, character).Velocity with { Y = 0 }).Length());
        }
        Assert.True(jumps > 5, $"should have hopped, jumped {jumps} times");
        return fastest;
    }

    // Fly (a profile's mode): no gravity, moves where it looks and up on Jump, and stops at a wall.
    // Noclip (the character's own mode over its profile's): goes through the wall.
    [Fact]
    public void FlyIgnoresGravityButCollidesAndNoclipGoesThroughWalls()
    {
        using var engine = NewEngine();
        var world = Ground(engine, "fly");
        Box(world, new Vector3(0, 5, -4), new Vector3(10, 10, 0.5f), "wall");   // z -3.75..-4.25
        world.RunFixed(Dt);
        var character = Character(world, new Vector3(0, 0, 0), "test_fly");

        // Up on Jump, and hangs there with nothing pressed.
        Drive(world, character, Vector2.Zero, 60, jump: true);
        float y = Feet(world, character).Y;
        Assert.True(y > 3f, $"flying up at 8 m/s for a second, but at {y}");
        Drive(world, character, Vector2.Zero, 60);
        Assert.InRange(Feet(world, character).Y, y, y + 1f);   // no gravity, and it stops
        Assert.False(Controller(world, character).Grounded);

        // Into the wall: stopped in front of it.
        Drive(world, character, new Vector2(0, 1), 90);
        Assert.InRange(Feet(world, character).Z, -3.75f + 0.3f, -3f);

        // Noclip, the character's own: straight through it.
        world.Get<CharacterController>(character).Mode = MovementMode.Noclip;
        Drive(world, character, new Vector2(0, 1), 60);
        Assert.True(Feet(world, character).Z < -5f, $"noclip should go through the wall, at {Feet(world, character)}");

        // Back to its profile's mode, and a walk profile falls to the floor.
        world.Get<CharacterController>(character).Mode = MovementMode.Walk;
        Drive(world, character, Vector2.Zero, 120);
        Assert.True(Controller(world, character).Grounded);
        Assert.InRange(Feet(world, character).Y, -0.01f, 0.1f);
    }

    // `noclip` and `fly` are cheats that toggle the player's own mode, and toggling again gives the
    // profile's back.
    [Fact]
    public void NoclipAndFlyCommandsToggleThePlayersMode()
    {
        using var engine = NewEngine();
        var world = Ground(engine, "cheat");
        var player = Character(world, Vector3.Zero);
        player.AddTag<PlayerControlled>();
        var npc = Character(world, new Vector3(3, 0, 0));

        engine.CVars.Execute("noclip");   // a cheat: nothing without sv_cheats
        Assert.Equal(MovementMode.Default, Controller(world, player).Mode);

        engine.CVars.Execute("sv_cheats 1");
        engine.CVars.Execute("noclip");
        Assert.Equal(MovementMode.Noclip, Controller(world, player).Mode);
        Assert.Equal(MovementMode.Default, Controller(world, npc).Mode);
        engine.CVars.Execute("fly");
        Assert.Equal(MovementMode.Fly, Controller(world, player).Mode);
        engine.CVars.Execute("fly");
        Assert.Equal(MovementMode.Default, Controller(world, player).Mode);
    }
}
