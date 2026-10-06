#nullable enable

namespace Sage.Simulation;

// Whether the game holds the cursor, and what the first frame of holding it must not do (docs/design/08,
// issue #334).
//
// **Decisions, so they live here** beside `FocusPolicy`: `MouseListener` and the host only apply what this
// says (hide the cursor, warp it to the middle, read the delta from there). `Sage.Client` cannot be tested
// headlessly, this can.

// Everything the decision reads, gathered by the host once a frame.
public readonly record struct MouseCaptureInputs
{
    // The player's world has a pawn the player controls: nothing to look with otherwise (a menu, a cutscene
    // with no player, a game without one).
    public bool PawnPossessed { get; init; }

    // Any screen is open (classic or widget stack): it wants the pointer.
    public bool ScreenOpen { get; init; }

    public bool ConsoleOpen { get; init; }

    // The controls screen is waiting for a key or button (`InputRebinds.Capturing`): the pointer is needed to
    // cancel it, and a right click is a candidate binding.
    public bool ControlsCapturing { get; init; }

    // The developer UI (ImGui) has the pointer.
    public bool UiWantsMouse { get; init; }

    // The editor owns the view (`-edit`, not playing): its free camera is dragged with a visible cursor over
    // a viewport the window docks.
    public bool EditorViewport { get; init; }

    public bool WindowFocused { get; init; }

    // `m_capture 0`: the player (or a developer who needs the cursor) turned it off.
    public bool Enabled { get; init; }
}

public static class MouseCapturePolicy
{
    // Should the cursor be held (hidden, centred, read as a relative delta) this frame?
    public static bool ShouldCapture(in MouseCaptureInputs i) => Why(i) == null;

    // The first reason the cursor is free, or null when it is held. The order is the order a person would
    // answer "why is my mouse loose?": a window that is not ours first, then what is on top of the game.
    public static string? Why(in MouseCaptureInputs i)
    {
        if (!i.Enabled) return "m_capture is 0";
        if (!i.WindowFocused) return "window unfocused";
        if (i.EditorViewport) return "editor viewport";
        if (i.ConsoleOpen) return "console open";
        if (i.ControlsCapturing) return "controls screen capturing";
        if (i.ScreenOpen) return "screen open";
        if (i.UiWantsMouse) return "developer UI has the pointer";
        if (!i.PawnPossessed) return "no possessed pawn";
        return null;
    }
}

// What the applier does with the mouse this frame.
public readonly record struct MouseCaptureFrame(bool Captured, bool Changed, bool Warp, int DeltaX, int DeltaY);

// The model of a held cursor's delta, and of the seams where it would jump (#334).
//
// Held, the cursor is warped to the middle of the window every frame and the delta is how far it had got from
// there, so the view turns without the cursor meeting a screen edge. The seams:
//
// - **The frame the cursor is taken** (a screen closed, the console closed, focus back): it is wherever the
//   player left it, perhaps on a button at the far corner. Warp to the middle *and report no delta* — the
//   distance to the middle is not a flick of the view.
// - **While free** nothing is warped and the delta is the plain cursor travel (a screen reads positions).
// - **The window moved or resized**, so the middle is somewhere else: that frame's delta is dropped too.
public struct MouseCaptureTracker
{
    private bool _held;
    private int _anchorX, _anchorY;   // where the cursor was warped to last frame
    private int _lastX, _lastY;       // free: where it was last frame
    private bool _haveLast;

    public bool Captured => _held;

    // `want` is MouseCapturePolicy.ShouldCapture; (x, y) the cursor now and (centreX, centreY) the middle of
    // the window's client area, both in window pixels.
    public MouseCaptureFrame Step(bool want, int x, int y, int centreX, int centreY)
    {
        if (!want)
        {
            bool released = _held;
            _held = false;
            int dx = _haveLast ? x - _lastX : 0, dy = _haveLast ? y - _lastY : 0;
            _lastX = x; _lastY = y; _haveLast = true;
            // The frame the cursor is let go the delta is dropped as well: the last held frame warped it to
            // the middle, so the move from there is the cursor reappearing, not travel.
            return new MouseCaptureFrame(false, released, false, released ? 0 : dx, released ? 0 : dy);
        }

        bool taken = !_held;
        _held = true;
        int deltaX = 0, deltaY = 0;
        if (!taken && _anchorX == centreX && _anchorY == centreY)
        {
            deltaX = x - _anchorX;
            deltaY = y - _anchorY;
        }
        _anchorX = centreX; _anchorY = centreY;
        _lastX = centreX; _lastY = centreY; _haveLast = true;
        return new MouseCaptureFrame(true, taken, true, deltaX, deltaY);
    }
}
