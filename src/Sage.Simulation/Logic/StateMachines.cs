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
//   prefab     "sage:logic_state_machine": the part with no machine, for a placement to override
//              (`"overrides": { "parts": { "state_machine": { "machine": "game:guard" } } }`)
//
//   input    SetState <state>        go to that state now (exit, enter, OnStateChanged); unknown: a warning
//            <any `on` name>         a transition's `on`: any input delivered to the entity, registered or not
//   output   OnStateChanged          the state changed; a wire with no parameter of its own is handed the
//                                    new state's name (EntityIO.Fire's value)
//            OnEnter<state>          that state was entered / left (issue #280): each state's own outputs,
//            OnExit<state>           handed its name (a wire may write `OnEnterAlert` for `alert`: names ignore case)
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
// **Nested and parallel states** (issue #280): a state may hold `states` of its own and an `initial`
// (entering it enters that one), or `"parallel": true` (entering it enters all of them, each a region
// that moves on its own). Names are unique across the machine, so `to`, SetState and StateOf name any
// state, nested or not. A transition is tried from the innermost states outward, so an outer state's
// transitions apply in every state inside it; taking one leaves everything inside the nearest
// non-parallel state around both ends (innermost first) and enters the way down to `to` (outer first,
// with initials and every region). Each region moves at most once a tick; the machine's own
// `transitions` are tried only when none did. A flat machine behaves exactly as before.
//
// **During** (issue #280): a state's `during` actions run each tick it is in it, outer states first,
// from the tick after it is entered, before transitions are tried; allocation-free.
//
// **Saved by name:** the component holds the state's name and the seconds spent in it (and `Active`, every
// state it is in with its time, for a nested machine; the activator by persistent id), so a save written
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
    [Property(Tooltip = "The state a machine starts in (a nested one enters its parents first), and goes back to when its saved state is not in the machine any more")]
    public string Initial = "";
    [Property(Tooltip = "The states, by name; a state may hold states of its own (nested, or parallel regions). Names are unique across the machine")]
    public Dictionary<string, MachineState> States = new();
    [Property(Tooltip = "Transitions from any state, tried after the states' own; never to a state it is in")]
    public List<StateTransition> Transitions = new();

    // The machine as the tick reads it: states by index (outer before inner, in the order written),
    // transitions' targets resolved. Rebuilt when a reload hands the record new states (RecordStore keeps
    // the instance and copies the fields in).
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

        // Every state, nested ones too, in document order (a parent before its states): what a
        // component's Active entries are sorted by.
        public readonly string[] Names;
        public readonly string[] Paths;                           // where the record says it, for load errors
        public readonly MachineState[] ByIndex;
        public readonly int[] Parent;                             // -1: a top-level state
        public readonly int[][] Children;                         // empty: a leaf
        public readonly int[] TopLevel;
        public readonly bool[] Parallel;                          // with children: all of them at once
        public readonly int[] InitialOf;                          // a compound state's initial (-1: none, a load error)
        public readonly string[] EnterOutputs;                    // "OnEnter<name>"
        public readonly string[] ExitOutputs;                     // "OnExit<name>"
        public readonly int Initial;                              // -1: no such state (a load error)
        public readonly int MaxActive;                            // the most states it can be in at once
        private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<StateTransition, int> _targets = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<string> _heard = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _fired = new(StringComparer.OrdinalIgnoreCase);

        public Compiled(StateMachineRecord record)
        {
            States = record.States ?? new Dictionary<string, MachineState>();
            Any = record.Transitions ?? new List<StateTransition>();
            InitialName = record.Initial ?? "";

            var names = new List<string>();
            var paths = new List<string>();
            var states = new List<MachineState>();
            var parents = new List<int>();
            var children = new List<List<int>>();
            var top = new List<int>();
            Flatten(States, -1, "", names, paths, states, parents, children, top);

            Names = names.ToArray();
            Paths = paths.ToArray();
            ByIndex = states.ToArray();
            Parent = parents.ToArray();
            TopLevel = top.ToArray();
            Children = new int[Names.Length][];
            Parallel = new bool[Names.Length];
            InitialOf = new int[Names.Length];
            EnterOutputs = new string[Names.Length];
            ExitOutputs = new string[Names.Length];
            for (int i = 0; i < Names.Length; i++)
            {
                _index.TryAdd(Names[i], i);
                Children[i] = children[i].ToArray();
                Parallel[i] = ByIndex[i].Parallel && Children[i].Length > 0;
                EnterOutputs[i] = "OnEnter" + Names[i];
                ExitOutputs[i] = "OnExit" + Names[i];
                _fired.Add(EnterOutputs[i]);
                _fired.Add(ExitOutputs[i]);
            }
            for (int i = 0; i < Names.Length; i++)
            {
                int initial = IndexOf(ByIndex[i].Initial);
                InitialOf[i] = Children[i].Length > 0 && !Parallel[i] && initial >= 0 && IsBelow(initial, i) ? initial : -1;
            }
            Initial = IndexOf(InitialName);
            foreach (var state in ByIndex) Add(state.Transitions);
            Add(Any);

            int most = 0;
            foreach (int t in TopLevel) most = Math.Max(most, Width(t));
            MaxActive = Math.Max(1, most);
        }

        private static void Flatten(Dictionary<string, MachineState>? states, int parent, string at, List<string> names, List<string> paths,
                                    List<MachineState> byIndex, List<int> parents, List<List<int>> children, List<int> top)
        {
            if (states == null) return;
            foreach (var (name, state) in states)
            {
                int i = names.Count;
                string path = $"{at}States['{name}']";
                names.Add(name ?? "");
                paths.Add(path);
                byIndex.Add(state ?? new MachineState());
                parents.Add(parent);
                children.Add(new List<int>());
                if (parent >= 0) children[parent].Add(i);
                else top.Add(i);
                Flatten(state?.States, i, path + ".", names, paths, byIndex, parents, children, top);
            }
        }

        // The most states active at once in this one's subtree, itself included.
        private int Width(int node)
        {
            var kids = Children[node];
            if (kids.Length == 0) return 1;
            int width = 0;
            foreach (int k in kids) width = Parallel[node] ? width + Width(k) : Math.Max(width, Width(k));
            return 1 + width;
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
        public bool IsLeaf(int node) => Children[node].Length == 0;
        public int IndexOf(string? name) => name != null && _index.TryGetValue(name, out int i) ? i : -1;
        public int Target(StateTransition t) => _targets.TryGetValue(t, out int i) ? i : -1;
        public bool Hears(string input) => _heard.Contains(input);
        public bool Fires(string output) => _fired.Contains(output);

        // Whether `node` is inside `ancestor` (-1: the machine itself, which everything is in).
        public bool IsBelow(int node, int ancestor)
        {
            if (ancestor < 0) return node >= 0;
            for (int p = Parent[node]; p >= 0; p = Parent[p])
                if (p == ancestor) return true;
            return false;
        }

        // The state of `ancestor`'s own (or a top-level one, for -1) that `node` is in or is.
        public int ChildToward(int ancestor, int node)
        {
            int n = node;
            while (n >= 0 && Parent[n] != ancestor) n = Parent[n];
            return n;
        }

        // The state a compound one enters when entered itself: its initial, or its first state.
        public int InitialIn(int node) => InitialOf[node] >= 0 ? InitialOf[node] : Children[node][0];

        // Where a transition from `source` to `target` happens: the nearest state around `source` that is
        // not parallel and holds `target` (-1: the machine). Everything active inside it is left, and the
        // way down to `target` entered, so a transition to its own state leaves it and comes back.
        public int Domain(int source, int target)
        {
            for (int a = Parent[source]; a >= 0; a = Parent[a])
                if (!Parallel[a] && IsBelow(target, a)) return a;
            return -1;
        }
    }
}

