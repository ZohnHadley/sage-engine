#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// One condition and action language (docs/REDESIGN.md §4.3 stage 2, issue #89).
//
// **What decides and what does, as data every system shares.** A line of dialogue, a trigger's wire, a
// state machine's transition, a topic, a widget that shows only sometimes: each needs "may this happen?"
// and "what happens?", and each used to be a reason for a field of its own or a line of C#. These two
// vocabularies are the one answer, in the base engine, so a game with no dialogue plugin — or no
// gameplay plugins at all — has them:
//
//   "requires": { "all": [ { "has_item": "key_iron" }, { "not": { "var": "alarm", "eq": 1 } } ] },
//   "then":     [ { "fire": "hut_door", "input": "Open" }, { "add_var": "doors_opened" } ]
//
// Each entry is an id and its settings, written `{ "condition": "has_item", "item": "key_iron" }` or,
// shorter, `{ "has_item": "key_iron" }` (VocabularyAttribute.Shorthand; the value fills the entry's
// [EntryValue] field). Ids are the ones content always wrote; only the C# namespace moved, from
// Sage.Gameplay (decision D2; SAGE0120 is experimental, MAKING_A_GAME §10b).
//
// The base owns the words that need nothing but a world: `all`, `any`, `not`, `var` (Vars.cs); `fire`,
// `set_var`, `add_var`. The words that need gameplay — `has_item`, `standing`, `quest`, `give_item` —
// are Gameplay's, registered by the plugin that owns what they ask about, and read the same way.
//
// **Asking allocates nothing.** Lists are read into once when content loads; a test walks them by index
// and every reason is a constant (test: EvaluatingConditionsAllocatesNothing).

// Something that must hold. `why` is what a refusal says when it does not (R17): a greyed-out row, a
// wire that did not fire.
[Vocabulary("condition", Key = "condition", Shorthand = true)]
[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28): may change before 1.0
public interface ICondition
{
    bool Test(in ConditionContext context, out string why);
}

// Something that happens.
[Vocabulary("action", Key = "action", Shorthand = true)]
[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28): may change before 1.0
public interface IAction
{
    void Run(in ActionContext context);
}

[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28): may change before 1.0
public sealed class ConditionAttribute : VocabularyEntryAttribute<ICondition>
{
    public ConditionAttribute(string id) : base(id) { }
}

[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28): may change before 1.0
public sealed class ActionAttribute : VocabularyEntryAttribute<IAction>
{
    public ActionAttribute(string id) : base(id) { }
}

// Who a condition is asked about: `Subject` is the one it is about (the player, in a conversation; the
// activator, on a wire) and `Other` whoever else is involved (the speaker; the entity doing the asking),
// or nobody. `Records` is the world's content.
[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28): may change before 1.0
public readonly record struct ConditionContext(World World, Entity Subject, Entity Other = default)
{
    public RecordStore Records => World.Resources.Get<RecordStore>();
}

[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28): may change before 1.0
public readonly record struct ActionContext(World World, Entity Subject, Entity Other = default)
{
    public RecordStore Records => World.Resources.Get<RecordStore>();
}

// Asking and doing, for whatever reads a `requires` or a `then`: nothing (null, an empty list) is
// always allowed and does nothing.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // stage 2 logic (#89): may change before 1.0
public static class Conditions
{
    public static bool Test(ICondition? condition, in ConditionContext context, out string why)
    {
        if (condition == null)
        {
            why = "";
            return true;
        }
        return condition.Test(in context, out why);
    }

    // Every one, in order; `why` is the first refusal.
    public static bool TestAll(IReadOnlyList<ICondition>? conditions, in ConditionContext context, out string why)
    {
        why = "";
        if (conditions == null) return true;
        for (int i = 0; i < conditions.Count; i++)
            if (conditions[i] is { } condition && !condition.Test(in context, out why)) return false;
        return true;
    }

    public static bool Evaluate(World world, Entity subject, ICondition? condition, Entity other = default) =>
        Test(condition, new ConditionContext(world, subject, other), out _);

    public static bool Evaluate(World world, Entity subject, IReadOnlyList<ICondition>? conditions, Entity other = default) =>
        TestAll(conditions, new ConditionContext(world, subject, other), out _);

    // Every action, in order.
    public static void Run(IReadOnlyList<IAction>? actions, in ActionContext context)
    {
        if (actions == null) return;
        for (int i = 0; i < actions.Count; i++) actions[i]?.Run(in context);
    }

    public static void Run(World world, Entity subject, IReadOnlyList<IAction>? actions, Entity other = default) =>
        Run(actions, new ActionContext(world, subject, other));
}

