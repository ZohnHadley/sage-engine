#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sage.UI;

// The UI's records (docs/design/13 "As built (style and layout records)", issue #96): what a screen
// looks like and what it is made of, as content, so a designer or a mod re-lays out a screen, restyles
// it or translates it without C#. All three hot reload (UiScreens rebuilds what is open).
//
//   { "type": "ui_style", "id": "button", "base": "panel", "textColour": "#E0E0E0",
//     "states": { "focused": { "background": "#3050A0" }, "disabled": { "textColour": "#808080" } } }
//
//   { "type": "ui_layout", "id": "inventory", "style": "panel",
//     "nodes": {
//       "window": { "widget": "stack", "anchors": "center", "padding": 8, "spacing": 4 },
//       "title":  { "widget": "label", "parent": "window", "text": "@ui.inventory.title" },
//       "rows":   { "widget": "item_list", "parent": "window", "bind": "items" },
//       "row":    { "widget": "label", "parent": "rows", "bind": "name", "visibleIf": { "var": "debug" } } } }
//
//   { "type": "screen", "id": "inventory", "layout": "inventory", "viewModel": "inventory" }

// ---- ui_style -------------------------------------------------------------------------------------

// How a widget looks (drawing is #97's): colours, padding, a font, and the colours per state. `base`
// inherits every field from another style, `states` included, as any record's does (05 §3.5).
[Record("ui_style", Plugin = UiModule.Id)]
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class UiStyleRecord
{
    [AssetKind("font"), Property(Tooltip = "The bitmap font atlas text is drawn with; empty: the client's own font")]
    public AssetPath Font;

    [Property(Min = 0.1, Max = 10, Tooltip = "Text size, 1 = the font's own; laid out, so a label measures with it")]
    public float TextScale = 1f;

    [Property(Tooltip = "Space inside a widget of this style, unless its layout node says otherwise: 4, [h, v] or [left, top, right, bottom]")]
    public Thickness Padding;

    [Property(Min = 0, Unit = "units", Tooltip = "The border's width, in virtual units; 0 draws none")]
    public float BorderWidth;

    [JsonConverter(typeof(ColourJsonConverter)), Property(Tooltip = "Text colour")]
    public uint TextColour = ColourJsonConverter.Pack(255, 255, 255);

    [JsonConverter(typeof(ColourJsonConverter)), Property(Tooltip = "What fills the widget's rect behind its content; transparent by default")]
    public uint Background;

    [JsonConverter(typeof(ColourJsonConverter)), Property(Tooltip = "The border's colour")]
    public uint Border;

    [JsonConverter(typeof(ColourJsonConverter)), Property(Tooltip = "A bar's filled part")]
    public uint Fill = ColourJsonConverter.Pack(255, 255, 255);

    [JsonConverter(typeof(ColourJsonConverter)), Property(Tooltip = "What an image is multiplied by; white leaves it as drawn")]
    public uint Tint = ColourJsonConverter.Pack(255, 255, 255);

    [Property(Tooltip = "Colours that change with the widget's state: hover, focused, pressed, disabled")]
    public UiStyleStates States = new();
}

// The colours a state overrides; what it leaves out stays as the normal state has it.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class UiStyleStates
{
    public UiStyleState? Hover;
    public UiStyleState? Focused;
    public UiStyleState? Pressed;
    public UiStyleState? Disabled;
}

