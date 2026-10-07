#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage.Simulation;

// Blockout brushes (issue #61, docs/design/15 §3): boxes, wedges and cylinders a designer builds a room
// from in Sage's own editor, so a level no longer needs TrenchBroom.
//
// **A brush is a placement.** It is a prefab with the `brush` part (`sage:brush`, `sage:wedge`,
// `sage:cylinder`), placed in a placements document like a crate, its shape, size and materials written
// as the placement's overrides of that part. So everything a placement has, a brush has for nothing: it
// is picked, moved, turned and scaled with the gizmos, duplicated, deleted and undone; it is saved into
// the level's document with comments kept; a mod patches it as it patches any record; `sage validate`
// checks it (the override is checked as the part's options, and the part checks itself, ICheckedPart);
// and play-in-editor plays it.
//
// **What it becomes is what an imported `.map` brush becomes** (15 §10a): a convex polygon per face, wound
// the engine's way (a face's winding agrees with its normal), a static convex hull for physics, and a
// mesh per material that the client builds (Sage.Client's BlockoutMeshSystem). The geometry is made here,
// in the simulation, so a headless server has the collision and no meshes.
//
//   { "prefab": "sage:wedge", "at": [4, 0, -2], "yaw": 90, "id": "ramp",
//     "overrides": { "parts": { "brush": { "size": [2, 1, 4], "material": "game:stone", "top": "game:planks" } } } }
//
// The placement's `at` is the middle of the brush's **floor**, so a brush placed on a surface stands on
// it; its yaw, pitch and roll turn it, and its scale multiplies `size` (the scale gizmo resizes a brush).

