using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;


namespace sage_engine;
public class KeyboardListener
{
    // Cached once: Enum.GetValues allocates a new array (and boxes every value)
    // on each call, so we avoid doing it per-frame.
    private static readonly Keys[] AllKeys = (Keys[])Enum.GetValues(typeof(Keys));

    private KeyboardState _currentKeySet;
    private KeyboardState _previousKeySet;
    private FocusPolicy _focus;

    public KeyboardState CurrentKeySet {get{return _currentKeySet;}}
    public KeyboardState PreviousKeySet {get{return _previousKeySet;}}

    // Events fire on edges only (press / release); use the polling API for
    // continuous "is this key down right now" checks.
    public event Action<Keys>? OnKeyPressed;
    public event Action<Keys>? OnKeyReleased;


    public void Update(bool focused)
    {
        // Roll the state first, so the polling edges (IsKeyPressed/Released) stay valid until the next
        // Update. (They used to roll at the end, which made every polled edge false; TODO #40.)
        //
        // With the window unfocused the state is *empty* rather than whatever the device says (#60):
        // the frame focus is lost still reports the releases, so a held key stops moving the player,
        // and the frame it comes back reads both states from the device, so a key still held is not a
        // fresh press.
        switch (_focus.Step(focused))
        {
            case FocusStep.Neutral:
                _previousKeySet = _currentKeySet;
                _currentKeySet = default;
                break;
            case FocusStep.ReadAndResync:
                _currentKeySet = Keyboard.GetState();
                _previousKeySet = _currentKeySet;
                break;
            default:
                _previousKeySet = _currentKeySet;
                _currentKeySet = Keyboard.GetState();
                break;
        }

        // Check all possible Enum keys for a state change
        foreach (Keys key in AllKeys)
        {
            if (IsKeyPressed(key))
            {
                OnKeyPressed?.Invoke(key);
            }
            else if (IsKeyReleased(key))
            {
                OnKeyReleased?.Invoke(key);
            }
        }
    }

    // ---- Query API (polling) ----

    // Held this frame.
    public bool IsKeyDown(Keys key) => _currentKeySet.IsKeyDown(key);
    public bool IsKeyUp(Keys key) => _currentKeySet.IsKeyUp(key);

    // True only on the frame the key transitions up -> down.
    public bool IsKeyPressed(Keys key) =>
        _currentKeySet.IsKeyDown(key) && _previousKeySet.IsKeyUp(key);

    // True only on the frame the key transitions down -> up.
    public bool IsKeyReleased(Keys key) =>
        _currentKeySet.IsKeyUp(key) && _previousKeySet.IsKeyDown(key);

    // ---- Chord detection (exact match, no subsets) ----
    // A chord matches only when the keys currently down are EXACTLY the given set,
    // so e.g. Ctrl+Z will NOT fire while Ctrl+Shift+Z is held. The trade-off is that
    // any unrelated held key (e.g. a movement key) also blocks the chord.

    // True every frame exactly these keys are held.
    public bool AreKeysDown(params Keys[] keys) => IsExactChord(_currentKeySet, keys);

    // True only on the frame the chord completes (fires once, not every held frame).
    public bool IsChordPressed(params Keys[] keys) =>
        IsExactChord(_currentKeySet, keys) && !IsExactChord(_previousKeySet, keys);

    private static bool IsExactChord(KeyboardState state, Keys[] keys)
    {
        // Exact: the count of pressed keys must equal the chord size (no extras),
        // and every chord key must be down.
        if (state.GetPressedKeys().Length != keys.Length)
            return false;
        foreach (Keys key in keys)
            if (state.IsKeyUp(key))
                return false;
        return true;
    }
}
