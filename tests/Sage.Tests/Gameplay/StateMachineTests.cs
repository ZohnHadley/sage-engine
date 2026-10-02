#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// State machines as data (issue #92): the `state_machine` record, `sage:state_machine`, SetState and
// OnStateChanged, and the `on` names a machine listens for.
public class StateMachineTests
{
    public StateMachineTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    // The acceptance machine: a guard that idles, grows alert when the alarm var is set, attacks after a
    // second of it and goes back to idle when told to calm down (an input nobody registered).
    private const string Guard = """
    [
      { "type": "state_machine", "id": "guard", "initial": "idle",
        "states": {
          "idle":   { "enter": [ { "add_var": "idles" } ],
                      "transitions": [ { "to": "alert", "when": { "var": "alarm", "eq": 1 } } ] },
          "alert":  { "enter": [ { "add_var": "alerts" } ], "exit": [ { "add_var": "alert_exits" } ], "tags": ["hostile"],
                      "transitions": [ { "to": "attack", "after": 1 }, { "to": "idle", "on": "Calm" } ] },
          "attack": { "tags": ["hostile", "armed"],
                      "transitions": [ { "to": "idle", "on": "Calm", "then": [ { "set_var": "alarm", "value": 0 } ] } ] } } },
      { "type": "prefab", "id": "guard", "name": "guard", "parts": { "state_machine": { "machine": "guard" } } },
      { "type": "prefab", "id": "thing", "name": "thing" }
    ]
    """;

    // The same guard after an edit: no attack state any more, and alert gives up after two seconds.
    private const string GuardAfterEdit = """
    [
      { "type": "state_machine", "id": "guard", "initial": "idle",
        "states": {
          "idle":   { "enter": [ { "add_var": "idles" } ],
                      "transitions": [ { "to": "alert", "when": { "var": "alarm", "eq": 1 } } ] },
          "alert":  { "enter": [ { "add_var": "alerts" } ], "exit": [ { "add_var": "alert_exits" } ], "tags": ["hostile"],
                      "transitions": [ { "to": "idle", "after": 2 }, { "to": "idle", "on": "Calm" } ] },
          "unused": {} } },
      { "type": "prefab", "id": "guard", "name": "guard", "parts": { "state_machine": { "machine": "guard" } } },
      { "type": "prefab", "id": "thing", "name": "thing" }
    ]
    """;

    // What arrived where, and on which tick: a `Record` input that notes its parameter.
    private sealed class Arrivals
    {
        public readonly List<(long Tick, string Entity, string Parameter)> List = new();
        public string[] Parameters => List.Select(a => a.Parameter).ToArray();
    }

