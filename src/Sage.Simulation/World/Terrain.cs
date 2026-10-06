#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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

    // Which surface layer each cell is (issue #270): an index into Terrain.SurfaceLayers, so a footstep
    // on the road sounds unlike one in the grass. Layer 0 everywhere until a generator paints another,
    // and nothing is allocated until then.
    public byte[]? CellLayers { get; private set; }
    public int Cells => Resolution - 1;

    public void SetLayer(int cellX, int cellZ, byte layer)
    {
        if ((uint)cellX >= (uint)Cells || (uint)cellZ >= (uint)Cells) return;
        if (CellLayers == null)
        {
            if (layer == 0) return;
            CellLayers = new byte[Cells * Cells];
        }
        CellLayers[cellZ * Cells + cellX] = layer;
    }

    public byte LayerAt(int cellX, int cellZ) =>
        CellLayers == null || (uint)cellX >= (uint)Cells || (uint)cellZ >= (uint)Cells ? (byte)0 : CellLayers[cellZ * Cells + cellX];

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

    // Vertex normals (issue #277), worked out once when the sector is generated: central differences over
    // the grid, and **past its edge** from the generator itself (ITerrainSampler), so two sectors agree on
    // the normal of the vertices they share and lighting has no seam. Null until ComputeNormals runs.
    internal Vector3[]? Normals { get; private set; }

    // The normal at grid vertex (x, z): the one worked out at generation, or the local estimate.
    internal Vector3 VertexNormal(int x, int z) =>
        Normals != null ? Normals[z * Resolution + x] : NormalAt(x * Spacing, z * Spacing);

    // `outside` gives the height at a vertex index just past the grid (-1 or Resolution), in the
    // neighbouring sector; without it the edge clamps, as NormalAt does, and normals only match inside.
    internal void ComputeNormals(Func<int, int, float>? outside)
    {
        var normals = new Vector3[Resolution * Resolution];
        float d = Spacing;
        for (int z = 0; z < Resolution; z++)
            for (int x = 0; x < Resolution; x++)
            {
                float hl = Sample(x - 1, z, outside), hr = Sample(x + 1, z, outside);
                float hd = Sample(x, z - 1, outside), hu = Sample(x, z + 1, outside);
                normals[z * Resolution + x] = Vector3.Normalize(new Vector3(hl - hr, 2f * d, hd - hu));
            }
        Normals = normals;
    }

    private float Sample(int x, int z, Func<int, int, float>? outside)
    {
        if ((uint)x < (uint)Resolution && (uint)z < (uint)Resolution) return this[x, z];
        if (outside != null) return outside(x, z);
        return this[Math.Clamp(x, 0, Resolution - 1), Math.Clamp(z, 0, Resolution - 1)];
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

// Games implement this and give it to the world's Terrain resource (14 §4).
//
// **Generate runs on worker threads** (issue #277): streaming asks for the sectors just past the ring, and
// coarse copies of the far ring, on the thread pool, several at once. It must be a function of its
// arguments (sector, seed and the heightfield's resolution) and touch nothing shared. The heightfield may
// be coarser than a full sector's (Terrain.FarResolution for the far ring): fill it at `heights.Spacing`.
public interface ITerrainGenerator
{
    void Generate(SectorCoord sector, Heightfield heights, int seed);
}

// A generator that can say the height at any absolute point (issue #277). A sector's edge normals are
// worked out from its neighbours' heights, asked of this, so lighting has no seam where sectors meet.
// For the seam to vanish exactly, Generate must give a vertex the same height this gives its point:
// compute both from `corner + index * spacing` in doubles, as the built-in generators do.
public interface ITerrainSampler
{
    float SampleHeight(double absoluteX, double absoluteZ, int seed);
}

public sealed class TerrainSector
{
    public required SectorCoord Coord;
    public required Heightfield Heights;
    public bool MeshBuilt;        // the client builds chunk meshes once per sector
    public bool CollisionBuilt;   // physics builds a collision mesh once per sector (10)

    // Streaming made this sector live while its source walked (#277), so it is at least a sector away from
    // anything standing: physics and the client may build it over a few ticks. False for anything loaded
    // because something is about to stand on it (the first ring, a jump, travel, a game's own Load).
    public bool Budgeted;
}

// World resource: the loaded terrain sectors. Gameplay asks it for the ground height; the client
// builds meshes for sectors it hasn't drawn yet.
public sealed class Terrain
{
    private readonly Dictionary<SectorCoord, TerrainSector> _sectors = new();
    private readonly List<TerrainSector> _loaded = new();   // iterated every frame: a list, so foreach doesn't box

    public const int SectorResolution = 129;    // 128 cells of 8 m (14 §3)
    public const float SectorSize = 1024f;

    // Coarse heightfields for the far ring (issue #277): 16 cells of 64 m a sector.
    internal const int FarResolution = 17;

    // Setting either drops the sectors generated ahead from the old one: they are a different ground.
    public ITerrainGenerator? Generator
    {
        get => _generator;
        set { if (!ReferenceEquals(_generator, value)) { _generator = value; Changed(); } }
    }

    private ITerrainGenerator? _generator;
    private int _seed;

    // What each terrain layer is made of (issue #270): physics_material ids, by the layer a cell has
    // (Heightfield.SetLayer). Physics gives a sector's collision mesh these as it builds it; a terrain
    // record's "surfaces" sets them.
    public List<RecordId> SurfaceLayers { get; } = new();

    // What the ground is drawn with (issue #307): a `terrain_material` record, whose layers the client
    // blends by a weight map worked out from each sector's heights and normals. Empty: the one tiling
    // texture of `sage:terrain_default`. Set it before sectors load; ones already drawn keep their look.
    // A terrain record's "material" sets it.
    [Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
    public RecordId Material { get; set; }
    public int Seed
    {
        get => _seed;
        set { if (_seed != value) { _seed = value; Changed(); } }
    }

    // Bumped when the generator or seed changes: what was made from the old one is stale (#277).
    internal int Version { get; private set; }

    private void Changed()
    {
        Version++;
        _ahead.Clear();
    }
    public IReadOnlyList<TerrainSector> Sectors => _loaded;

    // Where the simulation is running (R6). Sectors are keyed **absolutely** — sector (12, -3) is the
    // same ground whatever the origin is — while every position handed to this class is in **origin
    // space**, because that is what transforms, physics and the camera hold. This is the one place the
    // two meet, and it is why `HeightAt` did not have to change for every caller when rebasing arrived.
    public Origin Origin { get; set; } = new();

    // Raised when a sector is unloaded, so the client can drop its meshes and physics its collision
    // (14 §3). The sector is gone from `Sectors` by the time this runs.
    public event Action<SectorCoord>? Unloaded;

    // ---- generation, here or on jobs (issue #277) ------------------------------------------------------
    //
    // Streaming asks for the sectors just past the ring **ahead of time** (Prefetch): each is generated on
    // the thread pool, and Load takes the result when the ring reaches it. The result does not depend on
    // which thread made it or when — a generator is a function of sector and seed — so a world is the same
    // with jobs on or off; only where the time is spent changes. A job not started yet when Load needs it
    // is run right there on the main thread rather than waited for (Lazy's claim), and one a worker is
    // part-way through is waited for.

    // Sectors generated ahead and not loaded yet, by coordinate.
    private readonly Dictionary<SectorCoord, Lazy<Heightfield>> _ahead = new();

    // Whether Prefetch uses the thread pool (`stream_jobs`). Off, nothing is generated ahead.
    internal bool Jobs { get; set; } = true;

    // Full sectors generated on the main thread (Load with nothing ahead, or a job run inline) and by
    // jobs: what the streaming scale test counts (issue #277).
    internal int GeneratedHere;
    internal int GeneratedOnJobs;
    private int _generatedOnJobs;   // written by workers

    // The main thread: what counts a generation as "here".
    private readonly int _mainThread = Environment.CurrentManagedThreadId;

    // Starts generating a sector on the thread pool, if it is not loaded or under way. No-op without a
    // generator, or with jobs off.
    internal void Prefetch(SectorCoord coord)
    {
        if (!Jobs || _generator == null || _sectors.ContainsKey(coord) || _ahead.ContainsKey(coord)) return;
        var job = Job(_generator, _seed, coord, SectorResolution);
        _ahead[coord] = job;
        Start(job);
    }

    // Sectors generated (or being generated) ahead, for streaming to drop the ones it has walked away from.
    internal void CollectAhead(List<SectorCoord> into)
    {
        foreach (var coord in _ahead.Keys) into.Add(coord);
    }

    internal void DropAhead(SectorCoord coord) => _ahead.Remove(coord);

    internal bool IsAhead(SectorCoord coord) => _ahead.ContainsKey(coord);

    // Whether every sector asked for ahead has been generated (the scale test waits on this the way a
    // player's walk across a kilometre would).
    internal bool AheadDone
    {
        get
        {
            foreach (var job in _ahead.Values)
                if (!job.IsValueCreated) return false;
            return true;
        }
    }

    // A coarse copy of a sector for the far ring (`resolution` a side), on the thread pool when jobs are
    // on and right now otherwise. Null without a generator.
    internal Lazy<Heightfield>? Coarse(SectorCoord coord, int resolution = FarResolution)
    {
        if (_generator == null) return null;
        var job = Job(_generator, _seed, coord, resolution);
        if (Jobs) Start(job);
        else _ = job.Value;
        return job;
    }

    // Every one of these is evaluated (Prefetch and Coarse start them, Load takes the value), so each is a
    // load in `stat assets` from when it is made to when its heights are there (issue #300).
    private Lazy<Heightfield> Job(ITerrainGenerator generator, int seed, SectorCoord coord, int resolution)
    {
        WorkStats.LoadStarted();
        return new(() =>
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            Heightfield heights;
            using (Profiler.Begin(resolution == SectorResolution ? "Job.TerrainSector" : "Job.TerrainCoarse"))
            {
                try { heights = Generate(generator, seed, coord, resolution); }
                finally { WorkStats.LoadFinished(System.Diagnostics.Stopwatch.GetTimestamp() - started); }
            }
            if (resolution == SectorResolution)
            {
                if (Environment.CurrentManagedThreadId == _mainThread) GeneratedHere++;
                else System.Threading.Interlocked.Increment(ref _generatedOnJobs);
            }
            return heights;
        }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
    }

    // In a method of its own: a lambda capturing `job` in Prefetch would allocate its closure on every
    // call, even the ones that return early (it is made where the captured local's scope begins).
    private static void Start(Lazy<Heightfield> job)
    {
        WorkStats.JobStarted();
        System.Threading.Tasks.Task.Run(() => Run(job));
    }

    // A worker's half: claims the job unless the main thread got there first. A generator that throws is
    // reported when the main thread takes the result (Lazy keeps the exception), not here.
    private static void Run(Lazy<Heightfield> job)
    {
        try { _ = job.Value; }
        catch (Exception) { /* rethrown by Lazy.Value on the main thread */ }
        finally { WorkStats.JobFinished(); }
    }

    // One sector's heights and normals, on whatever thread. With an ITerrainSampler the normals at the
    // edges come from the neighbours' heights, so they match theirs (seam-free); without one they clamp.
    internal static Heightfield Generate(ITerrainGenerator generator, int seed, SectorCoord coord, int resolution)
    {
        var heights = new Heightfield(resolution, SectorSize);
        generator.Generate(coord, heights, seed);
        if (generator is ITerrainSampler sampler)
        {
            var corner = coord.Origin(SectorSize);
            double spacing = heights.Spacing;
            heights.ComputeNormals((x, z) => sampler.SampleHeight(corner.X + x * spacing, corner.Z + z * spacing, seed));
        }
        else heights.ComputeNormals(null);
        return heights;
    }

    // Generates a sector if it isn't loaded yet — or takes it from the job that generated it ahead.
    // Returns it either way; null without a generator.
    public TerrainSector? Load(SectorCoord coord)
    {
        if (_sectors.TryGetValue(coord, out var existing)) return existing;
        if (_generator == null)
        {
            Assert.Ensure(false, "Terrain.Load without a Generator: the game sets one before loading sectors");
            return null;
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool ahead = _ahead.Remove(coord, out var job);
        var heights = (job ?? Job(_generator, _seed, coord, SectorResolution)).Value;
        GeneratedOnJobs = System.Threading.Volatile.Read(ref _generatedOnJobs);
        var sector = new TerrainSector { Coord = coord, Heights = heights };
        _sectors[coord] = sector;
        _loaded.Add(sector);
        Log.Info(LogCat.Streaming, $"Terrain sector {coord} {(ahead ? "taken from its job" : "generated")} in {watch.Elapsed.TotalMilliseconds:F1} ms " +
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
