#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Sage.Gameplay;

// Bridge I/O (issue #91, REDESIGN §4.3 stage 3): gameplay's own inputs and outputs, so a level wires
// to a quest, a conversation, an item, an effect or a faction the way it wires to a door. Each is
// registered by the plugin that owns what it touches, in that plugin's Init, so a game without quests
// has no `SetStage` and a wire that sends one is an error when the level loads.
//
//   input          owner                      parameter              at
//   SetStage       sage.gameplay.quests       "quest stage" | "stage" any entity (a bare stage: the target's quest_watch quest)
//   StartDialogue  sage.gameplay.dialogue     —                      a sage:dialogue speaker; the listener is the activator, else the player
//   GiveItem       sage.gameplay.items        "item [count]"         a sage:inventory
//   ApplyEffect    sage.gameplay.attributes   "effect [magnitude]"   any entity; the activator is the source
//   SetFaction     sage.gameplay.factions     "faction" | ""         any entity; nothing clears it
//
//   output          owner                     fired on                    activator       value
//   OnStageChanged  sage.gameplay.quests      every quest_watch on it     the player      the stage
//   OnQuestFinished sage.gameplay.quests      every quest_watch on it     the player      the stage
//   OnDeath         sage.gameplay.attributes  the victim (Died)           the killer      —
//   OnDamaged       sage.gameplay.combat      the one hurt (Damaged)      the attacker    the damage done
//   OnPickedUp      sage.gameplay.items       the pickup, as it is taken  who took it     —
//
// Parameters are words separated by spaces, not commas, because a `.map` wire is comma-separated.
// A parameter that names nothing is a warning at the entity, and the input does nothing.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities and bridges (#91)
public static class BridgeIO
{
    public const string SetStage = "SetStage";
    public const string StartDialogue = "StartDialogue";
    public const string GiveItem = "GiveItem";
    public const string ApplyEffect = "ApplyEffect";
    public const string SetFaction = "SetFaction";

    public const string OnStageChanged = "OnStageChanged";
    public const string OnQuestFinished = "OnQuestFinished";
    public const string OnDeath = "OnDeath";
    public const string OnDamaged = "OnDamaged";
    public const string OnPickedUp = "OnPickedUp";

    // ---- quests (QuestsModule.Init) ----

    internal static void RegisterQuests(Engine engine)
    {
        engine.Inputs.Register(SetStage, static (World world, in IOContext io) =>
        {
            var words = Words(io.Parameter);
            RecordId quest;
            string stage;
            if (words.Length >= 2)
            {
                quest = Resolve(world, "quest", words[0], io, SetStage);
                stage = words[1];
            }
            else if (words.Length == 1 && world.TryGet<QuestWatch>(io.Self, out var watch) && !watch.Quest.IsEmpty)
            {
                quest = watch.Quest;
                stage = words[0];
            }
            else
            {
                Log.Warn(LogCat.Events, $"I/O: {SetStage}({io.Parameter}) at {World.Describe(io.Self)}: the parameter is "
                                      + "\"quest stage\", or a stage at something with a quest_watch");
                return;
            }
            if (quest.IsEmpty) return;
            // Not on it yet: this puts it in the journal first, as a script that sets a stage means.
            if (Quests.IsNotStarted(world, quest)) Quests.Start(world, quest);
            Quests.SetStage(world, quest, stage);
        });
        engine.Outputs.Declare(OnStageChanged, "The quest this entity's quest_watch names started or moved to a stage; hands on the stage.");
        engine.Outputs.Declare(OnQuestFinished, "The quest this entity's quest_watch names was finished; hands on the stage it ended at.");
    }

    // ---- dialogue (DialogueModule.Init) ----

    internal static void RegisterDialogue(Engine engine)
    {
        engine.Inputs.Register<Dialogue>(StartDialogue, static (World world, in IOContext io) =>
        {
            var listener = world.IsAlive(io.Activator) ? io.Activator : BridgeIO.PlayerOf(world);
            if (!DialogueRules.Start(world, io.Self, listener))
                Log.Debug(LogCat.Events, $"I/O: {StartDialogue} at {World.Describe(io.Self)}: nothing to say");
        });
    }

