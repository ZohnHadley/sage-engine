#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// What the Mover component's fields do, one headless assertion each, and the cases around them that no
// other mover test crosses (issue #272): travel time, CloseAfter, a save mid-travel, a world with no
// physics, a replayed run that comes out bit for bit the same, and many movers beside many bodies.
// The push, rider, hinge and path tests are in MoverPushTests, MoverRiderTests and MoverHingePathTests.
public class MoverTests
{
    public MoverTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    private const string Content = """
    [
      { "type": "prefab", "id": "floor", "name": "floor", "parts": { "body": { "size": [60, 1, 60] } } },
      { "type": "prefab", "id": "slider", "name": "slider",
        "parts": { "body": { "size": [1, 2, 0.2] }, "mover": { "open": [4, 0, 0], "seconds": 2 } } },
      { "type": "prefab", "id": "auto_door", "base": "slider", "parts": { "mover": { "closeAfter": 1 } } },
      { "type": "scene", "id": "movers",
        "place": [
          { "prefab": "floor", "at": [0, -0.5, 0] },
          { "prefab": "auto_door", "at": [0, 1, 0], "name": "door" }
        ] }
    ]
    """;

    private static HeadlessApp App(bool scene = false)
    {
        var builder = HeadlessApp.Gameplay().File("data/movers.json", Content);
        if (scene) builder = builder.StartScene("sage:movers");
        var app = builder.Boot("movers");
        Assert.Equal(0, app.Records.ErrorCount);
        return app;
    }

    private static void Tick(World world, int times = 1)
    {
        for (int i = 0; i < times; i++) world.RunFixed(Dt);
    }

    private static Entity Spawn(World world, string prefab, Vector3 at) => world.Spawn(new RecordId("sage", prefab), at);

    private static Vector3 Position(World world, Entity entity) => world.Get<Transform>(entity).LocalPosition;

    private static int TicksUntil(World world, Func<bool> done, int limit = 2000)
    {
        for (int i = 1; i <= limit; i++)
        {
            Tick(world);
            if (done()) return i;
        }
        return -1;
    }

    // Seconds is how long shut to open takes; OpenOffset is where open is, from where it was placed;
    // Position runs 0 to 1 on the way and Direction says which way it goes and that it has stopped.
    [Fact]
    public void ATripTakesItsSecondsAndEndsAtItsOpenOffset()
    {
        using var app = App();
        var world = app.World;
        var door = Spawn(world, "slider", new Vector3(1, 1, 0));
        Tick(world);
        var mover = world.Get<Mover>(door);
        Assert.Equal(0f, mover.Position);
        Assert.Equal(0, mover.Direction);
        Assert.Equal(2f, mover.Seconds);
        Assert.Equal(new Vector3(4, 0, 0), mover.OpenOffset);
        Assert.Equal(new Vector3(1, 1, 0), mover.Closed);

        world.IO().FireInput(door, "Open");
        Tick(world, 60);
        mover = world.Get<Mover>(door);
        Assert.Equal(1, mover.Direction);
        Assert.InRange(mover.Position, 0.48f, 0.52f);
        Assert.InRange(Position(world, door).X, 2.9f, 3.1f);

        int more = TicksUntil(world, () => world.Get<Mover>(door).Direction == 0);
        Assert.InRange(60 + more, 119, 122);   // two seconds, give or take a tick
        mover = world.Get<Mover>(door);
        Assert.Equal(1f, mover.Position);
        Assert.Equal(new Vector3(5, 1, 0), Position(world, door));

        world.IO().FireInput(door, "Close");
        int back = TicksUntil(world, () => world.Get<Mover>(door).Direction == 0);
        Assert.InRange(back, 119, 122);
        Assert.Equal(0f, world.Get<Mover>(door).Position);
        Assert.Equal(new Vector3(1, 1, 0), Position(world, door));
    }

    // A mover is told where to go and nothing else: a Close at its closed stop is a no-op.
    [Fact]
    public void ClosingAShutMoverDoesNothing()
    {
        using var app = App();
        var world = app.World;
        var door = Spawn(world, "slider", new Vector3(0, 1, 0));
        Tick(world);
        world.IO().FireInput(door, "Close");
        Tick(world, 10);
        Assert.Equal(0f, world.Get<Mover>(door).Position);
        Assert.Equal(new Vector3(0, 1, 0), Position(world, door));
    }

