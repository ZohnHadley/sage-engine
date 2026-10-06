#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.UI;

// A direction focus can move: the four spatial ones (D-pad, arrow keys, a stick) and tab order.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public enum UiNavigation { None, Up, Down, Left, Right, Next, Previous }

// A press for the focused thing beyond Confirm (issue #346), for the screen's view-model to act on
// (IViewModel.Command): Alternate is the `MenuAlternate` button (Delete, the gamepad's X) — an item
// dropped on the ground — Rotate and Split are `MenuRotate` (R, the left shoulder) and `MenuSplit`
// (F, the left trigger): an item turned on its side, a stack halved.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public enum UiCommand { None, Alternate, Rotate, Split }

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

    // The primary button is held (this frame included): the widget under it draws pressed (#97).
    public bool PointerDown;

    // Confirm is held: the focused widget draws pressed (#97).
    public bool ConfirmHeld;

    // Wheel notches this frame, positive away from the user (scrolls up).
    public float Wheel;

    // Seconds since the last Update (tooltip delay).
    public float DeltaTime;

    // Characters typed this frame, as the window reported them ('\b' backspace, '\r' Enter), for the
    // focused text field (issue #340); null or empty: none.
    public string? Typed;

    // A command pressed this frame (issue #346), for the focused widget — or, while one is being dragged,
    // for the drag: R turns what is in the hand.
    public UiCommand Command;

    public static UiInput Nav(UiNavigation direction) => new() { Navigate = direction };
    public static UiInput Press => new() { Confirm = true };
    public static UiInput Cancel => new() { Back = true };
    public static UiInput Move(Vector2 pointer) => new() { Pointer = pointer, PointerMoved = true };
    public static UiInput Click(Vector2 pointer) => new() { Pointer = pointer, PointerMoved = true, PointerPressed = true };
    public static UiInput Scroll(Vector2 pointer, float notches) => new() { Pointer = pointer, Wheel = notches };
    public static UiInput Wait(float seconds) => new() { DeltaTime = seconds };
    public static UiInput Type(string text) => new() { Typed = text };
    public static UiInput Drag(Vector2 pointer) => new() { Pointer = pointer, PointerMoved = true, PointerDown = true };
    // The primary button goes down (and stays down) on `pointer`: the start of a drag, or of a slow click.
    public static UiInput Hold(Vector2 pointer) => new() { Pointer = pointer, PointerMoved = true, PointerPressed = true, PointerDown = true };
    // The primary button comes up at `pointer` (PointerDown false after a frame it was held).
    public static UiInput Release(Vector2 pointer) => new() { Pointer = pointer, PointerMoved = true };
    public static UiInput Do(UiCommand command) => new() { Command = command };
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

    // The pointer is over a hit-testable widget, or a focus scope shows: a click here is the UI's, not the world's.
    public bool PointerOverUi;

    // The widget whose value the player changed this frame (Widget.ValueChanged), if any: the last one.
    public Widget? Changed;

    // The focus scope that held focus this frame (UiRoot.ActiveScope), or null. Back while one shows is
    // for whatever showed it — a confirm prompt cancelling — so the screen stack does not close the layer.
    public Widget? Scope;

    // The command pressed (UiInput.Command) and the widget it is for: the focused one, or a drag's source.
    public UiCommand Command;
    public Widget? CommandTarget;

    // Drag and drop (issue #346): a drag began this frame, moved, or ended — dropped, or cancelled by Back
    // (Drag.Cancelled) — with Drag saying what, from where and over what.
    public bool DragStarted, DragMoved, Dropped;
    public UiDrag Drag;
}

// A drag in progress, or the one that just ended (UiRoot.Drag, UiResult.Drag; issue #346): what is being
// dragged, the row it stands for, where the pointer went down on it and where it is now (virtual units),
// and what is under the pointer — the widget a drop lands on, or null over nothing of the tree (the
// world: an item let go there is dropped on the ground).
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public readonly struct UiDrag
{
    public UiDrag(Widget source, object? payload, Vector2 start, Vector2 pointer, Widget? target, bool cancelled = false)
    {
        Source = source;
        Payload = payload;
        Start = start;
        Pointer = pointer;
        Target = target;
        Cancelled = cancelled;
    }

    public Widget? Source { get; }

    // The Data of the source or its nearest ancestor that has some: the row it shows (UiScreen.RowOf).
    public object? Payload { get; }
    public Vector2 Start { get; }
    public Vector2 Pointer { get; }
    public Widget? Target { get; }

    // Back was pressed while dragging: nothing is dropped, everything goes back where it was.
    public bool Cancelled { get; }

    public bool IsActive => Source != null;

    // How far the pointer has moved since it went down.
    public Vector2 Offset => Pointer - Start;

    // Where the ghost is drawn: the source's rect, moved with the pointer.
    public Rect Ghost => Source == null ? default : new Rect(Source.Rect.X + Offset.X, Source.Rect.Y + Offset.Y, Source.Rect.Width, Source.Rect.Height);
}
