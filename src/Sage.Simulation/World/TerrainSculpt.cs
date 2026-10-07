#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Numerics;

namespace Sage.Simulation;

// Sculpted and painted ground (issue #372, docs/design/15 §11 "terrain tools"; 14 §3).
//
// The built-in generators (TerrainRecord) give a whole world of ground from a seed, and nothing a person
// can shape: no hill where the castle stands, no road worn into the grass, no lake. A **sculpt** is what
// the editor's terrain tools write on top of a generator, sector by sector:
//
//   * **heights**: metres added to the generator's height at each of a sector's 129x129 vertices, so a
//     hill raised on noise stays a hill on the same noise, and a flattened square is flat however the
//     hills around it lie;
//   * **paint**: per vertex, how much of each of the four terrain layers is painted over what the
//     terrain material's height and slope rules say (one byte a layer, painter's order: a later layer
//     over the ones before it), which is also the cell's surface layer for footsteps and physics where a
//     layer is painted at least half;
//   * **water**: a water surface over the sector, in absolute metres: a `water_volume` from that height
//     down to the sector's lowest ground, placed with the sector and gone with it.
//
// A `terrain` record's `sculpt` names the file (`terrain/<record name>.sterrain` when it names none). It is
// read when a scene puts the record's ground in a world (Scenes.ApplyTerrain), and from then on every
// sector the streaming rings load, here or on a job, has its sculpt applied as it is generated — the full
// ring and the far ring's coarse copies alike. A vertex two sectors share is written in both, so the
// heights, and the normals worked out across the edge from them, have no seam.
//
// The file is binary, deflated: `SGTS`, a version, then the sectors that have anything in them.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public sealed class TerrainSculpt
{
    public const string Extension = ".sterrain";
    private const uint Magic = 0x53544753;   // "SGTS"
    private const int FileVersion = 1;

    private readonly Dictionary<SectorCoord, SculptSector> _sectors = new();

    public int Count => _sectors.Count;
    public IEnumerable<SculptSector> Sectors => _sectors.Values;

    public SculptSector? Get(SectorCoord coord) => _sectors.TryGetValue(coord, out var s) ? s : null;

    // Puts `sector` in, replacing what that coordinate had; an empty one takes the coordinate out.
    public void Set(SculptSector sector)
    {
        if (sector.IsEmpty) _sectors.Remove(sector.Coord);
        else _sectors[sector.Coord] = sector;
    }

    public bool Remove(SectorCoord coord) => _sectors.Remove(coord);

    // The sector's sculpt, made empty when it has none: for a tool that is about to write into it.
    public SculptSector GetOrAdd(SectorCoord coord)
    {
        if (!_sectors.TryGetValue(coord, out var s)) _sectors[coord] = s = new SculptSector(coord);
        return s;
    }

    public TerrainSculpt Clone()
    {
        var copy = new TerrainSculpt();
        foreach (var (coord, sector) in _sectors) copy._sectors[coord] = sector.Clone();
        return copy;
    }

    // ---- The file ---------------------------------------------------------------------------------------

    public void Write(Stream stream)
    {
        using var deflate = new ZLibStream(stream, CompressionLevel.Optimal, leaveOpen: true);
        using var w = new BinaryWriter(deflate);
        w.Write(Magic);
        w.Write(FileVersion);
        var sectors = new List<SculptSector>(_sectors.Values);
        sectors.RemoveAll(s => s.IsEmpty);
        sectors.Sort((a, b) => a.Coord.Z != b.Coord.Z ? a.Coord.Z.CompareTo(b.Coord.Z) : a.Coord.X.CompareTo(b.Coord.X));   // a stable file
        w.Write(sectors.Count);
        foreach (var s in sectors)
        {
            w.Write(s.Coord.X);
            w.Write(s.Coord.Z);
            bool heights = s.HasHeights;
            byte flags = (byte)((heights ? 1 : 0) | (s.Paint != null ? 2 : 0) | (s.Water != null ? 4 : 0));
            w.Write(flags);
            if (heights) foreach (float h in s.Heights) w.Write(h);
            if (s.Paint != null) foreach (uint p in s.Paint) w.Write(p);
            if (s.Water is { } water) w.Write(water);
        }
    }

    // Throws InvalidDataException for a file that is not one.
    public static TerrainSculpt Read(Stream stream)
    {
        var sculpt = new TerrainSculpt();
        using var inflate = new ZLibStream(stream, CompressionMode.Decompress, leaveOpen: true);
        using var r = new BinaryReader(inflate);
        try
        {
            if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a terrain sculpt (no SGTS header)");
            int version = r.ReadInt32();
            if (version != FileVersion) throw new InvalidDataException($"a terrain sculpt of version {version}; this engine reads {FileVersion}");
            int count = r.ReadInt32();
            if (count < 0) throw new InvalidDataException("a negative sector count");
            for (int i = 0; i < count; i++)
            {
                var s = new SculptSector(new SectorCoord(r.ReadInt32(), r.ReadInt32()));
                byte flags = r.ReadByte();
                if ((flags & 1) != 0) for (int v = 0; v < s.Heights.Length; v++) s.Heights[v] = r.ReadSingle();
                if ((flags & 2) != 0)
                {
                    s.Paint = new uint[SculptSector.Vertices];
                    for (int v = 0; v < s.Paint.Length; v++) s.Paint[v] = r.ReadUInt32();
                }
                if ((flags & 4) != 0) s.Water = r.ReadSingle();
                sculpt.Set(s);
            }
        }
        catch (EndOfStreamException) { throw new InvalidDataException("the file ends part-way through a sector"); }
        return sculpt;
    }

    public byte[] ToBytes()
    {
        using var memory = new MemoryStream();
        Write(memory);
        return memory.ToArray();
    }

    public static TerrainSculpt FromBytes(byte[] bytes)
    {
        using var memory = new MemoryStream(bytes, writable: false);
        return Read(memory);
    }
}

