#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Hinged doors, path movers and locks (issue #266): a Morrowind door swings on its hinge, a lift with
// floors stops at each, and a locked door says so instead of opening. All of them data-only prefabs.
public class MoverHingePathTests
{
    public MoverHingePathTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;
    private const byte PlayerLayer = 1;

    private static void Tick(World world, int times = 1)
    {
        for (int i = 0; i < times; i++) world.RunFixed(Dt);
    }

    // A door a metre wide hinged on its west edge, swinging a quarter turn in a second; a turntable;
    // and a lift with two floors above the ground at three metres each, going two metres a second.
    private const string Content = """
    [
      { "type": "prefab", "id": "floor", "name": "floor", "parts": { "body": { "size": [40, 1, 40] } } },
      { "type": "prefab", "id": "hinged_door", "name": "hinged door",
        "parts": { "body": { "size": [1, 2, 0.1] },
                   "mover": { "angle": 90, "axis": [0, 1, 0], "pivot": [-0.5, 0, 0], "seconds": 1 } } },
      { "type": "prefab", "id": "locked_door", "base": "hinged_door", "parts": { "mover": { "locked": true } } },
      { "type": "prefab", "id": "turntable", "name": "turntable",
        "parts": { "body": { "size": [4, 0.2, 4] }, "mover": { "angle": 90, "seconds": 2 } } },
      { "type": "prefab", "id": "two_stop_lift", "name": "lift",
        "parts": { "body": { "size": [2, 0.2, 2] }, "mover": { "path": [[0, 3, 0], [0, 6, 0]], "speed": 2 } } },
      { "type": "prefab", "id": "player", "name": "player", "parts": { "character": { "layer": "player" } } },
      { "type": "scene", "id": "movers",
        "place": [
          { "prefab": "floor", "at": [0, -0.5, 0] },
          { "prefab": "hinged_door", "at": [0, 1, 0], "name": "door" },
          { "prefab": "two_stop_lift", "at": [10, 0.1, 0], "name": "lift" }
        ] }
    ]
    """;

    private sealed class Heard
    {
        public readonly List<(string Output, Entity Activator)> List = new();
    }

    private static HeadlessApp App(Heard? heard = null, bool scene = false)
    {
        var builder = HeadlessApp.Gameplay().File("data/movers.json", Content);
        if (heard != null)
            builder = builder.OnRegistered(app => app.Engine.Inputs.Register("Heard", (World world, in IOContext io) =>
                heard.List.Add((io.Parameter, io.Activator))));
        if (scene) builder = builder.StartScene("sage:movers");
        var app = builder.Boot("movers");
        Assert.Equal(0, app.Records.ErrorCount);
        return app;
    }

    private static Entity Spawn(World world, string prefab, Vector3 at) => world.Spawn(new RecordId("sage", prefab), at);

    private static Vector3 Position(World world, Entity entity) => world.Get<Transform>(entity).LocalPosition;

    private static float YawDegrees(Quaternion q) =>
        MathF.Atan2(2f * (q.W * q.Y + q.X * q.Z), 1f - 2f * (q.Y * q.Y + q.Z * q.Z)) * 180f / MathF.PI;

