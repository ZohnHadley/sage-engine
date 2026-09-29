#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace sage_engine;

// Immediate-mode debug geometry (docs/design/06 §3.2, TODO F5). Simulation code says "draw a line
// there" and forgets about it; the client turns the queue into a line list once a frame.
//
// It lives in the engine, not the renderer, because the things worth drawing are simulation facts —
// a sweep, a sight cone, a ground normal, where the AI thinks you are. Three bugs found the hard way
// (a mirrored sprite sheet, a character falling through the world, a swing that hit nothing) were
// each a temporary Log line and a screenshot; every one of them is a shape you can see.
//
// Everything is decomposed into line segments here, so the client stays dumb and nothing in the
// engine needs a graphics type.

public readonly record struct DebugLine(Vector3 A, Vector3 B, uint Rgba);

// 0xRRGGBBAA, so a colour reads the way it is written.
public static class DebugColour
{
    public const uint White = 0xFFFFFFFF;
    public const uint Black = 0x000000FF;
    public const uint Red = 0xFF3B30FF;
    public const uint Green = 0x34C759FF;
    public const uint Blue = 0x0A84FFFF;
    public const uint Yellow = 0xFFD60AFF;
    public const uint Cyan = 0x32D8E0FF;
    public const uint Magenta = 0xFF2D95FF;
    public const uint Orange = 0xFF9F0AFF;
    public const uint Grey = 0x8E8E93FF;
}

// One per world. Nothing is recorded while `Enabled` is false, so a shipping build and a headless
// server pay for one bool test per call.
public sealed class DebugDraw
{
    private const int MaxLines = 16384;   // a runaway loop shouldn't eat the frame or the heap
    private const int CircleSegments = 16;

    private struct Entry
    {
        public DebugLine Line;
        public float Remaining;   // seconds; <= 0 means "this frame only"
        public bool Drawn;
    }

    private readonly List<Entry> _entries = new(256);
    private bool _warned;

    // The client sets this from `r_debugdraw` each frame; the simulation only reads it.
    public bool Enabled;

    public int Count => _entries.Count;

    // ---- shapes -------------------------------------------------------------------------------
    // `seconds` keeps a shape alive across frames, which is how you see something that happened in
    // one tick: a sweep that found nothing is gone before you can look at it otherwise.

    public void Line(Vector3 from, Vector3 to, uint colour = DebugColour.White, float seconds = 0f)
    {
        if (!Enabled) return;
        if (_entries.Count >= MaxLines)
        {
            if (!_warned) { Log.Warn(LogCat.Render, $"Debug draw is over {MaxLines} lines this frame; the rest are dropped"); _warned = true; }
            return;
        }
        _entries.Add(new Entry { Line = new DebugLine(from, to, colour), Remaining = seconds });
    }

    public void Ray(Vector3 from, Vector3 direction, float length, uint colour = DebugColour.White, float seconds = 0f) =>
        Line(from, from + direction * length, colour, seconds);

