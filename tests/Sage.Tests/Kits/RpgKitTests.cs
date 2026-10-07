#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The RPG kit (issue #27): not part of the base engine, loaded only for a game that names it in
// game.json, and what it brings — the readied spell a Cast button fires, and its own conventions.
public class RpgKitTests
{
    public RpgKitTests() { _ = TestEnv.UserRoot; }

    private static string Game(string folder) => Path.Combine(TestEnv.FolderAbove("Sage.sln"), folder);

    private static bool IsKit(string plugin) => plugin.StartsWith("sage.kits.", StringComparison.Ordinal);

    // ---- which games have it ------------------------------------------------------------------------

    // The acceptance test's two halves: the guide's example runs with no kit at all, and the Sandbox has
    // the RPG kit because its game.json says so — the simulation half only, headless, as a server would.
    [Fact]
    public void HelloHasNoKitAndTheSandboxHasTheRpgKit()
    {
        using (var hello = HeadlessApp.ForGame(Game("games/Hello"), new Hello.HelloModule()).Build())
        {
            Assert.Empty(hello.App.Game!.Kits);
            Assert.DoesNotContain(hello.PluginIds, IsKit);
        }

        using var sandbox = HeadlessApp.ForGame(Game("games/Sandbox"), new Sandbox.SandboxModule()).Build();
        Assert.Equal(new[] { RpgKitModule.Id }, sandbox.App.Game!.Kits);
        Assert.Equal(new[] { RpgKitModule.Id }, sandbox.PluginIds.Where(IsKit));   // no window: no client half
        // A kit is not a base plugin: nothing but a game.json brings it.
        Assert.DoesNotContain(BasePlugins.All(), m => IsKit(PluginInfo.Of(m).Id));
    }

    // The games made of data alone stay kit-free too.
    [Fact]
    public void TheDataOnlyGamesHaveNoKit()
    {
        foreach (string game in new[] { "tests/games/scene-only", "tests/games/camera-cut", "tests/games/no-plugins" })
        {
            using var app = HeadlessApp.ForGame(Game(game)).Build();
            Assert.DoesNotContain(app.PluginIds, IsKit);
        }
    }

    // A kit's assembly is named after its id, which is how the host finds it without being told a path.
    [Fact]
    public void AKitIdNamesItsAssembly()
    {
        Assert.Equal("Sage.Kits.Rpg", ModuleManager.KitAssemblyName("sage.kits.rpg"));
        Assert.Equal(typeof(RpgKitModule).Assembly.GetName().Name, ModuleManager.KitAssemblyName(RpgKitModule.Id));
    }

    // A kit that is not there is an error that names it and says where it looked, not a game quietly
    // missing its spellbook.
    [Fact]
    public void AKitThatIsNotThereIsAnErrorThatSaysWhere()
    {
        string folder = TestEnv.NewTempDir();
        File.WriteAllText(Path.Combine(folder, "game.json"), """{ "id": "kitless", "kits": ["sage.kits.nothing"] }""");

        var ex = Assert.Throws<FileNotFoundException>(() => HeadlessApp.ForGame(folder).Create());
        Assert.Contains("'sage.kits.nothing'", ex.Message);
        Assert.Contains("Sage.Kits.Nothing.dll", ex.Message);
    }

    // ---- the readied spell --------------------------------------------------------------------------

    private const string Records = """
    [
      { "type": "attribute", "id": "health", "start": 50, "min": 0, "max": 100 },
      { "type": "effect", "id": "mend", "cost": 6, "modifiers": [ { "attribute": "health", "op": "Add", "value": 10 } ] },
      { "type": "ability", "id": "heal",  "targeting": "Self", "effects": ["mend"] },
      { "type": "ability", "id": "shout", "targeting": "Self" },
      { "type": "prefab", "id": "hero", "name": "hero",
        "parts": { "character": { "layer": "player" }, "attributes": {}, "abilities": ["shout", "heal"] } }
    ]
    """;

    private static RecordId Id(string name) => new("sage", name);

