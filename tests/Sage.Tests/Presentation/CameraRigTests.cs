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
}
