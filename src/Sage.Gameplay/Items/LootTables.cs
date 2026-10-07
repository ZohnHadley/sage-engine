#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Gameplay;

// Loot tables and leveled lists (REDESIGN §5 4f, issue #379): what a creature carries or drops, as data.
//
//   { "type": "loot_table", "id": "goblin_loot", "rolls": 1, "maxRolls": 2,
//     "entries": [ { "item": "gold", "weight": 6, "min": 2, "max": 9 },
//                  { "table": "trinkets", "weight": 2 },
//                  { "weight": 4 },                                             // nothing
//                  { "item": "steel_sword", "requires": { "attribute": "level", "min": 5 } },
//                  { "item": "goblin_ear", "always": true } ] }
//
// A roll picks `rolls` (up to `maxRolls`) entries by weight from the ones whose `requires` holds, and
// gives each one `min`–`max` of its item or rolls its nested table that many times; an `always` entry
// is given on every roll of its table, whatever the weights say. An entry with neither item nor table is
// "nothing", which is how a table says "one time in three, no drop". **Conditions are the engine's own
// vocabulary** (Conditions.cs): a leveled list is entries gated on the player's level
// (`{ "attribute": "level", "min": 5 }`), a creature's own tags are `{ "has_tag": "undead", "entity": "!other" }`.
// They are asked about the player (the subject) with the entity being filled as the other.
//
// A prefab names a table with the `loot` part, rolled when it spawns or when it dies (the default) into its
// inventory, where the RPG kit's body search finds it. **The draws are the world's**: one SplitMix64
// stream per world, started from the world's name (its seed, as for `random`) and saved, so the same game
// drops the same things, and a loaded game drops what it would have (test: LootIsDeterministicFromTheWorldSeed).
//
// A table naming an item or a table that does not exist, and tables that nest each other in a circle,
// are load errors (test: ALootTableWithAMissingReferenceOrACycleIsALoadError).

[Record("loot_table", Plugin = "sage.gameplay.items")]
public sealed class LootTableRecord
{
    [Property(Min = 0, Tooltip = "How many entries one roll picks by weight")]
    public int Rolls = 1;
    [Property(Min = 0, Tooltip = "Up to this many picks, drawn between rolls and this; 0 = exactly rolls")]
    public int MaxRolls;
    [Property(Tooltip = "Asked before anything is rolled: the whole table gives nothing when it does not hold")]
    public ICondition? Requires;
    [Property(Tooltip = "What it may give: items, nested tables, or nothing, each with a weight")]
    public List<LootEntry> Entries = new();
}

public sealed class LootEntry
{
    [Property(Tooltip = "The item it gives; empty with no table = nothing")]
    public RecordRef<ItemRecord> Item;
    [Property(Tooltip = "A table it rolls instead of giving an item")]
    public RecordRef<LootTableRecord> Table;
    [Property(Min = 0, Tooltip = "How likely it is picked, against the others' weights")]
    public float Weight = 1f;
    [Property(Min = 0, Tooltip = "The fewest it gives (or times it rolls its table)")]
    public int Min = 1;
    [Property(Min = 0, Tooltip = "The most it gives; below min = exactly min")]
    public int Max;
    [Property(Tooltip = "Given on every roll of the table, not picked by weight")]
    public bool Always;
    [Property(Tooltip = "Only picked when this holds: a level, a tag, a quest stage")]
    public ICondition? Requires;
}

// When the `loot` part rolls its table.
public enum LootWhen
{
    Death,
    Spawn,
}

// A table still to roll when its holder dies (the `loot` part, "when": "death"). `Rolled` is set once it
// has been, so a body that dies twice (a resurrection, a load) drops once.
[Component("sage:loot")]
public struct Loot : IComponent
{
    [Property(Tooltip = "The loot table rolled into its inventory when it dies"), RecordRef("loot_table")]
    public RecordId Table;
    [Property(Tooltip = "It has been rolled")]
    public bool Rolled;
}

// "loot": "goblin_loot", or { "table": "goblin_loot", "when": "spawn" } — what a creature carries
// (rolled as it spawns) or drops (rolled as it dies), into its inventory. After `inventory`, so a capacity
// written there holds.
[PrefabPart("loot", Plugin = "sage.gameplay.items", After = new[] { "inventory" }, Shorthand = nameof(Table))]
public sealed class LootPart : IPrefabPart
{
    [Property(Tooltip = "The loot table it rolls")]
    public RecordRef<LootTableRecord> Table;
    [Property(Tooltip = "Rolled when it dies (a drop) or when it spawns (what it carries)")]
    public LootWhen When = LootWhen.Death;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Table.IsEmpty) { ctx.Error("needs a \"table\""); return; }
        ctx.World.AddInventory(ctx.Entity);
        if (When == LootWhen.Spawn) LootTables.Roll(ctx.World, ctx.Entity, Table);
        else ctx.World.Add(ctx.Entity, new Loot { Table = Table });
    }
}

