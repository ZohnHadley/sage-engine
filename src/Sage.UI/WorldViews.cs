#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.UI;

// Render targets and world views inside widgets (issue #348): a paper doll, an item being inspected, a
// character preview, a local map. A `view` widget is an image whose picture is a renderer's named
// render target rather than a texture file. Something has to draw into the target:
//
// - a camera entity whose `target` names it (#77) — a security camera's screen, a scripted shot; or
// - the view's own camera (`Camera`): Orbit looks at its subject (the player, the screen's other one, or
//   an entity by name) from in front, Distance away, turned by Yaw and Pitch — the player can drag it
//   round when it is Rotatable; TopDown looks straight down on the subject (or Centre) from Altitude,
//   Radius metres from the middle to each edge, north (−Z) up — a rendered local map.
//
// The pose is worked out here, headless (WorldViews.TryPose), so a test checks where the camera is; the
// client adds a view per such widget on screen, into a target the size of the widget's pixels, and the
// widget renderer draws the target where the image goes. An image or a view may also carry a fog mask
// (UiFogMask): what is not revealed is drawn over in FogColour, softly at the cell edges.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public enum ViewCamera
{
    // Nothing of the view's: the target is drawn by whatever names it (a camera entity, a render pass).
    None,
    // Round the subject, looking at it from in front.
    Orbit,
    // Straight down on the subject (or Centre), orthographic, north up: a map.
    TopDown,
}

// A picture a render target draws (see above). Without a Target it is an Image.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public class View : Image
{
    public const string Player = "player", Other = "other";

    private string? _target;
    private ViewCamera _camera;
    private bool _rotatable;
    private float _dragFrom;

    public override string TypeName => "view";

    // The render target shown, by name (a camera's `target`, a material's `rt:<name>`).
    public string? Target { get => _target; set { if (_target == value) return; _target = value; InvalidateVisual(); } }

    // Whose camera draws the target: none of the view's own, an orbit or a top-down one.
    public ViewCamera Camera { get => _camera; set { if (_camera == value) return; _camera = value; InvalidateVisual(); } }

    // What the camera looks at: "player" (the screen's subject, else the local player), "other" (the
    // screen's other one: the NPC talked to, the corpse looted) or an entity by name; empty: Centre.
    public string Subject { get; set; } = Player;

    // The point looked at when there is no subject (a fixed map), world metres.
    public Vector3 Centre { get; set; }

    // Orbit: metres from the point looked at, which is Height metres above the subject's origin.
    public float Distance { get; set; } = 2.5f;
    public float Height { get; set; } = 1f;

    // Degrees round the subject, from in front of it (0: facing the camera; 90: from its left side);
    // TopDown: the map turned.
    public float Yaw { get; set; }

    // Degrees above the subject (positive looks down on it).
    public float Pitch { get; set; } = 10f;

    // Degrees a second it turns by itself (a model on a turntable); 0: still.
    public float Spin { get; set; }

    // Orbit: the vertical field of view, degrees.
    public float FieldOfView { get; set; } = 35f;

    // TopDown: metres from the middle of the map to its edges (to the nearer pair, on a wide one), and how
    // far above the point looked at the camera is.
    public float Radius { get; set; } = 30f;
    public float Altitude { get; set; } = 100f;

    // The far clip plane, metres; the near one follows from the camera (5 cm for an orbit).
    public float Far { get; set; } = 500f;

    // The target's pixels across its longer side; 0: as many as the widget covers on screen.
    public int Resolution { get; set; }

    // The view's camera draws the sky and casts shadows (both off by default: a preview over the clear
    // colour, and no second shadow map for it).
    public bool Sky { get; set; }
    public bool Shadows { get; set; }

    // The player turns it: dragging across it, or Left and Right while it has focus (RotateStep each).
    public bool Rotatable
    {
        get => _rotatable;
        set { _rotatable = value; Focusable = value; }
    }

    public float RotateStep { get; set; } = 15f;

    // Degrees of Yaw per virtual unit dragged.
    public float DragRate { get; set; } = 0.5f;

    protected internal override void OnPointerPressed(Vector2 point) => _dragFrom = point.X;

    protected internal override void OnPointerDragged(Vector2 point)
    {
        if (!_rotatable) return;
        Yaw = Wrap(Yaw + (point.X - _dragFrom) * DragRate);
        _dragFrom = point.X;
    }

    protected internal override bool OnNavigate(UiNavigation direction)
    {
        if (!_rotatable) return false;
        switch (direction)
        {
            case UiNavigation.Left: Yaw = Wrap(Yaw - RotateStep); return true;
            case UiNavigation.Right: Yaw = Wrap(Yaw + RotateStep); return true;
            default: return false;
        }
    }

    private static float Wrap(float degrees)
    {
        degrees %= 360f;
        return degrees < 0f ? degrees + 360f : degrees;
    }
}

