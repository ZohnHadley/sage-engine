#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Render targets and world views inside widgets (issue #348): a `view` widget shows a render target by
// name; its own camera (an orbit round the player for a paper doll, a top-down one for a map) is worked
// out headless, so where it stands is asserted here; the stack hands the client the views on screen;
// and an image's or view's discovery fog (UiFogMask) is drawn over the picture without replanning.
public class WorldViewTests
{
    public WorldViewTests() { _ = TestEnv.UserRoot; }

    internal const string Records = """
    [
      { "type": "ui_layout", "id": "status",
        "nodes": {
          "window": { "widget": "stack", "anchors": "center" },
          "doll":   { "widget": "view", "parent": "window", "target": "paper_doll", "minSize": [200, 300],
                      "camera": { "mode": "Orbit", "distance": 3, "height": 1, "pitch": 0, "rotatable": true } },
          "map":    { "widget": "view", "parent": "window", "target": "local_map", "minSize": [100, 100], "fogColour": "#000000C0",
                      "bindings": { "fog": "fog", "radius": "range" }, "camera": { "mode": "TopDown" } },
          "chart":  { "widget": "image", "parent": "window", "source": "textures/chart.png", "naturalSize": [64, 64], "bindings": { "fog": "fog" } },
          "screen": { "widget": "view", "parent": "window", "bind": "monitor", "minSize": [50, 50] },
          "gone":   { "widget": "view", "parent": "window", "target": "hidden_doll", "visible": false, "camera": { "mode": "Orbit" } }
        } },
      { "type": "screen", "id": "status", "layout": "status", "viewModel": "uitest_views" }
    ]
    """;

    public sealed class ViewsModel : IViewModel
    {
        public UiFogMask Fog { get; } = new(4, 4);
        public int Range = 25;
        public string Monitor = "security_cam";
    }

    private static HeadlessApp Boot() => HeadlessApp.Bare().With(new UiModule()).Mount(UiRecordTests.Content(Records))
        .OnRegistered(app => app.Engine.Vocabularies.Of<IViewModel>().Register("uitest_views", typeof(ViewsModel), () => new ViewsModel()))
        .Boot("ui");

    private static readonly RecordId Status = new("uitest", "status");

    private static UiScreenStack Stack(World world)
    {
        var stack = world.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        stack.OpenSeconds = stack.CloseSeconds = 0f;
        return stack;
    }

