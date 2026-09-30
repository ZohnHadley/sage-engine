#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Ammunition, magazines and reload (issue #135, docs/design/16 "As built (ammunition)"): an attack that
// names an `ammo` item spends it, from a saved `sage:magazine` the Reload button fills from the
// wielder's Inventory, or straight from the Inventory when the attack has no magazine.
public class AmmunitionTests
{
    public AmmunitionTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;
    private static readonly RecordId Bullet = new("sage", "bullet");
    private static readonly RecordId Pistol = new("sage", "sidearm");
    private static readonly RecordId Bow = new("sage", "bow");

    private const string Records = """
        [{ "type": "item", "id": "bullet", "label": "a bullet", "weight": 0.01, "maxStack": 100 },
         { "type": "item", "id": "sidearm_item", "label": "a pistol", "slot": "MainHand", "attack": "sidearm" },
         { "type": "item", "id": "rifle_item", "label": "a rifle", "slot": "OffHand", "attack": "rifle" },
         { "type": "attack", "id": "sidearm", "delivery": "ray", "damage": 5, "range": 50, "windupTime": 0, "recoverTime": 0.05,
           "cooldown": 0.1, "ammo": "bullet", "magazine": 8, "reloadTime": 1.0 },
         { "type": "attack", "id": "rifle", "base": "sidearm", "magazine": 30, "automatic": true, "rateOfFire": 10 },
         { "type": "attack", "id": "bow", "delivery": "ray", "damage": 5, "range": 50, "windupTime": 0, "recoverTime": 0.05,
           "cooldown": 0.1, "ammo": "bullet" },
         { "type": "attack", "id": "wand", "delivery": "ray", "damage": 1, "range": 50, "windupTime": 0, "recoverTime": 0.05, "cooldown": 0.1 }]
        """;

    private static HeadlessApp NewGame()
    {
        var app = HeadlessApp.Gameplay().WithHands().File("data/hits.json", HitPipelineTests.Records).File("data/ammo.json", Records).Boot("ammo");
        Assert.Equal(0, app.Records.ErrorCount);
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var ground = app.World.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        app.World.Add(ground, Collider.Box(new Vector3(400, 1, 400)));
        return app;
    }

    // A shooter with `bullets` in the bag, holding `attack`, facing a target 10 m down -z.
    private static (Entity Shooter, Entity Target) Duel(HeadlessApp app, string attack, int bullets)
    {
        var world = app.World;
        var shooter = HitPipelineTests.Body(world, Vector3.Zero, "shooter", new Vector3(0, 0, -10), attack);
        world.Add(shooter, new Persistent { Id = PersistentId.FromName("shooter") });
        world.AddInventory(shooter);
        if (bullets > 0) Assert.True(world.Give(shooter, Bullet, bullets));
        var target = HitPipelineTests.Body(world, new Vector3(0, 0, -10), "target", Vector3.Zero);
        return (shooter, target);
    }

    private static void Hold(HeadlessApp app, Entity e, string? action, bool held = false)
    {
        ref var intent = ref app.World.Get<PawnIntent>(e);
        var mask = action == null ? default : default(ActionMask).With(app.Engine.Actions.Get(action));
        intent.Pressed = held ? default : mask;
        intent.Held = held ? mask : default;
    }

    private static void Press(HeadlessApp app, Entity e, string action)
    {
        Hold(app, e, action);
        app.World.RunFixed(Dt);
        Hold(app, e, null);
    }

    private static void Run(HeadlessApp app, int ticks)
    {
        for (int i = 0; i < ticks; i++) app.World.RunFixed(Dt);
    }

    // Press Reload and let the fallback timer finish it.
    private static void ReloadFully(HeadlessApp app, Entity e, float seconds = 1.0f)
    {
        Press(app, e, "Reload");
        Run(app, (int)MathF.Ceiling(seconds / Dt) + 2);
    }

    // Acceptance: eight shots empty an 8-round magazine and the ninth dry-fires.
    [Xunit.Fact]
    public void EightShotsEmptyAnEightRoundMagazineAndTheNinthDryFires()
    {
        using var app = NewGame();
        var world = app.World;
        var (shooter, target) = Duel(app, "sidearm", 20);
        var fired = new EventProbe<WeaponFired>(world);
        var dry = new EventProbe<DryFire>(world);
        ReloadFully(app, shooter);
        Assert.Equal(8, Ammunition.Loaded(world, shooter, Pistol));
        Assert.Equal(12, world.CountOf(shooter, Bullet));

        for (int shot = 1; shot <= 8; shot++)
        {
            Press(app, shooter, "Attack");
            Run(app, 12);                                   // past the recovery and the cooldown
            Assert.Equal(8 - shot, Ammunition.Loaded(world, shooter, Pistol));
        }
        Assert.Equal(8, fired.All.Count);
        Assert.Empty(dry.All);
        Assert.Equal(100f - 8 * 5f, world.Attribute(target, HitPipelineTests.Health), 3);

        Press(app, shooter, "Attack");                      // the ninth
        Run(app, 12);
        Assert.Equal(8, fired.All.Count);
        var click = Assert.Single(dry.All);
        Assert.Equal(shooter, click.Shooter);
        Assert.Equal(Pistol, click.Attack);
        Assert.Equal(60f, world.Attribute(target, HitPipelineTests.Health), 3);
    }

