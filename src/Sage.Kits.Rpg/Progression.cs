#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Kits.Rpg;

// Skills and levelling (issue #377, REDESIGN §5 4f), rules as data over the base's attribute_gain hook. A skill
// is two attributes: its rank (`blade`, what combat and checks read) and its use-XP (`blade_xp`, what an
// attribute_gain fills: "hit with a sword, +1"). When the use-XP reaches the skill's threshold the rank goes up
// and the use-XP starts again from what was left over. A rise counts toward a `levelling`: points in an
// attribute, and a level when they reach its threshold. A Morrowind character levels after ten rises of its
// major and minor skills; a Daggerfall one by its skills' use; an XP game by an attribute_gain on Kill straight
// into the levelling's points. Each is content:
//
//   { "type": "skill", "id": "blade", "rank": "blade", "experience": "blade_xp", "governing": "strength",
//     "rate": 1, "growth": 1, "levelling": "character", "levelPoints": 1 }
//   { "type": "levelling", "id": "character", "level": "level", "points": "level_progress", "rate": 10,
//     "attributeBonus": [ { "rises": 1, "gain": 2 }, { "rises": 5, "gain": 3 } ], "effects": ["level_up"] }
//
// What a level does to the governing attributes is Morrowind's: each attribute governing a skill that rose since
// the last level gains by how many rises it had (the attributeBonus steps). Those counts are saved
// (`rpg:progression`); the ranks, the use-XP, the level and its points are attributes and saved as attributes.

[Record("skill", Plugin = RpgKitModule.Id)]
public sealed class SkillRecord
{
    [Property(Tooltip = "The attribute that is the skill's rank: what combat and checks read, and what rises")]
    public RecordRef<AttributeRecord> Rank;

    [Property(Tooltip = "The attribute holding its use-XP, which attribute_gain records fill")]
    public RecordRef<AttributeRecord> Experience;

    [Property(Tooltip = "The attribute that governs it: a level raises it by how often this skill (and its kin) rose")]
    public RecordRef<AttributeRecord> Governing;

    [Property(Min = 0, Tooltip = "Use-XP to rise from rank 0; from rank r it takes rate × (1 + growth × r)")]
    public float Rate = 1f;

    [Property(Min = 0, Tooltip = "How much more each rank asks than the last, as a share of rate: 0 = always rate, 1 = rate × (r + 1)")]
    public float Growth = 1f;

    [Property(Category = "Levelling", Tooltip = "The levelling its rises count toward; empty: none (a miscellaneous skill)")]
    public RecordRef<LevellingRecord> Levelling;

    [Property(Category = "Levelling", Min = 0, Tooltip = "Points a rise adds to the levelling's points (a major skill 1, a minor one less)")]
    public float LevelPoints = 1f;

    // The use-XP it takes to rise from `rank`.
    public float Threshold(float rank) => Rate * (1f + Growth * MathF.Max(rank, 0f));

    internal static void Check(SkillRecord record, RecordCheck check)
    {
        if (record.Rank.IsEmpty) check.Error(nameof(Rank), "names no attribute for the skill's rank");
        if (record.Experience.IsEmpty) check.Error(nameof(Experience), "names no attribute for the skill's use-XP");
        else if (record.Experience.Id == record.Rank.Id) check.Error(nameof(Experience), "is the rank's own attribute; use-XP needs one of its own");
        if (record.Rate <= 0f) check.Error(nameof(Rate), $"must be above 0 (it is {record.Rate}): a skill that rises for nothing rises forever");
        if (record.Growth < 0f) check.Error(nameof(Growth), $"must not be below 0 (it is {record.Growth})");
    }
}

// A step of a level's attribute bonus: an attribute whose skills rose at least `Rises` times gains `Gain`.
public struct AttributeBonusStep
{
    [Property(Min = 1, Tooltip = "Rises of the skills it governs since the last level")]
    public int Rises;
    [Property(Tooltip = "What the governing attribute gains at the level")]
    public float Gain;
}

[Record("levelling", Plugin = RpgKitModule.Id)]
public sealed class LevellingRecord
{
    [Property(Tooltip = "The attribute that is the level")]
    public RecordRef<AttributeRecord> Level;

    [Property(Tooltip = "The attribute its points gather in: skill rises, or experience from an attribute_gain")]
    public RecordRef<AttributeRecord> Points;

