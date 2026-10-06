#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.UI;

// How a dialog was answered: still open, its confirm button, or cancelled (its cancel button, Back).
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public enum UiDialogResult { Open, Confirmed, Cancelled }

// A confirm prompt or a message box (issue #343): UiScreenStack.Confirm and Message put one on top of
// whatever is open — overwrite this save? quit without saving? — as a modal layer whose window is a
// focus scope, so neither the D-pad, Tab nor the pointer reaches the menu under it. Its confirm button
// answers yes; its cancel button or Back answers no; a press beside it does nothing. Answered, it closes
// (fading as any layer does) and the layer under it has the input again, its focus where it was.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class UiDialog
{
    private readonly UiScreenStack _stack;
    private readonly Action<bool>? _answered;

    internal UiDialog(UiScreenStack stack, Action<bool>? answered, string title, string message, string confirm, string? cancel,
                      string? windowStyle, string? buttonStyle, Thickness padding)
    {
        _stack = stack;
        _answered = answered;
        Window = new Stack
        {
            Name = "dialog",
            Anchors = Anchors.Center,
            Spacing = 8f,
            Padding = padding,
            MinSize = new Vector2(320f, 0f),
            Style = windowStyle,
            FocusScope = true,
            HitTestable = true,   // a press on the window's empty part is on the window
        };
        Title = Window.Add(new Label(title) { Name = "title", Visible = title.Length > 0 });
        Message = Window.Add(new Label(message) { Name = "message" });
        var buttons = Window.Add(new Stack { Name = "buttons", Direction = Orientation.Row, Spacing = 8f, HAlign = Align.End });
        ConfirmButton = buttons.Add(new Button(confirm) { Name = "confirm", Style = buttonStyle, MinSize = new Vector2(80f, 0f) });
        ConfirmButton.Pressed += _ => Confirm();
        if (cancel != null)
        {
            CancelButton = buttons.Add(new Button(cancel) { Name = "cancel", Style = buttonStyle, MinSize = new Vector2(80f, 0f) });
            CancelButton.Pressed += _ => Cancel();
        }
    }

    // The layer it is shown in (UiScreenStack.Layers).
    public UiLayer Layer { get; internal set; } = null!;

    // The window, a FocusScope: the title, the message and the row of buttons.
    public Stack Window { get; }
    public Label Title { get; }
    public Label Message { get; }
    public Button ConfirmButton { get; }

    // None on a message box.
    public Button? CancelButton { get; }

    public UiDialogResult Result { get; private set; }
    public bool IsOpen => Result == UiDialogResult.Open;

    // Answers yes, as its confirm button does: closes it and calls back with true.
    public void Confirm() => Answer(UiDialogResult.Confirmed);

    // Answers no, as its cancel button and Back do (a message box: dismissed): closes it and calls back with false.
    public void Cancel() => Answer(UiDialogResult.Cancelled);

    private void Answer(UiDialogResult result)
    {
        if (!IsOpen) return;
        Result = result;
        _stack.Close(Layer);
        _answered?.Invoke(result == UiDialogResult.Confirmed);
    }
}
