#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Phase 4j's exit criterion (issue #200, docs/design/17 "As built (the 4j exit game, issue 4j-7)"): a data mod
// that adds a weapon and patches a trader loads, and its conflicts are reported. tests/games/mods has no C#: a
// village with a trader (an NPC whose `inventory` part is his stock), a straw dummy and a player, and two mods
// in its `mods/` folder, found and mounted as the host finds them. better_blades adds a falchion to the stock
// and renames the trader; rival_trade adds a spear, renames him too, places a stall, has a cart to spawn and re-skins the falchion.
// The player buys through the RPG kit's shop screen (ShopView), driven by a gamepad, as RpgScreenTests does.
public class ModsExitTests
{
    public ModsExitTests() { _ = TestEnv.UserRoot; }

    private static readonly RecordId Falchion = new("better_blades", "falchion");
    private static readonly RecordId Spear = new("rival_trade", "spear");
    private static readonly RecordId Lantern = new("village", "lantern");
    private static readonly RecordId Trader = new("village", "trader");
    private static readonly RecordId Physical = new("sage", "physical");

    private const string Hilde = "Hilde the bladesmith", Rurik = "Rurik's trading post";

    private static string GameDirectory => Path.Combine(TestEnv.FolderAbove("tests"), "tests", "games", "mods");

    // The game with the mods in its own folder, ordered by the player's list `mods.json` (written to a file
    // of the test's own when given) and nothing from the shared user folder.
    private static HeadlessApp Boot(string? modsJson = null, string? saves = null)
    {
        string? list = null;
        if (modsJson != null)
        {
            list = Path.Combine(TestEnv.NewTempDir(), "mods.json");
            File.WriteAllText(list, modsJson);
        }
        var app = HeadlessApp.ForGame(GameDirectory).WithEngineContent().WithUserMods(null, list).Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        Assert.Null(app.Engine.Modules.Game);                    // no C#
        app.Engine.Saves.Root = saves ?? Path.Combine(TestEnv.NewTempDir(), "saves");
        Step(app.World, 3);
        return app;
    }

    private static void Step(World world, int ticks = 1) => NpcLocomotionTests.Step(world, ticks);

    private static string[] Active(HeadlessApp app) => app.Engine.Mods.Active.Select(m => m.Id).ToArray();

