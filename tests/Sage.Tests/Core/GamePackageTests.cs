#nullable enable
using Hello;
using Sage.Cli;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// `sage package` (issue #293): a game and a Shipping host, into one folder a player runs. The host here is a
// stand-in folder with the files a host build has (the packager copies files; it never runs them); the games
// are a made-up one with every kind of path game.json can hold, and the repository's own Hello and
// scene-only games. CI packages a template game with the real Player and runs it from the folder.
public class GamePackageTests
{
    public GamePackageTests() { _ = TestEnv.UserRoot; }

    private static string EngineContent => Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content");

    // A host's build output as `dotnet build src/Sage.Host -c Shipping` leaves it, with the engine content,
    // and the `sage` CLI beside it as the Player package keeps it. `devTools` adds what a Development build has.
    private static string Host(bool devTools = false)
    {
        string host = TestEnv.NewTempDir();
        foreach (string file in new[] { "Sage.Host.dll", "Sage.Host", "Sage.Host.deps.json", "Sage.Host.runtimeconfig.json", "Sage.Core.dll",
                                        "Sage.Client.dll", "Sage.Kits.Rpg.dll", "MonoGame.Framework.dll", "runtimes/linux-x64/native/libSDL2-2.0.so.0",
                                        "sage.dll", "sage.deps.json", "sage.runtimeconfig.json" })
            Write(host, file, "binary");
        if (devTools)
            foreach (string file in new[] { "Sage.Editor.dll", "Sage.Editing.dll", "ImGui.NET.dll", "runtimes/linux-x64/native/libcimgui.so" })
                Write(host, file, "binary");
        CopyTree(EngineContent, Path.Combine(host, "Content"));
        return host;
    }

    // A game with both halves built in Shipping, a mount inside its folder and one beside it, its own mods,
    // and what a game folder holds that a player must not get: sources, a project, obj/ and tools/.
    private static string Game(string root)
    {
        string game = Path.Combine(root, "MyGame");
        Write(game, "game.json", """
            {
              // comments and the keys the packager does not touch are kept
              "name": "My Game",
              "id": "mygame",
              "assembly": "Simulation/bin/{config}/MyGame.dll",
              "mounts": ["content", "../shared/art"],
              "modsDirectory": "mods",
              "kits": ["sage.kits.rpg", "sage.kits.extra"],
              "scene": "main",
              "modules": { "disable": ["sage.audio"], "add": ["bin/{config}/MyGame.Client.dll"] },
            }
            """);
        Write(game, "Simulation/bin/Shipping/MyGame.dll", "sim");
        Write(game, "Simulation/bin/Shipping/MyGame.pdb", "sim symbols");
        Write(game, "Simulation/bin/Shipping/Sage.Kits.Extra.dll", "a kit the host does not have");
        Write(game, "Simulation/bin/Debug/MyGame.dll", "the wrong configuration");
        Write(game, "bin/Shipping/MyGame.Client.dll", "client");
        Write(game, "content/data/scene.json", "[]");
        Write(game, "content/textures/wall.png", "png");
        Write(root, "shared/art/textures/sky.png", "png");
        Write(game, "mods/hats/mod.json", "{}");
        Write(game, "MyGame.Client.csproj", "<Project />");
        Write(game, "MyGameClientModule.cs", "// source");
        Write(game, "obj/project.assets.json", "{}");
        Write(game, "tools/build_atlas.py", "# a dev tool");
        return game;
    }

