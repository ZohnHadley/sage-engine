#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// Brushes into geometry (docs/design/15 §3, TODO F16).
//
// **A brush is planes; a renderer wants triangles and physics wants points.** Both come from the same
// place: the brush is the intersection of the half-spaces behind its planes, so a face's polygon is its
// plane clipped by every other plane in the brush. That is the whole algorithm, and it is worth
// understanding why it is the right one — a solid built this way *cannot* be open, inside out or
// non-convex, whatever the mapper did, because those are not things an intersection of half-spaces can
// be. A mesh format gives you none of that.
//
// Everything here is arithmetic on a parsed file, so it is engine-side and tested headlessly: the
// client only uploads the result, and physics only reads the hull points.

// How map units become engine units (14 §3). Quake's axes are Z-up and its unit is about an inch; ours
// are Y-up metres.
[Experimental("SAGE0122", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // brush maps from TrenchBroom (.map): replaced by the level editor (#61)
public readonly struct MapSpace
{
    // 32 units to the metre is Quake's own scale: a 56-unit player is 1.75 m, a 128-unit corridor is
    // four metres wide, and a mapper's habits carry over.
    public const float DefaultScale = 1f / 32f;

    // What a texture's size is assumed to be. Quake's texture coordinates are in texels, so turning
    // them into the 0..1 the sampler wants needs the image's size — which is a *client* fact the
    // simulation cannot see (07 §13). 64 is the size nearly every Quake-era texture is, and it is what
    // TrenchBroom itself assumes when it cannot find the texture. A material whose image is 128 tiles
    // twice as often as the editor showed; the fix, when it matters, is a size on the material record.
    public const float DefaultTextureSize = 64f;

    public MapSpace(float scale = DefaultScale, float textureSize = DefaultTextureSize)
    {
        _scale = scale;
        _textureSize = textureSize;
    }

    private readonly float _scale;
    private readonly float _textureSize;

    // Read through, so that a **zeroed** `MapSpace` means the defaults rather than a scale of zero.
    // This is not defensiveness: `new MapSpace()` does not run the constructor above — a constructor
    // whose parameters are all optional is not a parameterless one — so the zero value is what most
    // callers will actually hand over, and a zero scale collapses a level to a point.
    public float Scale => _scale > 0f ? _scale : DefaultScale;
    public float TextureSize => _textureSize > 0f ? _textureSize : DefaultTextureSize;

    // Map space (x east, y north, z up) to engine space (x east, y up, z south). The determinant is +1,
    // so this is a rotation and not a mirror — which matters more than it looks: a mirror would flip
    // every polygon's winding and turn the level inside out.
    public Vector3 ToEngine(Vector3 map) => new(map.X * Scale, map.Z * Scale, -map.Y * Scale);

    // Directions convert the same way, without the scale.
    public Vector3 DirectionToEngine(Vector3 map) => new(map.X, map.Z, -map.Y);
}

// One face of a built brush: a convex polygon in engine space, wound for the renderer.
public sealed class LevelFace
{
    public required string Texture { get; init; }
    public required Vector3 Normal { get; init; }
    public required Vector3[] Positions { get; init; }
    public required Vector2[] Uvs { get; init; }
}

// One built brush: its faces, and the points physics needs to make a convex hull of it.
[Experimental("SAGE0122", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // brush maps from TrenchBroom (.map): replaced by the level editor (#61)
public sealed class LevelBrush
{
    public required LevelFace[] Faces { get; init; }
    public required Vector3[] Hull { get; init; }
    public required Vector3 Min { get; init; }
    public required Vector3 Max { get; init; }

    public Vector3 Centre => (Min + Max) * 0.5f;
}

[Experimental("SAGE0122", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // brush maps from TrenchBroom (.map): replaced by the level editor (#61)
public static class BrushGeometry
{
    // Half the size of the world a brush can be built in, in map units. The starting polygon for a face
    // has to be bigger than the brush; Quake's own tools used the same trick with the same number.
    private const float WorldHalfSize = 16384f;

    // How near a vertex has to be to a plane to count as on it, and to another vertex to be the same
    // one. Map coordinates are usually integers, and floating point clipping of six planes leaves
    // vertices a whisker apart; welding them is what keeps a cube at eight corners instead of
    // twenty-four.
    private const float OnPlane = 0.01f;
    private const float Weld = 0.05f;

    // Builds one brush. Returns false for a brush that encloses nothing — which a mapper can write by
    // accident (two parallel planes facing away from each other) and which must not reach physics as an
    // empty hull.
    public static bool TryBuild(MapBrush brush, MapSpace space, out LevelBrush built, out string error)
    {
        built = null!;
        error = "";

        foreach (var face in brush.Faces)
        {
            // The three points are written clockwise seen from outside, so this normal points out of
            // the brush and the solid is where dot(n, x) <= d. (Verified against the canonical Quake
            // brush: all three points at x = -64 give normal (-1, 0, 0), distance 64.)
            var normal = Vector3.Cross(face.P3 - face.P1, face.P2 - face.P1);
            float length = normal.Length();
            if (length < 1e-6f)
            {
                error = $"line {face.Line}: the three points of a face are in a line, so it has no plane";
                return false;
            }
            face.Normal = normal / length;
            face.Distance = Vector3.Dot(face.Normal, face.P1);
            face.Polygon.Clear();
        }

        var faces = new List<LevelFace>(brush.Faces.Count);
        var hull = new List<Vector3>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var face in brush.Faces)
        {
            var polygon = StartingQuad(face.Normal, face.Distance);

            foreach (var other in brush.Faces)
            {
                if (ReferenceEquals(other, face)) continue;
                polygon = Clip(polygon, other.Normal, other.Distance);
                if (polygon.Count < 3) break;
            }

            // Fewer than three corners means this plane contributes no surface: a redundant face, which
            // mappers leave behind constantly (carve a brush, keep a plane). Not an error.
            if (polygon.Count < 3) continue;

            WeldInPlace(polygon);
            if (polygon.Count < 3) continue;

            var positions = new Vector3[polygon.Count];
            var uvs = new Vector2[polygon.Count];
            for (int i = 0; i < polygon.Count; i++)
            {
                uvs[i] = TextureCoordinate(face, polygon[i], space.TextureSize);
                positions[i] = space.ToEngine(polygon[i]);
                min = Vector3.Min(min, positions[i]);
                max = Vector3.Max(max, positions[i]);
                AddHullPoint(hull, positions[i]);
            }

            faces.Add(new LevelFace
            {
                Texture = face.Texture,
                Normal = Vector3.Normalize(space.DirectionToEngine(face.Normal)),
                Positions = positions,
                Uvs = uvs,
            });

            face.Polygon.AddRange(polygon);
        }

        if (faces.Count < 4 || hull.Count < 4)
        {
            error = $"line {brush.Line}: the brush's planes enclose nothing";
            return false;
        }

        built = new LevelBrush { Faces = faces.ToArray(), Hull = hull.ToArray(), Min = min, Max = max };
        return true;
    }

    // Builds every brush of a map, skipping (with a warning) the ones that enclose nothing, so one bad
    // solid costs its own geometry and not the level.
    public static List<LevelBrush> Build(IEnumerable<MapBrush> brushes, MapSpace space, out int skipped) =>
        Build(brushes, space, out skipped, quiet: false);

    // `quiet` counts the skipped brushes without saying so: a content check reading a level whose own
    // load will say it (MapLoader.Read).
    internal static List<LevelBrush> Build(IEnumerable<MapBrush> brushes, MapSpace space, out int skipped, bool quiet)
    {
        var built = new List<LevelBrush>();
        skipped = 0;
        foreach (var brush in brushes)
        {
            if (TryBuild(brush, space, out var one, out string error)) built.Add(one);
            else { skipped++; if (!quiet) Log.Warn(LogCat.Level, $"Brush skipped: {error}"); }
        }
        return built;
    }

    // A convex polygon as a triangle fan. Convexity is guaranteed by the clipping above, so a fan is
    // correct and there is nothing to decide.
    public static void Triangulate(int vertexCount, int firstVertex, List<int> indices)
    {
        for (int i = 2; i < vertexCount; i++)
        {
            indices.Add(firstVertex);
            indices.Add(firstVertex + i - 1);
            indices.Add(firstVertex + i);
        }
    }

    // A square on the plane, big enough to contain any brush, to be cut down by the other planes.
    private static List<Vector3> StartingQuad(Vector3 normal, float distance)
    {
        // Any axis that is not the normal will do for the first edge.
        var axis = MathF.Abs(normal.X) > 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(axis, normal));
        var v = Vector3.Cross(normal, u);
        var centre = normal * distance;

        // Wound so that (b - a) x (c - a) points *along* the normal, which is the convention the rest of
        // the engine reads: a face's winding and its normal agree. Clipping preserves the order, so
        // every face comes out of the builder wound the same way round its own normal. (The renderer
        // wants the opposite and flips once when it builds the buffers, the same as for glTF — that is
        // its quirk, and it does not belong in the geometry.)
        return new List<Vector3>(8)
        {
            centre - u * WorldHalfSize - v * WorldHalfSize,
            centre + u * WorldHalfSize - v * WorldHalfSize,
            centre + u * WorldHalfSize + v * WorldHalfSize,
            centre - u * WorldHalfSize + v * WorldHalfSize,
        };
    }

    // Sutherland-Hodgman against one half-space, keeping what is behind the plane.
    private static List<Vector3> Clip(List<Vector3> polygon, Vector3 normal, float distance)
    {
        var result = new List<Vector3>(polygon.Count + 1);

        for (int i = 0; i < polygon.Count; i++)
        {
            var current = polygon[i];
            var next = polygon[(i + 1) % polygon.Count];
            float a = Vector3.Dot(normal, current) - distance;
            float b = Vector3.Dot(normal, next) - distance;

            if (a <= OnPlane) result.Add(current);

            // Crossing the plane: add the crossing point. The `OnPlane` band on both sides stops a
            // vertex that sits exactly on the plane from producing a duplicate.
            if ((a > OnPlane && b < -OnPlane) || (a < -OnPlane && b > OnPlane))
                result.Add(current + (next - current) * (a / (a - b)));
        }

        return result;
    }

    private static void WeldInPlace(List<Vector3> polygon)
    {
        for (int i = polygon.Count - 1; i >= 0; i--)
        {
            var a = polygon[i];
            var b = polygon[(i + polygon.Count - 1) % polygon.Count];
            if (Vector3.DistanceSquared(a, b) < Weld * Weld) polygon.RemoveAt(i);
        }
    }

    private static void AddHullPoint(List<Vector3> hull, Vector3 point)
    {
        foreach (var existing in hull)
            if (Vector3.DistanceSquared(existing, point) < 1e-6f) return;
        hull.Add(point);
    }

    // Quake's texture coordinates, in texels, divided down by the assumed texture size.
    private static Vector2 TextureCoordinate(MapFace face, Vector3 point, float textureSize)
    {
        Vector3 u, v;
        if (face.HasAxes)
        {
            // Valve 220 wrote the axes down, rotation included.
            u = face.UAxis;
            v = face.VAxis;
        }
        else
        {
            var axis = StandardAxes(face.Normal, out u, out v);
            if (face.Rotation != 0f)
            {
                // Rotated about the **base** axis, not the face normal. Quake rotates the two derived
                // axes within their own coordinate pair, which is the same as turning them about the
                // third axis — and on a wall that is not axis-aligned the two are different rotations,
                // so a sloped, rotated face textured one way in the editor would arrive textured
                // another. Axis-aligned faces, which is most of a level, cannot tell the difference,
                // which is exactly why it is worth writing down.
                var rotation = Matrix4x4.CreateFromAxisAngle(axis, face.Rotation * MathF.PI / 180f);
                u = Vector3.TransformNormal(u, rotation);
                v = Vector3.TransformNormal(v, rotation);
            }
        }

        float s = Vector3.Dot(point, u) / face.UScale + face.UOffset;
        float t = Vector3.Dot(point, v) / face.VScale + face.VOffset;
        return new Vector2(s / textureSize, t / textureSize);
    }

    // The six base axis pairs of the standard format, chosen by whichever the face most faces. This is
    // Quake's own table, and the reason a standard-format wall's texture slides when the wall is
    // rotated: the axes come from the normal, not from the face.
    private static Vector3 StandardAxes(Vector3 normal, out Vector3 u, out Vector3 v)
    {
        Span<Vector3> normals = stackalloc Vector3[]
        {
            new(0, 0, 1), new(0, 0, -1), new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0),
        };
        Span<Vector3> us = stackalloc Vector3[]
        {
            new(1, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 1, 0), new(1, 0, 0), new(1, 0, 0),
        };
        Span<Vector3> vs = stackalloc Vector3[]
        {
            new(0, -1, 0), new(0, -1, 0), new(0, 0, -1), new(0, 0, -1), new(0, 0, -1), new(0, 0, -1),
        };

        int best = 0;
        float bestDot = float.MinValue;
        for (int i = 0; i < normals.Length; i++)
        {
            float dot = Vector3.Dot(normal, normals[i]);
            if (dot > bestDot) { bestDot = dot; best = i; }
        }
        u = us[best];
        v = vs[best];
        return normals[best];
    }
}
