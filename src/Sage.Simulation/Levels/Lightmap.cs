#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;

namespace Sage.Simulation;

// Lightmaps for brush levels (issue #313, docs/design/06 §3.9, 07 §3.2; HL1's answer for a large level).
//
// **What it is.** A level whose record asks for one (`"lightmap": 4`, texels per metre) has every face of its
// worldspawn brushes unwrapped into a chart of one atlas, and each texel of that atlas lit on the CPU: the
// direct light of the level's *baked* lamps (`PointLight.Baked`, a `light` part's `"baked": true`) with a
// shadow ray to each against the brushes, and in alpha how much of the sky the texel can see (a handful of
// rays; the ambient term of an interior was the sky's, lighting the inside of a closed room exactly as it
// lit the outside of it). The client draws those faces with lit.fx's `Lightmapped` technique, which adds the
// sun (still shadowed by the shadow map, because it moves), the ambient times the sky term, the baked light
// and the dynamic point lights on top — which is why a baked lamp is left out of a lightmapped draw's four,
// and still lights everything that is not lightmapped (a prop, a creature, a door).
//
// **When.** At load: once the level is placed and has spawned its entities (MapCollisionSystem), so the lamps
// are the entities the level placed, with their prefab's values and the map's own keys already applied. A
// bake is cached under `user://cache/lightmaps/`, named by a hash of everything it read (geometry, lamps,
// settings, format), so a second load of the same level reads it back, and a changed level cannot read a
// stale one. Headless and deterministic: the tests bake levels and read texels.
//
// **What it is not**, yet: bounce light (the sky term's floor stands in for it), light styles (a baked lamp
// cannot switch or flicker: it is in the texture), brush entities (a door moves; it stays dynamically lit
// and does not cast baked shadows), and terrain as an occluder.
internal readonly struct LightmapSettings
{
    // Texels per metre. HL1's 16 map units a luxel is two a metre at Quake's scale; 4 is sharper and cheap.
    public float Density { get; init; }

    // Sky rays per texel; 0 leaves the ambient alone (sky term 1).
    public int SkyRays { get; init; }

    // How far a sky ray looks for a brush before it counts as having reached the sky, in metres.
    public float SkyDistance { get; init; }

    // The least sky a texel keeps: what bounce light would have given a room with a door, roughly.
    public float MinSky { get; init; }

    // The atlas's largest side, and a chart's: a face bigger than a chart at the density gets fewer texels.
    public int MaxAtlas { get; init; }
    public int MaxChart { get; init; }

    public static LightmapSettings For(float density) => new()
    {
        Density = density, SkyRays = 16, SkyDistance = 32f, MinSky = 0.2f, MaxAtlas = 2048, MaxChart = 256,
    };
}

// One baked level: the atlas (RGBA8; rgb is light / `Scale`, a is the sky term) and each face's lightmap
// coordinates, one per corner, in the order of the face's `Positions`.
internal sealed class LevelLightmap
{
    // What one in the texture's rgb stands for: light up to four times white fits, in steps of 1/64.
    // lit.fx's LIGHTMAP_SCALE is this number.
    public const float Scale = 4f;

    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Rgba { get; init; }
    public required Dictionary<LevelFace, Vector2[]> Uvs { get; init; }

    // Lamps it was baked from, and whether it was read from the cache rather than baked now.
    public int Lights { get; init; }
    public bool FromCache { get; set; }

    // The texel at atlas coordinates `uv` (nearest), decoded: light in rgb, the sky term in w.
    public Vector4 Sample(Vector2 uv)
    {
        int x = Math.Clamp((int)(uv.X * Width), 0, Width - 1);
        int y = Math.Clamp((int)(uv.Y * Height), 0, Height - 1);
        int i = (y * Width + x) * 4;
        return new Vector4(Rgba[i] / 255f * Scale, Rgba[i + 1] / 255f * Scale, Rgba[i + 2] / 255f * Scale, Rgba[i + 3] / 255f);
    }

