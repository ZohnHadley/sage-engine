#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// First-person arms (issue #121, docs/design/12 "As built (first-person arms)", 06 "As built (the viewmodel
// pass)"): the `viewmodel` record, the `sage:viewmodel` component on the player's camera, the arms and the
// weapon ViewmodelSystem spawns in view space, the attack's `arms` choosing them, Reload, and the pass's
// decision to draw (ViewmodelPass: first person only). Headless, against SkinnedModelBuilder's rig: its
// `walk` clip (one second) stands in for the reload, with `mag_out` and `mag_in` added as clip events.
public class ViewmodelTests
{
    public ViewmodelTests() { _ = TestEnv.UserRoot; }

    internal const float Dt = 1f / 60f;

    // The arms' graph: idle, a one-second one-shot reload on the `reload` trigger and a swing on `attack`,
    // each back to idle when its clip is done. Fists show the arms bare; the sword holds the rig (as a
    // mesh) on the tip joint's socket; the stick has no arms at all (the HUD's sprite hands).
    private const string Content = """
    [
      { "type": "anim_graph", "id": "arms", "initial": "idle", "fade": 0,
        "params": { "attack": { "kind": "Trigger" }, "reload": { "kind": "Trigger" } },
        "states": {
          "idle":   { "clip": "idle" },
          "reload": { "clip": "walk", "loop": false, "tags": ["reloading"], "transitions": [ { "to": "idle", "after": 1 } ] },
          "swing":  { "clip": "walk", "loop": false, "transitions": [ { "to": "idle", "after": 1 } ] } },
        "transitions": [ { "to": "reload", "on": "reload" }, { "to": "swing", "on": "attack" } ] },
      { "type": "skeleton_sockets", "id": "rig", "model": "models/rig.glb", "sockets": { "hand_r": { "joint": "tip", "offset": [0, 0.5, 0] } } },
      { "type": "viewmodel", "id": "bare", "model": "models/rig.glb", "graph": "arms", "offset": [0.2, -0.5, -0.6] },
      { "type": "viewmodel", "id": "sword", "model": "models/rig.glb", "graph": "arms", "offset": [0.2, -0.5, -0.6],
        "weapon": "models/rig.glb", "socket": "hand_r" },
      { "type": "attack", "id": "fists", "arms": "bare" },
      { "type": "attack", "id": "sword", "arms": "sword" },
      { "type": "attack", "id": "stick" },
      { "type": "prefab", "id": "hero", "tags": ["player_controlled"],
        "parts": { "character": { "layer": "player" }, "attributes": {}, "melee": { "attack": "fists" } } }
    ]
    """;

    internal static HeadlessApp NewGame()
    {
        var files = new MountFixture();
        files.Write("game", "data/arms.json", Content);
        SkinnedModelBuilder.Write(Path.Combine(files.Dir("game"), "models", "rig.glb"));
        files.Mount("game", "test");
        return HeadlessApp.Gameplay().Mount(files).Boot("arms");
    }

