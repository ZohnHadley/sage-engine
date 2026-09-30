#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Style and layout records, screens over view-models, and localisation keys (docs/design/13 "As built
// (style and layout records)", issue #96) — headless: a screen is built from records, bound to a
// view-model and laid out without a window, and changing a record re-lays it out with no C#.
public class UiRecordTests
{
    public UiRecordTests() { _ = TestEnv.UserRoot; }

    internal const string Records = """
    [
      { "type": "ui_style", "id": "panel", "padding": 6, "background": "#202020C0", "textColour": "#E0E0E0",
        "states": { "disabled": { "textColour": "#808080" } } },
      { "type": "ui_style", "id": "button", "base": "panel", "padding": [4, 2], "textScale": 2,
        "states": { "focused": { "background": "#3050A0" }, "hover": { "background": "#304060", "border": "#FFFFFF" } } },

      { "type": "ui_layout", "id": "hud", "style": "panel",
        "nodes": {
          "window": { "widget": "stack", "anchors": "center", "spacing": 2 },
          "title":  { "widget": "label", "parent": "window", "text": "@uitest.hud.title" },
          "name":   { "widget": "label", "parent": "window", "bind": "name" },
          "health": { "widget": "bar", "parent": "window", "bind": "health", "bindings": { "max": "maxHealth" }, "minSize": [100, 8] },
          "arrows": { "widget": "label", "parent": "window", "text": "@uitest.hud.arrows", "args": { "count": "arrows" } },
          "alarm":  { "widget": "label", "parent": "window", "text": "ALARM", "visibleIf": { "var": "alarm", "eq": 1 } },
          "items":  { "widget": "item_list", "parent": "window", "bind": "items" },
          "item":   { "widget": "label", "parent": "items", "bind": "label", "bindings": { "visible": "count" } },
          "ok":     { "widget": "button", "parent": "window", "text": "OK", "style": "button", "order": -1 }
        } },

      { "type": "screen", "id": "hud", "layout": "hud", "viewModel": "uitest_hud" }
    ]
    """;

    internal const string Strings = """
    {
      "hud": {
        "title": "Status",
        "arrows": { "zero": "no arrows", "one": "{count} arrow", "other": "{count} arrows" }
      }
    }
    """;

    public sealed class Row
    {
        public string Label = "";
        public int Count = 1;
    }

    public sealed class HudModel : IViewModel
    {
        public string Name = "Ann";
        public float Health = 50f;
        public float MaxHealth = 100f;
        public int Arrows = 3;
        public List<Row> Items { get; } = new() { new Row { Label = "sword" }, new Row { Label = "bread", Count = 2 } };
        public int Refreshes;

        public void Refresh(in UiBindContext context) => Refreshes++;
    }

    internal static HeadlessApp Boot(MountFixture fixture) => HeadlessApp.Bare().With(new UiModule()).Mount(fixture)
        .OnRegistered(app => app.Engine.Vocabularies.Of<IViewModel>().Register("uitest_hud", typeof(HudModel), () => new HudModel()))
        .Boot("ui");

    internal static MountFixture Content(string records = Records, string strings = Strings)
    {
        var fixture = new MountFixture();
        fixture.Write("uitest", "data/ui.json", records);
        fixture.Write("uitest", "strings/en/uitest.json", strings);
        fixture.Mount("uitest", "uitest");
        return fixture;
    }

    private static readonly RecordId Hud = new("uitest", "hud");