    // Where on a face a point (engine space, the level's own frame) falls in the atlas: the face's corner
    // coordinates are an affine function of position on the face's plane, so three corners say it.
    public Vector2 UvAt(LevelFace face, Vector3 point)
    {
        var uvs = Uvs[face];
        var p = face.Positions;
        var e1 = p[1] - p[0];
        var e2 = p[2] - p[0];
        var d = point - p[0];
        float d11 = Vector3.Dot(e1, e1), d12 = Vector3.Dot(e1, e2), d22 = Vector3.Dot(e2, e2);
        float b1 = Vector3.Dot(d, e1), b2 = Vector3.Dot(d, e2);
        float det = d11 * d22 - d12 * d12;
        float a = (b1 * d22 - b2 * d12) / det;
        float b = (b2 * d11 - b1 * d12) / det;
        return uvs[0] + (uvs[1] - uvs[0]) * a + (uvs[2] - uvs[0]) * b;
    }
}

internal static class LightmapBaker
{
    // Bumped when what a bake writes changes, so a cached one from before is not read.
    public const int Version = 1;

    // How far off its face a texel's sample point stands, so its shadow rays do not start inside the wall.
    private const float Lift = 0.03f;

    // The worldspawn faces of `brushes`, unwrapped and lit by `lights` (positions in the brushes' frame;
    // `Colour` already times the intensity, as the renderer's samples are).
    public static LevelLightmap Bake(IReadOnlyList<LevelBrush> brushes, ReadOnlySpan<LightSample> lights, in LightmapSettings settings)
    {
        var occluders = new Occluders(brushes);
        var charts = new List<Chart>();
        foreach (var brush in brushes)
            foreach (var face in brush.Faces)
                if (face.Positions.Length >= 3) charts.Add(new Chart(face));

        // Pack at the density asked for, and lower it until the atlas fits.
        float density = settings.Density > 0f ? settings.Density : 4f;
        int width = 0, height = 0;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            foreach (var chart in charts) chart.Measure(density, settings.MaxChart);
            if (Pack(charts, settings.MaxAtlas, out width, out height)) break;
            density *= 0.7f;
            if (attempt == 11) throw new InvalidOperationException("the level's faces do not fit a lightmap atlas");
        }

        var rgba = new byte[width * height * 4];
        var lamps = lights.ToArray();
        var sky = SkyDirections(settings.SkyRays);
        var s = settings;
        Parallel.For(0, charts.Count, i => Light(charts[i], occluders, lamps, sky, s, rgba, width));

