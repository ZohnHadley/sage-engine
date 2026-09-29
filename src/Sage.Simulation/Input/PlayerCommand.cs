#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Simulation;

// Input as data (docs/design/08). The simulation side: actions are registered here by modules in
// Init, the client evaluates bindings and fills a CommandLatch every frame, and the host hands one
// PlayerCommand per fixed tick to the world (PlayerInput resource). Gameplay reads commands, never
// devices (readiness rule 2: a server would receive exactly these).

public enum ActionKind { Button, Axis1D, Axis2D }

// Index into the ActionRegistry; Bit is the ActionMask bit for buttons (-1 for axes).
public readonly record struct ActionId(int Index, int Bit)
{
    public static ActionId None => new(-1, -1);
    public bool IsValid => Index >= 0;
}

public sealed record ActionInfo(ActionId Id, string Name, ActionKind Kind);

public sealed class ActionRegistry
{
    public const int MaxButtons = 64;

    private readonly List<ActionInfo> _actions = new();
    private readonly Dictionary<string, ActionInfo> _byName = new(StringComparer.OrdinalIgnoreCase);
    private int _buttons;

    public IReadOnlyList<ActionInfo> All => _actions;

    // Closed when content loads (SageApp.LoadContent): bindings are built from the input records then.
    public RegistrationSeal Seal { get; } = new("input action", "the bindings built when the records loaded have none for it");

    // Who registered each action (issue #12); set by the Engine.
    public RegistrationLedger? Ledger { get; set; }

    // Modules register their actions in Init. Registering the same name again with the same kind
    // returns the existing id (two modules may both need "Use").
    public ActionId Register(string name, ActionKind kind)
    {
        if (_byName.TryGetValue(name, out var existing))
        {
            if (existing.Kind != kind)
                throw new InvalidOperationException($"Action '{name}' is already registered as {existing.Kind}, not {kind}.");
            return existing.Id;
        }
        Seal.Check(name);
        int bit = -1;
        if (kind == ActionKind.Button)
        {
            if (_buttons == MaxButtons)
                throw new InvalidOperationException($"More than {MaxButtons} button actions (docs/design/08 §3.4): can't register '{name}'.");
            bit = _buttons++;
        }
        var info = new ActionInfo(new ActionId(_actions.Count, bit), name, kind);
        _actions.Add(info);
        _byName[name] = info;
        Ledger?.Record("input action", name);
        return info.Id;
    }

    public bool TryGet(string name, out ActionInfo info) => _byName.TryGetValue(name, out info!);

    public ActionId Get(string name) => _byName.TryGetValue(name, out var info) ? info.Id : ActionId.None;

    public ActionInfo this[ActionId id] => _actions[id.Index];
}

// Button actions as bits (08 §3.4).
public readonly record struct ActionMask(ulong Bits)
{
    public bool Has(ActionId action) => action.Bit >= 0 && (Bits & (1UL << action.Bit)) != 0;
    public ActionMask With(ActionId action) => action.Bit >= 0 ? new ActionMask(Bits | (1UL << action.Bit)) : this;
    public bool IsEmpty => Bits == 0;
}

// One tick's worth of player intent (08 §3.4).
public struct PlayerCommand
{
    public long Tick;
    public Vector2 Move;              // x = right, y = forward; length ≤ 1; relative to the view yaw
    public float ViewYaw, ViewPitch;  // absolute view angles at sample time (radians)
    public ActionMask Held;           // buttons held at sample time
    public ActionMask Pressed;        // went down at any point since the previous command
    public ActionMask Released;       // went up at any point since the previous command
}

// World resource: the local player's command for the tick being simulated (set by the host before
// each fixed tick; read in the Commands phase). HasCommand is false in headless worlds.
public sealed class PlayerInput
{
    public PlayerCommand Command;
    public bool HasCommand;

    // A request to point the player somewhere: a teleport, a respawn, a map's `info_player_start`.
    //
    // The engine cannot just turn the view. The angles are accumulated in the host's `CommandLatch` at
    // frame rate, and `PawnIntent.Yaw` is overwritten from the resulting command every tick — so
    // anything the simulation writes to a transform is gone one tick later, silently, which is what
    // `map_goto` did at first: it landed the player in the right room facing whatever way they had been.
    // So the simulation asks here, and the host applies it before it samples the next command.
    private float _yaw, _pitch;
    private bool _wanted;

