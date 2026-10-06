#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.UI;

// A value easing from one number to another over some seconds of *frame* time (docs/design/13 "As
// built (drawing)", issue #97): a screen fading in, a window sliding up, a focus highlight coming on.
// 4b's Easing gives the curve. A struct advanced by whoever owns it — the UI's tweens run on the frame
// the player sees, never on simulation ticks, and never touch simulation state.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public struct UiTween
{
    public UiTween(float from, float to, float seconds, Ease ease)
    {
        From = from;
        To = to;
        Seconds = MathF.Max(seconds, 0f);
        Ease = ease;
        Elapsed = 0f;
    }

    public float From;
    public float To;
    public float Seconds;
    public Ease Ease;
    public float Elapsed;

    // A tween that is already where it is going.
    public static UiTween At(float value) => new(value, value, 0f, Ease.Linear);

    public readonly bool Done => Elapsed >= Seconds;

    // 0..1 of the time; 1 once done (and at once for a zero-length tween).
    public readonly float Progress => Seconds > 0f ? Math.Clamp(Elapsed / Seconds, 0f, 1f) : 1f;

    public readonly float Value => From + (To - From) * Easing.Apply(Ease, Progress);

    // Moves it on by `seconds` (negative counts as none) and returns where it is.
    public float Advance(float seconds)
    {
        if (seconds > 0f) Elapsed = MathF.Min(Elapsed + seconds, Seconds);
        return Value;
    }
}

// One tree on screen: a widget screen (a `screen` record, opened) or a tree a game built, with its own
// UiRoot — so its own focus, hover and tooltip — and what the renderer needs beyond the widgets: how
// far it has faded and slid in, the focus highlight's easing, and which widget is held down.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class UiLayer
{
    private readonly Widget _content;
    private UiTween _show, _focus = UiTween.At(1f);
    private Widget? _lastFocus;
    private int _builds;

    internal UiLayer(UiRoot root, Widget content, UiScreen? screen, bool modal)
    {
        Root = root;
        _content = content;
        Screen = screen;
        Modal = modal;
        _builds = screen?.Builds ?? 0;
    }

    public UiRoot Root { get; }

    // The tree shown: the screen's (which a reload may have replaced) or the one pushed.
    public Widget Content => Screen?.Root ?? _content;

    // The screen record this shows, when it was opened from one.
    public UiScreen? Screen { get; }

    // The confirm prompt or message box this shows (UiScreenStack.Confirm, Message), when it is one.
    public UiDialog? Dialog { get; internal set; }

    // Its screen `pauses` the world, and it still counts (UiScreenStack.PausesWorld): until it starts closing.
    internal bool HoldsPause { get; set; }

    // Takes the UI's input while it is on top, holds the `ui` input context and dims what is behind it.
    // A HUD layer is not modal: it is drawn, and never takes a key.
    public bool Modal { get; }

    public bool CloseOnBack { get; set; } = true;

    // A press that lands on nothing of this layer closes it, as most inventories do.
    public bool CloseOnClickOutside { get; set; } = true;

    // What is drawn over everything below a modal layer (packed colour; 0: nothing), faded with it.
    public uint Backdrop { get; set; }

    public bool IsClosing { get; private set; }
    public bool IsClosed { get; private set; }

    // The open/close transition: 0..1 of the way in, and how far below its place it is, in virtual units.
    public float Opacity { get; private set; }
    public float Offset { get; private set; }

    // The focus highlight: 0 as focus arrives on a widget, 1 once it has fully moved from PreviousFocus.
    public float FocusBlend => _focus.Value;
    public Widget? PreviousFocus { get; private set; }

    // What draws pressed this frame: the focused widget while Confirm is held, or while the pointer is
    // held on it (the UI itself tracks no held button, UiStyles.StateOf).
    public Widget? Pressed { get; private set; }

    // A widget in this layer was confirmed or clicked (after its own Pressed/ItemActivated).
    public event Action<UiLayer, Widget>? Activated;

    // The drawing, cached against the root's Version (the client's WidgetRenderer brings it up to date).
    internal UiRenderPlan Plan { get; } = new();

    internal void Opened(UiScreenStack stack)
    {
        _show = new UiTween(0f, 1f, stack.OpenSeconds, stack.OpenEase);
        Show(stack);
        _lastFocus = Root.Focused;   // focus it opened with arrives with the fade, not a highlight of its own
    }

    internal void StartClosing(UiScreenStack stack)
    {
        if (IsClosing) return;
        IsClosing = true;
        // From wherever the opening had got to, so closing half-open takes half as long.
        float from = _show.Value;
        _show = new UiTween(from, 0f, stack.CloseSeconds * from, stack.CloseEase);
        Pressed = null;
    }

    internal void Advance(UiScreenStack stack, float dt)
    {
        _show.Advance(dt);
        Show(stack);
        if (IsClosing && _show.Done) IsClosed = true;
        _focus.Advance(dt);
    }

    private void Show(UiScreenStack stack)
    {
        Opacity = _show.Value;
        Offset = (1f - Opacity) * stack.SlideDistance;
    }

    // Whether the screen was built again since last asked (a reload): the new tree needs looking over.
    internal bool Rebuilt()
    {
        if (Screen == null || Screen.Builds == _builds) return false;
        _builds = Screen.Builds;
        return true;
    }

    internal void Track(UiScreenStack stack, in UiInput input, bool top)
    {
        var focused = Root.Focused;
        if (focused != _lastFocus)
        {
            PreviousFocus = _lastFocus;
            _lastFocus = focused;
            _focus = new UiTween(0f, 1f, stack.FocusSeconds, stack.FocusEase);
        }
        Pressed = !top || focused == null ? null
            : input.ConfirmHeld || input.PointerDown && focused.Contains(Root.Hovered) ? focused
            : null;
    }

    internal void RaiseActivated(Widget widget) => Activated?.Invoke(this, widget);
}

