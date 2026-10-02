#nullable enable
using System;
using System.Numerics;

namespace Sage.Gameplay;

// Finding a way round things (docs/design/16 §3.4, TODO F23).
//
// **A navmesh first, a local grid when it has nothing to say.** The navmesh (`NavMesh`, issue #264) is
// baked from brush floors, terrain and static colliders, tile by tile as creatures need it, with a coarse
// graph of regions for crossing a level or a sector border; it is what sees an interior built out of
// brushes, which the grid below never did (a brush is a hull in physics, not a `Collider`).
//
// **The local grid, built when it is needed and thrown away,** is the fallback: where there is no mesh
// (no terrain, no level, no static collider under the creature or its goal), or with `nav_mesh 0`. It is
// a window around the agent and its goal, stamped from the colliders that are there *now* — which means
// a crate that has just been pushed into a doorway is in the path, and nothing has to be rebuilt when the
// world changes.
//
// Everything here is in **origin space** (R6), like every other position, so a rebase needs no special
// case: a grid is built, searched and forgotten inside one tick.
//
// The grid decides nothing about *who* walks: it answers "is this cell blocked" and "what is the way
// from here to there". The AI task that follows the answer is `MoveToTargetTask` (AI.cs).
public sealed class NavGrid
{
    // A window of 96 cells a side is 96 m at the default cell size: far enough to get round a building,
    // small enough that a search is a fraction of a millisecond and the whole grid is 1.1 KB of bits.
    public const int MaxSide = 96;

    private const int MaxCells = MaxSide * MaxSide;

    private readonly ulong[] _blocked = new ulong[(MaxCells + 63) / 64];

    // A* working set. Stamped with a search number rather than cleared, so a search costs nothing to
    // start: the steady-state allocation rule (02 §4.6) applies to pathfinding like everything else.
    private readonly float[] _cost = new float[MaxCells];
    private readonly int[] _from = new int[MaxCells];
    private readonly int[] _visited = new int[MaxCells];
    private readonly int[] _heap = new int[MaxCells];
    private readonly float[] _heapKey = new float[MaxCells];
    private int _heapCount;
    private int _search;

    public float CellSize { get; private set; } = 1f;

