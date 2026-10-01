#nullable enable
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Merge provenance and the content report (plan 4j-2): who wrote each value of a record, and where
// mods — mounts named `mods/<id>`, as phase 4j's boot names them — conflict (decision 5).
public class ContentReportTests
{
    public ContentReportTests() { _ = TestEnv.UserRoot; }

    private const string Sword = """[{ "type": "test_item", "id": "sword", "name": "Sword", "value": 10, "tags": ["weapon"], "stats": { "strength": 2, "agility": 1 } }]""";

    private static (RecordStore Store, ContentReport Report) Load(MountFixture fx)
    {
        var store = fx.Load();
        return (store, ContentReport.Build(store, fx.Vfs));
    }

    [Fact]
    public void TwoModsSetTheSameField_TheLaterWins_AndItIsAConflict()
    {
        var fx = new MountFixture();
        fx.Write("game", "data/items.json", Sword);
        fx.Write("mods/better_blades", "data/patch.json", """[{ "type": "test_item", "id": "sandbox:sword", "patch": true, "name": "Better sword" }]""");
        fx.Write("mods/rival_trade", "data/patch.json", """[{ "type": "test_item", "id": "sandbox:sword", "patch": true, "name": "Rival sword" }]""");
        fx.Mount("game", "sandbox");
        fx.Mount("mods/better_blades", "better_blades");
        fx.Mount("mods/rival_trade", "rival_trade");
        var (store, report) = Load(fx);

        Assert.Equal("Rival sword", store.Get<TestItem>(new RecordId("sandbox", "sword")).Name);
        var conflict = Assert.Single(report.Conflicts);
        Assert.Equal(ContentConflictKind.Value, conflict.Kind);
        Assert.Equal("test_item sandbox:sword name: better_blades, rival_trade; rival_trade won", conflict.Line);
        Assert.Contains("  " + conflict.Line, report.Lines());

        // Every writer of the field, in order, with the line that wrote it.
        var writes = store.Writes("test_item", new RecordId("sandbox", "sword")).Where(w => w.Path == "name").ToList();
        Assert.Equal(new[] { RecordWriteOp.Define, RecordWriteOp.Set, RecordWriteOp.Set }, writes.Select(w => w.Op));
        Assert.Equal(new[] { "game", "mods/better_blades", "mods/rival_trade" }, writes.Select(w => w.Mount!.Name));
        Assert.Equal("mods/rival_trade:data/patch.json:1:63", writes[2].At);
        string described = store.Describe("test_item", new RecordId("sandbox", "sword"));
        Assert.Contains("name <- mods/rival_trade:data/patch.json", described);
        Assert.Contains("set name  mods/better_blades:data/patch.json:1:63", described);
        Assert.Contains("define name  game:data/items.json:1:40", described);
    }

    [Fact]
    public void AModPatchingTheGameIsAnOverride_NotAConflict()
    {
        var fx = new MountFixture();
        fx.Write("game", "data/items.json", Sword);
        fx.Write("game", "data/patch.json", """[{ "type": "test_item", "id": "sword", "patch": true, "value": 11 }]""");
        fx.Write("mods/better_blades", "data/patch.json", """[{ "type": "test_item", "id": "sandbox:sword", "patch": true, "value": 12 }]""");
        fx.Mount("game", "sandbox");
        fx.Mount("mods/better_blades", "better_blades");
        var (_, report) = Load(fx);

        Assert.Empty(report.Conflicts);
        var mod = report.Mounts.Single(m => m.IsMod);
        Assert.Contains("  patched test_item sandbox:sword (overrides game): set value", report.Lines("better_blades"));
        Assert.Equal("game", mod.Patched.Single().DefinedBy!.Name);
    }

    [Fact]
    public void TwoModsAddingToOneList_IsNotAConflict()
    {
        var fx = new MountFixture();
        fx.Write("game", "data/items.json", Sword);
        fx.Write("mods/a", "data/patch.json", """[{ "type": "test_item", "id": "sandbox:sword", "patch": true, "tags+": ["sharp"] }]""");
        fx.Write("mods/b", "data/patch.json", """[{ "type": "test_item", "id": "sandbox:sword", "patch": true, "tags+": ["shiny"], "tags-": ["weapon"] }]""");
        fx.Mount("game", "sandbox");
        fx.Mount("mods/a", "a");
        fx.Mount("mods/b", "b");
        var (store, report) = Load(fx);

        Assert.Equal(new[] { "sharp", "shiny" }, store.Get<TestItem>(new RecordId("sandbox", "sword")).Tags);
        Assert.Empty(report.Conflicts);
        Assert.Equal(new[] { RecordWriteOp.Define, RecordWriteOp.Add, RecordWriteOp.Add, RecordWriteOp.Remove },
                     store.Writes("test_item", new RecordId("sandbox", "sword")).Where(w => w.Path == "tags").Select(w => w.Op));
    }

