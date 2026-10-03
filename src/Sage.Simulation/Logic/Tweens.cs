#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

namespace Sage.Simulation;

// Tweens (issue #90, phase 4b): move, turn or scale an entity's transform to somewhere else over some
// seconds, along an easing curve, from a wire. A lift that eases to the next floor, a crane arm that
// swings, a sign that grows as you approach — the things HL1 needed a func_train or a func_rotating for.
//
//   component  "sage:tween": { "channel": "Position", "target": [0, 3, 0], "relative": true,
//                              "duration": 2, "ease": "QuadInOut" }
//   part       "tween": the same fields
//
//   input    TweenTo [channel] [x y z] [seconds] [ease]
//   input    TweenPlay [once|loop|pingpong]   play the part's `sequence` from its first step (#281)
//   input    TweenStop            stop where it is (no OnTweenDone)
//   output   OnTweenDone          it arrived; the activator is whoever sent the TweenTo
//   output   OnTweenLoop          a play ended and another begins (`loop`); hands on how many have ended
//   output   OnTweenStep          a step of a sequence arrived; hands on its number, from 1
//
// **TweenTo's parameter** is words and numbers separated by spaces, every one optional, in this order:
//
//   channel  `position`, `rotation` or `scale` (to the goal), or `offset` (move by it) or `turn` (turn by
//            it); left out, the tween's own `channel` and `relative`
//   x y z    the goal: metres for position and offset (the transform's local position), degrees of
//            pitch, yaw and roll for rotation and turn, factors for scale; left out, the tween's `target`
//   seconds  how long; left out, the tween's `duration`. A single number on its own is the seconds.
//   ease     an easing curve by name (`QuadInOut`, `ease_out_bounce`: Easing.TryParse); left out, `ease`
//   loop     `once`, `loop` (start again from the beginning) or `pingpong` / `yoyo` (back the way it came,
//            then forth again); left out, the tween's `loop` (#281)
//
//   TweenTo ""                           the tween's own target, duration and ease
//   TweenTo "offset 0 3 0"               three metres up from wherever it is
//   TweenTo "position 0 0 0 1.5 BackOut" back home in a second and a half, overshooting a little
//   TweenTo "turn 0 90 0 2 SineInOut"    a quarter turn to the left over two seconds
//
// No commas, because a `.map`'s wires are comma-separated. A parameter it cannot read is a warning and
// the tween does not move. A rotation takes the short way round, so a turn of 180° or more wants two.
//
// **Loops, yoyos and sequences** (issue #281). With `loop` other than Once a tween does not stop when it
// arrives: `Restart` starts the same play again from its beginning, `PingPong` plays it backwards, then
// forwards, and so on. `loops` is how many plays in all (0: for ever); each that ends with another to come
// fires OnTweenLoop, and the last OnTweenDone. What a play overshot is carried into the next, so a loop
// does not drift. A `sequence` is a list of TweenTo parameters played one after another by TweenPlay, each
// from where the last left it ("offset 0 3 0 2", "turn 0 90 0 1", …): OnTweenStep as each arrives, and
// with a `loop` the whole list again (a sequence's PingPong restarts it, as Restart does; its relative
// steps carry on from where it is, so an `offset` sequence that loops climbs). A TweenTo stops a sequence.
//
// **One at a time.** A TweenTo while one is playing starts from wherever the entity is *now*, so a lift
// sent back half-way turns round without a jump. Every tick is `Elapsed += dt` and a pure function of it
// (Easing), so a tween is deterministic and a save taken half-way (Elapsed, From and To are saved) ends
// where and when it would have (test: TweensAreDeterministicAndSurviveASave).
//
// **When it moves.** In the EntityIO phase, before the dispatch, like timers: a TweenTo of N seconds
// delivered on tick D arrives on the tick an input sent on tick D with a delay of N does, and
// OnTweenDone's undelayed wires arrive that same tick. A body the engine built as a static (a brush door) is told where it went, as movers do; a
// kinematic body follows its transform; a dynamic one is physics', and tweening it fights physics.
//
// Owned by the engine (`sage.core`), like timers and cameras.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // easing, timers and tweens (#90)
public enum TweenChannel
{
    Position,
    Rotation,
    Scale,
}

