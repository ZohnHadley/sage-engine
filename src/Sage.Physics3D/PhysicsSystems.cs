#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Physics3D;

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
    private readonly List<Entity> _toAdd = new();

    public PhysicsSyncSystem(World world, PhysicsSpace space)
    {
        _space = space;
        _pending = world.Query<Transform, Collider>().WithoutComponent<PhysicsBody>();
        _bodies = world.Query<Transform, Collider, PhysicsBody>();
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;

        // New colliders: collect first, then add the components (adding changes the archetypes we're
        // iterating).
        _toAdd.Clear();
        foreach (var (_, _, entities) in _pending.Chunks)
            for (int n = 0; n < entities.Length; n++)
                _toAdd.Add(entities.EntityAt(n));

        foreach (var entity in _toAdd)
        {
            if (!world.IsAlive(entity)) continue;
            var collider = world.Get<Collider>(entity);
            var body = world.TryGet<RigidBody>(entity, out var rigid) ? rigid : new RigidBody { Kind = BodyKind.Static };
            var pose = world.Has<GlobalTransform>(entity)
                ? world.Get<GlobalTransform>(entity).Current
                : Pose.FromLocal(world.Get<Transform>(entity));
            world.Add(entity, _space.AddBody(entity, collider, body, pose));
        }

        // Kinematic bodies (and anything gameplay moved directly) follow their transform. A root's local
        // transform is its world one; a parented kinematic collider — a hitbox on a creature's bone
        // (issue #137) — is composed up its parents' transforms as they are now, not GlobalTransform,
        // which is last tick's: the owner has already moved this tick (sage.character.move runs first).
        // Dynamic bodies are still assumed to be roots (write-back sets the local transform).
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
                _space.SetPose(h[n], c[n], parent.IsNull ? Pose.FromLocal(t[n]) : Pose.Combine(WorldPose(parent), Pose.FromLocal(t[n])));
            }
        }
    }

    // An entity's world pose from its transform and its parents', as of now.
    private static Pose WorldPose(Entity entity)
    {
        var pose = entity.TryGetComponent<Transform>(out var local) ? Pose.FromLocal(local) : Pose.Identity;
        for (var parent = entity.Parent; !parent.IsNull; parent = parent.Parent)
            if (parent.TryGetComponent<Transform>(out var above)) pose = Pose.Combine(Pose.FromLocal(above), pose);
        return pose;
    }
}

// Physics: one Bepu step per tick.
[System("sage.physics.step", Phase.Physics)]
internal sealed class PhysicsStepSystem : ISystem
{
    private readonly PhysicsSpace _space;

    public PhysicsStepSystem(PhysicsSpace space) { _space = space; }

    public void Run(in SystemContext ctx) => _space.Step(ctx.Tick.Dt);
}

// PostPhysics: dynamic bodies win over their transform, and trigger overlaps become readable.
[System("sage.physics.write_back", Phase.PostPhysics)]
internal sealed class PhysicsWriteBackSystem : ISystem
{
    private readonly PhysicsSpace _space;
    private readonly Query<Transform, Collider, PhysicsBody> _bodies;

    public PhysicsWriteBackSystem(World world, PhysicsSpace space)
    {
        _space = space;
        _bodies = world.Query<Transform, Collider, PhysicsBody>();
    }

    public void Run(in SystemContext ctx)
    {
        foreach (var (transforms, colliders, handles, _) in _bodies.Chunks)
        {
            var t = transforms.Span;
            var c = colliders.Span;
            var h = handles.Span;
            for (int n = 0; n < t.Length; n++)
            {
                if (!_space.IsDynamic(h[n])) continue;
                var pose = _space.PoseOf(h[n]);
                t[n].LocalRotation = pose.Rotation;
                // Bepu poses the shape's centre; the transform is the entity's origin (review #44).
                t[n].LocalPosition = c[n].Center == Vector3.Zero
                    ? pose.Position
                    : pose.Position - Vector3.Transform(c[n].Center, pose.Rotation);
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

// PrePhysics: gives loaded terrain sectors a collision mesh (14 §3, closes F13's collision gap).
// One static mesh per sector; with streaming this becomes per chunk and is built on a job (F14).
#pragma warning disable SAGE0133 // as sage.physics.sync: the ground is pickable in the editor too (#219)
[System("sage.physics.terrain", Phase.PrePhysics, Condition = RunCondition.EvenWhenEditing)]
#pragma warning restore SAGE0133
internal sealed class TerrainCollisionSystem : ISystem
{
    private readonly PhysicsSpace _space;
    private Terrain? _terrain;
    private readonly World _world;
    private Vector3[] _vertices = Array.Empty<Vector3>();
    private int[] _indices = Array.Empty<int>();

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
        for (int i = 0; i < sectors.Count; i++)
        {
            var sector = sectors[i];
            if (sector.CollisionBuilt) continue;
            Build(sector);
            sector.CollisionBuilt = true;
        }
    }

    private void Build(TerrainSector sector)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var heights = sector.Heights;
        int side = heights.Resolution, cells = side - 1;
        // Origin space, not absolute: this mesh has to sit where the simulation currently is, and
        // move with it when the origin does (R6).
        Vector3 origin = _terrain!.CornerOf(sector.Coord);
        float spacing = heights.Spacing;

        if (_vertices.Length < side * side) _vertices = new Vector3[side * side];
        if (_indices.Length < cells * cells * 6) _indices = new int[cells * cells * 6];

        for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++)
                _vertices[z * side + x] = new Vector3(x * spacing, heights[x, z], z * spacing);

        int index = 0;
        for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                int v = z * side + x;
                _indices[index++] = v; _indices[index++] = v + 1; _indices[index++] = v + side;
                _indices[index++] = v + 1; _indices[index++] = v + side + 1; _indices[index++] = v + side;
            }

        var entity = _world.Create(Transform.At(origin), $"terrain collision {sector.Coord}");
        var body = _space.AddMesh(entity, _vertices.AsSpan(0, side * side), _indices.AsSpan(0, index), origin);
        _world.Add(entity, body);
        _world.Add(entity, new SectorOwned { Sector = sector.Coord });   // unloading the sector takes it
        Log.Info(LogCat.Physics, $"Terrain sector {sector.Coord}: collision mesh with {index / 3} triangles in {watch.Elapsed.TotalMilliseconds:F1} ms");
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
        var space = new PhysicsSpace();
        world.Resources.Add(space);          // disposed with the world
        world.Resources.Add<IPhysicsWorld>(space);   // what everything else reads (gameplay, levels, I/O)
        _spaces.Add(space);
        ApplyLayers(space);

        world.AddSystem(new TerrainCollisionSystem(world, space));
        world.AddSystem(new PhysicsSyncSystem(world, space));
        world.AddSystem(new PhysicsStepSystem(space));
        world.AddSystem(new PhysicsWriteBackSystem(world, space));
        world.AddSystem(new PhysicsDebugSystem(world, _records!, _debugDraw!));   // 10 §9, draws through IPhysicsWorld

        // A destroyed entity takes its body with it.
        world.EntityDestroyed += entity =>
        {
            if (world.TryGet<PhysicsBody>(entity, out var body)) space.RemoveBody(body);
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
