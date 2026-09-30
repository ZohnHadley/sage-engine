#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Sage.UI;

// What a widget looks like right now, for the renderer (#97): its style, by the id in Widget.Style
// (a ui_style record, with `base` already applied by the record store), in the state it is in. Colours
// are packed as ColourJsonConverter packs them (red in the low byte, as MonoGame's Color.PackedValue).
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public enum UiState { Normal, Hover, Focused, Pressed, Disabled }

[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public readonly record struct UiStyleColours(uint Text, uint Background, uint Border, uint Fill, uint Tint);

// A ui_style record resolved: each state's colours worked out once, when content loads.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class UiStyle
{
    private readonly UiStyleColours[] _colours = new UiStyleColours[5];

    internal UiStyle(RecordId id, UiStyleRecord record)
    {
        Id = id;
        Font = record.Font;
        TextScale = record.TextScale;
        Padding = record.Padding;
        BorderWidth = record.BorderWidth;
        var normal = new UiStyleColours(record.TextColour, record.Background, record.Border, record.Fill, record.Tint);
        _colours[(int)UiState.Normal] = normal;
        _colours[(int)UiState.Hover] = Over(normal, record.States.Hover);
        _colours[(int)UiState.Focused] = Over(normal, record.States.Focused);
        _colours[(int)UiState.Pressed] = Over(normal, record.States.Pressed);
        _colours[(int)UiState.Disabled] = Over(normal, record.States.Disabled);
    }

    public RecordId Id { get; }
    public AssetPath Font { get; }
    public float TextScale { get; }
    public Thickness Padding { get; }
    public float BorderWidth { get; }

    public UiStyleColours Colours(UiState state) => _colours[(int)state];

    private static UiStyleColours Over(UiStyleColours normal, UiStyleState? state) => state == null ? normal : new UiStyleColours(
        state.TextColour ?? normal.Text, state.Background ?? normal.Background, state.Border ?? normal.Border,
        state.Fill ?? normal.Fill, state.Tint ?? normal.Tint);
}

// Every ui_style, by id. Rebuilt when content (re)loads; Version moves then.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class UiStyles
{
    private readonly Dictionary<string, UiStyle> _byText = new(StringComparer.Ordinal);
    private readonly Dictionary<RecordId, UiStyle> _byId = new();
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    // White text on nothing: what a widget with no style, or a style that does not exist, looks like.
    public static UiStyle Default { get; } = new(default, new UiStyleRecord());

    public int Version { get; private set; }

    public int Count => _byId.Count;

    // A style by the id a widget carries ("ns:name", as the layout builder writes it). Null or empty is
    // the default; one that does not exist is the default too, said once. Allocates nothing.
    public UiStyle Get(string? id)
    {
        if (string.IsNullOrEmpty(id)) return Default;
        if (_byText.TryGetValue(id, out var style)) return style;
        if (_warned.Add(id)) Log.Warn(LogCat.UI, $"no ui_style '{id}'; drawn in the default style");
        return Default;
    }

    public UiStyle Get(RecordId id) => id.IsEmpty ? Default : _byId.TryGetValue(id, out var style) ? style : Get(id.ToString());

    public bool TryGet(RecordId id, [NotNullWhen(true)] out UiStyle? style) => _byId.TryGetValue(id, out style);

    // The state a widget is drawn in: disabled, else pressed (the renderer knows; the UI does not track
    // a held button), else focused, else hovered.
    public static UiState StateOf(Widget widget, bool pressed = false) =>
        !widget.IsEnabled ? UiState.Disabled
        : pressed ? UiState.Pressed
        : widget.IsFocused ? UiState.Focused
        : widget.IsHovered ? UiState.Hover
        : UiState.Normal;

    // The colours `widget` is drawn with now.
    public UiStyleColours ColoursOf(Widget widget, bool pressed = false) => Get(widget.Style).Colours(StateOf(widget, pressed));

    internal void Rebuild(RecordStore records)
    {
        _byText.Clear();
        _byId.Clear();
        _warned.Clear();
        foreach (var id in records.Ids("ui_style"))
            if (records.TryGet(id, out UiStyleRecord record))
            {
                var style = new UiStyle(id, record);
                _byId[id] = style;
                _byText[id.ToString()] = style;
            }
        Version++;
    }
}
