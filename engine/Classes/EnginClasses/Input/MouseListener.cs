using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace sage_engine;
internal class MouseListener
{
    private MouseState _currentMouseState;
    private MouseState _previousMouseState;

    public event Action <MouseButton> OnMouseButtonPressed;
    public event Action <int> ScrollWheelValue;
    public event Action <int> PositionXValue;
    public event Action <int> PositionYValue;

    public void update(GameTime gameTime)
    {
        _currentMouseState = Mouse.GetState();
        foreach (MouseButton mouseBtn in Enum.GetValues(typeof(MouseButton)))

            if (IsJustPressed(mouseBtn))
            {
                OnMouseButtonPressed?.Invoke(mouseBtn);
            }
            else if (IsJustReleased(mouseBtn))
            {
                OnMouseButtonPressed?.Invoke(mouseBtn);
            }

        _previousMouseState = _currentMouseState;
    }

    private bool IsJustPressed(MouseButton mouseBtn)
    {
        bool value = false;
        if(
        mouseBtn.Equals(MouseButton.LEFT) 
        && _currentMouseState.LeftButton.Equals(ButtonState.Pressed) 
        && _previousMouseState.LeftButton.Equals(ButtonState.Released)
        ||
        mouseBtn.Equals(MouseButton.RIGHT) 
        && _currentMouseState.RightButton.Equals(ButtonState.Pressed) 
        && _previousMouseState.RightButton.Equals(ButtonState.Released)
        ||
        mouseBtn.Equals(MouseButton.MIDDLE) 
        && _currentMouseState.MiddleButton.Equals(ButtonState.Pressed) 
        && _previousMouseState.MiddleButton.Equals(ButtonState.Released)
        )
        {
            value = true;
        }
        return value;
    } 

    private bool IsJustReleased(MouseButton mouseBtn)
    {
        bool value = false;
        if(
        mouseBtn.Equals(MouseButton.LEFT) 
        && _currentMouseState.LeftButton.Equals(ButtonState.Released) 
        && _previousMouseState.LeftButton.Equals(ButtonState.Pressed)
        ||
        mouseBtn.Equals(MouseButton.RIGHT) 
        && _currentMouseState.RightButton.Equals(ButtonState.Released) 
        && _previousMouseState.RightButton.Equals(ButtonState.Pressed)
        ||
        mouseBtn.Equals(MouseButton.MIDDLE) 
        && _currentMouseState.MiddleButton.Equals(ButtonState.Released) 
        && _previousMouseState.MiddleButton.Equals(ButtonState.Pressed)
        )
        {
            value = true;
        }
        return value;
    } 
}

public enum MouseButton
{
    LEFT = 1,
    RIGHT = 2,
    MIDDLE = 4
}