    [Fact]
    public void APackageIsTheShippingHostWithTheGameBesideItAndNothingElse()
    {
        string host = Host(), root = TestEnv.NewTempDir(), game = Game(root), output = Path.Combine(root, "out");

        var result = GamePackage.Run(new PackageOptions { GameDirectory = game, OutputDirectory = output, HostDirectory = host });
        Assert.True(result.Ok, string.Join("\n", result.Errors));

        // The host as built, with its engine content, less the CLI.
        foreach (string file in new[] { "Sage.Host.dll", "Sage.Host", "Sage.Host.runtimeconfig.json", "Sage.Core.dll", "Sage.Client.dll",
                                        "runtimes/linux-x64/native/libSDL2-2.0.so.0", "Content/data/materials.json" })
            Assert.True(File.Exists(Path.Combine(output, file)), file);
        Assert.DoesNotContain(result.Files, f => f.StartsWith("sage.", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())   // the apphost runs, whatever the host folder's copy had lost
            Assert.True(File.GetUnixFileMode(Path.Combine(output, "Sage.Host")).HasFlag(UnixFileMode.OtherExecute));
        Assert.DoesNotContain(result.Files, f => GamePackage.IsDevTool(Path.GetFileName(f)));

        // The game: its two halves (in the configuration asked for) and a kit the host lacks in bin/, its
        // mounts and its mods; nothing of its sources, project, obj/ or tools/.
        Assert.Equal("sim", File.ReadAllText(Path.Combine(output, "game/bin/MyGame.dll")));
        Assert.True(File.Exists(Path.Combine(output, "game/bin/MyGame.pdb")));
        Assert.True(File.Exists(Path.Combine(output, "game/bin/MyGame.Client.dll")));
        Assert.True(File.Exists(Path.Combine(output, "game/bin/Sage.Kits.Extra.dll")));
        Assert.False(File.Exists(Path.Combine(output, "game/bin/Sage.Kits.Rpg.dll")));   // the host has it
        Assert.True(File.Exists(Path.Combine(output, "game/content/data/scene.json")));
        Assert.True(File.Exists(Path.Combine(output, "game/mounts/art/textures/sky.png")));
        Assert.True(File.Exists(Path.Combine(output, "game/mods/hats/mod.json")));
        var gameFiles = result.Files.Where(f => f.StartsWith("game/", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(gameFiles, f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".csproj", StringComparison.Ordinal)
                                              || f.Contains("/obj/") || f.Contains("/tools/") || f.Contains("Simulation/"));

        // The manifest names the copies, with no "{config}" left, and keeps everything else; the host loads it.
        var manifest = GameManifest.Load(Path.Combine(output, "game"));
        Assert.Equal("My Game", manifest.Name);
        Assert.Equal("bin/MyGame.dll", manifest.Assembly);
        Assert.Equal(new[] { "content", "mounts/art" }, manifest.Mounts);
        Assert.Equal(new[] { "bin/MyGame.Client.dll" }, manifest.Modules.Add);
        Assert.Equal(new[] { "sage.audio" }, manifest.Modules.Disable);
        Assert.Equal(new[] { "sage.kits.rpg", "sage.kits.extra" }, manifest.Kits);
        Assert.Equal("main", manifest.Scene);
        Assert.True(File.Exists(manifest.AssemblyPath));
        Assert.All(manifest.ModuleAssemblies, path => Assert.True(File.Exists(path), path));
        Assert.Equal(Path.Combine(output, "game"), GameManifest.Locate(null, output));   // run with no -game

        // Packaging again replaces the old package.
        File.WriteAllText(Path.Combine(output, "stale.txt"), "left from last time");
        Assert.True(GamePackage.Run(new PackageOptions { GameDirectory = game, OutputDirectory = output, HostDirectory = host }).Ok);
        Assert.False(File.Exists(Path.Combine(output, "stale.txt")));
    }

    [Fact]
    public void AHostWithTheEditorOrImGuiInItIsRefused()
    {
        string host = Host(devTools: true), root = TestEnv.NewTempDir(), game = Game(root), output = Path.Combine(root, "out");

        var result = GamePackage.Run(new PackageOptions { GameDirectory = game, OutputDirectory = output, HostDirectory = host });

        Assert.False(result.Ok);
        Assert.Contains("Shipping", result.Errors.Single());
        Assert.Contains("Sage.Editor.dll", result.Errors.Single());
        Assert.False(Directory.Exists(output));
    }

    // The editor for modders (issue #375): a Development host in editor/ beside the Shipping one, and the
    // launchers that open the same game in it. The player's host at the top stays free of the editor.
    [Fact]
    public void WithTheEditorAPackageHasADevelopmentHostInEditorAndLaunchersOnTheSameGame()
    {
        string host = Host(), editorHost = Host(devTools: true), root = TestEnv.NewTempDir(), game = Game(root), output = Path.Combine(root, "out");

        var result = GamePackage.Run(new PackageOptions { GameDirectory = game, OutputDirectory = output, HostDirectory = host, EditorHostDirectory = editorHost });
        Assert.True(result.Ok, string.Join("\n", result.Errors));

        // The player's host, as without the editor: nothing of the editor outside editor/.
        Assert.DoesNotContain(result.Files, f => !f.StartsWith(GamePackage.EditorFolder + "/", StringComparison.Ordinal) && GamePackage.IsDevTool(Path.GetFileName(f)));
        Assert.True(File.Exists(Path.Combine(output, "Sage.Host.dll")));
        Assert.Equal(Path.Combine(output, "game"), GameManifest.Locate(null, output));

        // The editor host, as built, less the CLI; one copy of the game, which the launchers name.
        foreach (string file in new[] { "Sage.Host.dll", "Sage.Host", "Sage.Editor.dll", "Sage.Editing.dll", "ImGui.NET.dll", "Content/data/materials.json" })
            Assert.True(File.Exists(Path.Combine(output, "editor", file)), file);
        Assert.False(File.Exists(Path.Combine(output, "editor", "sage.dll")));
        Assert.False(Directory.Exists(Path.Combine(output, "editor", "game")));
        string sh = File.ReadAllText(Path.Combine(output, "edit.sh")), cmd = File.ReadAllText(Path.Combine(output, "edit.cmd"));
        Assert.StartsWith("#!/bin/sh", sh);
        Assert.Contains("\"$here/editor/Sage.Host\" -game \"$here/game\" -edit \"$@\"", sh);
        Assert.Contains("\"%~dp0editor\\Sage.Host.exe\" -game \"%~dp0game\" -edit %*", cmd);
        Assert.Contains("edit.sh", result.Files);
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Path.Combine(output, "edit.sh")).HasFlag(UnixFileMode.OtherExecute));
            Assert.True(File.GetUnixFileMode(Path.Combine(output, "editor", "Sage.Host")).HasFlag(UnixFileMode.OtherExecute));
        }
    }

    [Fact]
    public void AnEditorHostWithoutTheEditorOrTheGamesOwnHostIsRefused()
    {
        string host = Host(), root = TestEnv.NewTempDir(), game = Game(root), output = Path.Combine(root, "out");

        var shipping = GamePackage.Run(new PackageOptions { GameDirectory = game, OutputDirectory = output, HostDirectory = host, EditorHostDirectory = Host() });
        Assert.Contains("Sage.Editor.dll", shipping.Errors.Single());
        Assert.False(Directory.Exists(output));

        var missing = GamePackage.Run(new PackageOptions { GameDirectory = game, OutputDirectory = output, HostDirectory = host,
                                                           EditorHostDirectory = Path.Combine(root, "nowhere") });
        Assert.Contains("No editor host", missing.Errors.Single());
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void AGameNotBuiltInTheConfigurationOrAnOutputFolderInTheWayIsAnErrorThatWritesNothing()
    {
        string host = Host(), root = TestEnv.NewTempDir(), game = Game(root);

        var unbuilt = GamePackage.Run(new PackageOptions { GameDirectory = game, OutputDirectory = Path.Combine(root, "out"), HostDirectory = host,
                                                           Configuration = "Development" });
        Assert.False(unbuilt.Ok);
        Assert.Contains(unbuilt.Errors, e => e.Contains("MyGame.dll") && e.Contains("dotnet build -c Development"));
        Assert.False(Directory.Exists(Path.Combine(root, "out")));

        // A folder with something else in it is not deleted to make room; nor is the game's own content.
        Write(root, "documents/letter.txt", "mine");
        var occupied = GamePackage.Run(new PackageOptions { GameDirectory = game, OutputDirectory = Path.Combine(root, "documents"), HostDirectory = host });
        Assert.Contains("not empty", occupied.Errors.Single());
        Assert.True(File.Exists(Path.Combine(root, "documents/letter.txt")));
        var overlapping = GamePackage.Run(new PackageOptions { GameDirectory = game, OutputDirectory = Path.Combine(game, "content", "out"), HostDirectory = host });
        Assert.Contains("overlaps", overlapping.Errors.Single());
    }

    // The repository's own games, packaged and validated as `sage package` does: Hello (an assembly, built in
    // the tests' configuration) and the scene-only game (no C#, a scene and a player from data).
    [Fact]
    public void ThePackagedHelloAndSceneOnlyGamesLoadAndValidateFromTheOutputFolder()
    {
        string games = TestEnv.FolderAbove("Sage.sln");
        foreach (string folder in new[] { "games/Hello", "tests/games/scene-only" })
        {
            string host = Host(), output = Path.Combine(TestEnv.NewTempDir(), "out");
            var result = GamePackage.Run(new PackageOptions
            {
                GameDirectory = Path.Combine(games, folder), OutputDirectory = output, HostDirectory = host, Configuration = BuildInfo.ConfigurationName,
            });
            Assert.True(result.Ok, string.Join("\n", result.Errors));
            Assert.DoesNotContain(result.Files, f => f.EndsWith(".csproj", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal));

            var manifest = GameManifest.Load(Path.Combine(output, "game"));
            if (folder.EndsWith("Hello", StringComparison.Ordinal))
                Assert.Equal(Path.Combine(output, "game", "bin", "Hello.dll"), manifest.AssemblyPath);
            var report = ContentValidation.Run(new ValidateOptions
            {
                GameDirectory = Path.Combine(output, "game"),
                EngineContentDirectory = Path.Combine(output, "Content"),
                AvailablePlugins = BasePlugins.All(),
                // Hello is loaded in this process already (the tests reference it): the same module, not a second copy.
                GameModule = folder.EndsWith("Hello", StringComparison.Ordinal) ? new HelloModule() : null,
            });
            Assert.True(report.Ok, string.Join("\n", report.Errors));
            Assert.True(report.Records > 0);
        }
    }

    // The package's mounts are cooked (issue #302): a .sgmesh beside each .glb and a .sgtex beside each image,
    // in the package and never in the game's own folder, and listed with the package's files; the loose files
    // stay beside them. --no-cook (Cook = false) leaves them loose; a file that will not cook is a warning.
    [Fact]
    public void APackagesModelsAndTexturesAreCookedBesideTheLooseFilesUnlessCookIsOff()
    {
        string host = Host(), root = TestEnv.NewTempDir(), game = Game(root), output = Path.Combine(root, "out");
        string sandbox = Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox", "content");
        File.Copy(Path.Combine(sandbox, "models", "bunny.glb"), Path.Combine(game, "content", "bunny.glb"));
        File.Copy(Path.Combine(sandbox, "textures", "hut_wall.png"), Path.Combine(root, "shared", "art", "textures", "wall.png"));

        var result = GamePackage.Run(new PackageOptions { GameDirectory = game, OutputDirectory = output, HostDirectory = host });
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        Assert.Contains("game/content/bunny.glb.sgmesh", result.Files);
        Assert.Contains("game/mounts/art/textures/wall.png.sgtex", result.Files);
        Assert.True(File.Exists(Path.Combine(output, "game", "content", "bunny.glb")));   // the loose file stays
        Assert.Equal(2, result.Cook!.Cooked.Count);
        // The made-up game's "png" files are not images: warned about, left loose, not an error.
        Assert.Contains(result.Cook.Warnings, w => w.Contains("wall.png", StringComparison.Ordinal) && w.Contains("content", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(game, "*.sg*", SearchOption.AllDirectories));

        var vfs = new VirtualFileSystem();
        vfs.Mount(new FolderMount("content", Path.Combine(output, "game", "content"), "mygame"));
        Assert.NotNull(CookedAssets.LoadMesh(vfs, VirtualPath.Parse("bunny.glb")));

        var loose = GamePackage.Run(new PackageOptions { GameDirectory = game, OutputDirectory = output, HostDirectory = host, Cook = false });
        Assert.True(loose.Ok, string.Join("\n", loose.Errors));
        Assert.Null(loose.Cook);
        Assert.Empty(Directory.EnumerateFiles(output, "*.sg*", SearchOption.AllDirectories));
    }

    private static void Write(string folder, string relative, string text)
    {
        string path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void CopyTree(string source, string target)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
    }
}