    // Cell (0,0)'s centre, in origin space. Y is the height the grid was built at; nothing here is 3D.
    public Vector3 Corner { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    // What the last search cost, for `nav_stats`: the first question when the AI starts to stutter.
    public int LastNodes { get; private set; }

    public int Searches { get; private set; }

    public int Failures { get; private set; }

    public void ResetStats() { Searches = 0; Failures = 0; }

    // Opens a window big enough for both ends, clamped to `MaxSide`. Both points are origin space; the
    // window is padded so a path may bulge sideways round an obstacle rather than being boxed in by the
    // straight line between them.
    public void Open(Vector3 from, Vector3 to, float cellSize, float padding = 8f)
    {
        CellSize = MathF.Max(cellSize, 0.1f);
        float minX = MathF.Min(from.X, to.X) - padding, maxX = MathF.Max(from.X, to.X) + padding;
        float minZ = MathF.Min(from.Z, to.Z) - padding, maxZ = MathF.Max(from.Z, to.Z) + padding;

        Width = Math.Clamp((int)MathF.Ceiling((maxX - minX) / CellSize) + 1, 1, MaxSide);
        Height = Math.Clamp((int)MathF.Ceiling((maxZ - minZ) / CellSize) + 1, 1, MaxSide);

        // Centre the window on the two points, so clamping to MaxSide loses the far edges rather than
        // one whole side of the search.
        float midX = (minX + maxX) * 0.5f, midZ = (minZ + maxZ) * 0.5f;
        Corner = new Vector3(midX - (Width - 1) * 0.5f * CellSize,
                             (from.Y + to.Y) * 0.5f,
                             midZ - (Height - 1) * 0.5f * CellSize);
        Array.Clear(_blocked);
    }

    public Vector3 CellCentre(int x, int y) =>
        new(Corner.X + x * CellSize, Corner.Y, Corner.Z + y * CellSize);

    public bool Contains(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

    // The cell a position falls in. Outside the window the coordinates are clamped, because a caller
    // asking about somewhere the grid does not cover wants the nearest edge, not an exception.
    public void CellOf(Vector3 position, out int x, out int y)
    {
        x = Math.Clamp((int)MathF.Round((position.X - Corner.X) / CellSize), 0, Width - 1);
        y = Math.Clamp((int)MathF.Round((position.Z - Corner.Z) / CellSize), 0, Height - 1);
    }

    public bool IsBlocked(int x, int y) => !Contains(x, y) || (_blocked[Index(x, y) >> 6] & Bit(Index(x, y))) != 0;

    public void Block(int x, int y)
    {
        if (!Contains(x, y)) return;
        int i = Index(x, y);
        _blocked[i >> 6] |= Bit(i);
    }

    // Blocks every cell an axis-aligned box covers, grown by `grow` — the agent's radius, so a path is
    // one a body can actually take rather than one that clips the corner of every wall.
    public void BlockBox(Vector3 min, Vector3 max, float grow)
    {
        int x0 = (int)MathF.Floor((min.X - grow - Corner.X) / CellSize + 0.5f);
        int x1 = (int)MathF.Ceiling((max.X + grow - Corner.X) / CellSize - 0.5f);
        int y0 = (int)MathF.Floor((min.Z - grow - Corner.Z) / CellSize + 0.5f);
        int y1 = (int)MathF.Ceiling((max.Z + grow - Corner.Z) / CellSize - 0.5f);

        for (int y = Math.Max(y0, 0); y <= Math.Min(y1, Height - 1); y++)
            for (int x = Math.Max(x0, 0); x <= Math.Min(x1, Width - 1); x++)
                Block(x, y);
    }

    public int BlockedCells()
    {
        int n = 0;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (IsBlocked(x, y)) n++;
        return n;
    }

    // ---- the search ----------------------------------------------------------------------------------

    // A* over the grid, eight neighbours, then straightened. Writes the corners to turn at into
    // `corners` (the goal last) and returns how many, or 0 when there is no way through.
    //
    // `maxNodes` is a budget: a creature in a sealed room must cost one bounded search and then give up,
    // rather than a tick spike every time it thinks. A search that runs out of budget fails, and the
    // task that asked walks straight at the target as it always did.
    public int Search(Vector3 from, Vector3 to, Span<Vector3> corners, int maxNodes = 4096)
    {
        Searches++;
        LastNodes = 0;
        if (corners.Length == 0) return 0;

        CellOf(from, out int sx, out int sy);
        CellOf(to, out int gx, out int gy);

        // Standing inside something (a corpse, a crate pushed onto you) is not a reason to refuse to
        // move: the cell you are in is walkable by definition, whatever the stamp says.
        int start = Index(sx, sy), goal = Index(gx, gy);
        if (start == goal) { corners[0] = to; return 1; }

        // A goal inside an obstacle (a target standing in a doorway's wall, or on a crate) is answered
        // with the nearest cell that is not, so a creature still walks up to it.
        if (IsBlocked(gx, gy) && !NearestOpen(ref gx, ref gy)) { Failures++; return 0; }
        goal = Index(gx, gy);

        _search++;
        _heapCount = 0;
        _visited[start] = _search;
        _cost[start] = 0f;
        _from[start] = -1;
        Push(start, Heuristic(sx, sy, gx, gy));

        bool found = false;
        while (_heapCount > 0 && LastNodes < maxNodes)
        {
            int current = Pop();
            LastNodes++;
            if (current == goal) { found = true; break; }

            int cx = current % Width, cy = current / Width;
            for (int dir = 0; dir < 8; dir++)
            {
                int nx = cx + DirX[dir], ny = cy + DirY[dir];
                if (!Contains(nx, ny) || IsBlocked(nx, ny)) continue;

                // No cutting corners: a diagonal between two blocked cells is a gap a shoulder does not
                // fit through, and a path that uses one looks like the creature walked through a wall.
                if (DirX[dir] != 0 && DirY[dir] != 0 && (IsBlocked(cx + DirX[dir], cy) || IsBlocked(cx, cy + DirY[dir])))
                    continue;

                int next = Index(nx, ny);
                float step = DirX[dir] != 0 && DirY[dir] != 0 ? 1.41421356f : 1f;
                float cost = _cost[current] + step;
                if (_visited[next] == _search && cost >= _cost[next]) continue;

                _visited[next] = _search;
                _cost[next] = cost;
                _from[next] = current;
                Push(next, cost + Heuristic(nx, ny, gx, gy));
            }
        }

        if (!found) { Failures++; return 0; }
        return Straighten(start, goal, to, corners);
    }

    // Is there a clear line between two points on the grid? Also what the *caller* asks before paying
    // for a search at all: walking straight at something is the right answer most of the time.
    public bool LineIsClear(Vector3 from, Vector3 to)
    {
        CellOf(from, out int x0, out int y0);
        CellOf(to, out int x1, out int y1);
        return LineIsClear(x0, y0, x1, y1);
    }

    // ---- the plumbing -------------------------------------------------------------------------------

    private static readonly int[] DirX = { 1, -1, 0, 0, 1, 1, -1, -1 };
    private static readonly int[] DirY = { 0, 0, 1, -1, 1, -1, 1, -1 };

    private int Index(int x, int y) => y * Width + x;

    private static ulong Bit(int index) => 1UL << (index & 63);

    // Octile distance: the true cost of moving on eight neighbours, so the search stays admissible and
    // does not wander.
    private static float Heuristic(int x0, int y0, int x1, int y1)
    {
        int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
        return (dx + dy) + (1.41421356f - 2f) * Math.Min(dx, dy);
    }

    // A blocked goal takes the nearest open cell, spiralling out a few rings. Bounded on purpose: if
    // nothing within three metres is open, the goal really is unreachable and the search should say so.
    private bool NearestOpen(ref int gx, ref int gy)
    {
        for (int ring = 1; ring <= 3; ring++)
            for (int dy = -ring; dy <= ring; dy++)
                for (int dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Abs(dx) != ring && Math.Abs(dy) != ring) continue;   // the ring, not the disc
                    int x = gx + dx, y = gy + dy;
                    if (!Contains(x, y) || IsBlocked(x, y)) continue;
                    gx = x;
                    gy = y;
                    return true;
                }
        return false;
    }

    // Grid paths are staircases; a creature following one walks like a rook. This keeps only the corners
    // where the way ahead actually stops being clear, which is what makes the movement look deliberate.
    private int Straighten(int start, int goal, Vector3 goalPosition, Span<Vector3> corners)
    {
        // Walk back from the goal, collecting cells into the scratch heap array (it has done its job).
        int count = 0;
        for (int at = goal; at != -1 && count < MaxCells; at = _from[at]) _heap[count++] = at;

        // Then forward, keeping a cell only when the line from the last kept corner to the *next* cell
        // is blocked — the last cell that was still visible is a corner.
        int written = 0;
        int anchor = start;
        for (int i = count - 2; i >= 0 && written < corners.Length; i--)
        {
            int cell = _heap[i];
            if (i == 0) break;                      // the goal is written after the loop, as itself
            int next = _heap[i - 1];
            if (LineIsClear(anchor % Width, anchor / Width, next % Width, next / Width)) continue;
            corners[written++] = CellCentre(cell % Width, cell / Width);
            anchor = cell;
        }

        // The goal is the real position asked for, not the centre of its cell: a creature should walk to
        // what it is chasing, not to the middle of the square it is standing in.
        if (written < corners.Length) corners[written++] = goalPosition;
        return written;
    }

    // Supercover-ish line check: every cell the segment passes through must be open, including the ones
    // it only clips, or a "clear" line can pass through the corner of a wall.
    private bool LineIsClear(int x0, int y0, int x1, int y1)
    {
        int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int error = dx - dy;
        int x = x0, y = y0;

        while (true)
        {
            if (IsBlocked(x, y)) return false;
            if (x == x1 && y == y1) return true;

            int e2 = error * 2;
            bool stepX = e2 > -dy, stepY = e2 < dx;
            if (stepX && stepY && (IsBlocked(x + sx, y) || IsBlocked(x, y + sy))) return false;
            if (stepX) { error -= dy; x += sx; }
            if (stepY) { error += dx; y += sy; }
        }
    }

    private void Push(int cell, float key)
    {
        int i = _heapCount++;
        _heap[i] = cell;
        _heapKey[i] = key;
        while (i > 0)
        {
            int parent = (i - 1) / 2;
            if (_heapKey[parent] <= _heapKey[i]) break;
            (_heap[parent], _heap[i]) = (_heap[i], _heap[parent]);
            (_heapKey[parent], _heapKey[i]) = (_heapKey[i], _heapKey[parent]);
            i = parent;
        }
    }

    private int Pop()
    {
        int best = _heap[0];
        _heapCount--;
        if (_heapCount > 0)
        {
            _heap[0] = _heap[_heapCount];
            _heapKey[0] = _heapKey[_heapCount];
            int i = 0;
            while (true)
            {
                int left = i * 2 + 1, right = left + 1, smallest = i;
                if (left < _heapCount && _heapKey[left] < _heapKey[smallest]) smallest = left;
                if (right < _heapCount && _heapKey[right] < _heapKey[smallest]) smallest = right;
                if (smallest == i) break;
                (_heap[smallest], _heap[i]) = (_heap[i], _heap[smallest]);
                (_heapKey[smallest], _heapKey[i]) = (_heapKey[i], _heapKey[smallest]);
                i = smallest;
            }
        }
        return best;
    }
}

// What a creature is currently following (16 §3.4, F23).
//
// **Corners, not cells**, and only a few of them: the path is re-planned as the target moves, so six
// corners is further ahead than a creature ever usefully commits to. Keeping them here rather than in a
// pool means the path dies with the entity — the lesson of the campfire that hummed after it was
// destroyed (11 §3). All of it is transient: a save restores a creature standing still, thinking again.
// It is carried *inside* `AIState` rather than being a component of its own: a path is part of what the
// brain is doing, every thinking creature can have one, and no prefab has to remember to ask for it.
public struct NavPath
{
    public const int MaxCorners = 6;

