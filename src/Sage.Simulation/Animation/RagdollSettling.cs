#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// Ragdolls that settle, and ragdolls in saves (issue #248, phase 4k; docs/design/12 "As built (settling
// and saves, issue #248)"). RagdollSystem (RagdollRuntime.cs) calls in here each tick for an active ragdoll.
//
// **Settling:** each tick every body slower than the record's `settleSpeed` — its centre, and the reach
// of its spin (angular speed × the body's farthest point from its centre) — adds the tick to
// `Ragdoll.RestTime`; one body faster puts it back to 0. At `settleTime` the ragdoll has **settled**:
// `Ragdoll.Settled`, the `OnSettled` output (once), and its bodies are stopped and put to sleep, so a
// corpse lies still and costs nothing. Something that sends one moving faster than twice settleSpeed
// (a shot, a crate) unsettles it; it settles (and says so) again once it lies still again.
//
// **Asleep, nothing to do:** while every body sleeps, and the pose has been written since they went to
// sleep, RagdollSystem leaves the ragdoll alone: no damping, no pose write, no root follow. That breaks
// the pose seam's "rewrite every tick" on purpose, and holds because nothing adjusts a ragdolled entity's
// pose in place: foot IK and aim IK leave out `sage:ragdolled` entities, and attachments and hitboxes
// only read it. A game's own pose modifier must do the same (skip Ragdolled entities).
//
// **Saved:** `Settled`, `RestTime` and, while active, `Bodies`: each body's position, rotation, linear
// and angular velocity in the table's body order, written each tick the ragdoll moves (never the pose).
// A load rebuilds the bodies and joints as Start does — joints from the pose the suspended animator
// samples, so their anchors and rest turns are the original ones — and then puts every body back where
// it was, moving as it was; a settled ragdoll goes straight back to sleep where it lay. A save whose
// body count is not the table's (the record changed since) is rebuilt from the pose, with a warning.
public static partial class Ragdolls
{
    // The entity output: every body of the ragdoll has lain still for its record's settleTime.
    public const string OnSettled = "OnSettled";

    internal static void RegisterSettling(Engine engine) =>
        engine.Outputs.Declare(OnSettled, "This ragdoll has come to rest: every body slower than its record's settleSpeed for settleTime (sage:ragdoll). Once per settling.");

    // Has it come to rest (and not been disturbed since)? False when it is not a ragdoll.
    public static bool IsSettled(World world, Entity entity) =>
        !entity.IsNull && world.IsAlive(entity) && world.TryGet<Ragdoll>(entity, out var r) && r.Active && r.Settled;

    // What Start and Stop clear: a ragdoll starts unsettled, with no body states.
    internal static void ResetSettling(ref Ragdoll ragdoll)
    {
        ragdoll.Settled = false;
        ragdoll.RestTime = 0f;
        ragdoll.Bodies = null;
    }

    // Just after a rebuild (a load): every body back to its saved state, or false (and the bodies left as
    // built, at rest from the pose) when there is none or it does not fit the table.
    internal static bool RestoreBodies(IPhysicsWorld physics, RagdollInstance instance, ref Ragdoll ragdoll, Entity entity)
    {
        var saved = ragdoll.Bodies;
        if (saved == null || saved.Length == 0) return false;
        var bodies = instance.Bodies;
        if (saved.Length != bodies.Length)
        {
            Log.Warn(LogCat.Save, $"{World.Describe(entity)}: its saved ragdoll has {saved.Length} bodies and its ragdoll record {bodies.Length} now; " +
                                  "the bodies are rebuilt from its pose instead");
            ragdoll.Bodies = null;
            ragdoll.Settled = false;
            ragdoll.RestTime = 0f;
            return false;
        }
        for (int i = 0; i < bodies.Length; i++)
        {
            ref readonly var s = ref saved[i];
            var rotation = s.Rotation.LengthSquared() > 1e-6f ? Quaternion.Normalize(s.Rotation) : Quaternion.Identity;
            // A ragdoll body's collider has no centre offset: the shape's pose is the body's.
            physics.SetPose(bodies[i], default(Collider), new Pose { Position = s.Position, Rotation = rotation, Scale = Vector3.One });
            physics.SetVelocity(bodies[i], ragdoll.Settled ? Vector3.Zero : s.Linear);
            physics.SetAngularVelocity(bodies[i], ragdoll.Settled ? Vector3.Zero : s.Angular);
        }
        if (ragdoll.Settled) physics.Sleep(bodies);
        return true;
    }

