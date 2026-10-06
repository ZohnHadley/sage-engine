#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Gameplay;

// Talking to somebody (docs/design/16 §3.5, TODO F24).
//
// **A conversation is a record, not a script.** A `dialogue` record is a handful of nodes; a node is a
// line of text and the things you may say back; an option leads to another node and may *do* something
// on the way — change what a faction thinks of you, hand over an item, apply an effect, start or
// advance a quest. Everything it can do is machinery the engine already has, which is the whole design:
// dialogue is a way of *reaching* the rest of the game, not a system with rules of its own.
//
// What an option is allowed to do, and whether it may be picked at all, is asked the same way a
// spellmaker's "make it" row is asked (R17): `Dialogue.CanPick` answers it, `Dialogue.Pick` applies it,
// and the screen greys the row with the reason. A button and the attempt behind it cannot disagree.

// What has to be true before a line may be said.
public sealed class DialogueRequirement
{
    public RecordRef<TagRecord> Tag; // the speaker's player must have this tag
    public RecordRef<TagRecord> WithoutTag; // ...and not this one
    public RecordRef<ItemRecord> Item; // and be carrying this
    public int Count = 1;
    public RecordRef<FactionRecord> Faction; // and stand at least this well with them
    public float MinStanding = float.NegativeInfinity;
    public float MaxStanding = float.PositiveInfinity;

    // And where they are in a quest. `stage` alone means "on this quest, at this stage"; `finished`
    // means the whole thing is behind them. This is how a conversation remembers what it asked for.
    public RecordRef<QuestRecord> Quest;
    public string Stage = "";
    public bool Active;
    public bool Finished;
    public bool NotStarted;
}

// And what saying it does. Nothing here is new machinery: it is the same giving, taking, tagging and
// reputation that the rest of the game uses.
public sealed class DialogueOutcome
{
    public RecordRef<ItemRecord> GiveItem;
    public int GiveCount = 1;
    public RecordRef<ItemRecord> TakeItem;
    // Counted separately from what is given, because the commonest outcome in any game is a trade: one
    // relic for five coins. A single `count` for both made handing over one thing ask for five.
    public int TakeCount = 1;
    public RecordRef<EffectRecord> Effect; // applied to the player (a blessing, a curse, a disease)
    public RecordRef<FactionRecord> Faction; // and what this does to their name
    public float Standing;

    // Quests: asking for one, and saying it is done (16 §3.5).
    public RecordRef<QuestRecord> StartQuest;
    public RecordRef<QuestRecord> FinishQuest; // reported, done, over
    public RecordRef<QuestRecord> Quest; // the one whose stage to set
    public string Stage = "";
}

public sealed class DialogueOption
{
    public string Text = "";
    // The node to move to; empty ends the conversation. **A name, not a record id**: a node is a label
    // inside one dialogue, not a thing the world can refer to — typing it as a `RecordId` made the
    // validator hunt for a record called `sandbox:greet` and made every `goto` carry a namespace it
    // does not have (R11).
    public string Goto = "";
    public bool End;                            // or say so outright
    public DialogueRequirement? Requires;
    public DialogueOutcome? Then;
    public string Refusal = "";                 // what the row says when it is greyed out
    // Opens the speaker's topics (issue #93, Topics.cs): picking it does what it does, keeps the
    // conversation on this node and sets `Conversation.Topics`, which a screen reads to show the list.
    // `goto` and `end` are ignored on such an option.
    public bool Topics;

    // Conditions and actions by name (issue #28), asked and done after what `requires` and `then` say —
    // which are shorthand for the engine's own entries (DialogueSugar).
    public List<ICondition> Conditions = new();
    public List<IAction> Actions = new();

    // Everything it requires and does, shorthand first: made once per option (a reload makes new ones).
    internal List<ICondition> AllConditions() => _conditions ??= Combine(DialogueSugar.Conditions(Requires), Conditions);
    internal List<IAction> AllActions() => _actions ??= Combine(DialogueSugar.Actions(Then), Actions);

    private List<ICondition>? _conditions;
    private List<IAction>? _actions;

    private static List<T> Combine<T>(List<T> sugar, List<T> named)
    {
        sugar.AddRange(named);
        return sugar;
    }
}

public sealed class DialogueNode
{
    public string Id = "";
    public string Text = "";
    public List<DialogueOption> Options = new();
}

[Record("dialogue", Plugin = "sage.gameplay.dialogue")]
public sealed class DialogueRecord
{
    public string Label = "";
    public string Start = "";                   // empty = the first node
    public List<DialogueNode> Nodes = new();

    public DialogueNode? Node(string id)
    {
        if (id.Length == 0) return Nodes.Count > 0 ? Nodes[0] : null;
        foreach (var node in Nodes)
            if (node.Id == id) return node;
        return null;
    }
}

// Somebody worth talking to. The component is the engine's, so a headless server carries it and simply
// never opens a window.
[Component("sage:dialogue")]
public struct Dialogue : IComponent
{
    public RecordId Record;
}

// Where a conversation has got to. One per world: you can only talk to one person at a time, and a
// conversation that outlived the window it was shown in would be a second source of truth.
public sealed class Conversation
{
    public Entity Speaker;          // who is talking to you
    public Entity Listener;         // and who they are talking to (the player)
    public RecordId Record;
    public string Node = "";
    // An option with `topics: true` was picked: the screen shows the speaker's topics
    // (DialogueTopics.Available) until the next Start, Pick or Stop.
    public bool Topics;

