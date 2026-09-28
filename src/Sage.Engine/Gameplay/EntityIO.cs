#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Friflo.Engine.ECS;

namespace sage_engine;

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
public sealed class EntityInputs
{
    private readonly Dictionary<string, EntityInput> _inputs = new(StringComparer.OrdinalIgnoreCase);

    // Closed when the first world exists (SageApp.CreateWorld): its level's wiring was checked without it.
    public RegistrationSeal Seal { get; } = new("entity input", "a level's wiring may already have been checked without it");

    // Who registered each input (issue #12); set by the Engine.
    public RegistrationLedger? Ledger { get; set; }

    public void Register(string name, EntityInput handler)
    {
        Seal.Check(name);
        if (!_inputs.TryAdd(name, handler))
            Assert.Ensure(false, $"Entity input '{name}' is registered twice" +
                                 (Ledger?.OwnerOf("entity input", name) is { } first ? $" (first by {first})" : ""));
        else
            Ledger?.Record("entity input", name);
    }

    public bool Has(string name) => _inputs.ContainsKey(name);
    public bool TryGet(string name, out EntityInput handler) => _inputs.TryGetValue(name, out handler!);
    public IEnumerable<string> Names => _inputs.Keys;
}

// One wire: "when this entity fires `Output`, send `Input` to `Target` after `Delay` seconds".
public sealed class Connection
{
    public string Output = "";
    public string Target = "";              // a name, or !self / !activator / !caller
    public string Input = "";
    public string Parameter = "";
    public float Delay;
    public int Times = -1;                  // -1: as often as it fires

    // Filled in at load. A handle rather than a name lookup per fire — but the name is kept, because an
    // entity that is spawned later (or respawned) has to be found again (04 §3.4, "late binding").
    internal Entity Resolved;
    internal int Fired;
}

public struct IOConnections : IComponent
{
    public Connection[] Wires;
}

// The queue, per world. Firing an output puts deliveries in it; the `EntityIO` phase takes them out.
public sealed class EntityIO
{
    private struct Pending
    {
        public double Due;
        public long Order;                  // ties broken by when it was queued, so a tick is deterministic
        public Entity Target;
        public string Input;
        public string Parameter;
        public Entity Activator;
        public Entity Caller;
        public string TargetName;           // for late binding when the handle is dead
    }

    private readonly List<Pending> _pending = new();
    private readonly List<Pending> _due = new();
    private long _order;
    private double _time;
    private bool _warnedBudget;

    public int PendingCount => _pending.Count;
    public int DispatchedLastTick { get; private set; }

    // How many inputs one tick may deliver. A wire that fires itself is a level bug, not an engine one,
    // but it must cost a warning rather than the process (04 §3.4).
    public int Budget = 256;
    public bool Trace;

    // Fires an output: every wire on the entity with that name queues its input.
    public void Fire(World world, Entity source, string output, Entity activator = default)
    {
        if (source.IsNull || !source.HasComponent<IOConnections>()) return;

        var wires = source.GetComponent<IOConnections>().Wires;
        if (wires == null) return;

        foreach (var wire in wires)
        {
            if (!string.Equals(wire.Output, output, StringComparison.OrdinalIgnoreCase)) continue;
            if (wire.Times >= 0 && wire.Fired >= wire.Times) continue;
            wire.Fired++;

            var target = Resolve(world, wire, source, activator);
            Queue(target, wire.Input, wire.Parameter, wire.Delay, activator, source, wire.Target);
        }
    }

    // Fires one input directly, from code or the console.
    public void FireInput(Entity target, string input, string parameter = "", float delay = 0f,
                          Entity activator = default, Entity caller = default) =>
        Queue(target, input, parameter, delay, activator, caller, "");

    private void Queue(Entity target, string input, string parameter, float delay,
                       Entity activator, Entity caller, string targetName)
    {
        _pending.Add(new Pending
        {
            Due = _time + Math.Max(0f, delay),
            Order = _order++,
            Target = target,
            Input = input,
            Parameter = parameter ?? "",
            Activator = activator,
            Caller = caller,
            TargetName = targetName,
        });
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

        if (!wire.Resolved.IsNull && world.IsAlive(wire.Resolved)) return wire.Resolved;
        return wire.Resolved = world.FindByName(wire.Target);
    }

