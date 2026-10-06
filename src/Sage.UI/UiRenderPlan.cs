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

    // Text: the line drawn is Text[Start..Start+Length] (#338: a wrapped label is a command per line,
    // each a slice of the one string), in Font at FontSize (0: UiFonts.DefaultSize) — so its em is
    // FontSize × Size pixels. With Ellipsis, "…" follows it, EllipsisAt pixels from the line's left.
    public string? Text;
    public int Start, Length;
    public AssetPath Font;
    public float FontSize;
    public bool Ellipsis;
    public float EllipsisAt;
    public AssetPath Texture;

    // Nine-slice insets in texture pixels; zero draws the texture stretched whole.
    public Thickness Slice;

    // Image: drawn a quarter turn clockwise into Rect (a button's IconTurned, issue #346).
    public bool Turned;

    // Whose command this is (tests, and a debugger).
    public Widget? Widget;
}

internal sealed class UiRenderPlan
{
    private UiDrawCommand[] _commands = new UiDrawCommand[64];
    private readonly System.Collections.Generic.List<TextLine> _lines = new();
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
        if (root.IsDragging) DrawGhost(root);
        if (root.Popup is { IsOpen: true } popup) DrawPopup(root, styles, popup);

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

    // An open dropdown's list (issue #340), after everything else so it is over it: a box in the
    // dropdown's style (an opaque grey when the style has no background, so the rows are readable over
    // whatever is under them), the highlighted row in the style's focused background, each option's text.
    private void DrawPopup(UiRoot root, UiStyles styles, Dropdown dropdown)
    {
        var style = styles.Get(dropdown.Style);
        var normal = style.Colours(UiState.Normal);
        var focused = style.Colours(UiState.Focused);
        float scale = root.Scale;
        uint background = (normal.Background >> 24) != 0 ? normal.Background : ColourJsonConverter.Pack(32, 32, 32);
        uint highlight = (focused.Background >> 24) != 0 && focused.Background != normal.Background ? focused.Background : ColourJsonConverter.Pack(64, 96, 160);

        var list = root.ToPixels(dropdown.ListRect);
        ref var box = ref Add(UiDrawKind.Rect, dropdown, list);
        box.Colour = box.From = background;
        if (style.BorderWidth > 0f && (normal.Border >> 24) != 0)
        {
            ref var border = ref Add(UiDrawKind.Border, dropdown, list);
            border.Colour = border.From = normal.Border;
            border.Size = style.BorderWidth * scale;
        }
        var options = dropdown.Options;
        var padding = dropdown.Padding;
        for (int i = 0; i < options.Count; i++)
        {
            var row = dropdown.OptionRect(i);
            bool lit = i == dropdown.Highlighted;
            if (lit)
            {
                ref var bar = ref Add(UiDrawKind.Rect, dropdown, root.ToPixels(row));
                bar.Colour = bar.From = highlight;
            }
            if (options[i].Length == 0) continue;
            var size = dropdown.MeasureOf(root.Text).Measure(options[i], dropdown.TextScale);   // in the dropdown's font (#338)
            var at = new Rect(row.X + padding.Left, row.Y + (row.Height - size.Y) * 0.5f, size.X, size.Y);
            ref var text = ref Add(UiDrawKind.Text, dropdown, root.ToPixels(at));
            text.Colour = text.From = lit ? focused.Text : normal.Text;
            text.Text = options[i];
            text.Length = options[i].Length;
            text.Font = dropdown.Font;
            text.FontSize = dropdown.FontSize;
            text.Size = dropdown.TextScale * scale;
        }
    }

    // What is being dragged (issue #346), again, over everything and following the pointer: a copy of
    // the commands its widget and those inside it drew, moved by how far the pointer has, and faded —
    // the item's own box, picture and label, so the ghost is the thing itself. Clips are left out: the
    // ghost is wherever the pointer takes it, outside the scroll it came from too.
    private void DrawGhost(UiRoot root)
    {
        var drag = root.Drag;
        var source = drag.Source!;
        var offset = drag.Offset * root.Scale;
        int drawn = Count;
        for (int i = 0; i < drawn; i++)
        {
            if (_commands[i].Kind is UiDrawKind.PushClip or UiDrawKind.PopClip || !source.Contains(_commands[i].Widget)) continue;
            var copy = _commands[i];
            copy.Rect = new Rect(copy.Rect.X + offset.X, copy.Rect.Y + offset.Y, copy.Rect.Width, copy.Rect.Height);
            copy.Colour = UiColour.Fade(copy.Colour, GhostOpacity);
            copy.From = UiColour.Fade(copy.From, GhostOpacity);
            ref var ghost = ref Add(copy.Kind, source, copy.Rect);
            ghost = copy;
        }
    }

    // How opaque a dragged widget's ghost is.
    internal const float GhostOpacity = 0.7f;

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
            else if (ReferenceEquals(widget, _previousFocus) && state is UiState.Normal or UiState.Hover or UiState.Selected)
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
                    // A slider's handle, in the text colour: what moves when the player moves it.
                    if (bar is Slider slider)
                        Colour(ref _plan.Add(UiDrawKind.Rect, widget, _root.ToPixels(slider.KnobRect)), to.Text, from.Text, blend);
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

