#nullable enable
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Sage.Client;

// The device layer (docs/design/08 §3.1): keyboard, mouse and gamepad, polled once per frame by the
// host before anything reads them. Edges (pressed/released this frame) stay valid until the next
// Poll. Only UI, editor and camera code may use devices directly; gameplay reads PlayerCommand.
public sealed class InputDevices
{
    public KeyboardListener Keyboard { get; } = new();
    public MouseListener Mouse { get; } = new();

    // Every physical pad (0..3) is read; which of them is a player's is `Pads` (issue #331). `Gamepad` is
    // the first player's pad — a neutral, unplugged one when that player has none — so code that has only
    // ever known one pad is unchanged.
    public GamepadListener[] AllPads { get; } = { new(0), new(1), new(2), new(3) };
    public PadAssignment Pads { get; }
    public LastUsedDevice LastDevice { get; }
    public GamepadListener Gamepad => PadFor(0);

    public InputDevices() : this(null, null) { }

    public InputDevices(PadAssignment? pads, LastUsedDevice? last)
    {
        Pads = pads ?? new PadAssignment();
        LastDevice = last ?? new LastUsedDevice();
        Keyboard.OnKeyPressed += _ => _keyThisFrame = true;
        Mouse.OnButtonPressed += _ => _mouseButtonThisFrame = true;
    }

    // The pad a local player holds, or a neutral one.
    public GamepadListener PadFor(int player)
    {
        int pad = Pads.PadOf(player);
        return pad >= 0 ? AllPads[pad] : GamepadListener.None;
    }

    private bool _keyThisFrame, _mouseButtonThisFrame;
    private readonly bool[] _connected = new bool[PadAssignment.MaxPads];
    private readonly float[] _sentLow = new float[PadAssignment.MaxPads], _sentHigh = new float[PadAssignment.MaxPads];

    // What was *typed* this frame, in order, as the operating system decided it: the host feeds this
    // from the window's text-input event (08 §3.1). Keys are not characters — a keyboard layout, a
    // dead key and a modifier all sit between them, and reading `Keys.A` to mean "a" is how a name
    // typed on an AZERTY keyboard comes out wrong.
    //
    // Cleared every Poll, so a reader takes it in the same frame or misses it. Backspace arrives as
    // the backspace character and Return as the carriage return, which is why a field handles both.
    public ReadOnlySpan<char> Typed => _typed.AsSpan(0, _typedCount);

    private char[] _typed = new char[32];
    private int _typedCount;

    public void PushTyped(char c)
    {
        if (_typedCount == _typed.Length) Array.Resize(ref _typed, _typed.Length * 2);
        _typed[_typedCount++] = c;
    }

    // Called by the host once the frame has had its chance to read `Typed` (after the Frame schedule),
    // not at Poll: the window delivers characters *before* Update and a console script pushes them
    // *during* it, so clearing at the start of input would drop one or the other.
    public void EndFrame() => _typedCount = 0;

    // `focused` is the window's own `IsActive`. **A game must not act on input it is not the target
    // of** (08 §3.1, review #60): a click in somebody's browser was reaching the player's sword, and a
    // key held as they alt-tabbed stayed held. `FocusPolicy` (engine-side, tested) owns what happens at
    // the seam; the listeners just do what it says.
    public void Poll(bool focused)
    {
        // Worth a line each way: "the game ignores my keyboard" is otherwise a mystery, and this says
        // whose window it thinks it is. Debug, so it is compiled out of Shipping (02 §3.2).
        if (focused != Focused)
            Log.Debug(LogCat.Input, focused ? "Window focused: reading devices again"
                                            : "Window unfocused: devices ignored until it comes back");
        Focused = focused;
        Mouse.update(focused);
        Keyboard.Update(focused);
        for (int i = 0; i < AllPads.Length; i++)
        {
            AllPads[i].Update(focused);
            _connected[i] = AllPads[i].IsConnected;
        }

        // Hot-swap: who has which pad is re-decided every frame from what is plugged in (`PadAssignment`).
        foreach (var change in Pads.Update(_connected))
        {
            Log.Info(LogCat.Input, change.Gained ? $"Pad {change.Pad + 1} now drives player {change.Player + 1}"
                                                  : $"Pad {change.Pad + 1} lost: player {change.Player + 1} has no pad");
            if (!change.Gained) { _sentLow[change.Pad] = _sentHigh[change.Pad] = 0f; }
        }

        // What the player touched, for the prompts. Events from the listeners flag key and button presses;
        // the axes are measured.
        float stick = 0f;
        bool padButton = false;
        GamepadListener? used = null;
        foreach (var pad in AllPads)
        {
            bool pressed = pad.AnyButtonPressed;
            float axis = pad.MaxAxis;
            if (used == null && (pressed || axis >= LastUsedDevice.StickThreshold)) used = pad;
            padButton |= pressed;
            stick = MathF.Max(stick, axis);
        }
        // Whose button names the prompts show: the family of the pad just used, by its name (#352).
        if (used != null) LastDevice.SetPad(InputGlyphs.FamilyOf(used.Name));
        var delta = Mouse.PositionDelta;
        LastDevice.Observe(new DeviceActivity
        {
            Key = _keyThisFrame, MouseButton = _mouseButtonThisFrame,
            MouseMoved = focused ? MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y) : 0f,
            PadButton = padButton, PadStick = stick,
        });
        _keyThisFrame = _mouseButtonThisFrame = false;
    }

    // Applies the mixer's motors to the pads, once a frame. Only a changed value is sent, and the motors
    // are silenced while the window is not ours (a game must not buzz a pad in the background).
    public void ApplyRumble(RumbleMixer? mixer, bool focused)
    {
        for (int player = 0; player < PadAssignment.MaxPads; player++)
        {
            int pad = Pads.PadOf(player);
            if (pad < 0) continue;
            float low = focused && mixer != null ? mixer.Low(player) : 0f;
            float high = focused && mixer != null ? mixer.High(player) : 0f;
            if (low == _sentLow[pad] && high == _sentHigh[pad]) continue;
            _sentLow[pad] = low;
            _sentHigh[pad] = high;
            GamePad.SetVibration((PlayerIndex)pad, low, high);
        }
    }

    // For `in_contexts`, so "why is nothing responding?" has a visible answer.
    public bool Focused { get; private set; } = true;
}