    // Called by the dispatch system once a tick.
    internal void Run(World world, float dt, EntityInputs inputs)
    {
        _time += dt;
        DispatchedLastTick = 0;
        if (_pending.Count == 0) return;

        // Everything due this tick, oldest first. Taken out of the list before dispatching, because an
        // input may fire more outputs and those belong to the *next* pass, not this one — which is what
        // stops one tick from running a chain to its end and makes a delay of 0 still mean "next tick".
        _due.Clear();
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            if (_pending[i].Due > _time) continue;
            _due.Add(_pending[i]);
            _pending.RemoveAt(i);
        }
        if (_due.Count == 0) return;

        _due.Sort(static (a, b) => a.Due != b.Due ? a.Due.CompareTo(b.Due) : a.Order.CompareTo(b.Order));

        foreach (var pending in _due)
        {
            if (DispatchedLastTick >= Budget)
            {
                if (!_warnedBudget)
                {
                    _warnedBudget = true;
                    Log.Warn(LogCat.Events, $"Entity I/O: more than {Budget} inputs in one tick; the rest are dropped "
                                          + "(a wire that fires itself?). `io_trace 1` shows the chain.");
                }
                break;
            }

            var target = pending.Target;
            if ((target.IsNull || !world.IsAlive(target)) && pending.TargetName.Length > 0)
                target = world.FindByName(pending.TargetName);       // late binding (04 §3.4)

            if (target.IsNull || !world.IsAlive(target))
            {
                Log.Debug(LogCat.Events, $"I/O: '{pending.Input}' had no target"
                                       + (pending.TargetName.Length > 0 ? $" named '{pending.TargetName}'" : ""));
                continue;
            }

            if (!inputs.TryGet(pending.Input, out var handler))
            {
                // Logged at load too, but a `ent_fire` typo arrives here.
                Log.Warn(LogCat.Events, $"I/O: no input called '{pending.Input}' ({World.Describe(target)})");
                continue;
            }

            if (Trace)
                Log.Info(LogCat.Events, $"I/O: {World.Describe(pending.Caller)} → {World.Describe(target)}."
                                      + $"{pending.Input}({pending.Parameter})");

            DispatchedLastTick++;
            handler(world, new IOContext
            {
                Self = target,
                Activator = pending.Activator,
                Caller = pending.Caller,
                Parameter = pending.Parameter,
            });
        }
    }

    internal void Clear()
    {
        _pending.Clear();
        _due.Clear();
        _warnedBudget = false;
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
            if (entity.Name.value == name) return entity;
        return default;
    }
}

// The dispatch phase, plus the two outputs the engine itself fires.
[System("sage.io.dispatch", Phase.EntityIO)]
internal sealed class EntityIOSystem : ISystem
{
    private readonly World _world;
    private readonly EntityInputs _inputs;
    private readonly EntityIO _io;

    public EntityIOSystem(World world, EntityInputs inputs)
    {
        _world = world;
        _inputs = inputs;
        _io = world.Resources.Get<EntityIO>();
    }

    public void Run(in SystemContext ctx) => _io.Run(_world, ctx.Tick.Dt, _inputs);
}

// Physics triggers become `OnStartTouch` / `OnEndTouch`. A trigger volume in a map is the oldest level
// mechanism there is, and it costs one system to give it a wire (10 §3, 04 §3.4).
[System("sage.io.triggers", Phase.PostPhysics)]
internal sealed class TriggerOutputSystem : ISystem
{
    private readonly World _world;
    private readonly PhysicsSpace _space;

    public TriggerOutputSystem(World world)
    {
        _world = world;
        _space = world.Resources.Get<PhysicsSpace>();
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
            "ent_fire <name|!player> <input> [parameter] [delay]: send an input to an entity by name.", a =>
        {
            if (a.Count < 2) { Log.Warn(LogCat.Console, "ent_fire <name> <input> [parameter] [delay]"); return; }

            foreach (var world in ctx.Engine.Worlds)
            {
                if (!world.Resources.TryGet<EntityIO>(out var io) || io == null) continue;

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

        ctx.Engine.CVars.RegisterCommand("io_list", CVarFlags.None, "Every entity input this game has.", _ =>
        {
            var names = new List<string>(ctx.Engine.Inputs.Names);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names) Log.Info(LogCat.Console, $"  {name}");
            Log.Info(LogCat.Console, $"{names.Count} input(s)");
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
        world.Resources.Add(io);
        if (_trace != null) io.Trace = _trace.Value;
        if (_budget != null) io.Budget = _budget.Value;
        if (_trace != null) _trace.Changed += _ => io.Trace = _trace.Value;
        if (_budget != null) _budget.Changed += _ => io.Budget = _budget.Value;

        world.AddSystem(new TriggerOutputSystem(world));
        world.AddSystem(new EntityIOSystem(world, _inputs!));
    }
}
