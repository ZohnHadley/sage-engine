#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The animation graph as data (issue #118, docs/design/12 "As built (the animation graph)"): the
// `anim_graph` record, `sage:animator` and its part, AnimatorSystem's stepping and sampling, layers,
// params from the body and from I/O, saves, hot reload and animation LOD. Against the rig
// SkinnedModelBuilder writes, with its `run` clip: root → mid → tip, "idle" bobs the root and steps the
// tip's scale, "walk" and "run" swing mid about +Z (+90° at half a walk, −90° at half a run).
public class AnimatorTests
{
    public AnimatorTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;
    private const float Eps = 1e-3f;

    // Idle, a walk↔run blend over speed (from the body's velocity), a one-shot hop on a trigger with a
    // quarter-second linear cross-fade in, and an upper-body layer that waves (plays walk on mid's branch).
    private const string Hero = """
    [
      { "type": "anim_graph", "id": "hero", "initial": "idle", "fade": 0.25, "ease": "Linear",
        "params": {
          "speed": { "from": "Speed" },
          "armed": { "kind": "Bool" },
          "jump":  { "kind": "Trigger" },
          "wave":  { "kind": "Trigger" },
          "lower": { "kind": "Trigger" } },
        "states": {
          "idle": { "clip": "idle", "tags": ["grounded"],
                    "transitions": [ { "to": "move", "when": { "anim_param": "speed", "min": 0.5 } } ] },
          "move": { "blend": { "x": "speed", "points": [ { "clip": "run", "x": 4 }, { "clip": "walk", "x": 1.5 } ] },
                    "tags": ["grounded", "moving"],
                    "transitions": [ { "to": "idle", "when": { "anim_param": "speed", "max": 0.25 } } ] },
          "hop":  { "clip": "walk", "loop": false, "tags": ["airborne"],
                    "transitions": [ { "to": "idle", "after": 1 } ] } },
        "transitions": [ { "to": "hop", "on": "jump" } ],
        "layers": [
          { "name": "upper", "mask": ["mid"], "initial": "none",
            "states": { "none": {}, "wave": { "clip": "walk", "fade": 0 } },
            "transitions": [ { "to": "wave", "on": "wave" }, { "to": "none", "on": "lower" } ] } ] },
      { "type": "prefab", "id": "hero", "name": "hero", "parts": { "animator": { "graph": "hero", "model": "models/rig.glb" } } }
    ]
    """;

