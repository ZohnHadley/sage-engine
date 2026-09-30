#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Sage.UI;

// What bindings and conditions are read against: the world a `visibleIf` asks about, and who it asks
// about (the player looking at the screen). No world: conditions hold.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public readonly record struct UiBindContext(World? World, Entity Subject = default);

// A ui_layout record built into widgets (UiScreens.BuildLayout, or a screen's View), with its bindings.
// Refresh reads every binding and condition and changes only what differs from last time, so once built
// a view that nothing changed in costs a walk and allocates nothing (test: BindingsAndConditionsRefreshWithoutAllocating).
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class UiView
{
    private readonly BoundNode _root;

    internal UiView(RecordId layout, BoundNode root)
    {
        Layout = layout;
        _root = root;
    }

    public RecordId Layout { get; }

    // The layout's root: a Box that fills what it is put in (Anchors.Fill), holding the top-level nodes.
    public Widget Root => _root.Widget;

    public Widget? Find(string name) => Root.Find(name);
    public T? Find<T>(string name) where T : Widget => Root.Find<T>(name);

    // Reads `source` (the view-model) into the bound properties, and asks each condition.
    public void Refresh(object? source, in UiBindContext context) => _root.Refresh(source, in context);
}

// ---- build ----------------------------------------------------------------------------------------

// Turns a layout record into widgets. What it builds is laid out by the widgets' own rules; this only
// sets properties and puts children where their parent says.
internal sealed class LayoutBuilder
{
    private readonly UiStyles _styles;
    private readonly Localisation _text;

    public LayoutBuilder(UiStyles styles, Localisation text)
    {
        _styles = styles;
        _text = text;
    }

    public UiView Build(RecordId id, UiLayoutRecord layout)
    {
        var tree = new LayoutTree(layout);
        // The root is the whole screen and carries no style of its own: the layout's style is what its
        // nodes inherit, and a background there would otherwise be painted over the whole screen (#97).
        var root = new Box { Name = id.Name, Anchors = Anchors.Fill };
        var style = layout.Style.Id;
        var bound = new BoundNode(root, null);
        BuildChildren(tree, "", root, bound, style, top: true);
        return new UiView(id, bound);
    }

    private void BuildChildren(LayoutTree tree, string parent, Widget host, BoundNode bound, RecordId style, bool top = false)
    {
        var children = new List<BoundNode>();
        foreach (var (name, node) in tree.ChildrenOf(parent))
        {
            var child = BuildNode(tree, name, node, style, top);
            Attach(host, child.Widget);
            if (child.Dynamic) children.Add(child);
        }
        bound.SetChildren(children);
    }

    // `top`: a node of the layout's root box, which has no style, so the layout's style is applied here.
    internal BoundNode BuildNode(LayoutTree tree, string name, UiNode node, RecordId inherited, bool top = false)
    {
        var widget = WidgetTypes.Create(node.Widget) ?? new Box();
        widget.Name = name;
        var styleId = node.Style.IsEmpty ? inherited : node.Style.Id;
        var style = _styles.Get(styleId);
        if (!styleId.IsEmpty) widget.Style = styleId.ToString();
        // A style's padding, like the box the renderer draws for it (background, image, border), belongs
        // where the style is applied — where it differs from the parent's — not to every node inheriting
        // it: a window's padding goes round the window, not again round each row inside (#97). Its
        // text scale and colours are inherited.
        Apply(widget, node, style, owns: top || styleId != inherited);

        var bound = new BoundNode(widget, node);
        Bind(bound, node, tree, name, styleId);
        if (bound.Rows == null) BuildChildren(tree, name, widget, bound, styleId);
        return bound;
    }

    private static void Attach(Widget host, Widget child)
    {
        switch (host)
        {
            case Container container: container.Add(child); break;
            case Scroll scroll when scroll.Content == null: scroll.Content = child; break;
            default: Log.Warn(LogCat.UI, $"{host.TypeName} '{host.Name}' holds no child; '{child.Name}' is left out"); break;
        }
    }

