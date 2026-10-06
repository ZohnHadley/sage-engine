#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Drag and drop, spans and pictures in Sage.UI (issue #346): generic, so an inventory, a spell bar or a
// key-binding table use the same drag — a source, its row as the payload, the target under the pointer
// and a ghost that follows it — and the same grid that lets a child take several cells.
public class DragDropTests
{
    public DragDropTests() { _ = TestEnv.UserRoot; }

    // A 4 × 3 grid of 64-unit draggable slots, 4 apart, 10 in: slot i is at (10 + 68c, 10 + 68r).
    private static (UiRoot Root, Grid Grid, List<Button> Slots) Slots()
    {
        var (root, grid, slots) = WidgetFocusTests.Inventory();
        for (int i = 0; i < slots.Count; i++)
        {
            slots[i].Draggable = true;
            slots[i].Data = i;
        }
        return (root, grid, slots);
    }

    private static Vector2 Centre(Widget widget) => new(widget.Rect.X + widget.Rect.Width / 2f, widget.Rect.Y + widget.Rect.Height / 2f);

    [Fact]
    public void ADraggableWidgetIsDraggedAndDroppedOnWhatIsUnderThePointer()
    {
        var (root, _, slots) = Slots();
        var pressed = new List<Button>();
        foreach (var slot in slots) slot.Pressed += pressed.Add;

        // Held on slot 1: not yet a drag, and not activated either — that waits for the release.
        var start = Centre(slots[1]);
        var result = root.Update(UiInput.Hold(start));
        Assert.Null(result.Activated);
        Assert.Same(slots[1], root.Focused);
        Assert.False(root.IsDragging);

        // A few units is still a click in the making; past DragThreshold it is a drag.
        result = root.Update(UiInput.Drag(start + new Vector2(3f, 0f)));
        Assert.False(result.DragStarted);
        result = root.Update(UiInput.Drag(start + new Vector2(20f, 0f)));
        Assert.True(result.DragStarted);
        Assert.True(root.IsDragging);
        Assert.Same(slots[1], result.Drag.Source);
        Assert.Equal(1, result.Drag.Payload);                                            // its Data: the row it shows
        Assert.Equal(start, result.Drag.Start);
        Assert.Equal(new Vector2(20f, 0f), result.Drag.Offset);
        Assert.Equal(new Rect(slots[1].Rect.X + 20f, slots[1].Rect.Y, 64f, 64f), result.Drag.Ghost);

        // Over slot 6, focus follows it: what it would land on.
        result = root.Update(UiInput.Drag(Centre(slots[6])));
        Assert.True(result.DragMoved);
        Assert.Same(slots[6], result.Drag.Target);
        Assert.Same(slots[6], root.Focused);

        // Let go: dropped on slot 6, and nothing was activated on the way.
        result = root.Update(UiInput.Release(Centre(slots[6])));
        Assert.True(result.Dropped);
        Assert.False(result.Drag.Cancelled);
        Assert.Same(slots[1], result.Drag.Source);
        Assert.Same(slots[6], result.Drag.Target);
        Assert.False(root.IsDragging);
        Assert.Empty(pressed);

        // Let go over nothing of the tree: dropped on nothing (the world, for an inventory).
        root.Update(UiInput.Hold(Centre(slots[2])));
        Assert.True(root.Update(UiInput.Drag(new Vector2(900f, 600f))).DragStarted);
        result = root.Update(UiInput.Release(new Vector2(900f, 600f)));
        Assert.True(result.Dropped);
        Assert.Null(result.Drag.Target);

        // Back while dragging puts it back: a cancelled drop, and the Back is used.
        root.Update(UiInput.Hold(Centre(slots[3])));
        root.Update(UiInput.Drag(Centre(slots[7])));
        result = root.Update(new UiInput { Back = true, Pointer = Centre(slots[7]), PointerDown = true });   // the button still held
        Assert.True(result.Dropped);
        Assert.True(result.Drag.Cancelled);
        Assert.False(result.Back);
        Assert.False(root.IsDragging);
        Assert.Empty(pressed);
    }

