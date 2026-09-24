#nullable enable
using System;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// Finding a way round things (docs/design/16 §3.4, TODO F23).
//
// **A local grid, built when it is needed and thrown away.** Not a navmesh: a Daggerfall-like has
// dungeons on a grid and an outdoors that is heightfield plus scattered obstacles, and the thing a
// creature actually needs is "how do I get round this wall in the next twenty metres". So the grid is a
// window around the agent and its goal, stamped from the colliders that are there *now* — which means a
// crate that has just been pushed into a doorway is in the path, and nothing has to be rebuilt when the
// world changes. A navmesh (and a coarse graph for travelling across sectors) is still the answer for
// interiors built out of brushes, and stays F23's "later" half.
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
    public bool NoWayThrough;   // the last search failed: walk straight and stop asking

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

    public void Clear()
    {
        Count = 0;
        Step = 0;
    }

    public bool Walking => Step < Count;

    public Vector3 Next => this[Step];
}

// The world's navigation: one scratch grid, the budget that stops a hundred creatures all planning in
// the same tick, and the numbers `nav_stats` prints (16 §3.4, F23).
public sealed class Navigation
{
    private readonly Entity[] _nearby = new Entity[256];

    public NavGrid Grid { get; } = new();

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

    public void ResetStats() { Plans = 0; Refused = 0; NoRoute = 0; }

    public bool CanPlan(long tick)
    {
        if (tick != _budgetTick) { _budgetTick = tick; _plansThisTick = 0; }
        return _plansThisTick < PlansPerTick;
    }

    // Plans a way from `from` to `to` for a body of `radius`, writing the corners into `path`. False
    // means "walk straight at it": either the budget is spent this tick, or there is no way through.
    public bool Plan(World world, PhysicsSpace space, Vector3 from, Vector3 to, float radius,
                     float stepHeight, float maxSlopeDegrees, Entity self, Entity target, ref NavPath path)
    {
        if (!Enabled) return false;
        if (!CanPlan(world.Tick)) { Refused++; return false; }
        _plansThisTick++;
        Plans++;

        Grid.Open(from, to, CellSize);
        Stamp(world, space, radius, stepHeight, maxSlopeDegrees, self, target);

        Span<Vector3> corners = stackalloc Vector3[NavPath.MaxCorners];
        int count = Grid.Search(from, to, corners, MaxNodes);
        path.Clear();
        path.NoWayThrough = count == 0;
        if (count == 0) { NoRoute++; return false; }

        for (int i = 0; i < count; i++) path.Set(i, corners[i]);
        path.Count = (byte)count;
        path.PlannedFor = to;
        return true;
    }

    // Everything in the window that a body cannot walk through: the obstacles that are there *now*, and
    // ground too steep to climb.
    //
    // Creatures and the player are left out on purpose. They move, they are what the path is usually
    // *for*, and a crowd that blocks its own way round a corner is worse than one that bumps: the
    // raycast steering in `MoveToTargetTask` is what keeps bodies apart.
    private void Stamp(World world, PhysicsSpace space, float radius, float stepHeight,
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

        // Ground nobody can climb. The terrain is 8 m between samples (14 §3), so this is smooth at nav
        // resolution: it marks hillsides, not kerbs.
        if (!world.Resources.TryGet<Terrain>(out var terrain) || terrain == null) return;
        float limit = MathF.Cos(maxSlopeDegrees * MathF.PI / 180f);
        for (int y = 0; y < grid.Height; y++)
            for (int x = 0; x < grid.Width; x++)
            {
                if (grid.IsBlocked(x, y)) continue;
                var cell = grid.CellCentre(x, y);
                if (terrain.NormalAt(cell.X, cell.Z).Y < limit) grid.Block(x, y);
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
