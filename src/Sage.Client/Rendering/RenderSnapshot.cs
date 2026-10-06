#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// What the snapshot needs of a pooled list to empty it without knowing what is in it (see `Pool<T>`).
internal interface IPooledList { void Clear(); }

// A list that keeps its array between frames: Clear() keeps the capacity, so steady-state frames
// allocate nothing (docs/design/06 §3.2, 02 §4.6). Elements are accessed by ref.
internal sealed class PooledList<T> : IPooledList
{
    private T[] _items;

    public PooledList(int capacity = 64) { _items = new T[Math.Max(4, capacity)]; }

    public int Count { get; private set; }
    public int Capacity => _items.Length;

    public ref T this[int index] => ref _items[index];

    public ref T Add()
    {
        if (Count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
        return ref _items[Count++];
    }

    // `count` new entries at the end, as one span (a skinned renderer's palette, issue #117).
    public Span<T> AddRange(int count)
    {
        if (Count + count > _items.Length) Array.Resize(ref _items, Math.Max(_items.Length * 2, Count + count));
        var span = _items.AsSpan(Count, count);
        Count += count;
        return span;
    }

    public void Clear() => Count = 0;

    public Span<T> AsSpan() => _items.AsSpan(0, Count);
}

// One camera's view of the frame (06 §3.2), camera-relative: View has no translation (06 §3.3). A value
// type, so a render thread (later) can be handed a copy.
//
// Several per frame (issue #77): the main view, a split-screen partner, a minimap or a mirror drawing
// into a named render target. Extract writes one of each entity's items and sprites *per view*, tagged
// with the view's index; the renderer groups them (`RenderViewPlan`) and fills the ranges below.
internal struct RenderView
{
    public Matrix View;
    public Matrix Projection;
    public Matrix ViewProj;
    public Vector3 CameraPosition;   // origin space (what Extract subtracted)
    public Vector3 Forward;
    public float Near, Far;

    public int Target;               // RenderViewPlan.Screen (the back buffer) or a Renderer target id
    public Rectangle Viewport;       // pixels in the target
    public bool FullTarget;          // the viewport is the whole target: its clear is the target's
    public int Order;                // within a target, lower draws first (06 §3.4a)
    public int Hidden;               // id of an entity this view does not draw (0: none; ViewSource.HiddenFor)
    public bool DepthOnly;           // clears only depth, over what the target holds: the viewmodel pass (issue #121)
    public bool ShadowCaster;        // a cascade of the sun's map (issues 4h-4, 4n-11): casters only, drawn by `sage:shadow`, not as a view

    // Every view's own pass set (issue 4n-19). Where it came from: its index in the world's CameraViews
    // (-1: none, ActiveCamera with no CameraViews, or a pass's own view), and the passes its camera leaves
    // out (Camera.NoShadows and the rest).
    public int Source;
    public bool NoShadows, NoViewmodel, NoSky, NoDebugLines;
    // The shadow map this view's lit draws read: an index into RenderSnapshot.Shadows, or -1 (none: the sun
    // is unshadowed). Each receiver has its own (ShadowViews); the viewmodel's view shares its main view's.
    public int Shadow;
    // A caster view's receiver: the view whose map it is a cascade of (its LOD and draw distances are
    // that view's). -1 for every other view.
    public int Receiver;

    // Written during extract: lights and debug lines are added by one system each, a view at a time,
    // so each view's are contiguous.
    public int LightStart, LightCount;
    // Of those, the first this many are not baked (issue #313): a lightmapped draw is lit by these only,
    // its baked lamps being in its lightmap. The baked ones follow them in the run.
    public int DynamicLightCount;
    public int DebugStart, DebugCount;   // vertices (pairs)
    public int Culled;

