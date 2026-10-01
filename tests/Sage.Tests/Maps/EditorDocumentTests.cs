#nullable enable
using System.IO;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The editor's document (docs/design/15 §3, TODO F28).
//
// An editor's one irreplaceable job is that what it saves is what the game will load. That is a claim
// about files, so it is checked against files: write a document, move something, save, and read the file
// back through the record loader that a game would use.
public class EditorDocumentTests
{
    public EditorDocumentTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
        [
          { "type": "prefab", "id": "post", "components": { "transform": {} } },
          {
            "type": "placements",
            "id": "yard",
            "place": [ { "prefab": "post", "at": [10, 0, -4], "name": "corner" } ]
          }
        ]
        """;

    private static (Engine Engine, MountFixture Fixture) NewEngine()
    {
        var fixture = new MountFixture();
        fixture.Write("game", "data/yard.json", Records);
        fixture.Mount("game", "sandbox");

        var engine = HeadlessApp.Bare().With(new PhysicsModule()).Mount(fixture).Build().Engine;
        return (engine, fixture);
    }

    [Fact]
    public void SavingWritesWhereTheRecordCameFromRatherThanSomewhereNew()
    {
        var (engine, fixture) = NewEngine();
        using (engine)
        {
            var world = engine.CreateWorld("edit");
            var document = new EditorDocument(engine);

            Assert.True(document.Open(world, new RecordId("sandbox", "yard")));
            Assert.True(document.Save(world));

            // The record is defined in `yard.json` here, but a document may be defined in a file named
            // after something else entirely — and writing it to a file of its own name would leave two
            // definitions of one id, which is the single thing the record loader cannot sort out.
            Assert.EndsWith("yard.json", document.Path);
            Assert.False(File.Exists(Path.Combine(fixture.Dir("game"), "data", "placements.json")));
        }
    }

    [Fact]
    public void WhatIsSavedIsWhatAGameWillLoad()
    {
        var (engine, fixture) = NewEngine();
        string file;
        using (engine)
        {
            var world = engine.CreateWorld("edit");
            var document = new EditorDocument(engine);
            document.Open(world, new RecordId("sandbox", "yard"));

            foreach (var entity in world.Query<Transform, FromPlacements>().Entities)
                entity.GetComponent<Transform>().LocalPosition = new Vector3(7, 3, 2);

            Assert.True(document.Save(world));
            file = document.Path;
        }

        // Read it back the way a game does: a fresh engine, the record loader, no editor involved. The
        // saved file is copied into the second engine's own mount — the first fixture's folder is not
        // the second's, and copying a file onto itself is how this test first failed.
        var (second, secondFixture) = NewEngine();
        using (second)
        {
            File.Copy(file, Path.Combine(secondFixture.Dir("game"), "data", "yard.json"), overwrite: true);
            second.Records.Reload();

            Assert.True(second.Records.TryGet(new RecordId("sandbox", "yard"), out PlacementsRecord record));
            var placement = Assert.Single(record.Place);
            Assert.Equal(new Vector3(7, 3, 2), placement.At);
            Assert.Equal(new RecordId("sandbox", "post"), placement.Prefab.Id);
            Assert.Equal("corner", placement.Name);
        }
    }

    // Saving writes into the file as a person left it (issue #218): what did not change stays byte for
    // byte, comments and the records beside the document included, and a move is the line that moved.
    [Fact]
    public void SavingChangesOnlyWhatChangedInTheFile()
    {
        var (engine, fixture) = NewEngine();
        string written = """
            // The yard.
            [
              { "type": "prefab", "id": "post", "components": { "transform": {} } },   // kept
              {
                "type": "placements",
                "id": "yard",
                "place": [
                  { "prefab": "post", "at": [10, 0, -4], "name": "corner" },   // by the gate
                  { "prefab": "post", "at": [12, 0, -4], "name": "middle" },
                ],
              },
            ]
            """;
        fixture.Write("game", "data/yard.json", written);
        using (engine)
        {
            engine.Records.Reload();
            var world = engine.CreateWorld("edit");
            var document = new EditorDocument(engine);
            Assert.True(document.Open(world, new RecordId("sandbox", "yard")));
            string path = document.Path;
            string before = File.ReadAllText(path);

            Assert.True(document.Save(world));
            Assert.Equal(before, File.ReadAllText(path));   // nothing changed, nothing written differently

            foreach (var entity in world.Query<Transform, FromPlacements>().Entities)
                if (entity.Name == "middle") entity.GetComponent<Transform>().LocalPosition = new Vector3(13, 0, -4);
            Assert.True(document.Save(world));

            string[] a = before.Split('\n'), b = File.ReadAllText(path).Split('\n');
            Assert.Equal(a.Length, b.Length);
            var changed = Assert.Single(System.Linq.Enumerable.Range(0, a.Length), i => a[i] != b[i]);
            Assert.Contains("\"at\": [13, 0, -4], \"name\": \"middle\"", b[changed]);
            Assert.Contains("// The yard.", b[0]);

            // A second save writes from the first, not from what was opened.
            Assert.True(document.Save(world));
            Assert.Equal(string.Join('\n', b), File.ReadAllText(path));

            engine.Records.Reload();
            Assert.True(engine.Records.TryGet(new RecordId("sandbox", "post"), out PrefabRecord _));
            Assert.True(engine.Records.TryGet(new RecordId("sandbox", "yard"), out PlacementsRecord record));
            Assert.Equal(new Vector3(13, 0, -4), record.Place[1].At);
        }
    }

    [Fact]
    public void ClosingTakesBackWhatItPutInTheWorld()
    {
        var (engine, _) = NewEngine();
        using (engine)
        {
            var world = engine.CreateWorld("edit");
            var document = new EditorDocument(engine);

            document.Open(world, new RecordId("sandbox", "yard"));
            Assert.Equal(1, world.Query<FromPlacements>().Count);

            document.Close(world);

            Assert.Equal(0, world.Query<FromPlacements>().Count);
            Assert.False(document.IsOpen);
        }
    }

    [Fact]
    public void ADocumentSaysWhenItHasUnsavedWork()
    {
        var (engine, _) = NewEngine();
        using (engine)
        {
            var world = engine.CreateWorld("edit");
            var document = new EditorDocument(engine);
            document.Open(world, new RecordId("sandbox", "yard"));
            Assert.False(document.Dirty);

            document.Touch();                       // what the inspector does after an edit

            Assert.True(document.Dirty);
            Assert.Contains("*", document.Title);   // an editor that does not say so is one you lose work in

            document.Save(world);
            Assert.False(document.Dirty);
        }
    }
}