    // Acceptance: the reload moves 8 rounds out of the inventory on the tick `mag_in` arrives — long
    // before the fallback `reloadTime` (1 s) would have.
    [Xunit.Fact]
    public void AReloadMovesEightRoundsFromTheInventoryOnTheTickOfMagIn()
    {
        using var app = NewGame();
        var world = app.World;
        var (shooter, _) = Duel(app, "sidearm", 20);

        Press(app, shooter, "Reload");
        Run(app, 5);
        Assert.True(world.Get<Magazine>(shooter).Reloading);
        Assert.Equal(0, Ammunition.Loaded(world, shooter, Pistol));
        Assert.Equal(20, world.CountOf(shooter, Bullet));

        world.Events.Send(new AnimationEvent(shooter, "mag_in"));   // the clip crossed it in the Animation phase
        world.RunFixed(Dt);                                         // the very next Gameplay phase sees it
        Assert.Equal(8, Ammunition.Loaded(world, shooter, Pistol));
        Assert.Equal(12, world.CountOf(shooter, Bullet));
        Assert.False(world.Get<Magazine>(shooter).Reloading);
    }

    // Without an event the reload completes after the attack's reloadTime and not before.
    [Xunit.Fact]
    public void WithNoMagInEventTheReloadCompletesAfterReloadTime()
    {
        using var app = NewGame();
        var world = app.World;
        var (shooter, _) = Duel(app, "sidearm", 20);

        Press(app, shooter, "Reload");                      // tick 0 starts it; the timer runs from tick 1
        Run(app, 55);
        Assert.Equal(0, Ammunition.Loaded(world, shooter, Pistol));
        Run(app, 10);
        Assert.Equal(8, Ammunition.Loaded(world, shooter, Pistol));
    }

    // Acceptance: Reload does nothing when there is no ammunition in the inventory.
    [Xunit.Fact]
    public void ReloadDoesNothingWhenThereIsNoAmmo()
    {
        using var app = NewGame();
        var world = app.World;
        var (shooter, _) = Duel(app, "sidearm", 0);

        ReloadFully(app, shooter);
        Assert.Equal(0, Ammunition.Loaded(world, shooter, Pistol));
        Assert.False(world.Get<Magazine>(shooter).Reloading);
    }

    // A reload takes what there is, up to a full magazine, and tops a part-empty one up.
    [Xunit.Fact]
    public void AReloadTakesWhatThereIsAndTopsUpAPartEmptyMagazine()
    {
        using var app = NewGame();
        var world = app.World;
        var (shooter, _) = Duel(app, "sidearm", 5);

        ReloadFully(app, shooter);
        Assert.Equal(5, Ammunition.Loaded(world, shooter, Pistol));
        Assert.Equal(0, world.CountOf(shooter, Bullet));

        world.Give(shooter, Bullet, 10);
        ReloadFully(app, shooter);
        Assert.Equal(8, Ammunition.Loaded(world, shooter, Pistol));
        Assert.Equal(7, world.CountOf(shooter, Bullet));
    }

    // Acceptance: a save with 3 rounds loaded still has 3 after loading.
    [Xunit.Fact]
    public void ASaveWithThreeRoundsLoadedStillHasThreeAfterLoading()
    {
        using var app = NewGame();
        var world = app.World;
        var (shooter, _) = Duel(app, "sidearm", 3);
        ReloadFully(app, shooter);
        Assert.Equal(3, Ammunition.Loaded(world, shooter, Pistol));
        Press(app, shooter, "Reload");                      // mid-reload when saved: not saved (transient)
        Assert.True(app.Engine.Saves.Save("ammo"));

        world.Get<Magazine>(shooter).Set(Pistol, 0);
        Assert.Equal(0, Ammunition.Loaded(world, shooter, Pistol));
        Assert.True(app.Engine.Saves.Load("ammo"));

        var loaded = world.Resolve(PersistentId.FromName("shooter"));
        Assert.False(loaded.IsNull);
        Assert.Equal(3, Ammunition.Loaded(world, loaded, Pistol));
        Assert.False(world.Get<Magazine>(loaded).Reloading);
    }

    // The rounds are keyed by attack id, so swapping the attack in hand keeps each one's count.
    [Xunit.Fact]
    public void RoundsSurviveASwapOfTheAttackInHand()
    {
        using var app = NewGame();
        var world = app.World;
        var (shooter, _) = Duel(app, "sidearm", 20);
        ReloadFully(app, shooter);

        world.Get<Melee>(shooter).Attack = new RecordId("sage", "rifle");
        Run(app, 3);
        Assert.Equal(0, Ammunition.Loaded(world, shooter, new RecordId("sage", "rifle")));
        world.Get<Melee>(shooter).Attack = Pistol;
        Run(app, 3);
        Assert.Equal(8, Ammunition.Loaded(world, shooter, Pistol));
    }

