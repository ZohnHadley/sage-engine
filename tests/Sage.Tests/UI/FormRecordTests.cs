#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// A form from records (issue #340's acceptance): a settings screen with a tab for a name and a
// checkbox and a tab for a volume slider and two dropdowns, laid out by a ui_layout record and bound to
// a view-model whose fields the player's changes are written back to — no C# beyond the fields.
public class FormRecordTests
{
    public FormRecordTests() { _ = TestEnv.UserRoot; }

    internal const string Records = """
    [
      { "type": "ui_style", "id": "tab", "padding": [6, 2], "states": { "selected": { "textColour": "#FFD000" } } },
      { "type": "ui_layout", "id": "settings",
        "nodes": {
          "pages":     { "widget": "tabs", "anchors": "center", "spacing": 4, "tabStyle": "tab", "bind": "page" },
          "general":   { "widget": "stack", "parent": "pages", "title": "@uitest.settings.general" },
          "name":      { "widget": "text_field", "parent": "general", "bind": "name", "placeholder": "Your name", "maxLength": 12 },
          "subtitles": { "widget": "checkbox", "parent": "general", "text": "Subtitles", "bind": "subtitles" },
          "audio":     { "widget": "stack", "parent": "pages", "title": "Audio" },
          "volume":    { "widget": "slider", "parent": "audio", "bind": "volume", "step": 0.1, "minSize": [200, 10] },
          "quality":   { "widget": "dropdown", "parent": "audio", "bind": "quality", "bindings": { "options": "qualities" } },
          "mode":      { "widget": "dropdown", "parent": "audio", "options": ["Stereo", "Mono"], "bind": "mode" }
        } },
      { "type": "screen", "id": "settings", "layout": "settings", "viewModel": "uitest_settings" },

      { "type": "ui_layout", "id": "fixed",
        "nodes": { "level": { "widget": "slider", "bind": "fixed" }, "seen": { "widget": "checkbox", "bind": "subtitles" } } },
      { "type": "screen", "id": "fixed", "layout": "fixed", "viewModel": "uitest_settings" }
    ]
    """;

    internal const string Strings = """{ "settings": { "general": "General" } }""";

    public enum SoundMode { Stereo, Mono }

    public sealed class SettingsModel : IViewModel
    {
        public int Page;
        public string Name = "";
        public bool Subtitles;
        public float Volume = 0.5f;
        public string Quality = "medium";
        public List<string> Qualities { get; } = new() { "low", "medium", "high" };
        public SoundMode Mode;
        public float Fixed => 0.5f;
        public List<string> Changes { get; } = new();

        public void Changed(Widget widget, in UiBindContext context) => Changes.Add(widget.Name!);
    }

    internal static HeadlessApp Boot() => HeadlessApp.Bare().With(new UiModule()).Mount(UiRecordTests.Content(Records, Strings))
        .OnRegistered(app => app.Engine.Vocabularies.Of<IViewModel>().Register("uitest_settings", typeof(SettingsModel), () => new SettingsModel()))
        .Boot("ui");

    private static readonly RecordId Settings = new("uitest", "settings");

