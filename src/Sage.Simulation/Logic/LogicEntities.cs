#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Sage.Simulation;

// Logic entities (issue #91, REDESIGN §4.3 stage 3): Half-Life's relays, counters, comparisons and
// branches, as components with parts and engine prefabs. What a level's sequence needs between "the
// player walked in" and "the door opens": wait for three levers, only if the alarm is off, then open.
//
//   prefab           component              inputs                                         outputs
//   sage:logic_relay sage:logic_relay       Trigger, Enable, Disable, Toggle               OnTrigger
//   sage:logic_counter sage:logic_counter   Add, Subtract, SetValue, Reset, GetValue,      OnChanged, OnHitMax, OnHitMin,
//                                           Enable, Disable                                OnGetValue
//   sage:logic_compare sage:logic_compare   SetValue, SetValueCompare, SetCompareValue,    OnEqual, OnNotEqual, OnLess,
//                                           Compare                                        OnGreater
//   sage:logic_branch sage:logic_branch     SetValue, SetValueTest, Toggle, ToggleTest,    OnTrue, OnFalse
//                                           Test
//   sage:math_remap  sage:math_remap        SetValue                                       OnValue
//
// **The short names are routed** (EntityInputs.Register<T>): `SetValue` means a counter's on a counter
// and a branch's on a branch, `Toggle` a branch's here and a mover's on a door. An entity with two of
// these components hears an input both take on both.
//
// **Values travel.** An output with a value — a counter's `OnChanged`, a remap's `OnValue` — hands it
// to a wire that has no parameter of its own, so `OnChanged → gate_check.SetValueCompare` compares the
// count. Whole numbers are written without allocating (IOValues).
//
// **Saved.** Each is a component, so a save carries it: a counter at 2 of 3 is at 2 of 3 after a load
// (test: ACountersStateSurvivesASave). A relay's `requires` and `then` are content, not state: they sit
// in a [Transient] component its part puts there, which a load's respawn puts back.
//
// Owned by the engine (`sage.core`), like timers: a data-only game has them whatever plugins it lists;
// their outputs reach wires when the entity I/O plugin is on.

// ---- relay ----------------------------------------------------------------------------------------

// A relay: one input in, a condition, some actions and one output out. The wire a mapper draws from a
// trigger to a relay and out again to five doors, or the one that says "only if the alarm is off".
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[Component("sage:logic_relay")]
public struct LogicRelay : IComponent
{
    [Property(Tooltip = "Ignores Trigger until Enable")]
    public bool Disabled;
    [Property(Tooltip = "OnTrigger's wires with no delay arrive in the same tick, not the next")]
    public bool SameTick;
}

// What a relay asks and does: content, put there by its part and back by a load's respawn, never saved.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[Transient]
[Component("sage:logic_relay_script")]
public struct LogicRelayScript : IComponent
{
    public ICondition? Requires;
    public List<IAction>? Then;
}

// "logic_relay": { "requires": { … }, "then": [ … ], "startDisabled": false, "sameTick": false }
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[PrefabPart("logic_relay", Plugin = RegistrationOwners.Core)]
public sealed class LogicRelayPart : IPrefabPart
{
    [Property(Tooltip = "Trigger does nothing unless this holds, asked of the activator (subject) and the relay (other)")]
    public ICondition? Requires;
    [Property(Tooltip = "Done on Trigger, before OnTrigger fires: the activator is the subject, the relay the other")]
    public List<IAction> Then = new();
    [Property(Tooltip = "Starts disabled: Trigger does nothing until Enable")]
    public bool StartDisabled;
    [Property(Tooltip = "OnTrigger's wires with no delay arrive in the same tick rather than the next")]
    public bool SameTick;

    public void Apply(in PrefabPartContext ctx)
    {
        ctx.World.Add(ctx.Entity, new LogicRelay { Disabled = StartDisabled, SameTick = SameTick });
        if (Requires != null || Then.Count > 0)
            ctx.World.Add(ctx.Entity, new LogicRelayScript { Requires = Requires, Then = Then });
    }
}

// ---- counter --------------------------------------------------------------------------------------