// `{ "attribute": "level", "min": 5 }`: an attribute of the subject (or `entity`) inside [min, max] —
// a leveled list's "player level 5 or more". Something without that attribute has 0 of it.
[Condition("attribute", Plugin = "sage.gameplay.attributes")]
internal sealed class AttributeCondition : ICondition
{
    [EntryValue, Property(Tooltip = "The attribute asked about: level, health, a skill")]
    public RecordRef<AttributeRecord> Attribute;
    [Property(Tooltip = "The lowest it may be")]
    public float Min = float.NegativeInfinity;
    [Property(Tooltip = "The highest it may be")]
    public float Max = float.PositiveInfinity;
    [Property(Tooltip = "Who is asked: an entity's name, or !subject / !other; empty: the subject")]
    public string Entity = "";

    private Entity _found;

    public bool Test(in ConditionContext c, out string why)
    {
        why = "not yet";
        if (Attribute.IsEmpty) return true;
        var who = GameplayWords.Who(in c, Entity, ref _found);
        float value = 0f;
        if (c.World.Resources.TryGet<GameplayRegistries>(out var registries) && registries != null
            && registries.Attribute(Attribute) >= 0 && c.World.IsAlive(who))
            value = c.World.Attribute(who, Attribute);
        return value >= Min && value <= Max;
    }
}

public static class LootTables
{
    private const int MaxDepth = 16;   // tables are checked for cycles as they load; this is the backstop

    // Rolls a table into an entity's inventory, drawing from the world's loot stream. Conditions are asked
    // about the player (or `into`, in a world without one), with `into` as the other. Returns how many
    // items it gave.
    public static int Roll(World world, Entity into, RecordId table)
    {
        if (table.IsEmpty || !world.IsAlive(into)) return 0;
        var player = Scenes.Player(world);
        var context = new ConditionContext(world, world.IsAlive(player) ? player : into, into);
        return Roll(world, into, table, in context, LootRandom.Of(world), 0);
    }

    private static int Roll(World world, Entity into, RecordId table, in ConditionContext context, LootRandom random, int depth)
    {
        var records = world.Resources.Get<RecordStore>();
        if (!records.TryGet(table, out LootTableRecord record))
        {
            Log.Once(LogCat.Gameplay, LogLevel.Error, $"loot:{table}", $"No loot table {table}");
            return 0;
        }
        if (depth >= MaxDepth)
        {
            Log.Once(LogCat.Gameplay, LogLevel.Error, $"loot-deep:{table}", $"Loot table {table} nests deeper than {MaxDepth}; not rolled");
            return 0;
        }
        if (!Conditions.Test(record.Requires, in context, out _)) return 0;

        int given = 0;
        var entries = record.Entries;
        for (int i = 0; i < entries.Count; i++)
            if (entries[i] is { Always: true } always && Conditions.Test(always.Requires, in context, out _))
                given += Grant(world, into, always, in context, random, depth);

        int rolls = Between(random, world, record.Rolls, record.MaxRolls);
        for (int r = 0; r < rolls; r++)
        {
            // Asked again each pick: what a roll gave (or a `random`) may change what the next may be.
            float total = 0f;
            for (int i = 0; i < entries.Count; i++)
                if (Candidate(entries[i], in context)) total += entries[i].Weight;
            if (!(total > 0f)) break;

            double pick = random.Next(world) * total;
            LootEntry? chosen = null;
            for (int i = 0; i < entries.Count; i++)
            {
                if (!Candidate(entries[i], in context)) continue;
                chosen = entries[i];
                pick -= entries[i].Weight;
                if (pick < 0) break;
            }
            if (chosen != null) given += Grant(world, into, chosen, in context, random, depth);
        }
        return given;
    }

    private static bool Candidate(LootEntry? entry, in ConditionContext context) =>
        entry is { Always: false } && entry.Weight > 0f && Conditions.Test(entry.Requires, in context, out _);

    private static int Grant(World world, Entity into, LootEntry entry, in ConditionContext context, LootRandom random, int depth)
    {
        int count = Between(random, world, entry.Min, entry.Max);
        if (count <= 0) return 0;
        if (!entry.Table.IsEmpty)
        {
            int given = 0;
            for (int i = 0; i < count; i++) given += Roll(world, into, entry.Table, in context, random, depth + 1);
            return given;
        }
        if (entry.Item.IsEmpty) return 0;   // the "nothing" entry
        if (world.Give(into, entry.Item, count)) return count;
        Log.Debug(LogCat.Gameplay, $"{World.Describe(into)}: {count}x {entry.Item} from loot did not fit");
        return 0;
    }

    // min..max inclusive; a max below min is exactly min. Draws only when there is a range to draw from.
    private static int Between(LootRandom random, World world, int min, int max)
    {
        min = Math.Max(min, 0);
        if (max <= min) return min;
        return min + (int)(random.Next(world) * (max - min + 1));
    }

    // ---- content checks ---------------------------------------------------------------------------

