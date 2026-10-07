#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Gameplay;

// Crime, witnesses and bounty (issue #389, phase 4r): the world noticing what you do.
//
// - **A crime is a record**: what the engine raises it for (`kind`: Theft from an owned container, Assault on
//   somebody who was not fighting you, Murder of one, Trespass in an owned place out of hours; Other is raised
//   only by content, `commit_crime`), the bounty it adds (flat, plus `perValue` times what was stolen is
//   worth) and the standing it costs with the wronged faction.
//
//     { "type": "crime", "id": "theft", "kind": "Theft", "bounty": 10, "perValue": 1, "standing": 5 }
//
// - **Only a witness makes it count.** Somebody with a faction who can see the criminal (their AI profile's
//   sight range and cone, and a clear line), or the victim of an assault, and who sides with the wronged
//   faction (a member, an ally, or a guard of its law). Unseen, the crime is still a `CrimeCommitted` fact
//   with no witness, and costs nothing (`unwitnessed: true` makes one count anyway).
// - **A bounty is owed to a faction** (the saved `Bounties`), the player's alone, like reputation. Paid
//   (`pay_bounty`, in the currency named) or cleared (`clear_bounty`), it is gone.
// - **A guard** (`"guard": { "law": "town", "tolerates": 0, "arrests": 100, "hears": 20 }`) treats a player
//   whose bounty with its law is above `tolerates` as an enemy (Factions.Toward), so the AI it already has
//   pursues them; a guard that saw the crime, or is within `hears` of the one who reported it, is set on the
//   criminal at once. Its profile picks the response from the conditions `target_wanted` and `target_outlaw`
//   (above `arrests`): chase and confront, or fight.
// - **An owned place** (`"owned_place": { "size": [8, 4, 8], "faction": "town", "openFrom": 8, "openTo": 20 }`)
//   is a box round the entity; a player inside it while it is shut who is not its owner or a member of its
//   faction is trespassing, once a visit, when somebody sees it.

// What the engine raises a crime record for.
public enum CrimeKind
{
    Other,
    Theft,
    Assault,
    Murder,
    Trespass,
}

[Record("crime", Plugin = "sage.gameplay.factions")]
public sealed class CrimeRecord
{
    [Property(Tooltip = "What a message calls it; empty: its id")]
    public string Label = "";

    [Property(Tooltip = "What the engine raises it for: Theft (from an owned container), Assault (hurting somebody not fighting you), Murder, Trespass (an owned place while shut); Other: only content (commit_crime)")]
    public CrimeKind Kind;

    [Property(Min = 0, Tooltip = "The bounty it adds, owed to the wronged faction")]
    public float Bounty;

    [Property(Min = 0, Tooltip = "Theft: more bounty per unit of the stolen items' value")]
    public float PerValue;

    [Property(Min = 0, Tooltip = "Standing it costs with the wronged faction when it counts")]
    public float Standing;

    [Property(Tooltip = "Counts without a witness; off: only when somebody sees it")]
    public bool Unwitnessed;

    public string Describe(RecordId id) => Label.Length > 0 ? Label : id.Name;

    internal static void Check(CrimeRecord record, RecordCheck check)
    {
        if (record.Bounty < 0f) check.Error(nameof(Bounty), $"must not be below 0 (it is {record.Bounty})");
        if (record.PerValue < 0f) check.Error(nameof(PerValue), $"must not be below 0 (it is {record.PerValue})");
        if (record.Standing < 0f) check.Error(nameof(Standing), $"is a cost and must not be below 0 (it is {record.Standing})");
    }
}

// What the player owes each faction, saved with the world like the reputation it sits beside (09 §3.1).
[SavedResource("bounty", Plugin = "sage.gameplay.factions")]
public sealed class Bounties
{
    public sealed class Owed
    {
        public RecordId Faction { get; set; }
        public float Value { get; set; }
    }

    public List<Owed> Entries { get; set; } = new();

    public float Of(RecordId faction)
    {
        foreach (var entry in Entries)
            if (entry.Faction == faction) return entry.Value;
        return 0f;
    }

    public void Set(RecordId faction, float value)
    {
        foreach (var entry in Entries)
            if (entry.Faction == faction) { entry.Value = value; return; }
        Entries.Add(new Owed { Faction = faction, Value = value });
    }
}

