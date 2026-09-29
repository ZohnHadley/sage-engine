#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Gameplay;

// Effects (docs/design/16 §3.3, TODO F18): the one way attributes and tags change. Damage, healing,
// poison, a strength buff, a cooldown and "you are stunned" are all effect records, so every system
// that cares reads the same data — and saves only have to store which effects are running (09).

public enum ModifierOp { Add, Multiply, Override }
public enum EffectDuration { Instant, Timed, Infinite }
public enum EffectStacking { Separate, Refresh, Stack }

public sealed class AttributeModifier
{
    public RecordRef<AttributeRecord> Attribute;
    public ModifierOp Op = ModifierOp.Add;
    public float Value;
}

[Record("effect", Plugin = "sage.gameplay.attributes")]
public sealed class EffectRecord
{
    public List<AttributeModifier> Modifiers = new();
    public EffectDuration Duration = EffectDuration.Instant;
    public float Time = 0f;                     // seconds, for Timed
    public float Period = 0f;                   // > 0: apply the modifiers every Period seconds
    public EffectStacking Stacking = EffectStacking.Refresh;
    public int MaxStacks = 1;
    public List<RecordRef<TagRecord>> GrantTags = new();    // held while the effect is active
    public List<RecordRef<TagRecord>> RequireTags = new();  // the target must have all of these
    public List<RecordRef<TagRecord>> BlockTags = new();    // the target must have none of these
    public List<RecordRef<CueRecord>> Cues = new();         // presentation only (16 §3.3). Not raised yet: an effect
                                                // has three moments (applied, ticked, removed) and which
                                                // of them a cue means is an open question, 11 §13.

    // What this effect costs to *build a spell out of* (16 §3.3, F21's spellmaker). Zero means it is
    // not for sale: an effect the game applies itself — a cooldown, a mana spend, a trap's poison —
    // has no price because no player composes with it. It lives on the effect rather than in the
    // spellmaker so that a mod adding an effect prices it in the same file it defines it in.
    public float Cost;

    // What it does besides changing numbers (issue #28): knock back, teleport, summon, dispel, or a
    // game's own `effect_execution`. Run whenever the effect is applied, and on each period of a
    // periodic one (EffectExecutions).
    public List<IEffectExecution> Executions = new();
}

// A running effect on an entity.
public struct ActiveEffect
{
    public RecordId Record;
    public Entity Source;
    public float Remaining;     // seconds; ignored for Infinite
    public float PeriodTimer;
    public int Stacks;
    public float Magnitude;     // scales the record's modifiers; 1 = the record as written
}

[Component("sage:active_effects")]
public struct ActiveEffects : IComponent
{
    public List<ActiveEffect> Effects;

    public static ActiveEffects Create() => new() { Effects = new List<ActiveEffect>() };
}

