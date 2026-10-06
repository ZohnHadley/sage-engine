#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The form widgets (issue #340): slider, checkbox, dropdown, text field and tabs — each worked from the
// D-pad and the pointer through UiInput alone, headless, and each raising ValueChanged (and
// UiResult.Changed) only when the player changed it.
public class FormWidgetTests
{
    private static readonly MonospaceTextMeasure Mono = WidgetLayoutTests.Mono;   // 10 x 20 a character

    private static UiResult Step(UiRoot root, UiNavigation direction) => root.Update(UiInput.Nav(direction));

    // A press as the client reports it: the button went down this frame, and is down.
    private static UiInput Press(Vector2 pointer)
    {
        var input = UiInput.Click(pointer);
        input.PointerDown = true;
        return input;
    }

    [Fact]
    public void ASliderStepsWithTheDPadAndFollowsThePointer()
    {
        var root = new UiRoot(Mono);
        var column = root.Content.Add(new Stack());
        var slider = column.Add(new Slider { Name = "volume", MinSize = new Vector2(200f, 10f), Step = 0.1f, Value = 0.5f });
        var ok = column.Add(new Button("ok"));
        var changes = new List<float>();
        slider.ValueChanged += w => changes.Add(((Slider)w).Value);
        root.Focus(slider);

        var result = Step(root, UiNavigation.Right);
        Assert.Equal(0.6f, slider.Value, 4);
        Assert.Same(slider, root.Focused);                       // Right moved the value, not the focus
        Assert.Same(slider, result.Changed);
        Assert.False(result.FocusChanged);
        for (int i = 0; i < 10; i++) Step(root, UiNavigation.Left);
        Assert.Equal(0f, slider.Value);                          // clamped at Min
        Assert.Same(slider, root.Focused);                       // and still there: no sliding off sideways
        Assert.Equal(7, changes.Count);                          // 0.6, 0.5 ... 0.0: a press at the end changes nothing

        Assert.Null(Step(root, UiNavigation.Left).Changed);
        Step(root, UiNavigation.Down);
        Assert.Same(ok, root.Focused);                           // across its axis, focus moves as usual

        // The pointer sets it where it is pressed, snapped to its step, and drags it while held — even
        // off it, without dragging focus along.
        result = root.Update(Press(new Vector2(124f, 5f)));
        Assert.Equal(0.6f, slider.Value, 4);
        Assert.Same(slider, root.Focused);
        Assert.Same(slider, result.Changed);
        root.Update(UiInput.Drag(new Vector2(61f, 30f)));       // over the button, still held
        Assert.Equal(0.3f, slider.Value, 4);
        Assert.Same(slider, root.Focused);
        root.Update(UiInput.Move(new Vector2(190f, 30f)));       // released: no longer dragged
        Assert.Equal(0.3f, slider.Value, 4);

        slider.Value = 0.9f;                                     // the game's own change raises nothing
        Assert.Equal(0.3f, changes[^1], 4);
    }

    [Fact]
    public void ACheckboxFlipsOnConfirmAndClickAndDrawsSelected()
    {
        var root = new UiRoot(Mono);
        var column = root.Content.Add(new Stack());
        var box = column.Add(new Checkbox("Subtitles"));
        var other = column.Add(new Button("other"));
        int pressed = 0, changed = 0;
        box.Pressed += _ => pressed++;
        box.ValueChanged += _ => changed++;

        // A box a line high and as wide, then half a line, then the text: 20 + 10 + 90 across.
        root.Layout();
        Assert.Equal(new Vector2(120f, 20f), box.DesiredSize);
        Assert.Equal(new Rect(0f, 0f, 20f, 20f), box.BoxRect);

        root.Focus(box);
        var result = root.Update(UiInput.Press);
        Assert.True(box.Checked);
        Assert.Same(box, result.Changed);
        Assert.Same(box, result.Activated);
        root.Update(UiInput.Click(new Vector2(5f, 5f)));
        Assert.False(box.Checked);
        Assert.Equal(2, pressed);
        Assert.Equal(2, changed);

        box.Checked = true;                                      // set by the game: nothing raised
        Assert.Equal(2, changed);
        root.Update(UiInput.Move(new Vector2(600f, 600f)));    // the pointer off it
        root.Focus(other);
        Assert.Equal(UiState.Selected, UiStyles.StateOf(box));  // ticked, not focused or hovered
        root.Focus(box);
        Assert.Equal(UiState.Focused, UiStyles.StateOf(box));   // focus wins
        box.Enabled = false;
        root.Update(UiInput.Press);
        Assert.True(box.Checked);                                // disabled: nothing flips it
    }

