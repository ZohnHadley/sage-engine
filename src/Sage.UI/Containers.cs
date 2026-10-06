#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.UI;

// Children placed by their Anchors and Margin, each independently (Godot's Control in a plain
// Control): the root's Content is one, and so is a window with a title in one corner and a close button
// in another. It measures as big as its biggest child needs.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public class Box : Container
{
    private bool _clipChildren;

    public override string TypeName => "box";

    // Cuts off what its children draw outside its content rect, as a Scroll does (a map's picture, zoomed in
    // and panned, issue #349); off, a child may overhang it.
    public bool ClipChildren { get => _clipChildren; set { if (_clipChildren == value) return; _clipChildren = value; InvalidateArrange(); } }

    protected override Rect ChildClip => _clipChildren ? RectMath.Intersect(Clip, ContentRect) : Clip;

    protected override Vector2 MeasureContent(Vector2 available, ITextMeasure text)
    {
        var size = Vector2.Zero;
        for (int i = 0; i < ChildCount; i++)
            size = Vector2.Max(size, MeasureChild(Child(i), available, text));
        return size;
    }

    protected override void ArrangeContent(Rect content)
    {
        for (int i = 0; i < ChildCount; i++)
        {
            var child = Child(i);
            if (!child.Visible) continue;
            var a = child.Anchors;
            var m = child.Margin;
            var (x, width) = Place(content.X, content.Width, a.MinX, a.MaxX, m.Left, m.Right, child.DesiredSize.X);
            var (y, height) = Place(content.Y, content.Height, a.MinY, a.MaxY, m.Top, m.Bottom, child.DesiredSize.Y);
            ArrangeChildAt(child, new Rect(x, y, width, height));
        }
    }

    private static (float, float) Place(float start, float length, float from, float to, float marginFrom, float marginTo, float desired)
    {
        float a = start + length * from, b = start + length * to;
        if (to > from) return (a + marginFrom, MathF.Max(b - marginTo - a - marginFrom, 0f));
        return (a + marginFrom - from * (desired + marginFrom + marginTo), desired);
    }
}

// Children one after another in a row or a column (Godot's HBox/VBoxContainer), Spacing apart. Along
// the axis each child gets its measured size plus its share (Expand) of what is left; across it, the
// whole width or height, which its HAlign/VAlign then uses.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public class Stack : Container
{
    private Orientation _direction = Orientation.Column;
    private float _spacing;

    public Stack() { }
    public Stack(Orientation direction) { _direction = direction; }

    public override string TypeName => "stack";

    public Orientation Direction { get => _direction; set { if (_direction == value) return; _direction = value; InvalidateMeasure(); } }
    public float Spacing { get => _spacing; set { if (_spacing == value) return; _spacing = value; InvalidateMeasure(); } }

    protected override Vector2 MeasureContent(Vector2 available, ITextMeasure text)
    {
        bool row = _direction == Orientation.Row;
        float main = 0f, cross = 0f;
        int count = 0;
        for (int i = 0; i < ChildCount; i++)
        {
            var child = Child(i);
            var size = MeasureChild(child, available, text);
            if (!child.Visible) continue;
            main += row ? size.X : size.Y;
            cross = MathF.Max(cross, row ? size.Y : size.X);
            count++;
        }
        if (count > 1) main += _spacing * (count - 1);
        return row ? new Vector2(main, cross) : new Vector2(cross, main);
    }

    protected override void ArrangeContent(Rect content)
    {
        bool row = _direction == Orientation.Row;
        float used = 0f, expand = 0f;
        int count = 0;
        for (int i = 0; i < ChildCount; i++)
        {
            var child = Child(i);
            if (!child.Visible) continue;
            used += Main(child, row);
            expand += MathF.Max(child.Expand, 0f);
            count++;
        }
        if (count > 1) used += _spacing * (count - 1);
        float extra = MathF.Max((row ? content.Width : content.Height) - used, 0f);

        float position = row ? content.X : content.Y;
        for (int i = 0; i < ChildCount; i++)
        {
            var child = Child(i);
            if (!child.Visible) continue;
            float length = Main(child, row) + (expand > 0f ? extra * MathF.Max(child.Expand, 0f) / expand : 0f);
            ArrangeChild(child, row ? new Rect(position, content.Y, length, content.Height)
                                    : new Rect(content.X, position, content.Width, length));
            position += length + _spacing;
        }
    }

    private static float Main(Widget child, bool row) =>
        row ? child.DesiredSize.X + child.Margin.Left + child.Margin.Right
            : child.DesiredSize.Y + child.Margin.Top + child.Margin.Bottom;
}

