#nullable enable
using System.Linq;
using System.Numerics;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// What screens sound like (issue #330, docs/design/11 §13): `ui_sounds` names a sound per screen action
// (move, select, open, close), a game patches the default and a screen may name its own. The stack raises
// them as `SoundRequested`; the client plays them on the sound's bus, `Ui`, which is checked here with a
// mixer exactly as the audio system drives one.
public class UiSoundTests
{
    public UiSoundTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
    [
      { "type": "ui_style", "id": "panel", "padding": 6, "background": "#202020C0", "textColour": "#E0E0E0" },
      { "type": "ui_layout", "id": "menu", "style": "panel",
        "nodes": {
          "window": { "widget": "stack", "anchors": "center", "spacing": 2 },
          "a":      { "widget": "button", "parent": "window", "text": "A", "order": -1 },
          "b":      { "widget": "button", "parent": "window", "text": "B" }
        } },
      { "type": "sound", "id": "tick",  "variations": ["audio/a.wav"], "bus": "Ui", "maxDistance": 0 },
      { "type": "sound", "id": "click", "variations": ["audio/a.wav"], "bus": "Ui", "maxDistance": 0 },
      { "type": "sound", "id": "whoosh", "variations": ["audio/a.wav"], "bus": "Ui", "maxDistance": 0 },

      { "type": "ui_sounds", "id": "sage:default_ui_sounds", "patch": true, "move": "tick", "select": "click", "open": "whoosh" },
      { "type": "ui_sounds", "id": "quiet_set", "select": "whoosh" },

      { "type": "screen", "id": "menu", "layout": "menu" },
      { "type": "screen", "id": "own", "layout": "menu", "sounds": "quiet_set" }
    ]
    """;

    private static RecordId Id(string name) => new("uitest", name);

    private static HeadlessApp Boot()
    {
        var fixture = new MountFixture();
        fixture.Write("uitest", "data/ui.json", Records);
        fixture.Write("uitest", "audio/a.wav", "RIFF");
        fixture.Mount("uitest", "uitest");
        var app = HeadlessApp.Bare().WithEngineContent().With(new UiModule()).Mount(fixture)
            .OnRegistered(a => a.Records.Register<SoundRecord>())   // the client's record type
            .Boot("ui");
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", app.Records.LoadErrors));
        return app;
    }

    private static string[] Heard(EventProbe<SoundRequested> sounds) => sounds.Since().Select(s => s.Sound.Name).ToArray();

    // Opening, moving focus, confirming and closing each raise their sound; a pointer sliding about does not.
    [Xunit.Fact]
    public void ScreensRaiseTheirSoundsForOpenMoveSelectAndClose()
    {
        using var app = Boot();
        var stack = app.World.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        stack.OpenSeconds = stack.CloseSeconds = 0f;
        var sounds = new EventProbe<SoundRequested>(app.World);

        var layer = stack.Open(Id("menu"), new UiBindContext(app.World));
        Assert.Equal(new[] { "whoosh" }, Heard(sounds));

        stack.Update(UiInput.Nav(UiNavigation.Down));
        Assert.Equal(new[] { "tick" }, Heard(sounds));
        stack.Update(UiInput.Nav(UiNavigation.Up));
        Assert.Equal(new[] { "tick" }, Heard(sounds));
        stack.Update(UiInput.Nav(UiNavigation.Up));          // already on the first one in line: focus wraps or holds
        sounds.Since();

        stack.Update(UiInput.Press);
        Assert.Equal(new[] { "click" }, Heard(sounds));

        stack.Update(UiInput.Cancel);
        Assert.True(layer.IsClosing);
        Assert.Empty(Heard(sounds));                          // no `close` in the default set: silence, not a guess
    }

    // The screen's own set wins field by field; what it leaves empty is the game's default.
    [Xunit.Fact]
    public void AScreenNamesItsOwnSoundsAndFallsBackToTheDefaultForTheRest()
    {
        using var app = Boot();
        var stack = app.World.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        var sounds = new EventProbe<SoundRequested>(app.World);

        stack.Open(Id("own"), new UiBindContext(app.World));
        stack.Update(UiInput.Wait(1f));
        sounds.Since();

        stack.Update(UiInput.Press);
        stack.Update(UiInput.Nav(UiNavigation.Down));
        Assert.Equal(new[] { "whoosh", "tick" }, Heard(sounds));   // select is its own; move is the default's; open was raised before
    }

    // Raised sounds are 2D requests on the `Ui` bus: played by a mixer they land on that bus, so
    // `snd_ui` and nothing else turns the menus down.
    [Xunit.Fact]
    public void AScreenSoundPlaysThroughTheUiBus()
    {
        using var app = Boot();
        var stack = app.World.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        var sounds = new EventProbe<SoundRequested>(app.World);
        stack.Open(Id("menu"), new UiBindContext(app.World));

        var request = sounds.All.Single();
        Assert.False(request.Positional);
        var record = app.Records.Get<SoundRecord>(request.Sound);
        Assert.Equal(AudioBus.Ui, record.Bus);

        var mixer = new AudioMixer();
        mixer.SetListener(Vector3.Zero, Quaternion.Identity);
        mixer.SetBusVolume(AudioBus.Ui, 0.5f);
        var handle = mixer.Play(request.Sound, record, request.Point, request.Positional, volume: request.Volume <= 0f ? 1f : request.Volume);
        Assert.True(handle.IsValid);
        Assert.Equal(0.5, mixer.Find(handle)!.Gain, 2);
        mixer.SetBusVolume(AudioBus.Sfx, 0f);                    // another bus does not touch it
        mixer.Update(0.01);
        Assert.Equal(0.5, mixer.Find(handle)!.Gain, 2);
    }
}
