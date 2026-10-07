#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Physics3D;

#pragma warning disable SAGE0134, SAGE0124 // joints (phase 4k) and an entity input: the physics plugin ships with the engine that declares them

// The physics tick (docs/design/10 §3):
//   PrePhysics   new colliders get bodies; kinematic and teleported transforms are pushed into Bepu
//   Physics      Simulation.Timestep
//   PostPhysics  dynamic bodies are written back to Transform; trigger overlaps are published

// PrePhysics: keeps Bepu's contents in step with the world's colliders. In an edit world as well (issue
// #219): it only mirrors the world into Bepu, and the editor picks by raycasting what it mirrored.
#pragma warning disable SAGE0133 // the edit world's run condition: physics ships with the engine that declares it
[System("sage.physics.sync", Phase.PrePhysics, After = new[] { "sage.physics.terrain" }, Condition = RunCondition.EvenWhenEditing)]
#pragma warning restore SAGE0133
internal sealed class PhysicsSyncSystem : ISystem
{
    private readonly PhysicsSpace _space;
    private readonly Query<Transform, Collider> _pending;
    private readonly Query<Transform, Collider, PhysicsBody> _bodies;
    private readonly Query<PhysicsBody> _switchedOff;                         // ColliderOff, still with a body
    private readonly List<Entity> _toAdd = new();
    private readonly List<Entity> _rebuild = new();                          // compounds to (re)build this run
    private readonly List<PhysicsSpace.CompoundPart> _parts = new();
    private readonly List<Entity> _newParts = new();

    public PhysicsSyncSystem(World world, PhysicsSpace space)
    {
        _space = space;
        var off = Tags.Get<ColliderOff>();
        _pending = world.Query<Transform, Collider>().WithoutComponent<PhysicsBody>().WithoutAnyTags(off);   // and no ColliderPart: checked below
        _bodies = world.Query<Transform, Collider, PhysicsBody>();
        _switchedOff = world.Query<PhysicsBody>().AllTags(off).WithoutComponent<ColliderPart>();
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        _rebuild.Clear();
        Drain(world);
        SwitchOff(world);

        // New colliders: collect first, then add the components (adding changes the archetypes we're
        // iterating).
        _toAdd.Clear();
        foreach (var (_, _, entities) in _pending.Chunks)
            for (int n = 0; n < entities.Length; n++)
            {
                var entity = entities.EntityAt(n);
                if (!entity.HasComponent<ColliderPart>()) _toAdd.Add(entity);   // a part is its compound's
            }

        foreach (var entity in _toAdd)
        {
            if (!world.IsAlive(entity)) continue;
            var collider = world.Get<Collider>(entity);
            var body = world.TryGet<RigidBody>(entity, out var rigid) ? rigid : new RigidBody { Kind = BodyKind.Static };

            // A collider on a child entity (issue #268) moves with its parent, so "static" there means
            // "moved by nothing but its parent": under a dynamic body it is part of that body's compound,
            // anywhere else a kinematic body that follows the parent below. One that asks to be kinematic
            // or dynamic is that.
            var parent = entity.Parent;
            if (!parent.IsNull && body.Kind == BodyKind.Static)
            {
                var owner = CompoundOwner(parent);
                if (!owner.IsNull)
                {
                    world.Add(entity, new ColliderPart { Body = owner });
                    NoteRebuild(owner);
                    continue;
                }
                body.Kind = BodyKind.Kinematic;
            }

            // A child is placed by its parents' transforms as they are now: its GlobalTransform is last
            // tick's, or not yet computed at all for one spawned this tick.
            var pose = !parent.IsNull ? PhysicsPoses.WorldPose(entity)
                : world.Has<GlobalTransform>(entity) ? world.Get<GlobalTransform>(entity).Current
                : Pose.FromLocal(world.Get<Transform>(entity));
            var added = _space.AddBody(entity, collider, body, pose);
            world.Add(entity, added);
            if (body.Kind == BodyKind.Dynamic && entity.ChildCount > 0) NoteRebuild(entity);   // its children's colliders join it

            // A dynamic body keeps its velocity in a saved component (BodyMotion, written back each
            // step): one made from a load starts at it, so a swinging sign carries on swinging. Not in an
            // edit world, where nothing moves and nothing is saved from the bodies.
#pragma warning disable SAGE0133 // the edit world flag: physics ships with the engine that declares it
            bool editing = world.Editing;
#pragma warning restore SAGE0133
            if (body.Kind == BodyKind.Dynamic && !editing)
            {
                if (world.TryGet<BodyMotion>(entity, out var velocity))
                {
                    if (velocity.Linear != Vector3.Zero) _space.SetVelocity(added, velocity.Linear);
                    if (velocity.Angular != Vector3.Zero) _space.SetAngularVelocity(added, velocity.Angular);
                }
                else world.Add(entity, new BodyMotion());
            }
        }

        foreach (var owner in _rebuild) Rebuild(world, owner);

        // Kinematic bodies (and anything gameplay moved directly) follow their transform. A root's local
        // transform is its world one; a parented collider — a hitbox on a creature's bone (issue #137), an
        // attachment, a prefab's child (issue #268) — is composed up its parents' transforms as they are
        // now, not GlobalTransform, which is last tick's: the owner has already moved this tick
        // (sage.character.move runs first). One whose place has not changed is left alone, so it sleeps.
        foreach (var (transforms, colliders, handles, entities) in _bodies.Chunks)
        {
            var t = transforms.Span;
            var c = colliders.Span;
            var h = handles.Span;
            for (int n = 0; n < t.Length; n++)
            {
                if (h[n].IsStatic) continue;
                if (_space.IsDynamic(h[n])) continue;
                var parent = entities.EntityAt(n).Parent;
                if (parent.IsNull)
                {
                    _space.SetPose(h[n], c[n], Pose.FromLocal(t[n]));
                    continue;
                }
                var pose = Pose.Combine(PhysicsPoses.WorldPose(parent), Pose.FromLocal(t[n]));
                if (!_space.IsAt(h[n], c[n], pose)) _space.SetPose(h[n], c[n], pose);
            }
        }
    }