    // A line with a head, for anything with a direction: a normal, a facing, an aim.
    public void Arrow(Vector3 from, Vector3 to, uint colour = DebugColour.White, float seconds = 0f)
    {
        if (!Enabled) return;
        Line(from, to, colour, seconds);
        Vector3 along = to - from;
        float length = along.Length();
        if (length < 1e-4f) return;

        along /= length;
        Vector3 side = Vector3.Cross(along, MathF.Abs(along.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY);
        if (side.LengthSquared() > 1e-8f) side = Vector3.Normalize(side);
        float head = MathF.Min(0.18f * length, 0.25f);
        Vector3 back = to - along * head;
        Line(to, back + side * head * 0.5f, colour, seconds);
        Line(to, back - side * head * 0.5f, colour, seconds);
    }

    public void Cross(Vector3 at, float size = 0.15f, uint colour = DebugColour.White, float seconds = 0f)
    {
        if (!Enabled) return;
        Line(at - Vector3.UnitX * size, at + Vector3.UnitX * size, colour, seconds);
        Line(at - Vector3.UnitY * size, at + Vector3.UnitY * size, colour, seconds);
        Line(at - Vector3.UnitZ * size, at + Vector3.UnitZ * size, colour, seconds);
    }

    public void Box(Vector3 center, Vector3 halfExtents, uint colour = DebugColour.White, float seconds = 0f) =>
        Box(center, halfExtents, Quaternion.Identity, colour, seconds);

    public void Box(Vector3 center, Vector3 halfExtents, Quaternion rotation, uint colour = DebugColour.White, float seconds = 0f)
    {
        if (!Enabled) return;
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? -halfExtents.X : halfExtents.X,
                (i & 2) == 0 ? -halfExtents.Y : halfExtents.Y,
                (i & 4) == 0 ? -halfExtents.Z : halfExtents.Z);
            corners[i] = center + Vector3.Transform(corner, rotation);
        }
        // 0..7 is a bit per axis, so an edge is a pair differing in exactly one bit.
        for (int i = 0; i < 8; i++)
            for (int bit = 1; bit <= 4; bit <<= 1)
                if ((i & bit) == 0) Line(corners[i], corners[i | bit], colour, seconds);
    }

    public void Sphere(Vector3 center, float radius, uint colour = DebugColour.White, float seconds = 0f)
    {
        if (!Enabled) return;
        Circle(center, Vector3.UnitX, Vector3.UnitY, radius, colour, seconds);
        Circle(center, Vector3.UnitX, Vector3.UnitZ, radius, colour, seconds);
        Circle(center, Vector3.UnitY, Vector3.UnitZ, radius, colour, seconds);
    }

    // A capsule standing on `feet`, the shape a character actually collides with (10 §3).
    public void Capsule(Vector3 feet, float radius, float height, uint colour = DebugColour.White, float seconds = 0f)
    {
        if (!Enabled) return;
        radius = MathF.Max(radius, 1e-3f);
        float cylinder = MathF.Max(height - 2f * radius, 0f);
        Vector3 low = feet + Vector3.UnitY * radius;
        Vector3 high = low + Vector3.UnitY * cylinder;

        Circle(low, Vector3.UnitX, Vector3.UnitZ, radius, colour, seconds);
        Circle(high, Vector3.UnitX, Vector3.UnitZ, radius, colour, seconds);
        foreach (var side in stackalloc Vector3[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ })
            Line(low + side * radius, high + side * radius, colour, seconds);

        // The caps, as two arcs each, so a capsule reads as a capsule and not a tin can.
        Arc(high, Vector3.UnitX, Vector3.UnitY, radius, colour, seconds);
        Arc(high, Vector3.UnitZ, Vector3.UnitY, radius, colour, seconds);
        Arc(low, Vector3.UnitX, -Vector3.UnitY, radius, colour, seconds);
        Arc(low, Vector3.UnitZ, -Vector3.UnitY, radius, colour, seconds);
    }

    public void Circle(Vector3 center, Vector3 axisA, Vector3 axisB, float radius, uint colour = DebugColour.White, float seconds = 0f)
    {
        if (!Enabled) return;
        Vector3 previous = center + axisA * radius;
        for (int i = 1; i <= CircleSegments; i++)
        {
            float angle = i * MathF.Tau / CircleSegments;
            Vector3 point = center + (axisA * MathF.Cos(angle) + axisB * MathF.Sin(angle)) * radius;
            Line(previous, point, colour, seconds);
            previous = point;
        }
    }

    // A horizontal wedge on the ground, for a sight cone or an attack arc (16 §3.4).
    public void Cone(Vector3 at, float yaw, float degrees, float range, uint colour = DebugColour.White, float seconds = 0f)
    {
        if (!Enabled) return;
        float half = degrees * 0.5f * MathF.PI / 180f;
        const int Steps = 12;
        Vector3 previous = at + SageMath.ForwardFromYaw(yaw - half) * range;
        Line(at, previous, colour, seconds);
        for (int i = 1; i <= Steps; i++)
        {
            Vector3 point = at + SageMath.ForwardFromYaw(yaw - half + 2f * half * i / Steps) * range;
            Line(previous, point, colour, seconds);
            previous = point;
        }
        Line(at, previous, colour, seconds);
    }

    private void Arc(Vector3 center, Vector3 axisA, Vector3 axisB, float radius, uint colour, float seconds)
    {
        int steps = CircleSegments / 2;
        Vector3 previous = center + axisA * radius;
        for (int i = 1; i <= steps; i++)
        {
            float angle = i * MathF.PI / steps;
            Vector3 point = center + (axisA * MathF.Cos(angle) + axisB * MathF.Sin(angle)) * radius;
            Line(previous, point, colour, seconds);
            previous = point;
        }
    }

    // ---- tick and frame boundaries ---------------------------------------------------------------

    // Called at the start of every tick. Shapes with no duration belong to the tick that drew them,
    // so the next one replaces them: a frame shows the newest state rather than every tick since it
    // last drew, which at 60 Hz against 50 fps is how a few hundred lines became sixteen thousand.
    public void BeginTick()
    {
        _warned = false;
        for (int i = _entries.Count - 1; i >= 0; i--)
            if (_entries[i].Remaining <= 0f) _entries.RemoveAt(i);
    }


    // What the renderer should draw this frame, oldest first.
    public void CopyTo(List<DebugLine> into)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            into.Add(_entries[i].Line);
            var entry = _entries[i];
            entry.Drawn = true;
            _entries[i] = entry;
        }
    }

    // Ages timed shapes after a frame has drawn them and drops the finished ones. Momentary shapes
    // are left alone here; BeginTick owns those.
    public void Advance(float seconds)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var entry = _entries[i];
            if (entry.Remaining <= 0f) continue;
            entry.Remaining -= seconds;
            if (entry.Remaining <= 0f && entry.Drawn) _entries.RemoveAt(i);
            else _entries[i] = entry;
        }
    }

    public void Clear() => _entries.Clear();
}

public static class DebugDrawExtensions
{
    // Every world has one (03 §3.4), so debug drawing never needs a null check at the call site.
    public static DebugDraw Debug(this World world) => world.Resources.Get<DebugDraw>();
}