    private static Entity Player(World world, Vector3 at, float headingDegrees)
    {
        var transform = Transform.At(at);
        transform.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, headingDegrees * MathF.PI / 180f);
        var player = world.Create(transform, "hero");
        player.AddTag<PlayerControlled>();
        return player;
    }

    // Turns it now, global pose included (no tick runs here to propagate it).
    private static void Turn(World world, Entity entity, float degrees)
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees * MathF.PI / 180f);
        world.Get<Transform>(entity).LocalRotation = rotation;
        if (world.Has<GlobalTransform>(entity)) world.Get<GlobalTransform>(entity).Current.Rotation = rotation;
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-3f, $"expected {expected}, got {actual}");

    private static Vector3 Forward(in ViewPose pose) => Vector3.Transform(-Vector3.UnitZ, pose.Rotation);
    private static Vector3 Up(in ViewPose pose) => Vector3.Transform(Vector3.UnitY, pose.Rotation);

    [Fact]
    public void AViewFromALayoutShowsItsTargetWithItsCameraAndBindings()
    {
        using var app = Boot();
        var stack = Stack(app.World);
        var layer = stack.Open(Status, new UiBindContext(app.World));
        var view = layer.Screen!.View;
        var model = (ViewsModel)layer.Screen.ViewModel!;

        var doll = view.Find<View>("doll")!;
        Assert.Equal("view", doll.TypeName);
        Assert.Equal("paper_doll", doll.Target);
        Assert.Equal(ViewCamera.Orbit, doll.Camera);
        Assert.Equal(3f, doll.Distance);
        Assert.True(doll.Rotatable && doll.Focusable);

        var map = view.Find<View>("map")!;
        Assert.Equal(ViewCamera.TopDown, map.Camera);
        Assert.Equal(25f, map.Radius);                 // bound to the view-model's range
        Assert.Same(model.Fog, map.Fog);
        Assert.Equal(ColourJsonConverter.Pack(0, 0, 0, 0xC0), map.FogColour);
        Assert.Same(model.Fog, view.Find<Image>("chart")!.Fog);   // an authored picture takes fog too

        var screen = view.Find<View>("screen")!;
        Assert.Equal("security_cam", screen.Target);   // `bind` on a view is its target
        Assert.Equal(ViewCamera.None, screen.Camera);  // drawn by a camera entity naming it

        model.Range = 60;
        model.Monitor = "other_cam";
        stack.Update(default);
        Assert.Equal(60f, map.Radius);
        Assert.Equal("other_cam", screen.Target);
    }

    [Fact]
    public void ThePlanDrawsTheTargetAndThenItsFogWhichChangesWithoutReplanning()
    {
        using var app = Boot();
        var stack = Stack(app.World);
        var layer = stack.Open(Status, new UiBindContext(app.World));
        var model = (ViewsModel)layer.Screen!.ViewModel!;
        var plan = layer.Plan;
        plan.Update(layer.Root, app.World.Resources.Get<UiStyles>());

        var commands = plan.Commands.ToArray();
        string Of(string name) => string.Join(" ", commands.Where(c => c.Widget?.Name == name).Select(c => c.Kind));
        Assert.Equal("Image", Of("doll"));
        Assert.Equal("Image Fog", Of("map"));
        Assert.Equal("Image Fog", Of("chart"));
        Assert.Equal("", Of("gone"));

        var doll = commands.Single(c => c.Widget?.Name == "doll");
        Assert.Equal("paper_doll", doll.Target);
        Assert.True(doll.Texture.IsEmpty);
        var picture = layer.Screen.View.Find<View>("doll")!;
        Assert.Equal(layer.Root.ToPixels(picture.ImageRect), doll.Rect);

        var fog = commands.Single(c => c.Widget?.Name == "map" && c.Kind == UiDrawKind.Fog);
        Assert.Same(model.Fog, fog.Fog);
        Assert.Equal(ColourJsonConverter.Pack(0, 0, 0, 0xC0), fog.Colour);
        Assert.Equal(commands.Single(c => c.Widget?.Name == "map" && c.Kind == UiDrawKind.Image).Rect, fog.Rect);
        Assert.Equal("textures/chart.png", commands.Single(c => c.Widget?.Name == "chart" && c.Kind == UiDrawKind.Image).Texture.ToString());

        // Revealing ground changes the mask, not the screen: the client uploads it again, the plan stays.
        int builds = plan.Builds, version = model.Fog.Version;
        model.Fog[1, 2] = 255;
        stack.Update(default);
        Assert.False(plan.Update(layer.Root, app.World.Resources.Get<UiStyles>()));
        Assert.Equal(builds, plan.Builds);
        Assert.NotEqual(version, model.Fog.Version);
    }

    [Fact]
    public void AnOrbitViewLooksAtThePlayerFromInFrontAndTurnsWithYawAndSpin()
    {
        using var app = HeadlessApp.Bare().With(new UiModule()).Boot("ui");
        var world = app.World;
        var player = Player(world, new Vector3(10f, 0f, 5f), 0f);   // facing −Z
        var view = new View { Target = "doll", Camera = ViewCamera.Orbit, Distance = 3f, Height = 1f, Pitch = 0f };

        Assert.True(WorldViews.TryPose(world, view, new UiBindContext(world), 0d, out var pose));
        Assert.Equal(player, pose.Subject);                 // "player": the local player when the screen has no subject
        Near(new Vector3(10f, 1f, 2f), pose.Position);      // in front of it (−Z), three metres off, at head height
        Near(Vector3.UnitZ, Forward(pose));                 // looking back at it
        Assert.False(pose.Orthographic);
        Assert.Equal(35f * MathF.PI / 180f, pose.FovY, 4);

        // Turned 90° to the left, the player faces −X: the camera follows round to stay in front.
        Turn(world, player, 90f);
        Assert.True(WorldViews.TryPose(world, view, new UiBindContext(world), 0d, out pose));
        Near(new Vector3(7f, 1f, 5f), pose.Position);
        Near(Vector3.UnitX, Forward(pose));

        // Yaw goes round the subject; Spin adds to it with time; Pitch looks down from above.
        Turn(world, player, 0f);
        view.Spin = 45f;
        Assert.True(WorldViews.TryPose(world, view, new UiBindContext(world), 2d, out pose));   // 90° after two seconds
        Near(new Vector3(7f, 1f, 5f), pose.Position);       // round to the player's left (−X)
        view.Spin = 0f;
        view.Pitch = 30f;
        Assert.True(WorldViews.TryPose(world, view, new UiBindContext(world), 0d, out pose));
        Assert.True(pose.Position.Y > 1f && Forward(pose).Y < 0f);
        Assert.Equal(3f, Vector3.Distance(pose.Position, new Vector3(10f, 1f, 5f)), 3);

        // The screen's subject wins over the first player; "other" is the screen's other one; a name is an entity.
        var npc = world.Create(Transform.At(new Vector3(-4f, 0f, 0f)), "trader");
        Assert.True(WorldViews.TryPose(world, view, new UiBindContext(world, npc), 0d, out pose));
        Assert.Equal(npc, pose.Subject);
        view.Subject = View.Other;
        Assert.True(WorldViews.TryPose(world, view, new UiBindContext(world, player, npc), 0d, out pose));
        Assert.Equal(npc, pose.Subject);
        Assert.False(WorldViews.TryPose(world, view, new UiBindContext(world, player), 0d, out _));   // nobody there: no camera
        view.Subject = "trader";
        Assert.True(WorldViews.TryPose(world, view, new UiBindContext(world), 0d, out pose));
        Assert.Equal(npc, pose.Subject);

        view.Camera = ViewCamera.None;                      // a camera entity draws its target
        Assert.False(WorldViews.TryPose(world, view, new UiBindContext(world), 0d, out _));
    }

    [Fact]
    public void ATopDownViewLooksStraightDownWithNorthUp()
    {
        using var app = HeadlessApp.Bare().With(new UiModule()).Boot("ui");
        var world = app.World;
        Player(world, new Vector3(3f, 2f, -7f), 135f);   // which way the player faces does not turn the map
        var view = new View { Target = "map", Camera = ViewCamera.TopDown, Radius = 40f, Altitude = 80f };

        Assert.True(WorldViews.TryPose(world, view, new UiBindContext(world), 0d, out var pose));
        Assert.True(pose.Orthographic);
        Assert.Equal(80f, pose.OrthoHeight);                // forty metres from the middle to each edge
        Near(new Vector3(3f, 82f, -7f), pose.Position);
        Near(-Vector3.UnitY, Forward(pose));
        Near(-Vector3.UnitZ, Up(pose));                     // north (−Z) is the top of the picture
        Assert.True(pose.Far > 80f);

        // With no subject it looks down on Centre: a fixed map of a place.
        view.Subject = "";
        view.Centre = new Vector3(100f, 0f, 100f);
        Assert.True(WorldViews.TryPose(world, view, new UiBindContext(world), 0d, out pose));
        Assert.True(pose.Subject.IsNull);
        Near(new Vector3(100f, 80f, 100f), pose.Position);
    }

    [Fact]
    public void ARotatableViewTurnsWhenDraggedOrWithLeftAndRight()
    {
        var root = new UiRoot(new MonospaceTextMeasure(6f, 9f));
        root.SetViewport(UiRoot.DefaultDesignSize);
        var column = root.Content.Add(new Stack());
        var view = column.Add(new View { Target = "doll", Camera = ViewCamera.Orbit, Rotatable = true, MinSize = new Vector2(200f, 200f) });
        var below = column.Add(new Button("Close"));
        root.Layout();

        var middle = RectMath.Center(view.Rect);
        root.Update(new UiInput { Pointer = middle, PointerMoved = true, PointerPressed = true, PointerDown = true });
        Assert.Same(view, root.Focused);
        root.Update(UiInput.Drag(middle + new Vector2(40f, 0f)));
        Assert.Equal(20f, view.Yaw, 3);                     // half a degree a unit
        root.Update(UiInput.Drag(middle + new Vector2(20f, 0f)));
        Assert.Equal(10f, view.Yaw, 3);

        root.Update(UiInput.Nav(UiNavigation.Right));
        Assert.Equal(25f, view.Yaw, 3);
        root.Update(UiInput.Nav(UiNavigation.Left));
        root.Update(UiInput.Nav(UiNavigation.Left));
        Assert.Equal(355f, view.Yaw, 3);                    // kept within 0..360
        Assert.Same(view, root.Focused);                    // Left and Right turned it; focus stayed
        root.Update(UiInput.Nav(UiNavigation.Down));
        Assert.Same(below, root.Focused);                   // Down still leaves it

        var still = new View { Target = "x" };
        Assert.False(still.Focusable);                      // not rotatable: a picture, never focused
    }

    [Fact]
    public void TheStackHandsTheClientEachViewOnScreenWithItsScreensSubject()
    {
        using var app = Boot();
        var world = app.World;
        var player = Player(world, Vector3.Zero, 0f);
        var stack = Stack(world);
        var views = new List<UiWorldView>();

        stack.CollectViews(views);
        Assert.Empty(views);                                // nothing open

        var layer = stack.Open(Status, new UiBindContext(world, player));
        stack.Update(default);
        stack.CollectViews(views);
        // The doll and the map have cameras of their own; the bound monitor is a camera entity's, and the
        // hidden one is not on screen.
        Assert.Equal(new[] { "doll", "map" }, views.Select(v => v.View.Name).ToArray());
        Assert.All(views, v => Assert.Equal(player, v.Context.Subject));
        var doll = layer.Screen!.View.Find<View>("doll")!;
        Assert.Equal(layer.Root.ToPixels(doll.ImageRect), views[0].Pixels);

        stack.Close(layer);
        stack.Update(UiInput.Wait(1f));
        stack.CollectViews(views);
        Assert.Empty(views);                                // closed: its targets are no longer drawn
    }

    [Fact]
    public void AFogMaskRevealsTheGroundRoundAPoint()
    {
        var fog = new UiFogMask(10, 10) { Min = new Vector2(0f, 0f), Max = new Vector2(100f, 100f) };   // ten metres a cell
        Assert.False(fog.IsRevealed(new Vector3(55f, 0f, 55f)));
        int version = fog.Version;

        int changed = fog.Reveal(new Vector3(55f, 3f, 55f), 12f);
        Assert.True(changed > 0);
        Assert.NotEqual(version, fog.Version);
        Assert.Equal(255, fog[5, 5]);                       // its own cell, and the ones whose centres are within reach
        Assert.Equal(255, fog[4, 5]);
        Assert.Equal(255, fog[5, 6]);
        Assert.True(fog[4, 4] > 0 && fog[4, 4] < 255);      // a cell's width of soft edge outside the radius
        Assert.Equal(0, fog[0, 0]);
        Assert.True(fog.IsRevealed(new Vector3(51f, 0f, 59f)));
        Assert.True(fog.CellAt(new Vector3(5f, 0f, 95f), out int x, out int y) && x == 0 && y == 9);   // south-west: bottom-left
        Assert.False(fog.CellAt(new Vector3(-1f, 0f, 50f), out _, out _));

        version = fog.Version;
        Assert.Equal(0, fog.Reveal(new Vector3(55f, 0f, 55f), 12f));   // nothing new: no change, no upload
        Assert.Equal(version, fog.Version);

        var cells = fog.Cells.ToArray();
        var copy = new UiFogMask(10, 10);
        copy.Load(cells);
        Assert.Equal(cells, copy.Cells.ToArray());
        copy.Fill(255);
        Assert.True(copy.Cells.ToArray().All(c => c == 255));
        Assert.Throws<ArgumentException>(() => copy.Load(new byte[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiFogMask(0, 4));
    }

    [Fact]
    public void ViewMistakesAreLoadErrorsAtTheirLines()
    {
        using var capture = new CaptureSink();
        var fixture = new MountFixture();
        fixture.Write("uiview", "data/ui.json", """
            [
              { "type": "ui_layout", "id": "bad",
                "nodes": {
                  "label":  { "widget": "label", "target": "doll" },
                  "box":    { "widget": "box", "camera": { "mode": "Orbit" } },
                  "stack":  { "widget": "stack", "fogColour": "#000000" },
                  "blind":  { "widget": "view", "camera": { "mode": "TopDown" } },
                  "under":  { "widget": "label", "parent": "blind" },
                  "radius": { "widget": "image", "bindings": { "radius": "range" } }
                } }
            ]
            """);
        fixture.Mount("uiview", "uiview");
        using var app = UiRecordTests.Boot(fixture);

        var messages = capture.Entries.Select(e => e.Message).Where(m => m.StartsWith("uiview:", StringComparison.Ordinal)).ToList();
        void Has(string where, string what) =>
            Assert.True(messages.Any(m => m.StartsWith("uiview:data/ui.json:" + where, StringComparison.Ordinal) && m.Contains(what)),
                        $"no message at {where} saying \"{what}\" in:\n{string.Join("\n", messages)}");

        Has("4:", "a label shows no render target; a view does");
        Has("5:", "a box has no camera; a view does");
        Has("6:", "a stack has no picture to cover with fog");
        Has("7:", "view 'blind' has a camera and no `target` to draw into");
        Has("8:", "node 'under' is in 'blind', a view, which holds no children");
        Has("9:", "has no 'radius'");
    }
}
