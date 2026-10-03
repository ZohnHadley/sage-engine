#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using N = System.Numerics;

namespace Sage.Client;

// Draws the far ring (issue #277; SectorLod, docs/design/14 §3 "As built (the far ring, asset scopes and
// jobs)"). For each far sector: **one** mesh for its coarse ground (Terrain.FarResolution a side, 64 m
// cells, with a skirt hanging from its edges so the step to the full ring's 8 m ground shows no crack),
// one merged mesh of boxes for the far looks that are boxes, and an entity for each far look that is a
// low-detail model. When the sector leaves the far ring all of it goes, and its buffers with it.
//
// Nothing here simulates: these entities have a transform and a mesh renderer and nothing else, and are
// not saved. The camera's far plane decides how much of the ring is actually on screen.
[System("sage.client.far_lod", Phase.FrameUpdate, After = new[] { "sage.client.terrain_mesh" })]
internal sealed class FarLodSystem : ISystem
{
    private const float Skirt = 48f;   // metres the edge strip hangs down: more than a coarse cell can be wrong by

    private readonly World _world;
    private readonly Renderer _renderer;
    private readonly SectorLod _lod;
    private readonly Terrain _terrain;
    private readonly SectorAssets? _assets;

    private readonly Dictionary<SectorCoord, Built> _built = new();
    private readonly List<SectorCoord> _removed = new();
    private readonly List<VertexPositionNormalTexture> _vertices = new();
    private readonly List<int> _indices = new();

    private sealed class Built
    {
        public readonly List<Entity> Entities = new();
        public readonly List<MeshHandle> Meshes = new();
    }

    private static readonly RecordId TerrainMaterial = new("sage", "terrain_default");

    public FarLodSystem(World world, Renderer renderer)
    {
        _world = world;
        _renderer = renderer;
        _lod = world.Resources.Get<SectorLod>();
        _terrain = world.Resources.Get<Terrain>();
        world.Resources.TryGet(out _assets);
        _lod.Removed += coord => _removed.Add(coord);
    }

    public void Run(in SystemContext ctx)
    {
        if (_removed.Count > 0)
        {
            for (int i = 0; i < _removed.Count; i++) Drop(_removed[i]);
            _removed.Clear();
        }

        var sectors = _lod.Sectors;
        for (int i = 0; i < sectors.Count; i++)
        {
            var far = sectors[i];
            if (!far.GroundBuilt && far.Heights != null)
            {
                Ground(far);
                far.GroundBuilt = true;
            }
            // Far looks stand on the coarse ground, so they wait for it (unless there is no ground at all).
            if (!far.ProxiesBuilt && (far.Heights != null || far.Job == null))
            {
                Proxies(far);
                far.ProxiesBuilt = true;
            }
        }
    }

    private Built For(SectorCoord coord)
    {
        if (!_built.TryGetValue(coord, out var built)) _built[coord] = built = new Built();
        return built;
    }

    // Everything drawn for a sector that left the far ring; the models only it drew are released too.
    private void Drop(SectorCoord coord)
    {
        if (!_built.Remove(coord, out var built)) return;
        foreach (var mesh in built.Meshes) _renderer.DestroyMesh(mesh);
        using (_assets?.Leave())
            foreach (var entity in built.Entities)
                if (_world.IsAlive(entity)) _world.Destroy(entity);
    }

    private void Ground(FarSector far)
    {
        var heights = far.Heights!;
        int side = heights.Resolution;
        float spacing = heights.Spacing;
        _vertices.Clear();
        _indices.Clear();
        float min = float.MaxValue, max = float.MinValue;

        for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++)
            {
                float h = heights[x, z];
                min = MathF.Min(min, h);
                max = MathF.Max(max, h);
                var n = heights.VertexNormal(x, z);
                _vertices.Add(new VertexPositionNormalTexture(new Vector3(x * spacing, h, z * spacing),
                                                              new Vector3(n.X, n.Y, n.Z), new Vector2(x, z) * (spacing / 8f)));
            }
        for (int z = 0; z < side - 1; z++)
            for (int x = 0; x < side - 1; x++)
            {
                int i = z * side + x;   // clockwise from above, as TerrainMesh's
                _indices.Add(i); _indices.Add(i + 1); _indices.Add(i + side);
                _indices.Add(i + 1); _indices.Add(i + side + 1); _indices.Add(i + side);
            }

        // The skirt: each edge's vertices again, `Skirt` lower, joined to the edge by a strip facing out.
        for (int e = 0; e < 4; e++)
            for (int k = 0; k < side - 1; k++)
            {
                int a = EdgeVertex(e, k, side), b = EdgeVertex(e, k + 1, side);
                int start = _vertices.Count;
                Hang(a);
                Hang(b);
                // Both windings: which side faces out depends on the edge, and a skirt is seen from outside.
                _indices.Add(a); _indices.Add(start); _indices.Add(b);
                _indices.Add(b); _indices.Add(start); _indices.Add(start + 1);
                _indices.Add(a); _indices.Add(b); _indices.Add(start);
                _indices.Add(b); _indices.Add(start + 1); _indices.Add(start);
            }

