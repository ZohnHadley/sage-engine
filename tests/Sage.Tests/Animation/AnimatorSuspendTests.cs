#nullable enable
using System;
using System.IO;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Handing a character to a ragdoll and back (issue #244, docs/design/12 "As built (suspend and fade from
// a snapshot)"): Animators.Suspend/Resume freeze the animator and leave its pose to whoever suspended it,
// and Animators.PlayFrom enters a state cross-fading from a frozen pose. Against SkinnedModelBuilder's rig:
// root → mid → tip, "walk" swings mid about +Z, "idle" bobs the root.
public class AnimatorSuspendTests
{
    public AnimatorSuspendTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;
    private const float Eps = 1e-3f;

    internal const string Graph = """
    [
      { "type": "anim_graph", "id": "hero", "initial": "idle", "fade": 0.25, "ease": "Linear",
        "params": { "speed": { "from": "Speed" }, "go": { "kind": "Trigger" } },
        "states": {
          "idle": { "clip": "idle", "transitions": [ { "to": "walk", "on": "go" } ] },
          "walk": { "clip": "walk" },
          "getup": { "clip": "walk", "loop": false, "transitions": [ { "to": "idle", "when": { "anim_finished": "base" } } ] } } },
      { "type": "prefab", "id": "hero", "name": "hero", "parts": { "animator": { "graph": "hero", "model": "models/rig.glb" } } }
    ]
    """;

    private static HeadlessApp NewGame()
    {
        var files = new MountFixture();
        files.Write("game", "data/hero.json", Graph);
        SkinnedModelBuilder.Write(Path.Combine(files.Dir("game"), "models", "rig.glb"), withRun: true);
        files.Mount("game", "game");
        var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).Mount(files).Boot("anim");
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        return app;
    }

    private static Entity Spawn(World world)
    {
        var e = world.Spawn(new RecordId("game", "hero"));
        e.Name = "hero";
        world.MakePersistent(e);
        return e;
    }

    private static void Tick(World world, int times = 1)
    {
        for (int i = 0; i < times; i++) world.RunFixed(Dt);
    }

    private static AnimationSet Rig(HeadlessApp app) =>
        app.Engine.Animations.TryGet(AssetPath.Intern("models/rig.glb"), out var set) ? set : throw new Xunit.Sdk.XunitException("the rig is not loaded");

    private static SkeletonPose Pose(World world, Entity e) =>
        Animators.TryGetPose(world, e, out var pose) ? pose : throw new Xunit.Sdk.XunitException("no pose");

    private static AnimatorLayer Layer(World world, Entity e) => world.Get<Animator>(e).Layers![0];

    private static SkeletonPose Expected(AnimationSet set, string clip, float time, bool loop = true)
    {
        var pose = new SkeletonPose(set.Skeleton);
        PoseSampler.Sample(set.FindClip(clip)!, time, loop, pose);
        return pose;
    }

    private static void Near(Quaternion expected, Quaternion actual)
    {
        float dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected), Quaternion.Normalize(actual)));
        Assert.True(dot > 1 - Eps, $"expected {expected}, got {actual}");
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < Eps, $"expected {expected}, got {actual}");

    // A ragdoll's pose: mid bent 70° about X, the root dropped half a metre.
    private static void Slump(SkeletonPose pose)
    {
        pose.ResetToRest();
        pose.Local[0].Position = new Vector3(0, -0.5f, 0);
        pose.Local[1].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 70f * MathF.PI / 180f);
        PoseSampler.ToModelSpace(pose.Skeleton, pose);
    }

    // Acceptance: a suspended animator leaves the pose alone — the test writes Local itself, as a ragdoll
    // would, and it stays — steps nothing while the body moves and triggers are set, keeps its pose
    // registered, and resumes where it was.
    [Fact]
    public void ASuspendedAnimatorLeavesThePoseAlone_AndResumesWhereItWas()
    {
        using var app = NewGame();
        var world = app.World;
        var hero = Spawn(world);
        Tick(world, 5);
        Assert.True(Animators.SetTrigger(world, hero, "go"));
        Tick(world, 25);                                                   // into walk, the fade done
        Assert.Equal("walk", Animators.StateOf(world, hero));
        Assert.False(Layer(world, hero).Fading);
        var before = Layer(world, hero);
        var pose = Pose(world, hero);

        Assert.False(Animators.IsSuspended(world, hero));
        Assert.True(Animators.Suspend(world, hero));
        Assert.True(Animators.IsSuspended(world, hero));
        Assert.False(Animators.Suspend(world, default));
        var bent = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1f);
        for (int i = 0; i < 30; i++)
        {
            pose.Local[1].Rotation = bent;                                 // the suspender's own write
            pose.Local[0].Position = new Vector3(0, -i * 0.01f, 0);
            PoseSampler.ToModelSpace(pose.Skeleton, pose);
            world.Get<Transform>(hero).LocalPosition += new Vector3(0.1f, 0, 0);   // the body is thrown about
            if (i == 10) Animators.SetTrigger(world, hero, "go");
            Tick(world);
            Near(bent, pose.Local[1].Rotation);
            Near(new Vector3(0, -i * 0.01f, 0), pose.Local[0].Position);
            Assert.True(Animators.TryGetPose(world, hero, out var still, out bool sampled));
            Assert.Same(pose, still);
            Assert.False(sampled);
            Assert.True(world.Resources.Get<SkeletonPoses>().TryGet(hero, out var registered));
            Assert.Same(pose, registered);
        }
        var frozen = Layer(world, hero);
        Assert.Equal("walk", frozen.State);
        Assert.Equal(before.Time, frozen.Time);
        Assert.Equal(before.Phase, frozen.Phase);
        Assert.Equal(before.Time, Layer(world, hero).Time);
        Assert.Contains("suspended", Animators.Describe(world, hero));

        Assert.True(Animators.Resume(world, hero));
        Assert.False(Animators.IsSuspended(world, hero));
        Tick(world);
        Assert.Equal("walk", Animators.StateOf(world, hero));
        Assert.Equal(before.Time + Dt, Layer(world, hero).Time, 4);
        Assert.True(Animators.GetParam(world, hero, "speed")!.Value < 0.01f);   // not measured across the suspension
        using var walk = Expected(Rig(app), "walk", Layer(world, hero).Phase * SkinnedModelBuilder.WalkDuration);
        Near(walk.Local[1].Rotation, pose.Local[1].Rotation);              // its own pose again
    }

    // Acceptance: Suspended is saved; a load keeps the animator frozen where it stood, drawn in its
    // state's pose (sampled once) rather than at rest.
    [Fact]
    public void SuspendedSurvivesSaveAndLoad()
    {
        using var app = NewGame();
        var world = app.World;
        var hero = Spawn(world);
        Tick(world, 5);
        Assert.True(Animators.Play(world, hero, "walk"));
        Tick(world, 24);
        Assert.True(Animators.Suspend(world, hero));
        Tick(world);
        var before = Layer(world, hero);
        Assert.True(app.Engine.Saves.Save("down"));
        string saved = File.ReadAllText(Path.Combine(app.Engine.Saves.Root, "down", "world_anim.json"));
        Assert.Contains("\"Suspended\": true", saved);

        Assert.True(Animators.Resume(world, hero));
        Tick(world, 20);
        Assert.True(app.Engine.Saves.Load("down"));
        hero = world.FindByName("hero");
        Assert.True(Animators.IsSuspended(world, hero));
        Tick(world, 10);
        var after = Layer(world, hero);
        Assert.Equal("walk", after.State);
        Assert.Equal(before.Time, after.Time, 4);
        Assert.Equal(before.Phase, after.Phase, 4);
        using var walk = Expected(Rig(app), "walk", after.Phase * SkinnedModelBuilder.WalkDuration);
        Near(walk.Local[1].Rotation, Pose(world, hero).Local[1].Rotation);
    }

    // Acceptance: a fade from a snapshot starts at the snapshot (the first tick is within a fifteenth of
    // it, along the ease) and ends exactly on the clip after `fade` seconds; the snapshot is copied, so
    // the caller's pose may change at once; and it resumes a suspended animator.
    [Fact]
    public void AFadeFromASnapshotStartsAtTheSnapshot_AndEndsOnTheClipAfterFadeSeconds()
    {
        using var app = NewGame();
        var world = app.World;
        var hero = Spawn(world);
        Tick(world, 5);
        var rig = Rig(app);
        using var snapshot = new SkeletonPose(rig.Skeleton);
        Slump(snapshot);
        var slumped = snapshot.Local[1].Rotation;
        var dropped = snapshot.Local[0].Position;
        Assert.True(Animators.Suspend(world, hero));
        Tick(world, 3);

        Assert.False(Animators.PlayFrom(world, hero, snapshot, "fly", 0.25f));
        Assert.False(Animators.PlayFrom(world, hero, snapshot, "getup", 0.25f, "upper"));
        using (var wrong = new SkeletonPose(new Skeleton(new[] { "a" }, new[] { -1 }, new[] { Sage.Simulation.Pose.Identity }, new[] { Matrix4x4.Identity })))
            Assert.Throws<ArgumentException>(() => Animators.PlayFrom(world, hero, wrong, "getup", 0.25f));
        Assert.True(Animators.PlayFrom(world, hero, snapshot, "getup", 0.25f));
        snapshot.ResetToRest();                                            // copied: the caller's may change
        Assert.False(Animators.IsSuspended(world, hero));
        Assert.Equal("getup", Animators.StateOf(world, hero));
        Assert.True(Layer(world, hero).Fading);
        Assert.Null(Layer(world, hero).From);
        Assert.Contains("fading from a pose snapshot", Animators.Describe(world, hero));

        Tick(world);
        var pose = Pose(world, hero);
        float f = Layer(world, hero).Fade / 0.25f;
        Assert.Equal(1f / 15f, f, 3);
        using (var clip = Expected(rig, "walk", Layer(world, hero).Phase * SkinnedModelBuilder.WalkDuration, loop: false))
        {
            Near(Quaternion.Slerp(slumped, clip.Local[1].Rotation, f), pose.Local[1].Rotation);
            Near(Vector3.Lerp(dropped, clip.Local[0].Position, f), pose.Local[0].Position);
        }
        Assert.True(MathF.Abs(Quaternion.Dot(slumped, pose.Local[1].Rotation)) > 0.99f, "the first tick should be at the snapshot");

        Tick(world, 13);
        Assert.True(Layer(world, hero).Fading);                            // 14 ticks: not yet
        Tick(world);
        Assert.False(Layer(world, hero).Fading);                           // the 15th: a quarter second
        Assert.Equal(15 * Dt, Layer(world, hero).Time, 4);
        using (var clip = Expected(rig, "walk", Layer(world, hero).Phase * SkinnedModelBuilder.WalkDuration, loop: false))
        {
            Near(clip.Local[1].Rotation, pose.Local[1].Rotation);
            Near(clip.Local[0].Position, pose.Local[0].Position);
        }

        // The one-shot plays out and its own transition takes it back to idle.
        Tick(world, 60);
        Assert.Equal("idle", Animators.StateOf(world, hero));
    }

    // The snapshot is not saved: a load mid-fade shows the state directly. A fade of 0 enters at once.
    [Fact]
    public void ALoadMidFadeFromASnapshotShowsTheStateDirectly()
    {
        using var app = NewGame();
        var world = app.World;
        var hero = Spawn(world);
        Tick(world, 5);
        var rig = Rig(app);
        using var snapshot = new SkeletonPose(rig.Skeleton);
        Slump(snapshot);
        Assert.True(Animators.PlayFrom(world, hero, snapshot, "walk", 0.5f));
        Tick(world, 5);
        Assert.True(Layer(world, hero).Fading);
        Assert.True(app.Engine.Saves.Save("rising"));

        Assert.True(app.Engine.Saves.Load("rising"));
        hero = world.FindByName("hero");
        Tick(world);
        Assert.Equal("walk", Animators.StateOf(world, hero));
        Assert.False(Layer(world, hero).Fading);
        Assert.Equal(6 * Dt, Layer(world, hero).Time, 4);
        using (var walk = Expected(rig, "walk", Layer(world, hero).Phase * SkinnedModelBuilder.WalkDuration))
            Near(walk.Local[1].Rotation, Pose(world, hero).Local[1].Rotation);

        Assert.True(Animators.PlayFrom(world, hero, snapshot, "idle", 0f));
        Assert.False(Layer(world, hero).Fading);
        Assert.Equal("idle", Animators.StateOf(world, hero));
    }
}

