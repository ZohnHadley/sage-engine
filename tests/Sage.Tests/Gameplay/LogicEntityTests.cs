#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Logic entities and bridge I/O (issue #91): inputs routed by component, relays, counters, comparisons,
// branches and remaps, conditional wires, same-tick relays, gameplay's bridge inputs and the outputs
// from its events.
public class LogicEntityTests
{
    public LogicEntityTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    // What arrived at a `Record` input: the tick, the parameter and the activator.
    private sealed class Heard
    {
        public readonly List<(long Tick, string Parameter, Entity Activator)> List = new();
        public IEnumerable<string> Parameters => List.Select(a => a.Parameter);
        public int Count(string parameter) => List.Count(a => a.Parameter == parameter);
    }

    private static HeadlessAppBuilder WithLog(HeadlessAppBuilder builder, Heard log) =>
        builder.OnRegistered(app => app.Engine.Inputs.Register("Record", (World world, in IOContext io) =>
            log.List.Add((world.Tick, io.Parameter, io.Activator))));

    private static void Tick(World world, int times = 1)
    {
        for (int i = 0; i < times; i++) world.RunFixed(Dt);
    }

    // The input arrives in the next tick's dispatch, and what it fires the tick after.
    private static void Fire(HeadlessApp app, string command)
    {
        Assert.True(app.Engine.CVars.Execute(command, ExecSource.Code), command);
        Tick(app.World, 2);
    }

    // Every logic entity from the engine's prefabs (or a game's prefab with the engine's part), placed by
    // a scene and wired to `log`.
    private const string Scene = """
    [
      { "type": "prefab", "id": "thing", "name": "thing" },
      { "type": "prefab", "id": "levers", "name": "levers", "parts": { "logic_counter": { "min": 0, "max": 3 } } },
      { "type": "prefab", "id": "guarded_relay", "name": "guarded relay",
        "parts": { "logic_relay": { "requires": { "var": "alarm", "eq": 0 }, "then": [ { "add_var": "relayed" } ] } } },
      { "type": "prefab", "id": "percent", "name": "percent",
        "parts": { "math_remap": { "inMin": 0, "inMax": 1, "outMin": 0, "outMax": 100 } } },
      { "type": "scene", "id": "logic",
        "place": [
          { "prefab": "thing", "at": [0, 0, 0], "name": "log" },
          { "prefab": "sage:logic_relay", "at": [0, 0, 0], "name": "relay",
            "outputs": [ { "output": "OnTrigger", "target": "log", "input": "Record" } ] },
          { "prefab": "guarded_relay", "at": [0, 0, 0], "name": "guarded",
            "outputs": [ { "output": "OnTrigger", "target": "log", "input": "Record", "parameter": "guarded" } ] },
          { "prefab": "levers", "at": [0, 0, 0], "name": "levers",
            "outputs": [
              { "output": "OnChanged", "target": "log", "input": "Record" },
              { "output": "OnHitMax", "target": "log", "input": "Record", "parameter": "max" },
              { "output": "OnHitMin", "target": "log", "input": "Record", "parameter": "min" },
              { "output": "OnGetValue", "target": "log", "input": "Record", "parameter": "" },
              { "output": "OnChanged", "target": "check", "input": "SetValueCompare" }
            ] },
          { "prefab": "sage:logic_compare", "at": [0, 0, 0], "name": "check",
            "outputs": [
              { "output": "OnEqual", "target": "log", "input": "Record", "parameter": "eq" },
              { "output": "OnNotEqual", "target": "log", "input": "Record", "parameter": "ne" },
              { "output": "OnLess", "target": "log", "input": "Record", "parameter": "less" },
              { "output": "OnGreater", "target": "log", "input": "Record", "parameter": "greater" }
            ] },
          { "prefab": "sage:logic_branch", "at": [0, 0, 0], "name": "switch",
            "outputs": [
              { "output": "OnTrue", "target": "log", "input": "Record", "parameter": "true" },
              { "output": "OnFalse", "target": "log", "input": "Record", "parameter": "false" }
            ] },
          { "prefab": "percent", "at": [0, 0, 0], "name": "percent",
            "outputs": [ { "output": "OnValue", "target": "log", "input": "Record" } ] },
          { "prefab": "thing", "at": [0, 0, 0], "name": "gate",
            "outputs": [ { "output": "OnUse", "target": "log", "input": "Record", "parameter": "through", "times": 1,
                           "requires": { "var": "gate_open", "eq": 1 } } ] }
        ] }
    ]
    """;

