#nullable enable
using System.Collections.Generic;
using System.Numerics;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The retained widgets' layout (issue #95, docs/design/13 "As built (widgets)"): two passes, measure
// then arrange, in virtual units, with a monospace ITextMeasure standing in for the font — so every
// number here is one a reader can work out by hand.
public class WidgetLayoutTests
{
    // 10 wide, 20 tall per character.
    internal static readonly MonospaceTextMeasure Mono = new(10f, 20f);

    internal static UiRoot Root(Vector2 viewport = default)
    {
        var root = new UiRoot(Mono);
        if (viewport != default) root.SetViewport(viewport);
        return root;
    }

    [Fact]
    public void AColumnStacksItsChildrenAndMeasuresTheirSumPlusSpacing()
    {
        var root = Root();
        var stack = root.Content.Add(new Stack { Spacing = 5f, Margin = new Thickness(10f) });
        var a = stack.Add(new Label("hello"));          // 50 x 20
        var b = stack.Add(new Label("hi") { MinSize = new Vector2(0f, 30f) });   // 20 x 30
        root.Layout();

        Assert.Equal(new Vector2(50f, 55f), stack.DesiredSize);
        Assert.Equal(new Rect(10f, 10f, 50f, 55f), stack.Rect);
        Assert.Equal(new Rect(10f, 10f, 50f, 20f), a.Rect);
        Assert.Equal(new Rect(10f, 35f, 50f, 30f), b.Rect);   // Fill across: as wide as the column
    }

    [Fact]
    public void ARowGivesWhatIsLeftToTheChildrenThatExpandAndAlignsTheRestAcross()
    {
        var root = Root();
        var row = root.Content.Add(new Stack(Orientation.Row) { Anchors = Anchors.TopWide, MinSize = new Vector2(0f, 40f) });
        var left = row.Add(new Label("ab") { VAlign = Align.Center });     // 20 x 20
        var fill = row.Add(new Label("") { Expand = 1f });
        var right = row.Add(new Label("xyz") { VAlign = Align.End });      // 30 x 20
        root.Layout();

        Assert.Equal(new Rect(0f, 0f, 1280f, 40f), row.Rect);
        Assert.Equal(new Rect(0f, 10f, 20f, 20f), left.Rect);
        Assert.Equal(new Rect(20f, 0f, 1230f, 40f), fill.Rect);
        Assert.Equal(new Rect(1250f, 20f, 30f, 20f), right.Rect);
    }

    [Fact]
    public void ABoxPlacesEachChildByItsAnchorsAndMargins()
    {
        var root = Root();
        var centre = root.Content.Add(new Label("centre") { Anchors = Anchors.Center });                          // 60 x 20
        var corner = root.Content.Add(new Label("hp") { Anchors = Anchors.BottomRight, Margin = new Thickness(0f, 0f, 16f, 8f) });
        var bar = root.Content.Add(new Bar { Anchors = Anchors.TopWide, Margin = new Thickness(100f, 4f, 100f, 0f), MinSize = new Vector2(0f, 12f) });
        root.Layout();

        Assert.Equal(new Rect(610f, 350f, 60f, 20f), centre.Rect);
        Assert.Equal(new Rect(1280f - 16f - 20f, 720f - 8f - 20f, 20f, 20f), corner.Rect);
        Assert.Equal(new Rect(100f, 4f, 1080f, 12f), bar.Rect);   // stretched between the anchor lines, less margins
    }

