#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Phase 4e's exit criterion (issue #139, docs/design/16 "As built (the weapons exit game, issue #139)"): a
// sword, a pistol, a crossbow and a fireball by records only. tests/games/weapons has no C#: a range of
// padded dummies with hitboxes (one in a helmet) and a player carrying all four. The player's input is a
// PlayerCommand, as the client's is; every hit is a `Damaged` event dealt by Combat.ApplyHit.
#pragma warning disable SAGE0127   // hit locations and magazines are phase 4e's
public class WeaponsExitTests
{
    public WeaponsExitTests() { _ = TestEnv.UserRoot; }

    private static readonly RecordId Sword = new("weapons", "sword");
    private static readonly RecordId Pistol = new("weapons", "pistol");
    private static readonly RecordId PistolShot = new("weapons", "pistol_shot");
    private static readonly RecordId Rounds = new("weapons", "pistol_round");
    private static readonly RecordId Crossbow = new("weapons", "crossbow");
    private static readonly RecordId CrossbowShot = new("weapons", "crossbow_shot");
    private static readonly RecordId Bolt = new("weapons", "bolt");
    private static readonly RecordId Dummy = new("weapons", "dummy");
    private static readonly RecordId HelmetedDummy = new("weapons", "helmeted_dummy");
    private static readonly RecordId Torso = new("weapons", "torso");
    private static readonly RecordId Head = new("weapons", "head");
    private static readonly RecordId Physical = new("sage", "physical");
    private static readonly RecordId Fire = new("sage", "fire");

    private const float Chest = 1.25f, Brow = 1.7f;

    private static HeadlessApp Boot()
    {
        var app = HeadlessApp.ForGame(ExitGamesDirectory("weapons")).WithEngineContent().Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        Assert.Null(app.Engine.Modules.Game);                    // no C#
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        return app;
    }

    private static string ExitGamesDirectory(string name) => Path.Combine(TestEnv.FolderAbove("tests"), "tests", "games", name);

