#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Camera rigs on camera entities (issues #78, #79; decision D2): the player's camera is an entity spawned
// from `sage:player_camera`, its rig writes CameraPose, and the director draws it. Headless: the rigs,
// the player camera and the question the crosshair asks are all simulation code.
public class CameraRigTests
{
    public CameraRigTests() { _ = TestEnv.UserRoot; }

    private const float Frame = 1f / 60f;

    internal static string SceneOnlyGame => Path.Combine(TestEnv.FolderAbove("tests"), "tests", "games", "scene-only");

    // The game with no C# (a floor, three crates and a player, all persistent), with the engine's content
    // mounted as the host mounts it, so the player camera comes from the real prefab.
    internal static HeadlessApp SceneOnly(string? extra = null)
    {
        var builder = HeadlessApp.ForGame(SceneOnlyGame).WithEngineContent();
        if (extra != null) builder.File("data/extra.json", extra, ns: "sceneonly");
        return builder.Boot();
    }

    internal static void Step(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++)
        {
            world.RunFixed(Frame);
            world.RunFrame(Frame, 1f);
        }
    }

    internal static Entity Player(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    internal static Entity[] PlayerCameras(World world) =>
        world.Query<Camera>().AllTags(Tags.Get<PlayerCamera>()).Entities.ToEntityList().ToArray();

    // Where the rig should put the camera: the character's eye (the swing's and Use's too), at alpha 1.
    internal static Vector3 EyeOf(HeadlessApp app, Entity player)
    {
        var world = app.World;
        var character = world.Get<CharacterController>(player);
        var profile = CharacterConventions.Of(world).ProfileOf(app.Records, character.Profile);
        return CharacterController.EyeOf(world.Get<GlobalTransform>(player).Current.Position, character, profile);
    }

    internal static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-3f) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    // #78's acceptance: the player's camera is an entity, from the engine's prefab, in the player's head.
    [Fact]
    public void ThePlayerGetsACameraEntity_InItsHead_FromTheEnginePrefab()
    {
        using var app = SceneOnly();
        var world = app.World;
        var player = Player(world);
        world.Get<PawnIntent>(player).Yaw = 0.7f;   // what PlayerControlSystem writes from the command
        world.Get<PawnIntent>(player).Pitch = -0.2f;
        Step(world);

        var camera = Assert.Single(PlayerCameras(world));
        Assert.Equal(PlayerCameraSystem.Prefab, world.Get<Sage.Simulation.FromPrefab>(camera).Prefab);
        Assert.Equal(player, world.Get<FirstPersonRig>(camera).Follow);
        Assert.False(world.Has<Camera>(player), "the pawn carries no camera state");

        var views = world.Resources.Get<CameraViews>();
        Assert.Equal(camera, views.Main.Entity);
        Near(EyeOf(app, player), views.Main.Position);
        Assert.True(Quaternion.Dot(Quaternion.CreateFromYawPitchRoll(0.7f, -0.2f, 0f), views.Main.Rotation) > 0.99999f);
        Assert.Equal(CameraRigKind.FirstPerson, world.MainViewRig());

        // Mirrored into ActiveCamera, which audio, weather and the renderer (until #77) read.
        var active = world.Resources.Get<ActiveCamera>();
        Assert.Equal(views.Main.Position, active.Position);
        Assert.Equal(Camera.DefaultFovY * MathF.PI / 180f, active.FovY, 5);

        Step(world, 10);   // one camera, however many frames
        Assert.Single(PlayerCameras(world));
    }

    // A world with no engine content (most tests, tools) still gets a player camera, from the parts' own
    // defaults; the camera does not wait for the prefab.
    [Fact]
    public void WithoutEngineContent_ThePlayerCameraIsBuiltFromThePartsDefaults()
    {
        using var app = HeadlessApp.Gameplay()
            .File("data/hero.json", """
                [ { "type": "prefab", "id": "hero", "tags": ["player_controlled"], "parts": { "character": { "layer": "player" } } } ]
                """, "test")
            .Boot("bare");
        var world = app.World;
        var player = world.Spawn(new RecordId("test", "hero"), new Vector3(3, 0, 4));
        Step(world);

        var camera = Assert.Single(PlayerCameras(world));
        Assert.False(world.Has<Sage.Simulation.FromPrefab>(camera));
        Assert.True(world.Get<Camera>(camera).Enabled);
        Assert.Equal(player, world.Get<FirstPersonRig>(camera).Follow);
        Assert.False(world.Has<Persistent>(camera), "a pawn that is not saved has a camera that is not either");
        Near(EyeOf(app, player), world.Resources.Get<CameraViews>().Main.Position);
    }

    // cam_free still flies the editor camera over the player's, and gives the screen back.
    [Fact]
    public void CamFree_FliesOverThePlayersCamera_AndGivesTheScreenBack()
    {
        using var app = SceneOnly();
        var world = app.World;
        Step(world);
        var camera = Assert.Single(PlayerCameras(world));
        var active = world.Resources.Get<ActiveCamera>();
        var views = world.Resources.Get<CameraViews>();
#pragma warning disable CS0618   // RigEnabled is cam_free's switch until the editor camera is an entity (#81)
        active.RigEnabled = false;                    // what DevTools does for cam_free
        active.Position = new Vector3(0, 50, 0);      // and where the free camera is
        Step(world);
        Assert.True(views.Main.FromActiveCamera);
        Assert.Equal(new Vector3(0, 50, 0), active.Position);
        Assert.Equal(CameraRigKind.None, world.MainViewRig());   // no crosshair, no hands
        Assert.False(active.DrivenByRig);

        active.RigEnabled = true;
        Step(world);
        Assert.Equal(camera, views.Main.Entity);
        Assert.Equal(CameraRigKind.FirstPerson, world.MainViewRig());
        Assert.True(active.DrivenByRig);
#pragma warning restore CS0618
    }

    // A camera of higher priority takes the screen (a cutscene, a fixed camera): the crosshair's question
    // says so, and the player's rig goes on running underneath.
    [Fact]
    public void ACameraOfHigherPriorityTakesTheScreen_AndTheRigKindSaysSo()
    {
        using var app = SceneOnly();
        var world = app.World;
        Step(world);
        var fixedCamera = world.Create(Transform.At(new Vector3(100, 10, 100)));
        world.Add(fixedCamera, Camera.Perspective(priority: 10));
        Step(world);
        Assert.Equal(fixedCamera, world.Resources.Get<CameraViews>().Main.Entity);
        Assert.Equal(CameraRigKind.None, world.MainViewRig());

        world.Destroy(fixedCamera);
        Step(world);
        Assert.Equal(CameraRigKind.FirstPerson, world.MainViewRig());
    }

    // Saves: the camera entity is saved with its pawn and rebuilt with it, so a load neither duplicates
    // nor loses it, and it follows the rebuilt pawn.
    [Fact]
    public void SaveAndLoad_KeepExactlyOneCamera_FollowingTheRebuiltPlayer()
    {
        using var app = SceneOnly();
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var world = app.World;
        Step(world);
        var camera = Assert.Single(PlayerCameras(world));
        var id = world.Get<Persistent>(camera).Id;
        Assert.Equal(PersistentId.FromName($"camera:{world.Get<Persistent>(Player(world)).Id}"), id);

        Assert.True(app.Engine.Saves.Save("rig"));
        for (int load = 0; load < 2; load++)
        {
            Assert.True(app.Engine.Saves.Load("rig"));
            var rebuilt = Assert.Single(PlayerCameras(world));   // before any frame: the save brought it back
            var player = Player(world);
            Assert.Equal(id, world.Get<Persistent>(rebuilt).Id);
            Assert.Equal(player, world.Get<FirstPersonRig>(rebuilt).Follow);

            Step(world, 3);
            Assert.Single(PlayerCameras(world));
            Assert.Equal(rebuilt, world.Resources.Get<CameraViews>().Main.Entity);
            Near(EyeOf(app, player), world.Resources.Get<CameraViews>().Main.Position);
        }
    }

    // What a save cannot carry is repaired, once: a save with no camera in it (from before #78) gets one,
    // and a camera left following nothing is pointed at the player rather than joined by a second.
    [Fact]
    public void ASaveWithoutACamera_GetsOne_AndAnOrphanIsRelinkedNotDuplicated()
    {
        using var app = SceneOnly();
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var world = app.World;
        Step(world);
        var camera = Assert.Single(PlayerCameras(world));

        // As a save from before #78 would be: no camera in it.
        world.Destroy(camera);
        Assert.True(app.Engine.Saves.Save("old"));
        Assert.True(app.Engine.Saves.Load("old"));
        Assert.Empty(PlayerCameras(world));
        Step(world);
        camera = Assert.Single(PlayerCameras(world));
        Assert.Equal(Player(world), world.Get<FirstPersonRig>(camera).Follow);

        // A camera that is not saved beside a pawn that is: the load rebuilds the pawn and leaves the
        // camera following the old one. It is relinked, not joined by a second.
        world.Remove<Persistent>(camera);
        Assert.True(app.Engine.Saves.Save("unsaved-camera"));
        Assert.True(app.Engine.Saves.Load("unsaved-camera"));
        Assert.False(world.IsAlive(world.Get<FirstPersonRig>(camera).Follow));
        Step(world);
        Assert.Equal(camera, Assert.Single(PlayerCameras(world)));
        Assert.Equal(Player(world), world.Get<FirstPersonRig>(camera).Follow);
    }

    // ---- Third person (#79) -----------------------------------------------------------------------

    // A static wall, four metres wide, for the probe to find.
    internal const string Wall = """
        [ { "type": "prefab", "id": "wall", "name": "wall", "parts": { "body": { "size": [4, 4, 0.4] } } } ]
        """;

    // The player camera in third person, looking along -Z (yaw 0), a frame drawn.
    internal static Entity ThirdPerson(World world)
    {
        var player = Player(world);
        world.Get<PawnIntent>(player).Yaw = 0f;
        world.Get<PawnIntent>(player).Pitch = 0f;
        Step(world);
        ToggleViewSystem.Toggle(world);
        Step(world);
        var camera = Assert.Single(PlayerCameras(world));
        Assert.Equal(CameraRigKind.ThirdPerson, world.MainViewRig());
        return camera;
    }

    // How far the screen's camera is from the player's eye.
    internal static float Boom(HeadlessApp app) =>
        Vector3.Distance(EyeOf(app, Player(app.World)), app.World.Resources.Get<CameraViews>().Main.Position);

    // The prefab's defaults: three metres back, a shoulder out and up, out of the scenery.
    private static float FullBoom => (ThirdPersonRig.DefaultShoulderOffset + new Vector3(0, 0, ThirdPersonRig.DefaultDistance)).Length();

    // Over the shoulder, looking where the pawn aims; the body is drawn from here, not from the head.
    [Fact]
    public void ThirdPerson_SitsBehindTheShoulder_LookingWhereThePawnAims()
    {
        using var app = SceneOnly();
        var world = app.World;
        var camera = ThirdPerson(world);
        var player = Player(world);
        var view = world.Resources.Get<CameraViews>().Main;

        var expected = EyeOf(app, player) + ThirdPersonRig.DefaultShoulderOffset + new Vector3(0, 0, ThirdPersonRig.DefaultDistance);
        Near(expected, view.Position);                           // yaw 0 looks down -Z: behind is +Z
        Assert.True(Quaternion.Dot(Quaternion.Identity, view.Rotation) > 0.99999f);
        Assert.Equal(camera, view.Entity);

        // The seam #77 left for this: the first-person view hides the body it sits in, this one does not.
        Assert.True(CameraRigs.HiddenBy(world, camera).IsNull);
        ToggleViewSystem.Toggle(world);
        Step(world);
        Assert.Equal(player, CameraRigs.HiddenBy(world, camera));
        world.Get<FirstPersonRig>(camera).ShowBody = true;       // a game with full-body awareness
        Assert.True(CameraRigs.HiddenBy(world, camera).IsNull);
    }

    // #79's acceptance: a wall behind the pawn pulls the camera in — at once — and when it goes the camera
    // eases back out rather than springing.
    [Fact]
    public void AWallBehindThePawn_PullsTheCameraIn_AtOnce_AndItEasesBackOut()
    {
        using var app = SceneOnly(Wall);
        var world = app.World;
        ThirdPerson(world);
        Assert.Equal(FullBoom, Boom(app), 3);

        var eye = EyeOf(app, Player(world));
        var wall = world.Spawn(new RecordId("sceneonly", "wall"), new Vector3(eye.X, eye.Y - 2f, eye.Z + 1.5f));
        Step(world);   // the body goes into the physics world on the tick; the probe finds it the same frame
        float pulled = Boom(app);
        Assert.True(pulled < ThirdPersonRig.DefaultDistance, $"the camera is {pulled} m out, through the wall");
        Assert.True(pulled > 0.3f, $"the camera is {pulled} m out: it should stop at the wall, not in the head");
        var camera = world.Resources.Get<CameraViews>().Main.Position;
        float face = eye.Z + 1.5f - 0.2f;   // the wall's near face
        Assert.True(camera.Z <= face - ThirdPersonRig.DefaultProbeRadius + 0.02f, $"the camera at z {camera.Z} is closer than the probe's radius to the wall at {face}");

        world.Destroy(wall);
        Step(world);
        float easing = Boom(app);
        Assert.True(easing > pulled && easing < FullBoom - 0.1f, $"out to {easing} m in one frame: it should ease, not spring");
        Step(world, 120);   // two seconds, a smoothing time of 0.3
        Assert.Equal(FullBoom, Boom(app), 2);
    }

    // The eased boom is a length, not a position: a floating-origin shift moves the camera with the
    // world and does not reset the easing.
    [Fact]
    public void RebasingMovesTheThirdPersonCamera_WithoutAJump()
    {
        using var app = SceneOnly(Wall);
        var world = app.World;
        var camera = ThirdPerson(world);
        var eye = EyeOf(app, Player(world));
        var wall = world.Spawn(new RecordId("sceneonly", "wall"), new Vector3(eye.X, eye.Y - 2f, eye.Z + 1.5f));
        Step(world);
        world.Destroy(wall);
        Step(world);
        var rig = world.Get<ThirdPersonRig>(camera);
        Assert.True(rig.Settled && rig.Boom < FullBoom - 0.1f, "mid-ease");
        var before = world.Get<CameraPose>(camera).Position;

        var offset = world.Rebase(new SectorCoord(1, 0));
        Assert.NotEqual(Vector3.Zero, offset);
        Assert.Equal(before + offset, world.Get<CameraPose>(camera).Position);
        Assert.Equal(rig.Boom, world.Get<ThirdPersonRig>(camera).Boom);

        Step(world);
        float after = Boom(app);
        Assert.True(after > rig.Boom && after < FullBoom, $"{after} m after the rebase, from {rig.Boom}: it should go on easing");
    }

    // The player's choice is saved on the camera: a load comes back in third person.
    [Fact]
    public void TheChosenViewIsSavedWithTheCamera()
    {
        using var app = SceneOnly();
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var world = app.World;
        ThirdPerson(world);
        Assert.True(app.Engine.Saves.Save("third"));
        ToggleViewSystem.Toggle(world);
        Step(world);
        Assert.Equal(CameraRigKind.FirstPerson, world.MainViewRig());

        Assert.True(app.Engine.Saves.Load("third"));
        var camera = Assert.Single(PlayerCameras(world));
        Assert.True(world.Get<ThirdPersonRig>(camera).Enabled);
        Assert.False(world.Get<FirstPersonRig>(camera).Enabled);
        Assert.Equal(Player(world), world.Get<ThirdPersonRig>(camera).Follow);
        Step(world);
        Assert.Equal(CameraRigKind.ThirdPerson, world.MainViewRig());
        Assert.Equal(FullBoom, Boom(app), 3);
    }

    // #79's other acceptance: the ToggleView button switches the view mid-fight within one frame, and the
    // fight goes on exactly as it would have. Two Sandbox worlds play the same commands; one also presses
    // ToggleView half-way through a swing at a creature.
    [Fact]
    public void ToggleView_MidMeleeInTheSandbox_SwitchesWithinAFrame_AndCombatIsUnaffected()
    {
        using var toggled = Sandbox();
        using var control = Sandbox();
        var attack = toggled.Engine.Actions.Get("Attack");
        var toggle = toggled.Engine.Actions.Get(ToggleViewSystem.Action);
        Assert.True(toggle.IsValid && attack.IsValid);
        var foes = new[] { Foe(toggled), Foe(control) };

        for (int tick = 1; tick <= 90; tick++)
        {
            var held = tick <= 60 ? default(ActionMask).With(attack) : default;
            var pressed = tick == 1 || tick == 40 ? default(ActionMask).With(attack) : default;
            Command(control.World, tick, held, pressed);
            Command(toggled.World, tick, held, tick == 8 ? pressed.With(toggle) : pressed);
            if (tick == 8)
            {
                var melee = toggled.World.Get<Melee>(Player(toggled.World));
                Assert.NotEqual(MeleePhase.Ready, melee.Phase);   // mid-swing
                Assert.Equal(CameraRigKind.FirstPerson, toggled.World.MainViewRig());
            }
            toggled.World.RunFixed(Frame);
            toggled.World.RunFrame(Frame, 1f);
            control.World.RunFixed(Frame);
            control.World.RunFrame(Frame, 1f);
            if (tick == 8)
                Assert.Equal(CameraRigKind.ThirdPerson, toggled.World.MainViewRig());   // the frame after the press
        }

        Assert.Equal(CameraRigKind.ThirdPerson, toggled.World.MainViewRig());
        Assert.Equal(CameraRigKind.FirstPerson, control.World.MainViewRig());

        // The fight: the swings, the creature's health and where everybody is.
        var health = new RecordId("sage", "health");
        float hurt = control.World.Attribute(foes[1], health);
        Assert.True(hurt < 100f, $"the creature was never hit ({hurt}): the test is not a fight");
        Assert.Equal(hurt, toggled.World.Attribute(foes[0], health));
        Assert.Equal(control.World.Attribute(Player(control.World), health), toggled.World.Attribute(Player(toggled.World), health));
        var a = toggled.World.Get<Melee>(Player(toggled.World));
        var b = control.World.Get<Melee>(Player(control.World));
        Assert.Equal((b.Phase, b.Timer, b.Cooldown), (a.Phase, a.Timer, a.Cooldown));
        Assert.Equal(control.World.Get<Transform>(Player(control.World)).LocalPosition,
                     toggled.World.Get<Transform>(Player(toggled.World)).LocalPosition);
        Assert.Equal(control.World.Get<PawnIntent>(Player(control.World)), toggled.World.Get<PawnIntent>(Player(toggled.World)));
    }

    // A scripted cut that locks the player's input (#80) holds this button too: the toggle reads the pawn's
    // intent after the lock, not the raw command.
    [Fact]
    public void ALockingScriptedCameraHoldsTheToggle()
    {
        using var app = SceneOnly();
        var world = app.World;
        Step(world);
        var camera = Assert.Single(PlayerCameras(world));
        var toggle = app.Engine.Actions.Get(ToggleViewSystem.Action);

        var cut = world.Spawn(new RecordId("sage", "scripted_camera"), new Vector3(100, 5, 100));
        world.Get<Camera>(cut).Enabled = true;
        Step(world);
        Assert.Equal(cut, world.Resources.Get<CameraViews>().Main.Entity);
        Command(world, 1, default, default(ActionMask).With(toggle));
        Step(world);
        world.Resources.Get<PlayerInput>().HasCommand = false;   // a press is one tick's
        Assert.True(world.Get<FirstPersonRig>(camera).Enabled, "the cut held the player's buttons, but not this one");

        world.Get<Camera>(cut).Enabled = false;
        Step(world);
        Assert.Equal(CameraRigKind.FirstPerson, world.MainViewRig());
        Command(world, 2, default, default(ActionMask).With(toggle));
        Step(world);
        Assert.Equal(CameraRigKind.ThirdPerson, world.MainViewRig());
    }

    private static string SandboxGame => Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox");

    private static HeadlessApp Sandbox() =>
        HeadlessApp.ForGame(SandboxGame, new global::Sandbox.SandboxModule()).WithEngineContent().Boot();

    // The scene's watcher, stood a pace in front of the player (who faces -Z): something to hit.
    private static Entity Foe(HeadlessApp app)
    {
        var world = app.World;
        var foe = world.FindByName("watcher");
        Assert.False(foe.IsNull);
        var at = world.Get<Transform>(Player(world)).LocalPosition + new Vector3(0, 0, -1.3f);
        var where = Transform.At(at);
        where.LocalRotation = SageMath.RotationFromYaw(MathF.PI);   // facing the player
        world.Teleport(foe, where);
        return foe;
    }

    private static void Command(World world, long tick, ActionMask held, ActionMask pressed)
    {
        var input = world.Resources.Get<PlayerInput>();
        input.HasCommand = true;
        input.Command = new PlayerCommand { Tick = tick, ViewYaw = 0f, ViewPitch = -0.1f, Held = held, Pressed = pressed };
    }
}