// ---- the base's conditions -------------------------------------------------------------------------

// Every one of them: `{ "all": [ … ] }`. The reason is the first that failed, or `why`.
[Condition("all", Plugin = RegistrationOwners.Core)]
internal sealed class AllCondition : ICondition
{
    [EntryValue, Property(Tooltip = "The conditions that must all hold")]
    public List<ICondition> Of = new();
    [Property(Tooltip = "What a refusal says, instead of the reason of the first that failed")]
    public string Why = "";

    public bool Test(in ConditionContext context, out string why)
    {
        why = "";
        for (int i = 0; i < Of.Count; i++)
        {
            if (Of[i] is not { } condition || condition.Test(in context, out why)) continue;
            if (Why.Length > 0) why = Why;
            return false;
        }
        return true;
    }
}

// At least one of them: `{ "any": [ … ] }`. None of nothing: an empty `any` never holds.
[Condition("any", Plugin = RegistrationOwners.Core)]
internal sealed class AnyCondition : ICondition
{
    [EntryValue, Property(Tooltip = "The conditions of which one must hold")]
    public List<ICondition> Of = new();
    [Property(Tooltip = "What a refusal says, instead of the reason of the last that failed")]
    public string Why = "";

    public bool Test(in ConditionContext context, out string why)
    {
        why = "not now";
        for (int i = 0; i < Of.Count; i++)
            if (Of[i] is { } condition && condition.Test(in context, out why)) return true;
        if (Why.Length > 0) why = Why;
        return false;
    }
}

// The opposite: `{ "not": { "has_item": "key_iron" } }`.
[Condition("not", Plugin = RegistrationOwners.Core)]
internal sealed class NotCondition : ICondition
{
    [EntryValue, Property(Tooltip = "The condition that must not hold")]
    public ICondition? Of;
    [Property(Tooltip = "What a refusal says")]
    public string Why = "";

    public bool Test(in ConditionContext context, out string why)
    {
        why = Why.Length > 0 ? Why : "not now";
        return Of == null || !Of.Test(in context, out _);
    }
}

// ---- the base's actions ----------------------------------------------------------------------------

// Sends an entity input, as a wire would (04 §3.4): `{ "fire": "hut_door", "input": "Open" }`. The
// target is a name, looked up when the action runs (nobody by that name: nothing is sent, as a wire
// with no target sends nothing), or `!subject` / `!activator` (the one it is about) or `!other` /
// `!self` / `!caller` (whoever is doing it: the speaker, the relay). What it sends arrives in the
// EntityIO phase, `delay` seconds on, with the subject as its activator and the other as its caller,
// through EntityIO.FireInput. Nothing happens in a game without entity I/O (`sage.gameplay.io` off).
[Action("fire", Plugin = RegistrationOwners.Core)]
internal sealed class FireAction : IAction
{
    [EntryValue, Property(Tooltip = "Who receives it: an entity's name, or !subject / !activator / !other / !self")]
    public string Target = "";
    [Property(Tooltip = "The input it sends: Open, Kill, CameraOn; io_list lists them")]
    public string Input = "";
    [Property(Tooltip = "Handed to the input: a hold time, a line to say, an output to fire")]
    public string Parameter = "";
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds between the action and the input arriving")]
    public float Delay;

    public void Run(in ActionContext context)
    {
        if (Target.Length == 0 || Input.Length == 0) return;
        if (!context.World.Resources.TryGet<EntityIO>(out var io) || io == null)
        {
            Log.Once(LogCat.Events, LogLevel.Warn, "fire-no-io",
                $"fire {Target}.{Input}: this game has no entity I/O (the sage.gameplay.io plugin is off), so nothing is sent");
            return;
        }

        Entity target;
        if (Target[0] == '!')
        {
            if (Is("!subject") || Is("!activator") || Is("!player")) target = context.Subject;
            else if (Is("!other") || Is("!self") || Is("!caller")) target = context.Other;
            else
            {
                Log.Once(LogCat.Events, LogLevel.Warn, $"fire-target:{Target}",
                    $"fire: no target '{Target}' (a name, or !subject / !activator / !other / !self)");
                return;
            }
        }
        else
        {
            target = context.World.FindByName(Target);
            if (target.IsNull)
            {
                Log.Debug(LogCat.Events, $"fire: no entity named '{Target}' for {Input}");
                return;
            }
        }
        io.FireInput(target, Input, Parameter, Delay, context.Subject, context.Other);
    }

    private bool Is(string special) => string.Equals(Target, special, StringComparison.OrdinalIgnoreCase);
}
