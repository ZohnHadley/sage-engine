#nullable enable
using System;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Ladders (issue #263, docs/design/10 §3): a `ladder` trigger volume the character controller climbs —
// pushing toward the rungs climbs, pulling away climbs down, Jump lets go, and a walkable ledge within
// step height of the top is stepped onto. Placed from a prefab (a trigger `body` and a `ladder` part) and
// drawn in a map as a `sage:ladder` brush entity.
public class LadderTests
{
    public LadderTests() { _ = TestEnv.UserRoot; }

    private const byte PlayerLayer = 1;

    // A ladder against the +Z face of a three-metre block: the block runs z -1..-5, so its face is at
    // z = -1 and its top (the ledge) at y = 3; the volume stands in front of it, facing +Z.
    private const string Prefabs = """
        [
          { "type": "prefab", "id": "test_ladder", "name": "test ladder",
            "parts": { "body": { "size": [1, 3, 0.6], "trigger": true }, "ladder": { "facing": 180, "speed": 3 } } }
        ]
        """;

    private static Engine NewEngine() => HeadlessApp.Gameplay().File("data/ladders.json", Prefabs).Build().Engine;

    private static Entity Box(World world, Vector3 center, Vector3 size, string name)
    {
        var entity = world.Create(Transform.At(center), name);
        world.Add(entity, Collider.Box(size));
        return entity;
    }

    private static Entity Character(World world, Vector3 feet, float yaw)
    {
        var entity = world.Create(Transform.At(feet) with { LocalRotation = SageMath.RotationFromYaw(yaw) }, "climber");
        world.AddCharacter(entity, PlayerLayer);
        return entity;
    }

    private static void Drive(World world, Entity character, Vector2 move, int ticks, bool jump = false)
    {
        var actions = world.Engine!.Actions;
        for (int i = 0; i < ticks; i++)
        {
            ref var intent = ref world.Get<PawnIntent>(character);
            intent.Move = move;
            intent.Pressed = jump && i == 0 ? default(ActionMask).With(actions.Get("Jump")) : default;
            world.RunFixed(1f / 60f);
        }
    }

    // Keeps pushing until it is off the ladder, at the top or the bottom; a few seconds at most.
    private static void ClimbOff(World world, Entity character, Vector2 move)
    {
        for (int i = 0; i < 300 && Controller(world, character).Climbing; i++) Drive(world, character, move, 1);
    }

    private static Vector3 Feet(World world, Entity entity) => world.Get<Transform>(entity).LocalPosition;
    private static CharacterController Controller(World world, Entity entity) => world.Get<CharacterController>(entity);

    private static (World world, Entity character) Tower(Engine engine)
    {
        var world = engine.CreateWorld("ladder");
        Box(world, new Vector3(0, -0.5f, 0), new Vector3(20, 1, 20), "floor");
        Box(world, new Vector3(0, 1.5f, -3), new Vector3(4, 3, 4), "block");
        world.Spawn(new RecordId("sage", "test_ladder"), new Vector3(0, 1.5f, -0.7f));
        world.RunFixed(1f / 60f);   // the statics are in the space
        return (world, Character(world, new Vector3(0, 0, 0.2f), 0f));   // yaw 0 looks at -Z: at the rungs
    }

    // The issue's exit: climb a ladder volume to a ledge and step off it onto the top.
    [Fact]
    public void ClimbsALadderToTheLedgeAndStepsOff()
    {
        using var engine = NewEngine();
        var (world, character) = Tower(engine);

        // Walk into the rungs: it catches the ladder and goes up, with no gravity on it.
        Drive(world, character, new Vector2(0, 1), 30);
        Assert.True(Controller(world, character).Climbing, $"pushing at the rungs should climb, at {Feet(world, character)}");
        float y = Feet(world, character).Y;
        Assert.True(y > 0.5f, $"climbing at 3 m/s, but only at {y}");

        // Up to the top, and over: a ledge within step height is stepped onto.
        ClimbOff(world, character, new Vector2(0, 1));
        var feet = Feet(world, character);
        Assert.False(Controller(world, character).Climbing, $"still on the ladder at {feet}");
        Assert.True(Controller(world, character).Grounded, $"not standing on the ledge at {feet}");
        Assert.InRange(feet.Y, 2.95f, 3.1f);
        Assert.True(feet.Z < -1f, $"on top of the block, not hanging in front of it: {feet}");

        // And walks on along the top.
        Drive(world, character, new Vector2(0, 1), 20);
        feet = Feet(world, character);
        Assert.True(feet.Z < -1.6f, $"walked on along the ledge, but is at {feet}");
        Assert.InRange(feet.Y, 2.95f, 3.1f);
    }

    // Down again: step off the top onto the ladder, climb down by pulling away from it, and walk off.
    [Fact]
    public void MountsAtTheTopClimbsDownAndWalksAwayAtTheBottom()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("ladder");
        Box(world, new Vector3(0, -0.5f, 0), new Vector3(20, 1, 20), "floor");
        Box(world, new Vector3(0, 1.5f, -3), new Vector3(4, 3, 4), "block");
        world.Spawn(new RecordId("sage", "test_ladder"), new Vector3(0, 1.5f, -0.7f));
        world.RunFixed(1f / 60f);
        var character = Character(world, new Vector3(0, 3.02f, -1.6f), MathF.PI);   // on the ledge, facing +Z, out over the ladder
        Drive(world, character, Vector2.Zero, 5);
        Assert.True(Controller(world, character).Grounded);

        // Walk off the edge: falling into the volume catches the ladder instead of dropping three metres.
        int ticks = 0;
        while (!Controller(world, character).Climbing && ticks++ < 60) Drive(world, character, new Vector2(0, 1), 1);
        Assert.True(Controller(world, character).Climbing, $"stepping off the top should catch the ladder, at {Feet(world, character)}");
        Assert.True(Feet(world, character).Y > 1.5f, $"caught at {Feet(world, character)}");
        Assert.True(MathF.Abs(Controller(world, character).Velocity.Y) < 3.5f, "and caught, not still falling");

        // Pulling away from the rungs (still facing +Z, still pushing forward) climbs down, to the floor,
        // and walks on.
        ClimbOff(world, character, new Vector2(0, 1));
        Drive(world, character, new Vector2(0, 1), 30);
        var feet = Feet(world, character);
        Assert.False(Controller(world, character).Climbing, $"still climbing at {feet}");
        Assert.True(Controller(world, character).Grounded);
        Assert.InRange(feet.Y, -0.05f, 0.1f);
        Assert.True(feet.Z > 0.5f, $"walked away from the foot of the ladder: {feet}");
    }

    // Standing at the bottom without pushing at it, or walking away from it, is not climbing; Jump lets go.
    [Fact]
    public void ALadderOnlyCatchesWhatPushesAtItAndJumpLetsGo()
    {
        using var engine = NewEngine();
        var (world, character) = Tower(engine);

        Drive(world, character, Vector2.Zero, 20);
        Assert.False(Controller(world, character).Climbing);
        Assert.InRange(Feet(world, character).Y, -0.05f, 0.05f);

        Drive(world, character, new Vector2(1, 0), 10);   // sideways along the bottom
        Assert.False(Controller(world, character).Climbing);

        Drive(world, character, new Vector2(0, 1), 30);
        Assert.True(Controller(world, character).Climbing);
        float height = Feet(world, character).Y;

        // Hanging still: no gravity on a ladder.
        Drive(world, character, Vector2.Zero, 30);
        Assert.True(Controller(world, character).Climbing);
        Assert.Equal(height, Feet(world, character).Y, 2);

        // Jump: off the face, and down to the floor; it does not catch the ladder again on the way.
        Drive(world, character, Vector2.Zero, 1, jump: true);
        Assert.False(Controller(world, character).Climbing);
        Drive(world, character, Vector2.Zero, 90);
        Assert.False(Controller(world, character).Climbing, $"caught the ladder again at {Feet(world, character)}");
        Assert.True(Controller(world, character).Grounded);
        Assert.True(Feet(world, character).Z > -0.2f, $"pushed off the face, at {Feet(world, character)}");
    }

    // Mappable: the same ladder drawn in a .map as a brush entity of the game's `ladder` prefab (a door is a
    // prefab with a `mover`; a ladder is one with a `ladder`), its facing a map key.
    private const string TowerMap = """
        {
        "classname" "worldspawn"
        {
        ( -320 -320 -32 ) ( -320 -319 -32 ) ( -320 -320 -31 ) floor 0 0 0 1 1
        ( -320 -320 -32 ) ( -320 -320 -31 ) ( -319 -320 -32 ) floor 0 0 0 1 1
        ( -320 -320 -32 ) ( -319 -320 -32 ) ( -320 -319 -32 ) floor 0 0 0 1 1
        ( 320 320 0 ) ( 320 321 0 ) ( 321 320 0 ) floor 0 0 0 1 1
        ( 320 320 0 ) ( 321 320 0 ) ( 320 320 1 ) floor 0 0 0 1 1
        ( 320 320 0 ) ( 320 320 1 ) ( 320 321 0 ) floor 0 0 0 1 1
        }
        {
        ( -64 32 0 ) ( -64 33 0 ) ( -64 32 1 ) wall 0 0 0 1 1
        ( -64 32 0 ) ( -64 32 1 ) ( -63 32 0 ) wall 0 0 0 1 1
        ( -64 32 0 ) ( -63 32 0 ) ( -64 33 0 ) wall 0 0 0 1 1
        ( 64 96 96 ) ( 64 97 96 ) ( 65 96 96 ) wall 0 0 0 1 1
        ( 64 96 96 ) ( 65 96 96 ) ( 64 96 97 ) wall 0 0 0 1 1
        ( 64 96 96 ) ( 64 96 97 ) ( 64 97 96 ) wall 0 0 0 1 1
        }
        }
        {
        "classname" "ladder"
        "trigger" "1"
        "ladder.facing" "180"
        {
        ( -16 12 0 ) ( -16 13 0 ) ( -16 12 1 ) clip 0 0 0 1 1
        ( -16 12 0 ) ( -16 12 1 ) ( -15 12 0 ) clip 0 0 0 1 1
        ( -16 12 0 ) ( -15 12 0 ) ( -16 13 0 ) clip 0 0 0 1 1
        ( 16 32 96 ) ( 16 33 96 ) ( 17 32 96 ) clip 0 0 0 1 1
        ( 16 32 96 ) ( 17 32 96 ) ( 16 32 97 ) clip 0 0 0 1 1
        ( 16 32 96 ) ( 16 32 97 ) ( 16 33 96 ) clip 0 0 0 1 1
        }
        }
        """;

    [Fact]
    public void ALadderBrushEntityInAMapIsClimbedToItsLedge()
    {
        using var engine = HeadlessApp.Gameplay()
            .With(new MapModule())
            .File("maps/tower.map", TowerMap, "sandbox")
            .File("data/level.json", """
                [
                  { "type": "map", "id": "tower", "file": "maps/tower.map" },
                  { "type": "prefab", "id": "ladder", "name": "ladder", "parts": { "ladder": {} } }
                ]
                """, "sandbox")
            .Build().Engine;
        var world = engine.CreateWorld("level");
        Assert.NotNull(MapLoader.Load(world, new RecordId("sandbox", "tower")));
        world.RunFixed(1f / 60f);

        // The brush became a ladder facing +Z (map south): the prefab's part, turned by the map's key.
        var ladder = Assert.Single(world.Query<Ladder>().Entities.ToEntityList());
        Assert.Equal(180f, world.Get<Ladder>(ladder).Facing);

        // The block's face is at map y 32 (engine z -1) and its top at map z 96 (3 m).
        var character = Character(world, new Vector3(0, 0, 0.2f), 0f);
        Drive(world, character, new Vector2(0, 1), 30);
        Assert.True(Controller(world, character).Climbing, $"pushing at the rungs should climb, at {Feet(world, character)}");
        ClimbOff(world, character, new Vector2(0, 1));
        var feet = Feet(world, character);
        Assert.False(Controller(world, character).Climbing, $"still on the ladder at {feet}");
        Assert.True(Controller(world, character).Grounded, $"not standing on the ledge at {feet}");
        Assert.InRange(feet.Y, 2.95f, 3.1f);
        Assert.True(feet.Z < -1f, $"on top of the block: {feet}");
    }
}