    internal static void Step(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++)
        {
            world.RunFixed(Dt);
            world.RunFrame(Dt, 1f);
        }
    }

    // The player and its camera, three ticks in: the camera spawned, the attack's arms chosen and spawned.
    internal static (Entity Player, Entity Camera, Entity Arms) Ready(World world)
    {
        var player = world.Spawn(new RecordId("test", "hero"), Vector3.Zero);
        Step(world, 3);
        var camera = Assert.Single(world.Query<Camera>().AllTags(Tags.Get<PlayerCamera>()).Entities.ToEntityList());
        var arms = Viewmodels.ArmsOf(world, camera);
        Assert.False(arms.IsNull, "the fists' arms were not spawned");
        return (player, camera, arms);
    }

    private static void Press(World world, ActionId action, long tick)
    {
        var input = world.Resources.Get<PlayerInput>();
        input.HasCommand = true;
        input.Command = new PlayerCommand { Tick = tick, Pressed = default(ActionMask).With(action) };
        Step(world);
        input.Command = new PlayerCommand { Tick = tick + 1 };   // a press is one tick's: the intent keeps the last command's
    }

    // Acceptance: Reload plays the arms' reload, which fires mag_out then mag_in, once each, and goes back
    // to idle when its clip is done.
    [Fact]
    public void Reload_Plays_FiresMagOutThenMagIn_AndReturnsToIdle()
    {
        using var app = NewGame();
        var world = app.World;
        var (_, camera, arms) = Ready(world);
        Assert.True(app.Engine.Animations.TryGet(AssetPath.Intern("models/rig.glb"), out var rig));
        var reloadClip = rig.FindClip(SkinnedModelBuilder.Walk)!;
        reloadClip.AddEvent(0.7f, "mag_in");                   // out of order on purpose: the clip sorts them
        reloadClip.AddEvent(0.3f, "mag_out");
        var events = new EventProbe<AnimationEvent>(world);
        Assert.Equal("idle", Animators.StateOf(world, arms));

        var reload = app.Engine.Actions.Get("Reload");
        Assert.True(reload.IsValid, "the combat plugin registers Reload");
        Press(world, reload, 1);
        Assert.Equal("reload", Animators.StateOf(world, arms));
        Assert.True(Animators.HasTag(world, arms, "reloading"));
        Assert.Empty(events.All);

        Step(world, 30);                                        // half a second: past mag_out, not mag_in
        Assert.Equal(new[] { "mag_out" }, events.All.Select(e => e.Name));
        Step(world, 40);                                        // past the end: back to idle
        Assert.Equal("idle", Animators.StateOf(world, arms));
        Assert.Equal(new[] { "mag_out", "mag_in" }, events.All.Select(e => e.Name));
        Assert.All(events.All, e => Assert.Equal(arms, e.Entity));

        Step(world, 120);                                       // idle loops on; the reload's events are its alone
        Assert.Equal(2, events.All.Count);
        Assert.Equal(arms, Viewmodels.ArmsOf(world, camera));   // nothing respawned
    }

    // A swing that starts sets the arms' `attack` trigger (the Attack button through MeleeCombatSystem).
    [Fact]
    public void ASwingPlaysTheArmsAttack()
    {
        using var app = NewGame();
        var world = app.World;
        var (_, _, arms) = Ready(world);
        Press(world, app.Engine.Actions.Get("Attack"), 1);
        Assert.Equal("swing", Animators.StateOf(world, arms));
    }

    // Acceptance: the viewmodel is drawn only looking out of the first-person rig — not in third person,
    // not from the editor's free camera — while its arms stay spawned and animating underneath.
    [Fact]
    public void TheViewmodelIsHiddenInThirdPersonAndFromTheEditorsFreeCamera()
    {
        using var app = NewGame();
        var world = app.World;
        var (_, camera, arms) = Ready(world);
        Assert.True(Viewmodels.IsDrawn(world));
        Assert.True(ViewmodelPass.TryGet(world, out var view));
        Assert.Equal(camera, view.Camera);
        Assert.Equal(arms, view.Arms);
        Assert.Equal(Viewmodel.DefaultFovY * MathF.PI / 180f, view.FovY, 5);
        Assert.Equal(Viewmodel.DefaultNear, view.Near);
        Assert.True(arms.Tags.Has<ViewmodelLayer>(), "the world's extracts must leave the arms out");

        ToggleViewSystem.Toggle(world);                        // third person
        Step(world);
        Assert.Equal(CameraRigKind.ThirdPerson, world.MainViewRig());
        Assert.False(Viewmodels.IsDrawn(world));
        Assert.Equal(arms, Viewmodels.ArmsOf(world, camera));

        ToggleViewSystem.Toggle(world);                        // and back
        Step(world);
        Assert.True(Viewmodels.IsDrawn(world));

        var free = DebugCamera.Spawn(world, "editor free camera");
        DebugCamera.Drive(world, free, new Vector3(0, 50, 0), Quaternion.Identity, overriding: true);   // cam_free 1
        Step(world);
        Assert.Equal(free, world.Resources.Get<CameraViews>().Main.Entity);
        Assert.False(Viewmodels.IsDrawn(world));

        DebugCamera.Drive(world, free, new Vector3(0, 50, 0), Quaternion.Identity, overriding: false);  // cam_free 0
        Step(world);
        Assert.True(Viewmodels.IsDrawn(world));

        world.Get<Viewmodel>(camera).Enabled = false;          // switched off by the game
        Step(world);
        Assert.False(Viewmodels.IsDrawn(world));
    }

    // The weapon hangs from its socket on the arms (#120's attachment, in view space), the attack in hand
    // chooses the arms, and an attack without any takes them away (the HUD's sprite hands come back).
    [Fact]
    public void TheWeaponHangsFromItsSocket_AndTheAttackInHandChoosesTheArms()
    {
        using var app = NewGame();
        var world = app.World;
        var (player, camera, bare) = Ready(world);
        Assert.True(Viewmodels.WeaponOf(world, camera).IsNull, "fists hold nothing");

        world.Get<Melee>(player).Attack = new RecordId("test", "sword");
        Step(world, 2);
        var arms = Viewmodels.ArmsOf(world, camera);
        var weapon = Viewmodels.WeaponOf(world, camera);
        Assert.False(world.IsAlive(bare), "the old arms were not taken away");
        Assert.False(weapon.IsNull);
        Assert.Equal(arms, weapon.Parent);
        Assert.True(weapon.Tags.Has<ViewmodelLayer>());
        Assert.Equal("hand_r", world.Get<BoneAttachment>(weapon).Socket);

        // Where the tip joint is (idle bobs the root), plus the socket's half metre, from the arms' offset.
        Step(world, 20);
        Assert.True(Animators.TryGetPose(world, arms, out var pose));
        var tip = pose.Skeleton.IndexOf(SkinnedModelBuilder.Tip);
        var expected = Vector3.Transform(new Vector3(0, 0.5f, 0), pose.ModelSpace[tip]) + new Vector3(0.2f, -0.5f, -0.6f);
        var at = world.Get<GlobalTransform>(weapon).Current.Position;
        Assert.True(Vector3.Distance(expected, at) < 1e-3f, $"expected {expected}, got {at}");

        world.Get<Melee>(player).Attack = new RecordId("test", "stick");
        Step(world, 2);
        Assert.True(Viewmodels.ArmsOf(world, camera).IsNull);
        Assert.False(world.IsAlive(arms) || world.IsAlive(weapon), "the sword's arms were left behind");
        Assert.False(Viewmodels.IsDrawn(world));
    }

    // The pass turns each piece's view-space pose into a camera-relative world matrix with the view's
    // rotation: the arms half a metre in front of the eye stay in front of it wherever it looks.
    [Fact]
    public void ThePassPutsThePiecesInFrontOfTheEye_WhereverItLooks()
    {
        using var app = NewGame();
        var world = app.World;
        var (player, _, arms) = Ready(world);
        world.Get<PawnIntent>(player).Yaw = MathF.PI / 2;      // looking along -X
        Step(world);
        Assert.True(ViewmodelPass.TryGet(world, out var view));
        var sink = new Sink();
        Assert.Equal(1, ViewmodelPass.Emit(world, in view, 1f, ref sink));
        Assert.Equal(arms, sink.Last);
        var expected = Vector3.Transform(new Vector3(0.2f, -0.5f, -0.6f), view.Rotation);
        Assert.True(Vector3.Distance(expected, sink.World.Translation) < 1e-3f, $"expected {expected}, got {sink.World.Translation}");
        Assert.True(Vector3.Distance(new Vector3(-0.6f, -0.5f, -0.2f), sink.World.Translation) < 1e-3f);
    }

    // A record without arms or a weapon without a socket is a load error, at its line.
    [Fact]
    public void AViewmodelWithoutArmsOrAWeaponWithoutASocketIsALoadError()
    {
        using var app = HeadlessApp.Simulation().File("data/bad.json", """
        [ { "type": "viewmodel", "id": "nothing" },
          { "type": "viewmodel", "id": "loose", "model": "models/rig.glb", "weapon": "models/rig.glb" } ]
        """).Build();
        Assert.Equal(2, app.Records.ErrorCount);
    }

    internal struct Sink : IViewmodelDraws
    {
        public int Meshes, Skinned;
        public Entity Last;
        public Matrix4x4 World;

        public void Mesh(Entity entity, in MeshRenderer renderer, in Matrix4x4 world)
        {
            Meshes++;
            Last = entity;
            World = world;
        }

        void IViewmodelDraws.Skinned(Entity entity, in SkinnedMeshRenderer renderer, in Matrix4x4 world)
        {
            Skinned++;
            Last = entity;
            World = world;
        }
    }
}