// Somebody keeps a faction's law: they pursue whoever owes it more than `Tolerates`.
[Component("sage:guard")]
public struct Guard : IComponent
{
    [RecordRef("faction"), Property(Tooltip = "Whose bounties it enforces; empty: its own faction's")]
    public RecordId Law;
    [Property(Min = 0, Tooltip = "Bounty it lets go; it pursues anybody who owes more")]
    public float Tolerates;
    [Property(Min = 0, Tooltip = "Bounty up to which it means to arrest; above it the target is an outlaw (target_outlaw)")]
    public float Arrests;
    [Property(Min = 0, Unit = "m", Tooltip = "How far a reported crime reaches it; 0: only what it sees")]
    public float Hears;
}

// "guard": { "law": "town", "tolerates": 0, "arrests": 100, "hears": 20 }
[PrefabPart("guard", Plugin = "sage.gameplay.factions", After = new[] { "faction" })]
public sealed class GuardPart : IPrefabPart
{
    [Property(Tooltip = "Whose bounties it enforces; empty: its own faction's")]
    public RecordRef<FactionRecord> Law;
    [Property(Min = 0, Tooltip = "Bounty it lets go; it pursues anybody who owes more")]
    public float Tolerates;
    [Property(Min = 0, Tooltip = "Bounty up to which it means to arrest; above it the target is an outlaw")]
    public float Arrests;
    [Property(Min = 0, Unit = "m", Tooltip = "How far a reported crime reaches it; 0: only what it sees")]
    public float Hears;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Law.IsEmpty && !ctx.World.Has<Faction>(ctx.Entity)) ctx.Warn("has no law and no faction: it enforces nobody's");
        ctx.World.Add(ctx.Entity, new Guard
        {
            Law = Law.Id,
            Tolerates = Math.Max(Tolerates, 0f),
            Arrests = Math.Max(Arrests, 0f),
            Hears = Math.Max(Hears, 0f),
        });
    }
}

// A place somebody owns: a box round the entity that a stranger may not be in while it is shut.
[Component("sage:owned_place")]
public struct OwnedPlace : IComponent
{
    [Property(Unit = "m", Tooltip = "The box it covers, centred on the entity")]
    public Vector3 Size;
    [Property(Tooltip = "The name of the entity it belongs to")]
    public string? Owner;
    [RecordRef("faction"), Property(Tooltip = "The faction it belongs to; its members come and go freely")]
    public RecordId Faction;
    [Property(Min = 0, Max = 24, Unit = "h", Tooltip = "The hour it opens to everybody; the same as openTo: never")]
    public float OpenFrom;
    [Property(Min = 0, Max = 24, Unit = "h", Tooltip = "The hour it shuts")]
    public float OpenTo;
    [RecordRef("crime"), Property(Tooltip = "The crime being found in it is; empty: the Trespass crime")]
    public RecordId Crime;

    internal bool Reported;    // this visit's trespass has been seen (internal: never saved)
    internal float NextLook;   // sim time it next asks whether anyone sees the trespasser

    public readonly bool IsOpen(double hour)
    {
        if (OpenFrom == OpenTo) return false;
        return OpenFrom < OpenTo ? hour >= OpenFrom && hour < OpenTo : hour >= OpenFrom || hour < OpenTo;
    }
}

// "owned_place": { "size": [8, 4, 8], "faction": "town", "owner": "smith", "openFrom": 8, "openTo": 20 }
[PrefabPart("owned_place", Plugin = "sage.gameplay.factions")]
public sealed class OwnedPlacePart : IPrefabPart
{
    [Property(Unit = "m", Tooltip = "The box it covers, centred on the entity")]
    public Vector3 Size = new(4f, 3f, 4f);
    [Property(Tooltip = "The name of the entity it belongs to")]
    public string Owner = "";
    [Property(Tooltip = "The faction it belongs to")]
    public RecordRef<FactionRecord> Faction;
    [Property(Min = 0, Max = 24, Unit = "h", Tooltip = "The hour it opens to everybody; the same as openTo: never")]
    public float OpenFrom;
    [Property(Min = 0, Max = 24, Unit = "h", Tooltip = "The hour it shuts")]
    public float OpenTo;
    [Property(Tooltip = "The crime being found in it is; empty: the Trespass crime")]
    public RecordRef<CrimeRecord> Crime;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Owner.Length == 0 && Faction.IsEmpty) ctx.Warn("belongs to nobody: name an owner or a faction");
        if (Size.X <= 0f || Size.Y <= 0f || Size.Z <= 0f) ctx.Warn("has no size: nobody can be in it");
        ctx.World.Add(ctx.Entity, new OwnedPlace
        {
            Size = Size,
            Owner = Owner.Length == 0 ? null : Owner,
            Faction = Faction.Id,
            OpenFrom = Math.Clamp(OpenFrom, 0f, 24f),
            OpenTo = Math.Clamp(OpenTo, 0f, 24f),
            Crime = Crime.Id,
        });
    }
}

