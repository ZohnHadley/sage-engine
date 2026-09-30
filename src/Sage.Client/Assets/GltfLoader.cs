#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Graphics.PackedVector;

namespace Sage.Client;

// Models, at runtime (docs/design/05 §3.4, TODO R12).
//
// **glTF, read when it is asked for.** The engine used to load models as `.xnb`, which meant MGCB had
// to run at build time and a model could not come from a mod, a downloaded asset, or a folder somebody
// dropped a file into. A `.glb` is a self-contained file with its buffers inside it, which is exactly
// what a VFS mount hands back (05 §3.2).
//
// What the engine wants out of it is small: positions, normals, texture coordinates and indices, in one
// buffer per primitive — plus, for a skinned primitive (issue #117), four joint indices and four weights
// per vertex and the skin's inverse bind and rest-pose joint matrices. Materials come from the
// *material record* pointed at by whatever draws the mesh (07 §3.3), so the file's own materials are
// read past — a model is geometry here, not a look.
internal static class GltfLoader
{
    // One drawable primitive: `Rigid` vertices baked into model space by their node's transform, or
    // `Skinned` vertices left in the skin's bind space (glTF ignores a skinned mesh node's own
    // transform: its joints place it). Exactly one of the two is set.
    internal sealed class Part
    {
        public VertexPositionNormalTexture[]? Rigid;
        public VertexSkinned[]? Skinned;
        public required int[] Indices;
        public int VertexCount => Rigid?.Length ?? Skinned!.Length;
    }

    // The file's skin (the first one; a model with several is drawn with the first, logged): each
    // joint's inverse bind matrix and where it stands in the file's rest pose, both in model space.
    internal sealed class Skin
    {
        public required System.Numerics.Matrix4x4[] InverseBind;
        public required System.Numerics.Matrix4x4[] RestJoints;
        public required string[] JointNames;
        public required int LogicalIndex;   // which of the file's skins
    }

    internal sealed class Model
    {
        public readonly List<Part> Parts = new();
        public Skin? Skin;
        public BoundingSphere Bounds = new(Vector3.Zero, 1f);
    }

    // Reads every primitive of every mesh in the file. Returns false when the file is not glTF or has
    // nothing drawable in it, which is a content problem and not a crash.
    public static bool TryLoad(Stream stream, string name, out Model model)
    {
        model = new Model();

        SharpGLTF.Schema2.ModelRoot root;
        try
        {
            // `ReadGLB` for the binary form; the text form has its buffers beside it, which a VFS mount
            // cannot always give us, so a model that ships as `.gltf` plus a `.bin` is not supported
            // (05 §8: say so rather than half-loading it).
            root = SharpGLTF.Schema2.ModelRoot.ReadGLB(stream, new SharpGLTF.Schema2.ReadSettings());
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat.Assets, $"Model '{name}': not a .glb ({ex.GetType().Name}: {ex.Message})");
            return false;
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        try
        {
            // `Collect` recurses, so this wants *roots* only. `LogicalNodes` is every node in the file,
            // children included, so the fallback for a file with no default scene has to filter them —
            // feeding it the flat list draws a hierarchy once per level of itself.
            var roots = root.DefaultScene?.VisualChildren
                        ?? System.Linq.Enumerable.Where(root.LogicalNodes, n => n.VisualParent == null);
            foreach (var node in roots) Collect(node, name, model, ref min, ref max);
        }
        catch (Exception ex)
        {
            // Reading geometry can throw on a file this loader does not handle — a primitive drawn as
            // points or lines has no triangles to ask for. Same contract as a bad header: a content
            // problem, logged, drawn as the error mesh.
            Log.Warn(LogCat.Assets, $"Model '{name}': could not read geometry ({ex.GetType().Name}: {ex.Message})");
            return false;
        }

        if (model.Parts.Count == 0)
        {
            Log.Warn(LogCat.Assets, $"Model '{name}': no drawable primitives");
            return false;
        }

        var centre = (min + max) * 0.5f;
        model.Bounds = new BoundingSphere(centre, Math.Max((max - centre).Length(), 0.001f));
        return true;
    }

