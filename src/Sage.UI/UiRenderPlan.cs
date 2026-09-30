#nullable enable
using System;
using System.Numerics;

namespace Sage.UI;

// What drawing a widget tree comes to (docs/design/13 "As built (drawing)", issue #97): a flat list of
// rectangles, borders, text, images and clip pushes and pops, in viewport pixels and drawing order.
// Headless, so a test asserts what is drawn, in which order and under which clip without a window; the
// client (Sage.Client's WidgetRenderer) turns each command into a UiDraw call and adds only what moves
// every frame — a layer's fade and slide, and how far the focus highlight has eased — so the plan
// itself is rebuilt only when what it says changes: the root's Version, the styles' or the string
// tables', the pressed widget or the viewport. A clean screen costs a comparison and allocates nothing.
internal enum UiDrawKind : byte { Rect, Border, Text, Image, PushClip, PopClip }

internal struct UiDrawCommand
{
    public UiDrawKind Kind;

    // Where, in viewport pixels. Text: where its measured box sits. PushClip: the clip, already
    // intersected with the one it is inside.
    public Rect Rect;

    // Packed like Color.PackedValue (ColourJsonConverter). With Blend the colour eases from From to
    // Colour as the focus highlight moves (UiLayer.FocusBlend); otherwise From == Colour.
    public uint Colour, From;
    public bool Blend;

    // Border: its width in pixels. Text: pixels per font pixel (TextScale × UiRoot.Scale). Image:
    // pixels per texture pixel, for the corners of a nine-slice.
    public float Size;

    public string? Text;
    public AssetPath Texture;

    // Nine-slice insets in texture pixels; zero draws the texture stretched whole.
    public Thickness Slice;

    // Whose command this is (tests, and a debugger).
    public Widget? Widget;
}

internal sealed class UiRenderPlan
{
    private UiDrawCommand[] _commands = new UiDrawCommand[64];
    private Rect[] _clips = new Rect[8];
    private int _clipDepth;

    private bool _built;
    private UiRoot? _root;
    private int _rootVersion, _stylesVersion, _textVersion;
    private Widget? _pressed, _previousFocus;
    private Vector2 _viewport;
    private UiStyles? _styles;

    public int Count { get; private set; }

    // How many times the plan was worked out: a test's proof that a clean frame reused it.
    public int Builds { get; private set; }

    public ref readonly UiDrawCommand this[int index] => ref _commands[index];

    public ReadOnlySpan<UiDrawCommand> Commands => _commands.AsSpan(0, Count);

    // Brings the plan up to date with `root`: lays it out and, when anything it depends on changed,
    // walks it again. `pressed` is the widget held down (the UI does not track that, UiStyles.StateOf);
    // `previousFocus` the widget focus just left, whose highlight eases out. `textVersion` is
    // Localisation.Version. Returns whether it rebuilt.
    public bool Update(UiRoot root, UiStyles styles, Widget? pressed = null, Widget? previousFocus = null, int textVersion = 0)
    {
        root.Layout();
        if (_built && ReferenceEquals(root, _root) && ReferenceEquals(styles, _styles) && root.Version == _rootVersion
            && styles.Version == _stylesVersion && textVersion == _textVersion && ReferenceEquals(pressed, _pressed)
            && ReferenceEquals(previousFocus, _previousFocus) && root.Viewport == _viewport)
            return false;

        Count = 0;
        _clipDepth = 0;
        var visitor = new Visitor(this, root, styles, pressed, previousFocus);
        root.Walk(ref visitor);
        while (_clipDepth > 0) PopClip(null);   // a walk always balances; this only guards a broken visitor

        _built = true;
        _root = root;
        _styles = styles;
        _rootVersion = root.Version;
        _stylesVersion = styles.Version;
        _textVersion = textVersion;
        _pressed = pressed;
        _previousFocus = previousFocus;
        _viewport = root.Viewport;
        Builds++;
        return true;
    }

    // Whether a widget draws its style's *box* — background, image, border. Only where the style is
    // applied, not again on each descendant that inherits it (a ui_layout node without a style has its
    // parent's, #96): a window's frame is drawn round the window, not round every label in it, while
    // the labels still take its text colour and scale. So a style is drawn where it changes going down
    // the tree; a widget that wants the same box as its parent inside it names another style.
    internal static bool OwnsBox(Widget widget) =>
        widget.Style != null && !string.Equals(widget.Style, widget.Parent?.Style, StringComparison.Ordinal);

    // Forgets the cached plan, so the next Update walks the tree whatever it says.
    public void Invalidate() => _built = false;

    // An image widget's Source as an asset path, parsed once per string: a rebuild (focus moving across
    // a grid of icons) then looks paths up rather than parsing them again. A path that is not one
    // (`..`, a colon) draws nothing and says so once.
    private readonly System.Collections.Generic.Dictionary<string, AssetPath> _paths = new(StringComparer.Ordinal);

