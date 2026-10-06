#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.UI;

// A widget tree and everything that is about the whole tree (docs/design/13 "As built (widgets)"):
//
// - **Virtual units.** Layout happens in a design resolution (DesignSize, 1280×720 by default) scaled
//   to the viewport by the smaller of the two ratios, the other axis getting the extra room (Godot's
//   "expand" aspect): Size is the viewport in virtual units, Scale turns them into pixels.
// - **Layout** runs only when something is dirty (Layout returns whether it did anything) and Version
//   moves whenever what would be drawn changed, so a renderer can cache its plan against it (#97).
// - **Focus**: one focused widget, moved by UiInput — tab order (Next/Previous), spatial navigation
//   (Up/Down/Left/Right: the nearest focusable widget that way, preferring ones in line), or the pointer
//   (hover moves focus, a click focuses and activates, as the panel screens do).
// - **Focus scopes** (issue #343): while a widget with FocusScope shows, the chain, navigation, Focus
//   and the pointer are kept inside it, and focus goes back where it was when it hides.
// - **Hit testing** in pixels or virtual units, respecting Scroll clipping.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class UiRoot
{
    public static readonly Vector2 DefaultDesignSize = new(1280f, 720f);

    private readonly List<Widget> _chain = new();
    private readonly List<(Widget Scope, Widget? Return)> _scopes = new();   // the scopes entered, outermost first
    private Widget? _scope;
    private bool _layoutDirty = true, _chainDirty = true;
    private ITextMeasure _text;
    private Widget? _focused, _hovered, _tooltipTarget;
    private Widget? _captured, _changed;   // the widget the pointer went down on, while held; the one the player changed
    private Dropdown? _popup;
    private float _tooltipTime;
    private bool _pointerLast;
    private Widget? _dragCandidate;   // a Draggable widget the pointer went down on: a click or a drag, as it turns out
    private Vector2 _pressPoint;
    private UiDrag _drag;

    public UiRoot(ITextMeasure text, Vector2 designSize = default)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        DesignSize = designSize == default ? DefaultDesignSize : designSize;
        Viewport = DesignSize;
        Scale = 1f;
        Size = DesignSize;
        Content = new Box();
        Content.SetRoot(this);
        Tooltip = new Tooltip();
        Tooltip.SetRoot(this);
    }

    // The tree: a Box the size of the screen. Add screens, windows and HUD pieces to it.
    public Box Content { get; }

    // The one tooltip, drawn over everything.
    public Tooltip Tooltip { get; }

    public ITextMeasure Text
    {
        get => _text;
        set
        {
            _text = value ?? throw new ArgumentNullException(nameof(value));
            Content.InvalidateTree();
            Tooltip.InvalidateTree();
            LayoutChanged();
        }
    }

    // The fonts a label's Font names (#338); none: every label measures with Text, scaled to its FontSize.
    public UiFonts? Fonts
    {
        get => _fonts;
        set
        {
            if (ReferenceEquals(_fonts, value)) return;
            _fonts = value;
            Content.InvalidateTree();
            Tooltip.InvalidateTree();
            LayoutChanged();
        }
    }

    private UiFonts? _fonts, _noFonts;

    // How text in `font` at `size` measures: the font's own metrics for a TTF, Text (scaled) otherwise.
    internal ITextMeasure MeasureFor(AssetPath font, float size)
    {
        if (font.IsEmpty && (size <= 0f || size == UiFonts.DefaultSize)) return _text;
        return (_fonts ?? (_noFonts ??= new UiFonts(static _ => null))).Measure(font, size, _text);
    }

    // Lines a label breaks its text into while it measures (TextLayout): one list per tree, reused.
    internal List<TextLine> TextLines { get; } = new();

    public Vector2 DesignSize { get; }
    public Vector2 Viewport { get; private set; }
    public float Scale { get; private set; }

    // The viewport in virtual units: DesignSize, plus whatever the viewport's aspect adds on one axis.
    public Vector2 Size { get; private set; }

    // Moves whenever layout, focus, hover, the tooltip or a widget's look changed.
    public int Version { get; private set; }

    public Widget? Focused => _focused;
    public Widget? Hovered => _hovered;

    // The drag in progress (issue #346), or an inactive one (IsActive false).
    public UiDrag Drag => _drag;
    public bool IsDragging => _drag.IsActive;

    // How far (virtual units) the pointer moves, held on a Draggable widget, before it is a drag and not a click.
    public float DragThreshold { get; set; } = 6f;

    // Ends the drag in progress without a drop (a view-model that will not take it): nothing is reported.
    public void CancelDrag()
    {
        if (!_drag.IsActive) return;
        _drag = default;
        Version++;
    }

    // The dropdown whose list is open, drawn over everything and hit first (issue #340).
    public Dropdown? Popup => _popup;

    // The focusable widgets in tab order, as of the last change to the tree: only those inside the
    // focus scope, while one shows.
    public IReadOnlyList<Widget> FocusChain { get { EnsureChain(); return _chain; } }

    // The focus scope that holds focus now (Widget.FocusScope), or null: the whole tree.
    public Widget? ActiveScope { get { EnsureChain(); return _scope; } }

    public void SetViewport(Vector2 pixels)
    {
        if (pixels == Viewport) return;
        Viewport = pixels;
        Scale = MathF.Max(MathF.Min(pixels.X / DesignSize.X, pixels.Y / DesignSize.Y), 1e-4f);
        Size = pixels / Scale;
        LayoutChanged();
    }

    public Vector2 ToVirtual(Vector2 pixels) => pixels / Scale;
    public Vector2 ToPixels(Vector2 point) => point * Scale;
    public Rect ToPixels(Rect rect) => new(rect.X * Scale, rect.Y * Scale, rect.Width * Scale, rect.Height * Scale);

    internal void LayoutChanged() { _layoutDirty = true; Version++; }
    internal void Touch() => Version++;
    internal void FocusChainChanged() { _chainDirty = true; Version++; }

    // ---- Layout ---------------------------------------------------------------------------------------

    // Measures and arranges what is dirty. Returns false, having done nothing, when the tree is clean.
    public bool Layout()
    {
        if (!_layoutDirty) return false;
        _layoutDirty = false;
        var screen = new Rect(0f, 0f, Size.X, Size.Y);
        Content.Measure(Size, _text);
        Content.Arrange(screen, screen);
        if (Tooltip.Visible) PlaceTooltip(screen);
        return true;
    }

    private void PlaceTooltip(Rect screen)
    {
        var size = Tooltip.Measure(Size, _text);
        var target = Tooltip.Target?.Rect ?? default;
        float x = Math.Clamp(target.X, 0f, MathF.Max(screen.Width - size.X, 0f));
        float y = target.Bottom + Tooltip.Offset;
        if (y + size.Y > screen.Height) y = MathF.Max(target.Y - Tooltip.Offset - size.Y, 0f);
        Tooltip.Arrange(new Rect(x, y, size.X, size.Y), screen);
    }

    // ---- Input ----------------------------------------------------------------------------------------

    // One frame of input: pointer first (hover, wheel, click), then navigation, confirm and back. Lays
    // out before (so hit tests see this frame's rects) and after (so the caller sees the result).
    public UiResult Update(in UiInput input)
    {
        var result = new UiResult();
        var before = _focused;
        _changed = null;
        Layout();
        DropInvalidFocus();
        if (_popup != null && (_popup.Root != this || !_popup.IsOpen || !_popup.IsVisibleInTree)) _popup = null;
        SyncScope();
        result.Scope = _scope;

        var point = ToVirtual(input.Pointer);
        if (_popup != null && (input.PointerMoved || input.PointerPressed))
        {
            // An open dropdown's list is over everything and has the pointer to itself: a row is
            // highlighted under it and picked by a click, and a click anywhere else only closes it.
            int row = _popup.OptionAt(point);
            result.PointerOverUi = true;
            if (input.PointerMoved) _pointerLast = true;
            if (input.PointerMoved && row >= 0) _popup.Highlight(row);
            if (input.PointerPressed)
            {
                var popup = _popup;
                if (row >= 0) { popup.Pick(row); result.Activated = popup; }
                else popup.Close();   // outside the list, its own box included: it folds
            }
        }
        else if (input.PointerMoved || input.PointerPressed || input.Wheel != 0f)
        {
            // Inside a focus scope nothing outside it is under the pointer, and the whole screen is the
            // UI's: a press beside a confirm prompt is on the prompt's backdrop, not the world or the menu.
            var hit = HitTestVirtual(point);
            if (_scope != null && !_scope.Contains(hit)) hit = null;
            result.PointerOverUi = hit != null || _scope != null;
            if (input.PointerMoved)
            {
                _pointerLast = true;
                SetHovered(hit);
                // While a slider is held, the pointer drags it rather than moving focus away; a drag moves
                // focus with it, onto what it would be dropped on.
                if ((_captured == null || _drag.IsActive) && FocusTarget(hit) is { } target) FocusCore(target);
            }
            if (input.Wheel != 0f && ScrollAncestor(hit) is { } scroll)
                scroll.ScrollBy(new Vector2(0f, -input.Wheel * scroll.WheelStep));
            if (input.PointerPressed && FocusTarget(hit) is { } pressed && FocusCore(pressed))
            {
                _captured = pressed;
                _drag = default;
                if (pressed.Draggable && pressed.IsEnabled && input.PointerDown)
                {
                    // Held on something that can be dragged: a click if it is let go where it is, a drag if
                    // it moves first — so it is activated on the release, never on the press.
                    _dragCandidate = pressed;
                    _pressPoint = point;
                }
                else
                {
                    _dragCandidate = null;
                    if (pressed.IsEnabled) pressed.OnPointerPressed(point);
                    result.Activated = Activate(pressed);
                }
            }
            else if (input.PointerDown && input.PointerMoved && _captured != null && _captured.Root == this && _captured.IsEnabled)
            {
                if (_drag.IsActive)
                {
                    _drag = new UiDrag(_drag.Source!, _drag.Payload, _drag.Start, point, hit);
                    result.DragMoved = true;
                    result.Drag = _drag;
                    Version++;   // the ghost moved
                }
                else if (_dragCandidate != null)
                {
                    if (Vector2.Distance(point, _pressPoint) >= DragThreshold)
                    {
                        _drag = new UiDrag(_dragCandidate, RowOf(_dragCandidate), _pressPoint, point, hit);
                        _dragCandidate = null;
                        result.DragStarted = true;
                        result.Drag = _drag;
                        Version++;
                    }
                }
                else _captured.OnPointerDragged(point);
            }
        }
        if (!input.PointerDown)
        {
            if (_drag.IsActive || _dragCandidate != null)
            {
                // Let go: what is under the pointer now, whether or not it moved this frame.
                var under = HitTestVirtual(point);
                if (_scope != null && !_scope.Contains(under)) under = null;
                if (_drag.IsActive)
                {
                    result.Dropped = true;
                    result.Drag = new UiDrag(_drag.Source!, _drag.Payload, _drag.Start, point, under);
                    _drag = default;
                    Version++;
                }
                else if (_dragCandidate!.Root == this && _dragCandidate.Contains(under))
                    result.Activated = Activate(_dragCandidate);   // a click on something draggable
                _dragCandidate = null;
            }
            _captured = null;
        }

        // A key that typed a character into the focused text field is not also a direction (W, A, S
        // and D move focus in the `ui` context): arrows and the D-pad type nothing, so they still do.
        bool typing = _focused != null && _focused.WantsText && _focused.IsEnabled && Printable(input.Typed);
        if (input.Navigate != UiNavigation.None && !typing)
        {
            _pointerLast = false;
            if (_focused == null || !_focused.IsEnabled || !_focused.OnNavigate(input.Navigate)) Navigate(input.Navigate);
            else Touch();
        }
        if (!string.IsNullOrEmpty(input.Typed) && _focused != null && _focused.WantsText && _focused.IsEnabled)
            _focused.OnText(input.Typed);
        if (input.Confirm && _focused != null && !_drag.IsActive) result.Activated = Activate(_focused);
        if (input.Command != UiCommand.None)
        {
            result.Command = input.Command;
            result.CommandTarget = _drag.IsActive ? _drag.Source : _focused;
        }
        if (input.Back && _drag.IsActive)
        {
            // Back while dragging puts it back: a drop that is cancelled, and Back used.
            result.Dropped = true;
            result.Drag = new UiDrag(_drag.Source!, _drag.Payload, _drag.Start, _drag.Pointer, null, cancelled: true);
            _drag = default;
            _dragCandidate = _captured = null;
            Version++;
        }
        else result.Back = input.Back && !(_focused != null && _focused.OnBack());
        result.Changed = _changed;

        UpdateTooltip(input.DeltaTime);
        Layout();
        result.FocusChanged = _focused != before;
        return result;
    }

    // Moves focus one step. Returns whether it moved. With nothing focused, any direction focuses the
    // first widget in tab order (Previous: the last).
    public bool Navigate(UiNavigation direction)
    {
        if (direction == UiNavigation.None) return false;
        Layout();
        DropInvalidFocus();
        var before = _focused;
        SyncScope();
        if (_focused != before) return true;   // a scope that has just shown took focus: that is the step
        if (_chain.Count == 0) return false;

        int index = _focused == null ? -1 : _chain.IndexOf(_focused);
        Widget? next;
        if (index < 0) next = direction == UiNavigation.Previous ? _chain[^1] : _chain[0];
        else next = direction switch
        {
            UiNavigation.Next => _chain[(index + 1) % _chain.Count],
            UiNavigation.Previous => _chain[(index - 1 + _chain.Count) % _chain.Count],
            _ => Neighbour(_focused!, direction) ?? Spatial(_focused!, direction),
        };
        if (next == null || next == _focused) return false;
        Focus(next);
        return true;
    }

    // Focuses `widget` (null clears focus), scrolling every Scroll it is in so it shows. Returns false
    // when it cannot take focus (not in this tree, hidden, disabled, not Focusable, or outside the focus
    // scope that shows).
    public bool Focus(Widget? widget)
    {
        SyncScope();
        return FocusCore(widget);
    }

    private bool FocusCore(Widget? widget)
    {
        if (widget == _focused) return true;
        if (widget != null && (widget.Root != this || !widget.CanFocus)) return false;
        if (widget != null && _scope != null && !_scope.Contains(widget)) return false;
        if (_popup != null && widget != _popup) _popup.Close();   // focus leaving folds an open list
        _focused = widget;
        Version++;
        if (widget == null) return true;

        Layout();
        for (var w = widget.Parent; w != null; w = w.Parent)
            if (w is Scroll scroll) { scroll.ScrollIntoView(widget); Layout(); }
        for (var w = widget.Parent; w != null; w = w.Parent) w.OnDescendantFocused(widget);
        return true;
    }

    // Confirms `widget` as Enter or a click would: its OnActivate, then each ancestor hears of it.
    public Widget? Activate(Widget widget)
    {
        if (widget.Root != this || !widget.IsEnabled) return null;
        widget.OnActivate();
        for (var w = widget.Parent; w != null; w = w.Parent) w.OnDescendantActivated(widget);
        return widget;
    }

    // ---- Hit testing ----------------------------------------------------------------------------------

    // The topmost hit-testable widget under a point in viewport pixels, or null.
    public Widget? HitTest(Vector2 pixels) => HitTestVirtual(ToVirtual(pixels));

    public Widget? HitTestVirtual(Vector2 point)
    {
        Layout();
        return Hit(Content, point);
    }

    private static Widget? Hit(Widget widget, Vector2 point)
    {
        if (!widget.Visible || !widget.Clip.Contains(point)) return null;
        for (int i = widget.ChildCount - 1; i >= 0; i--)
            if (Hit(widget.Child(i), point) is { } hit) return hit;
        return widget.HitTestable && widget.Rect.Contains(point) ? widget : null;
    }

    // ---- Walking (for the renderer, #97) --------------------------------------------------------------

    // Every visible widget, parents before children and in drawing order (Content, then the tooltip).
    // A struct visitor, so walking allocates nothing.
    public void Walk<TVisitor>(ref TVisitor visitor) where TVisitor : IWidgetVisitor
    {
        Layout();
        WalkFrom(Content, ref visitor);
        if (Tooltip.Visible) WalkFrom(Tooltip, ref visitor);
    }

    private static void WalkFrom<TVisitor>(Widget widget, ref TVisitor visitor) where TVisitor : IWidgetVisitor
    {
        if (!widget.Visible) return;
        if (visitor.Enter(widget))
            for (int i = 0; i < widget.ChildCount; i++) WalkFrom(widget.Child(i), ref visitor);
        visitor.Leave(widget);
    }

    // ---- Internals ------------------------------------------------------------------------------------

    internal void Detaching(Widget subtree)
    {
        if (subtree.Contains(_captured)) _captured = null;
        if (subtree.Contains(_dragCandidate)) _dragCandidate = null;
        if (_drag.IsActive && subtree.Contains(_drag.Source)) { _drag = default; Version++; }
        if (subtree.Contains(_popup)) _popup = null;
        if (subtree.Contains(_focused)) { _focused = null; Version++; }
        if (subtree.Contains(_hovered)) _hovered = null;
        if (subtree.Contains(_tooltipTarget)) HideTooltip();
    }

    internal void ValueChangedBy(Widget widget) { _changed = widget; Version++; }

    internal void OpenPopup(Dropdown dropdown)
    {
        if (_popup != null && _popup != dropdown) _popup.Close();
        _popup = dropdown;
        Version++;
    }

    internal void ClosePopup(Dropdown dropdown)
    {
        if (_popup != dropdown) return;
        _popup = null;
        Version++;
    }

    private static bool Printable(string? typed)
    {
        if (typed == null) return false;
        foreach (char c in typed)
            if (!char.IsControl(c)) return true;
        return false;
    }

    private void DropInvalidFocus()
    {
        if (_focused != null && (_focused.Root != this || !_focused.CanFocus)) { _focused = null; Version++; }
    }

    private void SetHovered(Widget? widget)
    {
        if (_hovered == widget) return;
        _hovered = widget;
        Version++;
    }

    private static Widget? FocusTarget(Widget? hit)
    {
        for (var w = hit; w != null; w = w.Parent)
            if (w.CanFocus) return w;
        return null;
    }

    private static object? RowOf(Widget? widget)
    {
        for (var w = widget; w != null; w = w.Parent)
            if (w.Data != null) return w.Data;
        return null;
    }

    private static Scroll? ScrollAncestor(Widget? hit)
    {
        for (var w = hit; w != null; w = w.Parent)
            if (w is Scroll scroll) return scroll;
        return null;
    }

    private void EnsureChain()
    {
        if (!_chainDirty) return;
        _chainDirty = false;
        _chain.Clear();
        _scope = FindScope(Content, null);
        Collect(_scope ?? Content, _scope?.Parent?.IsEnabled ?? true);
        // Stable insertion sort by TabIndex: the chain is short, and List.Sort would allocate a comparer.
        for (int i = 1; i < _chain.Count; i++)
        {
            var item = _chain[i];
            int j = i - 1;
            while (j >= 0 && _chain[j].TabIndex > item.TabIndex) { _chain[j + 1] = _chain[j]; j--; }
            _chain[j + 1] = item;
        }
    }

    // The last visible FocusScope in tree order: the one drawn on top, an inner one over its outer one.
    private static Widget? FindScope(Widget widget, Widget? found)
    {
        if (!widget.Visible) return found;
        if (widget.FocusScope) found = widget;
        for (int i = 0; i < widget.ChildCount; i++) found = FindScope(widget.Child(i), found);
        return found;
    }

    // Brings focus in line with the scope that shows: one that has just shown takes focus (its first
    // widget in tab order), remembering where it was; one that has gone gives it back.
    private void SyncScope()
    {
        EnsureChain();
        var current = _scopes.Count == 0 ? null : _scopes[^1].Scope;
        if (_scope == current) return;

        int outer = -1;
        for (int i = 0; i < _scopes.Count; i++)
            if (_scopes[i].Scope == _scope) outer = i;
        if (_scope == null || outer >= 0)
        {
            // Back out to the whole tree, or to a scope entered before: focus returns to where it was
            // when the innermost one being left was entered.
            int keep = _scope == null ? 0 : outer + 1;
            var back = _scopes[keep].Return;
            _scopes.RemoveRange(keep, _scopes.Count - keep);
            if (back == null || !FocusCore(back)) FocusCore(First());
            return;
        }

        // A new scope: entered from wherever focus is, which it gets back when this one goes.
        _scopes.Add((_scope, _focused));
        if (_focused == null || !_scope.Contains(_focused))
        {
            _focused = null;
            Version++;
            FocusCore(First());
        }
    }

    private Widget? First() => _chain.Count > 0 ? _chain[0] : null;

    // The neighbour `from` names that way, when there is one that can take focus now.
    private Widget? Neighbour(Widget from, UiNavigation direction)
    {
        string? name = direction switch
        {
            UiNavigation.Up => from.FocusUp,
            UiNavigation.Down => from.FocusDown,
            UiNavigation.Left => from.FocusLeft,
            UiNavigation.Right => from.FocusRight,
            _ => null,
        };
        if (string.IsNullOrEmpty(name)) return null;
        var target = (_scope ?? Content).Find(name);
        return target != null && target != from && target.CanFocus ? target : null;
    }

    private void Collect(Widget widget, bool enabled)
    {
        if (!widget.Visible) return;
        enabled &= widget.Enabled;
        if (enabled && widget.Focusable) _chain.Add(widget);
        for (int i = 0; i < widget.ChildCount; i++) Collect(widget.Child(i), enabled);
    }

    // The nearest focusable widget in `direction` from `from`: it has to lie beyond from's edge that way
    // (or, failing any, merely have its centre that way). One in line — overlapping `from` across the
    // direction — always beats one off to the side, so Right along a grid row skips a disabled slot to
    // the next one in the row rather than dropping diagonally; among each kind the score is the gap along
    // the direction plus twice the gap across it.
    private Widget? Spatial(Widget from, UiNavigation direction)
    {
        Widget? best = Pick(from, direction, strict: true);
        return best ?? Pick(from, direction, strict: false);
    }

    private Widget? Pick(Widget from, UiNavigation direction, bool strict)
    {
        const float Epsilon = 0.5f, OffLine = 1e7f;
        var f = from.Rect;
        var fc = RectMath.Center(f);
        Widget? best = null;
        float bestScore = float.MaxValue;
        foreach (var candidate in _chain)
        {
            if (candidate == from) continue;
            var r = candidate.Rect;
            var rc = RectMath.Center(r);
            float along, across, centreAcross;
            switch (direction)
            {
                case UiNavigation.Right:
                    along = r.X - f.Right; across = Gap(f.Y, f.Bottom, r.Y, r.Bottom); centreAcross = rc.Y - fc.Y;
                    if (strict ? along < -Epsilon : rc.X <= fc.X) continue;
                    break;
                case UiNavigation.Left:
                    along = f.X - r.Right; across = Gap(f.Y, f.Bottom, r.Y, r.Bottom); centreAcross = rc.Y - fc.Y;
                    if (strict ? along < -Epsilon : rc.X >= fc.X) continue;
                    break;
                case UiNavigation.Down:
                    along = r.Y - f.Bottom; across = Gap(f.X, f.Right, r.X, r.Right); centreAcross = rc.X - fc.X;
                    if (strict ? along < -Epsilon : rc.Y <= fc.Y) continue;
                    break;
                case UiNavigation.Up:
                    along = f.Y - r.Bottom; across = Gap(f.X, f.Right, r.X, r.Right); centreAcross = rc.X - fc.X;
                    if (strict ? along < -Epsilon : rc.Y >= fc.Y) continue;
                    break;
                default:
                    return null;
            }
            float score = (across > 0f ? OffLine : 0f) + MathF.Max(along, 0f) + 2f * across + 0.001f * MathF.Abs(centreAcross);
            if (score < bestScore) { bestScore = score; best = candidate; }
        }
        return best;
    }

    // The distance between two ranges, 0 when they overlap.
    private static float Gap(float aStart, float aEnd, float bStart, float bEnd) =>
        MathF.Max(0f, MathF.Max(bStart - aEnd, aStart - bEnd));

    private void UpdateTooltip(float deltaTime)
    {
        var target = TooltipOwner(_pointerLast ? _hovered : _focused);
        if (target != _tooltipTarget)
        {
            HideTooltip();
            _tooltipTarget = target;
            _tooltipTime = 0f;
            return;
        }
        if (target == null || Tooltip.Visible) return;
        _tooltipTime += deltaTime;
        if (_tooltipTime < Tooltip.Delay) return;
        Tooltip.Target = target;
        Tooltip.Text = target.TooltipText!;
        Tooltip.Visible = true;
        LayoutChanged();
    }

    private void HideTooltip()
    {
        _tooltipTarget = null;
        if (!Tooltip.Visible) return;
        Tooltip.Visible = false;
        Tooltip.Target = null;
        Version++;
    }

    private static Widget? TooltipOwner(Widget? widget)
    {
        for (var w = widget; w != null; w = w.Parent)
            if (!string.IsNullOrEmpty(w.TooltipText)) return w;
        return null;
    }
}

// Visits widgets for UiRoot.Walk: Enter before a widget's children (false skips them), Leave after —
// where a renderer pushes and pops a Scroll's clip.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public interface IWidgetVisitor
{
    bool Enter(Widget widget);
    void Leave(Widget widget);
}