        float half = heights.Size * 0.5f;
        var bounds = new BoundingSphere(new Vector3(half, (min + max) * 0.5f, half),
                                        MathF.Sqrt(2 * half * half + MathF.Pow((max - min) * 0.5f + Skirt, 2)));
        var handle = _renderer.CreateMesh(CollectionsMarshal.AsSpan(_vertices), CollectionsMarshal.AsSpan(_indices), bounds, $"far ground {far.Coord}");
        var entity = _world.Create(Transform.At(_terrain.CornerOf(far.Coord)), $"far ground {far.Coord}");
        _world.Add(entity, new MeshRenderer { Handle = handle, Material = TerrainMaterial });
        var built = For(far.Coord);
        built.Entities.Add(entity);
        built.Meshes.Add(handle);
    }

    private void Hang(int vertex)
    {
        var top = _vertices[vertex];
        _vertices.Add(new VertexPositionNormalTexture(top.Position - new Vector3(0, Skirt, 0), top.Normal, top.TextureCoordinate));
    }

    private static int EdgeVertex(int edge, int k, int side) => edge switch
    {
        0 => k,                                // south (z = 0)
        1 => (side - 1) * side + k,            // north
        2 => k * side,                         // west (x = 0)
        _ => k * side + side - 1,              // east
    };

    // The far looks: models as entities, boxes merged into one mesh a material.
    private void Proxies(FarSector far)
    {
        if (far.Proxies.Count == 0) return;
        var built = For(far.Coord);
        var corner = far.Coord.Origin(Terrain.SectorSize);
        var cornerHere = _terrain.CornerOf(far.Coord);

        foreach (var proxy in far.Proxies)
        {
            if (proxy.Mesh.IsEmpty) continue;
            var model = _world.Create(new Transform
            {
                LocalPosition = cornerHere + Foot(far, proxy, corner),
                LocalRotation = SageMath.RotationFromYaw(proxy.Yaw * MathF.PI / 180f),
                LocalScale = N.Vector3.One,
            }, $"far look {far.Coord}");
            _world.Add(model, new MeshRenderer { Mesh = proxy.Mesh, Material = proxy.Material });
            built.Entities.Add(model);
        }

        // Boxes: one mesh for each material, found by the first box that has it (no dictionary for the
        // usual case of one).
        for (int first = 0; first < far.Proxies.Count; first++)
        {
            if (!far.Proxies[first].Mesh.IsEmpty) continue;
            var material = far.Proxies[first].Material;
            bool seen = false;
            for (int j = 0; j < first && !seen; j++) seen = far.Proxies[j].Mesh.IsEmpty && far.Proxies[j].Material == material;
            if (seen) continue;

            _vertices.Clear();
            _indices.Clear();
            for (int k = first; k < far.Proxies.Count; k++)
            {
                var proxy = far.Proxies[k];
                if (proxy.Mesh.IsEmpty && proxy.Material == material)
                    Box(Foot(far, proxy, corner), proxy.Size, proxy.Yaw * MathF.PI / 180f);
            }

            float half = Terrain.SectorSize * 0.5f;
            var bounds = new BoundingSphere(new Vector3(half, 0, half), half * 1.5f + 200f);
            var handle = _renderer.CreateMesh(CollectionsMarshal.AsSpan(_vertices), CollectionsMarshal.AsSpan(_indices), bounds, $"far looks {far.Coord}");
            var entity = _world.Create(Transform.At(cornerHere), $"far looks {far.Coord}");
            _world.Add(entity, new MeshRenderer { Handle = handle, Material = material });
            built.Entities.Add(entity);
            built.Meshes.Add(handle);
        }
    }

    // Where a far look stands, from the sector's corner: on the coarse ground when it was placed on the ground.
    private static N.Vector3 Foot(FarSector far, in FarProxy proxy, N.Vector3 corner)
    {
        float y = proxy.OnGround ? far.HeightAt(proxy.Absolute.X, proxy.Absolute.Z) + proxy.Absolute.Y : proxy.Absolute.Y;
        return new N.Vector3(proxy.Absolute.X - corner.X, y, proxy.Absolute.Z - corner.Z);
    }

    // A box standing on `foot`, turned by `yaw`, into the merged lists.
    private void Box(N.Vector3 foot, N.Vector3 size, float yaw)
    {
        var turn = Matrix.CreateRotationY(yaw);
        var centre = new Vector3(foot.X, foot.Y + size.Y * 0.5f, foot.Z);
        var half = new Vector3(size.X, size.Y, size.Z) * 0.5f;
        Span<Vector3> normals = stackalloc Vector3[] { Vector3.Up, Vector3.Down, Vector3.Left, Vector3.Right, Vector3.Forward, Vector3.Backward };
        foreach (var normal in normals)
        {
            var side1 = new Vector3(normal.Y, normal.Z, normal.X);
            var side2 = Vector3.Cross(normal, side1);
            int start = _vertices.Count;
            _indices.Add(start); _indices.Add(start + 1); _indices.Add(start + 2);
            _indices.Add(start); _indices.Add(start + 2); _indices.Add(start + 3);
            var n = Vector3.TransformNormal(normal, turn);
            Corner(normal - side1 - side2, Vector2.Zero);
            Corner(normal - side1 + side2, Vector2.UnitX);
            Corner(normal + side1 + side2, Vector2.One);
            Corner(normal + side1 - side2, Vector2.UnitY);

            void Corner(Vector3 unit, Vector2 uv) =>
                _vertices.Add(new VertexPositionNormalTexture(centre + Vector3.Transform(unit * half, turn), n, uv));
        }
    }
}
