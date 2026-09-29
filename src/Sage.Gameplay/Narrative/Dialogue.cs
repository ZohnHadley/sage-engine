#nullable enable
using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;

namespace sage_engine;

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
}

public sealed class DialogueNode
{
    public string Id = "";
    public string Text = "";
    public List<DialogueOption> Options = new();
}

[Record("dialogue", Plugin = "sage.gameplay.factions")]
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

    public bool Running => !Record.IsEmpty;

    public void Stop()
    {
        Record = default;
        Node = "";
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
        if (!world.TryGet<Dialogue>(speaker, out var dialogue) || dialogue.Record.IsEmpty) return false;
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

        var conversation = world.Resources.Get<Conversation>();
        conversation.Speaker = speaker;
        conversation.Listener = listener;
        conversation.Record = dialogue.Record;
        conversation.Node = node.Id;
        world.Events.Send(new Spoke(speaker, listener, node.Id));
        return true;
    }

    public static DialogueNode? Current(World world)
    {
        var conversation = world.Resources.Get<Conversation>();
        if (!conversation.Running) return null;
        return world.Resources.Get<RecordStore>().TryGet(conversation.Record, out DialogueRecord record)
            ? record.Node(conversation.Node) : null;
    }

    // May this be said? The reason comes back in words, because the screen shows it and because a
    // refusal nobody can read is a bug report waiting to happen (R17).
    public static bool CanPick(World world, Entity listener, DialogueOption option, out string why)
    {
        why = "";
        var requires = option.Requires;
        if (requires == null) return true;

        if (!requires.Tag.IsEmpty && !world.HasTag(listener, requires.Tag))
        {
            why = option.Refusal.Length > 0 ? option.Refusal : "not yet";
            return false;
        }
        if (!requires.WithoutTag.IsEmpty && world.HasTag(listener, requires.WithoutTag))
        {
            why = option.Refusal.Length > 0 ? option.Refusal : "already done";
            return false;
        }
        if (!requires.Item.IsEmpty && world.CountOf(listener, requires.Item) < Math.Max(requires.Count, 1))
        {
            why = option.Refusal.Length > 0 ? option.Refusal : "you do not have it";
            return false;
        }
        if (!requires.Faction.IsEmpty)
        {
            float standing = Factions.StandingWith(world, requires.Faction);
            if (standing < requires.MinStanding || standing > requires.MaxStanding)
            {
                why = option.Refusal.Length > 0 ? option.Refusal : "they do not trust you";
                return false;
            }
        }
        if (!requires.Quest.IsEmpty)
        {
            bool ok = true;
            if (requires.NotStarted) ok = world.Resources.Get<Journal>().Of(requires.Quest) == null;
            else if (requires.Finished) ok = Quests.IsFinished(world, requires.Quest);
            else if (requires.Active) ok = Quests.IsActive(world, requires.Quest);
            if (ok && requires.Stage.Length > 0)
                ok = Quests.StageOf(world, requires.Quest) == requires.Stage && Quests.IsActive(world, requires.Quest);
            if (!ok)
            {
                why = option.Refusal.Length > 0 ? option.Refusal : "there is nothing to say about that";
                return false;
            }
        }
        return true;
    }

    // Says it: applies what it does, then moves to the node it leads to (or ends). Returns false when
    // the option was refused, and nothing happened.
    public static bool Pick(World world, DialogueOption option)
    {
        var conversation = world.Resources.Get<Conversation>();
        if (!conversation.Running) return false;

        var listener = conversation.Listener;
        if (!CanPick(world, listener, option, out _)) return false;

        Apply(world, listener, option.Then);

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

    private static void Apply(World world, Entity listener, DialogueOutcome? outcome)
    {
        if (outcome == null) return;

        if (!outcome.GiveItem.IsEmpty) world.Give(listener, outcome.GiveItem, Math.Max(outcome.GiveCount, 1));
        if (!outcome.TakeItem.IsEmpty) world.Take(listener, outcome.TakeItem, Math.Max(outcome.TakeCount, 1));
        if (!outcome.Effect.IsEmpty) Effects.Apply(world, listener, outcome.Effect);
        if (!outcome.Faction.IsEmpty && outcome.Standing != 0f)
            Factions.Change(world, outcome.Faction, outcome.Standing);

        if (!outcome.StartQuest.IsEmpty) Quests.Start(world, outcome.StartQuest);
        if (!outcome.Quest.IsEmpty && outcome.Stage.Length > 0) Quests.SetStage(world, outcome.Quest, outcome.Stage);
        if (!outcome.FinishQuest.IsEmpty) Quests.Finish(world, outcome.FinishQuest);

        // Taking or giving may have finished an errand: "bring me five pelts" is met the moment the
        // fifth is in the bag, and the conversation that took them should not leave it hanging.
        Quests.Check(world);
    }
}