    private AssetPath TexturePath(string source)
    {
        if (_paths.TryGetValue(source, out var path)) return path;
        try { path = AssetPath.Intern(source); }
        catch (ArgumentException ex)
        {
            Log.Warn(LogCat.UI, $"image '{source}': {ex.Message}; drawn as nothing");
            path = AssetPath.None;
        }
        if (_paths.Count > 1024) _paths.Clear();   // bound what a view-model inventing paths can grow it to
        _paths[source] = path;
        return path;
    }

    private ref UiDrawCommand Add(UiDrawKind kind, Widget widget, Rect rect)
    {
        if (Count == _commands.Length) Array.Resize(ref _commands, _commands.Length * 2);
        ref var command = ref _commands[Count++];
        command = new UiDrawCommand { Kind = kind, Widget = widget, Rect = rect };
        return ref command;
    }

    private void PushClip(Widget widget, Rect clip)
    {
        if (_clipDepth > 0) clip = RectMath.Intersect(clip, _clips[_clipDepth - 1]);
        if (_clipDepth == _clips.Length) Array.Resize(ref _clips, _clips.Length * 2);
        _clips[_clipDepth++] = clip;
        Add(UiDrawKind.PushClip, widget, clip);
    }

    private void PopClip(Widget? widget)
    {
        _clipDepth--;
        // A pop says what the clip goes back to (empty rect and depth 0: none), so the client need not
        // keep a stack of its own to know.
        var restored = _clipDepth > 0 ? _clips[_clipDepth - 1] : default;
        Add(UiDrawKind.PopClip, widget!, restored);
    }

    private struct Visitor : IWidgetVisitor
    {
        private readonly UiRenderPlan _plan;
        private readonly UiRoot _root;
        private readonly UiStyles _styles;
        private readonly Widget? _pressed, _previousFocus;

        public Visitor(UiRenderPlan plan, UiRoot root, UiStyles styles, Widget? pressed, Widget? previousFocus)
        {
            _plan = plan;
            _root = root;
            _styles = styles;
            _pressed = pressed;
            _previousFocus = previousFocus;
        }

        public bool Enter(Widget widget)
        {
            // Nothing of it is on screen (a row scrolled out of its list): drawn not at all. Containers
            // are still entered — a child may overhang its parent's rect by a margin.
            if (!Shown(widget)) return false;
            if (Overlaps(widget.Rect, widget.Clip)) Draw(widget);
            if (widget is Scroll) _plan.PushClip(widget, _root.ToPixels(RectMath.Intersect(widget.Clip, widget.ContentRect)));
            return true;
        }

        // UiRoot.Walk calls Leave whatever Enter said, so this pops exactly what Enter pushed.
        public void Leave(Widget widget)
        {
            if (widget is Scroll && Shown(widget)) _plan.PopClip(widget);
        }

        // Whether anything of it can be on screen. A container's child may overhang it, so a container
        // off screen is still entered — except a Scroll, which clips all it holds to itself.
        private static bool Shown(Widget widget) =>
            widget.ChildCount > 0 && widget is not Scroll || Overlaps(widget.Rect, widget.Clip);

        private void Draw(Widget widget)
        {
            var style = _styles.Get(widget.Style);
            var state = UiStyles.StateOf(widget, ReferenceEquals(widget, _pressed));
            var to = style.Colours(state);
            var from = to;
            bool blend = false;
            // The focus highlight eases in on the focused widget and out on the one it left (UiLayer's
            // FocusBlend, on frame time): the plan records both ends, so easing never rebuilds it.
            if (state == UiState.Focused)
            {
                from = style.Colours(widget.IsHovered ? UiState.Hover : UiState.Normal);
                blend = true;
            }
            else if (ReferenceEquals(widget, _previousFocus) && (state == UiState.Normal || state == UiState.Hover))
            {
                from = style.Colours(UiState.Focused);
                blend = true;
            }

            var rect = _root.ToPixels(widget.Rect);
            float scale = _root.Scale;
            bool box = OwnsBox(widget);

            if (box && (Visible(to.Background) || blend && Visible(from.Background)))
                Colour(ref _plan.Add(UiDrawKind.Rect, widget, rect), to.Background, from.Background, blend);

            if (box && !style.Image.IsEmpty)
            {
                ref var image = ref _plan.Add(UiDrawKind.Image, widget, rect);
                Colour(ref image, to.Tint, from.Tint, blend);
                image.Texture = style.Image;
                image.Slice = style.Slice;
                image.Size = scale;
            }

            if (box && style.BorderWidth > 0f && (Visible(to.Border) || blend && Visible(from.Border)))
            {
                ref var border = ref _plan.Add(UiDrawKind.Border, widget, rect);
                Colour(ref border, to.Border, from.Border, blend);
                border.Size = style.BorderWidth * scale;
            }

            switch (widget)
            {
                case Bar bar:
                    var fill = bar.FillRect;
                    if (fill.Width > 0f && fill.Height > 0f && (Visible(to.Fill) || blend && Visible(from.Fill)))
                        Colour(ref _plan.Add(UiDrawKind.Rect, widget, _root.ToPixels(fill)), to.Fill, from.Fill, blend);
                    break;

                case Image picture when !string.IsNullOrEmpty(picture.Source):
                    // A nine-sliced picture fills its content rect: keeping the aspect is what slicing
                    // is there to avoid.
                    bool sliced = style.Slice != Thickness.Zero;
                    ref var drawn = ref _plan.Add(UiDrawKind.Image, widget, _root.ToPixels(sliced ? widget.ContentRect : picture.ImageRect));
                    Colour(ref drawn, to.Tint, from.Tint, blend);
                    drawn.Texture = _plan.TexturePath(picture.Source);
                    drawn.Slice = style.Slice;
                    drawn.Size = scale;
                    break;

                case Label label when label.Text.Length > 0:
                    var size = _root.Text.Measure(label.Text, label.TextScale);
                    var content = widget.ContentRect;
                    float x = label.TextAlign switch
                    {
                        Align.Center => content.X + (content.Width - size.X) * 0.5f,
                        Align.End => content.Right - size.X,
                        _ => content.X,
                    };
                    float y = content.Y + (content.Height - size.Y) * 0.5f;
                    ref var text = ref _plan.Add(UiDrawKind.Text, widget, _root.ToPixels(new Rect(x, y, size.X, size.Y)));
                    Colour(ref text, to.Text, from.Text, blend);
                    text.Text = label.Text;
                    text.Size = label.TextScale * scale;
                    break;
            }
        }

