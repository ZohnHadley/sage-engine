#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SharpGLTF.Schema2;
using N = System.Numerics;

namespace Sage.Client;

// `r_testskin 1` (issue #117): a generated skinned model in front of the camera, bending, so GPU
// skinning can be seen working (and smoke-tested) with no content at all. The model is written as a
// .glb in memory and read back through GltfLoader, so the loader's skinned path is what draws it.
//
// The model: a square column two metres tall on two joints, `root` at its foot and `bend` a metre up.
// Rings below the middle follow the root, rings above follow `bend`, and the middle ring is shared
// half and half — the classic skinning test, where a bad palette shows as a torn or collapsed joint.
internal static class TestSkinModel
{
    public static readonly AssetPath Path = AssetPath.Intern("generated/testskin.glb");
    public const int Joints = 2;

    public static byte[] Build()
    {
        const int sides = 4, rings = 5;
        const float half = 0.25f, height = 2f;

        var positions = new List<N.Vector3>();
        var normals = new List<N.Vector3>();
        var joints = new List<ushort>();
        var weights = new List<N.Vector4>();
        for (int ring = 0; ring < rings; ring++)
        {
            float y = height * ring / (rings - 1);
            float up = y / height;   // 0 at the foot, 1 at the top
            // Weight of `bend`: 0 up to a quarter, 1 from three quarters, blended between.
            float w = Math.Clamp((up - 0.25f) * 2f, 0f, 1f);
            for (int side = 0; side < sides; side++)
            {
                float a = MathF.PI * 2f * (side + 0.5f) / sides;
                var n = new N.Vector3(MathF.Cos(a), 0f, MathF.Sin(a));
                positions.Add(new N.Vector3(n.X * half * MathF.Sqrt(2f), y, n.Z * half * MathF.Sqrt(2f)));
                normals.Add(n);
                joints.AddRange(new ushort[] { 0, 1, 0, 0 });
                weights.Add(new N.Vector4(1f - w, w, 0f, 0f));
            }
        }

        var indices = new List<uint>();
        for (int ring = 0; ring < rings - 1; ring++)
            for (int side = 0; side < sides; side++)
            {
                uint a = (uint)(ring * sides + side), b = (uint)(ring * sides + (side + 1) % sides);
                uint c = a + sides, d = b + sides;
                // Counter-clockwise seen from outside (glTF's front faces).
                indices.AddRange(new[] { a, c, b, b, c, d });
            }

        var model = ModelRoot.CreateModel();
        var scene = model.UseScene("testskin");
        model.DefaultScene = scene;
        var root = scene.CreateNode("root");
        var bend = root.CreateNode("bend");
        bend.LocalMatrix = N.Matrix4x4.CreateTranslation(0f, height / 2f, 0f);

        var mesh = model.CreateMesh("column");
        var primitive = mesh.CreatePrimitive();
        primitive.SetVertexAccessor("POSITION", Accessor(model, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(positions)), positions.Count, DimensionType.VEC3, EncodingType.FLOAT));
        primitive.SetVertexAccessor("NORMAL", Accessor(model, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(normals)), normals.Count, DimensionType.VEC3, EncodingType.FLOAT));
        primitive.SetVertexAccessor("JOINTS_0", Accessor(model, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(joints)), positions.Count, DimensionType.VEC4, EncodingType.UNSIGNED_SHORT));
        primitive.SetVertexAccessor("WEIGHTS_0", Accessor(model, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(weights)), weights.Count, DimensionType.VEC4, EncodingType.FLOAT));

        var indexView = model.UseBufferView(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(indices)).ToArray(), 0, null, 0, BufferMode.ELEMENT_ARRAY_BUFFER);
        var indexAccessor = model.CreateAccessor("indices");
        indexAccessor.SetIndexData(indexView, 0, indices.Count, IndexEncodingType.UNSIGNED_INT);
        primitive.SetIndexAccessor(indexAccessor);

        var skin = model.CreateSkin("testskin");
        skin.BindJoints(N.Matrix4x4.Identity, root, bend);   // inverse bind matrices from the pose above
        var body = scene.CreateNode("body");
        body.Mesh = mesh;
        body.Skin = skin;

        using var stream = new MemoryStream();
        model.WriteGLB(stream);
        return stream.ToArray();
    }

    private static Accessor Accessor(ModelRoot model, ReadOnlySpan<byte> bytes, int count, DimensionType dimensions, EncodingType encoding)
    {
        var view = model.UseBufferView(bytes.ToArray(), 0, null, 0, BufferMode.ARRAY_BUFFER);
        var accessor = model.CreateAccessor();
        accessor.SetVertexData(view, 0, count, new SharpGLTF.Memory.AttributeFormat(dimensions, encoding, false));
        return accessor;
    }
}