// A number that goes up and down: "three levers pulled", "five guards dead". Between `Min` and `Max`
// when they are set (Max > Min); with both 0 it has no limits, as Source's math_counter.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[Component("sage:logic_counter")]
public struct LogicCounter : IComponent
{
    [Property(Tooltip = "The count now")]
    public float Value;
    [Property(Tooltip = "Where Reset puts it")]
    public float Start;
    [Property(Tooltip = "The lowest it goes; OnHitMin when it gets there (limits only when max > min)")]
    public float Min;
    [Property(Tooltip = "The highest it goes; OnHitMax when it gets there (limits only when max > min)")]
    public float Max;
    [Property(Tooltip = "Ignores Add, Subtract, SetValue and Reset until Enable")]
    public bool Disabled;

    // Whether it has limits: only when max is above min (both 0, the default, is none).
    public readonly bool IsLimited() => Max > Min;
}

// "logic_counter": { "start": 0, "min": 0, "max": 3, "startDisabled": false }
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[PrefabPart("logic_counter", Plugin = RegistrationOwners.Core)]
public sealed class LogicCounterPart : IPrefabPart
{
    [Property(Tooltip = "The count it starts at, and where Reset puts it")]
    public float Start;
    [Property(Tooltip = "The lowest it goes; OnHitMin when it gets there (limits only when max > min)")]
    public float Min;
    [Property(Tooltip = "The highest it goes; OnHitMax when it gets there (limits only when max > min)")]
    public float Max;
    [Property(Tooltip = "Starts disabled: nothing changes it until Enable")]
    public bool StartDisabled;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Max < Min) ctx.Warn($"max {Max} is below min {Min}: the counter has no limits");
        var counter = new LogicCounter { Start = Start, Min = Min, Max = Max, Disabled = StartDisabled };
        counter.Value = counter.IsLimited() ? Math.Clamp(Start, Min, Max) : Start;
        ctx.World.Add(ctx.Entity, counter);
    }
}

// ---- compare --------------------------------------------------------------------------------------

// Two numbers and which is bigger: "is the counter at 3?" Equal within a hundred-thousandth, so a
// value that went through a remap still compares equal to the number a mapper typed.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[Component("sage:logic_compare")]
public struct LogicCompare : IComponent
{
    [Property(Tooltip = "The value compared (SetValue, SetValueCompare)")]
    public float Value;
    [Property(Tooltip = "What it is compared with (SetCompareValue)")]
    public float CompareValue;
}

// "logic_compare": { "value": 0, "compareValue": 3 }
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[PrefabPart("logic_compare", Plugin = RegistrationOwners.Core)]
public sealed class LogicComparePart : IPrefabPart
{
    [Property(Tooltip = "The value it starts with")]
    public float Value;
    [Property(Tooltip = "What the value is compared with")]
    public float CompareValue;

    public void Apply(in PrefabPartContext ctx) =>
        ctx.World.Add(ctx.Entity, new LogicCompare { Value = Value, CompareValue = CompareValue });
}

// ---- branch ---------------------------------------------------------------------------------------

// A remembered yes or no: "is the generator on?", tested when something needs to know.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[Component("sage:logic_branch")]
public struct LogicBranch : IComponent
{
    [Property(Tooltip = "True or false; Test fires OnTrue or OnFalse by it")]
    public bool Value;
}

// "logic_branch": { "value": false }
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[PrefabPart("logic_branch", Plugin = RegistrationOwners.Core)]
public sealed class LogicBranchPart : IPrefabPart
{
    [Property(Tooltip = "True or false to start with")]
    public bool Value;

    public void Apply(in PrefabPartContext ctx) => ctx.World.Add(ctx.Entity, new LogicBranch { Value = Value });
}

// ---- math_remap -----------------------------------------------------------------------------------

// A number from one range to another, along an ease: a lever's 0..1 to a light's 0..255, a counter's
// 0..5 to a lift's height.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[Component("sage:math_remap")]
public struct MathRemap : IComponent
{
    [Property(Tooltip = "The input range's start: maps to outMin")]
    public float InMin;
    [Property(Tooltip = "The input range's end: maps to outMax")]
    public float InMax;
    [Property(Tooltip = "What inMin becomes")]
    public float OutMin;
    [Property(Tooltip = "What inMax becomes")]
    public float OutMax;
    [Property(Tooltip = "Keep the result between outMin and outMax, whatever comes in")]
    public bool Clamp;
    [Property(Tooltip = "The curve between the ends (Linear, QuadInOut, …)")]
    public Ease Ease;
    [Property(Tooltip = "The last value it sent")]
    public float Value;
}

