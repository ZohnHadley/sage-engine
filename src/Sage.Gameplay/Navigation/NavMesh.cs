#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Gameplay;

// A navmesh built from the world's solid geometry (16 §3.4, issue #264, TODO F23's "later" half).
//
// **A heightfield, not polygons.** Recast's first half — voxelise the solids, keep the tops a body can
// stand on with room above it, link neighbouring tops a step apart, erode by the body's radius — and
// then *stop*: the walkable spans are searched directly rather than being traced into polygons. The
// polygon half of Recast is what makes a mesh small to store and quick to search over long distances;
// here the long distances are the coarse graph's job (below), so the fine search only ever covers a few
// tiles and a span grid is enough. It is also the half that is easy to get exactly right: a span is a
// column and a height, so every answer is checkable by hand.
//
// **Tiles of 8 m, 32 cells of 25 cm a side, keyed absolutely.** A sector is 1024 m (14 §3), so 128 tiles
// tile one exactly and a tile never straddles a sector border: a sector that unloads takes its tiles
// with it, and nothing is rebuilt when the origin moves (R6) — a tile's key is the same ground whatever
// the origin is, like a sector's. Quarter-metre cells because a doorway is about a metre: at half a
// metre, eroding by a 0.35 m body closes it.
//
// **Baked from the geometry, not from physics.** Brush hulls (their planes, exactly), terrain heights,
// and the boxes of static colliders. Nothing that moves — a dynamic crate, a door, a creature — is in
// the mesh; the local grid (`NavGrid`) and the steering in `MoveToTargetTask` still answer for those
// (#265 is doors and dynamic obstacles). Because the input is plain data the bake is deterministic and
// headless, and `sage validate` runs the same bake on a level's brushes alone.
//
// **Baked when first needed, kept, and dropped when its ground changes.** A tile is baked the first time
// a search reaches it (a per-tick budget, `BakesPerTick`), so a level's tiles exist once a creature
// thinks about it and a far sector's never do. A level placed or unloaded, a terrain sector loaded or
// unloaded, a static collider added, moved or removed: each drops the tiles it touches, and they are
// baked again on demand.
//
// **Two searches.** A coarse A* over *regions* — the connected pieces of each tile, linked where their
// spans link across a tile edge — finds the way across the map; a tile not baked yet is one optimistic
// node, so a long plan costs a few nodes per tile rather than a bake per tile. Then a fine A* over spans
// runs inside the first few regions of that route (the corridor) and is straightened into corners. A
// creature re-plans as it walks, so it only ever needs the next stretch exactly.
internal readonly record struct NavMeshAgent(float Radius, float Height, float StepHeight, float MaxSlopeDegrees)
{
    // The engine's default movement profile (CharacterMovement): what the mesh is baked for.
    public static NavMeshAgent Default => new(0.35f, 1.8f, 0.45f, 50f);
}

// Something solid, in absolute metres: a convex hull by its planes (a brush), or a box when `Planes` is
// null (a collider's bounds).
internal readonly struct NavSolid
{
    public readonly Vector3 Min, Max;
    public readonly Vector4[]? Planes;   // outward normal in XYZ, distance in W: inside is n·p <= w

    public NavSolid(Vector3 min, Vector3 max, Vector4[]? planes = null)
    {
        Min = min;
        Max = max;
        Planes = planes;
    }
}

// What a bake reads: solids and, optionally, terrain. Kept apart from the world so `sage validate` can
// bake a level's brushes with nothing else around.
internal sealed class NavGeometry
{
    public readonly List<NavSolid> Solids = new();
    public Terrain? Terrain;

    // A brush's planes, oriented outward and moved by `offset` (level-local to absolute).
    public static NavSolid FromBrush(LevelBrush brush, Vector3 offset)
    {
        var planes = new Vector4[brush.Faces.Length];
        var centre = brush.Centre;
        for (int i = 0; i < planes.Length; i++)
        {
            var face = brush.Faces[i];
            var n = face.Normal;
            float d = Vector3.Dot(n, face.Positions[0]);
            if (Vector3.Dot(n, centre) > d) { n = -n; d = -d; }   // whatever the winding said, outward
            planes[i] = new Vector4(n, d + Vector3.Dot(n, offset));
        }
        return new NavSolid(brush.Min + offset, brush.Max + offset, planes);
    }

    public void AddLevel(MapLevel level, Vector3 absolutePosition)
    {
        foreach (var brush in level.Brushes) Solids.Add(FromBrush(brush, absolutePosition));
    }
}

// One baked tile: its walkable spans, their links, and the regions they make.
internal sealed class NavTile
{
    public int X, Z;                 // absolute tile coordinates
    public int Slot;
    public long LastUsed;

    public int[] ColumnStart = Array.Empty<int>();    // Cells*Cells + 1
    public float[] Floor = Array.Empty<float>();
    public float[] Ceiling = Array.Empty<float>();
    public short[] Column = Array.Empty<short>();     // which column a span is in
    public int[] Links = Array.Empty<int>();          // span*4 + direction: a packed span, or -1
    public short[] Region = Array.Empty<short>();
    public int RegionCount;
    public Vector3[] RegionCentre = Array.Empty<Vector3>();
    public byte[] RegionSides = Array.Empty<byte>();  // bit d: the region has spans on the edge facing d

    // The fine search's working set, stamped with a search number rather than cleared.
    public float[] Cost = Array.Empty<float>();
    public int[] From = Array.Empty<int>();
    public int[] Visited = Array.Empty<int>();

    public int SpanCount => Floor.Length;
}

internal enum NavMeshAnswer { Found, NoRoute, CannotAnswer }

internal sealed class NavMesh
{
    public const int Cells = 32;
    public const float Cell = 0.25f;
    public const float TileSize = Cells * Cell;
    public const int MaxLayers = 4;

    private const int SpanBits = 12;                       // 32 * 32 columns * 4 layers = 4096 spans
    private const int SpanMask = (1 << SpanBits) - 1;
    private const int MaxCorridor = 8;                     // regions the fine search may cover
    private const float MergeGap = 0.02f;                  // solids closer than this are one solid
    // A solid has to reach this far into a cell to be in it. Without it a wall that only touches a cell's
    // edge fills it, which makes every wall a cell thicker and closes a metre-wide door.
    private const float Touch = 0.01f;

    // Directions: +X, +Z, -X, -Z. The opposite of d is (d + 2) & 3.
    private static readonly int[] Dx = { 1, 0, -1, 0 };
    private static readonly int[] Dz = { 0, 1, 0, -1 };

    private readonly Dictionary<(int, int), NavTile> _tiles = new();
    private readonly List<NavTile?> _slots = new();
    private readonly Stack<int> _freeSlots = new();