    private void Apply(Widget w, UiNode n, UiStyle style, bool owns)
    {
        w.MinSize = n.MinSize;
        w.Margin = n.Margin;
        w.Padding = n.Padding ?? (owns ? style.Padding : Thickness.Zero);
        if (n.HAlign is { } h) w.HAlign = h;
        if (n.VAlign is { } v) w.VAlign = v;
        w.Expand = n.Expand;
        if (n.Anchors is { } anchors) w.Anchors = anchors;
        w.Visible = n.Visible;
        w.Enabled = n.Enabled;
        if (n.Focusable is { } focusable) w.Focusable = focusable;
        w.TabIndex = n.TabIndex;
        if (n.Tooltip.Length > 0) w.TooltipText = _text.Text(n.Tooltip);

        switch (w)
        {
            case Label label:
                if (n.Text.Length > 0) label.Text = _text.Text(n.Text);
                label.TextScale = n.TextScale ?? style.TextScale;
                if (n.TextAlign is { } align) label.TextAlign = align;
                break;
            case Image image:
                if (!n.Source.IsEmpty) image.Source = n.Source.ToString();
                image.NaturalSize = n.NaturalSize;
                if (n.KeepAspect is { } keep) image.KeepAspect = keep;
                break;
            case Bar bar:
                if (n.Min is { } min) bar.Min = min;
                if (n.Max is { } max) bar.Max = max;
                if (n.Value is { } value) bar.Value = value;
                if (n.Direction is { } barDirection) bar.Direction = barDirection;
                break;
            case Grid grid:
                if (n.Columns > 0) grid.Columns = n.Columns;
                grid.Spacing = new Vector2(n.Spacing, n.Spacing);
                grid.Uniform = n.Uniform;
                break;
            case Stack stack:
                if (n.Direction is { } direction) stack.Direction = direction;
                stack.Spacing = n.Spacing;
                break;
            case Scroll scroll:
                if (n.Horizontal is { } across) scroll.Horizontal = across;
                if (n.Vertical is { } down) scroll.Vertical = down;
                break;
        }
    }

    private void Bind(BoundNode bound, UiNode node, LayoutTree tree, string name, RecordId style)
    {
        bound.VisibleIf = node.VisibleIf;
        bound.EnabledIf = node.EnabledIf;
        foreach (var (target, path) in UiBindings.Of(node))
        {
            var reader = new BindingReader(path);
            switch (target)
            {
                case UiBindings.Text: bound.Text = new TextSlot(node.Text, reader, node.Args, _text); break;
                case UiBindings.Tooltip: bound.Tooltip = new TextSlot("", reader, null, _text); break;
                case UiBindings.Value: bound.Value = reader; break;
                case UiBindings.Min: bound.Min = reader; break;
                case UiBindings.Max: bound.Max = reader; break;
                case UiBindings.Source: bound.Source = reader; break;
                case UiBindings.Style: bound.Style = reader; break;
                case UiBindings.Visible: bound.Visible = reader; break;
                case UiBindings.Enabled: bound.Enabled = reader; break;
                case UiBindings.Data: bound.Data = reader; break;
                case UiBindings.X: bound.X = reader; break;
                case UiBindings.Y: bound.Y = reader; break;
                case UiBindings.Rows:
                    var template = tree.ChildrenOf(name).FirstOrDefault();
                    if (template.Node != null && bound.Widget is Container host)
                        bound.Rows = new RowsSlot(host, reader, this, tree, template.Name, template.Node, style);
                    break;
            }
        }
        // Placeholders filled from the view-model, in text that is not itself bound.
        if (bound.Text == null && node.Args.Count > 0 && bound.Widget is Label)
            bound.Text = new TextSlot(node.Text, null, node.Args, _text);
    }
}

// A layout's nodes by parent, in order: as written, then by `order`.
internal sealed class LayoutTree
{
    private readonly Dictionary<string, List<(string Name, UiNode Node)>> _children = new(StringComparer.Ordinal);

    public LayoutTree(UiLayoutRecord layout)
    {
        foreach (var (name, node) in layout.Nodes)
        {
            if (!_children.TryGetValue(node.Parent, out var list)) _children[node.Parent] = list = new();
            list.Add((name, node));
        }
        foreach (var list in _children.Values)
        {
            var sorted = list.Select((child, index) => (child, index)).OrderBy(c => c.child.Node.Order).ThenBy(c => c.index).Select(c => c.child).ToList();
            list.Clear();
            list.AddRange(sorted);
        }
    }

    public IReadOnlyList<(string Name, UiNode Node)> ChildrenOf(string parent) =>
        _children.TryGetValue(parent, out var list) ? list : Array.Empty<(string, UiNode)>();
}

// What a node's `bind` and `bindings` may name, and which property `bind` alone means for each widget.
internal static class UiBindings
{
    public const string Text = "text", Tooltip = "tooltip", Value = "value", Min = "min", Max = "max", Source = "source",
                        Style = "style", Visible = "visible", Enabled = "enabled", Data = "data", Rows = "rows",
                        // Where in its parent Box it sits, 0..1 across and down: a point anchor there (a map's
                        // markers, issue #99). Kept inside the box: a point anchor places the widget proportionally.
                        X = "x", Y = "y";

