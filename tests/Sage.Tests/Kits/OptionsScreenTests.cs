#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;
using Sage.UI;
using Xunit;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The options screen (issue #339): the kit's `rpg:options` over Sage.UI's OptionsView, a page of rows
// per tab, each a `ui_option` record over a cvar. What the player changes is staged until Apply writes
// it to the cvars — so it takes effect as a console `set` would — and an archived cvar is in config.cfg
// at shutdown, so it survives a restart. Headless, the client's cvars (sound, video, mouse) are the
// test's own, registered as the client registers them.
public class OptionsScreenTests
{
    public OptionsScreenTests() { _ = TestEnv.UserRoot; }

    private static string Game()
    {
        string dir = TestEnv.NewTempDir();
        string content = Path.Combine(dir, "content");
        Directory.CreateDirectory(Path.Combine(content, "data"));
        Directory.CreateDirectory(Path.Combine(content, "strings", "fr"));
        Directory.CreateDirectory(Path.Combine(content, "strings", "en"));
        File.WriteAllText(Path.Combine(dir, "game.json"), """{ "name": "Options test", "id": "opttest", "mounts": ["content"], "version": "1.0.0" }""");
        // A second language: offered by the language option, which takes its list from the mounts.
        File.WriteAllText(Path.Combine(content, "strings", "fr", "rpg.json"), """{ "options": { "title": "Réglages" } }""");
        File.WriteAllText(Path.Combine(content, "strings", "en", "lang.json"), """{ "fr": "French" }""");
        return dir;
    }

    private static HeadlessApp Boot(string game, string? config = null)
    {
        var builder = HeadlessApp.ForGame(game).With(new RpgKitModule()).OnRegistered(app =>
        {
            // As the client and the host register them (ClientModule, HostCVars).
            var cvars = app.Engine.CVars;
            cvars.Register("snd_volume", 1f, CVarFlags.Archive, "Volume, 0..1.", 0f, 1f);
            cvars.Register("snd_music", 1f, CVarFlags.Archive, "Volume, 0..1.", 0f, 1f);
            cvars.Register("r_vsync", true, CVarFlags.Archive, "Wait for the display's vertical sync.");
            cvars.Register("vid_width", 800, CVarFlags.Archive, "Window width in pixels.", 320, 7680);
            cvars.Register("vid_height", 410, CVarFlags.Archive, "Window height in pixels.", 200, 4320);
            cvars.Register("m_sensitivity", 1f, CVarFlags.Archive, "Mouse look speed multiplier.", 0.01f, 20f);
        });
        if (config != null) builder = builder.WithConfig(config);
        return builder.Boot();
    }

    private static (UiScreenStack Stack, UiLayer Layer, OptionsView View) Open(HeadlessApp app)
    {
        var world = app.World;
        var stack = world.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        stack.OpenSeconds = stack.CloseSeconds = 0f;
        var layer = stack.Open(RpgKitModule.OptionsScreen, new UiBindContext(world));
        stack.Update(UiInput.Wait(0f));
        return (stack, layer, Assert.IsType<OptionsView>(layer.Screen!.ViewModel));
    }

    private static IEnumerable<Widget> All(Widget root)
    {
        yield return root;
        for (int i = 0; i < root.ChildCount; i++)
            foreach (var w in All(root.Child(i))) yield return w;
    }

    // The widget `node` of the row for option `id`.
    private static T Widget<T>(UiLayer layer, OptionsView view, string id, string node) where T : Widget
    {
        var row = view.Find(id)!;
        return (T)All(layer.Content).Single(w => w.Name != null && w.Name.EndsWith(node) && ReferenceEquals(UiScreen.RowOf(w), row));
    }

    private static void ShowPage(UiScreenStack stack, UiLayer layer, string page)
    {
        var tabs = layer.Content.Find<Tabs>("pages")!;
        for (int i = 0; i < tabs.PageCount; i++)
            if (tabs.Page(i).Name == page) tabs.Choose(i);
        stack.Update(UiInput.Wait(0f));
    }

    private static void Focus(UiScreenStack stack, UiLayer layer, Widget widget)
    {
        Assert.True(layer.Root.Focus(widget), $"{widget.Name} takes focus");
    }

    private static void Press(UiScreenStack stack, UiLayer layer, string button)
    {
        Focus(stack, layer, layer.Content.Find(button)!);
        stack.Update(UiInput.Press);
        stack.Update(UiInput.Wait(0f));
    }

    private static string Cvar(HeadlessApp app, string name) => app.Engine.CVars.Find(name)!.ValueString;

    [Fact]
    public void EachPageShowsItsOptionsAndApplyWritesWhatThePlayerChangedToTheCvars()
    {
        using var app = Boot(Game());
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", app.Records.LoadErrors));
        var (stack, layer, view) = Open(app);

        // Four tabs, as the layout says; each page the options whose cvars this host has.
        var tabs = layer.Content.Find<Tabs>("pages")!;
        Assert.Equal(new[] { "Graphics", "Audio", "Controls", "Gameplay" }, Enumerable.Range(0, tabs.PageCount).Select(i => tabs.Tab(i).Text));
        // No fullscreen, frame cap, quality or render scale: this host has none of their cvars.
        Assert.Equal(new[] { "resolution", "vsync" }, view.Graphics.Select(r => r.Id.Name));
        Assert.Equal(new[] { "master_volume", "music_volume" }, view.Audio.Select(r => r.Id.Name));
        Assert.Equal(new[] { "sensitivity" }, view.Controls.Select(r => r.Id.Name));
        Assert.Equal(new[] { "fov", "language", "autosave" }, view.Gameplay.Select(r => r.Id.Name));
        Assert.False(view.HasChanges);

        // What the cvars say: a window of 800 x 410 is none of the resolutions, so "Custom".
        var resolution = view.Find("resolution")!;
        Assert.Equal("Custom", resolution.Choices[^1]);
        Assert.Equal(resolution.Choices.Count - 1, resolution.Selected);
        Assert.Equal("100%", view.Find("master_volume")!.ValueText);
        Assert.Equal("Default", view.Find("fov")!.ValueText);   // fov 0: the camera's own
        Assert.Equal(new[] { "English", "French" }, view.Find("language")!.Choices);

        // Graphics: the next resolution along, vsync off.
        ShowPage(stack, layer, "graphics");
        Focus(stack, layer, Widget<Dropdown>(layer, view, "resolution", "choice"));
        stack.Update(UiInput.Nav(UiNavigation.Right));
        Assert.Equal("1280 x 720", Widget<Dropdown>(layer, view, "resolution", "choice").Text);
        Focus(stack, layer, Widget<Checkbox>(layer, view, "vsync", "toggle"));
        stack.Update(UiInput.Press);

        // Audio: master down a step (0.05).
        ShowPage(stack, layer, "audio");
        Focus(stack, layer, Widget<Slider>(layer, view, "master_volume", "slider"));
        stack.Update(UiInput.Nav(UiNavigation.Left));
        stack.Update(UiInput.Wait(0f));
        Assert.Equal("95%", Widget<Label>(layer, view, "master_volume", "value").Text);

        // Staged, not applied: the cvars are as they were, and each changed row is marked.
        Assert.True(view.HasChanges);
        Assert.True(view.Find("resolution")!.Dirty && view.Find("vsync")!.Dirty && view.Find("master_volume")!.Dirty);
        Assert.True(Widget<Label>(layer, view, "master_volume", "dirty").Visible);
        Assert.Equal("1", Cvar(app, "r_vsync"));
        Assert.Equal("1", Cvar(app, "snd_volume"));
        Assert.True(layer.Content.Find<Button>("apply")!.Enabled);

        Press(stack, layer, "apply");
        Assert.Equal("1280", Cvar(app, "vid_width"));
        Assert.Equal("720", Cvar(app, "vid_height"));
        Assert.Equal("0", Cvar(app, "r_vsync"));
        Assert.Equal("0.95", Cvar(app, "snd_volume"));
        Assert.False(view.HasChanges);
        Assert.Equal("Applied.", layer.Content.Find<Label>("message")!.Text);
        Assert.False(layer.Content.Find<Button>("apply")!.Enabled);
        Assert.Equal(0, view.Find("resolution")!.Selected);   // a resolution now, not "Custom"
    }

    // The language and the field of view are the engine's own cvars: applying them changes the running game.
    [Fact]
    public void ApplyingTheLanguageAndTheFieldOfViewChangesTheRunningGame()
    {
        using var app = Boot(Game());
        var (stack, layer, view) = Open(app);
        var player = app.World.Create(Transform.At(Vector3.Zero), "player camera");
        app.World.Add(player, Camera.Perspective(60f));
        player.AddTag<PlayerCamera>();
        var other = app.World.Create(Transform.At(Vector3.One), "security camera");
        var monitor = Camera.Perspective(60f);
        monitor.Target = "monitor";
        app.World.Add(other, monitor);

        ShowPage(stack, layer, "gameplay");
        Focus(stack, layer, Widget<Dropdown>(layer, view, "language", "choice"));
        stack.Update(UiInput.Nav(UiNavigation.Right));
        Focus(stack, layer, Widget<Slider>(layer, view, "fov", "slider"));
        for (int i = 0; i < 18; i++) stack.Update(UiInput.Nav(UiNavigation.Right));   // 0 -> 90, in steps of 5
        stack.Update(UiInput.Wait(0f));
        Assert.Equal("90", Widget<Label>(layer, view, "fov", "value").Text);
        Press(stack, layer, "apply");

        Assert.Equal("fr", Cvar(app, "lang"));
        Assert.Equal("fr", app.World.Resources.Get<Localisation>().Language);
        stack.Update(UiInput.Wait(0f));
        Assert.Equal("Réglages", stack.Top!.Content.Find<Label>("title")!.Text);   // the screen, rebuilt in French
        Assert.Equal("90", Cvar(app, "fov"));

        app.World.RunFrame(1f / 60f, 1f);
        var views = app.World.Resources.Get<CameraViews>();
        Assert.Equal(90f * System.MathF.PI / 180f, views.Main.FovY, 4);                   // the player's camera
        Assert.Equal(60f * System.MathF.PI / 180f, views[views.IndexOf("monitor")].FovY, 4);  // not anybody else's

        app.Engine.CVars.Execute("fov 0");   // the camera's own again
        app.World.RunFrame(1f / 60f, 1f);
        Assert.Equal(60f * System.MathF.PI / 180f, views.Main.FovY, 4);
    }

    [Fact]
    public void RevertPutsBackWhatTheCvarsSay_DefaultsStagesTheDefaults_AndTheConsoleShowsAtOnce()
    {
        using var app = Boot(Game());
        var (stack, layer, view) = Open(app);
        ShowPage(stack, layer, "controls");
        Focus(stack, layer, Widget<Slider>(layer, view, "sensitivity", "slider"));
        stack.Update(UiInput.Nav(UiNavigation.Right));
        Assert.True(view.HasChanges);
        Press(stack, layer, "revert");
        Assert.False(view.HasChanges);
        Assert.Equal(1f, view.Find("sensitivity")!.Value);

        // A cvar changed elsewhere (the console) shows on the next frame.
        app.Engine.CVars.Execute("m_sensitivity 2.5");
        app.Engine.CVars.Execute("snd_music 0.2");
        stack.Update(UiInput.Wait(0f));
        Assert.Equal(2.5f, view.Find("sensitivity")!.Value);
        Assert.Equal("2.50", Widget<Label>(layer, view, "sensitivity", "value").Text);
        Assert.False(view.HasChanges);

        Press(stack, layer, "defaults");
        Assert.True(view.HasChanges);
        Assert.Equal("2.5", Cvar(app, "m_sensitivity"));   // staged only
        Press(stack, layer, "apply");
        Assert.Equal("1", Cvar(app, "m_sensitivity"));
        Assert.Equal("1", Cvar(app, "snd_music"));
    }

    [Fact]
    public void BackWithChangesAsks_AndApplyingClosesTheScreen()
    {
        using var app = Boot(Game());
        var (stack, layer, view) = Open(app);
        ShowPage(stack, layer, "gameplay");
        Focus(stack, layer, Widget<Checkbox>(layer, view, "autosave", "toggle"));
        stack.Update(UiInput.Press);
        Assert.True(view.HasChanges);

        stack.Update(UiInput.Cancel);
        var dialog = stack.Top!.Dialog;
        Assert.NotNull(dialog);
        Assert.Equal("Unapplied changes", dialog!.Title.Text);
        Assert.False(layer.IsClosing);
        dialog.Confirm();
        stack.Update(UiInput.Wait(0f));
        Assert.Equal("0", Cvar(app, "save_autosave"));
        Assert.True(layer.IsClosed || layer.IsClosing);

        // Without changes, Back just closes it; discarding puts the cvars back as they were.
        var (stack2, layer2, view2) = Open(app);
        ShowPage(stack2, layer2, "gameplay");
        Focus(stack2, layer2, Widget<Checkbox>(layer2, view2, "autosave", "toggle"));
        stack2.Update(UiInput.Press);
        stack2.Update(UiInput.Cancel);
        stack2.Top!.Dialog!.Cancel();
        stack2.Update(UiInput.Wait(0f));
        Assert.Equal("0", Cvar(app, "save_autosave"));
        Assert.False(view2.HasChanges);
        Assert.True(layer2.IsClosed || layer2.IsClosing);
    }

    // The controls page opens the rebinding screen (#328) over the options screen.
    [Fact]
    public void TheControlsPageOpensTheKeyBindingsScreen()
    {
        using var app = Boot(Game());
        var (stack, layer, view) = Open(app);
        Assert.True(view.HasControls);
        ShowPage(stack, layer, "controls");
        Press(stack, layer, "keys");
        Assert.Equal(RpgKitModule.ControlsScreen, stack.Top!.Screen!.Id);
        Assert.IsType<ControlsView>(stack.Top.Screen.ViewModel);
        stack.Update(UiInput.Cancel);
        stack.Update(UiInput.Wait(0f));
        Assert.Same(layer, stack.Top);
    }

    // Applied settings are cvars like any other: archived, they are in config.cfg when the game closes
    // and read back when it starts.
    [Fact]
    public void AnAppliedSettingSurvivesARestart()
    {
        string game = Game();
        string config = Path.Combine(TestEnv.NewTempDir(), "config.cfg");
        using (var app = Boot(game, config))
        {
            var (stack, layer, view) = Open(app);
            ShowPage(stack, layer, "audio");
            Focus(stack, layer, Widget<Slider>(layer, view, "music_volume", "slider"));
            for (int i = 0; i < 4; i++) stack.Update(UiInput.Nav(UiNavigation.Left));
            ShowPage(stack, layer, "gameplay");
            Focus(stack, layer, Widget<Slider>(layer, view, "fov", "slider"));
            for (int i = 0; i < 15; i++) stack.Update(UiInput.Nav(UiNavigation.Right));
            Press(stack, layer, "apply");
        }
        Assert.Contains("snd_music \"0.8\"", File.ReadAllText(config));

        using var again = Boot(game, config);
        Assert.Equal("0.8", Cvar(again, "snd_music"));
        Assert.Equal("75", Cvar(again, "fov"));
        var (_, _, view2) = Open(again);
        Assert.Equal(0.8f, view2.Find("music_volume")!.Value, 4);
        Assert.Equal("80%", view2.Find("music_volume")!.ValueText);
        Assert.False(view2.HasChanges);
    }

    // What the load says about an option record by itself.
    [Fact]
    public void OptionMistakesAreLoadErrors()
    {
        using var app = HeadlessApp.Simulation().File("data/options.json", """
            [ { "type": "ui_option", "id": "a", "kind": "slider" },
              { "type": "ui_option", "id": "b", "kind": "slider", "cvar": "fov", "min": 5, "max": 5 },
              { "type": "ui_option", "id": "c", "kind": "choice" },
              { "type": "ui_option", "id": "d", "kind": "choice", "choices": [ { "label": "x", "value": "1" }, { "label": "y" } ] },
              { "type": "ui_option", "id": "e", "kind": "choice", "cvar": "lang", "choicesFrom": "languages" } ]
            """).Boot();
        var errors = app.Records.LoadErrors;
        Assert.Equal(5, app.Records.ErrorCount);
        Assert.Contains(errors, e => e.Contains("sage:a") && e.Contains("needs the cvar it sets"));
        Assert.Contains(errors, e => e.Contains("sage:b") && e.Contains("has to be above its min"));
        Assert.Contains(errors, e => e.Contains("sage:c") && e.Contains("needs its choices"));
        Assert.Contains(errors, e => e.Contains("sage:d") && e.Contains("names none; use `set`"));
        Assert.Contains(errors, e => e.Contains("sage:d") && e.Contains("sets nothing"));
    }
}