        var uvs = new Dictionary<LevelFace, Vector2[]>(charts.Count, ReferenceEqualityComparer.Instance);
        foreach (var chart in charts) uvs[chart.Face] = chart.CornerUvs(width, height);
        return new LevelLightmap { Width = width, Height = height, Rgba = rgba, Uvs = uvs, Lights = lamps.Length };
    }

    // One face's patch of the atlas: its plane's axes, its extent along them, and where it was packed.
    private sealed class Chart
    {
        public readonly LevelFace Face;
        public readonly Vector3 U, V, N;
        public readonly float MinS, MinT, MaxS, MaxT, Distance;
        public float Step;
        public int Columns, Rows;   // texels lit; the chart is one more all round (the border)
        public int X, Y;

        public Chart(LevelFace face)
        {
            Face = face;
            N = face.Normal;
            var helper = MathF.Abs(N.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
            U = Vector3.Normalize(Vector3.Cross(helper, N));
            V = Vector3.Cross(N, U);
            Distance = Vector3.Dot(N, face.Positions[0]);
            MinS = MinT = float.MaxValue;
            MaxS = MaxT = float.MinValue;
            foreach (var p in face.Positions)
            {
                float s = Vector3.Dot(p, U), t = Vector3.Dot(p, V);
                MinS = MathF.Min(MinS, s); MaxS = MathF.Max(MaxS, s);
                MinT = MathF.Min(MinT, t); MaxT = MathF.Max(MaxT, t);
            }
        }

        public int PackedWidth => Columns + 2;
        public int PackedHeight => Rows + 2;

        // Texel centres every `Step` metres from one edge of the face to the other, both edges included,
        // so filtering between centres covers the whole face and nothing past it.
        public void Measure(float density, int maxChart)
        {
            float extent = MathF.Max(MaxS - MinS, MaxT - MinT);
            Step = 1f / density;
            int most = Math.Max(maxChart - 2, 2);
            if (extent / Step + 1 > most) Step = extent / (most - 1);
            Columns = Math.Max(2, (int)MathF.Ceiling((MaxS - MinS) / Step - 1e-4f) + 1);
            Rows = Math.Max(2, (int)MathF.Ceiling((MaxT - MinT) / Step - 1e-4f) + 1);
        }

        public Vector3 PointAt(int column, int row) =>
            U * (MinS + column * Step) + V * (MinT + row * Step) + N * Distance;

        public Vector2[] CornerUvs(int width, int height)
        {
            var uvs = new Vector2[Face.Positions.Length];
            for (int i = 0; i < uvs.Length; i++)
            {
                var p = Face.Positions[i];
                float x = X + 1 + (Vector3.Dot(p, U) - MinS) / Step + 0.5f;
                float y = Y + 1 + (Vector3.Dot(p, V) - MinT) / Step + 0.5f;
                uvs[i] = new Vector2(x / width, y / height);
            }
            return uvs;
        }
    }

    // Shelf packing, tallest first, into the narrowest power-of-two width that holds every chart with the
    // height no more than the width. Deterministic: the order is the charts' sizes, then their order.
    private static bool Pack(List<Chart> charts, int maxAtlas, out int width, out int height)
    {
        var order = new List<int>(charts.Count);
        for (int i = 0; i < charts.Count; i++) order.Add(i);
        order.Sort((a, b) =>
        {
            int c = charts[b].PackedHeight.CompareTo(charts[a].PackedHeight);
            return c != 0 ? c : a.CompareTo(b);
        });

        for (width = 64; width <= maxAtlas; width *= 2)
        {
            int x = 0, y = 0, shelf = 0;
            bool fits = true;
            foreach (int i in order)
            {
                var chart = charts[i];
                if (chart.PackedWidth > width) { fits = false; break; }
                if (x + chart.PackedWidth > width) { x = 0; y += shelf; shelf = 0; }
                chart.X = x;
                chart.Y = y;
                x += chart.PackedWidth;
                shelf = Math.Max(shelf, chart.PackedHeight);
            }
            height = Math.Max(1, y + shelf);
            if (fits && height <= width) return true;
        }
        width = height = 0;
        return false;
    }

    private static void Light(Chart chart, Occluders occluders, LightSample[] lights, Vector3[] sky, in LightmapSettings settings,
                              byte[] rgba, int width)
    {
        int columns = chart.Columns, rows = chart.Rows;
        var light = new Vector3[columns * rows];
        var open = new float[columns * rows];
        var valid = new bool[columns * rows];
        var face = chart.Face;
        var centroid = Vector3.Zero;
        foreach (var p in face.Positions) centroid += p;
        centroid /= face.Positions.Length;

        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                var onFace = InsidePolygon(face, chart.PointAt(column, row), centroid);
                var at = onFace + chart.N * Lift;
                int k = row * columns + column;
                // Under another brush (the floor beneath a wall): nobody sees it, and lighting it dark would
                // bleed a dark line along the wall's foot. Filled from its neighbours below.
                if (occluders.Inside(at)) continue;
                valid[k] = true;

                var sum = Vector3.Zero;
                foreach (var lamp in lights)
                {
                    var toLight = lamp.Position - at;
                    float distance = toLight.Length();
                    if (distance >= lamp.Range || distance < 1e-4f) continue;
                    float lambert = Vector3.Dot(chart.N, toLight / distance);
                    if (lambert <= 0f) continue;
                    if (occluders.Blocked(at, lamp.Position)) continue;
                    // common.fxh's PointLights, exactly: (1 - d/range)² times Lambert.
                    float falloff = 1f - distance / lamp.Range;
                    sum += lamp.Colour * (falloff * falloff * lambert);
                }
                light[k] = sum;

                if (sky.Length == 0) { open[k] = 1f; continue; }
                int seen = 0;
                foreach (var d in sky)
                {
                    var direction = chart.U * d.X + chart.V * d.Y + chart.N * d.Z;
                    if (!occluders.Blocked(at, at + direction * settings.SkyDistance)) seen++;
                }
                open[k] = MathF.Max(settings.MinSky, (float)seen / sky.Length);
            }

        Fill(light, open, valid, columns, rows, settings.MinSky);

        // Into the atlas, with the border a copy of the nearest edge texel so filtering never reads a neighbour.
        for (int y = -1; y <= rows; y++)
            for (int x = -1; x <= columns; x++)
            {
                int k = Math.Clamp(y, 0, rows - 1) * columns + Math.Clamp(x, 0, columns - 1);
                int i = ((chart.Y + 1 + y) * width + chart.X + 1 + x) * 4;
                rgba[i] = Encode(light[k].X / LevelLightmap.Scale);
                rgba[i + 1] = Encode(light[k].Y / LevelLightmap.Scale);
                rgba[i + 2] = Encode(light[k].Z / LevelLightmap.Scale);
                rgba[i + 3] = Encode(open[k]);
            }
    }

    // Texels with no sample of their own take the average of their sampled neighbours, ring by ring.
    private static void Fill(Vector3[] light, float[] open, bool[] valid, int columns, int rows, float minSky)
    {
        bool any = false;
        foreach (bool v in valid) any |= v;
        if (!any)
        {
            Array.Fill(open, minSky);
            return;
        }

        var next = (bool[])valid.Clone();
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < columns; x++)
                {
                    int k = y * columns + x;
                    if (valid[k]) continue;
                    var sum = Vector3.Zero;
                    float skySum = 0f;
                    int n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= columns || ny >= rows) continue;
                            int j = ny * columns + nx;
                            if (!valid[j]) continue;
                            sum += light[j];
                            skySum += open[j];
                            n++;
                        }
                    if (n == 0) continue;
                    light[k] = sum / n;
                    open[k] = skySum / n;
                    next[k] = true;
                    changed = true;
                }
            Array.Copy(next, valid, valid.Length);
        }
    }

    private static byte Encode(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    // A texel centre outside its (convex) face is pulled onto the face's edge, and a hair toward its middle,
    // so a triangle's chart samples the triangle and not the air beside it.
    private static Vector3 InsidePolygon(LevelFace face, Vector3 point, Vector3 centroid)
    {
        var p = face.Positions;
        var n = face.Normal;
        bool inside = true;
        for (int i = 0; i < p.Length; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Length];
            if (Vector3.Dot(Vector3.Cross(b - a, point - a), n) < 0f) { inside = false; break; }
        }
        if (!inside)
        {
            float best = float.MaxValue;
            var nearest = point;
            for (int i = 0; i < p.Length; i++)
            {
                var a = p[i];
                var ab = p[(i + 1) % p.Length] - a;
                float t = Math.Clamp(Vector3.Dot(point - a, ab) / MathF.Max(ab.LengthSquared(), 1e-8f), 0f, 1f);
                var q = a + ab * t;
                float d = Vector3.DistanceSquared(point, q);
                if (d < best) { best = d; nearest = q; }
            }
            point = nearest;
        }
        var toCentre = centroid - point;
        float length = toCentre.Length();
        return length > 0.02f ? point + toCentre / length * 0.01f : point;
    }

    // Cosine-weighted directions about +Z (a texel's normal), the same for every texel: a fixed pattern
    // makes no noise, and a bake that is the same every time can be cached and tested.
    internal static Vector3[] SkyDirections(int count)
    {
        var directions = new Vector3[Math.Max(count, 0)];
        float golden = MathF.PI * (3f - MathF.Sqrt(5f));
        for (int i = 0; i < directions.Length; i++)
        {
            float r = MathF.Sqrt((i + 0.5f) / directions.Length);
            float phi = i * golden;
            directions[i] = new Vector3(r * MathF.Cos(phi), r * MathF.Sin(phi), MathF.Sqrt(MathF.Max(0f, 1f - r * r)));
        }
        return directions;
    }

    // What a bake read, hashed (FNV-1a 64): the cache's file name. Anything that changes the texels changes it.
    public static ulong Hash(IReadOnlyList<LevelBrush> brushes, ReadOnlySpan<LightSample> lights, in LightmapSettings settings)
    {
        ulong hash = 14695981039346656037UL;
        void Int(int v) { unchecked { for (int i = 0; i < 4; i++) { hash ^= (byte)(v >> (i * 8)); hash *= 1099511628211UL; } } }
        void Float(float f) => Int(BitConverter.SingleToInt32Bits(f));
        void Vec(Vector3 v) { Float(v.X); Float(v.Y); Float(v.Z); }

        Int(Version);
        Float(settings.Density); Int(settings.SkyRays); Float(settings.SkyDistance); Float(settings.MinSky);
        Int(settings.MaxAtlas); Int(settings.MaxChart);
        Int(brushes.Count);
        foreach (var brush in brushes)
        {
            Int(brush.Faces.Length);
            foreach (var face in brush.Faces)
            {
                Vec(face.Normal);
                Int(face.Positions.Length);
                foreach (var p in face.Positions) Vec(p);
            }
        }
        Int(lights.Length);
        foreach (var lamp in lights) { Vec(lamp.Position); Vec(lamp.Colour); Float(lamp.Range); }
        return hash;
    }

    // The level's brushes as convex solids for shadow rays, in a bounding-volume tree: a level of a thousand
    // brushes asks a few dozen of them per ray, not all of them.
    private sealed class Occluders
    {
        private readonly Vector4[][] _planes;      // per brush: normal, distance (solid where dot(n, x) <= d)
        private readonly Vector3[] _min, _max;
        private readonly List<Node> _nodes = new();
        private readonly int[] _order;

        private struct Node
        {
            public Vector3 Min, Max;
            public int Left, Right;   // children, or -1 for a leaf
            public int First, Count;  // a leaf's brushes in _order
        }

        public Occluders(IReadOnlyList<LevelBrush> brushes)
        {
            _planes = new Vector4[brushes.Count][];
            _min = new Vector3[brushes.Count];
            _max = new Vector3[brushes.Count];
            _order = new int[brushes.Count];
            for (int i = 0; i < brushes.Count; i++)
            {
                var faces = brushes[i].Faces;
                var planes = new Vector4[faces.Length];
                for (int f = 0; f < faces.Length; f++)
                    planes[f] = new Vector4(faces[f].Normal, Vector3.Dot(faces[f].Normal, faces[f].Positions[0]));
                _planes[i] = planes;
                _min[i] = brushes[i].Min;
                _max[i] = brushes[i].Max;
                _order[i] = i;
            }
            if (brushes.Count > 0) Build(0, brushes.Count);
        }

        private int Build(int first, int count)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            for (int i = first; i < first + count; i++)
            {
                min = Vector3.Min(min, _min[_order[i]]);
                max = Vector3.Max(max, _max[_order[i]]);
            }
            int index = _nodes.Count;
            _nodes.Add(new Node { Min = min, Max = max, Left = -1, Right = -1, First = first, Count = count });
            if (count <= 4) return index;

            var size = max - min;
            int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
            Array.Sort(_order, first, count, Comparer<int>.Create((a, b) =>
            {
                float ca = Axis(_min[a] + _max[a], axis), cb = Axis(_min[b] + _max[b], axis);
                int c = ca.CompareTo(cb);
                return c != 0 ? c : a.CompareTo(b);
            }));
            int half = count / 2;
            int left = Build(first, half);
            int right = Build(first + half, count - half);
            var node = _nodes[index];
            node.Left = left;
            node.Right = right;
            _nodes[index] = node;
            return index;
        }

        private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

        // Whether the segment from `from` to `to` passes through any brush.
        public bool Blocked(Vector3 from, Vector3 to)
        {
            if (_nodes.Count == 0) return false;
            var direction = to - from;
            Span<int> stack = stackalloc int[64];
            int top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                var node = _nodes[stack[--top]];
                if (!SegmentHitsBox(from, direction, node.Min, node.Max)) continue;
                if (node.Left < 0)
                {
                    for (int i = node.First; i < node.First + node.Count; i++)
                        if (SegmentHitsBrush(from, direction, _planes[_order[i]])) return true;
                    continue;
                }
                if (top + 2 > stack.Length) return SlowBlocked(from, direction);
                stack[top++] = node.Left;
                stack[top++] = node.Right;
            }
            return false;
        }

        private bool SlowBlocked(Vector3 from, Vector3 direction)
        {
            foreach (var planes in _planes)
                if (SegmentHitsBrush(from, direction, planes)) return true;
            return false;
        }

        // Whether a point is inside a brush (by more than a hair).
        public bool Inside(Vector3 point)
        {
            for (int b = 0; b < _planes.Length; b++)
            {
                if (point.X < _min[b].X || point.Y < _min[b].Y || point.Z < _min[b].Z
                    || point.X > _max[b].X || point.Y > _max[b].Y || point.Z > _max[b].Z) continue;
                bool inside = true;
                foreach (var plane in _planes[b])
                    if (Vector3.Dot(new Vector3(plane.X, plane.Y, plane.Z), point) - plane.W > -1e-3f) { inside = false; break; }
                if (inside) return true;
            }
            return false;
        }

        // Clipping the segment by each half-space: it meets the solid if anything of it is left. Touching a
        // face is not a hit, which keeps a ray that grazes a wall's surface from being shadowed by it.
        private static bool SegmentHitsBrush(Vector3 from, Vector3 direction, Vector4[] planes)
        {
            float enter = 0f, exit = 1f;
            foreach (var plane in planes)
            {
                var n = new Vector3(plane.X, plane.Y, plane.Z);
                float distance = Vector3.Dot(n, from) - plane.W;
                float denominator = Vector3.Dot(n, direction);
                if (MathF.Abs(denominator) < 1e-9f)
                {
                    if (distance > -1e-4f) return false;   // parallel and outside (or on) this plane
                    continue;
                }
                float t = -distance / denominator;
                if (denominator < 0f) enter = MathF.Max(enter, t);
                else exit = MathF.Min(exit, t);
                if (enter >= exit) return false;
            }
            float length = direction.Length();
            return (exit - enter) * length > 1e-3f;
        }

        private static bool SegmentHitsBox(Vector3 from, Vector3 direction, Vector3 min, Vector3 max)
        {
            float enter = 0f, exit = 1f;
            for (int axis = 0; axis < 3; axis++)
            {
                float o = Axis(from, axis), d = Axis(direction, axis), lo = Axis(min, axis), hi = Axis(max, axis);
                if (MathF.Abs(d) < 1e-9f)
                {
                    if (o < lo || o > hi) return false;
                    continue;
                }
                float t0 = (lo - o) / d, t1 = (hi - o) / d;
                if (t0 > t1) (t0, t1) = (t1, t0);
                enter = MathF.Max(enter, t0);
                exit = MathF.Min(exit, t1);
                if (enter > exit) return false;
            }
            return true;
        }
    }
}