// One state: what entering and leaving it does, what it does each tick it is in it, where it can go, the
// states inside it, and words for whatever reads the machine.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // state machines (#92): may change before 1.0
public sealed class MachineState
{
    [Property(Tooltip = "What entering it does: the activator is the subject, the machine the other")]
    public List<IAction> Enter = new();
    [Property(Tooltip = "What leaving it does, before the transition's then and the next state's enter")]
    public List<IAction> Exit = new();
    [Property(Tooltip = "What it does each tick it is in it, from the tick after it is entered, before its transitions are tried")]
    public List<IAction> During = new();
    [Property(Tooltip = "Where it can go, tried in order: the first that matches wins")]
    public List<StateTransition> Transitions = new();
    [Property(Tooltip = "Free words for whatever reads the machine (StateMachines.HasTag): a HUD, an animation layer")]
    public List<string> Tags = new();
    [Property(Tooltip = "States inside it (nested): in it means in one of them, or in all of them when parallel")]
    public Dictionary<string, MachineState>? States;
    [Property(Tooltip = "With states: the one entering it enters (one of its states, or deeper)")]
    public string Initial = "";
    [Property(Tooltip = "With states: in all of them at once (parallel regions), each moving on its own")]
    public bool Parallel;
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
    // anim_graph only (issue #358): this transition's own cross-fade, over the state's; a state_machine ignores them.
    [Property(Min = 0, Unit = "s", Tooltip = "anim_graph only: how long taking it cross-fades, over the state's fade; left out, the state's")]
    public float? Fade;
    [Property(Tooltip = "anim_graph only: the curve taking it cross-fades along, over the state's ease; left out, the state's")]
    public Ease? Ease;
}

