#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Root motion (issue #357, docs/design/12): a state's `rootMotion` takes its clips' root travel and turn
// out of the pose and gives them to the body — the Transform of a kinematic mover, or the character
// controller (Animators.TryTakeRootMotion) — so the body travels exactly as far as the clip's root did
// and the feet do not slide. Against a rig written here: root → mid → tip, whose clips move the root by
// known amounts.
public class RootMotionTests
{
    public RootMotionTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    // The clips' root keys (LINEAR): forward is -Z.
    private const float WalkDuration = 1f, WalkDistance = 1.5f;   // 1.5 m/s
    private const float RunDuration = 0.5f, RunDistance = 2f;     // 4 m/s
    private const float LungeDistance = 1.2f;                       // 0.6 s, one-shot
    private static readonly Vector3 ClimbEnd = new(0, 1.5f, -0.6f); // 1.2 s, one-shot: up a ledge, then onto it

    private const string Graph = """
    [
      { "type": "anim_graph", "id": "npc", "initial": "idle", "fade": 0,
        "params": { "speed": { "default": 1.5 } },
        "states": {
          "idle":  { "clip": "idle" },
          "move":  { "rootMotion": "Translation",
                     "blend": { "x": "speed", "points": [ { "clip": "walk", "x": 1.5 }, { "clip": "run", "x": 4 } ] } },
          "lunge": { "clip": "lunge", "loop": false, "rootMotion": "Translation" },
          "climb": { "clip": "climb", "loop": false, "rootMotion": "Translation", "rootMotionY": true },
          "hop":   { "clip": "climb", "loop": false, "rootMotion": "Translation" },
          "turn":  { "clip": "turn", "loop": false, "rootMotion": "Rotation" },
          "slide": { "clip": "walk" } } },
      { "type": "prefab", "id": "npc", "name": "npc", "parts": { "animator": { "graph": "npc", "model": "models/mover.glb" } } }
    ]
    """;

