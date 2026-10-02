#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// Joints in data (issue #245, phase 4k; docs/design/10 "As built (the joint part)"): a sign on a post, a
// lamp on a chain, a rope. The facade's joints (PhysicsJoints.cs) are the mechanism; this is how a
// prefab asks for one, and what a save keeps of it.

// Wires of a joint (entity I/O): `Break` removes the joint, and `OnBreak` says it went, whether by the
// input or by its break force.
[Experimental(PhysicsJointsApi.Experimental, UrlFormat = PhysicsJointsApi.Url)]
public static class PhysicsJointIO
{
    public const string Break = "Break";
    public const string OnBreak = "OnBreak";
}

// A joint from this entity (a dynamic body) to another entity's body, or to the world. The system that
// makes it (sage.physics.joints) waits until both bodies exist, so a load builds it again from this
// (the bodies come back first). Angles are degrees here and in data; the facade takes radians.
//
// Spaces: Anchor is in this entity's space (its origin, turned with it); TargetAnchor is in the target's
// (a world point when the target is the world). Left out, the target's anchor is the same point as
// this one's where they stand when the joint is made, which is how a sign is hung from a post: put the
// sign where it hangs. A joint's rest pose, the angles' zero, is how they stand then.
[Experimental(PhysicsJointsApi.Experimental, UrlFormat = PhysicsJointsApi.Url)]
[Component("sage:joint")]
public struct Joint : IComponent
{
    [Property(Category = "Joint", Tooltip = "Ball, Hinge, Fixed or Distance")]
    public JointKind Kind;
    [Property(Category = "Joint", Tooltip = "The entity it is joined to, by name; empty = the parent if it has a body, else the world")]
    public string Target;
    [Property(Category = "Joint", Unit = "m", Tooltip = "Where the joint is, in this entity's space")]
    public Vector3 Anchor;
    [Property(Category = "Joint", Unit = "m", Tooltip = "Where it is on the target (the world, for a joint to the world); unused unless Has Target Anchor")]
    public Vector3 TargetAnchor;
    [Property(Category = "Joint", Tooltip = "TargetAnchor is given; otherwise it is the same point as Anchor, where they stand when the joint is made")]
    public bool HasTargetAnchor;
    [Property(Category = "Joint", Tooltip = "Ball twist axis and hinge axis, in this entity's space; zero = up (+Y)")]
    public Vector3 Axis;
    [Property(Category = "Ball", Min = 0, Max = 179, Unit = "°", Tooltip = "Ball: half-angle of the swing cone around the axis; 0 = no cone")]
    public float Swing;
    [Property(Category = "Ball", Min = -180, Max = 180, Unit = "°", Tooltip = "Ball: twist about the axis; both 0 = free")]
    public float TwistMin;
    [Property(Category = "Ball", Min = -180, Max = 180, Unit = "°", Tooltip = "Ball: twist about the axis; both 0 = free")]
    public float TwistMax;
    [Property(Category = "Hinge", Min = -180, Max = 180, Unit = "°", Tooltip = "Hinge: the angle from rest about the axis; both 0 = free")]
    public float Min;
    [Property(Category = "Hinge", Min = -180, Max = 180, Unit = "°", Tooltip = "Hinge: the angle from rest about the axis; both 0 = free")]
    public float Max;
    [Property(Category = "Distance", Min = 0, Unit = "m", Tooltip = "Distance: the least the anchors may be apart")]
    public float MinDistance;
    [Property(Category = "Distance", Min = 0, Unit = "m", Tooltip = "Distance: the most the anchors may be apart; 0 = how far apart they are when it is made")]
    public float MaxDistance;
    [Property(Min = 0, Unit = "N", Tooltip = "The force above which the joint breaks; 0 = never")]
    public float BreakForce;
    [Property(Min = 0, Unit = "1/s", Tooltip = "Air drag: the share of this body's speed (and spin) it loses each second, so a swing settles; 0 = none")]
    public float Drag;

    // Saved. A broken joint stays broken through a save and load.
    [Property(Category = "State", Tooltip = "The joint has broken (or been broken by the Break input) and is gone for good")]
    public bool Broken;
    // Saved: how the joint was made, so a load makes the same one — the angles still measured from the
    // original rest pose and a world pivot where it was, not where the swing had got to.
    [Property(Category = "State", Tooltip = "The joint was made; the next two are how")]
    public bool Built;
    [Property(Category = "State", Tooltip = "The anchor in the target's body space, as made")]
    public Vector3 BuiltAnchor;
    [Property(Category = "State", Tooltip = "This entity's rotation in the target's space at rest, as made")]
    public Quaternion Rest;

    // The facade's handle for it, made by the system and gone with the session.
    [Transient] public PhysicsJoint Handle;
    [Transient] public bool Warned;
}

