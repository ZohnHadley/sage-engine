#nullable enable
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Two worlds in one engine (issue #286): each keeps its own resources and entities, and a save writes
// one file per world and a load puts each back into its own.
public class MultiWorldTests
{
    public MultiWorldTests() { _ = TestEnv.UserRoot; }

    private static Entity Placed(World world, string name, float x, float health)
    {
        var e = world.Create(Transform.At(new Vector3(x, 0, 0)), name);
        world.Add(e, new Health { Current = health });
        world.MakePersistent(e);
        return e;
    }

    [Xunit.Fact]
    public void TwoWorldsKeepTheirOwnClocksAndEntities_AcrossASaveAndALoad()
    {
        using var app = HeadlessApp.Simulation().Build();
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var overworld = app.CreateWorld("overworld");
        var battle = app.CreateWorld("battle");
        Placed(overworld, "hero", 1, 100);
        Placed(battle, "ogre", 7, 40);
        WorldClock.Of(overworld).Day = 3;
        WorldClock.Of(battle).Day = 9;
        overworld.RunFixed(1f / 60f);
        battle.RunFixed(1f / 60f);

        Assert.True(app.Engine.Saves.Save("two"));
        string dir = Path.Combine(app.Engine.Saves.Root, "two");
        Assert.True(File.Exists(Path.Combine(dir, "world_overworld.json")));
        Assert.True(File.Exists(Path.Combine(dir, "world_battle.json")));

        // Change both, then load: each world gets back what it saved and none of the other's.
        overworld.Get<Health>(overworld.FindByName("hero")).Current = 1;
        battle.Destroy(battle.FindByName("ogre"));
        WorldClock.Of(overworld).Day = 50;
        WorldClock.Of(battle).Day = 60;

        Assert.True(app.Engine.Saves.Load("two"));

        Assert.Equal(100f, overworld.Get<Health>(overworld.FindByName("hero")).Current);
        Assert.Equal(40f, battle.Get<Health>(battle.FindByName("ogre")).Current);
        Assert.True(overworld.FindByName("ogre").IsNull);
        Assert.True(battle.FindByName("hero").IsNull);
        Assert.Equal(3, WorldClock.Of(overworld).Day);
        Assert.Equal(9, WorldClock.Of(battle).Day);
        Assert.Equal(1, overworld.Query<Persistent>().Count);
        Assert.Equal(1, battle.Query<Persistent>().Count);
    }

    [Xunit.Fact]
    public void ResourcesAreIsolatedPerWorld_AndATickInOneLeavesTheOtherAlone()
    {
        using var app = HeadlessApp.Simulation().Build();
        var a = app.CreateWorld("a");
        var b = app.CreateWorld("b");
        Assert.NotSame(WorldClock.Of(a), WorldClock.Of(b));

        a.RunFixed(1f / 60f);
        a.RunFixed(1f / 60f);
        Assert.Equal(2, a.Tick - b.Tick);
        Assert.Equal(0, b.Tick);
    }
}
