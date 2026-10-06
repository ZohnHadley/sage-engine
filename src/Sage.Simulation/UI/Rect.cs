#nullable enable
using System.Numerics;

namespace Sage.Simulation;

// A rectangle in screen pixels (or a layout's virtual units). `System.Drawing` is not referenced and
// MonoGame's lives in the client, so this is the engine's own — four floats and one question. It was
// PanelLayout's until the panel screens were retired (issue #350); the widgets (Sage.UI) lay out in it.
public readonly record struct Rect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;

    public bool Contains(Vector2 point) =>
        point.X >= X && point.X < Right && point.Y >= Y && point.Y < Bottom;
}
