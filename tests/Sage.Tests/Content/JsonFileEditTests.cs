#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// File-preserving write-back (issue #218, docs/design/15-editor.md): an edit changes the text of the
// value it is about and nothing else, so what a person wrote in a record file — comments, key order,
// the records beside the one being saved — survives the editor saving into it.
public class JsonFileEditTests
{
    private static readonly RecordId Yard = new("sandbox", "yard");

    // Written with `\n` whatever a checkout does to this file's line endings.
    private static readonly string Placements = PlacementsSource.ReplaceLineEndings("\n");

    private const string PlacementsSource = """
        // The yard: what stands around the house.
        [
          {
            "type": "prefab", "id": "post",   // a record beside the document
            "components": { "transform": {} }
          },
          {
            "type": "placements",
            "id": "yard",
            /* Absolute metres. */
            "place": [
              { "prefab": "post", "at": [10, 0, -4], "name": "corner" },   // by the gate
              { "prefab": "post", "at": [12, 0, -4], "yaw": 90, "name": "middle" },
              {
                "prefab": "post",
                "at": [14, 0, -4],
                "name": "end",
              },
            ],
          },
        ]
        """;

    private static JsonFileEdit Edit(string? text = null) => new((text ?? Placements).ReplaceLineEndings("\n"), new RecordStore().Json) { Namespace = "sandbox" };