    [Property(Min = 0, Tooltip = "Points from the first level to the second; from level l it takes rate × (1 + growth × (l − 1))")]
    public float Rate = 10f;

    [Property(Min = 0, Tooltip = "How much more each level asks than the last, as a share of rate: 0 = always rate (Morrowind's ten rises)")]
    public float Growth;

    [Property(Tooltip = "What a level gives each governing attribute by how many rises its skills had since the last: the highest step reached counts")]
    public List<AttributeBonusStep> AttributeBonus = new();

    [Property(Tooltip = "Applied at each level (health +10, a perk); their source is the one levelling")]
    public List<RecordRef<EffectRecord>> Effects = new();

    // The points it takes to rise from `level`.
    public float Threshold(float level) => Rate * (1f + Growth * MathF.Max(level - 1f, 0f));

    // The bonus for `rises` rises: the highest step reached, or nothing.
    public float BonusFor(int rises)
    {
        float gain = 0f;
        int best = 0;
        foreach (var step in AttributeBonus)
            if (rises >= step.Rises && step.Rises > best) { best = step.Rises; gain = step.Gain; }
        return gain;
    }

    internal static void Check(LevellingRecord record, RecordCheck check)
    {
        if (record.Level.IsEmpty) check.Error(nameof(Level), "names no attribute for the level");
        if (record.Points.IsEmpty) check.Error(nameof(Points), "names no attribute for the points toward a level");
        else if (record.Points.Id == record.Level.Id) check.Error(nameof(Points), "is the level's own attribute; points need one of their own");
        if (record.Rate <= 0f) check.Error(nameof(Rate), $"must be above 0 (it is {record.Rate})");
        if (record.Growth < 0f) check.Error(nameof(Growth), $"must not be below 0 (it is {record.Growth})");
        for (int i = 0; i < record.AttributeBonus.Count; i++)
            if (record.AttributeBonus[i].Rises < 1) check.Error($"{nameof(AttributeBonus)}[{i}]", "a step needs at least one rise");
    }
}

// How often the skills each attribute governs rose since the last level, per levelling: what the next level's
// attribute bonus is counted from. Saved; added the first time one of its skills rises.
[Component("rpg:progression")]
public struct Progression : IComponent
{
    [Property(Tooltip = "Rises since the last level, by levelling and governing attribute")]
    public List<GoverningRises>? Rises;
}

public struct GoverningRises
{
    public RecordId Levelling;
    public RecordId Attribute;
    public int Count;
}

// A skill's rank went up, to `Rank`.
[GameEvent]
public readonly record struct SkillRaised(Entity Entity, RecordId Skill, float Rank);

// A levelling's level went up, to `Level`.
[GameEvent]
public readonly record struct LevelledUp(Entity Entity, RecordId Levelling, float Level);

public static class Skills
{
    // An entity's rank in a skill, and its use-XP: the skill's attributes' base values (what an effect that
    // fortifies a skill does is in the attribute's current value, world.Attribute).
    public static float RankOf(World world, Entity entity, RecordId skill) =>
        world.Records().TryGet(skill, out SkillRecord record) ? Base(world, entity, record.Rank) : 0f;

    public static float ExperienceOf(World world, Entity entity, RecordId skill) =>
        world.Records().TryGet(skill, out SkillRecord record) ? Base(world, entity, record.Experience) : 0f;

    // Adds use-XP to a skill, as an attribute_gain would, and rises it as far as that goes. For a game's own
    // code and the `practise` command; content uses attribute_gain.
    public static void Practise(World world, Entity entity, RecordId skill, float amount)
    {
        if (!world.Records().TryGet(skill, out SkillRecord record) || !world.Has<Attributes>(entity)) return;
        AddBase(world, entity, record.Experience, amount);
        world.Resources.Get<ProgressionRules>().Settle(world, entity, record.Experience);
    }