    [Fact]
    public void AGridPutsItsChildrenInCellsRowByRow()
    {
        var root = Root();
        var grid = root.Content.Add(new Grid { Columns = 3, Spacing = new Vector2(4f, 6f), Margin = new Thickness(10f) });
        var cells = new List<Label>();
        for (int i = 0; i < 7; i++) cells.Add(grid.Add(new Label(new string('x', i + 1))));   // widths 10..70
        root.Layout();

        Assert.Equal(3, grid.UsedColumns);
        Assert.Equal(3, grid.UsedRows);
        // Columns as wide as their widest cell: 70 (0,3,6), 50 (1,4), 60 (2,5); rows 20 tall.
        Assert.Equal(new Vector2(70f + 50f + 60f + 8f, 60f + 12f), grid.DesiredSize);
        Assert.Equal(new Rect(10f, 10f, 70f, 20f), cells[0].Rect);
        Assert.Equal(new Rect(84f, 10f, 50f, 20f), cells[1].Rect);
        Assert.Equal(new Rect(138f, 36f, 60f, 20f), cells[5].Rect);
        Assert.Equal(new Rect(10f, 62f, 70f, 20f), cells[6].Rect);

        grid.Uniform = true;   // every cell the size of the biggest
        root.Layout();
        Assert.Equal(new Rect(10f + 74f, 10f + 26f, 70f, 20f), cells[4].Rect);
    }

    [Fact]
    public void PaddingAndMinSizeAndHiddenChildren()
    {
        var root = Root();
        var stack = root.Content.Add(new Stack { Padding = new Thickness(8f, 4f) });
        var hidden = stack.Add(new Label("not here") { Visible = false });
        var shown = stack.Add(new Label("here"));
        var small = stack.Add(new Label("") { MinSize = new Vector2(100f, 0f) });
        root.Layout();

        Assert.Equal(new Vector2(100f + 16f, 40f + 8f), stack.DesiredSize);
        Assert.Equal(new Rect(8f, 4f, 100f, 20f), shown.Rect);
        Assert.Equal(new Rect(8f, 24f, 100f, 20f), small.Rect);
        Assert.Equal(new Rect(8f, 4f, 100f, 40f), stack.ContentRect);

        hidden.Visible = true;
        root.Layout();
        Assert.Equal(new Rect(8f, 4f, 100f, 20f), hidden.Rect);
        Assert.Equal(new Rect(8f, 24f, 100f, 20f), shown.Rect);
    }

    // Virtual units: the design resolution scaled by the smaller ratio, the other axis getting the room.
    [Fact]
    public void LayoutIsInVirtualUnitsScaledToTheViewport()
    {
        var root = Root(new Vector2(1920f, 1080f));
        Assert.Equal(1.5f, root.Scale);
        Assert.Equal(new Vector2(1280f, 720f), root.Size);

        var label = root.Content.Add(new Label("hud") { Anchors = Anchors.BottomRight });
        root.Layout();
        Assert.Equal(new Rect(1250f, 700f, 30f, 20f), label.Rect);
        Assert.Equal(new Rect(1875f, 1050f, 45f, 30f), root.ToPixels(label.Rect));

        root.SetViewport(new Vector2(2560f, 1080f));   // ultrawide: the same height, more width
        Assert.Equal(1.5f, root.Scale);
        Assert.Equal(720f, root.Size.Y);
        Assert.True(root.Layout());
        Assert.Equal(root.Size.X - 30f, label.Rect.X, 3);
        Assert.Equal(2560f, root.ToPixels(label.Rect).Right, 2);
    }

    // Dirty flags: a clean tree lays out nothing, and a change lays out again and moves Version.
    [Fact]
    public void OnlyADirtyTreeIsLaidOutAgain()
    {
        var root = Root();
        var stack = root.Content.Add(new Stack());
        var title = stack.Add(new Label("Bag"));
        var below = stack.Add(new Label("sword"));
        Assert.True(root.Layout());
        Assert.False(root.Layout());
        int version = root.Version;
        Assert.False(root.Layout());
        Assert.Equal(version, root.Version);

        title.Text = "Bag";   // the same text: nothing to do
        Assert.False(root.Layout());

        title.Text = "Bag\nof holding";   // two lines now
        Assert.True(root.Layout());
        Assert.NotEqual(version, root.Version);
        Assert.Equal(new Rect(0f, 40f, 100f, 20f), below.Rect);

        var bar = stack.Add(new Bar { MinSize = new Vector2(100f, 10f), Max = 10f });
        root.Layout();
        version = root.Version;
        bar.Value = 5f;   // only what is drawn: no layout, but the version moves
        Assert.False(root.Layout());
        Assert.NotEqual(version, root.Version);
        Assert.Equal(new Rect(bar.Rect.X, bar.Rect.Y, 50f, 10f), bar.FillRect);
    }

