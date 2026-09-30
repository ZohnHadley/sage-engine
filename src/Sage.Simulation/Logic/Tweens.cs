#nullable enable
using System;
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
//   input    TweenStop            stop where it is (no OnTweenDone)
//   output   OnTweenDone          it arrived; the activator is whoever sent the TweenTo
//
// **TweenTo's parameter** is words and numbers separated by spaces, every one optional, in this order:
//
//   channel  `position`, `rotation` or `scale` (to the goal), or `offset` (move by it) or `turn` (turn by
//            it); left out, the tween's own `channel` and `relative`
//   x y z    the goal: metres for position and offset (the transform's local position), degrees of
//            pitch, yaw and roll for rotation and turn, factors for scale; left out, the tween's `target`
//   seconds  how long; left out, the tween's `duration`. A single number on its own is the seconds.
//   ease     an easing curve by name (`QuadInOut`, `ease_out_bounce`: Easing.TryParse); left out, `ease`
//
//   TweenTo ""                           the tween's own target, duration and ease
//   TweenTo "offset 0 3 0"               three metres up from wherever it is
//   TweenTo "position 0 0 0 1.5 BackOut" back home in a second and a half, overshooting a little
//   TweenTo "turn 0 90 0 2 SineInOut"    a quarter turn to the left over two seconds
//
// No commas, because a `.map`'s wires are comma-separated. A parameter it cannot read is a warning and
// the tween does not move. A rotation takes the short way round, so a turn of 180° or more wants two.
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

    // The one playing now: saved, so a save taken half-way ends where it would have.
    public bool Playing;
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

    public void Apply(in PrefabPartContext ctx)
    {
        ctx.World.Add(ctx.Entity, new Tween
        {
            Channel = Channel,
            Target = Target,
            Relative = Relative,
            Duration = Timers.Seconds(Duration, 1f, "duration", ctx),
            Ease = Ease,
        });
    }
}

[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // easing, timers and tweens (#90)
public static class Tweens
{
    public const string ToInput = "TweenTo";
    public const string StopInput = "TweenStop";
    public const string OnTweenDone = "OnTweenDone";

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
            if (!TryRead(io.Parameter, tween, out var channel, out var goal, out bool relative, out float seconds, out var ease))
            {
                Log.Warn(LogCat.Events, $"I/O: {ToInput}({io.Parameter}) at {World.Describe(io.Self)}: expected "
                                      + "\"[position|rotation|scale|offset|turn] [x y z] [seconds] [ease]\"; it stays where it is");
                return;
            }
            tween.Activator = io.Activator;
            Begin(ref tween, world.Get<Transform>(io.Self), channel, goal, relative, seconds, ease);
        });
        engine.Inputs.Register(StopInput, static (World world, in IOContext io) =>
        {
            if (world.Has<Tween>(io.Self)) world.Get<Tween>(io.Self).Playing = false;
        });
        engine.Outputs.Declare(OnTweenDone, "This tween arrived where a TweenTo sent it (sage:tween).");
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
                               out bool relative, out float seconds, out Ease ease)
    {
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
            else if (!Easing.TryParse(token, out ease)) return false;
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
// arrives. Allocation-free.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // easing, timers and tweens (#90)
[System(Id, Phase.EntityIO, Before = new[] { "?sage.io.dispatch" })]
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
        float dt = ctx.Tick.Dt;
        foreach (var (tweens, transforms, entities) in _tweens.Chunks)
        {
            var tw = tweens.Span;
            var tr = transforms.Span;
            for (int i = 0; i < tw.Length; i++)
            {
                ref var tween = ref tw[i];
                if (!tween.Playing) continue;
                tween.Elapsed += dt;
                // Done on the tick its seconds are up, not one later for float dust (Timers.Epsilon).
                bool done = tween.Elapsed >= tween.PlayingDuration - Timers.Epsilon;
                if (done) tween.Elapsed = tween.PlayingDuration;
                Tweens.Apply(tween, ref tr[i]);

                var entity = entities.EntityAt(i);
                if (tween.PlayingChannel == TweenChannel.Position && _space != null
                    && entity.TryGetComponent<PhysicsBody>(out var body) && body.IsStatic)
                    _space.MoveStatic(body, tr[i].LocalPosition);

                if (!done) continue;
                tween.Playing = false;
                _world.FireOutput(entity, Tweens.OnTweenDone, tween.Activator);
            }
        }
    }
}
