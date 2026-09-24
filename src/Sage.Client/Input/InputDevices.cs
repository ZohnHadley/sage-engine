#nullable enable
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace sage_engine;

// The device layer (docs/design/08 §3.1): keyboard, mouse and gamepad, polled once per frame by the
// host before anything reads them. Edges (pressed/released this frame) stay valid until the next
// Poll. Only UI, editor and camera code may use devices directly; gameplay reads PlayerCommand.
public sealed class InputDevices
{
    internal KeyboardListener Keyboard { get; } = new();
    internal MouseListener Mouse { get; } = new();
    public GamepadListener Gamepad { get; } = new();

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
        Gamepad.Update(focused);
    }

    // For `in_contexts`, so "why is nothing responding?" has a visible answer.
    public bool Focused { get; private set; } = true;
}

// Player one's gamepad (08 §3.1). Raw values: dead zones are per binding (InputActions).
public sealed class GamepadListener
{
    private GamePadState _current;
    private GamePadState _previous;

    public bool IsConnected => _connected;

    public event Action<bool>? ConnectionChanged;   // true = connected

    public void Update(bool focused)
    {
        var step = _focus.Step(focused);

        // Read every frame whatever the focus, because **being plugged in is hardware, not input**: a
        // pad connected while the player is reading a wiki in another window is known about by the time
        // they come back, and `IsConnected` does not flicker every time they alt-tab. What the game must
        // not see while the window is not ours is the *buttons* (#60).
        var real = GamePad.GetState(PlayerIndex.One, GamePadDeadZone.None);
        if (real.IsConnected != _connected)
        {
            _connected = real.IsConnected;
            Log.Info(LogCat.Input, _connected ? "Gamepad connected" : "Gamepad disconnected");
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

    public Vector2 LeftStick => _current.ThumbSticks.Left;     // +Y = up
    public Vector2 RightStick => _current.ThumbSticks.Right;
    public float LeftTrigger => _current.Triggers.Left;
    public float RightTrigger => _current.Triggers.Right;
    internal float PreviousTrigger(bool right) => right ? _previous.Triggers.Right : _previous.Triggers.Left;
}
