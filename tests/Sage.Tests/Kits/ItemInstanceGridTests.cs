#nullable enable
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The RPG kit's grid and loot screen with item instances (issue #383): splitting, dropping, moving and
// taking all keep each stack's instance, and a named one is shown by its name.
#pragma warning disable SAGE0125   // the RPG screens are experimental (MAKING_A_GAME §10b)
public class ItemInstanceGridTests
{
    public ItemInstanceGridTests() { _ = TestEnv.UserRoot; }

    private static RecordId Id(string name) => new("sage", name);

    private static Entity Carrier(World world, string name)
    {
        var entity = world.Create(Transform.At(Vector3.Zero), name);
        world.AddInventory(entity, 0f);
        return entity;
    }

    [Fact]
    public void TheGridSplitsDropsAndMovesAStackWithItsInstance()
    {
        using var app = RpgScreenTests.Boot();
        var world = app.World;
        var hero = Carrier(world, "hero");
        var chest = Carrier(world, "chest");
        world.Give(hero, Id("coin"), 10);
        world.Give(hero, Id("coin"), new ItemInstance { Name = "gilded coin" }, 6);

        var bag = new ItemGrid(4, 3);
        bag.Refresh(world, hero);
        var gilded = bag.Items.Single(i => i.Label.Contains("gilded coin"));
        Assert.True(bag.Split(world, gilded, out _));
        var stacks = world.Get<Inventory>(hero).Items;
        Assert.Equal(2, stacks.Count(s => s.Instance?.Name == "gilded coin"));   // 3 and 3, both still gilded
        Assert.Equal(16, world.CountOf(hero, Id("coin")));

        var half = bag.Items.Last(i => i.Label.Contains("gilded coin"));
        Assert.True(bag.Drop(world, half, out _));
        var pile = Assert.Single(world.Query<Pickup>().Entities.ToEntityList());
        Assert.Equal("gilded coin", world.Get<Pickup>(pile).Instance!.Name);
        Assert.Equal(3, world.Get<Pickup>(pile).Count);

        var box = new ItemGrid(4, 3);
        box.Refresh(world, chest);
        var rest = bag.Items.Single(i => i.Label.Contains("gilded coin"));
        Assert.True(ItemGrid.Transfer(world, rest, box, -1, -1, out _));
        var moved = Assert.Single(world.Get<Inventory>(chest).Items);
        Assert.Equal("gilded coin", moved.Instance!.Name);
        Assert.Equal(10, world.CountOf(hero, Id("coin")));
    }

    [Fact]
    public void TakeAllKeepsEachStacksInstance()
    {
        using var app = RpgScreenTests.Boot();
        var world = app.World;
        var hero = Carrier(world, "hero");
        var corpse = Carrier(world, "corpse");
        world.Give(corpse, Id("sword"));
        world.Give(corpse, Id("sword"), new ItemInstance { Name = "Dawnblade" });
        world.Give(corpse, Id("bread"), 2);

        var loot = new LootView();
        Assert.Equal(3, loot.TakeAll(world, corpse, hero));
        Assert.Empty(world.Get<Inventory>(corpse).Items);
        var stacks = world.Get<Inventory>(hero).Items;
        Assert.Equal(2, world.CountOf(hero, Id("sword")));
        Assert.Single(stacks, s => s.Instance?.Name == "Dawnblade");
    }
}
#pragma warning restore SAGE0125