    private static Entity Player(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    private static Entity[] Named(World world, string name) =>
        world.QueryAll().Entities.ToEntityList().Where(e => e.Name == name).ToArray();

    private static Entity One(World world, string name) => Assert.Single(Named(world, name));

    private static RecordId[] Stock(World world, Entity trader) =>
        world.Get<Inventory>(trader).Items.Select(s => s.Item).ToArray();

    // What `mod_conflicts` prints, as the console shows it.
    private static List<string> ModConflicts(HeadlessApp app)
    {
        using var log = new CaptureSink();
        Assert.True(app.CVars.Execute("mod_conflicts"));
        return log.Entries.Where(e => e.Category == LogCat.Console).Select(e => e.Message).ToList();
    }

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

    // The trader's shop (the kit's rpg:shop over ShopView), with a gamepad: the falchion picked up from his
    // stock and put down in the player's bag.
    private static ShopView BuyTheFalchion(HeadlessApp app, Entity player, Entity trader)
    {
        var stack = app.World.Resources.Get<UiScreenStack>();
        stack.CloseAll();
        var shop = new RpgScreenTests.Pad(stack, stack.Open(new RecordId("rpg", "shop"), new UiBindContext(app.World, player, trader)));
        var view = Assert.IsType<ShopView>(shop.Screen.ViewModel);
        shop.NavUntil(UiNavigation.Right, p => p.Focused is GridCell c && c.Grid == view.Stock && c.Item?.Item == Falchion);
        shop.A();                                                                       // pick it up
        Assert.True(view.HeldIsStock);
        Assert.Equal(30, view.HeldPrice);                                               // its value, by the stub rule
        shop.NavUntil(UiNavigation.Right, p => p.Focused is GridCell c && c.Grid == view.Bag && c.Item == null);
        shop.A();                                                                       // put it in the bag: bought
        Assert.Equal("", view.Message);
        return view;
    }

    [Xunit.Fact]
    public void TheModsGameValidates_WithItsMods()
    {
        var report = ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = GameDirectory,
            EngineContentDirectory = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content"),
            AvailablePlugins = BasePlugins.All(),
            GameMods = true,
        });
        Assert.True(report.Ok, string.Join("\n", report.Errors));
        Assert.Equal(new[] { "better_blades", "rival_trade" }, report.Mods.Active.Select(m => m.Id));
        Assert.Empty(report.Mods.Refused);
        Assert.Equal(2, report.Conflicts);                                              // warnings, not errors
    }

    // Acceptance: both mods found in the game's mods/ folder and mounted after it; the falchion is a record of
    // better_blades', the trader stocks the game's goods and both mods' (each mod's `items+`, merged), and the
    // player buys the falchion through the shop, wields it in the kit's main hand and lands a cut on the dummy.
    [Xunit.Fact]
    public void ModsExit_TheFalchionIsBoughtFromTheTrader_Wielded_AndLandsAHit()
    {
        using var app = Boot();
        var world = app.World;
        Assert.Equal(new[] { "better_blades", "rival_trade" }, Active(app));
        Assert.Equal(new[] { "mods/better_blades", "mods/rival_trade" }, app.Vfs.Mounts.Select(m => m.Name).TakeLast(2));
        var falchion = app.Records.Get<ItemRecord>(Falchion);
        Assert.Equal(new RecordId("better_blades", "falchion_cut"), falchion.Attack.Id);

        // rival_trade loads last, so its name wins; the stock is the game's two and one from each mod.
        var trader = One(world, Rurik);
        Assert.Equal(new[] { Lantern, new RecordId("village", "bread"), Falchion, Spear }, Stock(world, trader));

        var player = Player(world);
        var view = BuyTheFalchion(app, player, trader);
        Assert.Equal(Rurik, view.MerchantName);
        Assert.Equal(30, view.Balance);
        Assert.Equal(1, world.CountOf(player, Falchion));
        Assert.Equal(0, world.CountOf(trader, Falchion));
        world.Resources.Get<UiScreenStack>().CloseAll();

        Assert.True(world.Equip(player, Falchion));
        Step(world, 2);
        Assert.Equal(Falchion, world.Get<Equipment>(player).In(RpgKitModule.MainHand));

        var dummy = One(world, "straw dummy");
        var at = world.Get<Transform>(dummy).LocalPosition;
        world.Teleport(player, Transform.At(new Vector3(at.X, 0.1f, at.Z + 1.6f)));
        Step(world, 10);
        var damaged = new EventProbe<Damaged>(world);
        var chest = at with { Y = 1.1f };
        Press(app, player, null, chest, 2);
        Press(app, player, "Attack", chest, 60);
        var hit = Assert.Single(damaged.All, d => d.Hit.Target == dummy);
        Assert.Equal(player, hit.Hit.Attacker);
        Assert.Equal(Physical, hit.Hit.Type);
        Assert.Equal(15f, hit.Applied, 3);                                              // the falchion's cut
        Assert.Equal(25f, world.Attribute(dummy, new RecordId("sage", "health")), 3);
    }

    // `mod_conflicts`: both mods set the trader's name and rival_trade, later, won; both ship
    // textures/falchion.png and rival_trade's is the one used. Both adding to the stock is no conflict.
    [Xunit.Fact]
    public void ModsExit_ModConflictsReportsTheNameAndTheTexture()
    {
        using var app = Boot();
        var lines = ModConflicts(app);
        Assert.Contains("2 conflict(s) between mods (the later mod wins):", lines);
        Assert.Contains("  prefab village:trader name: better_blades, rival_trade; rival_trade won", lines);
        Assert.Contains("  asset textures/falchion.png: better_blades, rival_trade; rival_trade won", lines);
        Assert.DoesNotContain(lines, l => l.Contains(" won") && l.Contains("items"));   // both `items+`: merged
        Assert.Contains("  shadows textures/falchion.png in better_blades", lines);
        Assert.Contains("  patched prefab village:trader (overrides village/content): set name, add parts.inventory.items", lines);
        Assert.Equal("mods/rival_trade", app.Vfs.Which(VirtualPath.Parse("textures/falchion.png"))!.Name);
    }

    // The player's list puts rival_trade first: better_blades now loads last and wins both.
    [Xunit.Fact]
    public void ModsExit_WithTheOrderReversed_TheOtherModWins()
    {
        using var app = Boot("""{ "order": ["rival_trade", "better_blades"] }""");
        Assert.Equal(new[] { "rival_trade", "better_blades" }, Active(app));
        var world = app.World;
        var trader = One(world, Hilde);
        Assert.Empty(Named(world, Rurik));
        Assert.Equal(new[] { Lantern, new RecordId("village", "bread"), Spear, Falchion }, Stock(world, trader));

        var lines = ModConflicts(app);
        Assert.Contains("  prefab village:trader name: rival_trade, better_blades; better_blades won", lines);
        Assert.Contains("  asset textures/falchion.png: rival_trade, better_blades; better_blades won", lines);
        Assert.Equal("mods/better_blades", app.Vfs.Which(VirtualPath.Parse("textures/falchion.png"))!.Name);
    }

    // rival_trade switched off in the player's list: not mounted, its spear and stall are gone, the trader is
    // better_blades', and there is nothing to report.
    [Xunit.Fact]
    public void ModsExit_WithRivalTradeSwitchedOff_ItsItemIsGoneAndNothingIsReported()
    {
        using var app = Boot("""{ "disabled": ["rival_trade"] }""");
        Assert.Equal(new[] { "better_blades" }, Active(app));
        Assert.Equal("rival_trade", Assert.Single(app.Engine.Mods.Disabled).Id);
        Assert.DoesNotContain(app.Vfs.Mounts, m => m.Name == "mods/rival_trade");
        Assert.False(app.Records.TryGet(Spear, out ItemRecord _));

        var world = app.World;
        var trader = One(world, Hilde);
        Assert.Equal(new[] { Lantern, new RecordId("village", "bread"), Falchion }, Stock(world, trader));
        Assert.Empty(Named(world, "rival's stall"));

        var lines = ModConflicts(app);
        Assert.Contains("No conflicts between mods.", lines);
        Assert.DoesNotContain(lines, l => l.Contains("rival_trade"));
        Assert.Equal("mods/better_blades", app.Vfs.Which(VirtualPath.Parse("textures/falchion.png"))!.Name);
    }

    // A save made with both mods, loaded with rival_trade switched off: the slot and the load say the mod is
    // not active, and the load still happens. rival_trade's cart, spawned in play, comes back as a placeholder;
    // its stall, which its scene patch placed, is content no longer placed and is dropped; and nothing is
    // doubled — every other saved entity once, the bought falchion once in the bag and not back in the stock.
    [Xunit.Fact]
    public void ModsExit_ASaveWithBothLoadedWithOne_WarnsKeepsThePlaceholderAndDoublesNothing()
    {
        string saves = TestEnv.NewTempDir();
        List<PersistentId> before;
        PersistentId cartId, stallId;
        using (var both = Boot(saves: saves))
        {
            var world = both.World;
            Assert.Equal(new[] { "better_blades", "rival_trade" }, Active(both));
            BuyTheFalchion(both, Player(world), One(world, Rurik));
            world.Resources.Get<UiScreenStack>().CloseAll();
            Assert.True(both.CVars.Execute("sv_cheats 1"));
            Assert.True(both.CVars.Execute("ent_spawn rival_trade:cart -4 0.5 4"));
            Step(world, 2);
            cartId = One(world, "rival's cart").GetComponent<Persistent>().Id;
            stallId = One(world, "rival's stall").GetComponent<Persistent>().Id;
            before = Ids(world);
            Assert.Equal(before.Count, before.Distinct().Count());
            Assert.True(both.Engine.Saves.Save("market"));
        }

        using var one = Boot("""{ "disabled": ["rival_trade"] }""", saves);
        Assert.Equal(new[] { "better_blades" }, Active(one));
        var slot = Assert.Single(one.Engine.Saves.Slots);
        Assert.Equal(new[] { new SavedMod("better_blades", "1.0.0"), new SavedMod("rival_trade", "1.0.0") }, slot.Mods);
        Assert.Contains("mod 'rival_trade' 1.0.0 is not active", slot.Mismatches);
        Assert.DoesNotContain(slot.Mismatches, m => m.Contains("better_blades"));

        using (var log = new CaptureSink())
        {
            Assert.True(one.Engine.Saves.Load("market"));
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("rival_trade"));
        }
        var w = one.World;
        Step(w, 2);

        // The cart: without its mod, a placeholder that keeps its prefab's id and is written back by a save.
        var cart = w.Resolve(cartId);
        Assert.False(cart.IsNull);
        Assert.Equal(new RecordId("rival_trade", "cart"), w.Get<SavePlaceholder>(cart).Prefab);
        Assert.True(w.Resolve(stallId).IsNull);

        // Nothing doubled, nothing else lost: every id the save had once each, but the stall's, and the cart's,
        // which a query does not see (a placeholder is disabled so that nothing else does).
        var after = Ids(w);
        Assert.Equal(after.Count, after.Distinct().Count());
        Assert.Equal(before.Where(i => i != stallId && i != cartId).OrderBy(i => i.Value), after.OrderBy(i => i.Value));
        var player = Player(w);
        var trader = One(w, Hilde);                                                    // better_blades' name now
        Assert.Equal(1, w.CountOf(player, Falchion));
        Assert.Equal(0, w.CountOf(trader, Falchion));
        Assert.Equal(1, w.CountOf(trader, Lantern));
    }

    private static List<PersistentId> Ids(World world) =>
        world.Query<Persistent>().Entities.ToEntityList().Select(e => e.GetComponent<Persistent>().Id).ToList();
}