[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class UiStyleState
{
    [JsonConverter(typeof(OptionalColourJsonConverter))] public uint? TextColour;
    [JsonConverter(typeof(OptionalColourJsonConverter))] public uint? Background;
    [JsonConverter(typeof(OptionalColourJsonConverter))] public uint? Border;
    [JsonConverter(typeof(OptionalColourJsonConverter))] public uint? Fill;
    [JsonConverter(typeof(OptionalColourJsonConverter))] public uint? Tint;
}

// ---- ui_layout ------------------------------------------------------------------------------------

// A widget tree, as nodes by name. Flat rather than nested, the way Godot's scenes are: each node says
// its `parent` (none: the layout's own root, a Box filling what it is put in), so a patch can change one
// node by name — `"nodes": { "title": { "text": "@mymod.title" } }` merges into that node alone — or add
// one anywhere, without copying the tree. Siblings keep the order they are written in, then `order`.
[Record("ui_layout", Plugin = UiModule.Id)]
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class UiLayoutRecord
{
    [Property(Tooltip = "The style of every node that names none and has no ancestor that does")]
    public RecordRef<UiStyleRecord> Style;

    [Property(Tooltip = "The widgets, by name: each has a widget type and names its parent (none: the layout's root)")]
    public Dictionary<string, UiNode> Nodes = new();
}

// One widget of a layout. Fields a widget type does not have are errors at load (a label has no
// `columns`); what a node does not set keeps the widget's own default. Text starting with '@' is a
// localisation key (Localisation), resolved when the screen is built and again when the language changes.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class UiNode
{
    [JsonConverter(typeof(WidgetTypeJsonConverter)), Property(Tooltip = "What it is: box, stack, grid, label, image, button, item_list, bar or scroll")]
    public string Widget = "";

    [Property(Tooltip = "The node it is inside, by name; empty: the layout's root")]
    public string Parent = "";

    [Property(Tooltip = "Among its siblings, lower first; equal ones keep the order they are written in")]
    public int Order;

    [Property(Tooltip = "Its style; empty: its parent's")]
    public RecordRef<UiStyleRecord> Style;

    // ---- what it shows

    [Property(Tooltip = "A label's or button's text; '@ns.key' is a localisation key")]
    public string Text = "";

    [Property(Tooltip = "Values for the text's {placeholders}, by name: each a path into the view-model ({count} also picks the plural form)")]
    public Dictionary<string, string> Args = new();

    [Property(Tooltip = "What its tooltip says; '@ns.key' is a localisation key")]
    public string Tooltip = "";

    [AssetKind("texture"), Property(Tooltip = "An image's picture")]
    public AssetPath Source;

    [Property(Unit = "units", Tooltip = "An image's size when nothing stretches it")]
    public Vector2 NaturalSize;

    [Property(Tooltip = "An image keeps its proportions (default true)")]
    public bool? KeepAspect;

    [Property(Tooltip = "Text size, 1 = the font's; empty: its style's")]
    public float? TextScale;

    [Property(Tooltip = "Where text sits in a bigger label: Start, Center, End")]
    public Align? TextAlign;

    // ---- where it goes

    [Property(Unit = "units", Tooltip = "Never smaller than this")]
    public Vector2 MinSize;

    [Property(Tooltip = "Space around it: 4, [h, v] or [left, top, right, bottom]")]
    public Thickness Margin;

    [Property(Tooltip = "Space inside it; empty: its style's")]
    public Thickness? Padding;

    [Property(Tooltip = "How it sits across its slot in a stack, grid or scroll: Fill, Start, Center, End")]
    public Align? HAlign;

    [Property(Tooltip = "How it sits down its slot: Fill, Start, Center, End")]
    public Align? VAlign;

    [Property(Min = 0, Tooltip = "In a stack, its share of the room left over")]
    public float Expand;

    [Property(Tooltip = "In a box: a name (top_left, center, bottom_right, fill, top_wide...) or [minX, minY, maxX, maxY]")]
    public Anchors? Anchors;

    // ---- containers

    [Property(Tooltip = "A stack's or bar's direction: Row or Column")]
    public Orientation? Direction;

    [Property(Min = 0, Unit = "units", Tooltip = "Between a stack's children, or a grid's cells")]
    public float Spacing;

    [Property(Min = 0, Tooltip = "A grid's cells to a row (default 1)")]
    public int Columns;

    [Property(Tooltip = "A grid's cells are all the size of the biggest")]
    public bool Uniform;

    [Property(Tooltip = "A scroll scrolls across (default false)")]
    public bool? Horizontal;

    [Property(Tooltip = "A scroll scrolls down (default true)")]
    public bool? Vertical;

    // ---- a bar

    public float? Min;
    public float? Max;
    public float? Value;

    // ---- state

    [Property(Tooltip = "Hidden, it takes no room (default true)")]
    public bool Visible = true;

    [Property(Tooltip = "Disabled, it cannot be focused or pressed (default true)")]
    public bool Enabled = true;

    [Property(Tooltip = "Takes focus; buttons and list items do by default")]
    public bool? Focusable;

    [Property(Tooltip = "Tab order: lower first, then tree order")]
    public int TabIndex;

    [Property(Tooltip = "Shown only while this holds, asked about the screen's subject: { \"var\": \"alarm\", \"eq\": 1 }")]
    public ICondition? VisibleIf;

    [Property(Tooltip = "Enabled only while this holds")]
    public ICondition? EnabledIf;

    // ---- bindings

    [Property(Tooltip = "A path into the view-model for its main value: a label's text, a bar's value, an image's source, a list's rows")]
    public string Bind = "";

    [Property(Tooltip = "Paths for other properties, by property: text, value, min, max, source, tooltip, style, visible, enabled, rows")]
    public Dictionary<string, string> Bindings = new();
}

// ---- screen ---------------------------------------------------------------------------------------

// A layout bound to a view-model: what UiScreens.Open makes. The view-model is a registered entry of the
// `view_model` vocabulary ([ViewModel("inventory")]), made new for each screen opened.
[Record("screen", Plugin = UiModule.Id)]
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class ScreenRecord
{
    [Property(Tooltip = "The layout it shows")]
    public RecordRef<UiLayoutRecord> Layout;

    [VocabularyRef(UiScreens.ViewModelVocabulary), Property(Tooltip = "The view-model its bindings read, by id; empty: none, and the layout may bind nothing")]
    public string ViewModel = "";
}

// ---- JSON -----------------------------------------------------------------------------------------

// A widget type by name (WidgetTypes.Names): an unknown one is an error at its line, with the nearest.
[SchemaShape("""
    {
      "description": "A widget type.",
      "type": "string",
      "enum": ["box", "stack", "grid", "label", "image", "button", "item_list", "bar", "scroll"]
    }
    """)]
internal sealed class WidgetTypeJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException($"a widget type is a string: one of {string.Join(", ", WidgetTypes.Names)}");
        string name = reader.GetString() ?? "";
        foreach (string known in WidgetTypes.Names)
            if (string.Equals(known, name, StringComparison.Ordinal)) return known;
        throw new JsonException($"no widget type '{name}'" + Spelling.Suggest(name, WidgetTypes.Names) + $" (there are: {string.Join(", ", WidgetTypes.Names)})");
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

// A colour a style state may leave out (ColourJsonConverter's forms, or null).
[SchemaShape("""
    {
      "description": "A colour: \"#RRGGBB\" or \"#RRGGBBAA\" in hex, or [r, g, b] / [r, g, b, a] as whole numbers 0-255.",
      "anyOf": [
        { "type": "string", "pattern": "^\\s*#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})\\s*$" },
        { "type": "array", "items": { "type": "integer", "minimum": 0, "maximum": 255 }, "minItems": 3, "maxItems": 4 },
        { "type": "integer", "minimum": 0, "maximum": 4294967295 },
        { "type": "null" }
      ]
    }
    """)]
internal sealed class OptionalColourJsonConverter : JsonConverter<uint?>
{
    private static readonly ColourJsonConverter Colour = new();

    public override bool HandleNull => true;

    public override uint? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : Colour.Read(ref reader, typeof(uint), options);

    public override void Write(Utf8JsonWriter writer, uint? value, JsonSerializerOptions options)
    {
        if (value is { } packed) Colour.Write(writer, packed, options);
        else writer.WriteNullValue();
    }
}

// Thickness: 4 (all sides), [4] (the same), [h, v], or [left, top, right, bottom].
[SchemaShape("""
    {
      "description": "Space on each side, in virtual units: 4 (all), [h, v] or [left, top, right, bottom].",
      "anyOf": [
        { "type": "number" },
        { "type": "array", "items": { "type": "number" }, "minItems": 1, "maxItems": 4 }
      ]
    }
    """)]
internal sealed class ThicknessJsonConverter : JsonConverter<Thickness>
{
    public override Thickness Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return new Thickness(reader.GetSingle());
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("a thickness is a number, [h, v] or [left, top, right, bottom]");
        Span<float> v = stackalloc float[4];
        int n = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.Number || n == 4) throw new JsonException("a thickness is a number, [h, v] or [left, top, right, bottom]");
            v[n++] = reader.GetSingle();
        }
        return n switch
        {
            1 => new Thickness(v[0]),
            2 => new Thickness(v[0], v[1]),
            4 => new Thickness(v[0], v[1], v[2], v[3]),
            _ => throw new JsonException($"a thickness has 1, 2 or 4 numbers, not {n}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, Thickness value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Left);
        writer.WriteNumberValue(value.Top);
        writer.WriteNumberValue(value.Right);
        writer.WriteNumberValue(value.Bottom);
        writer.WriteEndArray();
    }
}

