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

    // Views drawn this frame, and how many of them into render targets (issue #77).
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0123")] public int Views;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0123")] public int TargetViews;

    // Items drawn skinned this frame, and the joints written for them (issue #117): one palette per
    // skinned renderer, shared by every view that sees it.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0126", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")] public int Skinned;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0126", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")] public int Bones;

    // Items in the sun's caster view this frame (issue 4h-4), drawn into its shadow map: 0 with `r_shadows 0` or at night.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")] public int ShadowCasters;

    // Of `Culled`, the draws fog hid wholly this frame (issue 4h-5, FogMath.CullDistance), and the views the
    // sky pass drew a sky behind.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")] public int FogCulled;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")] public int Skies;
}

// A drawable piece of a mesh: one ModelMeshPart with its bone transform baked in (06 §4).
internal sealed class MeshPart
{
    public required VertexBuffer VertexBuffer;
    public required IndexBuffer IndexBuffer;
    public int VertexOffset, StartIndex, PrimitiveCount;
    public Matrix Bone = Matrix.Identity;      // mesh space → model space
    public BoundingSphere Bounds;              // model space
    public bool Skinned;                       // VertexSkinned, in the skin's bind space (issue #117)
}

internal sealed class MeshData
{
    public required string Name;
    public required MeshPart[] Parts;
    public bool IsError;
    public bool Owned;   // buffers the renderer created (error mesh, terrain chunks): it disposes them
    public GltfLoader.Skin? Skin;              // the file's skin, if any of its parts is skinned
}

