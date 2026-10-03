#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sage.Tests;

using Assert = Xunit.Assert;

[Component("test:structural_mark")] public struct StructuralMark : IComponent { public int Value; }

// Structural changes as queued game events (issue #282, 04 §3.3): `Added<T>` and `Removed<T>` read with a
// cursor, in place of the immediate `World.ComponentAdded`/`ComponentRemoved` callbacks; and the engine
// signals (world created, scene loaded, pause) as C# events on the engine and `EngineSignal` in the world.
public class StructuralEventTests
{
    public StructuralEventTests() { _ = TestEnv.UserRoot; }

    // A reactive system: counts, per entity id, the adds and removes of StructuralMark it has read.
    [System("test.structural.reactive", Phase.Late)]
    internal sealed class Reactive : ISystem
    {
        private readonly EventReader<Added<StructuralMark>> _added;
        private readonly EventReader<Removed<StructuralMark>> _removed;
        public readonly int[] Adds = new int[4096];
        public readonly int[] Removes = new int[4096];
        public readonly int[] LastRemovedValue = new int[4096];

        public Reactive(World world)
        {
            _added = world.Events.Reader<Added<StructuralMark>>(this);
            _removed = world.Events.Reader<Removed<StructuralMark>>(this);
        }

        public void Run(in SystemContext ctx)
        {
            foreach (ref readonly var ev in _added.Read()) Adds[ev.Entity.Id]++;
            foreach (ref readonly var ev in _removed.Read())
            {
                Removes[ev.Entity.Id]++;
                LastRemovedValue[ev.Entity.Id] = ev.Value.Value;
            }
        }
    }

    // The issue's done criterion: a reactive system sees exactly one add and one remove per entity —
    // whether the component was removed, or went with its entity, directly or from a command buffer.
    [Fact]
    public void AReactiveSystemSeesOneAddAndOneRemovePerEntity()
    {
        using var world = new World("structural");
        var reactive = new Reactive(world);
        world.AddSystem(reactive);

        var entities = Enumerable.Range(0, 30).Select(i => world.Create($"e{i}")).ToArray();
        for (int i = 0; i < entities.Length; i++) world.Add(entities[i], new StructuralMark { Value = i });
        entities[0].AddComponent(new StructuralMark { Value = 100 });    // a replaced value is not another add
        world.Add(entities[1], new Velocity());                          // nor is a component nobody reads
        world.RunFixed(1f / 60f);

        Assert.All(entities, e => Assert.Equal(1, reactive.Adds[e.Id]));
        Assert.All(entities, e => Assert.Equal(0, reactive.Removes[e.Id]));

        int[] ids = entities.Select(e => e.Id).ToArray();
        for (int i = 0; i < 10; i++) world.Remove<StructuralMark>(entities[i]);   // removed
        for (int i = 10; i < 20; i++) world.Destroy(entities[i]);                 // destroyed with it
        for (int i = 20; i < 30; i++) world.Commands.Destroy(entities[i]);        // destroyed at playback
        world.FlushCommands();
        world.RunFixed(1f / 60f);
        world.RunFixed(1f / 60f);

        for (int i = 0; i < ids.Length; i++)
        {
            Assert.Equal(1, reactive.Adds[ids[i]]);
            Assert.Equal(1, reactive.Removes[ids[i]]);
            Assert.Equal(i == 0 ? 100 : i, reactive.LastRemovedValue[ids[i]]);   // the value as it went
        }
    }

    // An add and a remove in one tick are both delivered; Sequence says which came first.
    [Fact]
    public void AnAddAndARemoveInOneTickAreBothDeliveredInOrder()
    {
        using var world = new World("structural-order");
        var added = world.Events.Reader<Added<StructuralMark>>("test");
        var removed = world.Events.Reader<Removed<StructuralMark>>("test");

        var a = world.Create("a");
        var b = world.Create("b");
        world.Add(a, new StructuralMark { Value = 1 });   // a: added, then removed
        world.Remove<StructuralMark>(a);
        world.Add(b, new StructuralMark { Value = 2 });   // b: had one, removed, then added again
        world.RunFixed(1f / 60f);
        foreach (ref readonly var _ in added.Read()) { }
        world.Remove<StructuralMark>(b);
        world.Add(b, new StructuralMark { Value = 3 });

        var adds = new List<(Entity Entity, long Sequence)>();
        var removes = new List<(Entity Entity, int Value, long Sequence)>();
        foreach (ref readonly var ev in added.Read()) adds.Add((ev.Entity, ev.Sequence));
        foreach (ref readonly var ev in removed.Read()) removes.Add((ev.Entity, ev.Value.Value, ev.Sequence));

        Assert.Equal(new[] { b }, adds.Select(x => x.Entity));
        Assert.Equal(new[] { a, b }, removes.Select(x => x.Entity));
        Assert.Equal(new[] { 1, 2 }, removes.Select(x => x.Value));
        Assert.True(removes[1].Sequence < adds[0].Sequence);   // b was removed, then added: it has one now
        Assert.True(world.Has<StructuralMark>(b));
    }