// Applying effects. Gameplay calls Apply; the EffectSystem does the rest.
public static class Effects
{
    // Applies an effect record to an entity. Instant effects change the base value immediately;
    // timed and infinite ones start running. Returns false when tags blocked it.
    //
    // `magnitude` scales every modifier the record carries, which is how one `damage` effect serves
    // every weapon and spell in the game (16 §3.2): the record says "health -1", the hit says how
    // much. GAS calls this a set-by-caller magnitude.
    public static bool Apply(World world, Entity target, RecordId effect, Entity source = default, float magnitude = 1f)
    {
        if (!world.IsAlive(target)) return false;
        var records = world.Records();
        if (!records.TryGet(effect, out EffectRecord record))
        {
            Log.Once(LogCat.Gameplay, LogLevel.Error, $"effect:{effect}", $"No effect record {effect}");
            return false;
        }

        var registries = world.Resources.Get<GameplayRegistries>();
        if (!TagsAllow(world, target, record, registries)) return false;

        // Effects never add components: they are applied from inside system loops (the AI's melee
        // task, for one), where a structural change throws. Entities that take part in gameplay are
        // set up once with world.AddAttributes (16 §3.3).
        if (!world.Has<Attributes>(target) || !world.Has<ActiveEffects>(target))
        {
            Log.Once(LogCat.Gameplay, LogLevel.Warn, $"effect-target:{World.Describe(target)}",
                $"{World.Describe(target)} has no attributes, so {effect} does nothing (call world.AddAttributes when it is created)");
            return false;
        }

        if (record.Duration == EffectDuration.Instant)
        {
            ApplyInstant(world, target, record, registries, 1, magnitude);
            EffectExecutions.Run(world, target, source, effect, record, 1, magnitude);
            return true;
        }

        ref var active = ref world.Get<ActiveEffects>(target);
        var list = active.Effects;

        if (record.Stacking != EffectStacking.Separate)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Record != effect) continue;
                var existing = list[i];
                existing.Remaining = record.Duration == EffectDuration.Timed ? record.Time : existing.Remaining;
                existing.Magnitude = magnitude;   // the newest application sets the strength
                if (record.Stacking == EffectStacking.Stack)
                    existing.Stacks = Math.Min(existing.Stacks + 1, Math.Max(record.MaxStacks, 1));
                list[i] = existing;
                EffectExecutions.Run(world, target, source, effect, record, existing.Stacks, magnitude);
                return true;
            }
        }

        list.Add(new ActiveEffect
        {
            Record = effect,
            Source = source,
            Remaining = record.Duration == EffectDuration.Timed ? record.Time : float.PositiveInfinity,
            PeriodTimer = 0f,
            Stacks = 1,
            Magnitude = magnitude,
        });
        EffectExecutions.Run(world, target, source, effect, record, 1, magnitude);
        return true;
    }

    // Removes every instance of an effect. Returns how many were removed.
    public static int Remove(World world, Entity target, RecordId effect)
    {
        if (!world.Has<ActiveEffects>(target)) return 0;
        ref var active = ref world.Get<ActiveEffects>(target);
        int removed = active.Effects.RemoveAll(e => e.Record == effect);
        return removed;
    }

    public static bool IsActive(World world, Entity target, RecordId effect)
    {
        if (!world.TryGet<ActiveEffects>(target, out var active) || active.Effects == null) return false;
        foreach (var running in active.Effects)
            if (running.Record == effect) return true;
        return false;
    }

    internal static bool TagsAllow(World world, Entity target, EffectRecord record, GameplayRegistries registries)
    {
        if (record.RequireTags.Count == 0 && record.BlockTags.Count == 0) return true;
        world.TryGet<GameplayTags>(target, out var tags);

        foreach (var id in record.RequireTags)
            if (!tags.Has(registries.Tag(id))) return false;
        foreach (var id in record.BlockTags)
            if (tags.Has(registries.Tag(id))) return false;
        return true;
    }

    // An instant effect changes the base value: damage and healing are permanent until something
    // else changes them.
    internal static void ApplyInstant(World world, Entity target, EffectRecord record, GameplayRegistries registries, int stacks = 1, float magnitude = 1f)
    {
        ref var attributes = ref world.Get<Attributes>(target);
        var records = world.Records();
        foreach (var modifier in record.Modifiers)
        {
            int index = registries.Attribute(modifier.Attribute);
            if (index < 0)
            {
                Log.Once(LogCat.Gameplay, LogLevel.Warn, $"attr:{modifier.Attribute}", $"Effect modifies unknown attribute {modifier.Attribute}");
                continue;
            }
            float value = attributes.Values.BaseOf(index);
            value = modifier.Op switch
            {
                ModifierOp.Add => value + modifier.Value * stacks * magnitude,
                ModifierOp.Multiply => value * MathF.Pow(modifier.Value, stacks),
                _ => modifier.Value,
            };
            attributes.Values.SetBase(index, Clamp(records, modifier.Attribute, value));
        }
    }

    internal static float Clamp(RecordStore records, RecordId attribute, float value) =>
        records.TryGet(attribute, out AttributeRecord record) ? Math.Clamp(value, record.Min, record.Max) : value;


}

// Something's health ran out (16 §3.1, issue #26): the victim is tagged dead, and `Killer` is whoever
// last hurt it this tick, or nobody (poison, a fall). **The** death seam: factions charge the killer
// for it, quests count it and the game's rules decide what it means, each by reading this — the
// effect system calls none of them, so a game without factions or quests is just a game where
// nobody reads it for that.
[GameEvent]
public readonly record struct Died(Entity Victim, Entity Killer);

