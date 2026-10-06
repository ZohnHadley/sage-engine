#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Modal focus scopes, explicit focus neighbours and confirm prompts (issue #343): a prompt over a menu —
// in the same layer, or the stack's own dialog layer — cannot be left by the D-pad, Tab or the pointer
// until it is answered, and focus goes back where it was when it closes.
public class FocusScopeTests
{
    public FocusScopeTests() { _ = TestEnv.UserRoot; }

    private static readonly UiNavigation[] Every =
        { UiNavigation.Up, UiNavigation.Down, UiNavigation.Left, UiNavigation.Right, UiNavigation.Next, UiNavigation.Previous };

    private static Vector2 Centre(Widget w) => w.Root!.ToPixels(new Vector2(w.Rect.X + w.Rect.Width * 0.5f, w.Rect.Y + w.Rect.Height * 0.5f));

    [Fact]
    public void AFocusScopeOverAMenuCannotBeLeftByGamepadOrMouse()
    {
        var root = WidgetLayoutTests.Root();
        var menu = root.Content.Add(new Stack { Margin = new Thickness(10f), Spacing = 4f });
        var items = Enumerable.Range(0, 3).Select(i => menu.Add(new Button($"item {i}") { MinSize = new Vector2(200f, 40f) })).ToList();
        var prompt = root.Content.Add(new Stack { Anchors = Anchors.Center, Direction = Orientation.Row, Spacing = 8f, Visible = false, FocusScope = true });
        var yes = prompt.Add(new Button("yes") { MinSize = new Vector2(80f, 40f) });
        var no = prompt.Add(new Button("no") { MinSize = new Vector2(80f, 40f) });
        var pressed = new List<Button>();
        foreach (var b in items.Append(yes).Append(no)) b.Pressed += pressed.Add;

        root.Update(UiInput.Nav(UiNavigation.Down));
        root.Update(UiInput.Nav(UiNavigation.Down));
        Assert.Same(items[1], root.Focused);
        Assert.Null(root.ActiveScope);

        // The prompt shows: it takes focus, and the chain is its buttons alone.
        prompt.Visible = true;
        root.Update(UiInput.Wait(0f));
        Assert.Same(prompt, root.ActiveScope);
        Assert.Same(yes, root.Focused);
        Assert.Equal(new Widget[] { yes, no }, root.FocusChain);

        // The D-pad and Tab, every way, many times: never out of it.
        for (int round = 0; round < 4; round++)
            foreach (var direction in Every)
            {
                var result = root.Update(UiInput.Nav(direction));
                Assert.True(root.Focused == yes || root.Focused == no, $"{direction} left the prompt for {root.Focused?.Name}");
                Assert.Same(prompt, result.Scope);
            }

        // The pointer: hovering a menu item neither hovers nor focuses it, a click on one does nothing
        // (and is still the UI's, not the world's), and Focus refuses it.
        root.Focus(yes);
        root.Update(UiInput.Move(Centre(items[2])));
        Assert.Null(root.Hovered);
        Assert.Same(yes, root.Focused);
        var click = root.Update(UiInput.Click(Centre(items[0])));
        Assert.Null(click.Activated);
        Assert.True(click.PointerOverUi);
        Assert.Same(yes, root.Focused);
        Assert.False(root.Focus(items[0]));
        Assert.Empty(pressed);

        // Inside it the pointer and Confirm work as anywhere.
        Assert.Same(no, root.Update(UiInput.Click(Centre(no))).Activated);
        root.Update(UiInput.Nav(UiNavigation.Left));
        Assert.Same(yes, root.Update(UiInput.Press).Activated);
        Assert.Equal(new[] { no, yes }, pressed);

        // It hides: focus goes back to the menu item that had it, and the whole tree is reachable again.
        prompt.Visible = false;
        root.Update(UiInput.Wait(0f));
        Assert.Null(root.ActiveScope);
        Assert.Same(items[1], root.Focused);
        Assert.Equal(items.Cast<Widget>(), root.FocusChain);
        Assert.Same(items[0], root.Update(UiInput.Click(Centre(items[0]))).Activated);
    }

