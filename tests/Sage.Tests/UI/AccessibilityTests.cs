#nullable enable
using System.Linq;
using System.Numerics;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Accessibility and scaling (issue #351): the player's UI scale and text size, applied live; a style
// set (high contrast) swapped in live; the load's contrast check; subtitles and captions.
public class AccessibilityTests
{
    public AccessibilityTests() { _ = TestEnv.UserRoot; }

    private static readonly MonospaceTextMeasure Mono = WidgetLayoutTests.Mono;   // 10 × 20 a character

    private const string Records = """
    [
      { "type": "ui_style", "id": "window", "padding": 10, "background": "#202020", "textColour": "#E0E0E0" },
      { "type": "ui_style", "id": "button", "padding": 4, "background": "#303030", "textColour": "#E0E0E0",
        "states": { "focused": { "background": "#4060A0", "textColour": "#FFFFFF" } } },
      { "type": "ui_style", "id": "window_hc", "padding": 12, "background": "#000000", "textColour": "#FFFFFF" },
      { "type": "ui_style", "id": "button_hc", "padding": 6, "background": "#000000", "textColour": "#FFFF00", "border": "#FFFFFF", "borderWidth": 2,
        "states": { "focused": { "background": "#FFFFFF", "textColour": "#000000" } } },
      { "type": "ui_style_set", "id": "high_contrast", "label": "High contrast", "highContrast": true,
        "swaps": [ { "from": "window", "to": "window_hc" }, { "from": "button", "to": "button_hc" } ] },

      { "type": "ui_layout", "id": "menu", "style": "window",
        "nodes": {
          "window": { "widget": "stack", "anchors": "center", "spacing": 4, "style": "window" },
          "title":  { "widget": "label", "parent": "window", "text": "Options" },
          "ok":     { "widget": "button", "parent": "window", "text": "OK", "style": "button" }
        } },
      { "type": "screen", "id": "menu", "layout": "menu" },

      { "type": "sound", "id": "creak", "variations": ["audio/a.wav"], "caption": "door creaks", "maxDistance": 0 },
      { "type": "sound", "id": "halt",  "variations": ["audio/a.wav"], "caption": "Halt! Who goes there?", "speaker": "Guard", "bus": "Voice", "maxDistance": 0 },
      { "type": "sound", "id": "thud",  "variations": ["audio/a.wav"], "maxDistance": 0 }
    ]
    """;

    private static RecordId Id(string name) => new("uitest", name);

    private static HeadlessApp Boot(string records = Records)
    {
        var fixture = new MountFixture();
        fixture.Write("uitest", "data/ui.json", records);
        fixture.Write("uitest", "audio/a.wav", "RIFF");
        fixture.Mount("uitest", "uitest");
        return HeadlessApp.Bare().WithEngineContent().With(new UiModule()).Mount(fixture)
            .OnRegistered(a => a.Records.Register<SoundRecord>())   // the client's record type
            .Boot("ui");
    }

    // ---- Scale --------------------------------------------------------------------------------------

    // At 1.5 everything is half as big again on screen, laid out on a virtual screen two thirds the size:
    // what is centred stays centred and what is in a corner stays in it, with no rebuild.
    [Fact]
    public void UiScaleMakesEverythingBiggerAndReflowsTheLayoutLive()
    {
        var root = new UiRoot(Mono);
        root.SetViewport(new Vector2(1280f, 720f));
        var centre = root.Content.Add(new Label("centre") { Anchors = Anchors.Center });                                     // 60 × 20
        var corner = root.Content.Add(new Label("hp") { Anchors = Anchors.BottomRight, Margin = new Thickness(0f, 0f, 16f, 8f) });
        root.Layout();
        Assert.Equal(new Rect(610f, 350f, 60f, 20f), centre.Rect);
        Assert.Equal(new Rect(610f, 350f, 60f, 20f), root.ToPixels(centre.Rect));

        int version = root.Version;
        root.UiScale = 1.5f;
        Assert.NotEqual(version, root.Version);
        Assert.True(root.Layout());
        Assert.Equal(1.5f, root.Scale);
        Assert.Equal(1280f / 1.5f, root.Size.X, 3);
        Assert.Equal(480f, root.Size.Y, 3);
        Assert.Equal(new Vector2(60f, 20f), new Vector2(centre.Rect.Width, centre.Rect.Height));                         // the same in virtual units...
        var pixels = root.ToPixels(centre.Rect);
        Assert.Equal(90f, pixels.Width, 3);                                            // ...half as big again on screen
        Assert.Equal(30f, pixels.Height, 3);
        Assert.Equal(640f, pixels.X + pixels.Width / 2f, 3);                          // and still centred
        Assert.Equal(360f, pixels.Y + pixels.Height / 2f, 3);
        var hp = root.ToPixels(corner.Rect);
        Assert.Equal(1280f - 16f * 1.5f, hp.Right, 3);                                 // still in its corner
        Assert.Equal(720f - 8f * 1.5f, hp.Bottom, 3);

        // The pointer is in pixels: it finds the widget where it is drawn now.
        Assert.Same(centre, root.HitTest(new Vector2(640f, 360f)));

        root.UiScale = 10f;                                                            // clamped
        Assert.Equal(UiRoot.MaxUiScale, root.UiScale);
        root.UiScale = float.NaN;
        Assert.Equal(1f, root.UiScale);
    }

    // The text size grows text — and the boxes laid out round it — and nothing else.
    [Fact]
    public void TextScaleGrowsTextAndWhatHoldsItButNotTheRest()
    {
        var root = new UiRoot(Mono);
        var column = root.Content.Add(new Stack { Spacing = 5f });
        var label = column.Add(new Label("hello") { TextScale = 2f });                  // 100 × 40
        var button = column.Add(new Button("ok") { Padding = new Thickness(4f) });       // 28 × 28
        var bar = column.Add(new Bar { MinSize = new Vector2(80f, 10f) });
        root.Layout();
        Assert.Equal(new Vector2(100f, 40f), label.DesiredSize);
        Assert.Equal(new Vector2(28f, 28f), button.DesiredSize);

        root.TextScale = 1.5f;
        Assert.True(root.Layout());
        Assert.Equal(2f, label.TextScale);                                             // what content said is kept
        Assert.Equal(3f, label.EffectiveTextScale);
        Assert.Equal(new Vector2(150f, 60f), label.DesiredSize);
        Assert.Equal(new Vector2(30f + 8f, 30f + 8f), button.DesiredSize);
        Assert.Equal(new Vector2(80f, 10f), bar.DesiredSize);
        Assert.Equal(60f + 5f, button.Rect.Y);                                         // the column reflowed

        // The renderer draws it at the size it was measured at.
        var styles = new UiStyles();
        var plan = new UiRenderPlan();
        plan.Update(root, styles);
        var text = plan.Commands.ToArray().First(c => c.Kind == UiDrawKind.Text && c.Widget == label);
        Assert.Equal(3f, text.Size);
        Assert.Equal(150f, text.Rect.Width);
    }

    // Through the cvars, on screens already open and ones opened after.
    [Fact]
    public void TheScaleCvarsApplyToOpenScreensLive()
    {
        using var app = Boot();
        var stack = app.World.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        var layer = stack.Open(Id("menu"), new UiBindContext(app.World));
        var window = layer.Screen!.View.Find("window")!;
        layer.Root.Layout();
        var before = layer.Root.ToPixels(window.Rect);

        Assert.True(app.Engine.CVars.Execute("ui_scale 1.5"));
        Assert.Equal(1.5f, stack.UiScale);
        Assert.Equal(1.5f, layer.Root.Scale);
        layer.Root.Layout();
        var after = layer.Root.ToPixels(window.Rect);
        Assert.Equal(before.Width * 1.5f, after.Width, 2);
        Assert.Equal(before.Height * 1.5f, after.Height, 2);

        Assert.True(app.Engine.CVars.Execute("ui_text_scale 2"));
        var title = layer.Screen.View.Find<Label>("title")!;
        layer.Root.Layout();
        Assert.Equal(new Vector2(7 * 6f * 2f, 9f * 2f), title.DesiredSize);             // "Options" in the engine font's cells, doubled

        stack.Close(layer);
        stack.Update(UiInput.Wait(1f));
        var next = stack.Open(Id("menu"), new UiBindContext(app.World));
        Assert.Equal(1.5f, next.Root.UiScale);
        Assert.Equal(2f, next.Root.TextScale);
    }

    // ---- Style sets ---------------------------------------------------------------------------------

    // `ui_style_set high_contrast` swaps the styles layouts name for the set's, on what is open, and an
    // empty one puts them back.
    [Fact]
    public void AHighContrastStyleSetAppliesLive()
    {
        using var app = Boot();
        var styles = app.World.Resources.Get<UiStyles>();
        var stack = app.World.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        var layer = stack.Open(Id("menu"), new UiBindContext(app.World));
        Assert.Equal(new[] { Id("high_contrast") }, styles.Sets.ToArray());
        var ok = layer.Screen!.View.Find("ok")!;
        Assert.Equal(new Thickness(4f), ok.Padding);
        Assert.Equal(ColourJsonConverter.Pack(0xFF, 0xFF, 0xFF), styles.ColoursOf(ok).Text);                // focused
        int version = styles.Version;

        Assert.True(app.Engine.CVars.Execute("ui_style_set high_contrast"));
        Assert.Equal(Id("high_contrast"), styles.ActiveSet);
        Assert.NotEqual(version, styles.Version);                                      // a renderer's plan is rebuilt
        stack.Update(UiInput.Wait(0.1f));                                              // the screen was built again
        ok = layer.Screen.View.Find("ok")!;
        Assert.Equal("uitest:button", ok.Style);                                       // layouts name what they always did
        Assert.Equal(ColourJsonConverter.Pack(0xFF, 0xFF, 0x00), styles.Get(ok.Style).Colours(UiState.Normal).Text);
        Assert.Equal(new Thickness(6f), ok.Padding);
        Assert.Equal(2f, styles.Get("uitest:button").BorderWidth);

        var plan = new UiRenderPlan();
        plan.Update(layer.Root, styles);
        Assert.Contains(plan.Commands.ToArray(), c => c.Kind == UiDrawKind.Text && c.Widget == ok && c.Colour == ColourJsonConverter.Pack(0, 0, 0));   // focused: black on white

        Assert.True(app.Engine.CVars.Execute("ui_style_set \"\""));
        Assert.True(styles.ActiveSet.IsEmpty);
        Assert.Equal(ColourJsonConverter.Pack(0xE0, 0xE0, 0xE0), styles.Get("uitest:button").Colours(UiState.Normal).Text);
    }

    // And both together: scale 1.5 with the high-contrast set, the layout asserted at each.
    [Fact]
    public void ScaleAndHighContrastTogetherLayOutAsExpected()
    {
        using var app = Boot();
        var stack = app.World.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        var layer = stack.Open(Id("menu"), new UiBindContext(app.World));
        layer.Root.Layout();
        var window = layer.Screen!.View.Find("window")!;
        // 6×9 cells: "Options" 42×9; "OK" 12×9 + 4 padding each way = 20×17; 4 spacing; 10 window padding.
        Assert.Equal(new Vector2(42f + 20f, 9f + 4f + 17f + 20f), window.DesiredSize);

        app.Engine.CVars.Execute("ui_scale 1.5");
        app.Engine.CVars.Execute("ui_style_set high_contrast");
        stack.Update(UiInput.Wait(0.1f));
        window = layer.Screen.View.Find("window")!;
        layer.Root.Layout();
        // window_hc pads 12, button_hc 6: "OK" 24×21.
        Assert.Equal(new Vector2(42f + 24f, 9f + 4f + 21f + 24f), window.DesiredSize);
        var pixels = layer.Root.ToPixels(window.Rect);
        Assert.Equal((42f + 24f) * 1.5f, pixels.Width, 2);
        Assert.Equal(640f, pixels.X + pixels.Width / 2f, 2);
    }

    // ---- Contrast -----------------------------------------------------------------------------------

    [Fact]
    public void ContrastRatiosAreWcags()
    {
        uint black = ColourJsonConverter.Pack(0, 0, 0), white = ColourJsonConverter.Pack(255, 255, 255);
        Assert.Equal(21.0, UiContrast.Ratio(black, white), 2);
        Assert.Equal(21.0, UiContrast.Ratio(white, black), 2);
        Assert.Equal(1.0, UiContrast.Ratio(white, white), 2);
        Assert.Equal(4.48, UiContrast.Ratio(ColourJsonConverter.Pack(0x77, 0x77, 0x77), white), 2);
        uint half = UiContrast.Over(ColourJsonConverter.Pack(255, 255, 255, 128), black);   // half white over black
        Assert.InRange(half & 0xFF, 127u, 129u);
        Assert.Equal(255u, half >> 24);
    }

    // Text a player can hardly read is said at its node; a set that promises high contrast and does not
    // keep it is an error at the set. Text over nothing (a HUD over the world) is not judged.
    [Fact]
    public void TheLoadSaysWhereTextIsHardToRead()
    {
        const string records = """
        [
          { "type": "ui_style", "id": "panel", "background": "#404040", "textColour": "#606060" },
          { "type": "ui_style", "id": "good", "background": "#000000", "textColour": "#FFFFFF" },
          { "type": "ui_style", "id": "hud", "textColour": "#303030" },
          { "type": "ui_style", "id": "grey_button", "background": "#000000", "textColour": "#FFFFFF",
            "states": { "focused": { "background": "#909090", "textColour": "#A0A0A0" } } },
          { "type": "ui_style_set", "id": "claims", "highContrast": true, "swaps": [ { "from": "panel", "to": "panel" } ] },
          { "type": "ui_layout", "id": "bad", "style": "panel",
            "nodes": {
              "box":   { "widget": "stack" },
              "dim":   { "widget": "label", "parent": "box", "text": "dim" },
              "fine":  { "widget": "label", "parent": "box", "text": "fine", "style": "good" },
              "press": { "widget": "button", "parent": "box", "text": "go", "style": "grey_button" }
            } },
          { "type": "ui_layout", "id": "over_world",
            "nodes": { "note": { "widget": "label", "text": "faint", "style": "hud" } } }
        ]
        """;
        using var capture = new CaptureSink();
        using var app = Boot(records);
        var lines = capture.Entries.Select(e => (e.Level, e.Message)).Where(e => e.Message.Contains("uitest:")).ToArray();

        Assert.Contains(lines, e => e.Level == LogLevel.Warn && e.Message.Contains("node 'dim'") && e.Message.Contains("#606060 text on #404040") && e.Message.Contains("WCAG"));
        Assert.Contains(lines, e => e.Level == LogLevel.Warn && e.Message.Contains("node 'press'") && e.Message.Contains("when focused"));
        Assert.DoesNotContain(lines, e => e.Message.Contains("node 'fine'"));
        Assert.DoesNotContain(lines, e => e.Message.Contains("node 'note'"));
        Assert.Contains(lines, e => e.Level == LogLevel.Error && e.Message.Contains("ui_style_set uitest:claims is high contrast") && e.Message.Contains("node 'dim'"));
    }

    [Fact]
    public void AStyleSetSwapsEachStyleOnce()
    {
        const string records = """
        [
          { "type": "ui_style", "id": "a" }, { "type": "ui_style", "id": "b" },
          { "type": "ui_style_set", "id": "twice", "swaps": [ { "from": "a", "to": "b" }, { "from": "a", "to": "a" }, { "from": "b" } ] }
        ]
        """;
        using var app = Boot(records);
        Assert.Contains(app.Records.LoadErrors, e => e.Contains("uitest:a is swapped twice"));
        Assert.Contains(app.Records.LoadErrors, e => e.Contains("a swap needs a `from` style and a `to` style"));
    }

    // ---- Subtitles ----------------------------------------------------------------------------------

    [Fact]
    public void SubtitlesShowForAsLongAsItTakesToReadThem()
    {
        var subtitles = new Subtitles { MaxLines = 2 };
        Assert.True(subtitles.Say("Guard", "Halt!"));
        Assert.False(subtitles.Caption("door creaks"));                               // captions are off until asked for
        subtitles.ShowCaptions = true;
        Assert.True(subtitles.Caption("door creaks", 5f));
        Assert.Equal(new[] { "Guard: Halt!", "[door creaks]" }, subtitles.Lines.Select(l => Subtitles.Format(l)).ToArray());
        Assert.Equal(2f, subtitles.Lines[0].Remaining);                                // never less than MinSeconds

        int version = subtitles.Version;
        Assert.True(subtitles.Caption("door creaks", 1f));                            // the same again: no second line
        Assert.Equal(version, subtitles.Version);
        Assert.Equal(2, subtitles.Lines.Count);

        subtitles.Say("", "A narrator.");                                              // a third pushes the oldest out
        Assert.Equal(new[] { "[door creaks]", "A narrator." }, subtitles.Lines.Select(l => Subtitles.Format(l)).ToArray());

        subtitles.Advance(2.5f);
        Assert.Equal(new[] { "[door creaks]" }, subtitles.Lines.Select(l => Subtitles.Format(l)).ToArray());
        subtitles.ShowCaptions = false;                                                // turned off: what showed goes
        Assert.Empty(subtitles.Lines);
    }

    // A sound with a caption puts it up when it plays; `captions` and `subtitles` turn the two kinds on
    // and off; the stack shows them over every layer, a window opened after them included.
    [Fact]
    public void SoundsCaptionThemselvesAndTheStackShowsThemOnTop()
    {
        using var app = Boot();
        var subtitles = app.World.Resources.Get<Subtitles>();
        var stack = app.World.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        stack.OpenSeconds = 0f;

        app.World.Events.Send(new SoundRequested(Id("creak"), default, Vector3.Zero, false, 1f));
        app.World.Events.Send(new SoundRequested(Id("halt"), default, Vector3.Zero, false, 1f));
        app.World.Events.Send(new SoundRequested(Id("thud"), default, Vector3.Zero, false, 1f));
        app.World.RunFixed(1f / 60f);
        Assert.Equal(new[] { "Guard: Halt! Who goes there?" }, subtitles.Lines.Select(l => Subtitles.Format(l)).ToArray());

        Assert.True(app.Engine.CVars.Execute("captions 1"));
        app.World.Events.Send(new SoundRequested(Id("creak"), default, Vector3.Zero, false, 1f));
        app.World.RunFixed(1f / 60f);
        Assert.Equal("[door creaks]", Subtitles.Format(subtitles.Lines[^1]));

        stack.Update(UiInput.Wait(0f));
        var box = Assert.IsType<SubtitleBox>(stack.SubtitleLayer!.Content);
        Assert.True(box.Visible);
        Assert.Equal(2, box.ChildCount);
        Assert.Equal("sage:subtitles", box.Child(0).Style);
        Assert.Equal("sage:captions", box.Child(1).Style);
        Assert.Equal(2f, ((Label)box.Child(0)).TextScale);                            // the engine style's size
        Assert.False(stack.SubtitleLayer.Modal);

        var menu = stack.Open(Id("menu"), new UiBindContext(app.World));
        Assert.Same(stack.SubtitleLayer, stack.Layers[^1]);                            // still on top
        Assert.Same(menu, stack.Top);                                                  // and never the one input goes to
        stack.SubtitleLayer.Root.Layout();
        Assert.True(box.Rect.Bottom > 600f);                                           // at the bottom of the screen

        Assert.True(app.Engine.CVars.Execute("subtitles 0"));
        Assert.Equal(new[] { "[door creaks]" }, subtitles.Lines.Select(l => Subtitles.Format(l)).ToArray());
        for (int i = 0; i < 300; i++) app.World.RunFixed(1f / 60f);                     // time runs out
        stack.Update(UiInput.Wait(0f));
        Assert.Empty(subtitles.Lines);
        Assert.False(box.Visible);
    }

    // Menus play sounds while the world is paused or waits at the title: their captions still show, and
    // lines still run out on real time, so the subtitle reader never falls behind a paused world.
    [Fact]
    public void ACaptionShowsAndRunsOutWhileTheWorldIsPaused()
    {
        using var app = Boot();
        var subtitles = app.World.Resources.Get<Subtitles>();
        Assert.True(app.Engine.CVars.Execute("captions 1"));
        app.World.Paused = true;

        app.World.Events.Send(new SoundRequested(Id("creak"), default, Vector3.Zero, false, 1f));
        app.World.RunFixed(1f / 60f);
        Assert.Equal(new[] { "[door creaks]" }, subtitles.Lines.Select(l => Subtitles.Format(l)).ToArray());

        for (int i = 0; i < 300; i++) app.World.RunFixed(1f / 60f);                     // five paused seconds
        Assert.Empty(subtitles.Lines);
        Assert.True(app.World.Paused);
    }
}
