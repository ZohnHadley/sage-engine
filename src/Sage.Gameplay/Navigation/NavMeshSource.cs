#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Gameplay;

// What a world's navmesh is baked from, kept in step with the world (issue #264).
//
// Once a tick that plans, it looks at the three things a tile is made of — the placed levels, the loaded
// terrain sectors and the static colliders — and drops the tiles under whatever changed. **Static** is
// the word that matters: a collider with no rigid body or a static one, not a trigger, not a character,
// not on a query-only layer. What moves is not baked, because a baked tile is kept; the local grid and
// the creature's own steering answer for it (and #265 for doors and other things that open and close).
//
// Looking costs one pass over the colliders, and only in a tick in which something planned. The answer is
// compared with the last one, so a world that is not changing invalidates nothing.
internal sealed class NavWorldGeometry
{
    public NavGeometry Geometry { get; } = new();

    private sealed class PlacedLevel
    {
        public required MapLevel Level;
        public required Vector3 At;              // absolute
        public required Vector3 Min, Max;        // absolute bounds of its brushes
        public readonly List<NavSolid> Solids = new();
    }

    private readonly List<PlacedLevel> _levels = new();
    private readonly List<PlacedLevel> _levelScratch = new();
    private List<NavSolid> _boxes = new(), _previousBoxes = new();
    private readonly HashSet<(Vector3, Vector3)> _boxKeys = new();
    private readonly HashSet<SectorCoord> _sectors = new(), _sectorScratch = new();
    private Query<Transform, Collider>? _colliders;
    private World? _world;
    private long _syncedTick = long.MinValue;

    public int StaticBoxes => _boxes.Count;
    public int Levels => _levels.Count;

    public void Sync(World world, NavMesh mesh)
    {
        if (world.Tick == _syncedTick && ReferenceEquals(world, _world)) return;
        _syncedTick = world.Tick;
        if (!ReferenceEquals(world, _world))
        {
            _world = world;
            _colliders = world.Query<Transform, Collider>();
        }

        var offset = world.Resources.TryGet<Origin>(out var origin) && origin != null ? origin.ToAbsolute(Vector3.Zero) : Vector3.Zero;
        bool changed = SyncTerrain(world, mesh);
        changed |= SyncLevels(world, mesh, offset);
        changed |= SyncColliders(world, mesh, offset);
        if (!changed) return;

        Geometry.Solids.Clear();
        foreach (var level in _levels) Geometry.Solids.AddRange(level.Solids);
        Geometry.Solids.AddRange(_boxes);
    }

    private bool SyncTerrain(World world, NavMesh mesh)
    {
        world.Resources.TryGet<Terrain>(out var terrain);
        Geometry.Terrain = terrain;
        _sectorScratch.Clear();
        if (terrain != null) foreach (var sector in terrain.Sectors) _sectorScratch.Add(sector.Coord);
        if (_sectorScratch.SetEquals(_sectors)) return false;

        foreach (var coord in _sectorScratch) if (!_sectors.Contains(coord)) DropSector(mesh, coord);
        foreach (var coord in _sectors) if (!_sectorScratch.Contains(coord)) DropSector(mesh, coord);
        _sectors.Clear();
        _sectors.UnionWith(_sectorScratch);
        return false;   // terrain is read through Geometry.Terrain, not the solids list
    }

    private static void DropSector(NavMesh mesh, SectorCoord coord)
    {
        var corner = coord.Origin(Terrain.SectorSize);
        mesh.Invalidate(corner, corner + new Vector3(Terrain.SectorSize, 0, Terrain.SectorSize));
    }

    private bool SyncLevels(World world, NavMesh mesh, Vector3 offset)
    {
        _levelScratch.Clear();
        bool changed = false;
        if (world.Resources.TryGet<MapLevels>(out var levels) && levels != null)
            foreach (var level in levels.Loaded)
            {
                if (!level.Placed) continue;
                var at = level.Position + offset;
                var known = Known(_levels, level);
                if (known != null && Vector3.DistanceSquared(known.At, at) < 1e-4f)
                {
                    _levelScratch.Add(known);
                    continue;
                }
                var placed = Place(level, at);
                mesh.Invalidate(placed.Min, placed.Max);
                if (known != null) mesh.Invalidate(known.Min, known.Max);
                _levelScratch.Add(placed);
                changed = true;
            }

        foreach (var old in _levels)
            if (Known(_levelScratch, old.Level) == null)
            {
                mesh.Invalidate(old.Min, old.Max);
                changed = true;
            }

        _levels.Clear();
        _levels.AddRange(_levelScratch);
        return changed;
    }