    // The hinged door, as data: shut it stands across the doorway; open it has swung a quarter turn about
    // its west edge, which stayed put, and the doorway is clear to walk (and see) through.
    [Fact]
    public void AHingedDoorIsADataOnlyPrefabThatSwingsOnItsHinge()
    {
        using var app = App();
        var world = app.World;
        Spawn(world, "floor", new Vector3(0, -0.5f, 0));
        var door = Spawn(world, "hinged_door", new Vector3(0, 1, 0));
        var space = world.Resources.Get<IPhysicsWorld>();
        Tick(world, 2);

        var through = space.Raycast(new Vector3(0.3f, 1, 2), -Vector3.UnitZ, 4f);
        Assert.True(through.Hit && through.Entity == door, "the shut door blocks the doorway");

        world.IO().FireInput(door, "Open");
        Tick(world, 30);
        Assert.InRange(world.Get<Mover>(door).Position, 0.45f, 0.55f);
        Assert.InRange(YawDegrees(world.Get<Transform>(door).LocalRotation), 40f, 50f);   // half open, half way round
        Tick(world, 40);

        var mover = world.Get<Mover>(door);
        Assert.Equal(1f, mover.Position);
        Assert.Equal(0, mover.Direction);
        Assert.InRange(YawDegrees(world.Get<Transform>(door).LocalRotation), 89.9f, 90.1f);
        // The origin swung round the hinge at (-0.5, 1, 0): from half a metre east of it to half a metre north.
        Assert.True(Vector3.Distance(Position(world, door), new Vector3(-0.5f, 1, -0.5f)) < 1e-3f, $"at {Position(world, door)}");

        // The body turned with it: the doorway is open, and the door is where it swung to.
        var body = world.Get<PhysicsBody>(door);
        Assert.False(body.IsStatic);
        Assert.InRange(MathF.Abs(Quaternion.Dot(space.PoseOf(body).Rotation, world.Get<Transform>(door).LocalRotation)), 0.9999f, 1.0001f);
        var clear = space.Raycast(new Vector3(0.3f, 1, 2), -Vector3.UnitZ, 4f);
        Assert.False(clear.Hit && clear.Entity == door, "the open door still blocks the doorway");
        var side = space.Raycast(new Vector3(-2f, 1, -0.5f), Vector3.UnitX, 4f);
        Assert.True(side.Hit && side.Entity == door, "the open door is not where it swung to");
        Assert.InRange(side.Distance, 1.4f, 1.5f);   // its face at x = -0.55

        // And it swings back shut, exactly where it was.
        world.IO().FireInput(door, "Close");
        Tick(world, 70);
        Assert.Equal(0f, world.Get<Mover>(door).Position);
        Assert.True(Vector3.Distance(Position(world, door), new Vector3(0, 1, 0)) < 1e-4f);
        Assert.InRange(MathF.Abs(world.Get<Transform>(door).LocalRotation.W), 0.9999f, 1.0001f);
    }

    // A door placed turned (a prefab spawned with a yaw) hinges on its own west edge, wherever that is.
    [Fact]
    public void AHingedDoorPlacedTurnedSwingsAboutItsOwnHinge()
    {
        using var app = App();
        var world = app.World;
        var door = world.Spawn(new RecordId("sage", "hinged_door"), new Vector3(0, 1, 0), 90f);
        var placed = world.Get<Transform>(door).LocalRotation;
        var hinge = new Vector3(0, 1, 0) + Vector3.Transform(new Vector3(-0.5f, 0, 0), placed);
        Tick(world);

        world.IO().FireInput(door, "Open");
        Tick(world, 70);

        // The hinge is still half a metre from the origin, and where it was.
        var rotation = world.Get<Transform>(door).LocalRotation;
        var now = Position(world, door) + Vector3.Transform(new Vector3(-0.5f, 0, 0), rotation);
        Assert.True(Vector3.Distance(now, hinge) < 1e-3f, $"the hinge moved from {hinge} to {now}");
        var turned = rotation * Quaternion.Inverse(placed);
        Assert.InRange(MathF.Abs(YawDegrees(turned)), 89.9f, 90.1f);
    }

    // What stands on something that turns is carried round with it, at the speed of the ground under its
    // feet (v + ω × r), not the speed of the ground's centre.
    [Fact]
    public void ATurningMoverCarriesWhatStandsOnItRoundWithIt()
    {
        using var app = App();
        var world = app.World;
        Spawn(world, "floor", new Vector3(0, -0.5f, 0));
        var table = Spawn(world, "turntable", new Vector3(0, 0.1f, 0));
        var player = world.Create(Transform.At(new Vector3(1.5f, 0.25f, 0)), "player");
        world.AddCharacter(player, PlayerLayer);
        Tick(world, 30);

        world.IO().FireInput(table, "Open");
        Tick(world, 30);
        // A quarter turn in two seconds about up through the centre: the ground under the feet moves at
        // ω × r, round the centre, not at the centre's speed (nothing).
        var ground = world.Get<CharacterController>(player).GroundVelocity;
        var feet = Position(world, player);
        var expected = Vector3.Cross(new Vector3(0, MathF.PI / 4f, 0), new Vector3(feet.X, 0, feet.Z));
        Assert.True(Vector3.Distance(ground, expected) < 0.05f, $"the ground under the feet moves at {ground}, not {expected}");
        Assert.InRange(ground.Length(), MathF.PI / 4f * 1.5f - 0.05f, MathF.PI / 4f * 1.5f + 0.05f);

        Tick(world, 120);
        Assert.Equal(1f, world.Get<Mover>(table).Position);
        var at = Position(world, player);
        Assert.True(Vector3.Distance(new Vector3(at.X, 0, at.Z), new Vector3(0, 0, -1.5f)) < 0.15f, $"carried round to {at}");
        Assert.True(world.Get<CharacterController>(player).Grounded);
        Assert.Equal(Vector3.Zero, world.Get<CharacterController>(player).GroundVelocity);
    }