// Discovery fog over a map picture: a grid of cells, each 0 (unseen) to 255 (revealed), laid over the
// image as a whole — cell (0, 0) at its top-left — and, for Reveal, over a rectangle of the world's
// ground: Min is the north-west corner (least X, least Z) and Max the south-east one, so north (−Z) is
// up as on a top-down view. Version changes with any cell, so the client uploads it again only then.
// A kit keeps what a player discovered in one of these (and saves the cells); the UI only draws it.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class UiFogMask
{
    public const int MaxSize = 1024;
    private readonly byte[] _cells;

    public UiFogMask(int width, int height)
    {
        if (width < 1 || height < 1 || width > MaxSize || height > MaxSize)
            throw new ArgumentOutOfRangeException(nameof(width), $"A fog mask is {width}x{height}; each side is 1 to {MaxSize} cells.");
        Width = width;
        Height = height;
        _cells = new byte[width * height];
        Max = new Vector2(width, height);
    }

    public int Width { get; }
    public int Height { get; }

    // Bumped by every change to a cell.
    public int Version { get; private set; }

    // The ground it covers, world X and Z: Min the north-west corner, Max the south-east one.
    public Vector2 Min { get; set; }
    public Vector2 Max { get; set; }

    // Row by row from the top-left.
    public ReadOnlySpan<byte> Cells => _cells;

    public byte this[int x, int y]
    {
        get => _cells[Index(x, y)];
        set
        {
            ref var cell = ref _cells[Index(x, y)];
            if (cell == value) return;
            cell = value;
            Version++;
        }
    }

    private int Index(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            throw new ArgumentOutOfRangeException(nameof(x), $"Cell ({x}, {y}) is outside a {Width}x{Height} fog mask.");
        return y * Width + x;
    }

    // Every cell set to `value` (255: all revealed, 0: all hidden).
    public void Fill(byte value)
    {
        if (Array.TrueForAll(_cells, c => c == value)) return;
        Array.Fill(_cells, value);
        Version++;
    }

    // Copies cells in (a save being loaded); `cells` is Width × Height, row by row.
    public void Load(ReadOnlySpan<byte> cells)
    {
        if (cells.Length != _cells.Length)
            throw new ArgumentException($"A {Width}x{Height} fog mask has {_cells.Length} cells, not {cells.Length}.", nameof(cells));
        cells.CopyTo(_cells);
        Version++;
    }

    // The cell a world position is in, or false when it is outside the mask's ground.
    public bool CellAt(Vector3 position, out int x, out int y)
    {
        var size = Max - Min;
        float u = size.X != 0f ? (position.X - Min.X) / size.X : -1f, v = size.Y != 0f ? (position.Z - Min.Y) / size.Y : -1f;
        x = (int)MathF.Floor(u * Width);
        y = (int)MathF.Floor(v * Height);
        return u >= 0f && v >= 0f && x < Width && y < Height;
    }

    public bool IsRevealed(Vector3 position) => CellAt(position, out int x, out int y) && _cells[y * Width + x] == 255;

    // Reveals every cell whose centre is within `radius` metres of `position` (on the ground, X and Z),
    // with a cell's width of soft edge outside it. Returns how many cells it changed.
    public int Reveal(Vector3 position, float radius)
    {
        var size = Max - Min;
        if (!(size.X > 0f) || !(size.Y > 0f) || !(radius > 0f)) return 0;   // Max must be south-east of Min
        float cellW = size.X / Width, cellH = size.Y / Height;
        float soft = MathF.Max(cellW, cellH);
        float reach = radius + soft;
        int x0 = Math.Max(0, (int)MathF.Floor((position.X - reach - Min.X) / cellW));
        int x1 = Math.Min(Width - 1, (int)MathF.Floor((position.X + reach - Min.X) / cellW));
        int y0 = Math.Max(0, (int)MathF.Floor((position.Z - reach - Min.Y) / cellH));
        int y1 = Math.Min(Height - 1, (int)MathF.Floor((position.Z + reach - Min.Y) / cellH));

        int changed = 0;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float cx = Min.X + (x + 0.5f) * cellW, cz = Min.Y + (y + 0.5f) * cellH;
                float d = MathF.Sqrt((cx - position.X) * (cx - position.X) + (cz - position.Z) * (cz - position.Z));
                float t = d <= radius ? 1f : 1f - (d - radius) / soft;
                if (t <= 0f) continue;
                byte value = (byte)MathF.Round(t * 255f);
                ref var cell = ref _cells[y * Width + x];
                if (value <= cell) continue;
                cell = value;
                changed++;
            }
        if (changed > 0) Version++;
        return changed;
    }
}