    // A scope inside a scope holds while it shows, and gives focus back to the outer one when it hides.
    [Fact]
    public void ANestedScopeReturnsFocusToTheOuterOne()
    {
        var root = WidgetLayoutTests.Root();
        var menu = root.Content.Add(new Button("menu"));
        var outer = root.Content.Add(new Stack { Anchors = Anchors.Center, FocusScope = true });
        var a = outer.Add(new Button("a"));
        var b = outer.Add(new Button("b"));
        var inner = outer.Add(new Stack { FocusScope = true, Visible = false });
        var c = inner.Add(new Button("c"));

        root.Update(UiInput.Wait(0f));
        Assert.Same(a, root.Focused);
        root.Update(UiInput.Nav(UiNavigation.Next));
        Assert.Same(b, root.Focused);

        inner.Visible = true;
        root.Update(UiInput.Nav(UiNavigation.Next));
        Assert.Same(inner, root.ActiveScope);
        Assert.Same(c, root.Focused);
        root.Update(UiInput.Nav(UiNavigation.Up));
        Assert.Same(c, root.Focused);

        inner.Visible = false;
        root.Update(UiInput.Wait(0f));
        Assert.Same(outer, root.ActiveScope);
        Assert.Same(b, root.Focused);

        outer.Visible = false;
        root.Update(UiInput.Wait(0f));
        Assert.Null(root.ActiveScope);
        Assert.Same(menu, root.Focused);   // nothing had focus before the outer scope: the first in tab order
    }

    [Fact]
    public void ExplicitNeighboursOverrideSpatialNavigation()
    {
        var (root, _, slots) = WidgetFocusTests.Inventory();
        slots[0].FocusRight = "slot3";      // the end of the row, not the next slot
        slots[3].FocusDown = "slot8";       // the start of the last row
        slots[8].FocusUp = "nope";          // no such widget: the nearest one up
        slots[4].FocusRight = "slot6";
        slots[6].Enabled = false;           // cannot take focus: the nearest one right

        root.Focus(slots[0]);
        root.Update(UiInput.Nav(UiNavigation.Right));
        Assert.Same(slots[3], root.Focused);
        root.Update(UiInput.Nav(UiNavigation.Down));
        Assert.Same(slots[8], root.Focused);
        root.Update(UiInput.Nav(UiNavigation.Up));
        Assert.Same(slots[4], root.Focused);
        root.Update(UiInput.Nav(UiNavigation.Right));
        Assert.Same(slots[5], root.Focused);
        root.Update(UiInput.Nav(UiNavigation.Left));
        Assert.Same(slots[4], root.Focused);
    }

    // The stack's own prompt: a dialog layer over a menu layer, answered by its buttons or Back.
    [Fact]
    public void AConfirmPromptOverAMenuTrapsFocusUntilAnsweredAndGivesItBack()
    {
        var stack = new UiScreenStack { OpenSeconds = 0f, CloseSeconds = 0f };
        stack.SetViewport(new Vector2(1280f, 720f));
        var column = new Stack { Margin = new Thickness(10f), Spacing = 4f };
        var save = column.Add(new Button("save") { MinSize = new Vector2(200f, 40f) });
        var quit = column.Add(new Button("quit") { MinSize = new Vector2(200f, 40f) });
        var menuPresses = new List<Button>();
        save.Pressed += menuPresses.Add;
        quit.Pressed += menuPresses.Add;
        var menu = stack.Push(column);
        stack.Update(UiInput.Nav(UiNavigation.Down));
        Assert.Same(quit, menu.Root.Focused);

        var answers = new List<bool>();
        var dialog = stack.Confirm("Quit?", "Quit without saving?", answers.Add, confirm: "Quit", cancel: "Stay");
        Assert.Same(dialog.Layer, stack.Top);
        Assert.Same(dialog, dialog.Layer.Dialog);
        Assert.Same(dialog.CancelButton, dialog.Layer.Root.Focused);           // the safe answer first
        Assert.Equal("Stay", dialog.CancelButton!.Text);

        for (int round = 0; round < 3; round++)
            foreach (var direction in Every)
            {
                stack.Update(UiInput.Nav(direction));
                Assert.True(dialog.ConfirmButton.Contains(dialog.Layer.Root.Focused) || dialog.CancelButton.Contains(dialog.Layer.Root.Focused));
                Assert.Same(quit, menu.Root.Focused);                           // the menu under it is untouched
            }
        // A press on the menu, under the dialog, answers nothing and closes nothing.
        var result = stack.Update(UiInput.Click(Centre(save)));
        Assert.True(result.PointerOverUi);
        Assert.Null(result.Activated);
        Assert.True(dialog.IsOpen);
        Assert.False(dialog.Layer.IsClosing);
        Assert.Empty(menuPresses);

        // Back is no: the dialog closes and the menu has the input again, focus where it was.
        stack.Update(UiInput.Cancel);
        Assert.Equal(new[] { false }, answers);
        Assert.Equal(UiDialogResult.Cancelled, dialog.Result);
        Assert.False(menu.IsClosing);
        stack.Update(UiInput.Wait(0f));
        Assert.Same(menu, stack.Top);
        Assert.Same(quit, menu.Root.Focused);
        stack.Update(UiInput.Cancel);                                           // answered once only
        Assert.Single(answers);
        Assert.True(menu.IsClosing);                                            // the menu's own Back

        // Its confirm button is yes.
        menu = stack.Push(column = new Stack());
        column.Add(new Button("again"));
        var second = stack.Confirm("Overwrite?", "Slot 1 has a save in it.", answers.Add);
        stack.Update(UiInput.Nav(UiNavigation.Left));
        Assert.Same(second.ConfirmButton, second.Layer.Root.Focused);
        stack.Update(UiInput.Press);
        Assert.Equal(new[] { false, true }, answers);
        Assert.Equal(UiDialogResult.Confirmed, second.Result);

        // A message box has one button, focused; it or Back dismisses it.
        int closed = 0;
        var message = stack.Message("Saved", "Your game was saved.", () => closed++);
        Assert.Null(message.CancelButton);
        Assert.Same(message.ConfirmButton, message.Layer.Root.Focused);
        stack.Update(UiInput.Click(Centre(message.ConfirmButton)));
        Assert.Equal(1, closed);
        stack.Message("Saved", "Again.", () => closed++);
        stack.Update(UiInput.Cancel);
        Assert.Equal(2, closed);
        Assert.Same(menu, stack.Top);
    }

