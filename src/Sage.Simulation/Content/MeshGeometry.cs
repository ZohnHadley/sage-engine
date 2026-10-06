#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Sage.Simulation;

// A model's geometry, headless (docs/design/05 §3.4, §7; issue #302): what the client's renderer turns into
// vertex and index buffers, read either from a `.glb` (`ReadGlb`, R12's runtime glTF loading, moved here
// from the client's GltfLoader) or from the cooked `.sgmesh` written from it (`CookedMesh`). Both give the
// same object, so the renderer has one path and the cook can be checked against the loose file in a test.
//
// What the engine wants out of a file is small: positions, normals, texture coordinates and indices, in one
// array per primitive — plus, for a skinned primitive (issue #117), four joint indices and four weights per
// vertex and the skin's inverse bind and rest-pose joint matrices. Since issue #410 each vertex also has a
// tangent (the file's TANGENT, or worked out from the texture coordinates when it has none: `Tangents`)
// for lit.fx's normal maps, and a colour (the file's COLOR_0, white without one) for a material's
// `vertexColors`. Materials come from the *material record*
// pointed at by whatever draws the mesh (07 §3.3), so the file's own materials are read past.
internal sealed class MeshGeometry
{
    public readonly List<MeshGeometryPart> Parts = new();
    public MeshSkin? Skin;
    // The model's morph targets (issue #363, GltfMorphs): names, and the rest weights a skinned part's
    // vertices are baked at. A skinned part's MeshMorph deltas index these.
    public string[] MorphTargets = Array.Empty<string>();
    public float[] RestMorphWeights = Array.Empty<float>();
    public Vector3 BoundsCentre;
    public float BoundsRadius = 1f;

    // Reads every primitive of every mesh in a `.glb`. Returns null when the file is not glTF, uses a feature
    // outside the supported subset (`GltfSubset`: one named error per feature, logged and returned in
    // `errors`) or has nothing drawable in it, which is a content problem, not a crash.
    public static MeshGeometry? ReadGlb(Stream stream, string name) => ReadGlb(stream, name, out _);

    public static MeshGeometry? ReadGlb(Stream stream, string name, out IReadOnlyList<string> errors)
    {
        byte[] bytes;
        using (var copy = new MemoryStream())
        {
            stream.CopyTo(copy);
            bytes = copy.ToArray();
        }

        var warnings = new List<string>();
        var problems = GltfSubset.Check(bytes, name, warnings);
        errors = problems;
        if (problems.Count > 0)
        {
            foreach (string problem in problems) Log.Error(LogCat.Assets, problem);
            return null;
        }
        foreach (string warning in warnings) Log.Warn(LogCat.Assets, warning);

        SharpGLTF.Schema2.ModelRoot root;
        try
        {
            // `ReadGLB` for the binary form; the text form has its buffers beside it, which a VFS mount
            // cannot always give us, so a model that ships as `.gltf` plus a `.bin` is rejected by the
            // subset check (05 §8: say so rather than half-loading it).
            root = SharpGLTF.Schema2.ModelRoot.ReadGLB(new MemoryStream(bytes, writable: false), new SharpGLTF.Schema2.ReadSettings());
        }
        catch (Exception ex)
        {
            string error = GltfSubset.Error(GltfSubset.NotGltf, name, $"{ex.GetType().Name}: {ex.Message}");
            errors = new[] { error };
            Log.Error(LogCat.Assets, error);
            return null;
        }

        var model = new MeshGeometry();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        try
        {
            // `Collect` recurses, so this wants *roots* only. `LogicalNodes` is every node in the file,
            // children included, so the fallback for a file with no default scene has to filter them —
            // feeding it the flat list draws a hierarchy once per level of itself.
            var roots = root.DefaultScene?.VisualChildren
                        ?? System.Linq.Enumerable.Where(root.LogicalNodes, n => n.VisualParent == null);
            var morphs = GltfMorphs.Read(root);
            model.MorphTargets = morphs.Names;
            model.RestMorphWeights = morphs.Rest;
            foreach (var node in roots) Collect(node, name, model, morphs, ref min, ref max);
        }
        catch (Exception ex)
        {
            // Reading geometry can throw on a file this loader does not handle — a primitive drawn as
            // points or lines has no triangles to ask for. Same contract as a bad header.
            string error = GltfSubset.Error(GltfSubset.NotGltf, name, $"could not read geometry, {ex.GetType().Name}: {ex.Message}");
            errors = new[] { error };
            Log.Error(LogCat.Assets, error);
            return null;
        }

        if (model.Parts.Count == 0)
        {
            string error = GltfSubset.Error(GltfSubset.NoGeometry, name);
            errors = new[] { error };
            Log.Error(LogCat.Assets, error);
            return null;
        }

        model.BoundsCentre = (min + max) * 0.5f;
        model.BoundsRadius = Math.Max((max - model.BoundsCentre).Length(), 0.001f);
        return model;
    }