// The widget screens a world has open (docs/design/13 "As built (drawing)", issue #97): a stack of
// layers, the top modal one taking input. Headless — it is fed a UiInput and a frame's seconds and says
// what happened — so a test opens a screen, presses Back and watches it fade out and close. The client's
// ScreenSystem fills the UiInput from the `ui` input context and the mouse, hosts this beside the panel
// screens (Screen, ScreenStack) and draws each layer; UiModule adds one to every world's resources.
//
// What it decides, so no screen has to: Back closes the top layer, a press on nothing of it closes it
// (click-outside), a screen opens with its first focusable widget focused (a gamepad has no pointer to
// start from), and opening and closing fade and slide on frame time.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class UiScreenStack
{
    private readonly UiScreens? _screens;
    private readonly UiStyles? _styles;
    private readonly World? _world;   // where the screen sounds are raised (UiSounds); none for a stack of tests and tools
    private readonly List<UiLayer> _layers = new();
    private readonly List<ActionId> _openers = new();
    private readonly Dictionary<int, RecordId> _byAction = new();
    private ITextMeasure _text = FontCells;
    private Vector2 _viewport = UiRoot.DefaultDesignSize;
    private float _uiScale = 1f, _textScale = 1f;

    // The engine font's cell (BitmapFont: a 5×7 glyph with a pixel of air each way), in font pixels:
    // what the client measures with too, so a layout is the same headless and on screen.
    internal static readonly MonospaceTextMeasure FontCells = new(6f, 9f);

    internal UiScreenStack(UiScreens? screens, UiStyles? styles, World? world = null, UiFonts? fonts = null)
    {
        _screens = screens;
        _styles = styles;
        _world = world;
        Fonts = fonts;
    }

    // The fonts a style names (#338), for every layer's layout and for the client that draws them;
    // none for a stack of tests and tools that set no fonts (every label in the engine font).
    public UiFonts? Fonts { get; }

    // A stack with no records behind it: Push only (tests, tools).
    public UiScreenStack() : this(null, null) { }

    // How every layer's text is measured (the client sets its font's).
    public ITextMeasure Text
    {
        get => _text;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_text, value)) return;
            _text = value;
            foreach (var layer in _layers) layer.Root.Text = value;
        }
    }

    public Vector2 Viewport => _viewport;

    // The player's UI and text scale (UiRoot.UiScale, UiRoot.TextScale), for every layer, open and to
    // come; UiModule keeps them at the `ui_scale` and `ui_text_scale` cvars (issue #351).
    public float UiScale
    {
        get => _uiScale;
        set
        {
            _uiScale = float.IsFinite(value) ? Math.Clamp(value, UiRoot.MinUiScale, UiRoot.MaxUiScale) : 1f;
            foreach (var layer in _layers) layer.Root.UiScale = _uiScale;
        }
    }

    public float TextScale
    {
        get => _textScale;
        set
        {
            _textScale = float.IsFinite(value) ? Math.Clamp(value, UiRoot.MinUiScale, UiRoot.MaxUiScale) : 1f;
            foreach (var layer in _layers) layer.Root.TextScale = _textScale;
        }
    }

    // Bottom to top, closing layers included until they have faded.
    public IReadOnlyList<UiLayer> Layers => _layers;

    // A modal layer is open (and not on its way out): the UI has the input.
    public bool IsOpen => Top != null;

    // The topmost modal layer that is not closing: the one input goes to.
    public UiLayer? Top
    {
        get
        {
            for (int i = _layers.Count - 1; i >= 0; i--)
                if (_layers[i].Modal && !_layers[i].IsClosing) return _layers[i];
            return null;
        }
    }

    // Transitions, in seconds of frame time; 0 is instant.
    public float OpenSeconds { get; set; } = 0.18f;
    public float CloseSeconds { get; set; } = 0.12f;
    public Ease OpenEase { get; set; } = Ease.CubicOut;
    public Ease CloseEase { get; set; } = Ease.QuadIn;

    // How far below its place a layer starts (and leaves to), in virtual units.
    public float SlideDistance { get; set; } = 24f;

    public float FocusSeconds { get; set; } = 0.12f;
    public Ease FocusEase { get; set; } = Ease.QuadOut;

    // What a new modal layer draws behind itself (packed; 0: nothing).
    public uint Backdrop { get; set; } = ColourJsonConverter.Pack(0, 0, 0, 150);

    // The style id new layers' tooltips are drawn in.
    public string? TooltipStyle { get; set; }

    // The style ids a confirm prompt's or message box's window and buttons are drawn in (Confirm, Message).
    public string? DialogStyle { get; set; }
    public string? DialogButtonStyle { get; set; }

    // The subtitles shown over every layer (issue #351): the world's, set by UiModule; null shows none.
    // Their layer is made when the first line comes and stays on top of whatever opens after it.
    public Subtitles? Subtitles { get; set; }

    // The style ids subtitle lines and captions are drawn in (sage:subtitles, engine content).
    public string? SubtitleStyle { get; set; } = "sage:subtitles";
    public string? CaptionStyle { get; set; } = "sage:captions";

    // The layer subtitles show in, once there has been one; never modal, never Top.
    public UiLayer? SubtitleLayer => _subtitleLayer;

    private UiLayer? _subtitleLayer;
    private SubtitleBox? _subtitleBox;

    // Something in any layer was confirmed or clicked.
    public event Action<UiLayer, Widget>? Activated;

    private int _pausing;        // open layers whose screen `pauses` (issue #342)
    private bool _pausedWorld;   // and the world was running when the first opened: this stack paused it

    // A screen that `pauses` is open (a pause menu, issue #342): the world's time stands still until the
    // last of them starts closing, and runs again then — unless it was already paused when the first one
    // opened (the `pause` command, the title), which is left as it was.
    public bool PausesWorld => _pausing > 0;

    // This stack paused the world itself (PausesWorld, on a world that was running), and will let it run.
    // A save taken from a pause menu unpauses the world around it, or the save would load paused.
    public bool HoldsPause => _pausedWorld;

    private void Hold(UiLayer layer)
    {
        layer.HoldsPause = true;
        if (_pausing++ == 0 && _world != null && !_world.Paused)
        {
            _world.Paused = true;
            _pausedWorld = true;
        }
    }

    private void Release(UiLayer layer)
    {
        if (!layer.HoldsPause) return;
        layer.HoldsPause = false;
        if (--_pausing > 0 || !_pausedWorld) return;
        _pausedWorld = false;
        if (_world != null) _world.Paused = false;
    }

    public void SetViewport(Vector2 pixels)
    {
        if (pixels.X < 1f || pixels.Y < 1f || pixels == _viewport) return;
        _viewport = pixels;
        foreach (var layer in _layers) layer.Root.SetViewport(pixels);
    }

    // Opens a screen record (UiScreens.OpenScreen) as a modal layer on top.
    public UiLayer Open(RecordId screen, UiBindContext context = default)
    {
        if (_screens == null) throw new InvalidOperationException("this stack has no screens to open (UiModule's has)");
        var opened = _screens.OpenScreen(screen, context);
        return Add(opened.Root, opened, modal: true);
    }

    // Opens a screen record as a layer that is drawn and never takes input — a HUD (issue #99). It sits
    // where it is opened in the stack, so a HUD opened first is under every window; its view-model is
    // read every frame like any open screen's, and Back or a click never closes it.
    public UiLayer OpenHud(RecordId screen, UiBindContext context = default)
    {
        if (_screens == null) throw new InvalidOperationException("this stack has no screens to open (UiModule's has)");
        var opened = _screens.OpenScreen(screen, context);
        return Add(opened.Root, opened, modal: false);
    }

    // Shows a tree a game built: modal (a window) or not (a HUD).
    public UiLayer Push(Widget content, bool modal = true)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Add(content, null, modal);
    }

    // Asks the player to confirm something — overwrite a save, delete one, quit without saving (issue
    // #343) — in a window over everything open that traps focus until it is answered: `answered` hears
    // true for its confirm button, false for its cancel button or Back. Texts starting with '@' are
    // localisation keys. Cancel has focus to begin with (the safe answer), unless `focusConfirm`.
    public UiDialog Confirm(string title, string message, Action<bool>? answered = null, string confirm = "OK", string cancel = "Cancel",
                            bool focusConfirm = false)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        var dialog = ShowDialog(title, message, answered, confirm, cancel);
        if (focusConfirm) dialog.Layer.Root.Focus(dialog.ConfirmButton);
        return dialog;
    }

    // Tells the player something, with one button (focused) that closes it, as Back does; `closed` hears
    // when it has been dismissed.
    public UiDialog Message(string title, string message, Action? closed = null, string ok = "OK") =>
        ShowDialog(title, message, closed == null ? null : _ => closed(), ok, null);

    private UiDialog ShowDialog(string title, string message, Action<bool>? answered, string confirm, string? cancel)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(confirm);
        var text = _screens?.Text;
        string T(string s) => text?.Text(s) ?? s;
        var padding = _styles != null && DialogStyle != null ? _styles.Get(DialogStyle).Padding : new Thickness(12f);
        var dialog = new UiDialog(this, answered, T(title), T(message), T(confirm), cancel == null ? null : T(cancel),
                                  DialogStyle, DialogButtonStyle, padding);
        var layer = Add(dialog.Window, null, modal: true, focus: dialog.CancelButton ?? dialog.ConfirmButton);
        layer.CloseOnClickOutside = false;   // a press beside it is on its backdrop, never an answer
        layer.Dialog = dialog;
        dialog.Layer = layer;
        return dialog;
    }

    private UiLayer Add(Widget content, UiScreen? screen, bool modal, Widget? focus = null)
    {
        var root = new UiRoot(_text) { Fonts = Fonts, UiScale = _uiScale, TextScale = _textScale };
        root.SetViewport(_viewport);
        if (TooltipStyle != null)
        {
            // The tooltip is no layout's node, so its style's size and padding are applied here.
            root.Tooltip.Style = TooltipStyle;
            if (_styles != null)
            {
                var style = _styles.Get(TooltipStyle);
                root.Tooltip.TextScale = style.TextScale;
                root.Tooltip.Font = style.Font;
                root.Tooltip.FontSize = style.FontSize;
                root.Tooltip.Padding = style.Padding;
            }
        }
        root.Content.Add(content);
        var layer = new UiLayer(root, content, screen, modal) { Backdrop = modal ? Backdrop : 0u };
        MarkSolid(content);
        _layers.Add(layer);
        if (modal && screen is { Record.Pauses: true }) Hold(layer);
        // Subtitles stay over everything, a window opened after them included.
        if (_subtitleLayer != null && _layers.Remove(_subtitleLayer)) _layers.Add(_subtitleLayer);
        if (modal && (focus == null || !root.Focus(focus))) root.Navigate(UiNavigation.Next);
        layer.Opened(this);
        if (modal) UiSounds.Raise(_world, _screens, screen, UiSound.Open);
        return layer;
    }

    // Starts `layer` fading out; it leaves Layers when it has (and its screen closes then).
    public void Close(UiLayer layer)
    {
        if (!_layers.Contains(layer)) return;
        bool leaving = layer.IsClosing;
        Release(layer);   // the world runs again as a pause menu fades out
        layer.StartClosing(this);
        if (!leaving && layer.Modal) UiSounds.Raise(_world, _screens, layer.Screen, UiSound.Close);
    }

    public bool CloseTop()
    {
        if (Top is not { } top) return false;
        Close(top);
        return true;
    }

    // Every layer, at once (a world unloading, a save loading): nothing fades.
    public void CloseAll()
    {
        for (int i = _layers.Count - 1; i >= 0; i--) Remove(i);
    }

    // "This action opens that screen", and pressing it again while that screen is on top closes it —
    // ScreenStack.Bind's rule, for widget screens.
    public void Bind(ActionId action, RecordId screen)
    {
        if (!action.IsValid) { Log.Warn(LogCat.UI, $"UiScreenStack.Bind: {screen} bound to an unregistered action"); return; }
        if (!_byAction.ContainsKey(action.Index)) _openers.Add(action);
        _byAction[action.Index] = screen;
    }

    // The actions Bind was given, for whatever reads the buttons (ScreenSystem).
    public IReadOnlyList<ActionId> OpenActions => _openers;

    public bool Toggle(ActionId action, UiBindContext context = default)
    {
        if (!action.IsValid || !_byAction.TryGetValue(action.Index, out var screen)) return false;
        if (Top is { Screen: { } shown } top && shown.Id == screen) { Close(top); return true; }
        Open(screen, context);
        return true;
    }

    // One frame: transitions move on by input.DeltaTime, open screens read their view-models, the top
    // modal layer takes the input (the others only their tooltips' time), and Back or a press outside
    // it closes it. Returns what the top layer's UiRoot.Update said (default when nothing is open).
    public UiResult Update(in UiInput input)
    {
        float dt = MathF.Max(input.DeltaTime, 0f);
        SyncSubtitles();
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            _layers[i].Advance(this, dt);
            if (_layers[i].IsClosed) Remove(i);
        }

        var top = Top;
        var result = default(UiResult);
        var idle = UiInput.Wait(dt);
        for (int i = 0; i < _layers.Count; i++)
        {
            var layer = _layers[i];
            if (layer.IsClosing) continue;   // frozen as it was: it is leaving
            if (layer.Rebuilt())
            {
                // A reload built the tree again: the old focus went with the old tree.
                MarkSolid(layer.Content);
                if (layer.Modal && layer.Root.Focused == null) layer.Root.Navigate(UiNavigation.Next);
            }
            layer.Screen?.Refresh();
            if (layer == top)
            {
                var focusBefore = layer.Root.Focused;
                result = layer.Root.Update(input);
                // Focus moved by the player's own direction: the "ui_move" tick (a pointer sliding over
                // rows is not one, and nor is a screen opening with its first widget focused).
                if (input.Navigate != UiNavigation.None && layer.Root.Focused != focusBefore)
                    UiSounds.Raise(_world, _screens, layer.Screen, UiSound.Move);
            }
            else layer.Root.Update(idle);
            layer.Track(this, layer == top ? input : idle, layer == top);
        }

        if (top != null)
        {
            if (result.Activated is { } activated)
            {
                UiSounds.Raise(_world, _screens, top.Screen, UiSound.Select);
                top.RaiseActivated(activated);
                Activated?.Invoke(top, activated);
            }
            // The screen's view-model acts on what was activated, and may use Back itself — put down the
            // item it holds — in which case the screen stays (IViewModel.Activate/Back, issue #98).
            bool used = !top.IsClosing && top.Screen != null && top.Screen.Handle(in result);
            // A dialog's Back is its cancel; Back while a focus scope shows inside a screen is for whatever
            // showed it (its view-model, above), never a cue to close the whole layer under it.
            if (result.Back && top.Dialog is { } dialog) dialog.Cancel();
            else if (result.Back && top.CloseOnBack && !used && result.Scope == null) Close(top);
            else if (input.PointerPressed && !result.PointerOverUi && top.CloseOnClickOutside && !top.IsClosing) Close(top);
        }
        return result;
    }

    private void Remove(int index)
    {
        var layer = _layers[index];
        _layers.RemoveAt(index);
        Release(layer);
        layer.Screen?.Close();
        if (layer == _subtitleLayer) { _subtitleLayer = null; _subtitleBox = null; }
    }

    private void SyncSubtitles()
    {
        var feed = Subtitles;
        if (_subtitleBox != null && !ReferenceEquals(_subtitleBox.Feed, feed))
        {
            // Another feed (or none): the old layer goes, and a new one comes with the next line.
            int at = _subtitleLayer == null ? -1 : _layers.IndexOf(_subtitleLayer);
            if (at >= 0) Remove(at);
            _subtitleLayer = null;
            _subtitleBox = null;
        }
        if (feed == null) return;
        if (_subtitleBox == null)
        {
            if (feed.Lines.Count == 0) return;
            var box = new SubtitleBox(feed) { LineStyle = SubtitleStyle, CaptionStyle = CaptionStyle };
            if (_styles != null)
            {
                var line = _styles.Get(SubtitleStyle);
                var caption = CaptionStyle == null ? line : _styles.Get(CaptionStyle);
                box.LineTextScale = line.TextScale;
                box.LinePadding = line.Padding;
                box.CaptionTextScale = caption.TextScale;
                box.CaptionPadding = caption.Padding;
            }
            _subtitleBox = box;
            _subtitleLayer = Add(box, null, modal: false);
            _subtitleLayer.CloseOnBack = _subtitleLayer.CloseOnClickOutside = false;
        }
        _subtitleBox.Sync();
    }

    // A widget that paints something behind itself — a window's background, a frame (the box its style
    // draws, UiRenderPlan.OwnsBox) — is solid to the pointer, container or not: a click on a window's empty part is on the window, not "outside", and
    // must not close it. Containers are see-through otherwise (#95), which keeps a full-screen layout's
    // root from swallowing every click.
    private void MarkSolid(Widget widget)
    {
        if (_styles == null) return;
        if (!widget.HitTestable && UiRenderPlan.OwnsBox(widget))
        {
            var style = _styles.Get(widget.Style);
            if ((style.Colours(UiState.Normal).Background >> 24) != 0 || !style.Image.IsEmpty) widget.HitTestable = true;
        }
        for (int i = 0; i < widget.ChildCount; i++) MarkSolid(widget.Child(i));
    }
}