    private static (HeadlessApp App, Entity Hero) Hero(bool kit, string extra = "")
    {
        var builder = HeadlessApp.Gameplay().File("data/kit_test.json", Records);
        if (kit) builder.With(new Sage.UI.UiModule(), new RpgKitModule());
        if (extra.Length > 0) builder.File("data/extra.json", extra);
        var app = builder.Boot("kit");
        var ground = app.World.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        app.World.Add(ground, Collider.Box(new Vector3(50, 1, 50)));
        var hero = app.World.Spawn(Id("hero"), Vector3.Zero);
        app.World.RunFixed(1f / 60f);
        return (app, hero);
    }

    // Not player-controlled, so nothing overwrites its intent from PlayerInput: the test is the controller.
    private static void Press(World world, Entity who, ActionId action)
    {
        world.Get<PawnIntent>(who).Pressed = default(ActionMask).With(action);
        world.RunFixed(1f / 60f);
        world.Get<PawnIntent>(who).Pressed = default;
        world.RunFixed(1f / 60f);
    }

    // Daggerfall's casting, end to end: nothing readied means the first spell learned; readying another
    // makes the Cast button throw that one instead. The cast itself is the base's, through world.Cast's
    // queue, so a cast by button and a cast by console are the same cast.
    [Fact]
    public void TheCastButtonFiresTheReadiedSpell()
    {
        var (app, hero) = Hero(kit: true);
        using var disposeApp = app;
        var world = app.World;
        var cast = app.Engine.Actions.Get(RpgConventions.Of(world).CastAction);
        var casts = new EventProbe<AbilityCast>(world);

        Assert.Equal(Id("shout"), world.Readied(hero));   // the first one learned
        Press(world, hero, cast);
        Assert.Equal(Id("shout"), Assert.Single(casts.All).Ability);

        Assert.True(world.Ready(hero, Id("heal")));
        Assert.False(world.Ready(hero, Id("fireball")));   // not known: nothing changes
        Press(world, hero, cast);
        Assert.Equal(Id("heal"), casts.All.Last().Ability);
        Assert.Equal(60f, world.Attribute(hero, Id("health")));
    }

    // Without the kit there is no Cast button at all: the base casts what it is asked to, and asking is
    // world.Cast. The readied spell in the component is just data it carries for a kit.
    [Fact]
    public void WithoutTheKitThereIsNoCastButtonButCastingWorks()
    {
        var (app, hero) = Hero(kit: false);
        using var disposeApp = app;
        var world = app.World;
        Assert.False(app.Engine.Actions.TryGet("Cast", out _));
        Assert.DoesNotContain(app.PluginIds, IsKit);

        var casts = new EventProbe<AbilityCast>(world);
        Assert.True(world.Cast(hero, Id("heal")));
        world.RunFixed(1f / 60f);
        Assert.Equal(Id("heal"), Assert.Single(casts.All).Ability);
    }

    // ---- the kit's conventions ----------------------------------------------------------------------

    // The namespace composed spells live in was a constant, "custom"; now it is the game's to choose.
    [Fact]
    public void AGameChoosesTheNamespaceItsComposedSpellsLiveIn()
    {
        var (app, hero) = Hero(kit: true, """[ { "type": "rpg_conventions", "id": "rpg", "spellNamespace": "invented" } ]""");
        using var disposeApp = app;
        Assert.Equal(0, app.Records.ErrorCount);

        var made = Spellmaker.Compose(app.World, new SpellDraft { Name = "Warm Hands", Targeting = AbilityTargeting.Self, Effects = { Id("mend") } });
        Assert.True(made.Ok, made.Problem);
        Assert.Equal(new RecordId("invented", "warm_hands"), made.Id);
        Assert.True(app.Records.Exists(made.Id));
    }

    // With none, the defaults: the engine's words, as before the kit existed.
    [Fact]
    public void WithoutRpgConventionsTheKitUsesItsDefaults()
    {
        var (app, _) = Hero(kit: true);
        using var __ = app;
        var conventions = RpgConventions.Of(app.World);
        Assert.Same(RpgConventionsRecord.Default, conventions);
        Assert.Equal("custom", conventions.SpellNamespace);
        Assert.Equal("Cast", conventions.CastAction);
    }