    // Unequipping the weapon returns its loaded rounds to the inventory.
    [Xunit.Fact]
    public void UnequippingTheWeaponReturnsItsLoadedRounds()
    {
        using var app = NewGame();
        var world = app.World;
        var (shooter, _) = Duel(app, "sidearm", 20);
        var item = new RecordId("sage", "sidearm_item");
        Assert.True(world.Give(shooter, item));
        Assert.True(world.Equip(shooter, item));
        ReloadFully(app, shooter);
        Assert.Equal(12, world.CountOf(shooter, Bullet));

        world.Unequip(shooter, Hands.Main);
        Assert.Equal(20, world.CountOf(shooter, Bullet));
        Assert.Equal(0, Ammunition.Loaded(world, shooter, Pistol));
    }

    // So does dropping it.
    [Xunit.Fact]
    public void DroppingTheWeaponReturnsItsLoadedRounds()
    {
        using var app = NewGame();
        var world = app.World;
        var (shooter, _) = Duel(app, "sidearm", 20);
        var item = new RecordId("sage", "sidearm_item");
        Assert.True(world.Give(shooter, item));
        Assert.True(world.Equip(shooter, item));
        ReloadFully(app, shooter);

        Assert.False(world.Drop(shooter, item).IsNull);
        Assert.Equal(20, world.CountOf(shooter, Bullet));
        Assert.Equal(0, Ammunition.Loaded(world, shooter, Pistol));
    }

    // An attack with no magazine takes each shot straight from the inventory; an empty bag dry-fires.
    [Xunit.Fact]
    public void AnAttackWithNoMagazineSpendsTheInventoryAndDryFiresWhenItIsEmpty()
    {
        using var app = NewGame();
        var world = app.World;
        var (shooter, _) = Duel(app, "bow", 2);
        var fired = new EventProbe<WeaponFired>(world);
        var dry = new EventProbe<DryFire>(world);

        for (int i = 0; i < 3; i++) { Press(app, shooter, "Attack"); Run(app, 12); }
        Assert.Equal(2, fired.All.Count);
        Assert.Single(dry.All);
        Assert.Equal(0, world.CountOf(shooter, Bullet));
        Assert.False(world.Has<Magazine>(shooter));
    }

    // No `ammo` is infinite: never a dry fire, never a Magazine component, and every shot is reported.
    [Xunit.Fact]
    public void AnAttackWithNoAmmoNeverRunsDry()
    {
        using var app = NewGame();
        var world = app.World;
        var (shooter, _) = Duel(app, "wand", 0);
        var fired = new EventProbe<WeaponFired>(world);
        var dry = new EventProbe<DryFire>(world);

        for (int i = 0; i < 12; i++) { Press(app, shooter, "Attack"); Run(app, 12); }
        Assert.Equal(12, fired.All.Count);
        Assert.Empty(dry.All);
        Assert.False(world.Has<Magazine>(shooter));
    }

    // Firing mid-reload does nothing (no shot, no click), and swapping away abandons the reload.
    [Xunit.Fact]
    public void TheTriggerIsDeadWhileReloadingAndASwapAbandonsIt()
    {
        using var app = NewGame();
        var world = app.World;
        var (shooter, _) = Duel(app, "sidearm", 20);
        var fired = new EventProbe<WeaponFired>(world);
        var dry = new EventProbe<DryFire>(world);

        Press(app, shooter, "Reload");
        Run(app, 3);
        Press(app, shooter, "Attack");
        Run(app, 3);
        Assert.Empty(fired.All);
        Assert.Empty(dry.All);

        world.Get<Melee>(shooter).Attack = Bow;
        Run(app, 90);
        Assert.False(world.Get<Magazine>(shooter).Reloading);
        Assert.Equal(0, Ammunition.Loaded(world, shooter, Pistol));
    }

    // `automatic` fires on every tick the cooldown allows while the button is held, at `rateOfFire`.
    [Xunit.Fact]
    public void AnAutomaticWeaponFiresAtItsRateOfFireWhileTheButtonIsHeld()
    {
        using var app = NewGame();
        var world = app.World;
        var (shooter, _) = Duel(app, "rifle", 60);
        var fired = new EventProbe<WeaponFired>(world);
        ReloadFully(app, shooter, 1.0f);
        Assert.Equal(30, Ammunition.Loaded(world, shooter, new RecordId("sage", "rifle")));

        Hold(app, shooter, "Attack", held: true);
        Run(app, 60);                                       // one second at 10 shots a second
        Hold(app, shooter, null);
        Assert.InRange(fired.All.Count, 9, 11);
    }
}
