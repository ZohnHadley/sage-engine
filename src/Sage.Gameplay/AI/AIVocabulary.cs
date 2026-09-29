#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Gameplay;

// What a creature knows, and how it picks what to do about it, as open vocabularies (docs/REDESIGN.md
// §4.3 stage 1, issue #28).
//
// **Conditions.** An agent's conditions are a 64-bit mask (AIState.Conditions). The engine's own —
// `SeeEnemy`, `EnemyInMeleeRange`, … — are set by its perception and keep the bits the `AICondition`
// enum gives them; a game declares more, and each is *sensed*: asked every think, after the engine's
// perception, with what the creature already knows.
//
//   [AICondition("is_night", Plugin = "mygame")]
//   public sealed class IsNight : IAICondition
//   {
//       public bool Sense(in AIPerception p) => p.World.Resources.Get<Clock>().Hour is < 6 or >= 21;
//   }
//
// A schedule interrupts on it and a profile's rules pick a schedule by it, from JSON alone:
//   { "type": "ai_schedule", "id": "prowl", "tasks": [...], "interrupts": ["is_night"] }
//
// **Selectors.** Which schedule a creature runs is its profile's `selector` (issue #28; before, a private
// static in AIThinkSystem). `default` is that code, unchanged: reach, then magic, then chase, then idle,
// through the conventions' five schedules. `rules` reads the profile's `rules` first — the first whose
// `when` conditions all hold and none of whose `unless` do names the schedule — and falls back to
// `default`. A profile with rules and no selector uses `rules`.

// A condition an AI can know. The engine's own are set by its perception (`IsSensed` false) and never
// asked; a game's are asked every think, so keep `Sense` cheap and allocation-free.
[Vocabulary("ai_condition")]
public interface IAICondition
{
    bool Sense(in AIPerception perception);

    bool IsSensed => true;
}

public sealed class AIConditionAttribute : VocabularyEntryAttribute<IAICondition>
{
    public AIConditionAttribute(string id) : base(id) { }
}

// The engine's conditions: names for the bits its perception sets (AIThinkSystem.Perceive).
[AICondition(nameof(AICondition.SeeEnemy), Plugin = "sage.gameplay.ai")]
[AICondition(nameof(AICondition.LostEnemy), Plugin = "sage.gameplay.ai")]
[AICondition(nameof(AICondition.EnemyInMeleeRange), Plugin = "sage.gameplay.ai")]
[AICondition(nameof(AICondition.NoEnemy), Plugin = "sage.gameplay.ai")]
[AICondition(nameof(AICondition.TaskFailed), Plugin = "sage.gameplay.ai")]
[AICondition(nameof(AICondition.ScheduleDone), Plugin = "sage.gameplay.ai")]
[AICondition(nameof(AICondition.CanCastAtEnemy), Plugin = "sage.gameplay.ai")]
[AICondition(nameof(AICondition.CanMelee), Plugin = "sage.gameplay.ai")]
[AICondition(nameof(AICondition.SpellComingBack), Plugin = "sage.gameplay.ai")]
[AICondition(nameof(AICondition.Casting), Plugin = "sage.gameplay.ai")]
[AICondition(nameof(AICondition.RememberEnemy), Plugin = "sage.gameplay.ai")]
public sealed class PerceivedCondition : IAICondition
{
    public bool Sense(in AIPerception perception) => false;
    public bool IsSensed => false;
}

// What a sensed condition may look at: the creature, what it perceived this think, and its world.
public ref struct AIPerception
{
    public World World;
    public Entity Entity;
    public ref readonly Transform Transform;
    public ref readonly AIState State;
    public AIProfileRecord Profile;
    public float Time;
    // The conditions set so far this think: the engine's, and any game condition sensed before this one.
    public ulong Conditions;

    public readonly bool Has(AICondition condition) => (Conditions & (ulong)condition) != 0;
}

// Condition names to bits, for one engine's vocabulary: the engine's at their enum bits, a game's in
// the bits left, in id order. Made once per world (AIThinkSystem), when every plugin has registered.
public sealed class AIConditions
{
    public const int Max = 64;

    private readonly Dictionary<string, int> _bits = new(StringComparer.Ordinal);
    private readonly string[] _names = new string[Max];
    private readonly Sensor[] _sensed;

    internal readonly record struct Sensor(ulong Bit, IAICondition Condition);