    private static void Collect(SharpGLTF.Schema2.Node node, string name, MeshGeometry model, GltfMorphs morphs, ref Vector3 min, ref Vector3 max)
    {
        foreach (var child in node.VisualChildren) Collect(child, name, model, morphs, ref min, ref max);
        if (node.Mesh == null) return;

        // A skinned node: its vertices stay in bind space, and its skin says where they go.
        Matrix4x4[]? restPalette = null;
        if (node.Skin != null)
        {
            model.Skin ??= ReadSkin(node.Skin, name);
            if (model.Skin.LogicalIndex != node.Skin.LogicalIndex)
            {
                Log.Warn(LogCat.Assets, $"Model '{name}': node '{node.Name}' uses a second skin; only the first is supported, so it is not drawn");
                return;
            }
            restPalette = new Matrix4x4[model.Skin.RestJoints.Length];
            SkinMath.Palette(model.Skin.RestJoints, model.Skin.InverseBind, restPalette);
        }

        // The node's own place in the file, folded into rigid vertices: the engine draws a mesh at an
        // entity's transform, and a model with its own hierarchy would otherwise arrive inside out.
        Matrix4x4 transform = node.WorldMatrix;

        Span<int> j = stackalloc int[SkinMath.Influences];
        Span<float> w = stackalloc float[SkinMath.Influences];
        foreach (var primitive in node.Mesh.Primitives)
        {
            var positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array();
            if (positions == null || positions.Count == 0) continue;

            var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
            var uvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
            var uvs1 = primitive.GetVertexAccessor("TEXCOORD_1")?.AsVector2Array();
            var fileTangents = primitive.GetVertexAccessor("TANGENT")?.AsVector4Array();
            var colours = primitive.GetVertexAccessor("COLOR_0")?.AsColorArray();
            var joints = restPalette != null ? primitive.GetVertexAccessor("JOINTS_0")?.AsVector4Array() : null;
            var weights = joints != null ? primitive.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array() : null;
            bool skinned = joints != null && weights != null;
            if (restPalette != null && !skinned)
                Log.Warn(LogCat.Assets, $"Model '{name}': a primitive of skinned node '{node.Name}' has no JOINTS_0/WEIGHTS_0; drawn rigid");

            var part = new MeshGeometryPart { Indices = Triangles(primitive) };
            if (part.Indices.Length == 0) continue;
            // The second UV set (lightmaps), beside the vertices rather than in them: the client vertex
            // formats are the first set's, and a file without TEXCOORD_1 leaves this null.
            if (uvs1 != null && uvs1.Count >= positions.Count)
            {
                part.Uv1 = new Vector2[positions.Count];
                for (int i = 0; i < part.Uv1.Length; i++) part.Uv1[i] = uvs1[i];
            }
            if (skinned) part.Skinned = new SkinnedMeshVertex[positions.Count];
            else part.Rigid = new MeshVertex[positions.Count];

            // Morph targets (issue #363): the vertices are baked at the rest weights; a skinned part keeps
            // its deltas, so the renderer can move it from there by a pose's weights (MeshMorphing).
            var deltas = ReadMorphs(primitive, morphs.Of(node.Mesh), positions.Count);
            if (deltas != null && skinned) part.Morphs = deltas;
            else if (deltas != null)
                Log.Debug(LogCat.Assets, $"Model '{name}': rigid node '{node.Name}' has morph targets; drawn at its rest weights (only a skinned mesh animates them)");

            // Tangents (issue #410): the file's when it has them for every vertex, else worked out here, in
            // the file's own space; rigid ones are then turned with their node like the normals.
            var tangents = new Vector4[positions.Count];
            if (fileTangents != null && fileTangents.Count >= positions.Count)
                for (int i = 0; i < tangents.Length; i++) tangents[i] = fileTangents[i];
            else
            {
                var ps = new Vector3[positions.Count];
                var ns = new Vector3[positions.Count];
                var ts = new Vector2[positions.Count];
                for (int i = 0; i < ps.Length; i++)
                {
                    ps[i] = positions[i];
                    ns[i] = normals != null && i < normals.Count ? normals[i] : Vector3.UnitY;
                    ts[i] = uvs != null && i < uvs.Count ? uvs[i] : Vector2.Zero;
                }
                Tangents(ps, ns, ts, part.Indices, tangents);
            }
            bool mirrored = transform.GetDeterminant() < 0f;

            for (int i = 0; i < positions.Count; i++)
            {
                var p = positions[i];
                var n = normals != null && i < normals.Count ? normals[i] : Vector3.UnitY;
                var uv = uvs != null && i < uvs.Count ? uvs[i] : Vector2.Zero;
                var colour = colours != null && i < colours.Count ? VertexColour.From(colours[i]) : VertexColour.White;
                var t = tangents[i];
                if (deltas != null)
                    foreach (var morph in deltas)
                    {
                        float rw = model.RestMorphWeights[morph.Target];
                        if (rw == 0f) continue;
                        p += rw * morph.Positions[i];
                        if (morph.Normals != null && normals != null) n += rw * morph.Normals[i];
                    }

                if (skinned)
                {
                    Influences(joints![i], i < weights!.Count ? weights[i] : default, restPalette!.Length, j, w);
                    n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
                    part.Skinned![i] = new SkinnedMeshVertex(p, n, uv, j, new Vector4(w[0], w[1], w[2], w[3]), Orthogonal(t, n), colour);

                    // Bounds in the rest pose, which is where the vertices are when nothing poses them.
                    var rest = SkinMath.SkinPosition(p, restPalette, j, w);
                    min = Vector3.Min(min, rest);
                    max = Vector3.Max(max, rest);
                    continue;
                }

                p = Vector3.Transform(p, transform);
                n = Vector3.TransformNormal(n, transform);
                // A zero-length normal normalises to NaN, and a NaN in a vertex buffer is invisible
                // geometry with nothing in the log to explain it. Exporters do write them.
                n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
                // A tangent turns with the node as the surface does; a mirroring node flips its handedness.
                var turned = Vector3.TransformNormal(new Vector3(t.X, t.Y, t.Z), transform);
                t = new Vector4(turned, mirrored ? -t.W : t.W);
                part.Rigid![i] = new MeshVertex(p, n, uv, Orthogonal(t, n), colour);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            model.Parts.Add(part);
            Log.Debug(LogCat.Assets, $"Model {name}: {(skinned ? "skinned " : "")}primitive with {part.VertexCount} vertices, {part.Indices.Length / 3} triangles");
        }
    }

    // A primitive's morph targets as deltas per model target (`map`: the mesh's target i is the model's
    // map[i]). Null when it has none. A target without POSITION deltas moves nothing; TANGENT deltas are
    // not read (the tangent is kept across the normal).
    private static MeshMorph[]? ReadMorphs(SharpGLTF.Schema2.MeshPrimitive primitive, int[]? map, int vertices)
    {
        int count = primitive.MorphTargetsCount;
        if (count == 0 || map == null) return null;
        var result = new List<MeshMorph>(count);
        for (int i = 0; i < count && i < map.Length; i++)
        {
            var accessors = primitive.GetMorphTargetAccessors(i);
            var positions = accessors.TryGetValue("POSITION", out var pa) ? pa.AsVector3Array() : null;
            var normals = accessors.TryGetValue("NORMAL", out var na) ? na.AsVector3Array() : null;
            if (positions == null && normals == null) continue;
            var morph = new MeshMorph { Target = map[i], Positions = new Vector3[vertices] };
            if (normals != null) morph.Normals = new Vector3[vertices];
            for (int v = 0; v < vertices; v++)
            {
                if (positions != null && v < positions.Count) morph.Positions[v] = positions[v];
                if (normals != null && v < normals.Count) morph.Normals![v] = normals[v];
            }
            result.Add(morph);
        }
        return result.Count == 0 ? null : result.ToArray();
    }

    private static int[] Triangles(SharpGLTF.Schema2.MeshPrimitive primitive)
    {
        var triangles = new List<int>();
        foreach (var (a, b, c) in primitive.GetTriangleIndices())
        {
            // glTF is right-handed with counter-clockwise front faces; the renderer culls
            // counter-clockwise (06 §3.4), so the winding is flipped once, here, rather than in
            // every material that ever draws a model.
            triangles.Add(a);
            triangles.Add(c);
            triangles.Add(b);
        }
        return triangles.ToArray();
    }

    private static MeshSkin ReadSkin(SharpGLTF.Schema2.Skin skin, string name)
    {
        int count = skin.JointsCount;
        var result = new MeshSkin
        {
            InverseBind = new Matrix4x4[count],
            RestJoints = new Matrix4x4[count],
            JointNames = new string[count],
            LogicalIndex = skin.LogicalIndex,
        };
        for (int i = 0; i < count; i++)
        {
            var (joint, inverseBind) = skin.GetJoint(i);
            result.InverseBind[i] = inverseBind;
            result.RestJoints[i] = joint.WorldMatrix;
            result.JointNames[i] = joint.Name ?? $"joint{i}";
        }
        if (count > SkinMath.MaxBones)
            Log.Warn(LogCat.Assets, $"Model '{name}': its skin has {count} joints; a draw takes {SkinMath.MaxBones} (vs_3_0), so vertices bound to the rest follow the root");
        return result;
    }

    // Per-vertex tangents worked out from the triangles' texture coordinates (issue #410), for a file with no
    // TANGENT of its own: glTF says to, and lit.fx's normal maps need one. xyz points along +u, and w (±1)
    // says which way the bitangent `cross(normal, xyz) * w` goes: towards -v, the top of the image as it is
    // drawn, which is the way a glTF normal map's green channel points ("+Y is up"). Each triangle adds its
    // own direction to its corners, weighted by its size; a vertex with no usable triangle (no UVs, or all
    // of them degenerate) gets any direction across its normal, so the shader still has a frame.
    internal static void Tangents(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> normals, ReadOnlySpan<Vector2> uvs,
                                  ReadOnlySpan<int> indices, Span<Vector4> tangents)
    {
        var tan = new Vector3[positions.Length];
        var bit = new Vector3[positions.Length];
        for (int k = 0; k + 2 < indices.Length; k += 3)
        {
            int a = indices[k], b = indices[k + 1], c = indices[k + 2];
            if ((uint)a >= (uint)positions.Length || (uint)b >= (uint)positions.Length || (uint)c >= (uint)positions.Length) continue;
            Vector3 e1 = positions[b] - positions[a], e2 = positions[c] - positions[a];
            Vector2 d1 = uvs[b] - uvs[a], d2 = uvs[c] - uvs[a];
            float det = d1.X * d2.Y - d2.X * d1.Y;
            if (MathF.Abs(det) < 1e-12f) continue;   // no area in UV space: says nothing about direction
            float r = 1f / det;
            // Weighted by the triangle's area in model space over its area in UV space: |det| cancels out
            // of the direction but not the length, so a big triangle counts for more than a sliver.
            var t = (e1 * d2.Y - e2 * d1.Y) * r;
            var bv = (e2 * d1.X - e1 * d2.X) * r;   // along +v
            float weight = Vector3.Cross(e1, e2).Length() * MathF.Abs(det);
            if (!(weight > 0f) || !float.IsFinite(t.X + t.Y + t.Z + bv.X + bv.Y + bv.Z)) continue;
            t = SafeNormalize(t) * weight;
            bv = SafeNormalize(bv) * weight;
            tan[a] += t; tan[b] += t; tan[c] += t;
            bit[a] += bv; bit[b] += bv; bit[c] += bv;
        }
        for (int i = 0; i < positions.Length; i++)
        {
            var n = SafeNormalize(normals[i]);
            if (n == Vector3.Zero) n = Vector3.UnitY;
            var t = tan[i] - n * Vector3.Dot(n, tan[i]);   // Gram-Schmidt: across the normal
            if (t.LengthSquared() < 1e-12f) t = AnyPerpendicular(n);
            t = Vector3.Normalize(t);
            // The bitangent the shader builds is cross(n, t) * w; it should point up the image, -v.
            float w = Vector3.Dot(Vector3.Cross(n, t), -bit[i]) < 0f ? -1f : 1f;
            tangents[i] = new Vector4(t, w);
        }
    }

    // A tangent made exactly perpendicular to the (unit) normal, of unit length, its handedness kept to ±1.
    // A file's tangent that is zero or parallel to the normal gets any perpendicular direction instead.
    internal static Vector4 Orthogonal(Vector4 tangent, Vector3 n)
    {
        var t = new Vector3(tangent.X, tangent.Y, tangent.Z);
        if (!float.IsFinite(t.X + t.Y + t.Z)) t = Vector3.Zero;
        t -= n * Vector3.Dot(n, t);
        if (t.LengthSquared() < 1e-12f) t = AnyPerpendicular(n);
        return new Vector4(Vector3.Normalize(t), tangent.W < 0f ? -1f : 1f);
    }

    private static Vector3 AnyPerpendicular(Vector3 n)
    {
        var axis = MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ;
        return Vector3.Normalize(axis - n * Vector3.Dot(n, axis));
    }

    private static Vector3 SafeNormalize(Vector3 v) => v.LengthSquared() > 1e-24f ? Vector3.Normalize(v) : Vector3.Zero;

    // A vertex's four influences, cleaned up: a joint the palette cannot hold (past MaxBones, or past
    // the skin) loses its weight, the weights are made to sum to one, and a vertex left with no weight
    // at all follows joint 0 rather than collapsing to the origin.
    internal static void Influences(Vector4 joints, Vector4 weights, int jointCount, Span<int> j, Span<float> w)
    {
        int limit = Math.Min(jointCount, SkinMath.MaxBones);
        j[0] = (int)joints.X; j[1] = (int)joints.Y; j[2] = (int)joints.Z; j[3] = (int)joints.W;
        w[0] = weights.X; w[1] = weights.Y; w[2] = weights.Z; w[3] = weights.W;
        float sum = 0f;
        for (int k = 0; k < 4; k++)
        {
            if (j[k] < 0 || j[k] >= limit || !(w[k] > 0f)) { j[k] = 0; w[k] = 0f; }
            sum += w[k];
        }
        if (sum <= 1e-6f) { w[0] = 1f; return; }
        for (int k = 0; k < 4; k++) w[k] /= sum;
    }
}

// One drawable primitive: `Rigid` vertices baked into model space by their node's transform, or `Skinned`
// vertices left in the skin's bind space (glTF ignores a skinned mesh node's own transform: its joints
// place it). Exactly one of the two is set.
internal sealed class MeshGeometryPart
{
    public MeshVertex[]? Rigid;
    public SkinnedMeshVertex[]? Skinned;
    public required int[] Indices;
    public Vector2[]? Uv1;   // TEXCOORD_1 per vertex (issue #321), null when the file has no second set
    public MeshMorph[]? Morphs;   // a skinned part's morph targets (issue #363), null when it has none
    public int VertexCount => Rigid?.Length ?? Skinned!.Length;
}

// The file's skin (the first one; a model with several is drawn with the first, logged): each joint's
// inverse bind matrix and where it stands in the file's rest pose, both in model space.
// One morph target of a skinned part (issue #363): per-vertex deltas, in bind space, for the model's
// target `Target` (MeshGeometry.MorphTargets).
internal sealed class MeshMorph
{
    public int Target;
    public required Vector3[] Positions;
    public Vector3[]? Normals;
}

internal sealed class MeshSkin
{
    public required Matrix4x4[] InverseBind;
    public required Matrix4x4[] RestJoints;
    public required string[] JointNames;
    public required int LogicalIndex;   // which of the file's skins
}

// A vertex's colour (glTF COLOR_0), four bytes in the order MonoGame's `Color` keeps them (red first), which
// the client's vertex declarations read as COLOR0, 0 to 1. White without one, so it multiplies to nothing.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly record struct VertexColour(byte R, byte G, byte B, byte A)
{
    public static readonly VertexColour White = new(255, 255, 255, 255);

    public static VertexColour From(Vector4 c)
    {
        static byte B(float v) => (byte)MathF.Round(Math.Clamp(float.IsFinite(v) ? v : 1f, 0f, 1f) * 255f);
        return new VertexColour(B(c.X), B(c.Y), B(c.Z), B(c.W));
    }
}

// A rigid vertex (52 bytes), laid out byte for byte as the client's VertexMesh: VertexPositionNormalTexture's
// three elements, then the tangent (issue #410; w is the bitangent's sign) and the colour. The client hands
// the array to a vertex buffer as it is, and a cooked file is the array's bytes.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct MeshVertex : IEquatable<MeshVertex>
{
    public readonly Vector3 Position;
    public readonly Vector3 Normal;
    public readonly Vector2 TextureCoordinate;
    public readonly Vector4 Tangent;
    public readonly VertexColour Colour;

    public MeshVertex(Vector3 position, Vector3 normal, Vector2 uv, Vector4 tangent, VertexColour colour)
    {
        Position = position;
        Normal = normal;
        TextureCoordinate = uv;
        Tangent = tangent;
        Colour = colour;
    }

    public bool Equals(MeshVertex other) =>
        Position == other.Position && Normal == other.Normal && TextureCoordinate == other.TextureCoordinate
        && Tangent == other.Tangent && Colour == other.Colour;
    public override bool Equals(object? obj) => obj is MeshVertex other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Position, Normal, TextureCoordinate, Tangent, Colour);