    public static readonly string[] All = { Text, Tooltip, Value, Min, Max, Source, Style, Visible, Enabled, Data, Rows, X, Y };

    // What `bind` means on a widget of this type, or null when it has no main value.
    public static string? Primary(string widget) => widget switch
    {
        "label" or "button" => Text,
        "bar" => Value,
        "image" => Source,
        "stack" or "item_list" or "grid" => Rows,
        _ => null,
    };

    // Whether a widget of this type has the property at all.
    public static bool Applies(string widget, string target) => target switch
    {
        Text => widget is "label" or "button",
        Value or Min or Max => widget == "bar",
        Source => widget == "image",
        Rows => widget is "stack" or "item_list" or "grid" or "box",
        _ => true,
    };

    // Every (target, path) a node binds, `bind` first.
    public static IEnumerable<(string Target, string Path)> Of(UiNode node)
    {
        if (node.Bind.Length > 0 && Primary(node.Widget) is { } primary) yield return (primary, node.Bind);
        foreach (var (target, path) in node.Bindings)
        {
            string key = target.ToLowerInvariant();
            if (Array.IndexOf(All, key) >= 0) yield return (key, path);
        }
    }
}

// ---- refresh --------------------------------------------------------------------------------------

// A built widget and what changes it: conditions, bindings, and the bound children below it. Only nodes
// with something to read (or something below them that has) are walked.
internal sealed class BoundNode
{
    private BoundNode[] _children = Array.Empty<BoundNode>();
    private bool _enabled = true;

    public BoundNode(Widget widget, UiNode? node)
    {
        Widget = widget;
        Node = node;
        _enabled = node?.Enabled ?? true;
    }

    public Widget Widget { get; }
    public UiNode? Node { get; }

    public ICondition? VisibleIf, EnabledIf;
    public TextSlot? Text, Tooltip;
    public BindingReader? Value, Min, Max, Source, Style, Visible, Enabled, Data, X, Y;
    public RowsSlot? Rows;
    private string? _source, _style;

    public bool Dynamic => _children.Length > 0 || Rows != null || VisibleIf != null || EnabledIf != null || Text != null || Tooltip != null
                           || Value != null || Min != null || Max != null || Source != null || Style != null || Visible != null
                           || Enabled != null || Data != null || X != null || Y != null;

    public void SetChildren(List<BoundNode> children) => _children = children.ToArray();

    public void Refresh(object? source, in UiBindContext context)
    {
        if (Node != null)
        {
            bool visible = Node.Visible && Holds(VisibleIf, in context) && (Visible == null || Visible.Read(source).IsTrue);
            Widget.Visible = visible;
            if (!visible) return;   // nothing below a hidden widget is read
            Widget.Enabled = _enabled && Holds(EnabledIf, in context) && (Enabled == null || Enabled.Read(source).IsTrue);
        }

        if (Text != null && Widget is Label label) label.Text = Text.Resolve(source);
        if (Tooltip != null) Widget.TooltipText = Tooltip.Resolve(source);
        if (Widget is Bar bar)
        {
            if (Min != null) bar.Min = Min.Read(source).AsFloat;
            if (Max != null) bar.Max = Max.Read(source).AsFloat;
            if (Value != null) bar.Value = Value.Read(source).AsFloat;
        }
        if (Source != null && Widget is Image image)
        {
            // An asset path as the view-model holds it; compared by reference, so the same string costs nothing.
            string? path = Source.Read(source).AsString;
            if (!ReferenceEquals(path, _source)) { _source = path; image.Source = path; }
        }
        if (Style != null)
        {
            string? style = Style.Read(source).AsString;
            if (!ReferenceEquals(style, _style)) { _style = style; Widget.Style = style; }
        }
        if (Data != null) Widget.Data = Data.Read(source).Ref;
        if (X != null || Y != null)
        {
            var at = Widget.Anchors;
            float x = X != null ? Math.Clamp(X.Read(source).AsFloat, 0f, 1f) : at.MinX;
            float y = Y != null ? Math.Clamp(Y.Read(source).AsFloat, 0f, 1f) : at.MinY;
            Widget.Anchors = new Anchors(x, y, x, y);   // unchanged: no layout (Anchors compares)
        }

        Rows?.Refresh(source, in context);
        for (int i = 0; i < _children.Length; i++) _children[i].Refresh(source, in context);
    }

    private static bool Holds(ICondition? condition, in UiBindContext context) =>
        condition == null || context.World == null || Conditions.Test(condition, new ConditionContext(context.World, context.Subject), out _);
}