    private static (HeadlessApp App, MountFixture Files) NewGame(string content = Hero)
    {
        var files = new MountFixture();
        files.Write("game", "data/hero.json", content);
        SkinnedModelBuilder.Write(Path.Combine(files.Dir("game"), "models", "rig.glb"), withRun: true);
        files.Mount("game", "game");
        var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).Mount(files).Boot("anim");
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        return (app, files);
    }

    private static Entity Spawn(World world, string name = "hero", Vector3 at = default)
    {
        var e = world.Spawn(new RecordId("game", "hero"), at);
        e.Name = name;
        world.MakePersistent(e);
        return e;
    }

    private static void Tick(World world, int times = 1)
    {
        for (int i = 0; i < times; i++) world.RunFixed(Dt);
    }

    // Moves the entity along +X at `speed` m/s for `ticks` ticks.
    private static void Walk(World world, Entity e, float speed, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            world.Get<Transform>(e).LocalPosition += new Vector3(speed * Dt, 0, 0);
            world.RunFixed(Dt);
        }
    }

    private static AnimationSet Rig(HeadlessApp app) =>
        app.Engine.Animations.TryGet(AssetPath.Intern("models/rig.glb"), out var set) ? set : throw new Xunit.Sdk.XunitException("the rig is not loaded");

    private static SkeletonPose Pose(World world, Entity e) =>
        Animators.TryGetPose(world, e, out var pose) ? pose : throw new Xunit.Sdk.XunitException("no pose");

    private static ref AnimatorLayer Layer(World world, Entity e, int layer = 0) => ref world.Get<Animator>(e).Layers![layer];

    private static void Near(Quaternion expected, Quaternion actual)
    {
        float dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected), Quaternion.Normalize(actual)));
        Assert.True(dot > 1 - Eps, $"expected {expected}, got {actual}");
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < Eps, $"expected {expected}, got {actual}");

    // What `clip` looks like at `time`, sampled on its own.
    private static SkeletonPose Expected(AnimationSet set, string clip, float time, bool loop = true)
    {
        var pose = new SkeletonPose(set.Skeleton);
        PoseSampler.Sample(set.FindClip(clip)!, time, loop, pose);
        return pose;
    }

    // Acceptance: the walk↔run blend's weights follow the body's speed (the Speed param reads how far
    // the transform moved), and the sampled pose at a blend point is that clip.
    [Fact]
    public void WalkAndRunBlendWeightsFollowSpeed()
    {
        var (app, _) = NewGame();
        using (app)
        {
            var world = app.World;
            var hero = Spawn(world);
            Tick(world, 2);
            Assert.Equal("idle", Animators.StateOf(world, hero));
            Assert.Equal(1f, Animators.ClipWeight(world, hero, "idle"));
            Assert.True(Animators.HasTag(world, hero, "grounded"));
            Assert.False(Animators.HasTag(world, hero, "moving"));

            Walk(world, hero, 2.75f, 3);
            Assert.Equal("move", Animators.StateOf(world, hero));
            Assert.True(Animators.HasTag(world, hero, "moving"));
            Assert.Equal(2.75f, Animators.GetParam(world, hero, "speed")!.Value, 2);
            Assert.Equal(0.5f, Animators.ClipWeight(world, hero, "walk"), 2);
            Assert.Equal(0.5f, Animators.ClipWeight(world, hero, "run"), 2);

            Walk(world, hero, 2.125f, 2);
            Assert.Equal(0.75f, Animators.ClipWeight(world, hero, "walk"), 2);
            Assert.Equal(0.25f, Animators.ClipWeight(world, hero, "run"), 2);

            Walk(world, hero, 1f, 2);                                         // below the first point: all walk
            Assert.Equal(1f, Animators.ClipWeight(world, hero, "walk"), 3);
            Assert.Equal(0f, Animators.ClipWeight(world, hero, "run"), 3);

            Walk(world, hero, 6f, 30);                                        // past the last: all run, the fade long done
            Assert.Equal(1f, Animators.ClipWeight(world, hero, "run"), 3);
            Assert.False(Layer(world, hero).Fading);
            var rig = Rig(app);
            using var run = Expected(rig, "run", Layer(world, hero).Phase * SkinnedModelBuilder.RunDuration);
            Near(run.Local[1].Rotation, Pose(world, hero).Local[1].Rotation);

            Walk(world, hero, 0f, 2);
            Assert.Equal("idle", Animators.StateOf(world, hero));
        }
    }

    // Acceptance: a cross-fade of a quarter second completes after 15 ticks at 60 Hz, blending the two
    // states along its ease on the way.
    [Fact]
    public void ACrossfadeCompletesAfterNTicks()
    {
        var (app, _) = NewGame();
        using (app)
        {
            var world = app.World;
            var hero = Spawn(world);
            Tick(world, 3);
            Assert.True(Animators.SetTrigger(world, hero, "jump"));
            Tick(world);
            Assert.Equal("hop", Animators.StateOf(world, hero));
            Assert.True(Layer(world, hero).Fading);
            Assert.Equal("idle", Layer(world, hero).From);
            Assert.Equal(0.25f, Layer(world, hero).FadeDuration);
            Assert.Null(Animators.GetParam(world, hero, "nope"));
            Assert.Equal(0f, Animators.GetParam(world, hero, "jump")!.Value);        // used up

            Tick(world, 7);
            // Half-way (7/15): mid is between idle's rest rotation and the hop's walk, not at either.
            var rig = Rig(app);
            using (var walk = Expected(rig, "walk", Layer(world, hero).Phase * SkinnedModelBuilder.WalkDuration, loop: false))
            {
                float f = Layer(world, hero).Fade / 0.25f;
                var between = Quaternion.Slerp(Quaternion.Identity, walk.Local[1].Rotation, f);
                Near(between, Pose(world, hero).Local[1].Rotation);
            }

            Tick(world, 7);
            Assert.True(Layer(world, hero).Fading);                           // 14 ticks: not yet
            Tick(world);
            Assert.False(Layer(world, hero).Fading);                          // the 15th: done
            Assert.Null(Layer(world, hero).From);
            using var after = Expected(rig, "walk", Layer(world, hero).Phase * SkinnedModelBuilder.WalkDuration, loop: false);
            Near(after.Local[1].Rotation, Pose(world, hero).Local[1].Rotation);
            Assert.True(Animators.HasTag(world, hero, "airborne"));

            // after: 1 — a second in the hop, then back to idle.
            Tick(world, 60 - 15 - 1);
            Assert.Equal("hop", Animators.StateOf(world, hero));
            Tick(world);
            Assert.Equal("idle", Animators.StateOf(world, hero));
        }
    }

    // Acceptance: state, time and the cross-fade are saved by name, and play on after a load as they
    // would have; the pose is not saved, and is sampled again.
    [Fact]
    public void StateAndTimeSurviveSaveAndLoad()
    {
        var (app, _) = NewGame();
        using (app)
        {
            var world = app.World;
            var hero = Spawn(world);
            Walk(world, hero, 3f, 30);
            Assert.True(Animators.SetTrigger(world, hero, "jump"));
            Assert.True(Animators.SetParam(world, hero, "armed", true));
            Tick(world, 6);
            Assert.Equal("hop", Animators.StateOf(world, hero));
            var before = Layer(world, hero);
            Assert.True(before.Fading);
            Assert.True(app.Engine.Saves.Save("mid"));

            string file = Path.Combine(app.Engine.Saves.Root, "mid", "world_anim.json");
            string saved = File.ReadAllText(file);
            Assert.Contains("sage:animator", saved);
            Assert.Contains("\"hop\"", saved);
            Assert.Contains("\"move\"", saved);                              // what it is fading from
            Assert.DoesNotContain("\"Slot\"", saved);
            Assert.DoesNotContain("\"Index\"", saved);

            Tick(world, 70);
            Assert.Equal("idle", Animators.StateOf(world, hero));

            Assert.True(app.Engine.Saves.Load("mid"));
            hero = world.FindByName("hero");
            var after = Layer(world, hero);
            Assert.Equal("hop", after.State);
            Assert.Equal(before.Time, after.Time, 4);
            Assert.Equal(before.Phase, after.Phase, 4);
            Assert.Equal("move", after.From);
            Assert.Equal(before.Fade, after.Fade, 4);
            Assert.Equal(1f, Animators.GetParam(world, hero, "armed")!.Value);   // params are saved by name too

            Tick(world);
            Assert.True(Animators.TryGetPose(world, hero, out _));
            Tick(world, 60 - 5 - 1 - 1);                                      // a second in the hop, counted from its start
            Assert.Equal("hop", Animators.StateOf(world, hero));
            Tick(world);
            Assert.Equal("idle", Animators.StateOf(world, hero));
        }
    }

    // Acceptance: a hot reload that drops the state an animator is in sends that layer to its initial
    // state with a warning; one whose state is still there keeps it, with its time.
    [Fact]
    public void AHotReloadThatDropsAStateFallsBackToTheInitialStateWithAWarning()
    {
        var (app, files) = NewGame();
        using (app)
        {
            var world = app.World;
            var hopper = Spawn(world, "hopper");
            var waver = Spawn(world, "waver");
            Tick(world, 2);
            Animators.SetTrigger(world, hopper, "jump");
            Animators.SetTrigger(world, waver, "wave");
            Tick(world, 10);
            Assert.Equal("hop", Animators.StateOf(world, hopper));
            Assert.Equal("wave", Animators.StateOf(world, waver, "upper"));
            float waving = Layer(world, waver, 1).Time;

            files.Write("game", "data/hero.json", Hero.Replace("\"hop\":  {", "\"stand\": {").Replace("\"to\": \"hop\"", "\"to\": \"stand\""));
            using var log = new CaptureSink();
            app.Records.Reload();
            Assert.Equal(0, app.Records.ErrorCount);
            Tick(world);

            Assert.Equal("idle", Animators.StateOf(world, hopper));
            Assert.False(Layer(world, hopper).Fading);
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("no state 'hop'"));
            Assert.Equal("wave", Animators.StateOf(world, waver, "upper"));
            Assert.Equal(waving + Dt, Layer(world, waver, 1).Time, 3);
            Assert.True(Animators.TryGetPose(world, hopper, out _));
        }
    }

    // Layers: the upper layer plays walk on mid's branch (mid and tip) while the base idles: the root
    // bobs with idle, mid swings with walk, and tip takes walk's rest scale over idle's stepped one.
    [Fact]
    public void ALayerBlendsOnlyTheBranchItsMaskNames()
    {
        var (app, _) = NewGame();
        using (app)
        {
            var world = app.World;
            var hero = Spawn(world);
            Tick(world, 70);                                                  // past idle's step: tip at scale 2
            Assert.Equal("none", Animators.StateOf(world, hero, "upper"));
            var rig = Rig(app);
            using (var idle = Expected(rig, "idle", Layer(world, hero).Phase * SkinnedModelBuilder.IdleDuration))
                Near(idle.Local[2].Scale, Pose(world, hero).Local[2].Scale);
            Near(new Vector3(2, 2, 2), Pose(world, hero).Local[2].Scale);

            world.IO().FireInput(hero, Animators.TriggerInput, "wave");       // through entity I/O
            Tick(world, 2);
            Assert.Equal("wave", Animators.StateOf(world, hero, "upper"));
            Assert.False(Layer(world, hero, 1).Fading);                       // its fade is 0
            Tick(world, 5);

            var pose = Pose(world, hero);
            using var idleNow = Expected(rig, "idle", Layer(world, hero).Phase * SkinnedModelBuilder.IdleDuration);
            using var walkNow = Expected(rig, "walk", Layer(world, hero, 1).Phase * SkinnedModelBuilder.WalkDuration);
            Near(idleNow.Local[0].Position, pose.Local[0].Position);          // root: the base's
            Near(walkNow.Local[1].Rotation, pose.Local[1].Rotation);          // mid: the layer's
            Near(Vector3.One, pose.Local[2].Scale);                           // tip: under mid, so the layer's (rest)
            Assert.True(MathF.Abs(pose.Local[1].Rotation.Z) > 0.01f, "mid should have swung");

            // The model-space matrices are ready for skinning (#117) and IK (#120).
            var expected = PoseSampler.Compose(pose.Local[1]) * PoseSampler.Compose(pose.Local[0]);
            Assert.True(Vector3.Distance(expected.Translation, pose.ModelSpace[1].Translation) < Eps);

            world.IO().FireInput(hero, Animators.TriggerInput, "lower");
            Tick(world, 2);
            Assert.Equal("none", Animators.StateOf(world, hero, "upper"));
        }
    }

    // #117's seam: the animator hands its pose to skinned renderers through SkinPoses, so the palette a
    // skinned_mesh is drawn with moves as the animation plays; the animator part takes the skinned
    // mesh's model when it names none; and a destroyed animator's pose is taken out again.
    [Fact]
    public void TheAnimatorPosesItsSkinnedMesh_AndThePaletteChangesOverTicks()
    {
        var (app, _) = NewGame(Hero.TrimEnd()[..^1] + """
            , { "type": "prefab", "id": "skinned", "name": "skinned",
                "parts": { "skinned_mesh": { "mesh": "models/rig.glb" }, "animator": { "graph": "hero" } } } ]
            """);
        using (app)
        {
            var world = app.World;
            var e = world.Spawn(new RecordId("game", "skinned"));
            Assert.Equal(AssetPath.Intern("models/rig.glb"), world.Get<Animator>(e).Model);
            var skin = world.Resources.Get<SkinPoses>();
            Walk(world, e, 3f, 5);
            Assert.True(world.Resources.Get<SkeletonPoses>().TryGet(e, out var registered, out var model));   // #120's seam
            Assert.Equal(AssetPath.Intern("models/rig.glb"), model);

            Assert.True(skin.TryGet(e.Id, out var shown));
            Assert.True(Animators.TryGetPose(world, e, out var pose));
            Assert.Same(pose, shown);
            Assert.Same(pose, registered);
            var inverseBind = pose.Skeleton.InverseBind;
            var before = new Matrix4x4[pose.JointCount];
            SkinMath.Palette(shown.ModelSpace, inverseBind, before);

            Walk(world, e, 3f, 10);
            var after = new Matrix4x4[pose.JointCount];
            Assert.True(skin.TryGet(e.Id, out shown));
            SkinMath.Palette(shown.ModelSpace, inverseBind, after);
            Assert.NotEqual(before[1], after[1]);                             // mid swings with the walk↔run blend
            var inSkinOrder = new Matrix4x4[pose.JointCount];
            Assert.True(SkinPoses.ToSkinOrder(shown, inSkinOrder));

            world.Destroy(e);
            Tick(world, 2);
            Assert.False(skin.TryGet(e.Id, out _));
            Assert.False(world.Resources.Get<SkeletonPoses>().TryGet(e, out _));
        }
    }

    // #120's aim IK: the graph's aim_pitch and aim_yaw params (degrees) are copied into the entity's
    // AimIk (radians) every tick, and the Late phase's aim IK turns the pose the animator rewrote.
    [Fact]
    public void AimParamsDriveTheEntitysAimIk()
    {
        var (app, _) = NewGame("""
        [
          { "type": "anim_graph", "id": "aimer", "initial": "idle",
            "params": { "aim_pitch": {}, "aim_yaw": {} },
            "states": { "idle": { "clip": "idle" } } },
          { "type": "prefab", "id": "hero",
            "parts": { "animator": { "graph": "aimer", "model": "models/rig.glb" },
                       "aim_ik": { "joints": [ { "joint": "mid", "weight": 1, "pitchLimit": 90, "yawLimit": 90 } ], "weight": 1 } } }
        ]
        """);
        using (app)
        {
            var world = app.World;
            var e = Spawn(world);
            Tick(world);
            Assert.Equal(0f, world.Get<AimIk>(e).Pitch);
            var unaimed = Pose(world, e).Local[1].Rotation;

            Animators.SetParam(world, e, "aim_pitch", 30f);
            Animators.SetParam(world, e, "aim_yaw", -45f);
            Tick(world);
            Assert.Equal(30f * MathF.PI / 180f, world.Get<AimIk>(e).Pitch, 4);
            Assert.Equal(-45f * MathF.PI / 180f, world.Get<AimIk>(e).Yaw, 4);
            var aimed = Pose(world, e).Local[1].Rotation;
            Assert.True(MathF.Abs(Quaternion.Dot(unaimed, aimed)) < 0.99f, "aim IK should have turned mid");

            // The next tick starts from the graph again: the turn is the same, not twice as much.
            Tick(world);
            Near(aimed, Pose(world, e).Local[1].Rotation);
        }
    }

    // SetAnimParam through entity I/O: "name value" for floats and bools, a trigger by name; the
    // anim_param condition reads them; a param with no such name is a warning.
    [Fact]
    public void SetAnimParamAndAnimTriggerArriveThroughEntityIO()
    {
        var (app, _) = NewGame();
        using (app)
        {
            var world = app.World;
            var hero = Spawn(world);
            Tick(world);
            world.IO().FireInput(hero, Animators.SetParamInput, "armed true");
            Tick(world);
            Assert.Equal(1f, Animators.GetParam(world, hero, "armed")!.Value);
            Assert.True(Conditions.Evaluate(world, hero, new AnimParamCondition { Name = "armed", Eq = 1 }, hero));
            Assert.False(Conditions.Evaluate(world, hero, new AnimParamCondition { Name = "missing" }, hero));

            world.IO().FireInput(hero, Animators.SetParamInput, "armed 0");
            world.IO().FireInput(hero, Animators.SetParamInput, "jump");
            Tick(world);
            Assert.Equal(0f, Animators.GetParam(world, hero, "armed")!.Value);
            Tick(world);
            Assert.Equal("hop", Animators.StateOf(world, hero));

            using var log = new CaptureSink();
            world.IO().FireInput(hero, Animators.SetParamInput, "armd 1");
            Tick(world);
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("no param 'armd'") && e.Message.Contains("armed"));
            Assert.True(Animators.Play(world, hero, "move"));
            Assert.False(Animators.Play(world, hero, "fly"));
            Assert.Equal("move", Animators.StateOf(world, hero));
        }
    }

    // A 2D blend space: exact at a point, inverse-distance weights between them, summing to one.
    [Fact]
    public void ATwoDimensionalBlendSpaceWeighsItsPointsByDistance()
    {
        var (app, _) = NewGame("""
        [
          { "type": "anim_graph", "id": "strafe", "initial": "move",
            "params": { "mx": {}, "my": {} },
            "states": { "move": { "blend": { "x": "mx", "y": "my", "points": [
                { "clip": "idle", "x": 0, "y": 0 }, { "clip": "walk", "x": 0, "y": 1 }, { "clip": "run", "x": 1, "y": 0 } ] } } } },
          { "type": "prefab", "id": "hero", "parts": { "animator": { "graph": "strafe", "model": "models/rig.glb" } } }
        ]
        """);
        using (app)
        {
            var world = app.World;
            var e = Spawn(world);
            Tick(world);
            Animators.SetParam(world, e, "my", 1f);
            Assert.Equal(1f, Animators.ClipWeight(world, e, "walk"), 4);
            Assert.Equal(0f, Animators.ClipWeight(world, e, "idle"), 4);

            Animators.SetParam(world, e, "mx", 0.5f);
            Animators.SetParam(world, e, "my", 0.5f);
            Assert.Equal(1f / 3f, Animators.ClipWeight(world, e, "idle"), 3);
            Assert.Equal(1f / 3f, Animators.ClipWeight(world, e, "run"), 3);

            Animators.SetParam(world, e, "mx", 0f);
            float idle = Animators.ClipWeight(world, e, "idle"), walk = Animators.ClipWeight(world, e, "walk"), run = Animators.ClipWeight(world, e, "run");
            Assert.Equal(1f, idle + walk + run, 4);
            Assert.Equal(idle, walk, 4);
            Assert.True(run < walk);
            Tick(world, 3);
            Assert.True(Animators.TryGetPose(world, e, out _));
        }
    }

    // Animation LOD: with the main camera at the origin and anim_lod_distance 30, an animator 45 m off
    // samples every 2nd tick and one 100 m off every 4th; all three step every tick all the same.
    [Fact]
    public void DistantAnimatorsSampleLessOftenButStepEveryTick()
    {
        var (app, _) = NewGame();
        using (app)
        {
            var world = app.World;
            var near = Spawn(world, "near", new Vector3(5, 0, 0));
            var mid = Spawn(world, "mid", new Vector3(45, 0, 0));
            var far = Spawn(world, "far", new Vector3(100, 0, 0));
            var views = world.Resources.Get<CameraViews>();
            Tick(world, 2);

            int[] sampled = new int[3];
            var all = new[] { near, mid, far };
            for (int t = 0; t < 16; t++)
            {
                views.Begin();
                views.SetScreen(new CameraView { Target = "", Position = Vector3.Zero, Rotation = Quaternion.Identity });
                views.End();
                Tick(world);
                for (int i = 0; i < 3; i++)
                    if (Animators.TryGetPose(world, all[i], out _, out bool fresh) && fresh) sampled[i]++;
            }
            Assert.Equal(new[] { 16, 8, 4 }, sampled);

            // A post-process (#120's IK) that bends the pose never compounds, sampled this tick or not:
            // the readers' pose is rewritten from the graph's output every tick.
            var farPose = Pose(world, far);
            var mid1 = farPose.Local[1];
            farPose.Local[1].Position += new Vector3(0, 5, 0);
            views.Begin();
            views.SetScreen(new CameraView { Target = "", Position = Vector3.Zero, Rotation = Quaternion.Identity });
            views.End();
            Tick(world);
            Assert.True(Animators.TryGetPose(world, far, out _, out bool resampled));
            Assert.False(resampled);                                          // an LOD-skipped tick
            Near(mid1.Position, farPose.Local[1].Position);
            Assert.Equal(Layer(world, near).Time, Layer(world, far).Time, 4);
            Assert.Equal(1, AnimatorSystem.Interval(29f, 30f));
            Assert.Equal(2, AnimatorSystem.Interval(59f, 30f));
            Assert.Equal(4, AnimatorSystem.Interval(61f, 30f));
            Assert.Equal(1, AnimatorSystem.Interval(1000f, 0f));             // 0: LOD off

            app.CVars.Execute("anim_debug far");                              // the console overlay: one line per layer
            Assert.Contains("far", Animators.Describe(world, far));
            Assert.Contains("LOD every 4", Animators.Describe(world, far));
        }
    }

    // Load checks: initial states, targets, blend axes and `on` names are checked where they are written.
    [Fact]
    public void AGraphIsCheckedAtLoad()
    {
        using var log = new CaptureSink();
        var (app, _) = NewGame("""
        [
          { "type": "anim_graph", "id": "broken", "initial": "sleep",
            "params": { "flag": { "kind": "Bool" }, "go": { "kind": "Trigger", "from": "Speed" } },
            "states": {
              "a": { "clip": "idle", "blend": { "x": "speed", "points": [ { "clip": "walk" } ] } },
              "b": { "blend": { "x": "flag", "points": [ { "clip": "walk" } ] },
                     "transitions": [ { "to": "c" }, { "to": "a", "on": "flag" } ] } },
            "layers": [ { "name": "base", "initial": "x", "states": { "x": {} } } ] },
          { "type": "prefab", "id": "hero", "parts": {} }
        ]
        """);
        using (app)
        {
            string errors = string.Join("\n", log.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message));
            Assert.Contains("'sleep' is not one of its states", errors);
            Assert.Contains("a trigger is set by inputs and code", errors);
            Assert.Contains("a clip or a blend, not both", errors);
            Assert.Contains("'flag' is a Bool", errors);
            Assert.Contains("'c' is not one of the layer's states", errors);
            Assert.Contains("'flag' is not one of its trigger params", errors);
            Assert.Contains("'base' is taken", errors);
        }
    }
}

