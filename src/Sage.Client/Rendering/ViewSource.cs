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
    public int Source;          // its index in the world's CameraViews; -1: none (issue 4n-19)
    public bool NoShadows, NoViewmodel, NoSky, NoDebugLines;   // the passes its camera leaves out (issue 4n-19)
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

    private readonly World _world;
    private readonly ActiveCamera _camera;
    private readonly CameraViews? _views;
    private readonly Renderer _renderer;
    private readonly CVar<int> _testView;
    private int _testTarget = -1;
    private readonly Sage.UI.UiScreenStack? _screens;
    private readonly System.Collections.Generic.List<Sage.UI.UiWorldView> _widgetViews = new();

    public ViewSource(World world, Renderer renderer, CVar<int> testView)
    {
        _world = world;
        _camera = world.Resources.Get<ActiveCamera>();
        world.Resources.TryGet(out _views);
        _renderer = renderer;
        _testView = testView;
        world.Resources.TryGet(out _screens);   // Sage.UI's widget screens, whose view widgets add cameras (#348)
    }

    // `time`: the frame's real time, seconds (a view widget's Spin turns with it).
    public void Collect(PooledList<ViewRequest> views, double time = 0d)
    {
        views.Clear();
        if (_views != null) FromCameraViews(views);
        else FromActiveCamera(views);

        switch (_testView.Value)
        {
            case 1: AddSplitScreen(views); break;
            case 2: AddMinimap(views); break;
        }
        if (_screens != null) AddWidgetViews(views, time);
    }

    // Render targets inside widgets (issue #348): each view widget on screen with a camera of its own
    // (Sage.UI's View, WorldViews.TryPose) draws into its target, sized to the pixels it covers (or its
    // Resolution), before the UI that shows it is drawn. One frame behind the layout, as it is made in
    // Overlay. Its camera draws the screen's own player, unhidden: a paper doll is the pawn a
    // first-person view leaves out.
    private void AddWidgetViews(PooledList<ViewRequest> views, double time)
    {
        _screens!.CollectViews(_widgetViews);
        for (int i = 0; i < _widgetViews.Count; i++)
        {
            var item = _widgetViews[i];
            var view = item.View;
            if (!Sage.UI.WorldViews.TryPose(_world, view, item.Context, time, out var pose)) continue;
            var (width, height) = TargetPixels(view.Resolution, item.Pixels.Width, item.Pixels.Height);
            int target = _renderer.DeclareTargetId(view.Target!, width, height);

            ref var request = ref views.Add();
            request.Position = pose.Position;      // System.Numerics → MonoGame (implicit)
            request.Rotation = pose.Rotation;
            request.Orthographic = pose.Orthographic;
            request.FovY = pose.FovY;
            request.OrthoHeight = pose.OrthoHeight;
            request.Near = pose.Near;
            request.Far = pose.Far;
            request.Rect = new Vector4(0f, 0f, 1f, 1f);
            request.Target = target;
            request.Order = 0;
            request.Main = false;
            request.Hidden = default;
            request.Source = -1;
            request.NoShadows = !view.Shadows;
            request.NoViewmodel = true;
            request.NoSky = !view.Sky;
            request.NoDebugLines = true;
        }
        _widgetViews.Clear();   // holds widgets: let a closed screen's go
    }

    // A widget view's target size: its pixels on screen (or `resolution` across the longer side, the
    // shape kept), rounded up to 8 so a few pixels of layout do not remake it, within 8..2048.
    internal static (int Width, int Height) TargetPixels(int resolution, float width, float height)
    {
        width = MathF.Max(width, 1f);
        height = MathF.Max(height, 1f);
        if (resolution > 0)
        {
            float k = resolution / MathF.Max(width, height);
            width *= k;
            height *= k;
        }
        static int Round(float v) => Math.Clamp(((int)MathF.Ceiling(v) + 7) / 8 * 8, 8, 2048);
        return (Round(width), Round(height));
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
            request.Order = view.Slot;                // one view per target and slot (CameraViews' contract)
            request.Main = i == resolved.MainIndex;
            request.Hidden = HiddenFor(view);
            request.Source = i;
            request.NoShadows = view.NoShadows;
            request.NoViewmodel = view.NoViewmodel;
            request.NoSky = view.NoSky;
            request.NoDebugLines = view.NoDebugLines;
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
        main.Source = -1;
        main.NoShadows = main.NoViewmodel = main.NoSky = main.NoDebugLines = false;
    }

    // **The seam for "don't draw my own body"** (#79: a first-person camera must not see the pawn it
    // sits in; the third-person camera behind it must). The entity a view leaves out: MeshExtract and
    // SpriteExtract skip an entity whose id this is, for this view only. The view's camera entity says
    // (`CameraRigs.HiddenBy`: the pawn a first-person rig sits in, unless it shows it). One entity, not a
    // subtree: the owner's attachments (a held weapon) are drawn unless they are hidden the same way.
    private Entity HiddenFor(in CameraView view) => CameraRigs.HiddenBy(_world, view.Entity);

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
        second.NoViewmodel = true;   // its eye is behind the camera's: arms there would float in front of it
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
        map.NoViewmodel = true;      // top-down: no arms
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