// A crime happened: by whom, which, against which faction, and who saw it (null: nobody, and it cost
// nothing unless the record counts it unseen). `Bounty` is what it added.
[GameEvent]
public readonly record struct CrimeCommitted(Entity Criminal, RecordId Crime, RecordId Faction, Entity Witness, float Bounty);

// What the player owes a faction changed, for a HUD to say so.
[GameEvent]
public readonly record struct BountyChanged(RecordId Faction, float Value, float Change);

public static class Crime
{
    // How far and how wide a witness with no AI profile sees.
    private const float DefaultSight = 20f;
    private const float DefaultCone = 200f;

    // The first crime record of a kind, by id: what the engine raises for a theft, a hit or a death.
    public static RecordId OfKind(World world, CrimeKind kind)
    {
        var records = world.Records();
        if (records.TypeNameOf(typeof(CrimeRecord)) == null) return default;
        RecordId best = default;
        foreach (var id in records.Ids("crime"))
            if (records.TryGet(id, out CrimeRecord record) && record.Kind == kind
                && (best.IsEmpty || string.CompareOrdinal(id.ToString(), best.ToString()) < 0))
                best = id;
        return best;
    }

    // ---- bounty -----------------------------------------------------------------------------------

    public static float BountyWith(World world, RecordId faction) =>
        faction.IsEmpty || !world.Resources.TryGet<Bounties>(out var bounties) || bounties == null ? 0f : bounties.Of(faction);

    // Sets what the player owes a faction (never below 0), and says so. Cleared, the guards who were after
    // the player stop.
    public static void SetBounty(World world, RecordId faction, float value)
    {
        if (faction.IsEmpty || !world.Resources.TryGet<Bounties>(out var bounties) || bounties == null) return;
        float before = bounties.Of(faction), after = MathF.Max(value, 0f);
        if (MathF.Abs(after - before) < 0.001f) return;
        bounties.Set(faction, after);
        world.Events.Send(new BountyChanged(faction, after, after - before));
        if (after < before) CallOff(world, faction);
    }

    public static void AddBounty(World world, RecordId faction, float amount) =>
        SetBounty(world, faction, BountyWith(world, faction) + amount);

    // Pays a faction what the payer owes it in `currency` (whole units, rounded up). False, paying nothing,
    // when there is nothing owed or the payer has too little.
    public static bool Pay(World world, Entity payer, RecordId faction, RecordId currency, out string why)
    {
        float owed = BountyWith(world, faction);
        if (owed <= 0f) { why = "you owe them nothing"; return false; }
        int price = (int)MathF.Ceiling(owed);
        if (currency.IsEmpty || world.CountOf(payer, currency) < price) { why = $"you need {price} to pay what you owe"; return false; }
        world.Take(payer, currency, price);
        SetBounty(world, faction, 0f);
        Log.Info(LogCat.Gameplay, $"{World.Describe(payer)} pays {price} {currency.Name} to clear the bounty with {faction}");
        why = "";
        return true;
    }

    // ---- who pursues whom ---------------------------------------------------------------------------

    // The faction whose law a guard keeps: its own `law`, else its faction.
    public static RecordId LawOf(World world, Entity guard) =>
        !world.TryGet<Guard>(guard, out var g) ? default : !g.Law.IsEmpty ? g.Law : Factions.FactionOf(world, guard);

