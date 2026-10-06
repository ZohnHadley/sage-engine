#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;

namespace Sage.UI;

// ---- ui_style_set ---------------------------------------------------------------------------------

// Styles swapped for others while the set is in use (issue #351): a high-contrast look, a colour-blind
// palette, larger buttons. The `ui_style_set` cvar picks one (UiStyles.UseSet), live; layouts keep
// naming the styles they always did.
//
//   { "type": "ui_style_set", "id": "high_contrast", "label": "@rpg.options.high_contrast", "highContrast": true,
//     "swaps": [ { "from": "rpg:button", "to": "rpg:button_hc" }, { "from": "rpg:window", "to": "rpg:window_hc" } ] }
[Record("ui_style_set", Plugin = UiModule.Id)]
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class UiStyleSetRecord
{
    [Property(Tooltip = "What an options screen calls it: text or @key")]
    public string Label = "";

    [Property(Tooltip = "Promises text at least 7:1 against what is behind it in every layout while the set is in use; checked when content loads")]
    public bool HighContrast;

    [Property(Tooltip = "Each style drawn as another while the set is in use")]
    public List<UiStyleSwap> Swaps = new();
}

[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class UiStyleSwap
{
    [Property(Tooltip = "The style layouts name")]
    public RecordRef<UiStyleRecord> From;

    [Property(Tooltip = "The style drawn in its place")]
    public RecordRef<UiStyleRecord> To;
}

// ---- Contrast ---------------------------------------------------------------------------------------

// WCAG 2's contrast ratio between two colours (packed as ColourJsonConverter packs them, red in the low
// byte): 1 (the same) to 21 (black on white). 4.5 is the bar for body text (AA), 7 for enhanced (AAA).
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public static class UiContrast
{
    public const double Minimum = 4.5, HighContrast = 7.0;

    public static double Ratio(uint a, uint b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    // Relative luminance of a colour's red, green and blue (its alpha ignored).
    public static double Luminance(uint colour) =>
        0.2126 * Channel(colour & 0xFF) + 0.7152 * Channel((colour >> 8) & 0xFF) + 0.0722 * Channel((colour >> 16) & 0xFF);

    // `top` drawn over `bottom` by its alpha; the result has the two alphas combined.
    public static uint Over(uint top, uint bottom)
    {
        float a = (top >> 24) / 255f, b = (bottom >> 24) / 255f;
        float alpha = a + b * (1f - a);
        if (alpha <= 0f) return 0u;
        uint Mix(int shift)
        {
            float t = ((top >> shift) & 0xFF) * a, u = ((bottom >> shift) & 0xFF) * b * (1f - a);
            return (uint)Math.Clamp(MathF.Round((t + u) / alpha), 0f, 255f) << shift;
        }
        return Mix(0) | Mix(8) | Mix(16) | ((uint)MathF.Round(alpha * 255f) << 24);
    }

    private static double Channel(uint value)
    {
        double c = value / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}

// What the load checks for accessibility (issue #351), after the records are in, since it reads layouts
// and styles together: every node that shows text against what is behind it — its own background and
// its parents', drawn over each other — in each state the player reads it in (disabled excepted, which
// WCAG leaves out, and pressed, a flash while a button is held). One style on one backdrop is said once,
// at the first node that shows it, not at every button that uses it. Text over nothing opaque enough (a HUD over the world) cannot be judged and is
// passed. Below 4.5:1 is a warning (dev builds and `sage validate`); with a set marked highContrast in
// use, below 7:1 is an error at the set.
internal static class UiAccessibilityChecks
{
    private static readonly string[] TextWidgets = { "label", "button", "checkbox", "dropdown", "text_field" };
    private static readonly string[] Focusable = { "button", "checkbox", "dropdown", "text_field", "slider" };
    private static readonly UiState[] States = { UiState.Normal, UiState.Hover, UiState.Focused, UiState.Selected };

    // How opaque what is behind the text must be, all layers together, for the check to say anything.
    private const float Judged = 0.9f;

    public static void Contrast(RecordStore records)
    {
        var styles = Styles(records, null);
        foreach (var problem in Once(Problems(records, styles, UiContrast.Minimum)))
            Log.Warn(LogCat.Records, $"{problem.Where}: {problem.Message}: hard to read; aim for {UiContrast.Minimum.ToString(CultureInfo.InvariantCulture)}:1 " +
                                     "(WCAG AA), or give a high-contrast ui_style_set a style to swap in");
        foreach (var id in records.Ids("ui_style_set"))
        {
            if (!records.TryGet(id, out UiStyleSetRecord set) || !set.HighContrast) continue;
            foreach (var problem in Once(Problems(records, Styles(records, set), UiContrast.HighContrast)))
                Log.Error(LogCat.Records, $"{records.Where("ui_style_set", id, "HighContrast")}: ui_style_set {id} is high contrast, and with it {problem.Message} " +
                                          $"({problem.Where}); it needs {UiContrast.HighContrast.ToString(CultureInfo.InvariantCulture)}:1");
        }
    }

    // A style set's own fields: swaps between styles that exist, each style swapped once.
    public static void StyleSet(UiStyleSetRecord set, RecordCheck check)
    {
        var seen = new HashSet<RecordId>();
        for (int i = 0; i < set.Swaps.Count; i++)
        {
            var swap = set.Swaps[i];
            if (swap.From.IsEmpty || swap.To.IsEmpty) check.Error($"Swaps[{i}]", "a swap needs a `from` style and a `to` style");
            else if (!seen.Add(swap.From.Id)) check.Error($"Swaps[{i}].From", $"{swap.From.Id} is swapped twice; one swap a style");
        }
    }

    private static Dictionary<RecordId, UiStyle> Styles(RecordStore records, UiStyleSetRecord? set)
    {
        var all = new Dictionary<RecordId, UiStyle>();
        foreach (var id in records.Ids("ui_style"))
            if (records.TryGet(id, out UiStyleRecord record)) all[id] = new UiStyle(id, record);
        if (set == null) return all;
        var swapped = new Dictionary<RecordId, UiStyle>(all);
        foreach (var swap in set.Swaps)
            if (!swap.From.IsEmpty && all.TryGetValue(swap.To.Id, out var to)) swapped[swap.From.Id] = to;
        return swapped;
    }

    private readonly record struct Problem(string Where, string Message, (RecordId Style, uint Text, uint Back) Key);

    private static IEnumerable<Problem> Once(IEnumerable<Problem> problems)
    {
        var said = new Dictionary<(RecordId, uint, uint), int>();
        var first = new List<Problem>();
        foreach (var problem in problems)
        {
            if (said.TryGetValue(problem.Key, out int n)) { said[problem.Key] = n + 1; continue; }
            said[problem.Key] = 1;
            first.Add(problem);
        }
        foreach (var problem in first)
        {
            int others = said[problem.Key] - 1;
            yield return others == 0 ? problem
                : problem with { Message = problem.Message + $" (and {others} more node(s) in that style)" };
        }
    }

    private static IEnumerable<Problem> Problems(RecordStore records, Dictionary<RecordId, UiStyle> styles, double minimum)
    {
        UiStyle StyleOf(RecordId id) => !id.IsEmpty && styles.TryGetValue(id, out var style) ? style : UiStyles.Default;
        foreach (var id in records.Ids("ui_layout"))
        {
            if (!records.TryGet(id, out UiLayoutRecord layout)) continue;
            var tree = new LayoutTree(layout);
            // Each node's background chain, from it out to the layout's own root (its style's background).
            var behind = new List<uint> { StyleOf(layout.Style.Id).Colours(UiState.Normal).Background };
            foreach (var problem in Walk(records, id, tree, "", layout.Style.Id, behind, StyleOf, minimum)) yield return problem;
        }
    }

    private static IEnumerable<Problem> Walk(RecordStore records, RecordId layoutId, LayoutTree tree, string parent, RecordId inherited,
                                             List<uint> behind, Func<RecordId, UiStyle> styleOf, double minimum)
    {
        foreach (var (name, node) in tree.ChildrenOf(parent))
        {
            var styleId = node.Style.IsEmpty ? inherited : node.Style.Id;
            var style = styleOf(styleId);
            // A style's box is drawn where it is applied, not on each node that inherits it (UiRenderPlan.OwnsBox).
            bool owns = styleId != inherited;
            // A node whose style is chosen by a binding is judged by the style it starts with.
            bool shows = Array.IndexOf(TextWidgets, node.Widget) >= 0 && (node.Text.Length > 0 || node.Bind.Length > 0 || node.Bindings.Keys.Any(k => string.Equals(k, UiBindings.Text, StringComparison.OrdinalIgnoreCase)));
            if (shows)
            {
                double worst = double.MaxValue;
                UiState at = UiState.Normal;
                uint text = 0, back = 0;
                foreach (var state in States)
                {
                    if (state != UiState.Normal && Array.IndexOf(Focusable, node.Widget) < 0) break;
                    var colours = style.Colours(state);
                    if (!Behind(behind, owns ? colours.Background : 0u, out uint backdrop)) continue;
                    uint drawn = UiContrast.Over(colours.Text, backdrop);
                    double ratio = UiContrast.Ratio(drawn, backdrop);
                    if (ratio < worst) { worst = ratio; at = state; text = colours.Text; back = backdrop; }
                }
                if (worst < minimum)
                    yield return new Problem(records.Where("ui_layout", layoutId, $"Nodes['{name}']"),
                        $"ui_layout {layoutId} node '{name}' ({styleId}) draws {Hex(text)} text on {Hex(back)}" +
                        (at == UiState.Normal ? "" : $" when {at.ToString().ToLowerInvariant()}") +
                        $", {worst.ToString("0.0", CultureInfo.InvariantCulture)}:1", (styleId, text, back));
            }
            behind.Add(owns ? style.Colours(UiState.Normal).Background : 0u);
            foreach (var problem in Walk(records, layoutId, tree, name, styleId, behind, styleOf, minimum)) yield return problem;
            behind.RemoveAt(behind.Count - 1);
        }
    }

    // What the text is drawn on: its own background over its parents', inside out; false when that is not
    // opaque enough to judge.
    private static bool Behind(List<uint> parents, uint own, out uint colour)
    {
        colour = own;
        for (int i = parents.Count - 1; i >= 0 && (colour >> 24) < 255; i--) colour = UiContrast.Over(colour, parents[i]);
        if ((colour >> 24) / 255f < Judged) return false;
        colour |= 0xFF000000u;   // nearly opaque: whatever shows through is taken to be its own colour
        return true;
    }

    private static string Hex(uint colour) =>
        $"#{colour & 0xFF:X2}{(colour >> 8) & 0xFF:X2}{(colour >> 16) & 0xFF:X2}";
}
