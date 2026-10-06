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
    // Shadow maps drawn this frame (issue 4n-19): one per view that receives shadows (ShadowViews).
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")] public int ShadowMaps;

    // Of `Culled`, the draws fog hid wholly this frame (issue 4h-5, FogMath.CullDistance), and the views the
    // sky pass drew a sky behind.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")] public int FogCulled;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")] public int Skies;

    // Full-screen draws of the post-processing chain this frame (issue 4h-6): 0 while it is off.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")] public int PostSteps;

    // Mesh LOD this frame (issue 4n-1, MeshLod), one per renderer per view: those not drawn because they
    // were past their LOD group's cull point or their layer's draw distance, and those drawn coarser.
    public int LodCulled, LodLowered;

    // Instancing this frame (issue 4n-5, `r_instancing`): mesh items drawn in instanced runs and the draw
    // calls those runs took (each run of n items saves n - 1 draws; the sun's casters are counted too),
    // and sprites drawn as instanced quads (the same draw count, a quarter of the vertex data written).
    public int Instanced, InstancedDraws, InstancedSprites;

    // What the renderer holds (`stat assets`, issue #300): mesh slots (an unloaded one counts until it is
    // reused), textures and materials, the placeholders not counted.
    public int Meshes, Textures, Materials;

    // Draws this frame with a baked lightmap (issue #313, `Lightmapped`).
    public int Lightmapped;
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
    public Texture2D? Lightmap;                // VertexLightmapped, drawn with `Lightmapped` (issue #313); not owned
}

internal sealed class MeshData
{
    public required string Name;
    public required MeshPart[] Parts;
    public bool IsError;
    public bool Owned;   // buffers the renderer created (error mesh, terrain chunks): it disposes them
    public MeshSkin? Skin;              // the file's skin, if any of its parts is skinned
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
public sealed partial class Renderer : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly ContentService _content;
    // Meshes by stable id (issue #308): 0 is the error mesh, a freed slot is reused, ids never shift.
    private readonly AssetTable<MeshData> _meshes;

    // Scratch for the per-draw light selection, so a frame of a thousand items allocates nothing.
    private readonly LightSample[] _lights = new LightSample[LightRules.PerObject];
    private readonly SpriteBatcher _sprites;
    private readonly DebugLineBatch _debugLines;
    private readonly SkyDome _sky;
    private readonly CVar<bool> _fog;
    private readonly CVar<bool> _spriteFaceCamera;
    private readonly CVar<bool> _wireframe;
    private readonly CVar<int> _anisotropy;
    private readonly CVar<bool> _freezeCull;
    private readonly CVar<bool> _debugThroughWalls;
    private readonly CVar<bool> _instancing;
    private readonly CVar<int> _instancingMin;
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

    // Post-processing (issue 4h-6): while `_toScene` is set, "the screen" a view or a pass binds is
    // `sage:scene` at `_sceneSize` (the render scale's), which the PostProcess stage then draws out.
    private readonly PostChain _post;
    private bool _toScene;
    private int _sceneTarget = NotBound;
    private Point _sceneSize;