    // Whether `guard` is after `other`: it is a guard, `other` is a player, and they owe its law more than it
    // tolerates. What Factions.Toward asks first, so the AI a guard already has does the pursuing.
    public static bool Pursues(World world, Entity guard, Entity other)
    {
        if (!other.Tags.Has<PlayerControlled>() || !world.TryGet<Guard>(guard, out var g)) return false;
        var law = !g.Law.IsEmpty ? g.Law : Factions.FactionOf(world, guard);
        return !law.IsEmpty && BountyWith(world, law) > g.Tolerates;
    }

    // Above what the guard would arrest for: an outlaw, to be fought.
    public static bool IsOutlaw(World world, Entity guard, Entity other)
    {
        if (!Pursues(world, guard, other)) return false;
        var g = world.Get<Guard>(guard);
        return BountyWith(world, LawOf(world, guard)) > g.Arrests;
    }

    // ---- committing one -----------------------------------------------------------------------------

    // The engine's crimes: the crime record of that kind, if the game has one.
    public static bool Commit(World world, Entity criminal, CrimeKind kind, Entity victim = default, RecordId faction = default, float value = 0f)
    {
        var crime = OfKind(world, kind);
        return !crime.IsEmpty && Commit(world, criminal, crime, victim, faction, value);
    }

    // `criminal` committed `crime` against `victim` (somebody, or a thing that is owned) and/or `faction` (the
    // wronged one; empty: the victim's, or its owner's). Witnessed — or counted unseen — it adds the bounty
    // and costs the standing, and the guards who saw it or hear of it come. True when it counted. Only a
    // player's crimes count: bounty, like reputation, is the player's.
    public static bool Commit(World world, Entity criminal, RecordId crime, Entity victim = default, RecordId faction = default, float value = 0f)
    {
        if (!world.IsAlive(criminal) || !criminal.Tags.Has<PlayerControlled>()) return false;
        if (!world.Resources.TryGet<Bounties>(out _)) return false;
        var records = world.Records();
        if (records.TypeNameOf(typeof(CrimeRecord)) == null || !records.TryGet(crime, out CrimeRecord record)) return false;
        if (!world.TryGet<Transform>(criminal, out var at)) return false;

        var wronged = !faction.IsEmpty ? faction : WrongedBy(world, victim);
        var witness = FindWitness(world, criminal, at.LocalPosition, victim, wronged, record.Kind);
        if (wronged.IsEmpty && !witness.IsNull) wronged = !LawOf(world, witness).IsEmpty ? LawOf(world, witness) : Factions.FactionOf(world, witness);

        if (wronged.IsEmpty || (witness.IsNull && !record.Unwitnessed))
        {
            Log.Info(LogCat.Gameplay, $"{World.Describe(criminal)} commits {record.Describe(crime)} unseen");
            world.Events.Send(new CrimeCommitted(criminal, crime, wronged, default, 0f));
            return false;
        }

        float bounty = record.Bounty + record.PerValue * MathF.Max(value, 0f);
        if (bounty > 0f) AddBounty(world, wronged, bounty);
        if (record.Standing > 0f) Factions.Change(world, wronged, -record.Standing);
        Log.Info(LogCat.Gameplay, $"{World.Describe(criminal)} commits {record.Describe(crime)} against {wronged}"
            + (witness.IsNull ? "" : $", seen by {World.Describe(witness)}") + $": bounty {BountyWith(world, wronged):0.#}");
        world.Say($"Crime: {record.Describe(crime)}. Bounty {BountyWith(world, wronged):0}", MessageKind.Bad, 3f);
        world.Events.Send(new CrimeCommitted(criminal, crime, wronged, witness, bounty));
        Alert(world, criminal, at.LocalPosition, witness, wronged);
        return true;
    }

    // The faction a victim's wrong is against: its faction; an owned container's; or its owner's, by name.
    private static RecordId WrongedBy(World world, Entity victim)
    {
        if (victim.IsNull || !world.IsAlive(victim)) return default;
        if (world.TryGet<ItemContainer>(victim, out var container))
        {
            if (!container.Faction.IsEmpty) return container.Faction;
            return OwnerFaction(world, container.Owner);
        }
        if (world.TryGet<OwnedPlace>(victim, out var place))
            return !place.Faction.IsEmpty ? place.Faction : OwnerFaction(world, place.Owner);
        var mine = Factions.FactionOf(world, victim);
        return mine == world.Conventions().PlayerFaction ? default : mine;
    }