    // Back while a focus scope shows inside a screen is for whatever showed it, not a cue to close the layer.
    [Fact]
    public void BackWithAScopeShowingInALayerDoesNotCloseIt()
    {
        var stack = new UiScreenStack { OpenSeconds = 0f };
        stack.SetViewport(new Vector2(1280f, 720f));
        var window = new Box();
        window.Add(new Button("delete"));
        var prompt = window.Add(new Stack { Anchors = Anchors.Center, FocusScope = true });
        prompt.Add(new Button("sure?"));
        var layer = stack.Push(window);

        var result = stack.Update(UiInput.Cancel);
        Assert.True(result.Back);
        Assert.Same(prompt, result.Scope);
        Assert.False(layer.IsClosing);
        stack.Update(UiInput.Click(new Vector2(1270f, 710f)));                 // beside the prompt: not "outside"
        Assert.False(layer.IsClosing);

        prompt.Visible = false;
        stack.Update(UiInput.Cancel);
        Assert.True(layer.IsClosing);
    }

    [Fact]
    public void LayoutRecordsSetScopesAndNeighboursAndANeighbourThatIsNoNodeIsAnError()
    {
        using var capture = new CaptureSink();
        var fixture = new MountFixture();
        fixture.Write("uiscope", "data/ui.json", """
            [
              { "type": "ui_layout", "id": "menu", "nodes": {
                  "load":   { "widget": "button", "text": "Load", "focusDown": "quit", "focusUp": "quit" },
                  "quit":   { "widget": "button", "text": "Quit", "focusRight": "load", "focusLeft": "load" },
                  "prompt": { "widget": "stack", "focusScope": true, "visible": false },
                  "ok":     { "widget": "button", "parent": "prompt", "text": "OK" } } },
              { "type": "ui_layout", "id": "typo", "nodes": { "a": { "widget": "button", "focusUp": "bb" }, "b": { "widget": "button" } } }
            ]
            """);
        fixture.Mount("uiscope", "uiscope");
        using var app = UiRecordTests.Boot(fixture);

        var view = app.World.Resources.Get<UiScreens>().BuildLayout(new RecordId("uiscope", "menu"));
        var load = view.Find("load")!;
        Assert.Equal("quit", load.FocusDown);
        Assert.Equal("quit", load.FocusUp);
        Assert.Null(load.FocusLeft);
        Assert.Equal("load", view.Find("quit")!.FocusRight);
        Assert.True(view.Find("prompt")!.FocusScope);
        Assert.False(load.FocusScope);

        var messages = capture.Entries.Select(e => e.Message).Where(m => m.StartsWith("uiscope:", StringComparison.Ordinal)).ToList();
        Assert.True(messages.Any(m => m.StartsWith("uiscope:data/ui.json:7:", StringComparison.Ordinal)
                                      && m.Contains("node 'a' goes to 'bb', which is not a node of this layout; did you mean 'b'?")),
                    string.Join("\n", messages));
        Assert.DoesNotContain(messages, m => m.Contains("uiscope:menu"));
    }
}
