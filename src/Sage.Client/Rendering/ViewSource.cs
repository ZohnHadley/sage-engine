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
    public Vector4 Rect;        // normalised x, y, width, height in the target from its top-left (0,0,1,1 = all of it)
    public int Target;          // RenderViewPlan.Screen or a Renderer target id
    public int Order;           // within a target, lower draws first
    public bool Main;           // the screen's main view: what the HUD and floating numbers project with
    public Entity Hidden;       // an entity this view does not draw (null: none); see ViewSource.HiddenFor
}

// Where a world's views come from (issue #77): the one place that knows. The world's `CameraViews`
// (#76: one resolved view per target, the screen's made from ActiveCamera when no camera entity draws
// there), or, for a world without that resource, a single screen view from `ActiveCamera`. Everything
// after this (CameraExtract, the per-view extracts, the renderer) works on a list of any length.
//
// Also the home of `r_testview`, a developer's way to see the multi-view path work with no camera
// entities at all: 1 splits the screen with a second view from behind and above; 2 draws a top-down
// orthographic view into the render target `testview`, which the HUD shows in a corner.
internal sealed class ViewSource
{
    public const string TestTarget = "testview";
    private const int TestTargetSize = 256;

    private readonly ActiveCamera _camera;
    private readonly CameraViews? _views;
    private readonly Renderer _renderer;
    private readonly CVar<int> _testView;
    private int _testTarget = -1;

    public ViewSource(World world, Renderer renderer, CVar<int> testView)
    {
        _camera = world.Resources.Get<ActiveCamera>();
        world.Resources.TryGet(out _views);
        _renderer = renderer;
        _testView = testView;
    }

    public void Collect(PooledList<ViewRequest> views)
    {
        views.Clear();
        if (_views != null) FromCameraViews(views);
        else FromActiveCamera(views);

        switch (_testView.Value)
        {
            case 1: AddSplitScreen(views); break;
            case 2: AddMinimap(views); break;
        }
    }

    private void FromCameraViews(PooledList<ViewRequest> views)
    {
        var resolved = _views!;
        for (int i = 0; i < resolved.Count; i++)
        {
            ref readonly var view = ref resolved[i];
            ref var request = ref views.Add();
            request.Position = view.Position;        // System.Numerics → MonoGame (implicit)
            request.Rotation = view.Rotation;
            request.Orthographic = view.Projection == CameraProjection.Orthographic;
            request.FovY = view.FovY;
            request.OrthoHeight = view.OrthoHeight;
            request.Near = view.Near;
            request.Far = view.Far;
            request.Rect = new Vector4(view.Viewport.X, view.Viewport.Y, view.Viewport.Width, view.Viewport.Height);
            request.Target = view.IsScreen ? RenderViewPlan.Screen : _renderer.TargetId(view.Target);
            request.Order = 0;                        // one view per target (CameraViews' contract)
            request.Main = i == resolved.MainIndex;
            request.Hidden = HiddenFor(view);
        }
    }

    private void FromActiveCamera(PooledList<ViewRequest> views)
    {
        ref var main = ref views.Add();
        main.Position = _camera.Position;
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
        main.Hidden = default;
    }

    // **The seam for "don't draw my own body"** (#79: a first-person camera must not see the pawn it
    // sits in; the third-person camera behind it must). The entity a view leaves out: MeshExtract and
    // SpriteExtract skip an entity whose id this is, for this view only. Nothing sets it yet; the rig
    // that knows its owner fills it here from the view's camera entity (`view.Entity`), e.g. from a
    // component on the camera naming the owner. One entity, not a subtree: the owner's attachments
    // (a held weapon) are drawn unless they are hidden the same way.
    private static Entity HiddenFor(in CameraView view) => default;

    private static int MainIndex(PooledList<ViewRequest> views)
    {
        for (int i = 0; i < views.Count; i++) if (views[i].Main) return i;
        return -1;
    }

    // The main view on the left half, and the same camera pulled back four metres and up two on the right.
    private static void AddSplitScreen(PooledList<ViewRequest> views)
    {
        int at = MainIndex(views);
        if (at < 0) return;
        views[at].Rect = new Vector4(0f, 0f, 0.5f, 1f);
        var main = views[at];
        ref var second = ref views.Add();
        second = main;
        second.Main = false;
        second.Order = 1;
        second.Hidden = default;
        second.Rect = new Vector4(0.5f, 0f, 0.5f, 1f);
        var back = Vector3.Transform(Vector3.Backward, main.Rotation);
        second.Position = main.Position + back * 4f + Vector3.Up * 2f;
    }

    // Straight down from forty metres, orthographic, sixty metres across, into the test target.
    private void AddMinimap(PooledList<ViewRequest> views)
    {
        int at = MainIndex(views);
        if (at < 0) return;
        if (_testTarget < 0) _testTarget = _renderer.DeclareTargetId(TestTarget, TestTargetSize, TestTargetSize);
        var main = views[at];
        ref var map = ref views.Add();
        map = main;
        map.Main = false;
        map.Hidden = default;
        map.Target = _testTarget;
        map.Rect = new Vector4(0f, 0f, 1f, 1f);
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