    // ---- items (ItemsModule.Init) ----

    internal static void RegisterItems(Engine engine)
    {
        engine.Inputs.Register<Inventory>(GiveItem, static (World world, in IOContext io) =>
        {
            var words = Words(io.Parameter);
            if (words.Length == 0)
            {
                Log.Warn(LogCat.Events, $"I/O: {GiveItem} at {World.Describe(io.Self)}: the parameter is \"item [count]\"");
                return;
            }
            var item = Resolve(world, "item", words[0], io, GiveItem);
            if (item.IsEmpty) return;
            int count = 1;
            if (words.Length > 1 && (!int.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count < 1))
            {
                Log.Warn(LogCat.Events, $"I/O: {GiveItem}({io.Parameter}) at {World.Describe(io.Self)}: the count should be a whole number from 1");
                return;
            }
            world.Give(io.Self, item, count);
        });
        engine.Outputs.Declare(OnPickedUp, "This pickup was taken (sage:pickup); the activator is who took it.");
    }

    // ---- attributes (AttributesModule.Init) ----

    internal static void RegisterAttributes(Engine engine)
    {
        engine.Inputs.Register(ApplyEffect, static (World world, in IOContext io) =>
        {
            var words = Words(io.Parameter);
            if (words.Length == 0)
            {
                Log.Warn(LogCat.Events, $"I/O: {ApplyEffect} at {World.Describe(io.Self)}: the parameter is \"effect [magnitude]\"");
                return;
            }
            var effect = Resolve(world, "effect", words[0], io, ApplyEffect);
            if (effect.IsEmpty) return;
            float magnitude = 1f;
            if (words.Length > 1 && !float.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out magnitude))
            {
                Log.Warn(LogCat.Events, $"I/O: {ApplyEffect}({io.Parameter}) at {World.Describe(io.Self)}: the magnitude should be a number");
                return;
            }
            Effects.Apply(world, io.Self, effect, io.Activator, magnitude);
        });
        engine.Outputs.Declare(OnDeath, "This entity died (Died); the activator is its killer, if anybody.");
    }

    // ---- combat (CombatModule.Init) ----

    internal static void RegisterCombat(Engine engine) =>
        engine.Outputs.Declare(OnDamaged, "This entity was hurt (Damaged); the activator is the attacker; hands on the damage done.");

    // ---- factions (FactionsModule.Init) ----

    internal static void RegisterFactions(Engine engine)
    {
        engine.Inputs.Register(SetFaction, static (World world, in IOContext io) =>
        {
            var words = Words(io.Parameter);
            if (words.Length == 0)
            {
                if (world.Has<Faction>(io.Self)) world.Remove<Faction>(io.Self);   // nobody's
                return;
            }
            var faction = Resolve(world, "faction", words[0], io, SetFaction);
            if (faction.IsEmpty) return;
            if (world.Has<Faction>(io.Self)) world.Get<Faction>(io.Self).Id = faction;
            else world.Add(io.Self, new Faction { Id = faction });
        });
    }

    // ---- helpers ----

    // The player: the first entity the local player controls, or nobody.
    internal static Entity PlayerOf(World world)
    {
        foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities)
            return entity;
        return default;
    }

    private static string[] Words(string parameter) =>
        parameter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // A record by id or bare name; empty, having said why at the entity, when there is none.
    private static RecordId Resolve(World world, string type, string text, in IOContext io, string input)
    {
        var records = world.Records();
        RecordId id;
        try { id = RecordId.Parse(text, "sage"); }
        catch (FormatException) { id = default; }
        if (!id.IsEmpty && text.Contains(':') && records.Exists(type, id)) return id;
        if (!id.IsEmpty && !text.Contains(':'))
            foreach (var candidate in records.Ids(type))
                if (candidate.Name == id.Name) return candidate;
        Log.Warn(LogCat.Events, $"I/O: {input}({io.Parameter}) at {World.Describe(io.Self)}: there is no {type} called '{text}'");
        return default;
    }
}