                case Label label:
                    if (label is Button { Icon: { Length: > 0 } icon } button)
                    {
                        // The picture first, across the content, so the text (a stack's count) is over it.
                        ref var picture = ref _plan.Add(UiDrawKind.Image, widget, _root.ToPixels(button.ContentRect));
                        Colour(ref picture, to.Tint, from.Tint, blend);
                        picture.Texture = _plan.TexturePath(icon);
                        picture.Turned = button.IconTurned;
                        picture.Size = scale;
                    }
                    DrawLabel(label, to, from, blend, scale);
                    break;
            }
        }

        // A label's text, and what the form widgets that are labels add to it (issue #340): a checkbox's
        // box and tick, a dropdown's arrow, a text field's placeholder (faded) and its caret while focused.
        private void DrawLabel(Label label, UiStyleColours to, UiStyleColours from, bool blend, float scale)
        {
            if (label is Checkbox checkbox)
            {
                var box = checkbox.BoxRect;
                ref var outline = ref _plan.Add(UiDrawKind.Border, label, _root.ToPixels(box));
                Colour(ref outline, to.Text, from.Text, blend);
                outline.Size = MathF.Max(box.Width / 8f, 1f) * scale;
                if (checkbox.Checked)
                {
                    float inset = box.Width / 4f;
                    var tick = new Rect(box.X + inset, box.Y + inset, MathF.Max(box.Width - inset * 2f, 0f), MathF.Max(box.Height - inset * 2f, 0f));
                    Colour(ref _plan.Add(UiDrawKind.Rect, label, _root.ToPixels(tick)), to.Fill, from.Fill, blend);
                }
            }
            if (label is Dropdown dropdown) Text(label, Dropdown.Arrow, dropdown.ArrowRect, Align.Center, to.Text, from.Text, blend, scale, fit: false);

            string shown = label.Text;
            uint colour = to.Text, was = from.Text;
            if (shown.Length == 0 && label is TextBox { Placeholder.Length: > 0 } empty)
            {
                shown = empty.Placeholder;
                colour = UiColour.Fade(colour, 0.5f);
                was = UiColour.Fade(was, 0.5f);
            }
            var origin = shown.Length > 0 ? Text(label, shown, label.TextArea, label.TextAlign, colour, was, blend, scale, fit: true) : Placed(label, label.TextArea, Vector2.Zero, label.TextAlign);

            if (label is TextBox field && field.IsFocused)
            {
                // The caret: a sliver a line high after the character before it (on the text actually
                // typed, not the placeholder, which it sits in front of).
                var measure = field.MeasureOf(_root.Text);   // in the field's own font (#338)
                var at = field.Text.Length == 0 ? Vector2.Zero : field.CaretOffset(measure);
                float line = measure.LineHeight * field.TextScale;
                if (field.Text.Length == 0) origin = Placed(label, label.TextArea, new Vector2(0f, line), Align.Start);
                var caret = new Rect(origin.X + at.X, origin.Y + at.Y, MathF.Max(field.TextScale, 1f), line);
                Colour(ref _plan.Add(UiDrawKind.Rect, label, _root.ToPixels(caret)), to.Text, from.Text, blend);
            }
        }

        // Draws `text` in `area` in the label's font (#338) and returns its top-left, virtual units. With
        // `fit`, the label's Wrap, Overflow and MaxWidth apply: lines are worked out again here, at the
        // width the label was given rather than the one it measured in, so what is drawn always fits, and
        // each line is a command of its own, a slice of the one string.
        private Vector2 Text(Label label, string text, Rect area, Align align, uint colour, uint was, bool blend, float scale, bool fit)
        {
            var measure = label.MeasureOf(_root.Text);
            var lines = _plan._lines;
            Vector2 size;
            fit &= label.Fitted;
            if (fit)
            {
                float height = label.Overflow == TextOverflow.Visible ? float.PositiveInfinity : area.Height;
                size = TextLayout.Break(text, measure, label.TextScale, label.FitWidth(area.Width), height, label.Wrap, label.Overflow, lines);
            }
            else
            {
                size = measure.Measure(text, label.TextScale);
                lines.Clear();
                lines.Add(new TextLine(0, text.Length, size.X, false));
            }

            bool clip = fit && label.Overflow == TextOverflow.Clip;
            if (clip) _plan.PushClip(label, _root.ToPixels(RectMath.Intersect(label.Clip, area)));

            // Centred down the area, unless it is taller: then from the top, so the first lines show and
            // what does not fit is what is cut. Not fitted, the text is one command whatever its '\n's.
            var origin = Placed(label, area, size, align);
            if (size.Y > area.Height) origin.Y = area.Y;
            float lineHeight = fit ? measure.LineHeight * label.TextScale : size.Y;
            float y = origin.Y, ellipsis = 0f;
            foreach (var line in lines)
            {
                if (line.Ellipsis && ellipsis == 0f) ellipsis = measure.Width(TextLayout.Ellipsis, label.TextScale);
                if (line.Length > 0 || line.Ellipsis)
                {
                    float x = Placed(label, area, new Vector2(line.Width, 0f), align).X;
                    ref var command = ref _plan.Add(UiDrawKind.Text, label, _root.ToPixels(new Rect(x, y, line.Width, lineHeight)));
                    Colour(ref command, colour, was, blend);
                    command.Text = text;
                    command.Start = line.Start;
                    command.Length = line.Length;
                    command.Size = label.TextScale * scale;
                    command.Font = label.Font;
                    command.FontSize = label.FontSize;
                    command.Ellipsis = line.Ellipsis;
                    command.EllipsisAt = line.Ellipsis ? (line.Width - ellipsis) * scale : 0f;
                }
                y += lineHeight;
            }

            if (clip) _plan.PopClip(label);
            return origin;
        }

        private static Vector2 Placed(Label label, Rect area, Vector2 size, Align align)
        {
            float x = align switch
            {
                Align.Center => area.X + (area.Width - size.X) * 0.5f,
                Align.End => area.Right - size.X,
                _ => area.X,
            };
            return new Vector2(x, area.Y + (area.Height - size.Y) * 0.5f);
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