    public const int Size = 52;
}

// A skinned vertex (issue #117), laid out as the client's VertexSkinned (72 bytes): lit.fx's `Skinned`
// technique reads BLENDINDICES0 as four joint indices (bytes) and BLENDWEIGHT0 as their weights; the
// tangent and colour (issue #410) come after them, so the first five elements keep their places.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct SkinnedMeshVertex : IEquatable<SkinnedMeshVertex>
{
    public readonly Vector3 Position;
    public readonly Vector3 Normal;
    public readonly Vector2 TextureCoordinate;
    public readonly byte Joint0, Joint1, Joint2, Joint3;
    public readonly Vector4 Weights;
    public readonly Vector4 Tangent;
    public readonly VertexColour Colour;

    public SkinnedMeshVertex(Vector3 position, Vector3 normal, Vector2 uv, ReadOnlySpan<int> joints, Vector4 weights, Vector4 tangent, VertexColour colour)
    {
        Position = position;
        Normal = normal;
        TextureCoordinate = uv;
        Joint0 = (byte)joints[0];
        Joint1 = (byte)joints[1];
        Joint2 = (byte)joints[2];
        Joint3 = (byte)joints[3];
        Weights = weights;
        Tangent = tangent;
        Colour = colour;
    }

    private SkinnedMeshVertex(in SkinnedMeshVertex from, Vector3 position, Vector3 normal)
    {
        this = from;
        Position = position;
        Normal = normal;
    }

    // The same vertex moved: what a morph target does to it (issue #363).
    public SkinnedMeshVertex WithShape(Vector3 position, Vector3 normal) => new(in this, position, normal);

    public bool Equals(SkinnedMeshVertex other) =>
        Position == other.Position && Normal == other.Normal && TextureCoordinate == other.TextureCoordinate
        && Joint0 == other.Joint0 && Joint1 == other.Joint1 && Joint2 == other.Joint2 && Joint3 == other.Joint3
        && Weights == other.Weights && Tangent == other.Tangent && Colour == other.Colour;
    public override bool Equals(object? obj) => obj is SkinnedMeshVertex other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Position, Normal, TextureCoordinate, Weights, Tangent, Colour);

    public const int Size = 72;
}
