#nullable enable
using System;
using System.Numerics;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The editor's picking and gizmo maths (issue #220, docs/design/15 §6): pure functions on rays, plus
// picking in a headless world with colliders.
public class PickingAndGizmoTests
{
    public PickingAndGizmoTests() { _ = TestEnv.UserRoot; }

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-3f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

    // A ray from a camera position through a world point.
    private static EditorRay Ray(Vector3 from, Vector3 to) => new(from, Vector3.Normalize(to - from));

    // ---- Rays ----

    [Fact]
    public void TheCentreOfTheViewportIsTheCameraForwardAndTheTopIsHalfTheFieldOfView()
    {
        var camera = new CameraPose(new Vector3(1, 2, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f));
        var size = new Vector2(800, 600);
        EditorRay centre = EditorPicking.RayFrom(camera, 1f, size, size / 2)!.Value;
        Near(camera.Position, centre.Origin);
        Near(CameraMath.Forward(camera.Rotation), centre.Direction);

        var identity = new CameraPose(Vector3.Zero, Quaternion.Identity);
        EditorRay top = EditorPicking.RayFrom(identity, 1f, size, new Vector2(400, 0))!.Value;
        Assert.Equal(0.5f, MathF.Atan2(top.Direction.Y, -top.Direction.Z), 4);
        Assert.Equal(1f, top.Direction.Length(), 4);

        // Screen right is +X for a camera looking down -Z.
        Assert.True(EditorPicking.RayFrom(identity, 1f, size, new Vector2(0, 300))!.Value.Direction.X < 0f);
        Assert.Null(EditorPicking.RayFrom(identity, 1f, Vector2.Zero, Vector2.Zero));
    }

    [Fact]
    public void ARayAgreesWithTheViewAndProjectionMatrices()
    {
        // Project a point with CameraMath's matrices, ray through the pixel it lands on: the ray passes through the point.
        var camera = new CameraPose(new Vector3(0, 1, 5), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f));
        var size = new Vector2(1280, 720);
        Matrix4x4 viewProjection = CameraMath.View(camera.Position, camera.Rotation) * CameraMath.Perspective(1.1f, size.X / size.Y, 0.1f, 100f);
        Vector3 point = new(-0.5f, 1.5f, 0f);
        Vector4 clip = Vector4.Transform(new Vector4(point, 1f), viewProjection);
        var pixel = new Vector2((clip.X / clip.W * 0.5f + 0.5f) * size.X, (0.5f - clip.Y / clip.W * 0.5f) * size.Y);

