#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;

namespace Sage.Physics3D;

// SAGE0134's id and link, for this assembly's own experimental members (Sage.Simulation's
// PhysicsJointsApi is internal to it).
internal static class JointsApi
{
    internal const string Id = "SAGE0134";
    internal const string Url = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api";
}

// Joints (issue #242, docs/design/10 "As built (joints)"): IPhysicsWorld's joints as Bepu constraints.
//
//   Ball      BallSocket, plus a SwingLimit (the cone) and a TwistLimit when asked for
//   Hinge     Hinge (BallSocket and AngularHinge solved together), plus a TwistLimit about the axis
//   Fixed     Weld
//   Distance  DistanceLimit
//
// Bepu constraints join bodies only, so a joint to the world or to a static gets a shapeless kinematic
// body of its own, at the joint's anchor and turned like B's frame: it never collides, it moves with a
// rebase like every other body, and BodyCount and the debug draw leave it out.
//
// A joint lives in a slot; its PhysicsJoint is the slot (+1) and the slot's version, so a handle kept
// after its joint went (removed, broken, or its body removed) never reaches whatever reuses the slot.
// Removing a body walks the slots for joints that use it: joints are few next to bodies, and bodies are
// removed far less often than the world steps.
#pragma warning disable SAGE0134 // the backend that implements the experimental joints
public sealed partial class PhysicsSpace
{
    private struct JointSlot
    {
        public bool Used;
        public int Version;
        public JointKind Kind;
        public BodyHandle A;
        public BodyHandle B;            // B's body, or the anchor this joint owns
        public bool OwnsAnchor;         // B is a shapeless kinematic made for this joint (world or static)
        public int StaticB;             // the static B was anchored to, or -1
        public Vector3 LocalA, LocalB;  // anchors in A's and B's body space
        public Vector3 AxisA;           // in A's space
        public ConstraintHandle Main, Swing, Twist;   // Swing and Twist are -1 when absent
        public float BreakForce;
        public Entity EntityA, EntityB;
    }

    private JointSlot[] _joints = new JointSlot[16];
    private int _jointHigh;                            // slots in use or once used: [0, _jointHigh)
    private readonly Stack<int> _freeJoints = new();
    private readonly List<JointBroken> _broken = new();
    private static readonly ConstraintHandle NoConstraint = new(-1);

    [Experimental(JointsApi.Id, UrlFormat = JointsApi.Url)]
    public int JointCount { get; private set; }

    [Experimental(JointsApi.Id, UrlFormat = JointsApi.Url)]
    public PhysicsJoint AddJoint(in PhysicsBody a, in JointDesc desc) => Add(a, default, world: true, desc);

    [Experimental(JointsApi.Id, UrlFormat = JointsApi.Url)]
    public PhysicsJoint AddJoint(in PhysicsBody a, in PhysicsBody b, in JointDesc desc) => Add(a, b, world: false, desc);

    [Experimental(JointsApi.Id, UrlFormat = JointsApi.Url)]
    public bool JointExists(in PhysicsJoint joint) => SlotOf(joint) >= 0;

    [Experimental(JointsApi.Id, UrlFormat = JointsApi.Url)]
    public void RemoveJoint(in PhysicsJoint joint)
    {
        int slot = SlotOf(joint);
        if (slot >= 0) Release(slot);
    }

    private int SlotOf(in PhysicsJoint joint)
    {
        int slot = joint.Id - 1;
        if ((uint)slot >= (uint)_jointHigh) return -1;
        ref var s = ref _joints[slot];
        return s.Used && s.Version == joint.Version ? slot : -1;
    }

