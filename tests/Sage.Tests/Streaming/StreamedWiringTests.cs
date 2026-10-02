#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Entity I/O and time across sleeping sectors (issue #286). Wires target by name, so a wire to an entity in
// a dormant sector reaches nothing — and works the moment that sector is placed again; a time skip is one
// event whatever sectors are live, and a sector that slept through it comes back once, not replayed.
public class StreamedWiringTests
{
    public StreamedWiringTests() { _ = TestEnv.UserRoot; }

    private const float Sector = Terrain.SectorSize;

    // Sector (0, 0): the hero, a button wired to "east", a goblin and a repeating timer. Sector (2, 0): "east".
    private const string Content = """
    [
      { "type": "attribute", "id": "health", "max": 100, "start": 100 },
      { "type": "effect", "id": "hurt", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
      { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"] },
      { "type": "prefab", "id": "thing", "name": "thing" },
      { "type": "prefab", "id": "goblin", "name": "goblin", "parts": { "attributes": {} } },
      { "type": "prefab", "id": "blinker", "name": "blinker", "parts": { "timer": { "interval": 1, "repeat": true } } },
      { "type": "scene", "id": "valley", "streamed": true,
        "player": { "prefab": "hero", "at": [20, 1, 20] },
        "place": [
          { "prefab": "thing", "at": [30, 0, 30], "name": "button",
            "outputs": [ { "output": "OnTimer", "target": "east", "input": "Record", "parameter": "ping" },
                         { "output": "OnLater", "target": "east", "input": "Record", "parameter": "late", "delay": 2 } ] },
          { "prefab": "goblin", "at": [40, 0, 40], "name": "goblin" },
          { "prefab": "blinker", "at": [50, 0, 50], "name": "blinker",
            "outputs": [ { "output": "OnTimer", "target": "goblin", "input": "Record", "parameter": "blink" } ] },
          { "prefab": "thing", "at": [2078, 0, 30], "name": "east" } ] }
    ]
    """;

    private readonly List<(string Entity, string Parameter)> _arrived = new();

    private int Count(string parameter) => _arrived.Count(a => a.Parameter == parameter);

    private HeadlessApp Run()
    {
        var files = new MountFixture();
        files.Write("game", "data/content.json", Content);
        files.Mount("game", "game");
        var app = HeadlessApp.Bare().With(new PhysicsModule(), new StreamingModule()).WithGameplay()
            .OnRegistered(a => a.Engine.Inputs.Register("Record", (World world, in IOContext io) =>
                _arrived.Add((io.Self.Name ?? "", io.Parameter))))
            .Mount(files).StartScene("game:valley").Boot();
        app.Engine.Saves.Root = System.IO.Path.Combine(TestEnv.NewTempDir(), "saves");
        app.CVars.Execute("save_autosave 0");
        Tick(app.World);
        return app;
    }

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    private static Entity One(World world, string name) =>
        Assert.Single(world.QueryAll().Entities.ToEntityList(), e => e.Name == name);

    private static Entity Hero(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    private static void Walk(World world, float x)
    {
        world.Get<Transform>(Hero(world)).LocalPosition = world.Origin().ToOrigin(new Vector3(x, 1, 20));
        Tick(world);
    }

    private static bool Live(World world, string name) =>
        world.QueryAll().Entities.ToEntityList().Any(e => e.Name == name);

    // The wire's target is asleep: the output reaches nothing (no error), and the same wire works once the
    // sector is placed again. A delayed input due while the target sleeps is dropped; one still waiting when
    // it wakes arrives.
    [Xunit.Fact]
    public void AWireToASleepingEntityDoesNothing_ThenWorksOnceItWakes()
    {
        using var app = Run();
        var world = app.World;
        Assert.False(Live(world, "east"));

        world.FireOutput(One(world, "button"), "OnTimer");
        Tick(world, 5);
        Assert.Equal(0, Count("ping"));

        Walk(world, Sector + 20);                         // (1, 0): east's sector is in the ring, the button's too
        Assert.True(Live(world, "east"));
        world.FireOutput(One(world, "button"), "OnTimer");
        Tick(world, 2);
        Assert.Equal(1, Count("ping"));
        Assert.Equal("east", _arrived.Single(a => a.Parameter == "ping").Entity);
    }

    [Xunit.Fact]
    public void ADelayedInputIsDroppedIfItsTargetSleepsWhenDue_AndArrivesIfItWokeInTime()
    {
        using var app = Run();
        var world = app.World;

        world.FireOutput(One(world, "button"), "OnLater");      // due in two seconds; east is asleep
        Tick(world, 150);
        Assert.Equal(0, Count("late"));                         // dropped, not held for the wake
        Walk(world, Sector + 20);
        Tick(world, 5);
        Assert.Equal(0, Count("late"));

        world.FireOutput(One(world, "button"), "OnLater");
        Tick(world, 100);
        Assert.Equal(0, Count("late"));                         // not yet
        Tick(world, 30);
        Assert.Equal(1, Count("late"));
    }

    // A skip across a sector change: the clock moves once, a dormant sector's state is not replayed tick by
    // tick (its timer does not fire for the hours it slept), and it wakes once with its wounds.
    [Xunit.Fact]
    public void ATimeSkipWhileASectorSleeps_PassesOnceAndTheSectorWakesIntact()
    {
        using var app = Run();
        var world = app.World;
        var clock = WorldClock.Of(world);
        Effects.Apply(world, One(world, "goblin"), new RecordId("game", "hurt"), One(world, "goblin"), 7f);
        world.FlushCommands();
        world.IO().FireInput(One(world, "blinker"), "TimerStart");     // running when its sector goes to sleep
        Tick(world, 90);

        Walk(world, 3 * Sector + 20);                           // (0, 0) goes dormant
        Assert.False(Live(world, "goblin"));

        double before = clock.Elapsed;
        Assert.True(Time.Pass(world, 8, "rest"));
        Assert.Equal(before + 8, clock.Elapsed, 3);
        Tick(world);

        Walk(world, 20);                                        // and back
        Tick(world);
        Assert.Equal(93f, world.Attribute(One(world, "goblin"), new RecordId("game", "health")));
        Assert.Single(world.QueryAll().Entities.ToEntityList(), e => e.Name == "blinker");
        var ids = world.Query<Persistent>().Entities.ToEntityList().Select(e => e.GetComponent<Persistent>().Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        _arrived.Clear();
        Tick(world, 150);                                       // 2.5 s: a repeating one-second timer, not 8 hours of it
        Assert.InRange(Count("blink"), 1, 3);
    }
}