// A label's text: fixed or bound, a key or not, with placeholders filled from the view-model. Worked out
// again only when what it reads changed, or the language did.
internal sealed class TextSlot
{
    private readonly string _fixed;
    private readonly BindingReader? _reader;
    private readonly Arg[] _args;
    private readonly Localisation _text;
    private UiValue _last;
    private int _version = -1;
    private string _current = "";

    public TextSlot(string fixedText, BindingReader? reader, Dictionary<string, string>? args, Localisation text)
    {
        _fixed = fixedText;
        _reader = reader;
        _text = text;
        _args = args == null ? Array.Empty<Arg>() : args.Select(a => new Arg(a.Key, new BindingReader(a.Value))).ToArray();
    }

    public string Resolve(object? source)
    {
        bool changed = _version != _text.Version;
        if (_reader != null)
        {
            var value = _reader.Read(source);
            if (!value.Same(in _last)) { _last = value; changed = true; }
        }
        for (int i = 0; i < _args.Length; i++)
        {
            var value = _args[i].Reader.Read(source);
            if (!value.Same(in _args[i].Last)) { _args[i].Last = value; changed = true; }
        }
        if (!changed) return _current;

        _version = _text.Version;
        string template = _reader == null ? _fixed : _last.Kind == UiValueKind.Text ? (string)_last.Ref! : _last.ToString();
        if (_args.Length == 0)
            _current = _reader != null && _last.Kind != UiValueKind.Text ? template : _text.Text(template);
        else
        {
            var args = new SlotArgs(_args);
            _current = _text.Format(template, ref args);
        }
        return _current;
    }

    internal struct Arg
    {
        public Arg(string name, BindingReader reader)
        {
            Name = name;
            Reader = reader;
            Last = default;
        }

        public readonly string Name;
        public readonly BindingReader Reader;
        public UiValue Last;
    }

    private readonly struct SlotArgs : IPlaceholderValues
    {
        private readonly Arg[] _args;

        public SlotArgs(Arg[] args) { _args = args; }

        public bool TryAppend(ReadOnlySpan<char> name, StringBuilder builder)
        {
            for (int i = 0; i < _args.Length; i++)
                if (name.Equals(_args[i].Name, StringComparison.Ordinal))
                {
                    _args[i].Last.AppendTo(builder);
                    return true;
                }
            return false;
        }

        public bool TryGetNumber(string name, out double value)
        {
            for (int i = 0; i < _args.Length; i++)
                if (_args[i].Name == name && _args[i].Last.Kind is not (UiValueKind.None or UiValueKind.Text or UiValueKind.Object))
                {
                    value = _args[i].Last.Number;
                    return true;
                }
            value = 0;
            return false;
        }
    }
}

// A container whose children are a list's rows: one copy of its template per row, each bound to its row
// (and its Widget.Data the row). Rows are made when the list grows past what was ever made and kept
// aside when it shrinks, so a list that changes length within what it has been allocates nothing.
internal sealed class RowsSlot
{
    private readonly Container _host;
    private readonly BindingReader _reader;
    private readonly LayoutBuilder _builder;
    private readonly LayoutTree _tree;
    private readonly string _name;
    private readonly UiNode _template;
    private readonly RecordId _style;
    private readonly List<BoundNode> _rows = new(), _spare = new();

    public RowsSlot(Container host, BindingReader reader, LayoutBuilder builder, LayoutTree tree, string name, UiNode template, RecordId style)
    {
        _host = host;
        _reader = reader;
        _builder = builder;
        _tree = tree;
        _name = name;
        _template = template;
        _style = style;
    }

    public int Count => _rows.Count;

    public void Refresh(object? source, in UiBindContext context)
    {
        var list = _reader.Read(source).Ref as IList;
        int count = list?.Count ?? 0;
        while (_rows.Count < count)
        {
            BoundNode row;
            if (_spare.Count > 0)
            {
                row = _spare[^1];
                _spare.RemoveAt(_spare.Count - 1);
            }
            else row = _builder.BuildNode(_tree, _name, _template, _style);
            _host.Add(row.Widget);
            _rows.Add(row);
            // Room kept aside now, while rows are being made anyway, for all of them to be put aside
            // later: a list emptying (a HUD's messages ageing out) must not grow this then (#99).
            if (_spare.Capacity < _rows.Count + _spare.Count) _spare.Capacity = _rows.Count + _spare.Count;
        }
        while (_rows.Count > count)
        {
            var row = _rows[^1];
            _rows.RemoveAt(_rows.Count - 1);
            _host.Remove(row.Widget);
            _spare.Add(row);
        }
        for (int i = 0; i < count; i++)
        {
            object? item = list![i];
            _rows[i].Widget.Data = item;
            _rows[i].Refresh(item, in context);
        }
    }
}
