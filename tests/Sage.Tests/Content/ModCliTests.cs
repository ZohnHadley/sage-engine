#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// `sage validate|schema|mods` with mods (phase 4j, issue 4j-5): ContentValidation reads each mod.json, orders the
// mods as a boot would (ModManager), mounts them after the game's, and reports what `sage mods` prints; the
// template mod validates against a game; mod.json and game.json have schemas.
public class ModCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sage-modcli-" + Guid.NewGuid().ToString("N"));

    public ModCliTests() { _ = TestEnv.UserRoot; }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Repo => TestEnv.FolderAbove("Sage.sln");

    private string Game()
    {
        string game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(game, "content", "data"));
        File.WriteAllText(Path.Combine(game, "game.json"),
            """{ "name": "G", "id": "village", "mounts": ["content"], "modsDirectory": "mods", "version": "1.0.0" }""");
        File.WriteAllText(Path.Combine(game, "content", "data", "trader.json"),
            """[ { "type": "prefab", "id": "traders", "name": "traders" } ]""");
        return game;
    }

    private static string Mod(string folder, string id, string records, string extra = "")
    {
        Directory.CreateDirectory(Path.Combine(folder, "data"));
        File.WriteAllText(Path.Combine(folder, "mod.json"),
            $$"""{ "id": "{{id}}", "name": "{{id}}", "version": "1.0.0", "game": "village" {{extra}} }""");
        File.WriteAllText(Path.Combine(folder, "data", "records.json"), records);
        return folder;
    }

    private ValidationReport Run(string game, IReadOnlyList<string>? mods = null, bool gameMods = false, Action<Engine>? inspect = null) =>
        ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = game,
            EngineContentDirectory = Path.Combine(Repo, "engine_content"),
            AvailablePlugins = BasePlugins.All(),
            Mods = mods ?? Array.Empty<string>(),
            GameMods = gameMods,
            Inspect = inspect,
        });

    private const string Rename = """[ { "type": "prefab", "id": "village:traders", "patch": true, "name": "NAME" } ]""";

    [Fact]
    public void AModNamedWithMods_IsReadOrderedAndMounted_AfterTheGame()
    {
        string game = Game();
        string a = Mod(Path.Combine(_root, "a"), "alpha", """[ { "type": "prefab", "id": "guild", "name": "guild" } ]""");
        string? mount = null;
        var report = Run(game, new[] { a }, inspect: engine => mount = engine.Vfs.Mounts[^1].Name);

        Assert.True(report.Ok, string.Join("\n", report.Errors));
        Assert.Equal("mods/alpha", mount);
        Assert.Equal("alpha", Assert.Single(report.Mods.Active).Id);
        Assert.Contains(report.ModLines, l => l.Contains("1. alpha 1.0.0"));
        Assert.Contains(report.ReportLines, l => l.Contains("added prefab: alpha:guild"));
    }

    [Fact]
    public void AFolderOfMods_HoldsEachSubfolderWithAModJson_AndSkipsTheRest()
    {
        string game = Game();
        string mods = Path.Combine(_root, "mods");
        Mod(Path.Combine(mods, "one"), "one", "[]");
        Mod(Path.Combine(mods, "two"), "two", "[]");
        Directory.CreateDirectory(Path.Combine(mods, "notes"));
        var report = Run(game, new[] { mods });

        Assert.Equal(new[] { "one", "two" }, report.Mods.Active.Select(m => m.Id));
        Assert.Contains(report.Warnings, w => w.Contains("notes has no mod.json"));
    }

    [Fact]
    public void GameMods_AreTheModsInTheGamesOwnModsDirectory()
    {
        string game = Game();
        Mod(Path.Combine(game, "mods", "inside"), "inside", "[]");
        string outside = Mod(Path.Combine(_root, "outside"), "outside", "[]", """, "loadAfter": ["inside"]""");

        Assert.Empty(Run(game).Mods.Active);
        Assert.Equal(new[] { "inside" }, Run(game, gameMods: true).Mods.Active.Select(m => m.Id));
        Assert.Equal(new[] { "inside", "outside" }, Run(game, new[] { outside }, gameMods: true).Mods.Active.Select(m => m.Id));
    }

    [Fact]
    public void TheOrderIsTheModManagers_NotTheOrderOnTheCommandLine()
    {
        string game = Game();
        string late = Mod(Path.Combine(_root, "late"), "late", "[]", """, "loadAfter": ["early"]""");
        string early = Mod(Path.Combine(_root, "early"), "early", "[]");
        var report = Run(game, new[] { late, early });

        Assert.Equal(new[] { "early", "late" }, report.Mods.Active.Select(m => m.Id));
    }

    [Fact]
    public void ARefusedMod_IsReportedWithItsReason_AndTheGameStillValidates()
    {
        string game = Game();
        string wrong = Mod(Path.Combine(_root, "wrong"), "wrong", "[]", """, "dependencies": { "missing": "^1.0" }""");
        string broken = Path.Combine(_root, "broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "mod.json"), """{ "id": "broken", "loadafter_typo": [] }""");
        var report = Run(game, new[] { wrong, broken });

        Assert.True(report.Ok, string.Join("\n", report.Errors));
        Assert.Empty(report.Mods.Active);
        Assert.Equal(2, report.Mods.Refused.Count);
        Assert.Contains(report.ModLines, l => l.Contains("wrong:") && l.Contains("missing"));
        Assert.Contains(report.ModLines, l => l.Contains("broken") && l.Contains("can't be read"));
    }

    [Fact]
    public void TwoModsSettingOneField_ConflictAsAWarning_NotAnError()
    {
        string game = Game();
        string a = Mod(Path.Combine(_root, "a"), "alpha", Rename.Replace("NAME", "guild"));
        string b = Mod(Path.Combine(_root, "b"), "beta", Rename.Replace("NAME", "league"), """, "loadAfter": ["alpha"]""");
        var report = Run(game, new[] { a, b });

        Assert.True(report.Ok, string.Join("\n", report.Errors));
        Assert.Equal(1, report.Conflicts);
        Assert.Contains(report.ReportLines, l => l.Contains("alpha, beta") && l.Contains("beta won"));
        Assert.Equal(0, Run(game, new[] { a }).Conflicts);
    }

    [Fact]
    public void ADataErrorInAMod_FailsValidation()
    {
        string game = Game();
        string a = Mod(Path.Combine(_root, "a"), "alpha", """[ { "type": "prefab", "id": "guild", "colour": 1 } ]""");
        Assert.False(Run(game, new[] { a }).Ok);
    }

    [Fact]
    public void TheTemplateMod_LoadsInAGame_AddsItsWeapon_AndPatchesThePlayer()
    {
        string game = Path.Combine(_root, "templated");
        Directory.CreateDirectory(Path.Combine(game, "content", "data"));
        File.WriteAllText(Path.Combine(game, "game.json"), """{ "name": "G", "id": "mygame", "mounts": ["content"] }""");
        File.Copy(Path.Combine(Repo, "sdk", "Sage.Templates", "content", "sage-game", "content", "data", "scene.json"),
            Path.Combine(game, "content", "data", "scene.json"));

        // What `dotnet new sage-mod-data --game mygame` makes: the template with its game id substituted.
        string template = Path.Combine(Repo, "sdk", "Sage.Templates", "content", "sage-mod-data");
        string mod = Path.Combine(_root, "MyMod");
        foreach (string file in new[] { "mod.json", Path.Combine("data", "records.json") })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(mod, file))!);
            File.WriteAllText(Path.Combine(mod, file), File.ReadAllText(Path.Combine(template, file)).Replace("targetgame", "mygame"));
        }

        var report = Run(game, new[] { mod });
        Assert.True(report.Ok, string.Join("\n", report.Errors));
        Assert.Equal("mymod", Assert.Single(report.Mods.Active).Id);
        Assert.Contains(report.ReportLines, l => l.Contains("added item: mymod:falchion"));
        Assert.Contains(report.ReportLines, l => l.Contains("patched prefab mygame:player"));
        Assert.Equal(0, report.Conflicts);
    }

    // ---- mod.json and game.json have schemas -------------------------------------------------------------

    private static SchemaValidator Validator() => new(Path.Combine(Repo, "schemas"));

    private static JsonNode Json(string path) => SchemaValidator.ReadData(path)!;

    [Fact]
    public void TheModSchema_AcceptsTheTemplatesModJson_AndRefusesAnUnknownKey()
    {
        var validator = Validator();
        var template = Json(Path.Combine(Repo, "sdk", "Sage.Templates", "content", "sage-mod-data", "mod.json"));
        Assert.Empty(validator.Validate(template, RecordSchemas.Mod));

        template["loadafter"] = new JsonArray();
        Assert.NotEmpty(validator.Validate(template, RecordSchemas.Mod));
        Assert.NotEmpty(validator.Validate(JsonNode.Parse("""{ "name": "no id" }"""), RecordSchemas.Mod));
        Assert.NotEmpty(validator.Validate(JsonNode.Parse("""{ "id": "x", "dependencies": ["y"] }"""), RecordSchemas.Mod));
    }

    [Fact]
    public void TheGameSchema_AcceptsEveryGamesGameJson()
    {
        var validator = Validator();
        var games = new[] { "games", Path.Combine("tests", "games") }
            .SelectMany(folder => Directory.GetDirectories(Path.Combine(Repo, folder)))
            .Select(game => Path.Combine(game, "game.json")).Where(File.Exists).ToList();
        Assert.NotEmpty(games);
        foreach (string game in games)
            Assert.True(validator.Validate(Json(game), RecordSchemas.Game).Count == 0, $"{game}: " + string.Join("; ", validator.Validate(Json(game), RecordSchemas.Game)));

        Assert.NotEmpty(validator.Validate(JsonNode.Parse("""{ "id": "g", "mount": ["content"] }"""), RecordSchemas.Game));
    }

    [Fact]
    public void TheWorkspaceSettings_MapModJsonAndGameJsonOntoTheirSchemas()
    {
        var settings = SchemaValidator.ReadData(Path.Combine(Repo, ".vscode", "settings.json"))!;
        foreach (var (match, schema) in new[] { ("**/mod.json", RecordSchemas.Mod), ("**/game.json", RecordSchemas.Game) })
        {
            var mapping = settings["json.schemas"]!.AsArray().Single(m => (string?)m!["url"] == "./schemas/" + schema)!;
            Assert.Contains(match, mapping["fileMatch"]!.AsArray().Select(n => (string?)n));
            Assert.True(File.Exists(Path.Combine(Repo, "schemas", schema)));
        }
    }
}
