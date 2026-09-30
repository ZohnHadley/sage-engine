#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.UI;

// A direction focus can move: the four spatial ones (D-pad, arrow keys, a stick) and tab order.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public enum UiNavigation { None, Up, Down, Left, Right, Next, Previous }

// One frame of UI input (UiRoot.Update). The client fills it from the `ui` input context's actions and
// the mouse (#97); a test fills it by hand. It is what the widgets know of devices: nothing reads a key.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public struct UiInput
{
    // Focus movement pressed this frame.
    public UiNavigation Navigate;

    // Activate the focused widget (Enter, the gamepad's A).
    public bool Confirm;

    // Leave (Escape, the gamepad's B): reported in UiResult.Back for the screen to act on.
    public bool Back;

    // Where the pointer is, in viewport pixels.
    public Vector2 Pointer;

    // The pointer moved this frame: hover follows it, and it moves focus onto what it is over.
    public bool PointerMoved;

    // The primary button went down this frame: focus and activate what is under the pointer.
    public bool PointerPressed;

    // Wheel notches this frame, positive away from the user (scrolls up).
    public float Wheel;

    // Seconds since the last Update (tooltip delay).
    public float DeltaTime;

    public static UiInput Nav(UiNavigation direction) => new() { Navigate = direction };
    public static UiInput Press => new() { Confirm = true };
    public static UiInput Cancel => new() { Back = true };
    public static UiInput Move(Vector2 pointer) => new() { Pointer = pointer, PointerMoved = true };
    public static UiInput Click(Vector2 pointer) => new() { Pointer = pointer, PointerMoved = true, PointerPressed = true };
    public static UiInput Scroll(Vector2 pointer, float notches) => new() { Pointer = pointer, Wheel = notches };
    public static UiInput Wait(float seconds) => new() { DeltaTime = seconds };
}

// What one Update did, for the screen that owns the tree.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public struct UiResult
{
    // The widget confirmed or clicked this frame, if any (its Pressed/ItemActivated have already run).
    public Widget? Activated;

    // Back was pressed.
    public bool Back;

    // Focus moved to another widget (or to none).
    public bool FocusChanged;

    // The pointer is over a hit-testable widget: a click here is the UI's, not the world's.
    public bool PointerOverUi;
}
