#nullable enable
using System;
using Sage.UI;
using Color = Microsoft.Xna.Framework.Color;

namespace Sage.Client;

// Draws widget screens (docs/design/13 "As built (drawing)", issue #97). What to draw is Sage.UI's
// render plan — headless and tested: backgrounds, style images, borders, bar fills, pictures and text
// per style and state, with a Scroll's clip pushed and popped around what it holds — cached against
// the root's, the styles' and the string tables' versions. This only replays it into UiDraw, adding
// what moves every frame without changing the plan: the layer's fade and slide, and the focus
// highlight easing between two colours the plan already worked out. So a screen nobody touches costs
// one comparison and a loop over structs, and allocates nothing.
internal static class WidgetRenderer
{
    public static void Draw(UiDraw ui, UiScreenStack stack, UiStyles styles, Localisation text, ContentService content, TrueTypeText fonts,
                            UiPictures? pictures = null)
    {
        fonts.BeginFrame(stack.Fonts);
        pictures?.BeginFrame();
        var layers = stack.Layers;
        for (int i = 0; i < layers.Count; i++) DrawLayer(ui, layers[i], styles, text, content, stack.Fonts, fonts, pictures);
        pictures?.EndFrame();
    }

    private static void DrawLayer(UiDraw ui, UiLayer layer, UiStyles styles, Localisation text, ContentService content, UiFonts? fonts, TrueTypeText truetype,
                                  UiPictures? pictures)
    {
        var root = layer.Root;
        var plan = layer.Plan;
        plan.Update(root, styles, layer.Pressed, layer.PreviousFocus, text.Version);

        float opacity = layer.Opacity;
        if (opacity <= 0f) return;
        float dy = layer.Offset * root.Scale;
        float blend = layer.FocusBlend;
        float fontPixel = ui.FontPixelSize;

        if ((layer.Backdrop >> 24) != 0)
            ui.Rect(0f, 0f, ui.Size.X, ui.Size.Y, Colour(layer.Backdrop, opacity));

        int depth = ui.ClipDepth;
        for (int i = 0; i < plan.Count; i++)
        {
            ref readonly var command = ref plan[i];
            var r = command.Rect;
            float y = r.Y + dy;
            switch (command.Kind)
            {
                case UiDrawKind.Rect:
                    ui.Rect(r.X, y, r.Width, r.Height, Colour(command, blend, opacity));
                    break;
                case UiDrawKind.Border:
                    ui.Frame(r.X, y, r.Width, r.Height, Colour(command, blend, opacity), MathF.Max(MathF.Round(command.Size), 1f));
                    break;
                case UiDrawKind.Text:
                    DrawText(ui, command, r.X, y, Colour(command, blend, opacity), fontPixel, content, fonts, truetype);
                    break;
                case UiDrawKind.Image when command.Target != null:
                    // A render target (a view widget's, issue #348): as drawn this frame, or as last drawn.
                    if (pictures?.Target(command.Target) is { } picture)
                        ui.Image(picture, Pixels(r.X, y, r.Width, r.Height), Colour(command, blend, opacity));
                    break;
                case UiDrawKind.Fog:
                    if (pictures?.Fog(command.Fog!) is { } fog)
                        ui.Image(fog, Pixels(r.X, y, r.Width, r.Height), Colour(command, blend, opacity));
                    break;
                case UiDrawKind.Image:
                    // Asked every frame, as the font is: a hot reload disposes the texture under a
                    // reference kept (UiRenderSystem). A dictionary lookup on an interned path.
                    if (command.Texture.IsEmpty || content.LoadTexture(command.Texture, AssetScope.Ui) is not { } texture) break;
                    var colour = Colour(command, blend, opacity);
                    var slice = command.Slice;
                    if (command.Turned)
                        ui.ImageTurned(texture, new Microsoft.Xna.Framework.Rectangle((int)MathF.Round(r.X), (int)MathF.Round(y),
                                       (int)MathF.Round(r.Width), (int)MathF.Round(r.Height)), colour);
                    else if (slice == Thickness.Zero)
                        ui.Image(texture, new Microsoft.Xna.Framework.Rectangle((int)MathF.Round(r.X), (int)MathF.Round(y),
                                 (int)MathF.Round(r.Width), (int)MathF.Round(r.Height)), colour);
                    else
                        ui.NineSlice(texture, r.X, y, r.Width, r.Height, slice.Left, slice.Top, slice.Right, slice.Bottom, colour, command.Size);
                    break;
                case UiDrawKind.PushClip:
                    ui.PushClip(r.X, y, r.Width, r.Height);
                    break;
                case UiDrawKind.PopClip:
                    ui.PopClip();
                    break;
            }
        }
        while (ui.ClipDepth > depth) ui.PopClip();   // the plan balances; a layer never leaks a clip into the next
    }

