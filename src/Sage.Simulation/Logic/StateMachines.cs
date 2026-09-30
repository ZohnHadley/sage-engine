#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Sage.Simulation;

// State machines as data (issue #92, decision D4: a record and a small component). A guard that idles,
// grows alert when the alarm var is set, attacks after three seconds of it and calms down when told to;
// a door that is locked, closed, open; a level sequence with steps. Level logic whose *state* matters,
// written in data like the rest of entity I/O:
//
//   { "type": "state_machine", "id": "guard", "initial": "idle",
//     "states": {
//       "idle":   { "enter": [ { "set_var": "guard_mood", "value": 0 } ],
//                   "transitions": [ { "to": "alert", "when": { "var": "alarm", "eq": 1 } } ] },
//       "alert":  { "transitions": [ { "to": "attack", "after": 3 }, { "to": "idle", "on": "Calm" } ] },
//       "attack": { "enter": [ { "fire": "!self", "input": "Say", "parameter": "Halt!" } ],
//                   "transitions": [ { "to": "idle", "on": "Calm" } ] } } }
//
//   component  "sage:state_machine": { "machine": "guard", "state": "alert", "timeInState": 1.5 }
//   part       "state_machine": { "machine": "guard" }
//
//   input    SetState <state>        go to that state now (exit, enter, OnStateChanged); unknown: a warning
//            <any `on` name>         a transition's `on`: any input delivered to the entity, registered or not
//   output   OnStateChanged          the state changed; a wire with no parameter of its own is handed the
//                                    new state's name (EntityIO.Fire's value)
//
// **A transition** goes `to` a state and fires when every trigger it names holds: `on` — that input
// arrived at the entity; `when` — the condition (#89's language) holds; `after` — the machine has been
// in the state that long (seconds). A transition with `on` is only ever tried when an input arrives;
// the others are tried once a tick. The state's own transitions are tried first, in order, then the
// machine's top-level `transitions` (from any state, never to the state it is in); **the first that
// matches wins**, and a machine changes state at most once a tick (and once per input), so a chain of
// transitions with nothing in the way takes a tick a link, like wires.
//
// **A change** runs the old state's `exit`, the transition's `then`, the new state's `enter` and fires
// OnStateChanged. Actions are asked with the machine's activator (who sent the input that moved it, when
// it is still there) as the subject and the machine as the other, so `fire` at `!self` reaches the
// machine and `!activator` whoever set it off; `when` is asked the same way. A machine that has never run
// starts in `initial` on its first tick, running its `enter` without an OnStateChanged.
//
// **Saved by name:** the component holds the state's name and the seconds spent in it, so a save written
// before a state was renamed or removed still loads — and a state that is not in the machine any more
// (a save or a hot reload of the record) sends the machine to `initial`, with a warning.
//
// **Timing** matches timers': the tick runs in the EntityIO phase before the dispatch, so an `after: N`
// entered on tick D fires on the tick a wire sent on tick D with a delay of N arrives, and OnStateChanged's
// wires with no delay arrive the same tick. **Allocation-free** per tick (compiled once per record load).
//
// **For 4d's animation state machine:** the shape is generic on purpose. A state is actions, `tags` (free
// words for whatever reads the machine: an animation layer, a HUD) and transitions; a transition
// (`StateTransition`) and `StateMachines.FirstTransition` know nothing of guards or doors, so an
// `anim_graph` record whose states carry a clip and a blend reuses both, with animation events arriving
// as `on` names.
//
// Owned by the engine (`sage.core`), like timers: a data-only game has state machines whatever plugins it
// lists; OnStateChanged reaches wires when the entity I/O plugin is on, as every output does.
[Record("state_machine", Plugin = RegistrationOwners.Core)]
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // state machines (#92): may change before 1.0
public sealed class StateMachineRecord
{
    [Property(Tooltip = "The state a machine starts in, and goes back to when its saved state is not in the machine any more")]
    public string Initial = "";
    [Property(Tooltip = "The states, by name")]
    public Dictionary<string, MachineState> States = new();
    [Property(Tooltip = "Transitions from any state, tried after the state's own; never to the state it is in")]
    public List<StateTransition> Transitions = new();

    // The machine as the tick reads it: states by index, transitions' targets resolved. Rebuilt when a
    // reload hands the record new states (RecordStore keeps the instance and copies the fields in).
    private Compiled? _compiled;

    internal Compiled Compile()
    {
        var c = _compiled;
        if (c != null && ReferenceEquals(c.States, States) && ReferenceEquals(c.Any, Transitions) && c.InitialName == Initial)
            return c;
        return _compiled = new Compiled(this);
    }

