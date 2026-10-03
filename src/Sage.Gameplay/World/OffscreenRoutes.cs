#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Gameplay;

// The coarse graph an off-screen agent walks (issue 4m-10): what is in its way, and the ways round it, read
// from the content of one scene — not from the world, where nothing of an unloaded sector exists.
//
// - **Walls** are the static solids content places: a placement whose prefab (or its overrides) has a
//   non-trigger `body` without mass, or a `collider` with no rigid body or a static one; and a `.map`'s
//   worldspawn brushes and its solid entities that are not triggers. Only what is taller than a step
//   (`Step`) and overlaps a body's height (`Height`) above where the agent walks is a wall: a floor is not,
//   and neither is a ceiling. A wall is its box (turned by the placement's yaw, the box round that), so a
//   round tower is a square one to an agent.
// - **Doors** are not walls: a `mover` (and a map's solid entity whose classname is a prefab with one) is a
//   way through, a node on the graph at its middle, unless its `nav_door` is `locked`, which makes it a wall
//   again. Off-screen, nobody needs to open it.
// - **Nav links** (`nav_link`) are edges from where they stand to their `end`, of their length plus their
//   cost, one way unless `twoWay`: off-screen a ladder is climbed and a drop dropped without a second look.
// - **Corners**: each wall's corners, `Clearance` out, are nodes too (the ones inside another wall are
//   dropped), so an agent walks round a building rather than through it even where nobody drew a door.
//
// An agent walks straight while the next minute's walk is clear; when it is not, it plans (A* over the nodes
// within `Reach` of each other that can see each other, the goal from any of them) from where it is, and
// follows the corners. A plan that finds nothing walks straight, as before 4m-10: an agent never stands
// still because there was no way. Planning allocates; walking a planned route, or a clear straight line, does not.
internal sealed class OffscreenGraph
{
    public const float Step = 0.5f;        // m: lower than this, a body steps over it
    public const float Height = 1.8f;      // m: the height of a body, over which nothing is in its way
    public const float Clearance = 0.6f;   // m: how far out of a wall's corner the corner's node is
    public const float Reach = 256f;       // m: how far apart two nodes may be to be joined
    public const int MaxExpanded = 4096;   // nodes a plan may look at before it gives up
    private const float Cell = 64f;        // m: the buckets walls and nodes are found in

    private readonly struct Wall
    {
        public readonly Vector2 Min, Max;  // x, z
        public readonly float Bottom, Top; // y: absolute, or above the ground when `Ground`
        public readonly bool Ground;

        public Wall(Vector2 min, Vector2 max, float bottom, float top, bool ground)
        {
            Min = min;
            Max = max;
            Bottom = bottom;
            Top = top;
            Ground = ground;
        }
    }

    private readonly List<Wall> _walls = new();
    private readonly List<Vector3> _nodes = new();
    private readonly Dictionary<int, List<(int To, float Cost)>> _links = new();
    private readonly Dictionary<(int, int), List<int>> _wallCells = new();
    private readonly Dictionary<(int, int), List<int>> _nodeCells = new();
    private int[] _seen = Array.Empty<int>();
    private int _mark;
    private bool _finished;

    public int WallCount => _walls.Count;
    public int NodeCount => _nodes.Count;

    // A solid box, absolute (or, `ground`, with y above the ground there).
    public void AddWall(Vector3 min, Vector3 max, bool ground)
    {
        if (_finished) throw new InvalidOperationException("the graph is finished");
        if (max.Y - min.Y <= Step) return;   // a floor, a kerb: stepped over
        _walls.Add(new Wall(new Vector2(min.X, min.Z), new Vector2(max.X, max.Z), min.Y, max.Y, ground));
    }

    // A way through: a door's middle, a link's end. On the ground its height is the terrain's, which content
    // does not know: NaN, which a plan reads as the goal's.
    public int AddNode(Vector3 at, bool ground = false)
    {
        if (_finished) throw new InvalidOperationException("the graph is finished");
        _nodes.Add(ground ? at with { Y = float.NaN } : at);
        return _nodes.Count - 1;
    }

