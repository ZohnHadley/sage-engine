#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// REDESIGN §5 phase 4d's exit criterion (issue #122, docs/design/12 "As built (phase 4d's exit)"): an NPC
// walks, runs, aims and attacks, blended, and first-person arms reload a weapon. The NPC is
// tests/games/skeletal's — a generated box mannequin (games/Sandbox/tools/make_mannequin.py) with an
// anim_graph, aim IK and foot IK, all data, no C# — and the arms are the Sandbox's.
public class NpcLocomotionTests
{
    public NpcLocomotionTests() { _ = TestEnv.UserRoot; }

    internal const float Dt = 1f / 60f;
    private const float DegToRad = MathF.PI / 180f;

    internal static string Game(string name) => Path.Combine(TestEnv.FolderAbove("tests"), "tests", "games", name);

    internal static HeadlessApp Skeletal() => HeadlessApp.ForGame(Game("skeletal")).WithEngineContent().Boot();

    internal static readonly RecordId Npc = new("skeletal", "npc");

    internal static void Step(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++)
        {
            world.RunFixed(Dt);
            world.RunFrame(Dt, 1f);
        }
    }

    // Moves the entity along its facing (-Z at yaw 0) at `speed` m/s for `ticks` ticks.
    private static void Move(World world, Entity e, float speed, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            world.Get<Transform>(e).LocalPosition += new Vector3(0, 0, -speed * Dt);
            Step(world);
        }
    }

    private static SkeletonPose Pose(World world, Entity e) =>
        Animators.TryGetPose(world, e, out var pose) ? pose : throw new Xunit.Sdk.XunitException("the NPC has no pose");

    // A joint's model-space rotation, and the elevation (degrees above level) of the bone from `from` to `to`.
    private static Quaternion Rotation(SkeletonPose pose, string joint) =>
        Quaternion.CreateFromRotationMatrix(pose.ModelSpace[pose.Skeleton.IndexOf(joint)]);

    private static float Elevation(SkeletonPose pose, string from, string to)
    {
        var d = Vector3.Normalize(pose.ModelSpace[pose.Skeleton.IndexOf(to)].Translation - pose.ModelSpace[pose.Skeleton.IndexOf(from)].Translation);
        return MathF.Asin(Math.Clamp(d.Y, -1f, 1f)) / DegToRad;
    }

    // Where a joint's forward (-Z) points, as degrees above level.
    private static float Pitch(SkeletonPose pose, string joint)
    {
        var forward = Vector3.Transform(-Vector3.UnitZ, Rotation(pose, joint));
        return MathF.Asin(Math.Clamp(forward.Y, -1f, 1f)) / DegToRad;
    }

    // Acceptance: one NPC, driven from code — its speed by moving it, its aim and attack by params — walks,
    // blends into a run, aims with its upper body over the legs' blend (the layer's own blend space on
    // aim_pitch, and aim IK up the spine), strikes and goes back to aiming.
    [Fact]
    public void AnNpcWalksRunsAimsAndAttacks_Blended()
    {
        using var app = Skeletal();
        var world = app.World;
        var npc = world.Spawn(Npc, new Vector3(12, 0, 14));
        Step(world, 3);

        // Standing: idle, nothing on the upper layer, both feet on the ground with nothing to correct.
        Assert.Equal("idle", Animators.StateOf(world, npc));
        Assert.Equal("none", Animators.StateOf(world, npc, "upper"));
        Assert.Equal(1f, Animators.ClipWeight(world, npc, "idle"));
        var feet = world.Get<FootIk>(npc);
        Assert.True(feet.LeftGrounded && feet.RightGrounded, "the mannequin's feet do not reach the floor");
        Assert.True(feet.PelvisDrop > -0.005f, $"the pelvis dropped {feet.PelvisDrop} m on flat ground");

        // A walk: all walk once the fade (a quarter second) is done.
        Move(world, npc, 1.5f, 30);
        Assert.Equal("move", Animators.StateOf(world, npc));
        Assert.True(Animators.HasTag(world, npc, "moving"));
        Assert.Equal(1.5f, Animators.GetParam(world, npc, "speed")!.Value, 2);
        Assert.Equal(1f, Animators.ClipWeight(world, npc, "walk"), 3);
        Assert.Equal(0f, Animators.ClipWeight(world, npc, "run"), 3);
        Assert.False(world.Get<Animator>(npc).Layers![0].Fading);

        // A jog: half walk, half run, phase-synced (one phase for both clips).
        Move(world, npc, 3f, 10);
        Assert.Equal(0.5f, Animators.ClipWeight(world, npc, "walk"), 2);
        Assert.Equal(0.5f, Animators.ClipWeight(world, npc, "run"), 2);

        // A run: all run past the last point.
        Move(world, npc, 5f, 10);
        Assert.Equal(1f, Animators.ClipWeight(world, npc, "run"), 3);
        Assert.Equal(0f, Animators.ClipWeight(world, npc, "walk"), 3);
        Assert.Equal("none", Animators.StateOf(world, npc, "upper"));

        // Aiming level while jogging: the upper layer blends in `aim`, the legs keep the base's blend.
        Animators.SetParam(world, npc, "aiming", true);
        Animators.SetParam(world, npc, "aim_pitch", 0f);
        Move(world, npc, 3f, 20);
        Assert.Equal("aim", Animators.StateOf(world, npc, "upper"));
        Assert.True(Animators.HasTag(world, npc, "aiming"));
        Assert.Equal("move", Animators.StateOf(world, npc));
        Assert.Equal(1f, Animators.ClipWeight(world, npc, "aim", "upper"), 3);
        Assert.Equal(0.5f, Animators.ClipWeight(world, npc, "run"), 2);
        float levelArm = Elevation(Pose(world, npc), "upper_arm_r", "forearm_r");

        // Aiming 30° up: half aim, half aim_up on the layer; aim IK turns the head the whole 30° and the
        // chest half of it, and the arms (the clips' half plus the chest's) rise by the 30° too.
        Animators.SetParam(world, npc, "aim_pitch", 30f);
        Move(world, npc, 3f, 20);
        Assert.Equal(0.5f, Animators.ClipWeight(world, npc, "aim", "upper"), 3);
        Assert.Equal(0.5f, Animators.ClipWeight(world, npc, "aim_up", "upper"), 3);
        Assert.Equal(0f, Animators.ClipWeight(world, npc, "aim_down", "upper"), 3);
        Assert.Equal(30f * DegToRad, world.Get<AimIk>(npc).Pitch, 4);
        var pose = Pose(world, npc);
        Assert.Equal(30f, Pitch(pose, "head"), 0);                      // the aim clips leave the spine at rest
        Assert.Equal(15f, Pitch(pose, "chest"), 0);
        float raised = Elevation(pose, "upper_arm_r", "forearm_r") - levelArm;
        Assert.True(MathF.Abs(raised - 30f) < 3f, $"the right arm rose {raised}°, not 30°");

        // The legs walk on under the aim: their pose keeps changing while the arms hold still.
        float thigh = Pitch(pose, "thigh_l");
        float arm = Elevation(pose, "upper_arm_r", "forearm_r");
        Move(world, npc, 3f, 9);
        pose = Pose(world, npc);
        Assert.True(MathF.Abs(Pitch(pose, "thigh_l") - thigh) > 10f, "the legs stopped under the aim");
        Assert.True(MathF.Abs(Elevation(pose, "upper_arm_r", "forearm_r") - arm) < 2f, "the aim moved with the legs");

        // Aiming down past the last point: all aim_down.
        Animators.SetParam(world, npc, "aim_pitch", -70f);
        Move(world, npc, 3f, 2);
        Assert.Equal(1f, Animators.ClipWeight(world, npc, "aim_down", "upper"), 3);

        // The attack: a strike on the upper layer, the legs still jogging, its `hit` raised once (the
        // mannequin's anim_events: 0.4 s in), then back to aiming when the clip is done.
        var events = new EventProbe<AnimationEvent>(world);
        string[] Heard() => events.All.Where(e => e.Entity == npc).Select(e => e.Name).ToArray();
        Animators.SetParam(world, npc, "aim_pitch", 0f);
        Assert.True(Animators.SetTrigger(world, npc, "attack"));
        Move(world, npc, 3f, 1);
        Assert.Equal("attack", Animators.StateOf(world, npc, "upper"));
        Assert.True(Animators.HasTag(world, npc, "attacking"));
        Assert.Equal("move", Animators.StateOf(world, npc));
        Assert.Equal(0.5f, Animators.ClipWeight(world, npc, "walk"), 2);
        Move(world, npc, 3f, 14);                                        // a quarter of a second in: arms up and back
        Assert.True(Elevation(Pose(world, npc), "upper_arm_r", "forearm_r") > 45f, "the strike did not raise the arm");
        Assert.Empty(Heard());
        Move(world, npc, 3f, 12);                                        // past 0.4 s: the blow connects
        Assert.Equal(new[] { "hit" }, Heard());
        Move(world, npc, 3f, 26);                                        // past its 0.8 s
        Assert.Equal("aim", Animators.StateOf(world, npc, "upper"));
        Assert.False(Animators.HasTag(world, npc, "attacking"));
        Assert.Equal(new[] { "hit" }, Heard());

        // A reload on the same layer: mag_out, then mag_in, then back to aiming.
        Assert.True(Animators.SetTrigger(world, npc, "reload"));
        Move(world, npc, 3f, 1);
        Assert.Equal("reload", Animators.StateOf(world, npc, "upper"));
        Move(world, npc, 3f, 30);
        Assert.Equal(new[] { "hit", "mag_out" }, Heard());
        Move(world, npc, 3f, 30);
        Assert.Equal(new[] { "hit", "mag_out", "mag_in" }, Heard());
        Move(world, npc, 3f, 30);
        Assert.Equal("aim", Animators.StateOf(world, npc, "upper"));

        // Lowering the aim and stopping: the upper layer lets the base show, and the base goes to idle.
        Animators.SetParam(world, npc, "aiming", false);
        Move(world, npc, 0f, 30);
        Assert.Equal("none", Animators.StateOf(world, npc, "upper"));
        Assert.Equal("idle", Animators.StateOf(world, npc));
    }

    // The yard, as CI runs it: with nobody at the keys, the walker, jogger and runner patrol their lanes at
    // their own paces (their state machines send tweens), the trooper aims and strikes while it walks, and
    // the sentry aims, strikes and reloads.
    [Fact]
    public void TheYardsNpcsPatrolAtTheirOwnPaces_AimStrikeAndReload()
    {
        using var app = Skeletal();
        var world = app.World;
        Assert.Null(app.Engine.Modules.Game);
        Entity Named(string name)
        {
            var e = world.FindByName(name);
            Assert.False(e.IsNull, $"no {name} in the yard");
            return e;
        }
        var walker = Named("walker");
        var jogger = Named("jogger");
        var runner = Named("runner");
        var trooper = Named("trooper");
        var sentry = Named("sentry");

        var sentryStates = new HashSet<string>();
        var trooperStates = new HashSet<string>();
        bool trooperAimedWalking = false, runnerRan = false, joggerBlended = false, walkerWalked = false;
        for (int tick = 0; tick < 60 * 12; tick++)
        {
            Step(world);
            sentryStates.Add(Animators.StateOf(world, sentry, "upper") ?? "");
            trooperStates.Add(Animators.StateOf(world, trooper, "upper") ?? "");
            trooperAimedWalking |= Animators.StateOf(world, trooper, "upper") == "aim" && Animators.StateOf(world, trooper) == "move"
                                   && Animators.ClipWeight(world, trooper, "walk") > 0.8f;
            runnerRan |= Animators.StateOf(world, runner) == "move" && Animators.ClipWeight(world, runner, "run") > 0.99f;
            joggerBlended |= Animators.StateOf(world, jogger) == "move" && MathF.Abs(Animators.ClipWeight(world, jogger, "run") - 0.5f) < 0.02f;
            walkerWalked |= Animators.StateOf(world, walker) == "move" && Animators.ClipWeight(world, walker, "walk") > 0.99f;
        }
        Assert.True(walkerWalked, "the walker never walked");
        Assert.True(joggerBlended, "the jogger never blended walk and run half and half");
        Assert.True(runnerRan, "the runner never ran");
        Assert.True(trooperAimedWalking, "the trooper never aimed while walking");
        Assert.Contains("attack", trooperStates);
        Assert.Contains("aim", sentryStates);
        Assert.Contains("attack", sentryStates);
        Assert.Contains("reload", sentryStates);
        Assert.Equal("patrol", world.Get<StateMachine>(walker).Machine.Name);

        // They went up their lanes and came back (a full lap is two 12 m legs and two turns).
        Assert.InRange(world.Get<Transform>(runner).LocalPosition.X, -6.5f, 6.5f);
        Assert.Equal(-5f, world.Get<Transform>(jogger).LocalPosition.Z, 3);
    }

    [Fact]
    public void TheSkeletalGameValidates()
    {
        var report = ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = Game("skeletal"),
            EngineContentDirectory = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content"),
            AvailablePlugins = BasePlugins.All(),
        });
        Assert.True(report.Ok, string.Join("\n", report.Errors));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("scene.json") || w.Contains("npc.json"));
    }

    // Acceptance: the Sandbox's first-person arms (make_mannequin.py) reload the sword in hand: R plays
    // their reload, which fires mag_out then mag_in, once each, and goes back to idle.
    [Fact]
    public void FirstPersonArmsReloadAWeapon()
    {
        using var app = HeadlessApp.ForGame(Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox"), new global::Sandbox.SandboxModule())
            .WithEngineContent().Boot();
        var world = app.World;
        var player = Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());
        world.Get<Melee>(player).Attack = new RecordId("sandbox", "sword_swing");
        Step(world, 3);
        var camera = Assert.Single(world.Query<Camera>().AllTags(Tags.Get<PlayerCamera>()).Entities.ToEntityList());
        var arms = Viewmodels.ArmsOf(world, camera);
        Assert.False(arms.IsNull || Viewmodels.WeaponOf(world, camera).IsNull, "the sword's arms and sword were not spawned");
        Assert.True(app.Engine.Animations.TryGet(AssetPath.Intern("models/arms.glb"), out var set));
        var reloadClip = set.FindClip("reload")!;
        Assert.Equal(1.2f, reloadClip.Duration, 3);
        // The events come from the Sandbox's anim_events record for the arms (viewmodel.json), not from code.
        Assert.Equal(new[] { "mag_out", "mag_in" }, reloadClip.Events.Select(e => e.Name));
        var events = new EventProbe<AnimationEvent>(world);
        Assert.Equal("idle", Animators.StateOf(world, arms));

        var input = world.Resources.Get<PlayerInput>();
        input.HasCommand = true;
        input.Command = new PlayerCommand { Tick = 1, Pressed = default(ActionMask).With(app.Engine.Actions.Get("Reload")) };
        Step(world);
        input.Command = new PlayerCommand { Tick = 2 };
        Assert.Equal("reload", Animators.StateOf(world, arms));
        Assert.True(Animators.HasTag(world, arms, "reloading"));

        Step(world, 30);                                         // half a second: the magazine is out
        Assert.Equal(new[] { "mag_out" }, events.All.Where(e => e.Entity == arms).Select(e => e.Name));
        Step(world, 30);                                         // a second: and back in
        Assert.Equal(new[] { "mag_out", "mag_in" }, events.All.Where(e => e.Entity == arms).Select(e => e.Name));
        Step(world, 20);                                         // past its 1.2 s: idle again, the sword still in hand
        Assert.Equal("idle", Animators.StateOf(world, arms));
        Assert.False(Animators.HasTag(world, arms, "reloading"));
        Step(world, 120);
        Assert.Equal(2, events.All.Count(e => e.Entity == arms));
        Assert.Equal(arms, Viewmodels.ArmsOf(world, camera));
    }
}

