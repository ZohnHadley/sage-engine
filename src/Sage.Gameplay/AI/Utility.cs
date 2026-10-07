#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Gameplay;

// Utility scoring (docs/design/16 §3.4 "Later", issue #387): choosing by how *much* something is worth
// rather than by the first rule that matches. One scoring, two users:
//
//   - the `utility` schedule selector, which runs the profile's best-scoring `utility` option;
//   - a behaviour tree's `utility` node, which runs its best-scoring child.
//
// A score is a base number plus what each *consideration* adds. A consideration counts only while its
// `when` conditions all hold and none of its `unless` do; it adds `add`, or — with a `measure` — `add`
// scaled by where the measure stands between `from` (nothing) and `to` (all of it):
//
//   { "schedule": "flee", "score": 0,
//     "considerations": [ { "when": ["SeeEnemy"], "measure": "health", "from": 0.5, "to": 0.1, "add": 3 } ] }
//
// Measures are an open vocabulary, as conditions are (issue #28): the engine's are `health` (a fraction of
// its maximum), `target_distance` (metres) and `noise_loudness`; a game declares its own.
//
//   [AIMeasure("ammo", Plugin = "mygame")]
//   public sealed class Ammo : IAIMeasure { public float Measure(in AIPerception p) => …; }

// A number about a creature and its situation, for a consideration to scale by. Asked when a creature
// thinks, so keep it cheap and allocation-free.
[Vocabulary("ai_measure")]
[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28, #387): may change before 1.0
public interface IAIMeasure
{
    float Measure(in AIPerception perception);
}

[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28, #387): may change before 1.0
public sealed class AIMeasureAttribute : VocabularyEntryAttribute<IAIMeasure>
{
    public AIMeasureAttribute(string id) : base(id) { }
}

// Its own health as a fraction of the attribute's maximum (1 with no health to measure).
[AIMeasure("health", Plugin = "sage.gameplay.ai")]
internal sealed class HealthMeasure : IAIMeasure
{
    public float Measure(in AIPerception p)
    {
        var world = p.World;
        var health = world.Conventions().Health.Id;
        if (health.IsEmpty || !world.Resources.TryGet<GameplayRegistries>(out var registries) || registries == null) return 1f;
        if (registries.Attribute(health) is not (>= 0 and var index)) return 1f;
        if (!world.TryGet<Attributes>(p.Entity, out var attributes) || attributes.Values == null || !attributes.Values.Has(index)) return 1f;
        float value = attributes.Values[index];
        float max = world.Engine != null && world.Engine.Records.TryGet(health, out AttributeRecord record) && record.Max > 0f && record.Max < float.MaxValue
            ? record.Max : 0f;
        return max > 0f ? Math.Clamp(value / max, 0f, 1f) : value;
    }
}

// Metres to what it is after: where the target is while it sees it, where it last saw it while it
// remembers; float.MaxValue with nothing to chase.
[AIMeasure("target_distance", Plugin = "sage.gameplay.ai")]
internal sealed class TargetDistanceMeasure : IAIMeasure
{
    public float Measure(in AIPerception p)
    {
        var state = p.State;
        if (state.Target.IsNull || !p.World.IsAlive(state.Target) || !p.World.TryGet<Transform>(state.Target, out var at)) return float.MaxValue;
        var where = p.Has(AICondition.SeeEnemy) ? at.LocalPosition : state.LastSeen;
        return SageMath.DistanceXZ(p.Transform.LocalPosition, where);
    }
}

// How loud the noise it is looking into was where it stood (issue #386); 0 with nothing heard.
[AIMeasure("noise_loudness", Plugin = "sage.gameplay.ai")]
internal sealed class NoiseLoudnessMeasure : IAIMeasure
{
    public float Measure(in AIPerception p) => p.State.HeardUntil > p.Time ? p.State.HeardLoudness : 0f;
}

// One thing a score weighs (see the top of the file).
public sealed class AIConsideration
{
    [VocabularyRef("ai_condition"), Property(Tooltip = "Conditions that must all hold for it to count")]
    public List<string> When = new();
    [VocabularyRef("ai_condition"), Property(Tooltip = "Conditions none of which may hold for it to count")]
    public List<string> Unless = new();
    [VocabularyRef("ai_measure"), Property(Tooltip = "What it scales by (an ai_measure); empty = it adds `add` whole")]
    public string Measure = "";
    [Property(Tooltip = "The measure at which it adds nothing")]
    public float From;
    [Property(Tooltip = "The measure at which it adds all of `add` (below `from` to count more as the measure falls)")]
    public float To = 1f;
    [Property(Tooltip = "What it adds to the score, at most (negative to count against)")]
    public float Add = 1f;

    private List<string>? _whenFor, _unlessFor;
    private ulong _when, _unless;
    private IAIMeasure? _measure;
    private string? _measureFor;

    internal bool Matches(ulong conditions, AIConditions names)
    {
        if (!ReferenceEquals(_whenFor, When)) { _when = names.MaskOf(When); _whenFor = When; }
        if (!ReferenceEquals(_unlessFor, Unless)) { _unless = names.MaskOf(Unless); _unlessFor = Unless; }
        return (conditions & _when) == _when && (conditions & _unless) == 0;
    }

    // What it adds for this creature now.
    internal float Value(in AIPerception perception, AIConditions names)
    {
        if (!Matches(perception.Conditions, names)) return 0f;
        if (Measure.Length == 0) return Add;
        if (!ReferenceEquals(_measureFor, Measure))
        {
            _measure = perception.World.Engine?.Vocabularies.Of<IAIMeasure>().Find(Measure);
            _measureFor = Measure;
            if (_measure == null)
                Log.Once(LogCat.AI, LogLevel.Error, $"ai-measure:{Measure}", $"no AI measure '{Measure}' is registered; a consideration that names it adds nothing");
        }
        if (_measure == null) return 0f;
        return Add * AIUtility.Curve(_measure.Measure(in perception), From, To);
    }
}

// One option of a profile's `utility`: a schedule and what it scores.
public sealed class AIUtilityOption
{
    [Property(Tooltip = "The schedule it runs when it scores best")]
    public RecordRef<AIScheduleRecord> Schedule;
    [Property(Tooltip = "Its score before its considerations")]
    public float Score;
    public List<AIConsideration> Considerations = new();
}

[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28, #387): may change before 1.0
public static class AIUtility
{
    // A base score and what its considerations add, for one creature now.
    public static float Score(in AIPerception perception, AIConditions names, float score, IReadOnlyList<AIConsideration> considerations)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(considerations);
        for (int i = 0; i < considerations.Count; i++) score += considerations[i].Value(in perception, names);
        return score;
    }

    // Where `value` stands between `from` (0) and `to` (1), clamped; a step at `from` when they are equal.
    public static float Curve(float value, float from, float to)
    {
        if (float.IsNaN(value)) return 0f;
        if (to == from) return to >= from ? (value >= from ? 1f : 0f) : 0f;
        return Math.Clamp((value - from) / (to - from), 0f, 1f);
    }
}

// The profile's `utility` options, best score first; the engine's choice when none scores above 0.
// Ties go to the schedule it is running, then to the earlier option, so a creature does not flicker
// between two options that score the same.
[AIScheduleSelector("utility", Plugin = "sage.gameplay.ai")]
internal sealed class UtilityScheduleSelector : IAIScheduleSelector
{
    public RecordId Choose(in AIScheduleChoice choice)
    {
        var options = choice.Profile.Utility;
        RecordId best = default;
        float top = 0f;
        for (int i = 0; i < options.Count; i++)
        {
            var option = options[i];
            if (option.Schedule.IsEmpty) continue;
            float score = AIUtility.Score(in choice.Perception, choice.Names, option.Score, option.Considerations);
            if (score > top || (score == top && score > 0f && option.Schedule.Id == choice.Current))
            {
                top = score;
                best = option.Schedule;
            }
        }
        return best.IsEmpty ? DefaultScheduleSelector.Situation(in choice) : best;
    }
}
