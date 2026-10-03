#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Group targets, the I/O history and a throwing input (issues #276 and #402; 04 §3.4, §8).
public class EntityIOGroupTests
{
    public EntityIOGroupTests() { _ = TestEnv.UserRoot; }

    private static void Tick(World world, int times = 1)
    {
        for (int i = 0; i < times; i++) world.RunFixed(1f / 60f);
    }

    // Who heard `Hit`, by name, per app (the input table is the engine's, and tests run in parallel).
    private static HeadlessApp Boot(List<string> heard, Action<Engine>? more = null) =>
        HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).OnRegistered(a =>
        {
            a.Engine.Inputs.Register("Hit", (World w, in IOContext io) => heard.Add(io.Self.Name ?? "?"));
            more?.Invoke(a.Engine);
        }).Boot("io");

    private static Entity Named(World world, string name, string? groups = null)
    {
        var entity = world.Create(Transform.At(Vector3.Zero), name);
        entity.Name = name;
        if (groups != null) world.Add(entity, new IOGroup { Names = groups });
        return entity;
    }

    private static readonly Vector3[] Cube =
    {
        new(-1, -1, -1), new(1, -1, -1), new(1, 1, -1), new(-1, 1, -1),
        new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1),
    };

    [Fact]
    public void ATriggerFiresEveryEntityInAGroup()
    {
        var heard = new List<string>();
        using var app = Boot(heard);
        var world = app.World;
        var space = world.Resources.Get<IPhysicsWorld>();

        // A trigger volume wired to `@lamps`: three lamps (one in two groups), and a stool in none.
        var trigger = world.Create(Transform.At(Vector3.Zero), "trigger");
        world.Add(trigger, space.AddHull(trigger, Cube, Vector3.Zero, 0, isTrigger: true));
        world.Add(trigger, new IOConnections
        {
            Wires = new[] { new Connection { Output = "OnStartTouch", Target = "@lamps", Input = "Hit", Times = 1 } },
        });
        Named(world, "lamp_1", "lamps");
        Named(world, "lamp_2", "hall, lamps");
        Named(world, "stool");
        Named(world, "lamp_3", "LAMPS");

        var faller = world.Create(Transform.At(new Vector3(0, 3, 0)), "faller");
        world.Add(faller, Collider.Box(new Vector3(0.5f, 0.5f, 0.5f)));
        world.Add(faller, new RigidBody { Kind = BodyKind.Dynamic, Mass = 10f });
        Tick(world, 60);

        heard.Sort(StringComparer.Ordinal);
        Assert.Equal(new[] { "lamp_1", "lamp_2", "lamp_3" }, heard);
        Assert.Equal(3, Enumerable.Range(0, world.IO().HistoryCount).Count(i => world.IO().HistoryAt(i).TargetName == "@lamps"));
    }

    [Fact]
    public void AGroupIsResolvedWhenTheInputArrives_SoAMemberSpawnedDuringTheDelayIsReached()
    {
        var heard = new List<string>();
        using var app = Boot(heard);
        var world = app.World;
        var source = Named(world, "button");
        world.Add(source, new IOConnections
        {
            Wires = new[] { new Connection { Output = "OnUse", Target = "@lamps", Input = "Hit", Delay = 0.5f } },
        });
        Named(world, "early", "lamps");

        world.FireOutput(source, "OnUse");
        Tick(world, 10);
        Named(world, "late", "lamps");
        Tick(world, 30);

        heard.Sort(StringComparer.Ordinal);
        Assert.Equal(new[] { "early", "late" }, heard);

        // A group with no members is not an error when it fires: nothing happens, and the history says so.
        heard.Clear();
        world.IO().FireInput("@nobody", "Hit");
        Tick(world);
        Assert.Empty(heard);
        Assert.Equal(IOOutcome.NoTarget, world.IO().HistoryAt(world.IO().HistoryCount - 1).Outcome);
    }

    [Fact]
    public void AMapWiresToAGroupByItsGroupKey_AndToAClassByItsClassname()
    {
        var heard = new List<string>();
        var fixture = new MountFixture();
        fixture.Write("game", "maps/hall.map", """
            {
            "classname" "worldspawn"
            }
            {
            "classname" "relay"
            "origin" "0 0 0"
            "targetname" "switch"
            "OnUser1" "@lamps,Hit"
            "OnUser2" "@class:torch,Hit"
            }
            {
            "classname" "torch"
            "origin" "64 0 0"
            "targetname" "torch_1"
            "group" "lamps"
            }
            {
            "classname" "torch"
            "origin" "128 0 0"
            "targetname" "torch_2"
            }
            {
            "classname" "relay"
            "origin" "192 0 0"
            "targetname" "chandelier"
            "group" "lamps hall"
            }
            """);
        fixture.Write("game", "data/level.json", """
            [
              { "type": "map", "id": "hall", "file": "maps/hall.map" },
              { "type": "prefab", "id": "relay", "components": { "transform": {} } },
              { "type": "prefab", "id": "torch", "components": { "transform": {} } }
            ]
            """);
        fixture.Mount("game", "sandbox");
        using var app = HeadlessApp.Bare()
            .With(new PhysicsModule(), new MapModule(), new EntityIOModule())
            .OnRegistered(a => a.Engine.Inputs.Register("Hit", (World w, in IOContext io) => heard.Add(io.Self.Name ?? "?")))
            .Mount(fixture)
            .Boot("map");
        var world = app.World;
        Assert.NotNull(MapLoader.Load(world, new RecordId("sandbox", "hall")));
        Tick(world);

        var chandelier = world.FindByName("chandelier");
        Assert.True(world.Get<IOGroup>(chandelier).Has("hall"));

        world.FireOutput(world.FindByName("switch"), "OnUser1");
        Tick(world);
        heard.Sort(StringComparer.Ordinal);
        Assert.Equal(new[] { "chandelier", "torch_1" }, heard);

        heard.Clear();
        world.FireOutput(world.FindByName("switch"), "OnUser2");
        Tick(world);
        heard.Sort(StringComparer.Ordinal);
        Assert.Equal(new[] { "torch_1", "torch_2" }, heard);
    }

    [Fact]
    public void APlacementsWireReachesAPrefabsGroup_AClass_AndATag()
    {
        var fixture = new MountFixture();
        fixture.Write("game", "data/scene.json", """
        [
          { "type": "prefab", "id": "box", "components": { "transform": {} } },
          { "type": "prefab", "id": "lamp", "components": { "transform": {}, "sage:io_group": { "names": "lamps" } } },
          { "type": "prefab", "id": "marked", "components": { "transform": {} }, "tags": ["sage:player_camera"] },
          { "type": "scene", "id": "main",
            "place": [
              { "prefab": "box", "name": "switch", "outputs": [
                  { "output": "OnUser1", "target": "@lamps", "input": "Kill" },
                  { "output": "OnUser2", "target": "@class:box", "input": "Kill" },
                  { "output": "OnUser3", "target": "@tag:sage:player_camera", "input": "Kill" } ] },
              { "prefab": "lamp", "name": "lamp_a" },
              { "prefab": "lamp", "name": "lamp_b" },
              { "prefab": "box", "name": "crate" },
              { "prefab": "marked", "name": "marked" },
              { "prefab": "box", "name": "bad", "outputs": [ { "output": "OnUse", "target": "@tag:sage:no_such_tag", "input": "Kill" } ] }
            ] }
        ]
        """);
        fixture.Mount("game", "game");
        using var app = HeadlessApp.Gameplay().Mount(fixture).StartScene("game:main").Boot();
        Assert.Equal(1, app.Records.ErrorCount);             // the tag nobody declares, said at load
        var world = app.World;

        world.FireOutput(world.FindByName("switch"), "OnUser1");
        Tick(world);
        Assert.True(world.FindByName("lamp_a").IsNull && world.FindByName("lamp_b").IsNull, "the lamps are still there");
        Assert.False(world.FindByName("crate").IsNull);

        world.FireOutput(world.FindByName("switch"), "OnUser3");
        Tick(world);
        Assert.True(world.FindByName("marked").IsNull, "the tagged entity is still there");

        // A class reaches the switch itself too: it is a box.
        world.FireOutput(world.FindByName("switch"), "OnUser2");
        Tick(world);
        Assert.True(world.FindByName("crate").IsNull && world.FindByName("switch").IsNull);
    }

    [Fact]
    public void AnInputThatThrowsIsLoggedWithItsWire_AndTheRestOfTheTickStillArrives()
    {
        var heard = new List<string>();
        using var app = Boot(heard, engine =>
            engine.Inputs.Register("Boom", static (World w, in IOContext io) => throw new InvalidOperationException("the game's own bug")));
        var world = app.World;
        var source = Named(world, "lever");
        world.Add(source, new IOConnections
        {
            Wires = new[]
            {
                new Connection { Output = "OnPull", Target = "bomb", Input = "Boom" },
                new Connection { Output = "OnPull", Target = "bell", Input = "Hit" },
            },
        });
        Named(world, "bomb");
        Named(world, "bell");

        using var log = new CaptureSink();
        world.FireOutput(source, "OnPull");
        Tick(world);
        Log.Flush();

        Assert.Equal(new[] { "bell" }, heard);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("lever") && e.Message.Contains("OnPull")
                                          && e.Message.Contains("bomb") && e.Message.Contains("Boom") && e.Message.Contains("the game's own bug"));
        var io = world.IO();
        Assert.Equal(IOOutcome.Failed, io.HistoryAt(io.HistoryCount - 2).Outcome);
        Assert.Equal(IOOutcome.Delivered, io.HistoryAt(io.HistoryCount - 1).Outcome);

        // And the next tick is not poisoned by it.
        world.FireOutput(source, "OnPull");
        Tick(world);
        Assert.Equal(new[] { "bell", "bell" }, heard);
    }

    [Fact]
    public void TheHistoryKeepsTheLastDeliveriesInARing_AndSaysWhatBecameOfEach()
    {
        var heard = new List<string>();
        using var app = Boot(heard);
        var world = app.World;
        var io = world.IO();
        var target = Named(world, "target");
        var button = Named(world, "button");
        world.Add(button, new IOConnections { Wires = new[] { new Connection { Output = "OnUse", Target = "target", Input = "Hit", Parameter = "p" } } });

        world.FireOutput(button, "OnUse");
        io.FireInput(target, "NoSuchThing");
        Tick(world);

        var delivered = io.HistoryAt(io.HistoryCount - 2);
        Assert.Equal((button, "OnUse", target, "Hit", "p", IOOutcome.Delivered),
                     (delivered.Caller, delivered.Output, delivered.Target, delivered.Input, delivered.Parameter, delivered.Outcome));
        Assert.Equal(IOOutcome.NoSuchInput, io.HistoryAt(io.HistoryCount - 1).Outcome);
        var mine = new List<IORecord>();
        io.HistoryOf(button, mine);
        Assert.Single(mine);
        var line = EntityIO.Describe(delivered);
        Assert.True(line.Contains("button") && line.Contains(".OnUse -> ") && line.EndsWith(": Delivered", StringComparison.Ordinal), line);

        // More than it holds: the oldest go first.
        for (int i = 0; i < EntityIO.HistoryCapacity + 10; i++)
        {
            io.FireInput(target, "Hit", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (i % 100 == 99) Tick(world);          // under io_maxdispatch a tick
        }
        Tick(world);
        Assert.Equal(EntityIO.HistoryCapacity, io.HistoryCount);
        Assert.Equal((EntityIO.HistoryCapacity + 9).ToString(System.Globalization.CultureInfo.InvariantCulture), io.HistoryAt(io.HistoryCount - 1).Parameter);
    }

    [Fact]
    public void AMapEntityKeyIsNotAWireToABadSelector()
    {
        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).Boot("io");
        using var log = new CaptureSink();
        var entity = new MapEntity();
        entity.Keys["OnUse"] = "@,Kill";
        entity.Keys["OnTrigger"] = "@tag:nobody:declares_this,Kill";
        entity.Keys["OnUser1"] = "@lamps,Kill";
        var wires = MapEntityIO.Read(entity, app.Engine, "test.map:4");
        Assert.Equal("@lamps", Assert.Single(wires!).Target);
        Log.Flush();
        Assert.Contains(log.Entries, e => e.Message.Contains("test.map:4") && e.Message.Contains("declares_this"));
    }
}

