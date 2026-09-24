#nullable enable

namespace sage_engine;

// When the game may act on what a device says, and what a press *is* (docs/design/08 §3.1, review #60).
//
// **These are decisions, so they live here** rather than in the client with the devices — the same split
// as `AudioMixer`/`IAudioBackend`. The client owns `Mouse.GetState()`, which decides nothing; what
// counts as a press, and whether the game is even allowed to hear it, is tested headlessly below.
//
// All three rules exist because of one bug: the player swung with nobody pressing anything (#60). A
// press nobody made can arrive three ways — from a window that does not have focus, from a button the
// UI already swallowed, and from an analogue trigger resting on its own threshold.

// What to do with a device this frame, given whether the window has focus.
public enum FocusStep
{
    Read,             // normal: roll the previous state forward and read the device
    Neutral,          // the window is not ours: report everything up, and let this frame's release land
    ReadAndResync,    // focus has just come back: read, and forget what was held before
}

// The seam either side of losing focus, which is where the awkward cases are:
//
// - **On the frame focus is lost**, the previous state stays real and the new one is neutral, so a held
//   key *releases* once. Without that, a player who alt-tabs mid-stride keeps walking for ever.
// - **While unfocused**, both are neutral: no held, no edges, no cursor delta. A click in somebody's
//   browser is not an attack, which is exactly what #60 looked like.
// - **On the frame focus returns**, both are read from the device, so a button that was already down is
//   *not* a new press, and a cursor that moved across the desk is not a flick of the mouse.
public struct FocusPolicy
{
    private bool _started;
    private bool _wasFocused;

    public bool Focused { get; private set; }

    public FocusStep Step(bool focused)
    {
        // The first call is a resync whatever it says: there is no previous frame to compare with, so a
        // key already down as the game starts must not read as pressed on frame one.
        bool first = !_started;
        _started = true;
        bool regained = focused && (!_wasFocused || first);
        _wasFocused = focused;
        Focused = focused;
        return !focused ? FocusStep.Neutral : regained ? FocusStep.ReadAndResync : FocusStep.Read;
    }
}

// What a button action reports, from the raw device state and whether something above it swallowed the
// binding this frame (a screen, the console, the editor).
//
// **Edges come from the raw state, not the swallowed one.** The filtered state was the bug: an action
// consumed for one frame looked un-held, so the next frame it looked *newly pressed* — close an
// inventory with the attack button still down and the character swung at nothing.
public static class InputEdges
{
    public static bool Held(bool rawHeld, bool consumed) => rawHeld && !consumed;

    public static bool Pressed(bool rawHeld, bool wasRawHeld, bool consumed) =>
        rawHeld && !wasRawHeld && !consumed;

    // A release still lands while consumed: whatever started the press has to hear that it ended, or a
    // held action that is swallowed mid-way is never told to stop.
    public static bool Released(bool rawHeld, bool wasRawHeld) => !rawHeld && wasRawHeld;

    // An analogue trigger used as a button, with hysteresis. A trigger resting near a single threshold
    // chatters, and a chattering button is a press every frame it crosses back — which for a sword is a
    // swing every time its cooldown ends. Press high, release low, and a resting trigger stays put.
    public const float TriggerPress = 0.6f;
    public const float TriggerRelease = 0.4f;

    public static bool TriggerDown(float value, bool wasDown) =>
        value >= (wasDown ? TriggerRelease : TriggerPress);
}
