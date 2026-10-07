#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Issue #377's exit (REDESIGN §5 4f-1): tests/games/skills has no C#. A Morrowind-style blade skill rises as the
// player cuts a training dummy with a sword (an attribute_gain on Hit, the kit's skill), and a Daggerfall-style
// level comes from those rises (the kit's levelling), raising strength, which governs blade, by how often blade
// rose. The input is a PlayerCommand, as the client's is; F5 saves, and a fresh app loads with F9 and goes on.
#pragma warning disable SAGE0131   // saves you can trust (phase 4i)
public class SkillsExitTests
{
    public SkillsExitTests() { _ = TestEnv.UserRoot; }

    private static RecordId Id(string name) => new("skills", name);
    private static readonly RecordId Blade = Id("blade");
    private static readonly RecordId Health = new("sage", "health");

    private static string GameDirectory => Path.Combine(TestEnv.FolderAbove("tests"), "tests", "games", "skills");

    private static HeadlessApp Boot(string saves)
    {
        var app = HeadlessApp.ForGame(GameDirectory).WithEngineContent().Boot();
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", app.Records.LoadErrors));
        Assert.Null(app.Engine.Modules.Game);                    // no C#
        app.Engine.Saves.Root = saves;
        Step(app.World, 3);
        return app;
    }

    private static void Step(World world, int ticks = 1) => NpcLocomotionTests.Step(world, ticks);

    private static Entity Player(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    private static Entity Dummy(World world) =>
        Assert.Single(world.QueryAll().Entities.ToEntityList(), e => e.Name == "dummy");

    private static Vector3 Eye(World world, Entity player)
    {
        var character = world.Get<CharacterController>(player);
        var profile = CharacterConventions.Of(world).ProfileOf(world.Resources.Get<RecordStore>(), character.Profile);
        return CharacterController.EyeOf(world.Get<Transform>(player).LocalPosition, in character, profile);
    }

    // Presses `action` for one tick looking at `at`, then waits `ticks` looking at it still.
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
        input.Command = input.Command with { Pressed = default };
    }

    // One cut at the dummy's chest: windup, recovery and cooldown are 0.4 s.
    private static void Cut(HeadlessApp app)
    {
        var world = app.World;
        var player = Player(world);
        var chest = world.Get<Transform>(Dummy(world)).LocalPosition with { Y = 1.1f };
        Press(app, player, null, chest, 2);
        Press(app, player, "Attack", chest, 40);
    }

    private static float Of(World world, string attribute) => world.Attribute(Player(world), Id(attribute));

    [Xunit.Fact]
    public void TheSkillsGameValidates()
    {
        var report = ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = GameDirectory,
            EngineContentDirectory = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content"),
            AvailablePlugins = BasePlugins.All(),
        });
        Assert.True(report.Ok, string.Join("\n", report.Errors));
    }

    // Acceptance: eight cuts raise blade from 2 to 5 (thresholds 2, 2.5 and 3 of use-XP, one a cut) and the
    // first two rises are a level (strength +2 for two rises, luck +1 from the level's effect). F5 with one rise
    // counted toward the next level; a fresh app, F9, and three more cuts: blade 6, level 3, and strength +2
    // again because the rise from before the save was kept.
    [Xunit.Fact]
    public void SkillsExit_ABladeSkillRisesWithUseAndLevelsTheCharacterAcrossASave()
    {
        string saves = TestEnv.NewTempDir();
        using (var first = Boot(saves))
        {
            var world = first.World;
            var player = Player(world);
            var dummy = Dummy(world);
            Assert.Equal(2f, Skills.RankOf(world, player, Blade));
            Assert.Equal(1f, Of(world, "level"));
            Assert.Equal(40f, Of(world, "strength"));

            Assert.True(world.Equip(player, Id("sword")));
            Step(world, 2);
            var damaged = new EventProbe<Damaged>(world);
            var raised = new EventProbe<SkillRaised>(world);
            var levelled = new EventProbe<LevelledUp>(world);

            float[] rankAfter = { 2, 3, 3, 3, 4, 4, 4, 5 };
            for (int cut = 0; cut < rankAfter.Length; cut++)
            {
                Cut(first);
                Assert.Equal(cut + 1, damaged.All.Count(d => d.Hit.Target == dummy && d.Applied > 0f));
                Assert.Equal(rankAfter[cut], Skills.RankOf(world, player, Blade));
            }
            Assert.Equal(92f, world.Attribute(dummy, Health));
            Assert.Equal(0.5f, Skills.ExperienceOf(world, player, Blade));
            Assert.Equal(new[] { 3f, 4f, 5f }, raised.All.Select(r => r.Rank));
            Assert.Equal(2f, Assert.Single(levelled.All).Level);
            Assert.Equal(2f, Of(world, "level"));
            Assert.Equal(1f, Of(world, "level_progress"));
            Assert.Equal(42f, Of(world, "strength"));
            Assert.Equal(41f, Of(world, "luck"));
            var rises = Assert.Single(world.Get<Progression>(player).Rises!);
            Assert.Equal((Id("character"), Id("strength"), 1), (rises.Levelling, rises.Attribute, rises.Count));

            Press(first, player, SaveSystem.QuickSaveAction, Vector3.Zero, 1);
            Assert.Equal(SaveKind.Quick, Assert.Single(first.Engine.Saves.Slots).Kind);
        }

        using var second = Boot(saves);
        Assert.Equal(2f, Skills.RankOf(second.World, Player(second.World), Blade));   // a fresh start, until F9
        Press(second, Player(second.World), SaveSystem.QuickLoadAction, Vector3.Zero, 1);
        var w = second.World;
        var p = Player(w);
        Assert.Equal(5f, Skills.RankOf(w, p, Blade));
        Assert.Equal(0.5f, Skills.ExperienceOf(w, p, Blade));
        Assert.Equal(2f, Of(w, "level"));
        Assert.Equal(1f, Of(w, "level_progress"));
        Assert.Equal(42f, Of(w, "strength"));
        Assert.Equal(41f, Of(w, "luck"));
        Assert.Equal(1, Assert.Single(w.Get<Progression>(p).Rises!).Count);
        Assert.Equal(Id("sword"), w.Get<Equipment>(p).In(RpgKitModule.MainHand));

        // From 5 it takes 3.5: three more cuts, blade 6, and the second point is level 3.
        for (int cut = 0; cut < 3; cut++) Cut(second);
        Assert.Equal(6f, Skills.RankOf(w, p, Blade));
        Assert.Equal(3f, Of(w, "level"));
        Assert.Equal(44f, Of(w, "strength"));                    // two rises since level 2, one of them saved
        Assert.Equal(42f, Of(w, "luck"));
    }
}
