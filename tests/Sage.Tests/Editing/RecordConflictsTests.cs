#nullable enable
using System.IO;
using System.Linq;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The records tab's per-field conflict view (issue #401): a record two mods both patched opens with each
// conflicted field — keyed where a list is — every write to it in load order with the value it wrote,
// read back from its file, and the mod that won. Headless, through Sage.Editing's RecordEditor, the model
// the Records panel draws.
public class RecordConflictsTests
{
    public RecordConflictsTests() { _ = TestEnv.UserRoot; }

    private static readonly RecordId Trader = new("village", "trader");

    private static void Write(string folder, string relative, string text)
    {
        string file = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    // A village whose trader sells lanterns and bread, and mods that each patch him with `fields`.
    private static string Village(params (string Id, string Fields)[] mods)
    {
        string dir = TestEnv.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "game.json"),
            """{ "name": "Village", "id": "village", "version": "1.0.0", "mounts": ["content"], "plugins": ["sage.gameplay.items"] }""");
        Write(Path.Combine(dir, "content"), "data/village.json", """
            [
              { "type": "item", "id": "lantern", "label": "lantern" },
              { "type": "item", "id": "bread", "label": "bread", "maxStack": 10 },
              { "type": "prefab", "id": "trader", "name": "trader",
                "parts": { "inventory": { "items": [ { "item": "lantern", "count": 2 }, { "item": "bread", "count": 5 } ] } } }
            ]
            """);
        foreach (var (id, fields) in mods)
        {
            string mod = Path.Combine(dir, "mods", id);
            Write(mod, "mod.json", $$"""{ "id": "{{id}}", "version": "1.0.0", "game": "village" }""");
            Write(mod, "data/trader.json", $$"""
                [
                  { "type": "prefab", "id": "village:trader", "patch": true,
                    {{fields}} }
                ]
                """);
        }
        return dir;
    }

    [Fact]
    public void OpeningAConflictedRecord_ShowsEachModsValueForEachField_AndTheWinner()
    {
        string game = Village(
            ("alpha", """ "name": "Hilde", "parts": { "inventory": { "items": [ { "item": "village:lantern", "count": 4 } ] } } """),
            ("beta", """ "name": "Rurik", "parts": { "inventory": { "items": [ { "item": "village:lantern", "count": 9 }, { "item": "village:bread", "$remove": true } ] } } """));
        using var app = HeadlessApp.ForGame(game).Boot();
        Assert.Equal(new[] { "alpha", "beta" }, app.Engine.Mods.Active.Select(m => m.Id));
        Assert.Equal(0, app.Engine.Records.ErrorCount);

        var editor = new RecordEditor(app.Engine);
        Assert.True(editor.Open("prefab", Trader));
        var conflicts = editor.Conflicts;
        Assert.Equal(new[] { "name", "parts.inventory.items[village:lantern].count" }, conflicts.Select(c => c.Path).OrderBy(p => p));

        // A top-level field: the game's definition, then each mod's value, the later mod winning.
        var name = conflicts.Single(c => c.Path == "name");
        Assert.Equal("beta", name.Winner);
        Assert.Equal("\"Rurik\"", name.Current);
        Assert.Equal(new[] { ("village/content", RecordWriteOp.Define, "\"trader\"", false), ("alpha", RecordWriteOp.Set, "\"Hilde\"", false), ("beta", RecordWriteOp.Set, "\"Rurik\"", true) },
                     name.Contributions.Select(c => (c.Mount, c.Op, c.Value, c.Wins)));
        Assert.False(name.Contributions[0].IsMod);
        Assert.True(name.Contributions[1].IsMod);
        Assert.Contains("mods/alpha:data/trader.json:", name.Contributions[1].At);

        // A keyed list entry's field (#399): each value found by key in its own file — the game's bare
        // "lantern", the mods' "village:lantern" — whatever else the list holds.
        var count = conflicts.Single(c => c.Path == "parts.inventory.items[village:lantern].count");
        Assert.Equal("beta", count.Winner);
        Assert.Equal("9", count.Current);
        Assert.Equal(new[] { ("village/content", "2"), ("alpha", "4"), ("beta", "9") }, count.Contributions.Select(c => (c.Mount, c.Value)));
        Assert.Equal("parts", count.Contributions[0].Path);   // the definition wrote the whole of `parts`
        Assert.True(count.Contributions[^1].Wins);

        // The console prints the same; bread, which only beta touched, is not a conflict.
        using var log = new CaptureSink();
        editor.Register(app.Engine.CVars);
        app.Engine.CVars.Execute("ed_rec_conflicts");
        string text = string.Join('\n', log.Entries.Select(e => e.Message));
        Assert.Contains("2 field(s) mods conflict over", text);
        Assert.Contains("parts.inventory.items[village:lantern].count: beta won, now 9", text);
        Assert.Contains("* beta: set parts.inventory.items[village:lantern].count = 9", text);
        Assert.DoesNotContain("bread", text);
    }

    [Fact]
    public void TwoModsEditingDifferentKeys_AreNoConflict_AndNothingOpenHasNone()
    {
        string game = Village(
            ("alpha", """ "parts": { "inventory": { "items": [ { "item": "village:lantern", "count": 4 } ] } } """),
            ("beta", """ "parts": { "inventory": { "items": [ { "item": "village:bread", "count": 1 } ] } } """));
        using var app = HeadlessApp.ForGame(game).Boot();
        Assert.Equal(0, app.Engine.Records.ErrorCount);
        var editor = new RecordEditor(app.Engine);
        Assert.True(editor.Open("prefab", Trader));
        Assert.Empty(editor.Conflicts);   // different keys of one list: merged, not a conflict
        editor.Close();
        Assert.Empty(editor.Conflicts);   // nothing open

        // Through the content report directly: the same answer, for a record with no mods at all.
        Assert.Empty(RecordConflicts.Find(app.Engine, "item", new RecordId("village", "lantern")));
    }

    [Fact]
    public void AModDisablingARecordAnotherPatched_ShowsBothWrites()
    {
        string game = Village(
            ("alpha", """ "name": "Hilde" """),
            ("beta", """ "disabled": true """));
        using var app = HeadlessApp.ForGame(game).Boot();
        var conflicts = RecordConflicts.Find(app.Engine, "prefab", Trader);
        var conflict = Assert.Single(conflicts);
        Assert.Equal(ContentConflictKind.Disabled, conflict.Kind);
        Assert.Equal("", conflict.Path);
        Assert.Equal("beta", conflict.Winner);
        Assert.Equal(new[] { ("alpha", RecordWriteOp.Set, "\"Hilde\""), ("beta", RecordWriteOp.Disable, "(disabled the record)") },
                     conflict.Contributions.Select(c => (c.Mount, c.Op, c.Value)));
    }
}