    public Vector3 C0, C1, C2, C3, C4, C5;
    public byte Count;          // corners in use
    public byte Step;           // which one it is walking to
    public float ReplanIn;      // seconds until it is worth asking again
    public Vector3 PlannedFor;  // where the target was when this was planned
    public bool NoWayThrough;   // the last search failed: back off and try again later
    // "Is the way clear?" costs three raycasts, and the answer does not change sixty times a second.
    // Asked at roughly the rate a creature thinks and remembered in between (16 §3.4).
    public float CheckIn;
    public bool LineBlocked;

    public Vector3 this[int i] => i switch
    {
        0 => C0, 1 => C1, 2 => C2, 3 => C3, 4 => C4, _ => C5,
    };

    public void Set(int i, Vector3 corner)
    {
        switch (i)
        {
            case 0: C0 = corner; break;
            case 1: C1 = corner; break;
            case 2: C2 = corner; break;
            case 3: C3 = corner; break;
            case 4: C4 = corner; break;
            default: C5 = corner; break;
        }
    }

    // What the path asks of the creature on the way (#265): at corner `CrossingCorner` there is a door to
    // open or a link to cross, and the corner after it is the far side. The path ends there; the creature
    // plans again once it is across.
    internal NavCrossing Crossing;
    internal byte CrossingCorner;
    internal Entity CrossingEntity;   // the door, or the link's entity
    internal float CrossingTime;      // seconds since it reached the near side
    internal bool CrossingStarted;    // the door asked to open, the jump launched
    internal int Version;             // Navigation.Version it was planned at: stale when that moves on