    private static (HeadlessApp App, MountFixture Files) NewGame(Arrivals arrivals, string content = Guard)
    {
        var files = new MountFixture();
        files.Write("game", "data/guard.json", content);
        files.Mount("game", "game");
        var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).Mount(files)
            .OnRegistered(a => a.Engine.Inputs.Register("Record", (World world, in IOContext io) =>
                arrivals.List.Add((world.Tick, io.Self.Name ?? "", io.Parameter))))
            .Boot("guard");
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        return (app, files);
    }

    // A guard, persistent (so a save carries it), with OnStateChanged wired to a watcher.
    private static Entity SpawnGuard(World world, string name = "guard")
    {
        var guard = world.Spawn(new RecordId("game", "guard"));
        guard.Name = name;
        world.MakePersistent(guard);
        world.Add(guard, new IOConnections
        {
            Wires = new[] { new Connection { Output = StateMachines.OnStateChanged, Target = "watcher", Input = "Record" } },
        });
        return guard;
    }

    private static Entity Watcher(World world)
    {
        var watcher = world.Create(Transform.At(Vector3.Zero), "watcher");
        watcher.Name = "watcher";
        return watcher;
    }

    private static void Tick(World world, int times = 1)
    {
        for (int i = 0; i < times; i++) world.RunFixed(Dt);
    }

    private static string? State(World world, Entity e) => StateMachines.StateOf(world, e);
    private static double Var(World world, string name) => Vars.ValueOf(world, name);

    // The acceptance test: idle → alert on a condition → attack after a timeout → idle on an input, each
    // on the tick it should, with enter, exit and then actions run and OnStateChanged naming the state.
    [Fact]
    public void AGuardIdlesGrowsAlertAttacksAfterATimeoutAndCalmsDownOnAnInput()
    {
        var arrivals = new Arrivals();
        var (app, _) = NewGame(arrivals);
        using (app)
        {
            var world = app.World;
            Watcher(world);
            var guard = SpawnGuard(world);
            Assert.Null(State(world, guard));                             // not started before its first tick

            Tick(world);
            Assert.Equal("idle", State(world, guard));
            Assert.Equal(1, Var(world, "idles"));                         // idle's enter ran
            Assert.Empty(arrivals.List);                                  // starting is not a change
            Tick(world, 10);
            Assert.Equal("idle", State(world, guard));

            Vars.Of(world).Set("alarm", 1);
            Tick(world);
            long alerted = world.Tick;
            Assert.Equal("alert", State(world, guard));
            Assert.Equal(1, Var(world, "alerts"));
            Assert.True(StateMachines.HasTag(world, guard, "hostile"));
            Assert.False(StateMachines.HasTag(world, guard, "armed"));
            // OnStateChanged reached its wire on the same tick, handed the new state's name.
            Assert.Equal((alerted, "watcher", "alert"), arrivals.List.Single());

            // `after: 1`: on the 60th tick in the state, not the 59th.
            Tick(world, 59);
            Assert.Equal("alert", State(world, guard));
            Tick(world);
            Assert.Equal("attack", State(world, guard));
            Assert.Equal(alerted + 60, arrivals.List.Last().Tick);
            Assert.Equal(1, Var(world, "alert_exits"));
            Assert.True(StateMachines.HasTag(world, guard, "armed"));

            // `on: "Calm"`: an input no plugin registered, sent the way a wire or ent_fire would.
            Assert.True(app.Engine.Inputs.Has("Calm"));
            Assert.False(app.Engine.Inputs.TryGet("Calm", out _));
            world.IO().FireInput(guard, "Calm");
            Tick(world);
            Assert.Equal("idle", State(world, guard));
            Assert.Equal(0, Var(world, "alarm"));                         // the transition's then
            Assert.Equal(2, Var(world, "idles"));
            Tick(world);                                                  // an output fired by an input arrives next tick
            Assert.Equal(new[] { "alert", "attack", "idle" }, arrivals.Parameters);

            // Calm in a state that does not listen for it: nothing, and no warning of an unknown input.
            using var log = new CaptureSink();
            world.IO().FireInput(guard, "Calm");
            Tick(world, 5);
            Assert.Equal("idle", State(world, guard));
            Assert.DoesNotContain(log.Entries, e => e.Message.Contains("no input called 'Calm'"));
        }
    }

    // State and time in it are saved by name: a save taken half-way through alert's second attacks on the
    // tick it would have, after the load.
    [Fact]
    public void StateAndTimeInItSurviveSaveAndLoad()
    {
        var arrivals = new Arrivals();
        var (app, _) = NewGame(arrivals);
        using (app)
        {
            var world = app.World;
            Watcher(world);
            SpawnGuard(world);
            Tick(world);
            Vars.Of(world).Set("alarm", 1);
            Tick(world);
            Tick(world, 30);                                              // half a second in alert
            Assert.True(app.Engine.Saves.Save("half"));

            string file = Path.Combine(app.Engine.Saves.Root, "half", "world_guard.json");
            var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.ToJsonString();
            Assert.Contains("sage:state_machine", saved);
            Assert.Contains("\"alert\"", saved);
            Assert.DoesNotContain("\"Index\"", saved);                  // the cache is not saved: the name is

            Tick(world, 40);
            Assert.Equal("attack", State(world, world.FindByName("guard")));

            Assert.True(app.Engine.Saves.Load("half"));
            var guard = world.FindByName("guard");
            Assert.Equal("alert", State(world, guard));
            Assert.Equal(0.5f, world.Get<StateMachine>(guard).TimeInState, 3);
            Assert.Equal(1, Var(world, "alerts"));                        // no enter again: it was already there
            Tick(world, 29);
            Assert.Equal("alert", State(world, guard));
            Tick(world);
            Assert.Equal("attack", State(world, guard));
        }
    }

    // Hot reload: a machine whose state is still there stays in it, with its time and the new
    // transitions; one whose state was removed goes to initial, with a warning and an OnStateChanged.
    [Fact]
    public void HotReloadKeepsAStateThatIsStillThere_AndSendsOneThatIsGoneToInitialWithAWarning()
    {
        var arrivals = new Arrivals();
        var (app, files) = NewGame(arrivals);
        using (app)
        {
            var world = app.World;
            Watcher(world);
            var attacker = SpawnGuard(world, "attacker");
            var watchful = SpawnGuard(world, "watchful");
            Tick(world);
            Vars.Of(world).Set("alarm", 1);
            Tick(world, 61);
            Assert.Equal("attack", State(world, attacker));
            StateMachines.SetState(world, watchful, "alert");
            Tick(world, 30);
            Assert.Equal("alert", State(world, watchful));
            Assert.Equal(0.5f, world.Get<StateMachine>(watchful).TimeInState, 3);
            arrivals.List.Clear();

            // The edit: no attack state any more, and alert now gives up after two seconds.
            files.Write("game", "data/guard.json", GuardAfterEdit);
            Vars.Of(world).Set("alarm", 0);
            using var log = new CaptureSink();
            app.Records.Reload();
            Assert.Equal(0, app.Records.ErrorCount);
            Tick(world);

            Assert.Equal("idle", State(world, attacker));
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("no state 'attack'"));
            Assert.Contains((world.Tick, "watcher", "idle"), arrivals.List);

            // The other stayed in alert with its time, and follows the reloaded transition.
            Assert.Equal("alert", State(world, watchful));
            Assert.Equal(0.5f + Dt, world.Get<StateMachine>(watchful).TimeInState, 3);
            Tick(world, 88);
            Assert.Equal("alert", State(world, watchful));
            Tick(world);
            Assert.Equal("idle", State(world, watchful));
        }
    }

    // SetState: to a state now (exit, enter, OnStateChanged), nothing when already there, a warning for
    // a state the machine has not got. A wire with a parameter of its own keeps it.
    [Fact]
    public void SetStateGoesThereAtOnce_AndAWireWithItsOwnParameterKeepsIt()
    {
        var arrivals = new Arrivals();
        var (app, _) = NewGame(arrivals);
        using (app)
        {
            var world = app.World;
            Watcher(world);
            var guard = SpawnGuard(world);
            world.Get<IOConnections>(guard).Wires = new[]
            {
                new Connection { Output = StateMachines.OnStateChanged, Target = "watcher", Input = "Record" },
                new Connection { Output = StateMachines.OnStateChanged, Target = "watcher", Input = "Record", Parameter = "changed" },
            };
            Tick(world);

            world.IO().FireInput(guard, StateMachines.SetStateInput, "attack");
            Tick(world);
            Assert.Equal("attack", State(world, guard));
            Tick(world);
            Assert.Equal(new[] { "attack", "changed" }, arrivals.Parameters);

            arrivals.List.Clear();
            using var log = new CaptureSink();
            world.IO().FireInput(guard, StateMachines.SetStateInput, "attack");      // already there
            world.IO().FireInput(guard, StateMachines.SetStateInput, "sleep");       // no such state
            Tick(world, 2);
            Assert.Equal("attack", State(world, guard));
            Assert.Empty(arrivals.List);
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("has no state 'sleep'"));

            // At an entity with no machine: a warning, nothing else.
            var thing = world.Spawn(new RecordId("game", "thing"));
            Assert.False(StateMachines.SetState(world, thing, "idle"));
        }
    }

    // Load checks: an initial state and every `to` that are states, and a wire that sends a name a
    // machine listens for is not an unknown input — though a scene is checked before the machine is.
    [Fact]
    public void AMachineIsCheckedAtLoad_AndAWireMaySendANameAMachineListensFor()
    {
        var files = new MountFixture();
        files.Write("game", "data/machines.json", """
        [
          { "type": "state_machine", "id": "broken", "initial": "idel",
            "states": { "idle": { "transitions": [ { "to": "atack", "on": "Provoke" }, { "to": "idle", "after": -1 } ] } } },
          { "type": "prefab", "id": "thing", "name": "thing" },
          { "type": "scene", "id": "main",
            "place": [
              { "prefab": "thing", "name": "a", "outputs": [
                  { "output": "OnUse", "target": "b", "input": "Provoke" },
                  { "output": "OnUse", "target": "b", "input": "Provok" } ] },
              { "prefab": "thing", "name": "b" } ] }
        ]
        """);
        files.Mount("game", "game");
        using var log = new CaptureSink();
        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).Mount(files).StartScene("game:main").Boot();

        var errors = log.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message).ToList();
        Assert.Equal(4, app.Records.ErrorCount);
        Assert.Contains(errors, m => m.Contains("'idel' is not one of its states") && m.Contains("idle"));
        Assert.Contains(errors, m => m.Contains("'atack' is not one of its states"));
        Assert.Contains(errors, m => m.Contains("-1 is not a time in seconds"));
        Assert.Contains(errors, m => m.Contains("'Provok' is not an input"));
        Assert.DoesNotContain(errors, m => m.Contains("'Provoke' is not an input"));
    }

    // The engine's, in a game with no plugins: the input, the output, the part, the record type and the
    // system, and the part puts a machine that has not started on the entity.
    [Fact]
    public void StateMachinesAreTheEngines_InAGameWithNoPlugins()
    {
        using var bare = HeadlessApp.Bare().File("data/m.json", """
            [ { "type": "state_machine", "id": "m", "initial": "a", "states": { "a": {} } },
              { "type": "prefab", "id": "p", "parts": { "state_machine": { "machine": "m" } } } ]
            """, "test").Boot("bare");
        var engine = bare.Engine;
        Assert.True(engine.Inputs.Has(StateMachines.SetStateInput));
        Assert.Equal("sage.core", engine.Registrations.OwnerOf("entity input", StateMachines.SetStateInput));
        Assert.True(engine.Outputs.Has(StateMachines.OnStateChanged));
        Assert.True(engine.Prefabs.TryGet("state_machine", out _));
        Assert.Equal("sage.core", engine.Registrations.OwnerOf("record type", "state_machine"));
        var system = bare.World.Systems.Find("sage.logic.state_machines")!;
        Assert.Equal(Phase.EntityIO, system.Phase);
        Assert.Equal("sage.core", system.Owner);

        var e = bare.World.Spawn(new RecordId("test", "p"));
        Assert.Equal(new RecordId("test", "m"), bare.World.Get<StateMachine>(e).Machine);
        Assert.Null(StateMachines.StateOf(bare.World, e));
        bare.World.RunFixed(Dt);
        Assert.Equal("a", StateMachines.StateOf(bare.World, e));
    }

    // The helper another kind of machine (4d's animation graph) steps with: first match wins, `on` only
    // for its input, `after` and `when` both.
    [Fact]
    public void FirstTransitionTakesTheFirstThatMatches()
    {
        using var bare = HeadlessApp.Bare().Boot("bare");
        var world = bare.World;
        var context = new ConditionContext(world, default);
        var onInput = new StateTransition { To = "b", On = "Go" };
        var later = new StateTransition { To = "c", After = 1f };
        var soon = new StateTransition { To = "d" };
        var list = new List<StateTransition> { onInput, later, soon };

        Assert.Same(soon, StateMachines.FirstTransition(list, null, 0f, in context));
        Assert.Same(later, StateMachines.FirstTransition(list, null, 1f, in context));
        Assert.Same(onInput, StateMachines.FirstTransition(list, "go", 0f, in context));
        Assert.Null(StateMachines.FirstTransition(new List<StateTransition> { onInput }, "Stop", 5f, in context));
    }
}

