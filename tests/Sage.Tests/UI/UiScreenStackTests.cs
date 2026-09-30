#nullable enable
using System.Numerics;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Hosting widget screens, headless (docs/design/13 "As built (drawing)", issue #97): the UiScreenStack
// the client's ScreenSystem feeds and draws. A screen opens focused and fades and slides in on frame
// time, Back or a press outside it closes it (after it has faded out), only the top layer takes input,
// a HUD layer is drawn but never holds the input, and an action toggles a screen as a panel key does.
public class UiScreenStackTests
{
    public UiScreenStackTests() { _ = TestEnv.UserRoot; }

    internal static readonly RecordId Hud = new("uitest", "hud");

    internal static HeadlessApp Boot() => HeadlessApp.Bare().With(new UiModule()).Mount(UiRecordTests.Content())
        .OnRegistered(app =>
        {
            app.Engine.Vocabularies.Of<IViewModel>().Register("uitest_hud", typeof(UiRecordTests.HudModel), () => new UiRecordTests.HudModel());
            app.Engine.Actions.Register("TestHud", ActionKind.Button);
        })
        .Boot("ui");

    private static UiScreenStack Stack(HeadlessApp app)
    {
        var stack = app.World.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        return stack;
    }

    [Fact]
    public void AScreenOpensFocusedFadesInAndBackFadesItOutAndClosesIt()
    {
        using var app = Boot();
        var stack = Stack(app);
        var screens = app.World.Resources.Get<UiScreens>();

        var layer = stack.Open(Hud, new UiBindContext(app.World));
        Assert.True(stack.IsOpen);
        Assert.Same(layer, stack.Top);
        Assert.Equal("ok", layer.Root.Focused?.Name);                    // first in tab order: a gamepad can start
        Assert.Equal(0f, layer.Opacity);
        Assert.Equal(stack.SlideDistance, layer.Offset);

        stack.Update(UiInput.Wait(stack.OpenSeconds * 0.5f));
        Assert.InRange(layer.Opacity, 0.5f, 0.99f);                        // CubicOut: past half at half time
        Assert.InRange(layer.Offset, 0.01f, stack.SlideDistance * 0.5f);
        stack.Update(UiInput.Wait(stack.OpenSeconds));
        Assert.Equal(1f, layer.Opacity);
        Assert.Equal(0f, layer.Offset);

        stack.Update(UiInput.Cancel);
        Assert.True(layer.IsClosing);
        Assert.False(stack.IsOpen);                                        // the input is the world's again
        Assert.Single(stack.Layers);                                       // but it is still drawn, fading
        Assert.Single(screens.Open);

        stack.Update(UiInput.Wait(stack.CloseSeconds * 0.5f));
        Assert.InRange(layer.Opacity, 0.01f, 0.99f);
        stack.Update(UiInput.Wait(stack.CloseSeconds));
        Assert.True(layer.IsClosed);
        Assert.Empty(stack.Layers);
        Assert.Empty(screens.Open);                                        // the screen record's view is closed
        Assert.Null(layer.Content.Parent);
    }

    // A press on nothing of the top screen closes it; a press on its window — a container, but one that
    // paints a background, so solid to the pointer — does not.
    [Fact]
    public void APressOutsideTheScreenClosesItButOneOnItsWindowDoesNot()
    {
        using var app = Boot();
        var stack = Stack(app);
        stack.OpenSeconds = 0f;
        var layer = stack.Open(Hud, new UiBindContext(app.World));
        stack.Update(UiInput.Wait(0f));

        var window = layer.Content.Find("window")!;
        Assert.True(window.HitTestable);                                   // uitest:panel has a background
        var padding = layer.Root.ToPixels(new Vector2(window.Rect.X + 2f, window.Rect.Y + 2f));
        var result = stack.Update(UiInput.Click(padding));
        Assert.True(result.PointerOverUi);
        Assert.False(layer.IsClosing);

        result = stack.Update(UiInput.Click(new Vector2(5f, 5f)));
        Assert.False(result.PointerOverUi);
        Assert.True(layer.IsClosing);

        // Neither Back nor a press closes a layer that says it stays.
        var stays = stack.Open(Hud, new UiBindContext(app.World));
        stays.CloseOnBack = stays.CloseOnClickOutside = false;
        stack.Update(UiInput.Cancel);
        stack.Update(UiInput.Click(new Vector2(5f, 5f)));
        Assert.False(stays.IsClosing);
    }

    [Fact]
    public void OnlyTheTopLayerTakesInputAndAHudLayerNeverDoes()
    {
        var stack = new UiScreenStack { OpenSeconds = 0f };
        stack.SetViewport(new Vector2(1280f, 720f));
        (UiLayer Layer, Button A, Button B) Window()
        {
            var column = new Stack();
            var a = column.Add(new Button("a"));
            var b = column.Add(new Button("b"));
            return (stack.Push(column), a, b);
        }

        var hud = stack.Push(new Label("100 hp"), modal: false);
        Assert.False(stack.IsOpen);
        Assert.Null(stack.Top);
        Assert.Equal(0u, hud.Backdrop);

        var (lower, lowerA, _) = Window();
        var (upper, upperA, upperB) = Window();
        Assert.Same(upper, stack.Top);
        Assert.NotEqual(0u, upper.Backdrop);

        Widget? activated = null;
        stack.Activated += (_, widget) => activated = widget;
        stack.Update(UiInput.Nav(UiNavigation.Down));
        Assert.Same(upperB, upper.Root.Focused);
        Assert.Same(lowerA, lower.Root.Focused);                          // untouched underneath
        Assert.Same(upperA, upper.PreviousFocus);
        Assert.Equal(0f, upper.FocusBlend);                                // the highlight starts moving...
        stack.Update(UiInput.Wait(stack.FocusSeconds));
        Assert.Equal(1f, upper.FocusBlend);                                // ...and arrives on frame time

        stack.Update(new UiInput { ConfirmHeld = true });
        Assert.Same(upperB, upper.Pressed);                                // held: drawn pressed
        stack.Update(UiInput.Press);
        Assert.Same(upperB, activated);

        stack.CloseTop();
        Assert.Same(lower, stack.Top);                                     // the one underneath has the input now
        stack.CloseAll();
        Assert.Empty(stack.Layers);
    }

    [Fact]
    public void AnActionTogglesItsScreenAndTheConsoleOpensAndClosesScreens()
    {
        using var app = Boot();
        var stack = Stack(app);
        var action = app.Engine.Actions.Get("TestHud");
        stack.Bind(action, Hud);
        Assert.Equal(new[] { action }, stack.OpenActions);

        Assert.True(stack.Toggle(action, new UiBindContext(app.World)));
        Assert.Equal(Hud, stack.Top?.Screen?.Id);
        Assert.True(stack.Toggle(action));                                 // pressed again: closes it
        Assert.False(stack.IsOpen);
        Assert.False(stack.Toggle(ActionId.None));

        stack.Update(UiInput.Wait(1f));
        app.CVars.Execute("ui_open uitest:hud");
        Assert.True(stack.IsOpen);
        Assert.Same(app.World, stack.Top!.Screen!.Context.World);
        app.CVars.Execute("ui_close");
        Assert.False(stack.IsOpen);
        app.CVars.Execute("ui_open hud");
        app.CVars.Execute("ui_close all");
        Assert.Empty(stack.Layers);
    }
}

// Zero allocation per frame with a widget screen up (02 §4.6, issue #97): hosting it — its view-model
// and bindings, its input and transitions — and bringing its render plan up to date, both while nothing
// changes and while focus moves round it (which rebuilds the plan every frame).
[Collection(MeasurementsCollection.Name)]
public class UiDrawingAllocationTests
{
    public UiDrawingAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void AnOpenScreenIsHostedAndPlannedWithoutAllocating()
    {
        using var app = UiScreenStackTests.Boot();
        var stack = app.World.Resources.Get<UiScreenStack>();
        var styles = app.World.Resources.Get<UiStyles>();
        var text = app.World.Resources.Get<Localisation>();
        stack.SetViewport(new Vector2(1920f, 1080f));
        var layer = stack.Open(UiScreenStackTests.Hud, new UiBindContext(app.World));

        var idle = UiInput.Wait(1f / 60f);
        var down = new UiInput { Navigate = UiNavigation.Down, DeltaTime = 1f / 60f };
        var up = new UiInput { Navigate = UiNavigation.Up, DeltaTime = 1f / 60f };
        void Frame(in UiInput input)
        {
            stack.Update(input);
            layer.Plan.Update(layer.Root, styles, layer.Pressed, layer.PreviousFocus, text.Version);
        }
        for (int i = 0; i < 60; i++) Frame(i % 2 == 0 ? down : up);
        for (int i = 0; i < 60; i++) Frame(idle);
        int builds = layer.Plan.Builds;

        AllocationProbe.AssertNone(500, () => Frame(idle));
        Assert.Equal(builds, layer.Plan.Builds);                          // nothing changed: the plan was reused

        bool flip = false;
        AllocationProbe.AssertNone(500, () => { Frame((flip = !flip) ? down : up); });
        Assert.True(layer.Plan.Builds > builds + 400);                     // and focus moving rebuilt it each frame
    }
}
