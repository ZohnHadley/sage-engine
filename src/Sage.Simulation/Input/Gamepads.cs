#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// Several pads, several players, and what the player is using (docs/design/08 §3.1, issue #331).
//
// **Decisions, so they live here**, where the headless tests reach them: the client owns
// `GamePad.GetState`, which decides nothing; which pad belongs to which player, when a pad that was
// unplugged comes back, what counts as "the player picked up the pad", and how a stick's travel becomes an
// axis are rules.

// Which physical pad (0..3) drives which local player (0..3).
//
// - A connected pad with no player goes to the lowest player without a pad, in pad order — so a game with
//   one player and a pad in the second port is played with that pad.
// - **Hot-swap:** a pad that goes away leaves its player without one but remembers it, and the same pad
//   coming back returns to that player even when another pad has plugged in meanwhile and an earlier
//   player is also waiting. Otherwise unplugging and replugging two pads would swap the players.
// - A pad no player wants (more pads than local players) is ignored until a player is free.
public sealed class PadAssignment
{
    public const int MaxPads = 4;

    private readonly int[] _padOfPlayer = new int[MaxPads];     // -1 = none
    private readonly int[] _lastPadOfPlayer = new int[MaxPads]; // the pad a player had until it went away
    private readonly int[] _playerOfPad = new int[MaxPads];
    private readonly bool[] _connected = new bool[MaxPads];
    private int _players = 1;

    public PadAssignment()
    {
        Array.Fill(_padOfPlayer, -1);
        Array.Fill(_lastPadOfPlayer, -1);
        Array.Fill(_playerOfPad, -1);
    }

    // How many local players there are (1..4): `joy_players`. Lowering it frees the pads of the players
    // that went.
    public int Players
    {
        get => _players;
        set
        {
            _players = Math.Clamp(value, 1, MaxPads);
            for (int p = _players; p < MaxPads; p++)
                if (_padOfPlayer[p] >= 0) { _playerOfPad[_padOfPlayer[p]] = -1; _padOfPlayer[p] = -1; _lastPadOfPlayer[p] = -1; }
        }
    }

    // The pad a player holds, or -1 when they have none (a keyboard player, or a pad unplugged).
    public int PadOf(int player) => (uint)player < MaxPads ? _padOfPlayer[player] : -1;

    // The player a pad drives, or -1.
    public int PlayerOf(int pad) => (uint)pad < MaxPads ? _playerOfPad[pad] : -1;

    public bool IsConnected(int pad) => (uint)pad < MaxPads && _connected[pad];

    public readonly record struct Change(int Pad, int Player, bool Gained);

    // Once a frame with which pads are plugged in. Returns what changed, in order (a player lost a pad,
    // a player got one) so the host can log it and a game can pause: `Gained = false` is a hot-unplug.
    public ReadOnlySpan<Change> Update(ReadOnlySpan<bool> connected)
    {
        int n = 0;
        var changes = _changes;
        for (int pad = 0; pad < MaxPads; pad++)
        {
            bool now = pad < connected.Length && connected[pad];
            _connected[pad] = now;
            int player = _playerOfPad[pad];
            if (!now && player >= 0)
            {
                _playerOfPad[pad] = -1;
                _padOfPlayer[player] = -1;
                _lastPadOfPlayer[player] = pad;     // remembered: it may come back
                changes[n++] = new Change(pad, player, false);
            }
        }
        for (int pad = 0; pad < MaxPads; pad++)
        {
            if (!_connected[pad] || _playerOfPad[pad] >= 0) continue;
            int player = -1;
            for (int p = 0; p < _players && player < 0; p++)    // its own player first
                if (_padOfPlayer[p] < 0 && _lastPadOfPlayer[p] == pad) player = p;
            // Then the lowest player with none, unless that one is still waiting for a different pad that
            // is plugged in right now (it will be handed out in its turn).
            for (int p = 0; p < _players && player < 0; p++)
                if (_padOfPlayer[p] < 0 && !(_lastPadOfPlayer[p] >= 0 && _lastPadOfPlayer[p] != pad && _connected[_lastPadOfPlayer[p]] && _playerOfPad[_lastPadOfPlayer[p]] < 0))
                    player = p;
            if (player < 0) continue;
            _padOfPlayer[player] = pad;
            _playerOfPad[pad] = player;
            _lastPadOfPlayer[player] = -1;
            changes[n++] = new Change(pad, player, true);
        }
        return new ReadOnlySpan<Change>(changes, 0, n);
    }

    private readonly Change[] _changes = new Change[MaxPads * 2];

