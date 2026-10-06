#nullable enable

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Whether the cursor is held, and that taking it back does not turn the view (docs/design/08, issue #334).
// The listener and the host only apply these.
public class MouseCaptureTests
{
    private static MouseCaptureInputs Playing => new()
    {
        PawnPossessed = true, WindowFocused = true, Enabled = true,
    };

    [Xunit.Fact]
    public void ThePossessedPlayerHoldsTheCursor()
    {
        Assert.True(MouseCapturePolicy.ShouldCapture(Playing));
        Assert.Null(MouseCapturePolicy.Why(Playing));
        // No pawn, nothing to look with.
        Assert.Equal("no possessed pawn", MouseCapturePolicy.Why(Playing with { PawnPossessed = false }));
    }

    [Xunit.Fact]
    public void EachThingThatWantsAPointerReleasesIt()
    {
        Assert.Equal("screen open", MouseCapturePolicy.Why(Playing with { ScreenOpen = true }));
        Assert.Equal("console open", MouseCapturePolicy.Why(Playing with { ConsoleOpen = true }));
        Assert.Equal("controls screen capturing", MouseCapturePolicy.Why(Playing with { ControlsCapturing = true }));
        Assert.Equal("developer UI has the pointer", MouseCapturePolicy.Why(Playing with { UiWantsMouse = true }));
        Assert.Equal("editor viewport", MouseCapturePolicy.Why(Playing with { EditorViewport = true }));
        Assert.Equal("window unfocused", MouseCapturePolicy.Why(Playing with { WindowFocused = false }));
        Assert.Equal("m_capture is 0", MouseCapturePolicy.Why(Playing with { Enabled = false }));
        Assert.False(MouseCapturePolicy.ShouldCapture(Playing with { ScreenOpen = true, ConsoleOpen = true }));
    }

    // Opening a screen frees the cursor; closing it takes it back with the cursor wherever the player left
    // it (a button in the corner), and the first frame must not report that distance as a flick.
    [Xunit.Fact]
    public void RecapturingAfterAScreenDoesNotJumpTheLook()
    {
        var mouse = new MouseCaptureTracker();
        const int cx = 400, cy = 200;

        var f = mouse.Step(true, cx, cy, cx, cy);                       // playing: taken, no delta
        Assert.True(f.Captured && f.Changed && f.Warp);
        Assert.Equal((0, 0), (f.DeltaX, f.DeltaY));

        f = mouse.Step(true, cx + 7, cy - 3, cx, cy);                    // looking: the delta is from the middle
        Assert.True(f.Captured && !f.Changed && f.Warp);
        Assert.Equal((7, -3), (f.DeltaX, f.DeltaY));

        f = mouse.Step(false, cx, cy, cx, cy);                           // a screen opens
        Assert.False(f.Captured);
        Assert.True(f.Changed);
        Assert.False(f.Warp);
        Assert.Equal((0, 0), (f.DeltaX, f.DeltaY));

        f = mouse.Step(false, 780, 20, cx, cy);                          // the player moves to a corner button
        Assert.False(f.Warp);
        Assert.Equal((380, -180), (f.DeltaX, f.DeltaY));                 // free: the plain travel

        f = mouse.Step(true, 780, 20, cx, cy);                           // the screen closes, cursor in the corner
        Assert.True(f.Captured && f.Changed && f.Warp);
        Assert.Equal((0, 0), (f.DeltaX, f.DeltaY));                      // no look jump

        f = mouse.Step(true, cx + 2, cy + 1, cx, cy);                    // and the next frame is from the middle
        Assert.Equal((2, 1), (f.DeltaX, f.DeltaY));
    }

    [Xunit.Fact]
    public void RegainingFocusDoesNotJumpTheLook()
    {
        var mouse = new MouseCaptureTracker();
        const int cx = 400, cy = 200;
        mouse.Step(true, cx, cy, cx, cy);
        mouse.Step(true, cx + 1, cy, cx, cy);

        // Alt-tab: the policy says no (window unfocused), the cursor wanders across the desk.
        var lost = mouse.Step(false, cx, cy, cx, cy);
        Assert.True(lost.Changed);
        mouse.Step(false, 1500, 900, cx, cy);

        var back = mouse.Step(true, 1500, 900, cx, cy);
        Assert.True(back.Captured && back.Warp);
        Assert.Equal((0, 0), (back.DeltaX, back.DeltaY));
        Assert.Equal((5, 0), (mouse.Step(true, cx + 5, cy, cx, cy).DeltaX, 0));
    }

    // The window resizing moves the middle: the distance from the old one is not a look either.
    [Xunit.Fact]
    public void AMovedMiddleDropsThatFramesDelta()
    {
        var mouse = new MouseCaptureTracker();
        mouse.Step(true, 400, 200, 400, 200);
        var f = mouse.Step(true, 400, 200, 500, 300);
        Assert.True(f.Captured && f.Warp);
        Assert.Equal((0, 0), (f.DeltaX, f.DeltaY));
        Assert.Equal((3, 4), (mouse.Step(true, 503, 304, 500, 300).DeltaX, 4));
    }

    // The sequence a player's screen goes through, from the policy to the delta: the model end to end.
    [Xunit.Fact]
    public void ScreenOpenThenClosedGoesThroughThePolicy()
    {
        var mouse = new MouseCaptureTracker();
        var inputs = Playing;
        var (cx, cy) = (320, 240);

        Assert.True(mouse.Step(MouseCapturePolicy.ShouldCapture(inputs), cx, cy, cx, cy).Captured);
        inputs = inputs with { ScreenOpen = true };
        Assert.False(mouse.Step(MouseCapturePolicy.ShouldCapture(inputs), 10, 10, cx, cy).Captured);
        inputs = inputs with { ScreenOpen = false };
        var f = mouse.Step(MouseCapturePolicy.ShouldCapture(inputs), 10, 10, cx, cy);
        Assert.True(f.Captured);
        Assert.Equal((0, 0), (f.DeltaX, f.DeltaY));
    }
}
