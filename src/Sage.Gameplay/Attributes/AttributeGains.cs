#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Gameplay;

// The event-to-attribute hook (issue #377, REDESIGN §5 4f): "when somebody does X, add Y to their attribute Z",
// in data. An `attribute_gain` record listens to an event the engine already raises — a blow that landed
// (`Damaged`), a spell (`AbilityCast`), a use (`Used`), a kill (`Died`) — and adds to an attribute of whoever
// did it. That is all the base knows: what the number *means* is a kit's. The RPG kit's skills read it as use-XP
// ("hit with a sword, practise the blade") and its levelling as points toward a level; a shooter could count
// kills, a survival game hours walked. Nothing here knows the words skill or level.
//
//   { "type": "attribute_gain", "id": "blade_practice", "on": "Hit", "wielding": ["iron_sword"],
//     "attribute": "blade_xp", "amount": 1 }

// The moment a gain listens for, and whose attribute it adds to.
public enum AttributeGainEvent
{
    Hit,    // a blow it struck landed (`Damaged`, the attacker): practising a weapon
    Hurt,   // a blow landed on it (`Damaged`, the target): practising armour
    Kill,   // something it hurt died (`Died`, the killer): experience for the kill
    Cast,   // it cast an ability (`AbilityCast`, the caster): practising a school of magic
    Use,    // it used something (`Used`, the user): picking a lock, taking an item
}

[Record("attribute_gain", Plugin = "sage.gameplay.attributes")]
public sealed class AttributeGainRecord
{
    [Property(Tooltip = "The moment it listens for: Hit, Hurt, Kill, Cast or Use")]
    public AttributeGainEvent On = AttributeGainEvent.Hit;

    [Property(Tooltip = "The attribute of whoever did it that gains (a skill's use-XP, experience)")]
    public RecordRef<AttributeRecord> Attribute;

    [Property(Tooltip = "Added each time; clamped to the attribute's range")]
    public float Amount = 1f;

    [Property(Min = 0, Tooltip = "Added for each point of damage the blow did (Hit and Hurt), on top of amount")]
    public float PerDamage;

    // Filters: each that is not empty must hold. Empty means "any".
    [Property(Category = "Only when", Tooltip = "Whoever did it has one of these items equipped (a blade skill: the swords)")]
    public List<RecordRef<ItemRecord>> Wielding = new();

    [Property(Category = "Only when", Tooltip = "The blow was of one of these damage types (Hit and Hurt)")]
    public List<RecordRef<DamageTypeRecord>> DamageTypes = new();

    [Property(Category = "Only when", Tooltip = "The ability cast was one of these (Cast)")]
    public List<RecordRef<AbilityRecord>> Abilities = new();

    [Property(Category = "Only when", Tooltip = "What was taken was one of these items (Use)")]
    public List<RecordRef<ItemRecord>> Items = new();

    internal static void Check(AttributeGainRecord record, RecordCheck check)
    {
        if (record.Attribute.IsEmpty) check.Error(nameof(Attribute), "names no attribute to add to");
        if (record.DamageTypes.Count > 0 && record.On is not (AttributeGainEvent.Hit or AttributeGainEvent.Hurt))
            check.Error(nameof(DamageTypes), $"only a Hit or a Hurt has a damage type; this one is on {record.On}");
        if (record.PerDamage != 0f && record.On is not (AttributeGainEvent.Hit or AttributeGainEvent.Hurt))
            check.Error(nameof(PerDamage), $"only a Hit or a Hurt does damage; this one is on {record.On}");
        if (record.Abilities.Count > 0 && record.On != AttributeGainEvent.Cast)
            check.Error(nameof(Abilities), $"only a Cast has an ability; this one is on {record.On}");
        if (record.Items.Count > 0 && record.On != AttributeGainEvent.Use)
            check.Error(nameof(Items), $"only a Use takes an item; this one is on {record.On}");
    }
}

// An attribute went up (or down) because of an `attribute_gain`: what, on whom, by how much after the clamp, and
// which rule. What a kit reads to turn use-XP into a skill's rank (Sage.Kits.Rpg's progression).
[GameEvent]
public readonly record struct AttributeGained(Entity Entity, RecordId Attribute, float Amount, RecordId Rule);

// The gain records by the event they listen for, found again when content reloads: one list per event, so the
// system asks only the rules that can match and allocates nothing in the steady state.
internal sealed class AttributeGainRules
{
    private readonly RecordStore _records;
    private readonly List<(RecordId Id, AttributeGainRecord Record)>[] _byEvent;
    private bool _stale = true;
    private int _count;

    public AttributeGainRules(RecordStore records)
    {
        _records = records;
        _byEvent = new List<(RecordId, AttributeGainRecord)>[Enum.GetValues<AttributeGainEvent>().Length];
        for (int i = 0; i < _byEvent.Length; i++) _byEvent[i] = new();
        records.Reloaded += () => _stale = true;
    }

    // How many there are; none, and the system has nothing to listen for.
    public int Count
    {
        get { Rebuild(); return _count; }
    }

    public List<(RecordId Id, AttributeGainRecord Record)> On(AttributeGainEvent kind)
    {
        Rebuild();
        return _byEvent[(int)kind];
    }

