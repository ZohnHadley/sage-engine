#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

public struct RenderStats
{
    public int Items, Sprites, Culled, DrawCalls, Triangles, MaterialSwitches, DebugLines;

    // Point lights extracted this frame, and the most any one draw was lit by (06 §3.9). Here because
    // lighting was otherwise only checkable by looking at a picture and saying it seemed warmer — and a
    // warm-looking wooden floor in daylight reads exactly like a lamp. A number does not.
    public int Lights, MaxLightsOnADraw;
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
    public bool Owned;   // buffers the renderer created (error mesh, terrain chunks): it disposes them
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

    // Scratch for the per-draw light selection, so a frame of a thousand items allocates nothing.
    private readonly LightSample[] _lights = new LightSample[LightRules.PerObject];
    private readonly List<Texture2D> _textures = new();
    private readonly Dictionary<AssetPath, int> _textureIds = new();
    private readonly SpriteBatcher _sprites;
    private readonly DebugLineBatch _debugLines;
    private readonly CVar<bool> _fog;
    private readonly CVar<bool> _spriteFaceCamera;
    private readonly CVar<bool> _wireframe;
    private readonly CVar<bool> _freezeCull;
    private readonly CVar<bool> _debugThroughWalls;
    private long _frame;

    internal Renderer(ClientHost host, ContentService content, Engine engine, RendererCVars settings)
    {
        _device = host.GraphicsDevice;
        _content = content;
        Materials = new MaterialCache(_device, content, engine.Records);
        engine.Records.Reloaded += Materials.Invalidate;

        // An asset changed on disk (05 §3.6, F32). The renderer holds textures by index and a built
        // material holds them by reference, so both have to let go: the table is repointed at the new
        // object and every material is rebuilt lazily, which also picks up a reloaded effect.
        content.Reloaded += path =>
        {
            if (_textureIds.TryGetValue(path, out int id) && _content.LoadTexture(path) is { } texture)
                _textures[id] = texture;
            Materials.Invalidate();
        };
        _meshes.Add(CreateErrorMesh(_device));    // id 0
        _textures.Add(Materials.MissingTexture);  // id 0: the checker placeholder
        _sprites = new SpriteBatcher(_device);
        _debugLines = new DebugLineBatch(_device);

        var cvars = engine.CVars;
        _fog = settings.Fog;
        _wireframe = settings.Wireframe;
        _spriteFaceCamera = settings.SpriteFaceCamera;
        _freezeCull = settings.FreezeCull;
        _debugThroughWalls = settings.DebugThroughWalls;
        cvars.RegisterCommand("r_stats", CVarFlags.None, "Print last frame's render stats.", _ =>
            Log.Info(LogCat.Console, $"  items {LastFrame.Items}, sprites {LastFrame.Sprites}, debug lines {LastFrame.DebugLines}, culled {LastFrame.Culled}, draw calls {LastFrame.DrawCalls}, " +
                                     $"triangles {LastFrame.Triangles}, material switches {LastFrame.MaterialSwitches}, " +
                                     $"lights {LastFrame.Lights} (max {LastFrame.MaxLightsOnADraw} on a draw); " +
                                     $"{_meshes.Count - 1} meshes, {_textures.Count - 1} textures, {Materials.Count} materials"));
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
    //
    // **Read at runtime** (R12): the file comes off a VFS mount as bytes and becomes buffers here, so a
    // model can come from a mod, a downloaded asset or a folder somebody dropped a file into — none of
    // which a build-time content pipeline can see.
    internal int ResolveMesh(AssetPath path)
    {
        if (_meshIds.TryGetValue(path, out int id)) return id;
        id = 0;

        var stream = _content.Open(path);
        if (stream == null)
            Log.Warn(LogCat.Render, $"Mesh '{path}' unavailable; drawing the error mesh");
        else
        {
            using (stream)
            {
                if (GltfLoader.TryLoad(stream, path.ToString(), out var loaded, out var bounds))
                {
                    var parts = new List<MeshPart>(loaded.Count);
                    foreach (var (vertices, indices) in loaded)
                    {
                        var vb = new VertexBuffer(_device, VertexPositionNormalTexture.VertexDeclaration,
                                                  vertices.Length, BufferUsage.WriteOnly);
                        vb.SetData(vertices);
                        var ib = new IndexBuffer(_device, IndexElementSize.ThirtyTwoBits, indices.Length,
                                                 BufferUsage.WriteOnly);
                        ib.SetData(indices);

                        parts.Add(new MeshPart
                        {
                            VertexBuffer = vb,
                            IndexBuffer = ib,
                            VertexOffset = 0,
                            StartIndex = 0,
                            PrimitiveCount = indices.Length / 3,
                            Bone = Matrix.Identity,      // the file's hierarchy is already baked in
                            Bounds = bounds,
                        });
                    }

                    id = _meshes.Count;
                    // Owned: these buffers are the renderer's, so they are disposed with it. An `.xnb`
                    // model belonged to the ContentManager, which is the thing that has gone away.
                    _meshes.Add(new MeshData { Name = path.ToString(), Parts = parts.ToArray(), Owned = true });
                }
                else
                {
                    Log.Warn(LogCat.Render, $"Mesh '{path}' could not be read; drawing the error mesh");
                }
            }
        }

        _meshIds[path] = id;
        return id;
    }

    internal MeshData Mesh(int id) => _meshes[id];

    // A mesh built by the engine or a game (terrain chunks, 14 §3): the renderer owns the buffers and
    // hands back a handle (06 §4). Destroy it with DestroyMesh when the chunk goes away.
    public MeshHandle CreateMesh(ReadOnlySpan<VertexPositionNormalTexture> vertices, ReadOnlySpan<int> indices, BoundingSphere bounds, string name = "(procedural)")
    {
        var vb = new VertexBuffer(_device, VertexPositionNormalTexture.VertexDeclaration, vertices.Length, BufferUsage.WriteOnly);
        vb.SetData(vertices.ToArray());
        var ib = new IndexBuffer(_device, IndexElementSize.ThirtyTwoBits, indices.Length, BufferUsage.WriteOnly);
        ib.SetData(indices.ToArray());
        var part = new MeshPart
        {
            VertexBuffer = vb,
            IndexBuffer = ib,
            PrimitiveCount = indices.Length / 3,
            Bounds = bounds,
        };
        _meshes.Add(new MeshData { Name = name, Parts = new[] { part }, Owned = true });
        return new MeshHandle(_meshes.Count - 1);
    }

    // A box mesh, for props and debug geometry.
    public MeshHandle CreateBox(Vector3 size, string name = "(box)")
    {
        var vertices = new List<VertexPositionNormalTexture>();
        var indices = new List<int>();
        BuildBox(size, vertices, indices);
        return CreateMesh(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices),
                          System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices),
                          new BoundingSphere(Vector3.Zero, size.Length() * 0.5f), name);
    }

    // The corners of a box centred on the origin, six quads with outward normals. Shared by CreateBox
    // and the error mesh, which were the same fourteen lines twice (review #52).
    private static void BuildBox(Vector3 size, List<VertexPositionNormalTexture> vertices, List<int> indices)
    {
        Vector3[] normals = { Vector3.Up, Vector3.Down, Vector3.Left, Vector3.Right, Vector3.Forward, Vector3.Backward };
        foreach (var normal in normals)
        {
            var side1 = new Vector3(normal.Y, normal.Z, normal.X);
            var side2 = Vector3.Cross(normal, side1);
            int start = vertices.Count;
            indices.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
            vertices.Add(new VertexPositionNormalTexture((normal - side1 - side2) * size * 0.5f, normal, Vector2.Zero));
            vertices.Add(new VertexPositionNormalTexture((normal - side1 + side2) * size * 0.5f, normal, Vector2.UnitX));
            vertices.Add(new VertexPositionNormalTexture((normal + side1 + side2) * size * 0.5f, normal, Vector2.One));
            vertices.Add(new VertexPositionNormalTexture((normal + side1 - side2) * size * 0.5f, normal, Vector2.UnitY));
        }
    }

    public void DestroyMesh(MeshHandle handle)
    {
        if (handle.IsEmpty || handle.Id >= _meshes.Count) return;
        var mesh = _meshes[handle.Id];
        if (mesh.IsError) return;
        foreach (var part in mesh.Parts)
        {
            part.VertexBuffer.Dispose();
            part.IndexBuffer.Dispose();
        }
        _meshes[handle.Id] = _meshes[0];   // the slot keeps its id; anything still drawing it gets the error mesh
    }

    // The texture id for an asset path, loading it on first use. Missing → 0, the checker (05 §8).
    internal int ResolveTexture(AssetPath path)
    {
        if (_textureIds.TryGetValue(path, out int id)) return id;
        var texture = _content.LoadTexture(path);
        if (texture == null)
        {
            id = 0;
        }
        else
        {
            id = _textures.Count;
            _textures.Add(texture);
        }
        _textureIds[path] = id;
        return id;
    }

    internal Texture2D Texture(int id) => _textures[id];

    // The frame's atlas rect as UVs in its texture (flipped horizontally for mirrored directions),
    // and its pivot as a 0..1 position inside the quad. Both need the texture size, which only the
    // client knows, so extract asks for them here.
    internal Vector4 Uv(int texture, SpriteFrame frame, bool flipU)
    {
        var t = _textures[texture];
        var (x, y, w, h) = RectOf(frame, t);
        // Half-texel inset: without it the quad's edge pixels sample the neighbouring frame in the
        // atlas (the classic sprite-sheet bleed).
        float du = 0.5f / t.Width, dv = 0.5f / t.Height;
        float u0 = x / (float)t.Width + du, v0 = y / (float)t.Height + dv;
        float u1 = (x + w) / (float)t.Width - du, v1 = (y + h) / (float)t.Height - dv;
        return flipU ? new Vector4(u1, v0, u0, v1) : new Vector4(u0, v0, u1, v1);
    }

    internal Vector2 Pivot(int texture, SpriteFrame frame)
    {
        var (_, _, w, h) = RectOf(frame, _textures[texture]);
        if (frame.Pivot.Length < 2) return new Vector2(0.5f, 1f);   // default: bottom centre (the feet)
        return new Vector2(frame.Pivot[0] / (float)w, frame.Pivot[1] / (float)h);
    }

    private static (int X, int Y, int W, int H) RectOf(SpriteFrame frame, Texture2D texture) =>
        frame.Rect.Length >= 4 ? (frame.Rect[0], frame.Rect[1], frame.Rect[2], frame.Rect[3]) : (0, 0, texture.Width, texture.Height);

    // ---- Drawing ----

    internal void Draw(RenderSnapshot s)
    {
        _frame++;
        ref readonly var env = ref s.Environment;
        _device.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, new Color(env.ClearColor), 1f, 0);   // pass 3, sky: a clear colour in v1
        var stats = new RenderStats
        {
            Items = s.Items.Count, Sprites = s.Sprites.Count, Culled = s.Culled, Lights = s.Lights.Count,
        };
        if (!s.HasView) { LastFrame = stats; return; }

        int items = s.Items.Count, sprites = s.Sprites.Count;
        Grow(items, ref s.SortKeys, ref s.Order);
        Grow(sprites, ref s.SpriteKeys, ref s.SpriteOrder);
        for (int i = 0; i < items; i++) { s.SortKeys[i] = s.Items[i].SortKey; s.Order[i] = i; }
        for (int i = 0; i < sprites; i++) { s.SpriteKeys[i] = s.Sprites[i].SortKey; s.SpriteOrder[i] = i; }
        Array.Sort(s.SortKeys, s.Order, 0, items);       // TODO (06 §3.5): radix sort when counts grow
        Array.Sort(s.SpriteKeys, s.SpriteOrder, 0, sprites);
        _sprites.FaceCameraPosition = _spriteFaceCamera.Value;
        _sprites.Begin(s.View);

        // Passes in order (06 §3.4). Both lists are sorted by a key whose top bits are the pass, so
        // each pass is a contiguous run in each list; meshes are drawn before sprites within a pass.
        bool wire = _wireframe.Value;
        _current = -1;
        int item = 0, sprite = 0;
        foreach (var pass in Passes)
        {
            int itemEnd = RunEnd(s.SortKeys, item, items, pass);
            int spriteEnd = RunEnd(s.SpriteKeys, sprite, sprites, pass);
            DrawItems(s, item, itemEnd, wire, ref stats);
            DrawSprites(s, sprite, spriteEnd, wire, ref stats);
            item = itemEnd;
            sprite = spriteEnd;
        }

        // Pass 5 (06 §3.4): debug geometry, over everything the world drew and under the UI.
        if (s.DebugLines.Count >= 2 && _debugLines.Ready(_content))
        {
            stats.DrawCalls += _debugLines.Draw(s.DebugLines, s.View.ViewProj, _debugThroughWalls.Value);
            stats.DebugLines = s.DebugLines.Count / 2;
            _current = -1;   // the debug effect changed the device state out from under the material cache
        }
        LastFrame = stats;
    }

    private static readonly RenderPass[] Passes = { RenderPass.Opaque, RenderPass.AlphaTested, RenderPass.Transparent };
    private int _current;   // the material currently applied to the device

    // The pooled sort arrays grow with the scene and are never shrunk (06 §3.2).
    private static void Grow(int count, ref ulong[] keys, ref int[] order)
    {
        if (keys.Length >= count) return;
        keys = new ulong[Math.Max(count, keys.Length * 2)];
        order = new int[keys.Length];
    }

    // The end of the run of entries belonging to `pass`, starting at `from` in a sorted key array.
    private static int RunEnd(ulong[] keys, int from, int count, RenderPass pass)
    {
        ulong bits = pass switch { RenderPass.Opaque => 0UL, RenderPass.AlphaTested => 1UL, _ => 3UL };
        int end = from;
        while (end < count && (keys[end] >> 60) == bits) end++;
        return end;
    }

    private MaterialRuntime? Use(int material, in RenderView view, in EnvironmentParams env, bool wire, ref RenderStats stats)
    {
        var m = Materials.Get(material);
        if (m == null) return null;
        if (m.Effect.FrameStamp != _frame)
        {
            m.Effect.SetFrame(view, env);
            m.Effect.FrameStamp = _frame;
        }
        if (material != _current)
        {
            MaterialCache.Apply(_device, m, wire);
            _current = material;
            stats.MaterialSwitches++;
        }
        if (m.DrawnFrame != _frame) { m.DrawnFrame = _frame; m.Drawn = 0; }
        return m;
    }

    private void DrawItems(RenderSnapshot s, int from, int to, bool wire, ref RenderStats stats)
    {
        for (int k = from; k < to; k++)
        {
            ref var item = ref s.Items[s.Order[k]];
            var m = Use(item.Material, s.View, s.Environment, wire, ref stats);
            if (m == null) continue;
            m.Drawn++;

            m.Effect.World?.SetValue(item.World);
            m.Effect.Tint?.SetValue(item.Tint);

            // Which lamps light this one (06 §3.9). The item's world matrix is camera-relative, so its
            // translation is where it is relative to the camera — the same frame the lights are in.
            var at = new System.Numerics.Vector3(item.World.M41, item.World.M42, item.World.M43);
            int lit = LightRules.Nearest(s.Lights.AsSpan(), at, _lights);
            m.Effect.SetLights(_lights.AsSpan(0, lit));
            if (lit > stats.MaxLightsOnADraw) stats.MaxLightsOnADraw = lit;
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
    }

    // Sprites in runs of the same material and texture: one draw per run (06 §3.7). The sheet's
    // texture overrides the material's Albedo, so one sprite material serves every sheet.
    private void DrawSprites(RenderSnapshot s, int from, int to, bool wire, ref RenderStats stats)
    {
        int run = from;
        while (run < to)
        {
            ref var first = ref s.Sprites[s.SpriteOrder[run]];
            int material = first.Material, texture = first.Texture;
            int end = run + 1;
            while (end < to)
            {
                ref var next = ref s.Sprites[s.SpriteOrder[end]];
                if (next.Material != material || next.Texture != texture) break;
                end++;
            }

            var m = Use(material, s.View, s.Environment, wire, ref stats);
            if (m != null)
            {
                m.Drawn += end - run;
                m.Effect.World?.SetValue(Matrix.Identity);   // sprite vertices are already in camera-relative space
                m.Effect.Tint?.SetValue(Vector4.One);
                foreach (var pass in m.Technique.Passes)
                {
                    int draws = _sprites.Draw(s.Sprites, s.SpriteOrder, run, end - run, _textures[texture], pass, m.Albedo);
                    stats.DrawCalls += draws;
                    stats.Triangles += (end - run) * 2;
                }
            }
            run = end;
        }
    }

    // A 1 m cube (06 §8: the error mesh is drawn, never skipped silently).
    private static MeshData CreateErrorMesh(GraphicsDevice device)
    {
        var vertices = new List<VertexPositionNormalTexture>();
        var indices = new List<int>();
        BuildBox(Vector3.One, vertices, indices);
        var shortIndices = new short[indices.Count];
        for (int i = 0; i < indices.Count; i++) shortIndices[i] = (short)indices[i];

        var vb = new VertexBuffer(device, VertexPositionNormalTexture.VertexDeclaration, vertices.Count, BufferUsage.WriteOnly);
        vb.SetData(vertices.ToArray());
        var ib = new IndexBuffer(device, IndexElementSize.SixteenBits, shortIndices.Length, BufferUsage.WriteOnly);
        ib.SetData(shortIndices);
        return new MeshData
        {
            Name = "(error)",
            IsError = true,
            Owned = true,
            Parts = new[] { new MeshPart { VertexBuffer = vb, IndexBuffer = ib, PrimitiveCount = indices.Count / 3, Bounds = new BoundingSphere(Vector3.Zero, 0.87f) } },
        };
    }

    public void Dispose()
    {
        // Buffers the renderer made (the error mesh, terrain chunks). Model buffers, effects and
        // textures belong to the ContentService.
        var disposed = new HashSet<VertexBuffer>();
        foreach (var mesh in _meshes)
        {
            if (!mesh.Owned) continue;
            foreach (var part in mesh.Parts)
            {
                if (!disposed.Add(part.VertexBuffer)) continue;   // DestroyMesh leaves aliased slots
                part.VertexBuffer.Dispose();
                part.IndexBuffer.Dispose();
            }
        }
        _sprites.Dispose();
        _debugLines.Dispose();
        Materials.Dispose();
    }
}