    public NavMesh(NavMeshAgent agent)
    {
        Agent = agent;
        _cosSlope = MathF.Cos(agent.MaxSlopeDegrees * MathF.PI / 180f);
        _pad = (int)MathF.Ceiling(agent.Radius / Cell) + 1;
    }

    public NavMeshAgent Agent { get; }
    private readonly float _cosSlope;
    private readonly int _pad;

    public int TileCount => _tiles.Count;

    // Tiles kept at once; past it the least recently used goes. 2048 tiles is a square kilometre of
    // quarter-metre cells at about 40 KB each, and a creature only ever searches a few dozen.
    public int MaxTiles { get; set; } = 2048;

    // Tiles a tick may bake, across every plan. A bake is about a fifth of a millisecond.
    public int BakesPerTick { get; set; } = 16;

    public int Baked { get; private set; }
    public int LastNodes { get; private set; }
    public void ResetStats() { Baked = 0; }

    private long _tick = -1;
    private int _bakesThisTick;
    private long _useClock;

    // ---- tiles ---------------------------------------------------------------------------------------

    public static int TileOf(float absolute) => (int)MathF.Floor(absolute / TileSize);

    public NavTile? Tile(int x, int z) => _tiles.TryGetValue((x, z), out var tile) ? tile : null;

    public bool IsBaked(int x, int z) => _tiles.ContainsKey((x, z));

    public void Clear()
    {
        _tiles.Clear();
        _slots.Clear();
        _freeSlots.Clear();
    }

    // Starts a tick's bake budget, and lets go of the least recently used tiles past `MaxTiles` — here,
    // between searches, because a search holds tiles by their slots.
    public void BeginTick(long tick)
    {
        if (tick == _tick) return;
        _tick = tick;
        _bakesThisTick = 0;
        Evict();
    }

    // Bakes a tile if the budget allows; true if it is baked now.
    public bool TryBake(int x, int z, NavGeometry geometry, bool unbudgeted = false)
    {
        if (IsBaked(x, z)) return true;
        if (!unbudgeted && _bakesThisTick >= BakesPerTick) return false;
        _bakesThisTick++;
        Bake(x, z, geometry);
        return true;
    }

