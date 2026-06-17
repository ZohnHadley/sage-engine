using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;


namespace sage_engine;
internal class KeyboardListener
{
    private KeyboardState _currentKeySet;
    private KeyboardState _previousKeySet;

    public KeyboardState CurrentKeySet {get{return _currentKeySet;}}
    public KeyboardState PreviousKetSet {get{return _previousKeySet;}}

    // Events that other classes can subscribe to
    public event Action<Keys> OnKeyPressed;
    public event Action<Keys> OnKeyReleased;

    public void Update(GameTime gameTime)
    {
        // Polling the latest hardware state
        _currentKeySet = Keyboard.GetState();

        // Check all possible Enum keys for a state change
        foreach (Keys key in Enum.GetValues(typeof(Keys)))
        {
            if (IsJustPressed(key))
            {
                OnKeyPressed?.Invoke(key);
            }
            else if (IsJustReleased(key))
            {
                OnKeyReleased?.Invoke(key);
            }
        }

        // Cache state for the next frame comparisons
        _previousKeySet = _currentKeySet;
    }

    private bool IsJustPressed(Keys key) => 
        _currentKeySet.IsKeyDown(key) && _previousKeySet.IsKeyUp(key);

    private bool IsJustReleased(Keys key) => 
        _currentKeySet.IsKeyUp(key) && _previousKeySet.IsKeyDown(key);
}
