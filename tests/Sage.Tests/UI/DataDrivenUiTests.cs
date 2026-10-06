#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Screens made of records with no C# (issue #347): binding paths with indexers and that reach the
// view-model from inside a row (`$parent.`, `$root.`, `scope`), a layout including another with
// params, a button running a console command filled from its row, and the `ui_world` view-model —
// what a mod needs to add a screen with a working button and a reusable part from records alone.
public class DataDrivenUiTests
{
    public DataDrivenUiTests() { _ = TestEnv.UserRoot; }

    public sealed class Slot
    {
        public string Label = "";
        public int Count;
        public List<Slot> Inside { get; } = new();
    }

    public sealed class Grades
    {
        private readonly Dictionary<string, string> _grades = new() { ["math"] = "A" };
        public string this[string subject] => _grades.TryGetValue(subject, out var g) ? g : "-";
    }

    public sealed class Model : IViewModel
    {
        public string Title = "Pack";
        public float Volume = 0.5f;
        public List<Slot> Items { get; } = new()
        {
            new Slot { Label = "sword", Count = 1, Inside = { new Slot { Label = "gem" } } },
            new Slot { Label = "bread", Count = 2 },
        };
        public Dictionary<string, float> Stats { get; } = new() { ["health"] = 40f, ["volume"] = 0.25f };
        public int[] Slots = { 7, 8, 9 };
        public Grades Grades { get; } = new();
        public Slot Hand { get; set; } = new() { Label = "torch", Count = 3 };
        public int Refreshes;

        public void Refresh(in UiBindContext context) => Refreshes++;
    }

    internal const string Records = """
    [
      { "type": "ui_style", "id": "panel", "padding": 2 },
      { "type": "ui_style", "id": "card", "padding": 4 },

      { "type": "ui_layout", "id": "paths",
        "nodes": {
          "second":  { "widget": "label", "bind": "items[1].label" },
          "health":  { "widget": "label", "bind": "stats[health]" },
          "slot":    { "widget": "label", "bind": "slots[2]" },
          "grade":   { "widget": "label", "bind": "grades[math]" },
          "past":    { "widget": "label", "bind": "items[5].label" },
          "nokey":   { "widget": "label", "bind": "stats[mana]" },
          "nested":  { "widget": "label", "bind": "items[0].inside[0].label" },
          "volume":  { "widget": "slider", "bind": "stats['volume']", "min": 0, "max": 1 },
          "rows":    { "widget": "stack", "bind": "items" },
          "row":     { "widget": "stack", "parent": "rows" },
          "name":    { "widget": "label", "parent": "row", "text": "{n} in {pack}", "args": { "n": "label", "pack": "$parent.title" } },
          "inner":   { "widget": "stack", "parent": "row", "bind": "inside" },
          "gem":     { "widget": "label", "parent": "inner", "text": "{g}/{of}/{pack}", "args": { "g": "label", "of": "$parent.label", "pack": "$root.title" } },
          "hand":    { "widget": "stack", "scope": "hand" },
          "what":    { "widget": "label", "parent": "hand", "text": "{n} x{c} ({t})", "args": { "n": "label", "c": "count", "t": "$parent.title" } }
        } },
      { "type": "screen", "id": "paths", "layout": "paths", "viewModel": "dd_model" },

      { "type": "ui_layout", "id": "card", "style": "card",
        "params": { "caption": "Untitled" },
        "nodes": {
          "title": { "widget": "label", "text": "{$caption}" },
          "value": { "widget": "label", "bind": "{$path}" },
          "ok":    { "widget": "button", "text": "OK", "focusDown": "title" }
        } },
      { "type": "ui_layout", "id": "page", "style": "panel",
        "nodes": {
          "window": { "widget": "stack" },
          "first":  { "include": "card", "parent": "window", "params": { "caption": "Health", "path": "stats[health]" },
                      "actions": [ { "set_var": "pressed", "value": 1 } ] },
          "second": { "include": "card", "parent": "window", "params": { "path": "title" } },
          "after":  { "widget": "label", "parent": "first", "text": "after" }
        } },
      { "type": "screen", "id": "page", "layout": "page", "viewModel": "dd_model" },

      { "type": "ui_layout", "id": "commands",
        "nodes": {
          "rows": { "widget": "stack", "bind": "items" },
          "row":  { "widget": "button", "parent": "rows", "bind": "label",
                    "actions": [ { "command": "dd_echo {label} {count} {$root.title}" } ] },
          "odd":  { "widget": "button", "text": "odd", "actions": [ { "command": "dd_echo {nothing}" } ] },
          "cheat": { "widget": "button", "text": "cheat", "actions": [ { "command": "dd_cheat" } ] }
        } },
      { "type": "screen", "id": "commands", "layout": "commands", "viewModel": "dd_model" }
    ]
    """;