// "math_remap": { "inMin": 0, "inMax": 1, "outMin": 0, "outMax": 100, "clamp": true, "ease": "Linear" }
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[PrefabPart("math_remap", Plugin = RegistrationOwners.Core)]
public sealed class MathRemapPart : IPrefabPart
{
    [Property(Tooltip = "The input range's start: maps to outMin")]
    public float InMin;
    [Property(Tooltip = "The input range's end: maps to outMax")]
    public float InMax = 1f;
    [Property(Tooltip = "What inMin becomes")]
    public float OutMin;
    [Property(Tooltip = "What inMax becomes")]
    public float OutMax = 1f;
    [Property(Tooltip = "Keep the result between outMin and outMax, whatever comes in")]
    public bool Clamp = true;
    [Property(Tooltip = "The curve between the ends (Linear, QuadInOut, …)")]
    public Ease Ease = Ease.Linear;

    public void Apply(in PrefabPartContext ctx)
    {
        if (InMax == InMin) ctx.Warn($"inMin and inMax are both {InMin}: every value maps to outMin");
        ctx.World.Add(ctx.Entity, new MathRemap
        {
            InMin = InMin, InMax = InMax, OutMin = OutMin, OutMax = OutMax, Clamp = Clamp, Ease = Ease, Value = OutMin,
        });
    }
}

// ---- the inputs -----------------------------------------------------------------------------------

// Every logic entity's inputs and outputs, and the arithmetic. Registered by the engine (Engine's
// constructor, sage.core). Each handler runs only for an entity with its component (routed, #91).
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
public static class LogicEntities
{
    public const string OnTrigger = "OnTrigger";
    public const string OnChanged = "OnChanged";
    public const string OnHitMax = "OnHitMax";
    public const string OnHitMin = "OnHitMin";
    public const string OnGetValue = "OnGetValue";
    public const string OnEqual = "OnEqual";
    public const string OnNotEqual = "OnNotEqual";
    public const string OnLess = "OnLess";
    public const string OnGreater = "OnGreater";
    public const string OnTrue = "OnTrue";
    public const string OnFalse = "OnFalse";
    public const string OnValue = "OnValue";

    // Equal, for logic_compare: within this, relative to the larger when that is over 1.
    internal const float Tolerance = 1e-5f;