    // `skills`: the local player's ranks, use-XP and levels; `practise <skill> [amount]`: use-XP, as a cheat.
    internal static void RegisterCommands(Engine engine)
    {
        engine.CVars.RegisterCommand("skills", CVarFlags.None, "The local player's skills (rank, use-XP toward the next) and levels.", _ =>
            engine.ForEachPlayer((world, entity) =>
            {
                var records = world.Records();
                foreach (var id in records.Ids("skill"))
                {
                    var skill = records.Get<SkillRecord>(id);
                    float rank = Base(world, entity, skill.Rank);
                    Log.Info(LogCat.Console, $"{id.Name,-16} {rank,5:0.##}  {Base(world, entity, skill.Experience):0.##}/{skill.Threshold(rank):0.##}");
                }
                foreach (var id in records.Ids("levelling"))
                {
                    var levelling = records.Get<LevellingRecord>(id);
                    float level = Base(world, entity, levelling.Level);
                    Log.Info(LogCat.Console, $"{id.Name,-16} level {level:0.##}  {Base(world, entity, levelling.Points):0.##}/{levelling.Threshold(level):0.##}");
                }
            }));

        engine.CVars.RegisterCommand("practise", CVarFlags.Cheat, "practise <skill> [amount]: add use-XP to a skill of the local player.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "practise <skill> [amount]"); return; }
            var skill = engine.Records.Resolve("skill", a[0]);
            if (skill.IsEmpty) return;
            float amount = a.Count > 1 && float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float n) ? n : 1f;
            engine.ForEachPlayer((world, entity) =>
            {
                Practise(world, entity, skill, amount);
                Log.Info(LogCat.Console, $"{World.Describe(entity)}: {skill.Name} {RankOf(world, entity, skill):0.##}");
            });
        });
    }

    internal static float Base(World world, Entity entity, RecordId attribute)
    {
        int index = world.Resources.Get<GameplayRegistries>().Attribute(attribute);
        return index >= 0 && world.TryGet<Attributes>(entity, out var attributes) ? attributes.Values.BaseOf(index) : 0f;
    }

    // Adds to an attribute's base, clamped to its range; what it actually added.
    internal static float AddBase(World world, Entity entity, RecordId attribute, float amount)
    {
        int index = world.Resources.Get<GameplayRegistries>().Attribute(attribute);
        if (index < 0 || !world.Has<Attributes>(entity)) return 0f;
        ref var attributes = ref world.Get<Attributes>(entity);
        float before = attributes.Values.BaseOf(index);
        var records = world.Records();
        float after = records.TryGet(attribute, out AttributeRecord a) ? Math.Clamp(before + amount, a.Min, a.Max) : before + amount;
        attributes.Values.SetBase(index, after);
        return after - before;
    }

    internal static float Max(World world, RecordId attribute) =>
        world.Records().TryGet(attribute, out AttributeRecord a) ? a.Max : float.MaxValue;
}

// The skill and levelling records by the attribute they read, found again when content reloads.
internal sealed class ProgressionRules
{
    private readonly RecordStore _records;
    private readonly Dictionary<RecordId, List<(RecordId Id, SkillRecord Record)>> _skillsByExperience = new();
    private readonly Dictionary<RecordId, List<(RecordId Id, LevellingRecord Record)>> _levellingsByPoints = new();
    private bool _stale = true;

    public ProgressionRules(RecordStore records)
    {
        _records = records;
        records.Reloaded += () => _stale = true;
    }

    public bool Any
    {
        get { Rebuild(); return _skillsByExperience.Count > 0 || _levellingsByPoints.Count > 0; }
    }

    private void Rebuild()
    {
        if (!_stale) return;
        _stale = false;
        _skillsByExperience.Clear();
        _levellingsByPoints.Clear();
        if (_records.TypeNameOf(typeof(SkillRecord)) == null) return;
        foreach (var id in _records.Ids("skill"))
            if (_records.TryGet(id, out SkillRecord skill) && !skill.Experience.IsEmpty)
            {
                if (!_skillsByExperience.TryGetValue(skill.Experience.Id, out var list)) _skillsByExperience[skill.Experience.Id] = list = new();
                list.Add((id, skill));
            }
        foreach (var id in _records.Ids("levelling"))
            if (_records.TryGet(id, out LevellingRecord levelling) && !levelling.Points.IsEmpty)
            {
                if (!_levellingsByPoints.TryGetValue(levelling.Points.Id, out var list)) _levellingsByPoints[levelling.Points.Id] = list = new();
                list.Add((id, levelling));
            }
    }

    // `attribute` of `entity` changed: rise every skill whose use-XP it is as far as it goes, and level every
    // levelling whose points it is (or whose points those rises fed).
    public void Settle(World world, Entity entity, RecordId attribute)
    {
        Rebuild();
        if (_skillsByExperience.TryGetValue(attribute, out var skills))
            foreach (var (id, skill) in skills) Rise(world, entity, id, skill);
        if (_levellingsByPoints.TryGetValue(attribute, out var levellings))
            foreach (var (id, levelling) in levellings) Level(world, entity, id, levelling);
    }

