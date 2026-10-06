#nullable enable
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;
using Sage.UI;
using Xunit;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The controls screen (issue #328): the kit's `rpg:controls` over Sage.UI's ControlsView, a view of the
// engine's InputRebinds. Clicking Rebind waits for the next press (the client offers it; a test offers it
// the same way), a press another action has asks to replace or keep, Reset puts the game's bindings back,
// and everything is written to the player's input.json and in force at once.
public class ControlsScreenTests
{
    public ControlsScreenTests() { _ = TestEnv.UserRoot; }

    private static string Game()
    {
        string dir = TestEnv.NewTempDir();
        string content = Path.Combine(dir, "content", "data");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(dir, "game.json"), """{ "name": "Controls test", "id": "ctrltest", "mounts": ["content"], "version": "1.0.0" }""");
        File.WriteAllText(Path.Combine(content, "input.json"), """
            [ { "type": "input_map", "id": "gameplay", "context": "Gameplay",
                "actions": { "Jump": [ { "key": "Space" }, { "gamepad": "A" } ], "Use": [ { "key": "E" } ],
                             "Attack": [ { "mouse": "Left" } ], "Move": [ { "composite": "WASD" } ] } },
              { "type": "input_map", "id": "ui", "context": "UI",
                "actions": { "MenuConfirm": [ { "key": "Enter" } ] } } ]
            """);
        return dir;
    }

    private static HeadlessApp Boot(string game, string file) =>
        HeadlessApp.ForGame(game).With(new RpgKitModule()).WithUserInput(file).OnRegistered(app =>
        {
            app.Records.Register<InputMapRecord>();
            app.Engine.Actions.Register("MenuConfirm", ActionKind.Button);
        }).Boot();

    private static void Click(UiScreenStack stack, UiLayer layer, Widget widget)
    {
        var centre = new Vector2(widget.Rect.X + widget.Rect.Width * 0.5f, widget.Rect.Y + widget.Rect.Height * 0.5f);
        stack.Update(UiInput.Click(layer.Root.ToPixels(centre)));
        stack.Update(UiInput.Wait(0f));
    }

    private static System.Collections.Generic.IEnumerable<Widget> All(Widget root)
    {
        yield return root;
        for (int i = 0; i < root.ChildCount; i++)
            foreach (var w in All(root.Child(i))) yield return w;
    }

    private static Widget Button(UiLayer layer, ControlsView view, string action, string node)
    {
        var row = view.Rows.Single(r => r.Action == action);
        return All(layer.Content).Single(w => w.Name == node && ReferenceEquals(UiScreen.RowOf(w), row));
    }

    private static (UiScreenStack Stack, UiLayer Layer, ControlsView View) Open(HeadlessApp app)
    {
        var world = app.World;
        var stack = world.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        stack.OpenSeconds = stack.CloseSeconds = 0f;
        var layer = stack.Open(RpgKitModule.ControlsScreen, new UiBindContext(world));
        stack.Update(UiInput.Wait(0f));
        return (stack, layer, Assert.IsType<ControlsView>(layer.Screen!.ViewModel));
    }

    private static string Text(UiLayer layer, string node) => layer.Content.Find<Label>(node)!.Text;

    [Fact]
    public void PressingAKeyAfterRebindSetsTheBindingAndItSurvivesARestart()
    {
        string game = Game();
        string file = Path.Combine(TestEnv.NewTempDir(), "input.json");
        using (var app = Boot(game, file))
        {
            var (stack, layer, view) = Open(app);
            var rebinds = app.Engine.Rebinds;

            // Gameplay's buttons, with what they have (the game's, then the kit's default keys, issue #354);
            // Move is an axis and not listed.
            Assert.Equal(new[] { "Jump", "Use", "Attack", "Spellbook", "Spellmaker", "Journal", "Rest" }, view.Rows.Select(r => r.Action).ToArray());
            Assert.Equal("Space, Pad A", view.Rows[0].Bindings);
            Assert.Equal("Mouse Left", view.Rows[2].Bindings);
            Assert.False(view.Rows[1].Overridden);
            Assert.False(view.Capturing);

            Click(stack, layer, Button(layer, view, "Use", ControlsView.RebindButton));
            Assert.True(view.Capturing);
            Assert.True(rebinds.Capturing);
            Assert.Equal("@rpg.controls.press", view.Rows[1].RebindLabel);
            Assert.Contains("Use", Text(layer, "capturing"));

            rebinds.Offer(new InputBinding { Key = "Q" });   // what the client does with the player's keypress
            stack.Update(UiInput.Wait(0f));
            Assert.False(view.Capturing);
            Assert.Equal("Q", view.Rows[1].Bindings);
            Assert.True(view.Rows[1].Overridden);
            Assert.Equal("Saved.", Text(layer, "message"));
            Assert.Equal(new[] { "Use" }, rebinds.ActionsFor(InputContext.Gameplay, new InputBinding { Key = "Q" }).ToArray());
        }

        using var next = Boot(game, file);   // the next start
        Assert.Equal(new[] { "Use" }, next.Engine.Rebinds.ActionsFor(InputContext.Gameplay, new InputBinding { Key = "Q" }).ToArray());
        Assert.Empty(next.Engine.Rebinds.ActionsFor(InputContext.Gameplay, new InputBinding { Key = "E" }));
        var (_, _, view2) = Open(next);
        Assert.Equal("Q", view2.Rows.Single(r => r.Action == "Use").Bindings);
    }

    [Fact]
    public void AConflictAsksToReplaceOrKeepAndBackCancels()
    {
        string game = Game();
        using var app = Boot(game, Path.Combine(TestEnv.NewTempDir(), "input.json"));
        var (stack, layer, view) = Open(app);
        var rebinds = app.Engine.Rebinds;

        // Jump gets E, which Use has: nothing changes, and the screen says who has it.
        Click(stack, layer, Button(layer, view, "Jump", ControlsView.RebindButton));
        rebinds.Offer(new InputBinding { Key = "E" });
        stack.Update(UiInput.Wait(0f));
        Assert.True(view.ConflictPending);
        Assert.True(view.CanReplace);
        Assert.Equal("E is already used by Use.", Text(layer, "conflict"));
        Assert.Equal("Space, Pad A", view.Rows[0].Bindings);

        // Keep: as it was.
        Click(stack, layer, layer.Content.Find(ControlsView.KeepButton)!);
        Assert.False(view.ConflictPending);
        Assert.Equal("E", view.Rows[1].Bindings);

        // Replace: Jump has E (and still its pad button), Use has nothing.
        Click(stack, layer, Button(layer, view, "Jump", ControlsView.RebindButton));
        rebinds.Offer(new InputBinding { Key = "E" });
        stack.Update(UiInput.Wait(0f));
        Click(stack, layer, layer.Content.Find(ControlsView.ReplaceButton)!);
        Assert.Equal("E, Pad A", view.Rows[0].Bindings);
        Assert.Equal("-", view.Rows[1].Bindings);

        // WASD is Move's, and cannot be taken by a button.
        Click(stack, layer, Button(layer, view, "Attack", ControlsView.RebindButton));
        rebinds.Offer(new InputBinding { Key = "W" });
        stack.Update(UiInput.Wait(0f));
        Assert.True(view.ConflictPending);
        Assert.False(view.CanReplace);
        Assert.Contains("Move", Text(layer, "message"));
        Assert.Equal("Mouse Left", view.Rows[2].Bindings);

        // Back puts down a capture, then the conflict; only then does it close the screen.
        Click(stack, layer, Button(layer, view, "Use", ControlsView.RebindButton));
        Assert.True(view.Capturing);
        stack.Update(UiInput.Cancel);
        Assert.False(view.Capturing);
        Assert.True(stack.IsOpen);
        stack.Update(UiInput.Cancel);
        Assert.False(stack.IsOpen);
    }

    [Fact]
    public void ResetAndTheContextTabs()
    {
        string game = Game();
        using var app = Boot(game, Path.Combine(TestEnv.NewTempDir(), "input.json"));
        var (stack, layer, view) = Open(app);
        var rebinds = app.Engine.Rebinds;
        rebinds.Rebind(InputContext.Gameplay, "Use", new InputBinding { Key = "Q" });
        rebinds.Rebind(InputContext.UI, "MenuConfirm", new InputBinding { Key = "Space" });
        stack.Update(UiInput.Wait(0f));
        Assert.True(view.Rows.Single(r => r.Action == "Use").Overridden);   // a console `bind` shows when the next frame reads it

        // One action back to the game's.
        Click(stack, layer, Button(layer, view, "Use", ControlsView.ResetButton));
        Assert.Equal("E", view.Rows.Single(r => r.Action == "Use").Bindings);
        Assert.False(view.Rows.Single(r => r.Action == "Use").Overridden);

        // The other contexts have their own lists.
        Click(stack, layer, layer.Content.Find("tabUI")!);
        Assert.Equal(InputContext.UI, view.Context);
        Assert.Equal("MenuConfirm", view.Rows[0].Action);
        Assert.Equal(5, view.Rows.Count);                                   // and the kit's four
        Assert.Equal("Space", view.Rows[0].Bindings);
        Assert.Equal("@rpg.controls.ctx_UI", view.ContextLabel);

        // Reset all covers every context.
        Click(stack, layer, layer.Content.Find(ControlsView.ResetAllButton)!);
        Assert.Equal("Enter", view.Rows[0].Bindings);
        Click(stack, layer, layer.Content.Find("tabGameplay")!);
        Assert.Equal(InputContext.Gameplay, view.Context);
        Assert.Equal(7, view.Rows.Count);
    }
}
