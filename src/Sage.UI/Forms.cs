#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text;

namespace Sage.UI;

// The widgets a form is made of (issue #340): a slider, a checkbox, a dropdown, a text field and tabs.
// Each takes focus and works from the D-pad as well as the pointer — a slider steps with Left/Right, a
// dropdown cycles with them and opens a list on Confirm, a text field takes the characters the window
// reports — and each raises Widget.ValueChanged when the player changes it, which a screen's binding
// writes back to its view-model, so a settings page is a ui_layout record with nothing else to write.

// A Bar the player sets: Left/Right (Up/Down in a Column) move it by Increment, and the pointer sets it
// where it is pressed and drags it while held. Value is clamped to Min..Max and snapped to Step.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public class Slider : Bar
{
    private float _step;

    public Slider() { Focusable = true; }

    public override string TypeName => "slider";

    // The values it snaps to, from Min; 0: none (any value), a twentieth of the range a press.
    public float Step { get => _step; set { value = MathF.Max(value, 0f); if (_step == value) return; _step = value; InvalidateVisual(); } }

    // How far one press moves it.
    public float Increment => _step > 0f ? _step : (Max - Min) / 20f;

    // Sets the value as the player would: clamped, snapped, and ValueChanged raised when it moved.
    public bool SetValue(float value)
    {
        float lo = MathF.Min(Min, Max), hi = MathF.Max(Min, Max);
        if (_step > 0f) value = Min + MathF.Round((value - Min) / _step) * _step;
        value = Math.Clamp(value, lo, hi);
        if (value == Value) return false;
        Value = value;
        NotifyValueChanged();
        return true;
    }

    // The handle, centred on the end of the filled part: half as wide as the bar is tall (in a Row).
    public Rect KnobRect
    {
        get
        {
            var c = ContentRect;
            var fill = FillRect;
            float size = MathF.Max(MathF.Min(c.Width, c.Height) * 0.5f, 2f);
            if (Direction == Orientation.Row)
            {
                float end = IsRightToLeft ? fill.X : fill.Right;   // a right-to-left slider fills from the right (#345)
                return new Rect(Math.Clamp(end - size * 0.5f, c.X, MathF.Max(c.Right - size, c.X)), c.Y, size, c.Height);
            }
            return new Rect(c.X, Math.Clamp(fill.Y - size * 0.5f, c.Y, MathF.Max(c.Bottom - size, c.Y)), c.Width, size);
        }
    }

    protected internal override bool OnNavigate(UiNavigation direction)
    {
        int sign = Direction == Orientation.Row
            ? direction switch { UiNavigation.Left => -1, UiNavigation.Right => 1, _ => 0 } * (IsRightToLeft ? -1 : 1)
            : direction switch { UiNavigation.Down => -1, UiNavigation.Up => 1, _ => 0 };
        if (sign == 0) return false;
        SetValue(Value + sign * Increment);
        return true;   // at an end it stays put rather than letting focus slide off sideways
    }

    protected internal override void OnPointerPressed(Vector2 point) => Follow(point);
    protected internal override void OnPointerDragged(Vector2 point) => Follow(point);

    private void Follow(Vector2 point)
    {
        var c = ContentRect;
        float f = Direction == Orientation.Row
            ? (c.Width > 0f ? (IsRightToLeft ? c.Right - point.X : point.X - c.X) / c.Width : 0f)
            : (c.Height > 0f ? (c.Bottom - point.Y) / c.Height : 0f);
        SetValue(Min + Math.Clamp(f, 0f, 1f) * (Max - Min));
    }
}