// "The quest this entity listens to" (issue #91): an entity with one fires OnStageChanged and
// OnQuestFinished when that quest moves, and takes a bare `SetStage <stage>` for it. Put it on a relay
// to start a sequence when the player reaches a stage.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities and bridges (#91)
[Component("sage:quest_watch")]
public struct QuestWatch : IComponent
{
    [RecordRef("quest"), Property(Tooltip = "The quest it listens to")]
    public RecordId Quest;
}

// "quest_watch": "sandbox:thin_the_wood"
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities and bridges (#91)
[PrefabPart("quest_watch", Plugin = "sage.gameplay.quests", Shorthand = nameof(Quest))]
public sealed class QuestWatchPart : IPrefabPart
{
    [Property(Tooltip = "The quest it listens to: OnStageChanged when it moves")]
    public RecordRef<QuestRecord> Quest;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Quest.IsEmpty) { ctx.Error("needs a quest"); return; }
        ctx.World.Add(ctx.Entity, new QuestWatch { Quest = Quest });
    }
}

// QuestChanged → OnStageChanged / OnQuestFinished on every quest_watch of that quest.
[System("sage.io.quests", Phase.Gameplay)]
internal sealed class QuestOutputSystem : ISystem
{
    private readonly EventReader<QuestChanged> _changed;
    private readonly Query<QuestWatch> _watches;

    public QuestOutputSystem(World world)
    {
        _changed = world.Events.Reader<QuestChanged>(this);
        _watches = world.Query<QuestWatch>();
    }

    public void Run(in SystemContext ctx)
    {
        if (!_changed.HasPending) return;
        var world = ctx.World;
        var player = BridgeIO.PlayerOf(world);
        foreach (ref readonly var changed in _changed.Read())
        {
            foreach (var (watches, entities) in _watches.Chunks)
            {
                var w = watches.Span;
                for (int i = 0; i < w.Length; i++)
                {
                    if (w[i].Quest != changed.Quest) continue;
                    var entity = entities.EntityAt(i);
                    // Firing only queues (entity I/O), so it is safe inside the query.
                    world.FireOutput(entity, changed.Finished ? BridgeIO.OnQuestFinished : BridgeIO.OnStageChanged, player, changed.Stage);
                }
            }
        }
    }
}

// Died → OnDeath on the victim, before the rules may destroy it (they run in sage.effects.deaths).
[System("sage.io.deaths", Phase.Gameplay, After = new[] { "sage.effects.tick" }, Before = new[] { "sage.effects.deaths" })]
internal sealed class DeathOutputSystem : ISystem
{
    private readonly EventReader<Died> _died;

    public DeathOutputSystem(World world) => _died = world.Events.Reader<Died>(this);

    public void Run(in SystemContext ctx)
    {
        if (!_died.HasPending) return;
        foreach (ref readonly var died in _died.Read())
            ctx.World.FireOutput(died.Victim, BridgeIO.OnDeath, died.Killer);
    }
}

// Damaged → OnDamaged on whoever was hurt, with the damage done as its value.
[System("sage.io.damage", Phase.Gameplay, After = new[] { "?sage.effects.tick" })]
internal sealed class DamageOutputSystem : ISystem
{
    private readonly EventReader<Damaged> _damaged;

    public DamageOutputSystem(World world) => _damaged = world.Events.Reader<Damaged>(this);

    public void Run(in SystemContext ctx)
    {
        if (!_damaged.HasPending) return;
        foreach (ref readonly var damaged in _damaged.Read())
            ctx.World.FireOutput(damaged.Hit.Target, BridgeIO.OnDamaged, damaged.Hit.Attacker, damaged.Applied);
    }
}