    private static RecordId OwnerFaction(World world, string? owner)
    {
        if (string.IsNullOrEmpty(owner)) return default;
        var found = world.FindByName(owner);
        return found.IsNull ? default : Factions.FactionOf(world, found);
    }

    // Whether `witness` would report a wrong against `wronged`: a guard of its law, a member, or an ally.
    // With nobody wronged yet (an owner nobody can find), anybody with a faction reports to their own.
    public static bool Reports(World world, Entity witness, RecordId wronged)
    {
        var mine = Factions.FactionOf(world, witness);
        if (mine == world.Conventions().PlayerFaction && !world.Has<Guard>(witness)) return false;
        var law = LawOf(world, witness);
        if (wronged.IsEmpty) return !mine.IsEmpty || !law.IsEmpty;
        if (!law.IsEmpty && law == wronged) return true;
        return !mine.IsEmpty && Factions.Between(world.Records(), mine, wronged) == Stance.Ally;
    }

    // Whether `witness` can see `target`: within its sight range and cone (its AI profile's, else 20 m and
    // 200°), with nothing solid between them.
    public static bool CanSee(World world, Entity witness, Entity target)
    {
        if (!world.TryGet<Transform>(witness, out var from) || !world.TryGet<Transform>(target, out var to)) return false;
        float range = DefaultSight, cone = DefaultCone;
        if (ProfileOf(world, witness) is { } profile) { range = profile.SightRange; cone = profile.SightAngleDegrees; }

        Vector3 a = from.LocalPosition, b = to.LocalPosition;
        float d = SageMath.DistanceXZ(a, b);
        if (d > range) return false;
        if (d > 0.01f && !SageMath.InCone(SageMath.YawOf(from.LocalRotation), a, b, cone)) return false;
        if (!world.Resources.TryGet<IPhysicsWorld>(out var space) || space == null) return true;

        Vector3 eye = a + Vector3.UnitY * 1.4f, targetEye = b + Vector3.UnitY * 1.2f;
        Vector3 ray = targetEye - eye;
        float length = ray.Length();
        if (length < 0.001f) return true;
        var hit = space.Raycast(eye, ray / length, length, LayerMask.All.Except(space.Layers.Enemy));
        return !hit.Hit || hit.Entity == target || hit.Entity == witness;
    }

    private static AIProfileRecord? ProfileOf(World world, Entity entity)
    {
        var records = world.Records();
        RecordId id = world.TryGet<AIState>(entity, out var state) && !state.Profile.IsEmpty ? state.Profile : world.Conventions().AiProfile.Id;
        return !id.IsEmpty && records.TypeNameOf(typeof(AIProfileRecord)) != null && records.TryGet(id, out AIProfileRecord profile) ? profile : null;
    }

    // The nearest one who saw it and would say so. The victim of an assault knows without looking.
    private static Entity FindWitness(World world, Entity criminal, Vector3 at, Entity victim, RecordId wronged, CrimeKind kind)
    {
        if (kind == CrimeKind.Assault && !victim.IsNull && world.IsAlive(victim) && !IsDead(world, victim)
            && world.Has<Transform>(victim) && Reports(world, victim, wronged))
            return victim;

        Entity best = default;
        float nearest = float.MaxValue;
        foreach (var (transforms, _, entities) in world.Query<Transform, Faction>().Chunks)
        {
            var t = transforms.Span;
            for (int n = 0; n < t.Length; n++)
            {
                var candidate = entities.EntityAt(n);
                if (candidate == criminal || (kind == CrimeKind.Murder && candidate == victim) || candidate.Tags.Has<PlayerControlled>()) continue;
                float d = Vector3.DistanceSquared(t[n].LocalPosition, at);
                if (d >= nearest || IsDead(world, candidate) || !Reports(world, candidate, wronged)) continue;
                if (!CanSee(world, candidate, criminal)) continue;
                best = candidate;
                nearest = d;
            }
        }
        return best;
    }

    private static bool IsDead(World world, Entity entity)
    {
        var dead = world.Conventions().Dead;
        return !dead.IsEmpty && world.HasTag(entity, dead);
    }