    // Crowds and areas (#271): seconds spent barely moving among other creatures (Crowd.Steer), and
    // whether the straight line to the goal crosses ground closed to it.
    internal float Stuck;
    internal bool LineForbidden;
    internal Vector3 Edge;            // where the closed ground starts, along the line (origin space)

    public void Clear()
    {
        Count = 0;
        Step = 0;
        Crossing = NavCrossing.None;
        CrossingEntity = default;
        CrossingTime = 0f;
        CrossingStarted = false;
    }

    public bool Walking => Step < Count;

    // At the near side of a crossing and on the way over it.
    internal bool InCrossing => Crossing != NavCrossing.None && Step == CrossingCorner + 1 && Step < Count;

    public Vector3 Next => this[Step];

    // Everything here is a position in origin space, so it all moves when the origin does (R6). Without
    // this a creature a kilometre and a half from where it started walks at a corner that is now 1024 m
    // away — the same mistake the renderer, the physics space and the audio mixer each had to be told
    // about, and the reason `Origin.Rebased` exists.
    public void Rebase(Vector3 offset)
    {
        for (int i = 0; i < Count; i++) Set(i, this[i] + offset);
        PlannedFor += offset;
        Edge += offset;
    }
}

// The world's navigation: the navmesh and what it is baked from, one scratch grid, the budget that stops
// a hundred creatures all planning in the same tick, and the numbers `nav_stats` prints (16 §3.4, F23,
// #264).
public sealed class Navigation
{
    private readonly Entity[] _nearby = new Entity[256];