    // CloseAfter: it counts HoldRemaining down once it is open and then shuts itself; without it, it stays.
    [Fact]
    public void CloseAfterShutsADoorByItselfAndZeroLeavesItOpen()
    {
        using var app = App();
        var world = app.World;
        var auto = Spawn(world, "auto_door", new Vector3(0, 1, 0));
        var held = Spawn(world, "slider", new Vector3(0, 1, 5));
        Tick(world);
        Assert.Equal(1f, world.Get<Mover>(auto).CloseAfter);
        Assert.Equal(0f, world.Get<Mover>(held).CloseAfter);

        world.IO().FireInput(auto, "Open");
        world.IO().FireInput(held, "Open");
        TicksUntil(world, () => world.Get<Mover>(auto).Direction == 0);
        var mover = world.Get<Mover>(auto);
        Assert.Equal(1f, mover.Position);
        Assert.Equal(1f, mover.HoldRemaining, 2);   // the hold has begun

        Tick(world, 30);
        Assert.Equal(1f, world.Get<Mover>(auto).Position);   // half a second in: still open
        Assert.InRange(world.Get<Mover>(auto).HoldRemaining, 0.4f, 0.6f);

        Tick(world, 31);
        Assert.Equal(-1, world.Get<Mover>(auto).Direction);   // held a second: it shuts

        int shut = TicksUntil(world, () => world.Get<Mover>(auto).Direction == 0);
        Assert.InRange(shut, 118, 123);
        Assert.Equal(0f, world.Get<Mover>(auto).Position);

        Tick(world, 600);
        Assert.Equal(1f, world.Get<Mover>(held).Position);   // CloseAfter 0: it stays open
        Assert.Equal(0, world.Get<Mover>(held).Direction);
    }

    // A save lands mid-travel and mid-hold, and what loads carries on from there rather than jumping.
    [Fact]
    public void AMoverSavedMidTravelAndMidHoldCarriesOnFromThere()
    {
        using var app = App(scene: true);
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var world = app.World;
        var door = world.FindByName("door");
        Tick(world);
        world.IO().FireInput(door, "Open");
        Tick(world, 45);

        var travelling = world.Get<Mover>(door);
        var travellingAt = Position(world, door);
        Assert.Equal(1, travelling.Direction);
        Assert.InRange(travelling.Position, 0.2f, 0.8f);
        Assert.True(app.Engine.Saves.Save("travel"));

        TicksUntil(world, () => world.Get<Mover>(door).Direction == 0);
        Tick(world, 30);   // open, and half way through its hold
        var holding = world.Get<Mover>(door);
        Assert.Equal(1f, holding.Position);
        Assert.True(app.Engine.Saves.Save("hold"));

        Tick(world, 400);
        Assert.Equal(0f, world.Get<Mover>(door).Position);

        Assert.True(app.Engine.Saves.Load("travel"));
        door = world.FindByName("door");
        var loaded = world.Get<Mover>(door);
        Assert.Equal(travelling.Position, loaded.Position);
        Assert.Equal(travelling.Direction, loaded.Direction);
        Assert.Equal(travelling.Closed, loaded.Closed);
        Assert.Equal(travelling.OpenOffset, loaded.OpenOffset);
        Assert.Equal(travelling.Seconds, loaded.Seconds);
        Assert.Equal(travelling.CloseAfter, loaded.CloseAfter);
        Assert.Equal(travellingAt, Position(world, door));
        TicksUntil(world, () => world.Get<Mover>(door).Direction == 0);
        Assert.Equal(1f, world.Get<Mover>(door).Position);

        Assert.True(app.Engine.Saves.Load("hold"));
        door = world.FindByName("door");
        Assert.Equal(holding.HoldRemaining, world.Get<Mover>(door).HoldRemaining);
        Assert.Equal(0, world.Get<Mover>(door).Direction);
        Tick(world, 31);
        Assert.Equal(-1, world.Get<Mover>(door).Direction);   // the rest of its hold ran out after the load
    }

