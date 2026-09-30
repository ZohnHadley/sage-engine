#nullable enable
using System.Numerics;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Zero allocation per frame (02 §4.6, issue #95): the retained UI is laid out and updated every frame
// at display rate, so a clean tree must cost nothing, and neither may moving focus or laying out again
// after a resize. Allocation is measured per thread, alone (AllocationProbe).
[Collection(MeasurementsCollection.Name)]
public class WidgetAllocationTests
{
    public WidgetAllocationTests() { _ = TestEnv.UserRoot; }

    // A screen with every kind of widget in it: a window (Box) holding a title, a scrolling item list,
    // an inventory grid, a bar and a picture, with a tooltip up.
    private static UiRoot Screen()
    {
        var (root, grid, slots) = WidgetFocusTests.Inventory(new Vector2(1920f, 1080f));
        var window = root.Content.Add(new Box { Anchors = Anchors.Right, Padding = new Thickness(8f), HitTestable = true });
        var column = window.Add(new Stack { Spacing = 4f });
        column.Add(new Label("Loot"));
        var scroll = column.Add(new Scroll { MinSize = new Vector2(240f, 100f) });
        var list = new ItemList();
        scroll.Content = list;
        for (int i = 0; i < 20; i++) list.Add(new Label($"item {i}"));
        column.Add(new Bar { MinSize = new Vector2(240f, 10f), Value = 0.5f });
        column.Add(new Image("ui/coin.png", new Vector2(16f, 16f)));
        slots[0].TooltipText = "Empty slot";
        _ = grid;
        root.Focus(slots[0]);
        for (int i = 0; i < 60; i++) root.Update(UiInput.Wait(1f / 60f));
        Assert.True(root.Tooltip.Visible);
        return root;
    }

    [Fact]
    public void LayingOutAndUpdatingACleanTreeAllocatesNothing()
    {
        var root = Screen();
        var idle = UiInput.Wait(1f / 60f);
        root.Update(idle);
        int version = root.Version;

        AllocationProbe.AssertNone(500, () =>
        {
            root.Layout();
            root.Update(idle);
        });
        Assert.False(root.Layout());
        Assert.Equal(version, root.Version);   // and it really was clean: nothing moved
    }

    // Not only when clean: navigating (focus, scrolling a list into view, the tooltip coming and going)
    // and laying the whole tree out again after the window is resized allocate nothing either, once the
    // tree has been built.
    [Fact]
    public void NavigatingAndRelayingOutAllocateNothing()
    {
        var root = Screen();
        var steps = new[] { UiNavigation.Right, UiNavigation.Down, UiNavigation.Left, UiNavigation.Up, UiNavigation.Next, UiNavigation.Previous };
        for (int i = 0; i < steps.Length * 4; i++) root.Update(UiInput.Nav(steps[i % steps.Length]));   // warm up

        int frame = 0;
        AllocationProbe.AssertNone(600, () =>
        {
            root.Update(UiInput.Nav(steps[frame % steps.Length]));
            root.Update(UiInput.Move(new Vector2(frame % 800, 60f)));
            root.SetViewport(frame % 2 == 0 ? new Vector2(1920f, 1080f) : new Vector2(1280f, 1024f));
            root.Layout();
            frame++;
        });
    }
}
