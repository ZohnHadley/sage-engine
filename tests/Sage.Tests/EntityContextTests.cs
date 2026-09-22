using sage_engine;

// EntityContext and EntityContextListener are still process-wide singletons (until TODO R1),
// so tests that touch them must not run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace sage_engine.Tests;

// The engine has its own `Assert` (Assert.Dev/Ensure/Check); inside this namespace it would hide xUnit's.
using Assert = Xunit.Assert;

public class EntityContextTests
{
    private sealed class TestTag : IComponent { }

    private static EntityContext Context => EntityContext.getInstance();

    [Fact]
    public void CreateEntity_AddsTransformAndRegisters()
    {
        var entity = Context.createEntity("test");
        try
        {
            Assert.Same(entity, Context.getEntity(entity.getId()));
            Assert.NotNull(entity.getComponent<ComponentTransform>());
        }
        finally
        {
            Context.removeEntity(entity);
        }
        Assert.Null(Context.getEntity(entity.getId()));
    }

    [Fact]
    public void Events_FireInDocumentedOrder()
    {
        var log = new List<string>();
        void Added(Entity e) => log.Add("entity+");
        void Removed(Entity e) => log.Add("entity-");
        void CompAdded(Entity e, IComponent c) => log.Add("+" + c.GetType().Name);
        void CompRemoved(Entity e, IComponent c) => log.Add("-" + c.GetType().Name);

        Context.OnEntityAdded += Added;
        Context.OnEntityRemoved += Removed;
        Context.OnComponentAdded += CompAdded;
        Context.OnComponentRemoved += CompRemoved;
        try
        {
            var entity = Context.createEntity();
            Context.addComponentFor(entity, new TestTag());
            Context.removeEntity(entity);
        }
        finally
        {
            Context.OnEntityAdded -= Added;
            Context.OnEntityRemoved -= Removed;
            Context.OnComponentAdded -= CompAdded;
            Context.OnComponentRemoved -= CompRemoved;
        }

        // OnEntityAdded before any OnComponentAdded; OnEntityRemoved after every OnComponentRemoved.
        Assert.Equal("entity+", log[0]);
        Assert.Equal("+ComponentTransform", log[1]);
        Assert.Equal("+TestTag", log[2]);
        Assert.Equal("entity-", log[^1]);
        Assert.Contains("-ComponentTransform", log);
        Assert.Contains("-TestTag", log);
        Assert.True(log.IndexOf("-TestTag") < log.Count - 1);
    }

    [Fact]
    public void ArchetypeView_TracksMembershipLive()
    {
        var view = EntityContextListener.getInstance().Track("ComponentTransform", "TestTag");
        var entity = Context.createEntity();
        try
        {
            Assert.DoesNotContain(entity, view.Entities);

            var tag = new TestTag();
            Context.addComponentFor(entity, tag);
            Assert.Contains(entity, view.Entities);

            Context.removeComponentFor(entity, tag);
            Assert.DoesNotContain(entity, view.Entities);

            Context.addComponentFor(entity, new TestTag());
        }
        finally
        {
            Context.removeEntity(entity);
        }
        Assert.DoesNotContain(entity, view.Entities);
    }
}