    internal static HeadlessApp Boot(string records = Records)
    {
        var fixture = new MountFixture();
        fixture.Write("ddtest", "data/ui.json", records);
        fixture.Mount("ddtest", "ddtest");
        return HeadlessApp.Bare().With(new UiModule()).Mount(fixture)
            .OnRegistered(app => app.Engine.Vocabularies.Of<IViewModel>().Register("dd_model", typeof(Model), () => new Model()))
            .Boot("dd");
    }

    private static UiScreen Open(HeadlessApp app, string name) =>
        app.World.Resources.Get<UiScreens>().OpenScreen(new RecordId("ddtest", name), new UiBindContext(app.World));

    private static string Text(UiScreen screen, string name) => screen.View.Find<Label>(name)!.Text;

    // A path's steps may be indexed: a list or an array by position, a dictionary by key (quoted or not),
    // a type's own indexer; past the end or a missing key reads as nothing. A slider bound through a
    // dictionary's key writes back into it.
    [Fact]
    public void BindingPathsIndexListsArraysDictionariesAndIndexers()
    {
        using var app = Boot();
        var screen = Open(app, "paths");
        var model = (Model)screen.ViewModel!;

        Assert.Equal("bread", Text(screen, "second"));
        Assert.Equal("40", Text(screen, "health"));
        Assert.Equal("9", Text(screen, "slot"));
        Assert.Equal("A", Text(screen, "grade"));
        Assert.Equal("", Text(screen, "past"));
        Assert.Equal("", Text(screen, "nokey"));
        Assert.Equal("gem", Text(screen, "nested"));
        var volume = screen.View.Find<Slider>("volume")!;
        Assert.Equal(0.25f, volume.Value);

        model.Items.Add(new Slot { Label = "a" });
        model.Items.Add(new Slot { Label = "b" });
        model.Items.Add(new Slot { Label = "c" });
        model.Items.Add(new Slot { Label = "rope" });
        model.Stats["mana"] = 3f;
        model.Slots[2] = 1;
        screen.Refresh();
        Assert.Equal("rope", Text(screen, "past"));
        Assert.Equal("3", Text(screen, "nokey"));
        Assert.Equal("1", Text(screen, "slot"));

        volume.SetValue(0.75f);                                  // the player moves it: written to stats[volume]
        Assert.Equal(0.75f, model.Stats["volume"], 3);
    }

    // Inside a list's rows a path reads its row; `$parent.` reaches the scope around it (the view-model
    // round a row, the row round a nested row) and `$root.` the view-model from however deep. A `scope`
    // node reads from where its path leads, which is its Data.
    [Fact]
    public void RowsAndScopesReachTheViewModelThroughParentAndRoot()
    {
        using var app = Boot();
        var screen = Open(app, "paths");
        var model = (Model)screen.ViewModel!;

        var rows = screen.View.Find<Stack>("rows")!;
        Assert.Equal(2, rows.ChildCount);
        Assert.Equal("sword in Pack", ((Label)rows.Child(0).Find("name")!).Text);
        Assert.Equal("bread in Pack", ((Label)rows.Child(1).Find("name")!).Text);
        Assert.Equal("gem/sword/Pack", ((Label)rows.Child(0).Find("gem")!).Text);
        Assert.Equal("torch x3 (Pack)", Text(screen, "what"));
        Assert.Same(model.Hand, screen.View.Find("hand")!.Data);

        model.Title = "Bag";
        model.Hand = new Slot { Label = "lamp", Count = 1 };
        screen.Refresh();
        Assert.Equal("sword in Bag", ((Label)rows.Child(0).Find("name")!).Text);
        Assert.Equal("gem/sword/Bag", ((Label)rows.Child(0).Find("gem")!).Text);
        Assert.Equal("lamp x1 (Bag)", Text(screen, "what"));
    }

    // A node that includes a layout has its nodes inside it, before its own children, named 'node/inner'
    // (focus neighbours too), in the included layout's style, with `{$name}` put in from the node's params
    // or the layout's defaults. Pressing an included button runs the including node's actions.
    [Fact]
    public void ALayoutIncludesAnotherWithItsParamsAndActions()
    {
        using var app = Boot();
        var screen = Open(app, "page");
        var view = screen.View;

        var first = view.Find<Stack>("first")!;                                  // an include with no widget: a stack
        Assert.Equal(new[] { "first/title", "first/value", "first/ok", "after" }, Enumerable.Range(0, first.ChildCount).Select(i => first.Child(i).Name));
        Assert.Equal("Health", Text(screen, "first/title"));
        Assert.Equal("40", Text(screen, "first/value"));
        Assert.Equal("Untitled", Text(screen, "second/title"));                   // the layout's default
        Assert.Equal("Pack", Text(screen, "second/value"));
        Assert.Equal("ddtest:card", view.Find("first/title")!.Style);
        Assert.Equal("first/title", view.Find("first/ok")!.FocusDown);

        Assert.Equal(0, Vars.ValueOf(app.World, "pressed"));
        screen.Handle(new UiResult { Activated = view.Find("first/ok") });
        Assert.Equal(1, Vars.ValueOf(app.World, "pressed"));
    }