    // The guards of the wronged faction's law who saw it, or are within `hears` of where it was reported, are
    // set on the criminal now — not at their next look round.
    private static void Alert(World world, Entity criminal, Vector3 at, Entity witness, RecordId law)
    {
        Vector3 reported = !witness.IsNull && world.TryGet<Transform>(witness, out var w) ? w.LocalPosition : at;
        float now = (float)world.SimTime;
        foreach (var (guards, states, transforms, entities) in world.Query<Guard, AIState, Transform>().Chunks)
        {
            var g = guards.Span;
            var s = states.Span;
            var t = transforms.Span;
            for (int n = 0; n < g.Length; n++)
            {
                var guard = entities.EntityAt(n);
                if (IsDead(world, guard) || LawOf(world, guard) != law || !Pursues(world, guard, criminal)) continue;
                bool told = guard == witness || (g[n].Hears > 0f && Vector3.Distance(t[n].LocalPosition, reported) <= g[n].Hears)
                         || CanSee(world, guard, criminal);
                if (!told) continue;
                s[n].Target = criminal;
                s[n].LastSeen = at;
                s[n].ForgetAt = now + MathF.Max(ProfileOf(world, guard)?.MemorySeconds ?? 6f, 6f);
                s[n].NextThink = 0f;   // look again now
            }
        }
    }

    // The guards of a faction's law who are after the player stop: the bounty that set them on was paid.
    private static void CallOff(World world, RecordId law)
    {
        foreach (var (_, states, entities) in world.Query<Guard, AIState>().Chunks)
        {
            var s = states.Span;
            for (int n = 0; n < s.Length; n++)
            {
                var guard = entities.EntityAt(n);
                if (s[n].Target.IsNull || !world.IsAlive(s[n].Target) || LawOf(world, guard) != law || Pursues(world, guard, s[n].Target)) continue;
                s[n].Target = default;
                s[n].ForgetAt = 0f;
            }
        }
    }

    // `bounty`: what the player owes; `bounty_set <faction> <value>`: a cheat.
    internal static void RegisterCommands(Engine engine)
    {
        engine.CVars.RegisterCommand("bounty", CVarFlags.None, "What you owe each faction for your crimes.", _ =>
        {
            foreach (var world in engine.Worlds)
            {
                if (!world.Resources.TryGet<Bounties>(out var bounties) || bounties == null) continue;
                int shown = 0;
                foreach (var entry in bounties.Entries)
                    if (entry.Value > 0f) { Log.Info(LogCat.Console, $"  {entry.Faction,-28} {entry.Value,8:F0}"); shown++; }
                if (shown == 0) Log.Info(LogCat.Console, "  no bounty on you");
            }
        });

        engine.CVars.RegisterCommand("bounty_set", CVarFlags.Cheat, "bounty_set <faction> <value>: set what you owe a faction.", a =>
        {
            if (a.Count < 2 || !float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value))
            {
                Log.Warn(LogCat.Console, "bounty_set <faction> <value>");
                return;
            }
            var id = engine.Records.Resolve("faction", a[0]);
            if (id.IsEmpty) return;
            foreach (var world in engine.Worlds) SetBounty(world, id, value);
            Log.Info(LogCat.Console, $"bounty with {id} now {value:F0}");
        });
    }
}

// Gameplay phase: theft (`Stolen`), assault (`Damaged`) and murder (`Died`) become crimes, and a player found
// in an owned place while it is shut is trespassing. Before the death seam's reputation change, so a murder is
// judged by what the victim thought of its killer while it lived.
[System("sage.factions.crime", Phase.Gameplay, After = new[] { "sage.effects.tick" }, Before = new[] { "sage.factions.deaths" })]
internal sealed class CrimeSystem : ISystem
{
    // A beating is one assault, not one per blow: the same victim is not assaulted again this soon.
    private const float AssaultAgain = 10f;
    private const float LookEvery = 0.5f;

    private readonly EventReader<Stolen> _stolen;
    private readonly EventReader<Damaged> _damaged;
    private readonly EventReader<Died> _died;
    private readonly Query<Transform, OwnedPlace> _places;
    private readonly Query<Transform> _players;
    private readonly Dictionary<Entity, float> _assaulted = new();