    public NavGrid Grid { get; } = new();

    // The body sizes a navmesh is baked for (#271), smallest first; each has its own mesh, baked as its
    // creatures need it. A body takes the smallest class it fits in (radius and height), so a rat goes
    // through a vent a person does not, and an ogre does not try a door a person fits through. The middle
    // one is the engine's default movement profile, and the only one baked unless a body asks for another.
    internal static readonly NavMeshAgent[] SizeClasses =
    {
        new(0.25f, 1.0f, NavMeshAgent.Default.StepHeight, NavMeshAgent.Default.MaxSlopeDegrees),   // small: a dog, a rat
        NavMeshAgent.Default,                                                                       // a person
        new(0.8f, 3.0f, NavMeshAgent.Default.StepHeight, NavMeshAgent.Default.MaxSlopeDegrees),    // large: an ogre, a horse
    };

    internal const int PersonClass = 1;

    private readonly NavMesh?[] _meshes = new NavMesh?[SizeClasses.Length];

    public Navigation()
    {
        _meshes[PersonClass] = new NavMesh(NavMeshAgent.Default);
        Source.Meshes.Add(_meshes[PersonClass]!);
    }

    // The person-sized mesh: what `nav_stats` reports and most creatures plan on.
    internal NavMesh Mesh => _meshes[PersonClass]!;

    internal NavWorldGeometry Source { get; } = new();

    // The smallest size class a body of this radius and height fits in; the largest when none does.
    internal static int SizeClassOf(float radius, float height)
    {
        for (int i = 0; i < SizeClasses.Length; i++)
            if (radius <= SizeClasses[i].Radius + 1e-3f && height <= SizeClasses[i].Height + 1e-3f) return i;
        return SizeClasses.Length - 1;
    }

    internal NavMesh MeshOf(int sizeClass)
    {
        if (_meshes[sizeClass] is { } mesh) return mesh;
        mesh = new NavMesh(SizeClasses[sizeClass]);
        _meshes[sizeClass] = mesh;
        Source.Meshes.Add(mesh);
        return mesh;
    }

    // ---- areas and crowds (#271) ----

    private NavAreas? _areas;

    // The nav_area records as the planner reads them, built the first time a world plans.
    internal NavAreas Areas => _areas ?? NavAreas.None;

    // Creatures steering round each other (`nav_avoid`); off, they walk through each other as before.
    internal bool Avoidance { get; set; } = true;

    internal Crowd Crowd { get; } = new();

    private void Prepare(World world)
    {
        if (_areas != null) return;
        _areas = NavAreas.From(world.Resources.TryGet<RecordStore>(out var records) ? records : null);
        Source.Areas = _areas;
    }

