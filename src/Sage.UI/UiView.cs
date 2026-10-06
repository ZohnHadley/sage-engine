#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Sage.UI;

// What bindings and conditions are read against: the world a `visibleIf` asks about, who it asks
// about (the player looking at the screen) and, for a screen between two, the other one — the corpse
// being looted, the NPC being asked (issue #98), a condition's `other`. No world: conditions hold.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public readonly record struct UiBindContext(World? World, Entity Subject = default, Entity Other = default);

// A ui_layout record built into widgets (UiScreens.BuildLayout, or a screen's View), with its bindings.
// Refresh reads every binding and condition and changes only what differs from last time, so once built
// a view that nothing changed in costs a walk and allocates nothing (test: BindingsAndConditionsRefreshWithoutAllocating).
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class UiView
{
    private readonly BoundNode _root;
    private readonly UiScopes _scopes = new();

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
    public void Refresh(object? source, in UiBindContext context)
    {
        _scopes.Clear();
        _root.Refresh(source, _scopes, in context);
    }
}

// ---- build ----------------------------------------------------------------------------------------

// Turns a layout record into widgets. What it builds is laid out by the widgets' own rules; this only
// sets properties and puts children where their parent says.
internal sealed class LayoutBuilder
{
    private readonly UiStyles _styles;
    private readonly Localisation _text;
    private readonly RecordStore? _records;

    // `records`: where the layouts a node includes are found (issue #347); none, and nothing is included.
    public LayoutBuilder(UiStyles styles, Localisation text, RecordStore? records = null)
    {
        _styles = styles;
        _text = text;
        _records = records;
    }

    public UiView Build(RecordId id, UiLayoutRecord layout)
    {
        var tree = new LayoutTree(layout, _records);
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
            Attach(host, child.Widget, node.Title.Length > 0 ? _text.Text(node.Title) : name);
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
        if (widget is Tabs tabs) StyleTabs(tabs, node);
        return bound;
    }

    // The tabs' buttons, made as its pages were attached: in its tab style, or in its own style without
    // drawing that style's box again round each tab (the header carries it, UiRenderPlan.OwnsBox).
    private void StyleTabs(Tabs tabs, UiNode node)
    {
        tabs.Header.Style = tabs.Style;
        tabs.TabStyle = node.TabStyle.IsEmpty ? tabs.Style : node.TabStyle.Id.ToString();
        var style = _styles.Get(tabs.TabStyle);
        for (int i = 0; i < tabs.PageCount; i++)
        {
            var tab = tabs.Tab(i);
            tab.TextScale = node.TextScale ?? style.TextScale;
            if (!node.TabStyle.IsEmpty) tab.Padding = style.Padding;
        }
        if (node.Selected is { } selected) tabs.Selected = selected;
    }