    private void Rebuild()
    {
        if (!_stale) return;
        _stale = false;
        _count = 0;
        foreach (var list in _byEvent) list.Clear();
        foreach (var id in _records.Ids("attribute_gain"))
            if (_records.TryGet(id, out AttributeGainRecord record))
            {
                _byEvent[(int)record.On].Add((id, record));
                _count++;
            }
    }
}

// Gameplay phase, after the effect tick (so a kill this tick has its `Died`): reads the events the gains listen
// for and adds to the attributes of whoever did the thing. A blow struck later in the tick is counted on the
// next one, the cursor's doing. Writes the base value, clamped, as an instant effect does, and says so with
// `AttributeGained`.
[System("sage.attributes.gains", Phase.Gameplay, After = new[] { "sage.effects.tick" })]
internal sealed class AttributeGainSystem : ISystem
{
    private readonly AttributeGainRules _rules;
    private readonly EventReader<Damaged> _damaged;
    private readonly EventReader<Died> _died;
    private readonly EventReader<AbilityCast> _cast;
    private readonly EventReader<Used> _used;

    public AttributeGainSystem(World world, AttributeGainRules rules)
    {
        _rules = rules;
        _damaged = world.Events.Reader<Damaged>(this);
        _died = world.Events.Reader<Died>(this);
        _cast = world.Events.Reader<AbilityCast>(this);
        _used = world.Events.Reader<Used>(this);
    }

    public void Run(in SystemContext ctx)
    {
        if (!_damaged.HasPending && !_died.HasPending && !_cast.HasPending && !_used.HasPending) return;
        var world = ctx.World;
        if (_rules.Count == 0)
        {
            // Nothing listens: let the events go by.
            foreach (ref readonly var _ in _damaged.Read()) { }
            foreach (ref readonly var _ in _died.Read()) { }
            foreach (ref readonly var _ in _cast.Read()) { }
            foreach (ref readonly var _ in _used.Read()) { }
            return;
        }

        foreach (ref readonly var damaged in _damaged.Read())
        {
            if (damaged.Applied <= 0f) continue;   // blocked or resisted to nothing: no practice
            foreach (var (id, rule) in _rules.On(AttributeGainEvent.Hit))
                if (Matches(world, rule, damaged.Hit.Attacker) && DamageTypeMatches(rule, damaged.Hit.Type))
                    Gain(world, damaged.Hit.Attacker, id, rule, rule.Amount + rule.PerDamage * damaged.Applied);
            foreach (var (id, rule) in _rules.On(AttributeGainEvent.Hurt))
                if (Matches(world, rule, damaged.Hit.Target) && DamageTypeMatches(rule, damaged.Hit.Type))
                    Gain(world, damaged.Hit.Target, id, rule, rule.Amount + rule.PerDamage * damaged.Applied);
        }

        foreach (ref readonly var died in _died.Read())
            foreach (var (id, rule) in _rules.On(AttributeGainEvent.Kill))
                if (Matches(world, rule, died.Killer))
                    Gain(world, died.Killer, id, rule, rule.Amount);

        foreach (ref readonly var cast in _cast.Read())
            foreach (var (id, rule) in _rules.On(AttributeGainEvent.Cast))
                if (Matches(world, rule, cast.Caster) && Contains(rule.Abilities, cast.Ability))
                    Gain(world, cast.Caster, id, rule, rule.Amount);

        foreach (ref readonly var used in _used.Read())
            foreach (var (id, rule) in _rules.On(AttributeGainEvent.Use))
                if (Matches(world, rule, used.User) && Contains(rule.Items, used.Item))
                    Gain(world, used.User, id, rule, rule.Amount);
    }

    // Whoever did it is still there, has attributes, and holds what the rule asks.
    private static bool Matches(World world, AttributeGainRecord rule, Entity actor)
    {
        if (actor.IsNull || !world.IsAlive(actor) || !world.Has<Attributes>(actor)) return false;
        if (rule.Wielding.Count == 0) return true;
        if (!world.TryGet<Equipment>(actor, out var equipment) || equipment.Worn == null) return false;
        foreach (var worn in equipment.Worn)
            if (Contains(rule.Wielding, worn.Item)) return true;
        return false;
    }

    private static bool DamageTypeMatches(AttributeGainRecord rule, RecordId type) => Contains(rule.DamageTypes, type);

    // An empty filter is "any".
    private static bool Contains<T>(List<RecordRef<T>> list, RecordId id) where T : class
    {
        if (list.Count == 0) return true;
        foreach (var entry in list)
            if (entry.Id == id) return true;
        return false;
    }

    private static void Gain(World world, Entity entity, RecordId rule, AttributeGainRecord record, float amount)
    {
        if (amount == 0f) return;
        int index = world.Resources.Get<GameplayRegistries>().Attribute(record.Attribute);
        if (index < 0) return;
        ref var attributes = ref world.Get<Attributes>(entity);
        float before = attributes.Values.BaseOf(index);
        float after = Effects.Clamp(world.Records(), record.Attribute, before + amount);
        if (after == before) return;
        attributes.Values.SetBase(index, after);
        world.Events.Send(new AttributeGained(entity, record.Attribute, after - before, rule));
    }
}
