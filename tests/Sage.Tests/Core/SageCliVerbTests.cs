#nullable enable
using System.Diagnostics;
using System.Text.Json.Nodes;
using Sage.Cli;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// `sage new` and `sage run` (issue #297): what each would start is planned by Commands.cs and tested here without
// starting it; then the real `sage` runs the real `dotnet new` on the repository's templates (installed into a
// throwaway template cache, never the machine's), so `sage new mod-data` is shown writing a mod, its schemas and
// validating it against a game, and `sage new game-client` a client half that game.json loads. CI builds the
// client half and plays the game through `sage run` (tools/smoke_run.sh --sage-run).
public class SageCliVerbTests
{
    public SageCliVerbTests() { _ = TestEnv.UserRoot; }

    private static string Repo => TestEnv.FolderAbove("Sage.sln");
    private static string Templates => Path.Combine(Repo, "sdk", "Sage.Templates", "content");

    // The `sage` this configuration built (the test project references it, so it is built first).
    private static string SageDll => Path.Combine(Repo, "src", "Sage.Cli", "bin", BuildInfo.ConfigurationName, "net8.0", "sage.dll");

    private static (int Code, string Output) Sage(params string[] args) => Dotnet(new[] { SageDll }.Concat(args).ToArray());

    private static (int Code, string Output) Dotnet(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + stderr.Result);
    }

    // A template cache of its own with the repository's templates in it: `--debug:custom-hive <it>` on every call.
    private static string Hive(params string[] templates)
    {
        string hive = TestEnv.NewTempDir();
        foreach (string template in templates)
        {
            var (code, output) = Dotnet("new", "install", Path.Combine(Templates, template), "--debug:custom-hive", hive);
            Assert.True(code == 0, output);
        }
        return hive;
    }

    // A game every engine plugin loads, with the sage-game template's scene (a `player` prefab to patch).
    private static string Game(string root, string manifest = """{ "name": "G", "id": "mygame", "mounts": ["content"] }""")
    {
        string game = Path.Combine(root, "game");
        Directory.CreateDirectory(Path.Combine(game, "content", "data"));
        File.WriteAllText(Path.Combine(game, "game.json"), manifest);
        File.Copy(Path.Combine(Templates, "sage-game", "content", "data", "scene.json"), Path.Combine(game, "content", "data", "scene.json"));
        return game;
    }

    // ---- sage new --------------------------------------------------------------------------------------------

    [Fact]
    public void New_TakesShortOrFullTemplateNames_AndPassesTheRestToDotnetNew()
    {
        var request = NewRequest.Parse(new[] { "game", "-o", "out/MyGame", "-n", "MyGame", "--feed", "/feed" }, out string? error)!;
        Assert.Null(error);
        Assert.Equal(new[] { "new", "sage-game", "-o", "out/MyGame", "-n", "MyGame", "--feed", "/feed" }, request.Command().Arguments);

        Assert.Equal("sage-game-data", NewRequest.Parse(new[] { "sage-game-data" }, out _)!.Template);
        Assert.Equal("sage-mod-data", NewRequest.Parse(new[] { "mod-data" }, out _)!.Template);
        Assert.Equal("sage-game-client", NewRequest.Parse(new[] { "game-client" }, out _)!.Template);
    }

    [Fact]
    public void New_WithAnUnknownTemplate_OrGameOnAGameTemplate_IsAUsageMistake()
    {
        Assert.Null(NewRequest.Parse(new[] { "sage-gmae" }, out string? error));
        Assert.Contains("sage-gmae", error);
        Assert.Null(NewRequest.Parse(Array.Empty<string>(), out _));
        Assert.Null(NewRequest.Parse(new[] { "game", "--game", "x" }, out error));
        Assert.Contains("--game", error);
        Assert.Null(NewRequest.Parse(new[] { "game-client", "--game", TestEnv.NewTempDir() }, out error));
        Assert.Contains("no game.json", error);

        var (code, output) = Sage("new", "nope");
        Assert.Equal(2, code);
        foreach (var (name, template, _) in NewRequest.Templates) Assert.Contains(name, output);
    }

    [Fact]
    public void New_ModData_WithAGameFolder_NamesTheGamesIdAndWhereItIs()
    {
        string root = TestEnv.NewTempDir();
        string game = Game(root);
        var request = NewRequest.Parse(new[] { "mod-data", "-o", Path.Combine(root, "MyMod"), "--game", game }, out _)!;
        var args = request.Command().Arguments;
        Assert.Equal("mygame", args[args.ToList().IndexOf("--game") + 1]);
        Assert.Equal("../game", args[args.ToList().IndexOf("--gameDir") + 1]);

        // A bare id is only the mod's "game".
        args = NewRequest.Parse(new[] { "mod-data", "-o", "m", "--game", "village" }, out _)!.Command().Arguments;
        Assert.Equal("village", args[args.ToList().IndexOf("--game") + 1]);
        Assert.DoesNotContain("--gameDir", args);
    }

    [Fact]
    public void New_GameClient_GoesInTheGamesClientFolder_NamedAfterTheGame()
    {
        string game = Game(TestEnv.NewTempDir());
        var request = NewRequest.Parse(new[] { "game-client", "--game", game }, out _)!;
        Assert.Equal(Path.Combine(game, "Client"), request.OutputDirectory);
        Assert.Equal("game", request.Name);
        Assert.Equal("Client/bin/{config}/game.Client.dll", request.ClientModulePath());
        Assert.Equal("..", request.Command().Arguments.SkipWhile(a => a != "--gameDir").ElementAt(1));
    }

    // Every template `sage new` names is one the Sage.Templates package has, by its shortName, and the other way round.
    [Fact]
    public void New_KnowsEveryTemplateThePackageHas()
    {
        var packaged = Directory.GetDirectories(Templates)
            .Select(dir => (string)SchemaValidator.ReadData(Path.Combine(dir, ".template.config", "template.json"))!["shortName"]!)
            .OrderBy(n => n).ToList();
        Assert.Equal(packaged, NewRequest.Templates.Select(t => t.Template).OrderBy(n => n).ToList());
        Assert.All(NewRequest.Templates, t => Assert.Equal("sage-" + t.Short, t.Template));
    }

    // The issue's done criterion: `sage new mod-data` makes a mod that `sage validate --mods` accepts, with its records
    // and mod.json mapped onto schemas written for the game it is for.
    [Fact]
    public void New_ModData_WritesAModThatValidatesAgainstTheGame_WithItsSchemasMapped()
    {
        string hive = Hive("sage-mod-data");
        string root = TestEnv.NewTempDir();
        string game = Game(root);
        string mod = Path.Combine(root, "MyMod");

        var (code, output) = Sage("new", "mod-data", "-o", mod, "--game", game, "--debug:custom-hive", hive);
        Assert.True(code == 0, output);
        Assert.Contains("0 error(s)", output);

        var manifest = SchemaValidator.ReadData(Path.Combine(mod, "mod.json"))!;
        Assert.Equal("mymod", (string?)manifest["id"]);
        Assert.Equal("mygame", (string?)manifest["game"]);

        // The VS Code mapping names schemas that were written, and they describe the game's ids and the mod's.
        var settings = SchemaValidator.ReadData(Path.Combine(mod, ".vscode", "settings.json"))!;
        foreach (var mapping in settings["json.schemas"]!.AsArray())
            Assert.True(File.Exists(Path.Combine(mod, ((string)mapping!["url"]!)[2..])), (string?)mapping["url"]);
        string ids = File.ReadAllText(Path.Combine(mod, "schemas", RecordSchemas.Ids));
        Assert.Contains("mymod:falchion", ids);
        Assert.Contains("mygame:player", ids);
        var validator = new SchemaValidator(Path.Combine(mod, "schemas"));
        Assert.Empty(validator.Validate(manifest, RecordSchemas.Mod));

        // The tasks check against the game, where it is from the mod.
        string tasks = File.ReadAllText(Path.Combine(mod, ".vscode", "tasks.json"));
        Assert.Contains("${workspaceFolder}/../game", tasks);
        Assert.DoesNotContain("SAGE_GAME_FOLDER", tasks);

        // And `sage validate --mods` on its own accepts it.
        (code, output) = Sage("validate", game, "--mods", mod);
        Assert.True(code == 0, output);
    }

    [Fact]
    public void New_GameClient_MakesTheClientHalf_AndGameJsonLoadsIt_KeepingWhatWasWritten()
    {
        string hive = Hive("sage-game-client");
        string game = Game(TestEnv.NewTempDir(), """
            {
              // the manifest, as a person wrote it
              "name": "G", "id": "mygame",
              "mounts": ["content"]
            }
            """);

        var (code, output) = Sage("new", "game-client", "--game", game, "--sdkVersion", "1.2.3", "--debug:custom-hive", hive);
        Assert.True(code == 0, output);

        string project = File.ReadAllText(Path.Combine(game, "Client", "game.Client.csproj"));
        Assert.Contains("Sdk=\"Sage.Sdk/1.2.3\"", project);
        Assert.Contains("<SageGameDirectory>$(MSBuildProjectDirectory)/..</SageGameDirectory>", project);
        Assert.Contains("[Plugin(\"game.client\"", File.ReadAllText(Path.Combine(game, "Client", "gameClientModule.cs")));

        string text = File.ReadAllText(Path.Combine(game, "game.json"));
        Assert.Contains("// the manifest, as a person wrote it", text);
        Assert.Equal(new[] { "Client/bin/{config}/game.Client.dll" }, GameManifest.Load(game).Modules.Add);

        // Again: nothing to add.
        Assert.Equal(text, GameJsonModules.Add(text, "Client/bin/{config}/game.Client.dll"));
    }

    [Fact]
    public void New_DryRun_PrintsTheCommandAndWritesNothing()
    {
        string game = Game(TestEnv.NewTempDir());
        var (code, output) = Sage("new", "game-client", "--game", game, "--dry-run");
        Assert.Equal(0, code);
        Assert.Contains("new sage-game-client -o", output);
        Assert.False(Directory.Exists(Path.Combine(game, "Client")));
        Assert.DoesNotContain("modules", File.ReadAllText(Path.Combine(game, "game.json")));
    }

    // ---- game.json's modules.add --------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{ "id": "g" }""", """{ "id": "g",\n  "modules": { "disable": [], "add": ["X.dll"] } }""")]
    [InlineData("""{ "id": "g", "modules": { "disable": ["a"] } }""", """{ "id": "g", "modules": { "add": ["X.dll"], "disable": ["a"] } }""")]
    [InlineData("""{ "id": "g", "modules": {} }""", """{ "id": "g", "modules": { "add": ["X.dll"] } }""")]
    [InlineData("""{ "id": "g", "modules": { "add": [] } }""", """{ "id": "g", "modules": { "add": ["X.dll"] } }""")]
    [InlineData("""{ "id": "g", "modules": { "add": ["a.dll", /* b */ "b.dll",] } }""", """{ "id": "g", "modules": { "add": ["a.dll", /* b */ "b.dll", "X.dll",] } }""")]
    [InlineData("""{ "id": "g", "modules": { "add": ["X.dll"] } }""", """{ "id": "g", "modules": { "add": ["X.dll"] } }""")]
    public void GameJsonModules_AddsThePath_WhereverTheManifestHasRoomForIt(string before, string after)
    {
        string added = GameJsonModules.Add(before, "X.dll");
        Assert.Equal(after.Replace("\\n", "\n"), added);
        string folder = TestEnv.NewTempDir();
        File.WriteAllText(Path.Combine(folder, "game.json"), added);
        Assert.Contains("X.dll", GameManifest.Load(folder).Modules.Add);
    }

    [Fact]
    public void GameJsonModules_KeepsCommentsAndNestedObjects()
    {
        const string Before = """
            {
              // the game
              "id": "g",
              "modules": { /* none yet */ "disable": [] },
              "scene": "main" // last
            }
            """;
        string added = GameJsonModules.Add(Before, "Client/bin/{config}/G.Client.dll");
        Assert.Contains("// the game", added);
        Assert.Contains("/* none yet */", added);
        Assert.Contains("\"scene\": \"main\" // last", added);
        Assert.NotNull(SchemaValidator.ReadData(WriteTemp(added)));
    }

    private static string WriteTemp(string text)
    {
        string path = Path.Combine(TestEnv.NewTempDir(), "game.json");
        File.WriteAllText(path, text);
        return path;
    }

    // ---- sage run -------------------------------------------------------------------------------------------

    private static string? NoHost(string config) => null;

    [Fact]
    public void Run_ASageSdkGame_IsDotnetRunOnItsProject_WithTheHostArgumentsAfterTheDash()
    {
        string hello = Path.Combine(Repo, "games", "Hello");
        var plan = RunRequest.Parse(new[] { hello, "--config", "Development", "--", "+map", "e1m1", "-nomods" })!.Plan(NoHost, out string? error)!;
        Assert.Null(error);
        Assert.Equal(new[] { "run", "--project", Path.Combine(hello, "Hello.csproj"), "-c", "Development", "--", "+map", "e1m1", "-nomods" }, plan.Arguments);
    }

    [Fact]
    public void Run_AGameWithAClientFolder_RunsTheClientProject_WhichBuildsTheGamesOwn()
    {
        string game = Game(TestEnv.NewTempDir());
        File.WriteAllText(Path.Combine(game, "G.csproj"), """<Project Sdk="Sage.Sdk/1.0.0" />""");
        Directory.CreateDirectory(Path.Combine(game, "Client"));
        File.WriteAllText(Path.Combine(game, "Client", "G.Client.csproj"), """<Project Sdk="Sage.Sdk/1.0.0" />""");
        Assert.Equal(Path.Combine(game, "Client", "G.Client.csproj"), RunRequest.SdkProject(game));

        File.Delete(Path.Combine(game, "Client", "G.Client.csproj"));
        Assert.Equal(Path.Combine(game, "G.csproj"), RunRequest.SdkProject(game));
    }

    [Fact]
    public void Run_AFolderWithNoSdkProject_StartsTheHostOnIt()
    {
        string game = Game(TestEnv.NewTempDir());
        string host = TestEnv.NewTempDir();
        File.WriteAllText(Path.Combine(host, "Sage.Host.dll"), "binary");

        var plan = RunRequest.Parse(new[] { game, "--", "-edit", "level" })!.Plan(_ => host, out string? error)!;
        Assert.Null(error);
        Assert.Equal(new[] { Path.Combine(host, "Sage.Host.dll"), "-game", game, "-edit", "level" }, plan.Arguments);
        Assert.Equal(host, plan.WorkingDirectory);

        // The Sandbox's project is not the SDK's: the solution builds it, and the host runs it.
        Assert.Null(RunRequest.SdkProject(Path.Combine(Repo, "games", "Sandbox")));

        Assert.Null(RunRequest.Parse(new[] { game })!.Plan(NoHost, out error));
        Assert.Contains("no " + BuildInfo.ConfigurationName + " host", error);
    }

    [Fact]
    public void Run_WithoutAGameFolder_IsAnError()
    {
        Assert.Null(RunRequest.Parse(Array.Empty<string>()));
        Assert.Null(RunRequest.Parse(new[] { "a", "b" }));
        var (code, output) = Sage("run", TestEnv.NewTempDir(), "--dry-run");
        Assert.Equal(1, code);
        Assert.Contains("has no game.json", output);

        (code, output) = Sage("run", Path.Combine(Repo, "games", "Hello"), "--dry-run", "--", "+quit", "1");
        Assert.Equal(0, code);
        Assert.Contains("run --project", output);
        Assert.Contains("-- +quit 1", output);
    }
}
