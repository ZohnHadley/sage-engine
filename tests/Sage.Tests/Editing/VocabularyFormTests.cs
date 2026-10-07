#nullable enable
using System;
using System.Linq;
using System.Text.Json.Nodes;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The conditions and actions editor's model (issue #370): a form over a vocabulary's value, built from what
// the vocabulary declares, so a wire's `requires` and a dialogue option's conditions are authored by picking
// entries and filling in their settings — never by typing JSON — each edit one undo step, checked live.
public class VocabularyFormTests
{
    public VocabularyFormTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
        [
          { "type": "item", "id": "key_iron", "label": "iron key", "weight": 0.1, "maxStack": 1 },
          { "type": "item", "id": "key_gold", "label": "gold key", "weight": 0.1, "maxStack": 1 },
          { "type": "dialogue", "id": "guard", "start": "start",
            "nodes": [ { "id": "start", "text": "Halt.", "options": [ { "text": "Let me in", "end": true } ] } ] },
          { "type": "prefab", "id": "plate", "components": { "transform": {} } },
          { "type": "prefab", "id": "door", "components": { "transform": {}, "sage:logic_relay": {} } },
          {
            "type": "placements",
            "id": "yard",
            "place": [
              { "prefab": "plate", "at": [0, 0, 0], "name": "plate",
                "outputs": [ { "output": "OnStartTouch", "target": "door", "input": "Toggle" } ] },
              { "prefab": "door", "at": [4, 0, 0], "name": "door" }
            ]
          }
        ]
        """;

    private sealed record Editor(HeadlessApp App, EditDocument Document, RecordEditor Records, VocabularyEditor Forms, MountFixture Fixture) : IDisposable
    {
        public CVarRegistry Console => App.Engine.CVars;
        public Engine Engine => App.Engine;
        public void Dispose() => App.Dispose();
    }

    private static Editor Open()
    {
        var fixture = new MountFixture();
        fixture.Write("game", "data/yard.json", Records);
        fixture.Mount("game", "sandbox");
        var app = HeadlessApp.Gameplay().Mount(fixture).Build();
        var document = new EditDocument(app.Engine.CreateWorld("edit"));
        var records = new RecordEditor(app.Engine);
        var forms = new VocabularyEditor(app.Engine, records);
        EditorCommands.Register(app.Engine.CVars, () => document);
        records.Register(app.Engine.CVars);
        forms.Register(app.Engine.CVars, () => document);
        Assert.True(app.Engine.CVars.Execute("doc_open yard"));
        return new Editor(app, document, records, forms, fixture);
    }

    [Fact]
    public void TheCatalogListsAVocabularysEntriesWithTheirDeclaredSettings()
    {
        using var editor = Open();
        var conditions = VocabularyCatalog.Named(editor.Engine, "condition")!;
        var entries = VocabularyCatalog.Entries(editor.Engine, conditions);
        Assert.Contains(entries, e => e.Id == "all");
        Assert.Contains(entries, e => e.Id == "var");

        var hasItem = Assert.Single(VocabularyCatalog.Entries(editor.Engine, conditions, "has_it"));
        Assert.Equal("sage.gameplay.items", hasItem.Owner);
        var item = hasItem.Parameter("item")!;
        Assert.True(item.IsValue);
        Assert.Equal("item", item.Field.RecordType);
        var count = hasItem.Parameter("count")!;
        Assert.Equal(1, count.Field.Min);
        Assert.Equal(1, (int?)count.Default);

        // A setting that holds conditions itself is a nested vocabulary, not a value to type.
        var of = VocabularyCatalog.Entry(editor.Engine, conditions, "all")!.Parameter("of")!;
        Assert.Same(conditions, of.Nested);
        Assert.True(of.NestedList);

        // Every vocabulary is offered, not just these two.
        Assert.Contains(VocabularyCatalog.All(editor.Engine), v => v.Name == "action");
        Assert.Contains(VocabularyCatalog.All(editor.Engine), v => v.Name == "quest_objective");
    }

    [Fact]
    public void AWiresRequiresIsAuthoredFromTheFormEachEditOneUndo()
    {
        using var editor = Open();
        var plate = editor.Document.Find("plate")!;
        var form = VocabularyForm.ForWire(editor.Document, plate, 0, out var error)!;
        Assert.Null(error);
        Assert.False(form.Many);
        Assert.Empty(form.Rows());

        Assert.True(form.Choose("", "all", out error), error);
        Assert.True(form.Add("of", "has_item", out error), error);
        Assert.True(form.SetParameter("of[0]", "item", "key_iron", out error), error);
        Assert.True(form.SetParameter("of[0]", "count", "2", out error), error);
        Assert.True(form.Add("of", "not", out error), error);
        Assert.True(form.Choose("of[1].of", "var", out error), error);
        Assert.True(form.SetParameter("of[1].of", "name", "alarm", out error), error);

        // The wire holds what the game reads, and the live entity got it too.
        var requires = plate.Outputs[0].Requires!;
        Assert.Equal("AllCondition", requires.GetType().Name);
        var live = editor.Document.World.Get<IOConnections>(editor.Document.EntityOf(plate)).Wires[0];
        Assert.NotNull(live.Requires);

        // Read back as the form shows it: the long form, defaults left out.
        var rows = form.Rows();
        Assert.Equal(new[] { "", "of[0]", "of[1]", "of[1].of" }, rows.Select(r => r.Path));
        Assert.Equal(new[] { "all", "has_item", "not", "var" }, rows.Select(r => r.Id));
        var hasItem = rows[1];
        Assert.Equal("sandbox:key_iron", hasItem.Settings.Single(s => s.Parameter.Name == "item").Text);
        Assert.Equal("2", hasItem.Settings.Single(s => s.Parameter.Name == "count").Text);
        Assert.Empty(form.Validate());

        // Seven edits, seven undo steps; each takes one back.
        Assert.True(editor.Document.Undo());
        Assert.Equal("", form.Rows().Last().Settings.Single(s => s.Parameter.Name == "name").Text);
        for (int i = 0; i < 6; i++) Assert.True(editor.Document.Undo());
        Assert.Null(plate.Outputs[0].Requires);
        Assert.True(editor.Document.Redo());
        Assert.Equal("all", Assert.Single(form.Rows()).Id);
    }

    [Fact]
    public void TheFormRefusesWhatTheEntryDoesNotDeclare()
    {
        using var editor = Open();
        var form = VocabularyForm.ForWire(editor.Document, editor.Document.Find("plate")!, 0, out _)!;
        Assert.False(form.Choose("", "has_itme", out var error));
        Assert.Contains("did you mean 'has_item'", error);
        Assert.True(form.Choose("", "has_item", out error), error);
        Assert.False(form.SetParameter("", "cuont", "2", out error));
        Assert.Contains("did you mean 'count'", error);
        Assert.False(form.SetParameter("", "count", "0", out error));      // below the declared Min = 1
        Assert.False(form.SetParameter("", "count", "two", out error));
        Assert.False(form.SetParameter("", "item", "key_bronze", out error));   // checked against the item records
        Assert.False(form.Add("", "has_item", out error));                  // one condition, not a list
        Assert.Equal(1, editor.Document.History.Position);
    }

    [Fact]
    public void ADialogueConditionIsAuthoredSavedAndReadByTheGame()
    {
        using var editor = Open();
        Assert.True(editor.Records.Open("dialogue", new RecordId("sandbox", "guard")));
        var record = editor.Records.Current!;

        // The option writes no `conditions` yet: the record browser offers the field all the same.
        Assert.Contains(VocabularyEditor.FieldsAt(record, "nodes[0].options[0]"), f => f.Name == "conditions");
        Assert.Contains(VocabularyEditor.FieldsAt(record, "nodes[0].options[0]"), f => f.Name == "actions");

        Assert.True(editor.Forms.OpenRecord("nodes[0].options[0].conditions", out var error), error);
        var form = editor.Forms.Current!;
        Assert.True(form.Many);
        Assert.True(form.Add("", "has_item", out error), error);
        Assert.True(form.SetParameter("[0]", "item", "key_iron", out error), error);
        Assert.True(form.Add("", "var", out error), error);
        Assert.True(form.SetParameter("[1]", "name", "gate_open", out error), error);
        Assert.True(form.SetParameter("[1]", "eq", "1", out error), error);
        Assert.True(form.Remove("[1]", out error), error);

        Assert.Equal(6, record.History.Position);
        var written = (JsonArray)record.Get("nodes[0].options[0].conditions")!;
        Assert.Equal("has_item", (string?)written[0]!["condition"]);
        Assert.Equal("key_iron", (string?)written[0]!["item"]);
        Assert.Empty(form.Validate());

        Assert.True(record.Save());
        var dialogue = editor.Engine.Records.Get<DialogueRecord>(new RecordId("sandbox", "guard"));
        var condition = Assert.Single(dialogue.Nodes[0].Options[0].Conditions);
        Assert.Equal("HasItemCondition", condition.GetType().Name);

        // Undo is the record's own.
        Assert.True(record.Undo());
        Assert.Equal(2, form.Rows().Count);
    }

    [Fact]
    public void ShorthandAndBareIdsReadAsTheLongFormAndProblemsReachTheProblemsPanel()
    {
        using var editor = Open();
        Assert.True(editor.Records.Open("dialogue", new RecordId("sandbox", "guard")));
        var record = editor.Records.Current!;
        // What a person may have typed: a shorthand, a bare id, and a mistake in each kind.
        record.Set("nodes[0].options[0].conditions", JsonNode.Parse("""
            [ { "has_item": "key_gold" }, "is_alive", { "has_item": "key_missing", "cuont": 2 }, { "condition": "nope" } ]
            """));
        Assert.True(editor.Forms.OpenRecord("nodes[0].options[0].conditions", out var error), error);
        var form = editor.Forms.Current!;

        var rows = form.Rows();
        Assert.Equal(new[] { "has_item", "is_alive", "has_item", "nope" }, rows.Select(r => r.Id));
        Assert.Equal("key_gold", rows[0].Settings.Single(s => s.Parameter.Name == "item").Text);

        var problems = form.Validate();
        Assert.Contains(problems, p => p.Path == "[2]" && p.Message.Contains("no item 'sandbox:key_missing'"));
        Assert.Contains(problems, p => p.Path == "[2]" && p.Message.Contains("did you mean 'count'"));
        Assert.Contains(problems, p => p.Path == "[3]" && p.Message.Contains("no condition 'nope'"));

        // Live in the problems panel, against the record, and gone once fixed.
        using var list = new ProblemList(editor.Engine, editor.Document);
        list.Add(editor.Forms);
        Assert.Contains(list.Problems, p => p.Record == record.Id && p.Message.Contains("key_missing"));
        Assert.True(form.Remove("[3]", out error), error);
        Assert.True(form.SetParameter("[2]", "item", "key_iron", out error), error);
        Assert.True(form.SetParameter("[2]", "cuont", "", out error), error);   // a mistyped setting is cleared like any other
        Assert.Empty(form.Validate());
        Assert.DoesNotContain(list.Problems, p => p.Record == record.Id);

        // A wire's `requires` is checked by the document, form open or not: one naming an item nothing defines
        // (as a wire read from a file may) is a problem of that placement.
        var plate = editor.Document.Find("plate")!;
        var wireForm = VocabularyForm.ForWire(editor.Document, plate, 0, out _)!;
        Assert.True(wireForm.Choose("", "has_item", out error), error);
        Assert.True(wireForm.SetParameter("", "item", "key_gold", out error), error);
        Assert.DoesNotContain(list.Problems, p => p.Placement == plate);
        var missing = VocabularyForm.Read(editor.Engine, wireForm.Vocabulary, false, JsonNode.Parse("""{ "has_item": "key_missing" }""")!, "sandbox", out error);
        Assert.NotNull(missing);
        var wires = plate.Outputs.Select(EditDocument.Copy).ToList();
        VocabularyForm.RequiresField.Set!(wires[0], missing);
        editor.Document.Execute(new SetOutputs(editor.Document, plate, wires));
        Assert.Contains(list.Problems, p => p.Placement == plate && p.Message.Contains("no item 'sandbox:key_missing'"));
        Assert.Contains("no item", Assert.Single(wireForm.Validate()).Message);
    }

    [Fact]
    public void TheConsolePressesEveryButtonOfTheForm()
    {
        using var editor = Open();
        var console = editor.Console;
        Assert.True(console.Execute("ed_vocab condition has_item"));
        Assert.True(console.Execute("ed_vocab_wire plate 1"));
        Assert.True(console.Execute("ed_vocab_pick . any"));
        Assert.True(console.Execute("ed_vocab_add of has_item"));
        Assert.True(console.Execute("ed_vocab_param of[0] item key_iron"));
        Assert.True(console.Execute("ed_vocab_add of has_item"));
        Assert.True(console.Execute("ed_vocab_param of[1] item key_gold"));
        Assert.True(console.Execute("ed_vocab_show"));
        var requires = editor.Document.Find("plate")!.Outputs[0].Requires!;
        Assert.Equal("AnyCondition", requires.GetType().Name);
        Assert.Equal(5, editor.Document.History.Position);

        Assert.True(console.Execute("ed_vocab_remove of[0]"));
        Assert.Single(editor.Forms.Current!.Rows(), r => r.Depth == 1);
        Assert.True(console.Execute("ed_vocab_close"));
        Assert.Null(editor.Forms.Current);

        // The dialogue: open the record, then the form on its option's conditions.
        Assert.True(console.Execute("ed_rec_open dialogue guard"));
        Assert.True(console.Execute("ed_vocab_rec nodes[0].options[0].conditions"));
        Assert.True(console.Execute("ed_vocab_add . has_item"));
        Assert.True(console.Execute("ed_vocab_param [0] item key_gold"));
        Assert.Equal("key_gold", (string?)editor.Records.Current!.Get("nodes[0].options[0].conditions[0].item"));

        // The form follows the record browser: closing the record closes it.
        editor.Records.Close();
        Assert.Null(editor.Forms.Current);
    }
}