    // A line of a label (#338): in its style's TrueType font, through the glyph atlases; else in a grid
    // atlas the style names, or the engine font. The em is FontSize × Size pixels either way.
    private static void DrawText(UiDraw ui, in UiDrawCommand command, float x, float y, Color colour, float fontPixel,
                                 ContentService content, UiFonts? fonts, TrueTypeText truetype)
    {
        float em = (command.FontSize > 0f ? command.FontSize : UiFonts.DefaultSize) * command.Size;
        string text = command.Text!;
        if (fonts != null && UiFonts.IsTrueType(command.Font) && fonts.Get(command.Font) is { } font)
        {
            truetype.Draw(ui, font, em, text, command.Start, command.Length, x, y, colour);
            if (command.Ellipsis) truetype.Draw(ui, font, em, TextLayout.Ellipsis, 0, 1, x + command.EllipsisAt, y, colour);
            return;
        }
        // A cell font: Size is pixels per font pixel at the engine font's size, and the font itself
        // multiplies by its PixelSize.
        var atlas = UiFonts.IsAtlas(command.Font) ? content.LoadFont(command.Font) : null;
        float scale = em / UiFonts.DefaultSize / (atlas?.PixelSize ?? fontPixel);
        ui.Text(x, y, text, command.Start, command.Length, colour, scale, atlas);
        if (command.Ellipsis) ui.Text(x + command.EllipsisAt, y, TextLayout.Ellipsis, 0, 1, colour, scale, atlas);
    }

    private static Microsoft.Xna.Framework.Rectangle Pixels(float x, float y, float width, float height) =>
        new((int)MathF.Round(x), (int)MathF.Round(y), (int)MathF.Round(width), (int)MathF.Round(height));

    private static Color Colour(in UiDrawCommand command, float blend, float opacity) =>
        Colour(command.Blend ? UiColour.Lerp(command.From, command.Colour, blend) : command.Colour, opacity);

    private static Color Colour(uint packed, float opacity) => new(UiColour.Fade(packed, opacity));
}

// The pictures widgets show that are not texture files (issue #348): render targets, by name, from the
// renderer; and fog masks, each uploaded to a texture of its own — white, alpha the part not revealed —
// again only when its Version changes, and freed once no screen has drawn it for a while.
internal sealed class UiPictures : IDisposable
{
    private const int KeepFrames = 120;

    private sealed class FogTexture
    {
        public required Microsoft.Xna.Framework.Graphics.Texture2D Texture;
        public int Version = -1;
        public int LastFrame;
        public Color[] Pixels = Array.Empty<Color>();
    }

    private readonly Renderer _renderer;
    private readonly System.Collections.Generic.Dictionary<UiFogMask, FogTexture> _fog = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly System.Collections.Generic.List<UiFogMask> _stale = new();
    private int _frame;

    public UiPictures(Renderer renderer) { _renderer = renderer; }

    public void BeginFrame() => _frame++;

    public Microsoft.Xna.Framework.Graphics.Texture2D? Target(string name) =>
#pragma warning disable SAGE0123   // the engine's own use of its experimental render-target API
        _renderer.FindTarget(name);
#pragma warning restore SAGE0123

    public Microsoft.Xna.Framework.Graphics.Texture2D Fog(UiFogMask mask)
    {
        if (!_fog.TryGetValue(mask, out var fog))
        {
            fog = new FogTexture { Texture = new Microsoft.Xna.Framework.Graphics.Texture2D(_renderer.Device, mask.Width, mask.Height) };
            _fog[mask] = fog;
        }
        fog.LastFrame = _frame;
        if (fog.Version != mask.Version)
        {
            var cells = mask.Cells;
            if (fog.Pixels.Length != cells.Length) fog.Pixels = new Color[cells.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                byte hidden = (byte)(255 - cells[i]);
                fog.Pixels[i] = new Color((byte)255, (byte)255, (byte)255, hidden);   // UiDraw blends non-premultiplied
            }
            fog.Texture.SetData(fog.Pixels);
            fog.Version = mask.Version;
        }
        return fog.Texture;
    }

    // Frees what no screen has drawn for KeepFrames frames (a map closed a while ago).
    public void EndFrame()
    {
        if (_fog.Count == 0) return;
        foreach (var (mask, fog) in _fog)
            if (_frame - fog.LastFrame > KeepFrames) _stale.Add(mask);
        foreach (var mask in _stale)
        {
            _fog[mask].Texture.Dispose();
            _fog.Remove(mask);
        }
        _stale.Clear();
    }

    public void Dispose()
    {
        foreach (var fog in _fog.Values) fog.Texture.Dispose();
        _fog.Clear();
    }
}

// How the engine font measures, for layout (Sage.UI's ITextMeasure, issue #97): in *font* pixels —
// a 5×7 glyph in a 6×9 cell — so a widget's TextScale is font pixels per virtual unit and one
// ui_style textScale means the same on every screen size. Counted exactly as BitmapFont.Measure counts,
// so what layout reserved is what the font draws.
internal sealed class BitmapFontMeasure : ITextMeasure
{
    public static readonly BitmapFontMeasure Instance = new();

    public float LineHeight => BitmapFont.GlyphHeight + 2f;

    public System.Numerics.Vector2 Measure(string text, float scale)
    {
        int longest = 0, width = 0, lines = 1;
        foreach (char c in text)
        {
            if (c == '\n') { longest = Math.Max(longest, width); width = 0; lines++; continue; }
            width++;
        }
        longest = Math.Max(longest, width);
        return new System.Numerics.Vector2(longest * (BitmapFont.GlyphWidth + 1f) * scale, lines * LineHeight * scale);
    }

    public float Width(ReadOnlySpan<char> text, float scale)
    {
        int count = 0;
        foreach (char c in text) if (c != '\n') count++;
        return count * (BitmapFont.GlyphWidth + 1f) * scale;
    }
}