    internal static void Register(Engine engine)
    {
        var inputs = engine.Inputs;
        var outputs = engine.Outputs;

        // relay
        inputs.Register<LogicRelay>("Trigger", static (World world, in IOContext io) => Trigger(world, io));
        inputs.Register<LogicRelay>("Enable", static (World world, in IOContext io) => world.Get<LogicRelay>(io.Self).Disabled = false);
        inputs.Register<LogicRelay>("Disable", static (World world, in IOContext io) => world.Get<LogicRelay>(io.Self).Disabled = true);
        inputs.Register<LogicRelay>("Toggle", static (World world, in IOContext io) =>
        {
            ref var relay = ref world.Get<LogicRelay>(io.Self);
            relay.Disabled = !relay.Disabled;
        });
        outputs.Declare(OnTrigger, "This relay was triggered, its requires held and its then ran (sage:logic_relay); hands on Trigger's parameter.");

        // counter
        inputs.Register<LogicCounter>("Add", static (World world, in IOContext io) => Count(world, io, "Add", +1));
        inputs.Register<LogicCounter>("Subtract", static (World world, in IOContext io) => Count(world, io, "Subtract", -1));
        inputs.Register<LogicCounter>("SetValue", static (World world, in IOContext io) => Count(world, io, "SetValue", 0));
        inputs.Register<LogicCounter>("Reset", static (World world, in IOContext io) =>
        {
            ref var counter = ref world.Get<LogicCounter>(io.Self);
            if (!counter.Disabled) SetCount(world, io, counter.Start);
        });
        inputs.Register<LogicCounter>("GetValue", static (World world, in IOContext io) =>
            world.FireOutput(io.Self, OnGetValue, io.Activator, world.Get<LogicCounter>(io.Self).Value));
        inputs.Register<LogicCounter>("Enable", static (World world, in IOContext io) => world.Get<LogicCounter>(io.Self).Disabled = false);
        inputs.Register<LogicCounter>("Disable", static (World world, in IOContext io) => world.Get<LogicCounter>(io.Self).Disabled = true);
        outputs.Declare(OnChanged, "This counter's value changed (sage:logic_counter); hands on the new value.");
        outputs.Declare(OnHitMax, "This counter reached its max (sage:logic_counter); hands on the value.");
        outputs.Declare(OnHitMin, "This counter reached its min (sage:logic_counter); hands on the value.");
        outputs.Declare(OnGetValue, "Answers GetValue with this counter's value (sage:logic_counter).");

        // compare
        inputs.Register<LogicCompare>("SetValue", static (World world, in IOContext io) =>
        {
            if (TryNumber(io, "SetValue", out float v)) world.Get<LogicCompare>(io.Self).Value = v;
        });
        inputs.Register<LogicCompare>("SetValueCompare", static (World world, in IOContext io) =>
        {
            if (!TryNumber(io, "SetValueCompare", out float v)) return;
            world.Get<LogicCompare>(io.Self).Value = v;
            Compare(world, io);
        });
        inputs.Register<LogicCompare>("SetCompareValue", static (World world, in IOContext io) =>
        {
            if (TryNumber(io, "SetCompareValue", out float v)) world.Get<LogicCompare>(io.Self).CompareValue = v;
        });
        inputs.Register<LogicCompare>("Compare", static (World world, in IOContext io) => Compare(world, io));
        outputs.Declare(OnEqual, "Compare found the value equal to the compare value (sage:logic_compare); hands on the value.");
        outputs.Declare(OnNotEqual, "Compare found the value not equal to the compare value (sage:logic_compare); hands on the value.");
        outputs.Declare(OnLess, "Compare found the value less than the compare value (sage:logic_compare); hands on the value.");
        outputs.Declare(OnGreater, "Compare found the value greater than the compare value (sage:logic_compare); hands on the value.");

        // branch
        inputs.Register<LogicBranch>("SetValue", static (World world, in IOContext io) =>
        {
            if (TryBool(io, "SetValue", out bool v)) world.Get<LogicBranch>(io.Self).Value = v;
        });
        inputs.Register<LogicBranch>("SetValueTest", static (World world, in IOContext io) =>
        {
            if (!TryBool(io, "SetValueTest", out bool v)) return;
            world.Get<LogicBranch>(io.Self).Value = v;
            Test(world, io);
        });
        inputs.Register<LogicBranch>("Toggle", static (World world, in IOContext io) =>
        {
            ref var branch = ref world.Get<LogicBranch>(io.Self);
            branch.Value = !branch.Value;
        });
        inputs.Register<LogicBranch>("ToggleTest", static (World world, in IOContext io) =>
        {
            ref var branch = ref world.Get<LogicBranch>(io.Self);
            branch.Value = !branch.Value;
            Test(world, io);
        });
        inputs.Register<LogicBranch>("Test", static (World world, in IOContext io) => Test(world, io));
        outputs.Declare(OnTrue, "Test found this branch true (sage:logic_branch).");
        outputs.Declare(OnFalse, "Test found this branch false (sage:logic_branch).");

        // math_remap
        inputs.Register<MathRemap>("SetValue", static (World world, in IOContext io) =>
        {
            if (!TryNumber(io, "SetValue", out float v)) return;
            ref var remap = ref world.Get<MathRemap>(io.Self);
            remap.Value = Remap(in remap, v);
            world.FireOutput(io.Self, OnValue, io.Activator, remap.Value);
        });
        outputs.Declare(OnValue, "A math entity's result (sage:math_remap); hands on the value.");
    }

    // ---- relay

    private static void Trigger(World world, in IOContext io)
    {
        var relay = world.Get<LogicRelay>(io.Self);
        if (relay.Disabled) return;
        if (world.TryGet<LogicRelayScript>(io.Self, out var script))
        {
            if (script.Requires != null && !script.Requires.Test(new ConditionContext(world, io.Activator, io.Self), out _)) return;
            Conditions.Run(script.Then, new ActionContext(world, io.Activator, io.Self));
        }
        if (!world.IsAlive(io.Self)) return;       // a `then` may have killed it
        world.FireOutput(io.Self, OnTrigger, io.Activator, io.Parameter.Length > 0 ? io.Parameter : null, relay.SameTick);
    }