    [Fact]
    public void AStyleInheritsItsBaseAndWorksOutEachState()
    {
        using var app = Boot(Content());
        var styles = app.World.Resources.Get<UiStyles>();

        var button = styles.Get("uitest:button");
        Assert.Equal(new Thickness(4f, 2f), button.Padding);                                    // its own
        Assert.Equal(2f, button.TextScale);
        var normal = button.Colours(UiState.Normal);
        Assert.Equal(ColourJsonConverter.Pack(0xE0, 0xE0, 0xE0), normal.Text);                    // the base's
        Assert.Equal(ColourJsonConverter.Pack(0x20, 0x20, 0x20, 0xC0), normal.Background);
        Assert.Equal(ColourJsonConverter.Pack(0x30, 0x50, 0xA0), button.Colours(UiState.Focused).Background);
        Assert.Equal(normal.Text, button.Colours(UiState.Focused).Text);                          // what a state leaves out
        Assert.Equal(ColourJsonConverter.Pack(255, 255, 255), button.Colours(UiState.Hover).Border);
        Assert.Equal(ColourJsonConverter.Pack(0x80, 0x80, 0x80), button.Colours(UiState.Disabled).Text);   // inherited state

        Assert.Same(UiStyles.Default, styles.Get("uitest:nope"));
        Assert.Same(UiStyles.Default, styles.Get((string?)null));

        var widget = new Button("x") { Style = "uitest:button", Enabled = false };
        Assert.Equal(UiState.Disabled, UiStyles.StateOf(widget));
        Assert.Equal(ColourJsonConverter.Pack(0x80, 0x80, 0x80), styles.ColoursOf(widget).Text);
    }

    [Fact]
    public void ALayoutRecordBuildsItsTreeInOrderWithItsStyles()
    {
        using var app = Boot(Content());
        var view = app.World.Resources.Get<UiScreens>().BuildLayout(Hud);

        Assert.Equal(Anchors.Fill, view.Root.Anchors);
        var window = view.Find<Stack>("window")!;
        Assert.Same(view.Root, window.Parent);
        Assert.Equal(Anchors.Center, window.Anchors);
        Assert.Equal(2f, window.Spacing);
        Assert.Equal(new Thickness(6f), window.Padding);                    // the layout's style
        Assert.Equal("uitest:panel", window.Style);
        // Written order, except `ok`, whose order is -1; `item` is the list's row template, not a child.
        Assert.Equal(new[] { "ok", "title", "name", "health", "arrows", "alarm", "items" },
                     Enumerable.Range(0, window.ChildCount).Select(i => window.Child(i).Name));
        var ok = view.Find<Button>("ok")!;
        Assert.Equal("uitest:button", ok.Style);
        Assert.Equal(new Thickness(4f, 2f), ok.Padding);
        Assert.Equal(2f, ok.TextScale);
        Assert.Equal("Status", view.Find<Label>("title")!.Text);            // a key, resolved
        Assert.Equal(new Vector2(100f, 8f), view.Find("health")!.MinSize);
        Assert.Equal(0, view.Find<ItemList>("items")!.ChildCount);          // no rows until bound
    }

    [Fact]
    public void AScreenBindsItsViewModelAndAsksItsConditions()
    {
        using var app = Boot(Content());
        var screen = app.World.Resources.Get<UiScreens>().OpenScreen(new RecordId("uitest", "hud"), new UiBindContext(app.World));
        var model = Assert.IsType<HudModel>(screen.ViewModel);
        var view = screen.View;

        Assert.Equal(1, model.Refreshes);                                    // opening reads it once
        Assert.Equal("Ann", view.Find<Label>("name")!.Text);
        Assert.Equal(0.5f, view.Find<Bar>("health")!.Fraction);
        Assert.Equal("3 arrows", view.Find<Label>("arrows")!.Text);
        Assert.False(view.Find("alarm")!.Visible);                          // var alarm is 0
        var items = view.Find<ItemList>("items")!;
        Assert.Equal(new[] { "sword", "bread" }, Enumerable.Range(0, items.ChildCount).Select(i => ((Label)items.Child(i)).Text));
        Assert.Same(model.Items[1], items.Child(1).Data);                    // a row's widget carries its row

        model.Name = "Bo";
        model.Health = 25f;
        model.Arrows = 1;
        model.Items[0].Count = 0;
        model.Items.Add(new Row { Label = "rope" });
        Vars.Of(app.World).Set("alarm", 1);
        screen.Refresh();

        Assert.Equal(2, model.Refreshes);
        Assert.Equal("Bo", view.Find<Label>("name")!.Text);
        Assert.Equal(0.25f, view.Find<Bar>("health")!.Fraction);
        Assert.Equal("1 arrow", view.Find<Label>("arrows")!.Text);            // the plural form for 1
        Assert.True(view.Find("alarm")!.Visible);
        Assert.Equal(3, items.ChildCount);
        Assert.False(items.Child(0).Visible);                                // bound visibility: count 0
        Assert.Equal("rope", ((Label)items.Child(2)).Text);

        model.Arrows = 0;
        model.Items.RemoveRange(1, 2);
        screen.Refresh();
        Assert.Equal("no arrows", view.Find<Label>("arrows")!.Text);         // zero, when the table has it
        Assert.Equal(1, items.ChildCount);
    }

