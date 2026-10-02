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
//
// **What moves (#265).** A mover is a door (NavLinks.cs) or, when it is too low to be in a body's way (a
// lift's floor), baked where it is now. A dynamic body is baked where it lies once it has come to rest,
// and a kinematic one where it is: a crate pushed into a doorway closes it to the planner, and one that
// is rolling is left to the creature's steering. What is not static is snapped outward to the mesh's
// cells first, so a body settling by millimetres does not drop the tiles under it every tick. The doors
// and the off-mesh links are gathered here too (`Crossings`), and `Version` changes whenever any of it
// changes what a plan would say, which is how a creature knows its path is out of date.
internal sealed class NavWorldGeometry
{
    public NavGeometry Geometry { get; } = new();

    public NavCrossings Crossings { get; } = new();

    // Bumped whenever the solids, a locked door or a link change: a path planned before is stale.
    public int Version { get; private set; }

    // How slow a dynamic body has to be going to count as lying where it is, in m/s.
    private const float AtRest = 0.05f;

    private readonly List<NavLinkSpan> _previousLinks = new();
    private readonly List<NavDoorSpan> _previousDoors = new();
    private Query<Mover, Transform>? _movers;
    private Query<NavLink, Transform>? _links;

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
            _movers = world.Query<Mover, Transform>();
            _links = world.Query<NavLink, Transform>();
        }

        var offset = world.Resources.TryGet<Origin>(out var origin) && origin != null ? origin.ToAbsolute(Vector3.Zero) : Vector3.Zero;
        bool changed = SyncTerrain(world, mesh);
        changed |= SyncLevels(world, mesh, offset);
        changed |= SyncColliders(world, mesh, offset);
        bool crossings = SyncCrossings(world, offset);
        if (changed || crossings) Version++;
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
        Crossings.Doors.Clear();

        foreach (var (transforms, colliders, entities) in _colliders!.Value.Chunks)
        {
            var t = transforms.Span;
            var c = colliders.Span;
            for (int n = 0; n < t.Length; n++)
            {
                ref readonly var collider = ref c[n];
                if (collider.IsTrigger || queryOnly.Has(collider.Layer) || collider.Layer == player || collider.Layer == enemy) continue;
                var entity = entities.EntityAt(n);
                if (world.Has<CharacterController>(entity)) continue;
                if (world.Has<Mover>(entity)) continue;   // below, with the brush-built movers

                // What moves is baked where it is once it has stopped (#265): a dynamic body at rest, a
                // kinematic one where it stands, both snapped outward to the cells.
                bool moves = false;
                if (world.TryGet<RigidBody>(entity, out var body) && body.Kind != BodyKind.Static)
                {
                    if (body.Kind == BodyKind.Dynamic && !Resting(world, space, entity)) continue;
                    moves = true;
                }

                var rotation = t[n].LocalRotation;
                var centre = t[n].LocalPosition + Vector3.Transform(collider.Center, rotation) + offset;
                var half = Rotated(HalfExtents(collider), rotation);
                _boxes.Add(moves ? Snapped(centre - half, centre + half) : new NavSolid(centre - half, centre + half));
            }
        }

        // Movers: a door is a crossing (and a wall only while it is locked shut); anything lower is baked
        // where it is now.
        foreach (var (movers, transforms, entities) in _movers!.Value.Chunks)
        {
            var m = movers.Span;
            var t = transforms.Span;
            for (int n = 0; n < m.Length; n++)
            {
                var entity = entities.EntityAt(n);
                if (!Bounds(world, entity, t[n], out var shape, out var layer) || layer == player || layer == enemy || queryOnly.Has(layer)) continue;
                // The bounds are about where it is now; shut is where it was drawn.
                var closedMin = shape.Min - t[n].LocalPosition + m[n].Closed + offset;
                var closedMax = shape.Max - t[n].LocalPosition + m[n].Closed + offset;
                bool door = closedMax.Y - closedMin.Y > mesh.Agent.StepHeight && m[n].OpenOffset != Vector3.Zero;
                if (!door)
                {
                    _boxes.Add(Snapped(shape.Min + offset, shape.Max + offset));
                    continue;
                }
                bool open = m[n].Position >= 1f && m[n].Direction >= 0;
                bool locked = world.TryGet<NavDoor>(entity, out var rules) && rules.Locked;
                if (locked && !open) _boxes.Add(new NavSolid(closedMin, closedMax));
                Crossings.Doors.Add(new NavDoorSpan(entity, closedMin, closedMax, open, locked));
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

    // The doors' footprints and lock, and the links, against the last tick's: a door opening is not a
    // change (its footprint is the same and the planner reads its state each plan), a locked door
    // shutting is (it is a wall now), and so is a link added, moved, switched or removed.
    private bool SyncCrossings(World world, Vector3 offset)
    {
        Crossings.Links.Clear();
        foreach (var (links, transforms, entities) in _links!.Value.Chunks)
        {
            var l = links.Span;
            var t = transforms.Span;
            for (int n = 0; n < l.Length; n++)
            {
                if (l[n].Disabled) continue;
                var start = t[n].LocalPosition + offset;
                Crossings.Links.Add(new NavLinkSpan(entities.EntityAt(n), start, start + l[n].End, l[n].Kind, l[n].TwoWay, l[n].Cost));
            }
        }

        bool changed = Crossings.Links.Count != _previousLinks.Count || Crossings.Doors.Count != _previousDoors.Count;
        for (int i = 0; !changed && i < Crossings.Links.Count; i++) changed = Crossings.Links[i] != _previousLinks[i];
        for (int i = 0; !changed && i < Crossings.Doors.Count; i++)
        {
            var a = Crossings.Doors[i];
            var b = _previousDoors[i];
            changed = a.Entity != b.Entity || a.Min != b.Min || a.Max != b.Max || a.Locked != b.Locked;
        }
        _previousLinks.Clear();
        _previousLinks.AddRange(Crossings.Links);
        _previousDoors.Clear();
        _previousDoors.AddRange(Crossings.Doors);
        return changed;
    }

    private static bool Resting(World world, IPhysicsWorld? space, Entity entity)
    {
        if (space == null || !world.TryGet<PhysicsBody>(entity, out var body)) return true;
        return space.VelocityOf(body).LengthSquared() <= AtRest * AtRest;
    }

    // Outward to the mesh's cells: a box that has moved by less than a cell bakes the same.
    private static NavSolid Snapped(Vector3 min, Vector3 max)
    {
        const float c = NavMesh.Cell;
        return new NavSolid(
            new Vector3(MathF.Floor(min.X / c) * c, MathF.Floor(min.Y / c) * c, MathF.Floor(min.Z / c) * c),
            new Vector3(MathF.Ceiling(max.X / c) * c, MathF.Ceiling(max.Y / c) * c, MathF.Ceiling(max.Z / c) * c));
    }

    // Where a mover's solid is now, in origin space: its collider's box, or the hull a map built for it.
    private static bool Bounds(World world, Entity entity, in Transform transform, out NavSolid bounds, out byte layer)
    {
        bounds = default;
        layer = 0;
        if (world.TryGet<Collider>(entity, out var collider))
        {
            if (collider.IsTrigger) return false;
            layer = collider.Layer;
            var centre = transform.LocalPosition + Vector3.Transform(collider.Center, transform.LocalRotation);
            var half = Rotated(HalfExtents(collider), transform.LocalRotation);
            bounds = new NavSolid(centre - half, centre + half);
            return true;
        }
        if (!world.TryGet<MapSolid>(entity, out var solid) || !world.Resources.TryGet<MapLevels>(out var levels) || levels == null) return false;
        foreach (var level in levels.Loaded)
        {
            if (level.Record != solid.Level || (uint)solid.Index >= (uint)level.Solids.Count) continue;
            var drawn = level.Solids[solid.Index];
            if (drawn.IsTrigger || drawn.Hull.Length == 0) return false;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var point in drawn.Hull)
            {
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }
            layer = level.Layer;
            bounds = new NavSolid(min + transform.LocalPosition, max + transform.LocalPosition);
            return true;
        }
        return false;
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