    [Fact]
    public void ADropdownCyclesOpensAListOverEverythingAndPicksFromIt()
    {
        var root = new UiRoot(Mono);
        var column = root.Content.Add(new Stack());
        var quality = column.Add(new Dropdown { Name = "quality" });
        quality.SetOptions(new[] { "low", "medium", "high" });
        quality.Selected = 0;
        var below = column.Add(new Button("below") { MinSize = new Vector2(80f, 60f) });
        var changes = new List<int>();
        quality.ValueChanged += w => changes.Add(((Dropdown)w).Selected);
        root.Focus(quality);

        // As wide as "medium" plus room for the arrow, whatever is chosen; it shows the chosen text.
        root.Layout();
        Assert.Equal(new Vector2(80f, 20f), quality.DesiredSize);
        Assert.Equal("low", quality.Text);

        // Closed, Left/Right cycle (wrapping) and keep focus.
        Step(root, UiNavigation.Right);
        Assert.Equal("medium", quality.Text);
        Step(root, UiNavigation.Left);
        Step(root, UiNavigation.Left);
        Assert.Equal(2, quality.Selected);
        Assert.Same(quality, root.Focused);
        Assert.Equal(new[] { 1, 0, 2 }, changes);

        // Confirm opens the list below it; Up/Down move the highlight, not focus; Confirm picks.
        var result = root.Update(UiInput.Press);
        Assert.True(quality.IsOpen);
        Assert.Same(quality, root.Popup);
        Assert.Null(result.Changed);
        Assert.Equal(new Rect(0f, 20f, 80f, 60f), quality.ListRect);
        Assert.Equal(2, quality.Highlighted);                    // starts on the chosen one
        Step(root, UiNavigation.Up);
        Step(root, UiNavigation.Up);
        Step(root, UiNavigation.Up);
        Assert.Equal(0, quality.Highlighted);
        Assert.Same(quality, root.Focused);
        result = root.Update(UiInput.Press);
        Assert.False(quality.IsOpen);
        Assert.Null(root.Popup);
        Assert.Equal(0, quality.Selected);
        Assert.Same(quality, result.Changed);

        // Back folds an open list and is used up; Back on a closed one is the screen's.
        root.Update(UiInput.Press);
        result = root.Update(UiInput.Cancel);
        Assert.False(quality.IsOpen);
        Assert.False(result.Back);
        Assert.True(root.Update(UiInput.Cancel).Back);

        // The pointer: the list is hit before what is under it (the button), a row lights under the
        // pointer and a click picks it; a click elsewhere only folds it.
        root.Update(UiInput.Click(new Vector2(10f, 10f)));
        Assert.True(quality.IsOpen);
        root.Update(UiInput.Move(new Vector2(40f, 50f)));       // row 1, over the button
        Assert.Equal(1, quality.Highlighted);
        Assert.Same(quality, root.Focused);
        result = root.Update(UiInput.Click(new Vector2(40f, 70f)));
        Assert.Equal("high", quality.Text);
        Assert.True(result.PointerOverUi);
        Assert.Same(quality, result.Activated);
        Assert.Same(quality, root.Focused);

        root.Update(UiInput.Press);
        result = root.Update(UiInput.Click(new Vector2(600f, 600f)));
        Assert.False(quality.IsOpen);
        Assert.True(result.PointerOverUi);                       // the click was the list's: not "outside" a screen
        Assert.Null(result.Activated);

        // Focus leaving folds it.
        root.Update(UiInput.Press);
        root.Focus(below);
        Assert.False(quality.IsOpen);
        Assert.Null(root.Popup);
    }