    private void Rise(World world, Entity entity, RecordId id, SkillRecord skill)
    {
        float max = Skills.Max(world, skill.Rank);
        for (int guard = 0; guard < 1000; guard++)
        {
            float rank = Skills.Base(world, entity, skill.Rank);
            if (rank >= max) return;                                   // mastered: the use-XP waits
            float threshold = skill.Threshold(rank);
            if (Skills.Base(world, entity, skill.Experience) < threshold) return;
            Skills.AddBase(world, entity, skill.Experience, -threshold);
            if (Skills.AddBase(world, entity, skill.Rank, 1f) <= 0f) return;
            world.Events.Send(new SkillRaised(entity, id, rank + 1f));

            if (skill.Levelling.IsEmpty || !_records.TryGet(skill.Levelling, out LevellingRecord levelling)) continue;
            if (!skill.Governing.IsEmpty) Count(world, entity, skill.Levelling.Id, skill.Governing.Id);
            if (skill.LevelPoints > 0f && Skills.AddBase(world, entity, levelling.Points, skill.LevelPoints) > 0f)
                Level(world, entity, skill.Levelling.Id, levelling);
        }
    }

    private static void Count(World world, Entity entity, RecordId levelling, RecordId attribute)
    {
        if (!world.Has<Progression>(entity)) world.Add(entity, new Progression { Rises = new List<GoverningRises>() });
        ref var progression = ref world.Get<Progression>(entity);
        var rises = progression.Rises ??= new List<GoverningRises>();
        for (int i = 0; i < rises.Count; i++)
            if (rises[i].Levelling == levelling && rises[i].Attribute == attribute)
            {
                rises[i] = rises[i] with { Count = rises[i].Count + 1 };
                return;
            }
        rises.Add(new GoverningRises { Levelling = levelling, Attribute = attribute, Count = 1 });
    }

    private static void Level(World world, Entity entity, RecordId id, LevellingRecord levelling)
    {
        float max = Skills.Max(world, levelling.Level);
        for (int guard = 0; guard < 1000; guard++)
        {
            float level = Skills.Base(world, entity, levelling.Level);
            if (level >= max) return;
            float threshold = levelling.Threshold(level);
            if (Skills.Base(world, entity, levelling.Points) < threshold) return;
            Skills.AddBase(world, entity, levelling.Points, -threshold);
            if (Skills.AddBase(world, entity, levelling.Level, 1f) <= 0f) return;

            // Morrowind's attribute bonus: each governing attribute by its skills' rises since the last level.
            if (world.Has<Progression>(entity))
            {
                ref var progression = ref world.Get<Progression>(entity);
                if (progression.Rises != null)
                {
                    foreach (var rises in progression.Rises)
                        if (rises.Levelling == id)
                        {
                            float gain = levelling.BonusFor(rises.Count);
                            if (gain != 0f) Skills.AddBase(world, entity, rises.Attribute, gain);
                        }
                    progression.Rises.RemoveAll(r => r.Levelling == id);
                }
            }
            foreach (var effect in levelling.Effects)
                if (!effect.IsEmpty) Sage.Gameplay.Effects.Apply(world, entity, effect.Id, entity);
            world.Events.Send(new LevelledUp(entity, id, level + 1f));
        }
    }
}

// Gameplay phase, after the base's gains: an attribute that went up because of an attribute_gain may be a
// skill's use-XP or a levelling's points; rise and level as far as it goes.
[System("rpg.progression", Phase.Gameplay, After = new[] { "sage.attributes.gains" })]
internal sealed class ProgressionSystem : ISystem
{
    private readonly ProgressionRules _rules;
    private readonly EventReader<AttributeGained> _gained;

    public ProgressionSystem(World world, ProgressionRules rules)
    {
        _rules = rules;
        _gained = world.Events.Reader<AttributeGained>(this);
    }

    public void Run(in SystemContext ctx)
    {
        if (!_gained.HasPending) return;
        var world = ctx.World;
        bool any = _rules.Any;
        foreach (ref readonly var gained in _gained.Read())
            if (any && gained.Amount > 0f && world.IsAlive(gained.Entity))
                _rules.Settle(world, gained.Entity, gained.Attribute);
    }
}
