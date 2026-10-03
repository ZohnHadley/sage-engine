#nullable enable
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Parents and children when the parent is destroyed during a query (issue #286). A destroy inside a loop is
// a command (`world.Commands.Destroy`), applied at the flush, so the loop sees every entity it started with.
public class HierarchyTests
{
    public HierarchyTests() { _ = TestEnv.UserRoot; }

    [Xunit.Fact]
    public void DestroyingAParentInsideAQueryIsDeferred_AndTheLoopStillSeesItsChildren()
    {
        using var world = new World("hierarchy");
        var parent = world.Create(Transform.At(new Vector3(5, 0, 0)), "rider");
        var a = world.Create(Transform.At(Vector3.Zero), "sword");
        var b = world.Create(Transform.At(Vector3.Zero), "shield");
        foreach (var e in new[] { parent, a, b }) world.Add(e, new Health());
        world.SetParent(a, parent);
        world.SetParent(b, parent);

        var seen = new List<string>();
        foreach (var e in world.Query<Health>().Entities)
        {
            seen.Add(e.Name ?? "");
            if (e == parent) world.Commands.Destroy(e);
            Assert.True(world.IsAlive(parent));        // nothing changed under the loop
            Assert.Equal(2, parent.ChildCount);
        }
        Assert.Equal(3, seen.Count);

        world.FlushCommands();
        Assert.False(world.IsAlive(parent));
    }

    // Children a game parented by hand are not tagged FromParentPrefab: they outlive the parent as roots
    // (World.NoteOrphans), with their place in the world kept.
    [Xunit.Fact]
    public void HandParentedChildrenOutliveADestroyedParentAsRoots()
    {
        using var world = new World("hierarchy");
        var parent = world.Create(Transform.At(new Vector3(5, 0, 0)), "rider");
        var child = world.Create(Transform.At(new Vector3(1, 0, 0)), "sword");
        world.SetParent(child, parent);
        world.RunFixed(0.1f);

        foreach (var _ in world.Query<Transform>().Entities) { }
        world.Commands.Destroy(parent);
        world.FlushCommands();

        Assert.False(world.IsAlive(parent));
        Assert.True(world.IsAlive(child));
        Assert.True(child.Parent.IsNull);
        world.RunFixed(0.1f);                             // and it still ticks
    }

    [Xunit.Fact]
    public void ChildrenAPrefabPlacedGoWithTheirParent_WhetherDestroyedDirectlyOrFromACommand()
    {
        foreach (bool viaCommand in new[] { false, true })
        {
            using var world = new World("hierarchy");
            var parent = world.Create(Transform.At(Vector3.Zero), "rider");
            var child = world.Create(Transform.At(Vector3.Zero), "sword");
            var loose = world.Create(Transform.At(Vector3.Zero), "pebble");
            world.SetParent(child, parent);
            world.SetParent(loose, parent);
            child.AddTag<FromParentPrefab>();

            if (viaCommand) { world.Commands.Destroy(parent); world.FlushCommands(); }
            else world.Destroy(parent);

            Assert.False(world.IsAlive(parent));
            Assert.False(world.IsAlive(child));           // placed by the prefab: gone with it
            Assert.True(world.IsAlive(loose));            // parented by hand: stays
        }
    }

    [Xunit.Fact]
    public void DestroyingAChildLeavesTheParentWithoutIt()
    {
        using var world = new World("hierarchy");
        var parent = world.Create("rider");
        var a = world.Create("sword");
        var b = world.Create("shield");
        world.SetParent(a, parent);
        world.SetParent(b, parent);

        world.Commands.Destroy(a);
        world.FlushCommands();

        Assert.Equal(1, parent.ChildCount);
        Assert.True(world.IsAlive(b));
        Assert.Equal(parent, b.Parent);
    }
}