    // Colliders tagged ColliderOff (issue #273) lose their bodies; the pending query above leaves them
    // without one until the tag goes. Collected first: removing PhysicsBody changes the archetypes.
    private void SwitchOff(World world)
    {
        _toAdd.Clear();
        foreach (var (_, entities) in _switchedOff.Chunks)
            for (int n = 0; n < entities.Length; n++) _toAdd.Add(entities.EntityAt(n));
        foreach (var entity in _toAdd)
        {
            _space.RemoveBody(world.Get<PhysicsBody>(entity));
            world.Remove<PhysicsBody>(entity);
        }
    }

    // What the module's EntityDestroyed hook left: compounds that lost a part, and parts that lost their
    // compound (they get bodies of their own as ordinary new colliders).
    private void Drain(World world)
    {
        foreach (var owner in _space.CompoundsToRebuild) NoteRebuild(owner);
        _space.CompoundsToRebuild.Clear();
        foreach (var part in _space.PartsLeftBehind)
            if (world.IsAlive(part) && world.Has<ColliderPart>(part)) world.Remove<ColliderPart>(part);
        _space.PartsLeftBehind.Clear();
    }

    private void NoteRebuild(Entity owner)
    {
        if (!_rebuild.Contains(owner)) _rebuild.Add(owner);
    }

    // The dynamic body a static child collider below `parent` belongs to: the nearest ancestor with a body
    // of its own, when that is dynamic. A plain transform in between (a socket, a group) is looked through.
    private static Entity CompoundOwner(Entity parent)
    {
        for (var p = parent; !p.IsNull; p = p.Parent)
        {
            if (p.TryGetComponent<ColliderPart>(out var part)) return part.Body;
            if (p.TryGetComponent<RigidBody>(out var rigid))
                return rigid.Kind == BodyKind.Dynamic && p.HasComponent<Collider>() ? p : default;
            if (p.HasComponent<Collider>()) return default;
        }
        return default;
    }

