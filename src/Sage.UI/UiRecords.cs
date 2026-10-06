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
    [AssetKind("font"), Property(Tooltip = "The font text is drawn with: a .ttf or .otf, rasterised at run time, or a .png grid atlas like the engine's; empty: the engine's own font")]
    public AssetPath Font;

    [Property(Min = 0, Max = 200, Unit = "units", Tooltip = "The font's size (its em) in virtual units at textScale 1; 0: 9, the engine font's line")]
    public float FontSize;

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

    [JsonConverter(typeof(ColourJsonConverter)), Property(Tooltip = "A bar's or slider's filled part, a checkbox's tick")]
    public uint Fill = ColourJsonConverter.Pack(255, 255, 255);

    [JsonConverter(typeof(ColourJsonConverter)), Property(Tooltip = "What an image is multiplied by; white leaves it as drawn")]
    public uint Tint = ColourJsonConverter.Pack(255, 255, 255);

    [AssetKind("texture"), Property(Tooltip = "A picture drawn behind the widget's content, over its background and multiplied by its tint: a window's frame, a button's plate (#97)")]
    public AssetPath Image;

    [Property(Tooltip = "Nine-slice insets in texture pixels — the corners that keep their size while the middle stretches — for the style's image and an image widget's picture; 0: stretched whole. 4, [h, v] or [left, top, right, bottom]")]
    public Thickness Slice;

    [Property(Tooltip = "Colours that change with the widget's state: hover, focused, pressed, disabled, selected")]
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

    // A ticked checkbox, the open tab (Widget.IsSelected), when it is not focused, hovered or pressed.
    public UiStyleState? Selected;
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
    [JsonConverter(typeof(WidgetTypeJsonConverter)), Property(Tooltip = "What it is: box, stack, grid, label, image, button, item_list, bar, scroll, slider, checkbox, dropdown, text_field or tabs")]
    public string Widget = "";

    [Property(Tooltip = "The node it is inside, by name; empty: the layout's root")]
    public string Parent = "";

    [Property(Tooltip = "Among its siblings, lower first; equal ones keep the order they are written in")]
    public int Order;

    [Property(Tooltip = "Its style; empty: its parent's")]
    public RecordRef<UiStyleRecord> Style;

    // ---- what it shows

    [Property(Tooltip = "A label's, button's or checkbox's text, or what a text field starts with; '@ns.key' is a localisation key")]
    public string Text = "";

    [Property(Tooltip = "Values for the text's {placeholders}, by name: each a path into the view-model ({count} also picks the plural form)")]
    public Dictionary<string, string> Args = new();

    [Property(Tooltip = "What its tooltip says; '@ns.key' is a localisation key")]
    public string Tooltip = "";

    [AssetKind("texture"), Property(Tooltip = "An image's picture")]
    public AssetPath Source;

    [AssetKind("texture"), Property(Tooltip = "A button's picture, drawn across it under its text (an item over its squares)")]
    public AssetPath Icon;

    [Property(Unit = "units", Tooltip = "An image's size when nothing stretches it")]
    public Vector2 NaturalSize;

    [Property(Tooltip = "An image keeps its proportions (default true)")]
    public bool? KeepAspect;

    [Property(Tooltip = "Text size, 1 = the font's; empty: its style's")]
    public float? TextScale;

    [Property(Tooltip = "Where text sits in a bigger label: Start, Center, End")]
    public Align? TextAlign;

    [Property(Tooltip = "A label's text breaks at spaces to fit its width (and maxWidth): a paragraph")]
    public bool Wrap;

    [Property(Tooltip = "What a label's text that still does not fit does: Visible (runs past), Clip, Ellipsis (ends in '…')")]
    public TextOverflow? Overflow;

    [Property(Min = 0, Unit = "units", Tooltip = "A label is never wider than this; 0: no limit but its room")]
    public float MaxWidth;

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

    [Property(Min = 0, Max = 64, Tooltip = "In a grid, how many cells across it takes; 0 or 1: one")]
    public int ColumnSpan;

    [Property(Min = 0, Max = 64, Tooltip = "In a grid, how many cells down it takes; 0 or 1: one")]
    public int RowSpan;

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

    [Property(Tooltip = "A box cuts off what is inside it at its edges (a map's picture, zoomed in and panned)")]
    public bool Clip;

    [Property(Tooltip = "A scroll scrolls across (default false)")]
    public bool? Horizontal;

    [Property(Tooltip = "A scroll scrolls down (default true)")]
    public bool? Vertical;

    // ---- a bar or a slider

    public float? Min;
    public float? Max;
    public float? Value;

    // ---- form widgets (issue #340)

    [Property(Min = 0, Tooltip = "A slider's step: the values it snaps to from min, and what a press moves it; empty: a twentieth of the range, unsnapped")]
    public float? Step;

    [Property(Tooltip = "A checkbox starts ticked")]
    public bool? Checked;

    [Property(Tooltip = "A dropdown's options, in order; '@ns.key' is a localisation key")]
    public List<string> Options = new();

    [Property(Min = -1, Tooltip = "The dropdown option or tab shown at first, from 0")]
    public int? Selected;

    [Property(Tooltip = "What an empty text field shows, faded; '@ns.key' is a localisation key")]
    public string Placeholder = "";

    [Property(Min = 0, Tooltip = "The most characters a text field takes; 0: 256")]
    public int MaxLength;

    [Property(Tooltip = "A text field takes Enter as a new line")]
    public bool Multiline;

    [Property(Tooltip = "Its tab's title, when it is a page of tabs; '@ns.key' is a localisation key")]
    public string Title = "";

    [Property(Tooltip = "The style of a tabs' tab buttons; the open one is drawn in its `selected` state")]
    public RecordRef<UiStyleRecord> TabStyle;

    // ---- state

    [Property(Tooltip = "Hidden, it takes no room (default true)")]
    public bool Visible = true;

    [Property(Tooltip = "Disabled, it cannot be focused or pressed (default true)")]
    public bool Enabled = true;

    [Property(Tooltip = "Takes focus; buttons and list items do by default")]
    public bool? Focusable;

    [Property(Tooltip = "The pointer can drag it and drop it on something else; a click on it then activates on release")]
    public bool Draggable;

    [Property(Tooltip = "Tab order: lower first, then tree order")]
    public int TabIndex;

    [Property(Tooltip = "Traps focus while it shows: the D-pad, Tab and the pointer reach nothing outside it, and focus goes back where it was when it hides (a confirm prompt)")]
    public bool FocusScope;

    [Property(Tooltip = "The node Up goes to from here, by name; empty: the nearest one above")]
    public string FocusUp = "";

    [Property(Tooltip = "The node Down goes to from here, by name; empty: the nearest one below")]
    public string FocusDown = "";

    [Property(Tooltip = "The node Left goes to from here, by name; empty: the nearest one to the left")]
    public string FocusLeft = "";

    [Property(Tooltip = "The node Right goes to from here, by name; empty: the nearest one to the right")]
    public string FocusRight = "";

    [Property(Tooltip = "Shown only while this holds, asked about the screen's subject: { \"var\": \"alarm\", \"eq\": 1 }")]
    public ICondition? VisibleIf;

    [Property(Tooltip = "Enabled only while this holds")]
    public ICondition? EnabledIf;

    // ---- what it does (issue #344)

    [Property(Tooltip = "Done when it is pressed, before the view-model hears of it: actions by name, with the screen's subject and other " +
                        "({ \"open_screen\": \"rpg:shop\" }, { \"close_screen\": {} })")]
    public List<IAction> Actions = new();

    // ---- bindings

    [Property(Tooltip = "A path into the view-model for its main value: a label's text, a bar's value, an image's source, a list's rows; a slider's value, a checkbox's checked, a dropdown's or tabs' selected and a text field's text, which the player's changes are written back to")]
    public string Bind = "";

    [Property(Tooltip = "Paths for other properties, by property: text, value, min, max, source, tooltip, style, visible, enabled, rows, checked, selected, options, icon, iconTurned, columnSpan, rowSpan")]
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

    [Property(Category = "Sound", Tooltip = "The ui_sounds this screen uses where it names a sound; each one it leaves empty is the game's default (sage:default_ui_sounds)")]
    public RecordRef<UiSoundsRecord> Sounds;

    [Property(Tooltip = "The world's time stands still while it is open (a pause menu), and runs again when the last such screen closes")]
    public bool Pauses;
}

