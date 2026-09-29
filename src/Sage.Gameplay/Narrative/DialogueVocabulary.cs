#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Gameplay;

// What a line of dialogue may require and what saying it does, as open vocabularies (docs/REDESIGN.md
// §4.3, issue #28). They were two fixed classes, `DialogueRequirement` and `DialogueOutcome`; those stay
// as the shorthand content already writes (`requires`, `then`) and mean exactly these entries — so a
// conversation can now also say what a game declares:
//
//   "conditions": [{ "condition": "is_night" }, { "condition": "has_item", "item": "key_iron" }],
//   "actions":    [{ "action": "heal", "amount": 20 }, { "action": "start_quest", "quest": "thin_the_wood" }]
//
// The engine's conditions and actions belong to the dialogue plugin, the quest ones to the quests
// plugin. They are the seed of stage 2's shared condition and action language (§4.3): nothing about
// them is dialogue's except where they are read today.

// Something that must hold. `why` is what a greyed-out row says when it does not (R17), unless the
// option writes its own `refusal`.
[Vocabulary("condition", Key = "condition")]
[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28): may change before 1.0
public interface ICondition
{
    bool Test(in ConditionContext context, out string why);
}

// Something that happens.
[Vocabulary("action", Key = "action")]
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

// Who a condition is asked about: `Subject` is the one it is about (the player, in a conversation) and
// `Other` whoever else is involved (the speaker), or nobody.
[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28): may change before 1.0
public readonly record struct ConditionContext(World World, Entity Subject, Entity Other = default);

[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28): may change before 1.0
public readonly record struct ActionContext(World World, Entity Subject, Entity Other = default);

// ---- the engine's conditions ----------------------------------------------------------------------

[Condition("has_tag", Plugin = "sage.gameplay.dialogue")]
internal sealed class HasTagCondition : ICondition
{
    public RecordRef<TagRecord> Tag;

    public bool Test(in ConditionContext c, out string why)
    {
        why = "not yet";
        return Tag.IsEmpty || c.World.HasTag(c.Subject, Tag);
    }
}

[Condition("lacks_tag", Plugin = "sage.gameplay.dialogue")]
internal sealed class LacksTagCondition : ICondition
{
    public RecordRef<TagRecord> Tag;

    public bool Test(in ConditionContext c, out string why)
    {
        why = "already done";
        return Tag.IsEmpty || !c.World.HasTag(c.Subject, Tag);
    }
}

[Condition("has_item", Plugin = "sage.gameplay.dialogue")]
internal sealed class HasItemCondition : ICondition
{
    public RecordRef<ItemRecord> Item;
    [Property(Min = 1)] public int Count = 1;

    public bool Test(in ConditionContext c, out string why)
    {
        why = "you do not have it";
        return Item.IsEmpty || c.World.CountOf(c.Subject, Item) >= Math.Max(Count, 1);
    }
}

// Standing with a faction inside [min, max]; 0 in a game without factions (issue #26).
[Condition("standing", Plugin = "sage.gameplay.dialogue")]
internal sealed class StandingCondition : ICondition
{
    public RecordRef<FactionRecord> Faction;
    public float Min = float.NegativeInfinity;
    public float Max = float.PositiveInfinity;

    public bool Test(in ConditionContext c, out string why)
    {
        why = "they do not trust you";
        if (Faction.IsEmpty) return true;
        float standing = Factions.StandingWith(c.World, Faction);
        return standing >= Min && standing <= Max;
    }
}

// Where the player is in a quest: `notStarted`, `finished` or `active`, and/or at `stage`. Nobody is on
// any quest in a game without the quests plugin (issue #26).
[Condition("quest", Plugin = "sage.gameplay.quests")]
internal sealed class QuestCondition : ICondition
{
    public RecordRef<QuestRecord> Quest;
    public string Stage = "";
    public bool Active;
    public bool Finished;
    public bool NotStarted;

    public bool Test(in ConditionContext c, out string why)
    {
        why = "there is nothing to say about that";
        if (Quest.IsEmpty) return true;
        bool ok = true;
        if (NotStarted) ok = Quests.IsNotStarted(c.World, Quest);
        else if (Finished) ok = Quests.IsFinished(c.World, Quest);
        else if (Active) ok = Quests.IsActive(c.World, Quest);
        if (ok && Stage.Length > 0)
            ok = Quests.StageOf(c.World, Quest) == Stage && Quests.IsActive(c.World, Quest);
        return ok;
    }
}

// ---- the engine's actions -------------------------------------------------------------------------

[Action("give_item", Plugin = "sage.gameplay.dialogue")]
internal sealed class GiveItemAction : IAction
{
    public RecordRef<ItemRecord> Item;
    [Property(Min = 1)] public int Count = 1;

