#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The editor's animation preview (issue #362): a model's clips scrubbed by hand with their event markers
// and sockets, a graph run by the real animator with every param and state the preview's to set, the
// `anim_preview*` commands that press the same buttons, and the Sandbox's skeletal creature, which the
// real game runs and the preview opens. Against SkinnedModelBuilder's rig (root -> mid -> tip, a metre
// apart; `walk` turns mid 90° about +Z at half a second while its root slides 0.75 m along +X by then).
public class AnimationPreviewTests
{
    public AnimationPreviewTests() { _ = TestEnv.UserRoot; }

    private const float Eps = 1e-3f;

    private const string Hero = """
    [
      { "type": "anim_graph", "id": "hero", "initial": "idle", "fade": 0.1, "ease": "Linear",
        "params": {
          "speed": { "from": "Speed" },
          "armed": { "kind": "Bool" },
          "jump":  { "kind": "Trigger" },
          "wave":  { "kind": "Trigger" } },
        "states": {
          "idle": { "clip": "idle", "transitions": [ { "to": "move", "when": { "anim_param": "speed", "min": 0.5 } } ] },
          "move": { "blend": { "x": "speed", "points": [ { "clip": "walk", "x": 1.5 }, { "clip": "run", "x": 4 } ] },
                    "transitions": [ { "to": "idle", "when": { "anim_param": "speed", "max": 0.25 } } ] },
          "hop":  { "clip": "walk", "loop": false, "transitions": [ { "to": "idle", "after": 1 } ] } },
        "transitions": [ { "to": "hop", "on": "jump" } ],
        "layers": [
          { "name": "upper", "mask": ["mid"], "initial": "none",
            "states": { "none": {}, "wave": { "clip": "walk", "fade": 0 } },
            "transitions": [ { "to": "wave", "on": "wave" } ] } ] },
      { "type": "anim_graph", "id": "lonely", "initial": "a", "states": { "a": {} } },
      { "type": "anim_events", "id": "rig", "model": "models/rig.glb", "clips": { "walk": [ { "time": 0.25, "name": "step" } ] } },
      { "type": "skeleton_sockets", "id": "rig", "model": "models/rig.glb",
        "sockets": { "top": { "joint": "tip", "offset": [0, 0.5, 0] }, "nowhere": { "joint": "no_such_joint" } } },
      { "type": "prefab", "id": "hero", "name": "hero", "parts": { "animator": { "graph": "hero", "model": "models/rig.glb" } } }
    ]
    """;

    private static readonly AssetPath Rig = AssetPath.Intern("models/rig.glb");
    private static readonly RecordId HeroGraph = new("game", "hero");

