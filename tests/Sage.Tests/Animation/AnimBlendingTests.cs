#nullable enable
using System;
using System.IO;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Additive layers, sync markers and per-transition fades (issue #358, docs/design/12): against the rig
// SkinnedModelBuilder writes with its `run` clip (root → mid → tip; "walk" swings mid +90° about +Z at
// half its 1 s, "run" −90° at half its 0.5 s), with foot-plant events from an anim_events record.
public class AnimBlendingTests
{
    public AnimBlendingTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;
    private const float Eps = 1e-3f;

    // The walk plants its left foot 10% in and its right 60% in; the run its right 5% in and its left
    // 60% in (0.3 s of 0.5): by normalised time the two are a whole step out of phase.
    private const string Content = """
    [
      { "type": "anim_events", "id": "feet", "model": "models/rig.glb",
        "clips": { "walk": [ { "time": 0.1, "name": "foot_l" }, { "time": 0.6, "name": "foot_r" } ],
                   "run":  [ { "time": 0.05, "name": "foot_r" }, { "time": 0.3, "name": "foot_l" } ] } },
      { "type": "anim_graph", "id": "runner", "initial": "move", "fade": 0.25, "ease": "SmoothStep",
        "params": {
          "speed": { "default": 1 },
          "hop":   { "kind": "Trigger" },
          "snap":  { "kind": "Trigger" },
          "back":  { "kind": "Trigger" },
          "flinch": { "kind": "Trigger" },
          "calm":  { "kind": "Trigger" } },
        "states": {
          "move":  { "blend": { "x": "speed", "sync": ["foot_l", "foot_r"],
                                "points": [ { "clip": "walk", "x": 1.5 }, { "clip": "run", "x": 4.5 } ] } },
          "plain": { "blend": { "x": "speed", "points": [ { "clip": "walk", "x": 1.5 }, { "clip": "run", "x": 4.5 } ] } },
          "hop":   { "clip": "walk", "loop": false },
          "idle":  { "clip": "idle" } },
        "transitions": [
          { "to": "hop", "on": "hop", "fade": 0.1, "ease": "Linear" },
          { "to": "idle", "on": "snap", "fade": 0 },
          { "to": "move", "on": "back" } ],
        "layers": [
          { "name": "hit", "blend": "Additive", "mask": ["mid"], "initial": "none",
            "states": { "none": {}, "flinch": { "clip": "run", "fade": 0 } },
            "transitions": [ { "to": "flinch", "on": "flinch" }, { "to": "none", "on": "calm" } ] } ] },
      { "type": "prefab", "id": "runner", "name": "runner", "parts": { "animator": { "graph": "runner", "model": "models/rig.glb" } } }
    ]
    """;

    private static HeadlessApp NewGame(string content = Content)
    {
        var files = new MountFixture();
        files.Write("game", "data/runner.json", content);
        SkinnedModelBuilder.Write(Path.Combine(files.Dir("game"), "models", "rig.glb"), withRun: true);
        files.Mount("game", "game");
        return HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).Mount(files).Boot("anim");
    }

    private static Entity Spawn(World world) => world.Spawn(new RecordId("game", "runner"), default);

    private static void Tick(World world, int times = 1)
    {
        for (int i = 0; i < times; i++) world.RunFixed(Dt);
    }

    private static AnimationSet Rig(HeadlessApp app) =>
        app.Engine.Animations.TryGet(AssetPath.Intern("models/rig.glb"), out var set) ? set : throw new Xunit.Sdk.XunitException("the rig is not loaded");

    private static SkeletonPose Pose(World world, Entity e) =>
        Animators.TryGetPose(world, e, out var pose) ? pose : throw new Xunit.Sdk.XunitException("no pose");

    private static ref AnimatorLayer Layer(World world, Entity e, int layer = 0) => ref world.Get<Animator>(e).Layers![layer];

    private static Quaternion Mid(AnimationSet set, string clip, float time)
    {
        using var pose = new SkeletonPose(set.Skeleton);
        PoseSampler.Sample(set.FindClip(clip)!, time, true, pose);
        return pose.Local[1].Rotation;
    }

    private static void Near(Quaternion expected, Quaternion actual)
    {
        float dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected), Quaternion.Normalize(actual)));
        Assert.True(dot > 1 - Eps, $"expected {expected}, got {actual}");
    }

    private static bool Close(Quaternion a, Quaternion b) => MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))) > 1 - Eps;

    // A clip's time when the synced blend's phase is `phase`, worked out by hand: two markers, so each
    // half of the phase runs one marker to the next (foot_l, then foot_r, then foot_l a pass later).
    private static float SyncedTime(float phase, float[] markers, float duration)
    {
        float m = phase * markers.Length;
        int k = Math.Min((int)m, markers.Length - 1);
        float u = m - k;
        float a = markers[k];
        float b = k + 1 < markers.Length ? markers[k + 1] : markers[0] + duration;
        float t = (a + u * (b - a)) % duration;
        return t;
    }

    private static readonly float[] WalkMarkers = { 0.1f, 0.6f };      // foot_l, foot_r
    private static readonly float[] RunMarkers = { 0.3f, 0.55f };      // foot_l, then foot_r a pass on (0.05 + 0.5)

    // Acceptance: an additive flinch layer composes over locomotion. The flinch (the run's swing of mid,
    // relative to its first frame) is added on top of the walk's swing — the two angles sum — where an
    // override layer would have replaced it; at half weight half of it is added; the root, outside the
    // layer's mask, stays the walk's.
    [Xunit.Theory]
    [Xunit.InlineData(1f)]
    [Xunit.InlineData(0.5f)]
    public void AnAdditiveFlinchLayerComposesOverLocomotion(float weight)
    {
        using var app = NewGame(weight == 1f ? Content : Content.Replace("\"blend\": \"Additive\"", $"\"blend\": \"Additive\", \"weight\": {weight}"));
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        var runner = Spawn(world);
        var rig = Rig(app);
        Tick(world, 10);
        Assert.True(Animators.TryGetClip(world, runner, out var clip, out float walkTime));
        Assert.Equal("walk", clip);
        Near(Mid(rig, "walk", walkTime), Pose(world, runner).Local[1].Rotation);    // no flinch: the walk alone

        Assert.True(Animators.SetTrigger(world, runner, "flinch"));
        for (int i = 0; i < 12; i++)
        {
            Tick(world);
            Assert.Equal("flinch", Animators.StateOf(world, runner, "hit"));
            Assert.True(Animators.TryGetClip(world, runner, out _, out walkTime));
            float flinchTime = Layer(world, runner, 1).Phase * SkinnedModelBuilder.RunDuration;
            var walk = Mid(rig, "walk", walkTime);
            var flinch = Mid(rig, "run", flinchTime);
            var added = Quaternion.Slerp(Quaternion.Identity, flinch, weight);
            var pose = Pose(world, runner);
            Near(Quaternion.Normalize(walk * added), pose.Local[1].Rotation);
            if (i > 3)
            {
                Assert.False(Close(walk, pose.Local[1].Rotation), "the flinch should show over the walk");
                Assert.False(Close(flinch, pose.Local[1].Rotation), "an additive layer adds; it does not replace");
            }
            using var walkPose = new SkeletonPose(rig.Skeleton);
            PoseSampler.Sample(rig.FindClip("walk")!, walkTime, true, walkPose);
            Assert.True(Vector3.Distance(walkPose.Local[0].Position, pose.Local[0].Position) < Eps, "the root is outside the mask");
        }
        Assert.Contains("hit (additive): flinch", Animators.Describe(world, runner), StringComparison.Ordinal);

        Assert.True(Animators.SetTrigger(world, runner, "calm"));
        Tick(world, 20);                                                          // the graph's fade back to nothing
        Assert.Equal("none", Animators.StateOf(world, runner, "hit"));
        Assert.True(Animators.TryGetClip(world, runner, out _, out walkTime));
        Near(Mid(rig, "walk", walkTime), Pose(world, runner).Local[1].Rotation);
    }

    // Acceptance: a walk-to-run blend keeps the feet in phase at the markers. Half walk, half run: every
    // tick each clip is sampled where the markers put it — when the walk plants its right foot (0.6 s)
    // the run is planting its right foot too (0.05 s), not half a step on as by normalised time — and
    // the same graph's unsynced blend is a whole step out.
    [Xunit.Fact]
    public void AWalkToRunBlendKeepsFeetInPhaseAtMarkers()
    {
        using var app = NewGame();
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        var runner = Spawn(world);
        var rig = Rig(app);
        Assert.True(Animators.SetParam(world, runner, "speed", 3f));
        Tick(world);
        Assert.Equal(0.5f, Animators.ClipWeight(world, runner, "walk"), 3);
        Assert.Equal(0.5f, Animators.ClipWeight(world, runner, "run"), 3);
        Assert.Contains("blend synced on foot_l/foot_r", Animators.Describe(world, runner), StringComparison.Ordinal);

        bool sawRightPlant = false, outOfStepUnsynced = false;
        for (int i = 0; i < 90; i++)
        {
            Tick(world);
            float phase = Layer(world, runner).Phase;
            float walk = SyncedTime(phase, WalkMarkers, SkinnedModelBuilder.WalkDuration);
            float run = SyncedTime(phase, RunMarkers, SkinnedModelBuilder.RunDuration);
            var expected = Quaternion.Slerp(Mid(rig, "walk", walk), Mid(rig, "run", run), 0.5f);
            Near(expected, Pose(world, runner).Local[1].Rotation);

            // The walk's clock (the heaviest clip, the first of equals) reads its synced time.
            Assert.True(Animators.TryGetClip(world, runner, out var clip, out float time));
            Assert.Equal("walk", clip);
            Assert.Equal(walk, time, 3);

            // Within a tick of the walk's right plant, the run is at its own right plant.
            // (The walk's time moves 0.022 s a tick here, so one tick a pass lands within 0.012 s of it.)
            if (MathF.Abs(walk - 0.6f) <= 0.012f)
            {
                sawRightPlant = true;
                Assert.True(MathF.Abs(run - 0.05f) < 0.01f, $"walk at {walk:F3} s, run at {run:F3} s: the feet are out of phase");
            }
            var unsynced = Quaternion.Slerp(Mid(rig, "walk", phase * SkinnedModelBuilder.WalkDuration),
                                            Mid(rig, "run", phase * SkinnedModelBuilder.RunDuration), 0.5f);
            outOfStepUnsynced |= !Close(unsynced, expected);
        }
        Assert.True(sawRightPlant, "the walk never planted its right foot");
        Assert.True(outOfStepUnsynced, "syncing changed nothing: the test's clips should be out of phase by normalised time");

        // The same blend without `sync` plays by normalised time, as before #358.
        Assert.True(Animators.Play(world, runner, "plain"));
        Tick(world, 20);
        float p = Layer(world, runner).Phase;
        Near(Quaternion.Slerp(Mid(rig, "walk", p * SkinnedModelBuilder.WalkDuration), Mid(rig, "run", p * SkinnedModelBuilder.RunDuration), 0.5f),
             Pose(world, runner).Local[1].Rotation);
    }

    // A transition's own fade and ease win over the state's: the hop's transition fades a tenth of a
    // second along Linear (6 ticks at 60 Hz) where the graph says a quarter along SmoothStep; one with
    // "fade": 0 cuts; one that says nothing takes the state's.
    [Xunit.Fact]
    public void ATransitionsOwnFadeWinsOverTheStates()
    {
        using var app = NewGame();
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        var runner = Spawn(world);
        Tick(world, 3);

        Assert.True(Animators.SetTrigger(world, runner, "hop"));
        Tick(world);
        Assert.Equal("hop", Animators.StateOf(world, runner));
        Assert.True(Layer(world, runner).Fading);
        Assert.Equal(0.1f, Layer(world, runner).FadeDuration);
        Assert.Equal(Ease.Linear, Layer(world, runner).FadeEase);
        Tick(world, 5);
        Assert.True(Layer(world, runner).Fading);                                 // 5 ticks: not yet
        Tick(world);
        Assert.False(Layer(world, runner).Fading);                                // the 6th: done

        Assert.True(Animators.SetTrigger(world, runner, "snap"));
        Tick(world);
        Assert.Equal("idle", Animators.StateOf(world, runner));
        Assert.False(Layer(world, runner).Fading);                                // fade 0: a cut

        Assert.True(Animators.SetTrigger(world, runner, "back"));
        Tick(world);
        Assert.Equal("move", Animators.StateOf(world, runner));
        Assert.Equal(0.25f, Layer(world, runner).FadeDuration);                   // the state's (the graph's)
        Assert.Equal(Ease.SmoothStep, Layer(world, runner).FadeEase);
    }

    // A negative transition fade and an empty sync marker are load errors; sync on a one-shot is a warning.
    [Xunit.Fact]
    public void TransitionFadesAndSyncMarkersAreCheckedAtLoad()
    {
        var files = new MountFixture();
        files.Write("game", "data/bad.json", """
        [ { "type": "anim_graph", "id": "bad", "initial": "a",
            "params": { "x": {} },
            "states": {
              "a": { "clip": "walk", "transitions": [ { "to": "b", "after": 1, "fade": -1 } ] },
              "b": { "blend": { "x": "x", "sync": [""], "points": [ { "clip": "walk", "x": 0 } ] } },
              "c": { "loop": false, "blend": { "x": "x", "sync": ["foot_l"], "points": [ { "clip": "walk", "x": 0 } ] } } } } ]
        """);
        files.Mount("game", "game");
        using var app = HeadlessApp.Bare().Mount(files).Boot("bad");
        Assert.Equal(2, app.Records.ErrorCount);
    }

    // A synced blend whose clips lack the first marker plays by normalised time (and says so once).
    [Xunit.Fact]
    public void ABlendWhoseClipsLackTheMarkersPlaysByNormalisedTime()
    {
        using var app = NewGame(Content.Replace("\"sync\": [\"foot_l\", \"foot_r\"]", "\"sync\": [\"heel\"]"));
        var world = app.World;
        var runner = Spawn(world);
        var rig = Rig(app);
        Assert.True(Animators.SetParam(world, runner, "speed", 3f));
        Tick(world, 25);
        float p = Layer(world, runner).Phase;
        Near(Quaternion.Slerp(Mid(rig, "walk", p * SkinnedModelBuilder.WalkDuration), Mid(rig, "run", p * SkinnedModelBuilder.RunDuration), 0.5f),
             Pose(world, runner).Local[1].Rotation);
    }
}