// A box, ticked or not, with its text beside it. Confirm or a click flips Checked (and raises Pressed,
// as a button would). Ticked, it is drawn in its style's `selected` state as well as with the tick.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public class Checkbox : Button
{
    private bool _checked;
    private float _box;

    public Checkbox() { TextAlign = Align.Start; }
    public Checkbox(string text) : base(text) { TextAlign = Align.Start; }

    public override string TypeName => "checkbox";

    public bool Checked { get => _checked; set { if (_checked == value) return; _checked = value; InvalidateVisual(); } }

    public override bool IsSelected => _checked;

    // The box: a line of text high and as wide, at the left of the content, centred down it.
    public Rect BoxRect
    {
        get
        {
            var c = ContentRect;
            return new Rect(IsRightToLeft ? c.Right - _box : c.X, c.Y + (c.Height - _box) * 0.5f, _box, _box);
        }
    }

    internal override Rect TextArea
    {
        get
        {
            var c = ContentRect;
            float offset = _box * 1.5f;
            return new Rect(IsRightToLeft ? c.X : c.X + offset, c.Y, MathF.Max(c.Width - offset, 0f), c.Height);
        }
    }

    protected internal override void OnActivate()
    {
        if (!IsEnabled) return;
        Checked = !_checked;
        NotifyValueChanged();
        base.OnActivate();
    }

    protected override Vector2 MeasureContent(Vector2 available, ITextMeasure text)
    {
        _box = MeasureOf(text).LineHeight * EffectiveTextScale;
        if (Text.Length == 0) return new Vector2(_box, _box);
        var label = base.MeasureContent(available, text);
        return new Vector2(_box * 1.5f + label.X, MathF.Max(_box, label.Y));
    }
}