    private static HeadlessApp SceneApp(Heard log)
    {
        var app = WithLog(HeadlessApp.Bare().WithEngineContent().With(new PhysicsModule(), new EntityIOModule())
                .File("data/logic_test.json", Scene), log)
            .StartScene("sage:logic").Boot("logic");
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        Assert.Equal(0, app.Records.ErrorCount);
        Tick(app.World);
        return app;
    }

    // ---- routed inputs --------------------------------------------------------------------------------

    // `Toggle` is a mover's on a mover and a branch's on a branch; an entity with neither is refused by
    // name, unless a game registered a global `Toggle`, which then takes it — and only it.
    [Fact]
    public void AnInputNameIsRoutedToEachComponentThatTakesIt()
    {
        var toggled = new List<Entity>();
        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule(), new MoverModule())
            .OnRegistered(a => a.Engine.Inputs.Register("Toggle", (World w, in IOContext io) => toggled.Add(io.Self)))
            .Boot("io");
        var world = app.World;
        var engine = app.Engine;

        var door = world.Create(Transform.At(Vector3.Zero), "door");
        world.Add(door, new Mover { OpenOffset = new Vector3(0, 3, 0), Seconds = 1f });
        var branch = world.Create(Transform.At(Vector3.Zero), "branch");
        world.Add(branch, new LogicBranch());
        var both = world.Create(Transform.At(Vector3.Zero), "both");     // a branch that is also a door
        world.Add(both, new Mover { OpenOffset = new Vector3(0, 3, 0), Seconds = 1f });
        world.Add(both, new LogicBranch());
        var lamp = world.Create(Transform.At(Vector3.Zero), "lamp");

        var io = world.IO();
        foreach (var e in new[] { door, branch, both, lamp }) io.FireInput(e, "Toggle");
        Tick(world);

        Assert.Equal(1, world.Get<Mover>(door).Direction);
        Assert.True(world.Get<LogicBranch>(branch).Value);
        Assert.Equal(1, world.Get<Mover>(both).Direction);                 // every component that takes it
        Assert.True(world.Get<LogicBranch>(both).Value);
        Assert.Equal(new[] { lamp }, toggled);                             // the global one, only where nobody else did