    // A world with no physics at all: the mover still travels, arrives and fires, with nothing to push.
    [Fact]
    public void AMoverInAWorldWithNoPhysicsStillTravelsAndArrives()
    {
        using var app = HeadlessApp.Bare().With(new MoverModule()).Boot("nophysics");
        var world = app.World;
        Assert.False(world.Resources.TryGet<IPhysicsWorld>(out _));
        var door = world.Create(Transform.At(new Vector3(0, 1, 0)), "door");
        world.Add(door, new Mover { OpenOffset = new Vector3(0, 3, 0), Seconds = 1f, Closed = new Vector3(0, 1, 0), CloseAfter = 0.5f });
        Tick(world);

        world.Get<Mover>(door).Direction = 1;   // what the Open input sets
        Tick(world, 62);
        Assert.Equal(1f, world.Get<Mover>(door).Position);
        Assert.Equal(new Vector3(0, 4, 0), Position(world, door));
        Assert.False(world.Get<Mover>(door).Blocked);

        Tick(world, 30);   // its hold, a second's worth
        Assert.Equal(-1, world.Get<Mover>(door).Direction);
        Tick(world, 62);
        Assert.Equal(0f, world.Get<Mover>(door).Position);
        Assert.Equal(new Vector3(0, 1, 0), Position(world, door));
    }

    // Blocked is set while a character it cannot push is squeezed in its way, and clears when it is not.
    [Fact]
    public void BlockedIsSetWhileSomethingItCannotPushIsInTheWay()
    {
        using var app = App();
        var world = app.World;
        var space = world.Resources.Get<IPhysicsWorld>();
        var floor = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "floor");
        world.Add(floor, Collider.Box(new Vector3(20, 1, 20)));
        var frame = world.Create(Transform.At(new Vector3(1.1f, 1, 0)), "frame");
        world.Add(frame, Collider.Box(new Vector3(1, 2, 2)));
        var closed = new Vector3(0, 1, 0);
        var open = closed + new Vector3(-2, 0, 0);
        var door = world.Create(Transform.At(open), "door");
        var corners = new[]
        {
            new Vector3(-0.5f, -1, -0.1f), new Vector3(0.5f, -1, -0.1f), new Vector3(0.5f, 1, -0.1f), new Vector3(-0.5f, 1, -0.1f),
            new Vector3(-0.5f, -1, 0.1f), new Vector3(0.5f, -1, 0.1f), new Vector3(0.5f, 1, 0.1f), new Vector3(-0.5f, 1, 0.1f),
        };
        world.Add(door, space.AddHull(door, corners, open));
        world.Add(door, new Mover { OpenOffset = new Vector3(-2, 0, 0), Seconds = 1f, Position = 1f, Direction = -1, Closed = closed, OnBlocked = MoverBlocked.Stop });
        var player = world.Create(Transform.At(new Vector3(0.2f, 0, 0)), "player");
        world.AddCharacter(player, 1);

        Tick(world, 90);
        var mover = world.Get<Mover>(door);
        Assert.True(mover.Blocked);
        Assert.Equal(-1, mover.Direction);       // Stop: it waits, still wanting to shut
        Assert.InRange(mover.Position, 0.05f, 0.95f);

        world.Get<Transform>(player).LocalPosition = new Vector3(0, 0, 4);
        Tick(world, 90);
        Assert.False(world.Get<Mover>(door).Blocked);
        Assert.Equal(0f, world.Get<Mover>(door).Position);
    }

    // The same scenario from the same start, run twice in two worlds, lands on the same bits: every mover,
    // crate and character, tick for tick.
    [Fact]
    public void ReplayingASteppingRunGivesBitIdenticalPositions()
    {
        var first = Scenario();
        var second = Scenario();
        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
            Assert.Equal(first[i], second[i]);
    }

    private static List<int[]> Scenario()
    {
        using var app = App();
        var world = app.World;
        var space = world.Resources.Get<IPhysicsWorld>();
        var floor = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "floor");
        world.Add(floor, Collider.Box(new Vector3(60, 1, 60)));

        var watched = new List<Entity>();
        for (int i = 0; i < 4; i++)
        {
            var closed = new Vector3(i * 4, 0.1f, 0);
            var lift = world.Create(Transform.At(closed), "lift" + i);
            var corners = new[]
            {
                new Vector3(-1, -0.1f, -1), new Vector3(1, -0.1f, -1), new Vector3(1, 0.1f, -1), new Vector3(-1, 0.1f, -1),
                new Vector3(-1, -0.1f, 1), new Vector3(1, -0.1f, 1), new Vector3(1, 0.1f, 1), new Vector3(-1, 0.1f, 1),
            };
            world.Add(lift, space.AddHull(lift, corners, closed));
            world.Add(lift, new Mover { OpenOffset = new Vector3(i, 2, 0), Seconds = 1.5f, Closed = closed, CloseAfter = 0.5f });
            watched.Add(lift);

            var crate = world.Create(Transform.At(new Vector3(i * 4 + 0.3f, 0.45f, 0.2f)), "crate" + i);
            world.Add(crate, Collider.Box(new Vector3(0.5f, 0.5f, 0.5f)));
            world.Add(crate, RigidBody.Dynamic(5f));
            watched.Add(crate);

            var player = world.Create(Transform.At(new Vector3(i * 4 - 0.4f, 0.3f, -0.3f)), "player" + i);
            world.AddCharacter(player, 1);
            watched.Add(player);
        }
        Tick(world, 20);

        var log = new List<int[]>();
        for (int tick = 0; tick < 400; tick++)
        {
            if (tick % 100 == 0)
                for (int i = 0; i < 4; i++) world.IO().FireInput(watched[i * 3], "Toggle");
            Tick(world);
            var row = new int[watched.Count * 3];
            for (int i = 0; i < watched.Count; i++)
            {
                var p = Position(world, watched[i]);
                row[i * 3] = BitConverter.SingleToInt32Bits(p.X);
                row[i * 3 + 1] = BitConverter.SingleToInt32Bits(p.Y);
                row[i * 3 + 2] = BitConverter.SingleToInt32Bits(p.Z);
            }
            log.Add(row);
        }
        return log;
    }
}

