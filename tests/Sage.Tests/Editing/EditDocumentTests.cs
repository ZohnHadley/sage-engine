#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The editor's document (docs/design/15 §3; F28, then issue #217's commands).
//
// An editor's one irreplaceable job is that what it saves is what the game will load. That is a claim
// about files, so it is checked against files: open a document, change it with commands, save, and read
// the file back through the record loader that a game would use. The rest is the document being the
// source of truth: every command changes it, re-spawns only what it touched, and undoes exactly.
public class EditDocumentTests
{
    public EditDocumentTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
        [
          { "type": "prefab", "id": "post", "components": { "transform": {}, "timer": { "interval": 2 } },
            "parts": { "body": { "shape": "Capsule", "radius": 0.5, "height": 2 } } },
          { "type": "prefab", "id": "lamp", "components": { "transform": {} } },
          { "type": "prefab", "id": "cart", "children": [ { "prefab": "lamp", "at": [0, 1, 0], "name": "cart lamp" } ] },
          {
            "type": "placements",
            "id": "yard",
            "place": [
              { "prefab": "post", "at": [10, 0, -4], "name": "corner" },
              { "prefab": "post", "at": [0, 0, 0], "name": "gate" }
            ]
          }
        ]
        """;

    private static readonly RecordId Yard = new("sandbox", "yard");

    private static (Engine Engine, MountFixture Fixture) NewEngine()
    {
        var fixture = new MountFixture();
        fixture.Write("game", "data/yard.json", Records);
        fixture.Mount("game", "sandbox");

        var engine = HeadlessApp.Bare().With(new PhysicsModule()).Mount(fixture).Build().Engine;
        return (engine, fixture);
    }

    private static EditDocument OpenYard(Engine engine)
    {
        var document = new EditDocument(engine.CreateWorld("edit"));
        Assert.True(document.Open(Yard));
        return document;
    }

    private static Placement Place(string prefab, Vector3 at, string name = "") =>
        new() { Prefab = new RecordId("sandbox", prefab), At = at, Name = name };

    private static int Placed(World world) => world.Query<FromPlacements>().Count;

    [Fact]
    public void SavingWritesWhereTheRecordCameFromRatherThanSomewhereNew()
    {
        var (engine, fixture) = NewEngine();
        using (engine)
        {
            var document = OpenYard(engine);
            Assert.True(document.Save());

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
        var (engine, _) = NewEngine();
        string file;
        using (engine)
        {
            var document = OpenYard(engine);
            var corner = document.Find("corner")!;
            document.Execute(new SetPlacement(document, corner, PlacementFields.Of(corner) with { At = new Vector3(7, 3, 2), Yaw = 90 }));
            document.Execute(new SetOverride(document, corner, OverrideSection.Component, "timer", "interval", JsonValue.Create(5f)));
            document.Execute(new AddPlacement(document, Place("lamp", new Vector3(1, 2, 3), "lantern")));

            Assert.True(document.Save());
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
            // The prefabs beside the document in its file are still there: a save replaces its own record.
            Assert.Equal(0, second.Records.ErrorCount);

            Assert.True(second.Records.TryGet(Yard, out PlacementsRecord record));
            Assert.Equal(3, record.Place.Count);
            var placement = record.Place[0];
            Assert.Equal(new Vector3(7, 3, 2), placement.At);
            Assert.Equal(90f, placement.Yaw);
            Assert.Equal(new RecordId("sandbox", "post"), placement.Prefab.Id);
            Assert.Equal("corner", placement.Name);
            Assert.Equal(5f, placement.Overrides!.Components!["timer"]!["interval"]!.GetValue<float>());
            Assert.Equal("lantern", record.Place[2].Name);
            Assert.Equal("lantern", record.Place[2].Id);   // a placement the editor adds has a stable id

            // And spawned by a game, the override is on the entity.
            var world = second.CreateWorld("play");
            world.SpawnPlacements(Yard);
            Assert.Equal(5f, world.Get<LogicTimer>(world.FindByName("corner")).Interval);
        }
    }

    [Fact]
    public void ClosingTakesBackWhatItPutInTheWorld()
    {
        var (engine, _) = NewEngine();
        using (engine)
        {
            var document = OpenYard(engine);
            Assert.Equal(2, Placed(document.World));

            document.Close();

            Assert.Equal(0, Placed(document.World));
            Assert.False(document.IsOpen);
            Assert.Empty(document.History.Entries);
        }
    }

    [Fact]
    public void ADocumentSaysWhenItHasUnsavedWork()
    {
        var (engine, _) = NewEngine();
        using (engine)
        {
            var document = OpenYard(engine);
            Assert.False(document.Dirty);
            var gate = document.Find("gate")!;

            document.Execute(new SetPlacement(document, gate, PlacementFields.Of(gate) with { At = new Vector3(1, 0, 0) }));
            Assert.True(document.Dirty);
            Assert.Contains("*", document.Title);   // an editor that does not say so is one you lose work in

            document.Save();
            Assert.False(document.Dirty);

            // Dirty is the log's position against the save's: undo past the save and the file and the
            // document disagree again; redo back to it and they agree.
            document.Undo();
            Assert.True(document.Dirty);
            document.Redo();
            Assert.False(document.Dirty);
        }
    }

    [Fact]
    public void AddingAPlacementSpawnsItAndUndoTakesItAway()
    {
        var (engine, _) = NewEngine();
        using (engine)
        {
            var document = OpenYard(engine);
            var world = document.World;
            var add = new AddPlacement(document, Place("post", new Vector3(5, 0, 5)));
            Assert.Equal("post", add.Placement.Id);   // unnamed: its id comes from its prefab

            document.Execute(add);
            Assert.Equal(3, document.Placements.Count);
            var entity = document.EntityOf(add.Placement);
            Assert.True(world.IsAlive(entity));
            Assert.Equal(new Vector3(5, 0, 5), world.Get<Transform>(entity).LocalPosition);
            Assert.Same(add.Placement, document.PlacementOf(entity));
            Assert.Equal("Place post", add.Description);

            Assert.True(document.Undo());
            Assert.Equal(2, document.Placements.Count);
            Assert.False(world.IsAlive(entity));
            Assert.Equal(2, Placed(world));

            Assert.True(document.Redo());
            Assert.Equal(3, Placed(world));
            Assert.True(world.IsAlive(document.EntityOf(add.Placement)));

            // A second one gets an id of its own.
            var again = new AddPlacement(document, Place("post", Vector3.Zero));
            Assert.Equal("post_2", again.Placement.Id);
        }
    }

    [Fact]
    public void RemovingAPlacementAndUndoingPutsItBackWhereItWas()
    {
        var (engine, _) = NewEngine();
        using (engine)
        {
            var document = OpenYard(engine);
            var world = document.World;
            var corner = document.Find("corner")!;
            var gate = document.Find("gate")!;
            var gateId = world.Get<Persistent>(document.EntityOf(gate)).Id;

            document.Execute(new RemovePlacement(document, corner));
            Assert.Equal(new[] { gate }, document.Placements);
            Assert.Equal(1, Placed(world));
            Assert.True(world.FindByName("corner").IsNull);
            // The gate moved up the list, and its identity comes from its place (it has no `id`): it was
            // re-spawned with the id a game loading the file would give it, not left with a stale one.
            Assert.NotEqual(gateId, world.Get<Persistent>(document.EntityOf(gate)).Id);

            document.Undo();
            Assert.Equal(new[] { corner, gate }, document.Placements);
            Assert.Equal(2, Placed(world));
            Assert.Equal(new Vector3(10, 0, -4), world.Get<Transform>(world.FindByName("corner")).LocalPosition);
            Assert.Equal(gateId, world.Get<Persistent>(document.EntityOf(gate)).Id);

            document.Redo();
            Assert.Equal(1, Placed(world));
        }
    }

    [Fact]
    public void SetPlacementReSpawnsOnlyThePlacementItChanged()
    {
        var (engine, _) = NewEngine();
        using (engine)
        {
            var document = OpenYard(engine);
            var world = document.World;
            var corner = document.Find("corner")!;
            var gateEntity = world.FindByName("gate");

            document.Execute(new SetPlacement(document, corner,
                PlacementFields.Of(corner) with { At = new Vector3(1, 2, 3), Yaw = 90, Name = "pillar" }));

            var pillar = document.EntityOf(corner);
            Assert.Equal("pillar", pillar.Name);
            Assert.Equal(new Vector3(1, 2, 3), world.Get<Transform>(pillar).LocalPosition);
            Assert.Equal(90f, SageMath.YawOf(world.Get<Transform>(pillar).LocalRotation) * 180f / MathF.PI, 3);
            Assert.True(world.IsAlive(gateEntity));          // the other placement's entity was not touched
            Assert.Equal("Rename corner to pillar", document.History.Entries[0].Description);

            document.Undo();
            var back = document.EntityOf(corner);
            Assert.Equal("corner", back.Name);
            Assert.Equal(new Vector3(10, 0, -4), world.Get<Transform>(back).LocalPosition);
            Assert.Equal(new Vector3(10, 0, -4), corner.At);
            Assert.True(world.IsAlive(gateEntity));

            // The frame: `at` measured from the document's origin, which is (0, 0, 0) here.
            document.Execute(new SetPlacement(document, corner, PlacementFields.Of(corner) with { RelativeTo = PlacementFrame.Origin }));
            Assert.Equal(PlacementFrame.Origin, corner.RelativeTo);
            document.Undo();
            Assert.Null(corner.RelativeTo);
        }
    }

    // The selection is an entity, and a command replaces the entity: what keeps a tool on the same thing
    // is the placement, and the identity a save would find it by.
    [Fact]
    public void AReSpawnKeepsItsPlacementIdentity()
    {
        var (engine, _) = NewEngine();
        using (engine)
        {
            var document = OpenYard(engine);
            var world = document.World;
            var selected = world.FindByName("gate");
            var placement = document.PlacementOf(selected)!;
            var id = world.Get<Persistent>(selected).Id;
            var replaced = new List<(Entity Old, Entity New)>();
            document.Respawned += (old, now) => replaced.Add((old, now));

            document.Execute(new SetPlacement(document, placement, PlacementFields.Of(placement) with { At = new Vector3(0, 0, 9) }));

            var (old, now) = Assert.Single(replaced);
            Assert.Equal(selected, old);
            Assert.False(world.IsAlive(selected));
            Assert.Same(placement, document.PlacementOf(now));
            Assert.Equal(now, document.EntityOf(placement));
            Assert.Equal(id, world.Get<Persistent>(now).Id);   // the same thing to a save
            Assert.Equal(1, world.Query<Persistent>().Entities.Count(e => world.Get<Persistent>(e).Id == id));
        }
    }

    [Fact]
    public void AReSpawnTakesThePrefabsChildrenWithIt()
    {
        var (engine, _) = NewEngine();
        using (engine)
        {
            var document = OpenYard(engine);
            var world = document.World;
            var add = new AddPlacement(document, Place("cart", Vector3.Zero, "cart"));
            document.Execute(add);
            Assert.False(world.FindByName("cart lamp").IsNull);
            int entities = world.QueryAll().Count;

            document.Execute(new SetPlacement(document, add.Placement, PlacementFields.Of(add.Placement) with { At = new Vector3(4, 0, 0) }));
            Assert.Equal(entities, world.QueryAll().Count);   // one cart and one lamp, not two of each
            var lamp = world.FindByName("cart lamp");
            Assert.Equal(document.EntityOf(add.Placement), lamp.Parent);

            document.Undo();
            document.Undo();
            Assert.True(world.FindByName("cart lamp").IsNull);
        }
    }

    [Fact]
    public void AnOverrideIsOneFieldOfOneComponentOrPartAndRevertsToThePrefab()
    {
        var (engine, _) = NewEngine();
        using (engine)
        {
            var document = OpenYard(engine);
            var world = document.World;
            var gate = document.Find("gate")!;

            // A component's field, written as a placement writes it, under the id the prefab used.
            document.Execute(new SetOverride(document, gate, OverrideSection.Component, "sage:timer", "interval", JsonValue.Create(7f)));
            Assert.Equal(7f, world.Get<LogicTimer>(document.EntityOf(gate)).Interval);
            Assert.Equal("""{"sage:timer":{"interval":7}}""", gate.Overrides!.Components!.ToJsonString());
            // A second field of the same component goes into the same body (a slider: merged into one edit).
            document.History.EndMerge();
            document.Execute(new SetOverride(document, gate, OverrideSection.Component, "timer", "spread", JsonValue.Create(1f)));
            Assert.Equal("""{"sage:timer":{"interval":7,"spread":1}}""", gate.Overrides!.Components!.ToJsonString());

            // A part's option: the part still builds the collider, from the placement's number.
            document.Execute(new SetOverride(document, gate, OverrideSection.Part, "body", "radius", JsonValue.Create(0.25f)));
            Assert.Equal(0.25f, world.Get<Collider>(document.EntityOf(gate)).Size.X, 3);
            Assert.Equal(0.5f, world.Get<Collider>(world.FindByName("corner")).Size.X, 3);

            // Revert to the prefab, one field at a time; an emptied body and emptied overrides go too.
            document.Execute(new ClearOverride(document, gate, OverrideSection.Part, "body", "radius"));
            Assert.Null(gate.Overrides!.Parts);
            Assert.Equal(0.5f, world.Get<Collider>(document.EntityOf(gate)).Size.X, 3);
            document.Execute(new ClearOverride(document, gate, OverrideSection.Component, "timer", "interval"));
            document.Execute(new ClearOverride(document, gate, OverrideSection.Component, "timer", "spread"));
            Assert.Null(gate.Overrides);
            Assert.Equal(2f, world.Get<LogicTimer>(document.EntityOf(gate)).Interval);

            // And every step of it undoes.
            document.Undo();
            document.Undo();
            Assert.Equal(7f, world.Get<LogicTimer>(document.EntityOf(gate)).Interval);
            document.Undo();
            Assert.Equal(0.25f, world.Get<Collider>(document.EntityOf(gate)).Size.X, 3);
            while (document.Undo()) { }
            Assert.Null(gate.Overrides);
            Assert.Equal(2f, world.Get<LogicTimer>(document.EntityOf(gate)).Interval);
            Assert.False(document.Dirty);
        }
    }

    [Fact]
    public void SettingOutputsWiresThePlacedEntityAndUndoUnwiresIt()
    {
        var (engine, _) = NewEngine();
        using (engine)
        {
            var document = OpenYard(engine);
            var world = document.World;
            var gate = document.Find("gate")!;
            var wire = new Connection { Output = "OnTimer", Target = "corner", Input = "Kill", Delay = 1 };

            document.Execute(new SetOutputs(document, gate, new[] { wire }));
            wire.Delay = 9;   // the command took a copy: what the caller does with its list afterwards is its own

            var wires = world.Get<IOConnections>(document.EntityOf(gate)).Wires;
            Assert.Equal("corner", Assert.Single(wires).Target);
            Assert.Equal(1f, gate.Outputs.Single().Delay);

            document.Undo();
            Assert.Empty(gate.Outputs);
            Assert.False(world.Has<IOConnections>(document.EntityOf(gate)));
            document.Redo();
            Assert.True(world.Has<IOConnections>(document.EntityOf(gate)));
        }
    }

    [Fact]
    public void ANewLevelIsASceneNamingANewPlacementsDocument()
    {
        var (engine, fixture) = NewEngine();
        using (engine)
        {
            var document = new EditDocument(engine.CreateWorld("edit"));
            var id = document.NewLevel(new RecordId("sandbox", "crypt"));
            Assert.Equal(new RecordId("sandbox", "crypt_placements"), id);
            Assert.True(document.Dirty);   // nothing saved yet
            document.Execute(new AddPlacement(document, Place("post", new Vector3(1, 0, 1), "altar")));
            Assert.True(document.Save());
            Assert.False(document.Dirty);

            // Both files are records a game loads: the scene places the document, the document the altar.
            engine.Records.Reload();
            Assert.Equal(0, engine.Records.ErrorCount);
            Assert.True(engine.Records.TryGet(new RecordId("sandbox", "crypt"), out SceneRecord scene));
            Assert.Equal(id, Assert.Single(scene.Placements).Id);
            Assert.True(engine.Records.TryGet(id, out PlacementsRecord record));
            Assert.Equal("altar", Assert.Single(record.Place).Name);
            Assert.True(File.Exists(Path.Combine(fixture.Dir("game"), "data", "crypt.json")));
        }
    }

    // Every menu item is a console command: the doc_* commands go through the document, and the history
    // is pressed with ed_undo, ed_redo and ed_history.
    [Fact]
    public void TheConsoleOpensUndoesRedoesAndListsTheHistory()
    {
        var (engine, _) = NewEngine();
        using (engine)
        {
            var document = new EditDocument(engine.CreateWorld("edit"));
            EditorCommands.Register(engine.CVars, () => document);
            var cvars = engine.CVars;

            Assert.True(cvars.Execute("doc_open yard"));
            Assert.True(document.IsOpen);
            var corner = document.Find("corner")!;
            document.Execute(new SetPlacement(document, corner, PlacementFields.Of(corner) with { At = new Vector3(1, 1, 1) }));
            document.History.EndMerge();
            document.Execute(new SetPlacement(document, corner, PlacementFields.Of(corner) with { Yaw = 45 }));

            using (var log = new CaptureSink())
            {
                cvars.Execute("ed_history");
                var history = log.Entries.Select(e => e.Message).Single(m => m.StartsWith("sandbox:yard"));
                Assert.Contains("2 of 2 edit(s) done", history);
                Assert.Contains("1. Move corner", history);
                Assert.Contains("> 2. Rotate corner", history);
            }

            cvars.Execute("ed_undo 2");
            Assert.Equal(new Vector3(10, 0, -4), corner.At);
            Assert.Equal(0, document.History.Position);
            cvars.Execute("ed_redo");
            Assert.Equal(new Vector3(1, 1, 1), corner.At);
            Assert.Equal(0f, corner.Yaw);
            Assert.Contains("(undone)", EditorCommands.History(document));

            cvars.Execute("doc_save");
            Assert.False(document.Dirty);
            cvars.Execute("doc_close");
            Assert.False(document.IsOpen);
            cvars.Execute("doc_level crypt");
            Assert.Equal(new RecordId("sandbox", "crypt"), document.Scene);
        }
    }
}