    // The acceptance test of issue #96: an open screen follows its records and string tables as they are
    // edited, with no C# — the tree is built again, put where the old one was, and laid out anew.
    [Fact]
    public void ChangingARecordReLaysOutAnOpenScreen()
    {
        var fixture = Content();
        using var app = Boot(fixture);
        var root = new UiRoot(new MonospaceTextMeasure(8f, 10f));
        var screen = app.World.Resources.Get<UiScreens>().OpenScreen(Hud, new UiBindContext(app.World));
        root.Content.Add(screen.Root);
        root.Layout();
        var before = screen.View.Find("window")!.Rect;
        Assert.Equal(640f, before.X + before.Width / 2f, 1);                 // centred

        fixture.Write("uitest", "data/ui.json", Records
            .Replace("\"anchors\": \"center\"", "\"anchors\": \"top_left\", \"margin\": [10, 20, 0, 0]")
            .Replace("\"text\": \"OK\"", "\"text\": \"@uitest.hud.ok\""));
        fixture.Write("uitest", "strings/en/uitest.json", Strings.Replace("\"title\": \"Status\"", "\"title\": \"State\", \"ok\": \"Fine\""));
        app.Records.Reload();
        root.Layout();

        Assert.Equal(2, screen.Builds);
        Assert.Same(screen.Root, root.Content.Child(0));                     // the new tree, where the old one was
        var after = screen.View.Find("window")!.Rect;
        Assert.Equal(10f, after.X);
        Assert.Equal(20f, after.Y);
        Assert.Equal("State", screen.View.Find<Label>("title")!.Text);
        Assert.Equal("Fine", screen.View.Find<Label>("ok")!.Text);
        Assert.Equal("Ann", screen.View.Find<Label>("name")!.Text);         // and bound again straight away

        screen.Close();
        Assert.Equal(0, root.Content.ChildCount);
        Assert.Empty(app.World.Resources.Get<UiScreens>().Open);
    }

    [Fact]
    public void AMissingKeyShowsTheKeyItselfAndWarnsOnce()
    {
        using var capture = new CaptureSink();
        using var app = Boot(Content());
        var text = app.World.Resources.Get<Localisation>();

        Assert.Equal("@uitest.nope", text.Text("@uitest.nope"));
        Assert.Equal("@uitest.nope", text.Text("@uitest.nope"));
        Assert.Equal("not a key", text.Text("not a key"));
        Assert.Equal("@home", text.Text("@@home"));
        Assert.Single(capture.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("no text for '@uitest.nope' in 'en'"));
    }

