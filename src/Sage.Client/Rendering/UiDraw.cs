#nullable enable
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// Immediate-mode screen-space drawing (docs/design/13 §3): rectangles, text and images, in pixels,
// queued by anything that runs in a Frame phase and drawn once in Overlay.
//
// This is not a UI *system*: the retained widgets are Sage.UI's (#95), and the client's WidgetRenderer
// draws them through this (#97). It is the smallest thing that lets a game draw a health bar and tell
// the player what just happened. ImGui stays what it is: a dev tool, compiled into dev builds only.
//
// Since #97 it also clips — PushClip/PopClip, a scissor stack, each clip inside the one before — and
// draws nine-slice images (NineSlice), which is what a window frame that fits any window needs.
public sealed class UiDraw
{
    private enum Kind { Rect, Text, Image, Clip }

    private struct Command
    {
        public Kind Kind;
        public Rectangle Destination;
        public Rectangle Source;
        public Color Colour;
        public string? Text;
        public float Scale;
        public Texture2D? Texture;
    }

    private readonly PooledList<Command> _commands = new(64);
    private BitmapFont? _font;
    private Rectangle[] _clips = new Rectangle[8];
    private int _clipDepth;

    // The viewport this frame, so a caller can place things against an edge without asking the device.
    public Vector2 Size { get; internal set; }

    public bool HasFont => _font != null;

    // The height of a line of text at scale 1, or a sensible guess before the font has loaded.
    // The fallback matches `BitmapFont` at its default pixel size, so a layout measured before the
    // font arrives (or with none at all) is laid out the same as one measured after it.
    public float LineHeight => _font?.LineHeight ?? 18f;

    internal void SetFont(BitmapFont? font) => _font = font;

    // Screen pixels per font pixel at scale 1: what a widget's text size (in font pixels) is divided by.
    internal float FontPixelSize => _font?.PixelSize ?? 2f;

    // How many clips are pushed.
    public int ClipDepth => _clipDepth;

    // Whatever is queued until the matching PopClip is drawn only inside this rectangle — and inside the
    // clip it is pushed within, so a list in a scrolling panel in a window is clipped by all three.
    public void PushClip(float x, float y, float width, float height)
    {
        int left = (int)MathF.Round(x), top = (int)MathF.Round(y);
        var clip = new Rectangle(left, top, Math.Max((int)MathF.Round(x + width) - left, 0), Math.Max((int)MathF.Round(y + height) - top, 0));
        if (_clipDepth > 0) clip = Rectangle.Intersect(clip, _clips[_clipDepth - 1]);
        if (_clipDepth == _clips.Length) Array.Resize(ref _clips, _clips.Length * 2);
        _clips[_clipDepth++] = clip;
        ref var command = ref _commands.Add();
        command = new Command { Kind = Kind.Clip, Destination = clip, Scale = 1f };
    }

    // Back to the clip before the last PushClip (none at the outermost). An unmatched pop is ignored.
    public void PopClip()
    {
        if (_clipDepth == 0) return;
        _clipDepth--;
        ref var command = ref _commands.Add();
        command = _clipDepth > 0
            ? new Command { Kind = Kind.Clip, Destination = _clips[_clipDepth - 1], Scale = 1f }
            : new Command { Kind = Kind.Clip, Scale = 0f };
    }

    // A nine-slice: the texture cut by `insets` (texture pixels: left, top, right, bottom) into corners
    // that keep their size — `cornerScale` screen pixels per texture pixel — edges that stretch one way
    // and a middle that stretches both, fitted to `destination`. The arithmetic is Sage.UI's NineSlice
    // (tested headless); each piece's edges are rounded the same way, so the pieces meet without seams.
    public void NineSlice(Texture2D texture, float x, float y, float width, float height, float insetLeft, float insetTop,
                          float insetRight, float insetBottom, Color colour, float cornerScale = 1f)
    {
        Span<Sage.Simulation.Rect> sources = stackalloc Sage.Simulation.Rect[Sage.UI.NineSlice.Pieces];
        Span<Sage.Simulation.Rect> targets = stackalloc Sage.Simulation.Rect[Sage.UI.NineSlice.Pieces];
        int n = Sage.UI.NineSlice.Compute(new Sage.Simulation.Rect(x, y, width, height),
            new System.Numerics.Vector2(texture.Width, texture.Height),
            new Sage.UI.Thickness(insetLeft, insetTop, insetRight, insetBottom), cornerScale, sources, targets);
        for (int i = 0; i < n; i++)
        {
            var s = sources[i];
            var d = targets[i];
            int left = (int)MathF.Round(d.X), top = (int)MathF.Round(d.Y);
            var destination = new Rectangle(left, top, (int)MathF.Round(d.Right) - left, (int)MathF.Round(d.Bottom) - top);
            if (destination.Width <= 0 || destination.Height <= 0) continue;
            int sl = (int)MathF.Round(s.X), st = (int)MathF.Round(s.Y);
            Image(texture, destination, colour, new Rectangle(sl, st, (int)MathF.Round(s.Right) - sl, (int)MathF.Round(s.Bottom) - st));
        }
    }

