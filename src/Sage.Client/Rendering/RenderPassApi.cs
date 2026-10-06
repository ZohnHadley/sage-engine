#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// A render pass (REDESIGN §4.7, issue 4h-1): declared with [RenderPass("ns:id", RenderStage.X, After = ...)]
// and added in a module's Init with `ctx.Get<RenderPasses>().Add(new MyPass())`. The client seals the
// registry in its Start and the renderer draws the passes in order (docs/design/06 "As built (render
// passes)"). One instance serves every world: what a pass keeps per world it looks up from
// `RenderContext.World`.
//
//   Extract  once per world per frame, in the Extract phase after the cameras and before the other
//            extracts: add views (which the world's meshes and sprites are then drawn into) and items.
//   Draw     in its stage: per view for Opaque to Debug, once for Shadow, PostProcess and Overlay.
//
// Both run on the main thread, every frame: allocate nothing there.
[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public interface IRenderPass
{
    void Extract(RenderContext context) { }
    void Draw(RenderContext context);
}

// The client's render passes: provided by ClientModule in its Init, sealed and ordered in its Start.
// `r_passes` lists them in draw order.
[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public sealed class RenderPasses : RenderPassRegistry<IRenderPass>
{
}

// What a view looks like to a pass: camera-relative matrices (the view has no translation, 06 §3.3),
// the camera's position in origin space, and where in its target it draws.
[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public readonly struct RenderViewInfo
{
    internal RenderViewInfo(in RenderView view, string? target)
    {
        View = view.View;
        Projection = view.Projection;
        ViewProj = view.ViewProj;
        CameraPosition = view.CameraPosition;
        Forward = view.Forward;
        Near = view.Near;
        Far = view.Far;
        Viewport = view.Viewport;
        Target = target;
    }

    public Matrix View { get; }
    public Matrix Projection { get; }
    public Matrix ViewProj { get; }
    public Vector3 CameraPosition { get; }
    public Vector3 Forward { get; }
    public float Near { get; }
    public float Far { get; }
    public Rectangle Viewport { get; }
    public string? Target { get; }   // null: the screen
}

// Everything a pass may touch, handed to its Extract and Draw (REDESIGN §4.7's public extract API). One
// instance, reused: do not keep it past the call.
[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public sealed class RenderContext
{
    private readonly Renderer _renderer;
    internal RenderSnapshot Snapshot = null!;

    // The run of the view's sorted items and sprites a scene stage draws (Renderer.DrawView).
    internal int ItemFrom, ItemTo, SpriteFrom, SpriteTo;
    // The frame's interpolation between ticks (FrameTime.Alpha), in Extract: where the lamps are drawn from (issue #315).
    internal float Alpha;

    internal RenderContext(Renderer renderer) { _renderer = renderer; }

    public Renderer Renderer => _renderer;
    public GraphicsDevice Device => _renderer.Device;

    // The world being extracted or drawn.
    public World World { get; internal set; } = null!;
    public RenderStage Stage { get; internal set; }

    // True in Extract, false in Draw.
    public bool IsExtracting { get; internal set; }

    // Whether this world draws to the screen (`Renderer.ScreenWorld`). PostProcess and Overlay passes
    // only run for that world.
    public bool IsScreen { get; internal set; }

    // The frame's views, the one being drawn in a per-view stage (-1 otherwise) and the screen's main one (-1: none).
    public int ViewCount => Snapshot.Views.Count;
    public int View { get; internal set; } = -1;
    public int MainView => Snapshot.MainView;

    public RenderViewInfo ViewInfo(int view)
    {
        ref var v = ref Snapshot.Views[CheckView(view)];
        return new RenderViewInfo(v, v.Target == RenderViewPlan.Screen ? null : _renderer.TargetName(v.Target));
    }

    // ---- Extract ----

    // One more view this frame, drawn from `position` looking along `rotation` (origin space): into the
    // named render target (null: the screen), over `rect` (normalised x, y, width, height; null: all of
    // it), with `orthoHeight` > 0 making it orthographic. Returns its index. The world's meshes, sprites,
    // lights and debug lines are extracted into it like any camera's; a screen view is dropped in a
    // world that does not draw to the screen (-1).
    public int AddView(Vector3 position, Quaternion rotation, float fovY, float near, float far,
                       string? target = null, Vector4? rect = null, int order = 0, float orthoHeight = 0f)
    {
        RequireExtract(nameof(AddView));
        var request = new ViewRequest
        {
            Position = position, Rotation = rotation, FovY = fovY, Near = near, Far = far,
            Orthographic = orthoHeight > 0f, OrthoHeight = orthoHeight,
            Rect = rect ?? new Vector4(0f, 0f, 1f, 1f),
            Target = target == null ? RenderViewPlan.Screen : _renderer.TargetId(target),
            Order = order,
            Source = -1,
        };
        if (request.Target == RenderViewPlan.Screen && !IsScreen) return -1;
        return CameraExtract.AddView(Snapshot, _renderer, request);
    }

    // One draw of every part of a mesh with a material (empty: sage:lit_default) in a view, at `world`
    // in origin space. Not culled: that is the caller's to decide.
    public void AddItem(int view, MeshHandle mesh, RecordId material, Matrix world, Vector4? tint = null, byte layer = 0)
    {
        RequireExtract(nameof(AddItem));
        ref var v = ref Snapshot.Views[CheckView(view)];
        var materials = _renderer.Materials;
        int meshId = mesh.IsEmpty ? 0 : mesh.Id;
        var data = _renderer.Mesh(meshId);
        int materialId = data.IsError ? 0 : materials.Resolve(material);
        var runtime = materials.Get(materialId);
        if (runtime == null) return;
        world.Translation -= v.CameraPosition;
        for (int p = 0; p < data.Parts.Length; p++)
        {
            var part = data.Parts[p];
            Matrix partWorld = part.Bone * world;
            Vector3 center = Vector3.Transform(part.Bounds.Center, partWorld);
            ref var item = ref Snapshot.Items.Add();
            item.Mesh = meshId;
            item.Part = p;
            item.Material = materialId;
            item.World = partWorld;
            item.Tint = tint ?? Vector4.One;
            item.SortKey = RenderSortKey.Make(runtime.Pass, layer, materialId, meshId, Vector3.Dot(center, v.Forward), v.Far);
            item.View = view;
            item.BoneStart = item.BoneCount = 0;
            item.Morph = 0;
        }
    }

    // ---- Draw ----

    // A named render target's texture, made at its declared size (or 512x512) on first use.
    public RenderTarget2D Target(string name) => _renderer.TargetTexture(_renderer.TargetId(name));

    // Draws into the named target from now on (null: the screen), over all of it, without clearing it.
    // In a per-view stage the renderer puts the view's target and viewport back after the pass.
    public void SetTarget(string? name)
    {
        RequireDraw(nameof(SetTarget));
        _renderer.SetTarget(name == null ? RenderViewPlan.Screen : _renderer.TargetId(name));
    }

    // A texture stretched over the current viewport, through `effect` when given (a post effect's pixel
    // shader), blended with `blend` (null: opaque).
    public void DrawFullScreen(Texture2D texture, Effect? effect = null, BlendState? blend = null)
    {
        RequireDraw(nameof(DrawFullScreen));
        _renderer.DrawFullScreen(texture, effect, blend);
    }

    private int CheckView(int view)
    {
        if ((uint)view >= (uint)Snapshot.Views.Count)
            throw new ArgumentOutOfRangeException(nameof(view), $"There are {Snapshot.Views.Count} view(s) this frame; {view} is not one.");
        return view;
    }

    private void RequireExtract(string what)
    {
        if (!IsExtracting) throw new InvalidOperationException($"RenderContext.{what} is for a pass's Extract, not its Draw.");
    }

    private void RequireDraw(string what)
    {
        if (IsExtracting) throw new InvalidOperationException($"RenderContext.{what} is for a pass's Draw, not its Extract.");
    }
}
