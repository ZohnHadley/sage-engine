#nullable enable
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The inspector on the document (issue #223): an edit is an override the placement writes, not a change
// to the live struct, so it survives a re-spawn and a save; "revert to prefab" takes it away; and every
// field says who set it. Driven through the console (ed_set, ed_revert), as a person or a script would.
public class InspectorTests
{
    public InspectorTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
        [
          { "type": "prefab", "id": "post",
            "components": { "transform": {}, "timer": { "interval": 2 } },
            "parts": { "body": { "shape": "Capsule", "radius": 0.5, "height": 2 } } },
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

    private static (Engine Engine, MountFixture Fixture, EditDocument Document) Open(string? patch = null)
    {
        var fixture = new MountFixture();
        fixture.Write("game", "data/yard.json", Records);
        fixture.Mount("game", "sandbox");
        if (patch != null)
        {
            fixture.Write("mods/sturdy", "data/patch.json", patch);
            fixture.Mount("mods/sturdy", "sturdy");
        }
        var engine = HeadlessApp.Bare().With(new PhysicsModule()).Mount(fixture).Build().Engine;
        var document = new EditDocument(engine.CreateWorld("edit"));
        EditorCommands.Register(engine.CVars, () => document);
        InspectorCommands.Register(engine.CVars, () => document);
        Assert.True(document.Open(Yard));
        return (engine, fixture, document);
    }

    private static InspectorModel Inspect(EditDocument document, string name) =>
        InspectorModel.Of(document, document.World, document.EntityOf(document.Find(name)!));

    private static float Interval(EditDocument document, string name) =>
        document.World.Get<LogicTimer>(document.EntityOf(document.Find(name)!)).Interval;

    [Fact]
    public void EdSetWritesAnOverrideTheRespawnedEntityHasAndASaveAndReopenKeep()
    {
        var (engine, _, document) = Open();
        using (engine)
        {
            var gate = document.Find("gate")!;
            Assert.True(engine.CVars.Execute("ed_set gate timer.interval 7"));

            // In the document, in the shape a placement writes, and on the entity it re-spawned.
            Assert.Equal("""{"timer":{"interval":7}}""", gate.Overrides!.Components!.ToJsonString());
            Assert.Equal(7f, Interval(document, "gate"));
            Assert.Equal(2f, Interval(document, "corner"));
            var row = Inspect(document, "gate").Find("timer.interval")!;
            Assert.True(row.Overridden);
            Assert.Equal(7f, row.Value);
            Assert.Equal("this placement (sandbox:yard)", row.Provenance);
            Assert.False(Inspect(document, "corner").Find("timer.interval")!.Overridden);

            // Saved, the file is reloaded and the document opened again: the override is still there.
            Assert.True(engine.CVars.Execute("doc_save"));
            engine.CVars.Execute("doc_close");
            engine.Records.Reload();
            Assert.Equal(0, engine.Records.ErrorCount);
            engine.CVars.Execute("doc_open yard");
            Assert.Equal(7f, Interval(document, "gate"));
            Assert.True(Inspect(document, "gate").Find("timer.interval")!.Overridden);
        }
    }

    [Fact]
    public void EdRevertGoesBackToThePrefabAndUndoRedoWalkBoth()
    {
        var (engine, _, document) = Open();
        using (engine)
        {
            var gate = document.Find("gate")!;
            engine.CVars.Execute("ed_set gate sage:timer.interval 7");
            document.History.EndMerge();
            engine.CVars.Execute("ed_revert gate timer.interval");
            Assert.Null(gate.Overrides);
            Assert.Equal(2f, Interval(document, "gate"));
            var row = Inspect(document, "gate").Find("timer.interval")!;
            Assert.False(row.Overridden);
            Assert.Contains("data/yard.json", row.Provenance);

            // Reverting what is not overridden is refused and changes nothing.
            using (var log = new CaptureSink())
            {
                engine.CVars.Execute("ed_revert gate timer.interval");
                Assert.Contains(log.Entries, e => e.Message.Contains("is not overridden"));
            }
            Assert.Equal(2, document.History.Position);

            document.Undo();
            Assert.Equal(7f, Interval(document, "gate"));
            document.Undo();
            Assert.Null(gate.Overrides);
            Assert.False(document.Dirty);
            document.Redo();
            Assert.Equal(7f, Interval(document, "gate"));
            document.Redo();
            Assert.Equal(2f, Interval(document, "gate"));
        }
    }

    // A widget dragged over a field is one edit: the inspector ends the merge only when nothing is active.
    [Fact]
    public void ADragOverAFieldIsOneUndo()
    {
        var (engine, _, document) = Open();
        using (engine)
        {
            for (int i = 1; i <= 5; i++)
            {
                var model = Inspect(document, "gate");
                Assert.True(model.Set(model.Find("timer.interval")!, model.ToNode(2f + i), out _));
            }
            Assert.Equal(1, document.History.Position);
            Assert.Equal(7f, Interval(document, "gate"));
            document.History.EndMerge();
            var again = Inspect(document, "gate");
            again.Set(again.Find("timer.interval")!, JsonValue.Create(8), out _);
            Assert.Equal(2, document.History.Position);
            document.Undo();
            document.Undo();
            Assert.Equal(2f, Interval(document, "gate"));
        }
    }

    // Each field names the last write to it: the prefab's own file, or a mod's patch over it.
    [Fact]
    public void ProvenanceNamesThePrefabsFileAndAPatchingMount()
    {
        const string patch = """[ { "type": "prefab", "id": "sandbox:post", "patch": true, "components": { "timer": { "interval": 3 } } } ]""";
        var (engine, _, document) = Open(patch);
        using (engine)
        {
            var model = Inspect(document, "gate");
            var interval = model.Find("timer.interval")!;
            Assert.Equal(3f, interval.Value);
            Assert.Equal("mods/sturdy", interval.SetBy!.Value.Mount!.Name);
            Assert.StartsWith("mods/sturdy:data/patch.json", interval.Provenance);

            var radius = model.Find("body.radius")!;
            Assert.Equal(0.5f, radius.Value);
            Assert.Equal("game", radius.SetBy!.Value.Mount!.Name);
            Assert.StartsWith("game:data/yard.json", radius.Provenance);

            // A field nothing writes is the component's default.
            Assert.Equal("default", model.Find("timer.spread")!.Provenance);

            // Overridden, it is this placement's, and the console lists it with a mark.
            engine.CVars.Execute("ed_set gate body.radius 0.25");
            Assert.Equal("this placement (sandbox:yard)", Inspect(document, "gate").Find("body.radius")!.Provenance);
            Assert.Contains("body.radius = 0.25 *", InspectorCommands.Describe(Inspect(document, "gate")));
        }
    }

    // Parts are their options (the part rebuilds what it makes from them); what the prefab does not
    // name, or the placement owns, is not an override; the placement's own fields are set as a placement.
    [Fact]
    public void PartsArePartOptionsAndThePlacementsOwnFieldsAreAForm()
    {
        var (engine, _, document) = Open();
        using (engine)
        {
            var world = document.World;
            var gate = document.Find("gate")!;

            Assert.True(engine.CVars.Execute("ed_set gate body.radius 0.25"));
            Assert.Equal("""{"body":{"radius":0.25}}""", gate.Overrides!.Parts!.ToJsonString());
            Assert.Equal(0.25f, world.Get<Collider>(document.EntityOf(gate)).Size.X, 3);
            Assert.Equal(0.25f, Inspect(document, "gate").Find("body.radius")!.Value);

            // The collider the part made, and the transform, are shown and not editable.
            var model = Inspect(document, "gate");
            var collider = model.Groups.Single(g => g.Rows.Count > 0 && g.Id.EndsWith("collider"));
            Assert.Null(collider.Section);
            Assert.All(collider.Rows, r => Assert.False(r.Editable));
            Assert.False(model.Set(collider.Rows[0], JsonValue.Create(1), out string why));
            Assert.Contains("not in the prefab's components", why);
            Assert.Null(model.Groups.Single(g => g.Key == "transform").Section);

            int before = document.History.Position;
            engine.CVars.Execute("ed_set gate timer.interval -4x");
            Assert.Equal(before, document.History.Position);

            engine.CVars.Execute("ed_set gate at 1,2,3");
            engine.CVars.Execute("ed_set gate yaw 90");
            engine.CVars.Execute("ed_set gate relativeTo origin");
            Assert.Equal(new Vector3(1, 2, 3), gate.At);
            Assert.Equal(90f, gate.Yaw);
            Assert.Equal(PlacementFrame.Origin, gate.RelativeTo);
            engine.CVars.Execute("ed_set gate relativeTo document");
            Assert.Null(gate.RelativeTo);
            engine.CVars.Execute("ed_set gate name \"front gate\"");
            Assert.Equal("front gate", gate.Name);
            Assert.NotNull(document.Find("front gate"));
        }
    }

    // What the document did not place has nowhere to save an edit: the model is read-only.
    [Fact]
    public void AnEntityTheDocumentDidNotPlaceIsReadOnly()
    {
        var (engine, _, document) = Open();
        using (engine)
        {
            var world = document.World;
            var spawned = world.Spawn(new RecordId("sandbox", "post"), new Vector3(5, 0, 5));
            var model = InspectorModel.Of(document, world, spawned);
            Assert.False(model.FromDocument);
            Assert.NotEmpty(model.Rows);
            Assert.All(model.Rows, r => Assert.False(r.Editable));
            Assert.False(model.TrySet("timer.interval", "9", out string why));
            Assert.Contains("not placed by the open document", why);
            Assert.Equal(0, document.History.Position);
        }
    }

    private enum Weather { Clear, Rain }

    [Fact]
    public void ConsoleValuesAreReadByTheFieldsShape()
    {
        static string? Parse(FieldMetadata field, string text, RecordStore? records = null) =>
            InspectorValue.TryParse(field, text, out var node, out _, records, "sandbox") ? node.ToJsonString() : null;

        var number = new FieldMetadata("Range", typeof(float), ValueKind.Number) { Min = 0, Max = 10 };
        Assert.Equal("2.5", Parse(number, "2.5"));
        Assert.Null(Parse(number, "11"));
        Assert.Null(Parse(number, "two"));
        Assert.Null(Parse(number, "NaN"));

        var integer = new FieldMetadata("Count", typeof(int), ValueKind.Integer);
        Assert.Equal("42", Parse(integer, "42"));
        Assert.Null(Parse(integer, "4.2"));

        var flag = new FieldMetadata("On", typeof(bool), ValueKind.Bool);
        Assert.Equal("true", Parse(flag, "yes"));
        Assert.Equal("false", Parse(flag, "OFF"));
        Assert.Null(Parse(flag, "maybe"));

        var vector = new FieldMetadata("At", typeof(Vector3), ValueKind.Vector3);
        Assert.Equal("[1,2,3]", Parse(vector, "1 2 3"));
        Assert.Equal("[1,-2.5,3]", Parse(vector, "1,-2.5, 3"));
        Assert.Equal("[1,2,3]", Parse(vector, "[1, 2, 3]"));
        Assert.Null(Parse(vector, "1 2"));
        Assert.Equal("[0,0,0,1]", Parse(new FieldMetadata("Q", typeof(Quaternion), ValueKind.Quaternion), "0 0 0 1"));

        var choice = new FieldMetadata("Weather", typeof(Weather), ValueKind.Enum) { EnumValues = new[] { "Clear", "Rain" } };
        Assert.Equal("\"Rain\"", Parse(choice, "rain"));
        Assert.Null(Parse(choice, "snow"));

        var text = new FieldMetadata("Label", typeof(string), ValueKind.String);
        Assert.Equal("\"front gate\"", Parse(text, "\"front gate\""));

        var json = new FieldMetadata("Tags", typeof(System.Collections.Generic.List<string>), ValueKind.List);
        Assert.Equal("[\"a\",\"b\"]", Parse(json, "[\"a\", \"b\"]"));
        Assert.Null(Parse(new FieldMetadata("Who", typeof(Entity), ValueKind.Entity), "1"));

        // A record id is checked against the records of the type the field names.
        var (engine, _, _) = Open();
        using (engine)
        {
            var prefab = new FieldMetadata("Prefab", typeof(RecordId), ValueKind.RecordId) { RecordType = "prefab" };
            Assert.Equal("\"post\"", Parse(prefab, "post", engine.Records));
            Assert.Equal("\"sandbox:post\"", Parse(prefab, "sandbox:post", engine.Records));
            Assert.Null(Parse(prefab, "nothing", engine.Records));
            Assert.Equal("\"\"", Parse(prefab, "none", engine.Records));
        }
    }
}
