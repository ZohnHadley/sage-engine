#nullable enable
using System;
using System.Numerics;
using Friflo.Engine.ECS;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// The kinematic character controller (docs/design/10 §3, TODO F7). All headless: the controller is
// simulation code, so these are the same paths a dedicated server would run.
public class CharacterTests
{
    public CharacterTests() { _ = TestEnv.UserRoot; }

    private const byte PlayerLayer = 1;

    private static Engine NewEngine()
    {
        var cvars = new CVarRegistry();
        var engine = new Engine(cvars, CoreCVars.Register(cvars));
        engine.Modules.Add(new PhysicsModule());
        engine.Modules.AddGameplay();
        engine.Modules.InitAll();
        engine.Modules.StartAll();
        return engine;
    }

    private static Entity Floor(World world, Vector3 center, Vector3 size)
    {
        var entity = world.Create(Transform.At(center), "floor");
        world.Add(entity, Collider.Box(size));
        return entity;
    }

    // A character sweeps against everything *except its own layer*, which is what lets creatures pass
    // through each other — and which makes the **default** layer a trap, because terrain collision and
    // every unlabelled static live there. A character left on it walks through the world and falls for
    // ever, with nothing in the log to say why.
    //
    // This cost an hour while writing the guide's example game: the player fell, the camera followed it
    // down, and the screen showed nothing but sky in every direction. So it says so now.
    [Fact]
    public void ACharacterOnTheDefaultLayerIsToldThatItWillIgnoreTheGround()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("layers");
        var layers = world.Resources.Get<PhysicsSpace>().Layers;

        using var log = new CaptureSink();
        var entity = world.Create(Transform.At(Vector3.Zero), "forgetful");
        world.AddCharacter(entity, layers.Default);