    // A dynamic body becomes the compound of its own collider and every static collider below it (not
    // through a child with a body of its own).
    private void Rebuild(World world, Entity owner)
    {
        if (!world.IsAlive(owner) || !world.TryGet<PhysicsBody>(owner, out var body) || !world.TryGet<Collider>(owner, out var own)) return;
        if (!_space.IsDynamic(body)) return;
        _parts.Clear();
        _newParts.Clear();
        Gather(owner, Pose.Identity);
        foreach (var part in _newParts)
            if (world.Has<ColliderPart>(part)) world.Get<ColliderPart>(part).Body = owner;
            else world.Add(part, new ColliderPart { Body = owner });
        _space.SetCompound(body, owner, own, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_parts));
        _newParts.Clear();
    }

    private void Gather(Entity entity, Pose relative)
    {
        foreach (var child in entity.ChildEntities)
        {
            if (child.TryGetComponent<RigidBody>(out var rigid) && rigid.Kind != BodyKind.Static) continue;
            if (child.HasComponent<PhysicsBody>()) continue;   // already a body of its own
            var local = Pose.Combine(relative, child.TryGetComponent<Transform>(out var t) ? Pose.FromLocal(t) : Pose.Identity);
            if (child.TryGetComponent<Collider>(out var collider) && collider.Shape != ColliderShape.Mesh)
            {
                _parts.Add(new PhysicsSpace.CompoundPart(child, collider, local));
                _newParts.Add(child);
            }
            Gather(child, local);
        }
    }
}

// World poses from the transforms as they are now (GlobalTransform is last tick's).
internal static class PhysicsPoses
{
    public static Pose WorldPose(Entity entity)
    {
        var pose = entity.TryGetComponent<Transform>(out var local) ? Pose.FromLocal(local) : Pose.Identity;
        for (var parent = entity.Parent; !parent.IsNull; parent = parent.Parent)
            if (parent.TryGetComponent<Transform>(out var above)) pose = Pose.Combine(Pose.FromLocal(above), pose);
        return pose;
    }

    // `world` in the space of a parent at `parent`: what its local transform has to be.
    public static Pose Relative(in Pose parent, in Pose world)
    {
        var inverse = Quaternion.Conjugate(parent.Rotation);
        var scale = new Vector3(parent.Scale.X == 0f ? 1f : parent.Scale.X, parent.Scale.Y == 0f ? 1f : parent.Scale.Y, parent.Scale.Z == 0f ? 1f : parent.Scale.Z);
        return new Pose
        {
            Position = Vector3.Transform(world.Position - parent.Position, inverse) / scale,
            Rotation = Quaternion.Normalize(inverse * world.Rotation),
            Scale = world.Scale / scale,
        };
    }
}

// Physics: one Bepu step per tick.
[System("sage.physics.step", Phase.Physics)]
internal sealed class PhysicsStepSystem : ISystem
{
    private readonly PhysicsSpace _space;
    private readonly World _world;

    public PhysicsStepSystem(World world, PhysicsSpace space)
    {
        _world = world;
        _space = space;
    }

    public void Run(in SystemContext ctx)
    {
#pragma warning disable SAGE0129 // space gravity (4m-17): physics ships with the engine that declares it
        // Each space's gravity (4m-17): the player's scene's, and the scenes held live beside it.
        if (_world.Resources.TryGet<SpaceGravity>(out var gravity) && gravity != null) _space.UseGravity(gravity, _world.Origin());
#pragma warning restore SAGE0129
        _space.Step(ctx.Tick.Dt);
    }
}

// PostPhysics: dynamic bodies win over their transform, and trigger overlaps become readable.
[System("sage.physics.write_back", Phase.PostPhysics)]
internal sealed class PhysicsWriteBackSystem : ISystem
{
    private readonly PhysicsSpace _space;
    private readonly Query<Transform, Collider, PhysicsBody> _bodies;
    private readonly Query<PhysicsBody, BodyMotion> _velocities;

    public PhysicsWriteBackSystem(World world, PhysicsSpace space)
    {
        _space = space;
        _velocities = world.Query<PhysicsBody, BodyMotion>();
        _bodies = world.Query<Transform, Collider, PhysicsBody>();
    }

