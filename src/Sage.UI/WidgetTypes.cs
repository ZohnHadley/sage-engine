#nullable enable
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Sage.UI;

// The built-in widgets by type name — what a ui_layout record will say (#96) and what Widget.TypeName
// returns. The tooltip is not here: a root has exactly one.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public static class WidgetTypes
{
    public static IReadOnlyList<string> Names { get; } = new[]
    {
        "box", "stack", "grid", "label", "image", "button", "item_list", "bar", "scroll",
        "slider", "checkbox", "dropdown", "text_field", "tabs", "view",
    };

    // A new widget of that type, or null for a name that is not one.
    public static Widget? Create(string typeName) => typeName switch
    {
        "box" => new Box(),
        "stack" => new Stack(),
        "grid" => new Grid(),
        "label" => new Label(),
        "image" => new Image(),
        "button" => new Button(),
        "item_list" => new ItemList(),
        "bar" => new Bar(),
        "scroll" => new Scroll(),
        "slider" => new Slider(),
        "checkbox" => new Checkbox(),
        "dropdown" => new Dropdown(),
        "text_field" => new TextBox(),
        "tabs" => new Tabs(),
        "view" => new View(),
        _ => null,
    };
}
