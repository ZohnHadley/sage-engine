#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// Directional attacks and blocks (issue #359, docs/design/12, TODO F10): which way a fighter is swinging
// or guarding, as Mount & Blade: Warband has it — overhead, from the right, a thrust, from the left —
// chosen by the player's mouse gesture or movement keys, or set by an AI or by code. This is the graph
// side only, and generic: the direction is a number an anim_graph reads (the `AttackDirection` and
// `Blocking` param sources), so a data-only graph picks a swing or a guard by it:
//
//   "params": { "attack": { "kind": "Trigger" },
//               "dir":    { "from": "AttackDirection" },          // 0 none, 1 overhead, 2 right, 3 thrust, 4 left
//               "block":  { "from": "Blocking" } },
//   "transitions": [
//     { "to": "swing_overhead", "on": "attack", "when": { "anim_param": "dir", "max": 1 } },
//     { "to": "swing_right",    "on": "attack", "when": { "anim_param": "dir", "eq": 2 } }, ... ]
//
// Sage.Gameplay chooses the direction each tick (AttackStanceSystem: the `input` below), sets `Blocking`
// from the Block button and keeps the direction a swing started with (Melee.Direction); when the blow
// lands is the swing clip's `hit` event, so every direction keeps its own timing, and a window between
// two clip events is Animators.InWindow and the `anim_window` condition. What a direction does to
// damage — a block that must match the blow, a thrust that hurts more — is the combat and RPG rules'.

// Which way a swing comes from or a guard faces. The numbers are what the AttackDirection param reads.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public enum AttackDirection
{
    // Not chosen yet: a graph's default swing.
    None = 0,
    // From above (the mouse moved up).
    Overhead = 1,
    // From the fighter's right (the mouse moved right).
    Right = 2,
    // Straight ahead (the mouse moved down).
    Thrust = 3,
    // From the fighter's left (the mouse moved left).
    Left = 4,
}

// How a fighter's direction is chosen (AttackStance.Input).
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public enum AttackDirectionInput
{
    // Only code, an AI task or a save sets it: what an AI's fighter uses.
    Manual,
    // From the look movement of the last moments (Warband's "by mouse movement"): up overhead, down a
    // thrust, left from the left, right from the right.
    Mouse,
    // From the movement keys (Warband's "by movement direction"): forward a thrust, back overhead, left and right.
    Movement,
}

// A fighter's direction and guard (see above). Saved: the direction and how it is chosen; the guard and
// the gesture being read start again after a load.
[Component("sage:attack_stance")]
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public struct AttackStance : IComponent
{
    [Property(Tooltip = "How the direction is chosen: Manual (code and AI), Mouse (the look gesture) or Movement (the movement keys)")]
    public AttackDirectionInput Input;
    [Property(Tooltip = "Which way its next swing or its guard goes: None, Overhead, Right, Thrust or Left")]
    public AttackDirection Direction;
    [Property(Min = 0, Unit = "°", Tooltip = "Mouse: how far the look must move (decaying over about a fifth of a second) to choose a direction; 0 = 2°")]
    public float Threshold;

    // Holding its guard up: the Block button (Sage.Gameplay), an AI's Block task, or code.
    [Transient] public bool Blocking;
    // The mouse gesture being read: the look movement so far (degrees: x left, y up), decaying, and the
    // look it was last measured from.
    [Transient] internal Vector2 Gesture;
    [Transient] internal float LastYaw, LastPitch;
    [Transient] internal bool Tracking;
}

