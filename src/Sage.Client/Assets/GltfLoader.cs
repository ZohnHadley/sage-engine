#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace sage_engine;

// Models, at runtime (docs/design/05 §3.4, TODO R12).
//
// **glTF, read when it is asked for.** The engine used to load models as `.xnb`, which meant MGCB had
// to run at build time and a model could not come from a mod, a downloaded asset, or a folder somebody
// dropped a file into. A `.glb` is a self-contained file with its buffers inside it, which is exactly
// what a VFS mount hands back (05 §3.2).
//
// What the engine wants out of it is small: positions, normals, texture coordinates and indices, in one
// buffer per primitive. Materials come from the *material record* pointed at by whatever draws the mesh
// (07 §3.3), so the file's own materials are read past — a model is geometry here, not a look.
internal static class GltfLoader
{
    // Reads every primitive of every mesh in the file, baked into world space by its node's transform.
    // Returns false when the file is not glTF or has nothing drawable in it, which is a content problem
    // and not a crash.
    public static bool TryLoad(Stream stream, string name,
                               out List<(VertexPositionNormalTexture[] Vertices, int[] Indices)> parts,
                               out BoundingSphere bounds)
    {
        parts = new List<(VertexPositionNormalTexture[], int[])>();
        bounds = new BoundingSphere(Vector3.Zero, 1f);

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

        foreach (var node in root.DefaultScene?.VisualChildren ?? root.LogicalNodes)
            Collect(node, name, parts, ref min, ref max);

        if (parts.Count == 0)
        {
            Log.Warn(LogCat.Assets, $"Model '{name}': no drawable primitives");
            return false;
        }

        var centre = (min + max) * 0.5f;
        bounds = new BoundingSphere(centre, Math.Max((max - centre).Length(), 0.001f));
        return true;
    }

    private static void Collect(SharpGLTF.Schema2.Node node, string name,
                                List<(VertexPositionNormalTexture[], int[])> parts,
                                ref Vector3 min, ref Vector3 max)
    {
        foreach (var child in node.VisualChildren) Collect(child, name, parts, ref min, ref max);
        if (node.Mesh == null) return;

        // The node's own place in the file, folded into the vertices: the engine draws a mesh at an
        // entity's transform, and a model with its own hierarchy would otherwise arrive inside out.
        var world = node.WorldMatrix;
        var transform = new Matrix(world.M11, world.M12, world.M13, world.M14,
                                   world.M21, world.M22, world.M23, world.M24,
                                   world.M31, world.M32, world.M33, world.M34,
                                   world.M41, world.M42, world.M43, world.M44);

        foreach (var primitive in node.Mesh.Primitives)
        {
            var positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array();
            if (positions == null || positions.Count == 0) continue;

            var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
            var uvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();

            var vertices = new VertexPositionNormalTexture[positions.Count];
            for (int i = 0; i < positions.Count; i++)
            {
                var p = Vector3.Transform(new Vector3(positions[i].X, positions[i].Y, positions[i].Z), transform);
                var n = normals != null && i < normals.Count
                    ? Vector3.TransformNormal(new Vector3(normals[i].X, normals[i].Y, normals[i].Z), transform)
                    : Vector3.Up;
                var uv = uvs != null && i < uvs.Count ? new Vector2(uvs[i].X, uvs[i].Y) : Vector2.Zero;

                vertices[i] = new VertexPositionNormalTexture(p, Vector3.Normalize(n), uv);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

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
            if (triangles.Count == 0) continue;

            parts.Add((vertices, triangles.ToArray()));
            Log.Debug(LogCat.Assets, $"Model {name}: primitive with {vertices.Length} vertices, {triangles.Count / 3} triangles");
        }
    }
}