    public void Run(in SystemContext ctx)
    {
        foreach (var (transforms, colliders, handles, entities) in _bodies.Chunks)
        {
            var t = transforms.Span;
            var c = colliders.Span;
            var h = handles.Span;
            for (int n = 0; n < t.Length; n++)
            {
                if (!_space.IsDynamic(h[n])) continue;
                var pose = _space.PoseOf(h[n]);
                // Bepu poses the shape's centre; the transform is the entity's origin (review #44).
                var position = c[n].Center == Vector3.Zero
                    ? pose.Position
                    : pose.Position - Vector3.Transform(c[n].Center, pose.Rotation);
                var parent = entities.EntityAt(n).Parent;
                if (!parent.IsNull)
                {
                    // A dynamic body on a child entity (issue #268): its local transform is in its parent's space.
                    var local = PhysicsPoses.Relative(PhysicsPoses.WorldPose(parent),
                                                      new Pose { Position = position, Rotation = pose.Rotation, Scale = t[n].LocalScale });
                    t[n].LocalPosition = local.Position;
                    t[n].LocalRotation = local.Rotation;
                    continue;
                }
                t[n].LocalRotation = pose.Rotation;
                t[n].LocalPosition = position;
            }
        }

        foreach (var (handles, velocities, _) in _velocities.Chunks)
        {
            var h = handles.Span;
            var v = velocities.Span;
            for (int n = 0; n < h.Length; n++)
            {
                if (!_space.IsDynamic(h[n])) continue;
                v[n].Linear = _space.VelocityOf(h[n]);
                v[n].Angular = _space.AngularVelocityOf(h[n]);
            }
        }

        if (LogCat.Physics.IsEnabled(LogLevel.Trace))
        {
            foreach (var overlap in _space.TriggerEnter)
                Log.Trace(LogCat.Physics, $"Trigger enter: {World.Describe(overlap.Other)} → {World.Describe(overlap.Trigger)}");
            foreach (var overlap in _space.TriggerExit)
                Log.Trace(LogCat.Physics, $"Trigger exit: {World.Describe(overlap.Other)} → {World.Describe(overlap.Trigger)}");
        }
    }
}

// PrePhysics: gives loaded terrain sectors collision (14 §3, closes F13's collision gap).
//
// **Per chunk, on a budget** (#277): a sector is 4x4 static meshes of 32x32 cells, not one of 128x128, and a
// sector streaming made live while the player walks (`TerrainSector.Budgeted`: a kilometre away, at least)
// gets `ChunksPerTick` of them a tick, so crossing an edge does not build 32,000 triangles' worth of tree in
// one tick. Anything else — the first ring, a jump, travel, a sector a game loaded itself — is built whole at
// once, because something may be about to stand on it.
#pragma warning disable SAGE0133 // as sage.physics.sync: the ground is pickable in the editor too (#219)
[System("sage.physics.terrain", Phase.PrePhysics, Condition = RunCondition.EvenWhenEditing)]
#pragma warning restore SAGE0133
internal sealed class TerrainCollisionSystem : ISystem
{
    public const int ChunkCells = 32;
    public const int ChunksPerTick = 4;

    private readonly PhysicsSpace _space;
    private Terrain? _terrain;
    private readonly World _world;
    private Vector3[] _vertices = Array.Empty<Vector3>();
    private int[] _indices = Array.Empty<int>();
    private byte[] _triangleLayers = Array.Empty<byte>();

    // Chunks built so far of the sectors under way (by sector object: a sector unloaded and loaded again is a new one).
    private readonly Dictionary<TerrainSector, int> _progress = new();
    private readonly List<TerrainSector> _gone = new();

    public TerrainCollisionSystem(World world, PhysicsSpace space)
    {
        _world = world;
        _space = space;
    }

