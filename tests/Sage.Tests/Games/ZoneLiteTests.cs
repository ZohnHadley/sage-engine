#nullable enable
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Phase 5's third sample (issue #578): games/Samples/ZoneLite, a S.T.A.L.K.E.R.-style zone in data on the RPG kit.
// The player's input is a PlayerCommand, as the client's is; walking is a teleport, journeys are Travel.To.
#pragma warning disable SAGE0127, SAGE0129   // magazines (4e); the open world (4g)
public class ZoneLiteTests
{
    public ZoneLiteTests() { _ = TestEnv.UserRoot; }

    private static readonly RecordId Zone = new("zonelite", "zone");

    private static RecordId Id(string name) => new("zonelite", name);

    private static string GameDirectory => Path.Combine(TestEnv.FolderAbove("engine_content"), "games", "Samples", "ZoneLite");

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    private static Entity Player(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    private static Entity[] Named(World world, string name) =>
        world.QueryAll().Entities.ToEntityList().Where(e => e.Name == name).ToArray();

    private static Vector3 At(World world, Entity entity) => world.Get<Transform>(entity).LocalPosition;

    private static bool IsDead(World world, Entity entity) => world.HasTag(entity, world.Conventions().Dead);

    private static OffscreenAgents Table(World world) => world.Resources.Get<OffscreenAgents>();

    private static Vector3 Eye(World world, Entity player)
    {
        var character = world.Get<CharacterController>(player);
        var profile = CharacterConventions.Of(world).ProfileOf(world.Resources.Get<RecordStore>(), character.Profile);
        return CharacterController.EyeOf(At(world, player), in character, profile);
    }

    // Presses `action` for one tick looking at `at`, then waits `ticks` looking at it still.
    private static void Press(HeadlessApp app, string action, Vector3 at, int ticks)
    {
        var world = app.World;
        var input = world.Resources.Get<PlayerInput>();
        var look = at - Eye(world, Player(world));
        float yaw = SageMath.YawOf(look), pitch = SageMath.PitchOf(look);
        input.HasCommand = true;
        input.Command = new PlayerCommand
        {
            Tick = input.Command.Tick + 1, ViewYaw = yaw, ViewPitch = pitch,
            Pressed = default(ActionMask).With(app.Engine.Actions.Get(action)),
        };
        Tick(world);
        for (int i = 1; i < ticks; i++)
        {
            input.Command = new PlayerCommand { Tick = input.Command.Tick + 1, ViewYaw = yaw, ViewPitch = pitch };
            Tick(world);
        }
        input.Command = input.Command with { Pressed = default };
    }

    // How many of `name` are alive in the world, and in the off-screen table.
    private static int AliveHere(World world, string name) => Named(world, name).Count(e => !IsDead(world, e));

    private static int AliveAway(World world, string name) => Table(world).Agents.Count(a => a.Name == name && !a.Dead);

    [Xunit.Fact]
    public void TheSampleValidates()
    {
        var report = ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = GameDirectory,
            EngineContentDirectory = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content"),
            AvailablePlugins = BasePlugins.All(),
        });
        Assert.True(report.Ok, string.Join("\n", report.Errors));
    }

    // Acceptance: the pistol, reloaded, kills a blind dog; the pack refuses what is too heavy; the trader buys an
    // artifact for rubles. Then the stalker goes to the outpost, the checkpoint's sector sleeps, and eight hours
    // pass: the bandits raid it by day and the fight is decided off-screen. Back at the checkpoint, who is alive
    // is who the table said was, nobody twice; and the clock has gone from morning to afternoon.
    [Xunit.Fact]
    public void TheZonePlaysFromCampToCheckpoint()
    {
        using var app = HeadlessApp.ForGame(GameDirectory).WithEngineContent().Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        Assert.Null(app.Engine.Modules.Game);                    // no C#
        var world = app.World;
        var clock = WorldClock.Of(world);
        clock.Scale = 0;                                         // the test passes the time
        Tick(world, 3);
        var player = Player(world);
        Assert.Equal(7.0, clock.Hour, 3);
        Assert.Equal(3, AliveHere(world, "loner"));
        Assert.Equal(3, AliveHere(world, "bandit"));

        // 1. The pistol: reloaded, two shots or so kill the dog eight metres off; rounds are spent.
        Assert.True(world.Equip(player, Id("pm")));
        var dog = Assert.Single(Named(world, "blind dog"));
        world.Teleport(player, Transform.At(At(world, dog) + new Vector3(0, 0.1f, -8)));
        Tick(world, 10);
        var chest = At(world, dog) with { Y = At(world, dog).Y + 1.1f };
        Press(app, "Reload", chest, 70);
        for (int tries = 0; tries < 12 && !IsDead(world, dog); tries++) Press(app, "Attack", chest, 30);
        Assert.True(IsDead(world, dog));
        Assert.True(world.CountOf(player, Id("pm_round")) < 32);

        // 2. The pack holds 30 kg: a 40 kg slab is refused.
        Assert.False(world.Give(player, Id("slab")));

        // 3. The trader buys the Jellyfish at half its value.
        var trader = Assert.Single(Named(world, "trader"));
        int jellyfish = world.Get<Inventory>(player).Items.FindIndex(s => s.Item == Id("jellyfish"));
        Assert.True(Merchants.Sell(world, player, trader, jellyfish, 1).Ok);
        Assert.Equal(1200, world.CountOf(player, Id("rubles")));

        // 4. Away to the outpost: the checkpoint's sector sleeps, its people go to the off-screen table.
        Assert.True(Travel.To(world, Zone, "outpost"));
        Tick(world, 3);
        Assert.Empty(Named(world, "loner"));
        Assert.Equal(3, Table(world).Agents.Count(a => a.Name == "loner"));
        Assert.Equal(3, Table(world).Agents.Count(a => a.Name == "bandit"));

        // 5. Eight hours: the bandits raid by day and the fight is decided while nobody watches.
        Assert.True(Time.Pass(world, 8, "wait"));
        Tick(world, 2);
        Assert.Equal(15.0, clock.Hour, 3);
        int loners = AliveAway(world, "loner"), bandits = AliveAway(world, "bandit");
        Assert.True(loners == 0 || bandits == 0, $"the fight is not decided: {loners} loners, {bandits} bandits");

        // 6. Back to the checkpoint: the survivors are there, as the table had them, and nobody twice.
        Assert.True(Travel.To(world, Zone, "checkpoint_road"));
        Tick(world, 3);
        Assert.Equal(loners, AliveHere(world, "loner"));
        Assert.Equal(bandits, AliveHere(world, "bandit"));
        var ids = world.Query<Persistent>().Entities.ToEntityList().Select(e => e.GetComponent<Persistent>().Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