    // The two-stop lift, as data: Next takes it a floor up and it stops there; Next again to the top;
    // Previous a floor down; Close to the ground; GoTo by number. OnArrived says which floor.
    [Fact]
    public void ATwoStopLiftIsADataOnlyPrefabThatStopsAtEachFloor()
    {
        var heard = new Heard();
        using var app = App(heard);
        var world = app.World;
        Spawn(world, "floor", new Vector3(0, -0.5f, 0));
        var lift = Spawn(world, "two_stop_lift", new Vector3(0, 0.1f, 0));
        world.Add(lift, new IOConnections
        {
            Wires = new[]
            {
                new Connection { Output = "OnArrived", Target = "!self", Input = "Heard", Parameter = "arrived" },
                new Connection { Output = "OnFullyOpen", Target = "!self", Input = "Heard", Parameter = "open" },
                new Connection { Output = "OnFullyClosed", Target = "!self", Input = "Heard", Parameter = "closed" },
            },
        });
        var player = world.Create(Transform.At(new Vector3(0, 0.25f, 0)), "player");
        world.AddCharacter(player, PlayerLayer);
        Tick(world, 30);
        Assert.Equal(3f, world.Get<Mover>(lift).Seconds, 4);   // six metres at two a second

        float Height() => Position(world, lift).Y;

        world.IO().FireInput(lift, "Next");
        Tick(world, 60 * 2);
        Assert.Equal(0.5f, world.Get<Mover>(lift).Position, 4);
        Assert.Equal(0, world.Get<Mover>(lift).Direction);
        Assert.Equal(3.1f, Height(), 3);
        Assert.InRange(Position(world, player).Y, 3.2f - 0.05f, 3.2f + 0.1f);   // and the rider with it
        Assert.Equal(new[] { "arrived" }, heard.List.Select(h => h.Output));

        world.IO().FireInput(lift, "Next");
        Tick(world, 60 * 2);
        Assert.Equal(1f, world.Get<Mover>(lift).Position);
        Assert.Equal(6.1f, Height(), 3);
        Assert.Equal(new[] { "arrived", "open", "arrived" }, heard.List.Select(h => h.Output));

        world.IO().FireInput(lift, "Next");                     // at the top already: stays
        Tick(world, 10);
        Assert.Equal(6.1f, Height(), 3);

        world.IO().FireInput(lift, "Previous");
        Tick(world, 60 * 2);
        Assert.Equal(3.1f, Height(), 3);

        world.IO().FireInput(lift, "Close");
        Tick(world, 60 * 2);
        Assert.Equal(0f, world.Get<Mover>(lift).Position);
        Assert.Equal(0.1f, Height(), 3);
        Assert.InRange(Position(world, player).Y, 0.2f - 0.05f, 0.2f + 0.1f);

        world.IO().FireInput(lift, "GoTo", "1");                  // straight to the first floor, by number
        Tick(world, 60 * 2);
        Assert.Equal(3.1f, Height(), 3);
        Assert.Equal(1, heard.List.Count(h => h.Output == "closed"));   // back at the ground, once
    }

    // Locked, Open is refused (and says who tried); Close still works; unlocked, it opens.
    [Fact]
    public void ALockedDoorRefusesToOpenUntilUnlocked()
    {
        var heard = new Heard();
        using var app = App(heard);
        var world = app.World;
        var door = Spawn(world, "locked_door", new Vector3(0, 1, 0));
        var knocker = world.Create(Transform.At(Vector3.Zero), "knocker");
        world.Add(door, new IOConnections
        {
            Wires = new[] { new Connection { Output = "OnLocked", Target = "!self", Input = "Heard", Parameter = "locked" } },
        });
        Tick(world);
        Assert.True(world.Get<Mover>(door).Locked);

        world.IO().FireInput(door, "Open", activator: knocker);
        world.IO().FireInput(door, "Toggle", activator: knocker);
        Tick(world, 70);
        Assert.Equal(0f, world.Get<Mover>(door).Position);
        Assert.Equal(2, heard.List.Count(h => h.Output == "locked"));
        Assert.Equal(knocker, heard.List[0].Activator);

        world.IO().FireInput(door, "Unlock");
        world.IO().FireInput(door, "Open");
        Tick(world, 70);
        Assert.Equal(1f, world.Get<Mover>(door).Position);

        // Locked open: it can still be shut, but not opened again.
        world.IO().FireInput(door, "Lock");
        world.IO().FireInput(door, "Close");
        Tick(world, 70);
        Assert.Equal(0f, world.Get<Mover>(door).Position);
        world.IO().FireInput(door, "Open");
        Tick(world, 70);
        Assert.Equal(0f, world.Get<Mover>(door).Position);
        Assert.Equal(3, heard.List.Count(h => h.Output == "locked"));
    }

