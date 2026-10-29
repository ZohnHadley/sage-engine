#nullable enable
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Phase 5's first sample (issue #576): games/Samples/BlackMesaLite, a Half-Life-style chapter in data alone on
// the base engine. The player's input is a PlayerCommand, as the client's is; walking is a teleport.
#pragma warning disable SAGE0127, SAGE0129   // magazines (4e); routines and followers (4g)
public class BlackMesaLiteTests
{
    public BlackMesaLiteTests() { _ = TestEnv.UserRoot; }

    private static readonly RecordId CrowbarSwing = new("blackmesalite", "crowbar_swing");
    private static readonly RecordId PistolShot = new("blackmesalite", "pistol_shot");

    private static string GameDirectory => Path.Combine(TestEnv.FolderAbove("engine_content"), "games", "Samples", "BlackMesaLite");

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    private static Entity Player(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    private static Entity One(World world, string name) => world.FindByName(name);

    private static Vector3 At(World world, Entity entity) => world.Get<Transform>(entity).LocalPosition;

    private static Vector3 Eye(World world, Entity player)
    {
        var character = world.Get<CharacterController>(player);
        var profile = CharacterConventions.Of(world).ProfileOf(world.Resources.Get<RecordStore>(), character.Profile);
        return CharacterController.EyeOf(At(world, player), in character, profile);
    }

    // Presses `action` for one tick looking at `at`, then waits `ticks` looking at it still.
    private static void Press(HeadlessApp app, string? action, Vector3 at, int ticks)
    {
        var world = app.World;
        var input = world.Resources.Get<PlayerInput>();
        var look = at - Eye(world, Player(world));
        float yaw = SageMath.YawOf(look), pitch = SageMath.PitchOf(look);
        input.HasCommand = true;
        input.Command = new PlayerCommand
        {
            Tick = input.Command.Tick + 1, ViewYaw = yaw, ViewPitch = pitch,
            Pressed = action == null ? default : default(ActionMask).With(app.Engine.Actions.Get(action)),
        };
        Tick(world);
        for (int i = 1; i < ticks; i++)
        {
            input.Command = new PlayerCommand { Tick = input.Command.Tick + 1, ViewYaw = yaw, ViewPitch = pitch };
            Tick(world);
        }
        input.Command = input.Command with { Pressed = default };
    }

    private static void StandAt(World world, Vector3 at)
    {
        world.Teleport(Player(world), Transform.At(at));
        Tick(world, 10);
    }

    private static bool IsDead(World world, Entity entity) => world.HasTag(entity, world.Conventions().Dead);

    // Attacks the zombie, aiming at its chest, until it dies (at most 10 tries).
    private static void Kill(HeadlessApp app, Entity zombie, bool reload = false)
    {
        var chest = At(app.World, zombie) with { Y = 1.2f };
        for (int tries = 0; tries < 10 && !IsDead(app.World, zombie); tries++)
        {
            if (reload) Press(app, "Reload", chest, 70);
            Press(app, "Attack", chest, 40);
        }
        Assert.True(IsDead(app.World, zombie), $"{World.Describe(zombie)} is still standing");
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

    // Issue #599: with the crowbar and the pistol carried, the player switches between them with the next and
    // previous buttons (the wheel, the D-pad) and with 1 and 2, in the order they were picked up; no kit, no C#.
    [Xunit.Fact]
    public void ThePlayerSwitchesBetweenTheCrowbarAndThePistol()
    {
        using var app = HeadlessApp.ForGame(GameDirectory).WithEngineContent()
            .OnRegistered(a => a.Records.Register<InputMapRecord>())   // the client's record type: the default bindings
            .Boot();
        var world = app.World;
        Tick(world, 3);
        var player = Player(world);
        StandAt(world, new Vector3(0, 0.1f, 22.5f));
        Press(app, "Use", At(world, One(world, "crowbar")), 3);
        StandAt(world, new Vector3(2, 0.1f, 15.5f));
        Press(app, "Use", At(world, One(world, "pistol")), 3);
        Assert.Equal(PistolShot, world.Get<Melee>(player).Attack);

        var ahead = At(world, player) + new Vector3(0, 1.5f, -5f);
        RecordId After(string? action) { Press(app, action, ahead, 3); return world.Get<Melee>(player).Attack; }
        Assert.Equal(CrowbarSwing, After("PreviousWeapon"));
        Assert.Equal(PistolShot, After("NextWeapon"));
        Assert.Equal(CrowbarSwing, After("NextWeapon"));     // round again
        Assert.Equal(CrowbarSwing, After("Weapon1"));        // already in hand: nothing
        Assert.Equal(PistolShot, After("Weapon2"));
        Assert.Equal(PistolShot, After("Weapon3"));          // nothing carried there
        Assert.Equal(PistolShot, After(null));

        // Bound by default: the wheel and the D-pad for next and previous, the number keys for each.
        var bound = app.Records.All<InputMapRecord>().SelectMany(m => m.Actions)
            .ToLookup(a => a.Key, a => a.Value, System.StringComparer.OrdinalIgnoreCase);
        Assert.Contains(bound["NextWeapon"].SelectMany(b => b), b => b.Mouse == "Wheel" && b.Scale < 0);
        Assert.Contains(bound["PreviousWeapon"].SelectMany(b => b), b => b.Mouse == "Wheel" && b.Scale > 0);
        Assert.Contains(bound["NextWeapon"].SelectMany(b => b), b => b.Gamepad == "DPadRight");
        Assert.Contains(bound["Weapon2"].SelectMany(b => b), b => b.Key == "D2");
    }

    // Acceptance: the chapter played start to end. The crowbar picked up goes to hand and kills the first zombie;
    // the pistol picked up goes to hand with rounds and shoots the second; with both down the lab door opens and
    // the scientist greets the player and follows them to the lift, which carries them up and ends the chapter.
    [Xunit.Fact]
    public void TheChapterPlaysFromStartToEnd()
    {
        using var app = HeadlessApp.ForGame(GameDirectory).WithEngineContent().Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        Assert.Null(app.Engine.Modules.Game);                    // no C#
        var world = app.World;
        Tick(world, 3);
        var player = Player(world);

        // 1. The crowbar: used, it is in hand.
        StandAt(world, new Vector3(0, 0.1f, 22.5f));
        Press(app, "Use", At(world, One(world, "crowbar")), 3);
        Assert.Equal(CrowbarSwing, world.Get<Melee>(player).Attack);
        var zombie1 = One(world, "zombie 1");
        StandAt(world, At(world, zombie1) + new Vector3(0, 0.1f, 1.6f));
        var died = new EventProbe<Died>(world);
        Kill(app, zombie1);
        Assert.Equal(1f, world.Get<LogicCounter>(One(world, "kills")).Value);
        Assert.Single(died.All);
        Assert.Equal(0f, world.Get<Mover>(One(world, "lab door")).Position);   // one zombie is not enough

        // 2. The pistol: in hand, with rounds; reloaded, it shoots the second zombie from eight metres.
        StandAt(world, new Vector3(2, 0.1f, 15.5f));
        Press(app, "Use", At(world, One(world, "pistol")), 3);
        Assert.Equal(PistolShot, world.Get<Melee>(player).Attack);
        Assert.True(world.CountOf(player, new RecordId("blackmesalite", "pistol_round")) > 0);
        var zombie2 = One(world, "zombie 2");
        StandAt(world, At(world, zombie2) + new Vector3(0, 0.1f, 8f));
        Kill(app, zombie2, reload: true);

        // 3. Both down: the door opens and the scientist greets the player.
        Tick(world, 90);
        Assert.Equal(1f, world.Get<Mover>(One(world, "lab door")).Position);
        Assert.Equal(1, Vars.Of(world).Get("scientist_met"));

        // 4. The scientist follows: the player walks off past the door, and he comes after them.
        var scientist = One(world, "scientist");
        StandAt(world, new Vector3(0, 0.1f, -9));
        float Apart() => Vector3.Distance(At(world, scientist) with { Y = 0 }, At(world, player) with { Y = 0 });
        for (int s = 0; s < 20 && Apart() >= 3.5f; s++) Tick(world, 60);   // twenty metres at a walk
        Assert.True(Apart() < 3.5f,
            $"the scientist is at {At(world, scientist)}, the player at {At(world, player)}");

        // 5. Onto the lift: it rises, and the chapter is done.
        Assert.Equal(0, Vars.Of(world).Get("chapter_done"));
        StandAt(world, new Vector3(0, 0.3f, -14));
        Tick(world, 60 * 4);
        Assert.Equal(1f, world.Get<Mover>(One(world, "lift")).Position);
        Assert.Equal(1, Vars.Of(world).Get("chapter_done"));
    }
}
