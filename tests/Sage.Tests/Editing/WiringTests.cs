#nullable enable
using System;
using System.Linq;
using System.Numerics;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Wiring entity I/O in the editor (issue #225). The panel only draws; what it lists, checks and writes
// is Sage.Editing's, pressed here through the console and checked in the document and the world.
public class WiringTests
{
    public WiringTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
        [
          { "type": "prefab", "id": "plate", "components": { "transform": {} } },
          { "type": "prefab", "id": "door", "components": { "transform": {}, "sage:logic_relay": {} } },
          {
            "type": "placements",
            "id": "yard",
            "place": [
              { "prefab": "plate", "at": [0, 0, 0], "name": "plate" },
              { "prefab": "door", "at": [4, 0, 0], "name": "door" },
              { "prefab": "plate", "at": [8, 0, 0] }
            ]
          }
        ]
        """;

    private sealed record Editor(HeadlessApp App, EditDocument Document) : IDisposable
    {
        public CVarRegistry Console => App.Engine.CVars;
        public void Dispose() => App.Dispose();
    }

    private static Editor Open()
    {
        var fixture = new MountFixture();
        fixture.Write("game", "data/yard.json", Records);
        fixture.Mount("game", "sandbox");
        var app = HeadlessApp.Gameplay().Mount(fixture).Build();
        var document = new EditDocument(app.Engine.CreateWorld("edit"));
        EditorCommands.Register(app.Engine.CVars, () => document);
        Assert.True(app.Engine.CVars.Execute("doc_open yard"));
        return new Editor(app, document);
    }

    [Fact]
    public void EdWireAddsAConnectionToTheDocumentAndTheRespawnedEntity()
    {
        using var editor = Open();
        var (document, console) = (editor.Document, editor.Console);
        var plate = document.Find("plate")!;

        Assert.True(console.Execute("ed_wire plate OnStartTouch door Toggle 1.5 hello there"));
        var wire = Assert.Single(plate.Outputs);
        Assert.Equal(("OnStartTouch", "door", "Toggle", 1.5f, "hello there"), (wire.Output, wire.Target, wire.Input, wire.Delay, wire.Parameter));

        var live = Assert.Single(document.World.Get<IOConnections>(document.EntityOf(plate)).Wires);
        Assert.Equal("Toggle", live.Input);
        Assert.Equal(1.5f, live.Delay);
        Assert.True(document.Dirty);

        // The panel's lines run from the plate to the door.
        var line = Assert.Single(WiringModel.Lines(document));
        Assert.Equal(new Vector3(0, 0, 0), line.From);
        Assert.Equal(new Vector3(4, 0, 0), line.To);
    }

    [Fact]
    public void WiringUndoesAndRedoesAsOneStepAndUnwireTakesWiresAway()
    {
        using var editor = Open();
        var (document, console) = (editor.Document, editor.Console);
        var plate = document.Find("plate")!;

        console.Execute("ed_wire plate OnStartTouch door Toggle");
        console.Execute("ed_wire plate OnStartTouch door Enable");
        console.Execute("ed_wire plate OnEndTouch door Disable");
        Assert.Equal(3, plate.Outputs.Count);

        document.Undo();
        Assert.Equal(2, plate.Outputs.Count);
        document.Redo();
        Assert.Equal(3, plate.Outputs.Count);

        // By number (as ed_wires prints them), then by output name: each one step.
        Assert.True(console.Execute("ed_unwire plate 3"));
        Assert.Equal(new[] { "Toggle", "Enable" }, plate.Outputs.Select(w => w.Input).ToArray());
        Assert.True(console.Execute("ed_unwire plate OnStartTouch"));
        Assert.Empty(plate.Outputs);
        Assert.False(document.World.Has<IOConnections>(document.EntityOf(plate)));
        document.Undo();
        Assert.Equal(2, plate.Outputs.Count);
        document.Undo();
        Assert.Equal(3, plate.Outputs.Count);
        Assert.True(console.Execute("ed_wires plate"));
    }

    [Fact]
    public void AnInputTheTargetCannotTakeIsRefusedAndTheReasonListsWhatItTakes()
    {
        using var editor = Open();
        var (document, console) = (editor.Document, editor.Console);
        var plate = document.Find("plate")!;

        // `Toggle` is a relay's: the plate has no relay, the door has.
        string? error = Wiring.Check(document, "plate", "Toggle");
        Assert.NotNull(error);
        Assert.Contains("takes:", error);
        Assert.Contains("Kill", error);
        Assert.DoesNotContain("Toggle", error![(error.IndexOf("takes:", StringComparison.Ordinal))..]);
        Assert.Null(Wiring.Check(document, "door", "Toggle"));
        Assert.Contains("Toggle", new WiringModel(document, plate).InputsOf("door").Select(i => i.Name));
        Assert.DoesNotContain("Toggle", new WiringModel(document, plate).InputsOf("plate").Select(i => i.Name));

        console.Execute("ed_wire door OnTimer plate Toggle");
        console.Execute("ed_wire plate OnUse nobody Kill");
        Assert.Empty(document.Find("door")!.Outputs);
        Assert.Empty(plate.Outputs);
        Assert.False(document.History.CanUndo);   // a refusal changed nothing

        // `!self` and friends are whoever it is about when it fires: not checked against a target.
        Assert.True(Wiring.Add(document, plate, new Connection { Output = "OnUse", Target = "!self", Input = "Kill" }, out _));
    }

    [Fact]
    public void ATargetWithNoNameCannotBeWiredUntilItIsNamed()
    {
        using var editor = Open();
        var document = editor.Document;
        var nameless = document.Placements[2];

        Assert.Null(Wiring.NameOf(nameless));
        string name = Wiring.NameIt(document, nameless);
        Assert.Equal("plate_2", name);   // `plate` is taken
        Assert.Equal(name, nameless.Name);
        Assert.True(editor.Console.Execute($"ed_wire door OnTimer {name} Kill"));
        Assert.Single(document.Find("door")!.Outputs);

        // Naming is its own undo step.
        document.Undo();
        document.Undo();
        Assert.Equal("", nameless.Name);
    }

    [Fact]
    public void AWireIsEditedInPlaceAndTheOutputsOnOfferAreDeclaredOrUsedInTheDocument()
    {
        using var editor = Open();
        var (document, console) = (editor.Document, editor.Console);
        var plate = document.Find("plate")!;
        console.Execute("ed_wire plate OnPressedByMe door Toggle");

        var changed = new Connection { Output = "OnPressedByMe", Target = "door", Input = "Toggle", Delay = 3, Parameter = "x" };
        Assert.True(Wiring.Update(document, plate, 0, changed, out _));
        Assert.Equal(3f, plate.Outputs[0].Delay);
        Assert.False(Wiring.Update(document, plate, 5, changed, out var error));
        Assert.Contains("no wire 6", error);

        var outputs = new WiringModel(document, plate).Outputs();
        Assert.Contains(outputs, o => o.Name == "OnStartTouch" && o.Declared);
        Assert.Contains(outputs, o => o.Name == "OnPressedByMe" && !o.Declared);
    }

    [Fact]
    public void AWiredPlateFiresTheDoorOnceTheEditHasRespawnedIt()
    {
        using var editor = Open();
        var (document, console) = (editor.Document, editor.Console);
        var world = document.World;
        Assert.True(console.Execute("ed_wire plate OnUse door Kill"));

        var door = document.EntityOf(document.Find("door")!);
        Assert.True(world.IsAlive(door));
        world.FireOutput(document.EntityOf(document.Find("plate")!), "OnUse");
        for (int i = 0; i < 3; i++) world.RunFixed(1f / 60f);
        Assert.False(world.IsAlive(door));
    }
}