    [Fact]
    public void AScrollShowsPartOfItsContentAndClipsTheRest()
    {
        var root = Root();
        var scroll = root.Content.Add(new Scroll { MinSize = new Vector2(200f, 100f), Margin = new Thickness(20f) });
        var list = new Stack();
        scroll.Content = list;
        var rows = new List<Label>();
        for (int i = 0; i < 10; i++) rows.Add(list.Add(new Label($"row {i}")));   // 20 tall each: 200 in all
        root.Layout();

        Assert.Equal(new Rect(20f, 20f, 200f, 100f), scroll.Rect);
        Assert.Equal(new Vector2(0f, 100f), scroll.MaxOffset);
        Assert.Equal(new Rect(20f, 20f, 200f, 100f), rows[0].Clip);
        Assert.Same(rows[1], root.HitTestVirtual(new Vector2(30f, 45f)));
        Assert.Null(root.HitTestVirtual(new Vector2(30f, 150f)));   // row 6 is there, but clipped

        scroll.Offset = new Vector2(0f, 500f);   // clamped to the end
        root.Layout();
        Assert.Equal(new Vector2(0f, 100f), scroll.Offset);
        Assert.Equal(20f, rows[5].Rect.Y);
        Assert.Same(rows[9], root.HitTestVirtual(new Vector2(30f, 110f)));

        scroll.ScrollIntoView(rows[2]);
        root.Layout();
        Assert.Equal(new Vector2(0f, 40f), scroll.Offset);
        Assert.Equal(20f, rows[2].Rect.Y);
    }

    [Fact]
    public void AnImageKeepsItsAspectInsideTheRoomItIsGiven()
    {
        var root = Root();
        var image = root.Content.Add(new Image("ui/portrait.png", new Vector2(64f, 32f)) { Anchors = Anchors.TopLeft, MinSize = new Vector2(64f, 64f) });
        root.Layout();
        Assert.Equal(new Rect(0f, 0f, 64f, 64f), image.Rect);
        Assert.Equal(new Rect(0f, 16f, 64f, 32f), image.ImageRect);
    }

    // #96 builds trees from records by type name, and #97 draws by walking them.
    [Fact]
    public void EveryWidgetTypeIsMadeByItsNameAndTheTreeWalksInDrawingOrder()
    {
        foreach (string name in WidgetTypes.Names)
            Assert.Equal(name, WidgetTypes.Create(name)!.TypeName);
        Assert.Null(WidgetTypes.Create("marquee"));

        var root = Root();
        var stack = root.Content.Add(new Stack { Name = "menu" });
        stack.Add(new Button("New") { Name = "new" });
        stack.Add(new Button("Load") { Visible = false });
        stack.Add(new Button("Quit") { Name = "quit" });
        Assert.Same(stack, root.Content.Find("menu"));
        Assert.Equal("Quit", root.Content.Find<Button>("quit")!.Text);

        var visitor = new Recorder { Seen = new List<string>() };
        root.Walk(ref visitor);
        Assert.Equal(new[] { "box", "stack", "button:New", "button:Quit" }, visitor.Seen);
        Assert.Equal(0, visitor.Depth);
    }

    private struct Recorder : IWidgetVisitor
    {
        public List<string> Seen;
        public int Depth;

        public bool Enter(Widget widget)
        {
            Seen.Add(widget is Label label ? $"{widget.TypeName}:{label.Text}" : widget.TypeName);
            Depth++;
            return true;
        }

        public void Leave(Widget widget) => Depth--;
    }
}
