#nullable enable
using System;
using System.Linq;
using System.Numerics;
using Friflo.Engine.ECS;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// Physics (docs/design/10). Everything here runs headless through the PhysicsModule, the same way a
// dedicated server would: create a world, add colliders, run ticks.
public class PhysicsTests
{
    public PhysicsTests() { _ = TestEnv.UserRoot; }

    private static Engine NewEngine()
    {
        return HeadlessApp.Bare().With(new PhysicsModule()).Build().Engine;
    }

    private static void Tick(World world, int ticks, float dt = 1f / 60f)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(dt);
    }

    [Fact]
    public void ADynamicBoxFallsAndRestsOnAStaticOne()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("physics");
        var space = world.Resources.Get<PhysicsSpace>();

        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        world.Add(ground, Collider.Box(new Vector3(20, 1, 20)));

        var crate = world.Create(Transform.At(new Vector3(0, 5, 0)), "crate");
        world.Add(crate, Collider.Box(Vector3.One));
        world.Add(crate, RigidBody.Dynamic(10f));

        Tick(world, 5);
        Assert.Equal(1, space.BodyCount);                       // the dynamic one; statics are separate
        Assert.Equal(1, space.StaticCount);
        Assert.True(world.Get<Transform>(crate).LocalPosition.Y < 5f, "it should be falling");

        Tick(world, 180);
        float restY = world.Get<Transform>(crate).LocalPosition.Y;
        Assert.InRange(restY, 0.45f, 0.6f);                     // half its height above the ground's top
        Assert.True(MathF.Abs(space.VelocityOf(world.Get<PhysicsBody>(crate)).Y) < 0.05f, "it should have settled");
    }

    // Bepu poses a shape by its centre. Anything that stands on its transform — a character, a tree —
    // says so with Collider.Standing, or its collider ends up buried to the waist (review #44).
    [Fact]
    public void AStandingCapsuleIsAnchoredAtItsFeet()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("physics");
        var space = world.Resources.Get<PhysicsSpace>();

        var post = world.Create(Transform.At(new Vector3(3, 0, -2)), "post");
        world.Add(post, Collider.Standing(0.35f, 2.2f));
        Tick(world, 1);

        Assert.Equal(1.1f, space.PoseOf(world.Get<PhysicsBody>(post)).Position.Y, 3);   // the shape's centre
        Assert.Equal(0f, world.Get<Transform>(post).LocalPosition.Y, 3);                // where it stands

        // It really occupies 0..2.2 m: a shin-high ray hits it and nothing is above its head.
        var shin = space.Raycast(new Vector3(3, 0.2f, 6), -Vector3.UnitZ, 20f);
        Assert.True(shin.Hit && shin.Entity == post, "a shin-high ray should hit a capsule standing on the ground");
        Assert.False(space.Raycast(new Vector3(3, 3f, 6), -Vector3.UnitZ, 20f).Hit, "nothing should be above its head");
    }

    [Fact]
    public void ADynamicColliderWithAnOffsetCentreWritesBackTheEntitysPosition()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("physics");
        var space = world.Resources.Get<PhysicsSpace>();

        var faller = world.Create(Transform.At(new Vector3(0, 6, 0)), "faller");
        world.Add(faller, Collider.Standing(0.3f, 1.8f));
        world.Add(faller, RigidBody.Dynamic(5f));
        Tick(world, 10);

        float centre = space.PoseOf(world.Get<PhysicsBody>(faller)).Position.Y;
        float feet = world.Get<Transform>(faller).LocalPosition.Y;
        Assert.True(feet < 6f, "it should be falling");
        Assert.Equal(0.9f, centre - feet, 2);   // the offset is put back on the way out, not baked in
    }

    // A trigger has no surface: it must not stop a ray, a sweep or a sword (review #53). Something
    // that wants to know about overlaps reads the trigger lists, or asks for them explicitly.
    [Fact]
    public void QueriesSeeThroughTriggersUnlessTheyAskForThem()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("physics");
        var space = world.Resources.Get<PhysicsSpace>();

        var curtain = world.Create(Transform.At(new Vector3(0, 1, -2)), "trigger curtain");
        world.Add(curtain, new Collider { Shape = ColliderShape.Box, Size = new Vector3(6, 4, 0.5f), IsTrigger = true });
        var wall = world.Create(Transform.At(new Vector3(0, 1, -5)), "wall");
        world.Add(wall, Collider.Box(new Vector3(6, 4, 0.5f)));
        Tick(world, 1);

        var hit = space.Raycast(new Vector3(0, 1, 2), -Vector3.UnitZ, 20f);
        Assert.True(hit.Hit);
        Assert.Equal(wall, hit.Entity);                       // straight through the trigger

        var seen = space.Raycast(new Vector3(0, 1, 2), -Vector3.UnitZ, 20f, includeTriggers: true);
        Assert.Equal(curtain, seen.Entity);
    }

    [Fact]
    public void DestroyingAnEntityRemovesItsBody()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("physics");
        var space = world.Resources.Get<PhysicsSpace>();

        var crate = world.Create(Transform.At(new Vector3(0, 5, 0)));
        world.Add(crate, Collider.Sphere(0.5f));
        world.Add(crate, RigidBody.Dynamic(1f));
        Tick(world, 2);
        Assert.Equal(1, space.BodyCount);

        world.Destroy(crate);
        Tick(world, 2);
        Assert.Equal(0, space.BodyCount);
    }

    [Fact]
    public void RaycastHitsTheNearestCollider_AndRespectsLayers()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("physics");
        var space = world.Resources.Get<PhysicsSpace>();

        var near = world.Create(Transform.At(new Vector3(0, 0, -5)), "near");
        world.Add(near, Collider.Box(new Vector3(2, 2, 2), layer: 1));
        var far = world.Create(Transform.At(new Vector3(0, 0, -20)), "far");
        world.Add(far, Collider.Box(new Vector3(2, 2, 2), layer: 2));
        Tick(world, 1);

        var hit = space.Raycast(Vector3.Zero, -Vector3.UnitZ, 100f);
        Assert.True(hit.Hit);
        Assert.Equal(near, hit.Entity);
        Assert.Equal(4f, hit.Distance, 2);                       // the box face, 1 m from its centre
        Assert.Equal(1f, hit.Normal.Z, 2);                       // pointing back at the ray

        var filtered = space.Raycast(Vector3.Zero, -Vector3.UnitZ, 100f, LayerMask.Only(2));
        Assert.True(filtered.Hit);
        Assert.Equal(far, filtered.Entity);                      // layer 1 was filtered out

        Assert.False(space.Raycast(Vector3.Zero, Vector3.UnitZ, 100f).Hit);   // nothing behind
    }

    [Fact]
    public void SweepFindsWhatAShapeWouldRunInto()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("physics");
        var space = world.Resources.Get<PhysicsSpace>();

        var wall = world.Create(Transform.At(new Vector3(0, 0, -6)), "wall");
        world.Add(wall, Collider.Box(new Vector3(8, 4, 0.5f)));
        Tick(world, 1);

        var capsule = Collider.Capsule(0.4f, 1.2f);
        var from = new Pose { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One };
        var hit = space.Sweep(capsule, from, -Vector3.UnitZ, 20f);

        Assert.True(hit.Hit);
        Assert.Equal(wall, hit.Entity);
        Assert.InRange(hit.Distance, 5f, 5.5f);                  // 6 m minus the wall's half depth and the radius
    }

    [Fact]
    public void OverlapBoxListsNearbyEntities()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("physics");
        var space = world.Resources.Get<PhysicsSpace>();

        var inside = world.Create(Transform.At(new Vector3(1, 0, 1)), "inside");
        world.Add(inside, Collider.Sphere(0.5f));
        var outside = world.Create(Transform.At(new Vector3(30, 0, 0)), "outside");
        world.Add(outside, Collider.Sphere(0.5f));
        Tick(world, 1);

        var results = new Entity[8];   // Entity holds a store reference, so it can't be stackalloc'd
        int count = space.OverlapBox(Vector3.Zero, new Vector3(5, 5, 5), results);
        Assert.Equal(1, count);
        Assert.Equal(inside, results[0]);
    }

    [Fact]
    public void ATriggerReportsWhatPassesThrough_AndDoesNotPushIt()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("physics");
        var space = world.Resources.Get<PhysicsSpace>();

        var gate = world.Create(Transform.At(new Vector3(0, 2, 0)), "gate");
        world.Add(gate, new Collider { Shape = ColliderShape.Box, Size = new Vector3(4, 0.5f, 4), IsTrigger = true });

        var ball = world.Create(Transform.At(new Vector3(0, 5, 0)), "ball");
        world.Add(ball, Collider.Sphere(0.3f));
        world.Add(ball, RigidBody.Dynamic(1f));

        bool entered = false, exited = false;
        for (int i = 0; i < 120; i++)
        {
            world.RunFixed(1f / 60f);
            foreach (var overlap in space.TriggerEnter)
                if (overlap.Trigger == gate && overlap.Other == ball) entered = true;
            foreach (var overlap in space.TriggerExit)
                if (overlap.Trigger == gate && overlap.Other == ball) exited = true;
        }

        Assert.True(entered, "the ball should have entered the trigger");
        Assert.True(exited, "and left it again");
        Assert.True(world.Get<Transform>(ball).LocalPosition.Y < 0f, "a trigger must not stop it falling");
    }

    [Fact]
    public void ABodyRestsOnTheTerrainsCollisionMesh()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("physics");
        var terrain = new Terrain { Origin = world.Origin(), Generator = new SlopeGenerator() };
        world.Resources.Add(terrain);   // what sage.streaming installs; collision needs only the resource
        terrain.Load(SectorCoord.Zero);

        var ball = world.Create(Transform.At(new Vector3(40, 30, 40)), "ball");
        world.Add(ball, Collider.Sphere(0.5f));
        world.Add(ball, RigidBody.Dynamic(2f));

        Tick(world, 300);

        var position = world.Get<Transform>(ball).LocalPosition;
        float ground = terrain.HeightAt(position.X, position.Z);
        Assert.InRange(position.Y - ground, 0.3f, 0.8f);   // resting on the surface, about its radius above it
    }

    // A gentle slope, so the ball lands on something that isn't flat.
    private sealed class SlopeGenerator : ITerrainGenerator
    {
        public void Generate(SectorCoord sector, Heightfield heights, int seed)
        {
            for (int z = 0; z < heights.Resolution; z++)
                for (int x = 0; x < heights.Resolution; x++)
                    heights[x, z] = x * heights.Spacing * 0.05f;
        }
    }
}

