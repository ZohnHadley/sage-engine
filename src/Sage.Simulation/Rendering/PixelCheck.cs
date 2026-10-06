#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Sage.Simulation;

// Pixel checks of a drawn frame (issue #318): named regions of a frame read back from the GPU, and checks
// on their mean colours, declared in a small JSON file. The host's `r_pixelcheck <spec>` reads the back
// buffer at the end of a frame and runs these; CI runs it on a fixed test scene (tests/games/render-check)
// so that "shadows stopped drawing" or "fog stopped drawing" fails a build instead of waiting for a person
// to look. Everything here is headless, so the comparison is unit-tested (PixelCheckTests); only the
// readback is the host's.
//
//   {
//     "width": 640, "height": 360,                       // the frame size the regions were laid out for
//     "regions": [
//       { "name": "sky", "rect": [0.02, 0.04, 0.12, 0.12], "sees": "sky" },   // x, y, w, h: fractions, from the top left
//       { "name": "shadow", "rect": [0.45, 0.70, 0.10, 0.08], "sees": "shadow" }
//     ],
//     "checks": [
//       { "name": "the sky is blue", "region": "sky", "when": "shaders", "measure": "b-r", "min": 0.3 },
//       { "name": "the shadow is dark", "region": "shadow", "than": "lit", "max": -0.4 },
//       { "name": "the sky's colour", "region": "sky", "color": [0.5, 0.6, 0.9], "tolerance": 0.08 }
//     ]
//   }
//
// A check measures its region's mean colour (`measure`: luma, the default; r, g, b; chroma, the spread of
// the channels, 0 for a grey; b-r and r-b), or that minus the same measure of the region `than` names, and
// asks for it between `min` and `max`; or it asks for the mean colour within `tolerance` of `color` on
// every channel. `when` says which frames a check applies to: `always` (the default), `shaders` (drawn with
// the engine's compiled effects) or `noShaders` (a host built without them, which draws only clear colours).
// `sees` is what the region looks at in the scene; the host ignores it, and the headless test of the scene
// casts rays to prove each region sees what it says (RenderCheckSceneTests).
internal enum PixelCheckWhen
{
    Always,
    Shaders,
    NoShaders,
}

internal enum PixelMeasure
{
    Luma,
    R,
    G,
    B,
    Chroma,
    BlueMinusRed,
    RedMinusBlue,
}

internal enum PixelCheckOutcome
{
    Passed,
    Failed,
    Skipped,
}

// A rectangle of the frame, in fractions of its width and height from the top left.
internal readonly record struct PixelRegion(string Name, float X, float Y, float Width, float Height, string Sees);

internal sealed class PixelCheckRule
{
    public string Name = "";
    public string Region = "";
    public string? Than;
    public PixelMeasure Measure = PixelMeasure.Luma;
    public float? Min;
    public float? Max;
    public Vector3? Color;
    public float Tolerance = 0.05f;
    public PixelCheckWhen When = PixelCheckWhen.Always;
}

internal readonly record struct PixelCheckResult(string Name, PixelCheckOutcome Outcome, string Detail);

internal sealed class PixelCheckSpec
{
    public int Width;    // 0: any size
    public int Height;
    public readonly List<PixelRegion> Regions = new();
    public readonly List<PixelCheckRule> Checks = new();