    [Fact]
    public void AClickOnADraggableWidgetActivatesItOnTheRelease()
    {
        var (root, _, slots) = Slots();
        var pressed = new List<Button>();
        foreach (var slot in slots) slot.Pressed += pressed.Add;

        Assert.Null(root.Update(UiInput.Hold(Centre(slots[5]))).Activated);
        var result = root.Update(UiInput.Release(Centre(slots[5]) + new Vector2(2f, 2f)));   // let go where it was
        Assert.Same(slots[5], result.Activated);
        Assert.False(result.Dropped);
        Assert.Equal(new[] { slots[5] }, pressed);

        // A press and release in one frame (UiInput.Click) is a click too, and a plain button still
        // activates on the press as it always did.
        Assert.Same(slots[4], root.Update(UiInput.Click(Centre(slots[4]))).Activated);
        slots[8].Draggable = false;
        Assert.Same(slots[8], root.Update(UiInput.Hold(Centre(slots[8]))).Activated);
        root.Update(UiInput.Release(Centre(slots[8])));

        // Let go somewhere else without having moved far: neither a drag nor a click.
        root.DragThreshold = 1000f;
        root.Update(UiInput.Hold(Centre(slots[0])));
        root.Update(UiInput.Drag(Centre(slots[1])));
        result = root.Update(UiInput.Release(Centre(slots[1])));
        Assert.Null(result.Activated);
        Assert.False(result.Dropped);
        Assert.Equal(new[] { slots[5], slots[4], slots[8] }, pressed);
    }

    [Fact]
    public void ACommandGoesToTheFocusedWidgetOrTheOneBeingDragged()
    {
        var (root, _, slots) = Slots();
        root.Focus(slots[2]);
        var result = root.Update(UiInput.Do(UiCommand.Split));
        Assert.Equal(UiCommand.Split, result.Command);
        Assert.Same(slots[2], result.CommandTarget);

        root.Update(UiInput.Hold(Centre(slots[0])));
        root.Update(UiInput.Drag(Centre(slots[5])));
        result = root.Update(new UiInput { Command = UiCommand.Rotate, Pointer = Centre(slots[5]), PointerDown = true });
        Assert.Equal(UiCommand.Rotate, result.Command);
        Assert.Same(slots[0], result.CommandTarget);                                      // not slot 5, which has focus

        // The client's buttons: MenuAlternate, MenuRotate, MenuSplit — one a frame.
        Assert.Equal(UiCommand.Alternate, UiInputMap.From(new UiControls { Alternate = true, Rotate = true }).Command);
        Assert.Equal(UiCommand.Rotate, UiInputMap.From(new UiControls { Rotate = true }).Command);
        Assert.Equal(UiCommand.Split, UiInputMap.From(new UiControls { Split = true }).Command);
        Assert.Equal(UiCommand.None, UiInputMap.From(new UiControls()).Command);
    }

