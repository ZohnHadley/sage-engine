#nullable enable
using System;
using Microsoft.Xna.Framework;

namespace Sage.Client;

// One camera the world is drawn from this frame, before CameraExtract turns it into a RenderView
// (matrices, pixels, frustum). Origin space, not yet camera-relative.
internal struct ViewRequest
{
    public Vector3 Position;
    public Quaternion Rotation;
    public bool Orthographic;
    public float FovY;          // radians, perspective
    public float OrthoHeight;   // metres from the bottom of the view to the top, orthographic
    public float Near, Far;
    public Vector4 Rect;        // normalised x, y, width, height in the target (0,0,1,1 = all of it)
    public int Target;          // RenderViewPlan.Screen or a Renderer target id
    public int Order;           // within a target, lower draws first
    public bool Main;           // the screen's main view: what the HUD and floating numbers project with
}

// Where a world's views come from (issue #77). The one place that knows: today a single view from
// `ActiveCamera`, the resource every world has; once camera components land (#76) the resolved
// `CameraViews` list, with ActiveCamera as the fallback for a world without one. Everything after this
// (CameraExtract, the per-view extracts, the renderer) works on a list of any length.
//
// Also the home of `r_testview`, a developer's way to see the multi-view path work with no camera
// entities at all: 1 splits the screen with a second view from behind and above; 2 draws a top-down
// orthographic view into the render target `testview`, which the HUD shows in a corner.
internal sealed class ViewSource
{
    public const string TestTarget = "testview";
    private const int TestTargetSize = 256;

    private readonly ActiveCamera _camera;
    private readonly Renderer _renderer;
    private readonly CVar<int> _testView;
    private int _testTarget = -1;

    public ViewSource(World world, Renderer renderer, CVar<int> testView)
    {
        _camera = world.Resources.Get<ActiveCamera>();
        _renderer = renderer;
        _testView = testView;
    }

    public void Collect(PooledList<ViewRequest> views)
    {
        views.Clear();

        ref var main = ref views.Add();
        main.Position = _camera.Position;        // System.Numerics → MonoGame (implicit)
        main.Rotation = _camera.Rotation;
        main.Orthographic = false;
        main.FovY = _camera.FovY;
        main.OrthoHeight = 0f;
        main.Near = _camera.Near;
        main.Far = _camera.Far;
        main.Rect = new Vector4(0f, 0f, 1f, 1f);
        main.Target = RenderViewPlan.Screen;
        main.Order = 0;
        main.Main = true;

        switch (_testView.Value)
        {
            case 1: AddSplitScreen(views); break;
            case 2: AddMinimap(views); break;
        }
    }

    // The main view on the left half, and the same camera pulled back four metres and up two on the right.
    private static void AddSplitScreen(PooledList<ViewRequest> views)
    {
        views[0].Rect = new Vector4(0f, 0f, 0.5f, 1f);
        var main = views[0];
        ref var second = ref views.Add();
        second = main;
        second.Main = false;
        second.Order = 1;
        second.Rect = new Vector4(0.5f, 0f, 0.5f, 1f);
        var back = Vector3.Transform(Vector3.Backward, main.Rotation);
        second.Position = main.Position + back * 4f + Vector3.Up * 2f;
    }

    // Straight down from forty metres, orthographic, sixty metres across, into the test target.
    private void AddMinimap(PooledList<ViewRequest> views)
    {
        if (_testTarget < 0) _testTarget = _renderer.DeclareTargetId(TestTarget, TestTargetSize, TestTargetSize);
        var main = views[0];
        ref var map = ref views.Add();
        map = main;
        map.Main = false;
        map.Target = _testTarget;
        map.Orthographic = true;
        map.OrthoHeight = 60f;
        map.Near = 1f;
        map.Far = 200f;
        map.Position = main.Position + Vector3.Up * 40f;
        // Looking down (-Y), with the camera's forward, flattened, as the top of the map.
        var forward = Vector3.Transform(Vector3.Forward, main.Rotation);
        float yaw = MathF.Atan2(-forward.X, -forward.Z);
        map.Rotation = Quaternion.CreateFromYawPitchRoll(yaw, -MathHelper.PiOver2, 0f);
    }
}
