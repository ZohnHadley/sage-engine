#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Simulation;

// Heightmap terrain (docs/design/14 §3). v1 is one sector with no LOD (TODO F13): a game supplies an
// ITerrainGenerator, the engine keeps the heightfield (gameplay samples it to stand on the ground),
// and the client builds the chunk meshes from it. Streaming rings, LOD and dormancy are F14.

// Which 1024 m sector of the exterior space something is in (14 §3, 03 §3.6). Origin rebasing and
// sector-local transforms come with R6; today everything lives in sector (0, 0).
public readonly record struct SectorCoord(int X, int Z)
{
    public static SectorCoord Zero => default;

    // The sector's south-west corner in world metres.
    public Vector3 Origin(float sectorSize) => new(X * sectorSize, 0, Z * sectorSize);

    public override string ToString() => $"({X}, {Z})";
}

// A square grid of heights. Resolution is vertices per side (129 for a sector: 128 cells of 8 m).
public sealed class Heightfield
{
    public Heightfield(int resolution, float size)
    {
        if (resolution < 2) throw new ArgumentOutOfRangeException(nameof(resolution), "a heightfield needs at least 2x2 vertices");
        Resolution = resolution;
        Size = size;
        Heights = new float[resolution * resolution];
    }

    public int Resolution { get; }
    public float Size { get; }                      // metres per side
    public float Spacing => Size / (Resolution - 1);
    public float[] Heights { get; }

    public float this[int x, int z]
    {
        get => Heights[z * Resolution + x];
        set => Heights[z * Resolution + x] = value;
    }

    // Bilinear height at a position inside the field (metres from its south-west corner). Positions
    // outside are clamped to the edge.
    public float HeightAt(float x, float z)
    {
        float fx = Math.Clamp(x / Spacing, 0, Resolution - 1);
        float fz = Math.Clamp(z / Spacing, 0, Resolution - 1);
        int x0 = Math.Min((int)fx, Resolution - 2), z0 = Math.Min((int)fz, Resolution - 2);
        float tx = fx - x0, tz = fz - z0;

        float h00 = this[x0, z0], h10 = this[x0 + 1, z0];
        float h01 = this[x0, z0 + 1], h11 = this[x0 + 1, z0 + 1];
        return (h00 * (1 - tx) + h10 * tx) * (1 - tz) + (h01 * (1 - tx) + h11 * tx) * tz;
    }

    // Surface normal from the neighbouring heights (central differences).
    public Vector3 NormalAt(float x, float z)
    {
        float d = Spacing;
        float hl = HeightAt(x - d, z), hr = HeightAt(x + d, z);
        float hd = HeightAt(x, z - d), hu = HeightAt(x, z + d);
        return Vector3.Normalize(new Vector3(hl - hr, 2f * d, hd - hu));
    }
}

// Games implement this and give it to the world's Terrain resource (14 §4). It runs on the main
// thread in v1; sector generation moves onto jobs with streaming (F14).
public interface ITerrainGenerator
{
    void Generate(SectorCoord sector, Heightfield heights, int seed);
}

public sealed class TerrainSector
{
    public required SectorCoord Coord;
    public required Heightfield Heights;
    public bool MeshBuilt;        // the client builds chunk meshes once per sector
    public bool CollisionBuilt;   // physics builds a collision mesh once per sector (10)
}

// World resource: the loaded terrain sectors. Gameplay asks it for the ground height; the client
// builds meshes for sectors it hasn't drawn yet.
public sealed class Terrain
{
    private readonly Dictionary<SectorCoord, TerrainSector> _sectors = new();
    private readonly List<TerrainSector> _loaded = new();   // iterated every frame: a list, so foreach doesn't box

    public const int SectorResolution = 129;    // 128 cells of 8 m (14 §3)
    public const float SectorSize = 1024f;

    public ITerrainGenerator? Generator { get; set; }
    public int Seed { get; set; }
    public IReadOnlyList<TerrainSector> Sectors => _loaded;

    // Where the simulation is running (R6). Sectors are keyed **absolutely** — sector (12, -3) is the
    // same ground whatever the origin is — while every position handed to this class is in **origin
    // space**, because that is what transforms, physics and the camera hold. This is the one place the
    // two meet, and it is why `HeightAt` did not have to change for every caller when rebasing arrived.
    public Origin Origin { get; set; } = new();

    // Raised when a sector is unloaded, so the client can drop its meshes and physics its collision
    // (14 §3). The sector is gone from `Sectors` by the time this runs.
    public event Action<SectorCoord>? Unloaded;

    // Generates a sector if it isn't loaded yet. Returns it either way; null without a generator.
    public TerrainSector? Load(SectorCoord coord)
    {
        if (_sectors.TryGetValue(coord, out var existing)) return existing;
        if (Generator == null)
        {
            Assert.Ensure(false, "Terrain.Load without a Generator: the game sets one before loading sectors");
            return null;
        }
        var heights = new Heightfield(SectorResolution, SectorSize);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Generator.Generate(coord, heights, Seed);
        var sector = new TerrainSector { Coord = coord, Heights = heights };
        _sectors[coord] = sector;
        _loaded.Add(sector);
        Log.Info(LogCat.Streaming, $"Terrain sector {coord} generated in {watch.Elapsed.TotalMilliseconds:F1} ms " +
                                   $"({SectorResolution}x{SectorResolution} heights, {SectorSize} m)");
        return sector;
    }

    // Drops a sector: its heights, and — through `Unloaded` — whatever the client and physics built
    // from them. Returns false if it was not loaded.
    public bool Unload(SectorCoord coord)
    {
        if (!_sectors.Remove(coord, out var sector)) return false;
        _loaded.Remove(sector);
        Unloaded?.Invoke(coord);
        Log.Info(LogCat.Streaming, $"Terrain sector {coord} unloaded");
        return true;
    }

    public bool IsLoaded(SectorCoord coord) => _sectors.ContainsKey(coord);

    public TerrainSector? Sector(SectorCoord coord) => _sectors.TryGetValue(coord, out var s) ? s : null;

    public static SectorCoord SectorOf(float worldX, float worldZ) =>
        new((int)MathF.Floor(worldX / SectorSize), (int)MathF.Floor(worldZ / SectorSize));

    // The ground height under a position **in origin space**, or 0 where no sector is loaded (v1: the
    // character controller and spawn placement use this until physics has a heightfield collider, F6).
    public float HeightAt(float x, float z)
    {
        var absolute = Origin.ToAbsolute(new Vector3(x, 0, z));
        var sector = Sector(SectorOf(absolute.X, absolute.Z));
        if (sector == null) return 0f;
        var corner = sector.Coord.Origin(SectorSize);
        return sector.Heights.HeightAt(absolute.X - corner.X, absolute.Z - corner.Z);
    }

    public Vector3 NormalAt(float x, float z)
    {
        var absolute = Origin.ToAbsolute(new Vector3(x, 0, z));
        var sector = Sector(SectorOf(absolute.X, absolute.Z));
        if (sector == null) return Vector3.UnitY;
        var corner = sector.Coord.Origin(SectorSize);
        return sector.Heights.NormalAt(absolute.X - corner.X, absolute.Z - corner.Z);
    }

    // Where a loaded sector's corner sits **in origin space** — what a mesh or a collision body is
    // built around, and what moves when the origin does.
    public Vector3 CornerOf(SectorCoord coord) => Origin.ToOrigin(coord.Origin(SectorSize));

    // Drops a position onto the ground.
    public Vector3 OnGround(Vector3 position) => position with { Y = HeightAt(position.X, position.Z) };
}