    // Content mistakes are load errors (issue #22): a cast button nobody registered, and two records
    // where which one counted would depend on the order of the mounts.
    [Fact]
    public void RpgConventionsMistakesAreLoadErrors()
    {
        var (app, _) = Hero(kit: true, """[ { "type": "rpg_conventions", "id": "rpg", "castAction": "Zap" } ]""");
        using (app) Assert.Equal(1, app.Records.ErrorCount);

        var ex = Assert.Throws<InvalidDataException>(() => Hero(kit: true, """
            [ { "type": "rpg_conventions", "id": "one" }, { "type": "rpg_conventions", "id": "two" } ]
            """));
        Assert.Contains("sage:one, sage:two", ex.Message);
    }

    // ---- equipment slots ----------------------------------------------------------------------------

    private const string Armoury = """
    [
      { "type": "item", "id": "sword",  "label": "a sword",  "slot": "MainHand" },
      { "type": "item", "id": "helmet", "label": "a helmet", "slot": "Head" }
    ]
    """;

    private static EquipSlots SlotsOf(HeadlessApp app) => app.Engine.Modules.Modules.OfType<ItemsModule>().Single().Slots;

    // The base assumes no body: it has no equipment slots, and an item that names one nobody registered
    // is a load error. The kit's default is two hands; a game adds what else it wears in its Init.
    [Fact]
    public void TheKitHasTwoHandsAndTheBaseNone()
    {
        using (var bare = HeadlessApp.Gameplay().File("data/armoury.json", Armoury).Boot("bare"))
        {
            Assert.Empty(SlotsOf(bare).Names);
            Assert.Equal(2, bare.Records.ErrorCount);   // neither slot exists
        }

        using var kit = HeadlessApp.Gameplay().With(new Sage.UI.UiModule(), new RpgKitModule())
            .OnRegistered(app => SlotsOf(app).Register("Head"))   // the game's own, beside the kit's
            .File("data/armoury.json", Armoury).Boot("kit");
        Assert.Equal(new[] { RpgKitModule.MainHand, RpgKitModule.OffHand, "Head" }, SlotsOf(kit).Names);
        Assert.Equal(0, kit.Records.ErrorCount);

        var world = kit.World;
        var knight = world.Create(Transform.At(Vector3.Zero), "knight");
        world.AddInventory(knight);
        Assert.True(world.Give(knight, Id("sword")) && world.Give(knight, Id("helmet")));
        Assert.True(world.Equip(knight, Id("sword")) && world.Equip(knight, Id("helmet")));
        Assert.Equal(Id("sword"), world.Get<Equipment>(knight).In(RpgKitModule.MainHand));
        Assert.Equal(Id("helmet"), world.Get<Equipment>(knight).In("head"));   // slot names ignore case

        world.Unequip(knight, "Head");
        Assert.True(world.Get<Equipment>(knight).In("Head").IsEmpty);
        Assert.Equal(Id("sword"), world.Get<Equipment>(knight).In(RpgKitModule.MainHand));
    }

    // A slot in data beside the kit's (issue #384): a record naming the kit's MainHand is that slot,
    // not a second one, and the data's Head comes after the code's two hands.
    [Fact]
    public void ASlotInDataJoinsTheKitsHands()
    {
        using var kit = HeadlessApp.Gameplay().With(new Sage.UI.UiModule(), new RpgKitModule())
            .File("data/slots.json", """
                [{ "type": "equip_slot", "id": "head", "name": "Head" },
                 { "type": "equip_slot", "id": "mainhand", "name": "mainhand" }]
                """)
            .File("data/armoury.json", Armoury).Boot("kit");
        Assert.Equal(new[] { RpgKitModule.MainHand, RpgKitModule.OffHand, "Head" }, SlotsOf(kit).Names);
        Assert.Equal(0, kit.Records.ErrorCount);
    }
}
