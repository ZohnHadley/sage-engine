#nullable enable
using System;
using System.Numerics;

namespace Sage.Gameplay;

// The engine's quest objectives (issue #28): `kill` and `have` are the two it always had, `reach` and
// `talk` are new. Each is an entry of the `quest_objective` vocabulary, owned by the quests plugin.

// Members of a faction, or entities from a prefab, killed by the player (Died, 16 §3.5).
[QuestObjective("kill", Plugin = "sage.gameplay.quests")]
internal sealed class KillObjective : QuestObjective
{
    [Property(Tooltip = "Whose members count")]
    public RecordRef<FactionRecord> Faction;
    [Property(Tooltip = "Or which prefab's entities count")]
    public RecordRef<PrefabRecord> Prefab;

    public override int Notice(World world, in QuestHappening happened)
    {
        if (happened.Kind != QuestHappening.Killed) return 0;
        var killer = happened.Actor;
        if (killer.IsNull || !world.IsAlive(killer) || !killer.Tags.Has<PlayerControlled>()) return 0;
        if (Faction.IsEmpty && Prefab.IsEmpty) return 0;   // counts nothing

        // The victim's own `Faction` component, not Factions.FactionOf: counting "wolves killed" is a
        // fact about the wolf, and holds in a game without the factions plugin (issue #26).
        var victim = happened.Subject;
        RecordId faction = world.TryGet<Faction>(victim, out var member) ? member.Id : default;
        RecordId prefab = world.TryGet<FromPrefab>(victim, out var from) ? from.Prefab : default;
        if (!Faction.IsEmpty && Faction != faction) return 0;
        if (!Prefab.IsEmpty && Prefab != prefab) return 0;
        return 1;
    }

    public override string Describe(World world, int count) =>
        $"kill {count} {(!Prefab.IsEmpty ? Quests.Label(world, Prefab) : Quests.Label(world, Faction))}";
}

// An item in the bag, right now: measured, never counted, so dropping it undoes it.
[QuestObjective("have", Plugin = "sage.gameplay.quests")]
internal sealed class HaveObjective : QuestObjective
{
    [Property(Tooltip = "The item to carry")]
    public RecordRef<ItemRecord> Item;

    public override int? Measure(World world, Entity player) => player.IsNull ? 0 : world.CountOf(player, Item);

    public override string Describe(World world, int count) => $"carry {count} {Quests.Label(world, Item)}";
}

// Somewhere to get to: the player within `radius` of `at`, measured across the ground (a place is
// reached whatever the terrain's height there). `at` is absolute, so it means the same place however
// far the origin has moved (R6).
[QuestObjective("reach", Plugin = "sage.gameplay.quests")]
internal sealed class ReachObjective : QuestObjective
{
    [Property(Unit = "m", Tooltip = "Where to go, in absolute world coordinates")]
    public Vector3 At;
    [Property(Min = 0, Unit = "m", Tooltip = "How close counts as there")]
    public float Radius = 2f;
    [Property(Tooltip = "What the journal calls the place")]
    public string Place = "";

    public override bool Polls => true;

    public override int Notice(World world, in QuestHappening happened)
    {
        if (happened.Kind != QuestHappening.Polled || !world.TryGet<Transform>(happened.Actor, out var transform)) return 0;
        var position = world.Resources.TryGet<Origin>(out var origin) && origin != null
            ? origin.ToAbsolute(transform.LocalPosition) : transform.LocalPosition;
        return SageMath.DistanceXZ(position, At) <= Radius ? 1 : 0;
    }

    public override string Describe(World world, int count) => Place.Length > 0 ? $"reach {Place}" : "reach the place";

    public override bool TryGetPlace(out Vector3 at)
    {
        at = At;
        return true;
    }
}

// Somebody to speak to: a conversation with a speaker of this dialogue (or from this prefab) that
// reaches `node`, or begins when no node is named (Spoke, 16 §3.5).
[QuestObjective("talk", Plugin = "sage.gameplay.quests")]
internal sealed class TalkObjective : QuestObjective
{
    [Property(Tooltip = "The conversation: a speaker with this dialogue counts")]
    public RecordRef<DialogueRecord> Dialogue;
    [Property(Tooltip = "Or a speaker from this prefab")]
    public RecordRef<PrefabRecord> Prefab;
    [Property(Tooltip = "The node the conversation must reach; empty = its first line")]
    public string Node = "";

    public override int Notice(World world, in QuestHappening happened)
    {
        if (happened.Kind != QuestHappening.Talked) return 0;
        var speaker = happened.Subject;
        RecordId said = world.TryGet<global::Sage.Gameplay.Dialogue>(speaker, out var dialogue) ? dialogue.Record : default;
        if (!Dialogue.IsEmpty && Dialogue != said) return 0;
        if (!Prefab.IsEmpty && (!world.TryGet<FromPrefab>(speaker, out var from) || from.Prefab != Prefab)) return 0;
        if (Dialogue.IsEmpty && Prefab.IsEmpty) return 0;   // counts nobody

        if (Node.Length > 0) return happened.Node == Node ? 1 : 0;
        // No node named: the conversation starting, which is the first line of the speaker's record.
        return world.Records().TryGet(said, out DialogueRecord record) && record.Node(record.Start)?.Id == happened.Node ? 1 : 0;
    }

    public override string Describe(World world, int count)
    {
        if (!Dialogue.IsEmpty && world.Records().TryGet(Dialogue, out DialogueRecord record) && record.Label.Length > 0)
            return $"talk to {record.Label}";
        return $"talk to {(!Prefab.IsEmpty ? Prefab.Id.Name : Dialogue.Id.Name)}";
    }
}

// What the quests plugin hears besides deaths (issue #28): conversations, for `talk`, and where the
// player is, for `reach`. Cheap when nobody is on a quest that asks — one journal look per tick.
[System("sage.quests.watch", Phase.Gameplay, After = new[] { "sage.effects.tick" })]
internal sealed class QuestWatchSystem : ISystem
{
    private readonly EventReader<Spoke> _spoke;
    private readonly Query<Transform> _players;

    public QuestWatchSystem(World world)
    {
        _spoke = world.Events.Reader<Spoke>(this);
        _players = world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>());
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        if (_spoke.HasPending)
            foreach (ref readonly var spoke in _spoke.Read())
                Quests.Notice(world, new QuestHappening(QuestHappening.Talked, spoke.Speaker, spoke.Listener) { Node = spoke.Node });

        if (Quests.JournalOf(world) is not { Entries.Count: > 0 }) return;
        foreach (var (transforms, entities) in _players.Chunks)
            if (transforms.Span.Length > 0)
            {
                Quests.Poll(world, entities.EntityAt(0));   // the local player; a second is a network game's question
                return;
            }
    }
}