// What a brush is shaped like.
[Experimental("SAGE0121", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // scenes and placements (#29): the level editor (#61) will reshape them
public enum BrushShape
{
    Box,        // six faces
    Wedge,      // a ramp rising to its north (-Z) side: top (the slope), bottom, north, east, west
    Cylinder,   // `sides` faces round it, a top and a bottom; an ellipse when its size is not square
}

// A face of a brush, by where it faces before the brush is turned: north is -Z, east is +X. A cylinder's
// faces round it are all `Side`.
[Experimental("SAGE0121", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // scenes and placements (#29): the level editor (#61) will reshape them
public enum BrushFace
{
    Top,
    Bottom,
    North,
    South,
    East,
    West,
    Side,
}

// "brush": { "shape": "Wedge", "size": [2, 1, 4], "material": "game:stone", "top": "game:grass" }
//
// A material per face: a face names its own, else the brush's `material`, else the engine's plain lit one
// (a blockout in grey is work in progress, not a mistake).
[PrefabPart("brush", Plugin = RegistrationOwners.Core)]
[Experimental("SAGE0121", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // scenes and placements (#29): the level editor (#61) will reshape them
public sealed class BrushPart : IPrefabPart, ICheckedPart
{
    public const int MinSides = 3, MaxSides = 64;

    [Property(Category = "Shape", Tooltip = "Box, Wedge (a ramp rising to the north) or Cylinder")]
    public BrushShape Shape = BrushShape.Box;
    [Property(Category = "Shape", Min = 0, Unit = "m", Tooltip = "Width (X), height (Y) and depth (Z); the placement's scale multiplies it")]
    public Vector3 Size = new(2f, 2f, 2f);
    [Property(Category = "Shape", Min = MinSides, Max = MaxSides, Tooltip = "Cylinder only: how many faces round it")]
    public int Sides = 12;

    [RecordRef("material"), Property(Category = "Materials", Tooltip = "Every face's material unless the face names its own; empty = the plain lit material")]
    public RecordId Material;
    [RecordRef("material"), Property(Category = "Materials", Tooltip = "The top face (a wedge's slope); empty = the brush's material")]
    public RecordId Top;
    [RecordRef("material"), Property(Category = "Materials", Tooltip = "The bottom face; empty = the brush's material")]
    public RecordId Bottom;
    [RecordRef("material"), Property(Category = "Materials", Tooltip = "The face to -Z before the brush is turned (a wedge's tall end); empty = the brush's material")]
    public RecordId North;
    [RecordRef("material"), Property(Category = "Materials", Tooltip = "Box only: the face to +Z before the brush is turned; empty = the brush's material")]
    public RecordId South;
    [RecordRef("material"), Property(Category = "Materials", Tooltip = "Box and wedge: the face to +X before the brush is turned; empty = the brush's material")]
    public RecordId East;
    [RecordRef("material"), Property(Category = "Materials", Tooltip = "Box and wedge: the face to -X before the brush is turned; empty = the brush's material")]
    public RecordId West;
    [RecordRef("material"), Property(Category = "Materials", Tooltip = "Cylinder only: the faces round it; empty = the brush's material")]
    public RecordId Side;
    [Property(Category = "Materials", Min = 0.01f, Max = 100, Unit = "m", Tooltip = "How many metres one repeat of a texture covers; textures line up across brushes")]
    public float TextureScale = 1f;

    [Property(Category = "Physics", Tooltip = "Physics layer by name; empty = default")]
    public string Layer = "";
    [RecordRef("physics_material"), Property(Category = "Physics", Tooltip = "What it is made of: footsteps and impacts; empty = none")]
    public RecordId Surface;

    // The faces a shape has, in the order its geometry lists them (a cylinder's sides are one name).
    public static IReadOnlyList<BrushFace> FacesOf(BrushShape shape) => shape switch
    {
        BrushShape.Wedge => WedgeFaces,
        BrushShape.Cylinder => CylinderFaces,
        _ => BoxFaces,
    };

    private static readonly BrushFace[] BoxFaces = { BrushFace.Top, BrushFace.Bottom, BrushFace.North, BrushFace.South, BrushFace.East, BrushFace.West };
    private static readonly BrushFace[] WedgeFaces = { BrushFace.Top, BrushFace.Bottom, BrushFace.North, BrushFace.East, BrushFace.West };
    private static readonly BrushFace[] CylinderFaces = { BrushFace.Top, BrushFace.Bottom, BrushFace.Side };

    // A face's own material field, as the part writes it.
    public RecordId FaceMaterial(BrushFace face) => face switch
    {
        BrushFace.Top => Top,
        BrushFace.Bottom => Bottom,
        BrushFace.North => North,
        BrushFace.South => South,
        BrushFace.East => East,
        BrushFace.West => West,
        _ => Side,
    };

    // What a face is drawn with: its own material, else the brush's; empty means the plain lit one.
    public RecordId MaterialOf(BrushFace face) => FaceMaterial(face) is { IsEmpty: false } own ? own : Material;

    public void Apply(in PrefabPartContext ctx)
    {
        foreach (var (field, message) in Problems(faces: true)) ctx.Error($"{field}: {message}");
        string where = $"{ctx.Where}: {ctx.Part}";
        Build(ctx.World, ctx.Entity, warn: message => Log.Warn(LogCat.Records, $"{where}: {message}"));
    }

    // The brush built on `entity` from these options and its transform as it stands: its geometry, and a
    // static hull (its corners turned with the entity, since a static has no rotation of its own) at its
    // position. A world with no physics (a tool's) has the geometry only. One built before is replaced:
    // a placement's scale is put on after its parts (SpawnTree), and the brush is built again for it.
    internal void Build(World world, Entity entity, Action<string> warn)
    {
        var transform = world.Has<Transform>(entity) ? world.Get<Transform>(entity) : Transform.At(Vector3.Zero);
        var scale = transform.LocalScale == Vector3.Zero ? Vector3.One : transform.LocalScale;
        var size = Vector3.Max(Size * scale, new Vector3(BlockoutGeometry.Smallest));
        int sides = Math.Clamp(Sides, MinSides, MaxSides);
        float tile = TextureScale > 0f ? TextureScale : 1f;
        var built = BlockoutGeometry.Build(Shape, size, sides, this, transform.LocalRotation, transform.LocalPosition, tile);

        bool hasPhysics = world.Resources.TryGet<IPhysicsWorld>(out var physics) && physics != null;
        if (world.Has<PhysicsBody>(entity) && world.Has<BlockoutBrush>(entity))
        {
            if (hasPhysics) physics!.RemoveBody(world.Get<PhysicsBody>(entity));
            world.Remove<PhysicsBody>(entity);
        }

        if (hasPhysics)
        {
            byte layer = physics!.Layers.Default;
            if (!string.IsNullOrEmpty(Layer) && !physics.Layers.TryIndexOf(Layer, out layer))
                warn($"no physics layer '{Layer}'; using '{physics.Layers.Name(layer)}'");
            var body = physics.AddHull(entity, built.Hull, transform.LocalPosition, layer);
            if (body.IsStatic)   // AddHull's failure is the default body
            {
                world.Add(entity, body);
                if (!Surface.IsEmpty)
                {
                    var faces = new SurfaceFace[built.Brush.Faces.Length];
                    for (int i = 0; i < faces.Length; i++)
                        faces[i] = new SurfaceFace(Vector3.Transform(built.Brush.Faces[i].Normal, transform.LocalRotation), Surface);
                    physics.SetSurfaces(body, faces);
                }
            }
            else warn("its hull could not be built; it has no collision");
        }

        var brush = new BlockoutBrush { Shape = Shape, Size = size, Sides = sides, Geometry = built.Brush, Faces = built.Faces, Options = this };
        if (world.Has<BlockoutBrush>(entity)) world.Get<BlockoutBrush>(entity) = brush;
        else world.Add(entity, brush);
    }

    // A brush's entity was scaled after its parts went on (a placement's `scale`, SpawnTree): built again.
    internal static void Rescaled(World world, Entity entity)
    {
        if (!world.Has<BlockoutBrush>(entity) || world.Get<BlockoutBrush>(entity).Options is not { } options) return;
        options.Build(world, entity, warn: message => Log.Warn(LogCat.Records, $"{World.Describe(entity)}: brush: {message}"));
    }

    // What is wrong with these options, field by field: checked when content loads (ICheckedPart) and
    // said again when one spawns. A partial body (a placement's override of two fields) is checked as
    // what it says, the rest at their defaults.
    internal IEnumerable<(string Field, string Message)> Problems(bool faces)
    {
        if (!(Size.X > 0f && Size.Y > 0f && Size.Z > 0f))
            yield return ("Size", $"a brush's size must be above 0 on every axis, not {Size}");
        if (Shape == BrushShape.Cylinder && (Sides < MinSides || Sides > MaxSides))
            yield return ("Sides", $"a cylinder has {MinSides} to {MaxSides} sides, not {Sides}");
        if (!(TextureScale > 0f))
            yield return ("TextureScale", $"must be above 0, not {TextureScale}");
        if (!faces) yield break;
        var has = (BrushFace[])FacesOf(Shape);
        foreach (BrushFace face in Enum.GetValues<BrushFace>())
            if (!FaceMaterial(face).IsEmpty && Array.IndexOf(has, face) < 0)
                yield return (face.ToString(), $"a {Shape.ToString().ToLowerInvariant()} has no {face.ToString().ToLowerInvariant()} face "
                                             + $"(it has {string.Join(", ", has.Select(f => f.ToString().ToLowerInvariant()))})");
    }

    // A body that does not say its shape is part of one (a placement's override of a cylinder's `side`):
    // which faces it may name is checked where the whole is known (BlockoutChecks.CheckPlacement).
    void ICheckedPart.Check(JsonNode body, string path, RecordCheck check)
    {
        bool complete = body is JsonObject fields && fields.Any(kv => string.Equals(kv.Key, "shape", StringComparison.OrdinalIgnoreCase));
        foreach (var (field, message) in Problems(complete)) check.Error($"{path}.{field}", message);
    }
}

// A built brush (issue #61): its shape and size as built, and the geometry the client meshes and the
// editor picks faces from. Not saved: the placement builds it again when a load re-places the content.
[Transient]
[Component("sage:blockout_brush")]
[Experimental("SAGE0121", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // scenes and placements (#29): the level editor (#61) will reshape them
public struct BlockoutBrush : IComponent
{
    public BrushShape Shape;
    public Vector3 Size;                // metres, the placement's scale included
    public int Sides;                   // a cylinder's

    internal LevelBrush? Geometry;      // faces relative to the entity, unturned; the hull, turned
    internal BrushFace[]? Faces;        // which face each of Geometry's faces is
    internal BrushPart? Options;        // what it was built from

    // How many polygons it has (a cylinder's sides each count).
    public readonly int FaceCount => Geometry?.Faces.Length ?? 0;

    // What `face` is drawn with (empty: the plain lit material).
    public readonly RecordId MaterialOf(BrushFace face) => Options?.MaterialOf(face) ?? default;

    // The material the whole brush names, and the one `face` names itself (empty: it takes the brush's).
    public readonly RecordId Material => Options?.Material ?? default;
    public readonly RecordId FaceMaterial(BrushFace face) => Options?.FaceMaterial(face) ?? default;

    // The face a ray (origin space) meets first, for a brush standing at `position` turned by `rotation`
    // (its transform's), and how far along the ray; null when it misses.
    public readonly (BrushFace Face, float Distance)? FaceHit(Vector3 position, Quaternion rotation, Vector3 origin, Vector3 direction)
    {
        if (Geometry is not { } geometry || Faces is not { } faces) return null;
        return BlockoutGeometry.FaceHit(geometry, position, rotation, origin, direction) is { } hit ? (faces[hit.Index], hit.Distance) : null;
    }
}

// The arithmetic of a blockout brush: polygons, hull and texture coordinates, with nothing to do with any
// world. Faces are wound so that a face's winding agrees with its normal (BrushGeometry's convention; the
// renderer flips once when it builds buffers).
internal static class BlockoutGeometry
{
    // No axis is built thinner than this, so a brush that is flat by mistake still has a hull.
    public const float Smallest = 0.01f;

    public readonly record struct Built(LevelBrush Brush, BrushFace[] Faces, Vector3[] Hull);

    public static Built Build(BrushShape shape, Vector3 size, int sides, BrushPart materials, Quaternion rotation, Vector3 position, float tile)
    {
        float hx = size.X * 0.5f, h = size.Y, hz = size.Z * 0.5f;
        var polygons = new List<(BrushFace Face, Vector3[] Points)>();

        switch (shape)
        {
            case BrushShape.Wedge:
                polygons.Add((BrushFace.Top, new[] { new Vector3(-hx, h, -hz), new Vector3(hx, h, -hz), new Vector3(hx, 0, hz), new Vector3(-hx, 0, hz) }));
                polygons.Add((BrushFace.Bottom, new[] { new Vector3(-hx, 0, -hz), new Vector3(hx, 0, -hz), new Vector3(hx, 0, hz), new Vector3(-hx, 0, hz) }));
                polygons.Add((BrushFace.North, new[] { new Vector3(-hx, 0, -hz), new Vector3(hx, 0, -hz), new Vector3(hx, h, -hz), new Vector3(-hx, h, -hz) }));
                polygons.Add((BrushFace.East, new[] { new Vector3(hx, 0, -hz), new Vector3(hx, h, -hz), new Vector3(hx, 0, hz) }));
                polygons.Add((BrushFace.West, new[] { new Vector3(-hx, 0, -hz), new Vector3(-hx, h, -hz), new Vector3(-hx, 0, hz) }));
                break;
            case BrushShape.Cylinder:
            {
                var ring = new Vector3[sides];
                for (int i = 0; i < sides; i++)
                {
                    // Half a step round, so a four-sided cylinder is a square standing on its corners'
                    // diagonals rather than a diamond.
                    float angle = (i + 0.5f) * MathF.Tau / sides;
                    ring[i] = new Vector3(MathF.Cos(angle) * hx, 0f, MathF.Sin(angle) * hz);
                }
                var top = new Vector3[sides];
                for (int i = 0; i < sides; i++) top[i] = ring[i] + new Vector3(0, h, 0);
                polygons.Add((BrushFace.Top, top));
                polygons.Add((BrushFace.Bottom, (Vector3[])ring.Clone()));
                for (int i = 0; i < sides; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % sides];
                    polygons.Add((BrushFace.Side, new[] { a, b, b + new Vector3(0, h, 0), a + new Vector3(0, h, 0) }));
                }
                break;
            }
            default:
                polygons.Add((BrushFace.Top, new[] { new Vector3(-hx, h, -hz), new Vector3(hx, h, -hz), new Vector3(hx, h, hz), new Vector3(-hx, h, hz) }));
                polygons.Add((BrushFace.Bottom, new[] { new Vector3(-hx, 0, -hz), new Vector3(hx, 0, -hz), new Vector3(hx, 0, hz), new Vector3(-hx, 0, hz) }));
                polygons.Add((BrushFace.North, new[] { new Vector3(-hx, 0, -hz), new Vector3(hx, 0, -hz), new Vector3(hx, h, -hz), new Vector3(-hx, h, -hz) }));
                polygons.Add((BrushFace.South, new[] { new Vector3(-hx, 0, hz), new Vector3(hx, 0, hz), new Vector3(hx, h, hz), new Vector3(-hx, h, hz) }));
                polygons.Add((BrushFace.East, new[] { new Vector3(hx, 0, -hz), new Vector3(hx, 0, hz), new Vector3(hx, h, hz), new Vector3(hx, h, -hz) }));
                polygons.Add((BrushFace.West, new[] { new Vector3(-hx, 0, -hz), new Vector3(-hx, 0, hz), new Vector3(-hx, h, hz), new Vector3(-hx, h, -hz) }));
                break;
        }

        // The middle of the solid: every face's normal points away from it, which is what orients them.
        var centre = new Vector3(0f, h * (shape == BrushShape.Wedge ? 1f / 3f : 0.5f), shape == BrushShape.Wedge ? -hz / 3f : 0f);

        var faces = new LevelFace[polygons.Count];
        var names = new BrushFace[polygons.Count];
        var hull = new List<Vector3>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (int f = 0; f < polygons.Count; f++)
        {
            var (face, points) = polygons[f];
            var normal = Vector3.Normalize(Vector3.Cross(points[1] - points[0], points[2] - points[0]));
            var middle = Vector3.Zero;
            foreach (var p in points) middle += p;
            middle /= points.Length;
            if (Vector3.Dot(normal, middle - centre) < 0f)
            {
                Array.Reverse(points);
                normal = -normal;
            }

            var turnedNormal = Vector3.Normalize(Vector3.Transform(normal, rotation));
            var uvs = new Vector2[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                var turned = Vector3.Transform(points[i], rotation);
                uvs[i] = WorldUv(position + turned, turnedNormal, tile);
                min = Vector3.Min(min, points[i]);
                max = Vector3.Max(max, points[i]);
                AddPoint(hull, turned);
            }

            var material = materials.MaterialOf(face);
            faces[f] = new LevelFace
            {
                Texture = material.IsEmpty ? "" : material.ToString(),
                Normal = normal,
                Positions = points,
                Uvs = uvs,
            };
            names[f] = face;
        }

        var brush = new LevelBrush { Faces = faces, Hull = hull.ToArray(), Min = min, Max = max };
        return new Built(brush, names, brush.Hull);
    }

    // Texture coordinates from where a corner is in the world, projected along the axis the face most
    // faces: two brushes side by side continue one another's texture, and a resized brush tiles rather
    // than stretches. `v` runs down the world so a wall's texture stands upright.
    private static Vector2 WorldUv(Vector3 world, Vector3 normal, float tile)
    {
        var n = Vector3.Abs(normal);
        Vector2 uv;
        if (n.Y >= n.X && n.Y >= n.Z) uv = new Vector2(world.X, world.Z);
        else if (n.X >= n.Z) uv = new Vector2(normal.X >= 0f ? -world.Z : world.Z, -world.Y);
        else uv = new Vector2(normal.Z >= 0f ? world.X : -world.X, -world.Y);
        return uv / tile;
    }

    private static void AddPoint(List<Vector3> hull, Vector3 point)
    {
        foreach (var existing in hull)
            if (Vector3.DistanceSquared(existing, point) < 1e-8f) return;
        hull.Add(point);
    }

    // Which of a built brush's faces a ray (origin space) meets first, and where: for picking a face to
    // paint. `position` and `rotation` are the brush's. Null when it misses.
    public static (int Index, float Distance)? FaceHit(LevelBrush brush, Vector3 position, Quaternion rotation, Vector3 origin, Vector3 direction)
    {
        var inverse = Quaternion.Inverse(rotation);
        var o = Vector3.Transform(origin - position, inverse);
        var d = Vector3.Transform(direction, inverse);
        (int, float)? best = null;
        for (int f = 0; f < brush.Faces.Length; f++)
        {
            var face = brush.Faces[f];
            float facing = Vector3.Dot(face.Normal, d);
            if (facing >= -1e-6f) continue;   // seen from behind or edge on
            float t = Vector3.Dot(face.Normal, face.Positions[0] - o) / facing;
            if (t < 0f || (best is { } b && t >= b.Item2)) continue;
            var hit = o + d * t;
            if (!Inside(face, hit)) continue;
            best = (f, t);
        }
        return best;
    }

    // Inside a convex polygon wound about its normal: on the inner side of every edge.
    private static bool Inside(LevelFace face, Vector3 point)
    {
        var p = face.Positions;
        for (int i = 0; i < p.Length; i++)
        {
            var edge = p[(i + 1) % p.Length] - p[i];
            if (Vector3.Dot(Vector3.Cross(edge, point - p[i]), face.Normal) < -1e-4f) return false;
        }
        return true;
    }
}

// A placement's brush as a whole (issue #61): its prefab's `brush` part with the placement's override merged
// in, so an override of a cylinder's `side` is checked against the cylinder it is part of.
internal static class BlockoutChecks
{
    public static void CheckPlacement(Engine engine, Placement placement, string path, RecordCheck check)
    {
        if (placement.Overrides?.Parts is not { } parts || !parts.Any(kv => string.Equals(kv.Key, "brush", StringComparison.OrdinalIgnoreCase))) return;
        var body = parts.First(kv => string.Equals(kv.Key, "brush", StringComparison.OrdinalIgnoreCase)).Value;
        if (body is JsonObject own && own.Any(kv => string.Equals(kv.Key, "shape", StringComparison.OrdinalIgnoreCase))) return;   // checked as it is
        var id = placement.Prefab.Id;
        if (id.IsEmpty || !check.TryGet(id, out PrefabRecord? prefab) || prefab == null) return;
        var merged = PrefabOverriding.Apply(engine, id, prefab, placement.Overrides);
        if (merged.Parts?.FirstOrDefault(kv => string.Equals(kv.Key, "brush", StringComparison.OrdinalIgnoreCase)).Value is not JsonObject whole) return;

        string? outer = RecordParseContext.Namespace;
        RecordParseContext.Namespace = id.Namespace;
        try
        {
            if (whole.Deserialize<BrushPart>(check.Json) is not { } brush) return;
            foreach (var (field, message) in brush.Problems(faces: true))
                if (Enum.TryParse<BrushFace>(field, out _)) check.Error($"{path}.Overrides.Parts.brush.{field}", message);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or NotSupportedException)
        {
            // What does not read is said by the override's own check.
        }
        finally
        {
            RecordParseContext.Namespace = outer;
        }
    }
}
