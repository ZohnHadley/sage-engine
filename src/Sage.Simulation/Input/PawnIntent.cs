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

// Which local player drives a pawn that is `PlayerControlled` (issue #331). A pawn without it is the first
// player's, so every existing game and save is unchanged; the second player's pawn carries `Index = 1`.
[Component("sage:player_slot")]
public struct PlayerSlot : IComponent
{
    [Property(Min = 0, Max = 3, Tooltip = "Which local player drives this pawn: 0 is the first, 1 the second")]
    public int Index;
}

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
    private readonly Query<PawnIntent, PlayerSlot> _slotted;

    public PlayerControlSystem(World world)
    {
        _pawns = world.Query<PawnIntent>().AllTags(Tags.Get<PlayerControlled>()).WithoutComponent<PlayerSlot>();
        _slotted = world.Query<PawnIntent, PlayerSlot>().AllTags(Tags.Get<PlayerControlled>());
    }

    public void Run(in SystemContext ctx)
    {
        var input = ctx.World.Resources.Get<PlayerInput>();
        if (input.HasCommand)
        {
            foreach (var (intents, _) in _pawns.Chunks)
            {
                var intent = intents.Span;
                for (int n = 0; n < intent.Length; n++) Drive(ref intent[n], in input.Command);
            }
        }

        foreach (var (intents, slots, _) in _slotted.Chunks)
        {
            var intent = intents.Span;
            var slot = slots.Span;
            for (int n = 0; n < intent.Length; n++)
                if (input.TryGetCommand(slot[n].Index, out var command)) Drive(ref intent[n], in command);
        }
    }

    private static void Drive(ref PawnIntent intent, in PlayerCommand command)
    {
        intent.Move = command.Move;
        intent.Yaw = command.ViewYaw;
        intent.Pitch = command.ViewPitch;
        intent.Held = command.Held;
        intent.Pressed = command.Pressed;
    }
}
