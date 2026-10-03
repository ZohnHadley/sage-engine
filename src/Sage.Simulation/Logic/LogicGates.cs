#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Sage.Simulation;

// The rest of the logic set (issue #281): what Half-Life and Source maps lean on beside relays and
// counters (LogicEntities.cs). Each is a component with a part and an engine prefab, owned by the engine
// (`sage.core`) like the others, its inputs routed by component.
//
//   prefab                 component              inputs                                  outputs
//   sage:logic_multisource sage:logic_multisource SetSource n, ClearSource n,             OnAllSet, OnNotAllSet,
//                                                 ToggleSource n, Reset, Test             OnTrue, OnFalse
//   sage:logic_case        sage:logic_case        InValue v, PickRandom,                  OnCase01 … OnCase16,
//                                                 PickRandomShuffle                       OnDefault
//   sage:logic_auto        sage:logic_auto        —                                       OnMapSpawn
//   (a brush or body)      sage:trigger           Enable, Disable, Toggle                 OnTrigger
//
// **multisource** (HL1): an AND gate of numbered sources, 1 to `sources` (at most 32). OnAllSet the moment
// the last is set, OnNotAllSet the moment one is cleared after that; Test says which it is now. Three
// buttons that must all be held, two generators that must both run.
//
// **logic_case** (Source): `InValue` compares its parameter with the `cases` (as text, ignoring case, or
// as numbers) and fires OnCaseNN for the first that matches, else OnDefault; each hands the value on.
// `PickRandom` fires one of OnCase01..NN at random (NN: `choices`, or the number of cases), and
// `PickRandomShuffle` the same without repeats until every one has been picked. The random stream is the
// entity's own and saved (Timers.Random01), so a run and a load pick what they would have.
//
// **logic_auto** (Source): OnMapSpawn once, on the first tick after it is placed; a load does not fire it
// again (whether it fired is saved). A level's "when this starts" without a trigger at the door.
//
// **trigger** (HL1's trigger_once and trigger_multiple): a filter on a trigger volume (the `body` part's
// `"trigger": true`). Something entering it that passes `requires` (asked of what entered, as subject, and
// the trigger, as other) fires OnTrigger with it as the activator; then, with `once`, the trigger
// disables itself (Enable re-arms it), else it ignores entries for `wait` seconds. OnStartTouch and
// OnEndTouch still fire for every entry and exit, filtered or not. It fires on entering, not for as long
// as something stays inside.
//
// Saved like the rest: the sources set, the random stream and the deck dealt, whether an auto fired, a
// trigger's spent or cooling state. What they are given as content (cases, a trigger's requires) sits in
// [Transient] components their parts put back on a load's respawn.

// ---- multisource -----------------------------------------------------------------------------------