    [Fact]
    public void ATextFieldTakesCharactersNotKeysAndMovesItsCaret()
    {
        var root = new UiRoot(Mono);
        var column = root.Content.Add(new Stack(Orientation.Row));
        var name = column.Add(new TextBox { Name = "name", MaxLength = 8, Placeholder = "Your name" });
        var next = column.Add(new Button("next"));
        int changed = 0;
        name.ValueChanged += _ => changed++;

        root.Layout();
        Assert.Equal(new Vector2(90f, 20f), name.DesiredSize);   // the placeholder, while empty
        Assert.Null(root.Update(UiInput.Type("lost")).Changed);  // not focused: not its
        Assert.Equal("", name.Text);

        root.Focus(name);
        var result = root.Update(UiInput.Type("Bob's"));
        Assert.Equal("Bob's", name.Text);
        Assert.Equal(5, name.Caret);
        Assert.Same(name, result.Changed);
        root.Update(UiInput.Type("\b\b"));
        Assert.Equal("Bob", name.Text);

        // Left moves the caret while it can; typing goes there; DEL deletes after it.
        Step(root, UiNavigation.Left);
        Step(root, UiNavigation.Left);
        Assert.Equal(1, name.Caret);
        Assert.Same(name, root.Focused);
        root.Update(UiInput.Type("ar"));
        Assert.Equal("Barob", name.Text);
        root.Update(UiInput.Type("\u007f"));
        Assert.Equal("Barb", name.Text);

        // W is "up" in the `ui` context: the key that typed a "w" moves nothing. An arrow types nothing.
        result = root.Update(new UiInput { Navigate = UiNavigation.Up, Typed = "w" });
        Assert.Equal("Barwb", name.Text);
        Assert.Same(name, root.Focused);
        Assert.False(result.FocusChanged);

        // Enter is not a character of a single line, and it stops at MaxLength.
        root.Update(UiInput.Type("\rxyzxyz"));
        Assert.Equal("Barwxyzb", name.Text);
        Assert.Equal(6, changed);

        // At the end, Right leaves it as it would any widget.
        name.Caret = name.Text.Length;
        Step(root, UiNavigation.Right);
        Assert.Same(next, root.Focused);

        var notes = root.Content.Add(new TextBox { Multiline = true, Anchors = Anchors.BottomLeft });
        root.Focus(notes);
        root.Update(UiInput.Type("one\rtwo"));
        Assert.Equal("one\ntwo", notes.Text);
        Assert.Equal(new Vector2(30f, 20f), notes.CaretOffset(Mono));   // after "two", on the second line

        // The client hands the window's characters over as they came.
        Assert.Equal("é\b", UiInputMap.From(new UiControls { Typed = "é\b" }).Typed);
    }

    [Fact]
    public void TabsShowOnePageAndFollowFocusAlongTheTabs()
    {
        var root = new UiRoot(Mono);
        var tabs = root.Content.Add(new Tabs { Spacing = 4f });
        var general = tabs.AddPage("General", new Stack());
        var name = general.Add(new Button("name"));
        var audio = tabs.AddPage("Audio", new Stack());
        var volume = audio.Add(new Slider { MinSize = new Vector2(100f, 10f) });
        var changes = new List<int>();
        tabs.ValueChanged += w => changes.Add(((Tabs)w).Selected);

        root.Layout();
        Assert.Equal(2, tabs.PageCount);
        Assert.Equal(0, tabs.Selected);                          // the first page added is shown
        Assert.True(general.Visible);
        Assert.False(audio.Visible);
        Assert.DoesNotContain(volume, root.FocusChain);          // a hidden page is out of the focus chain
        // The tabs in a row, then the page, Spacing below: the tallest page that is shown.
        Assert.Equal(new Rect(0f, 0f, 70f, 20f), tabs.Tab(0).Rect);
        Assert.Equal(new Rect(70f, 0f, 50f, 20f), tabs.Tab(1).Rect);
        Assert.Equal(new Vector2(120f, 44f), tabs.DesiredSize);

        root.Focus(tabs.Tab(0));
        Step(root, UiNavigation.Right);
        Assert.Same(tabs.Tab(1), root.Focused);
        Assert.Equal(1, tabs.Selected);                          // focusing a tab shows its page
        Assert.True(audio.Visible);
        Assert.False(general.Visible);
        Assert.Equal(new[] { 1 }, changes);
        Assert.Equal(UiState.Focused, UiStyles.StateOf(tabs.Tab(1)));
        Assert.Equal(UiState.Normal, UiStyles.StateOf(tabs.Tab(0)));
        Step(root, UiNavigation.Down);
        Assert.Same(volume, root.Focused);                       // and down into it
        Assert.Equal(UiState.Selected, UiStyles.StateOf(tabs.Tab(1)));
        Assert.DoesNotContain(name, root.FocusChain);

        tabs.Selected = 0;                                       // the game's: nothing raised
        Assert.Equal(new[] { 1 }, changes);
        Assert.True(tabs.RemovePage(general));
        Assert.Equal(0, tabs.Selected);
        Assert.Same(audio, tabs.SelectedPage);
        Assert.True(audio.Visible);
    }

