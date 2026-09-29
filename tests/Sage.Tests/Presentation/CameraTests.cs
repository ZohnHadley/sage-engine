#nullable enable
using System;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Cameras as entities (issue #76): the Camera component, CameraPose, the CameraDirector's choice per
// target, the CameraViews contract the renderer reads (#77), the ActiveCamera mirror (D1) and
// CameraMath. All headless: the director is simulation code, and so is every decision here.
public class CameraTests
{
    public CameraTests() { _ = TestEnv.UserRoot; }

    private const float Frame = 1f / 60f;

    // The character plugin brings an ActiveCamera (and the player camera and its rig, with no player to follow).
    private static HeadlessApp App() => HeadlessApp.Gameplay().Boot("cameras");

    private static Entity CameraAt(World world, Vector3 position, Camera camera, string? name = null)
    {
        var e = world.Create(Transform.At(position), name);
        world.Add(e, camera);
        return e;
    }

    [Fact]
    public void EveryEngineWorldHasTheDirectorAndItsViews_OwnedByTheEngine()
    {
        using var bare = HeadlessApp.Bare().Boot("bare");   // `"plugins": []`: cameras are the engine's
        Assert.NotNull(bare.World.Resources.Get<CameraViews>());
        var director = bare.World.Systems.Find(CameraDirector.Id)!;
        Assert.Equal(Phase.FrameUpdate, director.Phase);
        Assert.Equal("sage.core", director.Owner);

        using var world = new World("tool");   // a world made by hand has neither, as before
        Assert.False(world.Resources.TryGet<CameraViews>(out _));
        Assert.Empty(world.Systems);
    }

    [Fact]
    public void TheHigherPriorityCameraWins_AndDisablingItFallsBackToTheOther()
    {
        using var app = App();
        var world = app.World;
        var low = CameraAt(world, new Vector3(1, 2, 3), Camera.Perspective(priority: 1), "low");
        var high = CameraAt(world, new Vector3(10, 20, 30), Camera.Perspective(priority: 5), "high");

        world.RunFrame(Frame, 1f);
        var views = world.Resources.Get<CameraViews>();
        Assert.Equal(1, views.Count);
        Assert.Equal(high, views.Main.Entity);
        Assert.Equal(new Vector3(10, 20, 30), views.Main.Position);

        world.Get<Camera>(high).Enabled = false;
        world.RunFrame(Frame, 1f);
        Assert.Equal(low, views.Main.Entity);
        Assert.Equal(new Vector3(1, 2, 3), views.Main.Position);

        world.Get<Camera>(low).Enabled = false;   // none left: back to ActiveCamera
        world.RunFrame(Frame, 1f);
        Assert.True(views.Main.FromActiveCamera);
    }

    [Fact]
    public void ATieGoesToTheLowerEntityId_WhateverOrderTheyWereMadeIn()
    {
        using var app = App();
        var world = app.World;
        var first = CameraAt(world, Vector3.Zero, Camera.Perspective(priority: 2));
        var second = CameraAt(world, Vector3.One, Camera.Orthographic(priority: 2));
        world.RunFrame(Frame, 1f);
        var views = world.Resources.Get<CameraViews>();
        Assert.Equal(first.Id < second.Id ? first : second, views.Main.Entity);
    }

    [Fact]
    public void ActiveCameraMirrorsTheMainView_AndSaysSomethingDroveIt()
    {
        using var app = App();
        var world = app.World;
        var rotation = Quaternion.CreateFromYawPitchRoll(0.5f, -0.2f, 0f);
        var camera = Camera.Perspective(fovYDegrees: 70f, near: 0.5f, far: 300f);
        var e = world.Create(new Transform { LocalPosition = new Vector3(4, 5, 6), LocalRotation = rotation, LocalScale = Vector3.One });
        world.Add(e, camera);

        world.RunFrame(Frame, 1f);
        var active = world.Resources.Get<ActiveCamera>();
        var main = world.Resources.Get<CameraViews>().Main;
        Assert.Equal(main.Position, active.Position);
        Assert.Equal(new Vector3(4, 5, 6), active.Position);
        Assert.True(Quaternion.Dot(rotation, active.Rotation) > 0.99999f);
        Assert.Equal(70f * MathF.PI / 180f, active.FovY, 5);
        Assert.Equal(main.FovY, active.FovY);
        Assert.Equal((0.5f, 300f), (active.Near, active.Far));
#pragma warning disable CS0618   // the legacy flags are still what cam_free and the crosshair read
        Assert.True(active.DrivenByRig);
#pragma warning restore CS0618
    }

    // With no camera entity the director must change nothing about ActiveCamera — the editor's free
    // camera goes on driving it exactly as before — and the screen view is made
    // from it, so a renderer that reads only CameraViews still draws what it did. "As before" is
    // measured: the same frames in a world whose director is switched off.
    [Fact]
    public void WithNoCameraEntity_ActiveCameraIsLeftExactlyAsItWas()
    {
        using var app = App();
        using var without = App();
        Assert.True(without.World.Systems.Disable(CameraDirector.Id));
#pragma warning disable CS0618
        foreach (bool rigEnabled in new[] { true, false })
        foreach (bool driven in new[] { true, false })
        {
            foreach (var world in new[] { app.World, without.World })
            {
                var camera = world.Resources.Get<ActiveCamera>();
                camera.Position = new Vector3(7, 8, 9);
                camera.Rotation = Quaternion.CreateFromYawPitchRoll(1f, 0.1f, 0f);
                camera.FovY = 1.1f;
                camera.Near = 0.2f;
                camera.Far = 500f;
                camera.RigEnabled = rigEnabled;
                camera.DrivenByRig = driven;
                world.RunFrame(Frame, 0.5f);
            }

            var active = app.World.Resources.Get<ActiveCamera>();
            var expected = without.World.Resources.Get<ActiveCamera>();
            Assert.Equal((expected.Position, expected.Rotation, expected.FovY, expected.Near, expected.Far),
                         (active.Position, active.Rotation, active.FovY, active.Near, active.Far));
            Assert.Equal((expected.RigEnabled, expected.DrivenByRig), (active.RigEnabled, active.DrivenByRig));
            Assert.Equal(new Vector3(7, 8, 9), active.Position);

            var views = app.World.Resources.Get<CameraViews>();
            Assert.Equal(1, views.Count);
            var main = views.Main;
            Assert.True(main.FromActiveCamera && main.IsScreen);
            Assert.Equal((active.Position, active.Rotation, active.FovY, active.Near, active.Far),
                         (main.Position, main.Rotation, main.FovY, main.Near, main.Far));
            Assert.Equal(CameraProjection.Perspective, main.Projection);
            Assert.Equal(CameraViewport.Full, main.Viewport);
        }
#pragma warning restore CS0618
    }

    // cam_free: the editor camera flies over whatever camera entity had the screen, and gets it back.
    [Fact]
    public void CamFree_GivesTheScreenToTheFreeCamera_AndLeavesActiveCameraToIt()
    {
        using var app = App();
        var world = app.World;
        var e = CameraAt(world, new Vector3(1, 1, 1), Camera.Perspective());
        var active = world.Resources.Get<ActiveCamera>();
        world.RunFrame(Frame, 1f);
#pragma warning disable CS0618
        Assert.True(active.DrivenByRig);

        active.RigEnabled = false;                 // what DevTools does for cam_free
        active.Position = new Vector3(50, 0, 0);   // and where the free camera is
        world.RunFrame(Frame, 1f);
        var views = world.Resources.Get<CameraViews>();
        Assert.True(views.Main.FromActiveCamera);
        Assert.Equal(new Vector3(50, 0, 0), active.Position);   // not overwritten
        Assert.Equal(new Vector3(50, 0, 0), views.Main.Position);
        Assert.False(active.DrivenByRig);          // so the free camera may write it next frame

        active.RigEnabled = true;
        world.RunFrame(Frame, 1f);
        Assert.Equal(e, views.Main.Entity);
        Assert.Equal(new Vector3(1, 1, 1), active.Position);
        Assert.True(active.DrivenByRig);
#pragma warning restore CS0618
    }

    // Without a rig a camera looks from its GlobalTransform interpolated to the frame; a rig's CameraPose
    // replaces that while it is there.
    [Fact]
    public void ThePoseIsTheRigsWhenThereIsOne_ElseTheInterpolatedTransform()
    {
        using var app = App();
        var world = app.World;
        var e = CameraAt(world, Vector3.Zero, Camera.Perspective());
        world.Get<GlobalTransform>(e) = new GlobalTransform
        {
            Previous = new Pose { Position = new Vector3(0, 0, 0), Rotation = Quaternion.Identity, Scale = Vector3.One },
            Current = new Pose { Position = new Vector3(10, 0, 0), Rotation = Quaternion.Identity, Scale = Vector3.One },
        };
        world.RunFrame(Frame, 0.25f);
        var views = world.Resources.Get<CameraViews>();
        Assert.Equal(2.5f, views.Main.Position.X, 4);

        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f);
        world.Add(e, new CameraPose(new Vector3(0, 1.7f, 0), turned));
        world.RunFrame(Frame, 0.25f);
        Assert.Equal(new Vector3(0, 1.7f, 0), views.Main.Position);
        Assert.True(Quaternion.Dot(turned, views.Main.Rotation) > 0.99999f);

        world.Remove<CameraPose>(e);   // the rig lets go: back to the transform
        world.RunFrame(Frame, 1f);
        Assert.Equal(10f, views.Main.Position.X, 4);
    }

    // D2's hook: a rig in FrameUpdate declared Before the director is read the same frame.
    [Fact]
    public void ARigOrderedBeforeTheDirectorIsSeenTheSameFrame()
    {
        using var app = App();
        var world = app.World;
        var e = CameraAt(world, Vector3.Zero, Camera.Perspective());
        world.Add(e, new CameraPose(Vector3.Zero, Quaternion.Identity));
        var rig = new TestRig(e);
        world.AddSystem(rig, Phase.FrameUpdate, id: "test.camera.rig", before: new[] { CameraDirector.Id });

        var reader = new ActiveCameraReader(world.Resources.Get<ActiveCamera>());
        world.AddSystem(reader, Phase.FrameUpdate, id: "test.camera.reader");   // registered after, unordered

        var views = world.Resources.Get<CameraViews>();
        for (int i = 1; i <= 3; i++)
        {
            world.RunFrame(Frame, 1f);
            Assert.Equal(new Vector3(i, 0, 0), views.Main.Position);
            Assert.Equal(new Vector3(i, 0, 0), reader.Seen);   // a later FrameUpdate reader sees this frame's mirror
        }

        var order = world.Systems.Where(s => s.Phase == Phase.FrameUpdate).Select(s => s.Id).ToList();
        Assert.True(order.IndexOf(FirstPersonRigSystem.Id) < order.IndexOf(CameraDirector.Id));   // the engine's rigs first (#78)
        Assert.True(order.IndexOf(PlayerCameraSystem.Id) < order.IndexOf(FirstPersonRigSystem.Id));
        Assert.True(order.IndexOf("test.camera.rig") < order.IndexOf(CameraDirector.Id));
    }

    // One view per target; off-screen targets first by name, the screen last; viewports resolved.
    [Fact]
    public void ViewsAreOnePerTarget_OffScreenFirstByName_TheScreenLast()
    {
        using var app = App();
        var world = app.World;
        var screen = CameraAt(world, Vector3.Zero, Camera.Perspective());
        var mirror = Camera.Perspective(priority: 1);
        mirror.Target = "mirror";
        mirror.Viewport = new CameraViewport(0.5f, 0f, 0.8f, 1f);   // runs off the right edge: clamped
        var mirrorCam = CameraAt(world, Vector3.UnitX, mirror);
        var weaker = mirror;
        weaker.Priority = 0;
        CameraAt(world, Vector3.UnitY, weaker);
        var map = Camera.Orthographic(40f);
        map.Target = "automap";
        map.Viewport = default;   // empty: all of the target
        var mapCam = CameraAt(world, Vector3.UnitZ, map);

        world.RunFrame(Frame, 1f);
        var views = world.Resources.Get<CameraViews>();
        Assert.Equal(new[] { "automap", "mirror", "" }, views.Views.ToArray().Select(v => v.Target).ToArray());
        Assert.Equal(2, views.MainIndex);
        Assert.Equal((mapCam, mirrorCam, screen), (views[0].Entity, views[1].Entity, views[2].Entity));
        Assert.Equal(1, views.IndexOf("mirror"));
        Assert.Equal(-1, views.IndexOf("nothing"));
        Assert.Equal(new CameraViewport(0.5f, 0f, 0.5f, 1f), views[1].Viewport);
        Assert.Equal(CameraViewport.Full, views[0].Viewport);
        Assert.Equal((CameraProjection.Orthographic, 40f), (views[0].Projection, views[0].OrthoHeight));

        // No screen camera and no ActiveCamera (a headless world without the character plugin): no main.
        using var bare = HeadlessApp.Bare().Boot("bare");
        CameraAt(bare.World, Vector3.Zero, map);
        bare.World.RunFrame(Frame, 1f);
        var only = bare.World.Resources.Get<CameraViews>();
        Assert.Equal(1, only.Count);
        Assert.False(only.HasMain);
        Assert.False(only.TryGetMain(out _));
        Assert.Throws<InvalidOperationException>(() => only.Main.Position);
    }

    // A camera zeroed by hand, or half-written in content, resolves to something drawable.
    [Fact]
    public void NonsenseResolvesToTheDefaults()
    {
        var view = CameraDirector.Resolve(new Camera { Enabled = true, Near = -1f, Far = float.NaN, FovY = 400f },
                                          default, Vector3.Zero, default);
        Assert.Equal(Camera.DefaultNear, view.Near);
        Assert.Equal(Camera.DefaultFar, view.Far);
        Assert.Equal(Camera.DefaultFovY * MathF.PI / 180f, view.FovY, 5);
        Assert.Equal(Camera.DefaultOrthoHeight, view.OrthoHeight);
        Assert.Equal(Quaternion.Identity, view.Rotation);
        Assert.Equal(CameraViewport.Full, view.Viewport);
        Assert.Equal("", view.Target);
        Assert.True(view.IsScreen);

        var far = CameraDirector.Resolve(new Camera { Near = 2000f, Far = 10f }, default, Vector3.Zero, Quaternion.Identity);
        Assert.True(far.Far > far.Near);
        // Everything it resolves makes a matrix without throwing.
        _ = view.ProjectionMatrix(16f / 9f);
        _ = far.ProjectionMatrix(1f);
    }

    [Fact]
    public void TheCameraPart_IsAWorkingCameraFromItsDefaults_AndTakesTheComponentsFields()
    {
        using var app = HeadlessApp.Bare()
            .File("data/cameras.json", """
                [
                  { "type": "prefab", "id": "plain", "parts": { "camera": {} } },
                  { "type": "prefab", "id": "map", "parts": { "camera": {
                      "projection": "Orthographic", "orthoHeight": 24, "priority": 3, "target": "automap",
                      "viewport": { "x": 0.75, "y": 0, "width": 0.25, "height": 0.25 } } } }
                ]
                """, "test")
            .Boot("parts");
        var world = app.World;
        var plain = world.Get<Camera>(world.Spawn(new RecordId("test", "plain")));
        Assert.True(plain.Enabled);
        Assert.Equal((CameraProjection.Perspective, Camera.DefaultFovY, Camera.DefaultNear, Camera.DefaultFar),
                     (plain.Projection, plain.FovY, plain.Near, plain.Far));
        Assert.Equal(CameraViewport.Full, plain.Viewport);
        Assert.True(plain.IsScreen);

        var map = world.Get<Camera>(world.Spawn(new RecordId("test", "map")));
        Assert.Equal((CameraProjection.Orthographic, 24f, 3, "automap"), (map.Projection, map.OrthoHeight, map.Priority, map.Target));
        Assert.Equal(new CameraViewport(0.75f, 0f, 0.25f, 0.25f), map.Viewport);

        world.RunFrame(Frame, 1f);
        var views = world.Resources.Get<CameraViews>();
        Assert.Equal(new[] { "automap", "" }, views.Views.ToArray().Select(v => v.Target).ToArray());
    }

    // Origin rebasing moves rig poses and the resolved views with everything else.
    [Fact]
    public void RebasingMovesRigPosesAndViews()
    {
        using var app = App();
        var world = app.World;
        var e = CameraAt(world, Vector3.Zero, Camera.Perspective());
        world.Add(e, new CameraPose(new Vector3(100, 0, 0), Quaternion.Identity));
        world.RunFrame(Frame, 1f);
        var views = world.Resources.Get<CameraViews>();

        var offset = world.Rebase(new SectorCoord(1, 0));
        Assert.NotEqual(Vector3.Zero, offset);
        Assert.Equal(new Vector3(100, 0, 0) + offset, world.Get<CameraPose>(e).Position);
        Assert.Equal(new Vector3(100, 0, 0) + offset, views.Main.Position);
    }

    // ---- CameraMath ------------------------------------------------------------------------------

    [Fact]
    public void PerspectiveMatchesTheTextbookMatrix()
    {
        float fov = MathF.PI / 3f, aspect = 16f / 9f, near = 0.1f, far = 100f;
        var m = CameraMath.Perspective(fov, aspect, near, far);
        float y = 1f / MathF.Tan(fov / 2f);
        Assert.Equal(y / aspect, m.M11, 5);
        Assert.Equal(y, m.M22, 5);
        Assert.Equal(far / (near - far), m.M33, 5);
        Assert.Equal(-1f, m.M34);
        Assert.Equal(near * far / (near - far), m.M43, 5);
        Assert.Equal(0f, m.M44);

        // The near plane lands on depth 0 and the far plane on 1 (right-handed, looking down -Z).
        Assert.Equal(0f, Depth(m, -near), 5);
        Assert.Equal(1f, Depth(m, -far), 4);
    }

    [Fact]
    public void OrthographicMatchesTheTextbookMatrix_WidthFollowsTheAspect()
    {
        float height = 10f, aspect = 2f, near = 0.5f, far = 50f;
        var m = CameraMath.Orthographic(height, aspect, near, far);
        Assert.Equal(2f / (height * aspect), m.M11, 6);
        Assert.Equal(2f / height, m.M22, 6);
        Assert.Equal(1f / (near - far), m.M33, 6);
        Assert.Equal(near / (near - far), m.M43, 6);
        Assert.Equal(1f, m.M44);
        Assert.Equal(0f, m.M34);

        // The viewport's top edge is height/2 above the camera, and its right edge width/2 across.
        var top = Vector4.Transform(new Vector4(0, height / 2f, -near, 1), m);
        var right = Vector4.Transform(new Vector4(height * aspect / 2f, 0, -far, 1), m);
        Assert.Equal(1f, top.Y, 5);
        Assert.Equal(0f, top.Z, 5);
        Assert.Equal(1f, right.X, 5);
        Assert.Equal(1f, right.Z, 5);

        var view = new CameraView { Projection = CameraProjection.Orthographic, OrthoHeight = height, Near = near, Far = far };
        Assert.Equal(m, view.ProjectionMatrix(aspect));
    }

    [Fact]
    public void TheViewMatrixPutsWhatTheCameraFacesDownMinusZ()
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);   // turned left: facing -X
        var position = new Vector3(5, 2, 0);
        var view = CameraMath.View(position, rotation);
        var ahead = Vector3.Transform(position + new Vector3(-3, 0, 0), view);
        Assert.Equal(new Vector3(0, 0, -3), Round(ahead));
        var above = Vector3.Transform(position + Vector3.UnitY, view);
        Assert.Equal(Vector3.UnitY, Round(above));

        // Camera-relative: the same turn, with the position already subtracted.
        Assert.Equal(new Vector3(0, 0, -3), Round(Vector3.Transform(new Vector3(-3, 0, 0), CameraMath.ViewRelative(rotation))));
        Assert.Equal(Round(CameraMath.Forward(rotation)), new Vector3(-1, 0, 0));
        Assert.Equal(960f / 1080f, CameraMath.Aspect(1920, 1080, new CameraViewport(0.5f, 0f, 0.5f, 1f)), 4);
        Assert.Equal(1920f / 1080f, CameraMath.Aspect(1920, 1080, default), 4);
        Assert.Equal(1f, CameraMath.Aspect(0, 0, CameraViewport.Full));
    }

    private static float Depth(Matrix4x4 projection, float viewZ)
    {
        var clip = Vector4.Transform(new Vector4(0, 0, viewZ, 1), projection);
        return clip.Z / clip.W;
    }

    private static Vector3 Round(Vector3 v) => new(MathF.Round(v.X, 4) + 0f, MathF.Round(v.Y, 4) + 0f, MathF.Round(v.Z, 4) + 0f);

    private sealed class TestRig : ISystem
    {
        private readonly Entity _camera;
        private int _frame;
        public TestRig(Entity camera) { _camera = camera; }
        public void Run(in SystemContext ctx) =>
            ctx.World.Get<CameraPose>(_camera).Position = new Vector3(++_frame, 0, 0);
    }

    private sealed class ActiveCameraReader : ISystem
    {
        private readonly ActiveCamera _camera;
        public Vector3 Seen;
        public ActiveCameraReader(ActiveCamera camera) { _camera = camera; }
        public void Run(in SystemContext ctx) => Seen = _camera.Position;
    }
}