// The bakes kept between loads (user://cache/lightmaps/<hash>.sglm). Little-endian:
//   "SGLM" int32 version  uint64 hash  int32 width  int32 height  int32 lights  rgba (width × height × 4)
//   int32 faces; per face (worldspawn order): int32 corners, corners × (float u, float v)
// A file whose header or face list does not match what is asked of it is passed over and baked again.
internal static class LightmapCache
{
    private static ReadOnlySpan<byte> Magic => "SGLM"u8;

    public static string Folder => Path.Combine(UserPaths.Root, "cache", "lightmaps");

    public static string PathOf(ulong hash) => Path.Combine(Folder, $"{hash:x16}.sglm");

    public static void Write(Stream stream, LevelLightmap lightmap, ulong hash, IReadOnlyList<LevelBrush> brushes)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(LightmapBaker.Version);
        writer.Write(hash);
        writer.Write(lightmap.Width);
        writer.Write(lightmap.Height);
        writer.Write(lightmap.Lights);
        writer.Write(lightmap.Rgba);
        int faces = 0;
        foreach (var brush in brushes) faces += brush.Faces.Length;
        writer.Write(faces);
        foreach (var brush in brushes)
            foreach (var face in brush.Faces)
            {
                var uvs = lightmap.Uvs.TryGetValue(face, out var found) ? found : Array.Empty<Vector2>();
                writer.Write(uvs.Length);
                foreach (var uv in uvs) { writer.Write(uv.X); writer.Write(uv.Y); }
            }
    }

    // The bake for `hash` and these brushes, or null when the stream is not one.
    public static LevelLightmap? Read(Stream stream, ulong hash, IReadOnlyList<LevelBrush> brushes)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        Span<byte> magic = stackalloc byte[4];
        if (reader.Read(magic) != 4 || !magic.SequenceEqual(Magic)) return null;
        if (reader.ReadInt32() != LightmapBaker.Version || reader.ReadUInt64() != hash) return null;
        int width = reader.ReadInt32(), height = reader.ReadInt32(), lights = reader.ReadInt32();
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return null;
        var rgba = reader.ReadBytes(width * height * 4);
        if (rgba.Length != width * height * 4) return null;
        int faces = reader.ReadInt32();
        int expected = 0;
        foreach (var brush in brushes) expected += brush.Faces.Length;
        if (faces != expected) return null;
        var uvs = new Dictionary<LevelFace, Vector2[]>(faces, ReferenceEqualityComparer.Instance);
        foreach (var brush in brushes)
            foreach (var face in brush.Faces)
            {
                int corners = reader.ReadInt32();
                if (corners == 0) continue;
                if (corners != face.Positions.Length) return null;
                var corner = new Vector2[corners];
                for (int i = 0; i < corners; i++) corner[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                uvs[face] = corner;
            }
        return new LevelLightmap { Width = width, Height = height, Rgba = rgba, Uvs = uvs, Lights = lights, FromCache = true };
    }

    public static LevelLightmap? TryLoad(ulong hash, IReadOnlyList<LevelBrush> brushes)
    {
        string path = PathOf(hash);
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return Read(stream, hash, brushes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }

    // Written beside its final name and moved into place, so a reader never sees half a file, and two
    // worlds baking the same level at once write the same bytes to the same name.
    public static void TrySave(ulong hash, LevelLightmap lightmap, IReadOnlyList<LevelBrush> brushes)
    {
        string path = PathOf(hash);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Folder);
            using (var stream = File.Create(temporary)) Write(stream, lightmap, hash, brushes);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Once(LogCat.Level, LogLevel.Warn, "lightmap-cache", $"Lightmaps cannot be cached in {Folder} ({ex.Message}); each load bakes them");
            try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }
}