    public void Run(in SystemContext ctx)
    {
        // Looked up here rather than when the system is made: physics furnishes a world before streaming
        // does, and a world with no streaming plugin has no terrain at all.
        if (_terrain == null && !_world.Resources.TryGet(out _terrain)) return;
        var sectors = _terrain!.Sectors;
        int budget = ChunksPerTick;
        for (int i = 0; i < sectors.Count; i++)
        {
            var sector = sectors[i];
            if (sector.CollisionBuilt) continue;
            int chunks = (sector.Heights.Resolution - 1) / ChunkCells;
            int total = Math.Max(1, chunks * chunks);
            _progress.TryGetValue(sector, out int done);
            int upTo = sector.Budgeted ? Math.Min(total, done + budget) : total;
            if (upTo <= done) continue;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int c = done; c < upTo; c++) Build(sector, c, chunks);
            if (sector.Budgeted) budget -= upTo - done;
            if (upTo == total)
            {
                sector.CollisionBuilt = true;
                _progress.Remove(sector);
            }
            else _progress[sector] = upTo;
            Log.Debug(LogCat.Physics, $"Terrain sector {sector.Coord}: collision chunks {done}..{upTo - 1} of {total} in {watch.Elapsed.TotalMilliseconds:F1} ms");
        }

        // A sector unloaded part-way: forget it.
        if (_progress.Count == 0) return;
        _gone.Clear();
        foreach (var (sector, _) in _progress)
            if (!ReferenceEquals(_terrain.Sector(sector.Coord), sector)) _gone.Add(sector);
        for (int i = 0; i < _gone.Count; i++) _progress.Remove(_gone[i]);
    }

    // One chunk: (cx, cz) = (index % chunks, index / chunks); the whole field when it does not divide.
    private void Build(TerrainSector sector, int index, int chunks)
    {
        var heights = sector.Heights;
        int cells = chunks == 0 ? heights.Resolution - 1 : ChunkCells;
        int x0 = chunks == 0 ? 0 : index % chunks * ChunkCells, z0 = chunks == 0 ? 0 : index / chunks * ChunkCells;
        int side = cells + 1;
        // Origin space, not absolute: this mesh has to sit where the simulation currently is, and
        // move with it when the origin does (R6).
        Vector3 origin = _terrain!.CornerOf(sector.Coord);
        float spacing = heights.Spacing;

        if (_vertices.Length < side * side) _vertices = new Vector3[side * side];
        if (_indices.Length < cells * cells * 6) _indices = new int[cells * cells * 6];

        for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++)
                _vertices[z * side + x] = new Vector3((x0 + x) * spacing, heights[x0 + x, z0 + z], (z0 + z) * spacing);

        int n = 0;
        for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                int v = z * side + x;
                _indices[n++] = v; _indices[n++] = v + 1; _indices[n++] = v + side;
                _indices[n++] = v + 1; _indices[n++] = v + side + 1; _indices[n++] = v + side;
            }

        var entity = _world.Create(Transform.At(origin), $"terrain collision {sector.Coord} {index}");
        var body = _space.AddMesh(entity, _vertices.AsSpan(0, side * side), _indices.AsSpan(0, n), origin);
        _world.Add(entity, body);
        _world.Add(entity, new SectorOwned { Sector = sector.Coord });   // unloading the sector takes it
#pragma warning disable SAGE0129 // phase 4g's open world: a sculpt's refresh (#372) builds the collision again
        entity.AddTag<TerrainBuilt>();
#pragma warning restore SAGE0129
        Surfaces(body, heights, x0, z0, cells);
    }

    // What the ground is made of (issue #270): one surface when the sector is all layer 0, else each
    // triangle takes its cell's layer (two triangles a cell, in the order they were built above).
    private void Surfaces(in PhysicsBody body, Heightfield heights, int x0, int z0, int cells)
    {
        var layers = _terrain!.SurfaceLayers;
        if (layers.Count == 0) return;
        if (heights.CellLayers == null) { _space.SetSurface(body, layers[0]); return; }
        int triangles = cells * cells * 2;
        if (_triangleLayers.Length < triangles) _triangleLayers = new byte[triangles];
        for (int t = 0; t < triangles; t++)
        {
            int cell = t / 2;
            byte layer = heights.LayerAt(x0 + cell % cells, z0 + cell / cells);
            _triangleLayers[t] = layer < layers.Count ? layer : (byte)0;
        }
        _space.SetSurfaces(body, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(layers), _triangleLayers.AsSpan(0, triangles));
    }
}

// The engine's physics module (10 §14 steps 1–3): one space per world, the layer record, and the
// tick systems. A headless server loads it exactly like the client does.
[Plugin("sage.physics3d", "0.1.0")]
public sealed class PhysicsModule : IModule
{
    private readonly List<PhysicsSpace> _spaces = new();
    private RecordStore? _records;
    private CVar<bool>? _debugDraw;