    public void Run(in ActionContext c)
    {
        if (!Item.IsEmpty) c.World.Give(c.Subject, Item, Math.Max(Count, 1));
    }
}

[Action("take_item", Plugin = "sage.gameplay.dialogue")]
internal sealed class TakeItemAction : IAction
{
    public RecordRef<ItemRecord> Item;
    [Property(Min = 1)] public int Count = 1;

    public void Run(in ActionContext c)
    {
        if (!Item.IsEmpty) c.World.Take(c.Subject, Item, Math.Max(Count, 1));
    }
}

// An effect on the subject: a blessing, a curse, a disease.
[Action("apply_effect", Plugin = "sage.gameplay.dialogue")]
internal sealed class ApplyEffectAction : IAction
{
    public RecordRef<EffectRecord> Effect;
    public float Magnitude = 1f;

    public void Run(in ActionContext c)
    {
        if (!Effect.IsEmpty) Effects.Apply(c.World, c.Subject, Effect, default, Magnitude);
    }
}

[Action("change_standing", Plugin = "sage.gameplay.dialogue")]
internal sealed class ChangeStandingAction : IAction
{
    public RecordRef<FactionRecord> Faction;
    public float Amount;

    public void Run(in ActionContext c)
    {
        if (!Faction.IsEmpty && Amount != 0f) Factions.Change(c.World, Faction, Amount);
    }
}

[Action("start_quest", Plugin = "sage.gameplay.quests")]
internal sealed class StartQuestAction : IAction
{
    public RecordRef<QuestRecord> Quest;

    public void Run(in ActionContext c)
    {
        if (!Quest.IsEmpty) Quests.Start(c.World, Quest);
    }
}

[Action("set_stage", Plugin = "sage.gameplay.quests")]
internal sealed class SetStageAction : IAction
{
    public RecordRef<QuestRecord> Quest;
    public string Stage = "";

    public void Run(in ActionContext c)
    {
        if (!Quest.IsEmpty && Stage.Length > 0) Quests.SetStage(c.World, Quest, Stage);
    }
}

// Reported, done, over.
[Action("finish_quest", Plugin = "sage.gameplay.quests")]
internal sealed class FinishQuestAction : IAction
{
    public RecordRef<QuestRecord> Quest;

    public void Run(in ActionContext c)
    {
        if (!Quest.IsEmpty) Quests.Finish(c.World, Quest);
    }
}

// ---- the shorthand ---------------------------------------------------------------------------------

internal static class DialogueSugar
{
    // `requires`, as the conditions it means, in the order it was always asked.
    public static List<ICondition> Conditions(DialogueRequirement? requires)
    {
        var list = new List<ICondition>();
        if (requires == null) return list;
        if (!requires.Tag.IsEmpty) list.Add(new HasTagCondition { Tag = requires.Tag });
        if (!requires.WithoutTag.IsEmpty) list.Add(new LacksTagCondition { Tag = requires.WithoutTag });
        if (!requires.Item.IsEmpty) list.Add(new HasItemCondition { Item = requires.Item, Count = requires.Count });
        if (!requires.Faction.IsEmpty)
            list.Add(new StandingCondition { Faction = requires.Faction, Min = requires.MinStanding, Max = requires.MaxStanding });
        if (!requires.Quest.IsEmpty)
            list.Add(new QuestCondition
            {
                Quest = requires.Quest, Stage = requires.Stage, Active = requires.Active,
                Finished = requires.Finished, NotStarted = requires.NotStarted,
            });
        return list;
    }

    // `then`, as the actions it means, in the order they were always done.
    public static List<IAction> Actions(DialogueOutcome? then)
    {
        var list = new List<IAction>();
        if (then == null) return list;
        if (!then.GiveItem.IsEmpty) list.Add(new GiveItemAction { Item = then.GiveItem, Count = then.GiveCount });
        if (!then.TakeItem.IsEmpty) list.Add(new TakeItemAction { Item = then.TakeItem, Count = then.TakeCount });
        if (!then.Effect.IsEmpty) list.Add(new ApplyEffectAction { Effect = then.Effect });
        if (!then.Faction.IsEmpty && then.Standing != 0f) list.Add(new ChangeStandingAction { Faction = then.Faction, Amount = then.Standing });
        if (!then.StartQuest.IsEmpty) list.Add(new StartQuestAction { Quest = then.StartQuest });
        if (!then.Quest.IsEmpty && then.Stage.Length > 0) list.Add(new SetStageAction { Quest = then.Quest, Stage = then.Stage });
        if (!then.FinishQuest.IsEmpty) list.Add(new FinishQuestAction { Quest = then.FinishQuest });
        return list;
    }
}
