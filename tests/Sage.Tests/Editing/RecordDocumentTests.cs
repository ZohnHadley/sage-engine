#nullable enable
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The record browser's model (issue #224, docs/design/15 §10): a record opened as JSON, edited with
// commands, saved into the file it came from, or as a patch in the game's folder when the file belongs to
// a mount the game does not own, and read back through the record loader a game would use.
public class RecordDocumentTests
{
    public RecordDocumentTests() { _ = TestEnv.UserRoot; }

    private const string GameFile = """
        // the game's things
        [
          // a post, kept as a person wrote it
          { "type": "prefab", "id": "post", "name": "post",   // keep this comment
            "components": { "timer": { "interval": 2 }, "transform": {} }, "tags": ["solid", "wooden"] },
          { "type": "prefab", "id": "lamp", "name": "lamp" }
        ]
        """;

    private const string KitFile = """
        [ { "type": "prefab", "id": "crate", "name": "crate", "components": { "timer": { "interval": 9 } }, "tags": ["box"] } ]
        """;

    private static readonly RecordId Post = new("sandbox", "post");
    private static readonly RecordId Crate = new("kit", "crate");

    private static (Engine Engine, MountFixture Fixture) NewEngine()
    {
        var fixture = new MountFixture();
        fixture.Write("kit", "data/crate.json", KitFile);
        fixture.Write("game", "data/things.json", GameFile);
        fixture.Mount("kit", "kit");        // read-only to the game: another mount's content
        fixture.Mount("game", "sandbox");
        var engine = HeadlessApp.Bare().With(new PhysicsModule()).Mount(fixture).Build().Engine;
        return (engine, fixture);
    }

