#nullable enable
using System.Linq;
using Sage.Kits.Rpg;
using Sage.UI;
using Sandbox;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The Sandbox's ranged weapons (engine issue #138, docs/design/16 "As built (the Sandbox's weapons)"): a
// pistol and a crossbow that are only records (scene.json, viewmodel.json) and generated models
// (tools/make_weapons.py), on phase 4e's hit pipeline; the rounds they have loaded and carried on the HUD
// (HudView) and the kit's equipment screen (EquipmentView), both through the kit's AmmoReadout.
#pragma warning disable SAGE0127   // the magazine and the readout are phase 4e's
public class SandboxWeaponsTests
{
    public SandboxWeaponsTests() { _ = TestEnv.UserRoot; }

    private static readonly RecordId Pistol = new("sandbox", "pistol");
    private static readonly RecordId PistolShot = new("sandbox", "pistol_shot");
    private static readonly RecordId Rounds = new("sandbox", "pistol_rounds");
    private static readonly RecordId Crossbow = new("sandbox", "crossbow");
    private static readonly RecordId CrossbowShot = new("sandbox", "crossbow_shot");
    private static readonly RecordId Bolt = new("sandbox", "bolt");

    private static void Step(World world, int ticks = 1) => NpcLocomotionTests.Step(world, ticks);

    // Presses `action` as the player, for one tick, the way the client's input does.
    private static void Press(HeadlessApp app, string action)
    {
        var input = app.World.Resources.Get<PlayerInput>();
        input.HasCommand = true;
        input.Command = new PlayerCommand { Tick = input.Command.Tick + 1, Pressed = default(ActionMask).With(app.Engine.Actions.Get(action)) };
        Step(app.World);
        input.Command = new PlayerCommand { Tick = input.Command.Tick + 1 };
    }

    // Acceptance: the pistol reloads through the arms' `mag_in` event — 0.9 s into their reload clip, long
    // before its own 1.4 s `reloadTime` — and the HUD and the equipment screen show its rounds, loaded and
    // carried, as they change: 0 / 24, 8 / 16 after the reload, 7 / 16 after a shot.
    [Xunit.Fact]
    public void ThePistolReloadsOnTheArmsMagIn_AndTheHudShowsItsRounds()
    {
        using var app = SandboxScreensTests.Boot();
        var world = app.World;
        var player = SandboxScreensTests.Player(world);
        Step(world, 3);
        Assert.True(world.Give(player, Pistol));
        Assert.True(world.Give(player, Rounds, 24));
        Assert.True(world.Equip(player, Pistol));
        Step(world, 2);
        Assert.Equal(PistolShot, world.Get<Melee>(player).Attack);

        // The pistol in the arms, on their grip socket.
        var camera = Assert.Single(world.Query<Camera>().AllTags(Tags.Get<PlayerCamera>()).Entities.ToEntityList());
        var arms = Viewmodels.ArmsOf(world, camera);
        Assert.False(arms.IsNull || Viewmodels.WeaponOf(world, camera).IsNull, "the pistol's arms and pistol were not spawned");

        // The HUD layer, as SandboxClientModule opens it.
        var stack = SandboxScreensTests.Stack(world);
        var hud = stack.OpenHud(new RecordId("sandbox", "hud"), new UiBindContext(world));
        var view = Assert.IsType<HudView>(hud.Screen!.ViewModel);
        var label = (Label)hud.Content.Find("ammo")!;
        void Frame() => stack.Update(UiInput.Wait(1f / 60f));
        Frame();
        Assert.True(view.HasAmmo);
        Assert.Equal("0 / 24", view.Ammo);
        Assert.Equal("0 / 24", label.Text);
        Assert.True(label.Visible);

        // Reload: nothing goes in until the arms' clip says mag_in.
        var events = new EventProbe<AnimationEvent>(world);
        Press(app, "Reload");
        Assert.Equal("reload", Animators.StateOf(world, arms));
        Step(world, 48);                                        // 0.8 s: the magazine is still out
        Assert.Equal(0, Ammunition.Loaded(world, player, PistolShot));
        Step(world, 12);                                        // 1.0 s: mag_in at 0.9, well before 1.4
        Assert.Contains(events.All, e => e.Entity == arms && e.Name == "mag_in");
        Assert.Equal(8, Ammunition.Loaded(world, player, PistolShot));
        Assert.Equal(16, world.CountOf(player, Rounds));
        Frame();
        Assert.Equal("8 / 16", label.Text);

        // A shot spends a round.
        var fired = new EventProbe<WeaponFired>(world);
        Press(app, "Attack");
        Step(world, 5);
        Assert.Equal(PistolShot, Assert.Single(fired.All).Attack);
        Assert.Equal(7, Ammunition.Loaded(world, player, PistolShot));
        Frame();
        Assert.Equal("7 / 16", view.Ammo);

        // The kit's equipment screen shows the same line.
        var equipment = new EquipmentView();
        equipment.Refresh(new UiBindContext(world, player));
        Assert.True(equipment.Ammo.Has);
        Assert.Equal("7 / 16", equipment.Ammo.Text);
        Assert.Equal(8, equipment.Ammo.Magazine);

        // Holstered: the rounds loaded go back in the bag and the HUD has no ammunition to show.
        world.Unequip(player, RpgKitModule.MainHand);
        Step(world);
        Frame();
        Assert.Equal(23, world.CountOf(player, Rounds));
        Assert.False(view.HasAmmo);
        Assert.False(label.Visible);
    }

    // The crossbow holds one bolt: a reload loads it, a shot throws it — a projectile that falls — and the
    // readout counts what is left in the bag with nothing loaded.
    [Xunit.Fact]
    public void TheCrossbowLoadsOneBoltAndThrowsIt()
    {
        using var app = SandboxScreensTests.Boot();
        var world = app.World;
        var player = SandboxScreensTests.Player(world);
        Step(world, 3);
        Assert.True(world.Give(player, Crossbow));
        Assert.True(world.Give(player, Bolt, 12));
        Assert.True(world.Equip(player, Crossbow));
        Step(world, 2);

        Press(app, "Reload");
        Step(world, 60);
        Assert.Equal(1, Ammunition.Loaded(world, player, CrossbowShot));
        Assert.Equal(11, world.CountOf(player, Bolt));

        Press(app, "Attack");
        Step(world);
        // (The firebug may have a fireball in the air too: the same carrier.)
        var bolt = Assert.Single(world.Query<Transform, Projectile>().Entities.ToEntityList(), e => world.Get<Projectile>(e).Attack == CrossbowShot);
        Assert.Equal(player, world.Get<Projectile>(bolt).Caster);
        Assert.Equal(9.81f, world.Get<Projectile>(bolt).Gravity, 3);
        Assert.Equal(0, Ammunition.Loaded(world, player, CrossbowShot));

        var readout = new AmmoReadout();
        Assert.True(readout.Read(world, player));
        Assert.Equal("0 / 11", readout.Text);
        Assert.False(readout.Read(world, player));              // nothing changed: nothing rebuilt
    }
}
#pragma warning restore SAGE0127