    internal Renderer(ClientHost host, ContentService content, Engine engine, RendererCVars settings, RenderPasses passes)
    {
        if (!passes.IsSealed) throw new InvalidOperationException("The render passes are ordered when they are sealed; seal them before the renderer is made.");
        VertexLayouts.Check();   // the headless model structs go to vertex buffers as they are (issue #302)
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
        _post = new PostChain(engine.Records, engine.CVars, settings, Materials);

        // An asset changed on disk (05 §3.6, F32). Textures are held by id in the content service's table,
        // which swaps the new one into the same slot; a built material holds them by reference, so every
        // material is rebuilt lazily, which also picks up a reloaded effect. An evicted texture (#308) the
        // same.
        content.Reloaded += _ => Materials.Invalidate();
        content.Evicted += _ => Materials.Invalidate();
        _meshes = new AssetTable<MeshData>(CreateErrorMesh(_device));   // id 0
        _loadMesh = LoadMesh;
        Budget.MillisecondsPerFrame = settings.UploadMs.Value;
        settings.UploadMs.Changed += _ => Budget.MillisecondsPerFrame = settings.UploadMs.Value;
        _sprites = new SpriteBatcher(_device);
        _debugLines = new DebugLineBatch(_device);
        _sky = new SkyDome(_device);

        var cvars = engine.CVars;
        _fog = settings.Fog;
        _wireframe = settings.Wireframe;
        _anisotropy = settings.Anisotropy;
        _spriteFaceCamera = settings.SpriteFaceCamera;
        _freezeCull = settings.FreezeCull;
        _debugThroughWalls = settings.DebugThroughWalls;
        _instancing = settings.InstancingOn;
        _instancingMin = settings.InstancingMin;
        _canInstance = _device.GraphicsProfile == GraphicsProfile.HiDef;
        _waterOn = settings.Water;
        cvars.RegisterCommand("r_stats", CVarFlags.None, "Print last frame's render stats.", _ =>
            Log.Info(LogCat.Console, $"  views {LastFrame.Views} ({LastFrame.TargetViews} into render targets, {_targets.Count} target(s)), items {LastFrame.Items}, sprites {LastFrame.Sprites}, debug lines {LastFrame.DebugLines}, culled {LastFrame.Culled}, draw calls {LastFrame.DrawCalls}, " +
                                     $"triangles {LastFrame.Triangles}, material switches {LastFrame.MaterialSwitches}, " +
                                     $"lights {LastFrame.Lights} (max {LastFrame.MaxLightsOnADraw} on a draw), skinned {LastFrame.Skinned} ({LastFrame.Bones} bones), lightmapped {LastFrame.Lightmapped}, " +
                                     $"shadow casters {LastFrame.ShadowCasters} in {LastFrame.ShadowMaps} map(s){(settings.Shadows.Value ? "" : " (r_shadows 0)")}, " +
                                      $"fog culled {LastFrame.FogCulled}, lod culled {LastFrame.LodCulled}, lod lowered {LastFrame.LodLowered}, skies {LastFrame.Skies}, " +
                                      InstancingStats() + "; " +
                                      $"{_meshes.Count} meshes, {_content.Textures.Count} textures, {Materials.Count} materials" + PostStats()));
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

    // r_stats' instancing (issue 4n-5): what it drew and what that saved, or why it drew nothing.
    private string InstancingStats() =>
        !_instancing.Value ? "instanced 0 (r_instancing 0)" :
        !_canInstance ? "instanced 0 (the device cannot instance)" :
        $"instanced {LastFrame.Instanced} in {LastFrame.InstancedDraws} draw(s) (saved {LastFrame.Instanced - LastFrame.InstancedDraws}), instanced sprites {LastFrame.InstancedSprites}";

    // The r_stats line's tail: the post chain, when it drew last frame (issue 4h-6).
    private string PostStats() => LastFrame.PostSteps == 0 ? "" :
        $"; post {LastFrame.PostSteps} step(s), scene {_sceneSize.X}x{_sceneSize.Y}" + PostOptionStats();

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

    // ---- Asset scopes (issue #308) ----

    // The upload budget streamed content loads within (`asset_upload_ms`), and what the sectors of every
    // world released, collected at the safe point below.
    internal UploadBudget Budget { get; } = new();
    internal AssetReleases Releases { get; } = new();
    internal AssetTable<MeshData> Meshes => _meshes;
    private long _assetFrame = -1;
    private readonly Func<AssetPath, (MeshData?, long)> _loadMesh;   // made once: resolving is per item per frame

    // The frame's safe point for assets: FrameUpdate, before any world extracts, so no snapshot holds an
    // id or a buffer freed here (AssetScopeSystem; once a frame however many worlds run it). Frees what the
    // sectors released that no world holds now, and the UI's pictures no screen has drawn for a while, and
    // starts the frame's upload budget.
    internal void BeginAssetFrame(long frame)
    {
        if (frame == _assetFrame) return;
        _assetFrame = frame;
        _content.Frame = frame;
        Budget.BeginFrame(frame);
        int freed = Releases.Collect(_engine.Worlds, Evict);
        freed += _content.EvictUnusedUi();
        if (freed > 0) Log.Debug(LogCat.Assets, $"{freed} asset(s) evicted; {_meshes.Count} meshes and {_content.Textures.Count} textures loaded");
    }

    // Frees one released asset when it is loaded at a scope a sector lets go of.
    private bool Evict(AssetKey key) => key.Type == AssetType.Mesh ? EvictMesh(key.Path) : _content.EvictTexture(key.Path);

    // The scope a world's content loads at: a streaming world's is the sectors' (released with them, and
    // within the upload budget), any other world's is the game's.
    internal static AssetScope ScopeOf(World world) =>
        world.Resources.TryGet<SectorAssets>(out var assets) && assets != null ? AssetScope.Sector : AssetScope.Game;

    // ---- Meshes ----

    // The mesh id for an asset path, loading it on first use, at the game's scope (never budgeted).
    // Missing → 0, the error mesh (06 §8).
    //
    // **Read at runtime** (R12): the file comes off a VFS mount as bytes and becomes buffers here, so a
    // model can come from a mod, a downloaded asset or a folder somebody dropped a file into — none of
    // which a build-time content pipeline can see.
    internal int ResolveMesh(AssetPath path) => ResolveMesh(path, AssetScope.Game);

    internal int ResolveMesh(AssetPath path, AssetScope scope)
    {
        TryResolveMesh(path, scope, budgeted: false, out int id);
        return id;
    }

    // As ResolveMesh, but a Sector-scoped load waits for a frame with upload budget left (05 §3.4): false
    // means "not loaded yet, draw nothing for it this frame".
    internal bool TryResolveMesh(AssetPath path, AssetScope scope, out int id) => TryResolveMesh(path, scope, budgeted: true, out id);

    private bool TryResolveMesh(AssetPath path, AssetScope scope, bool budgeted, out int id) =>
        _meshes.TryResolve(path, scope, _assetFrame, budgeted ? Budget : null, _loadMesh, out id);

    // The cooked .sgmesh when the package has one (issue #302), else the .glb; the error mesh when neither.
    private (MeshData?, long) LoadMesh(AssetPath path)
    {
        var geometry = _content.LoadModel(path);
        if (geometry == null)
        {
            Log.Warn(LogCat.Render, $"Mesh '{path}' unavailable or unreadable; drawing the error mesh");
            return (null, 0);
        }
        return (BuildMesh(path.ToString(), geometry, out long bytes), bytes);
    }

    internal MeshData Mesh(int id) => _meshes[id]!;

    // Frees a model's buffers when it is loaded at a scope a sector lets go of (#277, #308). Its slot is
    // reused by a later load, and the path loads afresh the next time something resolves it. A mesh
    // registered from a stream (RegisterMesh) is the engine's: it is not on a mount to load again.
    internal bool EvictMesh(AssetPath path)
    {
        if (!_meshes.TryEvict(path, out var mesh) || mesh == null) return false;
        Dispose(mesh);
        Log.Debug(LogCat.Render, $"Mesh '{path}' evicted: nothing draws it now");
        return true;
    }

    private static void Dispose(MeshData mesh)
    {
        if (!mesh.Owned) return;
        foreach (var part in mesh.Parts)
        {
            part.VertexBuffer.Dispose();
            part.IndexBuffer.Dispose();
        }
    }

    // A .glb that is not on a mount (the generated `r_testskin` model), under a name nothing else uses:
    // afterwards `path` resolves to it like any other mesh. The engine's scope: never evicted.
    internal int RegisterMesh(AssetPath path, System.IO.Stream stream)
    {
        if (_meshes.TryFind(path, out int id) && id != 0) return id;
        _meshes.Forget(path);
        var geometry = MeshGeometry.ReadGlb(stream, path.ToString());
        if (geometry == null)
        {
            Log.Warn(LogCat.Render, $"Mesh '{path}' could not be read; drawing the error mesh");
            _meshes.AddFailed(path);
            return 0;
        }
        return _meshes.Add(path, BuildMesh(path.ToString(), geometry, out long bytes), AssetScope.Engine, bytes, _assetFrame);
    }

    // Hot reload of a `.glb` (issue 4h-3): the file is read again and the mesh replaces the old one *in
    // its slot*, so every id already handed out — in materials, components, this frame's snapshot —
    // draws the new geometry. A file that cannot be read keeps the old mesh. Returns false when the
    // path was never loaded, and a path that had fallen back to the error mesh gets a real one now.
    // Runs in FrameUpdate, before Extract and Render, so no snapshot holds an index into the old parts.
    internal bool ReloadMesh(AssetPath path)
    {
        if (!_meshes.TryFind(path, out int id)) return false;

        var geometry = _content.LoadModel(path);
        long bytes = 0;
        var fresh = geometry == null ? null : BuildMesh(path.ToString(), geometry, out bytes);
        if (fresh == null)
        {
            Log.Warn(LogCat.Render, $"Mesh '{path}' did not reload; keeping the copy already loaded");
            return false;
        }

        if (id == 0)
        {
            _meshes.Forget(path);
            _meshes.Add(path, fresh, AssetScope.Game, bytes, _assetFrame);
            return true;
        }

        var old = _meshes.Replace(id, fresh, bytes);
        if (old != null) Dispose(old);
        return true;
    }

    // The buffers for a model's geometry (a .glb read, or its cooked .sgmesh), not yet in the table. The
    // headless vertex structs are laid out as MonoGame's, so the arrays go to the buffers as they are.
    private MeshData BuildMesh(string name, MeshGeometry model, out long bytes)
    {
        bytes = 0;
        var bounds = new BoundingSphere(model.BoundsCentre, model.BoundsRadius);   // System.Numerics → MonoGame (implicit)
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
                vb = new VertexBuffer(_device, VertexMesh.VertexDeclaration, loaded.Rigid!.Length, BufferUsage.WriteOnly);
                vb.SetData(loaded.Rigid);
            }
            var ib = new IndexBuffer(_device, IndexElementSize.ThirtyTwoBits, loaded.Indices.Length, BufferUsage.WriteOnly);
            ib.SetData(loaded.Indices);
            long size = (long)vb.VertexCount * vb.VertexDeclaration.VertexStride + loaded.Indices.Length * 4L;
            WorkStats.Uploaded(size);
            bytes += size;

            parts.Add(new MeshPart
            {
                VertexBuffer = vb,
                IndexBuffer = ib,
                VertexOffset = 0,
                StartIndex = 0,
                PrimitiveCount = loaded.Indices.Length / 3,
                Bone = Matrix.Identity,      // the file's hierarchy is already baked in (rigid) or is the skin's (skinned)
                Bounds = bounds,
                Skinned = loaded.Skinned != null,
            });
        }