// FrameUpdate: while `r_testskin` is on, keeps one test column five metres in front of where the
// screen's camera was when it was turned on, and poses it every frame the way #118's animator will:
// the file's skeleton read by #116's GltfAnimationReader, a SkeletonPose with `bend` swung ±60° about
// Z, PoseSampler.ToModelSpace, and the pose handed to the renderer through `SkinPoses`. Turning it off
// removes it.
[System("sage.client.testskin", Phase.FrameUpdate, Condition = RunCondition.DevOnly)]
internal sealed class TestSkinSystem : ISystem
{
    private readonly Renderer _renderer;
    private readonly CVar<bool> _enabled;
    private readonly SkinPoses _poses;
    private Entity _column;
    private bool _registered;
    private SkeletonPose? _pose;
    private int _bend;

    public TestSkinSystem(World world, Renderer renderer, CVar<bool> enabled)
    {
        _renderer = renderer;
        _enabled = enabled;
        _poses = world.Resources.GetOrAdd(() => new SkinPoses());
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        bool alive = !_column.IsNull && world.IsAlive(_column);
        if (!_enabled.Value)
        {
            if (alive) Remove(world);
            return;
        }

        if (!alive)
        {
            if (!world.TryGetMainView(out var eye)) return;   // nothing to stand it in front of yet
            if (!_registered)
            {
                var bytes = TestSkinModel.Build();
                using (var glb = new MemoryStream(bytes)) _renderer.RegisterMesh(TestSkinModel.Path, glb);
                using (var glb = new MemoryStream(bytes))
                {
                    var skeleton = GltfAnimationReader.Read(glb, TestSkinModel.Path.ToString())?.Skeleton;
                    if (skeleton != null)
                    {
                        _pose = new SkeletonPose(skeleton);
                        _bend = skeleton.IndexOf("bend");
                    }
                }
                _registered = true;
            }
            var forward = new N.Vector3(eye.Forward.X, 0f, eye.Forward.Z);
            forward = forward.LengthSquared() > 1e-6f ? N.Vector3.Normalize(forward) : -N.Vector3.UnitZ;
            // Standing on the ground in front of an eye at about a player's height, and turned so its
            // local Z runs along the view: it bends across the picture, not towards the camera.
            var foot = eye.Position + forward * 5f - N.Vector3.UnitY * 1.6f;
            var place = Transform.At(foot);
            place.LocalRotation = N.Quaternion.CreateFromYawPitchRoll(MathF.Atan2(-forward.X, -forward.Z), 0f, 0f);
            _column = world.Create(place, "r_testskin");
            world.Add(_column, new SkinnedMeshRenderer { Mesh = TestSkinModel.Path });
            Log.Info(LogCat.Render, $"r_testskin: a skinned column ({TestSkinModel.Joints} joints) at {foot}");
        }

        if (_pose == null || _bend < 0) return;   // the reader warned: drawn in its rest pose
        _pose.ResetToRest();
        float angle = MathF.Sin((float)ctx.Frame.RealTime * 1.5f) * (MathF.PI / 3f);
        _pose.Local[_bend].Rotation = N.Quaternion.CreateFromAxisAngle(N.Vector3.UnitZ, angle);
        PoseSampler.ToModelSpace(_pose.Skeleton, _pose);
        _poses.Set(_column, _pose);
    }

    private void Remove(World world)
    {
        _poses.Remove(_column);
        world.Destroy(_column);
        _column = default;
    }
}