        Assert.Equal(new[] { "sage:logic_branch", "sage:logic_relay", "sage:mover" }, engine.Inputs.ComponentsTaking("Toggle").OrderBy(c => c));
        Assert.True(engine.Inputs.Takes(lamp, "Toggle"));
        Assert.False(engine.Inputs.Takes(lamp, "Trigger"));
        Assert.True(engine.Inputs.Takes(world.Create(Transform.Identity, "r"), "Kill"));
        Assert.Equal("sage.core", engine.Registrations.OwnerOf("entity input", "Trigger@sage:logic_relay"));
        Assert.Equal("sage.gameplay.movers", engine.Registrations.OwnerOf("entity input", "Toggle@sage:mover"));
    }

    // With no global handler, an entity with no component that takes the input is refused, with the
    // components that would have: the typo-class mistake of wiring `Trigger` at a door.
    [Fact]
    public void ARoutedInputAtAnEntityWithoutItsComponentIsRefusedByName()
    {
        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).Boot("io");
        var world = app.World;
        string name = TestEnv.Unique("plain");
        var plain = world.Create(Transform.At(Vector3.Zero), name);
        using var log = new CaptureSink();
        world.IO().FireInput(plain, "Trigger");
        Tick(world);
        Assert.Contains(log.Entries, e => e.Message.Contains(name) && e.Message.Contains("sage:logic_relay"));
    }

    // With state machines (#92): SetState is routed to the machine, and a machine's `on` hears an input
    // after the handlers of the entity's other components — a relay that is also a machine does both.
    [Fact]
    public void AStateMachineHearsAnInputBesideTheComponentsThatTakeIt()
    {
        var log = new Heard();
        using var app = WithLog(HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).File("data/switch.json", """
            [ { "type": "state_machine", "id": "lamp", "initial": "off",
                "states": { "off": { "transitions": [ { "to": "on", "on": "Trigger" } ] }, "on": {} } },
              { "type": "prefab", "id": "lamp_relay", "name": "lamp relay",
                "parts": { "logic_relay": { }, "state_machine": { "machine": "lamp" } } } ]
            """), log).Boot("io");
        var world = app.World;
        var sink = world.Create(Transform.At(Vector3.Zero), "sink");
        sink.Name = "sink";
        var lamp = world.Spawn(new RecordId("sage", "lamp_relay"));
        world.Add(lamp, new IOConnections { Wires = new[] { new Connection { Output = "OnTrigger", Target = "sink", Input = "Record", Parameter = "relayed" } } });
        Tick(world);

        world.IO().FireInput(lamp, "Trigger");
        Tick(world, 3);
        Assert.Equal(new[] { "relayed" }, log.Parameters);
        Assert.Equal("on", world.Get<StateMachine>(lamp).State);

        Assert.Equal(new[] { "sage:state_machine" }, app.Engine.Inputs.ComponentsTaking("SetState"));
        string name = TestEnv.Unique("no machine");
        using var capture = new CaptureSink();
        world.IO().FireInput(world.Create(Transform.At(Vector3.Zero), name), "SetState", "on");
        Tick(world);
        Assert.Contains(capture.Entries, e => e.Message.Contains(name) && e.Message.Contains("sage:state_machine"));
    }

    // ---- the logic entities, driven with ent_fire ---------------------------------------------------

    [Fact]
    public void ARelayTriggersRunsItsActionsOnlyWhenItsConditionHolds_AndCanBeDisabled()
    {
        var log = new Heard();
        using var app = SceneApp(log);
        var world = app.World;

        Fire(app, "ent_fire relay Trigger hello");
        Assert.Equal(new[] { "hello" }, log.Parameters);                  // Trigger's parameter handed on

        Fire(app, "ent_fire guarded Trigger");
        Assert.Equal(1, log.Count("guarded"));
        Assert.Equal(1, Vars.ValueOf(world, "relayed"));                   // its `then` ran

        Vars.Of(world).Set("alarm", 1);                                    // its `requires` fails now
        Fire(app, "ent_fire guarded Trigger");
        Assert.Equal(1, log.Count("guarded"));
        Assert.Equal(1, Vars.ValueOf(world, "relayed"));

        Vars.Of(world).Set("alarm", 0);
        Fire(app, "ent_fire guarded Disable");
        Fire(app, "ent_fire guarded Trigger");
        Assert.Equal(1, log.Count("guarded"));
        Fire(app, "ent_fire guarded Enable");
        Fire(app, "ent_fire guarded Trigger");
        Assert.Equal(2, log.Count("guarded"));
        Assert.Equal(2, Vars.ValueOf(world, "relayed"));
    }

    // Three levers: OnChanged hands each count on (to the log, and to a comparison), OnHitMax fires at 3
    // and the count stays there; Subtract to 0 is OnHitMin; GetValue answers.
    [Fact]
    public void ACounterCountsBetweenItsLimitsAndHandsItsValueOn()
    {
        var log = new Heard();
        using var app = SceneApp(log);
        var world = app.World;
        Fire(app, "ent_fire check SetCompareValue 3");

        Fire(app, "ent_fire levers Add");
        Tick(world);                                                       // the comparison hears it a tick later
        Fire(app, "ent_fire levers Add");
        Tick(world);
        Assert.Equal(new[] { "1", "ne", "less", "2", "ne", "less" }, log.Parameters);

        log.List.Clear();
        Fire(app, "ent_fire levers Add 5");                                // clamped to max
        Tick(world);
        Assert.Equal(3f, world.Get<LogicCounter>(world.FindByName("levers")).Value);
        Assert.Equal(new[] { "3", "max", "eq" }, log.Parameters);

        log.List.Clear();
        Fire(app, "ent_fire levers Add");                                  // at max: no change, nothing fires
        Fire(app, "ent_fire levers GetValue");
        Assert.Equal(new[] { "3" }, log.Parameters);

        log.List.Clear();
        Fire(app, "ent_fire levers SetValue 0");
        Assert.Contains("min", log.Parameters);
        Fire(app, "ent_fire levers Disable");
        Fire(app, "ent_fire levers Add");
        Assert.Equal(0f, world.Get<LogicCounter>(world.FindByName("levers")).Value);
        Fire(app, "ent_fire levers Enable");
        Fire(app, "ent_fire levers Add 2");
        Fire(app, "ent_fire levers Reset");
        Assert.Equal(0f, world.Get<LogicCounter>(world.FindByName("levers")).Value);
    }

    [Fact]
    public void ACompareSaysWhichIsBigger()
    {
        var log = new Heard();
        using var app = SceneApp(log);
        Fire(app, "ent_fire check SetCompareValue 3");
        Fire(app, "ent_fire check SetValue 5");                            // set, not compared
        Assert.Empty(log.List);
        Fire(app, "ent_fire check Compare");
        Assert.Equal(new[] { "ne", "greater" }, log.Parameters);
        log.List.Clear();
        Fire(app, "ent_fire check SetValueCompare 2.5");
        Assert.Equal(new[] { "ne", "less" }, log.Parameters);
        log.List.Clear();
        Fire(app, "ent_fire check SetValueCompare 3");
        Assert.Equal(new[] { "eq" }, log.Parameters);
    }

    [Fact]
    public void ABranchRemembersAndTells()
    {
        var log = new Heard();
        using var app = SceneApp(log);
        Fire(app, "ent_fire switch Test");
        Fire(app, "ent_fire switch SetValue true");
        Fire(app, "ent_fire switch Test");
        Fire(app, "ent_fire switch Toggle");
        Fire(app, "ent_fire switch Test");
        Fire(app, "ent_fire switch ToggleTest");
        Fire(app, "ent_fire switch SetValueTest 0");
        Assert.Equal(new[] { "false", "true", "false", "true", "false" }, log.Parameters);
    }

    [Fact]
    public void ARemapMapsOneRangeToAnother()
    {
        var log = new Heard();
        using var app = SceneApp(log);
        Fire(app, "ent_fire percent SetValue 0.25");
        Fire(app, "ent_fire percent SetValue 7");                          // clamped
        Assert.Equal(new[] { "25", "100" }, log.Parameters);

        var remap = new MathRemap { InMin = 0, InMax = 10, OutMin = 100, OutMax = 200, Ease = Ease.QuadIn };
        Assert.Equal(125f, LogicEntities.Remap(in remap, 5f), 3);
        Assert.Equal(300f, LogicEntities.Remap(in remap, 20f), 3);        // unclamped: the line carries on
    }

    // The acceptance test: a counter at 2 of 3 is at 2 of 3 after a load, and one more lever hits max.
    [Fact]
    public void ACountersStateSurvivesASave()
    {
        var log = new Heard();
        using var app = SceneApp(log);
        var world = app.World;
        Fire(app, "ent_fire levers Add");
        Fire(app, "ent_fire levers Add");
        Fire(app, "ent_fire switch SetValue 1");
        Fire(app, "ent_fire guarded Disable");
        Assert.True(app.Engine.Saves.Save("levers"));

        Fire(app, "ent_fire levers Add");
        Fire(app, "ent_fire switch Toggle");
        Fire(app, "ent_fire guarded Enable");
        Assert.Equal(1, log.Count("max"));

        Assert.True(app.Engine.Saves.Load("levers"));
        var levers = world.FindByName("levers");
        Assert.Equal(2f, world.Get<LogicCounter>(levers).Value);
        Assert.Equal(3f, world.Get<LogicCounter>(levers).Max);
        Assert.True(world.Get<LogicBranch>(world.FindByName("switch")).Value);
        Assert.True(world.Get<LogicRelay>(world.FindByName("guarded")).Disabled);
        // The relay's content came back with its part, though it is never saved.
        Assert.NotNull(world.Get<LogicRelayScript>(world.FindByName("guarded")).Then);

        Fire(app, "ent_fire levers Add");
        Assert.Equal(2, log.Count("max"));
    }

    // ---- conditional wires ------------------------------------------------------------------------------

    // A wire with `requires` sends nothing while it fails, and that does not spend its `times: 1`.
    [Fact]
    public void AConditionalWireFiresOnlyWhenItsConditionHolds_AndAFailureIsNotCounted()
    {
        var log = new Heard();
        using var app = SceneApp(log);
        var world = app.World;
        var gate = world.FindByName("gate");

        world.FireOutput(gate, "OnUse");
        world.FireOutput(gate, "OnUse");
        Tick(world, 2);
        Assert.Empty(log.List);

        Vars.Of(world).Set("gate_open", 1);
        world.FireOutput(gate, "OnUse");
        Tick(world);
        world.FireOutput(gate, "OnUse");                                   // spent now
        Tick(world);
        Assert.Equal(new[] { "through" }, log.Parameters);
    }

    // ---- same-tick relays -------------------------------------------------------------------------------

    private static Entity Relay(World world, string name, bool sameTick, string next, string input)
    {
        var relay = world.Create(Transform.At(Vector3.Zero), name);
        relay.Name = name;
        world.Add(relay, new LogicRelay { SameTick = sameTick });
        world.Add(relay, new IOConnections { Wires = new[] { new Connection { Output = "OnTrigger", Target = next, Input = input, Parameter = name } } });
        return relay;
    }

    // A chain of three relays takes a tick a hop; three same-tick relays deliver on the tick the first is
    // triggered. A wire with a delay still waits.
    [Fact]
    public void SameTickRelaysRunAChainInOneTick()
    {
        var log = new Heard();
        using var app = WithLog(HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()), log).Boot("io");
        var world = app.World;
        var sink = world.Create(Transform.At(Vector3.Zero), "sink");
        sink.Name = "sink";

        var slow = Relay(world, "slow1", false, "slow2", "Trigger");
        Relay(world, "slow2", false, "slow3", "Trigger");
        Relay(world, "slow3", false, "sink", "Record");
        var fast = Relay(world, "fast1", true, "fast2", "Trigger");
        Relay(world, "fast2", true, "fast3", "Trigger");
        Relay(world, "fast3", true, "sink", "Record");

        world.IO().FireInput(slow, "Trigger");
        world.IO().FireInput(fast, "Trigger");
        Tick(world);
        long first = world.Tick;
        Tick(world, 5);
        Assert.Equal(first, log.List.Single(a => a.Parameter == "fast3").Tick);
        Assert.Equal(first + 3, log.List.Single(a => a.Parameter == "slow3").Tick);   // a tick a hop
    }

    // A same-tick relay wired to itself is stopped by io_maxdispatch with the warning, not a hang.
    [Fact]
    public void ASameTickRelayWiredToItselfStopsAtTheBudget()
    {
        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).Boot("io");
        var world = app.World;
        var io = world.IO();
        io.Budget = 50;
        var loop = Relay(world, "loop", true, "!self", "Trigger");
        using var log = new CaptureSink();
        io.FireInput(loop, "Trigger");
        Tick(world);
        Assert.Equal(50, io.DispatchedLastTick);
        Assert.Contains(log.Entries, e => e.Message.Contains("in one tick"));
        Tick(world, 3);                                                    // and the world carries on
    }

    [Fact]
    public void TheLogicEntitiesAreTheEngines_WithPrefabs()
    {
        using var bare = HeadlessApp.Bare().WithEngineContent().Boot("bare");
        var engine = bare.Engine;
        foreach (var input in new[] { "Trigger", "Add", "Subtract", "SetValue", "Reset", "GetValue", "Enable", "Disable",
                                      "SetValueCompare", "SetCompareValue", "Compare", "SetValueTest", "Toggle", "ToggleTest", "Test" })
            Assert.True(engine.Inputs.Has(input), input);
        foreach (var output in new[] { "OnTrigger", "OnChanged", "OnHitMax", "OnHitMin", "OnGetValue", "OnEqual", "OnNotEqual",
                                       "OnLess", "OnGreater", "OnTrue", "OnFalse", "OnValue" })
            Assert.True(engine.Outputs.Has(output), output);
        foreach (var (prefab, has) in new (string, System.Func<World, Entity, bool>)[]
                 {
                     ("logic_relay", (w, e) => w.Has<LogicRelay>(e)), ("logic_counter", (w, e) => w.Has<LogicCounter>(e)),
                     ("logic_compare", (w, e) => w.Has<LogicCompare>(e)), ("logic_branch", (w, e) => w.Has<LogicBranch>(e)),
                     ("math_remap", (w, e) => w.Has<MathRemap>(e)),
                 })
        {
            Assert.True(engine.Prefabs.TryGet(prefab, out _), prefab);
            var entity = bare.World.Spawn(new RecordId("sage", prefab));
            Assert.True(has(bare.World, entity), prefab);
        }
        Assert.Equal(0, bare.Records.ErrorCount);
    }
}

