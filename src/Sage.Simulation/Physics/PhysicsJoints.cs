#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// The experimental id and link of physics joints and ragdolls (phase 4k, #130; docs/MAKING_A_GAME.md
// §10b): joints and collision groups in the facade first (#242), then the `joint` part, ragdolls and
// getting up, which will reshape what is here.
internal static class PhysicsJointsApi
{
    internal const string Experimental = "SAGE0134";
    internal const string Url = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api";
}

// What a joint lets its two bodies do (docs/design/10, "As built (joints)").
[Experimental(PhysicsJointsApi.Experimental, UrlFormat = PhysicsJointsApi.Url)]
public enum JointKind
{
    Ball,       // the anchors meet; B turns freely about it, within an optional swing cone and twist range
    Hinge,      // the anchors meet; B turns only about the axis, within an optional range
    Fixed,      // B keeps its place and rotation relative to A
    Distance,   // the anchors stay between a minimum and a maximum distance apart: a rope
}

// A joint between two bodies, or between a body and the world (IPhysicsWorld.AddJoint).
//
// **Spaces.** A body's space is its *shape's* space, as IPhysicsWorld.PoseOf reports it: the origin at
// the shape's centre (Collider.Center offsets that from the entity's origin) and turned with the body. A
// joint to the world, or to a static, uses that one's space for B: world space for the world, so
// AnchorB is a world point. FromWorld fills the local fields from a world anchor and axis.
//
// **Angles** are radians: A's rotation relative to B, right-handed about the axis (a hinge spun the way
// the axis's right-hand rule turns goes towards HingeMax). They are measured from the rest pose: A's
// rotation in B's space given by Rest, or, when Rest is left at default, as they are when the joint is
// added. So a limb (A) on its parent (B) or a door (A) on the world reads naturally. A limit whose min
// and max are both 0 is off; a swing of 0 is off.
[Experimental(PhysicsJointsApi.Experimental, UrlFormat = PhysicsJointsApi.Url)]
public struct JointDesc
{
    public JointKind Kind;

    // Where the joint is: on A, in A's space; on B, in B's space (the world's for a world joint).
    public Vector3 AnchorA;
    public Vector3 AnchorB;

    // In A's space: the ball's twist axis (also the centre of its swing cone) and the hinge's axis.
    // Zero means +Y. B's axis is A's, carried through the rest pose.
    public Vector3 Axis;

    // A's rotation in B's space at rest (A = B * Rest); default (all zeroes) = as they are when added.
    public Quaternion Rest;

    public float Swing;                  // ball: the cone's half-angle around Axis; 0 = no cone
    public float TwistMin, TwistMax;     // ball: twist about Axis; both 0 = free
    public float HingeMin, HingeMax;     // hinge: the angle about Axis; both 0 = free
    public float MinDistance;            // distance: 0 or more
    public float MaxDistance;            // distance: 0 = the anchors' distance when added

    // The force (newtons; impulse over the step) above which the joint breaks and is reported once in
    // IPhysicsWorld.JointBroken. 0 = never.
    public float BreakForce;

    // How hard the joint holds: a spring's frequency (Hz, 0 = 30) and damping ratio (0 = 1, critical).
    public float Stiffness;
    public float Damping;

    public const float DefaultStiffness = 30f;
    public const float DefaultDamping = 1f;

    // A joint of `kind` at a world point and about a world axis, for bodies posed at `a` and `b` (from
    // PoseOf; Pose.Identity for the world). Limits and break force are left for the caller to set.
    public static JointDesc FromWorld(JointKind kind, in Pose a, in Pose b, Vector3 worldAnchor, Vector3 worldAxis = default)
    {
        var inverseA = Quaternion.Inverse(a.Rotation);
        var inverseB = Quaternion.Inverse(b.Rotation);
        Vector3 axis = worldAxis.LengthSquared() > 1e-12f ? Vector3.Normalize(worldAxis) : Vector3.UnitY;
        return new JointDesc
        {
            Kind = kind,
            AnchorA = Vector3.Transform(worldAnchor - a.Position, inverseA),
            AnchorB = Vector3.Transform(worldAnchor - b.Position, inverseB),
            Axis = Vector3.Transform(axis, inverseA),
            Rest = Quaternion.Normalize(inverseB * a.Rotation),
        };
    }
}

// A backend's handle for a joint. Default = none; a removed or broken joint's handle stays stale (the
// version tells it from whatever reuses the slot).
[Experimental(PhysicsJointsApi.Experimental, UrlFormat = PhysicsJointsApi.Url)]
public readonly record struct PhysicsJoint(int Id, int Version)
{
    public bool IsNull => Id == 0;
}

// A joint that broke during the last step (JointDesc.BreakForce): already removed. B is null for a
// joint to the world.
[Experimental(PhysicsJointsApi.Experimental, UrlFormat = PhysicsJointsApi.Url)]
public readonly record struct JointBroken(PhysicsJoint Joint, Entity A, Entity B, float Force);
