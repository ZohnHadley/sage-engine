#nullable enable
using System;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Split screen and secondary views (issue 4n-19): every view gets the whole pass set — its own sun
// shadow map, its own first-person viewmodel — unless its camera turns a pass off (`noShadows`,
// `noViewmodel`, `noSky`, `noDebugLines`), and a screen holds one view per split-screen slot. What each
// view gets is decided headless (CameraViews, ShadowViews, ViewmodelPass); the client's `sage:shadow` and
// viewmodel extract only follow it, and the smoke run draws it.
public class SecondaryViewTests
{
    public SecondaryViewTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    private static void Step(World world, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            world.RunFixed(Dt);
            world.RunFrame(Dt, 1f);
        }
    }

    private static readonly Vector3 NoonSun = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.3f));

    // A camera entity at `at`, looking down -Z, with `setup` applied to its component.
    private static Entity CameraAt(World world, Vector3 at, Func<Camera, Camera>? setup = null)
    {
        var e = world.Create(Transform.At(at), "camera");
        var camera = Camera.Perspective(70f, far: 500f);
        world.Add(e, setup == null ? camera : setup(camera));
        return e;
    }

    // Each slot of the screen resolves its own camera by priority, as each target does; the lowest slot
    // is the main view, the one ActiveCamera mirrors, and the screen's views come last in slot order.
    [Fact]
    public void EachSplitScreenSlotResolvesItsOwnCamera_AndTheLowestIsTheMainView()
    {
        using var app = HeadlessApp.Gameplay().Boot("split");
        var world = app.World;
        var views = world.Resources.Get<CameraViews>();
        var one = CameraAt(world, new Vector3(0, 2, 0), c => { c.Viewport = new CameraViewport(0f, 0f, 0.5f, 1f); return c; });
        var two = CameraAt(world, new Vector3(40, 2, 0), c => { c.Slot = 1; c.Priority = 5; c.Viewport = new CameraViewport(0.5f, 0f, 0.5f, 1f); return c; });
        CameraAt(world, new Vector3(80, 2, 0), c => { c.Slot = 1; c.Priority = 1; return c; });                     // loses slot 1
        var monitor = CameraAt(world, new Vector3(0, 30, 0), c => { c.Target = "monitor"; return c; });
        Step(world);

        Assert.Equal(3, views.Count);
        Assert.Equal(monitor, views[0].Entity);                                    // off-screen first
        Assert.Equal(one, views[1].Entity);                                        // then the screen, by slot
        Assert.Equal(two, views[2].Entity);
        Assert.Equal(1, views.MainIndex);
        Assert.Equal(2, views.IndexOf("", 1));
        Assert.Equal(1, views.IndexOf(""));
        Assert.Equal(new Vector3(0, 2, 0), world.Resources.Get<ActiveCamera>().Position);

        world.Get<Camera>(one).Enabled = false;                                    // player one drops out
        Step(world);
        Assert.Equal(two, views.Main.Entity);                                      // the lowest slot left
    }

    // Done: each split-screen view draws its own viewmodel — player two's camera, in slot 1 with its own
    // first-person rig and arms, gets its own pass beside player one's, with its own pieces — and a
    // camera's `noViewmodel` turns its view's off and no other.
    [Fact]
    public void ASplitScreenPartnerDrawsItsOwnViewmodel()
    {
        using var app = ViewmodelTests.NewGame();
        var world = app.World;
        var (_, camera, arms) = ViewmodelTests.Ready(world);

        var partner = world.Create(Transform.At(new Vector3(30, 1.7f, 0)), "player two's camera");
        var lens = Camera.Perspective(70f);
        lens.Slot = 1;
        lens.Viewport = new CameraViewport(0.5f, 0f, 0.5f, 1f);
        world.Add(partner, lens);
        world.Add(partner, new FirstPersonRig { Enabled = true });
        world.Add(partner, Viewmodel.Create(new RecordId("test", "sword")));
        world.Get<Transform>(partner).LocalRotation = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2f, 0f, 0f);
        ViewmodelTests.Step(world, 3);

        var views = world.Resources.Get<CameraViews>();
        int mine = views.IndexOf("", 0), theirs = views.IndexOf("", 1);
        Assert.Equal(camera, views[mine].Entity);
        Assert.Equal(partner, views[theirs].Entity);

        Assert.True(ViewmodelPass.TryGet(world, in views[mine], out var first));
        Assert.True(ViewmodelPass.TryGet(world, in views[theirs], out var second));
        Assert.Equal(arms, first.Arms);
        var partnerArms = Viewmodels.ArmsOf(world, partner);
        Assert.False(partnerArms.IsNull);
        Assert.Equal(partnerArms, second.Arms);
        Assert.NotEqual(first.Arms, second.Arms);
        Assert.False(second.Weapon.IsNull, "player two holds the sword");
        Assert.Equal(views[theirs].Rotation, second.Rotation);                     // placed by its own view

        // Each view's pieces: player one's bare arms, player two's arms and sword, turned with their view.
        var mineDrawn = new ViewmodelTests.Sink();
        var theirsDrawn = new ViewmodelTests.Sink();
        Assert.Equal(1, ViewmodelPass.Emit(world, in first, 1f, ref mineDrawn));
        Assert.Equal(2, ViewmodelPass.Emit(world, in second, 1f, ref theirsDrawn));
        Assert.Equal(1, theirsDrawn.Skinned);
        Assert.Equal(1, theirsDrawn.Meshes);
        Assert.Equal(arms, mineDrawn.Last);

        // The flag is per view: player two's off, player one's still on.
        world.Get<Camera>(partner).NoViewmodel = true;
        ViewmodelTests.Step(world);
        Assert.False(ViewmodelPass.TryGet(world, in views[views.IndexOf("", 1)], out _));
        Assert.True(ViewmodelPass.TryGet(world, in views[views.IndexOf("", 0)], out _));
        Assert.True(Viewmodels.IsDrawn(world));
    }

    // Done: a camera drawing into a render target (a security monitor 400 m from the player) gets shadow
    // caster views of its own — a map fitted to its camera, holding the ground in front of it — where the
    // main view's map, 400 m away, holds nothing there. A minimap whose camera says `noShadows` gets none;
    // `r_shadow_views` caps how many views are fitted, the main view first.
    [Fact]
    public void ARenderTargetCameraViewGetsShadowCasterViewsOfItsOwn()
    {
        using var app = HeadlessApp.Gameplay().Boot("monitor");
        var world = app.World;
        var player = CameraAt(world, new Vector3(0, 2, 0));
        var monitor = CameraAt(world, new Vector3(400, 3, 0), c => { c.Target = "monitor"; return c; });
        var minimap = CameraAt(world, new Vector3(0, 40, 0), c => { c.Target = "minimap"; c.NoShadows = true; return c; });
        Step(world);
        var views = world.Resources.Get<CameraViews>();
        Assert.Equal(3, views.Count);

        // Which views receive: the main one and the monitor, not the minimap; the main view first.
        float distance = ShadowMath.DefaultDistance;
        var receives = new bool[views.Count];
        for (int v = 0; v < views.Count; v++)
            receives[v] = ShadowViews.Receives(false, false, views[v].NoShadows, views[v].Near, views[v].Far, distance);
        var chosen = new int[ShadowViews.MaxViews];
        int count = ShadowViews.Choose(receives, views.MainIndex, ShadowViews.DefaultViews, chosen);
        Assert.Equal(2, count);
        Assert.Equal(player, views[chosen[0]].Entity);
        Assert.Equal(monitor, views[chosen[1]].Entity);
        Assert.DoesNotContain(chosen.Take(count), i => views[i].Entity == minimap);
        Assert.Equal(1, ShadowViews.Choose(receives, views.MainIndex, max: 1, chosen));   // r_shadow_views 1: the main view only
        Assert.Equal(player, views[chosen[0]].Entity);

        // Each receiver's cascades, fitted to its own camera (the monitor's smaller: r_shadow_view_size).
        var mainFits = FitFor(views[views.MainIndex], ShadowViews.CascadeSize(true, ShadowMath.DefaultSize, ShadowViews.DefaultViewSize));
        int monitorSize = ShadowViews.CascadeSize(false, ShadowMath.DefaultSize, ShadowViews.DefaultViewSize);
        Assert.Equal(ShadowViews.DefaultViewSize, monitorSize);
        var monitorFits = FitFor(views[views.IndexOf("monitor")], monitorSize);

        // The ground ten metres in front of the monitor's camera: in its map, not in the player's.
        var ground = new Vector3(400, 0, -10);
        Assert.True(ShadowMath.CascadeOf(monitorFits, ground - new Vector3(400, 3, 0)) >= 0, "the monitor's map does not hold its own view");
        Assert.Equal(-1, ShadowMath.CascadeOf(mainFits, ground - new Vector3(0, 2, 0)));
        Assert.True(ShadowMath.CascadeOf(mainFits, new Vector3(0, -2, -10)) >= 0);
    }

    private static ShadowFit[] FitFor(in CameraView view, int size)
    {
        var fits = new ShadowFit[ShadowMath.DefaultCascades];
        var splits = new float[fits.Length];
        var projection = view.ProjectionMatrix(16f / 9f);
        Assert.True(ShadowViews.Fit(view.Position, view.Forward, projection, view.Near, view.Far, ShadowMath.DefaultDistance,
                                    NoonSun, size, Vector3.Zero, fits, splits));
        return fits;
    }

    // What a view leaves out is the camera's data: the `camera` part's flags reach its resolved view, and a
    // view with none of them set (a zeroed component, an old save) draws everything. A depth-only view (the
    // viewmodel's) and a caster view never receive a map of their own, nor does a view with no slice.
    [Fact]
    public void PerViewFlagsAreTheCamerasData_AndTurnItsPassesOff()
    {
        const string content = """
        [
          { "type": "prefab", "id": "monitor_cam", "name": "monitor_cam",
            "parts": { "camera": { "target": "monitor", "noShadows": true, "noSky": true, "noDebugLines": true } } },
          { "type": "prefab", "id": "partner", "name": "partner",
            "parts": { "camera": { "slot": 1, "noViewmodel": true, "viewport": { "x": 0.5, "y": 0, "width": 0.5, "height": 1 } } } },
          { "type": "scene", "id": "flags", "place": [ { "prefab": "monitor_cam", "at": [0, 5, 0], "name": "monitor_cam" },
                                                       { "prefab": "partner", "at": [9, 2, 0], "name": "partner" } ] }
        ]
        """;
        using var app = HeadlessApp.Gameplay().File("data/flags.json", content).StartScene("sage:flags").Boot("flags");
        var world = app.World;
        Step(world);
        var views = world.Resources.Get<CameraViews>();
        ref readonly var monitor = ref views[views.IndexOf("monitor")];
        Assert.True(monitor.NoShadows && monitor.NoSky && monitor.NoDebugLines);
        Assert.False(monitor.NoViewmodel);
        ref readonly var partner = ref views[views.IndexOf("", 1)];
        Assert.Equal(world.FindByName("partner"), partner.Entity);
        Assert.True(partner.NoViewmodel);
        Assert.False(partner.NoShadows || partner.NoSky || partner.NoDebugLines);
        Assert.Equal(1, world.Get<Camera>(world.FindByName("partner")).Slot);

        Assert.False(ShadowViews.Receives(false, false, monitor.NoShadows, monitor.Near, monitor.Far, 150f));
        Assert.True(ShadowViews.Receives(false, false, partner.NoShadows, partner.Near, partner.Far, 150f));
        Assert.False(ShadowViews.Receives(shadowCaster: true, false, false, 0.1f, 100f, 150f));
        Assert.False(ShadowViews.Receives(false, depthOnly: true, false, 0.1f, 100f, 150f));
        Assert.False(ShadowViews.Receives(false, false, false, near: 200f, far: 400f, distance: 150f));

        var zeroed = default(Camera);
        Assert.False(zeroed.NoShadows || zeroed.NoViewmodel || zeroed.NoSky || zeroed.NoDebugLines);
    }
}