    // `command` runs a console line as if typed: from a button its {placeholders} are read from the
    // pressed row (`$root.` the view-model), a value with a space quoted as one argument; one that reads
    // nothing is left as written; a cheat still needs sv_cheats.
    [Fact]
    public void AButtonRunsAConsoleCommandFilledFromItsRow()
    {
        using var capture = new CaptureSink();
        using var app = Boot();
        var heard = new List<string[]>();
        app.Engine.CVars.RegisterCommand("dd_echo", CVarFlags.None, "test", a => heard.Add(a.Args.ToArray()));
        bool cheated = false;
        app.Engine.CVars.RegisterCommand("dd_cheat", CVarFlags.Cheat, "test", _ => cheated = true);
        var screen = Open(app, "commands");
        var model = (Model)screen.ViewModel!;
        model.Title = "My pack; quit";
        screen.Refresh();

        var rows = screen.View.Find<Stack>("rows")!;
        Assert.True(screen.Handle(new UiResult { Activated = rows.Child(1) }));
        Assert.Equal(new[] { "bread", "2", "My pack; quit" }, Assert.Single(heard));

        screen.Handle(new UiResult { Activated = screen.View.Find("odd") });
        Assert.Equal(new[] { "{nothing}" }, heard[^1]);
        Assert.Contains(capture.Entries, e => e.Message.Contains("{nothing} reads nothing"));

        screen.Handle(new UiResult { Activated = screen.View.Find("cheat") });
        Assert.False(cheated);
        Assert.Contains(capture.Entries, e => e.Message.Contains("dd_cheat is a cheat"));
    }

    // What the load checks: an include loop, a param the included layout has not got (with the nearest),
    // a `{$name}` nobody gives, an include on a leaf, `$parent.` with no scope round it, an index that
    // cannot be, and a path through a scope that is not there.
    [Fact]
    public void IncludeAndPathMistakesAreErrorsAtTheirLines()
    {
        using var capture = new CaptureSink();
        using var app = Boot("""
            [
              { "type": "ui_layout", "id": "a", "nodes": { "b": { "include": "b" } } },
              { "type": "ui_layout", "id": "b", "nodes": { "a": { "include": "a" } } },
              { "type": "ui_layout", "id": "part", "nodes": { "t": { "widget": "label", "text": "{$caption} {$other}" } } },
              { "type": "ui_layout", "id": "user",
                "nodes": { "p": { "include": "part", "params": { "captoin": "x", "other": "y" } },
                           "leaf": { "widget": "label", "include": "part", "params": { "caption": "x", "other": "y" } },
                           "up": { "widget": "label", "bind": "$parent.title" },
                           "ix": { "widget": "label", "bind": "items[first].label" },
                           "bad": { "widget": "label", "bind": "items[0" } } },
              { "type": "screen", "id": "user", "layout": "user", "viewModel": "dd_model" }
            ]
            """);

        var messages = capture.Entries.Select(e => e.Message).Where(m => m.StartsWith("ddtest:", StringComparison.Ordinal)).ToList();
        void Has(string where, string what) =>
            Assert.True(messages.Any(m => m.StartsWith("ddtest:data/ui.json:" + where, StringComparison.Ordinal) && m.Contains(what)),
                        $"no message at {where} saying \"{what}\" in:\n{string.Join("\n", messages)}");

        Has("2:", "node 'b' includes ddtest:b, which includes ddtest:a again");
        Has("6:", "gives ddtest:part a param 'captoin' it has not got; did you mean 'caption'?");
        Has("6:", "which says {$caption}, and neither its params nor the layout's own give 'caption'");
        Has("7:", "node 'leaf' is a label, which holds no children, so it cannot include a layout");
        Has("10:", "'items[0' has a '[' with no ']'");
        Has("11:", "node 'up' binds text to '$parent.title': there is no scope around this one");
        Has("11:", "node 'ix' binds text to 'items[first].label': 'first' is not a position in List<…>");
    }

