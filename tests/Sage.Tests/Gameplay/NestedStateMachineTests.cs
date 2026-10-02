#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// State machines, part 2 (issue #280): nested and parallel states, `during` actions each tick, each
// state's OnEnter<name>/OnExit<name> outputs, a saved activator, and the engine prefab a placement
// points at its machine.
public class NestedStateMachineTests
{
    public NestedStateMachineTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    // A soldier: patrols (walking and looking around, a nested pair) until the alarm, then fights with
    // its legs and its arms at once (two parallel regions), until told to calm down.
    private const string Soldier = """
    [
      { "type": "state_machine", "id": "soldier", "initial": "patrol",
        "states": {
          "patrol": { "initial": "walk", "exit": [ { "add_var": "patrol_exits" } ],
                      "transitions": [ { "to": "combat", "when": { "var": "alarm", "eq": 1 } } ],
                      "states": {
                        "walk": { "enter": [ { "add_var": "walks" } ], "transitions": [ { "to": "look", "after": 0.5 } ] },
                        "look": { "transitions": [ { "to": "walk", "after": 0.5 } ] } } },
          "combat": { "parallel": true, "tags": ["hostile"],
                      "transitions": [ { "to": "patrol", "on": "Calm" } ],
                      "states": {
                        "legs": { "initial": "advance", "states": {
                          "advance": { "transitions": [ { "to": "cover", "on": "Hurt" } ] },
                          "cover":   { "tags": ["hiding"],
                                       "transitions": [ { "to": "advance", "after": 1,
                                                          "then": [ { "fire": "!activator", "input": "Record", "parameter": "regroup" } ] } ] } } },
                        "arms": { "initial": "aim", "states": {
                          "aim":   { "transitions": [ { "to": "shoot", "after": 0.25 } ] },
                          "shoot": { "during": [ { "add_var": "shots" } ] } } } } } } },
      { "type": "prefab", "id": "soldier", "name": "soldier", "parts": { "state_machine": { "machine": "soldier" } } },
      { "type": "prefab", "id": "thing", "name": "thing" }
    ]
    """;

    private sealed class Arrivals
    {
        public readonly List<(long Tick, string Entity, string Parameter)> List = new();
        public string[] Parameters => List.Select(a => a.Parameter).ToArray();
    }