// Anchors: a preset's name ("center", "bottom_right", "fill", "top_wide", ignoring case and '_'), or
// [minX, minY, maxX, maxY] as fractions of the box.
[SchemaShape("""
    {
      "description": "Where a box places it: a preset, or [minX, minY, maxX, maxY] as fractions of the box (equal: keep its size; different: stretch).",
      "anyOf": [
        { "type": "string", "enum": ["top_left", "top", "top_right", "left", "center", "right", "bottom_left", "bottom", "bottom_right", "fill", "top_wide", "bottom_wide"] },
        { "type": "array", "items": { "type": "number", "minimum": 0, "maximum": 1 }, "minItems": 4, "maxItems": 4 }
      ]
    }
    """)]
internal sealed class AnchorsJsonConverter : JsonConverter<Anchors>
{
    internal static readonly (string Name, Anchors Value)[] Presets =
    {
        ("top_left", Anchors.TopLeft), ("top", Anchors.Top), ("top_right", Anchors.TopRight),
        ("left", Anchors.Left), ("center", Anchors.Center), ("right", Anchors.Right),
        ("bottom_left", Anchors.BottomLeft), ("bottom", Anchors.Bottom), ("bottom_right", Anchors.BottomRight),
        ("fill", Anchors.Fill), ("top_wide", Anchors.TopWide), ("bottom_wide", Anchors.BottomWide),
    };