// Zero per-frame allocation with the player's camera in its head (02 §4.6). Allocation is measured per
// thread, alone.
[Xunit.Collection(MeasurementsCollection.Name)]
public class CameraRigAllocationTests
{
    public CameraRigAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void ThePlayersCameraAllocatesNothingPerFrame()
    {
        using var app = CameraRigTests.SceneOnly();
        var world = app.World;
        for (int i = 0; i < 5; i++) { CameraRigTests.Step(world); Profiler.EndFrame(); }   // warm up, and the spawn

        var views = world.Resources.Get<CameraViews>();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            world.RunFrame(1f / 60f, i / 200f);
            Profiler.EndFrame();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(CameraRigKind.FirstPerson, world.MainViewRig());
        Assert.False(views.Main.FromActiveCamera);
        Assert.Equal(0, allocated);
    }

    // And over the shoulder, with the probe sweeping every frame against a wall that keeps it pulled in.
    [Fact]
    public void TheThirdPersonCameraAndItsProbeAllocateNothingPerFrame()
    {
        using var app = CameraRigTests.SceneOnly(CameraRigTests.Wall);
        var world = app.World;
        CameraRigTests.ThirdPerson(world);
        var eye = CameraRigTests.EyeOf(app, CameraRigTests.Player(world));
        world.Spawn(new RecordId("sceneonly", "wall"), new Vector3(eye.X, eye.Y - 2f, eye.Z + 1.5f));
        for (int i = 0; i < 5; i++) { CameraRigTests.Step(world); Profiler.EndFrame(); }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            world.RunFrame(1f / 60f, i / 200f);
            Profiler.EndFrame();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(CameraRigKind.ThirdPerson, world.MainViewRig());
        Assert.True(CameraRigTests.Boom(app) < ThirdPersonRig.DefaultDistance, "the probe is not finding the wall");
        Assert.Equal(0, allocated);
    }
}