// Suspending, writing the pose by hand and fading back out of it, over and over, allocates nothing per
// tick once every animator has met it (the snapshot poses come from a pool).
[Collection(MeasurementsCollection.Name)]
public class AnimatorSuspendAllocationTests
{
    public AnimatorSuspendAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void SuspendingAndFadingFromSnapshotsAllocateNothingPerTick()
    {
        var files = new MountFixture();
        files.Write("game", "data/hero.json", AnimatorSuspendTests.Graph);
        SkinnedModelBuilder.Write(Path.Combine(files.Dir("game"), "models", "rig.glb"), withRun: true);
        files.Mount("game", "game");
        using var app = HeadlessApp.Bare().Mount(files).Boot("crowd");
        var world = app.World;
        var heroes = new Entity[40];
        for (int i = 0; i < heroes.Length; i++) heroes[i] = world.Spawn(new RecordId("game", "hero"), new Vector3(i, 0, 0));
        world.RunFixed(1f / 60f);
        Assert.True(app.Engine.Animations.TryGet(AssetPath.Intern("models/rig.glb"), out var rig));
        var lying = new SkeletonPose(rig.Skeleton);
        lying.Local[1].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.2f);

        int tick = 0;
        void Step()
        {
            tick++;
            for (int i = 0; i < heroes.Length; i++)
            {
                int t = (tick + i * 3) % 50;
                if (t == 0) Animators.Suspend(world, heroes[i]);
                else if (t == 30) Animators.PlayFrom(world, heroes[i], lying, (tick / 50 + i) % 2 == 0 ? "getup" : "walk", 0.2f);
                if (Animators.IsSuspended(world, heroes[i]) && Animators.TryGetPose(world, heroes[i], out var pose))
                {
                    lying.Local.CopyTo(pose.Local);                       // the ragdoll's write
                    PoseSampler.ToModelSpace(pose.Skeleton, pose);
                }
            }
            world.RunFixed(1f / 60f);
            Profiler.EndFrame();
        }

        for (int i = 0; i < 150; i++) Step();   // warm: every hero suspended, faded and pooled at least once
        AllocationProbe.AssertNone(300, Step);
        Assert.Contains(heroes, h => Animators.IsSuspended(world, h));
        Assert.Contains(heroes, h => world.Get<Animator>(h).Layers![0].Fading);
    }
}
