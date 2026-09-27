using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace sage_engine;
internal class MouseListener
{
    // Cached once to avoid Enum.GetValues allocating/boxing on every frame.
    private static readonly MouseButton[] AllButtons = (MouseButton[])Enum.GetValues(typeof(MouseButton));

    // Pixels of cursor travel from the press point before a hold becomes a drag.
    // Below this it's treated as a click; this is the click-vs-drag dead zone.
    public float DragThreshold = 5f;

    private MouseState _currentMouseState;
    private MouseState _previousMouseState;
    private FocusPolicy _focus;

    // Per-button drag state: where the button was pressed (anchor), and whether the
    // threshold has been crossed (i.e. an active drag gesture is in progress).
    private readonly Dictionary<MouseButton, Point> _pressAnchor = new Dictionary<MouseButton, Point>();
    private readonly Dictionary<MouseButton, bool> _dragging = new Dictionary<MouseButton, bool>();

    // ---- Events (edge / per-frame notifications) ----
    // Button events fire on edges only; use IsButtonDown for continuous checks.
    public event Action<MouseButton>? OnButtonPressed;   // up   -> down (single frame)
    public event Action<MouseButton>? OnButtonReleased;  // down -> up   (single frame)

    // Per-frame cursor movement delta (current position - previous position).
    public event Action<Point>? OnMouseMoved;
    // Per-frame scroll change (positive = wheel scrolled up / away from the user).
    public event Action<int>? OnScroll;

    // ---- Drag gesture (threshold-gated, with a start/update/end lifecycle) ----
    public event Action<MouseButton, Point>? OnDragStart; // once, when the threshold is crossed; passes the anchor (press position)
    public event Action<MouseButton, Point>? OnDrag;      // each frame the cursor moves while dragging; passes the per-frame delta
    public event Action<MouseButton>? OnDragEnd;          // when the button is released after a drag

    // ---- Queryable state (polling API) ----
    public Point Position => new Point(_currentMouseState.X, _currentMouseState.Y);
    public Point PreviousPosition => new Point(_previousMouseState.X, _previousMouseState.Y);
    public Point PositionDelta => new Point(
        _currentMouseState.X - _previousMouseState.X,
        _currentMouseState.Y - _previousMouseState.Y);
    public bool IsMoving => PositionDelta != Point.Zero;

    // Normalized direction of travel this frame in screen space (+X right, +Y down).
    // Vector2.Zero when the cursor is stationary.
    public Vector2 MoveDirection
    {
        get
        {
            Point direction = PositionDelta;
            if (direction == Point.Zero) return Vector2.Zero;
            Vector2 v = new Vector2(direction.X, direction.Y);
            v.Normalize();
            return v;
        }
    }

    // Absolute cumulative scroll value, and the change since last frame.
    public int ScrollWheelValue => _currentMouseState.ScrollWheelValue;
    public int ScrollWheelDelta => _currentMouseState.ScrollWheelValue - _previousMouseState.ScrollWheelValue;

    // Bit-flags of every button currently held this frame (for combo detection).
    public MouseButton PressedButtons
    {
        get
        {
            MouseButton pressed = 0;
            foreach (MouseButton button in AllButtons)
                if (IsButtonDown(button)) pressed |= button;
            return pressed;
        }
    }

    public MouseListener()
    {
        // Seed both states so the first Update doesn't report a bogus delta/scroll
        // spike from the default (0,0) state.
        _currentMouseState = Mouse.GetState();
        _previousMouseState = _currentMouseState;

        foreach (MouseButton button in AllButtons)
        {
            _pressAnchor[button] = Point.Zero;
            _dragging[button] = false;
        }
    }

