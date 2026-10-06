#nullable enable
using System;
using System.IO;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Camera blends (issue #90): `CameraOn [hold [blend [ease]]]` eases the screen from the view it had to the
// camera it turns on; a cut stays the default.
public class CameraBlendTests
{
    public CameraBlendTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    private static void Step(World world, int times = 1, float alpha = 1f)
    {
        for (int i = 0; i < times; i++)
        {
            world.RunFixed(Dt);
            world.RunFrame(Dt, alpha);
        }
    }

    private static Entity CameraAt(World world, Vector3 at, float fovY = 45f, ScriptedCamera? scripted = null)
    {
        var e = world.Create(Transform.At(at), "cam");
        var camera = Camera.Perspective(fovY, priority: 100);
        camera.Enabled = false;
        world.Add(e, camera);
        if (scripted is { } s) world.Add(e, s);
        return e;
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-3f, $"expected {expected}, was {actual}");

    [Fact]
    public void CameraOnWithABlendTime_EasesFromTheScreensViewToTheCamera_AndActiveCameraFollows()
    {
        using var app = HeadlessApp.Gameplay().Boot("blend");
        var world = app.World;
        var views = world.Resources.Get<CameraViews>();
        var active = world.Resources.Get<ActiveCamera>();
        var a = new Vector3(0, 2, 0);
        var b = new Vector3(12, 2, 0);
        active.Position = a;
        active.Rotation = Quaternion.Identity;
        active.FovY = 45f * MathF.PI / 180f;
        var cam = CameraAt(world, b, fovY: 90f);
        world.Get<Transform>(cam).LocalRotation = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2f, 0f, 0f);
        Step(world);
        Assert.True(views.Main.FromActiveCamera);

        world.IO().FireInput(cam, "CameraOn", "0 1 linear");        // no hold, a one-second linear blend
        Step(world);                                                 // delivered: on screen, still where the screen was
        Assert.Equal(cam, views.Main.Entity);
        Near(a, views.Main.Position);
        Assert.Equal(45f * MathF.PI / 180f, views.Main.FovY, 4);

        Step(world, 30);                                             // half-way
        Near(Vector3.Lerp(a, b, 0.5f), views.Main.Position);
        Near(Vector3.Lerp(a, b, 0.5f), active.Position);             // what ActiveCamera's readers see too
        Assert.Equal(67.5f * MathF.PI / 180f, views.Main.FovY, 4);
        float yaw = SageMath.YawOf(views.Main.Rotation);
        Assert.Equal(MathF.PI / 4f, yaw, 3);

        // A frame between ticks is between their blends, so it is smooth at any display rate.
        world.RunFrame(Dt, 0.5f);
        Near(Vector3.Lerp(a, b, 29.5f / 60f), views.Main.Position);

        Step(world, 30);
        Near(b, views.Main.Position);
        Assert.Equal(90f * MathF.PI / 180f, views.Main.FovY, 4);
        Step(world, 5);
        Near(b, views.Main.Position);
        Assert.False(world.Get<CameraBlend>(cam).Active);
    }

    // Without a blend time CameraOn is the cut it always was, and a later cut ends a blend left on the
    // entity. The part's blendTime is the default a wire can override.
    [Fact]
    public void ACutIsStillTheDefault_AndTheEntitysBlendTimeIsUsedWhenTheWireGivesNone()
    {
        using var app = HeadlessApp.Gameplay().Boot("cut");
        var world = app.World;
        var views = world.Resources.Get<CameraViews>();
        world.Resources.Get<ActiveCamera>().Position = Vector3.Zero;
        var b = new Vector3(0, 0, 20);
        var cam = CameraAt(world, b, scripted: new ScriptedCamera { BlendTime = 2f, BlendEase = Ease.SmoothStep });
        var plain = CameraAt(world, new Vector3(5, 0, 0));
        Step(world);

        world.IO().FireInput(plain, "CameraOn");
        Step(world);
        Near(new Vector3(5, 0, 0), views.Main.Position);             // a cut
        Assert.False(world.Has<CameraBlend>(plain));
        world.IO().FireInput(plain, "CameraOff");
        Step(world);

        // The screen is where the last camera left ActiveCamera (CameraDirector), and blends from there.
        var from = views.Main.Position;
        Near(new Vector3(5, 0, 0), from);
        world.IO().FireInput(cam, "CameraOn");                       // the entity's own: two seconds
        Step(world, 61);
        Near(Vector3.Lerp(from, b, 0.5f), views.Main.Position);      // SmoothStep is half-way at half-time
        world.IO().FireInput(cam, "CameraOff");
        Step(world);
        world.IO().FireInput(cam, "CameraOn", "0 0");                // the wire says 0: a cut after all
        Step(world);
        Near(b, views.Main.Position);
        Assert.False(world.Get<CameraBlend>(cam).Active);
    }

    // The blend is saved with the camera: a save taken half-way through ends it on time.
    [Fact]
    public void ABlendSavedHalfWay_EndsOnTime()
    {
        const string content = """
        [
          { "type": "prefab", "id": "cam", "name": "cam",
            "parts": { "camera": { "enabled": false, "priority": 100 }, "scripted_camera": { "blendTime": 1, "blendEase": "Linear" } } },
          { "type": "scene", "id": "blend", "place": [ { "prefab": "cam", "at": [0, 0, 30], "name": "cam" } ] }
        ]
        """;
        using var app = HeadlessApp.Gameplay().File("data/blend.json", content).StartScene("sage:blend").Boot("blend");
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var world = app.World;
        var views = world.Resources.Get<CameraViews>();
        world.Resources.Get<ActiveCamera>().Position = Vector3.Zero;
        Step(world);
        world.IO().FireInput(world.FindByName("cam"), "CameraOn");
        Step(world, 16);                                             // delivered, then a quarter of a second
        Near(new Vector3(0, 0, 7.5f), views.Main.Position);
        Assert.True(app.Engine.Saves.Save("blend"));
        Step(world, 20);
        Assert.True(app.Engine.Saves.Load("blend"));
        Step(world, 15);
        Near(new Vector3(0, 0, 15f), views.Main.Position);
        Step(world, 30);
        Near(new Vector3(0, 0, 30f), views.Main.Position);
    }

    [Fact]
    public void ABadBlendOrEaseIsAWarning_AndTheCameraStillComesOn()
    {
        using var app = HeadlessApp.Gameplay().Boot("warn");
        var world = app.World;
        var cam = CameraAt(world, new Vector3(3, 0, 0));
        Step(world);
        world.IO().FireInput(cam, "CameraOn", "0 soon wobbly");
        Step(world);
        Assert.True(world.Get<Camera>(cam).Enabled);
        Near(new Vector3(3, 0, 0), world.Resources.Get<CameraViews>().Main.Position);
    }

    // Blending out (issue 4n-19): `CameraOff [blend [ease]]` eases from where the camera was to whatever
    // has the screen next — here a lower-priority camera standing in for the player's rig — rather than
    // cutting back, and ActiveCamera's readers see it too. Without a blend time it is still a cut.
    [Fact]
    public void CameraOffWithABlendTime_EasesOutToWhatHasTheScreenNext()
    {
        using var app = HeadlessApp.Gameplay().Boot("blend-out");
        var world = app.World;
        var views = world.Resources.Get<CameraViews>();
        var active = world.Resources.Get<ActiveCamera>();
        var a = new Vector3(0, 2, 0);
        var b = new Vector3(12, 2, 0);
        var rig = world.Create(Transform.At(a), "rig");
        world.Add(rig, Camera.Perspective(45f, priority: 0));
        var cam = CameraAt(world, b, fovY: 90f);
        Step(world);
        Assert.Equal(rig, views.Main.Entity);

        world.IO().FireInput(cam, "CameraOn");                       // a cut in
        Step(world);
        Near(b, views.Main.Position);

        world.IO().FireInput(cam, "CameraOff", "1 linear");          // a one-second linear blend out
        Step(world);                                                 // delivered: the rig has the screen, still where the camera was
        Assert.Equal(rig, views.Main.Entity);
        Assert.True(world.Get<CameraBlend>(cam).Out);
        Near(b, views.Main.Position);
        Assert.Equal(90f * MathF.PI / 180f, views.Main.FovY, 4);

        Step(world, 30);                                             // half-way back
        Near(Vector3.Lerp(b, a, 0.5f), views.Main.Position);
        Near(Vector3.Lerp(b, a, 0.5f), active.Position);
        Assert.Equal(67.5f * MathF.PI / 180f, views.Main.FovY, 4);

        Step(world, 30);
        Near(a, views.Main.Position);
        Step(world, 5);
        Near(a, views.Main.Position);
        Assert.False(world.Get<CameraBlend>(cam).Active);

        // Without a blend time: the cut it always was.
        world.IO().FireInput(cam, "CameraOn");
        Step(world);
        world.IO().FireInput(cam, "CameraOff");
        Step(world);
        Near(a, views.Main.Position);

        // Turned on again mid-blend-out, a camera cuts back in and its stale blend out stops easing anything.
        world.IO().FireInput(cam, "CameraOn");
        Step(world);
        world.IO().FireInput(cam, "CameraOff", "1");
        Step(world, 10);
        world.IO().FireInput(cam, "CameraOn");
        Step(world);
        Near(b, views.Main.Position);
        Assert.False(world.Get<CameraBlend>(cam).Active);
    }

    // A scripted camera's hold running out blends out over its own BlendOutTime, along its BlendEase —
    // the `scripted_camera` part's `blendOutTime` — the way a wire's CameraOff with a blend does.
    [Fact]
    public void AHoldRunningOut_BlendsOutOverTheCamerasBlendOutTime()
    {
        const string content = """
        [
          { "type": "prefab", "id": "cut", "name": "cut",
            "parts": { "camera": { "enabled": false, "priority": 100 },
                       "scripted_camera": { "holdTime": 0.5, "blendOutTime": 1, "blendEase": "Linear" } } },
          { "type": "prefab", "id": "rig", "name": "rig", "parts": { "camera": {} } },
          { "type": "scene", "id": "hold", "place": [ { "prefab": "cut", "at": [0, 0, 30], "name": "cut" },
                                                      { "prefab": "rig", "at": [0, 0, 0], "name": "rig" } ] }
        ]
        """;
        using var app = HeadlessApp.Gameplay().File("data/hold.json", content).StartScene("sage:hold").Boot("hold");
        var world = app.World;
        var views = world.Resources.Get<CameraViews>();
        var cut = world.FindByName("cut");
        Assert.Equal(1f, world.Get<ScriptedCamera>(cut).BlendOutTime);
        Step(world);
        world.IO().FireInput(cut, "CameraOn");
        int ticks = 0;
        do { Step(world); ticks++; } while (world.Get<Camera>(cut).Enabled && ticks < 120);
        Assert.False(world.Get<Camera>(cut).Enabled);
        Assert.Equal(world.FindByName("rig"), views.Main.Entity);
        Near(new Vector3(0, 0, 30), views.Main.Position);            // the hold ended: still where the cut was
        Step(world, 30);
        Near(new Vector3(0, 0, 15), views.Main.Position);
        Step(world, 30);
        Near(Vector3.Zero, views.Main.Position);
    }
}