    private static MountFixture Files()
    {
        var files = new MountFixture();
        files.Write("game", "data/npc.json", Graph);
        var glb = Path.Combine(files.Dir("game"), "models", "mover.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(glb)!);
        File.WriteAllBytes(glb, BuildRig());
        files.Mount("game", "game");
        return files;
    }

    private static HeadlessApp NewApp() => HeadlessApp.Bare().Mount(Files()).Boot("root");

    private static Entity Spawn(World world, Vector3 at, float yaw = 0f)
    {
        var e = world.Spawn(new RecordId("game", "npc"), at);
        world.Get<Transform>(e).LocalRotation = SageMath.RotationFromYaw(yaw);
        return e;
    }

    private static void Tick(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(Dt);
    }

    private static Vector3 At(World world, Entity e) => world.Get<Transform>(e).LocalPosition;

    private static void Near(Vector3 expected, Vector3 actual, float eps = 2e-3f) =>
        Assert.True(Vector3.Distance(expected, actual) < eps, $"expected {expected}, got {actual}");

    // Acceptance (#357's Done): an NPC walking on root motion travels the clip's authored distance — two
    // passes of a 1.5 m walk is 3 m, whichever way it faces — and its root stays where the clip started,
    // so the feet stay where the clip put them instead of sliding at a tween's speed.
    [Fact]
    public void AnNpcTravelsTheClipsAuthoredDistance_AndItsRootStaysInPlace()
    {
        using var app = NewApp();
        var world = app.World;
        var north = Spawn(world, Vector3.Zero);
        var east = Spawn(world, new Vector3(10, 0, 0), yaw: -MathF.PI / 2);   // faces +X
        Tick(world, 1);
        var northStart = At(world, north);
        var eastStart = At(world, east);
        Assert.True(Animators.Play(world, north, "move"));
        Assert.True(Animators.Play(world, east, "move"));

        Tick(world, 120);                                                      // two seconds: two passes of the walk
        Near(northStart + new Vector3(0, 0, -2 * WalkDistance), At(world, north));
        Near(eastStart + new Vector3(2 * WalkDistance, 0, 0), At(world, east));

        // Half a pass more, and the pose's root is still where the clip starts (the clip has it 0.75 m on).
        Tick(world, 30);
        Near(northStart + new Vector3(0, 0, -2.5f * WalkDistance), At(world, north));
        Assert.True(Animators.TryGetPose(world, north, out var pose));
        Near(Vector3.Zero, pose.Local[0].Position, 1e-4f);

        // A state without root motion leaves the root in the pose and the body where it was (the old slide).
        var before = At(world, north);
        Assert.True(Animators.Play(world, north, "slide"));
        Tick(world, 30);
        Near(before, At(world, north), 1e-5f);
        Assert.True(Animators.TryGetPose(world, north, out pose));
        Assert.True(pose.Local[0].Position.Z < -0.5f, $"the clip's root moves in the pose: {pose.Local[0].Position}");
    }

    // A blend space's root motion is its clips' weighted: half walk, half run at 2.75 plays a pass of
    // 0.75 s (the weighted duration) that covers 1.75 m (the weighted distance).
    [Fact]
    public void ABlendSpaceMovesAtItsWeightedSpeed()
    {
        using var app = NewApp();
        var world = app.World;
        var npc = Spawn(world, Vector3.Zero);
        Tick(world, 1);
        Assert.True(Animators.SetParam(world, npc, "speed", 2.75f));
        Assert.True(Animators.Play(world, npc, "move"));
        var start = At(world, npc);
        Tick(world, 90);
        float pass = 0.5f * WalkDuration + 0.5f * RunDuration;
        float perPass = 0.5f * WalkDistance + 0.5f * RunDistance;
        Near(start + new Vector3(0, 0, -1.5f / pass * perPass), At(world, npc));

        // All run past the last point: 4 m/s.
        Assert.True(Animators.SetParam(world, npc, "speed", 6f));
        start = At(world, npc);
        Tick(world, 60);
        Near(start + new Vector3(0, 0, -RunDistance / RunDuration), At(world, npc));
    }

    // Done: a melee lunge and a climb are authored in clips. A one-shot lunge carries the body its 1.2 m
    // and stops there; a climb with rootMotionY lifts it 1.5 m and onto the ledge, where the same clip
    // without rootMotionY keeps its height (gravity's, in a character) and only moves it across.
    [Fact]
    public void ALungeAndAClimbAreAuthoredInClips()
    {
        using var app = NewApp();
        var world = app.World;
        var fighter = Spawn(world, Vector3.Zero);
        var climber = Spawn(world, new Vector3(5, 0, 0));
        var hopper = Spawn(world, new Vector3(10, 0, 0));
        Tick(world, 1);
        Animators.Play(world, fighter, "lunge");
        Animators.Play(world, climber, "climb");
        Animators.Play(world, hopper, "hop");

        Tick(world, 12);                                                       // 0.2 s: the wind-up's first 0.2 m
        Near(new Vector3(0, 0, -0.2f), At(world, fighter));
        Tick(world, 108);                                                      // two seconds in all: both done
        Near(new Vector3(0, 0, -LungeDistance), At(world, fighter));
        Near(new Vector3(5, 0, 0) + ClimbEnd, At(world, climber));
        Near(new Vector3(10, 0, ClimbEnd.Z), At(world, hopper));

        // Parked at their ends, one-shots move nothing more; the climber's root is held at its start.
        Tick(world, 30);
        Near(new Vector3(0, 0, -LungeDistance), At(world, fighter));
        Assert.True(Animators.TryGetPose(world, climber, out var pose));
        Near(Vector3.Zero, pose.Local[0].Position, 1e-4f);
        Assert.True(Animators.TryGetPose(world, hopper, out pose));
        Near(new Vector3(0, ClimbEnd.Y, 0), pose.Local[0].Position, 1e-4f);   // its height stays in the pose
    }

    // Rotation: a turn on the spot turns the body by the root's quarter turn and its intent with it (so a
    // character's controller faces it too); the pose's root keeps facing where the clip started.
    [Fact]
    public void ATurnClipTurnsTheBodyAndItsIntent()
    {
        using var app = NewApp();
        var world = app.World;
        var npc = Spawn(world, Vector3.Zero);
        world.Add(npc, new PawnIntent());
        Tick(world, 1);
        Animators.Play(world, npc, "turn");
        Tick(world, 90);
        Assert.Equal(MathF.PI / 2, SageMath.YawOf(world.Get<Transform>(npc).LocalRotation), 3);
        Assert.Equal(MathF.PI / 2, world.Get<PawnIntent>(npc).Yaw, 3);
        Near(Vector3.Zero, At(world, npc), 1e-5f);
        Assert.True(Animators.TryGetPose(world, npc, out var pose));
        Assert.True(MathF.Abs(Quaternion.Dot(Quaternion.Identity, pose.Local[0].Rotation)) > 1 - 1e-4f, $"root {pose.Local[0].Rotation}");
    }

    // A character: the animator leaves the travel for the controller, which makes it the next tick as a
    // swept move, so it walks the clip's distance on the ground (less the one tick still to be taken)
    // and stops where the clip stops, its intent's Move untouched.
    [Fact]
    public void ACharacterControllerWalksTheClipsDistance()
    {
        using var app = HeadlessApp.Gameplay().WithEngineContent().Mount(Files()).Build();
        var world = app.CreateWorld("walk");
        var floor = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "floor");
        world.Add(floor, Collider.Box(new Vector3(40, 1, 40)));
        var physics = world.Resources.Get<IPhysicsWorld>();
        world.RunFixed(Dt);

        var npc = world.Spawn(new RecordId("game", "npc"), new Vector3(0, 0.02f, 0));
        world.AddCharacter(npc, physics.Layers.Enemy);
        Tick(world, 30);                                                       // settles on the floor
        var start = At(world, npc);
        Animators.Play(world, npc, "move");
        Tick(world, 120);
        var walked = At(world, npc) - start;
        float perTick = WalkDistance / WalkDuration * Dt;
        Assert.InRange(-walked.Z, 2 * WalkDistance - perTick - 2e-3f, 2 * WalkDistance + 2e-3f);
        Assert.Equal(0f, walked.X, 3);
        Assert.Equal(0f, walked.Y, 2);
        Assert.True(world.Get<CharacterController>(npc).Grounded);

        // A lunge from there: its 1.2 m, then the controller's friction has nothing left to stop.
        Animators.Play(world, npc, "lunge");
        start = At(world, npc);
        Tick(world, 60);
        // What the last tick left is the next tick's to take, once: a parked one-shot leaves nothing to make.
        Assert.True(Animators.TryTakeRootMotion(world, npc, out var left, out bool vertical));
        Assert.Equal((Vector3.Zero, false), (left, vertical));
        Assert.False(Animators.TryTakeRootMotion(world, npc, out _, out _));
        Tick(world, 60);
        Assert.InRange(-(At(world, npc) - start).Z, LungeDistance - 0.05f, LungeDistance + 0.15f);
    }