    public void Rect(float x, float y, float width, float height, Color colour)
    {
        ref var command = ref _commands.Add();
        command = new Command
        {
            Kind = Kind.Rect,
            Destination = new Rectangle((int)MathF.Round(x), (int)MathF.Round(y), (int)MathF.Round(width), (int)MathF.Round(height)),
            Colour = colour,
        };
    }

    // A rectangle drawn as an outline, for bars and panels that shouldn't look like solid blocks.
    public void Frame(float x, float y, float width, float height, Color colour, float thickness = 1f)
    {
        Rect(x, y, width, thickness, colour);
        Rect(x, y + height - thickness, width, thickness, colour);
        Rect(x, y, thickness, height, colour);
        Rect(x + width - thickness, y, thickness, height, colour);
    }

    public void Text(float x, float y, string text, Color colour, float scale = 1f)
    {
        if (string.IsNullOrEmpty(text)) return;
        ref var command = ref _commands.Add();
        command = new Command
        {
            Kind = Kind.Text,
            Destination = new Rectangle((int)MathF.Round(x), (int)MathF.Round(y), 0, 0),
            Colour = colour,
            Text = text,
            Scale = scale,
        };
    }

    public void Image(Texture2D texture, Rectangle destination, Color colour, Rectangle? source = null)
    {
        ref var command = ref _commands.Add();
        command = new Command
        {
            Kind = Kind.Image,
            Destination = destination,
            Source = source ?? new Rectangle(0, 0, texture.Width, texture.Height),
            Colour = colour,
            Texture = texture,
        };
    }

    // Pixels a string will take, for centring and for right-aligned numbers. Zero before the font
    // has loaded, which callers can treat as "don't know yet".
    public Vector2 Measure(string text, float scale = 1f) =>
        _font == null || string.IsNullOrEmpty(text) ? Vector2.Zero : _font.Measure(text, scale);

    // Draws the queue in one SpriteBatch pass, restarted wherever the clip changes (a scissor
    // rectangle only applies between Begin and End).
    internal void Draw(SpriteBatch batch, Texture2D white, RasterizerState scissor)
    {
        var device = batch.GraphicsDevice;
        var bounds = device.Viewport.Bounds;
        bool clipped = false;
        Begin(batch, null);
        for (int i = 0; i < _commands.Count; i++)
        {
            ref var command = ref _commands[i];
            switch (command.Kind)
            {
                case Kind.Rect:
                    if (command.Destination.Width > 0 && command.Destination.Height > 0) batch.Draw(white, command.Destination, command.Colour);
                    break;
                case Kind.Text when _font != null && command.Text != null:
                    _font.Draw(batch, command.Text, new Vector2(command.Destination.X, command.Destination.Y),
                               command.Colour, command.Scale);
                    break;
                case Kind.Image when command.Texture != null:
                    batch.Draw(command.Texture, command.Destination, command.Source, command.Colour);
                    break;
                case Kind.Clip:
                    batch.End();
                    clipped = command.Scale > 0f;
                    if (clipped) device.ScissorRectangle = Rectangle.Intersect(command.Destination, bounds);
                    Begin(batch, clipped ? scissor : null);
                    break;
            }
        }
        batch.End();
        if (clipped) device.ScissorRectangle = bounds;
    }

    private static void Begin(SpriteBatch batch, RasterizerState? rasterizer) =>
        batch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.PointClamp, null, rasterizer);

    internal void Clear()
    {
        _commands.Clear();
        _clipDepth = 0;
    }
}