    // The records changed (a reload): the areas are read again, and every tile baked from the old ones goes.
    internal void ReloadAreas()
    {
        _areas = null;
        Source.Reset();
        foreach (var mesh in _meshes) mesh?.Clear();
    }

    // Plan on the navmesh where there is one (`nav_mesh`). Off, every plan is the local grid's, as before
    // #264.
    public bool UseMesh { get; set; } = true;

    // Moves on whenever what the navmesh is baked from, a locked door or an off-mesh link changes (#265):
    // a path planned at an older version may go through something that is not there any more, or miss
    // a way that is.
    internal int Version => Source.Version;

    internal int MeshPlans { get; private set; }     // answered by the navmesh
    internal int GridPlans { get; private set; }     // answered by the local grid

    public float CellSize { get; set; } = 1f;

    public int MaxNodes { get; set; } = 4096;

    // Plans per tick, across every creature. A search is cheap; a hundred searches in one tick is a
    // frame somebody notices, and a creature that waits a tick for its path loses nothing.
    public int PlansPerTick { get; set; } = 4;

    public bool Enabled { get; set; } = true;

    public int Plans { get; private set; }
    public int Refused { get; private set; }     // asked, but the budget was spent
    public int NoRoute { get; private set; }     // asked, and there is no way through

    private long _budgetTick = -1;
    private int _plansThisTick;

    public void ResetStats()
    {
        Plans = 0; Refused = 0; NoRoute = 0; MeshPlans = 0; GridPlans = 0;
        foreach (var mesh in _meshes) mesh?.ResetStats();
        Crowd.ResetStats();
    }

    private bool CanPlan(long tick)
    {
        if (tick != _budgetTick) { _budgetTick = tick; _plansThisTick = 0; }
        return _plansThisTick < PlansPerTick;
    }

    // Plans a way from `from` to `to` for a body of `radius`, writing the corners into `path`. False
    // means "walk straight at it": either the budget is spent this tick, or there is no way through.
    //
    // The body's height (for its size class) and its faction (for the areas closed to it, #271) are read
    // from `self`: its character's movement profile and its `faction`.
    public bool Plan(World world, IPhysicsWorld space, Vector3 from, Vector3 to, float radius,
                     float stepHeight, float maxSlopeDegrees, Entity self, Entity target, ref NavPath path) =>
        Plan(world, space, from, to, radius, HeightOf(world, self), FactionOf(world, self), stepHeight, maxSlopeDegrees, self, target, ref path);

    internal static float HeightOf(World world, Entity self)
    {
        if (self.IsNull || !world.IsAlive(self) || !world.TryGet<CharacterController>(self, out var character)) return NavMeshAgent.Default.Height;
        if (!world.Resources.TryGet<RecordStore>(out var records) || records == null) return NavMeshAgent.Default.Height;
        return CharacterConventions.Of(world).ProfileOf(records, character.Profile).StandHeight;
    }

    internal static RecordId FactionOf(World world, Entity self) =>
        !self.IsNull && world.IsAlive(self) && world.TryGet<Faction>(self, out var faction) ? faction.Id : default;

    internal bool Plan(World world, IPhysicsWorld space, Vector3 from, Vector3 to, float radius, float height, RecordId faction,
                       float stepHeight, float maxSlopeDegrees, Entity self, Entity target, ref NavPath path)
    {
        if (!Enabled) return false;
        if (!CanPlan(world.Tick)) { Refused++; return false; }
        _plansThisTick++;
        Plans++;

        Span<Vector3> corners = stackalloc Vector3[NavPath.MaxCorners];
        int count;
        NavMeshCrossing crossing = default;
        if (UseMesh && PlanOnMesh(world, MeshOf(SizeClassOf(radius, height)), faction, from, to, corners, out count, out crossing))
        {
            MeshPlans++;
        }
        else
        {
            GridPlans++;
            Grid.Open(from, to, CellSize);
            Stamp(world, space, radius, stepHeight, maxSlopeDegrees, self, target);
            count = Grid.Search(from, to, corners, MaxNodes);
        }

        path.Clear();
        path.NoWayThrough = count == 0;
        if (count == 0) { NoRoute++; return false; }

        for (int i = 0; i < count; i++) path.Set(i, corners[i]);
        path.Count = (byte)count;
        path.PlannedFor = to;
        path.Version = Source.Version;
        if (crossing.Kind != NavCrossing.None)
        {
            path.Crossing = crossing.Kind;
            path.CrossingCorner = (byte)crossing.Corner;
            path.CrossingEntity = crossing.Kind == NavCrossing.Door
                ? Source.Crossings.Doors[crossing.Index].Entity
                : Source.Crossings.Links[crossing.Index].Entity;
        }
        return true;
    }