    // ---- counter

    // `sign` +1 adds, -1 subtracts (the parameter, or 1), 0 sets (the parameter).
    private static void Count(World world, in IOContext io, string input, int sign)
    {
        ref var counter = ref world.Get<LogicCounter>(io.Self);
        if (counter.Disabled) return;
        float amount;
        if (sign != 0 && io.Parameter.Length == 0) amount = 1f;
        else if (!TryNumber(io, input, out amount)) return;
        SetCount(world, io, sign == 0 ? amount : counter.Value + sign * amount);
    }

    private static void SetCount(World world, in IOContext io, float value)
    {
        ref var counter = ref world.Get<LogicCounter>(io.Self);
        if (counter.IsLimited()) value = Math.Clamp(value, counter.Min, counter.Max);
        float before = counter.Value;
        if (value == before) return;
        counter.Value = value;
        bool hitMax = counter.IsLimited() && value >= counter.Max;
        bool hitMin = counter.IsLimited() && value <= counter.Min;

        // Written once, then fired: a wire may send this counter another input, which arrives next pass.
        world.FireOutput(io.Self, OnChanged, io.Activator, value);
        if (hitMax) world.FireOutput(io.Self, OnHitMax, io.Activator, value);
        if (hitMin) world.FireOutput(io.Self, OnHitMin, io.Activator, value);
    }

    // ---- compare

    private static void Compare(World world, in IOContext io)
    {
        var compare = world.Get<LogicCompare>(io.Self);
        float a = compare.Value, b = compare.CompareValue;
        bool equal = MathF.Abs(a - b) <= Tolerance * MathF.Max(1f, MathF.Max(MathF.Abs(a), MathF.Abs(b)));
        if (equal) world.FireOutput(io.Self, OnEqual, io.Activator, a);
        else
        {
            world.FireOutput(io.Self, OnNotEqual, io.Activator, a);
            world.FireOutput(io.Self, a < b ? OnLess : OnGreater, io.Activator, a);
        }
    }

    // ---- branch

    private static void Test(World world, in IOContext io) =>
        world.FireOutput(io.Self, world.Get<LogicBranch>(io.Self).Value ? OnTrue : OnFalse, io.Activator);

    // ---- remap

    public static float Remap(in MathRemap remap, float value)
    {
        if (remap.InMax == remap.InMin) return remap.OutMin;
        float t = (value - remap.InMin) / (remap.InMax - remap.InMin);
        if (remap.Clamp) t = Math.Clamp(t, 0f, 1f);
        // An ease is a curve on 0..1; outside it (unclamped), the straight line carries on.
        float eased = t >= 0f && t <= 1f ? Easing.Apply(remap.Ease, t) : t;
        return remap.OutMin + (remap.OutMax - remap.OutMin) * eased;
    }

    // ---- parameters

    private static bool TryNumber(in IOContext io, string input, out float value)
    {
        if (float.TryParse(io.Parameter, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value)) return true;
        Log.Warn(LogCat.Events, $"I/O: {input}({io.Parameter}) at {World.Describe(io.Self)}: the parameter should be a number");
        return false;
    }

    // true/false, 1/0, yes/no, on/off.
    internal static bool TryBool(in IOContext io, string input, out bool value)
    {
        var p = io.Parameter.AsSpan().Trim();
        if (p.Equals("1", StringComparison.Ordinal) || p.Equals("true", StringComparison.OrdinalIgnoreCase)
            || p.Equals("yes", StringComparison.OrdinalIgnoreCase) || p.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }
        if (p.Equals("0", StringComparison.Ordinal) || p.Equals("false", StringComparison.OrdinalIgnoreCase)
            || p.Equals("no", StringComparison.OrdinalIgnoreCase) || p.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }
        value = false;
        Log.Warn(LogCat.Events, $"I/O: {input}({io.Parameter}) at {World.Describe(io.Self)}: the parameter should be true or false (1/0)");
        return false;
    }
}
