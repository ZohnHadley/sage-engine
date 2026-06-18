using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace sage_engine;

internal class InputSystem : IEnginSystem
{
    private static InputSystem instance;
    private KeyboardListener _keyboardListener;
    private MouseListener _mouseListener;

    private bool isDebug = false;
    private Dictionary<String, Keys> key_binds = new Dictionary<string, Keys> {
        ["toggle_debug"] = Keys.OemTilde
    };

    // Central input events that consumers (e.g. DevCamera) subscribe to,
    // forwarded from the single shared listeners.
    public event Action<Keys> OnKeyPressed
    {
        add { _keyboardListener.OnKeyPressed += value; }
        remove { _keyboardListener.OnKeyPressed -= value; }
    }
    public event Action<Keys> OnKeyReleased
    {
        add { _keyboardListener.OnKeyReleased += value; }
        remove { _keyboardListener.OnKeyReleased -= value; }
    }

    // Mouse events, forwarded from the single shared MouseListener.
    public event Action<MouseButton> OnMouseButtonPressed
    {
        add { _mouseListener.OnButtonPressed += value; }
        remove { _mouseListener.OnButtonPressed -= value; }
    }
    public event Action<MouseButton> OnMouseButtonReleased
    {
        add { _mouseListener.OnButtonReleased += value; }
        remove { _mouseListener.OnButtonReleased -= value; }
    }
    public event Action<Point> OnMouseMoved
    {
        add { _mouseListener.OnMouseMoved += value; }
        remove { _mouseListener.OnMouseMoved -= value; }
    }
    public event Action<int> OnMouseScroll
    {
        add { _mouseListener.OnScroll += value; }
        remove { _mouseListener.OnScroll -= value; }
    }
    public event Action<MouseButton, Point> OnMouseDragStart
    {
        add { _mouseListener.OnDragStart += value; }
        remove { _mouseListener.OnDragStart -= value; }
    }
    public event Action<MouseButton, Point> OnMouseDrag
    {
        add { _mouseListener.OnDrag += value; }
        remove { _mouseListener.OnDrag -= value; }
    }
    public event Action<MouseButton> OnMouseDragEnd
    {
        add { _mouseListener.OnDragEnd += value; }
        remove { _mouseListener.OnDragEnd -= value; }
    }

    // ---- Keyboard query facade (polling API) ----
    public bool IsKeyDown(Keys key) => _keyboardListener.IsKeyDown(key);
    public bool IsKeyUp(Keys key) => _keyboardListener.IsKeyUp(key);
    public bool IsKeyPressed(Keys key) => _keyboardListener.IsKeyPressed(key);
    public bool IsKeyReleased(Keys key) => _keyboardListener.IsKeyReleased(key);
    public bool AreKeysDown(params Keys[] keys) => _keyboardListener.AreKeysDown(keys);
    public bool IsChordPressed(params Keys[] keys) => _keyboardListener.IsChordPressed(keys);

    // ---- Mouse query facade (polling API) ----
    public Point MousePosition => _mouseListener.Position;
    public Point MousePositionDelta => _mouseListener.PositionDelta;
    public bool IsMouseMoving => _mouseListener.IsMoving;
    public Vector2 MouseMoveDirection => _mouseListener.MoveDirection;
    public int MouseScrollDelta => _mouseListener.ScrollWheelDelta;
    public MouseButton PressedMouseButtons => _mouseListener.PressedButtons;
    public bool IsMouseButtonDown(MouseButton button) => _mouseListener.IsButtonDown(button);
    public bool IsMouseDragging(MouseButton button) => _mouseListener.IsDragging(button);
    public Point GetMouseDragDelta(MouseButton button) => _mouseListener.GetDragDelta(button);
    public bool AreMouseButtonsDown(MouseButton combo) => _mouseListener.AreButtonsDown(combo);
    public float MouseDragThreshold
    {
        get => _mouseListener.DragThreshold;
        set => _mouseListener.DragThreshold = value;
    }

    private InputSystem()
    {
        _keyboardListener = new KeyboardListener();
        _keyboardListener.OnKeyPressed += HandleKeyPressed;
        // _keyboardListener.OnKeyReleased += HandleKeyReleased;
        _mouseListener = new MouseListener();
        // _mouseListener.OnButtonPressed += HandledMouseButtonPressed;
    }

    public static InputSystem getInstance()
    {
        if(instance == null)
        {
            instance = new InputSystem();
        }
        return instance;
    }

    private void HandledMouseButtonPressed(MouseButton mousebtn)
    {
        Console.WriteLine(mousebtn);
    }

    private void HandleKeyPressed(Keys key)
    {
        if (key_binds["toggle_debug"].Equals(key))
        {
            isDebug = !isDebug;
            Console.WriteLine(isDebug);
        }
    }

    private void HandleKeyReleased(Keys key)
    {
        // Console.WriteLine(key + " UP");
    }

    public void update(GameTime deltaTime)
    {
        _mouseListener.update(deltaTime);
        _keyboardListener.Update(deltaTime);
    }
}