    internal static void Check(LootTableRecord record, RecordCheck check)
    {
        if (record.MaxRolls != 0 && record.MaxRolls < record.Rolls)
            check.Error(nameof(LootTableRecord.MaxRolls), $"{record.MaxRolls} is fewer than rolls ({record.Rolls})");
        for (int i = 0; i < record.Entries.Count; i++)
        {
            var entry = record.Entries[i];
            string at = $"Entries[{i}]";
            if (entry == null) { check.Error(at, "an empty entry"); continue; }
            if (!entry.Item.IsEmpty && !entry.Table.IsEmpty)
                check.Error(at, "gives an item or rolls a table, not both");
            if (!(entry.Weight >= 0f) || !float.IsFinite(entry.Weight))
                check.Error($"{at}.Weight", $"{entry.Weight} is not a weight of 0 or more");
            if (entry.Max != 0 && entry.Max < entry.Min)
                check.Error($"{at}.Max", $"{entry.Max} is fewer than min ({entry.Min})");
            if (!entry.Table.IsEmpty && Cycle(check, entry.Table, check.Id) is { } cycle)
                check.Error($"{at}.Table", $"nested tables go round in a circle ({check.Id} -> {string.Join(" -> ", cycle)})");
        }
    }

    // The tables from `start` that lead back to `home`, or null when none do.
    private static List<RecordId>? Cycle(RecordCheck check, RecordId start, RecordId home)
    {
        var path = new List<RecordId>();
        var seen = new HashSet<RecordId>();
        return Walk(start) ? path : null;

        bool Walk(RecordId table)
        {
            path.Add(table);
            if (table == home) return true;
#pragma warning disable SAGE0131   // following a nested table's own record while content loads, as prefab children are
            if (seen.Add(table) && check.TryGet<LootTableRecord>(table, out var record))
                foreach (var entry in record.Entries)
                    if (entry != null && !entry.Table.IsEmpty && Walk(entry.Table)) return true;
#pragma warning restore SAGE0131
            path.RemoveAt(path.Count - 1);
            return false;
        }
    }

    // ---- the console --------------------------------------------------------------------------------

    internal static void RegisterCommands(Engine engine)
    {
        engine.CVars.RegisterCommand("loot", CVarFlags.Cheat, "loot <table> [times]: roll a loot table into the local player's inventory.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "loot <table> [times]"); return; }
            var table = engine.Records.Resolve("loot_table", a[0]);
            if (table.IsEmpty) return;   // Resolve has said there is no such table
            int times = a.Count > 1 && int.TryParse(a[1], out int parsed) ? Math.Max(parsed, 1) : 1;
            engine.ForEachPlayer((world, entity) =>
            {
                int given = 0;
                for (int i = 0; i < times; i++) given += Roll(world, entity, table);
                Log.Info(LogCat.Console, $"{World.Describe(entity)} rolls {table.Name}{(times > 1 ? $" x{times}" : "")}: {given} item(s)");
            });
        });
    }
}

// The world's loot draws: SplitMix64, the same on every machine, started from the world's name (its seed)
// apart from the `random` condition's stream, so a `random` asked elsewhere never changes a drop. Saved,
// so a loaded game draws what it would have.
[SavedResource("loot_random", Plugin = "sage.gameplay.items")]
internal sealed class LootRandom
{
    // 0: not drawn from yet; the first draw starts it from the world's name.
    public ulong State { get; set; }

    public static LootRandom Of(World world) => world.Resources.GetOrAdd(static () => new LootRandom());

    // 0 <= value < 1.
    public double Next(World world)
    {
        if (State == 0) State = Seed(world.Name);
        State += 0x9E3779B97F4A7C15UL;
        ulong z = State;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53));
    }

    // FNV-1a of "loot:" and the name: stable across runs and machines.
    private static ulong Seed(string name)
    {
        ulong hash = 0xCBF29CE484222325UL;
        foreach (char c in "loot:" + (name ?? ""))
        {
            hash ^= c;
            hash *= 0x100000001B3UL;
        }
        return hash == 0 ? 0x9E3779B97F4A7C15UL : hash;
    }
}

// Gameplay phase: a death rolls the victim's `loot` table into its inventory, before the rules (which may
// destroy it or leave a body) and before anything searches it.
[System("sage.items.loot", Phase.Gameplay, After = new[] { "sage.effects.tick" }, Before = new[] { "sage.effects.deaths" })]
internal sealed class LootDeathSystem : ISystem
{
    private readonly EventReader<Died> _died;

    public LootDeathSystem(World world) => _died = world.Events.Reader<Died>(this);

    public void Run(in SystemContext ctx)
    {
        if (!_died.HasPending) return;
        var world = ctx.World;
        foreach (ref readonly var died in _died.Read())
        {
            if (!world.IsAlive(died.Victim) || !world.Has<Loot>(died.Victim)) continue;
            ref var loot = ref world.Get<Loot>(died.Victim);
            if (loot.Rolled) continue;
            loot.Rolled = true;
            var table = loot.Table;   // read before rolling: giving may move the component's storage
            LootTables.Roll(world, died.Victim, table);
        }
    }
}
