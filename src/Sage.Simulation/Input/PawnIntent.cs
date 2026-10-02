#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// Controllers and pawns (docs/design/16 §3.1). A player and an AI drive a body the same way: they
// write PawnIntent, and movement, combat and interaction only ever read that. It is the seed of the
// gameplay framework; GameRules, possession and AI controllers come with 16/F19.

// Marks a body a controller can drive.
[Component("sage:pawn")]
public struct Pawn : IComponent { }

// Tag: this pawn is driven by the local player's PlayerCommand (08).
[Tag("sage:player_controlled")]
public struct PlayerControlled : ITag { }

// What a controller wants the pawn to do this tick: written by controllers in the Commands phase,
// read by movement, combat and interaction later in the same tick. Written by controllers only.
// `IEquatable` is not decoration: this component is under a phase contract (03 §3.5), and the check
// compares it after every phase. Without a typed `Equals`, `EqualityComparer<T>.Default` falls back to
// `ValueType.Equals(object)` and **boxes both values on every comparison** — which measured at about
// 3 KB per character per tick, and is what R18's scale test found first.
// [FromPlacement]: the yaw starts as the placement's, so a save compares it with this pawn's own (4m-4).
[Component("sage:pawn_intent")]
[FromPlacement]
public struct PawnIntent : IComponent, IEquatable<PawnIntent>
{
    [Transient] public Vector2 Move;       // rewritten by a controller every tick
    public float Yaw, Pitch;               // radians: the authoritative facing, so it *is* saved
    [Transient] public ActionMask Held;    // bits over runtime action ids: meaningless next run
    [Transient] public ActionMask Pressed; // and a single-tick edge besides

    public readonly bool Equals(PawnIntent other) =>
        Move.Equals(other.Move) && Yaw.Equals(other.Yaw) && Pitch.Equals(other.Pitch)
        && Held.Equals(other.Held) && Pressed.Equals(other.Pressed);

    public readonly override bool Equals(object? obj) => obj is PawnIntent other && Equals(other);

    public readonly override int GetHashCode() => HashCode.Combine(Move, Yaw, Pitch, Held, Pressed);
}

// Commands phase: the local player's command becomes intent (16 §3.1, 08 §3.4). AI controllers write
// the same component from their own state, so everything downstream is shared.
[System("sage.character.player_control", Phase.Commands)]
public sealed class PlayerControlSystem : ISystem
{
    private readonly Query<PawnIntent> _pawns;

    public PlayerControlSystem(World world)
    {
        _pawns = world.Query<PawnIntent>().AllTags(Tags.Get<PlayerControlled>());
    }

    public void Run(in SystemContext ctx)
    {
        var input = ctx.World.Resources.Get<PlayerInput>();
        if (!input.HasCommand) return;
        ref readonly var command = ref input.Command;

        foreach (var (intents, _) in _pawns.Chunks)
        {
            var intent = intents.Span;
            for (int n = 0; n < intent.Length; n++)
            {
                intent[n].Move = command.Move;
                intent[n].Yaw = command.ViewYaw;
                intent[n].Pitch = command.ViewPitch;
                intent[n].Held = command.Held;
                intent[n].Pressed = command.Pressed;
            }
        }
    }
}