// What a dynamic body's velocity was when last it was written back, so a save keeps a swinging sign
// swinging (issue #245). Only written: a body created from a load takes it as its velocity. Added to
// every dynamic body by the physics sync; read by nothing else.
[Experimental(PhysicsJointsApi.Experimental, UrlFormat = PhysicsJointsApi.Url)]
[Component("sage:body_motion")]
public struct BodyMotion : IComponent
{
    [Property(Unit = "m/s", Tooltip = "Linear velocity at the last step")]
    public Vector3 Linear;
    [Property(Unit = "rad/s", Tooltip = "Angular velocity at the last step, about the world axes")]
    public Vector3 Angular;
}

// "joint": { "kind": "Hinge", "target": "post", "anchor": [0, 0.25, 0], "axis": [1, 0, 0], "min": -80, "max": 80 }
// "joint": { "kind": "Distance", "target": "beam", "anchor": [0, 0.15, 0], "targetAnchor": [0, -0.1, 0], "maxDistance": 1 }
//
// A joint from this entity, which has to be a dynamic body (give it a `body` with a mass), to the entity
// the target names, or to its parent, or to the world. See Joint for the spaces. An entity with a joint
// should be a root: a dynamic body's pose is written to its local transform.
[Experimental(PhysicsJointsApi.Experimental, UrlFormat = PhysicsJointsApi.Url)]
[PrefabPart("joint", Plugin = "sage.physics3d")]
public sealed class JointPart : IPrefabPart
{
    [Property(Category = "Joint", Tooltip = "Ball, Hinge, Fixed or Distance")]
    public JointKind Kind = JointKind.Ball;
    [Property(Category = "Joint", Tooltip = "The entity it is joined to, by name; empty = the parent if it has a body, else the world")]
    public string Target = "";
    [Property(Category = "Joint", Unit = "m", Tooltip = "Where the joint is, in this entity's space")]
    public Vector3 Anchor;
    [Property(Category = "Joint", Unit = "m", Tooltip = "Where it is on the target (a world point for the world); left out = the same point as the anchor, where they stand when the joint is made")]
    public Vector3 TargetAnchor;
    [Property(Category = "Joint", Tooltip = "Ball twist axis and hinge axis, in this entity's space; left out = up (+Y)")]
    public Vector3 Axis;
    [Property(Category = "Ball", Min = 0, Max = 179, Unit = "°", Tooltip = "Ball: half-angle of the swing cone around the axis; 0 = no cone")]
    public float Swing;
    [Property(Category = "Ball", Min = -180, Max = 180, Unit = "°", Tooltip = "Ball: twist about the axis; both 0 = free")]
    public float TwistMin;
    [Property(Category = "Ball", Min = -180, Max = 180, Unit = "°", Tooltip = "Ball: twist about the axis; both 0 = free")]
    public float TwistMax;
    [Property(Category = "Hinge", Min = -180, Max = 180, Unit = "°", Tooltip = "Hinge: the angle from rest about the axis; both 0 = free")]
    public float Min;
    [Property(Category = "Hinge", Min = -180, Max = 180, Unit = "°", Tooltip = "Hinge: the angle from rest about the axis; both 0 = free")]
    public float Max;
    [Property(Category = "Distance", Min = 0, Unit = "m", Tooltip = "Distance: the least the anchors may be apart")]
    public float MinDistance;
    [Property(Category = "Distance", Min = 0, Unit = "m", Tooltip = "Distance: the most the anchors may be apart; 0 = how far apart they are when it is made")]
    public float MaxDistance;
    [Property(Min = 0, Unit = "N", Tooltip = "The force above which the joint breaks; 0 = never")]
    public float BreakForce;
    [Property(Min = 0, Unit = "1/s", Tooltip = "Air drag: the share of the body's speed (and spin) it loses each second, so a swing settles; 0 = none")]
    public float Drag = 0.5f;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Min > Max) { ctx.Error($"a joint's \"min\" ({Min}) is above its \"max\" ({Max})"); return; }
        if (TwistMin > TwistMax) { ctx.Error($"a joint's \"twistMin\" ({TwistMin}) is above its \"twistMax\" ({TwistMax})"); return; }
        if (MaxDistance != 0f && MinDistance > MaxDistance) { ctx.Error($"a joint's \"minDistance\" ({MinDistance}) is above its \"maxDistance\" ({MaxDistance})"); return; }
        if (Drag < 0f) { ctx.Error("a joint's \"drag\" cannot be negative"); return; }
        if (BreakForce < 0f) { ctx.Error("a joint's \"breakForce\" cannot be negative"); return; }

        // Given when it is written, even as zero (the target's centre); left out, it is inferred.
        bool given = TargetAnchor != default
            || (ctx.Options is System.Text.Json.Nodes.JsonObject written
                && System.Linq.Enumerable.Any(written, kv => string.Equals(kv.Key, "targetAnchor", StringComparison.OrdinalIgnoreCase)));

        ctx.World.Add(ctx.Entity, new Joint
        {
            Kind = Kind, Target = Target ?? "", Anchor = Anchor,
            TargetAnchor = TargetAnchor, HasTargetAnchor = given,
            Axis = Axis, Swing = Swing, TwistMin = TwistMin, TwistMax = TwistMax, Min = Min, Max = Max,
            MinDistance = MinDistance, MaxDistance = MaxDistance, BreakForce = BreakForce, Drag = Drag,
        });
    }
}
