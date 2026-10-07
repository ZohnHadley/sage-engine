#nullable enable
using System;
using System.IO;
using System.Linq;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Namespaced mod assets (issue #398): a bare asset path in a mod's records that only that mod ships is the
// mod's own (`mod:path`), so two mods shipping one path each see theirs and do not conflict; a mod's file at
// a path the game has replaces it (an override, as before); and `@ns/path` replaces namespace ns's file on
// purpose, a mod's own as well as the game's.
public class ModAssetTests
{
    public ModAssetTests() { _ = TestEnv.UserRoot; }

    private static string Charm(string icon) => $$"""[{ "type": "test_charm", "id": "charm", "icon": "{{icon}}" }]""";

    private static (RecordStore Store, ContentReport Report) Load(MountFixture fx)
    {
        var store = new RecordStore { MissingAssetsAreErrors = true };
        store.Register<TestItem>();
        store.Register<TestSpell>();
        store.Register<TestCharm>();
        store.Load(fx.Vfs);
        Assert.Equal(0, store.ErrorCount);
        return (store, ContentReport.Build(store, fx.Vfs));
    }

    private static string Icon(RecordStore store, string ns) => store.Get<TestCharm>(new RecordId(ns, "charm")).Icon.ToString();

    private static string Read(VirtualFileSystem vfs, string path)
    {
        using var reader = new StreamReader(vfs.Open(VirtualPath.Parse(path)));
        return reader.ReadToEnd();
    }

    [Xunit.Theory]
    [Xunit.InlineData("a:textures/x.png", "a", "textures/x.png")]
    [Xunit.InlineData("Better_Blades:Textures\\Falchion.PNG", "better_blades", "textures/falchion.png")]
    [Xunit.InlineData("textures/x.png", "", "textures/x.png")]
    public void AVirtualPathMayNameANamespace(string text, string ns, string local)
    {
        var path = VirtualPath.Parse(text);
        Assert.Equal(ns, path.Namespace);
        Assert.Equal(local, path.Local.Value);
        Assert.Equal(ns.Length > 0, path.IsNamespaced);
    }

    [Xunit.Theory]
    [Xunit.InlineData("a:b:c.png")]
    [Xunit.InlineData(":x.png")]
    [Xunit.InlineData("a:")]
    [Xunit.InlineData("a b:x.png")]
    [Xunit.InlineData("a:../x.png")]
    public void ANamespaceIsAnIdBeforeTheOnlyColon(string text) =>
        Assert.Throws<ArgumentException>(() => VirtualPath.Parse(text));

    // The issue's acceptance: both mods ship textures/falchion.png; each one's record gets its own file,
    // whichever loads later, and the report has no conflict and nothing shadowed.
    [Xunit.Fact]
    public void TwoModsShippingOnePath_EachSeeTheirOwn_AndItIsNoConflict()
    {
        var fx = new MountFixture();
        fx.Write("mods/better_blades", "textures/falchion.png", "better");
        fx.Write("mods/better_blades", "data/charm.json", Charm("textures/falchion.png"));
        fx.Write("mods/rival_trade", "textures/falchion.png", "rival");
        fx.Write("mods/rival_trade", "data/charm.json", Charm("textures/falchion.png"));
        fx.Mount("game", "village");
        fx.Mount("mods/better_blades", "better_blades");
        fx.Mount("mods/rival_trade", "rival_trade");
        var (store, report) = Load(fx);

        Assert.Equal("better_blades:textures/falchion.png", Icon(store, "better_blades"));
        Assert.Equal("rival_trade:textures/falchion.png", Icon(store, "rival_trade"));
        Assert.Equal("better", Read(fx.Vfs, Icon(store, "better_blades")));
        Assert.Equal("rival", Read(fx.Vfs, Icon(store, "rival_trade")));
        Assert.Equal(fx.Dir("mods/better_blades"), Path.GetDirectoryName(Path.GetDirectoryName(
            fx.Vfs.Which(VirtualPath.Parse(Icon(store, "better_blades")))!.PhysicalPath(VirtualPath.Parse(Icon(store, "better_blades"))))));
        Assert.Empty(report.Conflicts);
        Assert.All(report.Mounts, m => Assert.Empty(m.Shadows));
        // A bare lookup from elsewhere still finds a file, the later mod's, as before.
        Assert.Equal("rival", Read(fx.Vfs, "textures/falchion.png"));
    }

    // A mod's file at a path the game has replaces the game's for every record (an override, reported as
    // one); its records keep the bare path. Two mods doing that are still a conflict.
    [Xunit.Fact]
    public void AModReplacingTheGamesFile_IsAnOverride_AndTwoAreAConflict()
    {
        var fx = new MountFixture();
        fx.Write("game", "textures/sign.png", "game");
        fx.Write("game", "data/charm.json", Charm("textures/sign.png"));
        fx.Write("mods/a", "textures/sign.png", "a");
        fx.Write("mods/a", "data/charm.json", Charm("textures/sign.png"));
        fx.Mount("game", "village");
        fx.Mount("mods/a", "a");
        var (store, report) = Load(fx);

        Assert.Equal("textures/sign.png", Icon(store, "village"));
        Assert.Equal("textures/sign.png", Icon(store, "a"));
        Assert.Equal("a", Read(fx.Vfs, "textures/sign.png"));
        Assert.Empty(report.Conflicts);
        Assert.Contains("  overrides textures/sign.png in game", report.Lines("a"));

        fx.Write("mods/b", "textures/sign.png", "b");
        fx.Mount("mods/b", "b");
        var (_, both) = Load(fx);
        Assert.Equal("asset textures/sign.png: a, b; b won", Assert.Single(both.Conflicts).Line);
    }

    // `@ns/path` replaces ns's file on purpose: another mod's own asset (which a file at the bare path would
    // not touch any more), or the game's. Each is an override, not a conflict; two mods replacing one
    // mod's asset are a conflict.
    [Xunit.Fact]
    public void AnAtNamespaceFolderReplacesThatNamespacesFileOnPurpose()
    {
        var fx = new MountFixture();
        fx.Write("game", "textures/sign.png", "game");
        fx.Write("game", "data/charm.json", Charm("textures/sign.png"));
        fx.Write("mods/better_blades", "textures/falchion.png", "better");
        fx.Write("mods/better_blades", "data/charm.json", Charm("textures/falchion.png"));
        fx.Write("mods/rival_trade", "@better_blades/textures/falchion.png", "reskin");
        fx.Write("mods/rival_trade", "@village/textures/sign.png", "rival sign");
        fx.Mount("game", "village");
        fx.Mount("mods/better_blades", "better_blades");
        fx.Mount("mods/rival_trade", "rival_trade");
        var (store, report) = Load(fx);

        Assert.Equal("better_blades:textures/falchion.png", Icon(store, "better_blades"));
        Assert.Equal("reskin", Read(fx.Vfs, Icon(store, "better_blades")));
        Assert.Equal("mods/rival_trade", fx.Vfs.Which(VirtualPath.Parse(Icon(store, "better_blades")))!.Name);
        Assert.Equal("rival sign", Read(fx.Vfs, "textures/sign.png"));
        Assert.Equal("rival sign", Read(fx.Vfs, "village:textures/sign.png"));
        Assert.Empty(report.Conflicts);
        var lines = report.Lines("rival_trade");
        Assert.Contains("  overrides better_blades:textures/falchion.png in better_blades", lines);
        Assert.Contains("  overrides textures/sign.png in game", lines);

        fx.Write("mods/third", "@better_blades/textures/falchion.png", "third");
        fx.Mount("mods/third", "third");
        var (again, three) = Load(fx);
        Assert.Equal("third", Read(fx.Vfs, Icon(again, "better_blades")));
        Assert.Equal("asset better_blades:textures/falchion.png: rival_trade, third; third won", Assert.Single(three.Conflicts).Line);
    }

    // A mod's record naming another mod's own asset in full gets that one, and a path the mod does not ship
    // stays bare, to be found wherever it is.
    [Xunit.Fact]
    public void ANamespacedPathNamesThatModsFile_AndAPathTheModLacksStaysBare()
    {
        var fx = new MountFixture();
        fx.Write("game", "textures/rock.png", "game rock");
        fx.Write("mods/better_blades", "textures/falchion.png", "better");
        fx.Write("mods/rival_trade", "textures/falchion.png", "rival");
        fx.Write("mods/rival_trade", "data/charm.json",
            """[{ "type": "test_charm", "id": "charm", "icon": "better_blades:textures/falchion.png" }, { "type": "test_charm", "id": "rock", "icon": "textures/rock.png" }]""");
        fx.Mount("game", "village");
        fx.Mount("mods/better_blades", "better_blades");
        fx.Mount("mods/rival_trade", "rival_trade");
        var (store, _) = Load(fx);

        Assert.Equal("better", Read(fx.Vfs, Icon(store, "rival_trade")));
        Assert.Equal("textures/rock.png", store.Get<TestCharm>(new RecordId("rival_trade", "rock")).Icon.ToString());
    }
}
