#nullable enable
using System;
using System.Collections.Generic;
using Sage.UI;

namespace Sage.Kits.Rpg;

// Faction ranks with promotion requirements (issue #389, phase 4r): Daggerfall's guilds as data. A faction's
// `faction_ranks` record is its ladder, lowest first; joining puts you on the first rung and promotion climbs one
// at a time, each rung asking its `requires` first — the base's ICondition vocabulary, so standing, a skill, a
// level, a finished quest or no bounty are all one list:
//
//   { "type": "faction_ranks", "id": "fighters", "faction": "fighters",
//     "ranks": [ { "name": "Associate", "requires": [ { "bounty": "town", "below": 1 } ] },
//                { "name": "Swordsman", "requires": [ { "standing": "fighters", "min": 10 }, { "skill": "blade", "atLeast": 3 } ] } ] }
//
// Rank-gated content asks `{ "rank": "fighters", "atLeast": 1 }` (the second rung) from a dialogue option, a door's
// trigger or a perk; `join_faction` and `promote` are the actions a guildmaster's conversation runs. The ranks an
// entity holds are saved with it (`rpg:memberships`). The `rpg:factions` screen lists the ladders: where you stand,
// and what the next rung asks.

// One rung: what it is called and what reaching it asks.
public sealed class FactionRank
{
    [Property(Tooltip = "What the rank is called")]
    public string Name = "";

    [Property(Tooltip = "Must all hold to reach it: { \"standing\": \"guild\", \"min\": 10 }, { \"skill\": \"blade\", \"atLeast\": 3 }")]
    public List<ICondition> Requires = new();
}

[Record("faction_ranks", Plugin = RpgKitModule.Id)]
public sealed class FactionRanksRecord
{
    [Property(Tooltip = "The faction these are the ranks of")]
    public RecordRef<FactionRecord> Faction;

    [Property(Tooltip = "The ranks, lowest first: joining reaches the first")]
    public List<FactionRank> Ranks = new();

    internal static void Check(FactionRanksRecord record, RecordCheck check)
    {
        if (record.Faction.IsEmpty) check.Error(nameof(Faction), "names no faction");
        if (record.Ranks.Count == 0) check.Error(nameof(Ranks), "has no ranks: nobody could join");
        for (int i = 0; i < record.Ranks.Count; i++)
            if (record.Ranks[i].Name.Length == 0) check.Error($"{nameof(Ranks)}[{i}]", "a rank needs a name");
    }
}

// One faction an entity belongs to, and its rank there (0: the first rung).
public struct Membership
{
    public RecordId Faction;
    public int Rank;
}

// The factions an entity has joined, in the order it joined them. Saved; added with the first.
[Component("rpg:memberships")]
public struct Memberships : IComponent
{
    [Property(Tooltip = "The factions it has joined, and its rank in each")]
    public List<Membership>? Of;
}

// Somebody joined a faction (Rank 0) or rose in it.
[GameEvent]
public readonly record struct RankChanged(Entity Entity, RecordId Faction, int Rank);

public static class FactionRanks
{
    // A faction's ladder, if it has one.
    public static FactionRanksRecord? LadderOf(World world, RecordId faction)
    {
        var records = world.Records();
        if (faction.IsEmpty || records.TypeNameOf(typeof(FactionRanksRecord)) == null) return null;
        foreach (var id in records.Ids("faction_ranks"))
            if (records.TryGet(id, out FactionRanksRecord ladder) && ladder.Faction.Id == faction) return ladder;
        return null;
    }

    // Its rank in a faction: 0 for the first rung, -1 when it is not a member.
    public static int RankOf(World world, Entity entity, RecordId faction)
    {
        if (!world.TryGet<Memberships>(entity, out var memberships) || memberships.Of == null) return -1;
        foreach (var membership in memberships.Of)
            if (membership.Faction == faction) return membership.Rank;
        return -1;
    }

    public static bool IsMember(World world, Entity entity, RecordId faction) => RankOf(world, entity, faction) >= 0;

    // What its rank is called there, or empty when it is not a member.
    public static string RankName(World world, Entity entity, RecordId faction)
    {
        int rank = RankOf(world, entity, faction);
        var ladder = LadderOf(world, faction);
        return rank < 0 || ladder == null || rank >= ladder.Ranks.Count ? "" : ladder.Ranks[rank].Name;
    }