    public void Init(ModuleContext ctx)
    {
        _records = ctx.Engine.Records;
        // The `body` prefab part is this plugin's (BodyPart, [PrefabPart]): physics owns the rule that a
        // capsule stands on its point while a box is centred on it (review #44, F31). It is declared in
        // Sage.Simulation beside the other physics data, so a 2D backend owns the same part (issue #30).

        // The joint part's wires (issue #245): Break removes the joint, OnBreak says it went.
        ctx.Engine.Inputs.Register<Joint>(PhysicsJointIO.Break, static (World world, in IOContext io) =>
        {
            ref var joint = ref world.Get<Joint>(io.Self);
            if (joint.Broken) return;
            if (!joint.Handle.IsNull) world.Resources.Get<IPhysicsWorld>().RemoveJoint(joint.Handle);
            joint.Handle = default;
            joint.Broken = true;
            world.IO().Fire(world, io.Self, PhysicsJointIO.OnBreak, io.Activator);
        });
        ctx.Engine.Outputs.Declare(PhysicsJointIO.OnBreak, "This entity's joint broke (its break force, or the Break input); the activator is what it was joined to.");

        _debugDraw = ctx.Engine.CVars.Register("phys_debug", false, CVarFlags.DevOnly,
            "Draw colliders and character capsules (needs r_debugdraw 1).");
        _records.Reloaded += ApplyLayers;

        ctx.Engine.CVars.RegisterCommand("phys_stats", CVarFlags.None, "Physics bodies, statics and step time per world.", _ =>
        {
            foreach (var world in ctx.Engine.Worlds)
            {
                if (!world.Resources.TryGet<PhysicsSpace>(out var space) || space == null) continue;
                Log.Info(LogCat.Console, $"  '{world.Name}': {space.BodyCount} active bodies, {space.StaticCount} statics, " +
                                         $"last step {space.LastStepMilliseconds:F2} ms, gravity {space.Gravity.Y:F2}");
            }
        });
    }

    public void OnWorldCreated(World world)
    {
        var space = new PhysicsSpace { Records = _records };   // surfaces' friction (issue #270)
        world.Resources.Add(space);          // disposed with the world
        world.Resources.Add<IPhysicsWorld>(space);   // what everything else reads (gameplay, levels, I/O)
        _spaces.Add(space);
        ApplyLayers(space);

        world.AddSystem(new TerrainCollisionSystem(world, space));
        world.AddSystem(new PhysicsSyncSystem(world, space));
        world.AddSystem(new PhysicsStepSystem(world, space));
        world.AddSystem(new PhysicsWriteBackSystem(world, space));
        world.AddSystem(new PhysicsEventSystem(space));   // the buffers as game events (issue #269)
        world.AddSystem(new JointSystem(world, space));
        world.AddSystem(new JointBreakSystem(space));
        world.AddSystem(new BuoyancySystem(world, space));   // water volumes float what falls in (issue #262)
        world.AddSystem(new PhysicsDebugSystem(world, _records!, _debugDraw!));   // 10 §9, draws through IPhysicsWorld

        // A destroyed entity takes its body with it.
        world.EntityDestroyed += entity =>
        {
            if (world.TryGet<Joint>(entity, out var joint) && !joint.Handle.IsNull) space.RemoveJoint(joint.Handle);
            if (world.TryGet<PhysicsBody>(entity, out var body))
            {
                space.NoteDestroyed(world, entity, body);   // before the body goes: it knows its compound's parts
                space.RemoveBody(body);
            }
            else space.NoteDestroyed(world, entity, null);
        };
    }

    public void Shutdown()
    {
        if (_records != null) _records.Reloaded -= ApplyLayers;
        _spaces.Clear();
    }

    private void ApplyLayers()
    {
        foreach (var space in _spaces) ApplyLayers(space);
    }

    private void ApplyLayers(PhysicsSpace space)
    {
        if (_records != null && _records.TryGet(PhysicsLayersRecord.Default, out PhysicsLayersRecord layers))
            space.Layers.Apply(layers);
    }
}
