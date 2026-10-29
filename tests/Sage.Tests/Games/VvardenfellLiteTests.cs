#nullable enable
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Phase 5's second sample (issue #577): games/Samples/VvardenfellLite, a Morrowind-style town in data on the RPG
// kit. The player's input is a PlayerCommand, as the client's is; walking is a teleport.
public class VvardenfellLiteTests
{
    public VvardenfellLiteTests() { _ = TestEnv.UserRoot; }

    private static RecordId Id(string name) => new("vvardenfelllite", name);

    private static string GameDirectory => Path.Combine(TestEnv.FolderAbove("engine_content"), "games", "Samples", "VvardenfellLite");

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++)
        {
            world.RunFixed(1f / 60f);
            world.RunFrame(1f / 60f, 1f);                      // the camera director resolves views in a frame
        }
    }

    private static Entity Player(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    private static Vector3 At(World world, Entity entity) => world.Get<Transform>(entity).LocalPosition;

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

    private static bool IsDead(World world, Entity entity) => world.HasTag(entity, world.Conventions().Dead);

    // Stands a step from the rat and cuts at it until it dies (at most 10 swings).
    private static void Kill(HeadlessApp app, Entity rat)
    {
        var world = app.World;
        world.Teleport(Player(world), Transform.At(At(world, rat) + new Vector3(0, 0.1f, 1.6f)));
        Tick(world, 10);
        var chest = At(world, rat) with { Y = 1.2f };
        for (int tries = 0; tries < 10 && !IsDead(world, rat); tries++) Press(app, "Attack", chest, 40);
        Assert.True(IsDead(world, rat), $"{World.Describe(rat)} is still standing");
    }

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

    // Acceptance: the elder's "rats" topic starts the quest; the two rats die to the sword and blade rises with
    // the hits; asked again he finishes the quest and pays; the trader refuses a potion the player cannot afford
    // and sells it once they can; V turns the view to third person and back.
    [Xunit.Fact]
    public void TheTownPlaysFromQuestToTrade()
    {
        using var app = HeadlessApp.ForGame(GameDirectory).WithEngineContent().Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        Assert.Null(app.Engine.Modules.Game);                    // no C#
        var world = app.World;
        Tick(world, 3);
        var player = Player(world);
        var elder = world.FindByName("elder");
        var trader = world.FindByName("trader");
        var rats = Id("rats");
        var quest = Id("cellar_rats");
        var gold = Id("gold");

        // 1. First and third person, on the V key.
        Assert.Equal(CameraRigKind.FirstPerson, world.MainViewRig());
        Press(app, "ToggleView", At(world, elder), 2);
        Assert.Equal(CameraRigKind.ThirdPerson, world.MainViewRig());
        Press(app, "ToggleView", At(world, elder), 2);
        Assert.Equal(CameraRigKind.FirstPerson, world.MainViewRig());

        // 2. The topic gives the quest.
        Assert.True(Quests.IsNotStarted(world, quest));
        Assert.StartsWith("Rats in my cellar", DialogueTopics.Ask(world, elder, player, rats)?.Text);
        Tick(world);
        Assert.Equal("hunt", Quests.StageOf(world, quest));
        Assert.Equal("I can still hear them down there.", DialogueTopics.Ask(world, elder, player, rats)?.Text);

        // 3. The trader's potion is 30 gold; the player has 10.
        int potion = world.Get<Inventory>(trader).Items.FindIndex(s => s.Item == Id("potion"));
        Assert.True(potion >= 0);
        Assert.False(Merchants.Buy(world, player, trader, potion, 1).Ok);

        // 4. The rats, with the sword: blade rises from the hits.
        Assert.True(world.Equip(player, Id("sword")));
        float blade = world.Attribute(player, Id("blade"));
        Kill(app, world.FindByName("rat 1"));
        Kill(app, world.FindByName("rat 2"));
        Tick(world, 5);
        Assert.True(world.Attribute(player, Id("blade")) > blade, $"blade is still {world.Attribute(player, Id("blade"))}");
        Assert.Equal("report", Quests.StageOf(world, quest));

        // 5. Back to the elder: the quest is done and he pays.
        Assert.StartsWith("The cellar is clear?", DialogueTopics.Ask(world, elder, player, rats)?.Text);
        Tick(world);
        Assert.True(Quests.IsFinished(world, quest));
        Assert.Equal(60, world.CountOf(player, gold));

        // 6. Now the potion: 30 gold for it.
        Assert.True(Merchants.Buy(world, player, trader, potion, 1).Ok);
        Assert.Equal(1, world.CountOf(player, Id("potion")));
        Assert.Equal(30, world.CountOf(player, gold));
    }
}
