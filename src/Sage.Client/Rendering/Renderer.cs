#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace sage_engine;

public struct RenderStats
{
    public int Items, Culled, DrawCalls, Triangles, MaterialSwitches;
}

// A drawable piece of a mesh: one ModelMeshPart with its bone transform baked in (06 §4).
internal sealed class MeshPart
{
    public required VertexBuffer VertexBuffer;
    public required IndexBuffer IndexBuffer;
    public int VertexOffset, StartIndex, PrimitiveCount;
    public Matrix Bone = Matrix.Identity;      // mesh space → model space
    public BoundingSphere Bounds;              // model space
}

internal sealed class MeshData
{
    public required string Name;
    public required MeshPart[] Parts;
    public bool IsError;
}

// The client renderer (docs/design/06): owns GPU-side meshes by id and the material cache, and draws a
// RenderSnapshot through a fixed pass list: clear (sky) → opaque → alpha-tested → transparent, in
// sort-key order (06 §3.4–3.5). The Overlay pass (ImGui) is drawn by the host afterwards.
// Provided to modules by ClientModule (ctx.Get<Renderer>()).
public sealed class Renderer : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly ContentService _content;
    private readonly List<MeshData> _meshes = new();
    private readonly Dictionary<AssetPath, int> _meshIds = new();
    private readonly CVar<bool> _fog;
    private readonly CVar<bool> _wireframe;
    private readonly CVar<bool> _freezeCull;
    private long _frame;

    internal Renderer(ClientHost host, ContentService content, Engine engine)
    {
        _device = host.GraphicsDevice;
        _content = content;
        Materials = new MaterialCache(_device, content, engine.Records);
        engine.Records.Reloaded += Materials.Invalidate;
        _meshes.Add(CreateErrorMesh(_device));   // id 0

        var cvars = engine.CVars;
        _fog = cvars.Register("r_fog", true, CVarFlags.None, "Distance fog (the environment's fog settings).");
        _wireframe = cvars.Register("r_wireframe", false, CVarFlags.DevOnly | CVarFlags.Cheat, "Draw the scene as wireframe.");
        _freezeCull = cvars.Register("r_freezecull", false, CVarFlags.DevOnly | CVarFlags.Cheat, "Keep the current culling frustum while the camera moves.");
        cvars.RegisterCommand("r_stats", CVarFlags.None, "Print last frame's render stats.", _ =>
            Log.Info(LogCat.Console, $"  items {LastFrame.Items}, culled {LastFrame.Culled}, draw calls {LastFrame.DrawCalls}, triangles {LastFrame.Triangles}, " +
                                     $"material switches {LastFrame.MaterialSwitches}; {_meshes.Count - 1} meshes, {Materials.Count} materials"));
        cvars.RegisterCommand("mat_list", CVarFlags.None, "List materials: id, effect, technique, pass, items drawn last frame.", _ =>
        {
            foreach (var (id, record, m) in Materials.Entries)
                Log.Info(LogCat.Console, m == null
                    ? $"  {id,3} {record} (not built yet)"
                    : $"  {id,3} {record}{(m.IsError ? " → sage:error" : "")}  {m.Effect.Effect.Name}/{m.Technique.Name} {m.Pass}, drawn {(m.DrawnFrame == _frame ? m.Drawn : 0)}");
        });
        cvars.RegisterCommand("mat_info", CVarFlags.None, "mat_info <id>: the merged material record and which file set each field.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "mat_info <id>"); return; }
            RecordId id;
            try { id = RecordId.Parse(a[0], "sage"); }
            catch (FormatException ex) { Log.Warn(LogCat.Console, ex.Message); return; }
            if (!a[0].Contains(':')) id = engine.Records.Ids("material").FirstOrDefault(i => i.Name == a[0]);
            Log.Info(LogCat.Console, engine.Records.Describe("material", id));
        });
    }

    internal GraphicsDevice Device => _device;
    internal MaterialCache Materials { get; }
    public RenderStats LastFrame { get; private set; }

    internal bool FogEnabled => _fog.Value;
    internal bool FreezeCull => _freezeCull.Value;

    // ---- Meshes ----

    // The mesh id for an asset path, loading it on first use. Missing → 0, the error mesh (06 §8).
    internal int ResolveMesh(AssetPath path)
    {
        if (_meshIds.TryGetValue(path, out int id)) return id;
        id = 0;
        var model = _content.LoadModel(path);
        if (model == null)
            Log.Warn(LogCat.Render, $"Mesh '{path}' unavailable; drawing the error mesh");
        else
        {
            var bones = new Matrix[model.Bones.Count];
            model.CopyAbsoluteBoneTransformsTo(bones);
            var parts = new List<MeshPart>();
            foreach (var mesh in model.Meshes)
            {
                var bone = bones[mesh.ParentBone.Index];
                foreach (var part in mesh.MeshParts)
                {
                    parts.Add(new MeshPart
                    {
                        VertexBuffer = part.VertexBuffer,
                        IndexBuffer = part.IndexBuffer,
                        VertexOffset = part.VertexOffset,
                        StartIndex = part.StartIndex,
                        PrimitiveCount = part.PrimitiveCount,
                        Bone = bone,
                        Bounds = mesh.BoundingSphere.Transform(bone),
                    });
                    Log.Debug(LogCat.Render, $"Mesh {path}: part with {part.NumVertices} vertices, {part.PrimitiveCount} triangles, " +
                                             $"layout {string.Join(" ", part.VertexBuffer.VertexDeclaration.GetVertexElements().Select(e => $"{e.VertexElementUsage}{e.UsageIndex}"))}");
                }
            }
            id = _meshes.Count;
            _meshes.Add(new MeshData { Name = path.ToString(), Parts = parts.ToArray() });
        }
        _meshIds[path] = id;
        return id;
    }

    internal MeshData Mesh(int id) => _meshes[id];

    // ---- Drawing ----

    internal void Draw(RenderSnapshot s)
    {
        _frame++;
        ref readonly var env = ref s.Environment;
        _device.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, new Color(env.ClearColor), 1f, 0);   // pass 3, sky: a clear colour in v1
        var stats = new RenderStats { Items = s.Items.Count, Culled = s.Culled };
        if (!s.HasView) { LastFrame = stats; return; }

        int n = s.Items.Count;
        if (s.SortKeys.Length < n)
        {
            s.SortKeys = new ulong[Math.Max(n, s.SortKeys.Length * 2)];
            s.Order = new int[s.SortKeys.Length];
        }
        for (int i = 0; i < n; i++)
        {
            s.SortKeys[i] = s.Items[i].SortKey;
            s.Order[i] = i;
        }
        Array.Sort(s.SortKeys, s.Order, 0, n);   // TODO(06 §3.5): radix sort when item counts grow

        bool wire = _wireframe.Value;
        int current = -1;
        for (int k = 0; k < n; k++)
        {
            ref var item = ref s.Items[s.Order[k]];
            var m = Materials.Get(item.Material);
            if (m == null) continue;
            var binding = m.Effect;
            if (binding.FrameStamp != _frame)
            {
                binding.SetFrame(s.View, env);
                binding.FrameStamp = _frame;
            }
            if (item.Material != current)
            {
                MaterialCache.Apply(_device, m, wire);
                current = item.Material;
                stats.MaterialSwitches++;
            }
            if (m.DrawnFrame != _frame) { m.DrawnFrame = _frame; m.Drawn = 0; }
            m.Drawn++;

            binding.World?.SetValue(item.World);
            binding.Tint?.SetValue(item.Tint);
            var part = _meshes[item.Mesh].Parts[item.Part];
            _device.SetVertexBuffer(part.VertexBuffer);
            _device.Indices = part.IndexBuffer;
            foreach (var pass in m.Technique.Passes)
            {
                pass.Apply();
                _device.DrawIndexedPrimitives(PrimitiveType.TriangleList, part.VertexOffset, part.StartIndex, part.PrimitiveCount);
                stats.DrawCalls++;
                stats.Triangles += part.PrimitiveCount;
            }
        }
        LastFrame = stats;
    }

    // A 1 m cube (06 §8: the error mesh is drawn, never skipped silently).
    private static MeshData CreateErrorMesh(GraphicsDevice device)
    {
        var vertices = new List<VertexPositionNormalTexture>();
        var indices = new List<short>();
        Vector3[] normals = { Vector3.Up, Vector3.Down, Vector3.Left, Vector3.Right, Vector3.Forward, Vector3.Backward };
        foreach (var normal in normals)
        {
            var side1 = new Vector3(normal.Y, normal.Z, normal.X);
            var side2 = Vector3.Cross(normal, side1);
            short start = (short)vertices.Count;
            indices.AddRange(new[] { start, (short)(start + 1), (short)(start + 2), start, (short)(start + 2), (short)(start + 3) });
            vertices.Add(new VertexPositionNormalTexture((normal - side1 - side2) * 0.5f, normal, Vector2.Zero));
            vertices.Add(new VertexPositionNormalTexture((normal - side1 + side2) * 0.5f, normal, Vector2.UnitX));
            vertices.Add(new VertexPositionNormalTexture((normal + side1 + side2) * 0.5f, normal, Vector2.One));
            vertices.Add(new VertexPositionNormalTexture((normal + side1 - side2) * 0.5f, normal, Vector2.UnitY));
        }
        var vb = new VertexBuffer(device, VertexPositionNormalTexture.VertexDeclaration, vertices.Count, BufferUsage.WriteOnly);
        vb.SetData(vertices.ToArray());
        var ib = new IndexBuffer(device, IndexElementSize.SixteenBits, indices.Count, BufferUsage.WriteOnly);
        ib.SetData(indices.ToArray());
        return new MeshData
        {
            Name = "(error)",
            IsError = true,
            Parts = new[] { new MeshPart { VertexBuffer = vb, IndexBuffer = ib, PrimitiveCount = indices.Count / 3, Bounds = new BoundingSphere(Vector3.Zero, 0.87f) } },
        };
    }

    public void Dispose()
    {
        var error = _meshes[0].Parts[0];
        error.VertexBuffer.Dispose();
        error.IndexBuffer.Dispose();
        Materials.Dispose();
        // Model buffers, effects and textures belong to the ContentService.
    }
}