    // Whether it may take the next rung now — join, for one not yet a member — and when not, why.
    public static bool CanRise(World world, Entity entity, RecordId faction, out string why)
    {
        var ladder = LadderOf(world, faction);
        if (ladder == null) { why = "it has no ranks"; return false; }
        int next = RankOf(world, entity, faction) + 1;
        if (next >= ladder.Ranks.Count) { why = "you hold its highest rank"; return false; }
        if (!Conditions.TestAll(ladder.Ranks[next].Requires, new ConditionContext(world, entity), out why))
        {
            if (why.Length == 0) why = "you do not meet its requirements";
            return false;
        }
        why = "";
        return true;
    }

    // Takes the next rung (joining, if not a member) when CanRise allows it.
    public static bool Rise(World world, Entity entity, RecordId faction, out string why)
    {
        if (!CanRise(world, entity, faction, out why)) return false;
        SetRank(world, entity, faction, RankOf(world, entity, faction) + 1);
        return true;
    }

    // Puts it at a rank, asking nothing (a quest's reward, the console); -1 leaves the faction.
    public static bool SetRank(World world, Entity entity, RecordId faction, int rank)
    {
        if (!world.IsAlive(entity) || faction.IsEmpty) return false;
        var ladder = LadderOf(world, faction);
        if (ladder == null) return false;
        rank = Math.Clamp(rank, -1, ladder.Ranks.Count - 1);
        if (rank == RankOf(world, entity, faction)) return false;

        if (!world.Has<Memberships>(entity)) world.Add(entity, new Memberships { Of = new List<Membership>() });
        var list = world.Get<Memberships>(entity).Of ??= new List<Membership>();
        int at = list.FindIndex(m => m.Faction == faction);
        if (rank < 0) { if (at >= 0) list.RemoveAt(at); }
        else if (at >= 0) list[at] = new Membership { Faction = faction, Rank = rank };
        else list.Add(new Membership { Faction = faction, Rank = rank });

        if (rank >= 0)
        {
            Log.Info(LogCat.Gameplay, $"{World.Describe(entity)} is now {ladder.Ranks[rank].Name} of {faction}");
            if (entity.Tags.Has<PlayerControlled>()) world.Say($"{ladder.Ranks[rank].Name} of {Label(world, faction)}", MessageKind.Good, 3f);
            world.Events.Send(new RankChanged(entity, faction, rank));
        }
        return true;
    }

    private static string Label(World world, RecordId faction) =>
        world.Records().TryGet(faction, out FactionRecord record) && record.Label.Length > 0 ? record.Label : faction.Name;

    // The factions screen's rows and the `ranks` command's: every faction with ranks, ticked where it is a
    // member, with its rank, its standing and any bounty; greyed with why when it cannot rise.
    public static Panel Panel(World world, Entity who, Panel? into = null)
    {
        var panel = (into ?? new Panel()).Begin("Factions", who);
        var records = world.Records();
        if (records.TypeNameOf(typeof(FactionRanksRecord)) == null) return panel;
        foreach (var id in records.Ids("faction_ranks"))
        {
            if (!records.TryGet(id, out FactionRanksRecord ladder) || ladder.Faction.IsEmpty) continue;
            var faction = ladder.Faction.Id;
            int rank = RankOf(world, who, faction);
            bool can = CanRise(world, who, faction, out string why);
            string detail = rank < 0 ? "not a member" : ladder.Ranks[Math.Min(rank, ladder.Ranks.Count - 1)].Name;
            detail += $", standing {Factions.StandingWith(world, faction):0}";
            float bounty = Crime.BountyWith(world, faction);
            if (bounty > 0f) detail += $", bounty {bounty:0}";
            if (!can && rank + 1 < ladder.Ranks.Count) why = $"{ladder.Ranks[rank + 1].Name}: {why}";
            panel.Add(PanelRow.Of(faction, Label(world, faction), detail, 1, rank >= 0, can, can ? "" : why));
        }
        return panel;
    }

    // `ranks`: the local player's factions; `rise <faction>`: join or take the next rank; `set_rank <faction> <n>`: a cheat.
    internal static void RegisterCommands(Engine engine)
    {
        engine.CVars.RegisterCommand("ranks", CVarFlags.None, "The local player's factions, ranks and what the next rank asks.", _ =>
            engine.ForEachPlayer((world, entity) => Panel(world, entity).Log(LogCat.Console)));

        engine.CVars.RegisterCommand("rise", CVarFlags.None, "rise <faction>: join a faction, or take its next rank, if you meet what it asks.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "rise <faction>"); return; }
            var faction = engine.Records.Resolve("faction", a[0]);
            if (faction.IsEmpty) return;
            engine.ForEachPlayer((world, entity) =>
            {
                if (Rise(world, entity, faction, out string why)) Log.Info(LogCat.Console, $"{World.Describe(entity)} is now {RankName(world, entity, faction)} of {faction}");
                else Log.Warn(LogCat.Console, $"{World.Describe(entity)} cannot rise in {faction}: {why}");
            });
        });