// One sector's sculpt: height offsets and paint by vertex (Terrain.SectorResolution a side, row by row from
// the south-west corner), and its water.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public sealed class SculptSector
{
    public const int Resolution = Terrain.SectorResolution;
    public const int Vertices = Resolution * Resolution;

    public SculptSector(SectorCoord coord)
    {
        Coord = coord;
        Heights = new float[Vertices];
    }

    public SectorCoord Coord { get; }

    // Metres added to the generator's height, by vertex.
    public float[] Heights { get; }

    // How much of each layer is painted over the material's rules, by vertex: layer i in byte i (0–255).
    // Null until something is painted.
    public uint[]? Paint { get; set; }

    // The water surface over the sector, absolute metres; null for none.
    public float? Water { get; set; }

    public bool HasHeights => Array.Exists(Heights, h => h != 0f);
    public bool IsEmpty => Water == null && (Paint == null || Array.TrueForAll(Paint, p => p == 0)) && !HasHeights;

    public float HeightAt(int x, int z) => Heights[z * Resolution + x];
    public uint PaintAt(int x, int z) => Paint == null ? 0u : Paint[z * Resolution + x];

    // How much of `layer` is painted at a vertex, 0 to 1.
    public float Painted(int x, int z, int layer) => ((PaintAt(x, z) >> (8 * layer)) & 0xFF) / 255f;

    public SculptSector Clone()
    {
        var copy = new SculptSector(Coord) { Water = Water, Paint = (uint[]?)Paint?.Clone() };
        Array.Copy(Heights, copy.Heights, Vertices);
        return copy;
    }

    // Equal in every height, paint and water: an undo step that changed nothing is not kept.
    public bool SameAs(SculptSector? other) =>
        other != null && other.Coord == Coord && other.Water == Water
        && Heights.AsSpan().SequenceEqual(other.Heights)
        && (Paint ?? Array.Empty<uint>()).AsSpan().SequenceEqual(other.Paint ?? Array.Empty<uint>());
}

// What the terrain applies as it generates (Terrain.Generate): an immutable snapshot of the sculpt, so a job
// on a worker reads the sectors as they were when it was started while the editor writes the next ones.
internal static class SculptApply
{
    public const int Cells = Terrain.SectorResolution - 1;

    // The offset at a vertex in absolute vertex units (a sector's corner is `coord * 128`): from the
    // sector that holds it, the south-west one of those that share it — the same value, since a tool writes
    // a shared vertex into each of them.
    public static float OffsetAt(ImmutableDictionary<SectorCoord, SculptSector> sculpt, long gx, long gz)
    {
        if (sculpt.IsEmpty) return 0f;
        var coord = new SectorCoord((int)FloorDiv(gx, Cells), (int)FloorDiv(gz, Cells));
        if (!sculpt.TryGetValue(coord, out var s)) return 0f;
        return s.HeightAt((int)(gx - (long)coord.X * Cells), (int)(gz - (long)coord.Z * Cells));
    }