// Zero allocation per tick with state machines changing state, hearing inputs and firing outputs.
[Collection(MeasurementsCollection.Name)]
public class StateMachineAllocationTests
{
    public StateMachineAllocationTests() { _ = TestEnv.UserRoot; }

    private static int _count;

    [Fact]
    public void StateMachinesAllocateNothingPerTick()
    {
        // Two states that hand over to each other for ever: a → b after a tenth of a second while a var
        // holds; b sends itself `Ping` on entering, and goes back to a when it arrives.
        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule())
            .File("data/pingpong.json", """
                [ { "type": "state_machine", "id": "pingpong", "initial": "a",
                    "states": {
                      "a": { "enter": [ { "add_var": "a" } ], "exit": [ { "add_var": "left_a" } ],
                             "transitions": [ { "to": "b", "after": 0.1, "when": { "var": "on", "eq": 1 } } ] },
                      "b": { "enter": [ { "fire": "!self", "input": "Ping", "delay": 0.05 } ],
                             "transitions": [ { "to": "a", "on": "Ping", "then": [ { "add_var": "pongs" } ] } ] } } },
                  { "type": "prefab", "id": "pingpong", "parts": { "state_machine": { "machine": "pingpong" } } } ]
                """, "test")
            .OnRegistered(a => a.Engine.Inputs.Register("Count", static (World w, in IOContext io) => _count++))
            .Boot("sm");
        var world = app.World;
        for (int i = 0; i < 8; i++)
        {
            var e = world.Spawn(new RecordId("test", "pingpong"));
            world.Add(e, new IOConnections
            {
                Wires = new[] { new Connection { Output = StateMachines.OnStateChanged, Target = "!self", Input = "Count" } },
            });
        }
        Vars.Of(world).Set("on", 1);

        for (int i = 0; i < 120; i++) { world.RunFixed(1f / 60f); Profiler.EndFrame(); }   // warm
        _count = 0;
        AllocationProbe.AssertNone(300, () => { world.RunFixed(1f / 60f); Profiler.EndFrame(); });
        Assert.True(_count > 100, $"the machines should have kept changing state ({_count})");
        Assert.True(Vars.ValueOf(world, "pongs") > 50);
    }
}
