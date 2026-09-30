#nullable enable
using System.Collections.Generic;
using System.Numerics;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Focus and the pointer (issue #95): tab order, spatial navigation with the D-pad, hit testing in
// pixels on a scaled viewport, activation — all driven by UiInput, the struct the client will fill
// from the `ui` input context (#97) and a test fills by hand.
public class WidgetFocusTests
{
    // A 4 x 3 grid of 64-unit slots, 4 apart, 10 in from the top-left: slot (row r, column c) is at
    // (10 + 68c, 10 + 68r).
    internal static (UiRoot Root, Grid Grid, List<Button> Slots) Inventory(Vector2 viewport = default)
    {
        var root = WidgetLayoutTests.Root(viewport);
        var grid = root.Content.Add(new Grid { Columns = 4, Spacing = new Vector2(4f, 4f), Margin = new Thickness(10f) });
        var slots = new List<Button>();
        for (int i = 0; i < 12; i++)
            slots.Add(grid.Add(new Button { Name = $"slot{i}", MinSize = new Vector2(64f, 64f) }));
        root.Layout();   // rects are what the last layout arranged
        return (root, grid, slots);
    }

    private static Widget? Step(UiRoot root, UiNavigation direction) { root.Update(UiInput.Nav(direction)); return root.Focused; }

    // The acceptance test: lay out a grid, move focus with the D-pad, hit-test the pointer.
    [Fact]
    public void TheDPadMovesFocusAroundAGridAndThePointerHitsTheSlotUnderIt()
    {
        var (root, _, slots) = Inventory(new Vector2(2560f, 1440f));   // scale 2
        Assert.Equal(new Rect(10f + 68f * 2f, 10f + 68f, 64f, 64f), slots[6].Rect);

        Assert.Same(slots[0], Step(root, UiNavigation.Down));   // nothing focused: the first slot
        Assert.Same(slots[4], Step(root, UiNavigation.Down));
        Assert.Same(slots[5], Step(root, UiNavigation.Right));
        Assert.Same(slots[6], Step(root, UiNavigation.Right));
        Assert.Same(slots[10], Step(root, UiNavigation.Down));
        Assert.Same(slots[10], Step(root, UiNavigation.Down));   // the bottom row: nowhere to go, it stays
        Assert.Same(slots[11], Step(root, UiNavigation.Right));
        Assert.Same(slots[11], Step(root, UiNavigation.Right));
        Assert.Same(slots[7], Step(root, UiNavigation.Up));
        Assert.Same(slots[3], Step(root, UiNavigation.Up));
        Assert.Same(slots[2], Step(root, UiNavigation.Left));
        Assert.True(slots[2].IsFocused);

        // The centre of slot 9 (row 2, column 1) in virtual units is (10 + 68 + 32, 10 + 136 + 32); the
        // pointer is in pixels, twice that.
        var pressed = new List<Button>();
        foreach (var slot in slots) slot.Pressed += pressed.Add;
        var result = root.Update(UiInput.Click(new Vector2(110f, 178f) * 2f));
        Assert.Same(slots[9], root.HitTest(new Vector2(220f, 356f)));
        Assert.Same(slots[9], result.Activated);
        Assert.Same(slots[9], root.Focused);
        Assert.True(result.PointerOverUi);
        Assert.True(result.FocusChanged);
        Assert.Equal(new[] { slots[9] }, pressed);

        // Between two slots, and off the grid: nothing, so a click there is not the UI's.
        Assert.Null(root.HitTest(new Vector2(75f, 20f) * 2f));
        var outside = root.Update(UiInput.Click(new Vector2(1000f, 600f)));
        Assert.False(outside.PointerOverUi);
        Assert.Null(outside.Activated);
        Assert.Same(slots[9], root.Focused);
    }

    [Fact]
    public void TabOrderIsTabIndexThenTreeOrderAndWraps()
    {
        var root = WidgetLayoutTests.Root();
        var column = root.Content.Add(new Stack());
        var a = column.Add(new Button("a"));
        var b = column.Add(new Button("b") { TabIndex = 1 });
        var c = column.Add(new Button("c"));
        column.Add(new Label("not focusable"));

        Assert.Equal(new Widget[] { a, c, b }, root.FocusChain);
        Assert.Same(a, Step(root, UiNavigation.Next));
        Assert.Same(c, Step(root, UiNavigation.Next));
        Assert.Same(b, Step(root, UiNavigation.Next));
        Assert.Same(a, Step(root, UiNavigation.Next));
        Assert.Same(b, Step(root, UiNavigation.Previous));
    }

    [Fact]
    public void ConfirmActivatesTheFocusedButtonAndBackIsReported()
    {
        var (root, _, slots) = Inventory();
        int presses = 0;
        slots[1].Pressed += _ => presses++;
        root.Focus(slots[1]);

        var result = root.Update(UiInput.Press);
        Assert.Same(slots[1], result.Activated);
        Assert.Equal(1, presses);
        Assert.False(result.FocusChanged);

        var back = root.Update(UiInput.Cancel);
        Assert.True(back.Back);
        Assert.Null(back.Activated);
    }

    [Fact]
    public void ADisabledButtonIsSkippedAndCannotBeActivated()
    {
        var (root, _, slots) = Inventory();
        slots[1].Enabled = false;
        int presses = 0;
        slots[1].Pressed += _ => presses++;

        root.Focus(slots[0]);
        Assert.Same(slots[2], Step(root, UiNavigation.Right));
        Assert.False(root.Focus(slots[1]));
        Assert.Null(root.Update(UiInput.Click(RectCentre(slots[1].Rect))).Activated);
        Assert.Equal(0, presses);

        // And a disabled parent disables what is in it.
        slots[2].Enabled = true;
        var grid = (Grid)slots[0].Parent!;
        grid.Enabled = false;
        Assert.Empty(root.FocusChain);
        Assert.Null(root.Update(default).Activated);
        Assert.Null(root.Focused);   // the focused slot lost focus with its grid
    }

    // The mouse moves the same focus the keys move, and only when it moves: a pointer resting over a
    // slot does not drag focus back each frame after the D-pad moved it (the panel screens' rule, F38).
    [Fact]
    public void HoverMovesFocusOnlyWhenThePointerMoves()
    {
        var (root, _, slots) = Inventory();
        root.Update(UiInput.Move(RectCentre(slots[5].Rect)));
        Assert.Same(slots[5], root.Focused);
        Assert.Same(slots[5], root.Hovered);

        Step(root, UiNavigation.Right);
        root.Update(new UiInput { Pointer = RectCentre(slots[5].Rect) });   // the pointer is still there
        Assert.Same(slots[6], root.Focused);
    }

    [Fact]
    public void AnItemListSelectsWhatHasFocusAndScrollsItIntoView()
    {
        var root = WidgetLayoutTests.Root();
        var scroll = root.Content.Add(new Scroll { MinSize = new Vector2(200f, 60f) });   // three rows show
        var list = new ItemList();
        scroll.Content = list;
        for (int i = 0; i < 8; i++) list.Add(new Label($"item {i}"));
        var activated = new List<int>();
        list.ItemActivated += (_, index) => activated.Add(index);
        root.Layout();

        Assert.Equal(-1, list.Selected);
        Step(root, UiNavigation.Down);
        Assert.Equal(0, list.Selected);
        for (int i = 0; i < 4; i++) Step(root, UiNavigation.Down);
        Assert.Equal(4, list.Selected);
        Assert.Equal(new Vector2(0f, 40f), scroll.Offset);   // item 4 (80..100) is the bottom row of 40..100
        Assert.Equal(new Rect(0f, 40f, 200f, 20f), list.SelectedItem!.Rect);

        root.Update(UiInput.Press);
        Assert.Equal(new[] { 4 }, activated);

        list.Select(1);
        Assert.Same(list.Child(1), root.Focused);
        Assert.Equal(new Vector2(0f, 20f), scroll.Offset);

        // The wheel scrolls what is under the pointer, 40 units a notch, within its range.
        root.Update(UiInput.Scroll(new Vector2(10f, 10f), -1f));
        Assert.Equal(new Vector2(0f, 60f), scroll.Offset);
        root.Update(UiInput.Scroll(new Vector2(10f, 10f), -5f));
        Assert.Equal(scroll.MaxOffset, scroll.Offset);
    }

    [Fact]
    public void RemovingTheFocusedWidgetDropsFocus()
    {
        var (root, grid, slots) = Inventory();
        root.Focus(slots[3]);
        grid.Remove(slots[3]);
        Assert.Null(root.Focused);
        Assert.Null(slots[3].Root);
        Assert.Equal(11, root.FocusChain.Count);
        Assert.Same(slots[0], Step(root, UiNavigation.Right));
    }

    // The tooltip shows for what focus rests on (or the pointer), after its delay, below it and on screen.
    [Fact]
    public void TheTooltipFollowsFocusAfterItsDelay()
    {
        var (root, _, slots) = Inventory();
        slots[4].TooltipText = "Iron sword";
        root.Focus(slots[4]);

        root.Update(UiInput.Wait(0.1f));
        root.Update(UiInput.Wait(0.3f));
        Assert.False(root.Tooltip.Visible);
        root.Update(UiInput.Wait(0.3f));
        Assert.True(root.Tooltip.Visible);
        Assert.Same(slots[4], root.Tooltip.Target);
        Assert.Equal("Iron sword", root.Tooltip.Text);
        Assert.Equal(new Rect(slots[4].Rect.X, slots[4].Rect.Bottom + root.Tooltip.Offset, 100f, 20f), root.Tooltip.Rect);
        Assert.NotSame(root.Tooltip, root.HitTestVirtual(RectCentre(root.Tooltip.Rect)));   // never in the way of a click

        Step(root, UiNavigation.Right);
        Assert.False(root.Tooltip.Visible);
    }

    private static Vector2 RectCentre(Rect r) => new(r.X + r.Width * 0.5f, r.Y + r.Height * 0.5f);
}