// Children in cells, row by row, Columns to a row (Godot's GridContainer): an inventory, a spell grid, a
// key-binding table. A column is as wide as its widest cell and a row as tall as its tallest, or every
// cell the size of the biggest when Uniform. Hidden children take no cell.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public class Grid : Container
{
    private int _columns = 1;
    private Vector2 _spacing;
    private bool _uniform;
    private float[] _widths = Array.Empty<float>(), _heights = Array.Empty<float>();
    private int _usedColumns, _usedRows;

    public override string TypeName => "grid";

    public int Columns { get => _columns; set { value = Math.Max(value, 1); if (_columns == value) return; _columns = value; InvalidateMeasure(); } }
    public Vector2 Spacing { get => _spacing; set { if (_spacing == value) return; _spacing = value; InvalidateMeasure(); } }
    public bool Uniform { get => _uniform; set { if (_uniform == value) return; _uniform = value; InvalidateMeasure(); } }

    // How many cells across and down the last layout used.
    public int UsedColumns => _usedColumns;
    public int UsedRows => _usedRows;

    protected override Vector2 MeasureContent(Vector2 available, ITextMeasure text)
    {
        int count = 0;
        for (int i = 0; i < ChildCount; i++) if (Child(i).Visible) count++;
        _usedColumns = Math.Min(_columns, count);
        _usedRows = count == 0 ? 0 : (count + _columns - 1) / _columns;
        // Grown only when the grid grows, never per frame.
        if (_widths.Length < _usedColumns) Array.Resize(ref _widths, _columns);
        if (_heights.Length < _usedRows) Array.Resize(ref _heights, _usedRows);
        Array.Clear(_widths);
        Array.Clear(_heights);

        int cell = 0;
        for (int i = 0; i < ChildCount; i++)
        {
            var child = Child(i);
            var size = MeasureChild(child, available, text);
            if (!child.Visible) continue;
            int column = cell % _columns, row = cell / _columns;
            _widths[column] = MathF.Max(_widths[column], size.X);
            _heights[row] = MathF.Max(_heights[row], size.Y);
            cell++;
        }
        if (_uniform)
        {
            float w = 0f, h = 0f;
            for (int c = 0; c < _usedColumns; c++) w = MathF.Max(w, _widths[c]);
            for (int r = 0; r < _usedRows; r++) h = MathF.Max(h, _heights[r]);
            for (int c = 0; c < _usedColumns; c++) _widths[c] = w;
            for (int r = 0; r < _usedRows; r++) _heights[r] = h;
        }

        float width = 0f, height = 0f;
        for (int c = 0; c < _usedColumns; c++) width += _widths[c];
        for (int r = 0; r < _usedRows; r++) height += _heights[r];
        if (_usedColumns > 1) width += _spacing.X * (_usedColumns - 1);
        if (_usedRows > 1) height += _spacing.Y * (_usedRows - 1);
        return new Vector2(width, height);
    }

    protected override void ArrangeContent(Rect content)
    {
        int cell = 0;
        float y = content.Y;
        float x = content.X;
        for (int i = 0; i < ChildCount; i++)
        {
            var child = Child(i);
            if (!child.Visible) continue;
            int column = cell % _columns, row = cell / _columns;
            if (column == 0)
            {
                x = content.X;
                if (row > 0) y += _heights[row - 1] + _spacing.Y;
            }
            ArrangeChild(child, new Rect(x, y, _widths[column], _heights[row]));
            x += _widths[column] + _spacing.X;
            cell++;
        }
    }
}

// A column of items, each a focus target (an item added is made Focusable): the rows of an inventory
// list, dialogue topics, a load-game list. Moving focus onto an item selects it, and confirming it (or
// clicking it) is ItemActivated. Put it in a Scroll to scroll it: focus keeps the selection in view.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public class ItemList : Stack
{
    private int _selected = -1;

    public ItemList() : base(Orientation.Column) { }

    public override string TypeName => "item_list";

    // The index of the item that has (or last had) focus, or -1.
    public int Selected => _selected;
    public Widget? SelectedItem => _selected >= 0 && _selected < ChildCount ? Child(_selected) : null;

    public event Action<ItemList, int>? SelectionChanged;
    public event Action<ItemList, int>? ItemActivated;

    // Selects an item, and focuses it when the list is in a tree.
    public void Select(int index)
    {
        if (index < 0 || index >= ChildCount) return;
        if (Root != null && Child(index).CanFocus) Root.Focus(Child(index));
        else SetSelected(index);
    }

    // An item is a focus target, and the pointer's even when it is a row of several widgets (a stack of
    // labels: a journal's line, issue #349), so clicking it activates it as the comment above says.
    protected override void OnChildAdded(Widget child)
    {
        child.Focusable = true;
        child.HitTestable = true;
    }

    protected override void OnChildRemoved(Widget child)
    {
        if (_selected >= ChildCount) SetSelected(ChildCount - 1);
    }

    protected internal override void OnDescendantFocused(Widget widget)
    {
        int index = ItemIndex(widget);
        if (index >= 0) SetSelected(index);
    }

    protected internal override void OnDescendantActivated(Widget widget)
    {
        int index = ItemIndex(widget);
        if (index >= 0) ItemActivated?.Invoke(this, index);
    }

    private int ItemIndex(Widget widget)
    {
        var w = widget;
        while (w != null && w.Parent != this) w = w.Parent;
        return w == null ? -1 : IndexOf(w);
    }

    private void SetSelected(int index)
    {
        if (_selected == index) return;
        _selected = index;
        InvalidateVisual();
        SelectionChanged?.Invoke(this, index);
    }
}