        // Owned: these buffers are the renderer's, so they are disposed with it. An `.xnb` model
        // belonged to the ContentManager, which is the thing that has gone away.
        return new MeshData { Name = name, Parts = parts.ToArray(), Owned = true, Skin = skinned ? model.Skin : null };
    }

    // A mesh built by the engine or a game (terrain chunks, 14 §3): the renderer owns the buffers and
    // hands back a handle (06 §4). Destroy it with DestroyMesh when the chunk goes away.
    public MeshHandle CreateMesh(ReadOnlySpan<VertexPositionNormalTexture> vertices, ReadOnlySpan<int> indices, BoundingSphere bounds, string name = "(procedural)") =>
        CreateMesh(vertices, VertexPositionNormalTexture.VertexDeclaration, indices, bounds, name);

    // A lightmapped brush mesh (issue #313): the same, with each vertex's lightmap coordinates, drawn with the
    // material's effect's `Lightmapped` technique and `lightmap` (which the caller owns and disposes).
    internal MeshHandle CreateMesh(ReadOnlySpan<VertexLightmapped> vertices, ReadOnlySpan<int> indices, BoundingSphere bounds, string name, Texture2D lightmap)
    {
        var handle = CreateMesh(vertices, VertexLightmapped.VertexDeclaration, indices, bounds, name);
        Mesh(handle.Id).Parts[0].Lightmap = lightmap;
        return handle;
    }

    // Splat terrain (issue #307): the same, with each vertex's four layer weights.
    internal MeshHandle CreateMesh(ReadOnlySpan<VertexTerrain> vertices, ReadOnlySpan<int> indices, BoundingSphere bounds, string name) =>
        CreateMesh(vertices, VertexTerrain.VertexDeclaration, indices, bounds, name);

    private MeshHandle CreateMesh<T>(ReadOnlySpan<T> vertices, VertexDeclaration declaration, ReadOnlySpan<int> indices, BoundingSphere bounds, string name)
        where T : struct
    {
        var vb = new VertexBuffer(_device, declaration, vertices.Length, BufferUsage.WriteOnly);
        vb.SetData(vertices.ToArray());
        var ib = new IndexBuffer(_device, IndexElementSize.ThirtyTwoBits, indices.Length, BufferUsage.WriteOnly);
        ib.SetData(indices.ToArray());
        long bytes = (long)vertices.Length * declaration.VertexStride + indices.Length * 4L;
        WorkStats.Uploaded(bytes);   // `stat render` (issue #300)
        var part = new MeshPart
        {
            VertexBuffer = vb,
            IndexBuffer = ib,
            PrimitiveCount = indices.Length / 3,
            Bounds = bounds,
        };
        // The game's scope: the code that made it destroys it (a sector's chunks with the sector, #277).
        return new MeshHandle(_meshes.Add(default, new MeshData { Name = name, Parts = new[] { part }, Owned = true }, AssetScope.Game, bytes, _assetFrame));
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

    // Frees a built mesh. Its slot is reused by a later mesh (#308), so a handle must not be drawn after
    // it is destroyed: the entity holding it goes with it (a sector's chunks, a level's brushes).
    public void DestroyMesh(MeshHandle handle)
    {
        if (handle.IsEmpty || !_meshes.IsLive(handle.Id) || !_meshes.EntryAt(handle.Id).Path.IsEmpty) return;
        if (_meshes.Remove(handle.Id) is { } mesh) Dispose(mesh);
    }

    // The texture id for an asset path (the content service's table), loading it on first use at the
    // game's scope. Missing → 0, the checker (05 §8).
    internal int ResolveTexture(AssetPath path) => _content.ResolveTexture(path, AssetScope.Game);

    // As ResolveTexture at a scope, a Sector-scoped load waiting for a frame with upload budget left
    // (05 §3.4): false means "not loaded yet, draw nothing for it this frame".
    internal bool TryResolveTexture(AssetPath path, AssetScope scope, out int id) =>
        _content.TryResolveTexture(path, scope, Budget, out id);

    internal Texture2D Texture(int id) => _content.Textures[id]!;

    // The frame's atlas rect as UVs in its texture (flipped horizontally for mirrored directions),
    // and its pivot as a 0..1 position inside the quad. Both need the texture size, which only the
    // client knows, so extract asks for them here.
    internal Vector4 Uv(int texture, SpriteFrame frame, bool flipU)
    {
        var t = Texture(texture);
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
        var (_, _, w, h) = RectOf(frame, Texture(texture));
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
            LodCulled = s.Lod.Culled, LodLowered = s.Lod.Lowered,
            Bones = s.Bones.Count,
            Meshes = _meshes.Count, Textures = _content.Textures.Count, Materials = Materials.Count,
        };
        Plan(s);
        _sprites.FaceCameraPosition = _spriteFaceCamera.Value;
        _wire = _wireframe.Value;
        TextureSampling.Anisotropy = _anisotropy.Value;   // the samplers materials pick from (issue #317)
        var clear = new Color(s.Environment.ClearColor);   // the horizon's colour; `sage:sky` draws over it when the world has a sky (4h-5)
        var ctx = Begin(world, s, screen, extracting: false);
        _drawing = s;            // whose shadow maps the lit draws read (RenderView.Shadow)

        // Post-processing (issue 4h-6): with the chain on, the screen's views draw into `sage:scene`
        // (at the render scale's size) instead of the back buffer; PostProcess draws it out below.
        // Views into other targets (a minimap, an editor viewport) are not redirected, so nothing is
        // post-processed twice.
        _waterWanted = screen && WantsWater(s);   // water turns the chain on by itself (issue #411)
        _toScene = screen && PreparePost();

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
        if (_toScene && _post.NeedsDepth) DrawSceneDepth(s);   // for effects that read it (issue #316)
        if (_toScene)
        {
            // The scene is drawn; from here "the screen" is the back buffer again, which the chain binds.
            _toScene = false;
            _bound = NotBound;
        }
        else _device.Viewport = new Viewport(0, 0, back.X, back.Y);

        // PostProcess: once, on the screen, over every view (`sage:post`, the post_effect chain, then a
        // game's passes on the back buffer).
        _stats.PostSteps = screen ? _post.Count : 0;
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
        bool scene = target == RenderViewPlan.Screen && _toScene;
        _device.SetRenderTarget(scene ? _targets.Texture(_sceneTarget) : target == RenderViewPlan.Screen ? null : _targets.Texture(target));
        var size = scene ? _sceneSize : TargetSize(target);
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
    internal void DrawFullScreen(Texture2D texture, Effect? effect, BlendState? blend, SamplerState? sampler = null)
    {
        _fullScreen ??= new SpriteBatch(_device);
        var viewport = _device.Viewport;
        _fullScreen.Begin(SpriteSortMode.Immediate, blend ?? BlendState.Opaque, sampler ?? SamplerState.LinearClamp,
                          DepthStencilState.None, RasterizerState.CullNone, effect);
        _fullScreen.Draw(texture, new Rectangle(0, 0, viewport.Width, viewport.Height), Color.White);
        _fullScreen.End();
        _current = -1;   // the batch set its own device state
        _stats.DrawCalls++;
        _stats.Triangles += 2;
    }

    // ---- Post-processing (issue 4h-6; the chain itself is in Renderer.Post.cs) ----

    // Where a view draws in its target: its viewport, scaled down with the scene when the screen's
    // views draw into `sage:scene` at a render scale below 1 (a split screen's halves stay halves).
    private Rectangle ViewRect(in RenderView view)
    {
        if (!_toScene || view.Target != RenderViewPlan.Screen) return view.Viewport;
        var back = TargetSize(RenderViewPlan.Screen);
        if (_sceneSize == back) return view.Viewport;
        float sx = _sceneSize.X / (float)back.X, sy = _sceneSize.Y / (float)back.Y;
        var r = view.Viewport;
        int x0 = (int)MathF.Round(r.X * sx), y0 = (int)MathF.Round(r.Y * sy);
        int x1 = (int)MathF.Round(r.Right * sx), y1 = (int)MathF.Round(r.Bottom * sy);
        return new Rectangle(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
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
        RenderViewPlan.Bucket(s.ItemViews.AsSpan(0, items), s.ItemKeys.AsSpan(0, items), starts, counts, s.SortKeys, s.Order);
        for (int v = 0; v < views; v++) { s.Views[v].ItemStart = starts[v]; s.Views[v].ItemCount = counts[v]; }
        RenderViewPlan.Bucket(s.SpriteInViews.AsSpan(0, sprites), s.SpriteInKeys.AsSpan(0, sprites), starts, counts, s.SpriteKeys, s.SpriteOrder);
        for (int v = 0; v < views; v++) { s.Views[v].SpriteStart = starts[v]; s.Views[v].SpriteCount = counts[v]; }
    }

    private void DrawView(RenderContext ctx, RenderSnapshot s, int index, Color clear)
    {
        ref var view = ref s.Views[index];
        _viewStamp++;
        var rect = ViewRect(view);
        _device.Viewport = new Viewport(rect);
        if (view.DepthOnly)
        {
            // Over what the target already holds (the viewmodel pass, issue #121): only depth is
            // cleared, so what this view draws is in front of everything drawn before it.
            if (!view.FullTarget)
            {
                _device.ScissorRectangle = rect;
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
            _device.ScissorRectangle = rect;
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
                    _device.Viewport = new Viewport(rect);
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
        // The view's lights, bucketed by cell (issue #314): each draw looks at the lamps that reach it.
        _lightGrid.Build(s.Lights.AsSpan().Slice(view.LightStart, view.LightCount));
        // A lightmapped draw's lamps are the ones not baked into its lightmap (issue #313): the view's first run.
        _dynamicGrid.Build(s.Lights.AsSpan().Slice(view.LightStart, view.DynamicLightCount));
        DrawItems(s, view, _lightGrid, ctx.ItemFrom, ctx.ItemTo, _wire, ref _stats);
        DrawSprites(s, view, _lightGrid, ctx.SpriteFrom, ctx.SpriteTo, _wire, ref _stats);
    }

    // `sage:debug` (06 §3.4, pass 5): debug geometry, over everything the world drew and under the UI.
    internal void DrawDebugLines(RenderContext ctx)
    {
        var s = ctx.Snapshot;
        ref var view = ref s.Views[ctx.View];
        if (view.DebugCount >= 2 && !view.NoDebugLines && _debugLines.Ready(_content))   // NoDebugLines: issue 4n-19
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
        if (view.DepthOnly || view.ShadowCaster || view.NoSky) return;   // NoSky: its camera says so (issue 4n-19)
        if (!_sky.Ready(_content)) return;
        int draws = _sky.Draw(view, s.Environment);
        _current = -1;   // the sky set its own device state
        _stats.DrawCalls += draws;
        _stats.Triangles += draws;
        _stats.Skies++;
    }

    // ---- Sun shadows (issue 4h-4) ----

    // The snapshot being drawn: a view's shadow map is its `Shadow` in this one's `Shadows`, once
    // DrawShadowCasters has drawn it (issue 4n-19). A view with none reads strength 0.
    private RenderSnapshot? _drawing;
    private static readonly ShadowFrame NoShadow = new() { View = -1, Receiver = -1 };

    // `sage:shadow`'s Draw: each cascade's casters into its square of the map. An opaque item is drawn with
    // lit.fx's ShadowCaster (or ShadowCasterSkinned, with the item's palette) whatever its material is: a
    // caster only needs its depth. An alpha-tested item, and a sprite, is drawn with its own material's
    // effect and parameters and that effect's cut-out technique (`ShadowMath.CasterTechnique`, issue
    // 4n-11), so a leaf card's shadow has the leaf's holes. The map holds light-space depth in [0, 1],
    // cleared to 1 (nothing in the way).
    internal void DrawShadowCasters(RenderContext ctx)
    {
        var s = ctx.Snapshot;
        if (s.Shadows.Count == 0) return;

        // A default material that could not be built (no shaders: Linux CI) is already reported, under Shaders.
        var material = Materials.Get(Materials.Resolve(MaterialRecord.Default));
        if (material == null || material.IsError) return;
        var caster = material.Effect;
        if (caster.ShadowCaster == null)
        {
            Log.Once(LogCat.Shaders, LogLevel.Warn, "no-shadow-caster", $"{MaterialRecord.Default}'s effect has no ShadowCaster technique; no sun shadows");
            return;
        }

        bool faceCamera = _sprites.FaceCameraPosition;
        _sprites.FaceCameraPosition = false;   // a sprite's shadow is its silhouette as the sun sees it
        // Every receiving view's map, each into its own target (issue 4n-19).
        for (int m = 0; m < s.Shadows.Count; m++)
        {
            ref var map = ref s.Shadows[m];
            SetTarget(map.Target);
            _device.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Vector4.One, 1f, 0);
            for (int c = 0; c < map.Count; c++)
            {
                ref var view = ref s.Views[map.View + c];
                _stats.ShadowCasters += view.ItemCount + view.SpriteCount;   // what the caster views hold, drawn or not (r_stats)
                _device.Viewport = new Viewport(view.Viewport);
                CasterState();
                caster.ViewProj?.SetValue(view.ViewProj);
                caster.FrameStamp = -1;   // the next view sets its own frame parameters on this effect
                _current = -1;
                DrawCasterItems(s, view, caster);
                DrawCasterSprites(s, view);
            }
            map.Drawn = true;
            _stats.ShadowMaps++;
        }
        _sprites.FaceCameraPosition = faceCamera;
        _current = -1;
    }

    // What every caster draws with: depth tested and written, no blending (the map is a float target),
    // both faces (open meshes such as terrain cast from either side).
    private void CasterState()
    {
        _device.BlendState = BlendState.Opaque;
        _device.DepthStencilState = DepthStencilState.Default;
        _device.RasterizerState = RasterizerState.CullNone;
    }

    // One cascade's mesh casters. Instancing (issue 4n-5): an opaque caster needs only its mesh and matrix,
    // so runs of one mesh part are one draw with ShadowCasterInstanced, whatever their materials. An
    // alpha-tested one needs its own material's texture and cutoff (issue 4n-11), so it is never instanced.
    private void DrawCasterItems(RenderSnapshot s, in RenderView view, EffectBinding caster)
    {
        int from = view.ItemStart, n = view.ItemCount;
        if (InstancingOn && caster.ShadowCasterInstanced != null && n >= _instancingMin.Value)
        {
            GrowInstancing(n);
            for (int i = 0; i < n; i++)
            {
                ref var item = ref s.Items[s.Order[from + i]];
                _candidates[i] = new InstanceCandidate(0, item.Mesh, item.Part, 0, item.BoneCount == 0 && !CutsOut(item));
            }
            int runs = Instancing.Plan(_candidates.AsSpan(0, n), _instancingMin.Value, Instancing.MaxPerDraw, _runs);
            for (int r = 0; r < runs; r++)
            {
                var run = _runs[r];
                if (run.Instanced && InstancingOn)
                {
                    ref var first = ref s.Items[s.Order[from + run.Start]];
                    var data = Instances.Data;
                    for (int i = 0; i < run.Count; i++) data[i].Set(s.Items[s.Order[from + run.Start + i]].World);
                    CasterState();
                    if (DrawInstances(Mesh(first.Mesh).Parts[first.Part], caster.ShadowCasterInstanced, run.Count, ref _stats)) continue;
                }
                DrawCasters(s, view, caster, from + run.Start, from + run.Start + run.Count);
            }
        }
        else DrawCasters(s, view, caster, from, from + n);
    }

    // Whether a caster is drawn with its material's cut-out (issue 4n-11).
    private bool CutsOut(in RenderItem item) => Materials.Get(item.Material) is { Pass: RenderPass.AlphaTested, IsError: false };

    // Shadow casters [from, to) of the snapshot's order, one draw each.
    private void DrawCasters(RenderSnapshot s, in RenderView view, EffectBinding caster, int from, int to)
    {
        var effect = caster.Effect;
        for (int k = from; k < to; k++)
        {
            ref var item = ref s.Items[s.Order[k]];
            if (Materials.Get(item.Material) is { Pass: RenderPass.AlphaTested, IsError: false } cutOut)
            {
                DrawCutOutCaster(s, view, item, cutOut);
                continue;
            }
            var technique = caster.ShadowCaster!;
            if (item.BoneCount > 0 && caster.ShadowCasterSkinned != null && caster.Bones != null)
            {
                var palette = s.Bones.AsSpan().Slice(item.BoneStart, item.BoneCount);
                for (int i = 0; i < palette.Length; i++) _bones[i] = palette[i];   // System.Numerics → MonoGame (implicit)
                caster.Bones.SetValue(_bones);
                technique = caster.ShadowCasterSkinned;
            }
            caster.World?.SetValue(item.World);
            effect.CurrentTechnique = technique;
            var part = Mesh(item.Mesh).Parts[item.Part];
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
    }

    // An alpha-tested mesh (issue 4n-11): its own material's texture and cutoff, its effect's cut-out caster.
    private void DrawCutOutCaster(RenderSnapshot s, in RenderView view, in RenderItem item, MaterialRuntime m)
    {
        bool skinned = item.BoneCount > 0 && m.Effect.Bones != null;
        var technique = skinned ? m.Effect.ShadowCasterAlphaTestSkinned : m.Effect.ShadowCasterAlphaTest;
        if (technique == null)
        {
            Log.Once(LogCat.Shaders, LogLevel.Warn, "no-cutout-caster:" + m.Id,
                     $"Material {m.Id}: its effect has no {ShadowMath.CasterTechnique(RenderPass.AlphaTested, skinned)} technique; it casts no sun shadow");
            return;
        }
        BindCaster(m, view);
        if (skinned)
        {
            var palette = s.Bones.AsSpan().Slice(item.BoneStart, item.BoneCount);
            for (int i = 0; i < palette.Length; i++) _bones[i] = palette[i];   // System.Numerics → MonoGame (implicit)
            m.Effect.Bones!.SetValue(_bones);
        }
        m.Effect.World?.SetValue(item.World);
        m.Effect.Tint?.SetValue(item.Tint);
        m.Effect.Effect.CurrentTechnique = technique;
        var part = Mesh(item.Mesh).Parts[item.Part];
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

    // A cut-out caster's material: its parameters and sampler (MaterialCache.Apply), the cascade's matrix,
    // and the caster's own device state over the material's. Its frame parameters are set again by the
    // next view that draws with it.
    private void BindCaster(MaterialRuntime m, in RenderView view)
    {
        MaterialCache.Apply(_device, m, wireframe: false);
        CasterState();
        m.Effect.ViewProj?.SetValue(view.ViewProj);
        m.Effect.FrameStamp = -1;
        _current = -1;
    }

    // Sprites (issue 4n-11): in runs of one material and texture, as the scene draws them, turned to the
    // sun (the caster view's axes are the light's; ShadowMath.BillboardAxes) and clipped at the material's
    // cutoff by its effect's ShadowCasterAlphaTest.
    private void DrawCasterSprites(RenderSnapshot s, in RenderView view)
    {
        if (view.SpriteCount == 0) return;
        _sprites.Begin(view);
        int run = view.SpriteStart, to = view.SpriteStart + view.SpriteCount;
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
            var m = Materials.Get(material);
            var technique = m?.Effect.ShadowCasterAlphaTest;
            if (m != null && !m.IsError && technique == null)
                Log.Once(LogCat.Shaders, LogLevel.Warn, "no-cutout-caster:" + m.Id,
                         $"Material {m.Id}: its effect has no ShadowCasterAlphaTest technique; its sprites cast no sun shadow");
            if (m != null && !m.IsError && technique != null)
            {
                BindCaster(m, view);
                m.Effect.World?.SetValue(Matrix.Identity);   // sprite vertices are already in camera-relative space
                m.Effect.Tint?.SetValue(Vector4.One);
                m.Effect.Effect.CurrentTechnique = technique;
                foreach (var pass in technique.Passes)
                {
                    _stats.DrawCalls += _sprites.Draw(s.Sprites, s.SpriteOrder, run, end - run, Texture(texture), pass, m.Albedo);
                    _stats.Triangles += (end - run) * 2;
                }
            }
            run = end;
        }
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
            // The view's own shadow map (issue 4n-19), if it has one and it was drawn.
            var shadows = _drawing;
            bool shadowed = shadows != null && view.Shadow >= 0 && view.Shadow < shadows.Shadows.Count && shadows.Shadows[view.Shadow].Drawn;
            ref readonly var shadow = ref shadowed ? ref shadows!.Shadows[view.Shadow] : ref NoShadow;
            m.Effect.SetFrame(view, env, shadow, shadowed ? _targets.Texture(shadow.Target) : null, Materials.MissingTexture);
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

    private readonly LightGrid _lightGrid = new();
    private readonly LightGrid _dynamicGrid = new();

    // Whether an item is drawn with its lightmap (issue #313): a mesh part with one, a material whose effect has
    // `Lightmapped` (MaterialRuntime.Lightmapped), and not skinned.
    private bool Lightmapped(in RenderItem item, MaterialRuntime? m) =>
        m is { Lightmapped: not null, Effect.Lightmap: not null } && item.BoneCount == 0 && Mesh(item.Mesh).Parts[item.Part].Lightmap != null;

    private void DrawItems(RenderSnapshot s, in RenderView view, LightGrid lights, int from, int to, bool wire, ref RenderStats stats)
    {
        int n = to - from;
        if (!InstancingOn || n < _instancingMin.Value)
        {
            for (int k = from; k < to; k++)
            {
                ref var item = ref s.Items[s.Order[k]];
                // Which lamps light this one (06 §3.9). The item's world matrix is camera-relative, so its
                // translation is where it is relative to the camera — the same frame the view's lights are in.
                var grid = Lightmapped(item, Materials.Get(item.Material)) ? _dynamicGrid : lights;
                int lit = grid.Nearest(LitAt(item), _lights);
                DrawItem(s, item, view, _lights.AsSpan(0, lit), wire, ref stats);
            }
            return;
        }

        // Instancing (issue 4n-5): each item's lamps and whether it can be instanced, then the runs
        // (Instancing.Plan). Neighbours with the same tint and lamps share a state number.
        GrowInstancing(n);
        int state = 0;
        for (int i = 0; i < n; i++)
        {
            ref var item = ref s.Items[s.Order[from + i]];
            var chosen = _itemLights.AsSpan(i * LightRules.PerObject, LightRules.PerObject);
            var m = Materials.Get(item.Material);
            bool lightmapped = Lightmapped(item, m);
            int lit = _itemLightCounts[i] = (lightmapped ? _dynamicGrid : lights).Nearest(LitAt(item), chosen);
            if (i > 0)
            {
                ref var previous = ref s.Items[s.Order[from + i - 1]];
                var before = _itemLights.AsSpan((i - 1) * LightRules.PerObject, _itemLightCounts[i - 1]);
                if (previous.Tint != item.Tint || !LightRules.SameSet(before, chosen[..lit])) state++;
            }
            // Not a lightmapped part: each has its own lightmap texture, and the instanced techniques read none.
            bool eligible = m is { Instanced: not null, IsError: false } && item.BoneCount == 0 && !lightmapped;
            _candidates[i] = new InstanceCandidate(item.Material, item.Mesh, item.Part, state, eligible);
        }

        int runs = Instancing.Plan(_candidates.AsSpan(0, n), _instancingMin.Value, Instancing.MaxPerDraw, _runs);
        for (int r = 0; r < runs; r++)
        {
            var run = _runs[r];
            if (run.Instanced && InstancingOn && DrawInstancedRun(s, view, from, run, wire, ref stats)) continue;
            for (int i = run.Start; i < run.Start + run.Count; i++)
                DrawItem(s, s.Items[s.Order[from + i]], view, _itemLights.AsSpan(i * LightRules.PerObject, _itemLightCounts[i]), wire, ref stats);
        }
    }

    // Where an item is lit from: its world matrix's translation, camera-relative like the view's lights.
    private static System.Numerics.Vector3 LitAt(in RenderItem item) => new(item.World.M41, item.World.M42, item.World.M43);

    private void DrawItem(RenderSnapshot s, in RenderItem item, in RenderView view, ReadOnlySpan<LightSample> chosen, bool wire, ref RenderStats stats)
    {
        var m = Use(item.Material, view, s.Environment, wire, ref stats);
        if (m == null) return;
        m.Drawn++;

        m.Effect.World?.SetValue(item.World);
        m.Effect.Tint?.SetValue(item.Tint);
        var technique = item.BoneCount > 0 ? Skin(s, item, m, ref stats) : m.Technique;
        var part = Mesh(item.Mesh).Parts[item.Part];

        // A lightmapped face (issue #313): its baked lamps are in the texture, and `chosen` holds only the others.
        if (Lightmapped(item, m))
        {
            technique = m.Lightmapped!;
            m.Effect.Lightmap!.SetValue(part.Lightmap);
            stats.Lightmapped++;
        }

        m.Effect.SetLights(chosen);
        if (chosen.Length > stats.MaxLightsOnADraw) stats.MaxLightsOnADraw = chosen.Length;
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

    // ---- Instancing (issue 4n-5, 06 §3.7) ----

    // The device can instance (HiDef, and no draw has said otherwise), and `r_instancing` is on.
    private bool _canInstance;
    private bool InstancingOn => _instancing.Value && _canInstance;

    // Per-instance world matrices, a run behind another; and the planning scratch, grown with the runs.
    private InstanceStream<InstanceTransform>? _instances;
    private readonly VertexBufferBinding[] _instanceBindings = new VertexBufferBinding[2];
    private InstanceCandidate[] _candidates = new InstanceCandidate[256];
    private InstanceRun[] _runs = new InstanceRun[256];
    private LightSample[] _itemLights = new LightSample[256 * LightRules.PerObject];
    private int[] _itemLightCounts = new int[256];

    private void GrowInstancing(int n)
    {
        if (_candidates.Length >= n) return;
        int size = Math.Max(n, _candidates.Length * 2);
        _candidates = new InstanceCandidate[size];
        _runs = new InstanceRun[size];
        _itemLights = new LightSample[size * LightRules.PerObject];
        _itemLightCounts = new int[size];
    }

    // A planned run (items from + run.Start ..) as one instanced draw with the material's instanced
    // technique: the tint and lamps are the first item's, which the plan made the same for all of them.
    // False when the device turned out not to instance; the caller then draws them one by one.
    private bool DrawInstancedRun(RenderSnapshot s, in RenderView view, int from, InstanceRun run, bool wire, ref RenderStats stats)
    {
        ref var first = ref s.Items[s.Order[from + run.Start]];
        var m = Use(first.Material, view, s.Environment, wire, ref stats);
        if (m == null) return true;   // not drawn here (Use said why), as one by one would not be
        var chosen = _itemLights.AsSpan(run.Start * LightRules.PerObject, _itemLightCounts[run.Start]);
        m.Effect.Tint?.SetValue(first.Tint);
        m.Effect.SetLights(chosen);
        if (chosen.Length > stats.MaxLightsOnADraw) stats.MaxLightsOnADraw = chosen.Length;

        var data = Instances.Data;
        for (int i = 0; i < run.Count; i++) data[i].Set(s.Items[s.Order[from + run.Start + i]].World);
        if (!DrawInstances(Mesh(first.Mesh).Parts[first.Part], m.Instanced!, run.Count, ref stats)) return false;
        m.Drawn += run.Count;
        return true;
    }

    private InstanceStream<InstanceTransform> Instances =>
        _instances ??= new InstanceStream<InstanceTransform>(_device, InstanceTransform.Declaration, 4 * Instancing.MaxPerDraw);

    // `count` instances of a mesh part, their matrices in Instances.Data[0, count). The part's vertices
    // are offset by the binding, not by a base vertex, which MonoGame's GL backend would add to the
    // instance stream's offset too.
    private bool DrawInstances(MeshPart part, EffectTechnique technique, int count, ref RenderStats stats)
    {
        int at = Instances.Upload(count);
        _instanceBindings[0] = new VertexBufferBinding(part.VertexBuffer, part.VertexOffset, 0);
        _instanceBindings[1] = new VertexBufferBinding(Instances.Buffer, at, 1);
        _device.SetVertexBuffers(_instanceBindings);
        _device.Indices = part.IndexBuffer;
        try
        {
            foreach (var pass in technique.Passes)
            {
                pass.Apply();
                _device.DrawInstancedPrimitives(PrimitiveType.TriangleList, 0, part.StartIndex, part.PrimitiveCount, count);
                stats.DrawCalls++;
                stats.Triangles += part.PrimitiveCount * count;
            }
        }
        catch (NotSupportedException e)
        {
            NoInstancing(e);
            return false;
        }
        stats.Instanced += count;
        stats.InstancedDraws++;
        return true;
    }

    // The device cannot instance after all (a GL below 3.2): everything is drawn one by one from now on.
    private void NoInstancing(Exception e)
    {
        _canInstance = false;
        Log.Warn(LogCat.Render, $"Instancing is not available on this device ({e.Message}); r_instancing draws one by one");
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
    private readonly LightSample[] _nextLights = new LightSample[LightRules.PerObject];

    // A run of sprites sharing a material and a texture is one draw, split where the lamps lighting them
    // change (06 §3.9): a draw carries one set of four, so sprites far from any lamp still batch, and
    // only the ones in a lamp's reach pay for their own lights.
    private void DrawSprites(RenderSnapshot s, in RenderView view, LightGrid lights, int from, int to, bool wire, ref RenderStats stats)
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
                bool lit = m.Effect.LightCount != null && lights.Count > 0;
                int start = run;
                while (start < end)
                {
                    int stop = end, count = 0;
                    if (lit)
                    {
                        count = lights.Nearest(LitAt(s.Sprites[s.SpriteOrder[start]]), _lights);
                        stop = start + 1;
                        while (stop < end)
                        {
                            int c = lights.Nearest(LitAt(s.Sprites[s.SpriteOrder[stop]]), _nextLights);
                            if (!LightRules.SameSet(_lights.AsSpan(0, count), _nextLights.AsSpan(0, c))) break;
                            stop++;
                        }
                    }
                    m.Effect.SetLights(_lights.AsSpan(0, count));
                    if (count > stats.MaxLightsOnADraw) stats.MaxLightsOnADraw = count;
                    bool instanced = InstancingOn && m.Instanced != null && stop - start >= _instancingMin.Value &&
                                     DrawSpritesInstanced(s, start, stop - start, Texture(texture), m, ref stats);
                    if (!instanced)
                        foreach (var pass in m.Technique.Passes)
                        {
                            stats.DrawCalls += _sprites.Draw(s.Sprites, s.SpriteOrder, start, stop - start, Texture(texture), pass, m.Albedo);
                            stats.Triangles += (stop - start) * 2;
                        }
                    start = stop;
                }
            }
            run = end;
        }
    }

    // A sprite run as instanced quads (issue 4n-5), or, when the device turns out not to instance, the
    // CPU-expanded way.
    private bool DrawSpritesInstanced(RenderSnapshot s, int start, int count, Texture2D texture, MaterialRuntime m, ref RenderStats stats)
    {
        try
        {
            foreach (var pass in m.Instanced!.Passes)
            {
                stats.DrawCalls += _sprites.DrawInstanced(s.Sprites, s.SpriteOrder, start, count, texture, pass, m.Albedo);
                stats.Triangles += count * 2;
            }
        }
        catch (NotSupportedException e)
        {
            NoInstancing(e);
            return false;
        }
        stats.InstancedSprites += count;
        return true;
    }

    // Where a sprite is lit from: half-way up the quad above its pivot, so a lamp at head height lights a
    // creature as much as one at its feet. Camera-relative, like the view's lights.
    private static System.Numerics.Vector3 LitAt(in SpriteInstance sprite) =>
        new(sprite.Center.X, sprite.Center.Y + sprite.Size.Y * (0.5f - (1f - sprite.Pivot.Y)), sprite.Center.Z);

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
        // Every mesh in the table and the error mesh. Effects and textures belong to the ContentService.
        foreach (var (_, entry) in _meshes.Entries) Dispose(entry.Value!);
        if (_meshes.Placeholder is { } error) Dispose(error);
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
    public readonly CVar<int> ShadowCascades;      // issue 4n-11
    public readonly CVar<float> ShadowSunStep;
    public readonly CVar<int> ShadowViews;         // issue 4n-19
    public readonly CVar<int> ShadowViewSize;

    // Post-processing (issue 4h-6): the post_effect chain, the engine's two effects' switches, and the
    // render scale (06 §3.9), which works with the chain off too.
    public readonly CVar<bool> Post;
    public readonly CVar<float> Scale;
    public readonly CVar<bool> PostGrade;
    public readonly CVar<bool> PostVignette;

    // HDR, bloom and anti-aliasing (issue #316). Each turns the chain on by itself, like r_scale; HDR and
    // bloom are off by default until they have been tried on more GPUs (decision 6).
    public readonly CVar<bool> Hdr;
    public readonly CVar<bool> Bloom;
    public readonly CVar<AntiAliasing> Aa;

    // Texture filtering (issue #317): the anisotropy smooth-filtered materials sample with.
    public readonly CVar<int> Anisotropy;
    // Water surfaces and the underwater view (issue #411): on by default, since a lake without them is a hole.
    public readonly CVar<bool> Water;
    // The per-frame upload budget for streamed content (05 §3.4, issue #308).
    public readonly CVar<float> UploadMs;

    // Instancing (issue 4n-5, 06 §3.7): on, and the shortest run of copies it collapses to one draw.
    public readonly CVar<bool> InstancingOn;
    public readonly CVar<int> InstancingMin;

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
            "Sun shadows (issues 4h-4, 4n-11): stable cascaded shadow maps over the nearest r_shadow_distance metres of the view.");
        ShadowSize = cvars.Register("r_shadow_size", ShadowMath.DefaultSize, CVarFlags.None,
            "Each cascade of the sun's shadow map, in texels a side (less if the cascades together would pass 4096).", 256, 4096);
        ShadowDistance = cvars.Register("r_shadow_distance", ShadowMath.DefaultDistance, CVarFlags.None,
            "How far from the camera the sun's shadows reach, in metres (the cascades cover this slice of the view).", 5f, 500f);
        ShadowCascades = cvars.Register("r_shadow_cascades", ShadowMath.DefaultCascades, CVarFlags.None,
            "Cascades of the sun's shadow map (issue 4n-11): the view's slice cut into this many maps, finer near the camera.",
            1, ShadowMath.MaxCascades);
        ShadowSunStep = cvars.Register("r_shadow_sun_step", ShadowMath.DefaultSunStep, CVarFlags.None,
            "Degrees the sun's direction snaps to for its shadows (issue 4n-11), so the texel grid holds still as the sun moves; 0: no snapping.",
            0f, 5f);
        ShadowViews = cvars.Register("r_shadow_views", global::Sage.Simulation.ShadowViews.DefaultViews, CVarFlags.None,
            "How many views a frame fits sun shadow maps to (issue 4n-19), the screen's main view first, then split-screen partners, " +
            "monitors and other render-target views; a view past this draws without shadows.", 1, global::Sage.Simulation.ShadowViews.MaxViews);
        ShadowViewSize = cvars.Register("r_shadow_view_size", global::Sage.Simulation.ShadowViews.DefaultViewSize, CVarFlags.None,
            "Each cascade of a shadow map for a view other than the screen's main one, in texels a side (never more than r_shadow_size).",
            256, 4096);
        Post = cvars.Register("r_post", false, CVarFlags.Archive,
            "Post-processing (issue 4h-6): the screen is drawn into sage:scene and through the post_effect chain (colour grade, vignette) before the UI.");
        Scale = cvars.Register("r_scale", 1f, CVarFlags.Archive,
            "Render scale (06 §3.9): the world is drawn at this fraction of the screen and upscaled with point filtering; the UI stays at full size.",
            PostChainPlan.MinScale, 1f);
        PostGrade = cvars.Register("r_post_grade", true, CVarFlags.Archive,
            "The colour grade and exposure post effect (sage:grade), with the sky's night tint; needs r_post 1.");
        PostVignette = cvars.Register("r_post_vignette", true, CVarFlags.Archive,
            "The vignette post effect (sage:vignette); needs r_post 1.");
        Hdr = cvars.Register("r_hdr", false, CVarFlags.Archive,
            "HDR (issue #316): the scene is drawn into a half-float sage:scene and tonemapped (ACES) to the screen; a device " +
            "without half-float targets falls back to 8-bit colour with a warning.");
        Bloom = cvars.Register("r_bloom", false, CVarFlags.Archive,
            "Bloom (issue #316): the scene's bright parts, blurred down and back up a chain of half-size targets (sage:bloom0..), " +
            "added before the tonemap. Brightest with r_hdr 1.");
        Aa = cvars.Register("r_aa", AntiAliasing.Off, CVarFlags.Archive,
            "Anti-aliasing (issue #316): off, fxaa (a post step, last in the chain), or msaa2/msaa4/msaa8 (sage:scene drawn with " +
            "that many samples a pixel; 2, 4 or 8 also work). A device that has fewer samples uses what it has, with a warning.");
        Anisotropy = cvars.Register("r_anisotropy", 4, CVarFlags.Archive,
            "Anisotropic filtering for materials with a Linear or Anisotropic sampler (issue #317): up to this many taps along a " +
            "surface seen at a slant, so a floor stays sharp into the distance; 1 is plain trilinear. Point-sampled pixel art is not affected.",
            1, TextureSampling.MaxAnisotropy);
        Water = cvars.Register("r_water", true, CVarFlags.Archive,
            "Water surfaces (issue #411): the tops of water volumes with a water_surface drawn rippling, reflecting the sky " +
            "and the shore and showing the bottom, and the view tinted under water; a post step that turns the chain on by itself.");
        UploadMs = cvars.Register("asset_upload_ms", 2f, CVarFlags.Archive,
            "Milliseconds a frame may spend loading a streaming world's meshes and textures (05 §3.4); what does not fit waits " +
            "for the next frame. The first load of a frame always starts; 0 is no budget.", 0f, 100f);
        InstancingOn = cvars.Register("r_instancing", false, CVarFlags.Archive,
            "Instancing (06 §3.7): runs of at least r_instancing_min copies of one mesh part with one material, tint and lamps are one " +
            "instanced draw, and long sprite runs instanced quads. Off where the device cannot instance (GL below 3.2).");
        InstancingMin = cvars.Register("r_instancing_min", Instancing.DefaultMin, CVarFlags.Archive,
            "The fewest copies r_instancing draws as one instanced draw; shorter runs are drawn one by one.", 2, Instancing.MaxPerDraw);
    }
}