// The renderer's cvars, registered in ClientModule.Init. The renderer itself needs the GPU, so it is
// built in Start — after config.cfg has been read — and registering these in its constructor meant a
// `r_fog 0` in config.cfg was dropped as an unknown cvar (the #58 trap; found when registration was
// sealed, issue #12).
internal sealed class RendererCVars
{
    public readonly CVar<bool> Fog;
    public readonly CVar<bool> Wireframe;
    public readonly CVar<bool> SpriteFaceCamera;
    public readonly CVar<bool> FreezeCull;
    public readonly CVar<bool> DebugThroughWalls;

    public RendererCVars(CVarRegistry cvars)
    {
        Fog = cvars.Register("r_fog", true, CVarFlags.None, "Distance fog (the environment's fog settings).");
        Wireframe = cvars.Register("r_wireframe", false, CVarFlags.DevOnly | CVarFlags.Cheat, "Draw the scene as wireframe.");
        SpriteFaceCamera = cvars.Register("r_sprite_facecamera", false, CVarFlags.DevOnly,
            "Turn billboards toward the camera's position instead of the view plane (06 §3.8; the classic look is off).");
        FreezeCull = cvars.Register("r_freezecull", false, CVarFlags.DevOnly | CVarFlags.Cheat, "Keep the current culling frustum while the camera moves.");
        DebugThroughWalls = cvars.Register("r_debugdraw_xray", false, CVarFlags.DevOnly,
            "Draw debug geometry through walls (06 §3.2): what the AI is chasing is usually behind something).");
    }
}