        engine.CVars.RegisterCommand("set_rank", CVarFlags.Cheat, "set_rank <faction> <rank>: put the local player at a rank (0 the first, -1 out).", a =>
        {
            if (a.Count < 2 || !int.TryParse(a[1], out int rank)) { Log.Warn(LogCat.Console, "set_rank <faction> <rank>"); return; }
            var faction = engine.Records.Resolve("faction", a[0]);
            if (faction.IsEmpty) return;
            engine.ForEachPlayer((world, entity) => SetRank(world, entity, faction, rank));
        });
    }
}

// ---- the condition rank-gated content asks, and the actions a guildmaster runs --------------------------

#pragma warning disable CS0649   // never assigned: the vocabulary reader fills them from content

// `{ "rank": "fighters", "atLeast": 1 }`: the subject is a member at that rank or higher (0: any member).
[Condition("rank", Plugin = RpgKitModule.Id)]
internal sealed class RankCondition : ICondition
{
    [EntryValue, Property(Tooltip = "The faction")]
    public RecordRef<FactionRecord> Faction;
    [Property(Min = 0, Tooltip = "The least rank that holds: 0 the first")]
    public int AtLeast;

    public bool Test(in ConditionContext c, out string why)
    {
        why = "your rank is too low";
        return Faction.IsEmpty || FactionRanks.RankOf(c.World, c.Subject, Faction.Id) >= Math.Max(AtLeast, 0);
    }
}

// `{ "join_faction": "fighters" }`: the subject joins, if the first rank's requirements hold.
[Action("join_faction", Plugin = RpgKitModule.Id)]
internal sealed class JoinFactionAction : IAction
{
    [EntryValue, Property(Tooltip = "The faction")]
    public RecordRef<FactionRecord> Faction;

    public void Run(in ActionContext c)
    {
        if (Faction.IsEmpty || FactionRanks.IsMember(c.World, c.Subject, Faction.Id)) return;
        if (!FactionRanks.Rise(c.World, c.Subject, Faction.Id, out string why) && c.Subject.Tags.Has<PlayerControlled>())
            c.World.Say(why, MessageKind.Bad, 3f);
    }
}

// `{ "promote": "fighters" }`: the subject takes the next rank, if its requirements hold.
[Action("promote", Plugin = RpgKitModule.Id)]
internal sealed class PromoteAction : IAction
{
    [EntryValue, Property(Tooltip = "The faction")]
    public RecordRef<FactionRecord> Faction;

    public void Run(in ActionContext c)
    {
        if (Faction.IsEmpty || !FactionRanks.IsMember(c.World, c.Subject, Faction.Id)) return;
        if (!FactionRanks.Rise(c.World, c.Subject, Faction.Id, out string why) && c.Subject.Tags.Has<PlayerControlled>())
            c.World.Say(why, MessageKind.Bad, 3f);
    }
}

#pragma warning restore CS0649

// The factions screen (issue #389): every faction with ranks, your rank and standing in it, and what its next
// rank asks, greyed with why. Confirm joins or rises. The rows are the `ranks` command's (FactionRanks.Panel).
//   screen rpg:factions — layout rpg:list
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = RpgKitModule.ExperimentalUrl)]
[ViewModel("rpg_factions")]
public sealed class FactionsView : PanelListView
{
    public override string Hint => "@rpg.factions.hint";

    protected override int Signature(World world, Entity subject)
    {
        int hash = 17;
        var records = world.Records();
        if (records.TypeNameOf(typeof(FactionRanksRecord)) == null) return hash;
        foreach (var id in records.Ids("faction_ranks"))
        {
            if (!records.TryGet(id, out FactionRanksRecord ladder)) continue;
            var faction = ladder.Faction.Id;
            hash = Fold(hash, FactionRanks.RankOf(world, subject, faction));
            hash = Fold(hash, (int)Factions.StandingWith(world, faction));
            hash = Fold(hash, (int)Crime.BountyWith(world, faction));
            hash = Fold(hash, FactionRanks.CanRise(world, subject, faction, out _) ? 1 : 2);
        }
        return hash;
    }

    protected override void Build(World world, Entity subject, Panel panel) => FactionRanks.Panel(world, subject, panel);

    protected override bool Activate(World world, Entity subject, ListRow row)
    {
        if (FactionRanks.Rise(world, subject, row.Id, out string why)) return true;
        Message = why;
        return true;
    }
}
