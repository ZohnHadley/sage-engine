#nullable enable
using System.IO;
using System.Linq;
using Xunit;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Player rebinds (issue #328; docs/design/08 §3.2, §7): `bind`, `unbind`, `bind_reset` and the model under
// the controls screen. They are record patches in `user://input.json`, layered over the game's maps and
// the mods', so a rebind survives a restart and the new key fires from the next frame (the client rebuilds
// its bindings from these same records; what an input triggers is read here with ActionsFor).
public class InputRebindTests
{
    public InputRebindTests() { _ = TestEnv.UserRoot; }

    private const string Engine = """
        [
          { "type": "input_map", "id": "gameplay", "context": "Gameplay",
            "actions": { "Jump": [ { "key": "Space" }, { "gamepad": "A" } ], "Use": [ { "key": "E" } ],
                         "Attack": [ { "mouse": "Left" } ], "Move": [ { "composite": "WASD" } ] } },
          { "type": "input_map", "id": "ui", "context": "UI",
            "actions": { "MenuUp": [ { "key": "Up" }, { "key": "W" } ], "MenuConfirm": [ { "key": "Enter" } ] } }
        ]
        """;

    // A mod's map: it adds J to Jump, in the same context.
    private const string Mod = """
        [ { "type": "input_map", "id": "extra", "context": "Gameplay", "actions": { "Jump": [ { "key": "J" } ] } } ]
        """;

    private static string NewFile() => Path.Combine(TestEnv.NewTempDir(), "input.json");

    private static HeadlessApp Boot(string? file, bool mod = false)
    {
        var builder = HeadlessApp.Bare().File("data/input.json", Engine);
        if (mod) builder.File("data/input.json", Mod, "mymod");
        if (file != null) builder.WithUserInput(file);
        return builder.OnRegistered(app =>
        {
            app.Records.Register<InputMapRecord>();
            foreach (string name in new[] { "Jump", "Use", "Attack", "MenuUp", "MenuConfirm" })
                app.Engine.Actions.Register(name, ActionKind.Button);
            app.Engine.Actions.Register("Move", ActionKind.Axis2D);
        }).Build();
    }

    private static InputBinding Key(string key) => new() { Key = key };

    private static string[] Fires(HeadlessApp app, InputContext context, string key) =>
        app.Engine.Rebinds.ActionsFor(context, Key(key)).ToArray();

    [Fact]
    public void ARebindIsSavedAndTheNewKeyFiresAfterARestart()
    {
        string file = NewFile();
        using (var app = Boot(file))
        {
            var rebinds = app.Engine.Rebinds;
            Assert.Equal(new[] { "Jump" }, Fires(app, InputContext.Gameplay, "Space"));
            Assert.Empty(Fires(app, InputContext.Gameplay, "F"));

            var result = rebinds.Rebind(InputContext.Gameplay, "Jump", Key("F"));
            Assert.Equal(RebindStatus.Applied, result.Status);
            Assert.True(File.Exists(file));

            // In force at once: records were reloaded, so the client rebuilds from them.
            Assert.Equal(new[] { "Jump" }, Fires(app, InputContext.Gameplay, "F"));
            Assert.Empty(Fires(app, InputContext.Gameplay, "Space"));
            Assert.True(rebinds.IsOverridden(InputContext.Gameplay, "Jump"));
            Assert.Equal(new[] { "F", "A" }, rebinds.Bindings(InputContext.Gameplay, "Jump").Select(b => b.Key ?? b.Gamepad).ToArray());
        }

        // The file is a record patch of the game's map plus the player's own list.
        string text = File.ReadAllText(file);
        Assert.Contains("\"patch\": true", text);
        Assert.Contains("sage:gameplay", text);
        Assert.Contains("user:rebinds_gameplay", text);

        // The next start: the same file, and the new key still fires.
        using var next = Boot(file);
        Assert.Equal(new[] { "Jump" }, Fires(next, InputContext.Gameplay, "F"));
        Assert.Empty(Fires(next, InputContext.Gameplay, "Space"));
        Assert.Equal(new[] { "Use" }, Fires(next, InputContext.Gameplay, "E"));   // everything else is the game's
    }

    [Fact]
    public void AModsBindingOfTheSameActionIsReplacedToo()
    {
        string file = NewFile();
        using var app = Boot(file, mod: true);
        Assert.Equal(new[] { "Jump" }, Fires(app, InputContext.Gameplay, "J"));
        Assert.Equal(RebindStatus.Applied, app.Engine.Rebinds.Rebind(InputContext.Gameplay, "Jump", Key("F")).Status);
        Assert.Empty(Fires(app, InputContext.Gameplay, "J"));   // the player's list is the whole list
        Assert.Equal(new[] { "Jump" }, Fires(app, InputContext.Gameplay, "F"));

        Assert.Equal(RebindStatus.Applied, app.Engine.Rebinds.Reset(InputContext.Gameplay, "Jump").Status);
        Assert.Equal(new[] { "Jump" }, Fires(app, InputContext.Gameplay, "J"));   // the mod's is back with the game's
        Assert.Equal(new[] { "Jump" }, Fires(app, InputContext.Gameplay, "Space"));
    }

    [Fact]
    public void BindAddsAnInputAndUnbindTakesItOff()
    {
        string file = NewFile();
        using var app = Boot(file);
        var rebinds = app.Engine.Rebinds;

        Assert.Equal(RebindStatus.Applied, rebinds.Bind(InputContext.Gameplay, "Use", Key("F")).Status);
        Assert.Equal(new[] { "Use" }, Fires(app, InputContext.Gameplay, "F"));
        Assert.Equal(new[] { "Use" }, Fires(app, InputContext.Gameplay, "E"));   // added, not replaced
        Assert.Equal(RebindStatus.Unchanged, rebinds.Bind(InputContext.Gameplay, "Use", Key("f")).Status);   // already there

        Assert.Equal(RebindStatus.Applied, rebinds.Unbind(InputContext.Gameplay, "Use", Key("E")).Status);
        Assert.Empty(Fires(app, InputContext.Gameplay, "E"));
        Assert.Equal(new[] { "Use" }, Fires(app, InputContext.Gameplay, "F"));
        Assert.Equal(RebindStatus.Unchanged, rebinds.Unbind(InputContext.Gameplay, "Use", Key("E")).Status);

        // An action with nothing left is still listed, so it can be bound again.
        Assert.Equal(RebindStatus.Applied, rebinds.Unbind(InputContext.Gameplay, null, Key("F")).Status);
        Assert.Empty(rebinds.Bindings(InputContext.Gameplay, "Use"));
        Assert.Contains(rebinds.ActionsIn(InputContext.Gameplay), a => a.Name == "Use");

        using var next = Boot(file);   // and it persisted
        Assert.Empty(next.Engine.Rebinds.Bindings(InputContext.Gameplay, "Use"));
    }

    [Fact]
    public void BindResetPutsTheGamesBindingsBack()
    {
        string file = NewFile();
        using var app = Boot(file);
        var rebinds = app.Engine.Rebinds;
        rebinds.Rebind(InputContext.Gameplay, "Jump", Key("F"));
        rebinds.Bind(InputContext.Gameplay, "Use", Key("G"));
        rebinds.Bind(InputContext.UI, "MenuConfirm", Key("Space"));

        Assert.Equal(RebindStatus.Applied, rebinds.Reset(InputContext.Gameplay, "Jump").Status);
        Assert.Equal(new[] { "Jump" }, Fires(app, InputContext.Gameplay, "Space"));
        Assert.Empty(Fires(app, InputContext.Gameplay, "F"));
        Assert.Equal(new[] { "Use" }, Fires(app, InputContext.Gameplay, "G"));   // the others stay

        Assert.Equal(RebindStatus.Applied, rebinds.Reset().Status);
        Assert.Empty(Fires(app, InputContext.Gameplay, "G"));
        Assert.Empty(Fires(app, InputContext.UI, "Space"));
        Assert.False(rebinds.IsOverridden(InputContext.UI, "MenuConfirm"));
        Assert.Equal(RebindStatus.Unchanged, rebinds.Reset().Status);

        using var next = Boot(file);
        Assert.Equal(new[] { "Jump" }, Fires(next, InputContext.Gameplay, "Space"));
    }

    [Fact]
    public void TheConsoleCommandsDoTheSame()
    {
        string file = NewFile();
        using var app = Boot(file);
        var cvars = app.Engine.CVars;
        Assert.True(cvars.Execute("bind F Use"));
        Assert.Equal(new[] { "Use" }, Fires(app, InputContext.Gameplay, "F"));
        Assert.True(cvars.Execute("bind mouse:Right Attack"));
        Assert.Equal(new[] { "Attack" }, app.Engine.Rebinds.ActionsFor(InputContext.Gameplay, new InputBinding { Mouse = "Right" }).ToArray());
        Assert.True(cvars.Execute("bind Space MenuConfirm UI"));
        Assert.Equal(new[] { "MenuConfirm" }, Fires(app, InputContext.UI, "Space"));

        Assert.True(cvars.Execute("unbind F Use"));
        Assert.Empty(Fires(app, InputContext.Gameplay, "F"));
        Assert.True(cvars.Execute("bind_reset Attack"));
        Assert.Empty(app.Engine.Rebinds.ActionsFor(InputContext.Gameplay, new InputBinding { Mouse = "Right" }));
        Assert.True(cvars.Execute("bind_reset all"));
        Assert.Empty(Fires(app, InputContext.UI, "Space"));
        Assert.False(app.Engine.Rebinds.IsOverridden(InputContext.Gameplay, "Use"));
    }

    [Fact]
    public void AnInputAnotherActionUsesIsAConflict()
    {
        string file = NewFile();
        using var app = Boot(file);
        var rebinds = app.Engine.Rebinds;

        var conflict = rebinds.Rebind(InputContext.Gameplay, "Jump", Key("E"));
        Assert.Equal(RebindStatus.Conflict, conflict.Status);
        Assert.Equal(new[] { "Use" }, conflict.Conflicts);
        Assert.True(conflict.CanReplace);
        Assert.False(File.Exists(file));   // nothing changed
        Assert.Equal(new[] { "Use" }, Fires(app, InputContext.Gameplay, "E"));

        // Replacing takes it from Use and gives it to Jump.
        Assert.Equal(RebindStatus.Applied, rebinds.Rebind(InputContext.Gameplay, "Jump", Key("E"), replaceConflicts: true).Status);
        Assert.Equal(new[] { "Jump" }, Fires(app, InputContext.Gameplay, "E"));
        Assert.Empty(rebinds.Bindings(InputContext.Gameplay, "Use"));

        // A key WASD uses is a conflict that cannot be settled by taking it: Move keeps its keys.
        var wasd = rebinds.Bind(InputContext.Gameplay, "Attack", Key("W"), replaceConflicts: true);
        Assert.Equal(RebindStatus.Conflict, wasd.Status);
        Assert.Equal(new[] { "Move" }, wasd.Conflicts);
        Assert.False(wasd.CanReplace);

        // The same key in another context is not a conflict: contexts are separate lists.
        Assert.Equal(RebindStatus.Applied, rebinds.Bind(InputContext.UI, "MenuConfirm", Key("E")).Status);
    }

    [Fact]
    public void EachContextHasItsOwnList()
    {
        using var app = Boot(NewFile());
        var rebinds = app.Engine.Rebinds;
        Assert.Equal(new[] { "Jump", "Use", "Attack", "Move" }, rebinds.ActionsIn(InputContext.Gameplay).Select(a => a.Name).ToArray());
        Assert.Equal(new[] { "MenuUp", "MenuConfirm" }, rebinds.ActionsIn(InputContext.UI).Select(a => a.Name).ToArray());
        Assert.Empty(rebinds.ActionsIn(InputContext.Console));
        Assert.Equal(new[] { InputContext.Gameplay, InputContext.UI }, rebinds.ContextsInUse.ToArray());

        // W is MenuUp's in the UI context and Move's in gameplay; rebinding one leaves the other.
        Assert.Equal(new[] { "MenuUp" }, Fires(app, InputContext.UI, "W"));
        Assert.Equal(new[] { "Move" }, Fires(app, InputContext.Gameplay, "W"));
        Assert.Equal(RebindStatus.Applied, rebinds.Rebind(InputContext.UI, "MenuUp", Key("I")).Status);
        Assert.Equal(new[] { "MenuUp" }, Fires(app, InputContext.UI, "I"));
        Assert.Empty(Fires(app, InputContext.UI, "W"));
        Assert.Equal(new[] { "Move" }, Fires(app, InputContext.Gameplay, "W"));
        Assert.False(rebinds.IsOverridden(InputContext.Gameplay, "Jump"));
    }

    [Fact]
    public void CaptureNextInputSetsTheBindingFromThePress()
    {
        string file = NewFile();
        using var app = Boot(file);
        var rebinds = app.Engine.Rebinds;

        Assert.False(rebinds.Capturing);
        Assert.False(rebinds.BeginCapture(InputContext.Gameplay, "Move"));   // an axis is not one press
        Assert.False(rebinds.BeginCapture(InputContext.Gameplay, "Nonesuch"));
        Assert.True(rebinds.BeginCapture(InputContext.Gameplay, "Use"));
        Assert.True(rebinds.Capturing);
        Assert.True(rebinds.TakeFresh());    // the frame the capture began in is skipped by the client...
        Assert.False(rebinds.TakeFresh());   // ...and the next one reads the device

        var result = rebinds.Offer(Key("Q"));   // the player pressed Q
        Assert.Equal(RebindStatus.Applied, result.Status);
        Assert.False(rebinds.Capturing);
        Assert.Equal(new[] { "Use" }, Fires(app, InputContext.Gameplay, "Q"));
        Assert.Empty(Fires(app, InputContext.Gameplay, "E"));

        // A press while not capturing is nothing.
        Assert.Equal(RebindStatus.Unchanged, rebinds.Offer(Key("Z")).Status);
        Assert.Empty(Fires(app, InputContext.Gameplay, "Z"));

        // A conflicting press ends the capture and waits for an answer; keeping changes nothing.
        rebinds.BeginCapture(InputContext.Gameplay, "Use");
        Assert.Equal(RebindStatus.Conflict, rebinds.Offer(Key("Space")).Status);
        Assert.False(rebinds.Capturing);
        var pending = Assert.IsType<PendingRebind>(rebinds.Pending);
        Assert.Equal(new[] { "Jump" }, pending.Conflicts);
        Assert.Equal(RebindStatus.Unchanged, rebinds.Resolve(replace: false).Status);
        Assert.Null(rebinds.Pending);
        Assert.Equal(new[] { "Jump" }, Fires(app, InputContext.Gameplay, "Space"));

        // Replacing takes it.
        rebinds.BeginCapture(InputContext.Gameplay, "Use");
        rebinds.Offer(Key("Space"));
        Assert.Equal(RebindStatus.Applied, rebinds.Resolve(replace: true).Status);
        Assert.Equal(new[] { "Use" }, Fires(app, InputContext.Gameplay, "Space"));

        // Cancelling ends a capture without a change.
        rebinds.BeginCapture(InputContext.Gameplay, "Jump");
        rebinds.CancelCapture();
        Assert.False(rebinds.Capturing);
        Assert.Equal(RebindStatus.Unchanged, rebinds.Offer(Key("T")).Status);
    }

    [Fact]
    public void ABadInputOrActionIsRefusedAndNothingIsWritten()
    {
        string file = NewFile();
        using var app = Boot(file);
        var rebinds = app.Engine.Rebinds;
        Assert.Equal(RebindStatus.Invalid, rebinds.Bind(InputContext.Gameplay, "Nonesuch", Key("F")).Status);
        Assert.Equal(RebindStatus.Invalid, rebinds.Bind(InputContext.Gameplay, "Use", new InputBinding { Composite = "WASD" }).Status);
        Assert.Equal(RebindStatus.Invalid, rebinds.Bind(InputContext.Gameplay, "Use", new InputBinding()).Status);

        // The client says which key names exist; a validator stands in for it.
        rebinds.Validator = (_, b) => b.Key == "NotAKey" ? "unknown key 'NotAKey'" : null;
        Assert.Equal(RebindStatus.Invalid, rebinds.Bind(InputContext.Gameplay, "Use", Key("NotAKey")).Status);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void WithNoUserFileAChangeIsRefused()
    {
        using var app = Boot(null);
        var result = app.Engine.Rebinds.Rebind(InputContext.Gameplay, "Jump", Key("F"));
        Assert.Equal(RebindStatus.CannotSave, result.Status);
        Assert.Equal(new[] { "Jump" }, Fires(app, InputContext.Gameplay, "Space"));
    }

    [Fact]
    public void ACorruptFileIsIgnoredAndKeptAsBad()
    {
        string file = NewFile();
        File.WriteAllText(file, "[ { \"type\": \"input_map\", ");
        using var app = Boot(file);
        Assert.Equal(new[] { "Jump" }, Fires(app, InputContext.Gameplay, "Space"));   // the game's own
        Assert.False(File.Exists(file));
        Assert.Equal("[ { \"type\": \"input_map\", ", File.ReadAllText(file + ".bad"));

        // And a rebind after that writes a fresh file.
        Assert.Equal(RebindStatus.Applied, app.Engine.Rebinds.Rebind(InputContext.Gameplay, "Jump", Key("F")).Status);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void BindingTextParsesTheDevicesAndNamesThem()
    {
        Assert.True(InputBinding.TryParse("E", out var key, out _));
        Assert.Equal("E", key.Key);
        Assert.True(InputBinding.TryParse("mouse:Right", out var mouse, out _));
        Assert.Equal("Right", mouse.Mouse);
        Assert.True(InputBinding.TryParse("pad:A", out var pad, out _));
        Assert.Equal("A", pad.Gamepad);
        Assert.Equal("Mouse Right", mouse.DisplayName);
        Assert.Equal("Pad A", pad.DisplayName);
        Assert.False(InputBinding.TryParse("touch:1", out _, out string error));
        Assert.Contains("unknown input kind", error);
        Assert.False(InputBinding.TryParse("", out _, out _));
    }
}
