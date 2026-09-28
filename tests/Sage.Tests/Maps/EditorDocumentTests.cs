#nullable enable
using System.IO;
using System.Numerics;
using Friflo.Engine.ECS;
using sage_engine;

namespace sage_engine.Tests;

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
