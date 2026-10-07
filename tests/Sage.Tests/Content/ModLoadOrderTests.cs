#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Mod manifests and the load order (phase 4j-1): mod.json read as strictly as game.json, the stable
// topological sort, every reason a mod is refused, and user://mods.json.
public class ModLoadOrderTests
{
    private static readonly GameManifest Game = new() { Id = "village", Version = "1.2.0" };
    private static readonly SemVersion Engine = new(0, 1, 0);

    private static ModManifest Mod(string id, string json = "")
    {
        string folder = Path.Combine(TestEnv.NewTempDir(), id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "mod.json"), $$"""{ "id": "{{id}}", "version": "1.0.0" {{(json == "" ? "" : ", " + json)}} }""");
        return ModManifest.Load(folder);
    }

    private static string Order(ModLoadResult r) => string.Join(",", r.Active.Select(m => m.Id));
    private static string Why(ModLoadResult r, string id) => r.Refused.Single(x => x.Id == id).Reason;

    // ---- the manifest ---------------------------------------------------------------------------------

    [Fact]
    public void AManifestReadsEveryField()
    {
        var m = Mod("blades", """
            "name": "Better Blades", "author": "me", "description": "swords", "game": "village", "gameVersion": ">=1.0",
            "sage": ">=0.1", "dependencies": { "core": "^1.0" }, "loadAfter": ["a"], "loadBefore": ["b"], "incompatible": ["c"]
            """);
        Assert.Equal(("Better Blades", "me", "swords", "village"), (m.Name, m.Author, m.Description, m.Game));
        Assert.Equal("^1.0", m.Dependencies["core"]);
        Assert.Equal(new[] { "a" }, m.LoadAfter);
        Assert.Equal(new[] { "b" }, m.LoadBefore);
        Assert.Equal(new[] { "c" }, m.Incompatible);
        Assert.Equal(new SemVersion(1, 0, 0), m.SemVersion);
    }

    [Fact]
    public void AnUnknownKeyInAModJsonIsAnErrorAsInGameJson()
    {
        string folder = TestEnv.NewTempDir();
        File.WriteAllText(Path.Combine(folder, "mod.json"), """{ "id": "x", "dependancies": {} }""");
        var ex = Assert.Throws<InvalidDataException>(() => ModManifest.Load(folder));
        Assert.Contains("dependancies", ex.Message);
    }

    [Theory]
    [InlineData("""{ "name": "no id" }""", "\"id\" is required")]
    [InlineData("""{ "id": "Bad Id" }""", "not a valid namespace")]
    [InlineData("""{ "id": "a:b" }""", "not a valid namespace")]
    [InlineData("""{ "id": "x", "version": "one" }""", "is not a version")]
    [InlineData("""{ "id": "x", "gameVersion": ">=oops" }""", "gameVersion")]
    [InlineData("""{ "id": "x", "dependencies": { "y": "^bad" } }""", "dependencies")]
    public void ABrokenManifestIsAnErrorThatSaysWhat(string json, string expected)
    {
        string folder = TestEnv.NewTempDir();
        File.WriteAllText(Path.Combine(folder, "mod.json"), json);
        Assert.Contains(expected, Assert.Throws<InvalidDataException>(() => ModManifest.Load(folder)).Message);
    }

    [Fact]
    public void GameJsonTakesAVersionAndRejectsAMalformedOne()
    {
        string folder = TestEnv.NewTempDir();
        File.WriteAllText(Path.Combine(folder, "game.json"), """{ "id": "g", "version": "2.1.0" }""");
        Assert.Equal("2.1.0", GameManifest.Load(folder).Version);
        File.WriteAllText(Path.Combine(folder, "game.json"), """{ "id": "g", "version": "two" }""");
        Assert.Contains("version", Assert.Throws<InvalidDataException>(() => GameManifest.Load(folder)).Message);
    }

    // ---- the sort -------------------------------------------------------------------------------------

    [Fact]
    public void WithNoConstraintsTheOrderIsTheUsersThenTheId()
    {
        var found = new[] { Mod("c"), Mod("a"), Mod("b"), Mod("d") };
        Assert.Equal("a,b,c,d", Order(ModLoadOrder.Resolve(found, null, Game, Engine)));
        var list = new ModList { Order = { "c", "b" } };
        Assert.Equal("c,b,a,d", Order(ModLoadOrder.Resolve(found, list, Game, Engine)));
    }

    [Fact]
    public void ADependencyLoadsBeforeItsDependentWhateverTheUsersOrderSays()
    {
        var found = new[] { Mod("top", """ "dependencies": { "base": "*" } """), Mod("base") };
        var list = new ModList { Order = { "top", "base" } };
        Assert.Equal("base,top", Order(ModLoadOrder.Resolve(found, list, Game, Engine)));
    }

    [Fact]
    public void LoadAfterAndLoadBeforeOrderModsAndIgnoreOnesThatAreNotThere()
    {
        var found = new[] { Mod("a", """ "loadAfter": ["c", "ghost"] """), Mod("b", """ "loadBefore": ["a"] """), Mod("c") };
        var r = ModLoadOrder.Resolve(found, null, Game, Engine);
        Assert.Equal("b,c,a", Order(r));
        Assert.Empty(r.Refused);
    }

    [Fact]
    public void TheSortIsStableAndFreeModsKeepTheUsersOrderAroundAConstraint()
    {
        // z must follow y; the user put z first, x last. y has to go before z, but x stays last.
        var found = new[] { Mod("x"), Mod("y"), Mod("z", """ "loadAfter": ["y"] """) };
        var list = new ModList { Order = { "z", "y", "x" } };
        Assert.Equal("y,z,x", Order(ModLoadOrder.Resolve(found, list, Game, Engine)));
        Assert.Equal("y,z,x", Order(ModLoadOrder.Resolve(found.Reverse(), list, Game, Engine)));
    }

    [Fact]
    public void AModTheListDoesNotKnowIsOnAndGoesAtTheEnd()
    {
        var found = new[] { Mod("old"), Mod("new"), Mod("older") };
        var list = new ModList { Order = { "older", "old" } };
        Assert.Equal("older,old,new", Order(ModLoadOrder.Resolve(found, list, Game, Engine)));
    }

    [Fact]
    public void ADisabledModIsListedAndNotActive()
    {
        var r = ModLoadOrder.Resolve(new[] { Mod("a"), Mod("b") }, new ModList { Disabled = { "a" } }, Game, Engine);
        Assert.Equal("b", Order(r));
        Assert.Equal("a", r.Disabled.Single().Id);
        Assert.Empty(r.Refused);
    }

    // ---- refusals -------------------------------------------------------------------------------------

    [Fact]
    public void AMissingDependencyRefusesTheModAndItsDependents()
    {
        var found = new[] { Mod("a", """ "dependencies": { "ghost": "*" } """), Mod("b", """ "dependencies": { "a": "*" } """), Mod("c") };
        var r = ModLoadOrder.Resolve(found, null, Game, Engine);
        Assert.Equal("c", Order(r));
        Assert.Contains("needs 'ghost'", Why(r, "a"));
        Assert.Contains("not installed", Why(r, "a"));
        Assert.Contains("needs 'a'", Why(r, "b"));
        Assert.Contains("refused", Why(r, "b"));
    }

    [Fact]
    public void ADependencyOutsideItsRangeIsRefused()
    {
        var found = new[] { Mod("a", """ "dependencies": { "b": ">=2.0" } """), Mod("b") };
        var r = ModLoadOrder.Resolve(found, null, Game, Engine);
        Assert.Equal("b", Order(r));
        Assert.Contains(">=2.0", Why(r, "a"));
        Assert.Contains("1.0.0", Why(r, "a"));
    }

    [Fact]
    public void ADependencyTheUserSwitchedOffSaysSo()
    {
        var found = new[] { Mod("a", """ "dependencies": { "b": "*" } """), Mod("b") };
        var r = ModLoadOrder.Resolve(found, new ModList { Disabled = { "b" } }, Game, Engine);
        Assert.Empty(r.Active);
        Assert.Contains("switched off", Why(r, "a"));
    }

    [Fact]
    public void ACycleRefusesItsMembersAndNamesThemButNotInnocentBystanders()
    {
        var found = new[]
        {
            Mod("a", """ "dependencies": { "b": "*" } """), Mod("b", """ "dependencies": { "a": "*" } """),
            Mod("free"), Mod("after_cycle", """ "loadAfter": ["a"] """),
        };
        var r = ModLoadOrder.Resolve(found, null, Game, Engine);
        Assert.Equal("after_cycle,free", Order(r));
        Assert.Contains("cycle", Why(r, "a"));
        Assert.Contains("a -> b -> a", Why(r, "a"));
        Assert.Contains("b -> a -> b", Why(r, "b"));
    }

    [Fact]
    public void AModIncompatibleWithAnEarlierOneIsRefusedAndTheEarlierOneStays()
    {
        var found = new[] { Mod("a"), Mod("b", """ "incompatible": ["a"] """), Mod("c") };
        var r = ModLoadOrder.Resolve(found, null, Game, Engine);
        Assert.Equal("a,c", Order(r));
        Assert.Contains("incompatible with 'a'", Why(r, "b"));
        // The user puts b first: now a is the later one.
        r = ModLoadOrder.Resolve(found, new ModList { Order = { "b", "a", "c" } }, Game, Engine);
        Assert.Equal("b,c", Order(r));
        Assert.Contains("incompatible with 'b'", Why(r, "a"));
    }

    [Fact]
    public void TheWrongGameGameVersionOrEngineRefusesAMod()
    {
        var found = new[]
        {
            Mod("fine", """ "game": "village", "gameVersion": "^1.0", "sage": ">=0.1" """),
            Mod("othergame", """ "game": "castle" """),
            Mod("oldgame", """ "gameVersion": ">=2.0" """),
            Mod("newengine", """ "sage": ">=0.5" """),
        };
        var r = ModLoadOrder.Resolve(found, null, Game, Engine);
        Assert.Equal("fine", Order(r));
        Assert.Contains("'castle'", Why(r, "othergame"));
        Assert.Contains("game version >=2.0", Why(r, "oldgame"));
        Assert.Contains("Sage >=0.5", Why(r, "newengine"));
    }

    [Fact]
    public void WithoutAGameVersionTheCheckIsSkippedWithANote()
    {
        var game = new GameManifest { Id = "village" };
        var r = ModLoadOrder.Resolve(new[] { Mod("a", """ "gameVersion": ">=9.0" """) }, null, game, Engine);
        Assert.Equal("a", Order(r));
        Assert.Contains(r.Notes, n => n.Contains("'a'") && n.Contains("gameVersion"));
    }

    // Code mods (phase 9, issue #396): one that names its assemblies is ordered like any mod (the app loads its
    // code); "kind": "code" with nothing to load is refused.
    [Fact]
    public void ACodeModIsOrderedLikeAnyMod_AndKindCodeWithNoAssembliesIsRefused()
    {
        var r = ModLoadOrder.Resolve(new[] { Mod("coder", "\"kind\": \"code\", \"assemblies\": [\"bin/A.dll\"]"), Mod("talker", "\"kind\": \"code\"") }, null, Game, Engine);
        Assert.Equal("coder", Order(r));
        Assert.Contains("names no \"assemblies\"", Why(r, "talker"));
    }

    [Fact]
    public void AModMayNotTakeTheEnginesTheGamesOrAKitsNamespace()
    {
        var found = new[] { Mod("sage"), Mod("village"), Mod("rpg"), Mod("mine") };
        var r = ModLoadOrder.Resolve(found, null, Game, Engine, reservedIds: new[] { "rpg" });
        Assert.Equal("mine", Order(r));
        Assert.Equal(3, r.Refused.Count);
    }

    [Fact]
    public void TheSameIdTwiceRefusesTheSecond()
    {
        var r = ModLoadOrder.Resolve(new[] { Mod("a"), Mod("a") }, null, Game, Engine);
        Assert.Equal("a", Order(r));
        Assert.Contains("another mod with the id 'a'", Why(r, "a"));
    }

    // ---- user://mods.json -----------------------------------------------------------------------------

    [Fact]
    public void ModListRoundTripsAndAMissingFileIsTheDefault()
    {
        string path = Path.Combine(TestEnv.NewTempDir(), "sub", "mods.json");
        var none = ModList.Load(path, out var warning);
        Assert.Null(warning);
        Assert.Empty(none.Order);

        new ModList { Order = { "b", "a" }, Disabled = { "c" } }.Save(path);
        Assert.False(File.Exists(path + ".tmp"));
        var back = ModList.Load(path, out warning);
        Assert.Null(warning);
        Assert.Equal(new[] { "b", "a" }, back.Order);
        Assert.Equal(new[] { "c" }, back.Disabled);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "order": "a" }""")]
    [InlineData("""{ "ordre": ["a"] }""")]
    [InlineData("null")]
    public void ABadModsJsonFallsBackToTheDefaultWithAWarning(string content)
    {
        string path = Path.Combine(TestEnv.NewTempDir(), "mods.json");
        File.WriteAllText(path, content);
        var list = ModList.Load(path, out var warning);
        Assert.NotNull(warning);
        Assert.Contains("default mod order", warning);
        Assert.Empty(list.Order);
        Assert.Empty(list.Disabled);
        Assert.Equal("a,b", Order(ModLoadOrder.Resolve(new[] { Mod("b"), Mod("a") }, list, Game, Engine)));
    }
}