// What the screens sound like (issue #330): the one noise per screen action, as data. A game patches
// `sage:default_ui_sounds` (engine_content/data/ui.json, every field empty = silent) and a `screen`
// may name a set of its own with `sounds`. Raised as `SoundRequested` for the client's audio system,
// which plays them on whatever bus the `sound` record names (`Ui`), so a headless world raises them
// and nobody hears.
[Record("ui_sounds", Plugin = UiModule.Id)]
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class UiSoundsRecord
{
    public static readonly RecordId DefaultId = new(ComponentSchema.EngineNamespace, "default_ui_sounds");

    [Property(Tooltip = "Focus moved by the arrow keys, the D-pad or Tab (`ui_move`)")]
    public RecordRef<SoundRecord> Move;
    [Property(Tooltip = "A widget was confirmed or clicked (`ui_select`)")]
    public RecordRef<SoundRecord> Select;
    [Property(Tooltip = "A window opened")]
    public RecordRef<SoundRecord> Open;
    [Property(Tooltip = "A window closed: Back, a click outside, or the game closing it")]
    public RecordRef<SoundRecord> Close;
}

internal enum UiSound { Move, Select, Open, Close }

// Finds the sound for a screen action and raises it (issue #330): the screen's own set first, field by
// field, then the game's default.
internal static class UiSounds
{
    public static void Raise(World? world, UiScreens? screens, UiScreen? screen, UiSound action)
    {
        if (world == null || screens == null) return;
        var records = screens.Records;
        var sound = Pick(screen?.Record.Sounds.Id ?? default, records, action);
        if (sound.IsEmpty) sound = Pick(UiSoundsRecord.DefaultId, records, action);
        if (sound.IsEmpty) return;
        world.Events.Send(new SoundRequested(sound, default, System.Numerics.Vector3.Zero, false, 0f));
    }

    private static RecordId Pick(RecordId set, RecordStore records, UiSound action)
    {
        if (set.IsEmpty || !records.TryGet(set, out UiSoundsRecord sounds)) return default;
        return action switch
        {
            UiSound.Move => sounds.Move.Id,
            UiSound.Select => sounds.Select.Id,
            UiSound.Open => sounds.Open.Id,
            _ => sounds.Close.Id,
        };
    }
}

// ---- JSON -----------------------------------------------------------------------------------------

// A widget type by name (WidgetTypes.Names): an unknown one is an error at its line, with the nearest.
[SchemaShape("""
    {
      "description": "A widget type.",
      "type": "string",
      "enum": ["box", "stack", "grid", "label", "image", "button", "item_list", "bar", "scroll", "slider", "checkbox", "dropdown", "text_field", "tabs"]
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