// The viewmodel allocates nothing per frame once spawned: its systems, the director and the pass's
// extract half (ViewmodelPass). Allocation is measured per thread, alone.
[Xunit.Collection(MeasurementsCollection.Name)]
public class ViewmodelAllocationTests
{
    public ViewmodelAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void TheViewmodelAndItsExtractAllocateNothingPerFrame()
    {
        using var app = ViewmodelTests.NewGame();
        var world = app.World;
        var (player, camera, _) = ViewmodelTests.Ready(world);
        world.Get<Melee>(player).Attack = new RecordId("test", "sword");
        for (int i = 0; i < 10; i++) { ViewmodelTests.Step(world); Profiler.EndFrame(); }
        Assert.False(Viewmodels.WeaponOf(world, camera).IsNull);

        // Nothing in the tick or the frame may allocate — except the physics backend's own step
        // (PhysicsStepSystem, 40 bytes a tick with a character in the world), which is not this issue's
        // code; the report shows it by scope (as #120's allocation test does).
        var sink = new ViewmodelTests.Sink();
        int drawn = 0;
        long physicsBefore = ScopeBytes("Fixed.Physics");
        var allocated = AllocationProbe.Measure(200, () =>
        {
            world.RunFixed(ViewmodelTests.Dt);
            world.RunFrame(ViewmodelTests.Dt, 1f);
            if (ViewmodelPass.TryGet(world, out var view)) drawn = ViewmodelPass.Emit(world, in view, 1f, ref sink);
            Profiler.EndFrame();
        });
        Assert.Equal(2, drawn);                                 // the arms and the sword
        Assert.True(sink.Skinned > 0 && sink.Meshes > 0);
        long physics = ScopeBytes("Fixed.Physics") - physicsBefore;
        Assert.True(allocated.Bytes - physics == 0, allocated.ToString());
    }

    private static long ScopeBytes(string name)
    {
        foreach (var entry in Profiler.All)
            if (entry.Name == name) return entry.AllocatedBytes;
        return 0;
    }
}