    [Fact]
    public void AGridChildSpansCellsAndTheOthersFlowAroundIt()
    {
        var root = WidgetLayoutTests.Root();
        var grid = root.Content.Add(new Grid { Columns = 4, Spacing = new Vector2(4f, 4f), Uniform = true });
        var cells = new List<Button>();
        for (int i = 0; i < 12; i++) cells.Add(grid.Add(new Button { Name = $"c{i}", MinSize = new Vector2(20f, 20f) }));

        // Cell 1 takes 2 × 2; the three squares it covers are hidden, as an item's are, and the rest
        // keep their places.
        cells[1].ColumnSpan = 2;
        cells[1].RowSpan = 2;
        cells[2].Visible = cells[5].Visible = cells[6].Visible = false;
        root.Layout();
        Assert.Equal(new Rect(24f, 0f, 44f, 44f), cells[1].Rect);
        Assert.Equal(new Rect(72f, 0f, 20f, 20f), cells[3].Rect);
        Assert.Equal(new Rect(0f, 24f, 20f, 20f), cells[4].Rect);
        Assert.Equal(new Rect(72f, 24f, 20f, 20f), cells[7].Rect);
        Assert.Equal(new Rect(24f, 48f, 20f, 20f), cells[9].Rect);
        Assert.Equal((4, 3), (grid.UsedColumns, grid.UsedRows));

        // The square under a point, the spacing counting as the square before it; none outside.
        Assert.True(grid.CellAt(new Vector2(50f, 30f), out int column, out int row));
        Assert.Equal((2, 1), (column, row));
        Assert.True(grid.CellAt(new Vector2(22f, 22f), out column, out row));
        Assert.Equal((0, 0), (column, row));
        Assert.False(grid.CellAt(new Vector2(200f, 10f), out _, out _));

        // The pointer and the D-pad land on the span as one thing.
        Assert.Same(cells[1], root.HitTestVirtual(new Vector2(60f, 40f)));
        root.Focus(cells[0]);
        root.Update(UiInput.Nav(UiNavigation.Right));
        Assert.Same(cells[1], root.Focused);
        root.Update(UiInput.Nav(UiNavigation.Right));
        Assert.Same(cells[3], root.Focused);

        // Without spans the grid is as it was: every child its own cell.
        cells[1].ColumnSpan = cells[1].RowSpan = 1;
        cells[2].Visible = cells[5].Visible = cells[6].Visible = true;
        root.Layout();
        Assert.Equal(new Rect(48f, 0f, 20f, 20f), cells[2].Rect);
        Assert.Equal(new Rect(24f, 24f, 20f, 20f), cells[5].Rect);
    }

    [Fact]
    public void AButtonsIconIsDrawnUnderItsTextAndTheGhostFollowsThePointer()
    {
        using var app = WidgetDrawingTests.Boot();
        var styles = app.World.Resources.Get<UiStyles>();
        var root = WidgetLayoutTests.Root(UiRoot.DefaultDesignSize * 2f);   // scale 2
        var grid = root.Content.Add(new Grid { Columns = 2, Spacing = new Vector2(4f, 4f) });
        var rifle = grid.Add(new Button("rifle") { Name = "rifle", Style = "uitest:button", MinSize = new Vector2(40f, 20f),
                                                    Icon = "icons/rifle.png", IconTurned = true, Draggable = true });
        grid.Add(new Button("empty") { Name = "empty", Style = "uitest:button", MinSize = new Vector2(40f, 20f) });

        var plan = new UiRenderPlan();
        plan.Update(root, styles);
        var drawn = plan.Commands.ToArray().Where(c => c.Widget == rifle).ToArray();
        Assert.Equal(new[] { UiDrawKind.Rect, UiDrawKind.Image, UiDrawKind.Text }, drawn.Select(c => c.Kind));   // box, picture, then text over it
        Assert.Equal(root.ToPixels(rifle.ContentRect), drawn[1].Rect);
        Assert.Equal("icons/rifle.png", drawn[1].Texture.ToString());
        Assert.True(drawn[1].Turned);

        // Dragged 30 units right and 10 down: the rifle again, last, moved by 60 × 20 pixels and faded.
        root.Update(UiInput.Hold(root.ToPixels(Centre(rifle))));
        Assert.True(root.Update(UiInput.Drag(root.ToPixels(Centre(rifle) + new Vector2(30f, 10f)))).DragStarted);
        Assert.True(plan.Update(root, styles));
        var all = plan.Commands.ToArray();
        var ghost = all.Skip(all.Length - 3).ToArray();
        Assert.All(ghost, g => Assert.Same(rifle, g.Widget));
        Assert.Equal(new[] { UiDrawKind.Rect, UiDrawKind.Image, UiDrawKind.Text }, ghost.Select(c => c.Kind));
        var at = root.ToPixels(rifle.ContentRect);
        Assert.Equal(new Rect(at.X + 60f, at.Y + 20f, at.Width, at.Height), ghost[1].Rect);
        Assert.Equal(UiColour.Fade(all.First(c => c.Widget == rifle).Colour, UiRenderPlan.GhostOpacity), ghost[0].Colour);

        // Let go: no ghost.
        root.Update(UiInput.Release(root.ToPixels(Centre(rifle))));
        plan.Update(root, styles);
        Assert.Equal(drawn.Length, plan.Commands.ToArray().Count(c => c.Widget == rifle));
    }
}