// Fifty NPCs — each an animator blending walk and run, an aim layer, aim IK and foot IK — allocate
// nothing per tick, beside the yard's own. Allocation is measured per thread, alone.
[Xunit.Collection(MeasurementsCollection.Name)]
public class NpcAllocationTests
{
    public NpcAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void FiftyNpcsAllocateNothingPerTick()
    {
        using var app = NpcLocomotionTests.Skeletal();
        var world = app.World;
        var npcs = new Entity[50];
        for (int i = 0; i < npcs.Length; i++)
            npcs[i] = world.Spawn(NpcLocomotionTests.Npc, new Vector3(-12 + (i % 10) * 2.5f, 0, 8 + (i / 10) * 2f));

        int tick = 0;
        void Step()
        {
            tick++;
            for (int i = 0; i < npcs.Length; i++)
            {
                float speed = 2.5f + 2.5f * MathF.Sin(tick * 0.03f + i);
                world.Get<Transform>(npcs[i]).LocalPosition += new Vector3(MathF.Sin(tick * 0.01f) * speed / 60f, 0, 0);
                int beat = (tick + i * 7) % 240;
                if (beat == 0) Animators.SetParam(world, npcs[i], "aiming", true);
                if (beat == 1) Animators.SetParam(world, npcs[i], "aim_pitch", (i % 5) * 20f - 40f);
                if (beat == 60) Animators.SetTrigger(world, npcs[i], "attack");
                if (beat == 120) Animators.SetTrigger(world, npcs[i], "reload");
                if (beat == 200) Animators.SetParam(world, npcs[i], "aiming", false);
            }
            world.RunFixed(NpcLocomotionTests.Dt);
            world.RunFrame(NpcLocomotionTests.Dt, 1f);
            Profiler.EndFrame();
        }

        for (int i = 0; i < 300; i++) Step();   // warm: every state, fade, layer and IK met at least once
        Assert.Contains(npcs, n => Animators.StateOf(world, n, "upper") == "aim");

        // Nothing may allocate but the physics backend's own step (PhysicsStepSystem, 40 bytes a tick with a
        // character in the world), which is not this code; the report shows it by scope, as #120's and #121's do.
        long physicsBefore = ScopeBytes("Fixed.Physics");
        var allocated = AllocationProbe.Measure(300, Step);
        long physics = ScopeBytes("Fixed.Physics") - physicsBefore;
        Assert.True(allocated.Bytes - physics == 0, allocated.ToString());
        Assert.All(npcs, n => Assert.True(Animators.TryGetPose(world, n, out _)));
        Assert.Contains(npcs, n => Animators.StateOf(world, n) == "move");
        Assert.Contains(npcs, n => world.Get<FootIk>(n).LeftGrounded);
    }

    private static long ScopeBytes(string name)
    {
        foreach (var entry in Profiler.All)
            if (entry.Name == name) return entry.AllocatedBytes;
        return 0;
    }
}