        Assert.Contains(log.Entries, e => e.Message.Contains("default") && e.Message.Contains("terrain"));
    }

    [Fact]
    public void ACharacterOnItsOwnLayerIsNotNagged()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("layers");

        using var log = new CaptureSink();
        var entity = world.Create(Transform.At(Vector3.Zero), "sensible");
        world.AddCharacter(entity, PlayerLayer);

        // The other half of a warning that is worth having: it must not fire when nothing is wrong, or
        // it teaches you to ignore it.
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("physics layer"));
    }

    private static Entity Character(World world, Vector3 feet)
    {
        var entity = world.Create(Transform.At(feet), "character");
        world.AddCharacter(entity, PlayerLayer);
        return entity;
    }

    // Placing a character used to be four Add calls a game had to get right, and PawnIntent.Yaw was
    // never seeded, so the first tick spun every creature to yaw 0 and threw away the direction the
    // scene placed it facing (review #43).
    [Fact]
    public void ACharacterKeepsFacingTheWayItWasPlaced()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("characters");
        Floor(world, new Vector3(0, -0.5f, 0), new Vector3(20, 1, 20));

        float yaw = 90f * MathF.PI / 180f;
        var entity = world.Create(new Transform
        {
            LocalPosition = Vector3.Zero,
            LocalRotation = SageMath.RotationFromYaw(yaw),
            LocalScale = Vector3.One,
        }, "sentry");
        world.AddCharacter(entity, PlayerLayer);

        Assert.Equal(yaw, world.Get<PawnIntent>(entity).Yaw, 3);

        Walk(world, entity, Vector2.Zero, 10);
        Assert.Equal(yaw, SageMath.YawOf(world.Get<Transform>(entity).LocalRotation), 3);
    }

    // Placed a little inside the ground — which is what happens when a scene's height data and the
    // collision mesh disagree by a few centimetres — a character used to fall through the world for
    // ever: its sweeps started overlapping, which the space reports as nothing at all (review #55).
    [Fact]
    public void ACharacterStandingInsideTheGroundIsPutBackOnTopOfIt()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("characters");
        Floor(world, new Vector3(0, -0.5f, 0), new Vector3(20, 1, 20));   // its top is y = 0

        var sunk = world.Create(Transform.At(new Vector3(0, -0.3f, 0)), "sunk");
        world.AddCharacter(sunk, PlayerLayer);

        Walk(world, sunk, Vector2.Zero, 10);

        Assert.InRange(Position(world, sunk).Y, -0.05f, 0.1f);
        Assert.True(world.Get<CharacterController>(sunk).Grounded, "and it should know it is standing on something");
    }

    private static void Walk(World world, Entity character, Vector2 move, int ticks, ActionMask held = default, ActionMask pressed = default)
    {
        for (int i = 0; i < ticks; i++)
        {
            ref var intent = ref world.Get<PawnIntent>(character);
            intent.Move = move;
            intent.Held = held;
            intent.Pressed = i == 0 ? pressed : default;   // a press lasts one tick
            world.RunFixed(1f / 60f);
        }
    }

    private static Vector3 Position(World world, Entity entity) => world.Get<Transform>(entity).LocalPosition;

    [Fact]
    public void FallsAndLandsOnTheFloor()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("kcc");
        Floor(world, new Vector3(0, -0.5f, 0), new Vector3(40, 1, 40));
        var character = Character(world, new Vector3(0, 4, 0));

        Walk(world, character, Vector2.Zero, 120);

        Assert.True(world.Get<CharacterController>(character).Grounded);
        Assert.InRange(Position(world, character).Y, 0f, 0.03f);   // on the floor, one skin width off it
    }

    [Fact]
    public void WalksForwardAtTheProfilesSpeed()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("kcc");
        Floor(world, new Vector3(0, -0.5f, 0), new Vector3(200, 1, 200));
        var character = Character(world, new Vector3(0, 0.1f, 0));
        Walk(world, character, Vector2.Zero, 30);              // settle

        var start = Position(world, character);
        Walk(world, character, new Vector2(0, 1), 60);          // one second forward (yaw 0 = toward -Z)
        var end = Position(world, character);

        Assert.InRange(start.Z - end.Z, 3.8f, 4.4f);            // ~4.2 m/s after a short acceleration
        Assert.Equal(start.X, end.X, 2);
    }

    [Fact]
    public void SlidesAlongAWallInsteadOfStopping()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("kcc");
        Floor(world, new Vector3(0, -0.5f, 0), new Vector3(200, 1, 200));
        var wall = world.Create(Transform.At(new Vector3(0, 2, -4)), "wall");
        world.Add(wall, Collider.Box(new Vector3(40, 4, 0.5f)));
        var character = Character(world, new Vector3(0, 0.1f, 0));
        Walk(world, character, Vector2.Zero, 30);

        // Forward and to the right: the wall blocks forward, so it should slide sideways along it.
        Walk(world, character, Vector2.Normalize(new Vector2(1, 1)), 120);
        var end = Position(world, character);

        Assert.True(end.Z > -4f + 0.5f, $"it must not pass through the wall (z = {end.Z})");
        Assert.InRange(end.Z, -3.8f, -3.2f);                    // resting against the wall's face
        Assert.True(end.X > 2f, $"it should have slid along the wall (x = {end.X})");
    }

    [Fact]
    public void StepsOntoALowLedgeButNotAHighOne()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("kcc");
        Floor(world, new Vector3(0, -0.5f, 0), new Vector3(200, 1, 200));

        // A long 0.4 m ledge (below the profile's 0.45 m step) ending in a 1.2 m wall.
        var step = world.Create(Transform.At(new Vector3(0, 0.2f, -9)), "step");
        world.Add(step, Collider.Box(new Vector3(20, 0.4f, 12)));
        var high = world.Create(Transform.At(new Vector3(0, 1.0f, -16)), "high");   // right at the ledge's end
        world.Add(high, Collider.Box(new Vector3(20, 2f, 2)));

        var character = Character(world, new Vector3(0, 0.1f, 0));
        Walk(world, character, Vector2.Zero, 30);
        Walk(world, character, new Vector2(0, 1), 90);

        var onStep = Position(world, character);
        Assert.InRange(onStep.Y, 0.35f, 0.45f);                 // it climbed the step
        Assert.True(onStep.Z < -3.5f, $"and kept walking along it (z = {onStep.Z})");

        Walk(world, character, new Vector2(0, 1), 180);
        var atWall = Position(world, character);
        Assert.InRange(atWall.Y, 0.35f, 0.45f);                 // still on the ledge, not on the wall
        Assert.True(atWall.Z > -16f, $"the 2 m wall must block it (z = {atWall.Z})");
    }

    [Fact]
    public void WalksUpAGentleSlopeButNotACliff()
    {
        using var engine = NewEngine();
        var gentle = engine.CreateWorld("gentle");
        gentle.Resources.Get<Terrain>().Generator = new SlopeGenerator(30f);
        gentle.Resources.Get<Terrain>().Load(SectorCoord.Zero);
        var walker = Character(gentle, new Vector3(100, 60f, 100));
        Walk(gentle, walker, Vector2.Zero, 120);                 // fall onto the slope
        float startY = Position(gentle, walker).Y;
        Walk(gentle, walker, new Vector2(1, 0), 120);             // +X is uphill
        Assert.True(Position(gentle, walker).Y > startY + 0.5f, "it should climb a 30° slope");

        using var engine2 = NewEngine();
        var cliff = engine2.CreateWorld("cliff");
        cliff.Resources.Get<Terrain>().Generator = new SlopeGenerator(70f);
        cliff.Resources.Get<Terrain>().Load(SectorCoord.Zero);
        var climber = Character(cliff, new Vector3(100, 300f, 100));
        Walk(cliff, climber, Vector2.Zero, 200);
        float baseY = Position(cliff, climber).Y;
        float baseX = Position(cliff, climber).X;
        Walk(cliff, climber, new Vector2(1, 0), 120);
        var end = Position(cliff, climber);
        Assert.True(end.X - baseX < 1.0f, $"a 70° face is past the slope limit (moved {end.X - baseX:F2} m)");
        Assert.True(end.Y - baseY < 1.5f, "and it should not have climbed it");
    }

    [Fact]
    public void JumpsOnlyWhenGrounded()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("kcc");
        Floor(world, new Vector3(0, -0.5f, 0), new Vector3(200, 1, 200));
        var character = Character(world, new Vector3(0, 0.1f, 0));
        var jump = default(ActionMask).With(engine.Actions.Get("Jump"));
        Walk(world, character, Vector2.Zero, 30);

        Walk(world, character, Vector2.Zero, 12, pressed: jump);
        float peakish = Position(world, character).Y;
        Assert.True(peakish > 0.5f, $"it should be in the air (y = {peakish})");
        Assert.False(world.Get<CharacterController>(character).Grounded);

        // A second jump while airborne does nothing.
        float before = world.Get<CharacterController>(character).Velocity.Y;
        Walk(world, character, Vector2.Zero, 1, pressed: jump);
        Assert.True(world.Get<CharacterController>(character).Velocity.Y < before);

        Walk(world, character, Vector2.Zero, 120);
        Assert.True(world.Get<CharacterController>(character).Grounded, "and it comes back down");
        Assert.InRange(Position(world, character).Y, 0f, 0.03f);
    }

    [Fact]
    public void CrouchesAndCannotStandUnderACeiling()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("kcc");
        Floor(world, new Vector3(0, -0.5f, 0), new Vector3(200, 1, 200));
        var ceiling = world.Create(Transform.At(new Vector3(0, 1.35f, 0)), "ceiling");
        world.Add(ceiling, Collider.Box(new Vector3(6, 0.3f, 6)));

        var character = Character(world, new Vector3(0, 0.1f, 0));
        var crouch = default(ActionMask).With(engine.Actions.Get("Crouch"));
        Walk(world, character, Vector2.Zero, 30);

        Walk(world, character, Vector2.Zero, 5, held: crouch);
        Assert.True(world.Get<CharacterController>(character).Crouching);
        Assert.Equal(1.05f, world.Get<CharacterController>(character).Height, 2);

        Walk(world, character, Vector2.Zero, 30);                 // released, but the ceiling is low
        Assert.True(world.Get<CharacterController>(character).Crouching, "there is no headroom to stand");

        // Step out from under it (crouched, so slowly), then it can stand again.
        Walk(world, character, new Vector2(1, 0), 180, held: crouch);
        Walk(world, character, Vector2.Zero, 30);
        Assert.False(world.Get<CharacterController>(character).Crouching);
        Assert.Equal(1.8f, world.Get<CharacterController>(character).Height, 2);
    }

    [Fact]
    public void ThePlayersCommandBecomesPawnIntent()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("kcc");
        var character = Character(world, new Vector3(0, 0.1f, 0));
        character.AddTag<PlayerControlled>();

        var input = world.Resources.Get<PlayerInput>();
        var jump = engine.Actions.Get("Jump");
        input.HasCommand = true;
        input.Command = new PlayerCommand
        {
            Move = new Vector2(0.5f, 1f),
            ViewYaw = 1.2f,
            ViewPitch = -0.3f,
            Pressed = default(ActionMask).With(jump),
        };

        world.RunFixed(1f / 60f);

        var intent = world.Get<PawnIntent>(character);
        Assert.Equal(new Vector2(0.5f, 1f), intent.Move);
        Assert.Equal(1.2f, intent.Yaw, 4);
        Assert.Equal(-0.3f, intent.Pitch, 4);
        Assert.True(intent.Pressed.Has(jump));
    }

    // A terrain sector that rises along +X at a given angle.
    private sealed class SlopeGenerator : ITerrainGenerator
    {
        private readonly float _tangent;
        public SlopeGenerator(float degrees) { _tangent = MathF.Tan(degrees * MathF.PI / 180f); }

        public void Generate(SectorCoord sector, Heightfield heights, int seed)
        {
            for (int z = 0; z < heights.Resolution; z++)
                for (int x = 0; x < heights.Resolution; x++)
                    heights[x, z] = x * heights.Spacing * _tangent;
        }
    }
}