    [Fact]
    public void AModsDisableAgainstAnothersPatch_IsAConflict()
    {
        var fx = new MountFixture();
        fx.Write("game", "data/items.json", Sword);
        fx.Write("mods/a", "data/patch.json", """[{ "type": "test_item", "id": "sandbox:sword", "patch": true, "value": 50 }]""");
        fx.Write("mods/b", "data/patch.json", """[{ "type": "test_item", "id": "sandbox:sword", "patch": true, "disabled": true }]""");
        fx.Mount("game", "sandbox");
        fx.Mount("mods/a", "a");
        fx.Mount("mods/b", "b");
        var (store, report) = Load(fx);

        Assert.False(store.TryGet(new RecordId("sandbox", "sword"), out TestItem _));
        var conflict = Assert.Single(report.Conflicts);
        Assert.Equal(ContentConflictKind.Disabled, conflict.Kind);
        Assert.Equal("test_item sandbox:sword: disabled by b, patched by a; removed", conflict.Line);
        // A disabled record keeps its writes, and rec_get says who disabled it.
        Assert.Equal(RecordWriteOp.Disable, store.Writes("test_item", new RecordId("sandbox", "sword"))[^1].Op);
        Assert.StartsWith("test_item sandbox:sword: disabled by mods/b:data/patch.json:1:", store.Describe("test_item", new RecordId("sandbox", "sword")));
    }

    [Fact]
    public void ANestedPathIsReportedAtTheDepthThePatchNamed()
    {
        var fx = new MountFixture();
        fx.Write("game", "data/items.json", Sword);
        fx.Write("mods/a", "data/patch.json", """[{ "type": "test_item", "id": "sandbox:sword", "patch": true, "stats": { "agility": 4 } }]""");
        fx.Write("mods/b", "data/patch.json", """[{ "type": "test_item", "id": "sandbox:sword", "patch": true, "stats": { "agility": 6, "strength": 3 } }]""");
        fx.Mount("game", "sandbox");
        fx.Mount("mods/a", "a");
        fx.Mount("mods/b", "b");
        var (store, report) = Load(fx);

        var sword = store.Get<TestItem>(new RecordId("sandbox", "sword"));
        Assert.Equal((6, 3), (sword.Stats.Agility, sword.Stats.Strength));
        // stats.strength was only b's: one conflict, on agility.
        var conflict = Assert.Single(report.Conflicts);
        Assert.Equal("stats.agility", conflict.Path);
        Assert.Equal("test_item sandbox:sword stats.agility: a, b; b won", conflict.Line);
        Assert.Equal("mods/b:data/patch.json:1:74", store.Writes("test_item", new RecordId("sandbox", "sword")).Last(w => w.Path == "stats.agility").At);
    }

    [Fact]
    public void TwoModsShippingOneTexture_IsAConflict_AModShadowingTheGameIsNot()
    {
        var fx = new MountFixture();
        fx.Write("game", "textures/rock.png", "game");
        fx.Write("game", "textures/tree.png", "game");
        fx.Write("mods/a", "textures/rock.png", "a");
        fx.Write("mods/b", "textures/rock.png", "b");
        fx.Write("mods/b", "textures/tree.png", "b");
        // Record files and manifests are merged, not shadowed.
        fx.Write("mods/a", "mod.json", "{}");
        fx.Write("mods/b", "mod.json", "{}");
        fx.Write("mods/a", "data/x.json", "[]");
        fx.Write("mods/b", "data/x.json", "[]");
        fx.Mount("game", "sandbox");
        fx.Mount("mods/a", "a");
        fx.Mount("mods/b", "b");
        var (_, report) = Load(fx);

        var conflict = Assert.Single(report.Conflicts);
        Assert.Equal(ContentConflictKind.Asset, conflict.Kind);
        Assert.Equal("asset textures/rock.png: a, b; b won", conflict.Line);
        var b = report.Mounts.Single(m => m.Mount.Name == "mods/b");
        Assert.Equal(new[] { "textures/rock.png", "textures/tree.png" }, b.Shadows.Select(s => s.Path.Value));
        Assert.Contains("  shadows textures/rock.png in game, a", report.Lines("b"));
        Assert.Equal(new[] { "game", "mods/a" }, fx.Vfs.Shadows().Single(s => s.Path.Value == "textures/rock.png").Shadowed.Select(m => m.Name));
    }