    public void AddLink(Vector3 start, Vector3 end, bool twoWay, float cost, bool ground = false)
    {
        int a = AddNode(start, ground), b = AddNode(end, ground);
        float length = Vector3.Distance(start, end) + MathF.Max(cost, 0f);
        Link(a, b, length);
        if (twoWay) Link(b, a, length);
    }

    private void Link(int from, int to, float cost)
    {
        if (!_links.TryGetValue(from, out var list)) _links[from] = list = new List<(int, float)>();
        list.Add((to, cost));
    }

    // The corners, and the buckets: once, when everything is in.
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        for (int i = 0; i < _walls.Count; i++)
        {
            var wall = _walls[i];
            for (int x = CellOf(wall.Min.X); x <= CellOf(wall.Max.X); x++)
                for (int z = CellOf(wall.Min.Y); z <= CellOf(wall.Max.Y); z++)
                    Bucket(_wallCells, (x, z)).Add(i);
        }
        _seen = new int[_walls.Count];

        // A corner's height is the wall's foot, or (on the ground) the goal's: NaN until a plan says.
        foreach (var wall in _walls)
        {
            float y = wall.Ground ? float.NaN : wall.Bottom;
            Corner(new Vector2(wall.Min.X - Clearance, wall.Min.Y - Clearance), y);
            Corner(new Vector2(wall.Max.X + Clearance, wall.Min.Y - Clearance), y);
            Corner(new Vector2(wall.Min.X - Clearance, wall.Max.Y + Clearance), y);
            Corner(new Vector2(wall.Max.X + Clearance, wall.Max.Y + Clearance), y);
        }
        for (int i = 0; i < _nodes.Count; i++)
            Bucket(_nodeCells, (CellOf(_nodes[i].X), CellOf(_nodes[i].Z))).Add(i);
    }

    // A corner inside another wall is no way round anything.
    private void Corner(Vector2 xz, float y)
    {
        foreach (var wall in _walls)
            if (xz.X > wall.Min.X && xz.X < wall.Max.X && xz.Y > wall.Min.Y && xz.Y < wall.Max.Y) return;
        _nodes.Add(new Vector3(xz.X, y, xz.Y));
    }

    private static int CellOf(float v) => (int)MathF.Floor(v / Cell);

    private static List<int> Bucket(Dictionary<(int, int), List<int>> cells, (int, int) key)
    {
        if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<int>();
        return list;
    }

    // ---- is the way clear ----------------------------------------------------------------------------------

    // Whether a wall stands between `a` and `b` (absolute). A wall either end is inside is not counted: a
    // marker drawn inside a hut's box, or an agent that was standing in one, is not walled in. Allocates nothing.
    public bool Blocked(Vector3 a, Vector3 b)
    {
        if (_walls.Count == 0) return false;
        Finish();
        if (++_mark == int.MaxValue)
        {
            Array.Clear(_seen);
            _mark = 1;
        }
        float bottom = MathF.Min(a.Y, b.Y) + Step, top = MathF.Max(a.Y, b.Y) + Height;
        int x0 = CellOf(MathF.Min(a.X, b.X)), x1 = CellOf(MathF.Max(a.X, b.X));
        int z0 = CellOf(MathF.Min(a.Z, b.Z)), z1 = CellOf(MathF.Max(a.Z, b.Z));
        var from = new Vector2(a.X, a.Z);
        var delta = new Vector2(b.X - a.X, b.Z - a.Z);
        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
            {
                if (!_wallCells.TryGetValue((x, z), out var list)) continue;
                foreach (int i in list)
                {
                    if (_seen[i] == _mark) continue;
                    _seen[i] = _mark;
                    var wall = _walls[i];
                    if (wall.Ground ? wall.Top <= Step || wall.Bottom >= Height : wall.Top <= bottom || wall.Bottom >= top) continue;
                    if (Inside(wall, from) || Inside(wall, from + delta)) continue;
                    if (Crosses(wall, from, delta)) return true;
                }
            }
        return false;
    }

    private static bool Inside(in Wall wall, Vector2 p) =>
        p.X >= wall.Min.X && p.X <= wall.Max.X && p.Y >= wall.Min.Y && p.Y <= wall.Max.Y;

    // The segment from `p` along `d` meets the box (slabs, Liang-Barsky).
    private static bool Crosses(in Wall wall, Vector2 p, Vector2 d)
    {
        float t0 = 0f, t1 = 1f;
        return Clip(-d.X, p.X - wall.Min.X, ref t0, ref t1) && Clip(d.X, wall.Max.X - p.X, ref t0, ref t1)
            && Clip(-d.Y, p.Y - wall.Min.Y, ref t0, ref t1) && Clip(d.Y, wall.Max.Y - p.Y, ref t0, ref t1);
    }

    private static bool Clip(float denominator, float numerator, ref float t0, ref float t1)
    {
        if (denominator == 0f) return numerator >= 0f;
        float t = numerator / denominator;
        if (denominator < 0f)
        {
            if (t > t1) return false;
            if (t > t0) t0 = t;
        }
        else
        {
            if (t < t0) return false;
            if (t < t1) t1 = t;
        }
        return true;
    }

    // ---- planning ----------------------------------------------------------------------------------------

    private readonly PriorityQueue<int, float> _open = new();
    private readonly Dictionary<int, float> _cost = new();
    private readonly Dictionary<int, int> _from = new();
    private readonly HashSet<int> _closed = new();

    // The corners from `start` to `goal` (absolute), the goal last, into `route`; false (and `route` empty)
    // when there is no way within the budget.
    public bool Plan(Vector3 start, Vector3 goal, List<Vector3> route)
    {
        route.Clear();
        Finish();
        if (!Blocked(start, goal))
        {
            route.Add(goal);
            return true;
        }
        int n = _nodes.Count, startId = n, goalId = n + 1;
        Vector3 At(int i) => i == startId ? start : i == goalId ? goal : float.IsNaN(_nodes[i].Y) ? _nodes[i] with { Y = goal.Y } : _nodes[i];

        _open.Clear();
        _cost.Clear();
        _from.Clear();
        _closed.Clear();
        _cost[startId] = 0f;
        _open.Enqueue(startId, Vector3.Distance(start, goal));
        int expanded = 0;
        int cells = (int)MathF.Ceiling(Reach / Cell);
        while (_open.TryDequeue(out int u, out _))
        {
            if (u == goalId)
            {
                for (int i = goalId; i != startId; i = _from[i]) route.Add(At(i));
                route.Reverse();
                return true;
            }
            if (!_closed.Add(u)) continue;
            if (++expanded > MaxExpanded) break;
            var here = At(u);
            float g = _cost[u];

            void Relax(int v, float edge)
            {
                if (_closed.Contains(v)) return;
                float cost = g + edge;
                if (_cost.TryGetValue(v, out float had) && had <= cost) return;
                _cost[v] = cost;
                _from[v] = u;
                _open.Enqueue(v, cost + Vector3.Distance(At(v), goal));
            }

            if (!Blocked(here, goal)) Relax(goalId, Vector3.Distance(here, goal));
            int cx = CellOf(here.X), cz = CellOf(here.Z);
            for (int x = cx - cells; x <= cx + cells; x++)
                for (int z = cz - cells; z <= cz + cells; z++)
                {
                    if (!_nodeCells.TryGetValue((x, z), out var list)) continue;
                    foreach (int v in list)
                    {
                        if (v == u || _closed.Contains(v)) continue;
                        var there = At(v);
                        float d = Vector3.Distance(here, there);
                        if (d > Reach || Blocked(here, there)) continue;
                        Relax(v, d);
                    }
                }
            if (u < n && _links.TryGetValue(u, out var links))
                foreach (var (to, cost) in links) Relax(to, cost);
        }
        route.Clear();
        return false;
    }
}
