#nullable enable
using System.Linq;
using System.Numerics;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// A box that clips (`"clip": true`) and the `w` and `h` bindings (issue #349, what a map's picture and fog
// are made of): with `x` and `y` a node's anchors are a rect a view-model places — overhanging its box,
// unlike a point — and the box cuts what it holds off at its edges, in the render plan and for the pointer.
public class ClipBoxTests
{
    public ClipBoxTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
    [
      { "type": "ui_layout", "id": "rects",
        "nodes": {
          "area":  { "widget": "box", "anchors": "top_left", "minSize": [100, 100], "clip": true },
          "pic":   { "widget": "image", "parent": "area", "bind": "source", "keepAspect": false,
                     "bindings": { "x": "x", "y": "y", "w": "w", "h": "h" } },
          "loose": { "widget": "box", "anchors": "top_left", "margin": [200, 0, 0, 0], "minSize": [100, 100] },
          "over":  { "widget": "image", "parent": "loose", "source": "textures/b.png", "anchors": [-0.5, 0, 1.5, 1] }
        } },
      { "type": "screen", "id": "rects", "layout": "rects", "viewModel": "uitest_rects" }
    ]
    """;

    public sealed class Rects : IViewModel
    {
        public string Source { get; set; } = "textures/a.png";
        public float X { get; set; } = -0.5f;
        public float Y { get; set; } = 0.25f;
        public float W { get; set; } = 2f;
        public float H { get; set; } = 0.5f;
    }

    private static HeadlessApp Boot() => HeadlessApp.Bare().With(new UiModule()).Mount(UiRecordTests.Content(Records))
        .OnRegistered(app => app.Engine.Vocabularies.Of<IViewModel>().Register("uitest_rects", typeof(Rects), () => new Rects()))
        .Boot("ui");

    private static string Kinds(UiRenderPlan plan) =>
        string.Join(" ", plan.Commands.ToArray().Select(c => c.Kind + (c.Widget?.Name is { } n ? ":" + n : "")));

    [Fact]
    public void AClipBoxCutsOffARectItsViewModelPlacesPastItsEdges()
    {
        using var app = Boot();
        var screen = app.World.Resources.Get<UiScreens>().OpenScreen(new RecordId("uitest", "rects"), new UiBindContext(app.World));
        var model = Assert.IsType<Rects>(screen.ViewModel);
        var root = new UiRoot(new MonospaceTextMeasure(6f, 9f));
        root.SetViewport(UiRoot.DefaultDesignSize);
        root.Content.Add(screen.Root);
        var plan = new UiRenderPlan();
        plan.Update(root, app.World.Resources.Get<UiStyles>());

        var area = screen.View.Find<Box>("area")!;
        var pic = screen.View.Find<Image>("pic")!;
        Assert.True(area.ClipChildren);
        Assert.Equal(new Anchors(-0.5f, 0.25f, 1.5f, 0.75f), pic.Anchors);                     // a rect, not clamped to the box
        Assert.Equal(new Rect(-50f, 25f, 200f, 50f), pic.Rect);
        Assert.Equal(new Rect(0f, 0f, 100f, 100f), pic.Clip);                                   // what the box cuts it to
        Assert.Equal("PushClip:area Image:pic PopClip:area Image:over", Kinds(plan));          // a box without `clip` pushes none
        Assert.Equal(new Rect(0f, 0f, 100f, 100f), plan.Commands[0].Rect);

        // Moved by the view-model: anchors follow on the next refresh.
        model.X = 0.1f;
        model.W = 0.2f;
        screen.Refresh();
        Assert.Equal(new Anchors(0.1f, 0.25f, 0.3f, 0.75f), pic.Anchors);
    }
}