    [Fact]
    public void TheReportListsAdditionsRedefinitionsAndSkippedPatches_ByMount()
    {
        var fx = new MountFixture();
        fx.Write("game", "data/items.json", Sword);
        fx.Write("mods/a", "data/items.json", """
            [{ "type": "test_item", "id": "falchion", "name": "Falchion" },
             { "type": "test_item", "id": "sandbox:sword", "value": 3 },
             { "type": "test_item", "id": "sandbox:bow", "patch": true, "value": 1 }]
            """);
        fx.Mount("game", "sandbox");
        fx.Mount("mods/a", "a");
        var (_, report) = Load(fx);

        var a = report.Mounts.Single(m => m.Mount.Name == "mods/a");
        Assert.Equal(new[] { "test_item a:falchion" }, a.Added);
        Assert.Equal(new[] { "test_item sandbox:sword at mods/a:data/items.json:2:2 (first defined at game:data/items.json:1:2)" }, a.Redefinitions);
        Assert.Equal(new[] { "test_item sandbox:bow at mods/a:data/items.json:3:2" }, a.SkippedPatches);
        var lines = report.Lines("a");
        Assert.Equal("No conflicts between mods.", lines[0]);
        Assert.Contains("a (mod): 1 added, 1 patched, 0 asset(s) shadowed, 1 redefinition(s), 1 skipped patch(es)", lines);
        Assert.Contains("  added test_item: a:falchion", lines);
    }

    // A field a record inherits is blamed on the base's file, and said to come through the base.
    [Fact]
    public void AnInheritedWriteSaysViaBase()
    {
        var fx = new MountFixture();
        fx.Write("game", "data/items.json", """
            [{ "type": "test_item", "id": "weapon_base", "abstract": true, "value": 5 },
             { "type": "test_item", "id": "sword", "base": "weapon_base", "name": "Sword" }]
            """);
        fx.Write("mods/a", "data/patch.json", """[{ "type": "test_item", "id": "sandbox:weapon_base", "patch": true, "value": 7 }]""");
        fx.Mount("game", "sandbox");
        fx.Mount("mods/a", "a");
        var (store, _) = Load(fx);

        Assert.Equal(7, store.Get<TestItem>(new RecordId("sandbox", "sword")).Value);
        var value = store.Writes("test_item", new RecordId("sandbox", "sword")).Last(w => w.Path == "value");
        Assert.Equal(new RecordId("sandbox", "weapon_base"), value.Via);
        Assert.Equal("mods/a", value.Mount!.Name);
        Assert.Contains("value <- mods/a:data/patch.json (via base sandbox:weapon_base)", store.Describe("test_item", new RecordId("sandbox", "sword")));
    }

    // With no mods the console still answers: what a game's mount patched in the engine's.
    [Fact]
    public void ModConflicts_RunsWithNoMods()
    {
        using var capture = new CaptureSink();
        using var app = HeadlessApp.Bare()
            .OnRegistered(a => a.Records.Register<TestItem>())
            .File("data/items.json", Sword.Replace("\"sword\"", "\"engine_sword\""))
            .File("data/patch.json", """[{ "type": "test_item", "id": "sage:engine_sword", "patch": true, "value": 2 }]""", ns: "mygame")
            .Boot();

        app.App.CVars.Execute("mod_conflicts");
        Log.Flush();
        var messages = capture.Entries.Select(e => e.Message).ToList();
        Assert.Contains("No conflicts between mods.", messages);
        Assert.Contains("  patched test_item sage:engine_sword (from engine): set value", messages);
    }
}

// Provenance costs every record a little at load (plan 4j-2, risks): the Sandbox's records still load
// within budget with it.
[Collection(MeasurementsCollection.Name)]
public class ContentReportMeasurementTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public ContentReportMeasurementTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _ = TestEnv.UserRoot;
        _output = output;
    }

    [Fact]
    public void TheSandboxsRecordsReloadWithinBudget()
    {
        using var app = HeadlessApp.ForGame(Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox"), new global::Sandbox.SandboxModule())
                                   .WithEngineContent().Build();
        Assert.Equal(0, app.Records.ErrorCount);
        app.Records.Reload();   // warm
        var times = new double[15];
        long bytes = 0;
        for (int i = 0; i < times.Length; i++)
        {
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            app.Records.Reload();
            times[i] = watch.Elapsed.TotalMilliseconds;
            bytes = System.GC.GetAllocatedBytesForCurrentThread() - before;
        }
        double median = times.OrderBy(t => t).ElementAt(times.Length / 2);
        _output.WriteLine($"Sandbox: {app.Records.Count} records reload in {median:F1} ms (median of {times.Length}), allocating {bytes / 1024} KB");
        Assert.True(median < 2000, $"the Sandbox's records took {median:F1} ms to reload");

        // The report is built when asked, not at load: what it costs on the Sandbox, which has no mods.
        var reportWatch = Stopwatch.StartNew();
        var report = ContentReport.Build(app.Records, app.Vfs);
        _output.WriteLine($"Sandbox: the content report took {reportWatch.Elapsed.TotalMilliseconds:F1} ms ({report.Lines().Count} lines)");
        Assert.Empty(report.Conflicts);
    }
}
