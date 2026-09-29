#nullable enable
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// Immediate-mode screen-space drawing (docs/design/13 §3): rectangles, text and images, in pixels,
// queued by anything that runs in a Frame phase and drawn once in Overlay.
//
// This is not a UI *system* — there are no widgets, layout or input handling, and picking one of
// those is still open (13 §3). It is the smallest thing that lets a game draw a health bar and tell
// the player what just happened, which the slice needed before it needed a widget tree. ImGui stays
// what it is: a dev tool, compiled into dev builds only.
public sealed class UiDraw
{
    private enum Kind { Rect, Text, Image }

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

    // The viewport this frame, so a caller can place things against an edge without asking the device.
    public Vector2 Size { get; internal set; }

    public bool HasFont => _font != null;

    // The height of a line of text at scale 1, or a sensible guess before the font has loaded.
    // The fallback matches `BitmapFont` at its default pixel size, so a layout measured before the
    // font arrives (or with none at all) is laid out the same as one measured after it.
    public float LineHeight => _font?.LineHeight ?? 18f;

    internal void SetFont(BitmapFont? font) => _font = font;

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

    internal void Draw(SpriteBatch batch, Texture2D white)
    {
        for (int i = 0; i < _commands.Count; i++)
        {
            ref var command = ref _commands[i];
            switch (command.Kind)
            {
                case Kind.Rect:
                    batch.Draw(white, command.Destination, command.Colour);
                    break;
                case Kind.Text when _font != null && command.Text != null:
                    _font.Draw(batch, command.Text, new Vector2(command.Destination.X, command.Destination.Y),
                               command.Colour, command.Scale);
                    break;
                case Kind.Image when command.Texture != null:
                    batch.Draw(command.Texture, command.Destination, command.Source, command.Colour);
                    break;
            }
        }
    }

    internal void Clear() => _commands.Clear();
}
