#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// The far ring (issue #277; docs/design/14 §3 "As built (the far ring, asset scopes and jobs)").
//
// The full-detail ring (`stream_radius`) is the world: its ground has collision, its content is placed and
// simulates. Past it, out to `stream_far_radius`, is **what you can see but not reach yet**: each sector has
// a coarse copy of its ground (Terrain.FarResolution a side, generated on a job) and, for a streamed scene,
// a list of proxies — the placements whose prefab says how it looks from afar (`PrefabRecord.Far`). Nothing
// there is an entity, nothing simulates, and nothing is saved: the client draws it cheaply (one mesh a
// sector for the ground, one for the boxes) and drops it when the sector comes into the full ring or leaves
// the far one.
//
// Which sectors are full, far or nothing is the simulation's decision (StreamingSystem), deterministic and
// in sector order. When a far sector's coarse ground arrives depends on the thread pool, so the far ring is
// presentation only: nothing in the simulation reads it.

// How a prefab looks from the far ring (`"far"` on a prefab record).
//
//   "far": { "mesh": "models/tower_far.glb" }              a low-detail model
//   "far": { "size": [6, 18, 6], "material": "stone" }     a box standing on the ground
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public sealed class FarLook
{
    [AssetKind("mesh")]
    [Property(Tooltip = "A low-detail model drawn in its place from the far ring; left out, a box of `size`")]
    public AssetPath Mesh;
    [Property(Unit = "m", Tooltip = "The box drawn when there is no mesh: width, height and depth, standing on its position")]
    public Vector3 Size = new(2f, 2f, 2f);
    [RecordRef("material")]
    [Property(Tooltip = "Empty = sage:lit_default")]
    public RecordId Material;
}

// One placement as the far ring sees it: where it is, absolutely, and how to draw it.
internal readonly record struct FarProxy(Vector3 Absolute, bool OnGround, float Yaw, AssetPath Mesh, Vector3 Size, RecordId Material);

// A sector of the far ring.
internal sealed class FarSector
{
    public required SectorCoord Coord;

    // Its coarse ground: null without a generator, and not ready until the job is done (Heights).
    public Lazy<Heightfield>? Job;
    public Heightfield? Heights;

    // What a streamed scene placed in it that has a far look (empty for anything else).
    public readonly List<FarProxy> Proxies = new();

    // The client's: whether it has drawn what is here so far (ground, proxies).
    public bool GroundBuilt;
    public bool ProxiesBuilt;

    // The ground height at an absolute position inside the sector, from the coarse copy; 0 before it is in.
    public float HeightAt(float absoluteX, float absoluteZ)
    {
        if (Heights == null) return 0f;
        var corner = Coord.Origin(Terrain.SectorSize);
        return Heights.HeightAt(absoluteX - corner.X, absoluteZ - corner.Z);
    }
}

// World resource of sage.streaming: the far ring.
internal sealed class SectorLod
{
    private readonly Dictionary<SectorCoord, FarSector> _byCoord = new();
    private readonly List<FarSector> _sectors = new();   // in the order they came in; indexed, so no boxing

    // `stream_far_radius` as last read: 0 or anything up to `stream_radius` means no far ring.
    public int Radius;

    // The terrain version and streamed scene the far sectors were made from: either changing starts over.
    internal int TerrainVersion;
    internal object? Scene;

    public IReadOnlyList<FarSector> Sectors => _sectors;
    public int Count => _sectors.Count;

    // Raised when a sector leaves the far ring — into the full ring or out of sight — so the client drops
    // what it drew for it. It is gone from `Sectors` by then.
    public event Action<SectorCoord>? Removed;

    public bool Contains(SectorCoord coord) => _byCoord.ContainsKey(coord);

    public FarSector? Get(SectorCoord coord) => _byCoord.TryGetValue(coord, out var far) ? far : null;

    public FarSector Add(SectorCoord coord)
    {
        var far = new FarSector { Coord = coord };
        _byCoord[coord] = far;
        _sectors.Add(far);
        return far;
    }

    public void Remove(SectorCoord coord)
    {
        if (!_byCoord.Remove(coord, out var far)) return;
        _sectors.Remove(far);
        Removed?.Invoke(coord);
    }

    public void Clear()
    {
        for (int i = _sectors.Count - 1; i >= 0; i--) Remove(_sectors[i].Coord);
    }

    // Takes whatever coarse ground has finished. Allocates nothing.
    public int Collect()
    {
        int arrived = 0;
        for (int i = 0; i < _sectors.Count; i++)
        {
            var far = _sectors[i];
            if (far.Heights != null || far.Job == null || !far.Job.IsValueCreated) continue;
            far.Heights = far.Job.Value;
            arrived++;
        }
        return arrived;
    }

    // Waits for every far sector's ground (tests, and a loading screen that wants the horizon in at once).
    public void Finish()
    {
        for (int i = 0; i < _sectors.Count; i++)
            if (_sectors[i] is { Heights: null, Job: { } job } far) far.Heights = job.Value;
    }
}