    public static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    // The sector's own sculpt laid onto freshly generated heights: offsets at every vertex (a coarse field
    // takes every `step`th), the paint, and the surface layers the paint decides.
    public static void Apply(ImmutableDictionary<SectorCoord, SculptSector> sculpt, SectorCoord coord, Heightfield heights)
    {
        if (!sculpt.TryGetValue(coord, out var s)) return;
        int res = heights.Resolution;
        int step = Cells / Math.Max(res - 1, 1);
        bool exact = step * (res - 1) == Cells;
        for (int z = 0; z < res; z++)
            for (int x = 0; x < res; x++)
                heights[x, z] += exact ? s.HeightAt(x * step, z * step) : Bilinear(s, x * (float)Cells / (res - 1), z * (float)Cells / (res - 1));

        if (s.Paint == null) return;
        if (exact && step == 1) heights.Paint = s.Paint;   // never written after it is given here (a tool clones first)
        else if (exact)
        {
            var coarse = new uint[res * res];
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    coarse[z * res + x] = s.PaintAt(x * step, z * step);
            heights.Paint = coarse;
        }
        if (res != Terrain.SectorResolution) return;

        // A cell takes the layer painted most over its four corners, if that is at least half.
        for (int z = 0; z < Cells; z++)
            for (int x = 0; x < Cells; x++)
            {
                int best = -1;
                float most = 0.5f;
                for (int layer = 0; layer < TerrainSplat.MaxLayers; layer++)
                {
                    float amount = (s.Painted(x, z, layer) + s.Painted(x + 1, z, layer) + s.Painted(x, z + 1, layer) + s.Painted(x + 1, z + 1, layer)) * 0.25f;
                    if (amount >= most) { most = amount; best = layer; }
                }
                if (best >= 0) heights.SetLayer(x, z, (byte)best);
            }
    }

    private static float Bilinear(SculptSector s, float fx, float fz)
    {
        int x0 = Math.Min((int)fx, Cells - 1), z0 = Math.Min((int)fz, Cells - 1);
        float tx = fx - x0, tz = fz - z0;
        return (s.HeightAt(x0, z0) * (1 - tx) + s.HeightAt(x0 + 1, z0) * tx) * (1 - tz)
             + (s.HeightAt(x0, z0 + 1) * (1 - tx) + s.HeightAt(x0 + 1, z0 + 1) * tx) * tz;
    }

    // The painted weights laid over the material's (TerrainSplat.Weights): each layer, in order, over the
    // ones before it by how much of it is painted — the same painter's order the rules use.
    public static Vector4 Overlay(Vector4 weights, uint paint)
    {
        if (paint == 0) return weights;
        Span<float> w = stackalloc float[] { weights.X, weights.Y, weights.Z, weights.W };
        for (int i = 0; i < TerrainSplat.MaxLayers; i++)
        {
            float a = ((paint >> (8 * i)) & 0xFF) / 255f;
            if (a <= 0f) continue;
            for (int j = 0; j < TerrainSplat.MaxLayers; j++) w[j] *= 1f - a;
            w[i] += a;
        }
        return new Vector4(w[0], w[1], w[2], w[3]);
    }
}

// A sculpt's water (issue #372): each loaded sector whose sculpt has a water surface gets a `water_volume`
// over the whole sector, from the surface down to a metre under its lowest ground, owned by the sector and
// gone with it. Runs in an edit world too, so the editor sees the water it sets.
[System("sage.streaming.terrain_water", Phase.Late, After = new[] { "sage.streaming.sectors" }, Condition = RunCondition.EvenWhenEditing)]
internal sealed class TerrainWaterSystem : ISystem
{
    private readonly World _world;
    private Terrain? _terrain;

    public TerrainWaterSystem(World world) => _world = world;

    public void Run(in SystemContext ctx)
    {
        if (_terrain == null && !_world.Resources.TryGet(out _terrain)) return;
        var sectors = _terrain!.Sectors;
        for (int i = 0; i < sectors.Count; i++)
        {
            var sector = sectors[i];
            if (sector.WaterBuilt) continue;
            sector.WaterBuilt = true;
            if (sector.Water is not { } surface) continue;
            float lowest = float.MaxValue;
            foreach (float h in sector.Heights.Heights) lowest = MathF.Min(lowest, h);
            float depth = MathF.Max(surface - lowest + 1f, 1f);
            var corner = _terrain.CornerOf(sector.Coord);
            var centre = new Vector3(corner.X + Terrain.SectorSize * 0.5f, corner.Y + surface - depth * 0.5f, corner.Z + Terrain.SectorSize * 0.5f);
            var entity = _world.Create(Transform.At(centre), $"water {sector.Coord}");
            _world.Add(entity, new WaterVolume { Size = new Vector3(Terrain.SectorSize, depth, Terrain.SectorSize), Drag = 2f, Buoyancy = 2f });
            _world.Add(entity, new SectorOwned { Sector = sector.Coord });
            entity.AddTag<TerrainBuilt>();
        }
    }

    // What a refreshed sector had built from its old heights, destroyed as a leaving sector's is (its chunk
    // meshes released), while the rest of what it owns stays.
    internal static void DropBuilt(World world, SectorAssets assets, SectorCoord coord)
    {
        using (assets.Leave())
        {
            foreach (var entity in world.Query<SectorOwned>().AllTags(Tags.Get<TerrainBuilt>()).Entities.ToEntityList())
                if (world.Get<SectorOwned>(entity).Sector == coord) world.Destroy(entity);
            world.FlushCommands();
        }
    }
}