    // SetSpeed: metres a second along the path, or degrees a second for something that only turns.
    [Fact]
    public void SetSpeedChangesHowLongTheTripTakes()
    {
        using var app = App();
        var world = app.World;
        var lift = Spawn(world, "two_stop_lift", new Vector3(0, 0.1f, 0));
        var door = Spawn(world, "hinged_door", new Vector3(5, 1, 0));
        Tick(world);

        world.IO().FireInput(lift, "SetSpeed", "6");
        world.IO().FireInput(door, "SetSpeed", "180");
        Tick(world);
        Assert.Equal(1f, world.Get<Mover>(lift).Seconds, 4);
        Assert.Equal(0.5f, world.Get<Mover>(door).Seconds, 4);

        world.IO().FireInput(lift, "Open");
        world.IO().FireInput(door, "Open");
        Tick(world, 62);
        Assert.Equal(1f, world.Get<Mover>(lift).Position);
        Assert.Equal(1f, world.Get<Mover>(door).Position);
    }

    // A door half way round and a lift between floors are saved where they are, and carry on from there.
    [Fact]
    public void AHalfOpenHingedDoorAndALiftBetweenFloorsSurviveASave()
    {
        using var app = App(scene: true);
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var world = app.World;
        Tick(world);
        world.IO().FireInput(world.FindByName("door"), "Open");
        world.IO().FireInput(world.FindByName("lift"), "Next");
        Tick(world, 30);

        var door = world.Get<Mover>(world.FindByName("door"));
        var lift = world.Get<Mover>(world.FindByName("lift"));
        var doorRotation = world.Get<Transform>(world.FindByName("door")).LocalRotation;
        Assert.True(app.Engine.Saves.Save("swing"));
        Tick(world, 200);
        Assert.True(app.Engine.Saves.Load("swing"));

        var loadedDoor = world.Get<Mover>(world.FindByName("door"));
        var loadedLift = world.Get<Mover>(world.FindByName("lift"));
        Assert.Equal(door.Position, loadedDoor.Position);
        Assert.Equal(door.ClosedRotation, loadedDoor.ClosedRotation);
        Assert.Equal(lift.Position, loadedLift.Position);
        Assert.Equal(lift.Halt, loadedLift.Halt);
        Assert.Equal(lift.Path, loadedLift.Path);
        Assert.InRange(MathF.Abs(Quaternion.Dot(doorRotation, world.Get<Transform>(world.FindByName("door")).LocalRotation)), 0.9999f, 1.0001f);

        Tick(world, 200);
        Assert.Equal(1f, world.Get<Mover>(world.FindByName("door")).Position);
        Assert.Equal(3.1f, Position(world, world.FindByName("lift")).Y, 3);   // stopped at the first floor, as sent
    }

    // The physics half: a kinematic body moved with a turn moves every point of it at v + ω × r.
    [Fact]
    public void AKinematicBodyMovedWithATurnMovesEachPointAtItsOwnSpeed()
    {
        using var app = App();
        var world = app.World;
        var space = world.Resources.Get<IPhysicsWorld>();
        var slab = world.Create(Transform.At(new Vector3(2, 0, 0)), "slab");
        var corners = new[]
        {
            new Vector3(0, -0.1f, -0.5f), new Vector3(2, -0.1f, -0.5f), new Vector3(2, 0.1f, -0.5f), new Vector3(0, 0.1f, -0.5f),
            new Vector3(0, -0.1f, 0.5f), new Vector3(2, -0.1f, 0.5f), new Vector3(2, 0.1f, 0.5f), new Vector3(0, 0.1f, 0.5f),
        };
        var body = space.MakeKinematic(space.AddHull(slab, corners, new Vector3(2, 0, 0)));
        Assert.False(body.IsStatic);

        // A quarter turn about up through the origin: the hull's centre, a metre east of it, is now a
        // metre north, and moves west at ω × r.
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        space.MoveKinematic(body, new Pose { Position = new Vector3(2, 0, 0), Rotation = turn, Scale = Vector3.One }, Vector3.Zero, new Vector3(0, 2, 0));
        var pose = space.PoseOf(body);
        Assert.True(Vector3.Distance(pose.Position, new Vector3(2, 0, -1)) < 1e-4f, $"centre at {pose.Position}");
        Assert.True(Vector3.Distance(space.VelocityOf(body), new Vector3(-2, 0, 0)) < 1e-4f, $"centre moving at {space.VelocityOf(body)}");
    }
}