[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[Component("sage:logic_multisource")]
public struct LogicMultisource : IComponent
{
    [Property(Min = 1, Max = 32, Tooltip = "How many sources must be set: numbered 1 to this")]
    public int Sources;
    [Property(Tooltip = "Which sources are set now, one bit each (source 1 is the lowest)")]
    public uint Set;
    [Property(Tooltip = "Every source is set (OnAllSet has fired, OnNotAllSet not since)")]
    public bool Complete;

    public readonly uint All => Sources >= 32 ? uint.MaxValue : (1u << Math.Max(Sources, 1)) - 1u;
}

// "logic_multisource": { "sources": 3 }
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[PrefabPart("logic_multisource", Plugin = RegistrationOwners.Core, Shorthand = nameof(Sources))]
public sealed class LogicMultisourcePart : IPrefabPart
{
    [Property(Min = 1, Max = 32, Tooltip = "How many sources must be set: numbered 1 to this")]
    public int Sources = 2;

    public void Apply(in PrefabPartContext ctx)
    {
        int sources = Math.Clamp(Sources, 1, 32);
        if (sources != Sources) ctx.Warn($"sources {Sources} is not 1 to 32; {sources} is used");
        ctx.World.Add(ctx.Entity, new LogicMultisource { Sources = sources });
    }
}

// ---- case ------------------------------------------------------------------------------------------

[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[Component("sage:logic_case")]
public struct LogicCase : IComponent
{
    // The random stream's state (xorshift32, Timers.Random01); 0 = from the entity. Saved.
    public uint Random;
    // PickRandomShuffle's picks since the deck was last full, one bit each. Saved.
    public uint Dealt;
}

// What a case compares with and picks among: content, put there by its part and back by a load's respawn.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[Transient]
[Component("sage:logic_case_script")]
public struct LogicCaseScript : IComponent
{
    public string[]? Cases;
    public int Choices;
}

// "logic_case": { "cases": ["red", "green", "blue"], "choices": 0, "seed": 0 }
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[PrefabPart("logic_case", Plugin = RegistrationOwners.Core, Shorthand = nameof(Cases))]
public sealed class LogicCasePart : IPrefabPart
{
    [Property(Tooltip = "What InValue compares with, in order: a match with the first fires OnCase01, and so on (at most 16)")]
    public List<string> Cases = new();
    [Property(Min = 0, Max = 16, Tooltip = "How many OnCase outputs PickRandom chooses among; 0 = one per case")]
    public int Choices;
    [Property(Tooltip = "Where the random stream starts; 0 = from the entity")]
    public uint Seed;

    public void Apply(in PrefabPartContext ctx)
    {
        int count = Cases.Count;
        if (count > LogicGates.MaxCases)
        {
            ctx.Warn($"{count} cases: at most {LogicGates.MaxCases}; the rest are ignored");
            count = LogicGates.MaxCases;
        }
        int choices = Math.Clamp(Choices, 0, LogicGates.MaxCases);
        if (choices != Choices) ctx.Warn($"choices {Choices} is not 0 to {LogicGates.MaxCases}; {choices} is used");
        var cases = new string[count];
        for (int i = 0; i < count; i++) cases[i] = Cases[i] ?? "";
        ctx.World.Add(ctx.Entity, new LogicCase { Random = Seed });
        ctx.World.Add(ctx.Entity, new LogicCaseScript { Cases = cases, Choices = choices });
    }
}

// ---- auto ------------------------------------------------------------------------------------------

[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[Component("sage:logic_auto")]
public struct LogicAuto : IComponent
{
    [Property(Tooltip = "OnMapSpawn has fired (saved, so a load does not fire it again)")]
    public bool Fired;
}

// "logic_auto": { }
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[PrefabPart("logic_auto", Plugin = RegistrationOwners.Core)]
public sealed class LogicAutoPart : IPrefabPart
{
    public void Apply(in PrefabPartContext ctx) => ctx.World.Add(ctx.Entity, new LogicAuto());
}

// ---- trigger ---------------------------------------------------------------------------------------

[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[Component("sage:trigger")]
public struct LogicTrigger : IComponent
{
    [Property(Tooltip = "Fire once, then disable itself (trigger_once); off: again after `wait` (trigger_multiple)")]
    public bool Once;
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds after firing before it fires again (not with once)")]
    public float Wait;
    [Property(Tooltip = "Ignores what enters it until Enable (and after firing, with once)")]
    public bool Disabled;
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds left before it may fire again")]
    public float Cooldown;
}

// A trigger's filter: content, put there by its part and back by a load's respawn, never saved.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[Transient]
[Component("sage:trigger_script")]
public struct LogicTriggerScript : IComponent
{
    public ICondition? Requires;
}

// "trigger": { "once": true, "wait": 0, "requires": { "var": "alarm", "eq": 0 }, "startDisabled": false }
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[PrefabPart("trigger", Plugin = RegistrationOwners.Core)]
public sealed class LogicTriggerPart : IPrefabPart
{
    [Property(Tooltip = "Fire once, then disable itself (trigger_once); off: again after `wait` (trigger_multiple)")]
    public bool Once;
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds after firing before it fires again (not with once)")]
    public float Wait;
    [Property(Tooltip = "Only what this holds of fires it, asked of what entered (subject) and the trigger (other)")]
    public ICondition? Requires;
    [Property(Tooltip = "Starts disabled: nothing fires it until Enable")]
    public bool StartDisabled;

    public void Apply(in PrefabPartContext ctx)
    {
        ctx.World.Add(ctx.Entity, new LogicTrigger { Once = Once, Wait = Timers.Seconds(Wait, 0f, "wait", ctx), Disabled = StartDisabled });
        if (Requires != null) ctx.World.Add(ctx.Entity, new LogicTriggerScript { Requires = Requires });
    }
}

// ---- the inputs ------------------------------------------------------------------------------------

[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
public static class LogicGates
{
    public const string OnAllSet = "OnAllSet";
    public const string OnNotAllSet = "OnNotAllSet";
    public const string OnDefault = "OnDefault";
    public const string OnMapSpawn = "OnMapSpawn";
    public const int MaxCases = 16;

    // OnCase01 … OnCase16, written once so firing one allocates nothing.
    private static readonly string[] CaseOutputs = MakeCaseOutputs();

    private static string[] MakeCaseOutputs()
    {
        var names = new string[MaxCases];
        for (int i = 0; i < names.Length; i++) names[i] = "OnCase" + (i + 1).ToString("00", CultureInfo.InvariantCulture);
        return names;
    }

    // The output for case `index` (0-based): OnCase01 for 0.
    public static string CaseOutput(int index) => CaseOutputs[index];

    internal static void Register(Engine engine)
    {
        var inputs = engine.Inputs;
        var outputs = engine.Outputs;

        // multisource
        inputs.Register<LogicMultisource>("SetSource", static (World world, in IOContext io) => Source(world, io, "SetSource", +1));
        inputs.Register<LogicMultisource>("ClearSource", static (World world, in IOContext io) => Source(world, io, "ClearSource", -1));
        inputs.Register<LogicMultisource>("ToggleSource", static (World world, in IOContext io) => Source(world, io, "ToggleSource", 0));
        inputs.Register<LogicMultisource>("Reset", static (World world, in IOContext io) =>
        {
            world.Get<LogicMultisource>(io.Self).Set = 0;
            Settle(world, io);
        });
        inputs.Register<LogicMultisource>("Test", static (World world, in IOContext io) =>
        {
            ref var gate = ref world.Get<LogicMultisource>(io.Self);
            world.FireOutput(io.Self, (gate.Set & gate.All) == gate.All ? LogicEntities.OnTrue : LogicEntities.OnFalse, io.Activator);
        });
        outputs.Declare(OnAllSet, "Every source of this multisource is set now (sage:logic_multisource); hands on how many.");
        outputs.Declare(OnNotAllSet, "A source of this multisource was cleared after every one was set (sage:logic_multisource).");

        // case
        inputs.Register<LogicCase>("InValue", static (World world, in IOContext io) => InValue(world, io));
        inputs.Register<LogicCase>("PickRandom", static (World world, in IOContext io) => Pick(world, io, shuffle: false));
        inputs.Register<LogicCase>("PickRandomShuffle", static (World world, in IOContext io) => Pick(world, io, shuffle: true));
        for (int i = 0; i < MaxCases; i++)
            outputs.Declare(CaseOutputs[i], $"InValue matched case {i + 1} of this logic_case, or PickRandom picked it (sage:logic_case); InValue hands the value on.");
        outputs.Declare(OnDefault, "InValue matched none of this logic_case's cases (sage:logic_case); hands the value on.");

        // auto
        outputs.Declare(OnMapSpawn, "This logic_auto was placed: once, on the first tick after (sage:logic_auto).");

        // trigger (OnTrigger is the relay's, declared by LogicEntities)
        inputs.Register<LogicTrigger>("Enable", static (World world, in IOContext io) => world.Get<LogicTrigger>(io.Self).Disabled = false);
        inputs.Register<LogicTrigger>("Disable", static (World world, in IOContext io) => world.Get<LogicTrigger>(io.Self).Disabled = true);
        inputs.Register<LogicTrigger>("Toggle", static (World world, in IOContext io) =>
        {
            ref var trigger = ref world.Get<LogicTrigger>(io.Self);
            trigger.Disabled = !trigger.Disabled;
        });
    }

    // ---- multisource

    // `change` +1 sets, -1 clears, 0 toggles source n (the parameter, from 1).
    private static void Source(World world, in IOContext io, string input, int change)
    {
        ref var gate = ref world.Get<LogicMultisource>(io.Self);
        if (!int.TryParse(io.Parameter, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > gate.Sources)
        {
            Log.Warn(LogCat.Events, $"I/O: {input}({io.Parameter}) at {World.Describe(io.Self)}: the parameter is a source, 1 to {gate.Sources}");
            return;
        }
        uint bit = 1u << (n - 1);
        gate.Set = change > 0 ? gate.Set | bit : change < 0 ? gate.Set & ~bit : gate.Set ^ bit;
        Settle(world, io);
    }

    // Fires on the edges: OnAllSet when it becomes complete, OnNotAllSet when it stops being.
    private static void Settle(World world, in IOContext io)
    {
        ref var gate = ref world.Get<LogicMultisource>(io.Self);
        bool complete = (gate.Set & gate.All) == gate.All;
        if (complete == gate.Complete) return;
        gate.Complete = complete;
        if (complete) world.FireOutput(io.Self, OnAllSet, io.Activator, gate.Sources);
        else world.FireOutput(io.Self, OnNotAllSet, io.Activator);
    }

    // ---- case

    private static void InValue(World world, in IOContext io)
    {
        var cases = world.TryGet<LogicCaseScript>(io.Self, out var script) ? script.Cases : null;
        if (cases != null)
            for (int i = 0; i < cases.Length; i++)
                if (Matches(cases[i], io.Parameter))
                {
                    world.FireOutput(io.Self, CaseOutputs[i], io.Activator, io.Parameter);
                    return;
                }
        world.FireOutput(io.Self, OnDefault, io.Activator, io.Parameter);
    }

    // As text ignoring case, or as numbers (so "3" matches "3.0").
    internal static bool Matches(string @case, string value)
    {
        var a = @case.AsSpan().Trim();
        var b = value.AsSpan().Trim();
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
        return float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            && float.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
            && MathF.Abs(x - y) <= LogicEntities.Tolerance * MathF.Max(1f, MathF.Max(MathF.Abs(x), MathF.Abs(y)));
    }

    private static void Pick(World world, in IOContext io, bool shuffle)
    {
        int count = world.TryGet<LogicCaseScript>(io.Self, out var script)
            ? (script.Choices > 0 ? script.Choices : script.Cases?.Length ?? 0)
            : 0;
        count = Math.Min(count, MaxCases);
        if (count == 0)
        {
            Log.Warn(LogCat.Events, $"I/O: {(shuffle ? "PickRandomShuffle" : "PickRandom")} at {World.Describe(io.Self)}: "
                                  + "it has no cases and no choices to pick among");
            return;
        }
        ref var logic = ref world.Get<LogicCase>(io.Self);
        int pick;
        if (!shuffle) pick = Math.Min((int)(Timers.Random01(ref logic.Random, io.Self) * count), count - 1);
        else
        {
            uint full = (1u << count) - 1u;
            if ((logic.Dealt & full) == full) logic.Dealt = 0;          // every one picked: a fresh deck
            int left = count - System.Numerics.BitOperations.PopCount(logic.Dealt & full);
            int nth = Math.Min((int)(Timers.Random01(ref logic.Random, io.Self) * left), left - 1);
            pick = 0;
            for (int i = 0; i < count; i++)
            {
                if ((logic.Dealt & (1u << i)) != 0) continue;
                if (nth-- == 0) { pick = i; break; }
            }
            logic.Dealt |= 1u << pick;
        }
        world.FireOutput(io.Self, CaseOutputs[pick], io.Activator);
    }

    // ---- trigger

    // Something entered a trigger volume that has a `sage:trigger` (TriggerOutputSystem, after its
    // OnStartTouch): OnTrigger if it is armed and `requires` holds of what entered.
    internal static void Touched(World world, Entity trigger, Entity other)
    {
        ref var filter = ref world.Get<LogicTrigger>(trigger);
        if (filter.Disabled || filter.Cooldown > 0f) return;
        if (world.TryGet<LogicTriggerScript>(trigger, out var script) && script.Requires != null
            && !script.Requires.Test(new ConditionContext(world, other, trigger), out _)) return;
        if (filter.Once) filter.Disabled = true;
        else filter.Cooldown = filter.Wait;
        world.FireOutput(trigger, LogicEntities.OnTrigger, other);
    }
}

// EntityIO phase, before the dispatch: logic_auto's OnMapSpawn (so its undelayed wires arrive the same
// tick) and the triggers' waits counting down. Allocation-free.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[System(Id, Phase.EntityIO, Before = new[] { "?sage.io.dispatch" })]
internal sealed class LogicGateSystem : ISystem
{
    public const string Id = "sage.logic.gates";

    private readonly World _world;
    private readonly Query<LogicAuto> _autos;
    private readonly Query<LogicTrigger> _triggers;

    public LogicGateSystem(World world)
    {
        _world = world;
        _autos = world.Query<LogicAuto>();
        _triggers = world.Query<LogicTrigger>();
    }

    public void Run(in SystemContext ctx)
    {
        foreach (var (autos, entities) in _autos.Chunks)
        {
            var a = autos.Span;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].Fired) continue;
                a[i].Fired = true;
                _world.FireOutput(entities.EntityAt(i), LogicGates.OnMapSpawn);
            }
        }

        float dt = ctx.Tick.Dt;
        if (dt <= 0f) return;
        foreach (var (triggers, _) in _triggers.Chunks)
        {
            var t = triggers.Span;
            for (int i = 0; i < t.Length; i++)
                if (t[i].Cooldown > 0f) t[i].Cooldown = MathF.Max(0f, t[i].Cooldown - dt);
        }
    }
}
