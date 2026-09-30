#nullable enable
using System;
using System.Linq;
using System.Numerics;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Drawing widgets, headless (docs/design/13 "As built (drawing)", issue #97): the render plan the
// client replays into UiDraw — what is drawn for each widget and style, in what order, in which pixels,
// under which clip — and the nine-slice arithmetic, colour blending and device-to-UiInput mapping the
// client uses. No window: what a screenshot would show is asserted as data.
public class WidgetDrawingTests
{
    public WidgetDrawingTests() { _ = TestEnv.UserRoot; }

    internal const string Styles = """
    [
      { "type": "ui_style", "id": "window", "background": "#101010", "border": "#FFFFFF", "borderWidth": 2 },
      { "type": "ui_style", "id": "text", "textColour": "#E0E0E0" },
      { "type": "ui_style", "id": "button", "base": "text", "background": "#202020",
        "states": { "hover": { "background": "#304060" }, "focused": { "background": "#3050A0" },
                    "pressed": { "background": "#A0A0A0" }, "disabled": { "textColour": "#808080" } } },
      { "type": "ui_style", "id": "bar", "background": "#300000", "fill": "#C04040" },
      { "type": "ui_style", "id": "icon", "tint": "#FF8080", "slice": 3 },
      { "type": "ui_style", "id": "framed", "image": "textures/frame.png", "slice": [8, 6], "tint": "#8080FF" }
    ]
    """;

    internal static HeadlessApp Boot() => UiRecordTests.Boot(UiRecordTests.Content(Styles));

    private static uint Rgb(byte r, byte g, byte b) => ColourJsonConverter.Pack(r, g, b);

    private static UiRoot Root(float scale = 1f)
    {
        var root = new UiRoot(new MonospaceTextMeasure(6f, 9f));
        root.SetViewport(UiRoot.DefaultDesignSize * scale);
        return root;
    }

    private static string Kinds(UiRenderPlan plan) =>
        string.Join(" ", plan.Commands.ToArray().Select(c => c.Kind + (c.Widget?.Name is { } n ? ":" + n : "")));

    [Fact]
    public void ThePlanDrawsEachWidgetsStyleInTreeOrderInPixels()
    {
        using var app = Boot();
        var styles = app.World.Resources.Get<UiStyles>();
        var root = Root(scale: 2f);
        var window = root.Content.Add(new Stack { Name = "window", Style = "uitest:window", Padding = new Thickness(4f) });
        window.Add(new Label("Hi") { Name = "label", Style = "uitest:text", TextScale = 2f });
        window.Add(new Bar { Name = "bar", Style = "uitest:bar", MinSize = new Vector2(100f, 10f), Value = 0.5f });
        window.Add(new Image("icons/a.png", new Vector2(16f, 16f)) { Name = "icon", Style = "uitest:icon" });
        window.Add(new Image("icons/b.png", new Vector2(16f, 16f)) { Name = "plain" });
        window.Add(new Label("") { Name = "empty" });

        var plan = new UiRenderPlan();
        Assert.True(plan.Update(root, styles));

        // A window's background then border; a label with no background is only its text; a bar is its
        // track then its fill; a picture is its image. Parents before children, siblings in order.
        Assert.Equal("Rect:window Border:window Text:label Rect:bar Rect:bar Image:icon Image:plain", Kinds(plan));

        var c = plan.Commands;
        Assert.Equal(new Rect(0f, 0f, 216f, (4f + 18f + 10f + 16f + 16f + 9f + 4f) * 2f), c[0].Rect);   // virtual × 2
        Assert.Equal(Rgb(0x10, 0x10, 0x10), c[0].Colour);
        Assert.Equal(Rgb(255, 255, 255), c[1].Colour);
        Assert.Equal(4f, c[1].Size);                                                     // borderWidth 2 × scale 2

        Assert.Equal("Hi", c[2].Text);
        Assert.Equal(new Rect(8f, 8f, 48f, 36f), c[2].Rect);                           // 2 chars × 6 × textScale 2 × 2
        Assert.Equal(4f, c[2].Size);                                                     // pixels per font pixel
        Assert.Equal(Rgb(0xE0, 0xE0, 0xE0), c[2].Colour);

        Assert.Equal(new Rect(8f, 44f, 200f, 20f), c[3].Rect);                         // the track
        Assert.Equal(new Rect(8f, 44f, 100f, 20f), c[4].Rect);                         // half full
        Assert.Equal(Rgb(0xC0, 0x40, 0x40), c[4].Colour);

        // A sliced picture fills its content rect; an unsliced one keeps its aspect (ImageRect).
        Assert.Equal(new Rect(8f, 64f, 200f, 32f), c[5].Rect);
        Assert.Equal(new Thickness(3f), c[5].Slice);
        Assert.Equal(AssetPath.Intern("icons/a.png"), c[5].Texture);
        Assert.Equal(Rgb(0xFF, 0x80, 0x80), c[5].Colour);
        Assert.Equal(2f, c[5].Size);
        Assert.Equal(new Rect(8f + 84f, 96f, 32f, 32f), c[6].Rect);
        Assert.Equal(Thickness.Zero, c[6].Slice);
    }

    [Fact]
    public void AStylesImageIsDrawnOverItsBackgroundNineSliced()
    {
        using var app = Boot();
        var styles = app.World.Resources.Get<UiStyles>();
        var root = Root();
        root.Content.Add(new Box { Name = "frame", Style = "uitest:framed", MinSize = new Vector2(50f, 40f) });
        var plan = new UiRenderPlan();
        plan.Update(root, styles);

        var image = Assert.Single(plan.Commands.ToArray());
        Assert.Equal(UiDrawKind.Image, image.Kind);
        Assert.Equal(AssetPath.Intern("textures/frame.png"), image.Texture);
        Assert.Equal(new Thickness(8f, 6f), image.Slice);
        Assert.Equal(new Rect(0f, 0f, 50f, 40f), image.Rect);
    }

    [Fact]
    public void TextSitsWhereItsAlignmentSaysAndCentredDown()
    {
        using var app = Boot();
        var root = Root();
        var column = root.Content.Add(new Stack());
        column.Add(new Label("abc") { Name = "start", MinSize = new Vector2(100f, 20f) });
        column.Add(new Label("abc") { Name = "centre", MinSize = new Vector2(100f, 20f), TextAlign = Align.Center });
        column.Add(new Label("abc") { Name = "end", MinSize = new Vector2(100f, 20f), TextAlign = Align.End, Padding = new Thickness(2f) });
        var plan = new UiRenderPlan();
        plan.Update(root, app.World.Resources.Get<UiStyles>());

        var c = plan.Commands;
        Assert.Equal(new Rect(0f, 5.5f, 18f, 9f), c[0].Rect);                          // (20 - 9) / 2 down
        Assert.Equal(new Rect(41f, 25.5f, 18f, 9f), c[1].Rect);                        // (100 - 18) / 2 across
        Assert.Equal(new Rect(100f - 2f - 18f, 45.5f, 18f, 9f), c[2].Rect);            // against the padding
    }

    // A Scroll pushes its viewport as a clip around what it holds and pops it after; a Scroll inside
    // another pushes the intersection of both, and its pop restores the outer one. Rows scrolled wholly
    // out of view are not drawn at all.
    [Fact]
    public void AScrollClipsWhatItHoldsAndNestedClipsIntersect()
    {
        using var app = Boot();
        var root = Root();
        var outer = root.Content.Add(new Scroll { Name = "outer", MinSize = new Vector2(100f, 25f) });
        var column = new Stack();
        outer.Content = column;
        for (int i = 0; i < 3; i++) column.Add(new Label($"a{i}") { Name = $"a{i}" });
        var inner = column.Add(new Scroll { Name = "inner", MinSize = new Vector2(0f, 20f) });
        var innerColumn = new Stack();
        inner.Content = innerColumn;
        for (int i = 0; i < 4; i++) innerColumn.Add(new Label($"b{i}") { Name = $"b{i}" });
        for (int i = 3; i < 10; i++) column.Add(new Label($"a{i}") { Name = $"a{i}" });
        outer.Offset = new Vector2(0f, 15f);

        var plan = new UiRenderPlan();
        plan.Update(root, app.World.Resources.Get<UiStyles>());

        // a0 (-15..-6) is scrolled out and not drawn; a1 overhangs the top and is. The inner scroll sits at
        // 12..32 in an outer view of 0..25: its clip is 12..25, and b1 (21..30) is the last it shows.
        Assert.Equal("PushClip:outer Text:a1 Text:a2 PushClip:inner Text:b0 Text:b1 PopClip:inner PopClip:outer", Kinds(plan));
        var c = plan.Commands;
        Assert.Equal(new Rect(0f, 0f, 100f, 25f), c[0].Rect);
        Assert.Equal(new Rect(0f, 12f, 100f, 13f), c[3].Rect);
        Assert.Equal(new Rect(0f, 0f, 100f, 25f), c[6].Rect);   // back to the outer clip
        Assert.Equal(default, c[7].Rect);                        // and to none

        // Scrolling moves what is drawn without anything else changing; the inner scroll, now wholly below
        // the outer's view, is skipped with everything in it.
        outer.Offset = Vector2.Zero;
        Assert.True(plan.Update(root, app.World.Resources.Get<UiStyles>()));
        Assert.Equal("PushClip:outer Text:a0 Text:a1 Text:a2 PopClip:outer", Kinds(plan));
    }

    // The plan is cached against everything it reads: a frame where nothing changed reuses it, and
    // each kind of change — a bar's value, focus, the pressed widget, the viewport, a string table —
    // works it out again.
    [Fact]
    public void ThePlanIsRebuiltOnlyWhenWhatItShowsChanged()
    {
        using var app = Boot();
        var styles = app.World.Resources.Get<UiStyles>();
        var root = Root();
        var column = root.Content.Add(new Stack());
        var bar = column.Add(new Bar { Style = "uitest:bar", MinSize = new Vector2(50f, 5f) });
        var button = column.Add(new Button("go") { Style = "uitest:button" });
        var plan = new UiRenderPlan();

        Assert.True(plan.Update(root, styles));
        for (int i = 0; i < 5; i++) Assert.False(plan.Update(root, styles));
        Assert.Equal(1, plan.Builds);

        bar.Value = 0.5f;
        Assert.True(plan.Update(root, styles));
        root.Focus(button);
        Assert.True(plan.Update(root, styles));
        Assert.True(plan.Update(root, styles, pressed: button));
        Assert.False(plan.Update(root, styles, pressed: button));
        root.SetViewport(new Vector2(1920f, 1080f));
        Assert.True(plan.Update(root, styles, pressed: button));
        Assert.True(plan.Update(root, styles, pressed: button, textVersion: 7));
        Assert.False(plan.Update(root, styles, pressed: button, textVersion: 7));
        Assert.Equal(6, plan.Builds);
    }

    // The focus highlight eases (UiLayer.FocusBlend): the focused widget's commands carry the colour it
    // had before as From, the widget focus left carries the focused colour as From, and the client
    // blends between them — the plan itself does not change while the highlight moves.
    [Fact]
    public void FocusCarriesTheColourItEasesFromAndPressedWins()
    {
        using var app = Boot();
        var styles = app.World.Resources.Get<UiStyles>();
        var root = Root();
        var column = root.Content.Add(new Stack());
        var first = column.Add(new Button("one") { Name = "first", Style = "uitest:button" });
        var second = column.Add(new Button("two") { Name = "second", Style = "uitest:button" });
        var off = column.Add(new Button("off") { Name = "off", Style = "uitest:button", Enabled = false });
        var plan = new UiRenderPlan();

        root.Focus(first);
        plan.Update(root, styles);
        var rect = plan.Commands.ToArray().First(c => c.Widget == first && c.Kind == UiDrawKind.Rect);
        Assert.True(rect.Blend);
        Assert.Equal(Rgb(0x20, 0x20, 0x20), rect.From);
        Assert.Equal(Rgb(0x30, 0x50, 0xA0), rect.Colour);
        Assert.False(plan.Commands.ToArray().First(c => c.Widget == first && c.Kind == UiDrawKind.Text).Blend);   // same colour both ends

        root.Focus(second);
        plan.Update(root, styles, previousFocus: first);
        var left = plan.Commands.ToArray().First(c => c.Widget == first && c.Kind == UiDrawKind.Rect);
        Assert.True(left.Blend);
        Assert.Equal(Rgb(0x30, 0x50, 0xA0), left.From);
        Assert.Equal(Rgb(0x20, 0x20, 0x20), left.Colour);
        Assert.True(plan.Commands.ToArray().First(c => c.Widget == second && c.Kind == UiDrawKind.Rect).Blend);

        plan.Update(root, styles, pressed: second, previousFocus: first);
        var pressed = plan.Commands.ToArray().First(c => c.Widget == second && c.Kind == UiDrawKind.Rect);
        Assert.False(pressed.Blend);
        Assert.Equal(Rgb(0xA0, 0xA0, 0xA0), pressed.Colour);

        var disabled = plan.Commands.ToArray().First(c => c.Widget == off && c.Kind == UiDrawKind.Text);
        Assert.Equal(Rgb(0x80, 0x80, 0x80), disabled.Colour);
        Assert.False(disabled.Blend);

        Assert.Equal(Rgb(0x28, 0x38, 0x60), UiColour.Lerp(Rgb(0x20, 0x20, 0x20), Rgb(0x30, 0x50, 0xA0), 0.5f));
        Assert.Equal(Rgb(0x20, 0x20, 0x20), UiColour.Lerp(Rgb(0x20, 0x20, 0x20), Rgb(0x30, 0x50, 0xA0), 0f));
        Assert.Equal(ColourJsonConverter.Pack(10, 20, 30, 100), UiColour.Fade(ColourJsonConverter.Pack(10, 20, 30, 200), 0.5f));
    }

    // Nine pieces: corners that keep their size (at `scale` pixels per texture pixel), edges that
    // stretch one way, a middle both; shrunk evenly when the destination is smaller than two corners,
    // pieces with no room skipped; no insets is the texture stretched whole.
    [Fact]
    public void ANineSliceKeepsItsCornersAndStretchesTheRest()
    {
        Span<Rect> sources = stackalloc Rect[NineSlice.Pieces];
        Span<Rect> targets = stackalloc Rect[NineSlice.Pieces];
        var texture = new Vector2(24f, 24f);

        int n = NineSlice.Compute(new Rect(10f, 20f, 100f, 50f), texture, new Thickness(8f), 2f, sources, targets);
        Assert.Equal(9, n);
        Assert.Equal(new Rect(0f, 0f, 8f, 8f), sources[0]);
        Assert.Equal(new Rect(10f, 20f, 16f, 16f), targets[0]);          // a corner, twice its texels
        Assert.Equal(new Rect(8f, 0f, 8f, 8f), sources[1]);
        Assert.Equal(new Rect(26f, 20f, 68f, 16f), targets[1]);          // the top edge stretches across
        Assert.Equal(new Rect(8f, 8f, 8f, 8f), sources[4]);
        Assert.Equal(new Rect(26f, 36f, 68f, 18f), targets[4]);          // the middle, both ways
        Assert.Equal(new Rect(16f, 16f, 8f, 8f), sources[8]);
        Assert.Equal(new Rect(94f, 54f, 16f, 16f), targets[8]);          // flush with the far corner

        n = NineSlice.Compute(new Rect(0f, 0f, 20f, 10f), texture, new Thickness(8f), 2f, sources, targets);
        Assert.Equal(4, n);                                                // no room for edges or middle
        Assert.Equal(new Rect(0f, 0f, 10f, 5f), targets[0]);
        Assert.Equal(new Rect(10f, 5f, 10f, 5f), targets[3]);

        n = NineSlice.Compute(new Rect(0f, 0f, 30f, 30f), texture, Thickness.Zero, 1f, sources, targets);
        Assert.Equal(1, n);
        Assert.Equal(new Rect(0f, 0f, 24f, 24f), sources[0]);
        Assert.Equal(new Rect(0f, 0f, 30f, 30f), targets[0]);
    }

    // What the client's devices say becomes one UiInput: one direction a frame (in a fixed order), Tab
    // round the tab order and Shift+Tab back, the wheel in notches, and no pointer at all while a dev
    // window has the mouse.
    [Fact]
    public void TheMenuActionsAndTheMouseMapToUiInput()
    {
        Assert.Equal(UiNavigation.Up, UiInputMap.From(new UiControls { Up = true, Left = true }).Navigate);
        Assert.Equal(UiNavigation.Left, UiInputMap.From(new UiControls { Left = true, Right = true, Tab = true }).Navigate);
        Assert.Equal(UiNavigation.Right, UiInputMap.From(new UiControls { Right = true }).Navigate);
        Assert.Equal(UiNavigation.Next, UiInputMap.From(new UiControls { Tab = true }).Navigate);
        Assert.Equal(UiNavigation.Previous, UiInputMap.From(new UiControls { Tab = true, Shift = true }).Navigate);
        Assert.Equal(UiNavigation.None, UiInputMap.From(new UiControls { Shift = true }).Navigate);

        var input = UiInputMap.From(new UiControls
        {
            Confirm = true, ConfirmHeld = true, Back = true, Pointer = new Vector2(10f, 20f), PointerMoved = true,
            PointerPressed = true, PointerDown = true, Wheel = -240, DeltaTime = 0.016f,
        });
        Assert.True(input.Confirm && input.ConfirmHeld && input.Back);
        Assert.Equal(new Vector2(10f, 20f), input.Pointer);
        Assert.True(input.PointerMoved && input.PointerPressed && input.PointerDown);
        Assert.Equal(-2f, input.Wheel);
        Assert.Equal(0.016f, input.DeltaTime);

        var taken = UiInputMap.From(new UiControls { PointerMoved = true, PointerPressed = true, PointerDown = true, Wheel = 120, Confirm = true, PointerTaken = true });
        Assert.False(taken.PointerMoved || taken.PointerPressed || taken.PointerDown);
        Assert.Equal(0f, taken.Wheel);
        Assert.True(taken.Confirm);                                        // the keys still work
    }

    [Fact]
    public void ATweenEasesOnTheTimeItIsGiven()
    {
        var tween = new UiTween(0f, 10f, 1f, Ease.Linear);
        Assert.Equal(0f, tween.Value);
        Assert.Equal(2.5f, tween.Advance(0.25f), 3);
        Assert.Equal(2.5f, tween.Advance(-1f), 3);                        // time never runs back
        Assert.False(tween.Done);
        Assert.Equal(10f, tween.Advance(5f));                              // and never overshoots the end
        Assert.True(tween.Done);

        var eased = new UiTween(0f, 1f, 1f, Ease.QuadOut);
        Assert.Equal(Easing.Apply(Ease.QuadOut, 0.5f), eased.Advance(0.5f), 5);
        Assert.True(UiTween.At(3f).Done);
        Assert.Equal(3f, UiTween.At(3f).Value);
    }
}
