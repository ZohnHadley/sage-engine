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
                      string? windowStyle, string? titleStyle, string? textStyle, string? buttonStyle, Thickness padding,
                      UiStyles? styles)
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
        Title = Window.Add(Styled(new Label(title) { Name = "title", Visible = title.Length > 0 }, titleStyle, styles));
        // A long question wraps rather than widening the window past the screen (#597).
        Message = Window.Add(Styled(new Label(message) { Name = "message", Wrap = true, MaxWidth = 480f }, textStyle, styles));
        var buttons = Window.Add(new Stack { Name = "buttons", Direction = Orientation.Row, Spacing = 8f, HAlign = Align.End });
        ConfirmButton = buttons.Add(Styled(new Button(confirm) { Name = "confirm", MinSize = new Vector2(80f, 0f) }, buttonStyle, styles));
        ConfirmButton.Pressed += _ => Confirm();
        if (cancel != null)
        {
            CancelButton = buttons.Add(Styled(new Button(cancel) { Name = "cancel", MinSize = new Vector2(80f, 0f) }, buttonStyle, styles));
            CancelButton.Pressed += _ => Cancel();
        }
    }

    // A style's letters, as a layout node's label takes them (UiView): no layout builds a dialog.
    private static T Styled<T>(T label, string? style, UiStyles? styles) where T : Label
    {
        label.Style = style;
        if (style == null || styles == null) return label;
        var resolved = styles.Get(style);
        label.Font = resolved.Font;
        label.FontSize = resolved.FontSize;
        label.TextScale = resolved.TextScale;
        return label;
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