        private static void Colour(ref UiDrawCommand command, uint to, uint from, bool blend)
        {
            command.Colour = to;
            command.From = blend ? from : to;
            command.Blend = blend && from != to;
        }

        private static bool Visible(uint packed) => (packed >> 24) != 0;

        private static bool Overlaps(Rect a, Rect b) =>
            a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;
    }
}

// Packed colours (ColourJsonConverter's layout: red in the low byte, alpha in the high one).
internal static class UiColour
{
    // Each channel `t` of the way from `from` to `to` (0..1, clamped).
    public static uint Lerp(uint from, uint to, float t)
    {
        if (!(t > 0f)) return from;
        if (t >= 1f) return to;
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            float a = (from >> shift) & 0xFF, b = (to >> shift) & 0xFF;
            result |= (uint)MathF.Round(a + (b - a) * t) << shift;
        }
        return result;
    }

    // Alpha multiplied by `opacity` (0..1): a layer fading in or out.
    public static uint Fade(uint packed, float opacity)
    {
        if (opacity >= 1f) return packed;
        uint alpha = (uint)MathF.Round(((packed >> 24) & 0xFF) * Math.Clamp(opacity, 0f, 1f));
        return (packed & 0x00FFFFFFu) | (alpha << 24);
    }
}

// A nine-slice (docs/design/13 "As built (drawing)"): a texture cut into three columns and three rows
// by insets in texture pixels, so the corners keep their size, the edges stretch along one axis and the
// middle along both — how a window's frame fits any window. The corners are `scale` pixels per texture
// pixel, shrunk together when the destination is smaller than two corners.
internal static class NineSlice
{
    public const int Pieces = 9;

    // Writes each non-empty piece's source (texture pixels) and destination (the destination's units)
    // and returns how many there are. Edges are shared, so a caller that rounds each edge the same way
    // leaves no seams.
    public static int Compute(Rect destination, Vector2 textureSize, Thickness insets, float scale,
                              Span<Rect> sources, Span<Rect> destinations)
    {
        float tw = textureSize.X, th = textureSize.Y;
        float l = Math.Clamp(insets.Left, 0f, tw), r = Math.Clamp(insets.Right, 0f, tw - l);
        float t = Math.Clamp(insets.Top, 0f, th), b = Math.Clamp(insets.Bottom, 0f, th - t);

        float dl = l * scale, dr = r * scale, dt = t * scale, db = b * scale;
        if (dl + dr > destination.Width && dl + dr > 0f) { float k = destination.Width / (dl + dr); dl *= k; dr *= k; }
        if (dt + db > destination.Height && dt + db > 0f) { float k = destination.Height / (dt + db); dt *= k; db *= k; }

        Span<float> sx = stackalloc float[] { 0f, l, tw - r, tw };
        Span<float> sy = stackalloc float[] { 0f, t, th - b, th };
        Span<float> dx = stackalloc float[] { destination.X, destination.X + dl, destination.Right - dr, destination.Right };
        Span<float> dy = stackalloc float[] { destination.Y, destination.Y + dt, destination.Bottom - db, destination.Bottom };

        int n = 0;
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
            {
                float sw = sx[column + 1] - sx[column], sh = sy[row + 1] - sy[row];
                float w = dx[column + 1] - dx[column], h = dy[row + 1] - dy[row];
                if (sw <= 0f || sh <= 0f || w <= 0f || h <= 0f) continue;
                sources[n] = new Rect(sx[column], sy[row], sw, sh);
                destinations[n] = new Rect(dx[column], dy[row], w, h);
                n++;
            }
        return n;
    }
}
