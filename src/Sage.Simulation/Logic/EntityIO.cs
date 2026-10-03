#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Sage.Simulation;

// Entity I/O (docs/design/04 §3.4, TODO F17).
//
// **Level logic, written in data.** A button opens a door; walking into a room starts an ambush; a lever
// says something and kills a light. None of that is worth a system, a component or a line of C#, and in
// every engine that made it code the levels stopped being editable by the person building them. Source's
// answer — an entity fires a named **output**, which is wired to a named **input** on another entity —
// is the one that has lasted, so it is the one here.
//
// What is different from Source: a connection is **resolved when the level loads** rather than by string
// lookup at every fire, and an input that does not exist is an error at load with the map and the entity
// named, instead of nothing happening at run time and nobody knowing why. What is the same: the wiring
// lives in the map file, so a mapper changes what a level does without a programmer.
//
// The dispatch table is a **registry**, not the `[Input]` attributes 04 §3.4 sketches: those want the
// source generator (09 §3.2), which does not cover them yet. Modules register their inputs in Init,
// the way prefab parts were registered before they were declared (issue #17); the generator will
// replace the registration rather than the design.

// What an input is handed. `Self` is the entity being fired at; `Activator` is who started the chain (the
// player who pressed the button); `Caller` is the entity that fired the output.
public readonly struct IOContext
{
    public required Entity Self { get; init; }
    public Entity Activator { get; init; }
    public Entity Caller { get; init; }
    public string Parameter { get; init; }

    // The parameter as a number, or `fallback` when it is not one. Most inputs want a number or nothing.
    public float Number(float fallback = 0f) =>
        float.TryParse(Parameter, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
}

public delegate void EntityInput(World world, in IOContext io);

// Every input the game knows, by name. Case-insensitive, because a mapper types these by hand.
//
// **Two kinds of handler** (issue #91). A *global* one (`Register(name, handler)`) is the only one for
// its name and takes every entity it is sent at: `Kill`, `Say`, `Fire`, a game's own. A *routed* one
// (`Register<T>(name, handler)`) belongs to one component: `Trigger` on a relay, `Add` on a counter,
// `Toggle` on a mover *and* on a branch. A name may have one routed handler per component, and a global
// one beside them. When an input arrives at an entity:
//
//   1. every routed handler whose component the entity has runs, in the order they were registered —
//      all of them, not the first: an entity that is a counter and a remap both hear `SetValue`, the
//      way a Source entity with two behaviours would, and nothing depends on registration order but
//      the order they run in;
//   2. if none did, the global handler runs, if there is one — so a game's own `Toggle` for things that
//      are not movers or branches still works, and every global handler registered before #91 works
//      exactly as it did;
//   3. if neither, the input is refused with a warning naming the components that take it.
//
// Short generic names can therefore coexist: `Enable`, `Disable`, `Toggle`, `Add`, `SetValue`,
// `Trigger`, each once per component that means something by it.
public sealed class EntityInputs
{
    // Who takes one input name: a global handler, routed ones, or both.
    private sealed class Entry
    {
        public EntityInput? Global;
        public Route[] Routes = Array.Empty<Route>();
    }

    internal readonly struct Route
    {
        public Route(Type component, string componentId, Func<Entity, bool> has, EntityInput handler)
        {
            Component = component;
            ComponentId = componentId;
            Has = has;
            Handler = handler;
        }

        public Type Component { get; }
        public string ComponentId { get; }
        public Func<Entity, bool> Has { get; }
        public EntityInput Handler { get; }
    }

    // What happened to one input at one entity (Deliver).
    internal enum Delivery { NoSuchInput, NobodyTookIt, Delivered }

    private readonly Dictionary<string, Entry> _inputs = new(StringComparer.OrdinalIgnoreCase);

    // Closed when the first world exists (SageApp.CreateWorld): its level's wiring was checked without it.
    public RegistrationSeal Seal { get; } = new("entity input", "a level's wiring may already have been checked without it");

    // Who registered each input (issue #12); set by the Engine. A routed one is recorded as
    // `name@component` ("Trigger@sage:logic_relay"), so each has an owner of its own, and under its name
    // too when it is the first of that name.
    public RegistrationLedger? Ledger { get; set; }

    // A global handler: the only one for this name, for any entity it is sent at.
    public void Register(string name, EntityInput handler)
    {
        Seal.Check(name);
        var entry = EntryFor(name);
        if (entry.Global != null)
            Assert.Ensure(false, $"Entity input '{name}' is registered twice" +
                                 (Ledger?.OwnerOf("entity input", name) is { } first ? $" (first by {first})" : ""));
        else
        {
            entry.Global = handler;
            Ledger?.Record("entity input", name);
        }
    }

    // A handler routed to one component (issue #91): it runs only for an entity that has a `T`, beside
    // any other component's handler for the same name. One per name and component.
    [Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
    public void Register<T>(string name, EntityInput handler) where T : struct, IComponent
    {
        Seal.Check(name);
        string component = ComponentId(typeof(T));
        var entry = EntryFor(name);
        foreach (var route in entry.Routes)
        {
            if (route.Component != typeof(T)) continue;
            Assert.Ensure(false, $"Entity input '{name}' is registered twice for {component}" +
                                 (Ledger?.OwnerOf("entity input", $"{name}@{component}") is { } first ? $" (first by {first})" : ""));
            return;
        }
        var routes = new Route[entry.Routes.Length + 1];
        entry.Routes.CopyTo(routes, 0);
        routes[^1] = new Route(typeof(T), component, static e => e.HasComponent<T>(), handler);
        entry.Routes = routes;
        Ledger?.Record("entity input", $"{name}@{component}");
        // And the name, for the first to register it (a global handler registered later takes it over),
        // so "who owns SetState" still has an answer.
        if (Ledger != null && Ledger.OwnerOf("entity input", name) == null) Ledger.Record("entity input", name);
    }

    // Whether anything takes this name, globally or on some component, or content listens for it (a
    // state machine's `on`, issue #92): what a wire may send without being an unknown input.
    public bool Has(string name) => _inputs.ContainsKey(name) || (Heard != null && Heard(name));

    // The *global* handler for a name. A name only components take has none: send it with EntityIO,
    // which routes it (Deliver).
    public bool TryGet(string name, out EntityInput handler)
    {
        if (_inputs.TryGetValue(name, out var entry) && entry.Global != null)
        {
            handler = entry.Global;
            return true;
        }
        handler = null!;
        return false;
    }

    public IEnumerable<string> Names => _inputs.Keys;

    // Whether this entity would take the input: a component of its has a handler for it, or there is
    // a global one.
    [Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
    public bool Takes(Entity entity, string name)
    {
        if (!_inputs.TryGetValue(name, out var entry)) return false;
        if (entry.Global != null) return true;
        foreach (var route in entry.Routes)
            if (route.Has(entity)) return true;
        return false;
    }

    // The components with a handler of their own for this name, in registration order ("sage:mover").
    [Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
    public IReadOnlyList<string> ComponentsTaking(string name)
    {
        if (!_inputs.TryGetValue(name, out var entry) || entry.Routes.Length == 0) return Array.Empty<string>();
        var ids = new string[entry.Routes.Length];
        for (int i = 0; i < ids.Length; i++) ids[i] = entry.Routes[i].ComponentId;
        return ids;
    }

    // Whether a global handler exists for this name (the registry dump says so beside the components).
    internal bool HasGlobal(string name) => _inputs.TryGetValue(name, out var entry) && entry.Global != null;

    // Runs what takes this input at `io.Self` (the rule in the class comment). Allocation-free: a
    // lookup, a walk of a small array and the handlers themselves.
    internal Delivery Deliver(World world, string name, in IOContext io)
    {
        bool known = _inputs.TryGetValue(name, out var entry);
        bool took = false;
        if (known)
        {
            var routes = entry!.Routes;
            for (int i = 0; i < routes.Length; i++)
            {
                // Asked afresh for each: an earlier handler may have added or removed a component.
                if (!world.IsAlive(io.Self) || !routes[i].Has(io.Self)) continue;
                took = true;
                routes[i].Handler(world, in io);
            }
            if (!took && entry.Global != null)
            {
                took = true;
                entry.Global(world, in io);
            }
        }

        // And whatever listens on the entity (a state machine's `on`, issue #92), if it is still there.
        bool heard = Listener != null && world.IsAlive(io.Self) && Listener(world, name, in io);
        if (took || heard) return Delivery.Delivered;
        return known ? Delivery.NobodyTookIt : Delivery.NoSuchInput;
    }

    private Entry EntryFor(string name)
    {
        if (!_inputs.TryGetValue(name, out var entry)) _inputs.Add(name, entry = new Entry());
        return entry;
    }

    private static string ComponentId(Type type) =>
        System.Reflection.CustomAttributeExtensions.GetCustomAttribute<ComponentAttribute>(type)?.Id ?? type.Name;

    // Inputs content listens for rather than code (issue #92): whether some record hears the name, for
    // Has; and, as each input arrives, whether the entity it arrives at heard it — called after the
    // handlers, if there are any (Deliver). Set by the engine (StateMachines.Register).
    internal Func<string, bool>? Heard;
    internal InputListener? Listener;
}

internal delegate bool InputListener(World world, string input, in IOContext io);

// Every output the engine and its plugins fire, by name, with what it means (issue #18). An output is
// a name a wire listens for, so nothing *needs* this list to work — but a mapper does: the FGD, the
// registry dump and `io_list` say which outputs exist from here, where they used to be a sentence in
// FgdExport that nothing kept true. A module declares an output in Init beside the code that fires it.
public sealed class EntityOutputs
{
    private readonly Dictionary<string, string> _outputs = new(StringComparer.OrdinalIgnoreCase);

    // Closed with the inputs, when the first world exists.
    public RegistrationSeal Seal { get; } = new("entity output", "the first world's FGD and wiring were checked without it");

    public RegistrationLedger? Ledger { get; set; }

    public void Declare(string name, string description)
    {
        Seal.Check(name);
        if (_outputs.TryAdd(name, description)) Ledger?.Record("entity output", name);
        else Assert.Ensure(false, $"Entity output '{name}' is declared twice" +
                                  (Ledger?.OwnerOf("entity output", name) is { } first ? $" (first by {first})" : ""));
    }

    public bool Has(string name) => _outputs.ContainsKey(name) || (Fired != null && Fired(name));
    public string? Describe(string name) => _outputs.TryGetValue(name, out var d) ? d : null;
    public IEnumerable<string> Names => _outputs.Keys;

    // Outputs content fires rather than code (issue #280): a state machine's OnEnter<state> and
    // OnExit<state>, for Has. Set by the engine (StateMachines.Register).
    internal Func<string, bool>? Fired;
}

// One wire: "when this entity fires `Output`, send `Input` to `Target` after `Delay` seconds". Written
// in a `.map` as `"OnStartTouch" "target,input,parameter,delay,times"`, and in a scene or placements
// document as a placement's `outputs` (issue #80): { "output": "OnStartTouch", "target": "intro_cam",
// "input": "CameraOn", "parameter": "3" }.
public sealed class Connection
{
    [Property(Tooltip = "The output of this entity that sends it: OnStartTouch, OnUse, OnCameraOff; io_list lists them")]
    public string Output = "";
    [Property(Tooltip = "Who receives it: an entity's name, or !self / !activator / !caller")]
    public string Target = "";              // a name, or !self / !activator / !caller
    [Property(Tooltip = "The input it sends: Open, Kill, CameraOn; io_list lists them")]
    public string Input = "";
    [Property(Tooltip = "Handed to the input: a hold time, a line to say, an output to fire")]
    public string Parameter = "";
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds between the output firing and the input arriving")]
    public float Delay;
    [Property(Tooltip = "How many times it may fire; -1 = every time")]
    public int Times = -1;                  // -1: as often as it fires

    // Fires only when this holds (issue #91), asked when the output fires, of the activator (the
    // condition's subject) and this entity (its other). A wire whose condition does not hold sends
    // nothing and is not counted toward `times`. Written in a scene or placements document; a `.map`
    // key has no room for one.
    [Property(Tooltip = "Fires only when this holds, asked of the activator (subject) and this entity (other); a wire that does not fire does not count toward times")]
    public ICondition? Requires;

    // A fresh wire with the same settings, for each entity a shared record places: what is resolved and
    // counted belongs to one entity.
    internal Connection Copy() => new()
    {
        Output = Output,
        Target = Target,
        Input = Input,
        Parameter = Parameter,
        Delay = Delay,
        Times = Times,
        Requires = Requires,
    };

    // Filled in at load. A handle rather than a name lookup per fire — but the name is kept, because an
    // entity that is spawned later (or respawned) has to be found again (04 §3.4, "late binding").
    internal Entity Resolved;
    internal int Fired;
}

// [Transient]: a level's wiring comes from the map, the same as the walls do, and holds resolved entity
// handles that mean nothing in another session. How often each wire has fired is saved all the same, by
// the `entity_io` saved resource (EntityIO), against the entity and the wire's index (issue #90).
[Transient]
[Component("sage:io_connections")]
public struct IOConnections : IComponent
{
    public Connection[] Wires;
}

// The queue, per world. Firing an output puts deliveries in it; the `EntityIO` phase takes them out.
//
// **Saved** (issue #90) as the `entity_io` resource: every input still on its way, with the time it has
// left and its target by persistent id (and by name, for an entity a map or a placements document put
// there, which has none), and every wire's `Fired` count, so a save taken half-way through a sequence
// finishes the sequence and a `times: 1` wire stays spent. A save from before #90 has no `entity_io`,
// and loads as a world with nothing on its way and every wire unfired — what it would have been then.
//
// **The clock** is this world's unpaused ticks. It moves on at the start of a tick, the first time
// anything in the tick asks it (an output fired in PostPhysics, the dispatch in EntityIO), so every delay
// is measured from the tick it was fired in wherever in the tick that was: a trigger's wire and a wire
// fired by an input arrive on the same tick for the same delay (it used to move only in the dispatch,
// so an output fired before it measured its delay from the previous tick; #80's note). A paused world's
// clock stands still, as the dispatch does.
//
// A load *replaces* this resource (SaveSystem), so hold `world.IO()` for a call, not across ticks.
[SavedResource("entity_io", Plugin = "sage.gameplay.io")]
public sealed class EntityIO : ISavedResource
{
    private struct Queued
    {
        public double Due;
        public long Order;                  // ties broken by when it was queued, so a tick is deterministic
        public Entity Target;
        public string Output;               // the wire's output, for the history ("" when sent directly)
        public string Input;
        public string Parameter;
        public Entity Activator;
        public Entity Caller;
        public string TargetName;           // for late binding when the handle is dead
        public bool SameTick;               // may arrive in this tick's dispatch (a same-tick relay, #91)
    }

    // A delay of N ticks' worth of seconds is due on the Nth tick, not one later for float dust: a tick's
    // time is a sum of float steps, and 60 of 1/60 s is a hair either side of 1.
    private const double DueEpsilon = 1e-6;

    private readonly List<Queued> _pending = new();
    private readonly List<Queued> _due = new();
    private long _order;
    private double _time;
    private long _clockTick = -1;
    private World? _world;
    private bool _warnedBudget;
    private bool _sameTickQueued;

    [Transient] public int PendingCount => _pending.Count;
    [Transient] public int DispatchedLastTick { get; private set; }

    // How many inputs one tick may deliver. A wire that fires itself is a level bug, not an engine one,
    // but it must cost a warning rather than the process (04 §3.4). Settings, not state: not saved, and
    // carried over to the resource a load puts in this one's place (EntityIOSystem).
    [Transient] public int Budget = 256;
    [Transient] public bool Trace;

    // Seconds of unpaused ticks this world has run, as entity I/O counts them (see the class comment).
    [Transient] public double Now => Clock(_world);

    // Fires an output: every wire on the entity with that name queues its input.
    public void Fire(World world, Entity source, string output, Entity activator = default) =>
        Fire(world, source, output, activator, null, 0f, false, false);

    // Fires an output with a value: a wire with no parameter of its own hands the value to its input
    // (Source's rule), so `OnStateChanged` tells a wire the state it changed to (issue #92) and a
    // counter's `OnChanged` a comparison the count (issue #91).
    public void Fire(World world, Entity source, string output, Entity activator, string value) =>
        Fire(world, source, output, activator, value ?? "", 0f, false, false);

    // The same, and `sameTick` (issue #91): its wires with no delay arrive in this tick's dispatch
    // rather than the next (a same-tick relay).
    [Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
    public void Fire(World world, Entity source, string output, Entity activator, string? value, bool sameTick) =>
        Fire(world, source, output, activator, value, 0f, false, sameTick);

    // With a number, written out only if a wire hands it on (and then without allocating for a whole
    // number up to a thousand or so, so a counter's outputs cost nothing per tick).
    [Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
    public void Fire(World world, Entity source, string output, Entity activator, float value, bool sameTick = false) =>
        Fire(world, source, output, activator, null, value, true, sameTick);

    private void Fire(World world, Entity source, string output, Entity activator,
                      string? text, float number, bool hasNumber, bool sameTick)
    {
        _world ??= world;
        if (source.IsNull || !source.HasComponent<IOConnections>()) return;

        var wires = source.GetComponent<IOConnections>().Wires;
        if (wires == null) return;

        foreach (var wire in wires)
        {
            if (!string.Equals(wire.Output, output, StringComparison.OrdinalIgnoreCase)) continue;
            if (wire.Times >= 0 && wire.Fired >= wire.Times) continue;
            // A condition on the wire (#91): asked of whoever set this off, about this entity. One that
            // does not hold sends nothing and spends none of the wire's `times`.
            if (wire.Requires != null && !wire.Requires.Test(new ConditionContext(world, activator, source), out _)) continue;
            wire.Fired++;

            string parameter = wire.Parameter.Length > 0 ? wire.Parameter
                             : text ?? (hasNumber ? IOValues.Format(number) : "");
            var target = Resolve(world, wire, source, activator);
            Queue(target, wire.Output, wire.Input, parameter, wire.Delay, activator, source, wire.Target, sameTick && wire.Delay <= 0f);
        }
    }

    // Fires one input directly, from code or the console.
    public void FireInput(Entity target, string input, string parameter = "", float delay = 0f,
                          Entity activator = default, Entity caller = default) =>
        Queue(target, "", input, parameter, delay, activator, caller, "", false);

    // Fires one input at an entity by name, found when it arrives rather than now (a wire's late
    // binding, 04 §3.4): what a delayed `fire` action (#89) or a wire aims at may be spawned, or
    // respawned, in the meantime. Nothing by that name when it arrives: nothing happens (logged at Debug).
    // A selector (`@lamps`, `@class:torch`, `@tag:ns:id`; IOTargets) reaches every member it has then.
    public void FireInput(string targetName, string input, string parameter = "", float delay = 0f,
                          Entity activator = default, Entity caller = default)
    {
        var target = _world != null && !IOTargets.IsSelector(targetName) ? _world.FindByName(targetName) : default;
        Queue(target, "", input, parameter, delay, activator, caller, targetName ?? "", false);
    }

    private void Queue(Entity target, string output, string input, string parameter, float delay,
                       Entity activator, Entity caller, string targetName, bool sameTick)
    {
        if (sameTick) _sameTickQueued = true;
        _pending.Add(new Queued
        {
            Due = Clock(_world) + Math.Max(0f, delay),
            Order = _order++,
            Target = target,
            Output = output,
            Input = input,
            Parameter = parameter ?? "",
            Activator = activator,
            Caller = caller,
            TargetName = targetName,
            SameTick = sameTick,
        });
    }

    // The clock, brought up to this tick: one step for a tick that is new and not paused. Asked at least
    // once every unpaused tick (by the dispatch), so it never has more than one tick to catch up.
    private double Clock(World? world)
    {
        if (world == null) return _time;
        long tick = world.Tick;
        if (tick != _clockTick)
        {
            if (_clockTick >= 0 && !world.Paused) _time += world.LastTick.Dt;
            _clockTick = tick;
        }
        return _time;
    }

    // `!self`, `!activator` and `!caller` are resolved when the wire fires, because they are *about* this
    // firing; a name was resolved at load and is only looked up again if that entity has gone.
    private static Entity Resolve(World world, Connection wire, Entity self, Entity activator)
    {
        if (wire.Target.Length > 0 && wire.Target[0] == '!')
        {
            if (string.Equals(wire.Target, "!self", StringComparison.OrdinalIgnoreCase)) return self;
            if (string.Equals(wire.Target, "!activator", StringComparison.OrdinalIgnoreCase)) return activator;
            if (string.Equals(wire.Target, "!caller", StringComparison.OrdinalIgnoreCase)) return self;
        }
        // A selector is resolved when the input arrives, member by member (Deliver).
        if (IOTargets.IsSelector(wire.Target)) return default;

        if (!wire.Resolved.IsNull && world.IsAlive(wire.Resolved)) return wire.Resolved;
        return wire.Resolved = world.FindByName(wire.Target);
    }

    // Called by the dispatch system once a tick.
    internal void Run(World world, EntityInputs inputs)
    {
        _world ??= world;
        double now = Clock(world);
        DispatchedLastTick = 0;
        _sameTickQueued = false;
        if (_pending.Count == 0) return;

        // Everything due this tick, oldest first. Taken out of the list before dispatching, because an
        // input may fire more outputs and those belong to the *next* pass, not this one — which is what
        // stops one tick from running a chain to its end and makes a delay of 0 still mean "next tick".
        if (!Deliver(world, inputs, now, sameTickOnly: false)) return;

        // The one exception (issue #91): a same-tick relay's wires with no delay, fired during the pass,
        // arrive in another pass of this tick, and so on down the chain. The budget still counts every
        // delivery of the tick, so a same-tick relay wired to itself stops at io_maxdispatch with the
        // warning, not in a hang.
        while (_sameTickQueued)
        {
            _sameTickQueued = false;
            if (!Deliver(world, inputs, now, sameTickOnly: true)) return;
        }
    }

    // One pass: takes what is due (only same-tick deliveries, for the passes after the first) and
    // delivers it in order. False when the budget ran out.
    private bool Deliver(World world, EntityInputs inputs, double now, bool sameTickOnly)
    {
        _due.Clear();
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            if (_pending[i].Due > now + DueEpsilon) continue;
            if (sameTickOnly && !_pending[i].SameTick) continue;
            _due.Add(_pending[i]);
            _pending.RemoveAt(i);
        }
        if (_due.Count == 0) return true;

        _due.Sort(static (a, b) => a.Due != b.Due ? a.Due.CompareTo(b.Due) : a.Order.CompareTo(b.Order));

        foreach (var pending in _due)
        {
            if (DispatchedLastTick >= Budget) { WarnBudget(); return false; }

            if (IOTargets.IsSelector(pending.TargetName) && pending.Target.IsNull)
            {
                // A group (issue #276): every member it has now, each one delivery. Collected first,
                // because an input may change the world (Kill) and a query cannot be walked meanwhile.
                _members.Clear();
                _selectors.Collect(world, pending.TargetName, _members);
                if (_members.Count == 0)
                {
                    Record(world, pending, default, IOOutcome.NoTarget);
                    Log.Debug(LogCat.Events, $"I/O: '{pending.Input}' had no target in '{pending.TargetName}'");
                    continue;
                }
                for (int m = 0; m < _members.Count; m++)
                {
                    if (DispatchedLastTick >= Budget) { WarnBudget(); return false; }
                    if (world.IsAlive(_members[m])) DeliverOne(world, inputs, pending, _members[m]);
                }
                continue;
            }

            var target = pending.Target;
            if ((target.IsNull || !world.IsAlive(target)) && pending.TargetName.Length > 0)
                target = world.FindByName(pending.TargetName);       // late binding (04 §3.4)

            if (target.IsNull || !world.IsAlive(target))
            {
                Record(world, pending, default, IOOutcome.NoTarget);
                Log.Debug(LogCat.Events, $"I/O: '{pending.Input}' had no target"
                                       + (pending.TargetName.Length > 0 ? $" named '{pending.TargetName}'" : ""));
                continue;
            }

            DeliverOne(world, inputs, pending, target);
        }
        return true;
    }

    private void WarnBudget()
    {
        if (_warnedBudget) return;
        _warnedBudget = true;
        Log.Warn(LogCat.Events, $"Entity I/O: more than {Budget} inputs in one tick; the rest are dropped "
                              + "(a wire that fires itself?). `io_trace 1` shows the chain, `io_history` the last of it.");
    }

    // One input at one live entity: the handlers, the history, and what went wrong said once.
    private void DeliverOne(World world, EntityInputs inputs, in Queued pending, Entity target)
    {
        if (Trace)
            Log.Info(LogCat.Events, $"I/O: {World.Describe(pending.Caller)}"
                                  + (pending.Output.Length > 0 ? $".{pending.Output}" : "")
                                  + $" → {World.Describe(target)}.{pending.Input}({pending.Parameter})"
                                  + (pending.TargetName.Length > 0 && IOTargets.IsSelector(pending.TargetName) ? $" via {pending.TargetName}" : ""));

        DispatchedLastTick++;
        EntityInputs.Delivery delivered;
        try
        {
            delivered = inputs.Deliver(world, pending.Input, new IOContext
            {
                Self = target,
                Activator = pending.Activator,
                Caller = pending.Caller,
                Parameter = pending.Parameter,
            });
        }
        catch (Exception ex) when (ex is not SageFatalException)
        {
            // 04 §8 (issue #402): one bad handler — a game's own input, as often as not — costs that one
            // delivery, said with the wire it came down, and the rest of the tick's inputs still arrive.
            Record(world, pending, target, IOOutcome.Failed);
            Log.Error(LogCat.Events, $"I/O: {World.Describe(pending.Caller)}"
                                   + (pending.Output.Length > 0 ? $".{pending.Output}" : "")
                                   + $" → {World.Describe(target)}.{pending.Input}({pending.Parameter}) threw "
                                   + $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            if (Debugger.IsAttached) Debugger.Break();
            return;
        }

        if (delivered == EntityInputs.Delivery.NoSuchInput)
        {
            // Logged at load too, but a `ent_fire` typo arrives here.
            DispatchedLastTick--;
            Record(world, pending, target, IOOutcome.NoSuchInput);
            Log.Warn(LogCat.Events, $"I/O: no input called '{pending.Input}' ({World.Describe(target)})");
        }
        else if (delivered == EntityInputs.Delivery.NobodyTookIt)
        {
            Record(world, pending, target, IOOutcome.NobodyTookIt);
            Log.Warn(LogCat.Events, $"I/O: '{pending.Input}' at {World.Describe(target)}, which has nothing that takes it "
                                  + $"(it is for {string.Join(", ", inputs.ComponentsTaking(pending.Input))})");
        }
        else Record(world, pending, target, IOOutcome.Delivered);
    }

    // ---- History (issue #276) ---------------------------------------------------------------------------

    // The last inputs delivered (or not), oldest overwritten first: what `io_history` prints and the
    // editor's link view shows beside a wire. A preallocated ring of structs whose strings are the wires'
    // own, so keeping it costs nothing per delivery. Not saved: it is a debugging aid about this session.
    public const int HistoryCapacity = 256;
    private readonly IORecord[] _history = new IORecord[HistoryCapacity];
    private int _historyNext, _historyCount;

    [Transient] public int HistoryCount => _historyCount;

    // Record `i`, 0 the oldest kept and HistoryCount - 1 the newest.
    public IORecord HistoryAt(int i)
    {
        if ((uint)i >= (uint)_historyCount) throw new ArgumentOutOfRangeException(nameof(i));
        int oldest = (_historyNext - _historyCount + HistoryCapacity) % HistoryCapacity;
        return _history[(oldest + i) % HistoryCapacity];
    }

    // The records about `entity` (as the target, or as the caller whose output sent it), newest first,
    // up to `max`, added to `into`. For a tool: it walks the ring.
    public void HistoryOf(Entity entity, List<IORecord> into, int max = HistoryCapacity)
    {
        for (int i = _historyCount - 1; i >= 0 && max > 0; i--)
        {
            var record = HistoryAt(i);
            if (record.Target != entity && record.Caller != entity) continue;
            into.Add(record);
            max--;
        }
    }

    public void ClearHistory() { _historyNext = 0; _historyCount = 0; }

    // One record as a line: `[t 1.25] relay.OnTrigger -> lamp_2.TurnOn("") via @lamps: Delivered`.
    public static string Describe(IORecord record) =>
        "[t " + record.Time.ToString("0.00", CultureInfo.InvariantCulture) + "] "
        + (record.Caller.IsNull ? "(direct)" : World.Describe(record.Caller))
        + (record.Output.Length > 0 ? "." + record.Output : "")
        + " -> " + (record.Target.IsNull ? record.TargetName : World.Describe(record.Target))
        + "." + record.Input + "(\"" + record.Parameter + "\")"
        + (IOTargets.IsSelector(record.TargetName) && !record.Target.IsNull ? " via " + record.TargetName : "")
        + ": " + record.Outcome;

    private void Record(World world, in Queued pending, Entity target, IOOutcome outcome)
    {
        _history[_historyNext] = new IORecord(world.Tick, _time, pending.Caller, pending.Output, target,
                                              pending.TargetName, pending.Input, pending.Parameter, outcome);
        _historyNext = (_historyNext + 1) % HistoryCapacity;
        if (_historyCount < HistoryCapacity) _historyCount++;
    }

    // A selector's members now (IOTargets.Members), with this world's cached queries.
    private readonly IOSelectorCache _selectors = new();
    private readonly List<Entity> _members = new();

    internal void Collect(World world, string selector, List<Entity> into) => _selectors.Collect(world, selector, into);

    internal void Clear()
    {
        _pending.Clear();
        _due.Clear();
        _warnedBudget = false;
    }

    // The settings a load's replacement keeps (they are cvars', not the save's).
    internal void AdoptSettings(EntityIO previous)
    {
        Budget = previous.Budget;
        Trace = previous.Trace;
    }

    internal void Bind(World world) => _world = world;

    // ---- Saving (issue #90) ---------------------------------------------------------------------------

    // One input on its way, as a save writes it. `Remaining` rather than a due time, because the clock is
    // this session's: a load counts it down from the tick it loads on. Entities are written by persistent
    // id (SaveJson's entity converter), and null when they have none — which is why the target's name is
    // written too.
    internal sealed class SavedInput
    {
        public double Remaining;
        public Entity Target;
        public string TargetName = "";
        public string Input = "";
        public string Parameter = "";
        public Entity Activator;
        public Entity Caller;
    }

    // How often one wire has fired: the entity it is on (by persistent id, else by name), the wire's
    // index in its IOConnections and, as a check that it is still the same wire, its output and input.
    internal sealed class SavedWire
    {
        public Entity Entity;
        public string Name = "";
        public int Wire;
        public string Output = "";
        public string Input = "";
        public int Fired;
    }

    private List<SavedInput>? _loadedPending;
    private List<SavedWire>? _loadedWires;

    // What a save writes, made when it is asked for (SaveSystem reads the resource at a tick boundary) and
    // taken back when a load hands it over; AfterLoad applies it once the world's entities are all back.
    [JsonInclude]
    internal List<SavedInput> Pending
    {
        get
        {
            var list = new List<SavedInput>(_pending.Count);
            if (_pending.Count == 0) return list;
            var world = _world;
            double now = Clock(world);
            var sorted = new List<Queued>(_pending);
            sorted.Sort(static (a, b) => a.Due != b.Due ? a.Due.CompareTo(b.Due) : a.Order.CompareTo(b.Order));
            foreach (var p in sorted)
            {
                bool alive = world != null && !p.Target.IsNull && world.IsAlive(p.Target);
                list.Add(new SavedInput
                {
                    Remaining = Math.Max(0.0, p.Due - now),
                    Target = alive ? p.Target : default,
                    TargetName = p.TargetName.Length > 0 ? p.TargetName : alive ? p.Target.Name ?? "" : "",
                    Input = p.Input,
                    Parameter = p.Parameter,
                    Activator = world != null && world.IsAlive(p.Activator) ? p.Activator : default,
                    Caller = world != null && world.IsAlive(p.Caller) ? p.Caller : default,
                });
            }
            return list;
        }
        set => _loadedPending = value;
    }

    [JsonInclude]
    internal List<SavedWire> Wires
    {
        get
        {
            var list = new List<SavedWire>();
            if (_world is not { } world) return list;
            foreach (var entity in world.Query<IOConnections>().Entities)
            {
                var wires = entity.GetComponent<IOConnections>().Wires;
                if (wires == null) continue;
                for (int i = 0; i < wires.Length; i++)
                {
                    if (wires[i].Fired == 0) continue;
                    list.Add(new SavedWire
                    {
                        Entity = entity,
                        Name = entity.Name ?? "",
                        Wire = i,
                        Output = wires[i].Output,
                        Input = wires[i].Input,
                        Fired = wires[i].Fired,
                    });
                }
            }
            return list;
        }
        set => _loadedWires = value;
    }

    // A load: this resource is new (the one before it went with the world it belonged to). Every wire in
    // the world starts unfired and unresolved — a map's entities outlive a load, and what they counted
    // was the game being left — and then the save's counts and inputs are put back.
    public void AfterLoad(World world)
    {
        _world = world;
        _clockTick = -1;
        Clock(world);

        foreach (var entity in world.Query<IOConnections>().Entities)
        {
            var wires = entity.GetComponent<IOConnections>().Wires;
            if (wires == null) continue;
            foreach (var wire in wires)
            {
                wire.Fired = 0;
                wire.Resolved = default;
            }
        }

        if (_loadedWires != null)
        {
            foreach (var saved in _loadedWires)
            {
                if (saved == null) continue;
                var entity = !saved.Entity.IsNull && world.IsAlive(saved.Entity) ? saved.Entity : world.FindByName(saved.Name);
                if (entity.IsNull || !entity.TryGetComponent<IOConnections>(out var io) || io.Wires == null
                    || saved.Wire < 0 || saved.Wire >= io.Wires.Length
                    || !string.Equals(io.Wires[saved.Wire].Output, saved.Output, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(io.Wires[saved.Wire].Input, saved.Input, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn(LogCat.Save, $"entity_io: wire {saved.Wire} ({saved.Output} → {saved.Input}) of "
                                        + $"'{saved.Name}' is not in the world any more; how often it fired is dropped");
                    continue;
                }
                io.Wires[saved.Wire].Fired = Math.Max(0, saved.Fired);
            }
            _loadedWires = null;
        }

        _pending.Clear();
        if (_loadedPending != null)
        {
            foreach (var saved in _loadedPending)
            {
                if (saved == null || string.IsNullOrEmpty(saved.Input)) continue;
                _pending.Add(new Queued
                {
                    Due = _time + Math.Max(0.0, saved.Remaining),
                    Order = _order++,
                    Target = saved.Target,
                    Input = saved.Input,
                    Parameter = saved.Parameter ?? "",
                    Activator = saved.Activator,
                    Caller = saved.Caller,
                    TargetName = saved.TargetName ?? "",
                });
            }
            _loadedPending = null;
        }
    }
}

public static class EntityIOExtensions
{
    public static EntityIO IO(this World world) => world.Resources.Get<EntityIO>();

    // Fires an output on an entity, if it has any wires. The usual caller is a gameplay system, and it
    // should not have to care whether this world has entity I/O installed at all: `EntityIOModule` is a
    // default module a game can switch off, and a headless test world often has only physics.
    public static void FireOutput(this World world, Entity source, string output, Entity activator = default)
    {
        if (world.Resources.TryGet<EntityIO>(out var io) && io != null) io.Fire(world, source, output, activator);
    }

    // With a value, which a wire with no parameter of its own passes on (EntityIO.Fire; #92, #91).
    public static void FireOutput(this World world, Entity source, string output, Entity activator, string value)
    {
        if (world.Resources.TryGet<EntityIO>(out var io) && io != null) io.Fire(world, source, output, activator, value);
    }

    // With a number (`OnChanged` with the counter's value, `OnDamaged` with the damage done), and a
    // same-tick option (issue #91).
    [Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
    public static void FireOutput(this World world, Entity source, string output, Entity activator, float value, bool sameTick = false)
    {
        if (world.Resources.TryGet<EntityIO>(out var io) && io != null) io.Fire(world, source, output, activator, value, sameTick);
    }

    [Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
    public static void FireOutput(this World world, Entity source, string output, Entity activator, string? value, bool sameTick)
    {
        if (world.Resources.TryGet<EntityIO>(out var io) && io != null) io.Fire(world, source, output, activator, value, sameTick);
    }

    // Whether an entity has a wire for this output. What makes a door usable is that using it *does*
    // something, and this is how the interaction system asks (04 §3.4).
    public static bool HasOutput(this Entity entity, string output)
    {
        if (entity.IsNull || !entity.HasComponent<IOConnections>()) return false;
        var wires = entity.GetComponent<IOConnections>().Wires;
        if (wires == null) return false;

        foreach (var wire in wires)
            if (string.Equals(wire.Output, output, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // The first entity with this name, or none. A scan, because entity names are not indexed — which is
    // affordable only because connections are resolved once at load and this is the fallback (04 §3.4).
    public static Entity FindByName(this World world, string name)
    {
        if (string.IsNullOrEmpty(name)) return default;
        foreach (var entity in world.Query<Transform>().Entities)
            if (entity.Name == name) return entity;
        return default;
    }
}

// A number as an output's value (issue #91): invariant, shortest round-trip, and a whole number from
// -1024 to 1023 written once and kept, so a counter counting doors allocates nothing after the first.
internal static class IOValues
{
    private const int Low = -1024, High = 1023;
    private static readonly string?[] Whole = new string?[High - Low + 1];

    public static string Format(float value)
    {
        if (value >= Low && value <= High && value == MathF.Floor(value))
        {
            int i = (int)value - Low;
            return Whole[i] ??= ((int)value).ToString(CultureInfo.InvariantCulture);
        }
        return value.ToString("R", CultureInfo.InvariantCulture);
    }
}

// The dispatch phase, plus the two outputs the engine itself fires.
[System("sage.io.dispatch", Phase.EntityIO)]
internal sealed class EntityIOSystem : ISystem
{
    private readonly World _world;
    private readonly EntityInputs _inputs;
    private EntityIO _io;

    public EntityIOSystem(World world, EntityInputs inputs)
    {
        _world = world;
        _inputs = inputs;
        _io = world.Resources.Get<EntityIO>();
    }

    public void Run(in SystemContext ctx)
    {
        // A load replaces the resource (it is saved, issue #90): the new one keeps the cvars' settings.
        if (!_world.Resources.TryGet<EntityIO>(out var io) || io == null) return;
        if (!ReferenceEquals(io, _io))
        {
            io.AdoptSettings(_io);
            _io = io;
        }
        io.Run(_world, _inputs);
    }
}

// Physics triggers become `OnStartTouch` / `OnEndTouch`. A trigger volume in a map is the oldest level
// mechanism there is, and it costs one system to give it a wire (10 §3, 04 §3.4).
[System("sage.io.triggers", Phase.PostPhysics)]
internal sealed class TriggerOutputSystem : ISystem
{
    private readonly World _world;
    private readonly IPhysicsWorld _space;

    public TriggerOutputSystem(World world)
    {
        _world = world;
        _space = world.Resources.Get<IPhysicsWorld>();
    }

    public void Run(in SystemContext ctx)
    {
        foreach (var overlap in _space.TriggerEnter)
            _world.FireOutput(overlap.Trigger, "OnStartTouch", overlap.Other);
        foreach (var overlap in _space.TriggerExit)
            _world.FireOutput(overlap.Trigger, "OnEndTouch", overlap.Other);
    }
}

[Plugin("sage.gameplay.io", "0.1.0")]
public sealed class EntityIOModule : IModule
{
    private EntityInputs? _inputs;
    private CVar<bool>? _trace;
    private CVar<int>? _budget;

    public void Init(ModuleContext ctx)
    {
        _inputs = ctx.Engine.Inputs;
        ctx.Engine.Outputs.Declare("OnStartTouch", "Something entered this trigger volume (\"trigger\" \"1\" on a brush entity).");
        ctx.Engine.Outputs.Declare("OnEndTouch", "Something left this trigger volume.");

        // The inputs every game has. Anything that moves geometry is in `Movers`; anything about a
        // specific game's rules belongs to that game.
        _inputs.Register("Kill", (World world, in IOContext io) => world.Destroy(io.Self));

        _inputs.Register("Say", (World world, in IOContext io) =>
        {
            if (io.Parameter.Length > 0) world.Say(io.Parameter);
        });

        // Chaining: fire one of this entity's own outputs. The wire that a mapper draws from a trigger to
        // a "relay" entity and out again to five doors.
        _inputs.Register("Fire", (World world, in IOContext io) =>
        {
            if (io.Parameter.Length > 0) world.FireOutput(io.Self, io.Parameter, io.Activator);
        });

        _trace = ctx.Engine.CVars.Register("io_trace", false, CVarFlags.DevOnly,
            "Log every entity I/O dispatch: who fired what at whom (04 §3.4).");
        _budget = ctx.Engine.CVars.Register("io_maxdispatch", 256, CVarFlags.DevOnly,
            "How many inputs one tick may deliver before the rest are dropped.", 1, 100000);

        ctx.Engine.CVars.RegisterCommand("ent_fire", CVarFlags.Cheat,
            "ent_fire <name|!player|@group> <input> [parameter] [delay]: send an input to an entity by name, or to every member of a group.", a =>
        {
            if (a.Count < 2) { Log.Warn(LogCat.Console, "ent_fire <name|@group> <input> [parameter] [delay]"); return; }

            foreach (var world in ctx.Engine.Worlds)
            {
                if (!world.Resources.TryGet<EntityIO>(out var io) || io == null) continue;

                if (IOTargets.IsSelector(a[0]))
                {
                    if (IOTargets.Problem(ctx.Engine, a[0]) is { } problem) { Log.Warn(LogCat.Console, $"ent_fire: {problem}"); return; }
                    if (!ctx.Engine.Inputs.Has(a[1])) { Log.Warn(LogCat.Console, $"ent_fire: no input '{a[1]}' (see io_list)"); return; }
                    var members = new List<Entity>();
                    IOTargets.Members(world, a[0], members);
                    float wait = a.Count > 3 && float.TryParse(a[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float w) ? w : 0f;
                    io.FireInput(a[0], a[1], a.Count > 2 ? a[2] : "", wait);
                    Log.Info(LogCat.Console, $"fired {a[1]} at {a[0]} ({members.Count} member(s) now)");
                    return;
                }

                // `!player` because half of what you want to fire at from a console is the player, and
                // the help said so before this did (found by the second pass: an untrue help string is
                // the same bug as an untrue doc, and cheaper to write).
                var target = a[0].Equals("!player", StringComparison.OrdinalIgnoreCase)
                    ? FirstPlayer(world)
                    : world.FindByName(a[0]);

                if (target.IsNull) { Log.Warn(LogCat.Console, $"ent_fire: no entity named '{a[0]}'"); return; }
                if (!ctx.Engine.Inputs.Has(a[1])) { Log.Warn(LogCat.Console, $"ent_fire: no input '{a[1]}' (see io_list)"); return; }

                float delay = a.Count > 3 && float.TryParse(a[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float d) ? d : 0f;
                io.FireInput(target, a[1], a.Count > 2 ? a[2] : "", delay);
                Log.Info(LogCat.Console, $"fired {a[1]} at {World.Describe(target)}");
                return;
            }
        });

        ctx.Engine.CVars.RegisterCommand("io_history", CVarFlags.None,
            "io_history [name] [count]: the last inputs delivered (all, or to or from one entity), newest last, with what became of each.", a =>
        {
            int count = 20;
            string? name = null;
            for (int i = 0; i < a.Count; i++)
            {
                if (int.TryParse(a[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) count = Math.Max(1, n);
                else name = a[i];
            }
            // Every world that has delivered anything (the editor's play world beside the edit world).
            int shown = 0;
            foreach (var world in ctx.Engine.Worlds)
            {
                if (!world.Resources.TryGet<EntityIO>(out var io) || io == null || io.HistoryCount == 0) continue;
                var records = new List<IORecord>();
                if (name != null)
                {
                    var entity = world.FindByName(name);
                    if (entity.IsNull) continue;
                    io.HistoryOf(entity, records, count);
                    records.Reverse();
                }
                else
                    for (int i = Math.Max(0, io.HistoryCount - count); i < io.HistoryCount; i++) records.Add(io.HistoryAt(i));
                Log.Info(LogCat.Console, $"world '{world.Name}': {records.Count} of {io.HistoryCount} kept (the last {EntityIO.HistoryCapacity})");
                foreach (var r in records) Log.Info(LogCat.Console, "  " + EntityIO.Describe(r));
                shown++;
            }
            if (shown == 0) Log.Info(LogCat.Console, name != null ? $"io_history: nothing sent to or from '{name}' yet" : "io_history: no input delivered yet");
        });

        ctx.Engine.CVars.RegisterCommand("io_list", CVarFlags.None, "Every entity input and output this game has.", _ =>
        {
            var names = new List<string>(ctx.Engine.Inputs.Names);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                // A name some components take says which (issue #91): `Toggle  sage:mover, sage:logic_branch`.
                var components = ctx.Engine.Inputs.ComponentsTaking(name);
                if (components.Count == 0) Log.Info(LogCat.Console, $"  {name}");
                else Log.Info(LogCat.Console, $"  {name,-14} {string.Join(", ", components)}"
                                            + (ctx.Engine.Inputs.HasGlobal(name) ? " (and any entity)" : ""));
            }
            Log.Info(LogCat.Console, $"{names.Count} input(s)");
            var outputs = new List<string>(ctx.Engine.Outputs.Names);
            outputs.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var name in outputs) Log.Info(LogCat.Console, $"  {name,-14} {ctx.Engine.Outputs.Describe(name)}");
            Log.Info(LogCat.Console, $"{outputs.Count} output(s)");
        });
    }

    private static Entity FirstPlayer(World world)
    {
        foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities)
            return entity;
        return default;
    }

    public void OnWorldCreated(World world)
    {
        var io = new EntityIO();
        io.Bind(world);
        world.Resources.Add(io);
        if (_trace != null) io.Trace = _trace.Value;
        if (_budget != null) io.Budget = _budget.Value;
        // The world's resource at the time, not this one: a load replaces it (issue #90).
        if (_trace != null) _trace.Changed += _ => { if (world.Resources.TryGet<EntityIO>(out var now) && now != null) now.Trace = _trace.Value; };
        if (_budget != null) _budget.Changed += _ => { if (world.Resources.TryGet<EntityIO>(out var now) && now != null) now.Budget = _budget.Value; };

        world.AddSystem(new TriggerOutputSystem(world));
        world.AddSystem(new EntityIOSystem(world, _inputs!));
    }
}
