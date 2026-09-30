#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.UI;

// A retained widget (docs/design/13 "As built (widgets)", issue #95).
//
// Layout is two passes, both driven by the UiRoot: **measure** (bottom up: how big would you like to
// be, given this much room?) and **arrange** (top down: here is your rect, place your children). A
// property that changes a size marks the widget and its ancestors dirty; a clean widget answers both
// passes from what it cached, so laying out a clean tree does nothing and allocates nothing (02 §4.6).
//
// Everything is in virtual units (UiRoot.Scale turns them into pixels) and nothing here draws: Rect,
// Clip and the widget's own properties are what the client's renderer reads (#97). Containers are
// Godot-like — a Box places children by anchors, a Stack in a row or a column, a Grid in cells — not
// flexbox.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public abstract class Widget
{
    private List<Widget>? _children;
    private bool _visible = true, _enabled = true, _focusable;
    private int _tabIndex;
    private Vector2 _minSize;
    private Thickness _margin, _padding;
    private Align _hAlign, _vAlign;
    private float _expand;
    private Anchors _anchors;
    private string? _tooltipText, _style;

    private bool _measureDirty = true, _arrangeDirty = true;
    private Vector2 _lastAvailable = new(-1f, -1f);

    // The name WidgetTypes.Create makes this from ("label", "stack", ...): what a ui_layout record says (#96).
    public abstract string TypeName { get; }

    // An id to find it by (Find), unique within its tree by convention; nothing enforces it.
    public string? Name { get; set; }

    // Whatever the game hangs on it: the record id an inventory cell shows, a view-model (#98).
    public object? Data { get; set; }

    public Widget? Parent { get; private set; }
    public UiRoot? Root { get; private set; }

    // ---- Layout properties ----------------------------------------------------------------------------

    // Hidden widgets take no room, draw nothing, are never hit and never focused.
    public bool Visible
    {
        get => _visible;
        set { if (_visible == value) return; _visible = value; InvalidateMeasure(); Root?.FocusChainChanged(); }
    }

    // A disabled widget still takes its room and draws (greyed, the renderer's choice) but cannot be
    // focused or activated; being disabled is inherited (IsEnabled).
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled == value) return; _enabled = value; Root?.FocusChainChanged(); Root?.Touch(); }
    }

    // Takes focus from navigation, the pointer and Focus(). Buttons and list items are focusable.
    public bool Focusable
    {
        get => _focusable;
        set { if (_focusable == value) return; _focusable = value; Root?.FocusChainChanged(); }
    }

    // Tab order (UiNavigation.Next/Previous): ascending, then tree order among equals.
    public int TabIndex
    {
        get => _tabIndex;
        set { if (_tabIndex == value) return; _tabIndex = value; Root?.FocusChainChanged(); }
    }

    // Never measured smaller than this (a bar, an icon slot, a fixed-width column).
    public Vector2 MinSize { get => _minSize; set { if (_minSize == value) return; _minSize = value; InvalidateMeasure(); } }

    // Outside the rect: space the parent leaves around it (and, in a Box, the offset from its anchors).
    public Thickness Margin { get => _margin; set { if (_margin == value) return; _margin = value; InvalidateMeasure(); } }

    // Inside the rect: between its edge and its content or children.
    public Thickness Padding { get => _padding; set { if (_padding == value) return; _padding = value; InvalidateMeasure(); } }

    // How it sits in the slot a Stack, Grid or Scroll gives it (a Box uses Anchors instead).
    public Align HAlign { get => _hAlign; set { if (_hAlign == value) return; _hAlign = value; InvalidateMeasure(); } }
    public Align VAlign { get => _vAlign; set { if (_vAlign == value) return; _vAlign = value; InvalidateMeasure(); } }

    // In a Stack, its share of the room left over along the stack's axis (0: none, the default).
    public float Expand { get => _expand; set { if (_expand == value) return; _expand = value; InvalidateMeasure(); } }

    // In a Box, where it is placed (see Anchors). TopLeft by default.
    public Anchors Anchors { get => _anchors; set { if (_anchors == value) return; _anchors = value; InvalidateMeasure(); } }

    // Shown by the root's Tooltip while this (or a child without one of its own) is hovered or focused.
    public string? TooltipText { get => _tooltipText; set { if (_tooltipText == value) return; _tooltipText = value; Root?.Touch(); } }

    // A style id for the renderer (#97) and ui_style records (#96); layout ignores it.
    public string? Style { get => _style; set { if (_style == value) return; _style = value; Root?.Touch(); } }

    // Whether the pointer can land on it. Leaves and Scroll do; Box, Stack and Grid are see-through by
    // default, so a click on the empty part of a screen is not "on the UI" unless a window says it is.
    public bool HitTestable { get; set; } = true;

    // ---- Results of layout ----------------------------------------------------------------------------

    // Measured size, padding included and margin not.
    public Vector2 DesiredSize { get; private set; }

    // Arranged rect, in virtual units from the root's top-left, margin excluded.
    public Rect Rect { get; private set; }

    // The part of the root that is visible to it: a Scroll clips what it holds.
    public Rect Clip { get; private set; }

    // Rect without the padding: where content and children go.
    public Rect ContentRect => Padding.Deflate(Rect);

    // ---- State ----------------------------------------------------------------------------------------

    public bool IsEnabled => _enabled && (Parent?.IsEnabled ?? true);
    public bool IsVisibleInTree => _visible && Root != null && (Parent?.IsVisibleInTree ?? true);
    public bool CanFocus => _focusable && IsEnabled && IsVisibleInTree;
    public bool IsFocused => Root != null && Root.Focused == this;
    public bool IsHovered => Root != null && Root.Hovered == this;

    // ---- Tree -----------------------------------------------------------------------------------------

    public int ChildCount => _children?.Count ?? 0;
    public Widget Child(int index) => (_children ?? throw new ArgumentOutOfRangeException(nameof(index)))[index];

    public int IndexOf(Widget child) => _children?.IndexOf(child) ?? -1;

    // This widget or one below it.
    public bool Contains(Widget? widget)
    {
        for (var w = widget; w != null; w = w.Parent)
            if (w == this) return true;
        return false;
    }

    // The first widget with this name, depth first, this one included.
    public Widget? Find(string name)
    {
        if (Name == name) return this;
        for (int i = 0; i < ChildCount; i++)
            if (_children![i].Find(name) is { } found) return found;
        return null;
    }

    public T? Find<T>(string name) where T : Widget => Find(name) as T;

    private protected void AddChild(Widget child) => InsertChild(ChildCount, child);

    private protected void InsertChild(int index, Widget child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (child.Parent != null || child.Root != null) throw new InvalidOperationException($"{child.TypeName} already has a parent");
        for (var w = this; w != null; w = w.Parent)
            if (w == child) throw new InvalidOperationException("a widget cannot contain itself");
        (_children ??= new List<Widget>()).Insert(index, child);
        child.Parent = this;
        child.SetRoot(Root);
        OnChildAdded(child);
        InvalidateMeasure();
        Root?.FocusChainChanged();
    }

    private protected bool RemoveChild(Widget child)
    {
        if (child.Parent != this || _children == null) return false;
        Root?.Detaching(child);
        _children.Remove(child);
        child.Parent = null;
        child.SetRoot(null);
        OnChildRemoved(child);
        InvalidateMeasure();
        Root?.FocusChainChanged();
        return true;
    }

    private protected void ClearChildren()
    {
        while (ChildCount > 0) RemoveChild(_children![^1]);
    }

    internal void SetRoot(UiRoot? root)
    {
        Root = root;
        _measureDirty = _arrangeDirty = true;
        for (int i = 0; i < ChildCount; i++) _children![i].SetRoot(root);
    }

    // ---- Invalidation ---------------------------------------------------------------------------------

    // Something that changes this widget's size changed: measure and arrange it, and its ancestors, again.
    public void InvalidateMeasure()
    {
        for (var w = this; w != null; w = w.Parent)
        {
            if (w._measureDirty && w != this) break;   // an ancestor already dirty has dirty ancestors too
            w._measureDirty = w._arrangeDirty = true;
            if (w.Parent == null) w.Root?.LayoutChanged();
        }
    }

    // Something that moves its children without changing its size changed (a scroll offset).
    public void InvalidateArrange()
    {
        for (var w = this; w != null; w = w.Parent)
        {
            if (w._arrangeDirty && w != this) break;
            w._arrangeDirty = true;
            if (w.Parent == null) w.Root?.LayoutChanged();
        }
    }

    // Something only the renderer cares about changed (a bar's value): the root's Version moves.
    protected void InvalidateVisual() => Root?.Touch();

    internal void InvalidateTree()
    {
        _measureDirty = _arrangeDirty = true;
        for (int i = 0; i < ChildCount; i++) _children![i].InvalidateTree();
    }

    // ---- Layout ---------------------------------------------------------------------------------------

    // `available` is the room offered, margin already taken off; it may be infinite along a Scroll's axis.
    internal Vector2 Measure(Vector2 available, ITextMeasure text)
    {
        if (!_visible)
        {
            DesiredSize = Vector2.Zero;
            _measureDirty = false;
            return DesiredSize;
        }
        if (!_measureDirty && available == _lastAvailable) return DesiredSize;

        var padding = _padding.Size;
        var content = MeasureContent(Vector2.Max(available - padding, Vector2.Zero), text);
        DesiredSize = Vector2.Max(content + padding, _minSize);
        _lastAvailable = available;
        _measureDirty = false;
        _arrangeDirty = true;
        return DesiredSize;
    }

    internal void Arrange(Rect rect, Rect clip)
    {
        if (!_visible) return;
        if (!_arrangeDirty && rect == Rect && clip == Clip) return;
        Rect = rect;
        Clip = clip;
        _arrangeDirty = false;
        ArrangeContent(_padding.Deflate(rect));
        Root?.Touch();
    }

    // The content's size (padding excluded) given `available` room for it. Containers measure their
    // children here with MeasureChild.
    protected virtual Vector2 MeasureContent(Vector2 available, ITextMeasure text) => Vector2.Zero;

    // Place the children inside `content` (the rect less the padding) with ArrangeChild.
    protected virtual void ArrangeContent(Rect content) { }

    // What children are clipped to: this widget's own clip, less its viewport for a Scroll.
    protected virtual Rect ChildClip => Clip;

    // A child's size with its margin, measured in `available` (its margin is taken off first).
    protected static Vector2 MeasureChild(Widget child, Vector2 available, ITextMeasure text)
    {
        if (!child._visible) { child.Measure(Vector2.Zero, text); return Vector2.Zero; }
        var margin = child._margin.Size;
        return child.Measure(Vector2.Max(available - margin, Vector2.Zero), text) + margin;
    }

    // Place a child in `slot` (margin included): the margin comes off and its alignment decides the rest.
    protected void ArrangeChild(Widget child, Rect slot)
    {
        var inner = child._margin.Deflate(slot);
        var (x, width) = RectMath.Place(child._hAlign, inner.X, inner.Width, child.DesiredSize.X);
        var (y, height) = RectMath.Place(child._vAlign, inner.Y, inner.Height, child.DesiredSize.Y);
        child.Arrange(new Rect(x, y, width, height), ChildClip);
    }

    // Place a child at exactly `rect` (margin and alignment already applied by the caller).
    protected void ArrangeChildAt(Widget child, Rect rect) => child.Arrange(rect, ChildClip);

    // ---- Hooks ----------------------------------------------------------------------------------------

    protected virtual void OnChildAdded(Widget child) { }
    protected virtual void OnChildRemoved(Widget child) { }

    // Confirm, or a click, on this widget while it has focus.
    protected internal virtual void OnActivate() { }

    // A widget below this one was activated or took focus (an ItemList turns that into its selection).
    protected internal virtual void OnDescendantActivated(Widget widget) { }
    protected internal virtual void OnDescendantFocused(Widget widget) { }
}

// A widget that holds any number of children: Box, Stack (and ItemList), Grid.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public abstract class Container : Widget
{
    protected Container() { HitTestable = false; }

    // Adds `child` last (drawn on top, first in tree order after its siblings) and returns it.
    public T Add<T>(T child) where T : Widget { AddChild(child); return child; }

    public T Insert<T>(int index, T child) where T : Widget { InsertChild(index, child); return child; }

    public bool Remove(Widget child) => RemoveChild(child);

    public void Clear() => ClearChildren();
}
