#nullable enable
using System;
using System.IO;
using System.Linq;
using Sage.Kits.Rpg;
using Xunit;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Mods at boot (phase 4j, issue 4j-3): found in the game's mods folder and the player's, ordered by
// ModLoadOrder and the player's list, mounted after the game as `mods/<id>` in the namespace `<id>`; a broken
// one is refused and the game still boots; the console writes the player's list for the next start.
public class ModBootTests
{
    public ModBootTests() { _ = TestEnv.UserRoot; }

    // A data-only game with one prefab, `modtest:rock`, named "rock".
    private static string NewGame()
    {
        string dir = TestEnv.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "game.json"), """{ "name": "Mod test", "id": "modtest", "mounts": ["content"], "version": "1.0.0" }""");
        Write(Path.Combine(dir, "content"), "data/rock.json", """[ { "type": "prefab", "id": "rock", "name": "rock" } ]""");
        return dir;
    }

    private static void Write(string folder, string relative, string text)
    {
        string file = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    // A mod in `parent/<folder>` that defines `<id>:thing` and renames the game's rock to `rockName`.
    private static string Mod(string parent, string id, string rockName = "", string extra = "", string folder = "")
    {
        string dir = Path.Combine(parent, folder.Length > 0 ? folder : id);
        Write(dir, "mod.json", $$"""{ "id": "{{id}}", "version": "1.0.0", "game": "modtest"{{extra}} }""");
        string patch = rockName.Length > 0 ? $$""", { "type": "prefab", "id": "modtest:rock", "patch": true, "name": "{{rockName}}" }""" : "";
        Write(dir, "data/things.json", $$"""[ { "type": "prefab", "id": "thing", "name": "{{id}} thing" }{{patch}} ]""");
        return dir;
    }

    private static string RockName(HeadlessApp app) => app.Records.Get<PrefabRecord>(new RecordId("modtest", "rock")).Name;

    private static string[] Active(HeadlessApp app) => app.Engine.Mods.Active.Select(m => m.Id).ToArray();

    [Fact]
    public void ModsAreFoundInTheGamesFolderAndThePlayersAndMountedAfterTheGame()
    {
        string game = NewGame();
        string user = TestEnv.NewTempDir();
        Mod(Path.Combine(game, "mods"), "alpha");
        Mod(user, "beta");

        using var app = HeadlessApp.ForGame(game).WithUserMods(user).Boot();

        Assert.Equal(new[] { "alpha", "beta" }, Active(app));
        Assert.Same(app.Engine.Mods, app.Engine.ModManager.Loaded);
        var mounts = app.Vfs.Mounts.Select(m => m.Name).ToList();
        Assert.Equal(new[] { "mods/alpha", "mods/beta" }, mounts.TakeLast(2));
        Assert.True(mounts.IndexOf("modtest/content") < mounts.IndexOf("mods/alpha"), "a mod mounts after the game");
        Assert.Equal("beta", app.Vfs.Mounts.Single(m => m.Name == "mods/beta").RecordNamespace);
        Assert.True(app.Records.TryGet(new RecordId("alpha", "thing"), out PrefabRecord _));
        Assert.True(app.Records.TryGet(new RecordId("beta", "thing"), out PrefabRecord _));
    }

    [Fact]
    public void TheSameIdInBothFoldersIsAnErrorAndThePlayersCopyIsUsed()
    {
        string game = NewGame();
        string user = TestEnv.NewTempDir();
        Mod(Path.Combine(game, "mods"), "twin", rockName: "game's twin");
        Mod(user, "twin", rockName: "player's twin");

        using var log = new CaptureSink();
        using var app = HeadlessApp.ForGame(game).WithUserMods(user).Boot();

        Assert.Equal(new[] { "twin" }, Active(app));
        Assert.Equal("player's twin", RockName(app));
        var refused = Assert.Single(app.Engine.Mods.Refused);
        Assert.Equal(Path.GetFullPath(Path.Combine(game, "mods", "twin")), refused.Folder);
        Assert.Contains("whose copy is used", refused.Reason);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Category == LogCat.Mods && e.Message.Contains("is in two places"));
    }

    [Fact]
    public void ThePlayersListOrdersTheModsAndSwitchesThemOff()
    {
        string game = NewGame();
        string mods = Path.Combine(game, "mods");
        Mod(mods, "aaa", rockName: "aaa's rock");
        Mod(mods, "bbb", rockName: "bbb's rock");
        Mod(mods, "ccc", rockName: "ccc's rock");
        string list = Path.Combine(TestEnv.NewTempDir(), "mods.json");
        File.WriteAllText(list, """{ "order": ["ccc", "aaa"], "disabled": ["bbb"] }""");

        using var app = HeadlessApp.ForGame(game).WithUserMods(null, list).Boot();

        Assert.Equal(new[] { "ccc", "aaa" }, Active(app));
        Assert.Equal("aaa's rock", RockName(app));   // the last mod wins
        Assert.Equal("bbb", Assert.Single(app.Engine.Mods.Disabled).Id);
        Assert.DoesNotContain(app.Vfs.Mounts, m => m.Name == "mods/bbb");
    }

    [Fact]
    public void ARefusedModIsNotMountedAndTheGameStillBoots()
    {
        string game = NewGame();
        string mods = Path.Combine(game, "mods");
        Mod(mods, "good", rockName: "good rock");
        Mod(mods, "coder", extra: """, "kind": "code" """);
        Mod(mods, "needy", extra: """, "dependencies": { "absent": "^1.0" } """);
        Write(Mod(mods, "elsewhere"), "mod.json", """{ "id": "elsewhere", "game": "another_game" }""");
        Write(Path.Combine(mods, "broken"), "mod.json", """{ "id": "broken", "colour": "red" }""");
        Mod(mods, "rpg");   // a kit's content namespace

        using var log = new CaptureSink();
        using var app = HeadlessApp.ForGame(game).With(new RpgKitModule()).Boot();

        Assert.Equal(new[] { "good" }, Active(app));
        Assert.Equal("good rock", RockName(app));
        var reasons = app.Engine.Mods.Refused.ToDictionary(r => r.Id, r => r.Reason);
        Assert.Equal(new[] { "broken", "coder", "elsewhere", "needy", "rpg" }, reasons.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Contains("can't be read", reasons["broken"]);
        Assert.Contains("code mods are phase 9", reasons["coder"]);
        Assert.Contains("another_game", reasons["elsewhere"]);
        Assert.Contains("'absent'", reasons["needy"]);
        Assert.Contains("a kit's content", reasons["rpg"]);
        Assert.Equal(new[] { "mods/good" }, app.Vfs.Mounts.Select(m => m.Name).Where(n => n.StartsWith("mods/", StringComparison.Ordinal)));
        Assert.Contains(log.Entries, e => e.Category == LogCat.Mods && e.Message.StartsWith("Mods: 1 active (good 1.0.0), 5 refused", StringComparison.Ordinal));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warn && e.Message.StartsWith("Mod 'coder' is not loaded: code mods are phase 9", StringComparison.Ordinal));
    }

    [Fact]
    public void NamedModsReplaceDiscoveryAndNoneMeansNone()
    {
        string game = NewGame();
        Mod(Path.Combine(game, "mods"), "found");
        string elsewhere = TestEnv.NewTempDir();
        string second = Mod(elsewhere, "second", rockName: "second's rock");
        string first = Mod(elsewhere, "first", rockName: "first's rock");

        using (var app = HeadlessApp.ForGame(game).WithMods(second, first).Boot())
        {
            Assert.Equal(new[] { "second", "first" }, Active(app));   // the order given, not the ids'
            Assert.Equal("first's rock", RockName(app));
            Assert.True(app.Engine.ModManager.Named);
        }
        using (var app = HeadlessApp.ForGame(game).WithMods().Boot())   // -nomods
        {
            Assert.Empty(Active(app));
            Assert.Equal("rock", RockName(app));
        }
    }

    [Fact]
    public void EditingAModsRecordHotReloadsIt()
    {
        string game = NewGame();
        string mod = Mod(Path.Combine(game, "mods"), "live", rockName: "first edit");

        using var app = HeadlessApp.ForGame(game).Boot();
        Assert.Equal("first edit", RockName(app));

        Write(mod, "data/things.json", """[ { "type": "prefab", "id": "modtest:rock", "patch": true, "name": "second edit" } ]""");
        app.Records.Reload();   // what RecordHotReload does when a watched data/ folder changes

        Assert.Equal("second edit", RockName(app));
        Assert.False(app.Records.TryGet(new RecordId("live", "thing"), out PrefabRecord _));
        Assert.IsType<FolderMount>(app.Vfs.Mounts.Single(m => m.Name == "mods/live"));   // the folders RecordHotReload watches
    }

    [Fact]
    public void TheConsoleWritesThePlayersListForTheNextStartAndChangesNothingNow()
    {
        string game = NewGame();
        string mods = Path.Combine(game, "mods");
        Mod(mods, "aaa", rockName: "aaa's rock");
        Mod(mods, "bbb", rockName: "bbb's rock");
        Mod(mods, "ccc", rockName: "ccc's rock");
        string list = Path.Combine(TestEnv.NewTempDir(), "mods.json");

        using (var log = new CaptureSink())
        using (var app = HeadlessApp.ForGame(game).WithUserMods(null, list).Boot())
        {
            Assert.Equal(new[] { "aaa", "bbb", "ccc" }, Active(app));
            app.CVars.Execute("mod_disable bbb");
            app.CVars.Execute("mod_move ccc 1");
            Assert.Contains(log.Entries, e => e.Message.Contains("applies at next start"));

            // Nothing changes until the next start: the VFS has no unmount.
            Assert.Equal(new[] { "aaa", "bbb", "ccc" }, Active(app));
            Assert.Equal("ccc's rock", RockName(app));
            Assert.True(app.Engine.ModManager.RestartNeeded);
            Assert.Equal(new[] { "ccc", "aaa" }, app.Engine.ModManager.Next().Active.Select(m => m.Id));

            Assert.False(app.CVars.Execute("mod_enable nosuch"));   // an error that names the mods there are
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("no mod 'nosuch' was found (mods: aaa, bbb, ccc)"));
        }

        var saved = ModList.Load(list, out string? warning);
        Assert.Null(warning);
        Assert.Equal(new[] { "ccc", "aaa", "bbb" }, saved.Order);
        Assert.Equal(new[] { "bbb" }, saved.Disabled);

        using (var app = HeadlessApp.ForGame(game).WithUserMods(null, list).Boot())
        {
            Assert.Equal(new[] { "ccc", "aaa" }, Active(app));
            Assert.Equal("aaa's rock", RockName(app));
            app.CVars.Execute("mod_enable bbb");
        }
        using (var app = HeadlessApp.ForGame(game).WithUserMods(null, list).Boot())
            Assert.Equal(new[] { "ccc", "aaa", "bbb" }, Active(app));
    }

    [Fact]
    public void TheBootWritesAModReport()
    {
        string game = NewGame();
        string mods = Path.Combine(game, "mods");
        Mod(mods, "kept", rockName: "kept's rock");
        Mod(mods, "rival", rockName: "rival's rock");
        Mod(mods, "coder", extra: """, "kind": "code" """);
        string report = Path.Combine(TestEnv.NewTempDir(), "logs", "mod_report.txt");

        using var app = HeadlessApp.ForGame(game).WithModReport(report).Boot();

        string text = File.ReadAllText(report);
        Assert.Contains("Mods: 2 active (kept 1.0.0, rival 1.0.0), 1 refused", text);
        Assert.Contains("  1. kept 1.0.0", text);
        Assert.Contains("coder: code mods are phase 9", text);
        // 4j-2's content report: the two mods' clash over the rock's name, and each mod's override of the game.
        Assert.Contains("prefab modtest:rock name: kept, rival; rival won", text);
        Assert.Contains("patched prefab modtest:rock (overrides modtest", text);
        Assert.Contains("added prefab: rival:thing", text);
    }

    [Fact]
    public void AChangedModJsonSaysRestartToApply()
    {
        string game = NewGame();
        string mod = Mod(Path.Combine(game, "mods"), "watched");
        using var app = HeadlessApp.ForGame(game).Boot();
        using var log = new CaptureSink();
        using var watch = app.Engine.ModManager.WatchManifests();

        File.WriteAllText(Path.Combine(mod, "mod.json"), """{ "id": "watched", "version": "1.1.0", "game": "modtest" }""");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !log.Entries.Any(Said))
            System.Threading.Thread.Sleep(50);
        Assert.Contains(log.Entries, Said);
        Assert.Equal("1.0.0", Assert.Single(app.Engine.Mods.Active).Version);   // still the one mounted

        bool Said(LogEntry e) => e.Category == LogCat.Mods && e.Message.Contains(Path.Combine(mod, "mod.json")) && e.Message.Contains("restart to apply");
    }
}