    public bool Running => !Record.IsEmpty;

    public void Stop()
    {
        Record = default;
        Node = "";
        Topics = false;
        Speaker = default;
        Listener = default;
    }
}

// A conversation began or ended, for a game that wants to know (a quest, a HUD, a camera).
[GameEvent]
public readonly record struct Spoke(Entity Speaker, Entity Listener, string Node);

public static class DialogueRules
{
    // Starts one. False when there is nothing to say: no record, no nodes, or the entity is not the
    // talking kind — all of which are content mistakes rather than crashes.
    public static bool Start(World world, Entity speaker, Entity listener)
    {
        // No Conversation: a game without the dialogue plugin, where nobody has anything to say.
        if (!world.Resources.TryGet<Conversation>(out var conversation) || conversation == null) return false;
        if (!world.TryGet<Dialogue>(speaker, out var dialogue) || dialogue.Record.IsEmpty) return false;
        // The dead say nothing: using a body is looting it (the RPG kit, issue #344), not a conversation.
        var dead = world.Conventions().Dead;
        if (!dead.IsEmpty && world.Has<GameplayTags>(speaker) && world.HasTag(speaker, dead.Id)) return false;
        var records = world.Resources.Get<RecordStore>();
        if (!records.TryGet(dialogue.Record, out DialogueRecord record))
        {
            Log.Once(LogCat.Gameplay, LogLevel.Error, $"dialogue:{dialogue.Record}",
                $"No dialogue record {dialogue.Record}: {World.Describe(speaker)} has nothing to say");
            return false;
        }

        var node = record.Node(record.Start);
        if (node == null)
        {
            Log.Once(LogCat.Gameplay, LogLevel.Error, $"dialogue-start:{dialogue.Record}",
                $"{dialogue.Record} has no node '{record.Start}' to start at");
            return false;
        }

        conversation.Speaker = speaker;
        conversation.Listener = listener;
        conversation.Record = dialogue.Record;
        conversation.Node = node.Id;
        conversation.Topics = false;
        world.Events.Send(new Spoke(speaker, listener, node.Id));
        return true;
    }

    public static DialogueNode? Current(World world)
    {
        if (!world.Resources.TryGet<Conversation>(out var conversation) || conversation is not { Running: true }) return null;
        return world.Resources.Get<RecordStore>().TryGet(conversation.Record, out DialogueRecord record)
            ? record.Node(conversation.Node) : null;
    }

    // May this be said? The reason comes back in words, because the screen shows it and because a
    // refusal nobody can read is a bug report waiting to happen (R17).
    //
    // A requirement on a plugin the game does not have is answered the way that plugin's absence
    // answers it (issue #26): every standing is 0 without factions, and nobody is on any quest
    // without quests.
    public static bool CanPick(World world, Entity listener, DialogueOption option, out string why)
    {
        why = "";
        var conditions = option.AllConditions();
        if (conditions.Count == 0) return true;

        var speaker = world.Resources.TryGet<Conversation>(out var conversation) && conversation != null ? conversation.Speaker : default;
        var context = new ConditionContext(world, listener, speaker);
        foreach (var condition in conditions)
        {
            if (condition.Test(in context, out string refusal)) continue;
            why = option.Refusal.Length > 0 ? option.Refusal : refusal;
            return false;
        }
        return true;
    }

    // Says it: applies what it does, then moves to the node it leads to (or ends). Returns false when
    // the option was refused, and nothing happened.
    public static bool Pick(World world, DialogueOption option)
    {
        if (!world.Resources.TryGet<Conversation>(out var conversation) || conversation is not { Running: true }) return false;

        var listener = conversation.Listener;
        if (!CanPick(world, listener, option, out _)) return false;

        Apply(world, listener, conversation.Speaker, option);
        conversation.Topics = false;

        if (option.Topics)
        {
            // Stays where it is: the node's line is still what was said, and the topics are asked of
            // the same speaker (DialogueTopics). A conversation the option's actions ended stays ended.
            if (conversation.Running) conversation.Topics = true;
            return true;
        }

        var records = world.Resources.Get<RecordStore>();
        if (option.End || option.Goto.Length == 0 || !records.TryGet(conversation.Record, out DialogueRecord record))
        {
            conversation.Stop();
            return true;
        }

        var next = record.Node(option.Goto);
        if (next == null)
        {
            Log.Once(LogCat.Gameplay, LogLevel.Error, $"dialogue-goto:{conversation.Record}:{option.Goto}",
                $"{conversation.Record}: no node '{option.Goto}', so the conversation ends there");
            conversation.Stop();
            return true;
        }

        conversation.Node = next.Id;
        world.Events.Send(new Spoke(conversation.Speaker, listener, next.Id));
        return true;
    }

    private static void Apply(World world, Entity listener, Entity speaker, DialogueOption option)
    {
        var actions = option.AllActions();
        if (actions.Count == 0 && option.Then == null) return;

        var context = new ActionContext(world, listener, speaker);
        Conditions.Run(actions, in context);   // through the runner, so a `wait` puts the rest off (#275)

        // Taking or giving may have finished an errand: "bring me five pelts" is met the moment the
        // fifth is in the bag, and the conversation that took them should not leave it hanging.
        Quests.Check(world);
    }
}
