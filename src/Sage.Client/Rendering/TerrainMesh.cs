#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace sage_engine;

// Turns loaded terrain sectors into chunk meshes (docs/design/14 §3, 06 §3.2). One sector's 129x129
// heightfield becomes a grid of chunks, each its own mesh and entity, so frustum culling and (later)
// LOD work per chunk. v1 builds every chunk of a sector at once, on the frame the sector appears;
// with streaming (F14) this moves onto jobs and the asset upload budget.
[System("sage.client.terrain_mesh", Phase.FrameUpdate)]
internal sealed class TerrainMeshSystem : ISystem
{
    // 32 cells per chunk → 4x4 chunks of 33x33 vertices per sector.
    public const int ChunkCells = 32;

    private readonly Renderer _renderer;
    private Terrain? _terrain;
    private readonly World _world;
    private readonly List<VertexPositionNormalTexture> _vertices = new();
    private readonly List<int> _indices = new();

    public TerrainMeshSystem(World world, Renderer renderer)
    {
        _world = world;
        _renderer = renderer;
    }

    public void Run(in SystemContext ctx)
    {
        // No streaming plugin, no terrain (issue #13): nothing to mesh.
        if (_terrain == null && !_world.Resources.TryGet(out _terrain)) return;
        var sectors = _terrain!.Sectors;
        for (int i = 0; i < sectors.Count; i++)   // indexed: foreach over the interface would box an enumerator
        {
            var sector = sectors[i];
            if (sector.MeshBuilt) continue;
            Build(sector);
            sector.MeshBuilt = true;
        }
    }

    private void Build(TerrainSector sector)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var heights = sector.Heights;
        int chunks = (heights.Resolution - 1) / ChunkCells;
        var corner = _terrain!.CornerOf(sector.Coord);   // origin space (R6), not absolute
        int built = 0;

        for (int cz = 0; cz < chunks; cz++)
        {
            for (int cx = 0; cx < chunks; cx++)
            {
                var handle = BuildChunk(heights, cx, cz, out BoundingSphere bounds);
                var entity = _world.Create(Transform.At(corner), $"terrain {sector.Coord} {cx},{cz}");
                _world.Add(entity, new MeshRenderer { Handle = handle, Material = TerrainMaterial });
                _world.Add(entity, new SectorOwned { Sector = sector.Coord });   // unloaded with it
                built++;
            }
        }
        Log.Info(LogCat.Streaming, $"Terrain sector {sector.Coord}: {built} chunk meshes " +
                                   $"({ChunkCells}x{ChunkCells} cells each) in {watch.Elapsed.TotalMilliseconds:F1} ms");
    }

    private static readonly RecordId TerrainMaterial = new("sage", "terrain_default");

    // One chunk: positions in world space (the entity transform stays identity, like other static
    // geometry), normals from the heightfield, and UVs that tile the material every metre.
    // Sector-local: the chunk is placed by its entity's transform, so an origin rebase moves it (R6).
    private MeshHandle BuildChunk(Heightfield heights, int chunkX, int chunkZ, out BoundingSphere bounds)
    {
        _vertices.Clear();
        _indices.Clear();
        int x0 = chunkX * ChunkCells, z0 = chunkZ * ChunkCells;
        int side = ChunkCells + 1;
        float spacing = heights.Spacing;
        float min = float.MaxValue, max = float.MinValue;

        for (int z = 0; z < side; z++)
        {
            for (int x = 0; x < side; x++)
            {
                float localX = (x0 + x) * spacing, localZ = (z0 + z) * spacing;
                float height = heights[x0 + x, z0 + z];
                min = MathF.Min(min, height);
                max = MathF.Max(max, height);
                var normal = heights.NormalAt(localX, localZ);
                // **Sector-local**, with the entity's transform placing the chunk (R6). Baking the
                // sector's position into the vertices would nail the mesh to the GPU buffer, so an
                // origin rebase would move the world and leave the ground where it was.
                _vertices.Add(new VertexPositionNormalTexture(
                    new Vector3(localX, height, localZ),
                    new Vector3(normal.X, normal.Y, normal.Z),
                    new Vector2(localX, localZ) / 8f));   // one texture tile per cell
            }
        }

        for (int z = 0; z < ChunkCells; z++)
        {
            for (int x = 0; x < ChunkCells; x++)
            {
                // Clockwise seen from above: MonoGame's default culls counter-clockwise faces.
                int i = z * side + x;
                _indices.Add(i); _indices.Add(i + 1); _indices.Add(i + side);
                _indices.Add(i + 1); _indices.Add(i + side + 1); _indices.Add(i + side);
            }
        }

        float half = ChunkCells * spacing * 0.5f;
        var center = new Vector3((x0 * spacing) + half, (min + max) * 0.5f, (z0 * spacing) + half);
        bounds = new BoundingSphere(center, MathF.Sqrt(2 * half * half + MathF.Pow((max - min) * 0.5f, 2)));
        return _renderer.CreateMesh(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_vertices),
                                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_indices),
                                    bounds, $"terrain {chunkX},{chunkZ}");
    }
}
