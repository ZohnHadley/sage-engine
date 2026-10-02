#nullable enable
using System;
using System.Numerics;
using Sage.Physics3D;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Shapes changed in place and colliders on child entities (issue #268): a collider on a child follows its
// parent and is hit where it is, a dynamic body and its children's colliders are one compound, and
// SetShape swaps a body's shape without a new handle and without leaking the old one.
public class ParentedColliderTests
{
    public ParentedColliderTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    private static Engine NewEngine() => HeadlessApp.Gameplay().Build().Engine;

    private static Pose At(Vector3 position) => new() { Position = position, Rotation = Quaternion.Identity, Scale = Vector3.One };

    private static Pose WorldPose(Entity entity) => PhysicsPoses.WorldPose(entity);

    private static Entity Floor(World world)
    {
        var floor = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "floor");
        world.Add(floor, Collider.Box(new Vector3(40, 1, 40)));
        return floor;
    }

    [Fact]
    public void AChildColliderFollowsItsParentAndIsHitByARayWhereItIs()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("parented");
        var physics = world.Resources.Get<IPhysicsWorld>();

        // A parent with no collider of its own (a cart, a socket), and a box on a child two metres to its +X.
        var parent = world.Create(Transform.At(Vector3.Zero), "parent");
        var child = world.Create(Transform.At(new Vector3(2, 1, 0)), "child");
        world.SetParent(child, parent);
        world.Add(child, Collider.Box(new Vector3(0.5f)));
        world.RunFixed(Dt);

        Assert.True(world.Has<PhysicsBody>(child));
        Assert.False(world.Get<PhysicsBody>(child).IsStatic);   // it moves with its parent, so it is not a static
        var hit = physics.Raycast(new Vector3(2, 1, -5), Vector3.UnitZ, 20f);
        Assert.True(hit.Hit);
        Assert.Equal(child, hit.Entity);
        Assert.InRange(hit.Position.Z, -0.26f, -0.24f);

        // The parent moves to x = 10 and turns a quarter left: the child's local +X becomes world -Z.
        ref var transform = ref world.Get<Transform>(parent);
        transform.LocalPosition = new Vector3(10, 0, 0);
        transform.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        world.RunFixed(Dt);

        Assert.False(physics.Raycast(new Vector3(2, 1, -5), Vector3.UnitZ, 20f).Hit);   // gone from where it was
        hit = physics.Raycast(new Vector3(15, 1, -2), -Vector3.UnitX, 20f);
        Assert.True(hit.Hit);
        Assert.Equal(child, hit.Entity);
        Assert.InRange(hit.Position.X, 10.24f, 10.26f);
        Assert.InRange(hit.Position.Z, -2.01f, -1.99f);
        Assert.InRange(Vector3.Distance(WorldPose(child).Position, new Vector3(10, 1, -2)), 0f, 1e-4f);

        // A query from the parent leaves its own child's collider out: a swing passes its own shield.
        Assert.False(physics.Raycast(new Vector3(15, 1, -2), -Vector3.UnitX, 20f, ignore: parent).Hit);
    }

    [Fact]
    public void ADynamicBodyAndItsChildrensCollidersAreOneCompound()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("compound");
        var physics = world.Resources.Get<IPhysicsWorld>();
        Floor(world);

        // A crate with a box on a child one metre to its side, both spawned in the same tick.
        var crate = world.Create(Transform.At(new Vector3(0, 2, 0)), "crate");
        world.Add(crate, Collider.Box(new Vector3(0.5f)));
        world.Add(crate, RigidBody.Dynamic(4f));
        var handle = world.Create(Transform.At(new Vector3(1, 0, 0)), "handle");
        world.SetParent(handle, crate);
        world.Add(handle, Collider.Box(new Vector3(0.5f)));
        world.RunFixed(Dt);

        Assert.False(world.Has<PhysicsBody>(handle));   // no body of its own: part of the crate's
        Assert.True(world.Has<ColliderPart>(handle));

        // It falls as one and lands level on both boxes.
        for (int i = 0; i < 180; i++) world.RunFixed(Dt);
        var cratePose = WorldPose(crate);
        Assert.InRange(cratePose.Position.Y, 0.2f, 0.3f);
        Assert.InRange(WorldPose(handle).Position.Y, 0.2f, 0.3f);

        // A ray names the part it hit.
        var at = WorldPose(handle).Position;
        var hit = physics.Raycast(at + new Vector3(0, 5, 0), -Vector3.UnitY, 10f);
        Assert.True(hit.Hit);
        Assert.Equal(handle, hit.Entity);
        Assert.InRange(hit.Position.Y, at.Y + 0.24f, at.Y + 0.26f);
        hit = physics.Raycast(cratePose.Position + new Vector3(0, 5, 0), -Vector3.UnitY, 10f);
        Assert.Equal(crate, hit.Entity);

        // Destroying the part takes it out of the compound.
        world.Destroy(handle);
        world.RunFixed(Dt);
        hit = physics.Raycast(at + new Vector3(0, 5, 0), -Vector3.UnitY, 10f);
        Assert.True(hit.Hit);
        Assert.NotEqual(crate, hit.Entity);   // the floor, through where the handle was
    }

    [Fact]
    public void ADynamicBodyOnAChildEntityWritesBackItsLocalTransform()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("dynamic-child");
        Floor(world);
        var group = world.Create(Transform.At(new Vector3(5, 0, 3)), "group");
        var ball = world.Create(Transform.At(new Vector3(1, 2, 0)), "ball");
        world.SetParent(ball, group);
        world.Add(ball, Collider.Sphere(0.25f));
        world.Add(ball, RigidBody.Dynamic(1f));
        for (int i = 0; i < 120; i++) world.RunFixed(Dt);

        var body = world.Resources.Get<IPhysicsWorld>().PoseOf(world.Get<PhysicsBody>(ball));
        Assert.InRange(Vector3.Distance(WorldPose(ball).Position, body.Position), 0f, 1e-3f);
        Assert.InRange(body.Position.X, 5.9f, 6.1f);   // fell where it started, not 5 m off
        Assert.InRange(body.Position.Y, 0.2f, 0.3f);
        Assert.InRange(world.Get<Transform>(ball).LocalPosition.X, 0.9f, 1.1f);
    }

    [Fact]
    public void SetShapeShrinksACharactersCapsuleInPlace()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("crouch");
        var physics = world.Resources.Get<IPhysicsWorld>();
        Floor(world);
        var character = world.Create(Transform.At(Vector3.Zero), "character");
        world.AddCharacter(character, 1);
        world.RunFixed(Dt);

        var body = world.Get<PhysicsBody>(character);
        var standing = world.Get<Collider>(character);
        Assert.True(physics.Raycast(new Vector3(0, 1.6f, -3), Vector3.UnitZ, 6f).Hit);   // head height

        // Crouched to a metre: the head-height ray passes over it, a knee-height one still stops.
        var crouched = Collider.Standing(standing.Size.X, 1.0f, standing.Layer);
        physics.SetShape(body, crouched, At(Vector3.Zero));
        world.Get<Collider>(character) = crouched;
        Assert.False(physics.Raycast(new Vector3(0, 1.6f, -3), Vector3.UnitZ, 6f).Hit);
        var knee = physics.Raycast(new Vector3(0, 0.5f, -3), Vector3.UnitZ, 6f);
        Assert.True(knee.Hit);
        Assert.Equal(character, knee.Entity);
        world.RunFixed(Dt);
        Assert.Equal(body, world.Get<PhysicsBody>(character));   // the same handle
        Assert.False(physics.Raycast(new Vector3(0, 1.6f, -3), Vector3.UnitZ, 6f).Hit);

        // Standing back up, every tick for a while, a little taller each time: nothing piles up.
        var space = (PhysicsSpace)physics;
        int capacity = space.Simulation.Shapes[BepuPhysics.Collidables.Capsule.Id].Capacity;
        for (int i = 0; i <= 500; i++)
            physics.SetShape(body, Collider.Standing(standing.Size.X, 1.0f + 0.8f * i / 500f, standing.Layer), At(Vector3.Zero));
        Assert.Equal(capacity, space.Simulation.Shapes[BepuPhysics.Collidables.Capsule.Id].Capacity);
        Assert.True(physics.Raycast(new Vector3(0, 1.6f, -3), Vector3.UnitZ, 6f).Hit);
    }

    [Fact]
    public void SetShapeReshapesAStaticAndReleasesItsHull()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("reshape");
        var physics = world.Resources.Get<IPhysicsWorld>();
        var entity = world.Create(Transform.At(new Vector3(0, 0, 0)), "brush");
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++) corners[i] = new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f);
        var body = physics.AddHull(entity, corners, new Vector3(3, 0, 0));
        Assert.InRange(physics.Raycast(new Vector3(3, 0, -5), Vector3.UnitZ, 10f).Distance, 4.49f, 4.51f);

        // A sphere of two metres, on a trigger layer now: solid queries no longer see it.
        var sphere = Collider.Sphere(2f, 3);
        physics.SetShape(body, sphere, At(new Vector3(3, 0, 0)));
        var hit = physics.Raycast(new Vector3(3, 0, -5), Vector3.UnitZ, 10f);
        Assert.InRange(hit.Distance, 2.99f, 3.01f);
        Assert.False(physics.Raycast(new Vector3(3, 0, -5), Vector3.UnitZ, 10f, LayerMask.Only(0)).Hit);
        Assert.True(physics.Raycast(new Vector3(3, 0, -5), Vector3.UnitZ, 10f, LayerMask.Only(3)).Hit);

        // Moving the static no longer adds the hull's offset: the hull, the static's own, is gone.
        physics.MoveStatic(body, new Vector3(6, 0, 0));
        Assert.InRange(physics.PoseOf(body).Position.X, 5.99f, 6.01f);
        physics.RemoveBody(body);
        Assert.False(physics.Raycast(new Vector3(6, 0, -5), Vector3.UnitZ, 10f, LayerMask.Only(3)).Hit);
    }
}