// Zero per-frame allocation with cameras on several targets, rigged and not (02 §4.6). Allocation is
// measured per thread, alone.
[Collection(MeasurementsCollection.Name)]
public class CameraAllocationTests
{
    public CameraAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void ResolvingCamerasEveryFrameAllocatesNothing()
    {
        using var app = HeadlessApp.Gameplay().Boot("alloc");
        var world = app.World;
        string[] targets = { "", "mirror", "automap", "" };
        for (int i = 0; i < 12; i++)
        {
            var camera = i % 2 == 0 ? Camera.Perspective(priority: i) : Camera.Orthographic(priority: i);
            camera.Target = targets[i % targets.Length];
            var e = world.Create(Transform.At(new Vector3(i, 0, 0)));
            world.Add(e, camera);
            if (i % 3 == 0) world.Add(e, new CameraPose(new Vector3(i, 1, 0), Quaternion.Identity));
        }
        for (int i = 0; i < 5; i++) { world.RunFixed(1f / 60f); world.RunFrame(1f / 60f, 0.5f); Profiler.EndFrame(); }   // warm up

        var views = world.Resources.Get<CameraViews>();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            world.RunFrame(1f / 60f, i / 200f);
            Profiler.EndFrame();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(3, views.Count);
        Assert.Equal(0, allocated);
    }
}