// Gameplay's bridge inputs and the outputs from its events (issue #91).
public class BridgeIOTests
{
    public BridgeIOTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "invulnerable": "state.invulnerable", "damageType": "physical" },
         { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "attribute", "id": "armor",  "start": 0,   "min": 0, "max": 95 },
         { "type": "tag", "id": "state.dead" },
         { "type": "tag", "id": "state.invulnerable" },
         { "type": "effect", "id": "damage", "duration": "Instant", "blockTags": ["state.invulnerable"],
           "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
         { "type": "effect", "id": "blessed", "duration": "Infinite",
           "modifiers": [ { "attribute": "armor", "op": "Add", "value": 10 } ] },
         { "type": "damage_type", "id": "physical", "resist": "armor", "effect": "damage" },
         { "type": "item", "id": "coin", "label": "coin", "weight": 0, "maxStack": 100 },
         { "type": "faction", "id": "guards", "standing": 0 },
         { "type": "quest", "id": "cull", "stages": [ { "id": "a" }, { "id": "b" }, { "id": "c" } ] },
         { "type": "dialogue", "id": "elder", "nodes": [ { "id": "hello", "text": "Well met.", "options": [ { "text": "Bye.", "end": true } ] } ] },
         { "type": "prefab", "id": "watcher", "name": "watcher", "parts": { "quest_watch": "cull" } }]
        """;

    private sealed class Heard
    {
        public readonly List<(string Parameter, Entity Activator)> List = new();
        public IEnumerable<string> Parameters => List.Select(a => a.Parameter);
    }

    private static HeadlessApp NewApp(Heard log)
    {
        var app = HeadlessApp.Gameplay().File("data/bridges.json", Records)
            .OnRegistered(a => a.Engine.Inputs.Register("Record", (World world, in IOContext io) => log.List.Add((io.Parameter, io.Activator))))
            .Boot("bridges");
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        world.Add(ground, Collider.Box(new Vector3(100, 1, 100)));
        return app;
    }

    private static Entity Named(World world, string name, Vector3 at = default)
    {
        var entity = world.Create(Transform.At(at), name);
        entity.Name = name;
        return entity;
    }

    private static Entity Player(World world)
    {
        var player = Named(world, "hero");
        world.AddCharacter(player, world.Resources.Get<IPhysicsWorld>().Layers.Player);
        world.AddInventory(player, 50f);
        world.AddAttributes(player);
        player.AddTag<PlayerControlled>();
        return player;
    }

    private static void Wire(World world, Entity entity, params (string Output, string Parameter)[] outputs) =>
        world.Add(entity, new IOConnections
        {
            Wires = outputs.Select(o => new Connection { Output = o.Output, Target = "log", Input = "Record", Parameter = o.Parameter }).ToArray(),
        });

    private static void Tick(World world, int times = 1)
    {
        for (int i = 0; i < times; i++) world.RunFixed(1f / 60f);
    }

    private static void Fire(HeadlessApp app, string command, int ticks = 2)
    {
        Assert.True(app.Engine.CVars.Execute(command, ExecSource.Code), command);
        Tick(app.World, ticks);
    }

    // SetStage moves a quest (starting it first when it is not on), and a quest_watch on the quest says
    // so with OnStageChanged; a bare stage means the watch's quest.
    [Fact]
    public void ABridgeInputChangesAQuestStage_AndAQuestWatchSaysSo()
    {
        var log = new Heard();
        using var app = NewApp(log);
        var world = app.World;
        var quest = new RecordId("sage", "cull");
        Named(world, "log");
        var player = Player(world);
        var watcher = world.Spawn(new RecordId("sage", "watcher"));
        watcher.Name = "watcher";
        Wire(world, watcher, ("OnStageChanged", ""), ("OnQuestFinished", "done"));
        Tick(world);

        Fire(app, "ent_fire watcher SetStage \"cull b\"");
        Assert.Equal("b", Quests.StageOf(world, quest));
        Assert.Equal(new[] { "a", "b" }, log.Parameters);                 // started, then moved
        Assert.All(log.List, e => Assert.Equal(player, e.Activator));

        Fire(app, "ent_fire watcher SetStage c");
        Assert.Equal("c", Quests.StageOf(world, quest));
        Quests.Finish(world, quest);
        Tick(world, 2);
        Assert.Equal(new[] { "a", "b", "c", "done" }, log.Parameters);
    }

    [Fact]
    public void BridgeInputsGiveItemsApplyEffectsSetFactionsAndStartConversations()
    {
        var log = new Heard();
        using var app = NewApp(log);
        var world = app.World;
        var player = Player(world);
        var elder = Named(world, "elder", new Vector3(2, 0, 0));
        world.Add(elder, new Dialogue { Record = new RecordId("sage", "elder") });
        Tick(world);

        Fire(app, "ent_fire hero GiveItem \"coin 3\"");
        Assert.Equal(3, world.CountOf(player, new RecordId("sage", "coin")));
        Fire(app, "ent_fire hero ApplyEffect blessed");
        Assert.True(Effects.IsActive(world, player, new RecordId("sage", "blessed")));
        Fire(app, "ent_fire hero SetFaction guards");
        Assert.Equal(new RecordId("sage", "guards"), world.Get<Faction>(player).Id);
        Fire(app, "ent_fire hero SetFaction");
        Assert.False(world.Has<Faction>(player));

        Fire(app, "ent_fire elder StartDialogue");
        var conversation = world.Resources.Get<Conversation>();
        Assert.True(conversation.Running);
        Assert.Equal(elder, conversation.Speaker);
        Assert.Equal(player, conversation.Listener);                        // nobody activated it: the player
    }

    // OnDamaged with the damage done and OnDeath once, with the attacker as the activator.
    [Fact]
    public void DeathAndDamageAreOutputs()
    {
        var log = new Heard();
        using var app = NewApp(log);
        var world = app.World;
        Named(world, "log");
        var player = Player(world);
        var wolf = Named(world, "wolf", new Vector3(0, 0, -4));
        world.AddCharacter(wolf, world.Resources.Get<IPhysicsWorld>().Layers.Enemy);
        world.AddAttributes(wolf);
        Wire(world, wolf, ("OnDamaged", ""), ("OnDeath", "dead"));
        Tick(world);

        Combat.ApplyDamage(world, new DamageInfo(player, wolf, default, 30f, Vector3.Zero, Vector3.UnitZ));
        Tick(world, 2);
        Assert.Equal(new[] { "30" }, log.Parameters);
        Combat.ApplyDamage(world, new DamageInfo(player, wolf, default, 500f, Vector3.Zero, Vector3.UnitZ));
        Tick(world, 3);
        Assert.Equal(1, log.List.Count(e => e.Parameter == "dead"));
        Assert.Equal(player, log.List.Single(e => e.Parameter == "dead").Activator);
        Assert.Equal(3, log.List.Count);
    }

    // A pickup's wires hear OnPickedUp as it is taken, with who took it.
    [Fact]
    public void APickupSaysItWasPickedUp()
    {
        var log = new Heard();
        using var app = NewApp(log);
        var world = app.World;
        Named(world, "log");
        var player = Player(world);
        world.Get<PawnIntent>(player).Yaw = SageMath.YawTo(Vector3.Zero, new Vector3(0, 0, -2));
        var coin = world.SpawnPickup(new RecordId("sage", "coin"), 1, new Vector3(0, 0, -1.4f));
        Wire(world, coin, ("OnPickedUp", "taken"));
        Tick(world, 2);

        world.Get<PawnIntent>(player).Pressed = new ActionMask().With(app.Engine.Actions.Get("Use"));
        Tick(world);
        world.Get<PawnIntent>(player).Pressed = default;
        Tick(world, 2);

        Assert.Equal(1, world.CountOf(player, new RecordId("sage", "coin")));
        var taken = Assert.Single(log.List);
        Assert.Equal("taken", taken.Parameter);
        Assert.Equal(player, taken.Activator);
    }
}

// Zero allocation per tick with the logic entities, routed inputs, values and a conditional wire running
// (issue #91's acceptance).
[Collection(MeasurementsCollection.Name)]
public class LogicEntityAllocationTests
{
    public LogicEntityAllocationTests() { _ = TestEnv.UserRoot; }

    private static int _count;

    private static Entity Named(World world, string name)
    {
        var entity = world.Create(Transform.At(Vector3.Zero), name);
        entity.Name = name;
        return entity;
    }

    [Fact]
    public void LogicEntitiesAllocateNothingPerTick()
    {
        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).OnRegistered(a =>
            a.Engine.Inputs.Register("Count", static (World w, in IOContext io) => _count++)).Boot("io");
        var world = app.World;
        Vars.Of(world).Set("on", 1);

        // timer → counter.Add; counter.OnChanged(value) → compare.SetValueCompare; compare.OnNotEqual →
        // branch.ToggleTest; branch.OnTrue → a same-tick relay → remap.SetValue → Count; the counter
        // cycles 0..5 (OnHitMax → Reset), so every value it hands on has been written before.
        var timer = Named(world, "timer");
        world.Add(timer, new LogicTimer { Interval = 0.05f, Repeat = true });
        world.Add(timer, new IOConnections { Wires = new[] { new Connection { Output = "OnTimer", Target = "counter", Input = "Add" } } });
        var counter = Named(world, "counter");
        world.Add(counter, new LogicCounter { Min = 0, Max = 5 });
        world.Add(counter, new IOConnections
        {
            Wires = new[]
            {
                new Connection { Output = "OnChanged", Target = "compare", Input = "SetValueCompare" },
                new Connection { Output = "OnHitMax", Target = "!self", Input = "Reset" },
            },
        });
        var compare = Named(world, "compare");
        world.Add(compare, new LogicCompare { CompareValue = 3 });
        world.Add(compare, new IOConnections { Wires = new[] { new Connection { Output = "OnNotEqual", Target = "branch", Input = "ToggleTest" } } });
        var branch = Named(world, "branch");
        world.Add(branch, new LogicBranch());
        world.Add(branch, new IOConnections { Wires = new[] { new Connection { Output = "OnTrue", Target = "relay", Input = "Trigger" } } });
        var relay = Named(world, "relay");
        world.Add(relay, new LogicRelay { SameTick = true });
        world.Add(relay, new IOConnections
        {
            Wires = new[]
            {
                new Connection { Output = "OnTrigger", Target = "remap", Input = "SetValue", Parameter = "0.5",
                                 Requires = Conditions(app, """{ "var": "on", "eq": 1 }""") },
            },
        });
        var remap = Named(world, "remap");
        world.Add(remap, new MathRemap { InMin = 0, InMax = 1, OutMin = 0, OutMax = 10, Clamp = true });
        world.Add(remap, new IOConnections { Wires = new[] { new Connection { Output = "OnValue", Target = "!self", Input = "Count" } } });

        world.IO().FireInput(timer, "TimerStart");

        for (int i = 0; i < 180; i++) { world.RunFixed(1f / 60f); Profiler.EndFrame(); }   // warm
        _count = 0;
        AllocationProbe.AssertNone(300, () => { world.RunFixed(1f / 60f); Profiler.EndFrame(); });
        Assert.True(_count > 20, $"the chain should have reached the remap often ({_count})");
    }

    // A condition read from JSON as content reads it (the `var` condition is internal to the engine).
    private static ICondition Conditions(HeadlessApp app, string json) =>
        System.Text.Json.JsonSerializer.Deserialize<ICondition>(json, app.Records.Json)!;
}