    private PhysicsJoint Add(in PhysicsBody a, in PhysicsBody b, bool world, in JointDesc desc)
    {
        var bodies = Simulation.Bodies;
        if (a.IsStatic || !bodies.BodyExists(new BodyHandle(a.Handle)))
            throw new ArgumentException("a joint's first body must be a body (kinematic or dynamic), not a static", nameof(a));
        if (!world && !b.IsStatic && !bodies.BodyExists(new BodyHandle(b.Handle)))
            throw new ArgumentException("a joint's second body does not exist", nameof(b));
        if (!world && !b.IsStatic && b.Handle == a.Handle)
            throw new ArgumentException("a joint needs two different bodies", nameof(b));
        if (!world && b.IsStatic && !Simulation.Statics.StaticExists(new StaticHandle(b.Handle)))
            throw new ArgumentException("a joint's static does not exist", nameof(b));

        var handleA = new BodyHandle(a.Handle);
        Simulation.Awakener.AwakenBody(handleA);   // constraints go into the active set
        var poseA = bodies[handleA].Pose;

        var slot = new JointSlot
        {
            Used = true, Kind = desc.Kind, A = handleA, StaticB = -1, LocalA = desc.AnchorA,
            BreakForce = MathF.Max(desc.BreakForce, 0f), Main = NoConstraint, Swing = NoConstraint, Twist = NoConstraint,
            EntityA = _data.BodyEntity(a.Handle, false),
        };

        // B's frame: a body as it is, or a kinematic anchor standing in for the world or a static.
        RigidPose poseB;
        if (world || b.IsStatic)
        {
            RigidPose frame = RigidPose.Identity;
            if (!world)
            {
                frame = Simulation.Statics[new StaticHandle(b.Handle)].Pose;
                slot.StaticB = b.Handle;
                slot.EntityB = _data.BodyEntity(b.Handle, true);
            }
            poseB = new RigidPose(frame.Position + Vector3.Transform(desc.AnchorB, frame.Orientation), frame.Orientation);
            slot.B = bodies.Add(BodyDescription.CreateKinematic(poseB, new CollidableDescription(default(TypedIndex), 0f),
                                                                  new BodyActivityDescription(0.01f)));
            slot.OwnsAnchor = true;
            slot.LocalB = Vector3.Zero;
        }
        else
        {
            slot.B = new BodyHandle(b.Handle);
            Simulation.Awakener.AwakenBody(slot.B);
            poseB = bodies[slot.B].Pose;
            slot.LocalB = desc.AnchorB;
            slot.EntityB = _data.BodyEntity(b.Handle, false);
        }

        // The rest pose as Bepu wants it, B's rotation in A's space: the inverse of JointDesc.Rest (A's in
        // B's), or as they are now.
        var rest = desc.Rest == default
            ? Quaternion.Inverse(poseA.Orientation) * poseB.Orientation
            : Quaternion.Inverse(desc.Rest);
        rest = Quaternion.Normalize(rest);
        var toB = Quaternion.Inverse(rest);
        var axisA = desc.Axis.LengthSquared() > 1e-12f ? Vector3.Normalize(desc.Axis) : Vector3.UnitY;
        var axisB = Vector3.Transform(axisA, toB);
        slot.AxisA = axisA;
        var basisA = BasisFor(axisA);
        var basisB = Quaternion.Normalize(toB * basisA);   // A's basis, carried through the rest pose

        var spring = new SpringSettings(desc.Stiffness > 0 ? desc.Stiffness : JointDesc.DefaultStiffness,
                                        desc.Damping > 0 ? desc.Damping : JointDesc.DefaultDamping);
        var solver = Simulation.Solver;
        switch (desc.Kind)
        {
            case JointKind.Ball:
                slot.Main = solver.Add(slot.A, slot.B, new BallSocket { LocalOffsetA = slot.LocalA, LocalOffsetB = slot.LocalB, SpringSettings = spring });
                if (desc.Swing > 0f && desc.Swing < MathF.PI)
                {
                    var swing = new SwingLimit { AxisLocalA = axisA, AxisLocalB = axisB, SpringSettings = spring };
                    swing.MaximumSwingAngle = desc.Swing;
                    slot.Swing = solver.Add(slot.A, slot.B, swing);
                }
                if (desc.TwistMin != 0f || desc.TwistMax != 0f)
                    slot.Twist = solver.Add(slot.A, slot.B, TwistFor(basisA, basisB, desc.TwistMin, desc.TwistMax, spring));
                break;

            case JointKind.Hinge:
                slot.Main = solver.Add(slot.A, slot.B, new Hinge
                {
                    LocalOffsetA = slot.LocalA, LocalHingeAxisA = axisA,
                    LocalOffsetB = slot.LocalB, LocalHingeAxisB = axisB,
                    SpringSettings = spring,
                });
                if (desc.HingeMin != 0f || desc.HingeMax != 0f)
                    slot.Twist = solver.Add(slot.A, slot.B, TwistFor(basisA, basisB, desc.HingeMin, desc.HingeMax, spring));
                break;

            case JointKind.Fixed:
                // Where the anchors meet: B's centre sits at anchorA - rest·anchorB in A's space.
                slot.Main = solver.Add(slot.A, slot.B, new Weld
                {
                    LocalOffset = slot.LocalA - Vector3.Transform(slot.LocalB, rest),
                    LocalOrientation = rest,
                    SpringSettings = spring,
                });
                break;

            case JointKind.Distance:
            {
                var worldA = poseA.Position + Vector3.Transform(slot.LocalA, poseA.Orientation);
                var worldB = poseB.Position + Vector3.Transform(slot.LocalB, poseB.Orientation);
                float max = desc.MaxDistance > 0f ? desc.MaxDistance : Vector3.Distance(worldA, worldB);
                float min = Math.Clamp(desc.MinDistance, 0f, max);
                slot.Main = solver.Add(slot.A, slot.B, new DistanceLimit(slot.LocalA, slot.LocalB, min, max, spring));
                break;
            }

            default:
                if (slot.OwnsAnchor) bodies.Remove(slot.B);
                throw new ArgumentException($"unknown joint kind {desc.Kind}", nameof(desc));
        }

        int index;
        if (_freeJoints.Count > 0) index = _freeJoints.Pop();
        else
        {
            if (_jointHigh == _joints.Length) Array.Resize(ref _joints, _joints.Length * 2);
            index = _jointHigh++;
        }
        slot.Version = _joints[index].Version + 1;
        _joints[index] = slot;
        JointCount++;
        return new PhysicsJoint(index + 1, slot.Version);
    }