// One of a list of options, shown as the chosen one's text with an arrow. Left/Right cycle through the
// options (wrapping) without opening it, the way a game's settings row does; Confirm or a click opens
// the list below it (above, if it would leave the screen) over everything else — UiRoot.Popup — where
// Up/Down and the pointer highlight a row, Confirm or a click picks it, and Back, a click elsewhere or
// focus leaving folds it again.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public class Dropdown : Button
{
    // Drawn at the right of the box (the engine font has no triangle).
    public const string Arrow = "v";

    private readonly List<string> _options = new();
    private int _selected = -1, _highlighted;
    private bool _open;
    private float _arrow;

    public Dropdown() { TextAlign = Align.Start; }

    public override string TypeName => "dropdown";

    public IReadOnlyList<string> Options => _options;

    // The chosen option's index, or -1 for none; the dropdown shows its text.
    public int Selected
    {
        get => _selected;
        set
        {
            value = Math.Clamp(value, -1, _options.Count - 1);
            if (_selected == value) return;
            _selected = value;
            Text = value >= 0 ? _options[value] : "";
        }
    }

    public string? SelectedOption => _selected >= 0 ? _options[_selected] : null;

    public bool IsOpen => _open;

    // The row of the open list that Confirm would pick.
    public int Highlighted => _highlighted;

    public void SetOptions(IEnumerable<string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options.Clear();
        foreach (string option in options) _options.Add(option ?? "");
        int selected = Math.Min(_selected, _options.Count - 1);
        _selected = -2;   // forces the text to be set again: the option at that index may have changed
        Selected = selected;
        if (_options.Count == 0) Close();
        _highlighted = Math.Clamp(_highlighted, 0, Math.Max(_options.Count - 1, 0));
        InvalidateMeasure();
    }

    // Chooses an option as the player would: ValueChanged is raised when the choice changed.
    public bool Choose(int index)
    {
        if (index < 0 || index >= _options.Count || index == _selected) return false;
        Selected = index;
        NotifyValueChanged();
        return true;
    }

    public void Open()
    {
        if (_open || Root == null || _options.Count == 0 || !IsEnabled) return;
        _open = true;
        _highlighted = Math.Max(_selected, 0);
        Root.OpenPopup(this);
    }

    public void Close()
    {
        if (!_open) return;
        _open = false;
        Root?.ClosePopup(this);
        InvalidateVisual();
    }

    // ---- The open list: rows a line of text high (plus the padding), as wide as the dropdown ---------

    public float RowHeight => (Root is { } root ? MeasureOf(root.Text).LineHeight : 0f) * EffectiveTextScale + Padding.Top + Padding.Bottom;

    public Rect ListRect
    {
        get
        {
            var r = Rect;
            float height = RowHeight * _options.Count;
            float y = r.Bottom;
            if (Root != null && y + height > Root.Size.Y && r.Y - height >= 0f) y = r.Y - height;
            return new Rect(r.X, y, r.Width, height);
        }
    }

    public Rect OptionRect(int index)
    {
        var list = ListRect;
        float row = RowHeight;
        return new Rect(list.X, list.Y + row * index, list.Width, row);
    }

    // The open list's row under a point in virtual units, or -1.
    public int OptionAt(Vector2 point)
    {
        if (!_open) return -1;
        var list = ListRect;
        float row = RowHeight;
        if (row <= 0f || !list.Contains(point)) return -1;
        int index = (int)((point.Y - list.Y) / row);
        return index >= 0 && index < _options.Count ? index : -1;
    }

    // Where the arrow is drawn: the right of the content, as wide as the arrow measured.
    public Rect ArrowRect
    {
        get
        {
            var c = ContentRect;
            if (IsRightToLeft) return new Rect(c.X, c.Y, MathF.Min(_arrow, c.Width), c.Height);   // at the end, which is the left (#345)
            return new Rect(MathF.Max(c.Right - _arrow, c.X), c.Y, MathF.Min(_arrow, c.Width), c.Height);
        }
    }

    internal override Rect TextArea
    {
        get
        {
            var c = ContentRect;
            return new Rect(IsRightToLeft ? c.X + _arrow * 2f : c.X, c.Y, MathF.Max(c.Width - _arrow * 2f, 0f), c.Height);
        }
    }

    internal void Highlight(int index)
    {
        index = Math.Clamp(index, 0, Math.Max(_options.Count - 1, 0));
        if (_highlighted == index) return;
        _highlighted = index;
        InvalidateVisual();
    }

    internal void Pick(int index)
    {
        Close();
        Choose(index);
    }

    protected internal override void OnActivate()
    {
        if (!IsEnabled) return;
        if (_open) Pick(_highlighted);
        else Open();
    }

    protected internal override bool OnNavigate(UiNavigation direction)
    {
        int count = _options.Count;
        if (_open)
        {
            switch (direction)
            {
                case UiNavigation.Up: Highlight(_highlighted - 1); return true;
                case UiNavigation.Down: Highlight(_highlighted + 1); return true;
                case UiNavigation.Left or UiNavigation.Right: return true;
                default: Close(); return false;   // Tab leaves, folding it
            }
        }
        if (count == 0 || direction is not (UiNavigation.Left or UiNavigation.Right)) return false;
        int step = direction == UiNavigation.Right ? 1 : -1;
        int from = _selected < 0 ? (step > 0 ? -1 : 0) : _selected;
        Choose(((from + step) % count + count) % count);
        return true;
    }

    protected internal override bool OnBack()
    {
        if (!_open) return false;
        Close();
        return true;
    }

    // As wide as its widest option (so choosing one does not move what is beside it), plus the arrow.
    protected override Vector2 MeasureContent(Vector2 available, ITextMeasure text)
    {
        float scale = EffectiveTextScale;
        text = MeasureOf(text);   // in its own font (#338)
        var size = new Vector2(0f, text.LineHeight * scale);
        for (int i = 0; i < _options.Count; i++) size = Vector2.Max(size, text.Measure(_options[i], scale));
        _arrow = text.Measure(Arrow, scale).X;
        return new Vector2(size.X + _arrow * 2f, size.Y);
    }
}