    internal sealed class Compiled
    {
        private static int _versions;

        public readonly Dictionary<string, MachineState> States;
        public readonly List<StateTransition> Any;
        public readonly string InitialName;
        public readonly int Version = Interlocked.Increment(ref _versions);   // never 0: a component's unset one

        public readonly string[] Names;
        public readonly MachineState[] ByIndex;
        public readonly int Initial;                              // -1: no such state (a load error)
        private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<StateTransition, int> _targets = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<string> _heard = new(StringComparer.OrdinalIgnoreCase);

        public Compiled(StateMachineRecord record)
        {
            States = record.States ?? new Dictionary<string, MachineState>();
            Any = record.Transitions ?? new List<StateTransition>();
            InitialName = record.Initial ?? "";
            Names = new string[States.Count];
            ByIndex = new MachineState[States.Count];
            int i = 0;
            foreach (var (name, state) in States)
            {
                Names[i] = name;
                ByIndex[i] = state ?? new MachineState();
                _index.TryAdd(name, i);
                i++;
            }
            Initial = IndexOf(InitialName);
            foreach (var state in ByIndex) Add(state.Transitions);
            Add(Any);
        }

        private void Add(List<StateTransition>? transitions)
        {
            if (transitions == null) return;
            foreach (var t in transitions)
            {
                if (t == null) continue;
                _targets[t] = IndexOf(t.To);
                if (!string.IsNullOrEmpty(t.On)) _heard.Add(t.On);
            }
        }

        public int Count => ByIndex.Length;
        public int IndexOf(string? name) => name != null && _index.TryGetValue(name, out int i) ? i : -1;
        public int Target(StateTransition t) => _targets.TryGetValue(t, out int i) ? i : -1;
        public bool Hears(string input) => _heard.Contains(input);
    }
}

// One state: what entering and leaving it does, where it can go, and words for whatever reads the machine.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // state machines (#92): may change before 1.0
public sealed class MachineState
{
    [Property(Tooltip = "What entering it does: the activator is the subject, the machine the other")]
    public List<IAction> Enter = new();
    [Property(Tooltip = "What leaving it does, before the transition's then and the next state's enter")]
    public List<IAction> Exit = new();
    [Property(Tooltip = "Where it can go, tried in order: the first that matches wins")]
    public List<StateTransition> Transitions = new();
    [Property(Tooltip = "Free words for whatever reads the machine (StateMachines.HasTag): a HUD, an animation layer")]
    public List<string> Tags = new();
}

// A way out of a state: `to` it, when every trigger it names holds — `on` an input, `when` a condition,
// `after` seconds in the state. None of them: at once (next tick).
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // state machines (#92): may change before 1.0
public sealed class StateTransition
{
    [Property(Tooltip = "The state it goes to")]
    public string To = "";
    [Property(Tooltip = "An input's name: only when that input arrives at the entity (any name; registered ones too)")]
    public string On = "";
    [Property(Tooltip = "Only while this holds, asked about the activator with the machine as the other")]
    public ICondition? When;
    [Property(Min = 0, Unit = "s", Tooltip = "Only once the machine has been in the state this long; 0 = no wait")]
    public float After;
    [Property(Tooltip = "What taking it does, between the old state's exit and the new one's enter")]
    public List<IAction> Then = new();
}

// The machine on an entity. Saved: `"sage:state_machine": { "Machine": "ns:guard", "State": "alert", "TimeInState": 1.5 }`.
[Component("sage:state_machine")]
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // state machines (#92): may change before 1.0
public struct StateMachine : IComponent
{
    [RecordRef("state_machine"), Property(Tooltip = "The state_machine record it runs")]
    public RecordId Machine;
    [Property(Tooltip = "The state it is in, by name; empty until its first tick puts it in the initial state")]
    public string? State;
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds it has been in that state")]
    public float TimeInState;

    // Who sent the input that last moved it: the subject of its actions and conditions. A handle,
    // meaningless in another session.
    [Transient] public Entity Activator;

    // The state's index in the compiled machine it was resolved against (its Version); not saved, found
    // again by name.
    internal int Index;
    internal int Version;
}

// "state_machine": { "machine": "guard" }
[PrefabPart("state_machine", Plugin = RegistrationOwners.Core)]
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // state machines (#92): may change before 1.0
public sealed class StateMachinePart : IPrefabPart
{
    [RecordRef("state_machine"), Property(Tooltip = "The state_machine record it runs; it starts in that machine's initial state")]
    public RecordId Machine;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Machine.IsEmpty)
        {
            ctx.Warn("a state_machine part needs a \"machine\" (a state_machine record)");
            return;
        }
        ctx.World.Add(ctx.Entity, new StateMachine { Machine = Machine, State = "" });
    }
}

