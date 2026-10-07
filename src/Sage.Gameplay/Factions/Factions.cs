#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Gameplay;

// Who counts as an enemy (docs/design/16 §3.5, TODO F24).
//
// Every fight the engine already runs asks this question and nobody had answered it: a swing hits
// whatever is solid, a fireball burns whatever it catches, and a creature chased the player because the
// player was the only thing it could see. **Factions are that answer**, and they are one rule consulted
// from three places — what an AI looks for, what a spell is allowed to catch, and what happens to the
// player's name when they kill somebody.
//
// The model is Daggerfall's, which is smaller than it looks: a faction has an opinion of other factions,
// and a separate one of *you* that moves as you act. Guilds, ranks and the rest of Daggerfall's social
// machinery are content stacked on that, not engine.

// What one side thinks of another. Deliberately three values and not a number: "hostile" is a decision,
// and the number that produced it (reputation) belongs to the player alone.
public enum Stance
{
    Neutral,
    Ally,
    Hostile,
}

// One line of a faction's opinion table, as data: `{ "faction": "sandbox:bandits", "stance": "Hostile" }`.
// A list rather than a map because record ids are not JSON object keys (05 §3.5).
public sealed class FactionRelation
{
    public RecordRef<FactionRecord> Faction;
    public Stance Stance = Stance.Neutral;
}

// A group with opinions (16 §3.5). Everything about *who* fights whom is here rather than in combat.
[Record("faction", Plugin = "sage.gameplay.factions")]
public sealed class FactionRecord
{
    public string Label = "";

    // What it thinks of other factions. Anything not listed gets `Default`, and a faction is always its
    // own ally without saying so.
    public List<FactionRelation> Relations = new();

    public Stance Default = Stance.Neutral;

    // And what it thinks of the player, which is a number because the player can change it. `Standing`
    // is where a new game starts; the thresholds are where the number turns into a stance.
    public float Standing;
    public float HostileBelow = -25f;
    public float FriendlyAbove = 25f;

    // What killing one of its members does to the player's standing with it, and with its allies.
    public float KillCost = 20f;

    // The faction a player-controlled entity belongs to when it has no `Faction` component of its own
    // is the game's to say: `playerFaction` in its gameplay_conventions (issue #26).
}

// Which faction an entity belongs to. Entities without one are nobody's ally and nobody's enemy — except
// that a creature with no faction at all still treats the player as an enemy, which is what "a monster"
// means in the absence of any social model, and is exactly what the engine did before F24.
[Component("sage:faction")]
public struct Faction : IComponent
{
    public RecordId Id;
}

// How every faction feels about the player, and the only part of this that is per game rather than per
// content (09 §3.1): it is saved, because a world that forgot you had robbed it is not the world you
// left. A list rather than a dictionary so it serialises as it stands, and a dozen factions are not
// worth a hash table.
[SavedResource("reputation", Plugin = "sage.gameplay.factions")]
public sealed class Reputation
{
    public sealed class Standing
    {
        public RecordId Faction { get; set; }
        public float Value { get; set; }
    }

    public List<Standing> Standings { get; set; } = new();

    public float Of(RecordId faction)
    {
        foreach (var standing in Standings)
            if (standing.Faction == faction) return standing.Value;
        return float.NaN;      // never asked about: the caller falls back to the record's start value
    }

    public void Set(RecordId faction, float value)
    {
        foreach (var standing in Standings)
            if (standing.Faction == faction) { standing.Value = value; return; }
        Standings.Add(new Standing { Faction = faction, Value = value });
    }
}

// Somebody's opinion of the player changed, for a HUD to say so and a quest to notice (F24).
[GameEvent]
public readonly record struct ReputationChanged(RecordId Faction, float Value, float Change);