[Collection(MeasurementsCollection.Name)]
public class EntityIOGroupMeasurements
{
    private static int _count;

    [Fact]
    public void AGroupFanOutAndItsHistoryAllocateNothingPerTick()
    {
        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).OnRegistered(a =>
            a.Engine.Inputs.Register("Count", static (World w, in IOContext io) => _count++)).Boot("io");
        var world = app.World;
        for (int i = 0; i < 8; i++)
        {
            var lamp = world.Create(Transform.At(Vector3.Zero), "lamp");
            world.Add(lamp, new IOGroup { Names = i % 2 == 0 ? "lamps" : "hall lamps" });
        }
        var timer = world.Create(Transform.At(Vector3.Zero), "timer");
        world.Add(timer, new LogicTimer { Interval = 0.05f, Repeat = true });
        world.Add(timer, new IOConnections
        {
            Wires = new[]
            {
                new Connection { Output = "OnTimer", Target = "@lamps", Input = "Count" },
            },
        });
        world.IO().FireInput(timer, "TimerStart");
        Assert.True(world.Systems.Disable("sage.physics.step"));     // Bepu's own 40 bytes a tick (see LogicTimeTests)

        for (int i = 0; i < 120; i++) { world.RunFixed(1f / 60f); Profiler.EndFrame(); }
        _count = 0;
        AllocationProbe.AssertNone(300, () => { world.RunFixed(1f / 60f); Profiler.EndFrame(); });
        Assert.True(_count >= 8 * 50, $"every lamp should have heard each timer ({_count})");
        Assert.Equal(EntityIO.HistoryCapacity, world.IO().HistoryCount);
    }
}