    public void update(bool focused)
    {
        // Roll first: PositionDelta, ScrollWheelDelta and the button edges stay valid until the next
        // update (TODO #40).
        //
        // Unfocused, the mouse reports **buttons up where the cursor already was** (#60): a click in
        // another window is not an attack, and the cursor crossing the desk while the game is in the
        // background is not a flick of the view. Coming back reads both states, so nothing jumps.
        switch (_focus.Step(focused))
        {
            case FocusStep.Neutral:
                _previousMouseState = _currentMouseState;
                _currentMouseState = new MouseState(
                    _previousMouseState.X, _previousMouseState.Y, _previousMouseState.ScrollWheelValue,
                    ButtonState.Released, ButtonState.Released, ButtonState.Released,
                    ButtonState.Released, ButtonState.Released);
                break;
            case FocusStep.ReadAndResync:
                _currentMouseState = Mouse.GetState();
                _previousMouseState = _currentMouseState;
                break;
            default:
                _previousMouseState = _currentMouseState;
                _currentMouseState = Mouse.GetState();
                break;
        }

        Point delta = PositionDelta;

        foreach (MouseButton mouseBtn in AllButtons)
        {
            if (IsButtonPressed(mouseBtn))
            {
                OnButtonPressed?.Invoke(mouseBtn);
                // Anchor a potential drag at the press position. Re-anchoring here is
                // also what prevents a "snap" when the button is re-pressed elsewhere.
                _pressAnchor[mouseBtn] = Position;
                _dragging[mouseBtn] = false;
            }
            else if (IsButtonReleased(mouseBtn))
            {
                OnButtonReleased?.Invoke(mouseBtn);
                if (_dragging[mouseBtn])
                {
                    _dragging[mouseBtn] = false;
                    OnDragEnd?.Invoke(mouseBtn);
                }
            }

            // Drag lifecycle while the button is held.
            if (IsButtonDown(mouseBtn))
            {
                if (!_dragging[mouseBtn])
                {
                    // Promote to a drag once travel from the anchor passes the threshold.
                    Point offset = Position - _pressAnchor[mouseBtn];
                    if (offset.X * offset.X + offset.Y * offset.Y >= DragThreshold * DragThreshold)
                    {
                        _dragging[mouseBtn] = true;
                        OnDragStart?.Invoke(mouseBtn, _pressAnchor[mouseBtn]);
                        if (delta != Point.Zero)
                            OnDrag?.Invoke(mouseBtn, delta);
                    }
                }
                else if (delta != Point.Zero)
                {
                    OnDrag?.Invoke(mouseBtn, delta);
                }
            }
        }

        if (delta != Point.Zero)
            OnMouseMoved?.Invoke(delta);

        int scrollDelta = ScrollWheelDelta;
        if (scrollDelta != 0)
            OnScroll?.Invoke(scrollDelta);
    }

    // ---- State helpers ----
    public bool IsButtonDown(MouseButton button) =>
        GetButtonState(_currentMouseState, button) == ButtonState.Pressed;

    public bool IsButtonUp(MouseButton button) =>
        GetButtonState(_currentMouseState, button) == ButtonState.Released;

    // True only on the frame the button transitions up -> down.
    public bool IsButtonPressed(MouseButton button) =>
        GetButtonState(_currentMouseState, button) == ButtonState.Pressed &&
        GetButtonState(_previousMouseState, button) == ButtonState.Released;

    // True only on the frame the button transitions down -> up.
    public bool IsButtonReleased(MouseButton button) =>
        GetButtonState(_currentMouseState, button) == ButtonState.Released &&
        GetButtonState(_previousMouseState, button) == ButtonState.Pressed;

    // True once a drag gesture is active (past the threshold) and until release.
    public bool IsDragging(MouseButton button) =>
        _dragging.TryGetValue(button, out bool d) && d;

    // Total displacement from the drag's start position (Point.Zero when not dragging).
    public Point GetDragDelta(MouseButton button) =>
        IsDragging(button) ? Position - _pressAnchor[button] : Point.Zero;

    // Multi-button combo check, e.g. AreButtonsDown(MouseButton.LEFT | MouseButton.RIGHT).
    public bool AreButtonsDown(MouseButton combo) => (PressedButtons & combo) == combo;

    private static ButtonState GetButtonState(MouseState state, MouseButton button)
    {
        switch (button)
        {
            case MouseButton.LEFT:   return state.LeftButton;
            case MouseButton.RIGHT:  return state.RightButton;
            case MouseButton.MIDDLE: return state.MiddleButton;
            default:                 return ButtonState.Released;
        }
    }
}

[Flags]
internal enum MouseButton
{
    LEFT = 1,
    RIGHT = 2,
    MIDDLE = 4
}
