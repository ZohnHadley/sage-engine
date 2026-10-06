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
    public override string TypeName => "box";

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
    private float[] _xs = Array.Empty<float>(), _ys = Array.Empty<float>();   // each column's left and row's top, as arranged
    private int[] _placed = Array.Empty<int>();                              // per child: its first cell (row * columns + column), -1 hidden
    private bool[] _taken = Array.Empty<bool>();
    private int _usedColumns, _usedRows;

    public override string TypeName => "grid";

    public int Columns { get => _columns; set { value = Math.Max(value, 1); if (_columns == value) return; _columns = value; InvalidateMeasure(); } }
    public Vector2 Spacing { get => _spacing; set { if (_spacing == value) return; _spacing = value; InvalidateMeasure(); } }
    public bool Uniform { get => _uniform; set { if (_uniform == value) return; _uniform = value; InvalidateMeasure(); } }

    // How many cells across and down the last layout used.
    public int UsedColumns => _usedColumns;
    public int UsedRows => _usedRows;

    // The cell under a point (virtual units) as of the last layout, or false outside every cell — the
    // spacing between cells counts as the cell before it, so a drop never falls in a crack (issue #346).
    public bool CellAt(Vector2 point, out int column, out int row)
    {
        column = row = -1;
        if (_usedColumns == 0 || _usedRows == 0) return false;
        var content = ContentRect;
        if (!content.Contains(point)) return false;
        column = 0;
        while (column + 1 < _usedColumns && point.X >= _xs[column + 1]) column++;
        row = 0;
        while (row + 1 < _usedRows && point.Y >= _ys[row + 1]) row++;
        return true;
    }

    // Each visible child goes in the next cell, row by row, that is free for its ColumnSpan × RowSpan —
    // cells a span covers are skipped — and grows the rows it spans to fit; a span of one sizes its
    // column and row, a wider one only adds what they lack, shared out.
    protected override Vector2 MeasureContent(Vector2 available, ITextMeasure text)
    {
        if (_placed.Length < ChildCount) Array.Resize(ref _placed, Math.Max(ChildCount, _placed.Length * 2));
        Array.Clear(_taken);
        int cursor = 0, rows = 0, columns = 0;
        for (int i = 0; i < ChildCount; i++)
        {
            var child = Child(i);
            MeasureChild(child, available, text);
            if (!child.Visible) { _placed[i] = -1; continue; }
            int w = Math.Min(child.ColumnSpan, _columns), h = child.RowSpan;
            while (!Free(cursor, w, h)) cursor++;
            _placed[i] = cursor;
            Take(cursor, w, h);
            int column = cursor % _columns, row = cursor / _columns;
            rows = Math.Max(rows, row + h);
            columns = Math.Max(columns, column + w);
            cursor++;
        }
        _usedColumns = columns;
        _usedRows = rows;
        // Grown only when the grid grows, never per frame.
        if (_widths.Length < _columns) { Array.Resize(ref _widths, _columns); Array.Resize(ref _xs, _columns); }
        if (_heights.Length < _usedRows) { Array.Resize(ref _heights, _usedRows); Array.Resize(ref _ys, _usedRows); }
        Array.Clear(_widths);
        Array.Clear(_heights);

        for (int pass = 0; pass < 2; pass++)   // single cells first, then what spans need beyond them
            for (int i = 0; i < ChildCount; i++)
            {
                if (_placed[i] < 0) continue;
                var child = Child(i);
                int w = Math.Min(child.ColumnSpan, _columns), h = child.RowSpan;
                bool single = w == 1 && h == 1;
                if (single != (pass == 0)) continue;
                var size = child.DesiredSize + child.Margin.Size;
                int column = _placed[i] % _columns, row = _placed[i] / _columns;
                Grow(_widths, column, w, size.X, _spacing.X);
                Grow(_heights, row, h, size.Y, _spacing.Y);
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

    // What `sizes[from..from+span]` (with the spacing between) lack of `need`, spread evenly over them.
    private static void Grow(float[] sizes, int from, int span, float need, float spacing)
    {
        float have = spacing * (span - 1);
        for (int k = 0; k < span; k++) have += sizes[from + k];
        if (have >= need) return;
        float each = (need - have) / span;
        for (int k = 0; k < span; k++) sizes[from + k] += each;
    }

    private bool Free(int cell, int w, int h)
    {
        int column = cell % _columns, row = cell / _columns;
        if (column + w > _columns) return false;
        for (int dy = 0; dy < h; dy++)
            for (int dx = 0; dx < w; dx++)
            {
                int at = (row + dy) * _columns + column + dx;
                if (at < _taken.Length && _taken[at]) return false;
            }
        return true;
    }

    private void Take(int cell, int w, int h)
    {
        int column = cell % _columns, row = cell / _columns;
        int last = (row + h - 1) * _columns + column + w;
        if (_taken.Length < last) Array.Resize(ref _taken, Math.Max(last, _taken.Length * 2));
        for (int dy = 0; dy < h; dy++)
            for (int dx = 0; dx < w; dx++) _taken[(row + dy) * _columns + column + dx] = true;
    }

    protected override void ArrangeContent(Rect content)
    {
        float x = content.X, y = content.Y;
        for (int c = 0; c < _usedColumns; c++) { _xs[c] = x; x += _widths[c] + _spacing.X; }
        for (int r = 0; r < _usedRows; r++) { _ys[r] = y; y += _heights[r] + _spacing.Y; }
        for (int i = 0; i < ChildCount; i++)
        {
            var child = Child(i);
            if (!child.Visible || i >= _placed.Length || _placed[i] < 0) continue;
            int column = _placed[i] % _columns, row = _placed[i] / _columns;
            int w = Math.Min(child.ColumnSpan, _columns), h = child.RowSpan;
            if (column + w > _usedColumns || row + h > _usedRows) continue;   // laid out before the child changed: next layout
            int lc = column + w - 1, lr = row + h - 1;
            ArrangeChild(child, new Rect(_xs[column], _ys[row], _xs[lc] + _widths[lc] - _xs[column], _ys[lr] + _heights[lr] - _ys[row]));
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

    protected override void OnChildAdded(Widget child) => child.Focusable = true;

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