// One physical gamepad (08 §3.1, issue #331). Raw values: dead zones are per binding and `joy_deadzone` (InputActions).
public sealed class GamepadListener
{
    // A pad that is not there: reads neutral, never connects. What a player with no pad is handed.
    public static readonly GamepadListener None = new(-1);

    private readonly int _index;

    public GamepadListener() : this(0) { }

    public GamepadListener(int index) => _index = index;

    private GamePadState _current;
    private GamePadState _previous;

    public bool IsConnected => _connected;

    // What the pad's driver calls it ("Xbox 360 Controller", "PS4 Controller"); empty while unplugged.
    public string Name { get; private set; } = "";

    public event Action<bool>? ConnectionChanged;   // true = connected

    public void Update(bool focused)
    {
        if (_index < 0) return;
        var step = _focus.Step(focused);

        // Read every frame whatever the focus, because **being plugged in is hardware, not input**: a
        // pad connected while the player is reading a wiki in another window is known about by the time
        // they come back, and `IsConnected` does not flicker every time they alt-tab. What the game must
        // not see while the window is not ours is the *buttons* (#60).
        var real = GamePad.GetState((PlayerIndex)_index, GamePadDeadZone.None);
        if (real.IsConnected != _connected)
        {
            _connected = real.IsConnected;
            Name = _connected ? GamePad.GetCapabilities((PlayerIndex)_index).DisplayName ?? "" : "";
            Log.Info(LogCat.Input, _connected ? $"Gamepad {_index + 1} connected ({Name})" : $"Gamepad {_index + 1} disconnected");
            ConnectionChanged?.Invoke(_connected);
        }

        switch (step)
        {
            case FocusStep.Neutral:
                _previous = _current;
                _current = default;       // sticks centred, triggers released, nothing held
                break;
            case FocusStep.ReadAndResync:
                _current = real;
                _previous = real;         // a trigger already held is not a press on the way back in
                break;
            default:
                _previous = _current;
                _current = real;
                break;
        }
    }

    private FocusPolicy _focus;
    private bool _connected;

    public bool IsDown(Buttons button) => _current.IsButtonDown(button);
    public bool IsPressed(Buttons button) => _current.IsButtonDown(button) && !_previous.IsButtonDown(button);
    public bool IsReleased(Buttons button) => !_current.IsButtonDown(button) && _previous.IsButtonDown(button);

    // For the last-used-device signal: a button went down this frame, and how far the furthest stick or trigger is pushed.
    public bool AnyButtonPressed
    {
        get
        {
            foreach (var button in AllButtons)
                if (_current.IsButtonDown(button) && !_previous.IsButtonDown(button)) return true;
            return false;
        }
    }
    private static readonly Buttons[] AllButtons = (Buttons[])Enum.GetValues(typeof(Buttons));
    public float MaxAxis => MathF.Max(MathF.Max(_current.ThumbSticks.Left.Length(), _current.ThumbSticks.Right.Length()),
                                      MathF.Max(_current.Triggers.Left, _current.Triggers.Right));

    public Vector2 LeftStick => _current.ThumbSticks.Left;     // +Y = up
    public Vector2 RightStick => _current.ThumbSticks.Right;
    public float LeftTrigger => _current.Triggers.Left;
    public float RightTrigger => _current.Triggers.Right;
    internal float PreviousTrigger(bool right) => right ? _previous.Triggers.Right : _previous.Triggers.Left;
}