    private static string GamePath(MountFixture fixture, string relative) => Path.Combine(fixture.Dir("game"), relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void ARecordIsOpenedAsItsJsonAndANestedFieldIsSetAndUndone()
    {
        var (engine, _) = NewEngine();
        using (engine)
        {
            var record = RecordDocument.Open(engine, "prefab", Post)!;
            Assert.Equal("post", (string?)record.Get("name"));
            Assert.Equal(2, (int?)record.Get("components.timer.interval"));
            Assert.Equal("wooden", (string?)record.Get("tags[1]"));
            Assert.False(record.Dirty);

            Assert.True(record.Set("components.timer.interval", JsonValue.Create(5)));
            Assert.Equal(5, (int?)record.Get("components.timer.interval"));
            Assert.True(record.Dirty);
            Assert.Equal(2, (int?)record.Original["components"]!["timer"]!["interval"]);   // the original is left alone

            Assert.True(record.Undo());
            Assert.Equal(2, (int?)record.Get("components.timer.interval"));
            Assert.False(record.Dirty);
            Assert.True(record.Redo());
            Assert.Equal(5, (int?)record.Get("components.timer.interval"));
            Assert.True(record.Dirty);
        }
    }

    [Fact]
    public void AFieldTheRecordDidNotHaveIsRemovedAgainByUndoAndTheObjectsItMadeWithIt()
    {
        var (engine, _) = NewEngine();
        using (engine)
        {
            var record = RecordDocument.Open(engine, "prefab", new RecordId("sandbox", "lamp"))!;
            Assert.True(record.Set("parts.glow.radius", JsonValue.Create(3)));
            Assert.True(record.Set("tags[0]", JsonValue.Create("lit")));   // a list it makes, with its first element
            Assert.Equal(3, (int?)record.Get("parts.glow.radius"));

            record.Undo();
            record.Undo();
            Assert.False(record.Working.ContainsKey("parts"));
            Assert.True(JsonNode.DeepEquals(record.Original, record.Working));
            Assert.False(record.Dirty);
        }
    }

    [Fact]
    public void ADragOfOneFieldIsOneEditAndAnUnreachablePathChangesNothing()
    {
        var (engine, _) = NewEngine();
        using (engine)
        {
            var record = RecordDocument.Open(engine, "prefab", Post)!;
            for (int i = 3; i <= 10; i++) record.Set("components.timer.interval", JsonValue.Create(i));
            Assert.Single(record.History.Entries);
            record.Undo();
            Assert.Equal(2, (int?)record.Get("components.timer.interval"));

            Assert.False(record.Set("name.first", JsonValue.Create("x")));   // "name" is a string, not an object
            Assert.False(record.Set("tags[7]", JsonValue.Create("x")));       // further out than one past the end
            Assert.False(record.Set("", JsonValue.Create("x")));
            Assert.Single(record.History.Entries);
        }
    }

    [Theory]
    [InlineData("120", "120")]
    [InlineData("true", "true")]
    [InlineData("\"Bob\"", "\"Bob\"")]
    [InlineData("Bob", "\"Bob\"")]
    [InlineData("[1,2,3]", "[1,2,3]")]
    [InlineData("1.5e", "\"1.5e\"")]
    [InlineData("[a, b, true]", "[\"a\",\"b\",true]")]
    [InlineData("{hp: 3}", "{\"hp\":3}")]
    public void AConsoleValueIsAJsonLiteralOrElseAString(string text, string expected) =>
        Assert.Equal(expected, RecordDocument.ParseValue(text)!.ToJsonString());

    [Fact]
    public void TheConsoleOpensSetsUndoesAndSavesARecord()
    {
        var (engine, fixture) = NewEngine();
        using (engine)
        {
            var editor = new RecordEditor(engine);
            editor.Register(engine.CVars);
            var cvars = engine.CVars;

            Assert.True(cvars.Execute("ed_rec_open prefab post"));      // a bare id is found in any namespace
            Assert.Equal(Post, editor.Current!.Id);
            cvars.Execute("ed_rec_set name \"Bob\"");
            cvars.Execute("ed_rec_set tags [\"a\", \"b\"]");
            Assert.Equal("Bob", (string?)editor.Current.Get("name"));
            Assert.Equal(2, editor.Current.Get("tags")!.AsArray().Count);

            cvars.Execute("ed_rec_undo");
            Assert.Equal("solid", (string?)editor.Current.Get("tags[0]"));
            cvars.Execute("ed_rec_redo");
            Assert.Equal("a", (string?)editor.Current.Get("tags[0]"));

            cvars.Execute("ed_rec_save");
            Assert.False(editor.Current.Dirty);
            Assert.Equal("Bob", engine.Records.Get<PrefabRecord>(Post).Name);
            Assert.Contains("\"Bob\"", File.ReadAllText(GamePath(fixture, "data/things.json")));

            using (var log = new CaptureSink())
            {
                cvars.Execute("ed_rec_get name");
                Assert.Contains(log.Entries.Select(e => e.Message), m => m.Contains("\"Bob\"") && m.Contains("things.json"));
            }
            cvars.Execute("ed_rec_close");
            Assert.Null(editor.Current);
        }
    }

    [Fact]
    public void ASaveGoesIntoTheFileTheRecordCameFromAndKeepsItsComments()
    {
        var (engine, fixture) = NewEngine();
        using (engine)
        {
            var record = RecordDocument.Open(engine, "prefab", Post)!;
            Assert.False(record.SavesAsPatch);
            record.Set("components.timer.interval", JsonValue.Create(7));
            record.Set("name", JsonValue.Create("signpost"));
            Assert.True(record.Save());

            string text = File.ReadAllText(GamePath(fixture, "data/things.json"));
            Assert.Contains("// the game's things", text);
            Assert.Contains("// a post, kept as a person wrote it", text);
            Assert.Contains("// keep this comment", text);
            Assert.Contains("\"id\": \"lamp\"", text);                           // the record beside it, untouched
            Assert.Contains("\"signpost\"", text);
            Assert.False(Directory.Exists(GamePath(fixture, "data/patches")));   // no patch file for the game's own record
            Assert.False(record.Dirty);
            Assert.Equal(record.SavedTo, GamePath(fixture, "data/things.json"));

            // The game sees it: the records were reloaded, and what is open is what they say now.
            Assert.Equal("signpost", engine.Records.Get<PrefabRecord>(Post).Name);
            Assert.Equal(7, (int?)engine.Records.RawJson("prefab", Post)!["components"]!["timer"]!["interval"]);
            Assert.True(JsonNode.DeepEquals(record.Original, record.Working));
        }
    }

    [Fact]
    public void ARecordFromAnotherMountIsSavedAsAPatchInTheGamesFolderAndTheMergedRecordShowsIt()
    {
        var (engine, fixture) = NewEngine();
        using (engine)
        {
            var record = RecordDocument.Open(engine, "prefab", Crate)!;
            Assert.True(record.SavesAsPatch);
            Assert.Contains("crate.json", record.Provenance("name"));

            record.Set("name", JsonValue.Create("big crate"));
            record.Set("components.timer.interval", JsonValue.Create(3));
            record.Set("tags", JsonNode.Parse("[\"box\", \"heavy\"]"));
            using (var log = new CaptureSink())
            {
                Assert.True(record.Save());
                Assert.Contains(log.Entries.Select(e => e.Message), m => m.Contains("does not own") && m.Contains("patch"));
            }

            // The kit's file is as it was; the change is a patch of one record, naming only what changed.
            Assert.Equal(KitFile, File.ReadAllText(Path.Combine(fixture.Dir("kit"), "data", "crate.json")));
            string patchFile = GamePath(fixture, "data/patches/prefab_kit_crate.json");
            Assert.Equal(patchFile, record.SavedTo);
            var patch = JsonNode.Parse(File.ReadAllText(patchFile), documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!;
            var written = (patch is JsonArray array ? array[0] : patch)!.AsObject();
            Assert.Equal("kit:crate", (string?)written["id"]);
            Assert.True((bool?)written["patch"]);
            Assert.Equal("big crate", (string?)written["name"]);
            Assert.Equal(3, (int?)written["components"]!["timer"]!["interval"]);
            Assert.Null(written["components"]!["transform"]);

            // After the reload the merged record has the new values, and says where each came from.
            var crate = engine.Records.Get<PrefabRecord>(Crate);
            Assert.Equal("big crate", crate.Name);
            Assert.Equal(new[] { "box", "heavy" }, crate.Tags);
            Assert.Equal(3, (int?)engine.Records.RawJson("prefab", Crate)!["components"]!["timer"]!["interval"]);
            Assert.Contains("prefab_kit_crate.json", record.Provenance("name"));
            Assert.Contains("prefab_kit_crate.json", record.Provenance("tags[0]"));   // the patch wrote the list whole
            Assert.Equal(2, record.Sources().Count);
            Assert.False(record.Dirty);

            // A second save adds to that patch rather than starting another.
            record.Set("name", JsonValue.Create("huge crate"));
            Assert.True(record.Save());
            Assert.Single(Directory.GetFiles(GamePath(fixture, "data/patches")));
            Assert.Equal("huge crate", engine.Records.Get<PrefabRecord>(Crate).Name);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(patchFile), "\"patch\""));
        }
    }