    // Drops every tile whose ground overlaps an absolute box (XZ only: a tile is a column of the world).
    public int Invalidate(Vector3 min, Vector3 max)
    {
        int x0 = TileOf(min.X - Cell * _pad), x1 = TileOf(max.X + Cell * _pad);
        int z0 = TileOf(min.Z - Cell * _pad), z1 = TileOf(max.Z + Cell * _pad);
        int dropped = 0;
        if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > _tiles.Count)
        {
            // A big box (a sector) and few tiles: look at the tiles rather than the box.
            _drop.Clear();
            foreach (var tile in _tiles.Values)
                if (tile.X >= x0 && tile.X <= x1 && tile.Z >= z0 && tile.Z <= z1) _drop.Add(tile);
            foreach (var tile in _drop) { Remove(tile); dropped++; }
            _drop.Clear();
            return dropped;
        }
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
                if (Tile(x, z) is { } tile) { Remove(tile); dropped++; }
        return dropped;
    }

    private readonly List<NavTile> _drop = new();

    private void Remove(NavTile tile)
    {
        // Its neighbours' links into it go with it.
        for (int d = 0; d < 4; d++)
        {
            if (Tile(tile.X + Dx[d], tile.Z + Dz[d]) is not { } other) continue;
            int back = (d + 2) & 3;
            for (int s = 0; s < other.SpanCount; s++)
            {
                int link = other.Links[s * 4 + back];
                if (link >= 0 && (link >> SpanBits) == tile.Slot) other.Links[s * 4 + back] = -1;
            }
        }
        _tiles.Remove((tile.X, tile.Z));
        _slots[tile.Slot] = null;
        _freeSlots.Push(tile.Slot);
    }

    // ---- baking -------------------------------------------------------------------------------------

    // Scratch for one bake, kept so a bake allocates only the tile it makes.
    private float[] _pFloor = Array.Empty<float>(), _pCeil = Array.Empty<float>();
    private int[] _pCount = Array.Empty<int>(), _pLinks = Array.Empty<int>(), _pDist = Array.Empty<int>(), _pMap = Array.Empty<int>();
    private int[] _queue = Array.Empty<int>();
    private readonly List<int> _candidates = new();
    private Interval[] _intervals = new Interval[16];

    private struct Interval
    {
        public float Lo, Hi, TopNy;
    }

    public NavTile Bake(int tx, int tz, NavGeometry geometry)
    {
        if (Tile(tx, tz) is { } stale) Remove(stale);

        int pad = _pad, w = Cells + 2 * pad, columns = w * w, capacity = columns * MaxLayers;
        if (_pFloor.Length < capacity)
        {
            _pFloor = new float[capacity];
            _pCeil = new float[capacity];
            _pLinks = new int[capacity * 4];
            _pDist = new int[capacity];
            _pMap = new int[capacity];
            _queue = new int[capacity];
            _pCount = new int[columns];
        }

        float x0 = tx * TileSize - pad * Cell, z0 = tz * TileSize - pad * Cell;
        float x1 = x0 + w * Cell, z1 = z0 + w * Cell;

        // The solids that can touch this tile at all.
        _candidates.Clear();
        for (int i = 0; i < geometry.Solids.Count; i++)
        {
            var solid = geometry.Solids[i];
            if (solid.Max.X < x0 || solid.Min.X > x1 || solid.Max.Z < z0 || solid.Min.Z > z1) continue;
            _candidates.Add(i);
        }

        // 1. Every column's walkable tops: the solids under it merged, each top with room above it.
        float half = Cell * 0.5f;
        for (int j = 0; j < w; j++)
            for (int i = 0; i < w; i++)
            {
                int column = j * w + i;
                float cx = x0 + (i + 0.5f) * Cell, cz = z0 + (j + 0.5f) * Cell;
                int n = 0;

                if (geometry.Terrain is { } terrain &&
                    terrain.Sector(Terrain.SectorOf(cx, cz)) is { } sector)
                {
                    var corner = sector.Coord.Origin(Terrain.SectorSize);
                    float h = sector.Heights.HeightAt(cx - corner.X, cz - corner.Z);
                    float ny = sector.Heights.NormalAt(cx - corner.X, cz - corner.Z).Y;
                    Push(ref n, -1e9f, h, ny);
                }

                foreach (int index in _candidates)
                {
                    var solid = geometry.Solids[index];
                    if (solid.Max.X <= cx - half + Touch || solid.Min.X >= cx + half - Touch ||
                        solid.Max.Z <= cz - half + Touch || solid.Min.Z >= cz + half - Touch) continue;
                    if (solid.Planes == null) { Push(ref n, solid.Min.Y, solid.Max.Y, 1f); continue; }
                    if (Column(solid.Planes, cx, cz, half, out float lo, out float hi, out float topNy)) Push(ref n, lo, hi, topNy);
                }

                // Sorted bottom up (insertion: a column has a handful), then merged where they touch.
                for (int a = 1; a < n; a++)
                {
                    var item = _intervals[a];
                    int b = a - 1;
                    while (b >= 0 && _intervals[b].Lo > item.Lo) { _intervals[b + 1] = _intervals[b]; b--; }
                    _intervals[b + 1] = item;
                }
                int merged = 0;
                for (int a = 0; a < n; a++)
                {
                    if (merged > 0 && _intervals[a].Lo <= _intervals[merged - 1].Hi + MergeGap)
                    {
                        ref var last = ref _intervals[merged - 1];
                        if (_intervals[a].Hi > last.Hi) { last.Hi = _intervals[a].Hi; last.TopNy = _intervals[a].TopNy; }
                        continue;
                    }
                    _intervals[merged++] = _intervals[a];
                }

                int count = 0;
                for (int a = 0; a < merged && count < MaxLayers; a++)
                {
                    float floor = _intervals[a].Hi;
                    float ceiling = a + 1 < merged ? _intervals[a + 1].Lo : float.PositiveInfinity;
                    if (ceiling - floor < Agent.Height || _intervals[a].TopNy < _cosSlope) continue;
                    int span = column * MaxLayers + count++;
                    _pFloor[span] = floor;
                    _pCeil[span] = ceiling;
                }
                _pCount[column] = count;
            }

        // 2. Links to the neighbouring columns: a step up or down, with room for a body across it.
        for (int j = 0; j < w; j++)
            for (int i = 0; i < w; i++)
            {
                int column = j * w + i;
                for (int k = 0; k < _pCount[column]; k++)
                {
                    int span = column * MaxLayers + k;
                    for (int d = 0; d < 4; d++)
                    {
                        int ni = i + Dx[d], nj = j + Dz[d];
                        int best = -1;
                        if ((uint)ni < (uint)w && (uint)nj < (uint)w)
                        {
                            int other = nj * w + ni;
                            float bestDy = float.MaxValue;
                            for (int m = 0; m < _pCount[other]; m++)
                            {
                                int candidate = other * MaxLayers + m;
                                if (!Connects(_pFloor[span], _pCeil[span], _pFloor[candidate], _pCeil[candidate])) continue;
                                float dy = MathF.Abs(_pFloor[candidate] - _pFloor[span]);
                                if (dy < bestDy) { bestDy = dy; best = candidate; }
                            }
                        }
                        _pLinks[span * 4 + d] = best;
                    }
                }
            }

        // 3. Erosion: how many steps each span is from an edge (a wall, a ledge), and only those far
        // enough for the body's radius are kept. Spans on the padding's own rim do not count as edges:
        // what is beyond them is unknown, and the padding is wide enough that it does not matter.
        int head = 0, tail = 0;
        for (int j = 0; j < w; j++)
            for (int i = 0; i < w; i++)
            {
                int column = j * w + i;
                bool rim = i == 0 || j == 0 || i == w - 1 || j == w - 1;
                for (int k = 0; k < _pCount[column]; k++)
                {
                    int span = column * MaxLayers + k;
                    bool edge = false;
                    if (!rim)
                        for (int d = 0; d < 4; d++)
                            if (_pLinks[span * 4 + d] < 0) { edge = true; break; }
                    _pDist[span] = edge ? 0 : int.MaxValue;
                    if (edge) _queue[tail++] = span;
                }
            }
        while (head < tail)
        {
            int span = _queue[head++];
            for (int d = 0; d < 4; d++)
            {
                int next = _pLinks[span * 4 + d];
                if (next < 0 || _pDist[next] <= _pDist[span] + 1) continue;
                _pDist[next] = _pDist[span] + 1;
                _queue[tail++] = next;
            }
        }

        // 4. The tile's own spans: the inner columns, eroded, compacted.
        int kept = 0;
        for (int j = 0; j < Cells; j++)
            for (int i = 0; i < Cells; i++)
            {
                int column = (j + pad) * w + (i + pad);
                for (int k = 0; k < _pCount[column]; k++)
                {
                    int span = column * MaxLayers + k;
                    bool walkable = _pDist[span] == int.MaxValue || (_pDist[span] + 0.5f) * Cell >= Agent.Radius;
                    _pMap[span] = walkable ? kept++ : -1;
                }
            }

        int slot = _freeSlots.Count > 0 ? _freeSlots.Pop() : AddSlot();
        var tile = new NavTile
        {
            X = tx, Z = tz, Slot = slot,
            ColumnStart = new int[Cells * Cells + 1],
            Floor = new float[kept], Ceiling = new float[kept], Column = new short[kept],
            Links = new int[kept * 4], Region = new short[kept],
            Cost = new float[kept], From = new int[kept], Visited = new int[kept],
        };
        int written = 0;
        for (int j = 0; j < Cells; j++)
            for (int i = 0; i < Cells; i++)
            {
                int local = j * Cells + i;
                tile.ColumnStart[local] = written;
                int column = (j + pad) * w + (i + pad);
                for (int k = 0; k < _pCount[column]; k++)
                {
                    int span = column * MaxLayers + k;
                    if (_pMap[span] < 0) continue;
                    tile.Floor[written] = _pFloor[span];
                    tile.Ceiling[written] = _pCeil[span];
                    tile.Column[written] = (short)local;
                    for (int d = 0; d < 4; d++)
                    {
                        int target = _pLinks[span * 4 + d];
                        int ti = target < 0 ? -1 : (target / MaxLayers) % w - pad, tj = target < 0 ? -1 : (target / MaxLayers) / w - pad;
                        bool inside = target >= 0 && (uint)ti < Cells && (uint)tj < Cells && _pMap[target] >= 0;
                        tile.Links[written * 4 + d] = inside ? Pack(slot, _pMap[target]) : -1;
                    }
                    written++;
                }
            }
        tile.ColumnStart[Cells * Cells] = written;

        BuildRegions(tile);
        _slots[slot] = tile;
        _tiles[(tx, tz)] = tile;
        tile.LastUsed = ++_useClock;
        for (int d = 0; d < 4; d++)
            if (Tile(tx + Dx[d], tz + Dz[d]) is { } neighbour) Stitch(tile, neighbour, d);

        Baked++;
        return tile;
    }

    private void Push(ref int n, float lo, float hi, float topNy)
    {
        if (n == _intervals.Length) Array.Resize(ref _intervals, n * 2);
        _intervals[n++] = new Interval { Lo = lo, Hi = hi, TopNy = topNy };
    }

    // The vertical extent of a convex hull over a square cell centred on (cx, cz), conservatively: every
    // plane is relaxed by how far the cell's corners reach along it, so a thin wall between two cell
    // centres still blocks the cell it is in rather than slipping between them (but one that only touches
    // a cell's edge is not in it: `Touch`).
    private static bool Column(Vector4[] planes, float cx, float cz, float half, out float lo, out float hi, out float topNy)
    {
        lo = float.NegativeInfinity;
        hi = float.PositiveInfinity;
        topNy = 1f;
        foreach (var p in planes)
        {
            float s = p.X * cx + p.Z * cz;
            float slack = half * (MathF.Abs(p.X) + MathF.Abs(p.Z));
            if (MathF.Abs(p.Y) < 1e-4f)
            {
                if (s - slack >= p.W - Touch) return false;
                continue;
            }
            float bound = (p.W - s + slack) / p.Y;
            if (p.Y > 0f) { if (bound < hi) { hi = bound; topNy = p.Y; } }
            else if (bound > lo) lo = bound;
        }
        return lo < hi && !float.IsInfinity(hi);
    }

    private bool Connects(float floorA, float ceilA, float floorB, float ceilB) =>
        MathF.Abs(floorA - floorB) <= Agent.StepHeight &&
        MathF.Min(ceilA, ceilB) - MathF.Max(floorA, floorB) >= Agent.Height;

    private int AddSlot()
    {
        _slots.Add(null);
        return _slots.Count - 1;
    }

    private static int Pack(int slot, int span) => (slot << SpanBits) | span;

    private NavTile TileAt(int packed) => _slots[packed >> SpanBits]!;

    // The connected pieces of a tile, by its own links only.
    private void BuildRegions(NavTile tile)
    {
        int count = tile.SpanCount;
        Array.Fill(tile.Region, (short)-1);
        if (_queue.Length < count) _queue = new int[count];
        var centres = new List<Vector3>();
        var sides = new List<byte>();
        for (int seed = 0; seed < count; seed++)
        {
            if (tile.Region[seed] >= 0) continue;
            short region = (short)centres.Count;
            int head = 0, tail = 0;
            _queue[tail++] = seed;
            tile.Region[seed] = region;
            Vector3 sum = Vector3.Zero;
            byte mask = 0;
            while (head < tail)
            {
                int span = _queue[head++];
                int column = tile.Column[span], ci = column % Cells, cj = column / Cells;
                sum += new Vector3((tile.X * Cells + ci + 0.5f) * Cell, tile.Floor[span], (tile.Z * Cells + cj + 0.5f) * Cell);
                if (ci == Cells - 1) mask |= 1;
                if (cj == Cells - 1) mask |= 2;
                if (ci == 0) mask |= 4;
                if (cj == 0) mask |= 8;
                for (int d = 0; d < 4; d++)
                {
                    int link = tile.Links[span * 4 + d];
                    if (link < 0) continue;
                    int next = link & SpanMask;
                    if (tile.Region[next] >= 0) continue;
                    tile.Region[next] = region;
                    _queue[tail++] = next;
                }
            }
            centres.Add(sum / tail);
            sides.Add(mask);
        }
        tile.RegionCount = centres.Count;
        tile.RegionCentre = centres.ToArray();
        tile.RegionSides = sides.ToArray();
    }

    // Links the spans along the edge two tiles share, both ways. `d` is the direction from `a` to `b`.
    private void Stitch(NavTile a, NavTile b, int d)
    {
        StitchOneWay(a, b, d);
        StitchOneWay(b, a, (d + 2) & 3);
    }

    private void StitchOneWay(NavTile from, NavTile to, int d)
    {
        for (int k = 0; k < Cells; k++)
        {
            // The edge column of `from` facing d, and the one of `to` facing back.
            int fi = d == 0 ? Cells - 1 : d == 2 ? 0 : k, fj = d == 1 ? Cells - 1 : d == 3 ? 0 : k;
            int ti = d == 0 ? 0 : d == 2 ? Cells - 1 : k, tj = d == 1 ? 0 : d == 3 ? Cells - 1 : k;
            int fc = fj * Cells + fi, tc = tj * Cells + ti;
            for (int s = from.ColumnStart[fc]; s < from.ColumnStart[fc + 1]; s++)
            {
                int best = -1;
                float bestDy = float.MaxValue;
                for (int t = to.ColumnStart[tc]; t < to.ColumnStart[tc + 1]; t++)
                {
                    if (!Connects(from.Floor[s], from.Ceiling[s], to.Floor[t], to.Ceiling[t])) continue;
                    float dy = MathF.Abs(from.Floor[s] - to.Floor[t]);
                    if (dy < bestDy) { bestDy = dy; best = t; }
                }
                from.Links[s * 4 + d] = best < 0 ? -1 : Pack(to.Slot, best);
            }
        }
    }

    private void Evict()
    {
        while (_tiles.Count > MaxTiles)
        {
            NavTile? oldest = null;
            foreach (var tile in _tiles.Values)
                if (oldest == null || tile.LastUsed < oldest.LastUsed) oldest = tile;
            Remove(oldest!);
        }
    }

    // ---- finding spans --------------------------------------------------------------------------------

    public static Vector3 SpanPosition(NavTile tile, int span)
    {
        int column = tile.Column[span];
        return new Vector3((tile.X * Cells + column % Cells + 0.5f) * Cell, tile.Floor[span],
                           (tile.Z * Cells + column / Cells + 0.5f) * Cell);
    }

    public Vector3 PositionOf(int packed) => SpanPosition(TileAt(packed), packed & SpanMask);

    public int RegionOf(int packed, out int slot)
    {
        slot = packed >> SpanBits;
        return TileAt(packed).Region[packed & SpanMask];
    }

    // The span a body standing at an absolute position is on: in its column, a floor no more than
    // `rise` above its feet and no more than `drop` below them (it may be in the air); failing that, the
    // nearest such span within `reach` cells, because a body pressed against a wall is standing in the
    // strip the erosion took away. -1 when there is none, or the tiles there are not baked.
    public int FindSpan(Vector3 at, float rise = 0.6f, float drop = 2.5f, int reach = 4)
    {
        int gx = (int)MathF.Floor(at.X / Cell), gz = (int)MathF.Floor(at.Z / Cell);
        for (int ring = 0; ring <= reach; ring++)
        {
            int best = -1;
            float bestScore = float.MaxValue;
            for (int dz = -ring; dz <= ring; dz++)
                for (int dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Abs(dx) != ring && Math.Abs(dz) != ring) continue;
                    int x = gx + dx, z = gz + dz;
                    int tx = FloorDiv(x, Cells), tz = FloorDiv(z, Cells);
                    if (Tile(tx, tz) is not { } tile) continue;
                    int column = (z - tz * Cells) * Cells + (x - tx * Cells);
                    for (int s = tile.ColumnStart[column]; s < tile.ColumnStart[column + 1]; s++)
                    {
                        float dy = tile.Floor[s] - at.Y;
                        if (dy > rise || dy < -drop) continue;
                        float score = dx * dx + dz * dz + dy * dy;
                        if (score < bestScore) { bestScore = score; best = Pack(tile.Slot, s); }
                    }
                }
            if (best >= 0) return best;
        }
        return -1;
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    // Every span reachable from `start`, by links: the flood `sage validate` asks "can a body get there".
    // Writes into `reached` the packed spans it got to.
    public void Flood(int start, HashSet<int> reached)
    {
        reached.Clear();
        var open = new Stack<int>();
        open.Push(start);
        reached.Add(start);
        while (open.Count > 0)
        {
            int at = open.Pop();
            var tile = TileAt(at);
            int span = at & SpanMask;
            for (int d = 0; d < 4; d++)
            {
                int next = tile.Links[span * 4 + d];
                if (next >= 0 && reached.Add(next)) open.Push(next);
            }
        }
    }

    // ---- searching ------------------------------------------------------------------------------------

    // The coarse graph's working set: nodes are a baked tile's region or a tile not baked yet.
    private struct CoarseNode
    {
        public int Slot, Region;     // Slot -1: a tile not baked yet, at (Tx, Tz)
        public int Tx, Tz;
        public Vector3 At;
        public float Cost;
        public int Parent;
        public bool Closed;
    }

    private CoarseNode[] _nodes = new CoarseNode[256];
    private int _nodeCount;
    private readonly Dictionary<long, int> _regionNodes = new();
    private readonly Dictionary<(int, int), int> _tileNodes = new();
    private readonly MinHeap _coarseOpen = new();
    private readonly MinHeap _fineOpen = new();
    private readonly List<int> _route = new();
    private readonly List<int> _fine = new();
    private readonly List<int> _neighbours = new();
    private readonly List<float> _neighbourCost = new();
    private int _search;

    // Plans from one absolute position to another. Writes corners (absolute) and returns how many;
    // `CannotAnswer` means the mesh has nothing to say here (no geometry, or an end off it) and the
    // caller should fall back to the local grid.
    public NavMeshAnswer Plan(Vector3 from, Vector3 to, NavGeometry geometry, int maxNodes,
                              Span<Vector3> corners, out int count, out bool reachedGoal) =>
        Plan(from, to, geometry, null, maxNodes, corners, out count, out reachedGoal, out _);

    // The same, with doors and off-mesh links (#265). When the way goes through a door or over a link the
    // corners stop at its far side, and `crossing` says at which corner it starts and what it is: the
    // creature acts on it there and plans again from the far side.
    public NavMeshAnswer Plan(Vector3 from, Vector3 to, NavGeometry geometry, NavCrossings? crossings, int maxNodes,
                              Span<Vector3> corners, out int count, out bool reachedGoal, out NavMeshCrossing crossing)
    {
        count = 0;
        reachedGoal = false;
        crossing = default;
        LastNodes = 0;
        _crossings = null;
        _edges.Clear();
        _doors.Clear();
        if (corners.Length == 0) return NavMeshAnswer.CannotAnswer;

        // The tile it stands in is baked whatever the budget says: without it there is no answer at all,
        // and the grid that would answer instead does not see brushes.
        int sx = TileOf(from.X), sz = TileOf(from.Z);
        TryBake(sx, sz, geometry, unbudgeted: true);
        int start = FindSpan(from);
        if (start < 0) return NavMeshAnswer.CannotAnswer;

        int gx = TileOf(to.X), gz = TileOf(to.Z);
        int goal = -1;
        if (TryBake(gx, gz, geometry))
        {
            goal = FindSpan(to);
            if (goal < 0) return NavMeshAnswer.CannotAnswer;   // standing on something the mesh does not have
        }

        ResolveCrossings(from, to, geometry, crossings);

        // ---- coarse: regions across tiles ----
        _nodeCount = 0;
        _regionNodes.Clear();
        _tileNodes.Clear();
        _coarseOpen.Clear();
        int startRegion = RegionOf(start, out int startSlot);
        int startNode = RegionNode(startSlot, startRegion);
        _nodes[startNode].Cost = 0f;
        _nodes[startNode].Parent = -1;
        _coarseOpen.Push(startNode, XZ(_nodes[startNode].At, to));

        int goalNode;
        if (goal >= 0)
        {
            int goalRegion = RegionOf(goal, out int goalSlot);
            goalNode = RegionNode(goalSlot, goalRegion);
        }
        else goalNode = TileNode(gx, gz);

        int nodes = 0;
        bool found = false;
        while (_coarseOpen.Count > 0 && nodes < maxNodes)
        {
            int current = _coarseOpen.Pop();
            if (_nodes[current].Closed) continue;
            _nodes[current].Closed = true;
            nodes++;
            if (current == goalNode) { found = true; break; }

            Neighbours(current, geometry);
            for (int k = 0; k < _neighbours.Count; k++)
            {
                int next = _neighbours[k];
                if (_nodes[next].Closed) continue;
                float cost = _nodes[current].Cost + _neighbourCost[k];
                if (_nodes[next].Parent != -2 && cost >= _nodes[next].Cost) continue;
                _nodes[next].Cost = cost;
                _nodes[next].Parent = current;
                _coarseOpen.Push(next, cost + XZ(_nodes[next].At, to));
            }
        }
        LastNodes = nodes;
        if (!found) return NavMeshAnswer.NoRoute;

        _route.Clear();
        for (int at = goalNode; at != -1; at = _nodes[at].Parent) _route.Add(at);
        _route.Reverse();

        // ---- the corridor: the first baked regions of the route ----
        int last = 0;
        while (last + 1 < _route.Count && last + 1 < MaxCorridor && _nodes[_route[last + 1]].Slot >= 0) last++;
        bool corridorHasGoal = goal >= 0 && _route[last] == goalNode;
        int exitNode = corridorHasGoal ? -1 : _route[last + 1];
        Vector3 aim = corridorHasGoal ? to : ExitPoint(_route[last], exitNode);

        // ---- fine: spans inside the corridor ----
        _search++;
        _fineOpen.Clear();
        var startTile = TileAt(start);
        int startSpan = start & SpanMask;
        startTile.Visited[startSpan] = _search;
        startTile.Cost[startSpan] = 0f;
        startTile.From[startSpan] = -1;
        startTile.LastUsed = ++_useClock;
        _fineOpen.Push(start, XYZ(PositionOf(start), aim));

        int end = -1;
        while (_fineOpen.Count > 0 && nodes < maxNodes)
        {
            int current = _fineOpen.Pop();
            var tile = TileAt(current);
            int span = current & SpanMask;
            if (tile.Visited[span] == -_search) continue;     // closed
            tile.Visited[span] = -_search;
            nodes++;

            if (corridorHasGoal ? current == goal : IsExit(current, _route[last], exitNode)) { end = current; break; }

            for (int dir = 0; dir < 8; dir++)
                Relax(current, Step(current, dir), tile.Cost[span] + (dir < 4 ? Cell : Cell * 1.41421356f), last, aim);

            // And over any link that starts here (#265).
            foreach (var edge in _edges)
                if (edge.From == current) Relax(current, edge.To, tile.Cost[span] + edge.Cost, last, aim);
        }
        LastNodes = nodes;
        if (end < 0)
        {
            // Out of budget, or the corridor is not joined up the way the regions said (it always is: a
            // region is connected by construction). Either way there is no answer this time.
            return NavMeshAnswer.NoRoute;
        }

        _fine.Clear();
        for (int at = end; at != -1; at = TileAt(at).From[at & SpanMask]) _fine.Add(at);
        _fine.Reverse();

        if (FirstCrossing(out int before, out int after, out crossing))
        {
            // Walk up to it, then its far side, and no further: the creature acts on it and plans again.
            count = Straighten(corners[..^1], before, out bool reachedIt);
            if (!reachedIt)
            {
                crossing = default;   // the corners ran out first: the crossing is for a later plan
                return NavMeshAnswer.Found;
            }
            if (crossing.Kind == NavCrossing.Door)
            {
                crossing.Corner = count - 1;
                corners[count++] = PositionOf(_fine[after]);
            }
            else
            {
                var edge = _edges[crossing.Index];
                corners[count - 1] = edge.FromAt;
                crossing.Corner = count - 1;
                crossing.Index = edge.Link;
                corners[count++] = edge.ToAt;
            }
            return NavMeshAnswer.Found;
        }

        count = Straighten(corners, _fine.Count - 1, out bool whole);
        if (corridorHasGoal && whole)
        {
            // The goal is what was asked for, not the middle of its cell.
            corners[count - 1] = to;
            reachedGoal = true;
        }
        return NavMeshAnswer.Found;
    }

    // ---- doors and links (#265) -----------------------------------------------------------------------

    // A link as the search sees it: from one span to another, for what it costs, and where its ends are.
    private struct LinkEdge
    {
        public int From, To;
        public int Link;               // index in NavCrossings.Links
        public Vector3 FromAt, ToAt;
        public float Cost;
    }

    private readonly List<LinkEdge> _edges = new();
    private readonly List<int> _doors = new();     // the doors near this plan, indices in NavCrossings.Doors
    private NavCrossings? _crossings;

    // How far from the two ends of a plan a door or a link is looked at, in metres. A plan only ever
    // covers the next few tiles exactly; anything further is for a later plan.
    private const float CrossingReach = 48f;

    // What walking under a closed door costs over walking under an open one, in metres: enough to take an
    // open way of about the same length, not so much that it walks round the building.
    private const float ClosedDoorCost = 2f;

    // The links near the plan as edges between spans, and the doors near it. A link whose ends are on
    // tiles that cannot be baked this tick is left out of this plan, not waited for.
    private void ResolveCrossings(Vector3 from, Vector3 to, NavGeometry geometry, NavCrossings? crossings)
    {
        _crossings = crossings;
        if (crossings == null) return;
        var min = Vector3.Min(from, to) - new Vector3(CrossingReach);
        var max = Vector3.Max(from, to) + new Vector3(CrossingReach);

        for (int i = 0; i < crossings.Links.Count; i++)
        {
            var link = crossings.Links[i];
            if (!Near(link.Start, min, max) && !Near(link.End, min, max)) continue;
            if (!TryBake(TileOf(link.Start.X), TileOf(link.Start.Z), geometry) ||
                !TryBake(TileOf(link.End.X), TileOf(link.End.Z), geometry)) continue;
            int a = FindSpan(link.Start), b = FindSpan(link.End);
            if (a < 0 || b < 0 || a == b) continue;
            float cost = Vector3.Distance(link.Start, link.End) + link.Cost;
            _edges.Add(new LinkEdge { From = a, To = b, Link = i, FromAt = link.Start, ToAt = link.End, Cost = cost });
            if (link.TwoWay)
                _edges.Add(new LinkEdge { From = b, To = a, Link = i, FromAt = link.End, ToAt = link.Start, Cost = cost });
        }

        for (int i = 0; i < crossings.Doors.Count; i++)
        {
            var door = crossings.Doors[i];
            if (door.Max.X < min.X || door.Min.X > max.X || door.Max.Z < min.Z || door.Min.Z > max.Z) continue;
            _doors.Add(i);
        }
    }

    private static bool Near(Vector3 p, Vector3 min, Vector3 max) =>
        p.X >= min.X && p.X <= max.X && p.Z >= min.Z && p.Z <= max.Z;

    // The door whose closed footprint a span is under, grown by the body's radius (a body in the doorway
    // is in the door's way), or -1.
    private int DoorAt(int packed)
    {
        if (_doors.Count == 0) return -1;
        var p = PositionOf(packed);
        float r = Agent.Radius;
        foreach (int i in _doors)
        {
            var door = _crossings!.Doors[i];
            if (p.X < door.Min.X - r || p.X > door.Max.X + r || p.Z < door.Min.Z - r || p.Z > door.Max.Z + r) continue;
            if (p.Y < door.Min.Y - Agent.StepHeight || p.Y > door.Max.Y) continue;
            return i;
        }
        return -1;
    }

    // One step of the fine search, from `current` to `next` for a total of `cost` so far.
    private void Relax(int current, int next, float cost, int last, Vector3 aim)
    {
        if (next < 0 || !InCorridor(next, last)) return;
        var nextTile = TileAt(next);
        int nextSpan = next & SpanMask;
        if (nextTile.Visited[nextSpan] == -_search) return;
        int door = DoorAt(next);
        if (door >= 0 && !_crossings!.Doors[door].Open) cost += ClosedDoorCost * Cell;
        if (nextTile.Visited[nextSpan] == _search && cost >= nextTile.Cost[nextSpan]) return;
        nextTile.Visited[nextSpan] = _search;
        nextTile.Cost[nextSpan] = cost;
        nextTile.From[nextSpan] = current;
        nextTile.LastUsed = _useClock;
        _fineOpen.Push(next, cost + XYZ(PositionOf(next), aim));
    }

    // The first door or link along the fine path: `before` is the last span short of it, `after` the
    // first beyond it (a door's: the first span out from under it).
    private bool FirstCrossing(out int before, out int after, out NavMeshCrossing crossing)
    {
        before = after = -1;
        crossing = default;
        if (_edges.Count == 0 && _doors.Count == 0) return false;

        for (int k = 0; k < _fine.Count; k++)
        {
            int door = DoorAt(_fine[k]);
            if (door >= 0)
            {
                before = Math.Max(k - 1, 0);
                after = k;
                while (after + 1 < _fine.Count && DoorAt(_fine[after]) == door && !IsLinkStep(after)) after++;
                crossing = new NavMeshCrossing { Kind = NavCrossing.Door, Index = door };
                return true;
            }
            if (k + 1 < _fine.Count && LinkStep(k) is int edge and >= 0)
            {
                before = k;
                after = k + 1;
                var link = _crossings!.Links[_edges[edge].Link];
                crossing = new NavMeshCrossing { Kind = NavCrossings.Of(link.Kind), Index = edge };
                return true;
            }
        }
        return false;
    }

    private bool IsLinkStep(int k) => k + 1 < _fine.Count && LinkStep(k) >= 0;

    // The edge the fine path took from `_fine[k]` to `_fine[k + 1]`, or -1 when that was a step.
    private int LinkStep(int k)
    {
        for (int e = 0; e < _edges.Count; e++)
            if (_edges[e].From == _fine[k] && _edges[e].To == _fine[k + 1]) return e;
        return -1;
    }

    private static float XZ(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static float XYZ(Vector3 a, Vector3 b) => Vector3.Distance(a, b);

    private int RegionNode(int slot, int region)
    {
        long key = ((long)slot << 16) | (uint)region;
        if (_regionNodes.TryGetValue(key, out int node)) return node;
        var tile = _slots[slot]!;
        node = NewNode(slot, region, tile.X, tile.Z, tile.RegionCentre[region]);
        _regionNodes[key] = node;
        return node;
    }

    private int TileNode(int tx, int tz)
    {
        if (_tileNodes.TryGetValue((tx, tz), out int node)) return node;
        node = NewNode(-1, -1, tx, tz, new Vector3((tx + 0.5f) * TileSize, 0f, (tz + 0.5f) * TileSize));
        _tileNodes[(tx, tz)] = node;
        return node;
    }

    private int NewNode(int slot, int region, int tx, int tz, Vector3 at)
    {
        if (_nodeCount == _nodes.Length) Array.Resize(ref _nodes, _nodes.Length * 2);
        _nodes[_nodeCount] = new CoarseNode { Slot = slot, Region = region, Tx = tx, Tz = tz, At = at, Parent = -2, Cost = float.MaxValue };
        return _nodeCount++;
    }

    // A coarse node's neighbours into `_neighbours`. A tile not baked yet is baked here if the tick's
    // budget allows, and is one optimistic node if it does not.
    private void Neighbours(int node, NavGeometry geometry)
    {
        _neighbours.Clear();
        _neighbourCost.Clear();
        var n = _nodes[node];
        for (int d = 0; d < 4; d++)
        {
            int back = (d + 2) & 3;
            NavTile? tile = n.Slot >= 0 ? _slots[n.Slot] : null;
            if (tile != null && (tile.RegionSides[n.Region] & (1 << d)) == 0) continue;
            int nx = n.Tx + Dx[d], nz = n.Tz + Dz[d];
            if (!TryBake(nx, nz, geometry)) { AddNeighbour(TileNode(nx, nz), node); continue; }
            var other = Tile(nx, nz)!;
            other.LastUsed = ++_useClock;

            if (tile == null)
            {
                // From the unknown: any region of the neighbour on the shared edge.
                for (int r = 0; r < other.RegionCount; r++)
                    if ((other.RegionSides[r] & (1 << back)) != 0) AddNeighbour(RegionNode(other.Slot, r), node);
                continue;
            }

            // From a region: the regions its edge spans actually link to.
            for (int k = 0; k < Cells; k++)
            {
                int ci = d == 0 ? Cells - 1 : d == 2 ? 0 : k, cj = d == 1 ? Cells - 1 : d == 3 ? 0 : k;
                int column = cj * Cells + ci;
                for (int s = tile.ColumnStart[column]; s < tile.ColumnStart[column + 1]; s++)
                {
                    if (tile.Region[s] != n.Region) continue;
                    int link = tile.Links[s * 4 + d];
                    if (link < 0) continue;
                    AddNeighbour(RegionNode(other.Slot, other.Region[link & SpanMask]), node);
                }
            }
        }

        // The regions a link from this one lands in (#265), for what walking to it and over it costs.
        if (n.Slot < 0) return;
        foreach (var edge in _edges)
        {
            if ((edge.From >> SpanBits) != n.Slot || _slots[n.Slot]!.Region[edge.From & SpanMask] != n.Region) continue;
            int target = RegionNode(edge.To >> SpanBits, TileAt(edge.To).Region[edge.To & SpanMask]);
            if (target == node) continue;
            AddNeighbour(target, node, XZ(n.At, edge.FromAt) + edge.Cost + XZ(edge.ToAt, _nodes[target].At));
        }
    }

    private void AddNeighbour(int next, int from, float cost = -1f)
    {
        if (cost < 0f) cost = XZ(_nodes[from].At, _nodes[next].At);
        int i = _neighbours.IndexOf(next);
        if (i >= 0) { _neighbourCost[i] = MathF.Min(_neighbourCost[i], cost); return; }
        _neighbours.Add(next);
        _neighbourCost.Add(cost);
    }

    private bool InCorridor(int packed, int last)
    {
        int slot = packed >> SpanBits;
        int region = _slots[slot]!.Region[packed & SpanMask];
        for (int i = 0; i <= last; i++)
            if (_nodes[_route[i]].Slot == slot && _nodes[_route[i]].Region == region) return true;
        return false;
    }

    // Is this span a way out of the corridor's last region into the next node of the route?
    private bool IsExit(int packed, int lastNode, int exitNode)
    {
        var tile = TileAt(packed);
        int span = packed & SpanMask;
        var last = _nodes[lastNode];
        if (tile.Slot != last.Slot || tile.Region[span] != last.Region) return false;
        var exit = _nodes[exitNode];
        if (exit.Slot >= 0)
            foreach (var edge in _edges)
                if (edge.From == packed && (edge.To >> SpanBits) == exit.Slot && TileAt(edge.To).Region[edge.To & SpanMask] == exit.Region) return true;
        for (int d = 0; d < 4; d++)
        {
            if (exit.Slot >= 0)
            {
                int link = tile.Links[span * 4 + d];
                if (link >= 0 && (link >> SpanBits) == exit.Slot && _slots[exit.Slot]!.Region[link & SpanMask] == exit.Region) return true;
            }
            else if (tile.X + Dx[d] == exit.Tx && tile.Z + Dz[d] == exit.Tz)
            {
                int column = tile.Column[span], ci = column % Cells, cj = column / Cells;
                bool onEdge = d switch { 0 => ci == Cells - 1, 1 => cj == Cells - 1, 2 => ci == 0, _ => cj == 0 };
                if (onEdge) return true;
            }
        }
        return false;
    }

    // Where the fine search aims when the corridor ends before the goal: the middle of the spans that
    // lead out of it, which is the doorway between this region and the next.
    private Vector3 ExitPoint(int lastNode, int exitNode)
    {
        var last = _nodes[lastNode];
        var tile = _slots[last.Slot]!;
        Vector3 sum = Vector3.Zero;
        int n = 0;
        for (int s = 0; s < tile.SpanCount; s++)
        {
            if (tile.Region[s] != last.Region) continue;
            if (!IsExit(Pack(tile.Slot, s), lastNode, exitNode)) continue;
            sum += SpanPosition(tile, s);
            n++;
        }
        return n > 0 ? sum / n : _nodes[exitNode].At;
    }

    // One step from a span: 0-3 along the axes, 4-7 diagonally — and a diagonal only where both ways
    // round its corner are open and arrive at the same span, so a path never cuts a wall's corner.
    private int Step(int packed, int dir)
    {
        if (dir < 4) return TileAt(packed).Links[(packed & SpanMask) * 4 + dir];
        int a = dir - 4, b = (a + 1) & 3;           // (+X,+Z) (+Z,-X) (-X,-Z) (-Z,+X)
        int viaA = Link(packed, a), viaB = Link(packed, b);
        if (viaA < 0 || viaB < 0) return -1;
        int ab = Link(viaA, b), ba = Link(viaB, a);
        return ab >= 0 && ab == ba ? ab : -1;
    }

    private int Link(int packed, int d) => TileAt(packed).Links[(packed & SpanMask) * 4 + d];

    // Can a body walk straight from one span to another over the mesh? Steps the cells the line crosses,
    // following links, and a diagonal step only where both ways round agree (as `Step`).
    public bool LineIsClear(int from, int to)
    {
        var a = PositionOf(from);
        var b = PositionOf(to);
        int x = (int)MathF.Floor(a.X / Cell), z = (int)MathF.Floor(a.Z / Cell);
        int x1 = (int)MathF.Floor(b.X / Cell), z1 = (int)MathF.Floor(b.Z / Cell);
        int dx = Math.Abs(x1 - x), dz = Math.Abs(z1 - z);
        int sx = x < x1 ? 1 : -1, sz = z < z1 ? 1 : -1;
        int dirX = sx > 0 ? 0 : 2, dirZ = sz > 0 ? 1 : 3;
        int error = dx - dz;
        int at = from;
        while (x != x1 || z != z1)
        {
            int e2 = error * 2;
            bool stepX = e2 > -dz, stepZ = e2 < dx;
            if (stepX && stepZ)
            {
                int viaX = Link(at, dirX), viaZ = Link(at, dirZ);
                if (viaX < 0 || viaZ < 0) return false;
                int xz = Link(viaX, dirZ), zx = Link(viaZ, dirX);
                if (xz < 0 || xz != zx) return false;
                at = xz;
                error += dx - dz;
                x += sx;
                z += sz;
            }
            else if (stepX)
            {
                at = Link(at, dirX);
                if (at < 0) return false;
                error -= dz;
                x += sx;
            }
            else
            {
                at = Link(at, dirZ);
                if (at < 0) return false;
                error += dx;
                z += sz;
            }
        }
        return at == to;
    }

    // The fine path's corners: keep a span only where the line from the last corner to the span after it
    // stops being clear (NavGrid.Straighten's rule), then the path's last span. `whole` is false when the
    // corners ran out first: the last one is then a real corner, not the end, and every leg is still a
    // straight walk — the creature plans again from there.
    //
    // `end` is the last span of the path to straighten (a crossing's near side stops it short).
    private int Straighten(Span<Vector3> corners, int end, out bool whole)
    {
        int written = 0;
        int anchor = _fine[0];
        for (int i = 1; i < end; i++)
        {
            if (LineIsClear(anchor, _fine[i + 1])) continue;
            corners[written++] = PositionOf(_fine[i]);
            anchor = _fine[i];
            if (written == corners.Length) { whole = false; return written; }
        }
        corners[written++] = PositionOf(_fine[end]);
        whole = true;
        return written;
    }

    // A binary heap of (item, key), kept and reused so a search allocates nothing once warm.
    private sealed class MinHeap
    {
        private int[] _items = new int[256];
        private float[] _keys = new float[256];
        public int Count { get; private set; }

        public void Clear() => Count = 0;

        public void Push(int item, float key)
        {
            if (Count == _items.Length)
            {
                Array.Resize(ref _items, Count * 2);
                Array.Resize(ref _keys, Count * 2);
            }
            int i = Count++;
            _items[i] = item;
            _keys[i] = key;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (_keys[parent] <= _keys[i]) break;
                (_items[parent], _items[i]) = (_items[i], _items[parent]);
                (_keys[parent], _keys[i]) = (_keys[i], _keys[parent]);
                i = parent;
            }
        }

        public int Pop()
        {
            int best = _items[0];
            Count--;
            if (Count > 0)
            {
                _items[0] = _items[Count];
                _keys[0] = _keys[Count];
                int i = 0;
                while (true)
                {
                    int left = i * 2 + 1, right = left + 1, smallest = i;
                    if (left < Count && _keys[left] < _keys[smallest]) smallest = left;
                    if (right < Count && _keys[right] < _keys[smallest]) smallest = right;
                    if (smallest == i) break;
                    (_items[smallest], _items[i]) = (_items[i], _items[smallest]);
                    (_keys[smallest], _keys[i]) = (_keys[i], _keys[smallest]);
                    i = smallest;
                }
            }
            return best;
        }
    }
}