    // The acceptance test of issue #347: a mod of records alone — no C#, no view-model of its own — adds
    // a screen over `ui_world` with a part it includes twice and buttons that change a world variable
    // and run a console command; driven through the world's widget stack with a press, the buttons work
    // and the screen shows what they did. `sage validate` finds nothing wrong with it.
    [Fact]
    public void AModAddsAScreenWithAWorkingButtonAndAReusablePartFromRecordsAlone()
    {
        const string mod = """
        [
          { "type": "ui_layout", "id": "counter", "params": { "label": "Count" },
            "nodes": {
              "row":  { "widget": "stack", "direction": "Row", "spacing": 4 },
              "name": { "widget": "label", "parent": "row", "text": "{$label}: {n}", "args": { "n": "vars[{$var}]" } },
              "more": { "widget": "button", "parent": "row", "text": "+", "actions": [ { "add_var": "{$var}" } ] }
            } },
          { "type": "ui_layout", "id": "tally",
            "nodes": {
              "window": { "widget": "stack", "anchors": "center" },
              "bells":  { "include": "counter", "parent": "window", "params": { "label": "Bells", "var": "bells" } },
              "books":  { "include": "counter", "parent": "window", "params": { "var": "books" } },
              "reset":  { "widget": "button", "parent": "window", "text": "Reset",
                          "actions": [ { "set_var": "bells", "value": 0 }, { "command": "set_var_test {$root.vars[books]}" } ] }
            } },
          { "type": "screen", "id": "tally", "layout": "tally", "viewModel": "ui_world" }
        ]
        """;
        var fixture = new MountFixture();
        fixture.Write("tallymod", "data/ui.json", mod);
        fixture.Mount("tallymod", "tallymod");
        using var app = HeadlessApp.Bare().With(new UiModule()).Mount(fixture).Boot("tally");
        var world = app.World;
        var said = new List<string>();
        app.Engine.CVars.RegisterCommand("set_var_test", CVarFlags.None, "test", a => said.Add(a[0]));

        var stack = world.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        stack.OpenSeconds = stack.CloseSeconds = 0f;
        var layer = stack.Open(new RecordId("tallymod", "tally"), new UiBindContext(world));
        var screen = layer.Screen!;
        Assert.Equal("Bells: 0", Text(screen, "bells/name"));
        Assert.Equal("Count: 0", Text(screen, "books/name"));

        void Press(string name)
        {
            Assert.True(layer.Root.Focus(screen.View.Find(name)));
            stack.Update(UiInput.Press);
            stack.Update(default);
        }
        Press("bells/more");
        Press("bells/more");
        Press("books/more");
        Assert.Equal(2, Vars.ValueOf(world, "bells"));
        Assert.Equal("Bells: 2", Text(screen, "bells/name"));
        Assert.Equal("Count: 1", Text(screen, "books/name"));

        Press("reset");
        Assert.Equal(0, Vars.ValueOf(world, "bells"));
        Assert.Equal("Bells: 0", Text(screen, "bells/name"));
        Assert.Equal(new[] { "1" }, said);

        var report = ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Hello"),
            EngineContentDirectory = Path.Combine(TestEnv.FolderAbove("Sage.sln"), "engine_content"),
            AvailablePlugins = BasePlugins.All(),
            GameModule = new Hello.HelloModule(),
            Mounts = new[] { (fixture.Dir("tallymod"), "tallymod") },
        });
        Assert.True(report.Ok, string.Join("\n", report.Errors));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("tallymod", StringComparison.Ordinal));
    }
}

// Indexed and scoped paths — a dictionary's key, a list's position, `$parent.` from a row, a `scope` —
// are read every frame without allocating once warm, like plain ones (02 §4.6).
[Collection(MeasurementsCollection.Name)]
public class DataDrivenUiAllocationTests
{
    public DataDrivenUiAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void IndexedAndScopedBindingsRefreshWithoutAllocating()
    {
        using var app = DataDrivenUiTests.Boot();
        var root = new UiRoot(new MonospaceTextMeasure(8f, 10f));
        var screen = app.World.Resources.Get<UiScreens>().OpenScreen(new RecordId("ddtest", "paths"), new UiBindContext(app.World));
        root.Content.Add(screen.Root);
        var model = (DataDrivenUiTests.Model)screen.ViewModel!;
        var spare = model.Items[1];

        int frame = 0;
        void Step()
        {
            model.Stats["health"] = 40f;                          // read every frame, by key and by position
            model.Slots[2] = 9;
            model.Hand.Count = 3;
            if (frame % 2 == 0) model.Items.RemoveAt(1); else model.Items.Add(spare);
            screen.Refresh();
            root.Layout();
            frame++;
        }
        for (int i = 0; i < 8; i++) Step();

        AllocationProbe.AssertNone(500, Step);
        Assert.True(model.Refreshes > 500);
    }
}
