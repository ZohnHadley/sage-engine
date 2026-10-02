#nullable enable
using System;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Water volumes and swimming (issue #262, phase 4l): a character that falls into a pool swims with its
// head out, dives on Crouch with the breath hook counting, and climbs out onto the bank; a crate floats
// half under, a current carries it, and in water too thin to hold it up it sinks.
public class WaterTests
{
    public WaterTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    // A pool 10 m across and 4 m deep, its surface at y = 0, with a bank to the +X side whose top is
    // 0.3 m above the water.
    private const string Content = """
    [
      { "type": "prefab", "id": "pool_floor", "name": "pool_floor", "parts": { "body": { "size": [30, 1, 30] } } },
      { "type": "prefab", "id": "bank", "name": "bank", "parts": { "body": { "size": [6, 4.3, 10] } } },
      { "type": "prefab", "id": "pool", "name": "pool", "parts": { "water": { "size": [10, 4, 10] } } },
      { "type": "prefab", "id": "river", "name": "river", "parts": { "water": { "size": [30, 4, 30], "current": [1, 0, 0] } } },
      { "type": "prefab", "id": "thin", "name": "thin", "parts": { "water": { "size": [30, 4, 30], "buoyancy": 0.5 } } },
      { "type": "prefab", "id": "swimmer", "name": "swimmer", "parts": { "character": { "layer": "player" } } },
      { "type": "prefab", "id": "crate", "name": "crate", "parts": { "body": { "size": [0.6, 0.6, 0.6], "mass": 8 } } },
      { "type": "prefab", "id": "counter", "name": "counter", "parts": { "logic_counter": { "max": 100 } } },

      { "type": "scene", "id": "pool_scene", "place": [
          { "prefab": "pool_floor", "at": [0, -4.5, 0], "name": "floor" },
          { "prefab": "bank", "at": [8, -1.85, 0], "name": "bank" },
          { "prefab": "counter", "at": [20, 0, 0], "name": "wet" },
          { "prefab": "counter", "at": [20, 0, 0], "name": "dry" },
          { "prefab": "counter", "at": [20, 0, 0], "name": "splashes" },
          { "prefab": "pool", "at": [0, -2, 0], "name": "pool",
            "outputs": [ { "output": "OnEnterWater", "target": "splashes", "input": "Add" } ] },
          { "prefab": "swimmer", "at": [0, 2, 0], "name": "swimmer",
            "outputs": [ { "output": "OnEnterWater", "target": "wet", "input": "Add" },
                         { "output": "OnExitWater", "target": "dry", "input": "Add" } ] },
          { "prefab": "crate", "at": [-2, 2, 3], "name": "crate" } ] },
      { "type": "scene", "id": "river_scene", "place": [
          { "prefab": "pool_floor", "at": [0, -4.5, 0], "name": "floor" },
          { "prefab": "river", "at": [0, -2, 0], "name": "river" },
          { "prefab": "crate", "at": [-5, 1, 0], "name": "crate" } ] },
      { "type": "prefab", "id": "puddle", "name": "puddle", "parts": { "water": { "size": [4, 1, 4] } } },
      { "type": "scene", "id": "shallow_scene", "place": [
          { "prefab": "pool_floor", "at": [0, -0.5, 0], "name": "floor" },
          { "prefab": "puddle", "at": [0, 0, 0], "name": "puddle" },
          { "prefab": "swimmer", "at": [-1, 0, 0], "name": "swimmer" } ] },
      { "type": "scene", "id": "thin_scene", "place": [
          { "prefab": "pool_floor", "at": [0, -4.5, 0], "name": "floor" },
          { "prefab": "thin", "at": [0, -2, 0], "name": "thin" },
          { "prefab": "crate", "at": [0, 1, 0], "name": "crate" } ] }
    ]
    """;

    private static HeadlessApp Boot(string scene)
    {
        var files = new MountFixture();
        files.Write("game", "data/content.json", Content);
        files.Mount("game", "game");
        var app = HeadlessApp.Gameplay().Mount(files).StartScene("game:" + scene).Boot();
        app.Engine.Saves.Root = System.IO.Path.Combine(TestEnv.NewTempDir(), "saves");
        app.World.RunFixed(Dt);
        return app;
    }

    private static void Run(World world, Entity character, int ticks, Vector2 move = default, ActionMask held = default)
    {
        for (int i = 0; i < ticks; i++)
        {
            if (!character.IsNull)
            {
                ref var intent = ref world.Get<PawnIntent>(character);
                intent.Move = move;
                intent.Held = held;
            }
            world.RunFixed(Dt);
        }
    }

    private static Vector3 Position(World world, Entity entity) => world.Get<Transform>(entity).LocalPosition;
    private static float Count(World world, string counter) => world.Get<LogicCounter>(world.FindByName(counter)).Value;

    // Falls in from 2 m up, goes under with the splash, and comes back up to float with its head out:
    // swimming, its feet SwimFloatDepth of its height under, and OnEnterWater fired once, on it and on
    // the pool.
    [Fact]
    public void ACharacterFallingIntoWaterSwimsWithItsHeadOut()
    {
        using var app = Boot("pool_scene");
        var world = app.World;
        var swimmer = world.FindByName("swimmer");
        var profile = MovementProfileRecord.Fallback;

        float lowest = float.MaxValue;
        for (int i = 0; i < 360; i++)
        {
            Run(world, swimmer, 1);
            lowest = MathF.Min(lowest, Position(world, swimmer).Y);
        }

        var character = world.Get<CharacterController>(swimmer);
        Assert.True(character.InWater);
        Assert.True(character.Swimming, $"swimming at immersion {character.Immersion}");
        Assert.False(character.Underwater, "its head is out");
        Assert.Equal(0f, character.UnderwaterSeconds);
        Assert.Equal(world.FindByName("pool"), character.Water);
        Assert.True(lowest < -profile.SwimFloatDepth * profile.StandHeight - 0.1f, $"the fall carries it under first (lowest {lowest})");
        Assert.True(lowest > -3.9f, $"and the water stops it before the bottom (lowest {lowest})");
        Assert.Equal(-profile.SwimFloatDepth * profile.StandHeight, Position(world, swimmer).Y, 0.1f);
        Assert.InRange(MathF.Abs(character.Velocity.Y), 0f, 0.1f);

        Assert.Equal(1f, Count(world, "wet"));
        Assert.Equal(1f, Count(world, "splashes"));
        Assert.Equal(0f, Count(world, "dry"));
    }

    // Crouch swims down; with its eyes under, the breath hook counts the seconds. Let go and it rises
    // back to the surface, and the count goes back to 0.
    [Fact]
    public void DivingCountsTheBreathHookAndItFloatsBackUp()
    {
        using var app = Boot("pool_scene");
        var world = app.World;
        var swimmer = world.FindByName("swimmer");
        var crouch = default(ActionMask).With(app.Engine.Actions.Get("Crouch"));
        Run(world, swimmer, 360);
        float floating = Position(world, swimmer).Y;

        Run(world, swimmer, 60, held: crouch);
        var dived = world.Get<CharacterController>(swimmer);
        Assert.True(Position(world, swimmer).Y < floating - 1.5f, $"swam down from {floating} to {Position(world, swimmer).Y}");
        Assert.True(dived.Underwater);
        Assert.True(dived.Swimming);
        Assert.False(dived.Crouching, "Crouch is 'swim down' in water, not a smaller capsule");
        Assert.InRange(dived.UnderwaterSeconds, 0.5f, 1f);

        Run(world, swimmer, 600);
        var surfaced = world.Get<CharacterController>(swimmer);
        Assert.False(surfaced.Underwater);
        Assert.Equal(0f, surfaced.UnderwaterSeconds);
        Assert.Equal(floating, Position(world, swimmer).Y, 0.1f);
    }

    // Swimming at the surface into the bank, whose top is 0.3 m above the water, it climbs out and
    // stands on it: out of the water, on the ground, and OnExitWater fired.
    [Fact]
    public void ASwimmerClimbsOutOntoTheBank()
    {
        using var app = Boot("pool_scene");
        var world = app.World;
        var swimmer = world.FindByName("swimmer");
        Run(world, swimmer, 360);
        world.Get<PawnIntent>(swimmer).Yaw = SageMath.YawOf(Vector3.UnitX);

        // Swims at the bank until it is out, then stops walking (or it walks off the far side).
        for (int i = 0; i < 240 && world.Get<CharacterController>(swimmer).InWater; i++)
            Run(world, swimmer, 1, move: new Vector2(0, 1));
        Run(world, swimmer, 10);

        var character = world.Get<CharacterController>(swimmer);
        Vector3 at = Position(world, swimmer);
        Assert.True(at.X > 5f, $"onto the bank (x > 5), at {at}");
        Assert.Equal(0.3f, at.Y, 0.05f);
        Assert.True(character.Grounded);
        Assert.False(character.InWater);
        Assert.False(character.Swimming);
        Assert.Equal(1f, Count(world, "dry"));
    }

    // Water up to its knees is waded, not swum: it stays on its feet, walks through (slowed a little by
    // the drag) and out the other side.
    [Fact]
    public void ShallowWaterIsWadedNotSwum()
    {
        using var app = Boot("shallow_scene");
        var world = app.World;
        var swimmer = world.FindByName("swimmer");
        world.Get<PawnIntent>(swimmer).Yaw = SageMath.YawOf(Vector3.UnitX);
        Run(world, swimmer, 30);

        var wading = world.Get<CharacterController>(swimmer);
        Assert.True(wading.InWater);
        Assert.False(wading.Swimming);
        Assert.True(wading.Grounded);
        Assert.InRange(wading.Immersion, 0.2f, 0.35f);

        Run(world, swimmer, 120, move: new Vector2(0, 1));
        var through = world.Get<CharacterController>(swimmer);
        Assert.True(Position(world, swimmer).X > 2.5f, $"walked out the far side, at {Position(world, swimmer)}");
        Assert.False(through.InWater);
        Assert.True(through.Grounded);
    }

    // A crate dropped in floats half under (buoyancy 2 is twice its weight fully under) and comes to
    // rest there.
    [Fact]
    public void ACrateFloatsHalfUnder()
    {
        using var app = Boot("pool_scene");
        var world = app.World;
        var crate = world.FindByName("crate");
        Run(world, default, 600);

        Assert.Equal(0f, Position(world, crate).Y, 0.06f);
        Assert.InRange(world.Get<BodyMotion>(crate).Linear.Length(), 0f, 0.1f);
    }

    // A river's current carries a floating crate along with it.
    [Fact]
    public void TheCurrentCarriesAFloatingCrate()
    {
        using var app = Boot("river_scene");
        var world = app.World;
        var crate = world.FindByName("crate");
        Run(world, default, 300);

        Assert.Equal(0f, Position(world, crate).Y, 0.1f);
        Assert.True(Position(world, crate).X > -2f, $"carried downstream from x = -5 to {Position(world, crate).X}");
        Assert.Equal(1f, world.Get<BodyMotion>(crate).Linear.X, 0.15f);
    }

    // In water with buoyancy below 1 the same crate sinks, slowed by the water, to the bottom.
    [Fact]
    public void InThinWaterACrateSinks()
    {
        using var app = Boot("thin_scene");
        var world = app.World;
        var crate = world.FindByName("crate");
        Run(world, default, 600);

        Assert.Equal(-3.7f, Position(world, crate).Y, 0.05f);
    }

    // A save made while swimming loads still swimming, and does not fire OnEnterWater a second time.
    [Fact]
    public void ASaveWhileSwimmingLoadsWithoutASecondSplash()
    {
        string saves;
        {
            using var app = Boot("pool_scene");
            var world = app.World;
            Run(world, world.FindByName("swimmer"), 300);
            Assert.Equal(1f, Count(world, "wet"));
            Assert.True(app.Engine.Saves.Save("swim"));
            saves = app.Engine.Saves.Root;
        }

        using var fresh = Boot("pool_scene");
        fresh.Engine.Saves.Root = saves;
        Assert.True(fresh.Engine.Saves.Load("swim"));
        var loaded = fresh.World;
        var swimmer = loaded.FindByName("swimmer");
        Run(loaded, swimmer, 30);

        var character = loaded.Get<CharacterController>(swimmer);
        Assert.True(character.Swimming);
        Assert.Equal(loaded.FindByName("pool"), character.Water);
        Assert.Equal(1f, Count(loaded, "wet"));
    }
}