// The machine on an entity. Saved:
//   "sage:state_machine": { "Machine": "ns:guard", "State": "alert", "TimeInState": 1.5, "Activator": "<persistent id>",
//                           "Active": [ { "Name": "alert", "Time": 1.5 } ] }
[Component("sage:state_machine")]
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // state machines (#92): may change before 1.0
public struct StateMachine : IComponent
{
    [RecordRef("state_machine"), Property(Tooltip = "The state_machine record it runs")]
    public RecordId Machine;
    [Property(Tooltip = "The state it is in, by name (the first innermost one, when nested or parallel); empty until its first tick puts it in the initial state")]
    public string? State;
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds it has been in that state")]
    public float TimeInState;

    // Who sent the input that last moved it: the subject of its actions and conditions. Saved by its
    // persistent id (issue #280), so `!activator` still reaches it after a load; one that is not
    // persistent, or is gone, loads as none.
    [Property(Tooltip = "Who last moved it (the subject of its actions); saved by persistent id")]
    public Entity Activator;

    // Every state it is in, outer before inner, with the seconds spent in each (what a nested state's
    // `after` reads); State and TimeInState are the first innermost one's. Filled on its first tick; a
    // save without it (written before #280) is placed from State.
    [Property(Tooltip = "Every state it is in, outer before inner, with seconds in each; filled on its first tick")]
    public ActiveState[]? Active;

    // The State's index in the compiled machine it was resolved against (its Version), how many of
    // Active are used, and room to list states while stepping; not saved, found again by name.
    internal int Index;
    internal int Version;
    internal int Used;
    internal int[]? Scratch;
}

// One state a machine is in, and for how long.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // state machines (#92): may change before 1.0
public struct ActiveState
{
    [Property(Tooltip = "The state, by name")]
    public string? Name;
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds in it")]
    public float Time;