// What a tween does when it arrives (issue #281).
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // easing, timers and tweens (#90)
public enum TweenLoop
{
    Once,        // stop, and OnTweenDone
    Restart,     // play again from the beginning
    PingPong,    // play back the way it came, then forth again
}

[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // easing, timers and tweens (#90)
[Component("sage:tween")]
public struct Tween : IComponent
{
    // What a TweenTo with nothing to say does.
    [Property(Tooltip = "What a TweenTo with no channel moves: Position, Rotation or Scale")]
    public TweenChannel Channel;
    [Property(Tooltip = "Where a TweenTo with no numbers goes: metres, degrees (pitch, yaw, roll) or scale factors")]
    public Vector3 Target;
    [Property(Tooltip = "The target is from where it is (moved by, turned by, scaled by) rather than where it ends up")]
    public bool Relative;
    [Property(Min = 0, Unit = "s", Tooltip = "How long a TweenTo with no seconds takes")]
    public float Duration;
    [Property(Tooltip = "The easing curve a TweenTo with no curve uses")]
    public Ease Ease;
    [Property(Tooltip = "Play in real seconds: through a pause, a hit-stop and any world speed (WorldTime)")]
    public bool RealTime;
    [Property(Tooltip = "On arriving: stop (Once), play again (Restart) or back the way it came (PingPong)")]
    public TweenLoop Loop;
    [Property(Min = 0, Tooltip = "How many plays in all when it loops; 0 = for ever")]
    public int Loops;

    // The one playing now: saved, so a save taken half-way ends where it would have.
    public bool Playing;
    public TweenLoop PlayingLoop;
    public int Played;                      // plays ended since the TweenTo or TweenPlay
    public bool Sequencing;                 // playing the `sequence` (TweenPlay)
    public int Step;                        // which of its steps
    public TweenChannel PlayingChannel;
    public float Elapsed;
    public float PlayingDuration;
    public Ease PlayingEase;
    public Vector3 From;                    // position or scale
    public Vector3 To;
    public Quaternion FromRotation;
    public Quaternion ToRotation;

    // Who sent the TweenTo: handed to OnTweenDone. A handle, meaningless in another session.
    [Transient] public Entity Activator;

    // How far along it is, 0..1 before the curve.
    public readonly float Progress => !Playing ? 1f : PlayingDuration > 0f ? Math.Clamp(Elapsed / PlayingDuration, 0f, 1f) : 1f;
}

// "tween": { "channel": "Position", "target": [0, 3, 0], "relative": true, "duration": 2, "ease": "QuadInOut" }
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // easing, timers and tweens (#90)
[PrefabPart("tween", Plugin = RegistrationOwners.Core)]
public sealed class TweenPart : IPrefabPart
{
    [Property(Tooltip = "What a TweenTo with no channel moves: Position, Rotation or Scale")]
    public TweenChannel Channel;
    [Property(Tooltip = "Where a TweenTo with no numbers goes: metres, degrees (pitch, yaw, roll) or scale factors")]
    public Vector3 Target;
    [Property(Tooltip = "The target is from where it is (moved by, turned by, scaled by) rather than where it ends up")]
    public bool Relative;
    [Property(Min = 0, Unit = "s", Tooltip = "How long a TweenTo with no seconds takes")]
    public float Duration = 1f;
    [Property(Tooltip = "The easing curve a TweenTo with no curve uses")]
    public Ease Ease = Ease.SmoothStep;
    [Property(Tooltip = "Play in real seconds: through a pause, a hit-stop and any world speed (WorldTime)")]
    public bool RealTime;
    [Property(Tooltip = "On arriving: stop (Once), play again (Restart) or back the way it came (PingPong)")]
    public TweenLoop Loop;
    [Property(Min = 0, Tooltip = "How many plays in all when it loops; 0 = for ever")]
    public int Loops;
    [Property(Tooltip = "Steps TweenPlay plays one after another, each a TweenTo parameter: \"offset 0 3 0 2\", \"turn 0 90 0 1 SineInOut\"")]
    public List<string> Sequence = new();

    public void Apply(in PrefabPartContext ctx)
    {
        var tween = new Tween
        {
            Channel = Channel,
            Target = Target,
            Relative = Relative,
            Duration = Timers.Seconds(Duration, 1f, "duration", ctx),
            Ease = Ease,
            RealTime = RealTime,
            Loop = Loop,
            Loops = Loops >= 0 ? Loops : 0,
        };
        if (Loops < 0) ctx.Warn($"loops {Loops} is not a count; 0 (for ever) is used");
        if (Sequence.Count > 0)
        {
            var steps = new string[Sequence.Count];
            for (int i = 0; i < steps.Length; i++)
            {
                steps[i] = Sequence[i] ?? "";
                if (!Tweens.TryRead(steps[i], tween, out _, out _, out _, out _, out _))
                    ctx.Warn($"sequence[{i}] \"{steps[i]}\" is not a TweenTo parameter "
                             + "(\"[position|rotation|scale|offset|turn] [x y z] [seconds] [ease]\"); the step is skipped");
            }
            ctx.World.Add(ctx.Entity, new TweenSequence { Steps = steps });
        }
        ctx.World.Add(ctx.Entity, tween);
    }
}

// A tween's `sequence` (issue #281): content its part puts there, and back on a load's respawn; never saved.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // easing, timers and tweens (#90)
[Transient]
[Component("sage:tween_sequence")]
public struct TweenSequence : IComponent
{
    public string[]? Steps;
}

[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // easing, timers and tweens (#90)
public static class Tweens
{
    public const string ToInput = "TweenTo";
    public const string StopInput = "TweenStop";
    public const string PlayInput = "TweenPlay";
    public const string OnTweenDone = "OnTweenDone";
    public const string OnTweenLoop = "OnTweenLoop";
    public const string OnTweenStep = "OnTweenStep";

    internal static void Register(Engine engine)
    {
        engine.Inputs.Register(ToInput, static (World world, in IOContext io) =>
        {
            if (!world.Has<Tween>(io.Self))
            {
                Log.Warn(LogCat.Events, $"I/O: {ToInput} at {World.Describe(io.Self)}, which has no tween (sage:tween)");
                return;
            }
            ref var tween = ref world.Get<Tween>(io.Self);
            if (!TryRead(io.Parameter, tween, out var channel, out var goal, out bool relative, out float seconds, out var ease, out var loop))
            {
                Log.Warn(LogCat.Events, $"I/O: {ToInput}({io.Parameter}) at {World.Describe(io.Self)}: expected "
                                      + "\"[position|rotation|scale|offset|turn] [x y z] [seconds] [ease]\"; it stays where it is");
                return;
            }
            tween.Activator = io.Activator;
            Begin(ref tween, world.Get<Transform>(io.Self), channel, goal, relative, seconds, ease);
            tween.PlayingLoop = loop;
        });
        engine.Inputs.Register(PlayInput, static (World world, in IOContext io) =>
        {
            if (!world.Has<Tween>(io.Self) || !world.TryGet<TweenSequence>(io.Self, out var sequence) || sequence.Steps is not { Length: > 0 })
            {
                Log.Warn(LogCat.Events, $"I/O: {PlayInput} at {World.Describe(io.Self)}, which has no tween with a sequence (the tween part's `sequence`)");
                return;
            }
            ref var tween = ref world.Get<Tween>(io.Self);
            var loop = tween.Loop;
            if (io.Parameter.Length > 0 && !TryLoop(io.Parameter.AsSpan().Trim(), ref loop))
                Log.Warn(LogCat.Events, $"I/O: {PlayInput}({io.Parameter}) at {World.Describe(io.Self)}: the parameter is once, loop or pingpong; "
                                      + $"its own ({tween.Loop}) is used");
            tween.Activator = io.Activator;
            tween.Played = 0;
            tween.PlayingLoop = loop;
            tween.Sequencing = true;
            if (!BeginStep(ref tween, world.Get<Transform>(io.Self), sequence.Steps, 0)) tween.Sequencing = false;
        });
        engine.Inputs.Register(StopInput, static (World world, in IOContext io) =>
        {
            if (!world.Has<Tween>(io.Self)) return;
            ref var tween = ref world.Get<Tween>(io.Self);
            tween.Playing = false;
            tween.Sequencing = false;
        });
        engine.Outputs.Declare(OnTweenDone, "This tween arrived where a TweenTo sent it, or ended its loops or its sequence (sage:tween).");
        engine.Outputs.Declare(OnTweenLoop, "This tween ended a play and begins another (sage:tween, loop); hands on how many have ended.");
        engine.Outputs.Declare(OnTweenStep, "A step of this tween's sequence arrived (sage:tween, TweenPlay); hands on its number, from 1.");
    }

    // Starts step `index` of a sequence from where the entity is now; false when no step from there on
    // can be read (each was warned about when the part was applied), and the sequence ends.
    internal static bool BeginStep(ref Tween tween, in Transform transform, string[] steps, int index)
    {
        for (; index < steps.Length; index++)
        {
            if (!TryRead(steps[index], tween, out var channel, out var goal, out bool relative, out float seconds, out var ease)) continue;
            int played = tween.Played;
            var loop = tween.PlayingLoop;
            Begin(ref tween, transform, channel, goal, relative, seconds, ease);
            tween.Played = played;
            tween.PlayingLoop = loop;
            tween.Sequencing = true;
            tween.Step = index;
            return true;
        }
        return false;
    }

    // Starts a tween from where `transform` is now. For C#; a wire's TweenTo ends up here too.
    public static void Begin(ref Tween tween, in Transform transform, TweenChannel channel, Vector3 goal, bool relative,
                             float seconds, Ease ease)
    {
        tween.PlayingChannel = channel;
        tween.PlayingDuration = seconds >= 0f && float.IsFinite(seconds) ? seconds : 0f;
        tween.PlayingEase = ease;
        tween.Elapsed = 0f;
        tween.Playing = true;
        tween.PlayingLoop = tween.Loop;
        tween.Played = 0;
        tween.Sequencing = false;
        switch (channel)
        {
            case TweenChannel.Position:
                tween.From = transform.LocalPosition;
                tween.To = relative ? tween.From + goal : goal;
                break;
            case TweenChannel.Rotation:
                tween.FromRotation = transform.LocalRotation;
                var turn = Quaternion.CreateFromYawPitchRoll(goal.Y * DegToRad, goal.X * DegToRad, goal.Z * DegToRad);
                tween.ToRotation = relative ? Quaternion.Normalize(tween.FromRotation * turn) : turn;
                break;
            case TweenChannel.Scale:
                tween.From = transform.LocalScale;
                tween.To = relative ? tween.From * goal : goal;
                break;
        }
    }

    // The pose `tween` puts on `transform` at its current progress.
    public static void Apply(in Tween tween, ref Transform transform)
    {
        float t = Easing.Apply(tween.PlayingEase, tween.Progress);
        switch (tween.PlayingChannel)
        {
            case TweenChannel.Position: transform.LocalPosition = Vector3.Lerp(tween.From, tween.To, t); break;
            case TweenChannel.Rotation: transform.LocalRotation = Quaternion.Normalize(Quaternion.Slerp(tween.FromRotation, tween.ToRotation, t)); break;
            case TweenChannel.Scale: transform.LocalScale = Vector3.Lerp(tween.From, tween.To, t); break;
        }
    }

    // TweenTo's parameter (see the top of the file). Allocation-free: an input may arrive every tick.
    public static bool TryRead(ReadOnlySpan<char> parameter, in Tween tween, out TweenChannel channel, out Vector3 goal,
                               out bool relative, out float seconds, out Ease ease) =>
        TryRead(parameter, tween, out channel, out goal, out relative, out seconds, out ease, out _);

    // The same, with the loop word (`once`, `loop`, `pingpong`; issue #281): left out, the tween's `loop`.
    public static bool TryRead(ReadOnlySpan<char> parameter, in Tween tween, out TweenChannel channel, out Vector3 goal,
                               out bool relative, out float seconds, out Ease ease, out TweenLoop loop)
    {
        loop = tween.Loop;
        channel = tween.Channel;
        relative = tween.Relative;
        goal = tween.Target;
        seconds = tween.Duration;
        ease = tween.Ease;

        Span<float> numbers = stackalloc float[4];
        int count = 0;
        bool first = true;
        var rest = parameter;
        while (NextToken(ref rest, out var token))
        {
            if (first && TryChannel(token, ref channel, ref relative)) { first = false; continue; }
            first = false;
            if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) && float.IsFinite(number))
            {
                if (count == numbers.Length) return false;
                numbers[count++] = number;
            }
            else if (!TryLoop(token, ref loop) && !Easing.TryParse(token, out ease)) return false;
        }

        switch (count)
        {
            case 0: break;
            case 1: seconds = numbers[0]; break;
            case 3: goal = new Vector3(numbers[0], numbers[1], numbers[2]); break;
            case 4: goal = new Vector3(numbers[0], numbers[1], numbers[2]); seconds = numbers[3]; break;
            default: return false;
        }
        return seconds >= 0f;
    }

    private static bool TryLoop(ReadOnlySpan<char> word, ref TweenLoop loop)
    {
        if (word.Equals("once", StringComparison.OrdinalIgnoreCase)) { loop = TweenLoop.Once; return true; }
        if (word.Equals("loop", StringComparison.OrdinalIgnoreCase) || word.Equals("restart", StringComparison.OrdinalIgnoreCase))
        { loop = TweenLoop.Restart; return true; }
        if (word.Equals("pingpong", StringComparison.OrdinalIgnoreCase) || word.Equals("ping_pong", StringComparison.OrdinalIgnoreCase)
            || word.Equals("yoyo", StringComparison.OrdinalIgnoreCase)) { loop = TweenLoop.PingPong; return true; }
        return false;
    }

    // What a play that ended does next (issue #281): another (true: it carries on), or nothing more.
    internal static bool Next(ref Tween tween, ref Transform transform, Entity entity, out string? output, out int value)
    {
        output = null;
        value = 0;
        if (tween.Sequencing)
        {
            value = tween.Step + 1;
            var steps = entity.TryGetComponent<TweenSequence>(out var sequence) ? sequence.Steps : null;
            if (steps != null && BeginStep(ref tween, transform, steps, tween.Step + 1)) { output = OnTweenStep; return true; }
            // The last step: the whole list again, or the end.
            tween.Played++;
            if (steps != null && tween.PlayingLoop != TweenLoop.Once && (tween.Loops <= 0 || tween.Played < tween.Loops)
                && BeginStep(ref tween, transform, steps, 0))
            {
                output = OnTweenStep;
                return true;
            }
            tween.Sequencing = false;
            output = OnTweenStep;
            return false;
        }

        tween.Played++;
        if (tween.PlayingLoop == TweenLoop.Once || (tween.Loops > 0 && tween.Played >= tween.Loops)) return false;
        if (tween.PlayingLoop == TweenLoop.PingPong)
        {
            (tween.From, tween.To) = (tween.To, tween.From);
            (tween.FromRotation, tween.ToRotation) = (tween.ToRotation, tween.FromRotation);
        }
        output = OnTweenLoop;
        value = tween.Played;
        return true;
    }

    private static bool TryChannel(ReadOnlySpan<char> word, ref TweenChannel channel, ref bool relative)
    {
        if (word.Equals("position", StringComparison.OrdinalIgnoreCase)) { channel = TweenChannel.Position; relative = false; return true; }
        if (word.Equals("rotation", StringComparison.OrdinalIgnoreCase)) { channel = TweenChannel.Rotation; relative = false; return true; }
        if (word.Equals("scale", StringComparison.OrdinalIgnoreCase)) { channel = TweenChannel.Scale; relative = false; return true; }
        if (word.Equals("offset", StringComparison.OrdinalIgnoreCase)) { channel = TweenChannel.Position; relative = true; return true; }
        if (word.Equals("turn", StringComparison.OrdinalIgnoreCase)) { channel = TweenChannel.Rotation; relative = true; return true; }
        return false;
    }

    private static bool NextToken(ref ReadOnlySpan<char> rest, out ReadOnlySpan<char> token)
    {
        rest = rest.TrimStart();
        if (rest.IsEmpty) { token = default; return false; }
        int end = 0;
        while (end < rest.Length && !char.IsWhiteSpace(rest[end])) end++;
        token = rest[..end];
        rest = rest[end..];
        return true;
    }

    private const float DegToRad = MathF.PI / 180f;
}