    // Has the world changed under a path planned at `version`? Looks (once a tick at most, and only with
    // the navmesh on) at what the mesh is baked from.
    internal bool Changed(World world, int version)
    {
        if (!Enabled || !UseMesh) return false;
        Prepare(world);
        Source.Sync(world);
        return Source.Version != version;
    }

    // The navmesh's answer, in origin space. False when it has none (no mesh at either end), and the
    // grid answers instead; true with `count` 0 when it has looked and there is no way through.
    //
    // The mesh is keyed absolutely, so the two ends go out of origin space and the corners come back
    // into it: a rebase moves nothing in the mesh (R6).
    private bool PlanOnMesh(World world, NavMesh mesh, RecordId faction, Vector3 from, Vector3 to, Span<Vector3> corners, out int count, out NavMeshCrossing crossing)
    {
        count = 0;
        Prepare(world);
        Source.Sync(world);
        mesh.BeginTick(world.Tick);
        var offset = world.Resources.TryGet<Origin>(out var origin) && origin != null ? origin.ToAbsolute(Vector3.Zero) : Vector3.Zero;
        var answer = mesh.Plan(from + offset, to + offset, Source.Geometry, Source.Crossings, MaxNodes, corners, out count, out _, out crossing,
                               Areas.FilterFor(faction));
        if (answer == NavMeshAnswer.CannotAnswer) return false;
        for (int i = 0; i < count; i++) corners[i] -= offset;
        return true;
    }

    // Is walking straight from one point to another the best way, as far as the areas go (#271)? With no
    // areas it always is (and nothing is looked at); with them, only when every span on the line is the
    // cheapest ground there is — otherwise a plan may find a cheaper way, and a creature asks for one —
    // and never when the line crosses ground closed to its faction.
    //
    // `edge`, when it is `Forbidden`, is the last point on the line before that ground (origin space).
    internal NavLine StraightLine(World world, Vector3 from, Vector3 to, float radius, float height, RecordId faction, out Vector3 edge)
    {
        edge = from;
        if (!Enabled || !UseMesh) return NavLine.Best;
        Prepare(world);
        if (Areas.Count == 0) return NavLine.Best;
        var mesh = MeshOf(SizeClassOf(radius, height));
        Source.Sync(world);
        mesh.BeginTick(world.Tick);
        var offset = world.Resources.TryGet<Origin>(out var origin) && origin != null ? origin.ToAbsolute(Vector3.Zero) : Vector3.Zero;
        var line = mesh.Line(from + offset, to + offset, Source.Geometry, Areas.FilterFor(faction), out edge);
        edge -= offset;
        return line;
    }

    // Drops every baked tile (`nav_rebuild`): they are baked again, from what is there now, as creatures
    // need them.
    internal void Rebuild()
    {
        foreach (var mesh in _meshes) mesh?.Clear();
    }