    public CrimeSystem(World world)
    {
        _stolen = world.Events.Reader<Stolen>(this);
        _damaged = world.Events.Reader<Damaged>(this);
        _died = world.Events.Reader<Died>(this);
        _places = world.Query<Transform, OwnedPlace>();
        _players = world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>());
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        float now = (float)ctx.Tick.SimTime;

        if (_stolen.HasPending)
            foreach (ref readonly var theft in _stolen.Read())
            {
                var records = world.Records();
                int value = records.TryGet(theft.Item, out ItemRecord item) ? item.Value * Math.Max(theft.Count, 1) : 0;
                Crime.Commit(world, theft.Thief, CrimeKind.Theft, theft.From, theft.Faction, value);
            }

        if (_damaged.HasPending)
            foreach (ref readonly var hit in _damaged.Read())
            {
                var victim = hit.Hit.Target;
                var attacker = hit.Hit.Attacker;
                if (hit.Applied <= 0f || !Victim(world, attacker, victim)) continue;
                if (_assaulted.TryGetValue(victim, out float until) && now < until) continue;
                if (_assaulted.Count >= 64) Forget(now);
                _assaulted[victim] = now + AssaultAgain;
                Crime.Commit(world, attacker, CrimeKind.Assault, victim);
            }

        if (_died.HasPending)
            foreach (ref readonly var died in _died.Read())
                if (Victim(world, died.Killer, died.Victim))
                    Crime.Commit(world, died.Killer, CrimeKind.Murder, died.Victim);

        Trespass(world, now);
    }

    // Drops the beatings that are over, so the table holds only the last few seconds' victims.
    private void Forget(float now)
    {
        var over = new List<Entity>();
        foreach (var (victim, until) in _assaulted)
            if (now >= until) over.Add(victim);
        foreach (var victim in over) _assaulted.Remove(victim);
    }

    // An innocent: somebody of a faction, not the player's, who was not fighting the player who hurt them.
    private static bool Victim(World world, Entity attacker, Entity victim)
    {
        if (attacker.IsNull || attacker == victim || !world.IsAlive(attacker) || !world.IsAlive(victim)) return false;
        if (!attacker.Tags.Has<PlayerControlled>() || victim.Tags.Has<PlayerControlled>()) return false;
        var faction = Factions.FactionOf(world, victim);
        if (faction.IsEmpty || faction == world.Conventions().PlayerFaction) return false;
        return Factions.Toward(world, victim, attacker) != Stance.Hostile;
    }

    private void Trespass(World world, float now)
    {
        double hour = -1.0;
        foreach (var (transforms, places, entities) in _places.Chunks)
        {
            var t = transforms.Span;
            var p = places.Span;
            for (int n = 0; n < p.Length; n++)
            {
                ref var place = ref p[n];
                Entity inside = default;
                Vector3 half = place.Size * 0.5f, centre = t[n].LocalPosition;
                foreach (var (players, playerEntities) in _players.Chunks)
                {
                    var pt = players.Span;
                    for (int i = 0; i < pt.Length && inside.IsNull; i++)
                    {
                        var d = Vector3.Abs(pt[i].LocalPosition - centre);
                        if (d.X <= half.X && d.Y <= half.Y && d.Z <= half.Z && !Belongs(world, playerEntities.EntityAt(i), in place))
                            inside = playerEntities.EntityAt(i);
                    }
                }
                if (inside.IsNull) { place.Reported = false; continue; }
                if (place.Reported || now < place.NextLook) continue;
                if (hour < 0.0) hour = WorldClock.Of(world).Hour;
                if (place.IsOpen(hour)) continue;

                place.NextLook = now + LookEvery;
                var crime = !place.Crime.IsEmpty ? place.Crime : Crime.OfKind(world, CrimeKind.Trespass);
                var placeEntity = entities.EntityAt(n);
                if (!crime.IsEmpty && Crime.Commit(world, inside, crime, placeEntity))
                    world.Get<OwnedPlace>(placeEntity).Reported = true;
            }
        }
    }

    private static bool Belongs(World world, Entity who, in OwnedPlace place)
    {
        if (!string.IsNullOrEmpty(place.Owner) && who.Name == place.Owner) return true;
        return !place.Faction.IsEmpty && world.TryGet<Faction>(who, out var faction) && faction.Id == place.Faction;
    }
}