[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public static class AttackStances
{
    public const float DefaultThreshold = 2f;      // degrees of look movement
    public const float GestureFade = 0.2f;         // seconds: a look movement older than about this is forgotten
    public const float MovementDeadZone = 0.5f;    // of the movement stick or keys

    // The direction a look gesture chooses: `look` is how far the view moved, x to the left (yaw grows)
    // and y up (pitch grows), in any unit. The larger axis wins; a tie goes to the vertical. None for no movement.
    public static AttackDirection FromGesture(Vector2 look)
    {
        if (!(look.LengthSquared() > 0f) || !float.IsFinite(look.X) || !float.IsFinite(look.Y)) return AttackDirection.None;
        if (MathF.Abs(look.Y) >= MathF.Abs(look.X)) return look.Y > 0f ? AttackDirection.Overhead : AttackDirection.Thrust;
        return look.X > 0f ? AttackDirection.Left : AttackDirection.Right;
    }

    // The direction the movement keys choose (PawnIntent.Move: x right, y forward): forward a thrust,
    // back overhead, left and right; None inside the dead zone.
    public static AttackDirection FromMovement(Vector2 move)
    {
        if (!float.IsFinite(move.X) || !float.IsFinite(move.Y)) return AttackDirection.None;
        if (MathF.Max(MathF.Abs(move.X), MathF.Abs(move.Y)) < MovementDeadZone) return AttackDirection.None;
        if (MathF.Abs(move.Y) >= MathF.Abs(move.X)) return move.Y > 0f ? AttackDirection.Thrust : AttackDirection.Overhead;
        return move.X > 0f ? AttackDirection.Right : AttackDirection.Left;
    }

    // One tick of choosing: what Sage.Gameplay's AttackStanceSystem does with each fighter's intent. The
    // gesture is measured from the intent's own look, so a player's mouse and an AI turning its head read alike.
    public static void Step(ref AttackStance stance, in PawnIntent intent, float dt)
    {
        if (!stance.Tracking || !float.IsFinite(stance.Gesture.X) || !float.IsFinite(stance.Gesture.Y))
        {
            stance.Tracking = true;
            stance.Gesture = Vector2.Zero;
            stance.LastYaw = intent.Yaw;
            stance.LastPitch = intent.Pitch;
        }
        const float deg = 180f / MathF.PI;
        var moved = new Vector2(SageMath.WrapPi(intent.Yaw - stance.LastYaw) * deg, (intent.Pitch - stance.LastPitch) * deg);
        stance.LastYaw = intent.Yaw;
        stance.LastPitch = intent.Pitch;
        if (!float.IsFinite(moved.X) || !float.IsFinite(moved.Y)) moved = Vector2.Zero;

        switch (stance.Input)
        {
            case AttackDirectionInput.Mouse:
            {
                float keep = dt > 0f ? MathF.Exp(-dt / GestureFade) : 1f;
                stance.Gesture = stance.Gesture * keep + moved;
                float threshold = stance.Threshold > 0f && float.IsFinite(stance.Threshold) ? stance.Threshold : DefaultThreshold;
                if (stance.Gesture.Length() >= threshold) stance.Direction = FromGesture(stance.Gesture);
                break;
            }
            case AttackDirectionInput.Movement:
            {
                stance.Gesture = Vector2.Zero;
                var chosen = FromMovement(intent.Move);
                if (chosen != AttackDirection.None) stance.Direction = chosen;
                break;
            }
            default:
                stance.Gesture = Vector2.Zero;
                break;
        }
    }

    // The fighter's direction (None without a stance).
    public static AttackDirection Of(World world, Entity entity) =>
        !entity.IsNull && world.IsAlive(entity) && world.TryGet<AttackStance>(entity, out var stance) ? stance.Direction : AttackDirection.None;

    // Whether it holds its guard up.
    public static bool IsBlocking(World world, Entity entity) =>
        !entity.IsNull && world.IsAlive(entity) && world.TryGet<AttackStance>(entity, out var stance) && stance.Blocking;

    // Sets the direction (an AI's choice, a script's). False without a stance: the prefab gives one (the
    // `sage:attack_stance` component), so a system never has to add a component mid-tick.
    public static bool Set(World world, Entity entity, AttackDirection direction)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (entity.IsNull || !world.IsAlive(entity) || !world.Has<AttackStance>(entity)) return false;
        world.Get<AttackStance>(entity).Direction = direction;
        return true;
    }
}
