#nullable enable
using System.IO;
using Sage.UI;
using Xunit;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Input glyphs in text (issue #352): `{action:Use}` shows what the player presses for an action on the
// device they are using, by the pad's family, following rebinds; a string table can name any glyph.
public class InputPromptTests
{
    public InputPromptTests() { _ = TestEnv.UserRoot; }

    private static string Game()
    {
        string dir = TestEnv.NewTempDir();
        string data = Path.Combine(dir, "content", "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(dir, "game.json"), """{ "name": "Prompt test", "id": "prompttest", "mounts": ["content"], "version": "1.0.0" }""");
        File.WriteAllText(Path.Combine(data, "input.json"), """
            [ { "type": "input_map", "id": "gameplay", "context": "Gameplay",
                "actions": { "Use": [ { "key": "E" }, { "gamepad": "X" } ], "Jump": [ { "key": "Space" }, { "gamepad": "A" } ],
                             "Attack": [ { "mouse": "Left" } ] } },
              { "type": "input_map", "id": "ui", "context": "UI",
                "actions": { "MenuConfirm": [ { "key": "Enter" }, { "gamepad": "A" } ] } } ]
            """);
        string en = Path.Combine(dir, "content", "strings", "en");
        Directory.CreateDirectory(en);
        File.WriteAllText(Path.Combine(en, "prompttest.json"), """
            { "use": "{action:Use}   Use", "take": "{action:Use}   Take {item}", "jump": "{action:Jump} jumps, {action:Attack} hits" }
            """);
        // The glyph registry is a string table: a translation names a key in its own language.
        string de = Path.Combine(dir, "content", "strings", "de");
        Directory.CreateDirectory(de);
        File.WriteAllText(Path.Combine(de, "input.json"), """{ "key": { "Space": "Leertaste" } }""");
        return dir;
    }

    private static HeadlessApp Boot(string game, string file) =>
        HeadlessApp.ForGame(game).WithUserInput(file).OnRegistered(app =>
        {
            app.Records.Register<InputMapRecord>();
            app.Engine.Actions.Register("MenuConfirm", ActionKind.Button);
        }).Boot();

    // Done: picking up the pad changes the prompt, to the names of that pad's family, and a rebind or a
    // change of device moves Localisation.Version so a label showing it is worked out again.
    [Fact]
    public void AnActionPlaceholderShowsTheGlyphOfTheDeviceInUse()
    {
        using var app = Boot(Game(), Path.Combine(TestEnv.NewTempDir(), "input.json"));
        var text = app.World.Resources.Get<Localisation>();
        var prompts = app.World.Resources.Get<InputPrompts>();
        var device = app.Engine.LastDevice;

        Assert.Equal("E   Use", text.Text("@prompttest.use"));
        Assert.Equal("E   Take sword", text.Format("@prompttest.take", ("item", "sword")));
        Assert.Equal("Space jumps, Left click hits", text.Text("@prompttest.jump"));
        Assert.Equal("Enter", prompts.Glyph("MenuConfirm"));                         // UI's, when gameplay has none
        Assert.Equal("Press E", text.Text("Press {action:use}"));                   // in any text, any case

        int version = text.Version;
        device.Observe(new DeviceActivity { PadButton = true });                    // the player picks up the pad
        Assert.NotEqual(version, text.Version);
        Assert.Equal("X   Use", text.Text("@prompttest.use"));
        Assert.Equal("A jumps, Left click hits", text.Text("@prompttest.jump"));    // Attack has only the mouse: it shows that

        device.SetPad(InputGlyphs.FamilyOf("PS5 Controller"));
        Assert.Equal("Square   Take sword", text.Format("@prompttest.take", ("item", "sword")));
        Assert.Equal("Cross", prompts.Glyph("MenuConfirm"));

        app.CVars.Execute("joy_glyphs xbox");                                       // the player's say beats the pad's name
        Assert.Equal("X   Use", text.Text("@prompttest.use"));
        app.CVars.Execute("joy_glyphs auto");
        Assert.Equal("Square   Use", text.Text("@prompttest.use"));

        device.Observe(new DeviceActivity { Key = true });                          // back to the keyboard
        Assert.Equal("E   Use", text.Text("@prompttest.use"));

        version = text.Version;
        app.Engine.Rebinds.Rebind(InputContext.Gameplay, "Use", new InputBinding { Key = "F" });
        Assert.NotEqual(version, text.Version);
        Assert.Equal("F   Use", text.Text("@prompttest.use"));

        version = text.Version;
        Assert.Equal("F   Use", text.Text("@prompttest.use"));
        Assert.Equal(version, text.Version);                                        // nothing moved: nothing to redo
    }

    [Fact]
    public void AStringTableNamesAGlyphAndAnUnboundActionShowsItsName()
    {
        using var capture = new CaptureSink();
        using var app = Boot(Game(), Path.Combine(TestEnv.NewTempDir(), "input.json"));
        var text = app.World.Resources.Get<Localisation>();

        Assert.Equal("Nope", text.Text("{action:Nope}"));
        Assert.Equal("Nope", text.Text("{action:Nope}"));
        Assert.Single(capture.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("no input binds 'Nope'"));
        Assert.Equal("sword", text.Format("{action:Use}", ("action:Use", "sword")));   // an argument of that name wins

        app.CVars.Execute("lang de");
        Assert.Equal("Leertaste jumps, Left click hits", text.Text("@prompttest.jump"));   // German's name, English's line
    }

    // The registry's built-in names and the pad family a driver's name says.
    [Fact]
    public void EachPadFamilyNamesItsButtonsAndAPadsNameSaysWhichItIs()
    {
        Assert.Equal(PadFamily.PlayStation, InputGlyphs.FamilyOf("PS4 Controller"));
        Assert.Equal(PadFamily.PlayStation, InputGlyphs.FamilyOf("Sony Interactive Entertainment Wireless Controller"));
        Assert.Equal(PadFamily.PlayStation, InputGlyphs.FamilyOf("DualSense Wireless Controller"));
        Assert.Equal(PadFamily.Xbox, InputGlyphs.FamilyOf("Xbox 360 Controller"));
        Assert.Equal(PadFamily.Xbox, InputGlyphs.FamilyOf("GPS500 Gamepad"));
        Assert.Equal(PadFamily.Xbox, InputGlyphs.FamilyOf(null));

        var rb = new InputBinding { Gamepad = "RightShoulder" };
        Assert.Equal("RB", InputGlyphs.DefaultName(rb, PadFamily.Xbox));
        Assert.Equal("R1", InputGlyphs.DefaultName(rb, PadFamily.PlayStation));
        Assert.Equal("Triangle", InputGlyphs.DefaultName(new InputBinding { Gamepad = "Y" }, PadFamily.PlayStation));
        Assert.Equal("Esc", InputGlyphs.DefaultName(new InputBinding { Key = "Escape" }, PadFamily.Xbox));
        Assert.Equal("1", InputGlyphs.DefaultName(new InputBinding { Key = "D1" }, PadFamily.Xbox));
        Assert.Equal("Right click", InputGlyphs.DefaultName(new InputBinding { Mouse = "Right" }, PadFamily.Xbox));
        Assert.Equal("playstation.RightShoulder", InputGlyphs.GlyphKey(rb, PadFamily.PlayStation));
        Assert.Equal("key.E", InputGlyphs.GlyphKey(new InputBinding { Key = "E" }, PadFamily.PlayStation));

        var device = new LastUsedDevice();
        int version = device.Version;
        device.SetPad(PadFamily.PlayStation);
        device.SetPad(PadFamily.PlayStation);
        Assert.Equal(version + 1, device.Version);
        Assert.Equal(InputDeviceKind.KeyboardMouse, device.Current);             // the family alone is not "using" the pad
    }
}