    // Everything in the window that a body cannot walk through: the obstacles that are there *now*, and
    // ground too steep to climb.
    //
    // Creatures and the player are left out on purpose. They move, they are what the path is usually
    // *for*, and a crowd that blocks its own way round a corner is worse than one that bumps: the crowd's
    // steering (Crowd.cs, #271) is what keeps bodies apart.
    private void Stamp(World world, IPhysicsWorld space, float radius, float stepHeight,
                       float maxSlopeDegrees, Entity self, Entity target)
    {
        var grid = Grid;
        float halfWidth = (grid.Width - 1) * 0.5f * grid.CellSize + grid.CellSize;
        float halfDepth = (grid.Height - 1) * 0.5f * grid.CellSize + grid.CellSize;
        Vector3 centre = grid.CellCentre((grid.Width - 1) / 2, (grid.Height - 1) / 2);

        // Broad-phase, so this is one query however much is in the window; what it over-reports is
        // narrowed by the box test below.
        var mask = LayerMask.All.Except(space.Layers.Enemy).Except(space.Layers.Player);
        int found = space.OverlapBox(centre, new Vector3(halfWidth, 8f, halfDepth), _nearby, mask);

        for (int i = 0; i < found; i++)
        {
            var entity = _nearby[i];
            if (entity == self || entity == target || !world.IsAlive(entity)) continue;
            if (!world.TryGet<Collider>(entity, out var collider)) continue;   // terrain has none (10 §4)
            if (collider.IsTrigger) continue;

            var at = world.Get<Transform>(entity).LocalPosition + collider.Center;
            Vector3 half = HalfExtents(collider);

            // Low enough to step over is not in the way, and high enough to walk under is not either:
            // a kerb and a gantry are both walkable, which is why height is read rather than ignored.
            float top = at.Y + half.Y, bottom = at.Y - half.Y;
            if (top <= grid.Corner.Y + stepHeight) continue;
            if (bottom >= grid.Corner.Y + 2.2f) continue;

            grid.BlockBox(new Vector3(at.X - half.X, 0, at.Z - half.Z),
                          new Vector3(at.X + half.X, 0, at.Z + half.Z), radius);
        }

        // Ground nobody can climb.
        //
        // **Sampled at the terrain's own resolution, not the grid's.** A heightfield sample is 8 m apart
        // (14 §3) and every `NormalAt` costs a sector lookup and four interpolations, so asking per 1 m
        // cell would be sixty-four questions with one answer between them — nine thousand of them for a
        // full-size window. One sample per terrain step, blocking the cells it covers, says exactly as
        // much for a sixty-fourth of the work.
        if (!world.Resources.TryGet<Terrain>(out var terrain) || terrain == null) return;
        float limit = MathF.Cos(maxSlopeDegrees * MathF.PI / 180f);
        int stride = Math.Clamp((int)(Terrain.SectorSize / (Terrain.SectorResolution - 1) / grid.CellSize), 1, 16);

        for (int y = 0; y < grid.Height; y += stride)
            for (int x = 0; x < grid.Width; x += stride)
            {
                var cell = grid.CellCentre(x, y);
                if (terrain.NormalAt(cell.X, cell.Z).Y >= limit) continue;
                for (int by = y; by < Math.Min(y + stride, grid.Height); by++)
                    for (int bx = x; bx < Math.Min(x + stride, grid.Width); bx++)
                        grid.Block(bx, by);
            }
    }

    private static Vector3 HalfExtents(in Collider collider) => collider.Shape switch
    {
        ColliderShape.Box => collider.Size * 0.5f,
        ColliderShape.Sphere => new Vector3(collider.Size.X),
        // A capsule's Size is radius and cylinder length, so the body is radius on X/Z and half the
        // cylinder plus a cap either end on Y.
        ColliderShape.Capsule => new Vector3(collider.Size.X, collider.Size.Y * 0.5f + collider.Size.X, collider.Size.X),
        _ => collider.Size * 0.5f,
    };

    // What the grid and the paths look like, for `nav_debug`: the cells it thinks are blocked, in the
    // window it last searched, and the corners each creature is walking to.
    public void DrawLastGrid(DebugDraw draw, float seconds = 0f)
    {
        var grid = Grid;
        for (int y = 0; y < grid.Height; y++)
            for (int x = 0; x < grid.Width; x++)
                if (grid.IsBlocked(x, y))
                    draw.Box(grid.CellCentre(x, y) + Vector3.UnitY * 0.5f,
                             new Vector3(grid.CellSize * 0.45f, 0.05f, grid.CellSize * 0.45f),
                             DebugColour.Red, seconds);
    }
}