// Many movers, bodies and characters stepping together stay inside a generous budget (the handoff's
// worry about what a busy level costs). The bound is loose on purpose: it catches a quadratic step, not
// a slow machine. Timed alone, like every measurement.
[Xunit.Collection(MeasurementsCollection.Name)]
public class MoverBudgetTests
{
    public MoverBudgetTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void ManyMoversBodiesAndCharactersStepWithinABudget()
    {
        using var app = HeadlessApp.Gameplay().Boot("budget");
        var world = app.World;
        var space = world.Resources.Get<IPhysicsWorld>();
        var floor = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "floor");
        world.Add(floor, Collider.Box(new Vector3(200, 1, 200)));

        var lifts = new List<Entity>();
        for (int i = 0; i < 50; i++)
        {
            var closed = new Vector3(-40 + (i % 10) * 8, 0.1f, -20 + (i / 10) * 8);
            var lift = world.Create(Transform.At(closed), "lift" + i);
            var corners = new[]
            {
                new Vector3(-1, -0.1f, -1), new Vector3(1, -0.1f, -1), new Vector3(1, 0.1f, -1), new Vector3(-1, 0.1f, -1),
                new Vector3(-1, -0.1f, 1), new Vector3(1, -0.1f, 1), new Vector3(1, 0.1f, 1), new Vector3(-1, 0.1f, 1),
            };
            world.Add(lift, space.AddHull(lift, corners, closed));
            world.Add(lift, new Mover { OpenOffset = new Vector3(0, 2, 0), Seconds = 1f, Closed = closed, CloseAfter = 0.25f });
            lifts.Add(lift);
            for (int c = 0; c < 4; c++)
            {
                var crate = world.Create(Transform.At(closed + new Vector3(-0.6f + c * 0.4f, 0.4f, 0)), "crate");
                world.Add(crate, Collider.Box(new Vector3(0.3f, 0.3f, 0.3f)));
                world.Add(crate, RigidBody.Dynamic(2f));
            }
            var player = world.Create(Transform.At(closed + new Vector3(0, 0.4f, 0.7f)), "npc");
            world.AddCharacter(player, 1);
        }
        for (int i = 0; i < 100; i++) world.RunFixed(1f / 60f);   // settle, and warm the JIT

        var clock = System.Diagnostics.Stopwatch.StartNew();
        const int Ticks = 240;
        for (int t = 0; t < Ticks; t++)
        {
            if (t % 80 == 0)
                foreach (var lift in lifts) world.IO().FireInput(lift, "Toggle");
            world.RunFixed(1f / 60f);
        }
        double msPerTick = clock.Elapsed.TotalMilliseconds / Ticks;

        // 50 lifts, 200 crates and 50 characters at a sixtieth of a second: a tick must not cost several.
        Assert.True(msPerTick < 50, $"{msPerTick:F1} ms a tick, over the 50 ms bound");
    }
}
