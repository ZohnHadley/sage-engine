#nullable enable
using System.Linq;
using System.Numerics;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// What the immediate-mode HUD drew by hand, as widgets (issue #350): the engine's crosshair, a HUD layer
// over the `ui_crosshair` view-model, and the Sandbox's first-person hands, a frame of a sprite sheet named
// by an image source's `#x,y,w,h`.
public class WidgetHudTests
{
    public WidgetHudTests() { _ = TestEnv.UserRoot; }

    // The crosshair is `sage:crosshair` in the engine's content: shown while the player's rig aims, hidden
    // under a window and with `ui_crosshair 0`.
    [Xunit.Fact]
    public void TheCrosshairIsAHudLayerShownOnlyWhileAiming()
    {
        using var app = HeadlessApp.ForGame(CameraRigTests.SceneOnlyGame).WithEngineContent().With(new UiModule()).Boot();
        var world = app.World;
        CameraRigTests.Step(world, 2);
        Assert.Equal(CameraRigKind.FirstPerson, world.MainViewRig());
        var stack = world.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        stack.OpenSeconds = stack.CloseSeconds = 0f;
        var hud = stack.OpenHud(CrosshairView.Screen, new UiBindContext(world));
        var view = Assert.IsType<CrosshairView>(hud.Screen!.ViewModel);
        var cross = hud.Content.Find("cross")!;

        stack.Update(UiInput.Wait(0f));
        Assert.True(view.Shown);
        Assert.True(cross.Visible);
        var bar = hud.Content.Find("across")!;
        Assert.Equal(640f, bar.Rect.X + bar.Rect.Width * 0.5f, 3);            // across the middle
        Assert.Equal(360f, bar.Rect.Y + bar.Rect.Height * 0.5f, 3);

        var window = stack.Push(new Box { MinSize = new Vector2(40f, 40f) });  // a window up: nothing to aim at
        stack.Update(UiInput.Wait(0f));
        Assert.False(cross.Visible);
        stack.CloseAll();
        hud = stack.OpenHud(CrosshairView.Screen, new UiBindContext(world));
        cross = hud.Content.Find("cross")!;

        Assert.True(app.Engine.CVars.Find(UiModule.CrosshairCVar)!.TrySet("0", out _));
        stack.Update(UiInput.Wait(0f));
        Assert.False(cross.Visible);
        _ = window;
    }

    // An image source may name a part of its texture, `path#x,y,w,h` in texture pixels (a sprite sheet's
    // frame); the plan draws that part, and a fragment that is not four numbers draws the whole texture.
    [Xunit.Fact]
    public void AnImageSourceNamesAPartOfItsTexture()
    {
        using var app = WidgetDrawingTests.Boot();
        var root = new UiRoot(new MonospaceTextMeasure(6f, 9f));
        root.SetViewport(UiRoot.DefaultDesignSize);
        var column = root.Content.Add(new Stack());
        column.Add(new Image("sheets/hands.png#16,0,16,32", new Vector2(16f, 32f)) { Name = "frame" });
        column.Add(new Image("sheets/hands.png#oops", new Vector2(16f, 32f)) { Name = "whole" });
        var plan = new UiRenderPlan();
        plan.Update(root, app.World.Resources.Get<UiStyles>());

        var images = plan.Commands.ToArray().Where(c => c.Kind == UiDrawKind.Image).ToArray();
        Assert.Equal(2, images.Length);
        Assert.Equal(AssetPath.Intern("sheets/hands.png"), images[0].Texture);
        Assert.Equal(new Rect(16f, 0f, 16f, 32f), images[0].Region);
        Assert.Equal(AssetPath.Intern("sheets/hands.png"), images[1].Texture);
        Assert.Equal(default, images[1].Region);
    }
}