    // Bepu measures B's twist relative to A; JointDesc's angles are A's relative to B, so they turn over.
    private static TwistLimit TwistFor(Quaternion basisA, Quaternion basisB, float min, float max, SpringSettings spring)
    {
        if (min > max) (min, max) = (max, min);
        return new TwistLimit { LocalBasisA = basisA, LocalBasisB = basisB, MinimumAngle = -max, MaximumAngle = -min, SpringSettings = spring };
    }

    // A rotation taking +Z to `axis`: a twist basis whose Z is the axis (TwistLimit measures about Z).
    private static Quaternion BasisFor(Vector3 axis)
    {
        float dot = axis.Z;
        if (dot > 0.99999f) return Quaternion.Identity;
        if (dot < -0.99999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        var cross = Vector3.Cross(Vector3.UnitZ, axis);
        return Quaternion.Normalize(new Quaternion(cross, 1f + dot));
    }

    // Takes a joint's constraints and anchor out of Bepu and frees its slot. The bodies are woken first:
    // a sleeping chain whose joint goes should fall.
    private void Release(int index)
    {
        ref var slot = ref _joints[index];
        var bodies = Simulation.Bodies;
        if (bodies.BodyExists(slot.A)) Simulation.Awakener.AwakenBody(slot.A);
        if (bodies.BodyExists(slot.B)) Simulation.Awakener.AwakenBody(slot.B);
        var solver = Simulation.Solver;
        if (solver.ConstraintExists(slot.Main)) solver.Remove(slot.Main);
        if (slot.Swing.Value >= 0 && solver.ConstraintExists(slot.Swing)) solver.Remove(slot.Swing);
        if (slot.Twist.Value >= 0 && solver.ConstraintExists(slot.Twist)) solver.Remove(slot.Twist);
        if (slot.OwnsAnchor && bodies.BodyExists(slot.B)) bodies.Remove(slot.B);
        slot.Used = false;
        slot.Main = slot.Swing = slot.Twist = NoConstraint;
        _freeJoints.Push(index);
        JointCount--;
    }

    // Every joint that uses `body` (as A, as B, or as the static it is anchored to).
    private void RemoveJointsOf(in PhysicsBody body)
    {
        if (JointCount == 0) return;
        for (int i = 0; i < _jointHigh; i++)
        {
            ref var slot = ref _joints[i];
            if (!slot.Used) continue;
            bool uses = body.IsStatic
                ? slot.StaticB == body.Handle
                : slot.A.Value == body.Handle || (!slot.OwnsAnchor && slot.B.Value == body.Handle);
            if (uses) Release(i);
        }
    }

    // After the step: a joint whose impulse over the step is more than its break force breaks, once.
    // Only awake joints are read; a sleeping one carries no new load.
    private void CheckBreaks(float dt)
    {
        if (JointCount == 0 || dt <= 0f) return;
        var bodies = Simulation.Bodies;
        for (int i = 0; i < _jointHigh; i++)
        {
            ref var slot = ref _joints[i];
            if (!slot.Used || slot.BreakForce <= 0f || !bodies[slot.A].Awake) continue;
            float force = Simulation.Solver.GetAccumulatedImpulseMagnitude(slot.Main) / dt;
            if (force <= slot.BreakForce) continue;
            var joint = new PhysicsJoint(i + 1, slot.Version);
            var broken = new JointBroken(joint, slot.EntityA, slot.EntityB, force);
            Release(i);
            _broken.Add(broken);
        }
    }

    // World anchors are bodies to Bepu and not to anyone else.
    private int AwakeAnchors()
    {
        if (JointCount == 0) return 0;
        int awake = 0;
        var bodies = Simulation.Bodies;
        for (int i = 0; i < _jointHigh; i++)
        {
            ref var slot = ref _joints[i];
            if (slot.Used && slot.OwnsAnchor && bodies[slot.B].Awake) awake++;
        }
        return awake;
    }

    // phys_debug: a yellow line between a joint's anchors, a cross on A's, and its axis in orange for
    // a ball or a hinge.
    private void DrawJoints(DebugDraw debug, Vector3 around, float range)
    {
        var bodies = Simulation.Bodies;
        for (int i = 0; i < _jointHigh; i++)
        {
            ref var slot = ref _joints[i];
            if (!slot.Used) continue;
            var poseA = bodies[slot.A].Pose;
            var poseB = bodies[slot.B].Pose;
            var atA = poseA.Position + Vector3.Transform(slot.LocalA, poseA.Orientation);
            if (SageMath.DistanceXZ(atA, around) > range) continue;
            var atB = poseB.Position + Vector3.Transform(slot.LocalB, poseB.Orientation);
            debug.Line(atA, atB, DebugColour.Yellow);
            debug.Cross(atA, 0.08f, DebugColour.Yellow);
            if (slot.Kind is JointKind.Ball or JointKind.Hinge)
                debug.Arrow(atA, atA + Vector3.Transform(slot.AxisA, poseA.Orientation) * 0.3f, DebugColour.Orange);
        }
    }
}
#pragma warning restore SAGE0134