// Gameplay phase: ticks the running effects, recomputes current attribute values and the tags the
// effects grant, and raises `Died` when something's health runs out (the seam combat, F20, builds on).
[System("sage.effects.tick", Phase.Gameplay)]
public sealed class EffectSystem : ISystem
{
    private readonly Query<Attributes, ActiveEffects> _affected;
    private readonly RecordStore _records;
    private readonly GameplayRegistries _registries;
    private readonly Deferred<Entity> _died = new();   // deaths are reported after the loop: the rules
                                                       // may add or destroy entities, which a query
                                                       // forbids (R14)
    private readonly EventReader<Damaged> _damage; // for "who killed me" (16 §3.2)
    private readonly List<Damaged> _hits = new();

    public EffectSystem(World world, RecordStore records)
    {
        _affected = world.Query<Attributes, ActiveEffects>();
        _records = records;
        _registries = world.Resources.Get<GameplayRegistries>();
        _damage = world.Events.Reader<Damaged>(this);
    }

    public void Run(in SystemContext ctx)
    {
        float dt = ctx.Tick.Dt;
        var world = ctx.World;
        var conventions = world.Conventions();
        int health = conventions.Health.IsEmpty ? -1 : _registries.Attribute(conventions.Health);
        int deadTag = conventions.Dead.IsEmpty ? -1 : _registries.Tag(conventions.Dead);


        // Whatever hurt anything since this system last ran, so a death can name its killer. Combat
        // runs before this system in the Gameplay phase, so a blow struck this tick is credited this
        // tick; a blow from an ability in a later phase is credited on the next one, which is the
        // cursor doing its job rather than an ordering rule in a comment.
        _hits.Clear();
        foreach (ref readonly var hit in _damage.Read()) _hits.Add(hit);
        foreach (var (attributeChunk, effectChunk, entities) in _affected.Chunks)
        {
            var a = attributeChunk.Span;
            var e = effectChunk.Span;
            for (int n = 0; n < a.Length; n++)
            {
                var entity = entities.EntityAt(n);
                Tick(world, entity, ref a[n], ref e[n], dt);
                Recompute(world, entity, ref a[n], ref e[n]);

                if (health >= 0 && a[n].Values.Has(health) && a[n].Values[health] <= 0f && !IsDead(world, entity, deadTag))
                    _died.Add(entity);
            }
        }

        foreach (var entity in _died.Drain()) Die(world, entity, deadTag);
    }

    // Durations, periodic ticks and expiry.
    private void Tick(World world, Entity entity, ref Attributes attributes, ref ActiveEffects active, float dt)
    {
        var list = active.Effects;
        if (list == null || list.Count == 0) return;

        for (int i = list.Count - 1; i >= 0; i--)
        {
            var running = list[i];
            if (!_records.TryGet(running.Record, out EffectRecord record)) { list.RemoveAt(i); continue; }

            if (record.Period > 0)
            {
                running.PeriodTimer += dt;
                while (running.PeriodTimer >= record.Period)
                {
                    running.PeriodTimer -= record.Period;
                    Effects.ApplyInstant(world, entity, record, _registries, running.Stacks, Magnitude(running));
                    EffectExecutions.Run(world, entity, running.Source, running.Record, record, running.Stacks, Magnitude(running));
                }
            }

            if (record.Duration == EffectDuration.Timed)
            {
                running.Remaining -= dt;
                if (running.Remaining <= 0f)
                {
                    list.RemoveAt(i);
                    continue;
                }
            }
            list[i] = running;
        }
    }