public class PhysicsLayerTests
{
    [Fact]
    public void EverythingCollidesByDefault()
    {
        var matrix = new LayerMatrix();
        Assert.True(matrix.Collide(0, 0));
        Assert.True(matrix.Collide(3, 7));
        Assert.Equal("default", matrix.Name(0));
    }

    [Fact]
    public void IgnoreIsSymmetric_AndNamesResolve()
    {
        var matrix = new LayerMatrix();
        matrix.Apply(new PhysicsLayersRecord
        {
            Layers = { "default", "player", "projectile" },
            Ignore = { ["projectile"] = new() { "player", "projectile" } },
        });

        Assert.Equal(1, matrix.IndexOf("player"));
        Assert.Equal(2, matrix.IndexOf("PROJECTILE"));
        Assert.False(matrix.Collide(2, 1));
        Assert.False(matrix.Collide(1, 2));       // symmetric
        Assert.False(matrix.Collide(2, 2));
        Assert.True(matrix.Collide(0, 1));
        Assert.Equal(-1, matrix.IndexOf("nope"));
    }

    [Fact]
    public void LayerMasks()
    {
        var mask = LayerMask.Only(1, 5);
        Assert.True(mask.Has(1));
        Assert.True(mask.Has(5));
        Assert.False(mask.Has(2));
        Assert.True(LayerMask.All.Has(31));
    }
}