    public bool TryRegion(string name, out PixelRegion region)
    {
        foreach (var r in Regions)
            if (string.Equals(r.Name, name, StringComparison.Ordinal)) { region = r; return true; }
        region = default;
        return false;
    }

    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Throws FormatException naming what is wrong: a spec with a mistake must not pass by checking less.
    public static PixelCheckSpec Parse(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json, Options); }
        catch (JsonException ex) { throw new FormatException($"not JSON: {ex.Message}"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("a pixel check spec is an object");
            var spec = new PixelCheckSpec();
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "width": spec.Width = PositiveInt(property.Value, "width"); break;
                    case "height": spec.Height = PositiveInt(property.Value, "height"); break;
                    case "regions":
                        foreach (var region in Array(property.Value, "regions")) spec.Regions.Add(ParseRegion(region));
                        break;
                    case "checks":
                        foreach (var check in Array(property.Value, "checks")) spec.Checks.Add(ParseCheck(check));
                        break;
                    default: throw new FormatException($"unknown key '{property.Name}' (width, height, regions, checks)");
                }
            }
            if ((spec.Width == 0) != (spec.Height == 0)) throw new FormatException("give both width and height, or neither");
            if (spec.Checks.Count == 0) throw new FormatException("no checks");

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var region in spec.Regions)
                if (!names.Add(region.Name)) throw new FormatException($"two regions are called '{region.Name}'");
            foreach (var check in spec.Checks)
            {
                if (!names.Contains(check.Region)) throw new FormatException($"check '{check.Name}': no region '{check.Region}'");
                if (check.Than != null && !names.Contains(check.Than)) throw new FormatException($"check '{check.Name}': no region '{check.Than}'");
            }
            return spec;
        }
    }

    private static PixelRegion ParseRegion(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new FormatException("a region is an object");
        string name = "", sees = "";
        float[]? rect = null;
        foreach (var p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name": name = Text(p.Value, "name"); break;
                case "sees": sees = Text(p.Value, "sees"); break;
                case "rect": rect = Numbers(p.Value, "rect", 4); break;
                default: throw new FormatException($"region: unknown key '{p.Name}' (name, rect, sees)");
            }
        }
        if (name.Length == 0) throw new FormatException("a region needs a name");
        if (rect == null) throw new FormatException($"region '{name}' needs a rect: [x, y, width, height]");
        float x = rect[0], y = rect[1], w = rect[2], h = rect[3];
        if (x < 0f || y < 0f || w <= 0f || h <= 0f || x + w > 1.0001f || y + h > 1.0001f)
            throw new FormatException($"region '{name}': rect [{x}, {y}, {w}, {h}] is not inside the frame (fractions, 0 to 1)");
        return new PixelRegion(name, x, y, w, h, sees);
    }

    private static PixelCheckRule ParseCheck(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new FormatException("a check is an object");
        var rule = new PixelCheckRule();
        foreach (var p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "name": rule.Name = Text(p.Value, "name"); break;
                case "region": rule.Region = Text(p.Value, "region"); break;
                case "than": rule.Than = Text(p.Value, "than"); break;
                case "measure": rule.Measure = ParseMeasure(Text(p.Value, "measure")); break;
                case "min": rule.Min = Number(p.Value, "min"); break;
                case "max": rule.Max = Number(p.Value, "max"); break;
                case "color":
                    var c = Numbers(p.Value, "color", 3);
                    rule.Color = new Vector3(c[0], c[1], c[2]);
                    break;
                case "tolerance": rule.Tolerance = Number(p.Value, "tolerance"); break;
                case "when": rule.When = ParseWhen(Text(p.Value, "when")); break;
                default: throw new FormatException($"check: unknown key '{p.Name}' (name, region, than, measure, min, max, color, tolerance, when)");
            }
        }
        if (rule.Region.Length == 0) throw new FormatException("a check needs a region");
        if (rule.Name.Length == 0) rule.Name = rule.Region;
        bool range = rule.Min.HasValue || rule.Max.HasValue;
        if (range == rule.Color.HasValue)
            throw new FormatException($"check '{rule.Name}': give min and/or max, or a color, not both and not neither");
        if (rule.Color.HasValue && rule.Than != null) throw new FormatException($"check '{rule.Name}': a color check has no 'than'");
        if (rule.Tolerance < 0f) throw new FormatException($"check '{rule.Name}': tolerance is below 0");
        return rule;
    }

    internal static PixelMeasure ParseMeasure(string s) => s switch
    {
        "luma" => PixelMeasure.Luma,
        "r" => PixelMeasure.R,
        "g" => PixelMeasure.G,
        "b" => PixelMeasure.B,
        "chroma" => PixelMeasure.Chroma,
        "b-r" => PixelMeasure.BlueMinusRed,
        "r-b" => PixelMeasure.RedMinusBlue,
        _ => throw new FormatException($"unknown measure '{s}' (luma, r, g, b, chroma, b-r, r-b)"),
    };

    private static PixelCheckWhen ParseWhen(string s) => s switch
    {
        "always" => PixelCheckWhen.Always,
        "shaders" => PixelCheckWhen.Shaders,
        "noShaders" => PixelCheckWhen.NoShaders,
        _ => throw new FormatException($"unknown 'when' '{s}' (always, shaders, noShaders)"),
    };

    private static JsonElement.ArrayEnumerator Array(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : throw new FormatException($"'{key}' is an array");

    private static string Text(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new FormatException($"'{key}' is a string");

    private static float Number(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Number ? e.GetSingle() : throw new FormatException($"'{key}' is a number");

    private static int PositiveInt(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int v) && v > 0 ? v : throw new FormatException($"'{key}' is a whole number above 0");

    private static float[] Numbers(JsonElement e, string key, int count)
    {
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != count) throw new FormatException($"'{key}' is {count} numbers");
        var values = new float[count];
        int i = 0;
        foreach (var v in e.EnumerateArray()) values[i++] = Number(v, key);
        return values;
    }
}

// A frame read back: RGBA, 8 bits a channel, rows from the top.
internal sealed class PixelFrame
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }

    public PixelFrame(int width, int height, byte[] rgba)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "a frame has pixels");
        if (rgba.Length < width * height * 4) throw new ArgumentException($"{rgba.Length} bytes is not {width}x{height} RGBA", nameof(rgba));
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    // The pixels a region covers: at least one, never outside the frame.
    public (int X0, int Y0, int X1, int Y1) Pixels(in PixelRegion region)
    {
        int x0 = Math.Clamp((int)MathF.Floor(region.X * Width), 0, Width - 1);
        int y0 = Math.Clamp((int)MathF.Floor(region.Y * Height), 0, Height - 1);
        int x1 = Math.Clamp((int)MathF.Ceiling((region.X + region.Width) * Width), x0 + 1, Width);
        int y1 = Math.Clamp((int)MathF.Ceiling((region.Y + region.Height) * Height), y0 + 1, Height);
        return (x0, y0, x1, y1);
    }

    // The region's mean colour, 0..1 a channel.
    public Vector3 Mean(in PixelRegion region)
    {
        var (x0, y0, x1, y1) = Pixels(region);
        double r = 0, g = 0, b = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * Width + x) * 4;
                r += Rgba[i];
                g += Rgba[i + 1];
                b += Rgba[i + 2];
            }
        double n = (double)(x1 - x0) * (y1 - y0) * 255.0;
        return new Vector3((float)(r / n), (float)(g / n), (float)(b / n));
    }
}