    private static HeadlessApp NewGame()
    {
        var files = new MountFixture();
        files.Write("game", "data/hero.json", Hero);
        SkinnedModelBuilder.Write(Path.Combine(files.Dir("game"), "models", "rig.glb"), withRun: true);
        files.Mount("game", "game");
        return HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).Mount(files).Boot("anim");
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < Eps, $"expected {expected}, got {actual}");

    private static Vector3 Joint(AnimationPreview preview, string name) => preview.Joints.Single(j => j.Name == name).Position;

    // What there is to preview: every graph, with the model a prefab's animator plays it on.
    [Fact]
    public void TheSubjectsAreTheGraphsWithTheModelAPrefabPlaysThemOn()
    {
        using var app = NewGame();
        var subjects = AnimationPreview.Subjects(app.Engine.Records).Where(s => s.Graph.Namespace == "game").ToList();
        Assert.Equal(new[] { new AnimationSubject(HeroGraph, Rig), new AnimationSubject(new RecordId("game", "lonely"), default) }, subjects);
    }

    // Clips: any clip scrubbed to a time, sampled straight from the clip; its events as markers; the
    // sockets where the pose puts them (a socket on a joint the skeleton has not got is left out).
    [Fact]
    public void AClipScrubsToATimeWithItsEventMarkersAndSockets()
    {
        using var app = NewGame();
        using var preview = new AnimationPreview(app.Engine);
        Assert.True(preview.OpenModel(Rig));
        Assert.Equal(AnimationPreviewMode.Clip, preview.Mode);
        Assert.Equal(new[] { "idle", "walk", "run" }, preview.Clips);
        Assert.Equal("idle", preview.Clip);

        Assert.True(preview.ShowClip("walk", 0.5f));
        Assert.Equal(1f, preview.ClipDuration, 3);
        Near(new Vector3(0.75f, 0, 0), Joint(preview, "root"));
        Near(new Vector3(0.75f, 1, 0), Joint(preview, "mid"));
        Near(new Vector3(-0.25f, 1, 0), Joint(preview, "tip"));          // mid turned a quarter about +Z
        Assert.Equal(new[] { 0, 1 }, preview.Joints.Skip(1).Select(j => j.Parent));
        var socket = Assert.Single(preview.Sockets);
        Assert.Equal(("top", "tip"), (socket.Name, socket.Joint));
        Near(new Vector3(-0.75f, 1, 0), socket.Position);                // half a metre further along the tip
        Assert.Equal(new[] { new PreviewMarker("step", 0.25f, 0.25f) }, preview.ClipMarkers);
        Assert.Empty(preview.MarkersOf("idle"));

        preview.Scrub(1.25f);                                            // a loop wraps
        Assert.Equal(0.25f, preview.ClipTime, 3);
        preview.ClipLoops = false;
        preview.Scrub(5f);                                               // a one-shot holds its end
        Assert.Equal(1f, preview.ClipTime, 3);
        Near(new Vector3(0, 1, 0) + Joint(preview, "root"), Joint(preview, "mid"));

        preview.Playing = true;                                          // played on: the clip's own clock
        preview.ClipLoops = true;
        preview.Scrub(0f);
        preview.Advance(0.5f);
        Assert.Equal(0.5f, preview.ClipTime, 3);

        Assert.False(preview.ShowClip("dance"));
        Assert.False(preview.OpenModel(AssetPath.Intern("models/missing.glb")));
        Assert.NotEmpty(preview.Error);
    }

    // The graph: the real animator, whose params are all the preview's (speed stays what it was set to,
    // though the graph reads it from a body that never moves), whose states can be entered by hand, and
    // whose clip events are kept; each layer with its clip, the clip's markers and the blend's weights.
    [Fact]
    public void AGraphRunsWithTheParamsAndStatesThePreviewSets()
    {
        using var app = NewGame();
        using var preview = new AnimationPreview(app.Engine);
        Assert.True(preview.Open(HeroGraph));
        Assert.Equal(Rig, preview.Model);                                 // from the hero prefab's animator
        Assert.Equal(AnimationPreviewMode.Graph, preview.Mode);
        Assert.Equal(3, preview.Joints.Count);
        Assert.Equal(new[] { "base", "upper" }, preview.Layers.Select(l => l.Name));
        Assert.Equal(new[] { "idle", "move", "hop" }, preview.Layers[0].States);
        Assert.Equal(("idle", "none"), (preview.Layers[0].State, preview.Layers[1].State));
        var speed = preview.Params.Single(p => p.Name == "speed");
        Assert.Equal((AnimParamKind.Float, AnimParamSource.Speed, 0f), (speed.Kind, speed.From, speed.Value));
        Assert.Equal(AnimParamKind.Trigger, preview.Params.Single(p => p.Name == "jump").Kind);

        Assert.True(preview.SetParam("speed", 4f));
        preview.Step(0.5f);
        var layer = preview.Layers[0];
        Assert.Equal("move", layer.State);
        Assert.Equal(4f, preview.Params.Single(p => p.Name == "speed").Value);
        Assert.Equal(new[] { ("walk", 0f), ("run", 1f) }, layer.Weights.Select(w => (w.Clip, MathF.Round(w.Weight, 3))));
        Assert.Equal("run", layer.Clip);
        Assert.Equal(0.5f, layer.ClipDuration, 3);

        Assert.True(preview.Trigger("wave"));                             // the upper layer plays walk on mid's branch
        preview.Step(1f);
        var upper = preview.Layers[1];
        Assert.Equal(("wave", "walk"), (upper.State, upper.Clip));
        Assert.Equal(new[] { new PreviewMarker("step", 0.25f, 0.25f) }, upper.Markers);
        Assert.Contains(preview.Events, e => e.Name == "step");
        Assert.True(preview.Time > 1.5f);

        Assert.True(preview.SetParam("armed", 3f));                       // a bool reads anything but 0 as 1
        Assert.Equal(1f, preview.Params.Single(p => p.Name == "armed").Value);
        Assert.False(preview.SetParam("nope", 1f));

        Assert.True(preview.PlayState("hop"));                            // entered by hand
        Assert.Equal("hop", preview.Layers[0].State);
        Assert.False(preview.PlayState("fly"));
        Assert.False(preview.PlayState("wave"));                          // not a base state
        Assert.True(preview.PlayState("none", "upper"));
        Assert.Equal("none", preview.Layers[1].State);

        // Paused, Advance holds; played, it runs at Speed.
        preview.Playing = false;
        float at = preview.Time;
        preview.Advance(1f);
        Assert.Equal(at, preview.Time);
        preview.Playing = true;
        preview.Speed = 0.5f;
        preview.Advance(0.2f);
        Assert.Equal(at + 0.1f, preview.Time, 2);

        // A clip of the model shown in the middle, and back to the graph where it was.
        Assert.True(preview.ShowClip("walk", 0.5f));
        Near(new Vector3(-0.25f, 1, 0), Joint(preview, "tip"));
        Assert.True(preview.ShowGraph());
        Assert.Equal(AnimationPreviewMode.Graph, preview.Mode);

        Assert.False(preview.Open(new RecordId("game", "missing")));
        Assert.True(preview.IsOpen);                                      // a failed open leaves what was open
        preview.Close();
        Assert.False(preview.IsOpen);
        Assert.Empty(preview.Joints);
    }

    // A graph no prefab plays on a model (a sprite's) still runs: states and params, no pose.
    [Fact]
    public void AGraphWithNoModelRunsWithoutAPose()
    {
        using var app = NewGame();
        using var preview = new AnimationPreview(app.Engine);
        Assert.True(preview.Open(new RecordId("game", "lonely")));
        Assert.True(preview.Model.IsEmpty);
        preview.Step(0.2f);
        Assert.Equal("a", preview.Layers[0].State);
        Assert.Empty(preview.Joints);
        Assert.Empty(preview.Sockets);
    }

    // The console presses the same buttons (phase 10a decision 6).
    [Fact]
    public void TheAnimPreviewCommandsDriveThePreview()
    {
        using var app = NewGame();
        using var preview = new AnimationPreview(app.Engine);
        var cvars = app.Engine.CVars;
        AnimationPreviewCommands.Register(cvars, () => preview);
        using var log = new CaptureSink();

        Assert.True(cvars.Execute("anim_preview hero"));
        Assert.Equal((HeroGraph, Rig), (preview.Graph, preview.Model));
        string said = log.Entries.Last(e => e.Message.StartsWith("anim_preview: graph game:hero", StringComparison.Ordinal)).Message;
        Assert.Contains("3 joints, 3 clip(s)", said);
        Assert.Contains("clip walk (1.00s) | step@0.25s", said);
        Assert.Contains("socket top on tip", said);
        Assert.Contains("layer upper: none", said);

        Assert.True(cvars.Execute("anim_preview_param speed 4"));
        Assert.True(cvars.Execute("anim_preview_step 0.5"));
        Assert.Equal("move", preview.Layers[0].State);
        Assert.True(cvars.Execute("anim_preview_param wave"));
        Assert.True(cvars.Execute("anim_preview_step 1"));
        Assert.Contains(log.Entries, e => e.Message.StartsWith("anim_preview: t=", StringComparison.Ordinal) && e.Message.Contains("step@"));
        Assert.True(cvars.Execute("anim_preview_state hop"));
        Assert.Equal("hop", preview.Layers[0].State);
        Assert.True(cvars.Execute("anim_preview_state none upper"));
        Assert.Equal("none", preview.Layers[1].State);

        Assert.True(cvars.Execute("anim_preview_clip walk 0.5"));
        Assert.Equal((AnimationPreviewMode.Clip, "walk", 0.5f), (preview.Mode, preview.Clip, preview.ClipTime));
        Assert.Contains(log.Entries, e => e.Message == "anim_preview: clip walk at 0.50/1.00s, 3 joints, 1 socket(s) | step@0.25s");
        Assert.True(cvars.Execute("anim_preview_clip"));
        Assert.Equal(AnimationPreviewMode.Graph, preview.Mode);

        Assert.True(cvars.Execute("anim_preview models/rig.glb"));
        Assert.True(preview.Graph.IsEmpty);
        Assert.Equal(AnimationPreviewMode.Clip, preview.Mode);

        Assert.True(cvars.Execute("anim_preview_close"));
        Assert.False(preview.IsOpen);
    }

    // Issue #362's done-when, the game's half: the Sandbox's brute is a creature on a skeleton that the real
    // game runs — its graph guards and swings on the upper layer, and the strike's `hit` is raised as the
    // game's AnimationEvent — and the preview opens its graph on its model, with the strike's marker and
    // the mannequin's sockets.
    [Fact]
    public void TheSandboxsBruteRunsOnASkeletonAndThePreviewOpensItsGraph()
    {
        using var app = SandboxScreensTests.Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        var brute = world.FindByName("brute");
        Assert.False(brute.IsNull, "no brute in the clearing");
        var events = new EventProbe<AnimationEvent>(world);
        var seen = new System.Collections.Generic.HashSet<string>();
        for (int tick = 0; tick < 60 * 5; tick++)
        {
            world.RunFixed(1f / 60f);
            if (Animators.StateOf(world, brute, "upper") is { } state) seen.Add(state);
        }
        Assert.True(Animators.TryGetPose(world, brute, out var pose));
        Assert.Equal(18, pose.JointCount);
        Assert.Superset(new System.Collections.Generic.HashSet<string> { "none", "guard", "attack" }, seen);
        Assert.Contains(events.All, e => e.Entity == brute && e.Name == "hit");

        using var preview = new AnimationPreview(app.Engine);
        Assert.True(preview.Open(new RecordId("sandbox", "brute")));
        Assert.Equal(AssetPath.Intern("models/mannequin.glb"), preview.Model);
        Assert.Equal(new[] { "guard", "attack" }, preview.Params.Select(p => p.Name).Skip(1));
        Assert.Equal(new[] { "hand_l", "hand_r", "head_top" }, preview.Sockets.Select(s => s.Name));
        Assert.Equal(new[] { new PreviewMarker("hit", 0.4f, 0.4f / preview.Set!.FindClip("attack")!.Duration) }, preview.MarkersOf("attack"));
        Assert.True(preview.SetParam("guard", 1f));
        preview.Step(0.3f);
        Assert.Equal("guard", preview.Layers[1].State);
        Assert.True(preview.Trigger("attack"));
        preview.Step(0.6f);
        Assert.Equal("attack", preview.Layers[1].State);
        Assert.Contains(preview.Events, e => e.Name == "hit");
        // The head is above the hands, wherever the strike has the arms.
        var head = preview.Sockets.Single(s => s.Name == "head_top").Position;
        Assert.True(head.Y > 1.5f, $"head_top at {head}");
    }
}