    // Current = base, then every active effect's modifiers, in order: adds, then multiplies, then
    // overrides. Tags granted by effects are refreshed the same way.
    private void Recompute(World world, Entity entity, ref Attributes attributes, ref ActiveEffects active)
    {
        var list = active.Effects;
        ulong granted = 0;

        for (int attribute = 0; attribute < _registries.AttributeCount; attribute++)
        {
            if (!attributes.Values.Has(attribute)) continue;
            float value = attributes.Values.BaseOf(attribute);
            if (list is { Count: > 0 })
            {
                value = Fold(list, attribute, value, ModifierOp.Add);
                value = Fold(list, attribute, value, ModifierOp.Multiply);
                value = Fold(list, attribute, value, ModifierOp.Override);
            }
            attributes.Values.SetCurrent(attribute, Effects.Clamp(_records, _registries.AttributeId(attribute), value));
        }

        if (list != null)
            foreach (var running in list)
                if (_records.TryGet(running.Record, out EffectRecord record))
                    foreach (var tag in record.GrantTags)
                    {
                        int index = _registries.Tag(tag);
                        if (index >= 0) granted |= 1UL << index;
                    }

        if (!world.Has<GameplayTags>(entity)) return;   // set up by AddAttributes; never added here
        ref var tags = ref world.Get<GameplayTags>(entity);
        tags.Bits = (tags.Bits & ~tags.Granted) | granted;   // keep tags gameplay set directly
        tags.Granted = granted;
    }

    private float Fold(List<ActiveEffect> list, int attribute, float value, ModifierOp op)
    {
        foreach (var running in list)
        {
            if (!_records.TryGet(running.Record, out EffectRecord record)) continue;
            if (record.Period > 0 || record.Duration == EffectDuration.Instant) continue;   // periodic effects change the base
            foreach (var modifier in record.Modifiers)
            {
                if (modifier.Op != op || _registries.Attribute(modifier.Attribute) != attribute) continue;
                value = op switch
                {
                    ModifierOp.Add => value + modifier.Value * running.Stacks * Magnitude(running),
                    ModifierOp.Multiply => value * MathF.Pow(modifier.Value, running.Stacks),
                    _ => modifier.Value,
                };
            }
        }
        return value;
    }

    // Effects that were running before magnitudes existed (and every save written then) have 0 here,
    // which means "as the record is written".
    private static float Magnitude(in ActiveEffect running) => running.Magnitude == 0f ? 1f : running.Magnitude;

    private static bool IsDead(World world, Entity entity, int deadTag) =>
        world.TryGet<GameplayTags>(entity, out var tags) && tags.Has(deadTag);

    // Health has run out: tag it dead once and say so (16 §3.1, issue #26). What death *means* —
    // reputation, a quest's count, ragdoll, loot, respawn — belongs to whoever reads `Died`, so this
    // runs outside the query loop and calls nobody.
    //
    // The killer comes from the Damaged events this system drained (16 §3.2), so nothing on the
    // damage path has to carry "who to blame" around; a death from drowning or poison has no killer.
    private void Die(World world, Entity entity, int deadTag)
    {
        if (!world.IsAlive(entity) || IsDead(world, entity, deadTag)) return;
        if (world.Has<GameplayTags>(entity)) world.Get<GameplayTags>(entity).Add(deadTag);
        world.Events.Send(new Died(entity, LastAttackerOf(entity)));
    }

    private Entity LastAttackerOf(Entity victim)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].Hit.Target == victim && _hits[i].Applied > 0f) return _hits[i].Hit.Attacker;
        return default;
    }
}

// The game's rules hear about a death (16 §3.1): `GameRules.OnEntityDied`, for every `Died`, in the
// tick it happened. After everything else that reads `Died` in this phase (factions, quests), so the
// rules see a world where the kill has already been counted — and may destroy or respawn the victim
// without taking it from under them.
[System("sage.effects.deaths", Phase.Gameplay, After = new[] { "sage.effects.tick" })]
public sealed class DeathRulesSystem : ISystem
{
    private readonly EventReader<Died> _died;

    public DeathRulesSystem(World world) => _died = world.Events.Reader<Died>(this);

    public void Run(in SystemContext ctx)
    {
        if (!_died.HasPending) return;
        var world = ctx.World;
        var rules = world.Resources.Get<GameRules>();
        foreach (ref readonly var died in _died.Read())
            rules.OnEntityDied(world, died.Victim, died.Killer);
    }
}