// The input, the output, the checks and the stepping. Registered by the engine (Engine's constructor, sage.core).
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // state machines (#92): may change before 1.0
public static class StateMachines
{
    public const string SetStateInput = "SetState";
    public const string OnStateChanged = "OnStateChanged";

    internal static void Register(Engine engine)
    {
        var records = engine.Records;
        // Routed to the machine (issue #91): an entity without one is refused naming sage:state_machine.
        engine.Inputs.Register<StateMachine>(SetStateInput, static (World world, in IOContext io) => SetState(world, io.Self, io.Parameter, io.Activator));
        engine.Outputs.Declare(OnStateChanged, "This state machine changed state (sage:state_machine); a wire with no parameter is handed the new state's name.");

        // A wire may send any name a machine listens for (checked at load against the content being
        // loaded, whichever order the record types are checked in), and each input that arrives is
        // offered to the machine on the entity it arrives at.
        engine.Inputs.Heard = name => Hears(records, name);
        engine.Inputs.Listener = Hear;
        records.AddCheck<StateMachineRecord>(Check);
    }

    // The state the entity's machine is in, or null (no machine, not started yet).
    public static string? StateOf(World world, Entity entity) =>
        world.IsAlive(entity) && world.TryGet<StateMachine>(entity, out var m) && !string.IsNullOrEmpty(m.State) ? m.State : null;