    private AIConditions(Vocabulary<IAICondition>? vocabulary)
    {
        foreach (AICondition flag in Enum.GetValues<AICondition>())
        {
            if (flag == AICondition.None) continue;
            int bit = BitOperations.TrailingZeroCount((ulong)flag);
            _bits[Vocabulary.Normalize(flag.ToString())] = bit;
            _names[bit] = flag.ToString();
        }

        var sensed = new List<Sensor>();
        if (vocabulary != null)
        {
            int next = 0;
            foreach (var entry in vocabulary.Entries)
            {
                string key = Vocabulary.Normalize(entry.Id);
                var condition = vocabulary.Find(entry.Id)!;
                if (!_bits.TryGetValue(key, out int bit))
                {
                    while (next < Max && _names[next] != null) next++;
                    if (next == Max)
                        throw new InvalidOperationException(
                            $"More than {Max} AI conditions are registered; '{entry.Id}' ({entry.Owner}) has no bit left (AIState.Conditions is 64 bits)");
                    bit = next;
                    _bits[key] = bit;
                    _names[bit] = entry.Id;
                }
                if (condition.IsSensed) sensed.Add(new Sensor(1UL << bit, condition));
            }
        }
        _sensed = sensed.ToArray();
    }

    // The world's table, made the first time something asks.
    public static AIConditions Of(World world)
    {
        if (world.Resources.TryGet<AIConditions>(out var existing) && existing != null) return existing;
        var made = new AIConditions(world.Engine?.Vocabularies.Of<IAICondition>());
        world.Resources.Add(made);
        return made;
    }

    // The bit a condition has, or -1 when there is no such condition.
    public int BitOf(string name) => _bits.TryGetValue(Vocabulary.Normalize(name), out int bit) ? bit : -1;

    public ulong MaskOf(string name) => BitOf(name) is >= 0 and var bit ? 1UL << bit : 0;

    public ulong MaskOf(IReadOnlyList<string> names)
    {
        ulong mask = 0;
        foreach (string name in names) mask |= MaskOf(name);
        return mask;
    }

    public string? NameOf(int bit) => bit is >= 0 and < Max ? _names[bit] : null;

    public bool Has(ulong conditions, string name) => (conditions & MaskOf(name)) != 0;

    // The game's conditions, in bit order: asked every think.
    internal ReadOnlySpan<Sensor> Sensed => _sensed;
}

// ---- choosing a schedule ------------------------------------------------------------------------

// Picks the schedule a creature runs from what it knows. Named by its profile's `selector`.
[Vocabulary("ai_schedule_selector")]
public interface IAIScheduleSelector
{
    // The schedule to run now; the one it is running (`choice.Current`) to carry on, or empty for none.
    RecordId Choose(in AIScheduleChoice choice);
}

public sealed class AIScheduleSelectorAttribute : VocabularyEntryAttribute<IAIScheduleSelector>
{
    public AIScheduleSelectorAttribute(string id) : base(id) { }
}

public ref struct AIScheduleChoice
{
    public World World;
    public Entity Entity;
    public ulong Conditions;
    public RecordId Current;
    public AIProfileRecord Profile;
    public AIScheduleConventions Schedules;
    public AIConditions Names;

    public readonly bool Has(AICondition condition) => (Conditions & (ulong)condition) != 0;
    public readonly bool Has(string condition) => Names.Has(Conditions, condition);
}

// One rule of a profile's `rules`: `{ "when": ["is_night", "SeeEnemy"], "unless": ["CanMelee"], "schedule": "flee" }`.
public sealed class AIScheduleRule
{
    [VocabularyRef("ai_condition"), Property(Tooltip = "Conditions that must all hold")]
    public List<string> When = new();
    [VocabularyRef("ai_condition"), Property(Tooltip = "Conditions none of which may hold")]
    public List<string> Unless = new();
    [Property(Tooltip = "The schedule this rule runs")]
    public RecordRef<AIScheduleRecord> Schedule;

    private List<string>? _whenFor, _unlessFor;
    private ulong _when, _unless;

    public bool Matches(ulong conditions, AIConditions names)
    {
        if (!ReferenceEquals(_whenFor, When)) { _when = names.MaskOf(When); _whenFor = When; }
        if (!ReferenceEquals(_unlessFor, Unless)) { _unless = names.MaskOf(Unless); _unlessFor = Unless; }
        return (conditions & _when) == _when && (conditions & _unless) == 0;
    }
}

// The engine's choice (16 §3.4), as it was in AIThinkSystem: which situation the creature is in, and
// the conventions say which record that situation runs (issue #26).
[AIScheduleSelector("default", Plugin = "sage.gameplay.ai")]
public sealed class DefaultScheduleSelector : IAIScheduleSelector
{
    public RecordId Choose(in AIScheduleChoice choice) => Situation(in choice);