    // Hands a pad to a player by hand (`pad_assign`): whoever held either is left without. False when the
    // numbers are out of range or the pad is not plugged in.
    public bool Assign(int player, int pad)
    {
        if ((uint)player >= (uint)_players || (uint)pad >= MaxPads || !_connected[pad]) return false;
        if (_playerOfPad[pad] is var other and >= 0) { _padOfPlayer[other] = -1; _lastPadOfPlayer[other] = -1; }
        if (_padOfPlayer[player] is var old and >= 0) _playerOfPad[old] = -1;
        _padOfPlayer[player] = pad;
        _playerOfPad[pad] = player;
        _lastPadOfPlayer[player] = -1;
        return true;
    }
}

// A stick's travel, and how it becomes an axis (`joy_deadzone`, `joy_curve`).
public static class StickResponse
{
    // Radial dead zone, rescaled so the output still reaches 1, then the response curve on the length:
    // `exponent` 1 is linear, 2 finer near the centre (quadratic), 3 finer still (cubic). The direction is
    // kept, which is what a radial zone is for: a square one turns a diagonal push into a staircase.
    public static Vector2 Apply(Vector2 value, float deadzone, float exponent = 1f)
    {
        float length = value.Length();
        if (length <= deadzone || length <= 0f) return Vector2.Zero;
        float t = Math.Min(1f, (length - deadzone) / MathF.Max(1e-4f, 1f - deadzone));
        return value / length * Curve(t, exponent);
    }

    public static float Apply(float value, float deadzone, float exponent = 1f)
    {
        float magnitude = MathF.Abs(value);
        if (magnitude <= deadzone) return 0f;
        float t = Math.Min(1f, (magnitude - deadzone) / MathF.Max(1e-4f, 1f - deadzone));
        return MathF.Sign(value) * Curve(t, exponent);
    }

    // 0..1 in, 0..1 out.
    public static float Curve(float t, float exponent) =>
        exponent == 1f ? t : MathF.Pow(Math.Clamp(t, 0f, 1f), Math.Max(0.1f, exponent));

    // `joy_curve`: a name (linear, quadratic, cubic) or an exponent.
    public static bool TryParseCurve(string text, out float exponent)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "linear": exponent = 1f; return true;
            case "quadratic": exponent = 2f; return true;
            case "cubic": exponent = 3f; return true;
        }
        return float.TryParse(text, System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture, out exponent) && exponent >= 0.1f && exponent <= 8f;
    }
}

// Which family of device the player last touched, for the prompts: a hint says "E" or "A" by this.
public enum InputDeviceKind { KeyboardMouse, Gamepad }

// What happened on the devices this frame, as the host gathers it.
public readonly record struct DeviceActivity
{
    public bool Key { get; init; }
    public bool MouseButton { get; init; }
    public float MouseMoved { get; init; }     // pixels this frame
    public bool PadButton { get; init; }
    public float PadStick { get; init; }       // the furthest any stick or trigger is pushed, 0..1
}

// The last-used-device signal. A stick resting off-centre or a mouse nudged by the desk is not "using"
// it, so axes need real travel (`MouseThreshold`, `StickThreshold`) and a button or key is always
// enough; when both families do something in one frame the one already in use keeps it, so a pad lying
// on the keyboard desk does not make the prompts flicker.
public sealed class LastUsedDevice
{
    public const float MouseThreshold = 3f;
    public const float StickThreshold = 0.5f;

    public InputDeviceKind Current { get; private set; } = InputDeviceKind.KeyboardMouse;

    // Raised once per change.
    public event Action<InputDeviceKind>? Changed;

    public void Observe(in DeviceActivity a)
    {
        bool keyboard = a.Key || a.MouseButton || a.MouseMoved >= MouseThreshold;
        bool pad = a.PadButton || a.PadStick >= StickThreshold;
        if (!keyboard && !pad) return;
        if (keyboard && pad) return;   // both at once: stay as we are
        Set(pad ? InputDeviceKind.Gamepad : InputDeviceKind.KeyboardMouse);
    }

    public void Set(InputDeviceKind kind)
    {
        if (kind == Current) return;
        Current = kind;
        Changed?.Invoke(kind);
    }
}

// Picks the binding a prompt should show for the device in use.
public static class InputGlyphs
{
    // The first binding of the family `device` names ("E", "Pad A"); the other family's first when the
    // action has none of this one (an action only the keyboard can do still shows a key); null when the
    // action has no bindings.
    public static InputBinding? Pick(System.Collections.Generic.IReadOnlyList<InputBinding>? bindings, InputDeviceKind device)
    {
        if (bindings == null) return null;
        InputBinding? other = null;
        foreach (var b in bindings)
        {
            if (b.IsGamepad == (device == InputDeviceKind.Gamepad)) return b;
            other ??= b;
        }
        return other;
    }
}