internal static class PixelCheck
{
    public static float Measure(PixelMeasure measure, Vector3 c) => measure switch
    {
        PixelMeasure.R => c.X,
        PixelMeasure.G => c.Y,
        PixelMeasure.B => c.Z,
        PixelMeasure.Chroma => MathF.Max(c.X, MathF.Max(c.Y, c.Z)) - MathF.Min(c.X, MathF.Min(c.Y, c.Z)),
        PixelMeasure.BlueMinusRed => c.Z - c.X,
        PixelMeasure.RedMinusBlue => c.X - c.Z,
        _ => c.X * 0.2126f + c.Y * 0.7152f + c.Z * 0.0722f,
    };

    // Runs every check of the spec on the frame. `shaders`: the frame was drawn with the engine's compiled
    // effects, so `shaders` checks run and `noShaders` ones are skipped (and the other way round). A frame
    // of another size than the spec's fails once, first: the regions were placed for that shape.
    public static List<PixelCheckResult> Run(PixelCheckSpec spec, PixelFrame frame, bool shaders)
    {
        var results = new List<PixelCheckResult>(spec.Checks.Count + 1);
        if (spec.Width > 0 && (frame.Width != spec.Width || frame.Height != spec.Height))
            results.Add(new PixelCheckResult("frame size", PixelCheckOutcome.Failed,
                $"the frame is {frame.Width}x{frame.Height}, the spec's regions are for {spec.Width}x{spec.Height} (set vid_width / vid_height)"));

        foreach (var check in spec.Checks)
        {
            if ((check.When == PixelCheckWhen.Shaders && !shaders) || (check.When == PixelCheckWhen.NoShaders && shaders))
            {
                results.Add(new PixelCheckResult(check.Name, PixelCheckOutcome.Skipped,
                    shaders ? "only without shaders" : "needs the compiled shaders"));
                continue;
            }
            spec.TryRegion(check.Region, out var region);
            var mean = frame.Mean(region);
            if (check.Color is { } want)
            {
                float off = MathF.Max(MathF.Abs(mean.X - want.X), MathF.Max(MathF.Abs(mean.Y - want.Y), MathF.Abs(mean.Z - want.Z)));
                bool ok = off <= check.Tolerance;
                results.Add(new PixelCheckResult(check.Name, ok ? PixelCheckOutcome.Passed : PixelCheckOutcome.Failed,
                    $"{check.Region} is {Format(mean)}, want {Format(want)} ± {F(check.Tolerance)} (off by {F(off)})"));
                continue;
            }

            float value = Measure(check.Measure, mean);
            string what = $"{MeasureName(check.Measure)} of {check.Region} {F(value)}";
            if (check.Than != null)
            {
                spec.TryRegion(check.Than, out var other);
                float otherValue = Measure(check.Measure, frame.Mean(other));
                value -= otherValue;
                what = $"{MeasureName(check.Measure)} of {check.Region} minus {check.Than} {F(value)} ({F(value + otherValue)} - {F(otherValue)})";
            }
            bool inRange = (!check.Min.HasValue || value >= check.Min.Value) && (!check.Max.HasValue || value <= check.Max.Value);
            string range = check.Min.HasValue && check.Max.HasValue ? $"want {F(check.Min.Value)} to {F(check.Max.Value)}"
                : check.Min.HasValue ? $"want at least {F(check.Min.Value)}" : $"want at most {F(check.Max!.Value)}";
            results.Add(new PixelCheckResult(check.Name, inRange ? PixelCheckOutcome.Passed : PixelCheckOutcome.Failed, $"{what}, {range}"));
        }
        return results;
    }

    private static string MeasureName(PixelMeasure m) => m switch
    {
        PixelMeasure.Luma => "luma",
        PixelMeasure.R => "r",
        PixelMeasure.G => "g",
        PixelMeasure.B => "b",
        PixelMeasure.Chroma => "chroma",
        PixelMeasure.BlueMinusRed => "b-r",
        _ => "r-b",
    };

    private static string F(float v) => v.ToString("0.000", CultureInfo.InvariantCulture);
    internal static string Format(Vector3 c) => $"({F(c.X)}, {F(c.Y)}, {F(c.Z)})";
}