public static class Factions
{
    // What `a`'s faction thinks of `b`'s, from the records alone. Same faction is always Ally; after
    // that it is what the table says, then the default.
    public static Stance Between(RecordStore records, RecordId a, RecordId b)
    {
        if (a.IsEmpty || b.IsEmpty) return Stance.Neutral;
        if (a == b) return Stance.Ally;
        if (!TryGetFaction(records, a, out var record)) return Stance.Neutral;

        foreach (var relation in record.Relations)
            if (relation.Faction == b) return relation.Stance;
        return record.Default;
    }

    // Whether two factions fight on sight, from the records alone: either one's table (or default) says
    // Hostile. What the off-screen simulation asks of two agents (issue 4g-6), which are no entities.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public static bool AreHostile(RecordStore records, RecordId a, RecordId b) =>
        a != b && (Between(records, a, b) == Stance.Hostile || Between(records, b, a) == Stance.Hostile);

    // The whole question: what does `viewer` think of `other`, right now, in this world.
    //
    // The player is the special case, because the player is the only one whose standing moves: a
    // creature's opinion of them is its reputation number against its own thresholds, not a table entry.
    public static Stance Toward(World world, Entity viewer, Entity other)
    {
        if (viewer == other) return Stance.Ally;
        if (!world.IsAlive(viewer) || !world.IsAlive(other)) return Stance.Neutral;

        // A guard is after whoever owes its law a bounty, whatever its faction thinks of them (issue #389).
        if (Crime.Pursues(world, viewer, other)) return Stance.Hostile;

        var records = world.Resources.Get<RecordStore>();
        RecordId mine = FactionOf(world, viewer), theirs = FactionOf(world, other);
        RecordId player = world.Conventions().PlayerFaction;

        // A creature that belongs to nothing still knows an intruder when it sees one. This is the
        // engine's behaviour from before there were factions, kept so that content which says nothing
        // about them behaves as it always did.
        if (mine.IsEmpty)
            return other.Tags.Has<PlayerControlled>() && !viewer.Tags.Has<PlayerControlled>()
                ? Stance.Hostile : Stance.Neutral;

        if (!player.IsEmpty && theirs == player && mine != player)
            return TowardPlayer(world, records, mine);
        if (!player.IsEmpty && mine == player && theirs != player)
            return TowardPlayer(world, records, theirs);   // symmetric: how they feel is how you are treated

        return Between(records, mine, theirs);
    }

    // A faction's opinion of the player is a number, and these are the two places it crosses into being
    // a decision. Between the thresholds it is neutral, which is where most of the world sits.
    private static Stance TowardPlayer(World world, RecordStore records, RecordId faction)
    {
        if (!TryGetFaction(records, faction, out var record)) return Stance.Neutral;
        float standing = StandingWith(world, faction);
        return standing <= record.HostileBelow ? Stance.Hostile
             : standing >= record.FriendlyAbove ? Stance.Ally
             : Stance.Neutral;
    }

    public static bool AreEnemies(World world, Entity a, Entity b) => Toward(world, a, b) == Stance.Hostile;

    // May `attacker` hurt `target`? Anything that is not its ally. **A player may hurt anyone** — swinging
    // at a townsman is allowed and it is their reputation that pays for it — so this is asked by the
    // things that choose targets on a creature's behalf: a spell's blast, an AI's sweep (16 §3.2).
    public static bool MayHurt(World world, Entity attacker, Entity target)
    {
        if (attacker == target) return true;
        if (!world.IsAlive(attacker) || !world.IsAlive(target)) return true;
        if (attacker.Tags.Has<PlayerControlled>()) return true;
        return Toward(world, attacker, target) != Stance.Ally;
    }

    // Nobody's, in a game without the factions plugin (issue #26): then who fights whom is the engine's
    // pre-faction answer above — a creature is hostile to the player — whatever components say.
    public static RecordId FactionOf(World world, Entity entity)
    {
        if (world.Records().TypeNameOf(typeof(FactionRecord)) == null) return default;
        if (world.TryGet<Faction>(entity, out var faction) && !faction.Id.IsEmpty) return faction.Id;
        return entity.Tags.Has<PlayerControlled>() ? world.Conventions().PlayerFaction : default;
    }