    private static void Collect(SharpGLTF.Schema2.Node node, string name, Model model, ref Vector3 min, ref Vector3 max)
    {
        foreach (var child in node.VisualChildren) Collect(child, name, model, ref min, ref max);
        if (node.Mesh == null) return;

        // A skinned node: its vertices stay in bind space, and its skin says where they go.
        System.Numerics.Matrix4x4[]? restPalette = null;
        if (node.Skin != null)
        {
            model.Skin ??= ReadSkin(node.Skin, name);
            if (model.Skin.LogicalIndex != node.Skin.LogicalIndex)
            {
                Log.Warn(LogCat.Assets, $"Model '{name}': node '{node.Name}' uses a second skin; only the first is supported, so it is not drawn");
                return;
            }
            restPalette = new System.Numerics.Matrix4x4[model.Skin.RestJoints.Length];
            SkinMath.Palette(model.Skin.RestJoints, model.Skin.InverseBind, restPalette);
        }

        // The node's own place in the file, folded into rigid vertices: the engine draws a mesh at an
        // entity's transform, and a model with its own hierarchy would otherwise arrive inside out.
        Matrix transform = node.WorldMatrix;   // System.Numerics → MonoGame (implicit)

        Span<int> j = stackalloc int[SkinMath.Influences];
        Span<float> w = stackalloc float[SkinMath.Influences];
        foreach (var primitive in node.Mesh.Primitives)
        {
            var positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array();
            if (positions == null || positions.Count == 0) continue;

            var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
            var uvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
            var joints = restPalette != null ? primitive.GetVertexAccessor("JOINTS_0")?.AsVector4Array() : null;
            var weights = joints != null ? primitive.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array() : null;
            bool skinned = joints != null && weights != null;
            if (restPalette != null && !skinned)
                Log.Warn(LogCat.Assets, $"Model '{name}': a primitive of skinned node '{node.Name}' has no JOINTS_0/WEIGHTS_0; drawn rigid");

            var part = new Part { Indices = Triangles(primitive) };
            if (part.Indices.Length == 0) continue;
            if (skinned) part.Skinned = new VertexSkinned[positions.Count];
            else part.Rigid = new VertexPositionNormalTexture[positions.Count];

            for (int i = 0; i < positions.Count; i++)
            {
                var p = new Vector3(positions[i].X, positions[i].Y, positions[i].Z);
                var n = normals != null && i < normals.Count ? new Vector3(normals[i].X, normals[i].Y, normals[i].Z) : Vector3.Up;
                var uv = uvs != null && i < uvs.Count ? new Vector2(uvs[i].X, uvs[i].Y) : Vector2.Zero;

                if (skinned)
                {
                    Influences(joints![i], i < weights!.Count ? weights[i] : default, restPalette!.Length, j, w);
                    n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.Up;
                    part.Skinned![i] = new VertexSkinned(p, n, uv, new Byte4(j[0], j[1], j[2], j[3]), new Vector4(w[0], w[1], w[2], w[3]));

                    // Bounds in the rest pose, which is where the vertices are when nothing poses them.
                    Vector3 rest = SkinMath.SkinPosition(p.ToNumerics(), restPalette, j, w);   // System.Numerics → MonoGame (implicit)
                    min = Vector3.Min(min, rest);
                    max = Vector3.Max(max, rest);
                    continue;
                }

                p = Vector3.Transform(p, transform);
                n = Vector3.TransformNormal(n, transform);
                // A zero-length normal normalises to NaN, and a NaN in a vertex buffer is invisible
                // geometry with nothing in the log to explain it. Exporters do write them.
                n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.Up;
                part.Rigid![i] = new VertexPositionNormalTexture(p, n, uv);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            model.Parts.Add(part);
            Log.Debug(LogCat.Assets, $"Model {name}: {(skinned ? "skinned " : "")}primitive with {part.VertexCount} vertices, {part.Indices.Length / 3} triangles");
        }
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

    private static Skin ReadSkin(SharpGLTF.Schema2.Skin skin, string name)
    {
        int count = skin.JointsCount;
        var result = new Skin
        {
            InverseBind = new System.Numerics.Matrix4x4[count],
            RestJoints = new System.Numerics.Matrix4x4[count],
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

    // A vertex's four influences, cleaned up: a joint the palette cannot hold (past MaxBones, or past
    // the skin) loses its weight, the weights are made to sum to one, and a vertex left with no weight
    // at all follows joint 0 rather than collapsing to the origin.
    internal static void Influences(System.Numerics.Vector4 joints, System.Numerics.Vector4 weights, int jointCount, Span<int> j, Span<float> w)
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

// A skinned vertex (issue #117): lit.fx's `Skinned` technique reads BLENDINDICES0 as four joint
// indices into the draw's palette and BLENDWEIGHT0 as their weights. The first three elements are
// VertexPositionNormalTexture's, so an effect that does not skin (a material without a `Skinned`
// technique) still draws it, in its bind pose.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct VertexSkinned : IVertexType
{
    public readonly Vector3 Position;
    public readonly Vector3 Normal;
    public readonly Vector2 TextureCoordinate;
    public readonly Byte4 BlendIndices;
    public readonly Vector4 BlendWeights;

    public VertexSkinned(Vector3 position, Vector3 normal, Vector2 uv, Byte4 indices, Vector4 weights)
    {
        Position = position;
        Normal = normal;
        TextureCoordinate = uv;
        BlendIndices = indices;
        BlendWeights = weights;
    }

    public static readonly VertexDeclaration VertexDeclaration = new(
        new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
        new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
        new VertexElement(24, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
        new VertexElement(32, VertexElementFormat.Byte4, VertexElementUsage.BlendIndices, 0),
        new VertexElement(36, VertexElementFormat.Vector4, VertexElementUsage.BlendWeight, 0));

    VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;
}