    public void RequestView(float yaw, float pitch = 0f) { _yaw = yaw; _pitch = pitch; _wanted = true; }

    public bool TryTakeView(out float yaw, out float pitch)
    {
        yaw = _yaw;
        pitch = _pitch;
        bool wanted = _wanted;
        _wanted = false;         // a request is answered once: the player is free to look away again
        return wanted;
    }
}

// Accumulates per-frame input between ticks (08 §3.4): taps shorter than a tick aren't lost, and the
// view angles advance at frame rate (look is applied every frame, before the ticks run).
public sealed class CommandLatch
{
    public static readonly float PitchLimit = 89f * MathF.PI / 180f;

    private ulong _held, _pressed, _released;
    private Vector2 _move;

    public float ViewYaw { get; private set; }
    public float ViewPitch { get; private set; }

    // Once per frame, with this frame's resolved action state.
    public void AddFrame(ActionMask held, ActionMask pressed, ActionMask released, Vector2 move)
    {
        _held = held.Bits;
        _pressed |= pressed.Bits;
        _released |= released.Bits;
        _move = move.LengthSquared() > 1f ? Vector2.Normalize(move) : move;
    }

    // Radians: +x turns right (yaw decreases: yaw is counter-clockwise seen from above, like
    // Quaternion.CreateFromYawPitchRoll), +y looks up. The client has already scaled mouse and stick.
    public void AddLook(Vector2 delta)
    {
        ViewYaw = WrapAngle(ViewYaw - delta.X);
        ViewPitch = Math.Clamp(ViewPitch + delta.Y, -PitchLimit, PitchLimit);
    }

    public void SetView(float yaw, float pitch)
    {
        ViewYaw = WrapAngle(yaw);
        ViewPitch = Math.Clamp(pitch, -PitchLimit, PitchLimit);
    }

    // Before each fixed tick. Pressed/Released are cleared once a command carries them.
    public PlayerCommand Sample(long tick)
    {
        var command = new PlayerCommand
        {
            Tick = tick,
            Move = _move,
            ViewYaw = ViewYaw,
            ViewPitch = ViewPitch,
            Held = new ActionMask(_held),
            Pressed = new ActionMask(_pressed),
            Released = new ActionMask(_released),
        };
        _pressed = 0;
        _released = 0;
        return command;
    }

    private static float WrapAngle(float a)
    {
        a = MathF.IEEERemainder(a, MathF.Tau);
        return a <= -MathF.PI ? a + MathF.Tau : a;
    }
}

// Who gets input (08 §3.3), highest priority first.
public enum InputContext { Editor, Console, UI, Gameplay }

// One binding of an action (08 §4). Exactly one of Key / Mouse / Gamepad / Composite is set.
public sealed class InputBinding
{
    public string? Key;         // MonoGame Keys name: "Space", "E", "OemTilde"
    public string? Mouse;       // Left, Right, Middle, Delta, Wheel
    public string? Gamepad;     // A, B, X, Y, Start, Back, LeftShoulder, RightShoulder, LeftTrigger, RightTrigger,
                                // LeftStick, RightStick, LeftStickButton, RightStickButton, DPadUp/Down/Left/Right
    public string? Composite;   // "WASD", "Arrows" (Axis2D)
    public float Scale = 1f;
    public float Deadzone = 0.2f; // sticks and triggers
    public bool Invert;           // flips Y for Axis2D, the value for Axis1D
    public bool Rate;             // the value is per second (sticks turning the view): multiplied by the frame time

    public override string ToString() =>
        Key != null ? $"key {Key}" : Mouse != null ? $"mouse {Mouse}" : Gamepad != null ? $"gamepad {Gamepad}" : Composite != null ? $"composite {Composite}" : "(empty)";
}

// Input map (08 §4): bindings for one context. Several maps may target the same context (engine,
// game, mods); their bindings add up. Player rebinds will be record patches (08 §3.2).
[Record("input_map", Plugin = "sage.client")]
public sealed class InputMapRecord
{
    public InputContext Context = InputContext.Gameplay;
    public Dictionary<string, List<InputBinding>> Actions = new(StringComparer.OrdinalIgnoreCase);
}