// What the client read from its devices this frame, before it is UiInput (issue #97): the `ui` input
// context's Menu* buttons and the mouse. Internal, and a plain struct, so the mapping below is tested
// headless and the client does nothing but fill it in.
internal struct UiControls
{
    public bool Up, Down, Left, Right, Tab, Shift, Confirm, ConfirmHeld, Back;

    // The mouse, in viewport pixels; wheel in the window's units (120 a notch).
    public Vector2 Pointer;
    public bool PointerMoved, PointerPressed, PointerDown;
    public int Wheel;

    // A dev window has the mouse (ImGui): the UI sees no pointer at all this frame.
    public bool PointerTaken;

    // Characters the window reported this frame, for a focused text field (issue #340); null: none, or
    // a dev window has the keyboard.
    public string? Typed;

    public float DeltaTime;
}

internal static class UiInputMap
{
    // One wheel notch, as the window reports it.
    public const float WheelNotch = 120f;

    public static UiInput From(in UiControls c)
    {
        var input = new UiInput
        {
            // One direction a frame, in a fixed order: two held at once is a thumb sliding on a D-pad.
            Navigate = c.Up ? UiNavigation.Up
                     : c.Down ? UiNavigation.Down
                     : c.Left ? UiNavigation.Left
                     : c.Right ? UiNavigation.Right
                     : c.Tab ? (c.Shift ? UiNavigation.Previous : UiNavigation.Next)
                     : UiNavigation.None,
            Confirm = c.Confirm,
            ConfirmHeld = c.ConfirmHeld,
            Back = c.Back,
            Pointer = c.Pointer,
            DeltaTime = c.DeltaTime,
            Typed = c.Typed,
        };
        if (!c.PointerTaken)
        {
            input.PointerMoved = c.PointerMoved;
            input.PointerPressed = c.PointerPressed;
            input.PointerDown = c.PointerDown;
            input.Wheel = c.Wheel / WheelNotch;
        }
        return input;
    }
}
