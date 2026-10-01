#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Physics3D;

#pragma warning disable SAGE0134 // the joint part's systems: the experimental joints through the facade

// The joint part (issue #245, docs/design/10 "As built (the joint part)"): makes each Joint's facade
// joint once both its bodies exist, and again after a load; notices one that went with a body.
[System("sage.physics.joints", Phase.PrePhysics, After = new[] { "sage.physics.sync" })]
internal sealed class JointSystem : ISystem
{
    private const float Degree = MathF.PI / 180f;

    private readonly IPhysicsWorld _space;
    private readonly Query<Joint, PhysicsBody> _joints;
    private readonly List<Entity> _work = new();

    public JointSystem(World world, IPhysicsWorld space)
    {
        _space = space;
        _joints = world.Query<Joint, PhysicsBody>();
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        _work.Clear();
        foreach (var (joints, _, entities) in _joints.Chunks)
        {
            var j = joints.Span;
            for (int n = 0; n < j.Length; n++)
                if (!j[n].Broken) _work.Add(entities.EntityAt(n));
        }

        foreach (var entity in _work)
        {
            ref var joint = ref world.Get<Joint>(entity);
            if (!joint.Handle.IsNull)
            {
                if (_space.JointExists(joint.Handle))
                {
                    Drag(entity, in joint, ctx.Tick.Dt);
                    continue;
                }
                // The target went (a body takes its joints with it): there is nothing left to hold.
                joint.Handle = default;
                joint.Broken = true;
                Log.Warn(LogCat.Physics, $"Joint at {World.Describe(entity)}: its target is gone; the joint is gone with it");
                continue;
            }
            Make(world, entity, ref joint);
        }
    }

    // Air drag on a jointed body: a swing with nothing to lose energy to never stops. Only while it is
    // awake and moving, so the body can still go to sleep once it is slow enough.
    private void Drag(Entity entity, in Joint joint, float dt)
    {
        if (joint.Drag <= 0f) return;
        var body = entity.GetComponent<PhysicsBody>();
        if (!_space.IsAwake(body)) return;
        float keep = MathF.Exp(-joint.Drag * dt);
        var linear = _space.VelocityOf(body);
        var angular = _space.AngularVelocityOf(body);
        if (linear.LengthSquared() + angular.LengthSquared() < 1e-6f) return;
        _space.SetVelocity(body, linear * keep);
        _space.SetAngularVelocity(body, angular * keep);
    }

    private void Make(World world, Entity entity, ref Joint joint)
    {
        var body = world.Get<PhysicsBody>(entity);
        if (body.IsStatic || !_space.IsDynamic(body))
        {
            if (!joint.Warned) Log.Error(LogCat.Physics, $"Joint at {World.Describe(entity)}: only a dynamic body can have a joint (give its body a mass)");
            joint.Warned = true;
            return;
        }

        // The target: named, else the parent when it has a body, else the world.
        Entity target = default;
        bool toWorld = false;
        if (joint.Target.Length > 0)
        {
            target = world.FindByName(joint.Target);
            if (target.IsNull || !world.Has<PhysicsBody>(target) || target == entity)
            {
                if (!joint.Warned) Log.Warn(LogCat.Physics, $"Joint at {World.Describe(entity)}: no body named '{joint.Target}' (yet); waiting for it");
                joint.Warned = true;
                return;
            }
        }
        else
        {
            var parent = entity.Parent;
            if (!parent.IsNull && world.Has<PhysicsBody>(parent)) target = parent;
            else toWorld = true;
        }

        var centreA = world.Get<Collider>(entity).Center;
        var poseA = _space.PoseOf(body);
        PhysicsBody targetBody = default;
        Pose poseB = Pose.Identity;
        Vector3 centreB = Vector3.Zero;
        if (!toWorld)
        {
            targetBody = world.Get<PhysicsBody>(target);
            poseB = _space.PoseOf(targetBody);
            centreB = world.Get<Collider>(target).Center;
        }

        // The anchor on this entity, in the shape's space (the entity's origin is Collider.Center away).
        var anchorA = joint.Anchor - centreA;
        JointDesc desc;
        if (joint.Built)
        {
            // From a save: the joint as it was made, not as the swing has left the bodies.
            desc = new JointDesc { Kind = joint.Kind, AnchorB = joint.BuiltAnchor, Rest = joint.Rest };
        }
        else
        {
            var worldAnchor = poseA.Position + Vector3.Transform(anchorA, poseA.Rotation);
            desc = JointDesc.FromWorld(joint.Kind, poseA, poseB, worldAnchor);
            if (joint.HasTargetAnchor) desc.AnchorB = joint.TargetAnchor - centreB;
            joint.Built = true;
            joint.BuiltAnchor = desc.AnchorB;
            joint.Rest = desc.Rest;
        }

        desc.Kind = joint.Kind;
        desc.AnchorA = anchorA;
        desc.Axis = joint.Axis;
        desc.Swing = joint.Swing * Degree;
        desc.TwistMin = joint.TwistMin * Degree;
        desc.TwistMax = joint.TwistMax * Degree;
        desc.HingeMin = joint.Min * Degree;
        desc.HingeMax = joint.Max * Degree;
        desc.MinDistance = joint.MinDistance;
        desc.MaxDistance = joint.MaxDistance;
        desc.BreakForce = joint.BreakForce;

        try
        {
            joint.Handle = toWorld ? _space.AddJoint(body, desc) : _space.AddJoint(body, targetBody, desc);
        }
        catch (ArgumentException ex)
        {
            Log.Error(LogCat.Physics, $"Joint at {World.Describe(entity)}: {ex.Message}");
            joint.Broken = true;
        }
    }
}

// PostPhysics: a joint that broke in the step says so on its entity (OnBreak), once, and stays broken.
[System("sage.physics.joint_breaks", Phase.PostPhysics, After = new[] { "sage.physics.write_back" })]
internal sealed class JointBreakSystem : ISystem
{
    private readonly IPhysicsWorld _space;

    public JointBreakSystem(IPhysicsWorld space) { _space = space; }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        foreach (var broken in _space.JointBroken)
        {
            if (!world.IsAlive(broken.A) || !world.TryGet<Joint>(broken.A, out var joint) || joint.Handle != broken.Joint) continue;
            ref var j = ref world.Get<Joint>(broken.A);
            j.Broken = true;
            j.Handle = default;
            var activator = !broken.B.IsNull && world.IsAlive(broken.B) ? broken.B : default;
            world.IO().Fire(world, broken.A, PhysicsJointIO.OnBreak, activator);
        }
    }
}