    private static Entity Player(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    private static void Step(World world, int ticks = 1) => NpcLocomotionTests.Step(world, ticks);

    // The range's own dummies out of the way, so each weapon meets only the one set up for it.
    private static void ClearTheRange(World world)
    {
        foreach (var name in new[] { "left dummy", "middle dummy", "helmeted dummy" })
            world.Destroy(world.FindByName(name));
        Step(world);
    }

    // A dummy `distance` metres in front of the player, facing it.
    private static Entity Stand(World world, Entity player, float distance, RecordId prefab)
    {
        var feet = world.Get<Transform>(player).LocalPosition;
        var dummy = world.Spawn(prefab, new Vector3(feet.X, 0f, feet.Z - distance));
        Assert.False(dummy.IsNull);
        ref var transform = ref world.Get<Transform>(dummy);
        transform.LocalRotation = Quaternion.CreateFromYawPitchRoll(MathF.PI, 0, 0);
        Step(world, 2);                                          // its bones posed, its boxes on them
        return dummy;
    }

    private static Vector3 Eye(World world, Entity player)
    {
        var character = world.Get<CharacterController>(player);
        var profile = CharacterConventions.Of(world).ProfileOf(world.Resources.Get<RecordStore>(), character.Profile);
        return CharacterController.EyeOf(world.Get<Transform>(player).LocalPosition, in character, profile);
    }

    // Presses `action` for one tick, looking at `at`, then waits `ticks` looking at it still.
    private static void Press(HeadlessApp app, Entity player, string? action, Vector3 at, int ticks)
    {
        var world = app.World;
        var input = world.Resources.Get<PlayerInput>();
        var look = at - Eye(world, player);
        float yaw = SageMath.YawOf(look), pitch = SageMath.PitchOf(look);
        input.HasCommand = true;
        input.Command = new PlayerCommand
        {
            Tick = input.Command.Tick + 1, ViewYaw = yaw, ViewPitch = pitch,
            Pressed = action == null ? default : default(ActionMask).With(app.Engine.Actions.Get(action)),
        };
        Step(world);
        for (int i = 1; i < ticks; i++)
        {
            input.Command = new PlayerCommand { Tick = input.Command.Tick + 1, ViewYaw = yaw, ViewPitch = pitch };
            Step(world);
        }
    }

    private static void Wield(World world, Entity player, RecordId item)
    {
        Assert.True(world.Equip(player, item));
        Step(world, 2);
    }

    // Strikes the dummy with `action` until it dies, reloading first when `reload` is set; returns the hits
    // that took its health, each checked: the player's, on `location`, of `type`, `each` (the last what was left).
    private static List<Damaged> Kill(HeadlessApp app, Entity player, Entity dummy, string action, float aimY,
                                      RecordId location, RecordId type, float each, bool reload = false, int wait = 40)
    {
        var world = app.World;
        var damaged = new EventProbe<Damaged>(world);
        var died = new EventProbe<Died>(world);
        var target = world.Get<Transform>(dummy).LocalPosition with { Y = aimY };
        var hits = new List<Damaged>();
        for (int tries = 0; tries < 20 && !died.All.Any(d => d.Victim == dummy); tries++)
        {
            if (reload) Press(app, player, "Reload", target, 70);
            Press(app, player, action, target, wait);
        }
        foreach (var hit in damaged.All.Where(d => d.Hit.Target == dummy))
        {
            Assert.Equal(player, hit.Hit.Attacker);
            Assert.Equal(location, hit.Hit.Location);
            Assert.Equal(type, hit.Hit.Type);
            hits.Add(hit);
        }
        // Every hit takes `each`, but the last, which takes what health is left.
        Assert.All(hits.Take(hits.Count - 1), hit => Assert.Equal(each, hit.Applied, 3));
        Assert.InRange(hits[^1].Applied, 0.001f, each + 0.001f);
        Assert.Equal(100f, hits.Sum(h => h.Applied), 3);
        Assert.Equal(player, Assert.Single(died.All, d => d.Victim == dummy).Killer);
        world.Destroy(dummy);
        Step(world);
        return hits;
    }

    [Xunit.Fact]
    public void TheWeaponsGameValidates()
    {
        var report = ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = ExitGamesDirectory("weapons"),
            EngineContentDirectory = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content"),
            AvailablePlugins = BasePlugins.All(),
        });
        Assert.True(report.Ok, string.Join("\n", report.Errors));
    }

    // Acceptance: each of the four kills a padded dummy in the number of hits its damage and the padding give
    // (armour 20 against the physical three, fire_resist 50 against the fireball), every hit on the torso a
    // `Damaged` through ApplyHit: the sword in 5 swings of 20, the pistol in 7 shots of 16, the crossbow in 3
    // bolts of 36 and the fireball in 5 casts of 20.
    [Xunit.Fact]
    public void WeaponsExit_SameDamagedPipelineForAllFour()
    {
        using var app = Boot();
        var world = app.World;
        var player = Player(world);
        Step(world, 3);
        ClearTheRange(world);

        Wield(world, player, Sword);
        Assert.Equal(5, Kill(app, player, Stand(world, player, 1.6f, Dummy), "Attack", Chest, Torso, Physical, 20f).Count);

        Wield(world, player, Pistol);
        Press(app, player, "Reload", Vector3.Zero, 70);          // a magazine starts empty
        Assert.Equal(7, Kill(app, player, Stand(world, player, 6f, Dummy), "Attack", Chest, Torso, Physical, 16f, wait: 30).Count);
        Assert.Equal(1, Ammunition.Loaded(world, player, PistolShot));

        Wield(world, player, Crossbow);
        Assert.Equal(3, Kill(app, player, Stand(world, player, 6f, Dummy), "Attack", Chest, Torso, Physical, 36f, reload: true).Count);
        Assert.Equal(10 - 3, world.CountOf(player, Bolt));

        world.Unequip(player, RpgKitModule.MainHand);
        Step(world, 2);
        Assert.Equal(5, Kill(app, player, Stand(world, player, 6f, Dummy), "Cast", Chest, Torso, Fire, 20f, wait: 60).Count);
    }

    // A pistol shot at the brow lands on the head for double: 32 against the torso's 16. The helmeted
    // dummy's `helmet` (armor_head 50) halves it back to 16.
    [Xunit.Fact]
    public void WeaponsExit_AHeadshotDealsDouble()
    {
        using var app = Boot();
        var world = app.World;
        var player = Player(world);
        Step(world, 3);
        ClearTheRange(world);
        Wield(world, player, Pistol);
        Press(app, player, "Reload", Vector3.Zero, 70);
        Assert.Equal(8, Ammunition.Loaded(world, player, PistolShot));

        var damaged = new EventProbe<Damaged>(world);
        var bare = Stand(world, player, 6f, Dummy);
        Press(app, player, "Attack", world.Get<Transform>(bare).LocalPosition with { Y = Brow }, 30);
        var head = Assert.Single(damaged.All);
        Assert.Equal(Head, head.Hit.Location);
        Assert.Equal(32f, head.Applied, 3);
        Press(app, player, "Attack", world.Get<Transform>(bare).LocalPosition with { Y = Chest }, 30);
        Assert.Equal(Torso, damaged.All[1].Hit.Location);
        Assert.Equal(16f, damaged.All[1].Applied, 3);
        world.Destroy(bare);
        Step(world);

        var helmeted = Stand(world, player, 6f, HelmetedDummy);
        Press(app, player, "Attack", world.Get<Transform>(helmeted).LocalPosition with { Y = Brow }, 30);
        Assert.Equal(3, damaged.All.Count);
        Assert.Equal(helmeted, damaged.All[2].Hit.Target);
        Assert.Equal(Head, damaged.All[2].Hit.Location);
        Assert.Equal(16f, damaged.All[2].Applied, 3);
    }

    // The rounds loaded and the rounds carried survive a reload and a save: 8 / 16 after a reload, 6 / 16
    // after two shots, and the same after a save, more shots, and a load.
    [Xunit.Fact]
    public void WeaponsExit_TheAmmoSurvivesAReloadAndASave()
    {
        using var app = Boot();
        var world = app.World;
        var player = Player(world);
        Step(world, 3);
        Wield(world, player, Pistol);
        Assert.Equal(0, Ammunition.Loaded(world, player, PistolShot));
        Assert.Equal(24, world.CountOf(player, Rounds));

        var away = new Vector3(0f, 1.5f, 40f);                  // behind the firing line: nothing to hit
        Press(app, player, "Reload", away, 70);
        Assert.Equal(8, Ammunition.Loaded(world, player, PistolShot));
        Assert.Equal(16, world.CountOf(player, Rounds));
        Press(app, player, "Attack", away, 30);
        Press(app, player, "Attack", away, 30);
        Assert.Equal(6, Ammunition.Loaded(world, player, PistolShot));
        Assert.True(app.Engine.Saves.Save("range"));

        Press(app, player, "Attack", away, 30);
        Press(app, player, "Reload", away, 70);
        Assert.Equal(8, Ammunition.Loaded(world, player, PistolShot));
        Assert.Equal(13, world.CountOf(player, Rounds));

        Assert.True(app.Engine.Saves.Load("range"));
        Step(world, 2);
        world = app.World;
        player = Player(world);
        Assert.Equal(6, Ammunition.Loaded(world, player, PistolShot));
        Assert.Equal(16, world.CountOf(player, Rounds));
        Assert.Equal(Pistol, world.Get<Equipment>(player).In(RpgKitModule.MainHand));
    }
}
#pragma warning restore SAGE0127
