#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;

namespace Sage.Testing;

// A small skinned `.glb`, written in code with SharpGLTF's Schema2 API (issue #116), so skeletal
// animation is tested against a real file without the repository shipping one. The model:
//
//   - a 3-joint chain, root → mid → tip, each child BoneLength above its parent (+Y), rest rotation
//     identity, rest scale one; the skin's inverse bind matrices are the inverse of each joint's rest
//     model-space matrix (root at 0, mid at +1, tip at +2);
//   - a thin strip mesh along it, six vertices at y = 0, 1, 2, each weighted fully to the joint at its
//     height (JOINTS_0 unsigned bytes, WEIGHTS_0 floats), on a node that carries the skin;
//   - two clips with known keys:
//       "idle" (2 s): root translation LINEAR 0 s (0,0,0), 1 s (0,0.1,0), 2 s (0,0,0);
//                     tip scale STEP 0 s (1,1,1), 1 s (2,2,2);
//       "walk" (1 s, meant to loop): mid rotation LINEAR about +Z, 0 s identity, 0.5 s 90°, 1 s identity;
//                     root translation CUBICSPLINE, 0 s value (0,0,0) out-tangent (1,0,0),
//                     1 s in-tangent (3,0,0) value (2,0,0) — (0.75,0,0) at 0.5 s.
//
// `withSkin: false` writes the same nodes, mesh and clips with no skin, for the reader's warning.
public static class SkinnedModelBuilder
{
    public const string Root = "root", Mid = "mid", Tip = "tip";
    public const string Idle = "idle", Walk = "walk";
    public const float BoneLength = 1f;
    public const float IdleDuration = 2f, WalkDuration = 1f;

    public static byte[] Build(bool withSkin = true)
    {
        var model = ModelRoot.CreateModel();
        var scene = model.UseScene("scene");

        var root = scene.CreateNode(Root);
        var mid = root.CreateNode(Mid);
        mid.LocalMatrix = Matrix4x4.CreateTranslation(0, BoneLength, 0);
        var tip = mid.CreateNode(Tip);
        tip.LocalMatrix = Matrix4x4.CreateTranslation(0, BoneLength, 0);

        var body = scene.CreateNode("body");
        body.Mesh = BuildMesh(model, withSkin);
        if (withSkin)
        {
            var skin = model.CreateSkin("rig");
            skin.BindJoints(Matrix4x4.Identity, root, mid, tip);
            body.Skin = skin;
        }

        var idle = model.CreateAnimation(Idle);
        idle.CreateTranslationChannel(root, new Dictionary<float, Vector3>
        {
            [0f] = Vector3.Zero, [1f] = new Vector3(0, 0.1f, 0), [2f] = Vector3.Zero,
        }, true);
        idle.CreateScaleChannel(tip, new Dictionary<float, Vector3>
        {
            [0f] = Vector3.One, [1f] = new Vector3(2, 2, 2),
        }, false);   // linear: false is STEP

        var walk = model.CreateAnimation(Walk);
        walk.CreateRotationChannel(mid, new Dictionary<float, Quaternion>
        {
            [0f] = Quaternion.Identity,
            [0.5f] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
            [1f] = Quaternion.Identity,
        }, true);
        walk.CreateTranslationChannel(root, new Dictionary<float, (Vector3, Vector3, Vector3)>
        {
            [0f] = (Vector3.Zero, Vector3.Zero, new Vector3(1, 0, 0)),
            [1f] = (new Vector3(3, 0, 0), new Vector3(2, 0, 0), Vector3.Zero),
        });

        return model.WriteGLB().ToArray();
    }

    // Writes Build(withSkin) to `file` (folders made as needed) and returns the path.
    public static string Write(string file, bool withSkin = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllBytes(file, Build(withSkin));
        return file;
    }

    private static Mesh BuildMesh(ModelRoot model, bool withSkin)
    {
        const float w = 0.1f;
        var positions = new Vector3[]
        {
            new(-w, 0, 0), new(w, 0, 0),
            new(-w, BoneLength, 0), new(w, BoneLength, 0),
            new(-w, 2 * BoneLength, 0), new(w, 2 * BoneLength, 0),
        };
        var joints = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0 };
        var weights = new Vector4[6];
        for (int i = 0; i < weights.Length; i++) weights[i] = new Vector4(1, 0, 0, 0);
        var indices = new ushort[] { 0, 1, 3, 0, 3, 2, 2, 3, 5, 2, 5, 4 };

        var mesh = model.CreateMesh("strip");
        var primitive = mesh.CreatePrimitive();
        primitive.DrawPrimitiveType = PrimitiveType.TRIANGLES;

        var position = model.CreateAccessor("positions");
        position.SetVertexData(View(model, MemoryMarshal.AsBytes(positions.AsSpan()).ToArray(), BufferMode.ARRAY_BUFFER), 0, positions.Length,
                               new AttributeFormat(DimensionType.VEC3, EncodingType.FLOAT, false));
        position.UpdateBounds();
        primitive.SetVertexAccessor("POSITION", position);

        if (withSkin)
        {
            var joint = model.CreateAccessor("joints");
            joint.SetVertexData(View(model, joints, BufferMode.ARRAY_BUFFER), 0, positions.Length,
                                new AttributeFormat(DimensionType.VEC4, EncodingType.UNSIGNED_BYTE, false));
            primitive.SetVertexAccessor("JOINTS_0", joint);

            var weight = model.CreateAccessor("weights");
            weight.SetVertexData(View(model, MemoryMarshal.AsBytes(weights.AsSpan()).ToArray(), BufferMode.ARRAY_BUFFER), 0, weights.Length,
                                 new AttributeFormat(DimensionType.VEC4, EncodingType.FLOAT, false));
            primitive.SetVertexAccessor("WEIGHTS_0", weight);
        }

        var index = model.CreateAccessor("indices");
        // Padded to four bytes: glTF wants every buffer view aligned.
        var indexBytes = new byte[(indices.Length * 2 + 3) & ~3];
        MemoryMarshal.AsBytes(indices.AsSpan()).CopyTo(indexBytes);
        index.SetIndexData(View(model, indexBytes, BufferMode.ELEMENT_ARRAY_BUFFER), 0, indices.Length, IndexEncodingType.UNSIGNED_SHORT);
        primitive.SetIndexAccessor(index);
        return mesh;
    }

    private static BufferView View(ModelRoot model, byte[] bytes, BufferMode mode) => model.UseBufferView(bytes, 0, null, 0, mode);
}