// EntityIO phase, before the dispatch: every playing tween one tick further along; OnTweenDone when it
// arrives. Allocation-free. In every pass, for the `realTime` tweens (issue #283; see LogicTimerSystem).
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // easing, timers and tweens (#90)
[System(Id, Phase.EntityIO, Before = new[] { "?sage.io.dispatch" }, Condition = RunCondition.Always)]
internal sealed class TweenSystem : ISystem
{
    public const string Id = "sage.logic.tweens";

    private readonly World _world;
    private readonly Query<Tween, Transform> _tweens;
    private IPhysicsWorld? _space;

    public TweenSystem(World world)
    {
        _world = world;
        _tweens = world.Query<Tween, Transform>();
    }

    public void Run(in SystemContext ctx)
    {
        // Physics is a plugin, installed after the engine's systems are: looked up until found.
        if (_space == null) _world.Resources.TryGet(out _space);
        float stepDt = ctx.Tick.Dt;
        float realDt = WorldTime.Of(_world).RealDt;
        if (stepDt <= 0f && realDt <= 0f) return;
        foreach (var (tweens, transforms, entities) in _tweens.Chunks)
        {
            var tw = tweens.Span;
            var tr = transforms.Span;
            for (int i = 0; i < tw.Length; i++)
            {
                ref var tween = ref tw[i];
                if (!tween.Playing) continue;
                float dt = tween.RealTime ? realDt : stepDt;
                if (dt <= 0f) continue;
                tween.Elapsed += dt;
                // Done on the tick its seconds are up, not one later for float dust (Timers.Epsilon).
                bool done = tween.Elapsed >= tween.PlayingDuration - Timers.Epsilon;
                float overshoot = done ? MathF.Max(0f, tween.Elapsed - tween.PlayingDuration) : 0f;
                if (done) tween.Elapsed = tween.PlayingDuration;
                Tweens.Apply(tween, ref tr[i]);

                var entity = entities.EntityAt(i);
                if (tween.PlayingChannel == TweenChannel.Position && _space != null
                    && entity.TryGetComponent<PhysicsBody>(out var body) && body.IsStatic)
                    _space.MoveStatic(body, tr[i].LocalPosition);

                if (!done) continue;
                // A loop or a sequence carries on (issue #281), with what this play overshot, so it does
                // not drift; a step that does carries it into the next step, which begins from here.
                bool more = Tweens.Next(ref tween, ref tr[i], entity, out var output, out int value);
                if (output != null) _world.FireOutput(entity, output, tween.Activator, value);
                if (more)
                {
                    tween.Elapsed = tween.PlayingDuration > 0f ? MathF.Min(overshoot, tween.PlayingDuration) : 0f;
                    continue;
                }
                tween.Playing = false;
                _world.FireOutput(entity, Tweens.OnTweenDone, tween.Activator);
            }
        }
    }
}