    // Whether the state the entity's machine is in carries `tag`.
    public static bool HasTag(World world, Entity entity, string tag)
    {
        if (!world.IsAlive(entity) || !world.TryGet<StateMachine>(entity, out var m) || string.IsNullOrEmpty(m.State)) return false;
        if (Compiled(world, m.Machine) is not { } c) return false;
        int index = c.IndexOf(m.State);
        if (index < 0) return false;
        var tags = c.ByIndex[index].Tags;
        if (tags == null) return false;
        for (int i = 0; i < tags.Count; i++)
            if (string.Equals(tags[i], tag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Goes to `state` now: the old state's exit, the new one's enter, OnStateChanged. Already there:
    // nothing. False, with a warning, when there is no machine or no such state.
    public static bool SetState(World world, Entity entity, string state, Entity activator = default)
    {
        if (!world.IsAlive(entity) || !world.Has<StateMachine>(entity))
        {
            Log.Warn(LogCat.Events, $"I/O: {SetStateInput}({state}) at {World.Describe(entity)}, which has no state machine (sage:state_machine)");
            return false;
        }
        ref var m = ref world.Get<StateMachine>(entity);
        if (Compiled(world, m.Machine) is not { } c)
        {
            MissingMachine(m.Machine);
            return false;
        }
        int to = c.IndexOf(state);
        if (to < 0)
        {
            Log.Warn(LogCat.Events, $"I/O: {SetStateInput}({state}) at {World.Describe(entity)}: state machine {m.Machine} has no state "
                                  + $"'{state}'" + Spelling.Suggest(state, c.Names));
            return false;
        }
        if (!activator.IsNull) m.Activator = activator;
        int from = Resolve(ref m, c);
        if (from == to) return true;
        Change(world, entity, c, from, null, to);
        return true;
    }

    // The first of `transitions` that may be taken now: `input` arrived (null: a tick, when transitions
    // with an `on` are not tried), the machine has been in the state `timeInState` seconds and `when`
    // holds, asked in `context`. `current` is never a target (-1: any). Allocation-free; what a machine
    // of another kind (4d's animation graph) steps with.
    public static StateTransition? FirstTransition(IReadOnlyList<StateTransition>? transitions, string? input, float timeInState,
                                                   in ConditionContext context)
    {
        if (transitions == null) return null;
        for (int i = 0; i < transitions.Count; i++)
            if (transitions[i] is { } t && May(t, input, timeInState, in context)) return t;
        return null;
    }

    private static bool May(StateTransition t, string? input, float timeInState, in ConditionContext context)
    {
        if (string.IsNullOrEmpty(t.On))
        {
            if (input != null) return false;
        }
        else if (input == null || !string.Equals(t.On, input, StringComparison.OrdinalIgnoreCase)) return false;
        if (t.After > 0f && timeInState + Timers.Epsilon < t.After) return false;
        return Conditions.Test(t.When, in context, out _);
    }

    // ---- the machine on an entity ----------------------------------------------------------------------

    // One tick of one machine (StateMachineSystem, outside its query: actions may change the world).
    internal static void Step(World world, Entity entity, float dt)
    {
        if (!world.IsAlive(entity) || !world.Has<StateMachine>(entity)) return;
        ref var m = ref world.Get<StateMachine>(entity);
        if (Compiled(world, m.Machine) is not { } c)
        {
            MissingMachine(m.Machine);
            return;
        }
        if (Start(world, entity, ref m, c)) return;          // it entered a state this tick: its clock starts next tick

        m = ref world.Get<StateMachine>(entity);
        m.TimeInState += dt;
        var t = Pick(world, entity, c, m.Index, null, m.TimeInState, m.Activator);
        if (t != null) Change(world, entity, c, m.Index, t, c.Target(t));
    }

    // An input arrived at the entity (EntityIO's dispatch): whether its machine listens for it, and if it
    // does, the transition it picks is taken.
    private static bool Hear(World world, string input, in IOContext io)
    {
        var entity = io.Self;
        if (!world.TryGet<StateMachine>(entity, out var peek)) return false;
        if (Compiled(world, peek.Machine) is not { } c || !c.Hears(input)) return false;

        ref var m = ref world.Get<StateMachine>(entity);
        if (!io.Activator.IsNull) m.Activator = io.Activator;
        Start(world, entity, ref m, c);                        // an input before its first tick: start it first
        if (!world.IsAlive(entity) || !world.Has<StateMachine>(entity)) return true;

        m = ref world.Get<StateMachine>(entity);
        var t = Pick(world, entity, c, m.Index, input, m.TimeInState, m.Activator);
        if (t != null) Change(world, entity, c, m.Index, t, c.Target(t));
        return true;
    }

    private static StateTransition? Pick(World world, Entity entity, StateMachineRecord.Compiled c, int current, string? input,
                                         float timeInState, Entity activator)
    {
        var context = new ConditionContext(world, Subject(world, entity, activator), entity);
        var own = FirstTransition(c.ByIndex[current].Transitions, input, timeInState, in context);
        if (own != null && c.Target(own) >= 0) return own;

        var any = c.Any;
        for (int i = 0; i < any.Count; i++)
        {
            if (any[i] is not { } t) continue;
            int to = c.Target(t);
            if (to < 0 || to == current) continue;
            if (May(t, input, timeInState, in context)) return t;
        }
        return null;
    }

    // Puts a machine that is in no known state into one: a machine that has not run yet into `initial`
    // (its enter, no OnStateChanged); one whose state is not in the machine any more (a load, a hot
    // reload) into `initial` with a warning and an OnStateChanged. True when it did either.
    private static bool Start(World world, Entity entity, ref StateMachine m, StateMachineRecord.Compiled c)
    {
        if (Resolve(ref m, c) >= 0) return false;
        if (c.Initial < 0) return true;                       // a load error said so; nothing to run

        bool lost = !string.IsNullOrEmpty(m.State);
        if (lost)
            Log.Warn(LogCat.Events, $"State machine {m.Machine} at {World.Describe(entity)}: no state '{m.State}' any more "
                                  + $"(a save or a reload from before it changed); going to '{c.Names[c.Initial]}'");
        Change(world, entity, c, -1, null, c.Initial, fire: lost);
        return true;
    }

    // The index of the state it is in, from its cache or by name; -1 when there is none.
    private static int Resolve(ref StateMachine m, StateMachineRecord.Compiled c)
    {
        if (m.Version == c.Version && m.Index >= 0 && m.Index < c.Count) return m.Index;
        int index = c.IndexOf(m.State);
        m.Index = index;
        m.Version = index >= 0 ? c.Version : 0;
        return index;
    }

    private static void Change(World world, Entity entity, StateMachineRecord.Compiled c, int from, StateTransition? via, int to,
                               bool fire = true)
    {
        if (to < 0) return;
        var activator = world.Get<StateMachine>(entity).Activator;
        var context = new ActionContext(world, Subject(world, entity, activator), entity);
        if (from >= 0) Conditions.Run(c.ByIndex[from].Exit, in context);
        if (via != null) Conditions.Run(via.Then, in context);
        if (!world.IsAlive(entity) || !world.Has<StateMachine>(entity)) return;   // an action took it away

        ref var m = ref world.Get<StateMachine>(entity);
        m.State = c.Names[to];
        m.Index = to;
        m.Version = c.Version;
        m.TimeInState = 0f;
        Conditions.Run(c.ByIndex[to].Enter, in context);
        if (fire && world.IsAlive(entity)) world.FireOutput(entity, OnStateChanged, activator, c.Names[to]);
    }

    private static Entity Subject(World world, Entity entity, Entity activator) =>
        !activator.IsNull && world.IsAlive(activator) ? activator : entity;

    private static StateMachineRecord.Compiled? Compiled(World world, RecordId machine)
    {
        if (machine.IsEmpty || !world.Resources.TryGet<RecordStore>(out var records) || records == null) return null;
        return records.TryGet(machine, out StateMachineRecord record) ? record.Compile() : null;
    }

    private static void MissingMachine(RecordId machine) =>
        Log.Once(LogCat.Events, LogLevel.Warn, $"state-machine:{machine}",
            $"No state_machine record {machine} (removed by a reload, or a typo); machines that run it stand still");

    // ---- content ---------------------------------------------------------------------------------------

    // Whether a machine in the content listens for `input` (a transition's `on`).
    internal static bool Hears(RecordStore records, string input)
    {
        if (string.IsNullOrEmpty(input) || records.TypeNameOf(typeof(StateMachineRecord)) == null) return false;
        foreach (var record in records.Latest<StateMachineRecord>())
            if (record.Compile().Hears(input)) return true;
        return false;
    }

    // Load: an initial state that is one, transitions that go somewhere, waits that are times.
    private static void Check(StateMachineRecord record, RecordCheck check)
    {
        var c = record.Compile();
        if (c.Count == 0)
        {
            check.Error("States", "a state machine needs at least one state");
            return;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in c.Names)
        {
            if (string.IsNullOrWhiteSpace(name)) check.Error("States", "a state needs a name");
            else if (!seen.Add(name)) check.Error($"States['{name}']", $"two states are called '{name}' (names ignore case)");
        }
        if (string.IsNullOrEmpty(record.Initial))
            check.Error("Initial", "a state machine needs an \"initial\" state");
        else if (c.Initial < 0)
            check.Error("Initial", $"'{record.Initial}' is not one of its states" + Spelling.Suggest(record.Initial, c.Names));

        for (int s = 0; s < c.Count; s++)
            Check(c, c.ByIndex[s].Transitions, $"States['{c.Names[s]}'].Transitions", check, s);
        Check(c, c.Any, "Transitions", check, -1);
    }

    private static void Check(StateMachineRecord.Compiled c, List<StateTransition>? transitions, string path, RecordCheck check, int state)
    {
        if (transitions == null) return;
        for (int i = 0; i < transitions.Count; i++)
        {
            string at = $"{path}[{i}]";
            if (transitions[i] is not { } t) { check.Error(at, "an empty transition"); continue; }
            if (string.IsNullOrEmpty(t.To)) check.Error(at, "a transition needs a \"to\" (the state it goes to)");
            else if (c.Target(t) < 0) check.Error($"{at}.To", $"'{t.To}' is not one of its states" + Spelling.Suggest(t.To, c.Names));
            if (!(t.After >= 0f) || !float.IsFinite(t.After)) check.Error($"{at}.After", $"{t.After} is not a time in seconds");
            bool always = string.IsNullOrEmpty(t.On) && t.When == null && !(t.After > 0f);
            if (always && state >= 0 && c.Target(t) == state)
                check.Warn(at, "a transition to its own state with no on, when or after is taken every tick");
            else if (always && state < 0)
                check.Warn(at, "a transition from any state with no on, when or after is taken from every other state at once");
        }
    }
}

// EntityIO phase, after timers and before the dispatch (see StateMachines for why): each machine's time
// in its state moves on and its transitions without `on` are tried. The machines are gathered in the
// query and stepped after it, because a change runs actions, and actions may change the world.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // state machines (#92): may change before 1.0
[System(Id, Phase.EntityIO, After = new[] { LogicTimerSystem.Id }, Before = new[] { "?sage.io.dispatch" })]
internal sealed class StateMachineSystem : ISystem
{
    public const string Id = "sage.logic.state_machines";

    private readonly World _world;
    private readonly Query<StateMachine> _machines;
    private readonly List<Entity> _step = new();

    public StateMachineSystem(World world)
    {
        _world = world;
        _machines = world.Query<StateMachine>();
    }

    public void Run(in SystemContext ctx)
    {
        _step.Clear();
        foreach (var (_, entities) in _machines.Chunks)
            for (int i = 0; i < entities.Length; i++)
                _step.Add(entities.EntityAt(i));

        float dt = ctx.Tick.Dt;
        for (int i = 0; i < _step.Count; i++) StateMachines.Step(_world, _step[i], dt);
        _step.Clear();
    }
}