    // Written by the renderer when it plans the frame: this view's run in the sorted order arrays.
    public int ItemStart, ItemCount;
    public int SpriteStart, SpriteCount;
}

// Frame-tier lighting/fog/sky parameters (06 §3.9, 07 §3.4), copied from RenderEnvironment at extract.
internal struct EnvironmentParams
{
    public Vector3 ClearColor;
    public Vector3 FogColor;
    public Vector4 FogParams;        // start, end, enabled (0/1), exp² (0: linear; FogMath.ShaderParam)
    public float FogCull;            // past this, fog hides an opaque fogged thing wholly: not drawn (issue 4h-5); +inf: none
    public Vector3 SunDirection;
    public Vector3 SunColor;
    public Vector3 AmbientSky;
    public Vector3 AmbientGround;
    public float Time;
    public float ShadowStrength;     // ShadowMath.Strength: 0 draws no shadow (issue 4h-4)
    public bool DrawSky;             // `sage:sky` draws (issue 4h-5); else the sky is ClearColor
    public Vector3 Zenith;
    public float Stars;
}

// One of the frame's shadow maps (issue 4h-4), written by `sage:shadow`: which views hold the casters, and
// what the lit shaders need to read the map. `Drawn` only once the map has been drawn this frame. Cascades
// (issue 4n-11): `Count` caster views from `View` on, one per cascade, each into its own square of the
// one target (`ShadowMath.Atlas`). One per receiving view (issue 4n-19; ShadowViews), each in its own target.
internal struct ShadowFrame
{
    public int Receiver;             // the view it was fitted to
    public int View;                 // the first caster view, or -1
    public int Count;                // cascades: caster views View .. View + Count - 1
    public int Target;               // the map's render target id
    public Vector3 Camera;           // origin space: the caster views' camera (the main view's)
    public int Size;                 // texels a side of each cascade
    public ShadowCascades Cascades;
    public bool Drawn;
}

// One cascade of the frame's shadow map, as the shaders read it (common.fxh `ShadowLit`).
internal struct ShadowCascade
{
    public Matrix ViewProj;          // relative to ShadowFrame.Camera → this cascade's clip space
    public float Bias;               // ShadowFit.DepthBias
    public Vector4 Rect;             // where it sits in the target, in uv: x, y, width, height
}

[System.Runtime.CompilerServices.InlineArray(ShadowMath.MaxCascades)]
internal struct ShadowCascades
{
    private ShadowCascade _first;
}

// One draw: a mesh part with a material at a camera-relative world matrix (06 §4).
internal struct RenderItem
{
    public int Mesh;           // Renderer mesh id (0 = the error mesh)
    public int Part;
    public int Material;       // MaterialCache id
    public Matrix World;       // camera-relative
    public Vector4 Tint;
    public ulong SortKey;
    public int View;           // index into RenderSnapshot.Views
    // A skinned draw's palette (issue #117): RenderSnapshot.Bones[BoneStart, BoneStart + BoneCount).
    // BoneCount 0 is a rigid draw. Every view that sees a renderer shares its one range.
    public int BoneStart, BoneCount;
}

// Everything the Render phase draws this frame (06 §3.1–3.2): Extract writes it, Render reads only it.
// A world resource installed by ClientModule; pooled, cleared at the start of every Extract.
internal sealed class RenderSnapshot
{
    // Every view this frame (issue #77; v1 had one). Written by CameraExtract; empty when the world
    // has nothing to draw from. The screen's main view, if any, is `MainView`.
    public readonly PooledList<RenderView> Views;
    public int MainView = -1;
    public EnvironmentParams Environment;
    // The frame's shadow maps (issue 4n-19): one per receiving view (RenderView.Shadow), the main view's first.
    public readonly PooledList<ShadowFrame> Shadows;
    // Every list here is made by `Pool<T>`, which is also what puts it in `_pools` for `Clear()`.
    // Point lights were added as a plain `new(32)` and left out of `Clear()`, and the picture stayed
    // right: the extras were duplicates of the same lamps, so the room looked lit while the list grew
    // by two entries a frame and every draw walked all of them. A list that is not cleared is not a
    // bug you can see, so it is one the type stops you writing.
    private readonly List<IPooledList> _pools = new();

    private PooledList<T> Pool<T>(int capacity)
    {
        var list = new PooledList<T>(capacity);
        _pools.Add(list);
        return list;
    }

    public readonly PooledList<RenderItem> Items;
    public readonly PooledList<SpriteInstance> Sprites;
    public readonly PooledList<VertexPositionColor> DebugLines;   // pairs of vertices (06 §3.2)

    // Every point light in range this frame, camera-relative (06 §3.9), per view (`RenderView.LightStart`).
    // Four of these light any one draw; which four is `LightRules.Nearest`, asked per item because a
    // wall and a lamp across the room want different answers.
    public readonly PooledList<LightSample> Lights;

    // Skinned renderers' palettes (issue #117), in model space: one run per renderer seen by any view
    // (`RenderItem.BoneStart`), written by SkinnedMeshExtract through `SkinnedExtract.Emit`.
    public readonly PooledList<System.Numerics.Matrix4x4> Bones;

    public RenderSnapshot()
    {
        Views = Pool<RenderView>(4);
        Items = Pool<RenderItem>(256);
        Sprites = Pool<SpriteInstance>(256);
        DebugLines = Pool<VertexPositionColor>(512);
        Lights = Pool<LightSample>(32);
        Bones = Pool<System.Numerics.Matrix4x4>(256);
        Shadows = Pool<ShadowFrame>(ShadowViews.MaxViews);
    }

    // The water the main view sees (issue #411), written by WaterExtract: drawn by the chain's water step.
    public readonly WaterFrame Water = new();

    public int Culled;                   // items rejected by frustum culling this frame, every view
    public int FogCulled;                // of those, rejected because fog hides them wholly (issue 4h-5)
    public LodCounts Lod;                // renderers per view LOD left out or drew coarser (issue 4n-1, MeshLod.Pick)