// 100 animators, blending, cross-fading and layering, allocate nothing per tick.
[Collection(MeasurementsCollection.Name)]
public class AnimatorAllocationTests
{
    public AnimatorAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void AHundredAnimatorsAllocateNothingPerTick()
    {
        var files = new MountFixture();
        files.Write("game", "data/crowd.json", """
        [
          { "type": "anim_graph", "id": "crowd", "initial": "idle", "fade": 0.1,
            "params": { "speed": { "from": "Speed" }, "pitch": { "from": "AimPitch" }, "wave": { "kind": "Trigger" } },
            "states": {
              "idle": { "clip": "idle", "transitions": [ { "to": "move", "after": 0.3, "when": { "anim_param": "speed", "min": 0.1 } } ] },
              "move": { "blend": { "x": "speed", "points": [ { "clip": "walk", "x": 1 }, { "clip": "run", "x": 4 } ] },
                        "transitions": [ { "to": "idle", "after": 0.4 } ] } },
            "transitions": [ { "to": "idle", "on": "wave" } ],
            "layers": [ { "name": "upper", "mask": ["mid"], "weight": 0.8, "initial": "none",
              "states": { "none": { "transitions": [ { "to": "wave", "after": 0.25 } ] },
                          "wave": { "blend": { "x": "speed", "y": "pitch", "points": [
                                      { "clip": "walk", "x": 0, "y": 0 }, { "clip": "run", "x": 4, "y": 0 }, { "clip": "idle", "x": 2, "y": 1 } ] },
                                    "transitions": [ { "to": "none", "after": 0.35 } ] } } } ] },
          { "type": "prefab", "id": "walker", "parts": { "animator": { "graph": "crowd", "model": "models/rig.glb" } } }
        ]
        """);
        SkinnedModelBuilder.Write(Path.Combine(files.Dir("game"), "models", "rig.glb"), withRun: true);
        files.Mount("game", "game");
        using var app = HeadlessApp.Bare().Mount(files).Boot("crowd");
        var world = app.World;
        var walkers = new Entity[100];
        for (int i = 0; i < walkers.Length; i++) walkers[i] = world.Spawn(new RecordId("game", "walker"), new Vector3(i, 0, 0));

        int tick = 0;
        void Step()
        {
            tick++;
            for (int i = 0; i < walkers.Length; i++)
            {
                float speed = 2f + 2f * MathF.Sin(tick * 0.05f + i);
                world.Get<Transform>(walkers[i]).LocalPosition += new Vector3(speed / 60f, 0, 0);
                if ((tick + i) % 97 == 0) Animators.SetTrigger(world, walkers[i], "wave");
            }
            world.RunFixed(1f / 60f);
            Profiler.EndFrame();
        }

        for (int i = 0; i < 120; i++) Step();   // warm: every state, fade and layer met at least once
        AllocationProbe.AssertNone(300, Step);
        Assert.All(walkers, w => Assert.True(Animators.TryGetPose(world, w, out _)));
        Assert.Contains(walkers, w => Animators.StateOf(world, w) == "move");
    }
}