    // Nothing is published for a type until somebody reads it: no queue, no events, nothing to prune.
    [Fact]
    public void ATypeNobodyReadsIsNotPublished()
    {
        using var world = new World("structural-optin");
        var e = world.Create("e");
        world.Add(e, new StructuralMark { Value = 1 });
        Assert.DoesNotContain(world.Events.Stats(), s => s.Event.StartsWith("Added") || s.Event.StartsWith("Removed"));

        var reader = world.Events.Reader<Added<StructuralMark>>("late");   // starts with what happens next
        Assert.False(reader.HasPending);
        world.Add(world.Create("f"), new StructuralMark());
        Assert.True(reader.HasPending);
        Assert.Equal(0, world.Events.Queue<Removed<StructuralMark>>().Count);   // made, but nobody reads it
    }

    // A display-rate reader asks for the Frame queue and sees the changes too.
    [Fact]
    public void AFrameReaderSeesTheChanges()
    {
        using var world = new World("structural-frame");
        var frame = world.Events.Reader<Removed<StructuralMark>>("hud", Schedule.Frame);
        var e = world.Create("e");
        world.Add(e, new StructuralMark { Value = 7 });
        world.Destroy(e);
        int seen = 0;
        foreach (ref readonly var ev in frame.Read()) { Assert.Equal(7, ev.Value.Value); seen++; }
        Assert.Equal(1, seen);
    }

    // The engine signals: world created and scene loaded as C# events on the engine, and as `EngineSignal`
    // in the world for a system that asked for a reader while the world was being furnished.
    private sealed class SignalModule : IModule
    {
        public EventReader<EngineSignal>? Reader;
        public void Init(ModuleContext ctx) { }
        public void OnWorldCreated(World world) => Reader = world.Events.Reader<EngineSignal>("signals");
    }

    [Fact]
    public void EngineSignalsSayAWorldWasCreatedAndASceneLoaded()
    {
        var module = new SignalModule();
        using var app = HeadlessApp.ForGame(Path.Combine(TestEnv.FolderAbove("tests"), "tests", "games", "scene-only"))
            .With(module).Build();
        var log = new List<string>();
        app.Engine.Signals.WorldCreated += w => log.Add("created " + w.Name);
        app.Engine.Signals.SceneLoaded += (w, scene) => log.Add($"scene {scene} in {w.Name}");
        app.Engine.Signals.PauseChanged += (w, paused) => log.Add($"paused {paused}");
        app.Engine.Signals.WorldDestroying += w => log.Add("destroying " + w.Name);

        var world = app.CreateWorld("main");
        var yard = new RecordId("sceneonly", "yard");
        Assert.Equal(new[] { "scene sceneonly:yard in main", "created main" }, log);

        Assert.True(app.Engine.Scenes.Load(world, yard));
        world.Paused = true;     // raised by the next tick, once however many ticks it stays paused
        world.RunFixed(1f / 60f);
        world.RunFixed(1f / 60f);
        world.Paused = false;
        world.RunFixed(1f / 60f);
        world.Paused = true;     // paused and resumed between two ticks: nothing to say
        world.Paused = false;
        world.RunFixed(1f / 60f);
        Assert.Equal("scene sceneonly:yard in main", log[2]);
        Assert.Equal(new[] { "paused True", "paused False" }, log.Skip(3));

        var kinds = new List<(EngineSignalKind, RecordId)>();
        foreach (ref readonly var ev in module.Reader!.Read()) kinds.Add((ev.Kind, ev.Scene));
        Assert.Equal(new[]
        {
            (EngineSignalKind.SceneLoaded, yard), (EngineSignalKind.WorldCreated, default(RecordId)),
            (EngineSignalKind.SceneLoaded, yard), (EngineSignalKind.Paused, default), (EngineSignalKind.Resumed, default),
        }, kinds);

        app.Engine.DestroyWorld(world);
        Assert.Equal("destroying main", log[^1]);
    }
}

// Allocation: the structural events cost nothing per tick once their queues are warm.
[Collection(MeasurementsCollection.Name)]
public class StructuralEventAllocationTests
{
    public StructuralEventAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void ReadingAddsAndRemovesAllocatesNothingPerTick()
    {
        using var world = new World("structural-alloc");
        var reactive = new StructuralEventTests.Reactive(world);
        world.AddSystem(reactive);
        var entities = Enumerable.Range(0, 64).Select(i => world.Create($"e{i}")).ToArray();

        void Step()
        {
            for (int i = 0; i < entities.Length; i++)
            {
                if (world.Has<StructuralMark>(entities[i])) world.Remove<StructuralMark>(entities[i]);
                else world.Add(entities[i], new StructuralMark { Value = i });
            }
            world.RunFixed(1f / 60f);
            Profiler.EndFrame();
        }

        for (int i = 0; i < 20; i++) Step();   // warm: archetypes, queues at their high-water mark
        AllocationProbe.AssertNone(200, Step);
        Assert.All(entities, e => Assert.Equal(110, reactive.Adds[e.Id]));
        Assert.All(entities, e => Assert.Equal(110, reactive.Removes[e.Id]));
    }
}