    private static PlacedLevel? Known(List<PlacedLevel> list, MapLevel level)
    {
        foreach (var placed in list) if (ReferenceEquals(placed.Level, level)) return placed;
        return null;
    }

    private static PlacedLevel Place(MapLevel level, Vector3 at)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var brush in level.Brushes)
        {
            min = Vector3.Min(min, brush.Min + at);
            max = Vector3.Max(max, brush.Max + at);
        }
        var placed = new PlacedLevel { Level = level, At = at, Min = min, Max = max };
        foreach (var brush in level.Brushes) placed.Solids.Add(NavGeometry.FromBrush(brush, at));
        return placed;
    }

    private bool SyncColliders(World world, NavMesh mesh, Vector3 offset)
    {
        (_previousBoxes, _boxes) = (_boxes, _previousBoxes);
        _boxes.Clear();
        var queryOnly = world.Resources.TryGet<IPhysicsWorld>(out var space) && space != null ? space.Layers.QueryOnly : default;
        byte player = space?.Layers.Player ?? 1, enemy = space?.Layers.Enemy ?? 2;

        foreach (var (transforms, colliders, entities) in _colliders!.Value.Chunks)
        {
            var t = transforms.Span;
            var c = colliders.Span;
            for (int n = 0; n < t.Length; n++)
            {
                ref readonly var collider = ref c[n];
                if (collider.IsTrigger || queryOnly.Has(collider.Layer) || collider.Layer == player || collider.Layer == enemy) continue;
                var entity = entities.EntityAt(n);
                if (world.TryGet<RigidBody>(entity, out var body) && body.Kind != BodyKind.Static) continue;
                if (world.Has<CharacterController>(entity)) continue;

                var rotation = t[n].LocalRotation;
                var centre = t[n].LocalPosition + Vector3.Transform(collider.Center, rotation) + offset;
                var half = Rotated(HalfExtents(collider), rotation);
                _boxes.Add(new NavSolid(centre - half, centre + half));
            }
        }

        if (Same(_boxes, _previousBoxes)) return false;

        // What appeared and what went, each dropping the tiles under it.
        _boxKeys.Clear();
        foreach (var box in _previousBoxes) _boxKeys.Add((box.Min, box.Max));
        foreach (var box in _boxes)
            if (!_boxKeys.Remove((box.Min, box.Max))) mesh.Invalidate(box.Min, box.Max);
        foreach (var (min, max) in _boxKeys) mesh.Invalidate(min, max);
        _boxKeys.Clear();
        return true;
    }

    private static bool Same(List<NavSolid> a, List<NavSolid> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Min != b[i].Min || a[i].Max != b[i].Max) return false;
        return true;
    }

    // The half extents of a box turned by a rotation, as an axis-aligned box: what a tile bakes.
    private static Vector3 Rotated(Vector3 half, Quaternion rotation)
    {
        if (rotation == Quaternion.Identity || rotation == default) return half;
        var m = Matrix4x4.CreateFromQuaternion(rotation);
        return new Vector3(
            MathF.Abs(m.M11) * half.X + MathF.Abs(m.M21) * half.Y + MathF.Abs(m.M31) * half.Z,
            MathF.Abs(m.M12) * half.X + MathF.Abs(m.M22) * half.Y + MathF.Abs(m.M32) * half.Z,
            MathF.Abs(m.M13) * half.X + MathF.Abs(m.M23) * half.Y + MathF.Abs(m.M33) * half.Z);
    }

    internal static Vector3 HalfExtents(in Collider collider) => collider.Shape switch
    {
        ColliderShape.Box => collider.Size * 0.5f,
        ColliderShape.Sphere => new Vector3(collider.Size.X),
        // A capsule's Size is radius and cylinder length, so the body is radius on X/Z and half the
        // cylinder plus a cap either end on Y.
        ColliderShape.Capsule => new Vector3(collider.Size.X, collider.Size.Y * 0.5f + collider.Size.X, collider.Size.X),
        _ => collider.Size * 0.5f,
    };
}