    [Fact]
    public void AFormKnowsAFieldsDeclarationAndWhereItCameFrom()
    {
        var (engine, _) = NewEngine();
        using (engine)
        {
            var record = RecordDocument.Open(engine, "prefab", Post)!;
            Assert.Equal(ValueKind.List, record.MetaAt("tags")!.Kind);
            Assert.Equal(ValueKind.String, record.MetaAt("tags[0]")!.Kind);
            Assert.Equal(ValueKind.Bool, record.MetaAt("persist")!.Kind);
            Assert.Null(record.MetaAt("nonsense"));
            Assert.Contains("things.json", record.Provenance("components.timer.interval"));
            Assert.Null(record.Provenance("persist"));

            Assert.Equal("components.timer", RecordDocument.ChildPath("components", "timer"));
            Assert.Equal("components['a.b']", RecordDocument.ChildPath("components", "a.b"));
            Assert.False(record.IsEdited("name"));
            record.Set("name", JsonValue.Create("changed"));
            Assert.True(record.IsEdited("name"));
            Assert.False(record.IsEdited("tags"));

            var editor = new RecordEditor(engine);
            Assert.Contains("prefab", editor.Types());
            Assert.Equal(new[] { Post }, editor.Ids("prefab", "pos"));
            Assert.False(editor.Open("prefab", new RecordId("sandbox", "missing")));
        }
    }
}