// Their fields are set from content by the vocabulary reader, never by this assembly's code.
#pragma warning disable CS0649

// `{ "bounty": "town" }`: the player owes the faction anything; `"atLeast": 50`: at least that; `"below": 1`: less
// than that (owing nothing, with 1), and then no bounty at all holds.
[Condition("bounty", Plugin = "sage.gameplay.factions")]
internal sealed class BountyCondition : ICondition
{
    [EntryValue, Property(Tooltip = "The faction owed")]
    public RecordRef<FactionRecord> Faction;
    [Property(Min = 0, Tooltip = "Holds when the bounty is at least this (and above 0, unless below is given)")]
    public float AtLeast;
    [Property(Min = 0, Tooltip = "Holds only when the bounty is below this; 0: no upper bound")]
    public float Below;

    public bool Test(in ConditionContext c, out string why)
    {
        why = "you owe them nothing";
        if (Faction.IsEmpty) return true;
        float owed = Crime.BountyWith(c.World, Faction.Id);
        if (Below > 0f)
        {
            if (owed >= Below) { why = "you owe them too much"; return false; }
            return owed >= AtLeast;
        }
        return owed > 0f && owed >= AtLeast;
    }
}

// `{ "commit_crime": "pickpocket" }`: the subject commits a crime against the other (a pickpocket's mark),
// witnessed as any other.
[Action("commit_crime", Plugin = "sage.gameplay.factions")]
internal sealed class CommitCrimeAction : IAction
{
    [EntryValue, Property(Tooltip = "The crime")]
    public RecordRef<CrimeRecord> Crime;
    [Property(Tooltip = "The wronged faction; empty: the other's")]
    public RecordRef<FactionRecord> Faction;

    public void Run(in ActionContext c)
    {
        if (!Crime.IsEmpty) Sage.Gameplay.Crime.Commit(c.World, c.Subject, Crime.Id, c.Other, Faction.Id);
    }
}

// `{ "pay_bounty": "town", "currency": "gold" }`: the subject pays what it owes the faction, if it can.
[Action("pay_bounty", Plugin = "sage.gameplay.factions")]
internal sealed class PayBountyAction : IAction
{
    [EntryValue, Property(Tooltip = "The faction owed")]
    public RecordRef<FactionRecord> Faction;
    [Property(Tooltip = "The item that is money")]
    public RecordRef<ItemRecord> Currency;

    public void Run(in ActionContext c)
    {
        if (!Faction.IsEmpty && !Crime.Pay(c.World, c.Subject, Faction.Id, Currency.Id, out string why) && c.Subject.Tags.Has<PlayerControlled>())
            c.World.Say(why, MessageKind.Bad, 3f);
    }
}

// `{ "clear_bounty": "town" }`: the bounty is forgiven (served in jail, pardoned).
[Action("clear_bounty", Plugin = "sage.gameplay.factions")]
internal sealed class ClearBountyAction : IAction
{
    [EntryValue, Property(Tooltip = "The faction owed")]
    public RecordRef<FactionRecord> Faction;

    public void Run(in ActionContext c)
    {
        if (!Faction.IsEmpty) Crime.SetBounty(c.World, Faction.Id, 0f);
    }
}

#pragma warning restore CS0649

// What a guard's profile picks a response by: its target owes its law more than it tolerates (a schedule that
// chases and confronts), or more than it would arrest for (one that fights).
[AICondition("target_wanted", Plugin = "sage.gameplay.factions")]
internal sealed class TargetWantedCondition : IAICondition
{
    public bool Sense(in AIPerception p) =>
        !p.State.Target.IsNull && p.World.IsAlive(p.State.Target) && Crime.Pursues(p.World, p.Entity, p.State.Target);
}

[AICondition("target_outlaw", Plugin = "sage.gameplay.factions")]
internal sealed class TargetOutlawCondition : IAICondition
{
    public bool Sense(in AIPerception p) =>
        !p.State.Target.IsNull && p.World.IsAlive(p.State.Target) && Crime.IsOutlaw(p.World, p.Entity, p.State.Target);
}