    // Every body asleep and the pose written since they went to sleep: nothing has moved.
    internal static bool IsQuiet(IPhysicsWorld physics, RagdollInstance instance)
    {
        if (!instance.Quiet) return false;
        var bodies = instance.Bodies;
        for (int i = 0; i < bodies.Length; i++)
            if (physics.IsAwake(bodies[i]))
            {
                instance.Quiet = false;
                return false;
            }
        return true;
    }

    // One tick of settling (see the top of this file). `dt` 0 (a rebuild's tick) only checks.
    internal static void Settle(World world, IPhysicsWorld physics, RagdollInstance instance, ref Ragdoll ragdoll, Entity entity, float dt)
    {
        var table = instance.Table!;
        var bodies = instance.Bodies;
        float limit = table.SettleSpeed;
        float fastest = 0f;
        bool awake = false;
        for (int i = 0; i < bodies.Length; i++)
        {
            if (!physics.IsAwake(bodies[i])) continue;   // asleep is at rest
            awake = true;
            var b = table.Bodies[i];
            float speed = MathF.Max(physics.VelocityOf(bodies[i]).Length(), physics.AngularVelocityOf(bodies[i]).Length() * Reach(b));
            if (speed > fastest) fastest = speed;
        }

        if (fastest >= limit)
        {
            ragdoll.RestTime = 0f;
            if (ragdoll.Settled && fastest > 2f * limit) ragdoll.Settled = false;
            return;
        }
        ragdoll.RestTime += dt;
        if (ragdoll.RestTime < table.SettleTime) return;
        ragdoll.RestTime = table.SettleTime;   // no further to count
        if (awake) physics.Sleep(bodies);
        if (ragdoll.Settled) return;
        ragdoll.Settled = true;
        world.FireOutput(entity, OnSettled, entity);
    }

    // The bodies' states into the component, for a save (allocated once per ragdoll, then written in
    // place); then the ragdoll is quiet if every body sleeps.
    internal static void Keep(IPhysicsWorld physics, RagdollInstance instance, ref Ragdoll ragdoll)
    {
        var bodies = instance.Bodies;
        if (ragdoll.Bodies == null || ragdoll.Bodies.Length != bodies.Length) ragdoll.Bodies = new RagdollBodyState[bodies.Length];
        var states = ragdoll.Bodies;
        bool asleep = true;
        for (int i = 0; i < bodies.Length; i++)
        {
            var pose = physics.PoseOf(bodies[i]);
            states[i] = new RagdollBodyState
            {
                Position = pose.Position,
                Rotation = pose.Rotation,
                Linear = physics.VelocityOf(bodies[i]),
                Angular = physics.AngularVelocityOf(bodies[i]),
            };
            if (physics.IsAwake(bodies[i])) asleep = false;
        }
        instance.Quiet = asleep;
    }

    // How far a body's surface reaches from its centre (what turns its spin into a speed).
    private static float Reach(ResolvedRagdollBody b) => MathF.Max(0.05f, b.Shape switch
    {
        RagdollShape.Sphere => b.Size.X,
        RagdollShape.Capsule => b.Size.X + 0.5f * b.Size.Y,
        _ => 0.5f * b.Size.Length(),
    });
}

// One ragdoll body as a save keeps it: where its shape is and how it moves, in world (origin) space.
[Experimental(RagdollApi.Experimental, UrlFormat = RagdollApi.Url)]
public struct RagdollBodyState
{
    [Property(Unit = "m", Tooltip = "The body's centre")]
    public Vector3 Position;
    [Property(Tooltip = "The body's rotation")]
    public Quaternion Rotation;
    [Property(Unit = "m/s", Tooltip = "Its linear velocity")]
    public Vector3 Linear;
    [Property(Unit = "rad/s", Tooltip = "Its angular velocity, about the world axes")]
    public Vector3 Angular;
}