// The client renderer (docs/design/06): owns GPU-side meshes by id, the material cache and the named
// render targets, and draws a RenderSnapshot through the registered render passes (issue 4h-1, REDESIGN
// §4.7; `RenderPasses`, ordered headless by `RenderPassRegistry`): the Shadow stage once; then a view at
// a time — views into render targets first, then the screen's — the target cleared to the sky, then
// the Opaque, AlphaTested, Sky, Transparent and Debug stages, the first four each over its run of the
// view's items and sprites in sort-key order (06 §3.4–3.5); then PostProcess on the screen. The Overlay
// stage (the game's UI as `sage:ui`, and whatever is added around it; ImGui after that) is drawn in the
// Overlay phase (`DrawOverlay`). Provided to modules by ClientModule (ctx.Get<Renderer>()).
//
// **One world draws to the screen** (`ScreenWorld`): with several worlds, each used to clear the back
// buffer and the last one won. Now the others draw only their views into render targets.
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
    private readonly SkyDome _sky;
    private readonly CVar<bool> _fog;
    private readonly CVar<bool> _spriteFaceCamera;
    private readonly CVar<bool> _wireframe;
    private readonly CVar<bool> _freezeCull;
    private readonly CVar<bool> _debugThroughWalls;
    private readonly Engine _engine;
    private readonly RenderTargetPool _targets;
    private World? _screenWorld;
    private readonly RenderPasses _passes;
    private readonly bool[][] _external;   // per stage, beside RenderPasses.In: a pass from outside this assembly
    private readonly RenderContext _context;
    private RenderStats _stats;            // this Draw's, added to by the passes
    private bool _wire;
    private bool _passBound;               // a pass bound `_bound` (RenderContext.SetTarget): a view must still clear it
    private SpriteBatch? _fullScreen;      // RenderContext.DrawFullScreen, made on first use
    private long _frame;
    private long _viewStamp;   // one per view drawn: an effect's frame-tier parameters are per view

    internal Renderer(ClientHost host, ContentService content, Engine engine, RendererCVars settings, RenderPasses passes)
    {
        if (!passes.IsSealed) throw new InvalidOperationException("The render passes are ordered when they are sealed; seal them before the renderer is made.");
        _passes = passes;
        _external = new bool[RenderStages.Count][];
        for (int stage = 0; stage < RenderStages.Count; stage++)
        {
            var inStage = passes.In((RenderStage)stage);
            _external[stage] = new bool[inStage.Length];
            for (int i = 0; i < inStage.Length; i++) _external[stage][i] = inStage[i].GetType().Assembly != typeof(Renderer).Assembly;
        }
        _context = new RenderContext(this);
        _device = host.GraphicsDevice;
        _content = content;
        _engine = engine;
        _targets = new RenderTargetPool(_device);
        Materials = new MaterialCache(_device, content, engine.Records, _targets);
        _targets.Changed += Materials.Invalidate;   // a remade target: materials sampling it rebuild
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
        _sky = new SkyDome(_device);

        var cvars = engine.CVars;
        _fog = settings.Fog;
        _wireframe = settings.Wireframe;
        _spriteFaceCamera = settings.SpriteFaceCamera;
        _freezeCull = settings.FreezeCull;
        _debugThroughWalls = settings.DebugThroughWalls;
        cvars.RegisterCommand("r_stats", CVarFlags.None, "Print last frame's render stats.", _ =>
            Log.Info(LogCat.Console, $"  views {LastFrame.Views} ({LastFrame.TargetViews} into render targets, {_targets.Count} target(s)), items {LastFrame.Items}, sprites {LastFrame.Sprites}, debug lines {LastFrame.DebugLines}, culled {LastFrame.Culled}, draw calls {LastFrame.DrawCalls}, " +
                                     $"triangles {LastFrame.Triangles}, material switches {LastFrame.MaterialSwitches}, " +
                                     $"lights {LastFrame.Lights} (max {LastFrame.MaxLightsOnADraw} on a draw), skinned {LastFrame.Skinned} ({LastFrame.Bones} bones), " +
                                     $"shadow casters {LastFrame.ShadowCasters}{(settings.Shadows.Value ? "" : " (r_shadows 0)")}, " +
                                     $"fog culled {LastFrame.FogCulled}, skies {LastFrame.Skies}; " +
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

    // ---- Screen and render targets (issue #77) ----

    // The world whose views draw to the screen, and whose UI does. Unset (or gone), it is the first
    // world with a client — the host's main world. An editor sets it to the world a viewport shows;
    // every other world still draws its views into render targets.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0123")]
    public World? ScreenWorld
    {
        get => ResolveScreenWorld();
        set => _screenWorld = value;
    }

    internal bool IsScreenWorld(World world) => ReferenceEquals(ResolveScreenWorld(), world);

    private World? ResolveScreenWorld()
    {
        var worlds = _engine.Worlds;
        if (_screenWorld != null)
            for (int i = 0; i < worlds.Count; i++)
                if (ReferenceEquals(worlds[i], _screenWorld)) return _screenWorld;
        for (int i = 0; i < worlds.Count; i++)
            if (worlds[i].Resources.TryGet<RenderSnapshot>(out _)) return worlds[i];
        return null;
    }

    // A named render target of a fixed size (decision D3): a camera draws into it by naming it as its
    // target, a material samples it as `rt:<name>`, and code (the editor's viewport, a HUD) draws the
    // texture this returns. Declaring it again at another size remakes it. A target content names and
    // no code declares is made at 512x512 the first time it is used.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0123")]
    public RenderTarget2D DeclareTarget(string name, int width, int height) => _targets.Texture(_targets.Declare(name, width, height));

    // The same, with the target's pixel format and depth buffer (issue 4h-1): a post-processing target
    // wants no depth, a shadow map a float format. Declaring another format or depth remakes it; the
    // three-argument form declares Color with Depth24.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public RenderTarget2D DeclareTarget(string name, int width, int height, SurfaceFormat format, DepthFormat depth) =>
        _targets.Texture(_targets.Declare(name, width, height, format, depth));

    // The target's texture, or null if nothing has declared or drawn it yet.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0123")]
    public RenderTarget2D? FindTarget(string name) => _targets.TryFind(name, out int id) ? _targets.Existing(id) : null;

    // Frees the target's texture. Anything that names it again gets a new one at the default size.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0123")]
    public bool ReleaseTarget(string name) => _targets.Release(name);

    internal int DeclareTargetId(string name, int width, int height) => _targets.Declare(name, width, height);
    internal int DeclareTargetId(string name, int width, int height, SurfaceFormat format, DepthFormat depth) => _targets.Declare(name, width, height, format, depth);
    internal int TargetId(string name) => _targets.Id(name);
    internal string TargetName(int target) => _targets.Name(target);

    // The pixel size of a view's target: the back buffer's for the screen.
    internal Point TargetSize(int target) => target == RenderViewPlan.Screen
        ? new Point(_device.PresentationParameters.BackBufferWidth, _device.PresentationParameters.BackBufferHeight)
        : _targets.Size(target);

    internal RenderTarget2D TargetTexture(int target) => _targets.Texture(target);
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
            using (stream) id = LoadMesh(path.ToString(), stream);

                _meshIds[path] = id;
        return id;
    }

    internal MeshData Mesh(int id) => _meshes[id];

    // A .glb that is not on a mount (the generated `r_testskin` model), under a name nothing else uses:
    // afterwards `path` resolves to it like any other mesh.
    internal int RegisterMesh(AssetPath path, System.IO.Stream stream)
    {
        if (_meshIds.TryGetValue(path, out int id) && id != 0) return id;
        return _meshIds[path] = LoadMesh(path.ToString(), stream);
    }

    // A .glb → buffers, one part per primitive: rigid parts as VertexPositionNormalTexture with the
    // file's hierarchy baked in, skinned ones as VertexSkinned in bind space (issue #117). 0 (the
    // error mesh) when it cannot be read.
    private int LoadMesh(string name, System.IO.Stream stream)
    {
        var mesh = BuildMesh(name, stream);
        if (mesh == null)
        {
            Log.Warn(LogCat.Render, $"Mesh '{name}' could not be read; drawing the error mesh");
            return 0;
        }
        _meshes.Add(mesh);
        return _meshes.Count - 1;
    }

    // Hot reload of a `.glb` (issue 4h-3): the file is read again and the mesh replaces the old one *in
    // its slot*, so every id already handed out — in materials, components, this frame's snapshot —
    // draws the new geometry. A file that cannot be read keeps the old mesh. Returns false when the
    // path was never loaded, and a path that had fallen back to the error mesh gets a real one now.
    // Runs in FrameUpdate, before Extract and Render, so no snapshot holds an index into the old parts.
    internal bool ReloadMesh(AssetPath path)
    {
        if (!_meshIds.TryGetValue(path, out int id)) return false;

        using var stream = _content.Open(path);
        var fresh = stream == null ? null : BuildMesh(path.ToString(), stream);
        if (fresh == null)
        {
            Log.Warn(LogCat.Render, $"Mesh '{path}' did not reload; keeping the copy already loaded");
            return false;
        }

        if (id == 0)
        {
            _meshes.Add(fresh);
            _meshIds[path] = _meshes.Count - 1;
            return true;
        }

        var old = _meshes[id];
        _meshes[id] = fresh;
        if (old.Owned)
            foreach (var part in old.Parts)
            {
                part.VertexBuffer.Dispose();
                part.IndexBuffer.Dispose();
            }
        return true;
    }

    // The mesh for a `.glb` stream, not yet in the table; null when it cannot be read.
    private MeshData? BuildMesh(string name, System.IO.Stream stream)
    {
        if (!GltfLoader.TryLoad(stream, name, out var model)) return null;

        var parts = new List<MeshPart>(model.Parts.Count);
        bool skinned = false;
        foreach (var loaded in model.Parts)
        {
            VertexBuffer vb;
            if (loaded.Skinned != null)
            {
                vb = new VertexBuffer(_device, VertexSkinned.VertexDeclaration, loaded.Skinned.Length, BufferUsage.WriteOnly);
                vb.SetData(loaded.Skinned);
                skinned = true;
            }
            else
            {
                vb = new VertexBuffer(_device, VertexPositionNormalTexture.VertexDeclaration, loaded.Rigid!.Length, BufferUsage.WriteOnly);
                vb.SetData(loaded.Rigid);
            }
            var ib = new IndexBuffer(_device, IndexElementSize.ThirtyTwoBits, loaded.Indices.Length, BufferUsage.WriteOnly);
            ib.SetData(loaded.Indices);

            parts.Add(new MeshPart
            {
                VertexBuffer = vb,
                IndexBuffer = ib,
                VertexOffset = 0,
                StartIndex = 0,
                PrimitiveCount = loaded.Indices.Length / 3,
                Bone = Matrix.Identity,      // the file's hierarchy is already baked in (rigid) or is the skin's (skinned)
                Bounds = model.Bounds,
                Skinned = loaded.Skinned != null,
            });
        }

        // Owned: these buffers are the renderer's, so they are disposed with it. An `.xnb` model
        // belonged to the ContentManager, which is the thing that has gone away.
        return new MeshData { Name = name, Parts = parts.ToArray(), Owned = true, Skin = skinned ? model.Skin : null };
    }

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

    internal void Draw(RenderSnapshot s, bool screen, World world)
    {
        _frame++;
        _stats = new RenderStats
        {
            Items = s.Items.Count, Sprites = s.Sprites.Count, Culled = s.Culled, FogCulled = s.FogCulled, Lights = s.Lights.Count,
            Bones = s.Bones.Count,
        };
        Plan(s);
        _sprites.FaceCameraPosition = _spriteFaceCamera.Value;
        _wire = _wireframe.Value;
        var clear = new Color(s.Environment.ClearColor);   // the horizon's colour; `sage:sky` draws over it when the world has a sky (4h-5)
        var ctx = Begin(world, s, screen, extracting: false);
        _shadow.Drawn = false;   // until `sage:shadow` draws this world's map
        _shadowMap = null;

        // Shadow: once, before any view (`sage:shadow`, issue 4h-4). A pass there binds its own target.
        _bound = NotBound;
        _passBound = false;
        RunFrameStage(ctx, RenderStage.Shadow);

        // Views in plan order: every render target's first, then the screen's (RenderViewPlan.OrderViews).
        for (int k = 0; k < s.Views.Count; k++)
        {
            int index = s.DrawOrder[k];
            ref var view = ref s.Views[index];
            if (view.Target == RenderViewPlan.Screen && !screen) continue;   // CameraExtract already drops these
            if (view.ShadowCaster) continue;                                  // drawn by `sage:shadow`, in the Shadow stage
            if (view.Target != _bound || _passBound) Bind(view.Target, clear);
            DrawView(ctx, s, index, clear);
            _stats.Views++;
            if (view.Target != RenderViewPlan.Screen) _stats.TargetViews++;
        }

        // Back to the screen, and the screen's viewport, for the UI and the dev tools. The screen world
        // clears it even with no view to draw (a world without a camera still shows its sky).
        if (_bound != RenderViewPlan.Screen || _passBound)
        {
            if (screen) Bind(RenderViewPlan.Screen, clear);
            else if (_bound != NotBound) _device.SetRenderTarget(null);
        }
        var back = TargetSize(RenderViewPlan.Screen);
        _device.Viewport = new Viewport(0, 0, back.X, back.Y);

        // PostProcess: once, on the screen, over every view (4h-6's chain).
        if (screen && _passes.In(RenderStage.PostProcess).Length > 0)
        {
            RunFrameStage(ctx, RenderStage.PostProcess);
            if (_bound != RenderViewPlan.Screen || _passBound) Rebind(RenderViewPlan.Screen);
            _device.Viewport = new Viewport(0, 0, back.X, back.Y);
        }
        if (screen) LastFrame = _stats;
    }

    // The Overlay stage, in the Overlay phase (UiRenderSystem): the screen world's UI (`sage:ui`) and
    // whatever a game draws over or under it, on the screen at full size.
    internal void DrawOverlay(World world, RenderSnapshot s)
    {
        var ctx = Begin(world, s, screen: true, extracting: false);
        _passBound = false;
        RunFrameStage(ctx, RenderStage.Overlay);
    }

    // Every pass's Extract, in draw order (RenderPassExtract, after the cameras).
    internal void ExtractPasses(World world, RenderSnapshot s, bool screen)
    {
        var ctx = Begin(world, s, screen, extracting: true);
        for (int stage = 0; stage < RenderStages.Count; stage++)
        {
            var passes = _passes.In((RenderStage)stage);
            ctx.Stage = (RenderStage)stage;
            for (int i = 0; i < passes.Length; i++) passes[i].Extract(ctx);
        }
    }

    private RenderContext Begin(World world, RenderSnapshot s, bool screen, bool extracting)
    {
        var ctx = _context;
        ctx.World = world;
        ctx.Snapshot = s;
        ctx.IsScreen = screen;
        ctx.IsExtracting = extracting;
        ctx.View = -1;
        ctx.ItemFrom = ctx.ItemTo = ctx.SpriteFrom = ctx.SpriteTo = 0;
        return ctx;
    }

    // A once-a-frame stage's passes. After a pass from outside the engine the material state is not
    // trusted; in Overlay, a target it bound is left for the screen again.
    private void RunFrameStage(RenderContext ctx, RenderStage stage)
    {
        var passes = _passes.In(stage);
        if (passes.Length == 0) return;
        var external = _external[(int)stage];
        ctx.Stage = stage;
        ctx.View = -1;
        for (int i = 0; i < passes.Length; i++)
        {
            passes[i].Draw(ctx);
            if (!external[i]) continue;
            _current = -1;
            if (stage == RenderStage.Overlay && _passBound) Rebind(RenderViewPlan.Screen);
        }
    }

    private const int NotBound = int.MinValue;
    private int _bound = NotBound;   // the target the device is drawing into during Draw

    // The render state that confines a clear to a view's rectangle (a split screen's half).
    private static readonly RasterizerState ScissorClear = new() { ScissorTestEnable = true, CullMode = CullMode.None };

    // Draw into `target` from now on: all of it cleared to the sky, depth included.
    private void Bind(int target, Color clear)
    {
        Rebind(target);
        _device.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, clear, 1f, 0);
    }

    // Draw into `target` from now on, over what it holds, all of it.
    private void Rebind(int target)
    {
        _device.SetRenderTarget(target == RenderViewPlan.Screen ? null : _targets.Texture(target));
        var size = TargetSize(target);
        _device.Viewport = new Viewport(0, 0, size.X, size.Y);
        _bound = target;
        _passBound = false;
        _current = -1;
    }

    // RenderContext.SetTarget: a pass draws into a target of its own (all of it, not cleared).
    internal void SetTarget(int target)
    {
        Rebind(target);
        _passBound = true;
    }

    // RenderContext.DrawFullScreen: the texture over the current viewport, through a sprite batch.
    internal void DrawFullScreen(Texture2D texture, Effect? effect, BlendState? blend)
    {
        _fullScreen ??= new SpriteBatch(_device);
        var viewport = _device.Viewport;
        _fullScreen.Begin(SpriteSortMode.Immediate, blend ?? BlendState.Opaque, SamplerState.LinearClamp,
                          DepthStencilState.None, RasterizerState.CullNone, effect);
        _fullScreen.Draw(texture, new Rectangle(0, 0, viewport.Width, viewport.Height), Color.White);
        _fullScreen.End();
        _current = -1;   // the batch set its own device state
        _stats.DrawCalls++;
        _stats.Triangles += 2;
    }

    // Plans the frame (RenderViewPlan): the views' draw order, and each view's run of items and sprites
    // sorted by key. Copies the keys and views out of the lists into pooled arrays first.
    private static void Plan(RenderSnapshot s)
    {
        int items = s.Items.Count, sprites = s.Sprites.Count, views = s.Views.Count;
        Grow(items, ref s.ItemKeys, ref s.ItemViews);
        Grow(items, ref s.SortKeys, ref s.Order);
        Grow(sprites, ref s.SpriteInKeys, ref s.SpriteInViews);
        Grow(sprites, ref s.SpriteKeys, ref s.SpriteOrder);
        if (s.DrawOrder.Length < views)
        {
            int n = Math.Max(views, s.DrawOrder.Length * 2);
            s.ViewTargets = new int[n];
            s.ViewOrders = new int[n];
            s.DrawOrder = new int[n];
            s.RangeStarts = new int[n];
            s.RangeCounts = new int[n];
        }

        for (int i = 0; i < items; i++) { ref var item = ref s.Items[i]; s.ItemKeys[i] = item.SortKey; s.ItemViews[i] = item.View; }
        for (int i = 0; i < sprites; i++) { ref var sprite = ref s.Sprites[i]; s.SpriteInKeys[i] = sprite.SortKey; s.SpriteInViews[i] = sprite.View; }
        for (int v = 0; v < views; v++) { s.ViewTargets[v] = s.Views[v].Target; s.ViewOrders[v] = s.Views[v].Order; }

        RenderViewPlan.OrderViews(s.ViewTargets.AsSpan(0, views), s.ViewOrders.AsSpan(0, views), s.DrawOrder.AsSpan(0, views));

        var starts = s.RangeStarts.AsSpan(0, views);
        var counts = s.RangeCounts.AsSpan(0, views);
        RenderViewPlan.Bucket(s.ItemViews.AsSpan(0, items), s.ItemKeys.AsSpan(0, items), starts, counts, s.SortKeys, s.Order);   // TODO (06 §3.5): radix sort when counts grow
        for (int v = 0; v < views; v++) { s.Views[v].ItemStart = starts[v]; s.Views[v].ItemCount = counts[v]; }
        RenderViewPlan.Bucket(s.SpriteInViews.AsSpan(0, sprites), s.SpriteInKeys.AsSpan(0, sprites), starts, counts, s.SpriteKeys, s.SpriteOrder);
        for (int v = 0; v < views; v++) { s.Views[v].SpriteStart = starts[v]; s.Views[v].SpriteCount = counts[v]; }
    }

    private void DrawView(RenderContext ctx, RenderSnapshot s, int index, Color clear)
    {
        ref var view = ref s.Views[index];
        _viewStamp++;
        _device.Viewport = new Viewport(view.Viewport);
        if (view.DepthOnly)
        {
            // Over what the target already holds (the viewmodel pass, issue #121): only depth is
            // cleared, so what this view draws is in front of everything drawn before it.
            if (!view.FullTarget)
            {
                _device.ScissorRectangle = view.Viewport;
                _device.RasterizerState = ScissorClear;
            }
            _device.Clear(ClearOptions.DepthBuffer, clear, 1f, 0);
            _device.RasterizerState = RasterizerState.CullCounterClockwise;
            _current = -1;
        }
        else if (!view.FullTarget)
        {
            // A part of the target: clear only that part (Bind cleared the whole target already
            // when this view is all of it).
            _device.ScissorRectangle = view.Viewport;
            _device.RasterizerState = ScissorClear;
            _device.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, clear, 1f, 0);
            _device.RasterizerState = RasterizerState.CullCounterClockwise;
            _current = -1;
        }
        _sprites.Begin(view);

        // The per-view stages in order (06 §3.4). Both runs are sorted by a key whose top bits are the
        // material's pass (RenderStages.SortKeyPass), so each scene stage's entries are a contiguous run
        // in each, which the stage's passes draw (the engine's: meshes, then sprites).
        int item = view.ItemStart, items = view.ItemStart + view.ItemCount;
        int sprite = view.SpriteStart, sprites = view.SpriteStart + view.SpriteCount;
        ctx.View = index;
        for (int stage = (int)RenderStage.Opaque; stage <= (int)RenderStage.Debug; stage++)
        {
            int bits = RenderStages.SortKeyPass((RenderStage)stage);
            int itemEnd = bits < 0 ? item : RunEnd(s.SortKeys, item, items, (ulong)bits);
            int spriteEnd = bits < 0 ? sprite : RunEnd(s.SpriteKeys, sprite, sprites, (ulong)bits);
            var passes = _passes.In((RenderStage)stage);
            if (passes.Length > 0)
            {
                var external = _external[stage];
                ctx.Stage = (RenderStage)stage;
                ctx.ItemFrom = item;
                ctx.ItemTo = itemEnd;
                ctx.SpriteFrom = sprite;
                ctx.SpriteTo = spriteEnd;
                for (int i = 0; i < passes.Length; i++)
                {
                    passes[i].Draw(ctx);
                    if (!external[i]) continue;
                    // A game's pass may have changed anything: the view's target, viewport and material
                    // state are put back before the next pass draws.
                    _current = -1;
                    if (_bound != view.Target || _passBound) Rebind(view.Target);
                    _device.Viewport = new Viewport(view.Viewport);
                }
            }
            item = itemEnd;
            sprite = spriteEnd;
        }
        ctx.View = -1;
    }

    // The engine's scene passes (`sage:opaque`, `sage:alpha_tested`, `sage:transparent`): the stage's run
    // of the view's items, then its sprites.
    internal void DrawSceneRun(RenderContext ctx)
    {
        var s = ctx.Snapshot;
        ref var view = ref s.Views[ctx.View];
        var lights = s.Lights.AsSpan().Slice(view.LightStart, view.LightCount);
        DrawItems(s, view, lights, ctx.ItemFrom, ctx.ItemTo, _wire, ref _stats);
        DrawSprites(s, view, ctx.SpriteFrom, ctx.SpriteTo, _wire, ref _stats);
    }

    // `sage:debug` (06 §3.4, pass 5): debug geometry, over everything the world drew and under the UI.
    internal void DrawDebugLines(RenderContext ctx)
    {
        var s = ctx.Snapshot;
        ref var view = ref s.Views[ctx.View];
        if (view.DebugCount >= 2 && _debugLines.Ready(_content))
        {
            _stats.DrawCalls += _debugLines.Draw(s.DebugLines.AsSpan().Slice(view.DebugStart, view.DebugCount), view.ViewProj, _debugThroughWalls.Value);
            _stats.DebugLines += view.DebugCount / 2;
            _current = -1;   // the debug effect changed the device state out from under the material cache
        }
    }

    private int _current;   // the material currently applied to the device

    // `sage:sky` (issue 4h-5): the sky behind what the view's opaque and alpha-tested runs drew, while the
    // environment says to draw one. Not in a depth-only view: the viewmodel's is drawn over a finished
    // picture with its depth cleared, where a sky would cover everything.
    internal void DrawSky(RenderContext ctx)
    {
        var s = ctx.Snapshot;
        if (!s.Environment.DrawSky) return;
        ref var view = ref s.Views[ctx.View];
        if (view.DepthOnly || view.ShadowCaster) return;
        if (!_sky.Ready(_content)) return;
        int draws = _sky.Draw(view, s.Environment);
        _current = -1;   // the sky set its own device state
        _stats.DrawCalls += draws;
        _stats.Triangles += draws;
        _stats.Skies++;
    }

    // ---- Sun shadows (issue 4h-4) ----

    // This frame's shadow map, for the lit shaders (EffectBinding.SetFrame): set by DrawShadowCasters,
    // cleared at the start of each Draw so a world without one reads strength 0.
    private ShadowFrame _shadow = new() { View = -1 };
    private Texture2D? _shadowMap;

    // `sage:shadow`'s Draw: the caster view's items into the map, with lit.fx's ShadowCaster (or
    // ShadowCasterSkinned) whatever each item's material is: a caster only needs its depth. The map holds
    // light-space depth in [0, 1], cleared to 1 (nothing in the way).
    internal void DrawShadowCasters(RenderContext ctx)
    {
        var s = ctx.Snapshot;
        if (s.Shadow.View < 0) return;
        ref var view = ref s.Views[s.Shadow.View];
        _stats.ShadowCasters += view.ItemCount;   // what the caster view holds, drawn or not (r_stats)

        // A default material that could not be built (no shaders: Linux CI) is already reported, under Shaders.
        var material = Materials.Get(Materials.Resolve(MaterialRecord.Default));
        if (material == null || material.IsError) return;
        var caster = material.Effect;
        if (caster.ShadowCaster == null)
        {
            Log.Once(LogCat.Shaders, LogLevel.Warn, "no-shadow-caster", $"{MaterialRecord.Default}'s effect has no ShadowCaster technique; no sun shadows");
            return;
        }

        SetTarget(s.Shadow.Target);
        _device.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Vector4.One, 1f, 0);
        _device.BlendState = BlendState.Opaque;
        _device.DepthStencilState = DepthStencilState.Default;
        _device.RasterizerState = RasterizerState.CullNone;   // open meshes (terrain) cast from either side
        var effect = caster.Effect;
        caster.ViewProj?.SetValue(view.ViewProj);
        caster.FrameStamp = -1;   // the next view sets its own frame parameters on this effect
        _current = -1;

        for (int k = view.ItemStart; k < view.ItemStart + view.ItemCount; k++)
        {
            ref var item = ref s.Items[s.Order[k]];
            var technique = caster.ShadowCaster;
            if (item.BoneCount > 0 && caster.ShadowCasterSkinned != null && caster.Bones != null)
            {
                var palette = s.Bones.AsSpan().Slice(item.BoneStart, item.BoneCount);
                for (int i = 0; i < palette.Length; i++) _bones[i] = palette[i];   // System.Numerics → MonoGame (implicit)
                caster.Bones.SetValue(_bones);
                technique = caster.ShadowCasterSkinned;
            }
            caster.World?.SetValue(item.World);
            effect.CurrentTechnique = technique;
            var part = _meshes[item.Mesh].Parts[item.Part];
            _device.SetVertexBuffer(part.VertexBuffer);
            _device.Indices = part.IndexBuffer;
            foreach (var pass in technique.Passes)
            {
                pass.Apply();
                _device.DrawIndexedPrimitives(PrimitiveType.TriangleList, part.VertexOffset, part.StartIndex, part.PrimitiveCount);
                _stats.DrawCalls++;
                _stats.Triangles += part.PrimitiveCount;
            }
        }

        s.Shadow.Drawn = true;
        _shadow = s.Shadow;
        _shadowMap = _targets.Texture(s.Shadow.Target);
    }

    // The pooled planning arrays grow with the scene and are never shrunk (06 §3.2).
    private static void Grow(int count, ref ulong[] keys, ref int[] other)
    {
        if (keys.Length >= count) return;
        keys = new ulong[Math.Max(count, keys.Length * 2)];
        other = new int[keys.Length];
    }

    // The end of the run of entries whose key starts with `bits` (RenderStages.SortKeyPass), from `from`
    // in a sorted key array.
    private static int RunEnd(ulong[] keys, int from, int count, ulong bits)
    {
        int end = from;
        while (end < count && (keys[end] >> 60) == bits) end++;
        return end;
    }

    private MaterialRuntime? Use(int material, in RenderView view, in EnvironmentParams env, bool wire, ref RenderStats stats)
    {
        var m = Materials.Get(material);
        if (m == null) return null;
        // A material may not sample the target being drawn into: reading and writing one texture in a
        // draw is undefined (a mirror that sees its own surface). The draw is skipped, not garbage.
        if (_bound >= 0 && _bound < 64 && (m.SampledTargets & (1UL << _bound)) != 0)
        {
            Log.Once(LogCat.Render, LogLevel.Debug, "rt-feedback", $"Material {m.Id} samples render target '{_targets.Name(_bound)}' while drawing into it; skipped there");
            return null;
        }
        if (m.Effect.FrameStamp != _viewStamp)
        {
            m.Effect.SetFrame(view, env, _shadow, _shadowMap, Materials.MissingTexture);
            m.Effect.FrameStamp = _viewStamp;
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

    private void DrawItems(RenderSnapshot s, in RenderView view, ReadOnlySpan<LightSample> lights, int from, int to, bool wire, ref RenderStats stats)
    {
        for (int k = from; k < to; k++)
        {
            ref var item = ref s.Items[s.Order[k]];
            var m = Use(item.Material, view, s.Environment, wire, ref stats);
            if (m == null) continue;
            m.Drawn++;

            m.Effect.World?.SetValue(item.World);
            m.Effect.Tint?.SetValue(item.Tint);
            var technique = item.BoneCount > 0 ? Skin(s, item, m, ref stats) : m.Technique;

            // Which lamps light this one (06 §3.9). The item's world matrix is camera-relative, so its
            // translation is where it is relative to the camera — the same frame the view's lights are in.
            var at = new System.Numerics.Vector3(item.World.M41, item.World.M42, item.World.M43);
            int lit = LightRules.Nearest(lights, at, _lights);
            m.Effect.SetLights(_lights.AsSpan(0, lit));
            if (lit > stats.MaxLightsOnADraw) stats.MaxLightsOnADraw = lit;
            var part = _meshes[item.Mesh].Parts[item.Part];
            _device.SetVertexBuffer(part.VertexBuffer);
            _device.Indices = part.IndexBuffer;
            foreach (var pass in technique.Passes)
            {
                pass.Apply();
                _device.DrawIndexedPrimitives(PrimitiveType.TriangleList, part.VertexOffset, part.StartIndex, part.PrimitiveCount);
                stats.DrawCalls++;
                stats.Triangles += part.PrimitiveCount;
            }
        }
    }

    // A skinned item's palette into the effect's `Bones`, and the technique that reads it: the
    // material's effect's `Skinned` (07 §3.2). An effect without one draws the mesh in its bind pose
    // with the material's own technique, which reads the same first three vertex elements.
    private readonly Matrix[] _bones = new Matrix[SkinMath.MaxBones];

    private EffectTechnique Skin(RenderSnapshot s, in RenderItem item, MaterialRuntime m, ref RenderStats stats)
    {
        if (m.Skinned == null || m.Effect.Bones == null)
        {
            if (!m.WarnedNoSkin)   // a flag, not Log.Once: its key would be a string built per draw
            {
                m.WarnedNoSkin = true;
                Log.Warn(LogCat.Render, $"Material {m.Id}: its effect has no Skinned technique (and Bones), so skinned meshes drawn with it stay in their bind pose");
            }
            return m.Technique;
        }
        var palette = s.Bones.AsSpan().Slice(item.BoneStart, item.BoneCount);
        for (int i = 0; i < palette.Length; i++) _bones[i] = palette[i];   // System.Numerics → MonoGame (implicit)
        m.Effect.Bones.SetValue(_bones);
        stats.Skinned++;
        return m.Skinned;
    }

    // Sprites in runs of the same material and texture: one draw per run (06 §3.7). The sheet's
    // texture overrides the material's Albedo, so one sprite material serves every sheet.
    private void DrawSprites(RenderSnapshot s, in RenderView view, int from, int to, bool wire, ref RenderStats stats)
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

            var m = Use(material, view, s.Environment, wire, ref stats);
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
        _sky.Dispose();
        _fullScreen?.Dispose();
        Materials.Dispose();
        _targets.Dispose();
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
    public readonly CVar<int> TestView;
    public readonly CVar<bool> TestSkin;
    public readonly CVar<bool> Shadows;
    public readonly CVar<int> ShadowSize;
    public readonly CVar<float> ShadowDistance;

    public RendererCVars(CVarRegistry cvars)
    {
        Fog = cvars.Register("r_fog", true, CVarFlags.None, "Distance fog (the environment's fog settings).");
        Wireframe = cvars.Register("r_wireframe", false, CVarFlags.DevOnly | CVarFlags.Cheat, "Draw the scene as wireframe.");
        SpriteFaceCamera = cvars.Register("r_sprite_facecamera", false, CVarFlags.DevOnly,
            "Turn billboards toward the camera's position instead of the view plane (06 §3.8; the classic look is off).");
        FreezeCull = cvars.Register("r_freezecull", false, CVarFlags.DevOnly | CVarFlags.Cheat, "Keep the current culling frustum while the camera moves.");
        DebugThroughWalls = cvars.Register("r_debugdraw_xray", false, CVarFlags.DevOnly,
            "Draw debug geometry through walls (06 §3.2): what the AI is chasing is usually behind something).");
        TestView = cvars.Register("r_testview", 0, CVarFlags.DevOnly | CVarFlags.Cheat,
            "Extra views to see several views per frame work (06 §3.4, issue #77): 1 splits the screen with a view from behind, " +
            "2 draws a top-down map into the render target 'testview' and shows it in a corner.", 0, 2);
        TestSkin = cvars.Register("r_testskin", false, CVarFlags.DevOnly | CVarFlags.Cheat,
            "Draw a generated skinned model in front of the camera, bending (issue #117): GPU skinning with no content.");
        Shadows = cvars.Register("r_shadows", false, CVarFlags.None,
            "Sun shadows (issue 4h-4): one stable shadow map over the nearest r_shadow_distance metres of the view.");
        ShadowSize = cvars.Register("r_shadow_size", ShadowMath.DefaultSize, CVarFlags.None,
            "The sun's shadow map, in texels a side.", 256, 4096);
        ShadowDistance = cvars.Register("r_shadow_distance", ShadowMath.DefaultDistance, CVarFlags.None,
            "How far from the camera the sun's shadows reach, in metres (the map covers this slice of the view).", 5f, 500f);
    }
}
