#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Friflo.Engine.ECS;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

public struct Health : IComponent { public float Current; }
public struct Velocity : IComponent { public Vector3 Value; }

// World over Friflo.Engine.ECS (docs/design/03). Requirements E1–E9 from the spike, the notification
// ordering guarantees (04 §3.3), and review items #15, #33, #38.
[Collection(ProcessWideStateCollection.Name)]
public class WorldTests
{
    public WorldTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void Create_AddsIdentityTransformAndName()
    {
        using var world = new World("test");
        var e = world.Create("bunny");

        Assert.True(world.IsAlive(e));
        Assert.Equal(Transform.Identity, world.Get<Transform>(e));
        Assert.Equal("bunny", e.GetComponent<EntityName>().value);
        Assert.Equal("bunny (" + e.Id + ")", World.Describe(e));
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void StaleHandle_IsDetected_EvenWhenTheIdIsReused()   // E1
    {
        using var world = new World("test");
        var old = world.Create();
        int id = old.Id;
        world.Destroy(old);
        var reused = world.Create();

        Assert.Equal(id, reused.Id);   // Friflo reuses the slot...
        Assert.False(world.IsAlive(old));   // ...but the old handle knows it's dead
        Assert.True(world.IsAlive(reused));
        Assert.False(world.TryGet(old, out Transform _));
        Assert.False(world.Has<Transform>(old));
    }

    [Fact]
    public void Get_ReturnsRef_MissingComponentThrowsInDevBuilds()
    {
        using var world = new World("test");
        var e = world.Create();
        world.Get<Transform>(e).LocalPosition = new Vector3(4, 5, 6);
        Assert.Equal(new Vector3(4, 5, 6), world.Get<Transform>(e).LocalPosition);

        Assert.Throws<InvalidOperationException>(() => world.Get<Health>(e));
    }

    [Fact]
    public void Add_RefusesDuplicates_Remove_ValidatesFirst()   // #33
    {
        using var world = new World("test");
        var e = world.Create();
        Assert.True(world.Add(e, new Health { Current = 10 }));
        Assert.False(world.Add(e, new Health { Current = 99 }));   // never silently replaces
        Assert.Equal(10, world.Get<Health>(e).Current);

        var removed = new List<Type>();
        world.ComponentRemoved += (_, t) => removed.Add(t);
        Assert.False(world.Remove<Velocity>(e));   // missing: no side effects, no event
        Assert.Empty(removed);
        Assert.True(world.Remove<Health>(e));
        Assert.Equal(new[] { typeof(Health) }, removed);
        Assert.False(world.Has<Health>(e));
    }

    [Fact]
    public void Notifications_FollowTheDocumentedOrder()   // 04 §3.3
    {
        using var world = new World("test");
        var log = new List<string>();
        world.EntitySpawned += _ => log.Add("spawned");
        world.ComponentAdded += (_, t) => log.Add("+" + t.Name);
        world.ComponentRemoved += (_, t) => log.Add("-" + t.Name);
        world.EntityDestroyed += _ => log.Add("destroyed");

        var e = world.Create();
        world.Add(e, new Health());
        world.Destroy(e);

        Assert.Equal(new[] { "spawned", "+Transform", "+GlobalTransform", "+Health" }, log.Take(4));
        Assert.Equal("destroyed", log[^1]);
        Assert.Contains("-Transform", log);
        Assert.Contains("-GlobalTransform", log);
        Assert.Contains("-Health", log);
        Assert.True(log.IndexOf("-Health") < log.IndexOf("destroyed"));
    }

    [Fact]
    public void BufferedDestroy_ReportsTheSameNotifications()
    {
        using var world = new World("test");
        var e = world.Create();
        world.Add(e, new Health());
        var log = new List<string>();
        world.ComponentRemoved += (_, t) => log.Add("-" + t.Name);
        world.EntityDestroyed += _ => log.Add("destroyed");

        foreach (var entity in world.Query<Health>().Entities)
            world.Commands.DeleteEntity(entity.Id);   // structural change inside a loop: buffered
        Assert.True(world.IsAlive(e));
        world.FlushCommands();

        Assert.False(world.IsAlive(e));
        Assert.Equal("destroyed", log[^1]);
        Assert.Contains("-Health", log);
    }

    [Fact]
    public void TypedQueries_TrackMembershipLive()   // #38, replaces ArchetypeView
    {
        using var world = new World("test");
        var query = world.Query<Transform, Health>();
        var a = world.Create();
        var b = world.Create();
        Assert.Equal(0, query.Count);

        world.Add(a, new Health { Current = 1 });
        world.Add(b, new Health { Current = 2 });
        Assert.Equal(2, query.Count);

        world.Remove<Health>(a);
        Assert.Equal(1, query.Count);
        float sum = 0;
        foreach (var (_, healths, _) in query.Chunks)
            foreach (var h in healths.Span) sum += h.Current;
        Assert.Equal(2, sum);

        world.Destroy(b);
        Assert.Equal(0, query.Count);
    }

    [Fact]
    public void ChunkIteration_DoesNotAllocate()   // E4
    {
        using var world = new World("test");
        for (int i = 0; i < 1000; i++)
        {
            var e = world.Create();
            world.Add(e, new Velocity { Value = Vector3.UnitX });
        }
        var query = world.Query<Transform, Velocity>();
        void Step()
        {
            foreach (var (transforms, velocities, _) in query.Chunks)
            {
                var t = transforms.Span; var v = velocities.Span;
                for (int n = 0; n < t.Length; n++) t[n].LocalPosition += v[n].Value;
            }
        }
        Step();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int k = 0; k < 50; k++) Step();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void PersistentIds_ResolveAndFollowDestruction()   // E2
    {
        using var world = new World("test");
        var e = world.Create();
        var id = world.MakePersistent(e);
        Assert.Equal(id, world.MakePersistent(e));   // idempotent
        Assert.Equal(e, world.Resolve(id));

        world.Destroy(e);
        Assert.True(world.Resolve(id).IsNull);
        Assert.True(world.Resolve(PersistentId.New()).IsNull);
    }

    [Fact]
    public void Hierarchy_SetAndClearParent()   // E6
    {
        using var world = new World("test");
        var parent = world.Create("rider");
        var child = world.Create("sword");
        world.SetParent(child, parent);
        Assert.Equal(parent, child.Parent);
        world.ClearParent(child);
        Assert.True(child.Parent.IsNull);
    }

    [Fact]
    public void Worlds_AreIndependent_AndResourcesDispose()   // E8
    {
        var app = HeadlessApp.Bare().Build();
        var engine = app.Engine;
        var overworld = app.CreateWorld("overworld");
        var battle = app.CreateWorld("battle");
        overworld.Create(); overworld.Create(); battle.Create();
        Assert.Equal(2, overworld.EntityCount);
        Assert.Equal(1, battle.EntityCount);

        var resource = new DisposableResource();
        battle.Resources.Add(resource);
        Assert.Same(resource, battle.Resources.Get<DisposableResource>());
        Assert.Throws<InvalidOperationException>(() => overworld.Resources.Get<DisposableResource>());

        engine.DestroyWorld(battle);
        Assert.True(resource.Disposed);
        Assert.Single(engine.Worlds);
        app.Dispose();
        LogCat.DefaultLevel = LogLevel.Info;   // CoreCVars.Register applied developer defaults
    }

    private sealed class DisposableResource : IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }
}
