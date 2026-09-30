#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// World variables (issue #89): named numbers content keeps without a record, a component or a line of
// C# — how many levers are down, whether the alarm has gone off, how often the player has lied to the
// guard. Morrowind's globals and Source's `math_counter` value, as one saved table per world.
//
//   "requires": { "var": "levers_down", "min": 3 }
//   "then":     [ { "add_var": "levers_down" }, { "set_var": "alarm", "value": 1 } ]
//
// A name nobody set reads as 0. Names are case-sensitive, like entity names. Saved as the `vars`
// resource: `"vars": { "version": 1, "data": { "Values": { "alarm": 1, "levers_down": 2 } } }`
// (test: AVarSurvivesSaveAndLoad).
[SavedResource("vars", Plugin = RegistrationOwners.Core)]
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // stage 2 logic (#89): may change before 1.0
public sealed class Vars
{
    private Dictionary<string, double> _values = new(StringComparer.Ordinal);

    // Every variable that has been set, by name: what the save writes.
    public Dictionary<string, double> Values
    {
        get => _values;
        set => _values = value == null ? new(StringComparer.Ordinal) : new(value, StringComparer.Ordinal);
    }

    public double Get(string name) => _values.TryGetValue(name, out double value) ? value : 0;

    public bool Has(string name) => _values.ContainsKey(name);

    public void Set(string name, double value) => _values[name] = value;

    // Adds to it (from 0 when unset) and returns what it now is.
    public double Add(string name, double amount) => _values[name] = Get(name) + amount;

    // The world's variables. Every world has them (Engine.CreateWorld); a load replaces them.
    public static Vars Of(World world) => world.Resources.GetOrAdd(static () => new Vars());

    // What a variable is in this world, 0 when unset or when the world has none, without making any.
    public static double ValueOf(World world, string name) =>
        world.Resources.TryGet<Vars>(out var vars) && vars != null ? vars.Get(name) : 0;
}

// `{ "var": "alarm", "eq": 1 }`, `{ "var": "levers_down", "min": 3 }`, `{ "var": "lies", "max": 2 }`:
// the variable equals `eq` (when given) and is inside [min, max]. An unset variable is 0.
[Condition("var", Plugin = RegistrationOwners.Core)]
internal sealed class VarCondition : ICondition
{
    [EntryValue, Property(Tooltip = "The world variable to test; unset reads as 0")]
    public string Name = "";
    [Property(Tooltip = "The value it must equal (leave out for any)")]
    public double? Eq;
    [Property(Tooltip = "The least it may be")]
    public double Min = double.NegativeInfinity;
    [Property(Tooltip = "The most it may be")]
    public double Max = double.PositiveInfinity;

    public bool Test(in ConditionContext context, out string why)
    {
        why = "not yet";
        if (Name.Length == 0) return true;
        double value = Vars.ValueOf(context.World, Name);
        return (Eq is not { } eq || value == eq) && value >= Min && value <= Max;
    }
}

// `{ "set_var": "alarm", "value": 1 }`.
[Action("set_var", Plugin = RegistrationOwners.Core)]
internal sealed class SetVarAction : IAction
{
    [EntryValue, Property(Tooltip = "The world variable to set")]
    public string Name = "";
    [Property(Tooltip = "What it becomes")]
    public double Value;

    public void Run(in ActionContext context)
    {
        if (Name.Length > 0) Vars.Of(context.World).Set(Name, Value);
    }
}

// `{ "add_var": "levers_down" }` adds 1; `"amount": -1` takes one away.
[Action("add_var", Plugin = RegistrationOwners.Core)]
internal sealed class AddVarAction : IAction
{
    [EntryValue, Property(Tooltip = "The world variable to add to; unset counts from 0")]
    public string Name = "";
    [Property(Tooltip = "How much to add; negative takes away")]
    public double Amount = 1;

    public void Run(in ActionContext context)
    {
        if (Name.Length > 0) Vars.Of(context.World).Add(Name, Amount);
    }
}