// A line of text the player types (Multiline: lines), with a caret: the widget the legacy
// Sage.Simulation.TextField is to the panel screens. It takes characters, not keys (08 §3.1) — '\b'
// deletes before the caret, DEL after it, Enter is a new line only when Multiline — and Left/Right move
// the caret while it can move, so at either end they move focus as usual. The type name is
// `text_field`; the class is not TextField, which would clash with the legacy one in every file that
// uses both namespaces.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public class TextBox : Label
{
    private readonly StringBuilder _edit = new();
    private int _caret = int.MaxValue, _maxLength = 256;
    private string _placeholder = "";
    private bool _multiline;

    public TextBox() { Focusable = true; TextAlign = Align.Start; }

    public override string TypeName => "text_field";

    // What is typed is shown as typed: the caret counts characters of it (#345 leaves editing joined
    // Arabic for later).
    internal override bool Shapes => false;

    public int MaxLength { get => _maxLength; set => _maxLength = Math.Max(value, 1); }

    // Shown, faded, while it is empty.
    public string Placeholder
    {
        get => _placeholder;
        set { value ??= ""; if (string.Equals(_placeholder, value, StringComparison.Ordinal)) return; _placeholder = value; InvalidateMeasure(); }
    }

    public bool Multiline { get => _multiline; set { if (_multiline == value) return; _multiline = value; InvalidateVisual(); } }

    // Where typing goes, 0..Text.Length; at the end until the player moves it.
    public int Caret
    {
        get => Math.Min(_caret, Text.Length);
        set
        {
            value = Math.Clamp(value, 0, Text.Length);
            if (Caret == value) return;
            _caret = value;
            InvalidateVisual();
        }
    }

    protected internal override bool WantsText => true;

    protected internal override void OnText(ReadOnlySpan<char> typed)
    {
        int caret = Caret;
        _edit.Clear().Append(Text);
        bool changed = false;
        foreach (char c in typed)
        {
            if (c == '\b') { if (caret > 0) { _edit.Remove(--caret, 1); changed = true; } }
            else if (c == '\u007f') { if (caret < _edit.Length) { _edit.Remove(caret, 1); changed = true; } }
            else if (c is '\r' or '\n') { if (_multiline && _edit.Length < _maxLength) { _edit.Insert(caret++, '\n'); changed = true; } }
            else if (!char.IsControl(c) && _edit.Length < _maxLength) { _edit.Insert(caret++, c); changed = true; }
        }
        if (!changed) return;
        Text = _edit.ToString();
        _caret = caret;
        NotifyValueChanged();
    }

    protected internal override bool OnNavigate(UiNavigation direction)
    {
        int caret = Caret;
        if (direction == UiNavigation.Left && caret > 0) { Caret = caret - 1; return true; }
        if (direction == UiNavigation.Right && caret < Text.Length) { Caret = caret + 1; return true; }
        return false;
    }

    // The caret's offset from the text's top-left, in virtual units (the renderer's; it measures the line
    // up to the caret, so it allocates — when the plan is rebuilt, not per frame).
    internal Vector2 CaretOffset(ITextMeasure measure)
    {
        string text = Text;
        int caret = Caret;
        if (caret == 0) return Vector2.Zero;
        int start = text.LastIndexOf('\n', caret - 1) + 1;
        int line = 0;
        for (int i = 0; i < start; i++) if (text[i] == '\n') line++;
        float x = start == caret ? 0f : measure.Measure(text.Substring(start, caret - start), EffectiveTextScale).X;
        return new Vector2(x, line * measure.LineHeight * EffectiveTextScale);
    }

    protected override Vector2 MeasureContent(Vector2 available, ITextMeasure text) =>
        Text.Length == 0 && _placeholder.Length > 0 ? MeasureOf(text).Measure(_placeholder, EffectiveTextScale) : base.MeasureContent(available, text);
}