    // ---- the rig --------------------------------------------------------------------------------------

    internal static byte[] BuildRig()
    {
        var model = ModelRoot.CreateModel();
        var scene = model.UseScene("scene");
        var root = scene.CreateNode("root");
        var mid = root.CreateNode("mid");
        mid.LocalMatrix = Matrix4x4.CreateTranslation(0, 1, 0);
        var tip = mid.CreateNode("tip");
        tip.LocalMatrix = Matrix4x4.CreateTranslation(0, 1, 0);
        var body = scene.CreateNode("body");
        body.Mesh = BuildMesh(model);
        var skin = model.CreateSkin("rig");
        skin.BindJoints(Matrix4x4.Identity, root, mid, tip);
        body.Skin = skin;

        var swing = new Dictionary<float, Quaternion>
        {
            [0f] = Quaternion.Identity, [0.25f] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f), [0.5f] = Quaternion.Identity,
        };
        model.CreateAnimation("idle").CreateRotationChannel(mid, swing, true);

        var walk = model.CreateAnimation("walk");
        walk.CreateRotationChannel(mid, swing, true);
        walk.CreateTranslationChannel(root, new Dictionary<float, Vector3>
        {
            [0f] = Vector3.Zero, [0.5f] = new(0, 0, -WalkDistance / 2), [WalkDuration] = new(0, 0, -WalkDistance),
        }, true);

        var run = model.CreateAnimation("run");
        run.CreateTranslationChannel(root, new Dictionary<float, Vector3>
        {
            [0f] = Vector3.Zero, [RunDuration] = new(0, 0, -RunDistance),
        }, true);

        var lunge = model.CreateAnimation("lunge");
        lunge.CreateTranslationChannel(root, new Dictionary<float, Vector3>
        {
            [0f] = Vector3.Zero, [0.2f] = new(0, 0, -0.2f), [0.6f] = new(0, 0, -LungeDistance),
        }, true);

        var climb = model.CreateAnimation("climb");
        climb.CreateTranslationChannel(root, new Dictionary<float, Vector3>
        {
            [0f] = Vector3.Zero, [0.6f] = new(0, ClimbEnd.Y, 0), [1.2f] = ClimbEnd,
        }, true);

