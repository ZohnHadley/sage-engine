#nullable enable
using System.Collections.Generic;
using System.Numerics;
using Friflo.Engine.ECS;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// Entity I/O (docs/design/04 §3.4, TODO F17): level logic written as wires rather than code.
//
// The whole value of this is that a mapper can be *wrong* — a typo, a dead target, a wire that fires
// itself — without the engine misbehaving. So most of these are about what happens when the data is bad.
public class EntityIOTests
{
    public EntityIOTests() { _ = TestEnv.UserRoot; }

    private static Engine NewEngine()
    {
        return HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule(), new MoverModule()).Build().Engine;
    }

    // A counter an input can move, so a test can see a wire arrive.
    private static int _counter;

    private static Engine NewEngineWithCounter()
    {
        var engine = NewEngine();
        _counter = 0;
        if (!engine.Inputs.Has("TestCount"))
            engine.Inputs.Register("TestCount", static (World world, in IOContext io) => _counter += (int)io.Number(1f));
        return engine;
    }

    private static Entity Wired(World world, string name, params Connection[] wires)
    {
        var entity = world.Create(Transform.At(Vector3.Zero), name);
        entity.Name = new EntityName(name);
        world.Add(entity, new IOConnections { Wires = wires });
        return entity;
    }

    private static void Tick(World world, int times = 1)
    {
        for (int i = 0; i < times; i++) world.RunFixed(1f / 60f);
    }

    [Fact]
    public void DestroyingAParentLeavesItsChildren()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("io");
        var parent = world.Create(Transform.At(Vector3.Zero), "parent");
        var child = world.Create(Transform.At(Vector3.Zero), "child");
        world.SetParent(child, parent);

        world.Destroy(parent);

        // A level's solid entity owns its meshes as children, and the two halves of the engine clean up
        // in a fixed order — so whether a child dies with its parent is not a detail, it is the
        // difference between the client freeing a GPU buffer and never seeing it again.
        Assert.True(world.IsAlive(child), "children died with the parent");
    }

    [Fact]
    public void AnOutputReachesTheInputItIsWiredTo()
    {
        using var engine = NewEngineWithCounter();
        var world = engine.CreateWorld("io");

        var target = world.Create(Transform.At(Vector3.Zero), "target");
        target.Name = new EntityName("target");
        var source = Wired(world, "source", new Connection { Output = "OnUse", Target = "target", Input = "TestCount" });

        world.FireOutput(source, "OnUse");
        Tick(world);

        Assert.Equal(1, _counter);
    }

    [Fact]
    public void ADelayedWireArrivesLateAndNotBefore()
    {
        using var engine = NewEngineWithCounter();
        var world = engine.CreateWorld("io");

        var target = world.Create(Transform.At(Vector3.Zero), "target");
        target.Name = new EntityName("target");
        var source = Wired(world, "source",
            new Connection { Output = "OnUse", Target = "target", Input = "TestCount", Delay = 0.5f });

        world.FireOutput(source, "OnUse");
        Tick(world, 20);                 // a third of a second
        Assert.Equal(0, _counter);

        Tick(world, 15);                 // past half a second
        Assert.Equal(1, _counter);
    }

    [Fact]
    public void TimesLimitsHowOftenAWireFires()
    {
        using var engine = NewEngineWithCounter();
        var world = engine.CreateWorld("io");

        var target = world.Create(Transform.At(Vector3.Zero), "target");
        target.Name = new EntityName("target");
        var source = Wired(world, "source",
            new Connection { Output = "OnUse", Target = "target", Input = "TestCount", Times = 2 });

        for (int i = 0; i < 5; i++) { world.FireOutput(source, "OnUse"); Tick(world); }

        // A one-shot trap is the commonest thing a mapper writes, and "times" is how they write it.
        Assert.Equal(2, _counter);
    }

    [Fact]
    public void SelfAndActivatorAreResolvedWhenTheWireFiresRatherThanAtLoad()
    {
        using var engine = NewEngine();
        var seen = new List<string>();
        engine.Inputs.Register("TestWho", (World w, in IOContext io) =>   // before the world, as a module's Init would
            seen.Add($"{io.Self.Name.value}<-{(io.Activator.IsNull ? "nobody" : io.Activator.Name.value)}"));
        var world = engine.CreateWorld("io");

        var source = Wired(world, "button",
            new Connection { Output = "OnUse", Target = "!self", Input = "TestWho" },
            new Connection { Output = "OnUse", Target = "!activator", Input = "TestWho" });

        var player = world.Create(Transform.At(Vector3.Zero), "player");
        player.Name = new EntityName("player");

        world.FireOutput(source, "OnUse", player);
        Tick(world);

        // `!self` is the button; `!activator` is whoever pressed it. Both are about *this* firing, which
        // is why neither can be resolved when the level loads.
        Assert.Equal(new[] { "button<-player", "player<-player" }, seen);
    }

    [Fact]
    public void AWireThatFiresItselfCannotRunAwayInsideOneTick()
    {
        using var engine = NewEngineWithCounter();
        var world = engine.CreateWorld("io");
        var io = world.Resources.Get<EntityIO>();

        // A loop: the entity's own output sends `Fire` back to itself. A mapper writes this by accident,
        // and in an engine that dispatches a chain to its end within the tick it is a hang.
        var loop = Wired(world, "loop",
            new Connection { Output = "OnLoop", Target = "!self", Input = "Fire", Parameter = "OnLoop" },
            new Connection { Output = "OnLoop", Target = "!self", Input = "TestCount" });

        world.FireOutput(loop, "OnLoop");
        Tick(world, 10);

        // What saves it is that an input fired *during* dispatch is due on the next tick, not this one:
        // the loop keeps turning at two deliveries a tick for ever, which is a bug a mapper can see and
        // a frame rate nobody notices. It is the reason a chain of wires takes a tick per link.
        Assert.Equal(2, io.DispatchedLastTick);
        Assert.Equal(10, _counter);
    }

    [Fact]
    public void OneOutputWiredToTooMuchIsCutOffWithAWarning()
    {
        using var engine = NewEngineWithCounter();
        var world = engine.CreateWorld("io");
        var io = world.Resources.Get<EntityIO>();
        io.Budget = 10;

        // The other shape of runaway, and the one the budget is actually for: a single output wired to
        // more things than a tick should deliver. Fan-out happens in one tick, so only a cap stops it.
        var wires = new Connection[40];
        for (int i = 0; i < wires.Length; i++)
            wires[i] = new Connection { Output = "OnUse", Target = "!self", Input = "TestCount" };
        var source = Wired(world, "source", wires);

        using var log = new CaptureSink();
        world.FireOutput(source, "OnUse");
        Tick(world);

        Assert.Equal(10, io.DispatchedLastTick);
        Assert.Contains(log.Entries, e => e.Message.Contains("in one tick"));
    }

    [Fact]
    public void AnInputThatDoesNotExistIsRefusedWhenTheLevelLoadsNotWhenItFires()
    {
        using var engine = NewEngine();
        using var log = new CaptureSink();

        var entity = new MapEntity();
        entity.Keys["classname"] = "button";
        entity.Keys["OnUse"] = "door,Opne";           // a typo, which is the whole point

        var wires = MapEntityIO.Read(entity, engine, "test.map:12");

        Assert.Null(wires);
        Assert.Contains(log.Entries, e => e.Message.Contains("Opne") && e.Message.Contains("test.map:12"));
    }

    [Fact]
    public void AConnectionIsReadTheWayHammerWritesOne()
    {
        using var engine = NewEngine();

        var entity = new MapEntity();
        entity.Keys["OnUse"] = "hut_door,Open,,1.5,1";

        var wires = MapEntityIO.Read(entity, engine, "test.map:3");

        var wire = Assert.Single(wires!);
        Assert.Equal("OnUse", wire.Output);
        Assert.Equal("hut_door", wire.Target);
        Assert.Equal("Open", wire.Input);
        Assert.Equal("", wire.Parameter);
        Assert.Equal(1.5f, wire.Delay);
        Assert.Equal(1, wire.Times);
    }

    [Fact]
    public void AWireFindsATargetThatDidNotExistYet()
    {
        using var engine = NewEngineWithCounter();
        var world = engine.CreateWorld("io");

        var source = Wired(world, "source",
            new Connection { Output = "OnUse", Target = "arrives_later", Input = "TestCount" });

        // Nothing of that name yet: the wire is queued with the name, and the name is looked up again
        // when it is delivered (04 §3.4, "late binding"). A quest NPC spawned mid-level needs this.
        world.FireOutput(source, "OnUse");

        var late = world.Create(Transform.At(Vector3.Zero), "arrives_later");
        late.Name = new EntityName("arrives_later");
        Tick(world);

        Assert.Equal(1, _counter);
    }

    [Fact]
    public void AThingWiredToOnUseCountsAsSomethingYouCanUse()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("io");

        var door = Wired(world, "door", new Connection { Output = "OnUse", Target = "!self", Input = "Open" });
        var scenery = world.Create(Transform.At(Vector3.Zero), "scenery");

        // The rule the interaction system asks about (16 §3, F17): a door in a map has no `Pickup` and
        // no tag, and is usable because using it does something. Without this a mapper wires a door and
        // then cannot open it, which is exactly what happened the first time.
        Assert.True(door.HasOutput("OnUse"));
        Assert.False(scenery.HasOutput("OnUse"));
        Assert.False(door.HasOutput("OnFullyOpen"));
    }

    [Fact]
    public void WalkingIntoATriggerVolumeFiresOnStartTouch()
    {
        using var engine = NewEngineWithCounter();
        var world = engine.CreateWorld("io");
        var space = world.Resources.Get<PhysicsSpace>();

        // A two-metre cube of trigger at the origin, built the way a `"trigger" "1"` brush entity is.
        var trigger = world.Create(Transform.At(Vector3.Zero), "trigger");
        var corners = new[]
        {
            new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(1, 1, -1), new Vector3(-1, 1, -1),
            new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(1, 1, 1), new Vector3(-1, 1, 1),
        };
        world.Add(trigger, space.AddHull(trigger, corners, Vector3.Zero, 0, isTrigger: true));
        world.Add(trigger, new IOConnections
        {
            Wires = new[] { new Connection { Output = "OnStartTouch", Target = "!self", Input = "TestCount" } },
        });

        // Something that falls into it. A dynamic body, because that is what generates contacts.
        var faller = world.Create(Transform.At(new Vector3(0, 3, 0)), "faller");
        world.Add(faller, Collider.Box(new Vector3(0.5f, 0.5f, 0.5f)));
        world.Add(faller, new RigidBody { Kind = BodyKind.Dynamic, Mass = 10f });

        Tick(world, 60);

        Assert.True(_counter > 0, "nothing came of walking into a trigger volume");
    }

    [Fact]
    public void ATriggerVolumeSeesTheKinematicThingsToo()
    {
        using var engine = NewEngineWithCounter();
        var world = engine.CreateWorld("io");
        var space = world.Resources.Get<PhysicsSpace>();

        var trigger = world.Create(Transform.At(Vector3.Zero), "trigger");
        var corners = new[]
        {
            new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(1, 1, -1), new Vector3(-1, 1, -1),
            new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(1, 1, 1), new Vector3(-1, 1, 1),
        };
        world.Add(trigger, space.AddHull(trigger, corners, Vector3.Zero, 0, isTrigger: true));
        world.Add(trigger, new IOConnections
        {
            Wires = new[] { new Connection { Output = "OnStartTouch", Target = "!self", Input = "TestCount" } },
        });

        // A kinematic capsule walked in by moving its transform, which is exactly what the character
        // controller does: it sweeps, sets the transform, and physics follows. The player is this.
        var walker = world.Create(Transform.At(new Vector3(0, 0, 4)), "walker");
        world.Add(walker, Collider.Capsule(0.35f, 1.8f));
        world.Add(walker, RigidBody.Kinematic());
        Tick(world);

        for (int i = 0; i < 40; i++)
        {
            ref var transform = ref walker.GetComponent<Transform>();
            transform.LocalPosition = new Vector3(0, 0, 4f - i * 0.2f);
            Tick(world);
        }

        Assert.True(_counter > 0, "a trigger volume never noticed the player walking through it");
    }

    [Fact]
    public void AMoverOpensSaysSoAndShutsItselfAgain()
    {
        using var engine = NewEngine();
        var opened = 0;
        engine.Inputs.Register("TestOpened", (World w, in IOContext io) => opened++);   // before the world
        var world = engine.CreateWorld("io");

        var door = world.Create(Transform.At(new Vector3(0, 0, 0)), "door");
        door.Name = new EntityName("door");
        world.Add(door, new Mover { OpenOffset = new Vector3(2, 0, 0), Seconds = 0.5f, CloseAfter = 0.5f });
        world.Add(door, new IOConnections
        {
            Wires = new[] { new Connection { Output = "OnFullyOpen", Target = "!self", Input = "TestOpened" } },
        });

        world.IO().FireInput(door, "Open");
        Tick(world, 40);                 // two thirds of a second: open, and said so

        Assert.Equal(2f, door.GetComponent<Transform>().LocalPosition.X, 2);
        Assert.Equal(1, opened);

        Tick(world, 60);                 // it holds, then shuts itself
        Assert.Equal(0f, door.GetComponent<Transform>().LocalPosition.X, 2);
    }

    [Fact]
    public void OpeningADoorMovesWhatYouWalkInto()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("io");
        var space = world.Resources.Get<PhysicsSpace>();

        // A metre cube of door at the origin, as a level would build it: a hull, and a mover to shift it.
        var door = world.Create(Transform.At(Vector3.Zero), "door");
        var corners = new[]
        {
            new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f),
            new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
            new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f),
            new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f),
        };
        world.Add(door, space.AddHull(door, corners, Vector3.Zero));
        world.Add(door, new Mover { OpenOffset = new Vector3(4, 0, 0), Seconds = 0.2f });

        var results = new Entity[8];
        var probe = new Vector3(0.2f, 0.2f, 0.2f);
        Assert.True(space.OverlapBox(Vector3.Zero, probe, results) > 0, "the shut door is not solid");

        world.IO().FireInput(door, "Open");
        Tick(world, 30);

        // The mesh moving and the collision staying put is the bug this pins: a static does not follow a
        // transform, it has to be told (15 §10a).
        Assert.Equal(0, space.OverlapBox(Vector3.Zero, probe, results));
        Assert.True(space.OverlapBox(new Vector3(4, 0, 0), probe, results) > 0, "the open door is nowhere");
    }
}