    private static HeadlessApp NewGame(Arrivals arrivals, string content = Soldier)
    {
        var files = new MountFixture();
        files.Write("game", "data/soldier.json", content);
        files.Mount("game", "game");
        var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).Mount(files)
            .OnRegistered(a => a.Engine.Inputs.Register("Record", (World world, in IOContext io) =>
                arrivals.List.Add((world.Tick, io.Self.Name ?? "", io.Parameter))))
            .Boot("soldier");
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        return app;
    }

    private static Entity Spawn(World world, string prefab, string name)
    {
        var e = world.Spawn(new RecordId("game", prefab));
        e.Name = name;
        world.MakePersistent(e);
        return e;
    }

    // A soldier wired to a watcher: OnEnterCombat with a parameter of its own, OnExitPatrol handed the
    // state's name. Output names ignore case, so the wire may capitalise the state.
    private static Entity SpawnSoldier(World world)
    {
        var soldier = Spawn(world, "soldier", "soldier");
        world.Add(soldier, new IOConnections
        {
            Wires = new[]
            {
                new Connection { Output = "OnEnterCombat", Target = "watcher", Input = "Record", Parameter = "fight" },
                new Connection { Output = "OnExitPatrol", Target = "watcher", Input = "Record" },
            },
        });
        return soldier;
    }

    private static void Tick(World world, int times = 1)
    {
        for (int i = 0; i < times; i++) world.RunFixed(Dt);
    }

    private static double Var(World world, string name) => Vars.ValueOf(world, name);
    private static bool In(World world, Entity e, string state) => StateMachines.IsIn(world, e, state);

    // The acceptance test: a nested pair moves inside its parent without leaving it; the parent's own
    // transition leaves both; a parallel state enters every region, each region moves on its own (by
    // time, by input), `during` runs each tick in its state, and the per-state outputs reach wires.
    [Fact]
    public void NestedStatesMoveInsideTheirParent_AndParallelRegionsMoveOnTheirOwn()
    {
        var arrivals = new Arrivals();
        using var app = NewGame(arrivals);
        var world = app.World;
        Spawn(world, "thing", "watcher");
        var soldier = SpawnSoldier(world);

        Tick(world);
        Assert.Equal("walk", StateMachines.StateOf(world, soldier));     // the innermost state
        Assert.True(In(world, soldier, "patrol"));
        Assert.Equal(1, Var(world, "walks"));

        // walk → look after half a second: inside patrol, which is not left.
        Tick(world, 29);
        Assert.Equal("walk", StateMachines.StateOf(world, soldier));
        Tick(world);
        Assert.Equal("look", StateMachines.StateOf(world, soldier));
        Assert.True(In(world, soldier, "patrol"));
        Assert.Equal(0, Var(world, "patrol_exits"));

        // The alarm: patrol's own transition applies in look, and leaves both for combat, whose two
        // regions enter their initial states.
        Vars.Of(world).Set("alarm", 1);
        Tick(world);
        long fought = world.Tick;
        Assert.Equal(1, Var(world, "patrol_exits"));
        Assert.False(In(world, soldier, "patrol"));
        Assert.False(In(world, soldier, "look"));
        foreach (var state in new[] { "combat", "legs", "advance", "arms", "aim" }) Assert.True(In(world, soldier, state), state);
        Assert.Equal("advance", StateMachines.StateOf(world, soldier));
        Assert.True(StateMachines.HasTag(world, soldier, "hostile"));  // a tag of a state around it
        Assert.Contains((fought, "watcher", "patrol"), arrivals.List);   // OnExitPatrol, handed the name
        Assert.Contains((fought, "watcher", "fight"), arrivals.List);    // OnEnterCombat, its own parameter

        // The arms move by time while the legs stay; shooting runs `during` each tick from the next.
        Tick(world, 14);
        Assert.True(In(world, soldier, "aim"));
        Tick(world);
        Assert.True(In(world, soldier, "shoot"));
        Assert.True(In(world, soldier, "advance"));
        Assert.Equal(0, Var(world, "shots"));
        Tick(world, 3);
        Assert.Equal(3, Var(world, "shots"));

        // The legs move on an input, the arms keep shooting.
        world.IO().FireInput(soldier, "Hurt");
        Tick(world);
        Assert.True(In(world, soldier, "cover"));
        Assert.True(In(world, soldier, "shoot"));
        Assert.Equal("cover", StateMachines.StateOf(world, soldier));
        Assert.True(StateMachines.HasTag(world, soldier, "hiding"));
        Assert.Equal(4, Var(world, "shots"));

        // Calm: combat's transition leaves every region; shooting stops.
        world.IO().FireInput(soldier, "Calm");
        Tick(world);
        Assert.Equal("walk", StateMachines.StateOf(world, soldier));
        Assert.False(In(world, soldier, "combat"));
        Assert.False(In(world, soldier, "shoot"));
        Assert.Equal(2, Var(world, "walks"));
        double shots = Var(world, "shots");
        Tick(world, 5);
        Assert.Equal(shots, Var(world, "shots"));

        // SetState into a region: the parallel parent and its other region come too.
        Assert.True(StateMachines.SetState(world, soldier, "shoot"));
        foreach (var state in new[] { "combat", "legs", "advance", "arms", "shoot" }) Assert.True(In(world, soldier, state), state);
        Assert.Equal(2, Var(world, "patrol_exits"));
    }

    // Done for #280: a nested machine saved mid-state (two regions, each with its own time) loads in the
    // same states with the same times, its activator found again by persistent id, and carries on: the
    // transition it was waiting for fires at the activator on the tick it would have.
    [Fact]
    public void ANestedMachineRoundTripsASaveMidState_WithItsActivator()
    {
        var arrivals = new Arrivals();
        using var app = NewGame(arrivals);
        var world = app.World;
        Spawn(world, "thing", "watcher");
        var player = Spawn(world, "thing", "player");
        var soldier = SpawnSoldier(world);
        Tick(world);
        Vars.Of(world).Set("alarm", 1);
        Tick(world, 20);                                               // shooting by now
        world.IO().FireInput(soldier, "Hurt", activator: player);
        Tick(world);
        Assert.True(In(world, soldier, "cover"));
        Assert.Equal(player, world.Get<StateMachine>(soldier).Activator);
        Tick(world, 30);                                               // half a second in cover
        float shootTime = Time(world, soldier, "shoot");
        Assert.True(app.Engine.Saves.Save("mid"));

        string file = Path.Combine(app.Engine.Saves.Root, "mid", "world_soldier.json");
        string saved = File.ReadAllText(file);
        Assert.Contains("\"cover\"", saved);
        Assert.Contains("\"shoot\"", saved);
        var machine = FindMachine(JsonNode.Parse(saved))!;
        Assert.Equal(world.Get<Persistent>(player).Id.ToString(), (string?)machine["Activator"]);   // by persistent id

        Tick(world, 40);
        Assert.Contains(arrivals.List, a => a.Parameter == "regroup");

        Assert.True(app.Engine.Saves.Load("mid"));
        arrivals.List.Clear();
        soldier = world.FindByName("soldier");
        player = world.FindByName("player");
        foreach (var state in new[] { "combat", "legs", "cover", "arms", "shoot" }) Assert.True(In(world, soldier, state), state);
        Assert.Equal("cover", StateMachines.StateOf(world, soldier));
        Assert.Equal(0.5f, world.Get<StateMachine>(soldier).TimeInState, 3);
        Assert.Equal(shootTime, Time(world, soldier, "shoot"), 3);
        Assert.Equal(player, world.Get<StateMachine>(soldier).Activator);

        // cover → advance after a second: 30 ticks more, then the then's fire at !activator arrives.
        Tick(world, 29);
        Assert.True(In(world, soldier, "cover"));
        Tick(world);
        Assert.True(In(world, soldier, "advance"));
        Tick(world);
        Assert.Contains(arrivals.List, a => a.Parameter == "regroup" && a.Entity == "player");
    }

    // A save written before `Active` existed (or one whose list is edited away) is placed from State:
    // the states around it, and the other region in its initial state, with nothing run.
    [Fact]
    public void ASaveWithOnlyTheStatePlacesTheStatesAroundIt()
    {
        var arrivals = new Arrivals();
        using var app = NewGame(arrivals);
        var world = app.World;
        Spawn(world, "thing", "watcher");
        var soldier = SpawnSoldier(world);
        Tick(world);
        Vars.Of(world).Set("alarm", 1);
        Tick(world, 20);
        world.IO().FireInput(soldier, "Hurt");
        Tick(world, 10);
        Assert.True(In(world, soldier, "shoot"));
        Assert.True(app.Engine.Saves.Save("old"));

        string file = Path.Combine(app.Engine.Saves.Root, "old", "world_soldier.json");
        var root = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.True(DropActive(root) > 0);
        File.WriteAllText(file, root.ToJsonString());

        using var log = new CaptureSink();
        Assert.True(app.Engine.Saves.Load("old"));
        soldier = world.FindByName("soldier");
        double shots = Var(world, "shots");
        Tick(world);
        foreach (var state in new[] { "combat", "legs", "cover", "arms", "aim" }) Assert.True(In(world, soldier, state), state);
        Assert.False(In(world, soldier, "shoot"));
        Assert.Equal(shots, Var(world, "shots"));
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warn && e.Message.Contains("State machine"));
    }

    private static float Time(World world, Entity e, string state) =>
        world.Get<StateMachine>(e).Active!.First(a => a.Name == state).Time;

    private static JsonObject? FindMachine(JsonNode? node)
    {
        if (node is JsonObject o)
        {
            if (o.ContainsKey("State") && o.ContainsKey("Active")) return o;
            foreach (var (_, child) in o) if (FindMachine(child) is { } found) return found;
        }
        else if (node is JsonArray a)
            foreach (var child in a) if (FindMachine(child) is { } found) return found;
        return null;
    }

    private static int DropActive(JsonNode? node)
    {
        int dropped = 0;
        if (node is JsonObject o)
        {
            if (o.ContainsKey("State") && o.Remove("Active")) dropped++;
            foreach (var (_, child) in o.ToList()) dropped += DropActive(child);
        }
        else if (node is JsonArray a)
            foreach (var child in a) dropped += DropActive(child);
        return dropped;
    }

    // Load checks for nested states: a compound state needs an initial inside it, a parallel one needs
    // states, a leaf has no initial, and names are unique across the machine.
    [Fact]
    public void NestedStatesAreCheckedAtLoad()
    {
        var files = new MountFixture();
        files.Write("game", "data/machines.json", """
        [
          { "type": "state_machine", "id": "bad", "initial": "a",
            "states": {
              "a": { "states": { "a1": {}, "a2": {} } },
              "b": { "initial": "a1", "states": { "b1": {} } },
              "c": { "parallel": true },
              "d": { "initial": "x" },
              "e": { "initial": "e1", "states": { "e1": {}, "A1": {} } } } }
        ]
        """);
        files.Mount("game", "game");
        using var log = new CaptureSink();
        using var app = HeadlessApp.Bare().Mount(files).Boot();

        var errors = log.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message).ToList();
        Assert.Equal(5, app.Records.ErrorCount);
        Assert.Contains(errors, m => m.Contains("a state with \"states\" needs an \"initial\""));
        Assert.Contains(errors, m => m.Contains("'a1' is not one of its states"));
        Assert.Contains(errors, m => m.Contains("parallel state needs \"states\""));
        Assert.Contains(errors, m => m.Contains("an \"initial\" on a state with no \"states\""));
        Assert.Contains(errors, m => m.Contains("two states are called 'A1'"));
    }

    // The engine prefab: placed with an override naming its machine, wired by a state's own output,
    // which load does not warn about (no plugin declares it; a machine fires it).
    [Fact]
    public void TheEnginePrefabRunsTheMachineAPlacementNames_AndAStatesOutputsAreWirable()
    {
        var arrivals = new Arrivals();
        var files = new MountFixture();
        files.Write("game", "data/alarm.json", """
        [
          { "type": "state_machine", "id": "alarm", "initial": "quiet",
            "states": { "quiet":   { "transitions": [ { "to": "ringing", "on": "Trip" } ] },
                        "ringing": { "enter": [ { "add_var": "rings" } ] } } },
          { "type": "prefab", "id": "thing", "name": "thing" },
          { "type": "scene", "id": "main",
            "place": [
              { "prefab": "sage:logic_state_machine", "name": "alarm",
                "overrides": { "parts": { "state_machine": { "machine": "game:alarm" } } },
                "outputs": [ { "output": "OnEnterRinging", "target": "siren", "input": "Record" },
                             { "output": "OnExitQuiet", "target": "siren", "input": "Record", "parameter": "hush over" } ] },
              { "prefab": "thing", "name": "siren" } ] }
        ]
        """);
        files.Mount("game", "game");
        using var log = new CaptureSink();
        using var app = HeadlessApp.Bare().WithEngineContent().With(new PhysicsModule(), new EntityIOModule()).Mount(files)
            .OnRegistered(a => a.Engine.Inputs.Register("Record", (World world, in IOContext io) =>
                arrivals.List.Add((world.Tick, io.Self.Name ?? "", io.Parameter))))
            .StartScene("game:main").Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warn && (e.Message.Contains("OnEnterRinging") || e.Message.Contains("OnExitQuiet")));
        Assert.True(app.Engine.Outputs.Has("OnEnterRinging"));
        Assert.False(app.Engine.Outputs.Has("OnEnterNowhere"));

        var world = app.World;
        var alarm = world.FindByName("alarm");
        Assert.Equal(new RecordId("game", "alarm"), world.Get<StateMachine>(alarm).Machine);
        Tick(world);
        Assert.Equal("quiet", StateMachines.StateOf(world, alarm));
        world.IO().FireInput("alarm", "Trip");
        Tick(world);
        Assert.Equal("ringing", StateMachines.StateOf(world, alarm));
        Assert.Equal(1, Var(world, "rings"));
        Tick(world);
        Assert.Equal(new[] { "hush over", "ringing" }, arrivals.Parameters);
        Assert.All(arrivals.List, a => Assert.Equal("siren", a.Entity));
    }
}