    [Fact]
    public void AFormBuiltFromALayoutRecordWritesWhatThePlayerChangesBack()
    {
        using var capture = new CaptureSink();
        using var app = Boot();
        var stack = app.World.Resources.Get<UiScreenStack>();
        stack.OpenSeconds = 0f;
        stack.SetViewport(new Vector2(1280f, 720f));
        var layer = stack.Open(Settings, new UiBindContext(app.World));
        var view = layer.Screen!.View;
        var model = (SettingsModel)layer.Screen.ViewModel!;
        void Press(UiNavigation direction) => stack.Update(UiInput.Nav(direction));

        var tabs = view.Find<Tabs>("pages")!;
        Assert.Equal(2, tabs.PageCount);
        Assert.Equal("General", tabs.Tab(0).Text);                    // a page's title, localised
        Assert.Equal("uitest:tab", tabs.Tab(1).Style);
        Assert.Equal(new Thickness(6f, 2f), tabs.Tab(1).Padding);
        Assert.Same(tabs.Tab(0), layer.Root.Focused);                  // the first tab, for a gamepad
        Assert.Equal("Your name", view.Find<TextBox>("name")!.Placeholder);
        Assert.Equal(12, view.Find<TextBox>("name")!.MaxLength);
        Assert.Equal(0.1f, view.Find<Slider>("volume")!.Step);

        // General: type a name, tick a box.
        Press(UiNavigation.Down);
        Assert.Equal("name", layer.Root.Focused?.Name);
        stack.Update(UiInput.Type("Ann"));
        Assert.Equal("Ann", model.Name);
        Press(UiNavigation.Down);
        stack.Update(UiInput.Press);
        Assert.True(model.Subtitles);
        Assert.Equal(new[] { "name", "subtitles" }, model.Changes);

        // Up to the tabs and along to Audio: the page the model says is shown follows.
        Press(UiNavigation.Up);
        Press(UiNavigation.Up);
        Press(UiNavigation.Right);
        Assert.Equal(1, model.Page);
        Assert.Same(view.Find("audio"), tabs.SelectedPage);

        // Audio: a slider stepped, a dropdown bound to text over the model's own options, one bound to an enum.
        Press(UiNavigation.Down);
        Press(UiNavigation.Right);
        Assert.Equal(0.6f, model.Volume, 4);
        Press(UiNavigation.Down);
        Assert.Equal(new[] { "low", "medium", "high" }, view.Find<Dropdown>("quality")!.Options);
        Assert.Equal("medium", view.Find<Dropdown>("quality")!.Text);
        Press(UiNavigation.Right);
        Assert.Equal("high", model.Quality);
        Press(UiNavigation.Down);
        Press(UiNavigation.Right);
        Assert.Equal(SoundMode.Mono, model.Mode);
        Assert.Equal(new[] { "name", "subtitles", "pages", "volume", "quality", "mode" }, model.Changes);

        // The game changing the model shows on the next frame, and is not the player's change.
        model.Volume = 0.2f;
        model.Qualities.Add("ultra");
        stack.Update(UiInput.Wait(0f));
        Assert.Equal(0.2f, view.Find<Slider>("volume")!.Value);
        Assert.Equal(4, view.Find<Dropdown>("quality")!.Options.Count);
        Assert.Equal(6, model.Changes.Count);

        // A read-only path shows its value and warns, at load, that what the player changes is lost.
        Assert.Contains(capture.Entries, e => e.Message.Contains("node 'level' binds value to 'fixed': 'Fixed' is read-only, so what the player changes is not kept"));
        Assert.DoesNotContain(capture.Entries, e => e.Message.Contains("node 'seen'"));
    }
}

// A form, once built, refreshes without allocating while its values move (02 §4.6).
[Collection(MeasurementsCollection.Name)]
public class FormBindingAllocationTests
{
    public FormBindingAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void AFormRefreshesWithoutAllocating()
    {
        using var app = FormRecordTests.Boot();
        var root = new UiRoot(new MonospaceTextMeasure(8f, 10f));
        var screen = app.World.Resources.Get<UiScreens>().OpenScreen(new RecordId("uitest", "settings"), new UiBindContext(app.World));
        root.Content.Add(screen.Root);
        var model = (FormRecordTests.SettingsModel)screen.ViewModel!;
        string[] names = { "Ann", "Bob" };

        int frame = 0;
        void Step()
        {
            model.Volume = frame % 10 / 10f;
            model.Subtitles = frame % 2 == 0;
            model.Page = frame % 2;
            model.Quality = model.Qualities[frame % 3];
            model.Mode = (FormRecordTests.SoundMode)(frame % 2);
            model.Name = names[frame % 2];
            screen.Refresh();
            root.Layout();
            frame++;
        }
        for (int i = 0; i < 8; i++) Step();

        AllocationProbe.AssertNone(500, Step);
        Assert.Equal((frame - 1) % 2, screen.View.Find<Tabs>("pages")!.Selected);
    }
}