    // A faction record, when this game has factions at all: without the plugin the record type is not
    // registered, and asking the store for one would be asking about a type it has never heard of.
    public static bool TryGetFaction(RecordStore records, RecordId id, out FactionRecord record)
    {
        record = null!;
        return !id.IsEmpty && records.TypeNameOf(typeof(FactionRecord)) != null && records.TryGet(id, out record);
    }

    // The player's standing with a faction: what they have earned, or what the record says they start
    // with. Reading it is what installs the starting value, so a HUD and a rule see the same number.
    // Without the factions plugin there is no reputation to keep, and every standing is 0.
    public static float StandingWith(World world, RecordId faction)
    {
        if (faction.IsEmpty || !world.Resources.TryGet<Reputation>(out var reputation) || reputation == null) return 0f;
        float value = reputation.Of(faction);
        if (!float.IsNaN(value)) return value;

        float start = TryGetFaction(world.Resources.Get<RecordStore>(), faction, out var record) ? record.Standing : 0f;
        reputation.Set(faction, start);
        return start;
    }

    // Changes it, and says so. Allies of the faction take a fifth of the same change, which is what
    // makes a reputation a web rather than a column of unrelated numbers.
    public static void Change(World world, RecordId faction, float amount)
    {
        if (faction.IsEmpty || amount == 0f || !world.Resources.TryGet<Reputation>(out _)) return;
        Apply(world, faction, amount);

        var records = world.Resources.Get<RecordStore>();
        if (!TryGetFaction(records, faction, out var record)) return;
        foreach (var relation in record.Relations)
        {
            if (relation.Stance == Stance.Neutral) continue;
            // An enemy of the faction you have wronged thinks a little better of you, and an ally a
            // little worse. A fifth, so the web is felt and not fought.
            Apply(world, relation.Faction, relation.Stance == Stance.Ally ? amount * 0.2f : amount * -0.2f);
        }
    }

    private static void Apply(World world, RecordId faction, float amount)
    {
        float before = StandingWith(world, faction);
        float after = Math.Clamp(before + amount, -100f, 100f);
        if (Math.Abs(after - before) < 0.001f) return;

        world.Resources.Get<Reputation>().Set(faction, after);
        world.Events.Send(new ReputationChanged(faction, after, after - before));
    }

    // The death seam's social half (16 §3.3): killing somebody is the commonest way to change what a
    // faction thinks of you, and the only one the engine knows about on its own. FactionDeathSystem
    // calls it for every `Died` (issue #26).
    public static void OnKilled(World world, Entity victim, Entity killer)
    {
        if (killer.IsNull || !world.IsAlive(killer) || !killer.Tags.Has<PlayerControlled>()) return;

        var faction = FactionOf(world, victim);
        if (faction.IsEmpty || faction == world.Conventions().PlayerFaction) return;
        if (!TryGetFaction(world.Resources.Get<RecordStore>(), faction, out var record)) return;

        Change(world, faction, -record.KillCost);
    }
}

// What a death costs the killer's name (16 §3.5, issue #26): reads `Died` rather than being called by
// the effect system, so a game without factions has nothing here to call. Before the rules, so they
// see the standing already moved, and while the victim still exists to ask its faction.
[System("sage.factions.deaths", Phase.Gameplay, After = new[] { "sage.effects.tick" }, Before = new[] { "sage.effects.deaths" })]
internal sealed class FactionDeathSystem : ISystem
{
    private readonly EventReader<Died> _died;

    public FactionDeathSystem(World world) => _died = world.Events.Reader<Died>(this);

    public void Run(in SystemContext ctx)
    {
        if (!_died.HasPending) return;
        foreach (ref readonly var died in _died.Read())
            Factions.OnKilled(ctx.World, died.Victim, died.Killer);
    }
}