// Where a view widget's camera is (WorldViews.TryPose): world metres, its rotation looking down −Z with
// +Y up as every camera's does, and its projection.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public struct ViewPose
{
    public Vector3 Position;
    public Quaternion Rotation;
    public bool Orthographic;
    public float FovY;          // radians, perspective
    public float OrthoHeight;   // metres from the bottom of the view to the top, orthographic
    public float Near, Far;
    public Entity Subject;      // what it looks at; null: Centre
}

// A view widget on screen that draws with a camera of its own, for the client to render (UiScreenStack.CollectViews).
internal struct UiWorldView
{
    public View View;
    public UiBindContext Context;
    public Rect Pixels;         // the picture's rect, viewport pixels
}

[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public static class WorldViews
{
    // The entity a view looks at: "player" is the screen's subject, else the first player-controlled
    // entity; "other" the screen's other one; anything else an entity by name. Null for none (Centre).
    public static Entity SubjectOf(World world, View view, in UiBindContext context)
    {
        string subject = view.Subject ?? "";
        if (subject.Length == 0) return default;
        if (subject == View.Player)
        {
            if (!context.Subject.IsNull && world.IsAlive(context.Subject)) return context.Subject;
            foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities) return entity;
            return default;
        }
        if (subject == View.Other) return !context.Other.IsNull && world.IsAlive(context.Other) ? context.Other : default;
        return world.FindByName(subject);
    }

    // Where the view's camera is, `time` seconds into the game (Spin turns it with time). False for a
    // view with no camera of its own, or whose subject is named and not there.
    public static bool TryPose(World world, View view, in UiBindContext context, double time, out ViewPose pose)
    {
        pose = default;
        if (view.Camera == ViewCamera.None) return false;
        var subject = SubjectOf(world, view, in context);
        if (subject.IsNull && (view.Subject ?? "").Length > 0) return false;

        Vector3 at = view.Centre;
        float facing = 0f;   // the subject's heading: radians round +Y, 0 facing −Z
        if (!subject.IsNull)
        {
            var where = world.TryGet<GlobalTransform>(subject, out var global) ? global.Current
                      : world.TryGet<Transform>(subject, out var local) ? Pose.FromLocal(local) : Pose.Identity;
            at = where.Position;
            var forward = Vector3.Transform(-Vector3.UnitZ, where.Rotation);
            if (forward.X * forward.X + forward.Z * forward.Z > 1e-8f) facing = MathF.Atan2(-forward.X, -forward.Z);
        }

        float yaw = (float)((view.Yaw + view.Spin * time) % 360d) * (MathF.PI / 180f);
        pose.Subject = subject;
        pose.Far = MathF.Max(view.Far, 1f);
        if (view.Camera == ViewCamera.TopDown)
        {
            // Straight down, the map's top towards −Z turned by Yaw (as ViewSource's test minimap is made).
            pose.Orthographic = true;
            pose.OrthoHeight = MathF.Max(view.Radius, 0.01f) * 2f;
            pose.Position = at + Vector3.UnitY * view.Altitude;
            pose.Rotation = Quaternion.CreateFromYawPitchRoll(yaw, -MathF.PI / 2f, 0f);
            pose.Near = 0.5f;
            pose.Far = MathF.Max(pose.Far, view.Altitude + 1f);
            return true;
        }

        // Orbit: from in front of the subject (its heading turned half round), up by Pitch, looking at the
        // point Height above its origin.
        float pitch = Math.Clamp(view.Pitch, -89f, 89f) * (MathF.PI / 180f);
        var focus = at + Vector3.UnitY * view.Height;
        pose.Rotation = Quaternion.CreateFromYawPitchRoll(facing + MathF.PI + yaw, -pitch, 0f);
        var look = Vector3.Transform(-Vector3.UnitZ, pose.Rotation);
        pose.Position = focus - look * MathF.Max(view.Distance, 0.01f);
        pose.FovY = Math.Clamp(view.FieldOfView, 1f, 170f) * (MathF.PI / 180f);
        pose.Near = 0.05f;
        return true;
    }
}
