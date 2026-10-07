#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Gameplay;

// Weight with consequences (issue #384). Without this, `Inventory.Capacity` is a wall: `Give` refuses
// what would go past it, and up to it a full pack costs nothing. An `encumbrance` record turns it into
// a slope — S.T.A.L.K.E.R.'s "you can carry more, but you will walk":
//
//     { "type": "encumbrance", "id": "burden", "maxLoad": 1.2,
//       "levels": [ { "load": 0.75, "effect": "laden" }, { "load": 1.0, "effect": "overloaded" } ] }
//
// `maxLoad` moves the wall (1.2 = 120 % of the capacity), and each level is an effect held while what is
// carried weighs at least `load` × the capacity: the heaviest level reached applies its effect, and the
// lighter levels' come off, so the effects are the whole of what a level means — a speed attribute
// halved, a stamina drain, a tag that forbids running. A carrier opts in with the `inventory` part's
// `encumbrance` field (or world.AddBurden), and BurdenSystem keeps the effect in step with the pack.
[Record("encumbrance", Plugin = "sage.gameplay.items")]
public sealed class EncumbranceRecord
{
    [Property(Min = 0, Tooltip = "How far past its capacity a carrier can load itself, as a fraction of it (1 = not at all); Give refuses beyond it")]
    public float MaxLoad = 1f;
    [Property(Tooltip = "Effects held while it carries at least each level's load; the heaviest level reached applies")]
    public List<BurdenLevel> Levels = new();
}

public sealed class BurdenLevel
{
    [Property(Min = 0, Tooltip = "The load it starts at, as a fraction of the carrier's capacity (1 = full)")]
    public float Load = 1f;
    [Property(Tooltip = "Held while the carrier is at this level: a movement attribute reduced, a tag")]
    public RecordRef<EffectRecord> Effect;
}

// What a carrier's weight does to it: its encumbrance rules and the level it is at (saved, beside the
// level's effect, which is saved in ActiveEffects).
[Component("sage:burden")]
public struct Burden : IComponent
{
    [Property(Tooltip = "Its encumbrance rules")]
    public RecordRef<EncumbranceRecord> Rules;
    [Property(Min = 0, Tooltip = "The level it is at: 0 = none, n = the rules' nth level")]
    public int Level;
}

public static class Encumbrance
{
    // Gives a carrier its encumbrance rules. Structural, so call it when the entity is created.
    public static void AddBurden(this World world, Entity entity, RecordRef<EncumbranceRecord> rules)
    {
        if (world.Has<Burden>(entity)) world.Get<Burden>(entity).Rules = rules;
        else world.Add(entity, new Burden { Rules = rules });
    }

    // The most it may carry, in kg: its capacity, times its rules' maxLoad; 0 = no limit.
    public static float CarryLimitOf(this World world, Entity entity)
    {
        if (!world.TryGet<Inventory>(entity, out var inventory) || inventory.Capacity <= 0f) return 0f;
        return RulesOf(world, entity) is { } rules ? inventory.Capacity * Math.Max(rules.MaxLoad, 0f) : inventory.Capacity;
    }

    // The level its load puts it at: 0 for none, n for the rules' nth (the heaviest it reaches).
    public static int BurdenLevelOf(this World world, Entity entity)
    {
        if (RulesOf(world, entity) is not { } rules || !world.TryGet<Inventory>(entity, out var inventory) || inventory.Capacity <= 0f)
            return 0;
        float load = world.WeightOf(entity) / inventory.Capacity;
        int level = 0;
        float reached = float.NegativeInfinity;
        for (int i = 0; i < rules.Levels.Count; i++)
            if (load >= rules.Levels[i].Load && rules.Levels[i].Load >= reached) { level = i + 1; reached = rules.Levels[i].Load; }
        return level;
    }

    // Moves it to the level its pack puts it at: the old level's effect off, the new one's on.
    internal static void Update(World world, Entity entity, ref Burden burden, EncumbranceRecord? rules)
    {
        int level = rules == null ? 0 : world.BurdenLevelOf(entity);
        if (level == burden.Level) return;
        if (rules != null && burden.Level > 0 && burden.Level <= rules.Levels.Count && !rules.Levels[burden.Level - 1].Effect.IsEmpty)
            Effects.Remove(world, entity, rules.Levels[burden.Level - 1].Effect);
        burden.Level = level;
        if (rules != null && level > 0 && !rules.Levels[level - 1].Effect.IsEmpty)
            Effects.Apply(world, entity, rules.Levels[level - 1].Effect, entity);
    }

    private static EncumbranceRecord? RulesOf(World world, Entity entity) =>
        world.TryGet<Burden>(entity, out var burden) && !burden.Rules.IsEmpty
        && world.Resources.Get<RecordStore>().TryGet(burden.Rules, out EncumbranceRecord rules) ? rules : null;
}

// Gameplay phase, before effects tick: a carrier's burden level follows what it carries, whoever
// changed it — a pickup, a trade, a script, a thief — so the effect a level holds is on by the time
// this tick's attributes are recomputed.
[System("sage.items.burden", Phase.Gameplay, After = new[] { "sage.items.use" }, Before = new[] { "sage.effects.tick" })]
internal sealed class BurdenSystem : ISystem
{
    private readonly Query<Burden, Inventory> _carriers;
    private readonly RecordStore _records;

    public BurdenSystem(World world, RecordStore records)
    {
        _carriers = world.Query<Burden, Inventory>();
        _records = records;
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        foreach (var (burdens, _, entities) in _carriers.Chunks)
        {
            var b = burdens.Span;
            for (int n = 0; n < b.Length; n++)
            {
                EncumbranceRecord? rules = !b[n].Rules.IsEmpty && _records.TryGet(b[n].Rules, out EncumbranceRecord found) ? found : null;
                Encumbrance.Update(world, entities.EntityAt(n), ref b[n], rules);
            }
        }
    }
}