    // The lines of `after` that are not the lines of `before`, position by position (both have as many).
    private static List<string> ChangedLines(string before, string after)
    {
        string[] a = before.Split('\n'), b = after.Split('\n');
        Assert.Equal(a.Length, b.Length);
        return Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]).Select(i => b[i]).ToList();
    }

    [Fact]
    public void AFileNothingChangedInIsWrittenBackByteForByte()
    {
        var edit = Edit();
        // The record as the file holds it, set back: every value compares equal, so nothing is written.
        edit.SetRecord("placements", Yard, edit.ReadRecord("placements", Yard)!);
        edit.Set("placements", Yard, "place[1].yaw", 90.0);

        Assert.False(edit.Changed);
        Assert.Equal(Placements, edit.Text);
    }

    [Fact]
    public void MovingOnePlacementChangesOneLine()
    {
        var edit = Edit();
        edit.Set("placements", Yard, "place[1].at", new Vector3(12.5f, 0, -4));

        var changed = Assert.Single(ChangedLines(Placements, edit.Text));
        Assert.Contains("\"at\": [12.5, 0, -4], \"yaw\": 90", changed);
    }

    [Fact]
    public void AVectorWrittenOverManyLinesChangesOnlyTheNumberThatMoved()
    {
        const string text = """
            [{
              "type": "placements", "id": "yard",
              "place": [ { "prefab": "post", "at": [
                10,
                0,
                -4
              ] } ]
            }]
            """;
        var edit = Edit(text);
        edit.Set("placements", Yard, "place[0].at", new Vector3(10, 2, -4));

        Assert.Equal(["    2,"], ChangedLines(text.ReplaceLineEndings("\n"), edit.Text));
    }

    [Fact]
    public void CommentsAndTheOtherRecordsStay()
    {
        var edit = Edit();
        edit.Set("placements", Yard, "place[2].name", "far end");
        edit.Remove("placements", Yard, "place[1]");
        edit.AddRecord(new JsonObject { ["type"] = "prefab", ["id"] = "lamp", ["components"] = new JsonObject() });

        foreach (string comment in new[] { "// The yard: what stands around the house.", "// a record beside the document", "/* Absolute metres. */", "// by the gate" })
            Assert.Contains(comment, edit.Text);
        Assert.Contains("\"type\": \"prefab\", \"id\": \"post\",   // a record beside the document\n    \"components\": { \"transform\": {} }", edit.Text);
        Assert.DoesNotContain("middle", edit.Text);

        // And it is still a file the record store reads, holding what the edits say.
        var reread = Edit(edit.Text);
        Assert.Equal("far end", (string?)reread.Read("placements", Yard, "place[1].name"));
        Assert.True(reread.Contains("prefab", new RecordId("sandbox", "post")));
        Assert.True(reread.Contains("prefab", new RecordId("sandbox", "lamp")));
        using var parsed = JsonDocument.Parse(edit.Text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        Assert.Equal(3, parsed.RootElement.GetArrayLength());
    }

    [Fact]
    public void ValuesAreSetAndRemovedAtAPathInsideARecord()
    {
        var edit = Edit();
        Assert.True(edit.Set("placements", Yard, "place[0].overrides.components.health.max", 50));
        Assert.True(edit.Set("placements", Yard, "place[3]", new JsonObject { ["prefab"] = "post", ["at"] = new JsonArray(16, 0, -4) }));
        Assert.True(edit.Remove("placements", Yard, "place[1].yaw"));
        Assert.False(edit.Remove("placements", Yard, "place[1].yaw"));
        Assert.False(edit.Set("placements", Yard, "place[9].at", Vector3.Zero));   // past the end of the list

        var reread = Edit(edit.Text);
        Assert.Equal(50, (int)reread.Read("placements", Yard, "place[0].overrides.components.health.max")!);
        Assert.Equal("[16,0,-4]", reread.Read("placements", Yard, "place[3].at")!.ToJsonString());
        Assert.Null(reread.Read("placements", Yard, "place[1].yaw"));
        Assert.Contains("\"at\": [16, 0, -4]", edit.Text);   // a vector on one line
    }

    [Fact]
    public void NewValuesAreWrittenInTheRecordStoresDialect()
    {
        var edit = Edit();
        edit.Set("placements", Yard, "place[2].frame", new { RelativeTo = System.DayOfWeek.Monday, Origin = new Vector3(1, 2, 3), Name = (string?)null });

        // Camel case, an enum by name, a vector on one line, a null left out, indented like its neighbours.
        Assert.Contains("\"name\": \"end\",\n        \"frame\": {\n          \"relativeTo\": \"Monday\",\n          \"origin\": [1, 2, 3]\n        },\n      },", edit.Text);
    }

    [Fact]
    public void RecordsAreReplacedAddedAndRemovedInPlace()
    {
        var edit = Edit();
        var record = edit.ReadRecord("placements", Yard)!.AsObject();
        record["place"]!.AsArray().RemoveAt(2);
        record["place"]![0]!["name"] = "gate";
        edit.SetRecord("placements", Yard, record);

        Assert.DoesNotContain("\"end\"", edit.Text);
        Assert.Contains("\"name\": \"gate\" },   // by the gate", edit.Text);

        Assert.True(edit.RemoveRecord("prefab", new RecordId("sandbox", "post")));
        Assert.False(edit.Contains("prefab", new RecordId("sandbox", "post")));
        Assert.True(edit.Contains("placements", Yard));
        Assert.StartsWith("// The yard: what stands around the house.\n[\n  {\n    \"type\": \"placements\"", edit.Text);

        // A file holding one record becomes an array when a second is added; an empty one gets a first.
        var single = Edit("{ \"type\": \"placements\", \"id\": \"yard\" }\n");
        single.AddRecord(new JsonObject { ["type"] = "placements", ["id"] = "lane" });
        Assert.True(Edit(single.Text).Contains("placements", new RecordId("sandbox", "lane")));
        Assert.True(Edit(single.Text).Contains("placements", Yard));

        var empty = Edit("");
        empty.SetRecord("placements", Yard, new JsonObject { ["type"] = "placements", ["id"] = "yard", ["place"] = new JsonArray() });
        Assert.Equal("[\n  {\n    \"type\": \"placements\",\n    \"id\": \"yard\",\n    \"place\": []\n  }\n]\n", empty.Text);
    }

    [Fact]
    public void APatchWritesOnlyWhatChangedBetweenTwoVersions()
    {
        // The program's record spells out fields the file leaves at their defaults (`yaw`, `id`): they
        // are not written unless they changed.
        var before = new JsonObject
        {
            ["type"] = "placements", ["id"] = "yard",
            ["place"] = new JsonArray(
                new JsonObject { ["prefab"] = "post", ["at"] = new JsonArray(10, 0, -4), ["yaw"] = 0, ["name"] = "corner", ["id"] = "" },
                new JsonObject { ["prefab"] = "post", ["at"] = new JsonArray(12, 0, -4), ["yaw"] = 90, ["name"] = "middle", ["id"] = "" },
                new JsonObject { ["prefab"] = "post", ["at"] = new JsonArray(14, 0, -4), ["yaw"] = 0, ["name"] = "end", ["id"] = "" }),
        };
        var after = before.DeepClone().AsObject();
        after["place"]![0]!["yaw"] = 45;

        var edit = Edit();
        Assert.True(edit.PatchRecord("placements", Yard, before, before.DeepClone()));
        Assert.False(edit.Changed);

        Assert.True(edit.PatchRecord("placements", Yard, before, after));
        Assert.Equal(["      { \"prefab\": \"post\", \"at\": [10, 0, -4], \"name\": \"corner\", \"yaw\": 45 },   // by the gate"], ChangedLines(Placements, edit.Text));
    }

    [Fact]
    public void ABareIdMeansTheFilesNamespace()
    {
        var edit = Edit();
        Assert.False(edit.Contains("placements", new RecordId("other", "yard")));
        Assert.True(Edit("[{ \"type\": \"placements\", \"id\": \"sandbox:yard\" }]").Contains("placements", Yard));
        Assert.False(edit.Contains("prefab", Yard));   // the type is part of what finds it
    }

    [Fact]
    public void AFileThatIsNotJsonIsRefused()
    {
        Assert.ThrowsAny<JsonException>(() => Edit("[{ \"type\": \"placements\", \"id\": \"yard\" "));
        Assert.ThrowsAny<JsonException>(() => Edit("[{ \"type\": tru }]"));
    }

    [Fact]
    public void SavingKeepsTheFilesByteOrderMarkAndLineEndings()
    {
        string path = Path.Combine(TestEnv.NewTempDir(), "yard.json");
        try
        {
            byte[] bom = [0xEF, 0xBB, 0xBF];
            string text = "[\r\n  { \"type\": \"placements\", \"id\": \"yard\", \"place\": [] }\r\n]\r\n";
            File.WriteAllBytes(path, [.. bom, .. System.Text.Encoding.UTF8.GetBytes(text)]);

            var edit = JsonFileEdit.Open(path, new RecordStore().Json);
            edit.Save(path);
            Assert.Equal([.. bom, .. System.Text.Encoding.UTF8.GetBytes(text)], File.ReadAllBytes(path));

            edit.Set("placements", Yard, "place[0]", new JsonObject { ["prefab"] = "post" });
            Assert.Contains("\"place\": [\r\n", edit.Text);
            Assert.DoesNotContain("\n", edit.Text.Replace("\r\n", ""));
        }
        finally { File.Delete(path); }
    }
}