    [Fact]
    public void StringTablesNestFillPlaceholdersAndFallBackToEnglish()
    {
        var fixture = Content();
        fixture.Write("uitest", "strings/en/uitest/items.json", """
            { "sword": { "name": "sword", "weight": "{name} weighs {kg} kg" } }
            """);
        fixture.Write("uitest", "strings/fr/uitest.json", """
            { "hud": { "title": "État", "arrows": { "one": "{count} flèche", "other": "{count} flèches" } } }
            """);
        // A later mount's line wins, so a mod retranslates one line without copying the file.
        fixture.Write("uimod", "strings/en/uitest.json", """{ "hud": { "title": "Stats" } }""");
        fixture.Mount("uimod", "uimod");
        using var app = Boot(fixture);
        var text = app.World.Resources.Get<Localisation>();

        Assert.Equal("Stats", text.Text("@uitest.hud.title"));
        Assert.Equal("sword weighs 3.5 kg", text.Format("@uitest.items.sword.weight", ("name", "sword"), ("kg", 3.5f)));
        Assert.Equal("{name} weighs {kg} kg", text.Text("@uitest.items.sword.weight"));
        Assert.Equal("{{literal}} 2", text.Format("{{{{literal}}}} {n}", ("n", 2)));
        Assert.Equal("2 arrows", text.Text("@uitest.hud.arrows", 2));
        Assert.True(text.Has("uitest.items.sword.name"));

        app.CVars.Execute("lang fr");
        Assert.Equal("fr", text.Language);
        Assert.Equal("État", text.Text("@uitest.hud.title"));
        Assert.Equal("0 flèche", text.Text("@uitest.hud.arrows", 0));       // French has no zero form: 0 is singular
        Assert.Equal("1 flèche", text.Text("@uitest.hud.arrows", 1));
        Assert.Equal("sword", text.Text("@uitest.items.sword.name"));        // French lacks it: English
        Assert.Equal(PluralCategory.One, text.PluralOf(0));                  // French: 0 is singular

        app.CVars.Execute("lang xx");                                           // no tables at all: English
        Assert.Equal("en", text.Language);
    }

    [Fact]
    public void LayoutMistakesAreErrorsAtTheirLines()
    {
        using var capture = new CaptureSink();
        var fixture = new MountFixture();
        fixture.Write("uibad", "data/ui.json", """
            [
              { "type": "ui_layout", "id": "typo", "nodes": { "a": { "widget": "lable" } } },
              { "type": "ui_layout", "id": "tree",
                "nodes": {
                  "box":   { "widget": "box" },
                  "text":  { "widget": "label", "parent": "bx" },
                  "leaf":  { "widget": "label", "parent": "box" },
                  "under": { "widget": "label", "parent": "leaf" },
                  "odd":   { "widget": "box", "parent": "box", "bind": "rows", "bindings": { "colour": "x" } },
                  "list":  { "widget": "item_list", "parent": "box", "bind": "items" },
                  "one":   { "widget": "label", "parent": "list" },
                  "two":   { "widget": "label", "parent": "list" }
                } },
              { "type": "ui_layout", "id": "bound",
                "nodes": { "name": { "widget": "label", "bind": "nmae" }, "rows": { "widget": "stack", "bind": "items" },
                           "row": { "widget": "label", "parent": "rows", "bind": "lable" } } },
              { "type": "screen", "id": "bound", "layout": "bound", "viewModel": "uitest_hud" },
              { "type": "screen", "id": "nobody", "layout": "bound", "viewModel": "uitest_hdu" }
            ]
            """);
        fixture.Mount("uibad", "uibad");
        using var app = Boot(fixture);

        var messages = capture.Entries.Select(e => e.Message).Where(m => m.StartsWith("uibad:", StringComparison.Ordinal)).ToList();
        void Has(string where, string what) =>
            Assert.True(messages.Any(m => m.StartsWith("uibad:data/ui.json:" + where, StringComparison.Ordinal) && m.Contains(what)),
                        $"no message at {where} saying \"{what}\" in:\n{string.Join("\n", messages)}");

        Has("2:", "no widget type 'lable'; did you mean 'label'?");
        Has("6:", "node 'text' is in 'bx', which is not a node of this layout; did you mean 'box'?");
        Has("8:", "node 'under' is in 'leaf', a label, which holds no children");
        Has("9:", "a box has no main value to `bind`");
        Has("9:", "no property 'colour' to bind");
        Has("10:", "'list' binds its rows, so it holds one node");
        Has("18:", "screen uibad:nobody: no view_model 'uitest_hdu'; did you mean 'uitest_hud'?");
        // A screen checks its layout's paths against its view-model's type, and a row template's against the rows'.
        Has("17:", "screen uibad:bound: layout uibad:bound node 'name' binds text to 'nmae': HudModel has no 'nmae'; did you mean 'name'?");
        Has("17:", "node 'row' binds text to 'lable': Row has no 'lable'; did you mean 'label'?");
    }