    // What the client draws for each (issue #97's plan): a slider's handle, a checkbox's box and tick, a
    // dropdown's arrow and — over everything, after the tree — its open list; a text field's faded
    // placeholder and its caret while focused.
    [Fact]
    public void ThePlanDrawsTheFormWidgetsAndAnOpenListLast()
    {
        using var app = WidgetDrawingTests.Boot();
        var styles = app.World.Resources.Get<UiStyles>();
        var root = new UiRoot(new MonospaceTextMeasure(6f, 9f));
        var column = root.Content.Add(new Stack { Name = "column" });
        var slider = column.Add(new Slider { Name = "slider", Style = "uitest:bar", MinSize = new Vector2(100f, 10f), Value = 0.5f });
        column.Add(new Checkbox("On") { Name = "box", Checked = true });
        var dropdown = column.Add(new Dropdown { Name = "drop", Style = "uitest:button" });
        dropdown.SetOptions(new[] { "a", "b" });
        var field = column.Add(new TextBox { Name = "field", Placeholder = "name" });
        column.Add(new Label("after") { Name = "after" });

        var plan = new UiRenderPlan();
        plan.Update(root, styles);
        string Kinds() => string.Join(" ", plan.Commands.ToArray().Select(c => c.Kind + ":" + c.Widget?.Name));
        Assert.Equal("Rect:slider Rect:slider Rect:slider Border:box Rect:box Text:box Rect:drop Text:drop Text:field Text:after", Kinds());
        Assert.Equal(slider.KnobRect, plan.Commands[2].Rect);
        Assert.Equal(new Rect(47.5f, 0f, 5f, 10f), slider.KnobRect);                  // half as wide as it is tall, on the fill's end
        Assert.Equal("v", plan.Commands[7].Text);
        Assert.Equal("name", plan.Commands[8].Text);
        Assert.Equal(UiColour.Fade(ColourJsonConverter.Pack(255, 255, 255), 0.5f), plan.Commands[8].Colour);

        root.Focus(field);
        plan.Update(root, styles);
        Assert.EndsWith("Text:field Rect:field Text:after", Kinds());               // the caret, in front of the placeholder
        Assert.Equal(field.ContentRect.X, plan.Commands[^2].Rect.X);

        root.Focus(dropdown);
        dropdown.Open();
        plan.Update(root, styles);
        // The list after "after": a box, the highlighted row, each option's text.
        Assert.EndsWith("Text:after Rect:drop Rect:drop Text:drop Text:drop", Kinds());
        var c = plan.Commands;
        Assert.Equal(dropdown.ListRect, c[^4].Rect);
        Assert.Equal(dropdown.OptionRect(0), c[^3].Rect);
        Assert.Equal(ColourJsonConverter.Pack(0x30, 0x50, 0xA0), c[^3].Colour);    // the style's focused background
        Assert.Equal(new[] { "a", "b" }, new[] { c[^2].Text, c[^1].Text });
    }
}