// Pages, one shown at a time, under a row of tabs (Godot's TabContainer): a settings screen's General,
// Video, Audio and Controls. Each tab is a button in Header; moving focus onto one shows its page (the
// way an ItemList selects), so the D-pad goes along the tabs and down into the page. Hidden pages take
// no room and are out of the focus chain.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public class Tabs : Widget
{
    private readonly Stack _header = new(Orientation.Row);
    private int _selected = -1;
    private float _spacing;
    private string? _tabStyle;
    private float _headerHeight;

    public Tabs()
    {
        HitTestable = false;
        AddChild(_header);
    }

    public override string TypeName => "tabs";

    // The tab buttons, in a row, one per page.
    public Stack Header => _header;

    public int PageCount => ChildCount - 1;
    public Widget Page(int index) => Child(index + 1);
    public Button Tab(int index) => (Button)_header.Child(index);

    // The shown page's index, or -1 when there are none. Setting it is the game's, and raises nothing.
    public int Selected { get => _selected; set => Select(value, player: false); }

    public Widget? SelectedPage => _selected >= 0 ? Page(_selected) : null;

    // Between the tabs and the page.
    public float Spacing { get => _spacing; set { if (_spacing == value) return; _spacing = value; InvalidateMeasure(); } }

    // The tab buttons' style; the open tab is drawn in its `selected` state.
    public string? TabStyle
    {
        get => _tabStyle;
        set
        {
            if (_tabStyle == value) return;
            _tabStyle = value;
            for (int i = 0; i < _header.ChildCount; i++) _header.Child(i).Style = value;
        }
    }

    // Adds a page with its tab, last. The first page added is shown.
    public T AddPage<T>(string title, T page) where T : Widget
    {
        ArgumentNullException.ThrowIfNull(page);
        AddChild(page);   // first: throws, adding no tab, if the page already has a parent
        _header.Add(new TabButton(this, title ?? "") { Style = _tabStyle });
        page.Visible = false;
        if (_selected < 0) Select(0, player: false);
        return page;
    }

    public bool RemovePage(Widget page)
    {
        int index = page.Parent == this ? IndexOf(page) - 1 : -1;
        if (index < 0) return false;
        _header.Remove(_header.Child(index));
        RemoveChild(page);
        if (index < _selected) _selected--;
        else if (index == _selected)
        {
            _selected = -1;
            if (PageCount > 0) Select(Math.Min(index, PageCount - 1), player: false);
        }
        return true;
    }

    // Shows a page as the player would: ValueChanged is raised when it changed.
    public bool Choose(int index) => Select(index, player: true);

    private bool Select(int index, bool player)
    {
        if (index < 0 || index >= PageCount || index == _selected) return false;
        if (_selected >= 0 && _selected < PageCount) Page(_selected).Visible = false;
        _selected = index;
        Page(index).Visible = true;
        InvalidateVisual();
        if (player) NotifyValueChanged();
        return true;
    }

    protected internal override void OnDescendantFocused(Widget widget)
    {
        if (widget is TabButton tab && tab.Owner == this) Choose(_header.IndexOf(tab));
    }

    protected override Vector2 MeasureContent(Vector2 available, ITextMeasure text)
    {
        var header = MeasureChild(_header, available, text);
        _headerHeight = header.Y;
        var room = new Vector2(available.X, MathF.Max(available.Y - header.Y - _spacing, 0f));
        var page = Vector2.Zero;
        for (int i = 1; i < ChildCount; i++) page = Vector2.Max(page, MeasureChild(Child(i), room, text));   // hidden pages measure nothing
        return new Vector2(MathF.Max(header.X, page.X), header.Y + (_selected >= 0 ? _spacing + page.Y : 0f));
    }

    protected override void ArrangeContent(Rect content)
    {
        ArrangeChild(_header, new Rect(content.X, content.Y, content.Width, _headerHeight));
        float top = content.Y + _headerHeight + _spacing;
        var slot = new Rect(content.X, top, content.Width, MathF.Max(content.Bottom - top, 0f));
        for (int i = 1; i < ChildCount; i++)
            if (Child(i).Visible) ArrangeChild(Child(i), slot);
    }
}

// One page's tab: a button drawn `selected` while its page is shown.
internal sealed class TabButton : Button
{
    public TabButton(Tabs owner, string title) : base(title) { Owner = owner; }

    public Tabs Owner { get; }

    public override string TypeName => "tab";

    public override bool IsSelected => Owner.Selected >= 0 && Owner.Header.IndexOf(this) == Owner.Selected;
}