// The bake of a loaded level, once it is placed and its lamps are in the world (MapCollisionSystem).
internal static class MapLightmaps
{
    public static void Ensure(World world, MapLevel level)
    {
        if (!level.LightmapPending) return;
        level.LightmapDone = true;

        // The lamps this level placed and marked baked, in its own frame: a light belongs to the level
        // that put it there, so what a level's lightmap holds does not depend on what else is loaded.
        var lights = new List<LightSample>();
        foreach (var entity in world.Query<FromMap>().Entities.ToEntityList())
        {
            if (entity.GetComponent<FromMap>().Level != level.Record || !world.Has<PointLight>(entity) || !world.Has<Transform>(entity)) continue;
            var light = world.Get<PointLight>(entity);
            if (!light.Baked || !light.Lit) continue;
            var at = world.Get<Transform>(entity).LocalPosition - level.Position;
            lights.Add(new LightSample(at, light.Colour * light.Intensity, light.Range));
        }

        var settings = LightmapSettings.For(level.LightmapDensity);
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lights);
        ulong hash = LightmapBaker.Hash(level.Brushes, span, settings);
        if (LightmapCache.TryLoad(hash, level.Brushes) is { } cached)
        {
            level.Lightmap = cached;
            Log.Info(LogCat.Level, $"{level.Source}: lightmap {cached.Width}x{cached.Height} read from the cache");
            return;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var baked = LightmapBaker.Bake(level.Brushes, span, settings);
        level.Lightmap = baked;
        LightmapCache.TrySave(hash, baked, level.Brushes);
        Log.Info(LogCat.Level, $"{level.Source}: lightmap {baked.Width}x{baked.Height} baked from {lights.Count} lamp(s) "
                             + $"in {watch.Elapsed.TotalMilliseconds:F0} ms");
    }
}