        EditorRay ray = EditorPicking.RayFrom(camera, 1.1f, size, pixel)!.Value;
        float along = Vector3.Dot(point - ray.Origin, ray.Direction);
        Near(point, ray.At(along), 2e-3f);
    }

    [Fact]
    public void AnOrthographicRayIsParallelAndOffsetByThePixel()
    {
        var camera = new CameraPose(Vector3.Zero, Quaternion.Identity);
        EditorRay corner = EditorPicking.RayFromOrthographic(camera, 10f, new Vector2(200, 100), new Vector2(200, 0))!.Value;
        Near(new Vector3(10f, 5f, 0f), corner.Origin);     // 10 m tall, so 20 m wide: the top right corner is (10, 5)
        Near(-Vector3.UnitZ, corner.Direction);
    }

    // ---- Picking ----

    private static Engine NewEngine() => HeadlessApp.Bare().With(new PhysicsModule()).Build().Engine;

    private static void Tick(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    [Fact]
    public void PickingTakesTheNearestColliderAndReportsWhereTheRayMetIt()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("edit");
        var near = world.Create(Transform.At(new Vector3(0, 0, -5)), "near");
        world.Add(near, Collider.Box(new Vector3(2, 2, 2)));
        var far = world.Create(Transform.At(new Vector3(0, 0, -10)), "far");
        world.Add(far, Collider.Box(new Vector3(2, 2, 2)));
        Tick(world, 2);

        var down = new EditorRay(Vector3.Zero, -Vector3.UnitZ);
        var hit = EditorPicking.Pick(world, down)!.Value;
        Assert.Equal(near, hit.Entity);
        Assert.Equal(4f, hit.Distance, 2);                 // the box's near face, 1 m in front of its centre
        Near(new Vector3(0, 0, -4), hit.Point);

        Assert.Null(EditorPicking.Pick(world, new EditorRay(Vector3.Zero, Vector3.UnitY)));
        Assert.Equal(far, EditorPicking.Pick(world, down, ignore: near)!.Value.Entity);
        Assert.Null(EditorPicking.Pick(world, down, maxDistance: 3f));
    }

    [Fact]
    public void AnEntityWithNoColliderIsPickedByItsFallbackSphereAndLosesToANearerCollider()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("edit");
        var marker = world.Create(Transform.At(new Vector3(0, 0, -8)), "marker");     // a light, say: nothing to collide with
        var wall = world.Create(Transform.At(new Vector3(0, 0, -4)), "wall");
        world.Add(wall, Collider.Box(new Vector3(4, 4, 1)));
        Tick(world, 2);

        // Through the wall, the wall wins; past its edge the marker is what is there.
        Assert.Equal(wall, EditorPicking.Pick(world, Ray(Vector3.Zero, new Vector3(0, 0, -8)))!.Value.Entity);
        var past = EditorPicking.Pick(world, Ray(new Vector3(5, 0, 0), new Vector3(0.2f, 0, -8)))!.Value;
        Assert.Equal(marker, past.Entity);
        Assert.InRange(past.Distance, 5f, 10f);

        // A ray that passes beyond the sphere misses it.
        Assert.Null(EditorPicking.Pick(world, Ray(new Vector3(5, 0, 0), new Vector3(1.5f, 0, -8))));
    }

    [Fact]
    public void PickingWorksInAWorldWithNoPhysicsAtAll()
    {
        using var engine = HeadlessApp.Bare().Build().Engine;
        var world = engine.CreateWorld("plain");
        var marker = world.Create(Transform.At(new Vector3(2, 0, 0)), "marker");
        var hit = EditorPicking.Pick(world, new EditorRay(Vector3.Zero, Vector3.UnitX))!.Value;
        Assert.Equal(marker, hit.Entity);
        Assert.Equal(2f - EditorPicking.FallbackRadius, hit.Distance, 3);
        Assert.Null(EditorPicking.Pick(world, new EditorRay(Vector3.Zero, -Vector3.UnitX)));   // behind the ray
    }

    // ---- Snap and size ----

    [Fact]
    public void SnapRoundsToTheGridAndTheAngleStepAndZeroMeansOff()
    {
        Assert.Equal(1.5f, Snap.ToGrid(1.4f, 0.5f), 4);
        Assert.Equal(-1.0f, Snap.ToGrid(-0.9f, 0.5f), 4);
        Assert.Equal(1.4f, Snap.ToGrid(1.4f, 0f));
        Assert.Equal(MathF.PI / 4, Snap.Angle(0.7f, 45f), 4);
        Assert.Equal(0.7f, Snap.Angle(0.7f, 0f));
    }

    [Fact]
    public void AGizmoKeepsItsPixelSizeAtAnyDistance()
    {
        float fov = MathF.PI / 2;     // 90°: at distance d the viewport's height covers 2d metres
        Assert.Equal(1f, GizmoMath.ScreenConstantSize(Vector3.Zero, new Vector3(0, 0, -5), fov, 100f, 10f), 4);   // 10 m over 100 px, 10 px
        float near = GizmoMath.ScreenConstantSize(Vector3.Zero, new Vector3(0, 0, -2), fov, 720f, 90f);
        float far = GizmoMath.ScreenConstantSize(Vector3.Zero, new Vector3(0, 0, -20), fov, 720f, 90f);
        Assert.Equal(10f, far / near, 3);
        Assert.Equal(0f, GizmoMath.ScreenConstantSize(Vector3.Zero, Vector3.One, fov, 0f, 90f));
    }

    // ---- Translate gizmo ----

    [Fact]
    public void TheTranslateGizmoFindsAxisAndPlaneHandles()
    {
        var origin = new Vector3(1, 1, 1);
        var camera = new Vector3(5, 5, 8);
        float size = 2f;
        Assert.Equal(GizmoHandle.X, TranslateGizmo.HitTest(Ray(camera, origin + new Vector3(1.4f, 0, 0)), origin, size));
        Assert.Equal(GizmoHandle.Y, TranslateGizmo.HitTest(Ray(camera, origin + new Vector3(0, 1.4f, 0)), origin, size));
        Assert.Equal(GizmoHandle.Z, TranslateGizmo.HitTest(Ray(camera, origin + new Vector3(0, 0, 1.4f)), origin, size));
        Assert.Equal(GizmoHandle.XY, TranslateGizmo.HitTest(Ray(camera, origin + new Vector3(0.75f, 0.75f, 0)), origin, size));
        Assert.Equal(GizmoHandle.XZ, TranslateGizmo.HitTest(Ray(camera, origin + new Vector3(0.75f, 0, 0.75f)), origin, size));
        Assert.Equal(GizmoHandle.YZ, TranslateGizmo.HitTest(Ray(camera, origin + new Vector3(0, 0.75f, 0.75f)), origin, size));

        // Past the end of an axis, beside it, and a gizmo with no size.
        Assert.Equal(GizmoHandle.None, TranslateGizmo.HitTest(Ray(camera, origin + new Vector3(3f, 0, 0)), origin, size));
        Assert.Equal(GizmoHandle.None, TranslateGizmo.HitTest(Ray(camera, origin + new Vector3(1.4f, 0.8f, 0.8f)), origin, size));
        Assert.Equal(GizmoHandle.None, TranslateGizmo.HitTest(Ray(camera, origin + new Vector3(1.4f, 0, 0)), origin, 0f));
    }

    [Fact]
    public void ADragAlongAnAxisMovesOnlyThatAxisByHowFarThePointerWent()
    {
        var camera = new Vector3(0, 4, 10);
        var start = Ray(camera, new Vector3(1, 0, 0));
        var current = Ray(camera, new Vector3(3.5f, 0, 0));
        Near(new Vector3(2.5f, 0, 0), TranslateGizmo.Drag(GizmoHandle.X, start, current, Vector3.Zero)!.Value);
        Assert.Null(TranslateGizmo.Drag(GizmoHandle.None, start, current, Vector3.Zero));

        // The axis snaps on its own and the others stay put.
        Near(new Vector3(3f, 0, 0), TranslateGizmo.Drag(GizmoHandle.X, start, Ray(camera, new Vector3(3.2f, 0, 0)), new Vector3(0.5f, 0, 0), grid: 1f)!.Value + new Vector3(0.5f, 0, 0));
    }

    [Fact]
    public void ADragOnAPlaneMovesBothAxesAndGridSnapsTheResult()
    {
        var origin = new Vector3(0.3f, 0, 0.1f);
        var camera = new Vector3(0, 10, 0.01f);
        var start = Ray(camera, origin);
        var current = Ray(camera, origin + new Vector3(1.2f, 0, -2.3f));
        Near(new Vector3(1.2f, 0, -2.3f), TranslateGizmo.Drag(GizmoHandle.XZ, start, current, origin)!.Value, 2e-3f);

        // On a 1 m grid the result lands on whole metres: 0.3 + 1.2 = 1.5 -> 2, 0.1 - 2.3 = -2.2 -> -2.
        Vector3 snapped = TranslateGizmo.Drag(GizmoHandle.XZ, start, current, origin, grid: 1f)!.Value;
        Near(new Vector3(2f, 0, -2f), origin + snapped, 1e-3f);
        Assert.Equal(0f, snapped.Y);    // the axis not moved is left alone
    }

    [Fact]
    public void ADragHasNoAnswerForARayParallelToItsAxisOrPlaneOrAPlaneBehindTheCamera()
    {
        var origin = Vector3.Zero;
        var along = new EditorRay(new Vector3(-5, 1.5f, 1.5f), Vector3.UnitX);             // runs along the X axis, clear of Y and Z
        Assert.Null(TranslateGizmo.Drag(GizmoHandle.X, along, along, origin));
        Assert.Equal(GizmoHandle.None, TranslateGizmo.HitTest(along, origin, 2f));          // looking down an axis grabs nothing on it
        var flat = new EditorRay(new Vector3(0, 3, 0), Vector3.UnitX);                      // parallel to the XZ plane
        Assert.Null(TranslateGizmo.Drag(GizmoHandle.XZ, flat, flat, origin));
        var away = new EditorRay(new Vector3(0, 3, 0), Vector3.UnitY);                      // the plane is behind it
        Assert.Null(TranslateGizmo.Drag(GizmoHandle.XZ, away, away, origin));
    }

    // ---- Rotate gizmo ----

    [Fact]
    public void TheRotateRingIsHitOnItsCircleOnly()
    {
        var origin = new Vector3(0, 1, 0);
        var camera = new Vector3(0, 6, 6);
        Assert.True(RotateGizmo.HitTest(Ray(camera, origin + new Vector3(2, 0, 0)), origin, 2f));
        Assert.True(RotateGizmo.HitTest(Ray(camera, origin + new Vector3(0, 0, 2.1f)), origin, 2f));
        Assert.False(RotateGizmo.HitTest(Ray(camera, origin), origin, 2f));                  // the centre
        Assert.False(RotateGizmo.HitTest(Ray(camera, origin + new Vector3(3f, 0, 0)), origin, 2f));
        Assert.False(RotateGizmo.HitTest(new EditorRay(new Vector3(0, 5, 0), Vector3.UnitX), origin, 2f));   // parallel
    }

    [Fact]
    public void ARotateDragGivesTheAngleAboutYInTheDirectionQuaternionsTurn()
    {
        var camera = new Vector3(0, 10, 0.001f);
        // CreateFromAxisAngle(UnitY, +90°) takes +X to -Z, so that drag is a positive quarter turn.
        float quarter = RotateGizmo.Drag(Ray(camera, new Vector3(2, 0, 0)), Ray(camera, new Vector3(0, 0, -2)), Vector3.Zero)!.Value;
        Assert.Equal(MathF.PI / 2, quarter, 3);
        Near(-Vector3.UnitZ, Vector3.Transform(Vector3.UnitX, Quaternion.CreateFromAxisAngle(Vector3.UnitY, quarter)));
        Assert.Equal(-MathF.PI / 2, RotateGizmo.Drag(Ray(camera, new Vector3(2, 0, 0)), Ray(camera, new Vector3(0, 0, 2)), Vector3.Zero)!.Value, 3);

        // 50° snapped to 15° steps is 45°.
        float rad = 50 * MathF.PI / 180;
        float snapped = RotateGizmo.Drag(Ray(camera, new Vector3(2, 0, 0)),
            Ray(camera, new Vector3(2 * MathF.Cos(rad), 0, -2 * MathF.Sin(rad))), Vector3.Zero, 15f)!.Value;
        Assert.Equal(45 * MathF.PI / 180, snapped, 3);

        // Across the ±π seam it takes the short way round.
        float seam = RotateGizmo.Drag(Ray(camera, new Vector3(-2, 0, 0.2f)), Ray(camera, new Vector3(-2, 0, -0.2f)), Vector3.Zero)!.Value;
        Assert.InRange(MathF.Abs(seam), 0f, 0.3f);
    }

    [Fact]
    public void ARotateDragHasNoAnswerOnTheAxisOrParallelToThePlane()
    {
        var down = new EditorRay(new Vector3(0, 5, 0), -Vector3.UnitY);                       // through the centre of the ring
        var side = new EditorRay(new Vector3(0, 5, 0), Vector3.UnitX);
        var ok = new EditorRay(new Vector3(2, 5, 0), -Vector3.UnitY);
        Assert.Null(RotateGizmo.Drag(down, ok, Vector3.Zero));
        Assert.Null(RotateGizmo.Drag(ok, side, Vector3.Zero));
    }
}
