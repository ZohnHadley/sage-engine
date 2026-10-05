#nullable enable
using System.IO;
using Sage.Host;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The host's launch, short of opening a window (issue #298): its -options (HostOptions, compiled in from
// src/Sage.Host) and the game.json it is pointed at. Until these, the host's arguments were checked only
// by the smoke run and CI's registry dump happening to work.
public class HostLaunchTests
{
    private static HostOptions Options(params string[] args) => HostOptions.From(LaunchArgs.Parse(args));

    [Fact]
    public void TheHostReadsItsOptionsAndLeavesTheCommandsToTheConsole()
    {
        var options = Options("-game", "games/Sandbox", "+map", "e1m1", "--dump-registry", "out/registry.json", "-edit", "level.json");
        Assert.Equal("games/Sandbox", options.Game);   // GameManifest.Locate makes it a full path
        Assert.Equal(Path.GetFullPath("out/registry.json"), options.DumpRegistry);
        Assert.Equal("level.json", options.Edit);
        Assert.Null(options.Mods);                      // what is installed
        Assert.Empty(options.Warnings);

        var none = Options();
        Assert.Null(none.Game);
        Assert.Null(none.DumpRegistry);
        Assert.Null(none.Edit);
        Assert.Equal("", Options("-edit").Edit);        // the editor on the game's own scene
        Assert.Equal("games/x", Options("-GAME", "games/x").Game);   // options ignore case, as typed on Windows
    }

    [Fact]
    public void ModsAreNamedInOrder_OrNoneWithNomods_WhichWinsOverMods()
    {
        var named = Options("-mods", "b, a,,c");
        Assert.Equal(new[] { Path.GetFullPath("b"), Path.GetFullPath("a"), Path.GetFullPath("c") }, named.Mods);
        Assert.Empty(named.Warnings);

        var clean = Options("-nomods");
        Assert.NotNull(clean.Mods);
        Assert.Empty(clean.Mods!);
        Assert.Empty(clean.Warnings);

        var both = Options("-mods", "a", "-nomods");
        Assert.Empty(both.Mods!);
        Assert.Equal(new[] { "-nomods and -mods together: -nomods wins, no mods load" }, both.Warnings);
    }

    [Fact]
    public void AMistypedOptionIsAWarningNeverAStop()
    {
        var options = Options("-gmae", "games/x", "-mods", "-dump-registry", "-nosound");
        Assert.Null(options.Game);
        Assert.Null(options.Mods);
        Assert.Null(options.DumpRegistry);
        Assert.Equal(new[]
        {
            "Unknown launch option -gmae (ignored)",
            "Unknown launch option -nosound (ignored)",
            "-mods needs folders, -mods <dir>[,<dir>] (ignored)",
            "-dump-registry needs a file name (ignored)",
        }, options.Warnings);
    }

    // Each way a game folder can be wrong ends the host with a message naming the file and what to do
    // (Program.cs logs it and writes a crash report), not with a stack trace from somewhere later.
    [Fact]
    public void EachWayAGameFolderIsWrongSaysWhereAndWhat()
    {
        string exe = TestEnv.NewTempDir();   // outside the repository: no list of games to offer
        var nothing = Assert.Throws<FileNotFoundException>(() => GameManifest.Locate(null, exe));
        Assert.Contains("pass -game <folder>", nothing.Message);
        Assert.Contains(Path.GetFullPath(exe), nothing.Message);
        Assert.DoesNotContain("Games in", nothing.Message);

        string empty = TestEnv.NewTempDir();   // -game at a folder with no game.json
        var missing = Assert.Throws<FileNotFoundException>(() => GameManifest.Load(GameManifest.Locate(empty, exe)));
        Assert.Equal($"No game.json in {Path.GetFullPath(empty)} (pass -game <folder>).", missing.Message);

        string InvalidMessage(string json)
        {
            string dir = TestEnv.NewTempDir();
            File.WriteAllText(Path.Combine(dir, "game.json"), json);
            var ex = Assert.Throws<InvalidDataException>(() => GameManifest.Load(dir));
            Assert.StartsWith(Path.Combine(dir, "game.json"), ex.Message);
            return ex.Message;
        }
        Assert.Contains("\"id\" is required", InvalidMessage("""{ "name": "x" }"""));
        Assert.Contains("mount", InvalidMessage("""{ "id": "x", "mount": ["content"] }"""));       // a misspelt key
        Assert.Contains("disabled", InvalidMessage("""{ "id": "x", "modules": { "disabled": [] } }"""));
        Assert.Contains("\"version\"", InvalidMessage("""{ "id": "x", "version": "one" }"""));
        Assert.Contains("\"sage\"", InvalidMessage("""{ "id": "x", "sage": "^99.0" }"""));         // another engine's game

        string bad = TestEnv.NewTempDir();
        File.WriteAllText(Path.Combine(bad, "game.json"), """{ "id": "Bad Id" }""");
        Assert.Throws<FormatException>(() => GameManifest.Load(bad));   // the id is a record namespace
    }
}