        var turn = model.CreateAnimation("turn");
        turn.CreateRotationChannel(root, new Dictionary<float, Quaternion>
        {
            [0f] = Quaternion.Identity,
            [0.5f] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4),
            [1f] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2),
        }, true);

        return model.WriteGLB().ToArray();
    }

    // A strip up the chain, each pair of vertices bound to the joint at its height.
    private static Mesh BuildMesh(ModelRoot model)
    {
        const float w = 0.1f;
        var positions = new Vector3[] { new(-w, 0, 0), new(w, 0, 0), new(-w, 1, 0), new(w, 1, 0), new(-w, 2, 0), new(w, 2, 0) };
        var joints = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0 };
        var weights = new Vector4[6];
        for (int i = 0; i < weights.Length; i++) weights[i] = new Vector4(1, 0, 0, 0);
        var indices = new ushort[] { 0, 1, 3, 0, 3, 2, 2, 3, 5, 2, 5, 4 };

        var mesh = model.CreateMesh("strip");
        var primitive = mesh.CreatePrimitive();
        primitive.DrawPrimitiveType = PrimitiveType.TRIANGLES;
        var position = model.CreateAccessor("positions");
        position.SetVertexData(View(model, MemoryMarshal.AsBytes(positions.AsSpan()).ToArray(), BufferMode.ARRAY_BUFFER), 0, positions.Length,
                               new AttributeFormat(DimensionType.VEC3, EncodingType.FLOAT, false));
        position.UpdateBounds();
        primitive.SetVertexAccessor("POSITION", position);
        var joint = model.CreateAccessor("joints");
        joint.SetVertexData(View(model, joints, BufferMode.ARRAY_BUFFER), 0, positions.Length,
                            new AttributeFormat(DimensionType.VEC4, EncodingType.UNSIGNED_BYTE, false));
        primitive.SetVertexAccessor("JOINTS_0", joint);
        var weight = model.CreateAccessor("weights");
        weight.SetVertexData(View(model, MemoryMarshal.AsBytes(weights.AsSpan()).ToArray(), BufferMode.ARRAY_BUFFER), 0, weights.Length,
                             new AttributeFormat(DimensionType.VEC4, EncodingType.FLOAT, false));
        primitive.SetVertexAccessor("WEIGHTS_0", weight);
        var index = model.CreateAccessor("indices");
        var indexBytes = new byte[(indices.Length * 2 + 3) & ~3];
        MemoryMarshal.AsBytes(indices.AsSpan()).CopyTo(indexBytes);
        index.SetIndexData(View(model, indexBytes, BufferMode.ELEMENT_ARRAY_BUFFER), 0, indices.Length, IndexEncodingType.UNSIGNED_SHORT);
        primitive.SetIndexAccessor(index);
        return mesh;
    }

    private static BufferView View(ModelRoot model, byte[] bytes, BufferMode mode) => model.UseBufferView(bytes, 0, null, 0, mode);
}

// Root motion allocates nothing per tick: fifty movers on a blend, lunging and turning.
[Collection(MeasurementsCollection.Name)]
public class RootMotionAllocationTests
{
    public RootMotionAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void RootMotionAllocatesNothingPerTick()
    {
        var files = new MountFixture();
        files.Write("game", "data/npc.json", """
        [
          { "type": "anim_graph", "id": "npc", "initial": "move", "fade": 0.1,
            "params": { "speed": {}, "lunge": { "kind": "Trigger" } },
            "states": {
              "move":  { "rootMotion": "Full", "blend": { "x": "speed", "points": [ { "clip": "walk", "x": 1.5 }, { "clip": "turn", "x": 4 } ] } },
              "lunge": { "clip": "lunge", "loop": false, "rootMotion": "Translation", "transitions": [ { "to": "move", "after": 0.7 } ] } },
            "transitions": [ { "to": "lunge", "on": "lunge" } ] },
          { "type": "prefab", "id": "npc", "parts": { "animator": { "graph": "npc", "model": "models/mover.glb" } } }
        ]
        """);
        var glb = Path.Combine(files.Dir("game"), "models", "mover.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(glb)!);
        File.WriteAllBytes(glb, RootMotionTests.BuildRig());
        files.Mount("game", "game");
        using var app = HeadlessApp.Bare().Mount(files).Boot("crowd");
        var world = app.World;
        var npcs = new Entity[50];
        for (int i = 0; i < npcs.Length; i++)
        {
            npcs[i] = world.Spawn(new RecordId("game", "npc"), new Vector3(i * 3, 0, 0));
            world.Add(npcs[i], new PawnIntent());
        }

        int tick = 0;
        void Step()
        {
            tick++;
            for (int i = 0; i < npcs.Length; i++)
            {
                Animators.SetParam(world, npcs[i], "speed", 2.5f + 1.5f * MathF.Sin(tick * 0.05f + i));
                if ((tick + i) % 89 == 0) Animators.SetTrigger(world, npcs[i], "lunge");
            }
            world.RunFixed(1f / 60f);
            Profiler.EndFrame();
        }

        for (int i = 0; i < 120; i++) Step();
        var before = world.Get<Transform>(npcs[0]).LocalPosition;
        AllocationProbe.AssertNone(300, Step);
        Assert.NotEqual(before, world.Get<Transform>(npcs[0]).LocalPosition);
    }
}