    // `sage validate` warns about every key content names that no table has — in any record, not only
    // the UI's — at its line, and a warning does not fail it.
    [Fact]
    public void ValidateWarnsAboutMissingKeys()
    {
        var mod = TestEnv.NewTempDir();
        Directory.CreateDirectory(Path.Combine(mod, "data"));
        Directory.CreateDirectory(Path.Combine(mod, "strings", "en"));
        File.WriteAllText(Path.Combine(mod, "strings", "en", "keymod.json"), """{ "hello": "Hello" }""");
        File.WriteAllText(Path.Combine(mod, "data", "mod.json"), """
            [
              { "type": "ui_layout", "id": "greeting",
                "nodes": { "hi": { "widget": "label", "text": "@keymod.hello" }, "bye": { "widget": "label", "text": "@keymod.goodbye" } } },
              { "type": "dialogue", "id": "chat", "nodes": [ { "id": "start", "text": "@keymod.chat.start" } ] }
            ]
            """);

        var report = ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Hello"),
            EngineContentDirectory = Path.Combine(TestEnv.FolderAbove("Sage.sln"), "engine_content"),
            AvailablePlugins = BasePlugins.All(),
            GameModule = new Hello.HelloModule(),
            Mounts = new[] { (mod, "keymod") },
        });

        Assert.True(report.Ok, string.Join("\n", report.Errors));
        string name = Path.GetFileName(mod);
        Assert.Equal(2, report.Warnings.Count(w => w.Contains("no text for '@keymod.")));
        Assert.Contains(report.Warnings, w => w.StartsWith($"UI: {name}:data/mod.json:3:", StringComparison.Ordinal) && w.Contains("ui_layout keymod:greeting: no text for '@keymod.goodbye'"));
        Assert.Contains(report.Warnings, w => w.StartsWith($"UI: {name}:data/mod.json:4:", StringComparison.Ordinal) && w.Contains("dialogue keymod:chat: no text for '@keymod.chat.start'"));
    }

    // What `sage schema` writes for the UI: the widget types, and the style and layout ids content has.
    [Fact]
    public void TheWidgetTypeSchemaListsEveryWidgetType()
    {
        var shape = typeof(UiNode).Assembly.GetType("Sage.UI.WidgetTypeJsonConverter")!
            .GetCustomAttributes(typeof(SchemaShapeAttribute), false).Cast<SchemaShapeAttribute>().Single();
        var names = System.Text.Json.Nodes.JsonNode.Parse(shape.Json)!["enum"]!.AsArray().Select(n => (string)n!).ToArray();
        Assert.Equal(WidgetTypes.Names, names);
    }
}

// Once built, a screen whose view-model and world change in ways that need no new string — a bar's
// value, a condition flipping a widget's visibility, rows coming and going within what was made —
// refreshes and lays out again without allocating (02 §4.6).
[Collection(MeasurementsCollection.Name)]
public class UiBindingAllocationTests
{
    public UiBindingAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void BindingsAndConditionsRefreshWithoutAllocating()
    {
        using var app = UiRecordTests.Boot(UiRecordTests.Content());
        var root = new UiRoot(new MonospaceTextMeasure(8f, 10f));
        var screen = app.World.Resources.Get<UiScreens>().OpenScreen(new RecordId("uitest", "hud"), new UiBindContext(app.World));
        root.Content.Add(screen.Root);
        var model = (UiRecordTests.HudModel)screen.ViewModel!;
        var vars = Vars.Of(app.World);
        var spare = model.Items[1];

        int frame = 0;
        void Step()
        {
            vars.Set("alarm", frame % 2);
            model.Health = frame % 100;
            if (frame % 2 == 0) model.Items.RemoveAt(1); else model.Items.Add(spare);
            screen.Refresh();
            root.Layout();
            frame++;
        }
        for (int i = 0; i < 8; i++) Step();   // warm: readers compiled, rows made, texts worked out

        AllocationProbe.AssertNone(500, Step);
        Assert.True(model.Refreshes > 500);
    }
}