    // Reach first, then magic, then closing the distance. A creature that can swing and is close
    // enough swings — cheaper, and no mana — and one that cannot swing at all casts instead of
    // walking into reach to do nothing, which is what a caster with no `Melee` used to do.
    public static RecordId Situation(in AIScheduleChoice c)
    {
        var schedules = c.Schedules;
        return
            c.Has(AICondition.Casting) ? schedules.CastSpell :
            c.Has(AICondition.EnemyInMeleeRange) && c.Has(AICondition.CanMelee) ? schedules.MeleeAttack :
            c.Has(AICondition.CanCastAtEnemy) ? schedules.CastSpell :
            // Between casts, a creature that cannot swing holds where it is rather than charging: it
            // is already in range, and its spell is seconds away. Charging is what it did before this
            // line existed, and it walked a pure caster into melee reach to stand there empty-handed.
            c.Has(AICondition.SpellComingBack) && !c.Has(AICondition.CanMelee) ? schedules.HoldGround :
            c.Has(AICondition.SeeEnemy) || c.Has(AICondition.RememberEnemy) ? schedules.Chase :
            schedules.Idle;
    }
}

// The profile's own rules, in order, then the engine's choice.
[AIScheduleSelector("rules", Plugin = "sage.gameplay.ai")]
public sealed class RulesScheduleSelector : IAIScheduleSelector
{
    public RecordId Choose(in AIScheduleChoice choice)
    {
        var rules = choice.Profile.Rules;
        for (int i = 0; i < rules.Count; i++)
            if (rules[i].Matches(choice.Conditions, choice.Names)) return rules[i].Schedule;
        return DefaultScheduleSelector.Situation(in choice);
    }
}

public static class AIScheduleSelectors
{
    public const string Default = "default";
    public const string Rules = "rules";

    // What a profile names, or what it means by naming nothing.
    public static string NameOf(AIProfileRecord profile) =>
        profile.Selector.Length > 0 ? profile.Selector : profile.Rules.Count > 0 ? Rules : Default;

    private static readonly DefaultScheduleSelector Fallback = new();

    // The profile's selector, remembered on the profile: asked every think.
    internal static IAIScheduleSelector Of(World world, AIProfileRecord profile)
    {
        string name = NameOf(profile);
        if (profile.SelectorInstance != null && ReferenceEquals(profile.SelectorFor, name)) return profile.SelectorInstance;
        var found = world.Engine?.Vocabularies.Of<IAIScheduleSelector>().Find(name);
        if (found == null)
            Log.Once(LogCat.AI, LogLevel.Error, $"ai-selector:{name}",
                $"no AI schedule selector '{name}' is registered; creatures with that profile use '{Default}'");
        profile.SelectorInstance = found ?? (name == Rules ? new RulesScheduleSelector() : Fallback);
        profile.SelectorFor = name;
        return profile.SelectorInstance;
    }
}

// Load checks for the names AI records use (AIModule adds them): a condition or selector nobody
// registered is reported at its line, with the nearest one that is.
public static class AIChecks
{
    public static void Schedule(Vocabularies vocabularies, AIScheduleRecord schedule, RecordCheck check)
    {
        var conditions = vocabularies.Of<IAICondition>();
        for (int i = 0; i < schedule.Interrupts.Count; i++)
            if (!conditions.Contains(schedule.Interrupts[i]))
                check.Error($"Interrupts[{i}]", $"no interrupt condition '{schedule.Interrupts[i]}'" + Spelling.Suggest(schedule.Interrupts[i], conditions.Ids));
    }

    public static void Profile(Vocabularies vocabularies, AIProfileRecord profile, RecordCheck check)
    {
        var selectors = vocabularies.Of<IAIScheduleSelector>();
        if (profile.Selector.Length > 0 && !selectors.Contains(profile.Selector))
            check.Error(nameof(AIProfileRecord.Selector), selectors.Unknown(profile.Selector));

        var conditions = vocabularies.Of<IAICondition>();
        for (int r = 0; r < profile.Rules.Count; r++)
        {
            var rule = profile.Rules[r];
            foreach (var (field, names) in new[] { (nameof(AIScheduleRule.When), rule.When), (nameof(AIScheduleRule.Unless), rule.Unless) })
                for (int i = 0; i < names.Count; i++)
                    if (!conditions.Contains(names[i]))
                        check.Error($"Rules[{r}].{field}[{i}]", $"no AI condition '{names[i]}'" + Spelling.Suggest(names[i], conditions.Ids));
            if (rule.Schedule.IsEmpty) check.Error($"Rules[{r}]", "a rule needs the \"schedule\" it runs");
        }
    }
}
