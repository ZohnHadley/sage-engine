#nullable enable
using System;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Movers push what is in their way, or are blocked by it (issue #260, TODO bug 61): a door closing on
// a character used to pass straight through them and leave them stuck inside it.
public class MoverPushTests
{
    public MoverPushTests() { _ = TestEnv.UserRoot; }

    private const byte PlayerLayer = 1;

    private static void Tick(World world, int times = 1)
    {
        for (int i = 0; i < times; i++) world.RunFixed(1f / 60f);
    }

    private static Entity Box(World world, Vector3 center, Vector3 size, string name)
    {
        var entity = world.Create(Transform.At(center), name);
        world.Add(entity, Collider.Box(size));
        return entity;
    }

    // A door slab one metre wide (X), two high and 0.2 thick, as a level builds it: a hull and a mover,
    // standing open two metres west of where it shuts at x = 0, and closing.
    private static Entity ClosingDoor(World world, MoverBlocked onBlocked = MoverBlocked.Reverse)
    {
        var space = world.Resources.Get<IPhysicsWorld>();
        var closed = new Vector3(0, 1, 0);
        var open = closed + new Vector3(-2, 0, 0);
        var door = world.Create(Transform.At(open), "door");
        door.Name = "door";
        var corners = new[]
        {
            new Vector3(-0.5f, -1, -0.1f), new Vector3(0.5f, -1, -0.1f), new Vector3(0.5f, 1, -0.1f), new Vector3(-0.5f, 1, -0.1f),
            new Vector3(-0.5f, -1, 0.1f), new Vector3(0.5f, -1, 0.1f), new Vector3(0.5f, 1, 0.1f), new Vector3(-0.5f, 1, 0.1f),
        };
        world.Add(door, space.AddHull(door, corners, open));
        world.Add(door, new Mover { OpenOffset = new Vector3(-2, 0, 0), Seconds = 1f, Position = 1f, Direction = -1, Closed = closed, OnBlocked = onBlocked });
        return door;
    }

    private static Entity Character(World world, Vector3 feet)
    {
        var entity = world.Create(Transform.At(feet), "player");
        world.AddCharacter(entity, PlayerLayer);
        return entity;
    }

    private static Vector3 Position(World world, Entity entity) => world.Get<Transform>(entity).LocalPosition;

    private static int Overlaps(World world, Entity character)
    {
        var shape = Collider.Standing(0.35f, 1.8f);
        var pose = new Pose { Position = Position(world, character), Rotation = Quaternion.Identity, Scale = Vector3.One };
        var hits = new OverlapHit[4];
        int n = world.Resources.Get<IPhysicsWorld>().Overlap(shape, pose, hits, LayerMask.All.Except(PlayerLayer));
        int inside = 0;
        for (int i = 0; i < n; i++)
            if (hits[i].Depth > 0.01f) inside++;   // not the few millimetres it sinks before the floor is in the space
        return inside;
    }

    private static (Engine Engine, World World) NewWorld(Action<Engine>? beforeWorld = null)
    {
        var engine = HeadlessApp.Gameplay().Build().Engine;
        beforeWorld?.Invoke(engine);
        var world = engine.CreateWorld("movers");
        Box(world, new Vector3(0, -0.5f, 0), new Vector3(20, 1, 20), "floor");
        return (engine, world);
    }

    private static void CountBlocked(Engine engine, int[] count, Entity[] blocker) =>
        engine.Inputs.Register("TestBlocked", (World w, in IOContext io) => { count[0]++; blocker[0] = io.Activator; });

    private static void WireBlocked(World world, Entity door) => world.Add(door, new IOConnections
    {
        Wires = new[] { new Connection { Output = "OnBlocked", Target = "!self", Input = "TestBlocked" } },
    });

    // With room to go, a closing door pushes the character along and shuts.
    [Fact]
    public void AClosingDoorPushesACharacterOutOfItsWayAndShuts()
    {
        var (engine, world) = NewWorld();
        using var _ = engine;
        var door = ClosingDoor(world);
        var player = Character(world, new Vector3(0, 0, 0));

        Tick(world, 90);

        Assert.Equal(0f, world.Get<Mover>(door).Position);
        Assert.True(Position(world, player).X > 0.5f + 0.3f, $"pushed east, clear of the shut door, not at {Position(world, player)}");
        Assert.Equal(0, Overlaps(world, player));
        Assert.True(world.Get<CharacterController>(player).Grounded);
    }

    // The Sandbox hut's case (TODO bug 61): squeezed against the door frame, the character can't be
    // pushed, so the door goes back, says what blocked it once, and the character is never inside it.
    [Fact]
    public void ADoorThatCannotPushACharacterReopensAndSaysWhoBlockedIt()
    {
        int[] blocked = { 0 };
        Entity[] blocker = { default };
        var (engine, world) = NewWorld(e => CountBlocked(e, blocked, blocker));
        using var _ = engine;
        Box(world, new Vector3(1.1f, 1, 0), new Vector3(1, 2, 2), "frame");   // its west face at x = 0.6
        var door = ClosingDoor(world);
        WireBlocked(world, door);
        var player = Character(world, new Vector3(0.2f, 0, 0));               // 0.05 m from the frame

        for (int i = 0; i < 120; i++)
        {
            Tick(world);
            Assert.True(Overlaps(world, player) == 0, $"tick {i}: the character is inside something");
        }

        Assert.Equal(1, blocked[0]);
        Assert.Equal(player, blocker[0]);
        Assert.Equal(1f, world.Get<Mover>(door).Position);   // went back, all the way open
        Assert.Equal(0, world.Get<Mover>(door).Direction);
    }

    // Stop waits where it is, and carries on once the way is clear.
    [Fact]
    public void ADoorSetToStopWaitsForTheWayToClear()
    {
        int[] blocked = { 0 };
        Entity[] blocker = { default };
        var (engine, world) = NewWorld(e => CountBlocked(e, blocked, blocker));
        using var _ = engine;
        Box(world, new Vector3(1.1f, 1, 0), new Vector3(1, 2, 2), "frame");
        var door = ClosingDoor(world, MoverBlocked.Stop);
        WireBlocked(world, door);
        var player = Character(world, new Vector3(0.2f, 0, 0));

        Tick(world, 90);
        float waiting = world.Get<Mover>(door).Position;
        Assert.InRange(waiting, 0.05f, 0.95f);
        Assert.Equal(-1, world.Get<Mover>(door).Direction);
        Assert.Equal(1, blocked[0]);   // once per blockage, not every tick
        Assert.Equal(0, Overlaps(world, player));

        world.Get<Transform>(player).LocalPosition = new Vector3(0, 0, 4);   // out of the doorway
        Tick(world, 60);
        Assert.Equal(0f, world.Get<Mover>(door).Position);
    }

    // Crush keeps going, firing OnBlocked every tick it is in someone, for a map to wire damage to.
    [Fact]
    public void ACrushingDoorKeepsGoingAndFiresEveryTick()
    {
        int[] blocked = { 0 };
        Entity[] blocker = { default };
        var (engine, world) = NewWorld(e => CountBlocked(e, blocked, blocker));
        using var _ = engine;
        Box(world, new Vector3(1.1f, 1, 0), new Vector3(1, 2, 2), "frame");
        var door = ClosingDoor(world, MoverBlocked.Crush);
        WireBlocked(world, door);
        var player = Character(world, new Vector3(0.2f, 0, 0));

        Tick(world, 90);

        Assert.Equal(0f, world.Get<Mover>(door).Position);
        Assert.True(blocked[0] > 3, $"fired {blocked[0]} times");
        Assert.Equal(player, blocker[0]);
    }

    // A crate in the way is shoved, not passed through.
    [Fact]
    public void AClosingDoorShovesADynamicBody()
    {
        var (engine, world) = NewWorld();
        using var _ = engine;
        var door = ClosingDoor(world);
        var crate = world.Create(Transform.At(new Vector3(0, 0.25f, 0)), "crate");
        world.Add(crate, Collider.Box(new Vector3(0.5f, 0.5f, 0.5f)));
        world.Add(crate, RigidBody.Dynamic(5f));

        Tick(world, 90);

        Assert.Equal(0f, world.Get<Mover>(door).Position);
        Assert.True(Position(world, crate).X > 0.5f, $"the crate should be east of the shut door, at {Position(world, crate)}");
    }

    // TODO bug 61 in the shipped Sandbox: stand in the hut's doorway and wait for the door to shut itself
    // (`closeAfter` 5). It used to close through the player and leave them inside it for good; now it
    // pushes them as far as the jamb lets it, then goes back, and they are never inside it.
    [Fact]
    public void TheSandboxHutDoorNoLongerTrapsAPlayerStandingInTheDoorway()
    {
        using var app = SandboxScreensTests.Boot();
        var world = app.World;
        Tick(world, 3);                                                   // the hut's map is in
        var door = world.FindByName("hut_door");
        Assert.False(door.IsNull, "no hut_door in the Sandbox");
        var player = SandboxScreensTests.Player(world);
        var space = world.Resources.Get<IPhysicsWorld>();

        world.IO().FireInput(door, "Open");
        for (int i = 0; i < 200 && world.Get<Mover>(door).Position < 1f; i++) Tick(world);
        Assert.Equal(1f, world.Get<Mover>(door).Position);

        // Into the doorway: on the floor where the shut door stands.
        var closed = world.Get<Mover>(door).Closed;
        var floor = space.Raycast(closed, -Vector3.UnitY, 10f, ignore: door);
        Assert.True(floor.Hit, "no floor under the doorway");
        world.Get<Transform>(player).LocalPosition = floor.Position + Vector3.UnitY * 0.05f;
        world.Get<CharacterController>(player).Velocity = Vector3.Zero;
        Tick(world, 10);

        var collider = world.Get<Collider>(player);
        var mask = LayerMask.All.Except(collider.Layer);
        var hits = new OverlapHit[4];
        bool closing = false;
        for (int i = 0; i < 60 * 12; i++)
        {
            Tick(world);
            closing |= world.Get<Mover>(door).Direction < 0;
            var pose = new Pose { Position = Position(world, player), Rotation = Quaternion.Identity, Scale = Vector3.One };
            int n = space.Overlap(collider, pose, hits, mask);
            for (int h = 0; h < n; h++)
                Assert.False(hits[h].Entity == door && hits[h].Depth > 0.01f, $"tick {i}: the player is {hits[h].Depth} m inside the door");
        }
        Assert.True(closing, "the door never tried to shut");
    }
}