// Nested, parallel and `during` cost nothing per tick either.
[Collection(MeasurementsCollection.Name)]
public class NestedStateMachineAllocationTests
{
    public NestedStateMachineAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void NestedAndParallelMachinesWithDuringAllocateNothingPerTick()
    {
        // Two regions that each flip between two states on their own clocks, a `during` in each, and
        // the whole thing re-entered by the outer state's own transition every second.
        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule())
            .File("data/busy.json", """
                [ { "type": "state_machine", "id": "busy", "initial": "both",
                    "states": {
                      "both": { "parallel": true, "during": [ { "add_var": "ticks" } ],
                                "transitions": [ { "to": "both", "after": 1, "then": [ { "add_var": "rounds" } ] } ],
                                "states": {
                                  "left":  { "initial": "l1", "states": {
                                    "l1": { "transitions": [ { "to": "l2", "after": 0.1 } ] },
                                    "l2": { "during": [ { "add_var": "l2" } ], "transitions": [ { "to": "l1", "after": 0.1 } ] } } },
                                  "right": { "initial": "r1", "states": {
                                    "r1": { "transitions": [ { "to": "r2", "after": 0.15 } ] },
                                    "r2": { "enter": [ { "fire": "!self", "input": "Back", "delay": 0.05 } ],
                                            "transitions": [ { "to": "r1", "on": "Back" } ] } } } } } } },
                  { "type": "prefab", "id": "busy", "parts": { "state_machine": { "machine": "busy" } } } ]
                """, "test")
            .Boot("sm");
        var world = app.World;
        for (int i = 0; i < 8; i++)
        {
            var e = world.Spawn(new RecordId("test", "busy"));
            world.Add(e, new IOConnections
            {
                Wires = new[] { new Connection { Output = "OnEnterl2", Target = "!self", Input = "SetState", Parameter = "l2" } },
            });
        }
        Assert.True(world.Systems.Disable("sage.physics.step"));

        for (int i = 0; i < 120; i++) { world.RunFixed(1f / 60f); Profiler.EndFrame(); }   // warm
        double rounds = Vars.ValueOf(world, "rounds");
        AllocationProbe.AssertNone(300, () => { world.RunFixed(1f / 60f); Profiler.EndFrame(); });
        Assert.True(Vars.ValueOf(world, "rounds") > rounds + 8, "the machines should have kept going round");
        Assert.True(Vars.ValueOf(world, "l2") > 50);
    }
}