    // `title`: what a page of tabs is called on its tab.
    private static void Attach(Widget host, Widget child, string title)
    {
        switch (host)
        {
            case Tabs tabs: tabs.AddPage(title, child); break;
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
        w.FocusScope = n.FocusScope;
        w.FocusUp = NameOrNull(n.FocusUp);
        w.FocusDown = NameOrNull(n.FocusDown);
        w.FocusLeft = NameOrNull(n.FocusLeft);
        w.FocusRight = NameOrNull(n.FocusRight);
        if (n.Tooltip.Length > 0) w.TooltipText = _text.Text(n.Tooltip);
        w.Actions = n.Actions.Count > 0 ? n.Actions : null;

        switch (w)
        {
            case Label label:
                if (n.Text.Length > 0) label.Text = _text.Text(n.Text);
                label.TextScale = n.TextScale ?? style.TextScale;
                if (n.TextAlign is { } align) label.TextAlign = align;
                label.Font = style.Font;
                label.FontSize = style.FontSize;
                label.Wrap = n.Wrap;
                if (n.Overflow is { } overflow) label.Overflow = overflow;
                label.MaxWidth = n.MaxWidth;
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
            case Tabs tabs:
                tabs.Spacing = n.Spacing;
                break;
        }

        // The form widgets (issue #340): what is theirs beyond a label's or a bar's.
        switch (w)
        {
            case Slider slider:
                if (n.Step is { } step) slider.Step = step;
                break;
            case Checkbox checkbox:
                if (n.Checked is { } ticked) checkbox.Checked = ticked;
                break;
            case Dropdown dropdown:
                if (n.Options.Count > 0) dropdown.SetOptions(n.Options.Select(o => _text.Text(o)));
                dropdown.Selected = n.Selected ?? (dropdown.Options.Count > 0 ? 0 : -1);
                break;
            case TextBox field:
                if (n.Placeholder.Length > 0) field.Placeholder = _text.Text(n.Placeholder);
                if (n.MaxLength > 0) field.MaxLength = n.MaxLength;
                field.Multiline = n.Multiline;
                break;
        }
    }

    private static string? NameOrNull(string name) => name.Length > 0 ? name : null;

    private void Bind(BoundNode bound, UiNode node, LayoutTree tree, string name, RecordId style)
    {
        bound.VisibleIf = node.VisibleIf;
        bound.EnabledIf = node.EnabledIf;
        if (node.Scope.Length > 0) bound.Scope = new BindingReader(node.Scope);
        foreach (var (target, path) in UiBindings.Of(node))
        {
            var reader = new BindingReader(path);
            switch (target)
            {
                // What a text field holds is the player's, shown as it is: never a key, never formatted.
                case UiBindings.Text when bound.Widget is TextBox: bound.Content = reader; break;
                case UiBindings.Text: bound.Text = new TextSlot(node.Text, reader, node.Args, _text); break;
                case UiBindings.Checked: bound.Checked = reader; break;
                case UiBindings.Selected: bound.Selected = reader; break;
                case UiBindings.Options: bound.Options = reader; break;
                case UiBindings.Tooltip: bound.Tooltip = new TextSlot("", reader, null, _text); break;
                case UiBindings.Value: bound.Value = reader; break;
                case UiBindings.Min: bound.Min = reader; break;
                case UiBindings.Max: bound.Max = reader; break;
                case UiBindings.Source: bound.Source = reader; break;
                case UiBindings.Style: bound.Style = reader; break;
                case UiBindings.Visible: bound.Visible = reader; break;
                case UiBindings.Enabled: bound.Enabled = reader; break;
                case UiBindings.Data: bound.Data = reader; break;
                case UiBindings.Columns: bound.Columns = reader; break;
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
        if (bound.Text == null && node.Args.Count > 0 && bound.Widget is Label and not TextBox)
            bound.Text = new TextSlot(node.Text, null, node.Args, _text);
        bound.WriteBack();
    }
}

// A layout's nodes by parent, in order: as written, then by `order`. A node that includes another
// layout (`include`, issue #347) has that layout's nodes inside it, before its own children, named
// `node/inner` (and so on down, for an include inside an include) and with its `params` put in for
// `{$name}` in their text and paths; a layout including itself, through however many others, is
// included once and stops there (UiContentChecks.Includes says so at load).
internal sealed class LayoutTree
{
    private readonly Dictionary<string, List<(string Name, UiNode Node, int Rank)>> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string Name, UiNode Node)>> _sorted = new(StringComparer.Ordinal);

    public LayoutTree(UiLayoutRecord layout, RecordStore? records = null)
    {
        Add(layout, "", "", null, records, new List<UiLayoutRecord> { layout }, rank: 1);
        foreach (var (parent, list) in _children)
            _sorted[parent] = list.Select((child, index) => (child, index)).OrderBy(c => c.child.Node.Order).ThenBy(c => c.child.Rank).ThenBy(c => c.index)
                                  .Select(c => (c.child.Name, c.child.Node)).ToList();
    }

    public IReadOnlyList<(string Name, UiNode Node)> ChildrenOf(string parent) =>
        _sorted.TryGetValue(parent, out var list) ? list : Array.Empty<(string, UiNode)>();

    // Every node, included ones too, by its full name.
    public IEnumerable<(string Name, UiNode Node)> All => _sorted.Values.SelectMany(l => l);

    // `prefix`: what an included layout's names start with ("" for the layout itself); `host`: the node
    // its top-level nodes go in; `values`: its params, or null for the layout itself.
    private void Add(UiLayoutRecord layout, string prefix, string host, Dictionary<string, string>? values, RecordStore? records,
                     List<UiLayoutRecord> including, int rank)
    {
        foreach (var (name, written) in layout.Nodes)
        {
            var node = written;
            if (prefix.Length > 0) node = Instantiate(written, prefix, values, layout);
            if (node.Include.IsEmpty == false && node.Widget.Length == 0)
            {
                node = ReferenceEquals(node, written) ? node.Copy() : node;
                node.Widget = "stack";   // an include names no widget: its nodes go in a column
            }
            string full = prefix + name;
            string parent = written.Parent.Length == 0 ? host : prefix + written.Parent;
            if (!_children.TryGetValue(parent, out var list)) _children[parent] = list = new();
            list.Add((full, node, written.Parent.Length == 0 ? rank : 1));

            if (node.Include.IsEmpty || records == null || !records.TryGet(node.Include.Id, out UiLayoutRecord included)) continue;
            if (including.Any(l => ReferenceEquals(l, included))) continue;   // a loop: UiContentChecks.Includes says so
            var merged = new Dictionary<string, string>(included.Params, StringComparer.Ordinal);
            foreach (var (key, value) in node.Params) merged[key] = value;
            including.Add(included);
            Add(included, full + "/", full, merged, records, including, rank: 0);
            including.RemoveAt(including.Count - 1);
        }
    }

    // A node of an included layout, as it is where it is included: its names prefixed, its params put in,
    // and a top-level node that names no style given the included layout's.
    private static UiNode Instantiate(UiNode written, string prefix, Dictionary<string, string>? values, UiLayoutRecord layout)
    {
        var node = written.Copy();
        node.Parent = written.Parent.Length == 0 ? "" : prefix + written.Parent;
        if (written.Parent.Length == 0 && node.Style.IsEmpty) node.Style = layout.Style;
        string Named(string name) => name.Length == 0 ? name : prefix + name;
        node.FocusUp = Named(written.FocusUp);
        node.FocusDown = Named(written.FocusDown);
        node.FocusLeft = Named(written.FocusLeft);
        node.FocusRight = Named(written.FocusRight);
        if (values == null || values.Count == 0) return node;
        string Put(string text) => UiParams.Fill(text, values);
        node.Text = Put(node.Text);
        node.Tooltip = Put(node.Tooltip);
        node.Title = Put(node.Title);
        node.Placeholder = Put(node.Placeholder);
        node.Bind = Put(node.Bind);
        node.Scope = Put(node.Scope);
        node.Options = node.Options.Select(Put).ToList();
        node.Args = node.Args.ToDictionary(a => a.Key, a => Put(a.Value));
        node.Bindings = node.Bindings.ToDictionary(b => b.Key, b => Put(b.Value));
        node.Params = node.Params.ToDictionary(p => p.Key, p => Put(p.Value));
        if (node.Actions.Count > 0) node.Actions = node.Actions.Select(a => UiParams.Fill(a, values)).ToList();
        return node;
    }
}

// `{$name}` in an included layout's text, paths and its actions' text (a var's name, a command): what
// the including node's `params` (or the layout's own, as defaults) say (issue #347). A name is letters,
// digits, '_' and '-', and never `root` or `parent`, so a command's `{$root.title}` is left for the press.
internal static class UiParams
{
    public static string Fill(string text, IReadOnlyDictionary<string, string> values)
    {
        if (text.IndexOf("{$", StringComparison.Ordinal) < 0) return text;
        var builder = new StringBuilder(text.Length);
        int i = 0;
        foreach (var (start, length, name) in Find(text))
        {
            builder.Append(text, i, start - i);
            if (values.TryGetValue(name, out var value)) builder.Append(value);
            else builder.Append(text, start, length);
            i = start + length;
        }
        builder.Append(text, i, text.Length - i);
        return builder.ToString();
    }

    // The `{$name}`s a text says.
    public static IEnumerable<string> Named(string text) => Find(text).Select(f => f.Name);

    // An action with `{$name}` in a text field: a copy with them put in; otherwise the action itself.
    public static IAction Fill(IAction action, IReadOnlyDictionary<string, string> values)
    {
        IAction? copy = null;
        foreach (var field in TextFields(action.GetType()))
        {
            if (field.GetValue(action) is not string text || text.IndexOf("{$", StringComparison.Ordinal) < 0) continue;
            copy ??= (IAction)Clone.Invoke(action, null)!;
            field.SetValue(copy, Fill(text, values));
        }
        return copy ?? action;
    }

    // The text an action's fields say, for UiContentChecks.Includes.
    public static IEnumerable<string> Texts(IAction action) =>
        TextFields(action.GetType()).Select(f => f.GetValue(action) as string).OfType<string>();

    private static readonly System.Reflection.MethodInfo Clone =
        typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    private static IEnumerable<System.Reflection.FieldInfo> TextFields(Type type) =>
        type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Where(f => f.FieldType == typeof(string));

    private static IEnumerable<(int Start, int Length, string Name)> Find(string text)
    {
        int i = 0;
        while ((i = text.IndexOf("{$", i, StringComparison.Ordinal)) >= 0)
        {
            int close = text.IndexOf('}', i + 2);
            if (close < 0) yield break;
            string name = text[(i + 2)..close];
            if (name.Length > 0 && name is not ("root" or "parent") && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-'))
            {
                yield return (i, close + 1 - i, name);
                i = close + 1;
            }
            else i += 2;
        }
    }
}

// What a node's `bind` and `bindings` may name, and which property `bind` alone means for each widget.
internal static class UiBindings
{
    public const string Text = "text", Tooltip = "tooltip", Value = "value", Min = "min", Max = "max", Source = "source",
                        Style = "style", Visible = "visible", Enabled = "enabled", Data = "data", Rows = "rows", Columns = "columns",
                        // The form widgets' (issue #340): a checkbox's tick, a dropdown's or tabs' choice (an
                        // index, or a dropdown's option text), a dropdown's options (a list, each shown as text).
                        Checked = "checked", Selected = "selected", Options = "options",
                        // Where in its parent Box it sits, 0..1 across and down: a point anchor there (a map's
                        // markers, issue #99). Kept inside the box: a point anchor places the widget proportionally.
                        X = "x", Y = "y";

    public static readonly string[] All = { Text, Tooltip, Value, Min, Max, Source, Style, Visible, Enabled, Data, Rows, Columns, X, Y, Checked, Selected, Options };

    // What `bind` means on a widget of this type, or null when it has no main value.
    public static string? Primary(string widget) => widget switch
    {
        "label" or "button" or "text_field" => Text,
        "bar" or "slider" => Value,
        "checkbox" => Checked,
        "image" => Source,
        "stack" or "item_list" or "grid" => Rows,
        "dropdown" or "tabs" => Selected,
        _ => null,
    };

    // The property a widget writes back to its binding when the player changes it, or null.
    public static string? Written(string widget) => widget switch
    {
        "slider" => Value,
        "checkbox" => Checked,
        "dropdown" or "tabs" => Selected,
        "text_field" => Text,
        _ => null,
    };

    // Whether a widget of this type has the property at all.
    public static bool Applies(string widget, string target) => target switch
    {
        Text => widget is "label" or "button" or "checkbox" or "text_field",
        Value or Min or Max => widget is "bar" or "slider",
        Checked => widget == "checkbox",
        Selected => widget is "dropdown" or "tabs",
        Options => widget == "dropdown",
        Source => widget == "image",
        Rows => widget is "stack" or "item_list" or "grid" or "box",
        Columns => widget == "grid",   // a grid as wide as its view-model says (an inventory's, issue #98)
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
    public BindingReader? Value, Min, Max, Source, Style, Visible, Enabled, Data, Columns, X, Y;
    public BindingReader? Checked, Selected, Options, Content;   // the form widgets' (issue #340)
    public RowsSlot? Rows;
    public BindingReader? Scope;    // what this node and everything inside it read from (issue #347)
    private string? _source, _style, _content;
    private IList? _options;
    private int _optionCount = -1;
    private bool _selectedIsText;

    public bool Dynamic => _children.Length > 0 || Rows != null || Scope != null || VisibleIf != null || EnabledIf != null || Text != null || Tooltip != null
                           || Value != null || Min != null || Max != null || Source != null || Style != null || Visible != null
                           || Enabled != null || Data != null || Columns != null || X != null || Y != null
                           || Checked != null || Selected != null || Options != null || Content != null;

    // Listens for the player changing the widget, when its value is bound, to write it back (issue #340).
    public void WriteBack()
    {
        bool bound = Widget switch
        {
            Slider => Value != null,
            Checkbox => Checked != null,
            Dropdown or Tabs => Selected != null,
            TextBox => Content != null,
            _ => false,
        };
        if (bound) Widget.ValueChanged += Written;
    }

    // Into what each binding last read from: the view-model, the row, or the scope its path named.
    private void Written(Widget widget)
    {
        switch (widget)
        {
            case Slider slider: Value!.Write(UiValue.Double(slider.Value)); break;
            case Checkbox checkbox: Checked!.Write(UiValue.Flag(checkbox.Checked)); break;
            case Dropdown dropdown:
                Selected!.Write(_selectedIsText ? UiValue.Text(dropdown.SelectedOption) : UiValue.Integer(dropdown.Selected));
                break;
            case Tabs tabs: Selected!.Write(UiValue.Integer(tabs.Selected)); break;
            case TextBox field:
                _content = field.Text;   // what is written is what will be read back: no change then
                Content!.Write(UiValue.Text(field.Text));
                break;
        }
    }

    public void SetChildren(List<BoundNode> children) => _children = children.ToArray();

    public void Refresh(object? source, UiScopes scopes, in UiBindContext context)
    {
        // A `scope` (issue #347): this node and what is inside it read from there, the one around it
        // still reachable as `$parent.`; Data is the scope, as a row's is its row.
        if (Scope == null) RefreshIn(source, scopes, in context);
        else
        {
            var inner = Scope.Read(source, scopes).Ref;
            Widget.Data = inner;
            scopes.Push(source);
            RefreshIn(inner, scopes, in context);
            scopes.Pop();
        }
    }

    private void RefreshIn(object? source, UiScopes scopes, in UiBindContext context)
    {
        if (Node != null)
        {
            bool visible = Node.Visible && Holds(VisibleIf, in context) && (Visible == null || Visible.Read(source, scopes).IsTrue);
            // A page of tabs is shown by the tabs, not by its node or its conditions; it is read while
            // hidden, so a tab opens on what is current.
            if (Widget.Parent is not Tabs) Widget.Visible = visible;
            if (!visible) return;   // nothing below a hidden widget is read
            Widget.Enabled = _enabled && Holds(EnabledIf, in context) && (Enabled == null || Enabled.Read(source, scopes).IsTrue);
        }

        if (Text != null && Widget is Label label) label.Text = Text.Resolve(source, scopes);
        if (Tooltip != null) Widget.TooltipText = Tooltip.Resolve(source, scopes);
        if (Widget is Bar bar)
        {
            if (Min != null) bar.Min = Min.Read(source, scopes).AsFloat;
            if (Max != null) bar.Max = Max.Read(source, scopes).AsFloat;
            if (Value != null) bar.Value = Value.Read(source, scopes).AsFloat;
        }
        if (Source != null && Widget is Image image)
        {
            // An asset path as the view-model holds it; compared by reference, so the same string costs nothing.
            string? path = Source.Read(source, scopes).AsString;
            if (!ReferenceEquals(path, _source)) { _source = path; image.Source = path; }
        }
        if (Style != null)
        {
            string? style = Style.Read(source, scopes).AsString;
            if (!ReferenceEquals(style, _style)) { _style = style; Widget.Style = style; }
        }
        if (Checked != null && Widget is Checkbox checkbox) checkbox.Checked = Checked.Read(source, scopes).IsTrue;
        if (Widget is Dropdown dropdown)
        {
            if (Options != null)
            {
                // Worked out again when the list is another one or its length changed (not per frame).
                var list = Options.Read(source, scopes).Ref as IList;
                int count = list?.Count ?? 0;
                if (!ReferenceEquals(list, _options) || count != _optionCount)
                {
                    _options = list;
                    _optionCount = count;
                    dropdown.SetOptions(list == null ? Array.Empty<string>() : list.Cast<object?>().Select(o => o?.ToString() ?? "").ToArray());
                }
            }
            if (Selected != null)
            {
                var value = Selected.Read(source, scopes);
                _selectedIsText = value.Kind == UiValueKind.Text;
                dropdown.Selected = _selectedIsText ? IndexOf(dropdown.Options, value.AsString!) : value.Kind == UiValueKind.None ? -1 : (int)value.Number;
            }
        }
        if (Selected != null && Widget is Tabs tabs)
        {
            var value = Selected.Read(source, scopes);
            if (value.Kind != UiValueKind.None) tabs.Selected = (int)value.Number;
        }
        if (Content != null && Widget is TextBox field)
        {
            // Compared by reference: the string written back is the one read next frame, costing nothing.
            string text = Content.Read(source, scopes).AsString ?? "";
            if (!ReferenceEquals(text, _content)) { _content = text; field.Text = text; }
        }
        if (Data != null) Widget.Data = Data.Read(source, scopes).Ref;
        if (Columns != null && Widget is Grid grid) grid.Columns = (int)Columns.Read(source, scopes).AsFloat;   // Grid keeps at least 1
        if (X != null || Y != null)
        {
            var at = Widget.Anchors;
            float x = X != null ? Math.Clamp(X.Read(source, scopes).AsFloat, 0f, 1f) : at.MinX;
            float y = Y != null ? Math.Clamp(Y.Read(source, scopes).AsFloat, 0f, 1f) : at.MinY;
            Widget.Anchors = new Anchors(x, y, x, y);   // unchanged: no layout (Anchors compares)
        }

        Rows?.Refresh(source, scopes, in context);
        for (int i = 0; i < _children.Length; i++) _children[i].Refresh(source, scopes, in context);
    }

    private static int IndexOf(IReadOnlyList<string> options, string option)
    {
        for (int i = 0; i < options.Count; i++)
            if (string.Equals(options[i], option, StringComparison.Ordinal)) return i;
        return -1;
    }

    private static bool Holds(ICondition? condition, in UiBindContext context) =>
        condition == null || context.World == null || Conditions.Test(condition, new ConditionContext(context.World, context.Subject, context.Other), out _);
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

    public string Resolve(object? source, UiScopes? scopes = null)
    {
        bool changed = _version != _text.Version;
        if (_reader != null)
        {
            var value = _reader.Read(source, scopes);
            if (!value.Same(in _last)) { _last = value; changed = true; }
        }
        for (int i = 0; i < _args.Length; i++)
        {
            var value = _args[i].Reader.Read(source, scopes);
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

    // The rows read their items, with `source` the scope around them (`$parent.`, issue #347).
    public void Refresh(object? source, UiScopes scopes, in UiBindContext context)
    {
        var list = _reader.Read(source, scopes).Ref as IList;
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
        if (count == 0) return;
        scopes.Push(source);
        for (int i = 0; i < count; i++)
        {
            object? item = list![i];
            _rows[i].Widget.Data = item;
            _rows[i].Refresh(item, scopes, in context);
        }
        scopes.Pop();
    }
}
