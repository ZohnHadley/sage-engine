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

    public void Clear() => Count = 0;

    public Span<T> AsSpan() => _items.AsSpan(0, Count);
}

// One camera's view of the frame (06 §3.2), camera-relative: View has no translation (06 §3.3).
internal struct RenderView
{
    public Matrix View;
    public Matrix Projection;
    public Matrix ViewProj;
    public Vector3 CameraPosition;   // origin space (what Extract subtracted)
    public Vector3 Forward;
    public float Near, Far;
}

// Frame-tier lighting/fog/sky parameters (06 §3.9, 07 §3.4), copied from RenderEnvironment at extract.
internal struct EnvironmentParams
{
    public Vector3 ClearColor;
    public Vector3 FogColor;
    public Vector4 FogParams;        // start, end, enabled (0/1), 0
    public Vector3 SunDirection;
    public Vector3 SunColor;
    public Vector3 AmbientSky;
    public Vector3 AmbientGround;
    public float Time;
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
}

// Everything the Render phase draws this frame (06 §3.1–3.2): Extract writes it, Render reads only it.
// A world resource installed by ClientModule; pooled, cleared at the start of every Extract.
internal sealed class RenderSnapshot
{
    public RenderView View;              // v1: one view (split screen, mirrors and shadow views later)
    public EnvironmentParams Environment;
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

    // Every point light in range this frame, camera-relative (06 §3.9). Four of these light any one
    // draw; which four is `LightRules.Nearest`, asked per item because a wall and a lamp across the
    // room want different answers.
    public readonly PooledList<LightSample> Lights;

    public RenderSnapshot()
    {
        Items = Pool<RenderItem>(256);
        Sprites = Pool<SpriteInstance>(256);
        DebugLines = Pool<VertexPositionColor>(512);
        Lights = Pool<LightSample>(32);
    }

    public int Culled;                   // items rejected by frustum culling this frame
    public bool HasView;

    internal ulong[] SortKeys = new ulong[256];
    internal int[] Order = new int[256];
    internal ulong[] SpriteKeys = new ulong[256];
    internal int[] SpriteOrder = new int[256];

    // Culling frustum (camera-relative to CullOrigin). r_freezecull keeps the old one while the camera moves.
    internal readonly BoundingFrustum Frustum = new(Matrix.Identity);
    internal Vector3 CullOrigin;
    internal bool CullValid;

    public void Clear()
    {
        foreach (var pool in _pools) pool.Clear();
        Culled = 0;
        HasView = false;
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
        ulong p = pass switch { RenderPass.Opaque => 0UL, RenderPass.AlphaTested => 1UL, _ => 3UL };   // 2 = sky
        float d = Math.Clamp(depth / MathF.Max(far, 0.001f), 0f, 1f);
        ulong key = (p << 60) | ((ulong)(uint)(layer & 0xF) << 56);
        if (pass != RenderPass.Transparent)
            return key | ((ulong)(uint)(material & 0xFFFFF) << 36) | ((ulong)(uint)(mesh & 0xFFFFF) << 16) | (ulong)(d * 0xFFFF);
        ulong backToFront = 0xFFFFFFUL - (ulong)(d * 0xFFFFFF);
        return key | (backToFront << 32) | ((ulong)(uint)(material & 0xFFFFF) << 12) | (ulong)(uint)(mesh & 0xFFF);
    }
}