    public override Anchors Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string name = reader.GetString() ?? "";
            string key = Vocabulary.Normalize(name);
            foreach (var (preset, value) in Presets)
                if (Vocabulary.Normalize(preset) == key) return value;
            var names = Array.ConvertAll(Presets, p => p.Name);
            throw new JsonException($"no anchors '{name}'" + Spelling.Suggest(name, names) + $" (there are: {string.Join(", ", names)}, or [minX, minY, maxX, maxY])");
        }
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("anchors are a name or [minX, minY, maxX, maxY]");
        Span<float> v = stackalloc float[4];
        int n = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.Number || n == 4) throw new JsonException("anchors are [minX, minY, maxX, maxY]");
            v[n++] = reader.GetSingle();
        }
        if (n != 4) throw new JsonException("anchors are [minX, minY, maxX, maxY]");
        return new Anchors(v[0], v[1], v[2], v[3]);
    }

    public override void Write(Utf8JsonWriter writer, Anchors value, JsonSerializerOptions options)
    {
        foreach (var (preset, anchors) in Presets)
            if (anchors == value) { writer.WriteStringValue(preset); return; }
        writer.WriteStartArray();
        writer.WriteNumberValue(value.MinX);
        writer.WriteNumberValue(value.MinY);
        writer.WriteNumberValue(value.MaxX);
        writer.WriteNumberValue(value.MaxY);
        writer.WriteEndArray();
    }
}