    internal int Node;                                            // its index in the compiled machine
    internal bool Fresh;                                          // entered (or moved) during this step
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

// The input, the outputs, the checks and the stepping. Registered by the engine (Engine's constructor, sage.core).
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
        // offered to the machine on the entity it arrives at. Each state's OnEnter<name> and
        // OnExit<name> are outputs a wire may name the same way (issue #280).
        engine.Inputs.Heard = name => Hears(records, name);
        engine.Inputs.Listener = Hear;
        engine.Outputs.Fired = name => Fires(records, name);
        records.AddCheck<StateMachineRecord>(Check);
    }

    // The state the entity's machine is in (the first innermost one), or null (no machine, not started yet).
    public static string? StateOf(World world, Entity entity) =>
        world.IsAlive(entity) && world.TryGet<StateMachine>(entity, out var m) && !string.IsNullOrEmpty(m.State) ? m.State : null;

    // Whether the entity's machine is in `state`: the innermost one, a state around it, or one of its
    // parallel regions.
    public static bool IsIn(World world, Entity entity, string state)
    {
        if (!world.IsAlive(entity) || !world.TryGet<StateMachine>(entity, out var m)) return false;
        if (m.Active == null) return string.Equals(m.State, state, StringComparison.OrdinalIgnoreCase);
        for (int i = 0; i < m.Active.Length; i++)
            if (m.Active[i].Name != null && string.Equals(m.Active[i].Name, state, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Whether a state the entity's machine is in (any of them, when nested or parallel) carries `tag`.
    public static bool HasTag(World world, Entity entity, string tag)
    {
        if (!world.IsAlive(entity) || !world.TryGet<StateMachine>(entity, out var m) || string.IsNullOrEmpty(m.State)) return false;
        if (Compiled(world, m.Machine) is not { } c) return false;
        if (m.Active == null) return Tagged(c, c.IndexOf(m.State), tag);
        for (int i = 0; i < m.Active.Length; i++)
            if (m.Active[i].Name is { } name && Tagged(c, c.IndexOf(name), tag)) return true;
        return false;
    }

    private static bool Tagged(StateMachineRecord.Compiled c, int index, string tag)
    {
        if (index < 0) return false;
        var tags = c.ByIndex[index].Tags;
        if (tags == null) return false;
        for (int i = 0; i < tags.Count; i++)
            if (string.Equals(tags[i], tag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Goes to `state` now: what it is in around it is left (exits), the way down entered (enters),
    // OnStateChanged. Already in it: nothing. False, with a warning, when there is no machine or no such state.
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
        Resolve(ref m, c);
        if (Find(ref m, to) >= 0) return true;
        Take(world, entity, c, -1, null, to);
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
        var active = m.Active!;
        for (int i = 0; i < m.Used; i++)
        {
            active[i].Time += dt;
            active[i].Fresh = false;
        }
        Mirror(ref m, c);

        if (!During(world, entity, c)) return;
        Transitions(world, entity, c, null);
    }

    // Each state it is in runs its `during`, outer before inner, unless a transition this step entered it.
    private static bool During(World world, Entity entity, StateMachineRecord.Compiled c)
    {
        ref var m = ref world.Get<StateMachine>(entity);
        var scratch = m.Scratch!;
        int n = 0;
        for (int i = 0; i < m.Used; i++)
            if (c.ByIndex[m.Active![i].Node].During is { Count: > 0 }) scratch[n++] = m.Active[i].Node;
        if (n == 0) return true;

        var context = new ActionContext(world, Subject(world, entity, m.Activator), entity);
        for (int k = 0; k < n; k++)
        {
            int node = scratch[k];
            m = ref world.Get<StateMachine>(entity);
            int at = Find(ref m, node);
            if (at < 0 || m.Active![at].Fresh) continue;
            Conditions.Run(c.ByIndex[node].During, in context);
            if (!world.IsAlive(entity) || !world.Has<StateMachine>(entity)) return false;
        }
        return true;
    }

    // An input arrived at the entity (EntityIO's dispatch): whether its machine listens for it, and if it
    // does, the transitions it picks are taken.
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
        if (Resolve(ref m, c) < 0) return true;
        for (int i = 0; i < m.Used; i++) m.Active![i].Fresh = false;
        Transitions(world, entity, c, input);
        return true;
    }

    // Each innermost state it is in, in order, tries its transitions and then those of the states around
    // it, and takes the first that matches: so each parallel region moves at most once a step (and once
    // per input), and a region a transition already left or entered this step is not tried again. When
    // none moved, the machine's own transitions (from any state) are tried.
    private static void Transitions(World world, Entity entity, StateMachineRecord.Compiled c, string? input)
    {
        ref var m = ref world.Get<StateMachine>(entity);
        var scratch = m.Scratch!;
        int n = 0;
        for (int i = 0; i < m.Used; i++)
            if (c.IsLeaf(m.Active![i].Node)) scratch[n++] = m.Active[i].Node;

        var context = new ConditionContext(world, Subject(world, entity, m.Activator), entity);
        bool moved = false;
        for (int k = 0; k < n; k++)
        {
            if (!world.IsAlive(entity) || !world.Has<StateMachine>(entity)) return;
            for (int node = scratch[k]; node >= 0; node = c.Parent[node])
            {
                m = ref world.Get<StateMachine>(entity);
                int at = Find(ref m, node);
                if (at < 0 || m.Active![at].Fresh) break;
                var t = FirstTransition(c.ByIndex[node].Transitions, input, m.Active[at].Time, in context);
                if (t == null || c.Target(t) < 0) continue;
                Take(world, entity, c, node, t, c.Target(t));
                moved = true;
                break;
            }
        }
        if (moved || !world.IsAlive(entity) || !world.Has<StateMachine>(entity)) return;

        m = ref world.Get<StateMachine>(entity);
        var any = c.Any;
        for (int i = 0; i < any.Count; i++)
        {
            if (any[i] is not { } t) continue;
            int to = c.Target(t);
            if (to < 0 || Find(ref m, to) >= 0) continue;
            if (!May(t, input, m.TimeInState, in context)) continue;
            Take(world, entity, c, -1, t, to);
            return;
        }
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
        Take(world, entity, c, -1, null, c.Initial, fire: lost);
        return true;
    }

    // The index of the innermost state it is in (State), from its cache, from what it says it is in
    // (Active: a load, a reload), or from State alone (a save from before Active, an edit); -1 when it is
    // in nothing it knows, with Active emptied.
    private static int Resolve(ref StateMachine m, StateMachineRecord.Compiled c)
    {
        if (m.Version == c.Version && m.Used > 0 && m.Active != null) return m.Index;

        var saved = m.Active;
        if (m.Active == null || m.Active.Length != c.MaxActive) m.Active = new ActiveState[c.MaxActive];
        if (m.Scratch == null || m.Scratch.Length < c.Count) m.Scratch = new int[Math.Max(c.Count, 1)];
        m.Version = 0;

        if (FromActive(ref m, c, saved) || FromState(ref m, c))
        {
            m.Version = c.Version;
            return m.Index;
        }
        Clear(ref m);
        m.Index = -1;
        return -1;
    }

    // Takes the states a save (or the last compile) says it is in, when they are all states of the
    // machine and make sense together: each one's parent is in it, a compound state is in exactly one of
    // its states, a parallel one in all of them, and State is the first innermost.
    private static bool FromActive(ref StateMachine m, StateMachineRecord.Compiled c, ActiveState[]? saved)
    {
        if (saved == null || string.IsNullOrEmpty(m.State)) return false;
        var into = m.Active!;
        int n = 0;
        for (int i = 0; i < saved.Length; i++)
        {
            if (saved[i].Name is not { } name) continue;
            int node = c.IndexOf(name);
            if (node < 0 || n >= into.Length) { Clear(ref m); return false; }
            var entry = new ActiveState { Name = c.Names[node], Time = saved[i].Time, Node = node };
            int at = n++;                                     // sorted by node as it goes (written in order, usually)
            while (at > 0 && into[at - 1].Node > node) { into[at] = into[at - 1]; at--; }
            if (at > 0 && into[at - 1].Node == node) { Clear(ref m); return false; }
            into[at] = entry;
        }
        for (int i = n; i < into.Length; i++) into[i] = default;
        m.Used = n;
        if (n == 0 || !Consistent(ref m, c)) { Clear(ref m); return false; }
        int leaf = FirstLeaf(ref m, c);
        if (leaf < 0 || !string.Equals(c.Names[m.Active![leaf].Node], m.State, StringComparison.OrdinalIgnoreCase)) { Clear(ref m); return false; }
        Mirror(ref m, c);
        return true;
    }

    private static bool Consistent(ref StateMachine m, StateMachineRecord.Compiled c)
    {
        int top = 0;
        for (int i = 0; i < m.Used; i++)
        {
            int node = m.Active![i].Node;
            int parent = c.Parent[node];
            if (parent < 0) top++;
            else if (Find(ref m, parent) < 0) return false;
            int inside = 0;
            foreach (int child in c.Children[node])
                if (Find(ref m, child) >= 0) inside++;
            int wanted = c.IsLeaf(node) ? 0 : c.Parallel[node] ? c.Children[node].Length : 1;
            if (inside != wanted) return false;
        }
        return top == 1;
    }

    // Places it in State without running anything: the states around it, and the other parallel regions
    // in their initial states, all with State's time.
    private static bool FromState(ref StateMachine m, StateMachineRecord.Compiled c)
    {
        int node = c.IndexOf(m.State);
        if (node < 0) return false;
        Clear(ref m);
        float time = m.TimeInState;
        Place(ref m, c, -1, node, time);
        Mirror(ref m, c);
        return true;
    }

    private static void Place(ref StateMachine m, StateMachineRecord.Compiled c, int from, int target, float time)
    {
        int child = c.ChildToward(from, target);
        if (child < 0) return;
        if (from >= 0 && c.Parallel[from])
        {
            foreach (int region in c.Children[from])
                if (Find(ref m, region) < 0) PlaceNode(ref m, c, region, region == child ? target : region, time);
        }
        else PlaceNode(ref m, c, child, target, time);
    }

    private static void PlaceNode(ref StateMachine m, StateMachineRecord.Compiled c, int node, int target, float time)
    {
        Insert(ref m, c, node, time, fresh: false);
        if (node != target) Place(ref m, c, node, target, time);
        else if (!c.IsLeaf(node))
        {
            if (c.Parallel[node])
                foreach (int region in c.Children[node]) PlaceNode(ref m, c, region, region, time);
            else Place(ref m, c, node, c.InitialIn(node), time);
        }
    }

    // A transition: from `source` (-1: the machine's own, or SetState) to `target`. Every state inside
    // the transition's domain is left, innermost and last first (exit, OnExit<name>), the transition's
    // `then` runs, and the way down to `target` is entered, outer first (enter, OnEnter<name>), with a
    // compound state's initial and every parallel region entered too; then OnStateChanged with the
    // target's name.
    private static void Take(World world, Entity entity, StateMachineRecord.Compiled c, int source, StateTransition? via, int target,
                             bool fire = true)
    {
        if (target < 0) return;
        ref var m = ref world.Get<StateMachine>(entity);
        var activator = m.Activator;
        var context = new ActionContext(world, Subject(world, entity, activator), entity);
        int domain = source >= 0 ? c.Domain(source, target) : ActiveAround(ref m, c, target);

        if (!Leave(world, entity, c, domain, activator, in context)) return;
        if (via != null) Conditions.Run(via.Then, in context);
        if (!world.IsAlive(entity) || !world.Has<StateMachine>(entity)) return;   // an action took it away

        if (!EnterFrom(world, entity, c, domain, target, activator, in context)) return;
        m = ref world.Get<StateMachine>(entity);
        Mirror(ref m, c);
        m.Version = c.Version;
        if (fire && world.IsAlive(entity)) world.FireOutput(entity, OnStateChanged, activator, c.Names[target]);
    }

    // The innermost state it is in that holds `target` (-1: none, the machine): where SetState and the
    // machine's own transitions go from.
    private static int ActiveAround(ref StateMachine m, StateMachineRecord.Compiled c, int target)
    {
        for (int a = c.Parent[target]; a >= 0; a = c.Parent[a])
            if (Find(ref m, a) >= 0) return a;
        return -1;
    }

    private static bool Leave(World world, Entity entity, StateMachineRecord.Compiled c, int domain, Entity activator, in ActionContext context)
    {
        while (true)
        {
            ref var m = ref world.Get<StateMachine>(entity);
            int node = -1;
            for (int i = m.Used - 1; i >= 0; i--)
                if (c.IsBelow(m.Active![i].Node, domain)) { node = m.Active[i].Node; break; }
            if (node < 0) return true;

            Conditions.Run(c.ByIndex[node].Exit, in context);
            if (!world.IsAlive(entity) || !world.Has<StateMachine>(entity)) return false;
            m = ref world.Get<StateMachine>(entity);
            Remove(ref m, node);
            world.FireOutput(entity, c.ExitOutputs[node], activator, c.Names[node]);
            if (!world.IsAlive(entity) || !world.Has<StateMachine>(entity)) return false;
        }
    }

    private static bool EnterFrom(World world, Entity entity, StateMachineRecord.Compiled c, int from, int target, Entity activator,
                                  in ActionContext context)
    {
        int child = c.ChildToward(from, target);
        if (child < 0) return true;
        if (from >= 0 && c.Parallel[from])
        {
            foreach (int region in c.Children[from])
            {
                if (Find(ref world.Get<StateMachine>(entity), region) >= 0) continue;
                if (!EnterNode(world, entity, c, region, region == child ? target : region, activator, in context)) return false;
            }
            return true;
        }
        return EnterNode(world, entity, c, child, target, activator, in context);
    }

    private static bool EnterNode(World world, Entity entity, StateMachineRecord.Compiled c, int node, int target, Entity activator,
                                  in ActionContext context)
    {
        Insert(ref world.Get<StateMachine>(entity), c, node, 0f, fresh: true);
        Conditions.Run(c.ByIndex[node].Enter, in context);
        if (!world.IsAlive(entity) || !world.Has<StateMachine>(entity)) return false;
        world.FireOutput(entity, c.EnterOutputs[node], activator, c.Names[node]);
        if (!world.IsAlive(entity) || !world.Has<StateMachine>(entity)) return false;

        if (node != target) return EnterFrom(world, entity, c, node, target, activator, in context);
        if (c.IsLeaf(node)) return true;
        if (c.Parallel[node])
        {
            foreach (int region in c.Children[node])
                if (!EnterNode(world, entity, c, region, region, activator, in context)) return false;
            return true;
        }
        return EnterFrom(world, entity, c, node, c.InitialIn(node), activator, in context);
    }

    // ---- the active list: sorted by node, so outer states come before the states inside them ---------

    private static int Find(ref StateMachine m, int node)
    {
        var active = m.Active;
        if (active == null) return -1;
        for (int i = 0; i < m.Used; i++)
            if (active[i].Node == node) return i;
        return -1;
    }

    private static void Insert(ref StateMachine m, StateMachineRecord.Compiled c, int node, float time, bool fresh)
    {
        var active = m.Active!;
        if (Find(ref m, node) >= 0) return;
        if (m.Used >= active.Length)
        {
            Log.Once(LogCat.Events, LogLevel.Error, $"state-machine-full:{m.Machine}",
                $"State machine {m.Machine}: more states at once than it can hold; '{c.Names[node]}' not entered");
            return;
        }
        int at = m.Used++;
        while (at > 0 && active[at - 1].Node > node) { active[at] = active[at - 1]; at--; }
        active[at] = new ActiveState { Name = c.Names[node], Time = time, Node = node, Fresh = fresh };
        Mirror(ref m, c);
    }

    private static void Remove(ref StateMachine m, int node)
    {
        int at = Find(ref m, node);
        if (at < 0) return;
        var active = m.Active!;
        for (int i = at; i < m.Used - 1; i++) active[i] = active[i + 1];
        active[--m.Used] = default;
    }

    private static void Clear(ref StateMachine m)
    {
        if (m.Active != null) Array.Clear(m.Active);
        m.Used = 0;
    }

    private static int FirstLeaf(ref StateMachine m, StateMachineRecord.Compiled c)
    {
        for (int i = 0; i < m.Used; i++)
            if (c.IsLeaf(m.Active![i].Node)) return i;
        return -1;
    }

    // State and TimeInState follow the first innermost state it is in.
    private static void Mirror(ref StateMachine m, StateMachineRecord.Compiled c)
    {
        int at = FirstLeaf(ref m, c);
        if (at < 0) return;
        int node = m.Active![at].Node;
        m.State = c.Names[node];
        m.TimeInState = m.Active[at].Time;
        m.Index = node;
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

    // Whether a machine in the content fires `output` (a state's OnEnter<name> or OnExit<name>).
    internal static bool Fires(RecordStore records, string output)
    {
        if (string.IsNullOrEmpty(output) || !output.StartsWith("On", StringComparison.OrdinalIgnoreCase)
            || records.TypeNameOf(typeof(StateMachineRecord)) == null) return false;
        foreach (var record in records.Latest<StateMachineRecord>())
            if (record.Compile().Fires(output)) return true;
        return false;
    }

    // Load: an initial state that is one, transitions that go somewhere, waits that are times, and nested
    // states that say where entering them goes.
    private static void Check(StateMachineRecord record, RecordCheck check)
    {
        var c = record.Compile();
        if (c.Count == 0)
        {
            check.Error("States", "a state machine needs at least one state");
            return;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int s = 0; s < c.Count; s++)
        {
            string name = c.Names[s];
            if (string.IsNullOrWhiteSpace(name)) check.Error(c.Paths[s], "a state needs a name");
            else if (!seen.Add(name)) check.Error(c.Paths[s], $"two states are called '{name}' (names ignore case, and nested states share them)");
        }
        if (string.IsNullOrEmpty(record.Initial))
            check.Error("Initial", "a state machine needs an \"initial\" state");
        else if (c.Initial < 0)
            check.Error("Initial", $"'{record.Initial}' is not one of its states" + Spelling.Suggest(record.Initial, c.Names));

        for (int s = 0; s < c.Count; s++)
        {
            var state = c.ByIndex[s];
            string at = c.Paths[s];
            bool nested = c.Children[s].Length > 0;
            if (state.Parallel && !nested) check.Error($"{at}.Parallel", "a parallel state needs \"states\" (its regions)");
            if (!nested && !string.IsNullOrEmpty(state.Initial))
                check.Error($"{at}.Initial", "an \"initial\" on a state with no \"states\"");
            else if (nested && state.Parallel && !string.IsNullOrEmpty(state.Initial))
                check.Warn($"{at}.Initial", "a parallel state enters all its states; its \"initial\" is ignored");
            else if (nested && !state.Parallel)
            {
                if (string.IsNullOrEmpty(state.Initial))
                    check.Error($"{at}.Initial", "a state with \"states\" needs an \"initial\" (the one entering it enters)");
                else if (c.InitialOf[s] < 0)
                    check.Error($"{at}.Initial", $"'{state.Initial}' is not one of its states" + Spelling.Suggest(state.Initial, Inside(c, s)));
            }
            Check(c, state.Transitions, $"{at}.Transitions", check, s);
        }
        Check(c, c.Any, "Transitions", check, -1);
    }

    private static List<string> Inside(StateMachineRecord.Compiled c, int node)
    {
        var names = new List<string>();
        for (int i = 0; i < c.Count; i++)
            if (c.IsBelow(i, node)) names.Add(c.Names[i]);
        return names;
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