    // The view whose LOD choices a renderer remembers for its hysteresis (issue 4n-1): the screen's main
    // view, else the first view that is not the sun's; -1 when there is none. A caster view measures from
    // its receiver (RenderView.Receiver; issue 4n-19).
    internal int LodView()
    {
        if (MainView >= 0) return MainView;
        for (int v = 0; v < Views.Count; v++) if (!Views[v].ShadowCaster) return v;
        return -1;
    }

    // Whether fog hides a camera-relative sphere drawn with `material` in `view` wholly (FogMath.Hides):
    // an opaque or alpha-tested material with fog on, in a view drawn with fog (not the sun's caster view,
    // whose casters may shade what is near). Counted as culled when it does.
    internal bool FogHides(ref RenderView view, MaterialRuntime material, Vector3 center, float radius)
    {
        if (view.ShadowCaster || material.Fog <= 0f || material.Pass == RenderPass.Transparent) return false;
        if (!FogMath.Hides(Environment.FogCull, center.ToNumerics(), radius)) return false;
        Culled++;
        FogCulled++;
        view.Culled++;
        return true;
    }
    public bool HasView => Views.Count > 0;

    // The main view (`MainView`), for what is drawn once on the screen: floating numbers, the HUD.
    public ref RenderView Main => ref Views[MainView];

    // Planning scratch (Renderer.Plan): the keys and views copied out of the lists, and what
    // RenderViewPlan makes of them. Grown with the scene, never shrunk (06 §3.2).
    internal ulong[] ItemKeys = new ulong[256];       // in: Items[i].SortKey
    internal int[] ItemViews = new int[256];          // in: Items[i].View
    internal ulong[] SortKeys = new ulong[256];       // out: keys by view, then key
    internal int[] Order = new int[256];              // out: item indices beside them
    internal ulong[] SpriteInKeys = new ulong[256];
    internal int[] SpriteInViews = new int[256];
    internal ulong[] SpriteKeys = new ulong[256];
    internal int[] SpriteOrder = new int[256];
    internal int[] ViewTargets = new int[4];
    internal int[] ViewOrders = new int[4];
    internal int[] DrawOrder = new int[4];
    internal int[] RangeStarts = new int[4];
    internal int[] RangeCounts = new int[4];

    // Culling state per view slot (camera-relative to CullOrigin). r_freezecull keeps each slot's old
    // frustum while the camera moves. Slot i belongs to whichever view is i-th this frame, which is
    // the same view from frame to frame as long as the set of cameras does not change.
    private BoundingFrustum[] _frustums = Array.Empty<BoundingFrustum>();
    internal Vector3[] CullOrigins = Array.Empty<Vector3>();
    internal bool[] CullValid = Array.Empty<bool>();

    internal BoundingFrustum Frustum(int view) => _frustums[view];

    // Grows the per-view culling state to `views` slots. Allocates only when the view count grows.
    internal void EnsureViewSlots(int views)
    {
        if (_frustums.Length >= views) return;
        int old = _frustums.Length;
        Array.Resize(ref _frustums, views);
        Array.Resize(ref CullOrigins, views);
        Array.Resize(ref CullValid, views);
        for (int i = old; i < views; i++) _frustums[i] = new BoundingFrustum(Matrix.Identity);
    }

    public void Clear()
    {
        foreach (var pool in _pools) pool.Clear();
        Culled = 0;
        FogCulled = 0;
        Lod = default;
        MainView = -1;
        Water.Clear();
    }
}

// Sort keys (06 §3.5), compared as integers:
//   opaque / alpha-tested: [pass:4][layer:4][material:20][mesh:20][depth:16, front to back]
//   transparent:           [pass:4][layer:4][depth:24, back to front][material:20][mesh:12]
// Sorting by material first minimises state changes; depth last gives cheap early-z.
internal static class RenderSortKey
{
    public static ulong Make(RenderPass pass, int layer, int material, int mesh, float depth, float far)
    {
        ulong p = (ulong)RenderStages.SortKeyPass(RenderStages.Of(pass));   // 0, 1 or 3; 2 = sky (issue 4h-1)
        float d = Math.Clamp(depth / MathF.Max(far, 0.001f), 0f, 1f);
        ulong key = (p << 60) | ((ulong)(uint)(layer & 0xF) << 56);
        if (pass != RenderPass.Transparent)
            return key | ((ulong)(uint)(material & 0xFFFFF) << 36) | ((ulong)(uint)(mesh & 0xFFFFF) << 16) | (ulong)(d * 0xFFFF);
        ulong backToFront = 0xFFFFFFUL - (ulong)(d * 0xFFFFFF);
        return key | (backToFront << 32) | ((ulong)(uint)(material & 0xFFFFF) << 12) | (ulong)(uint)(mesh & 0xFFF);
    }
}
