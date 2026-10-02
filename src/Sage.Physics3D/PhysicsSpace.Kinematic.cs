#nullable enable
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;

namespace Sage.Physics3D;

#pragma warning disable SAGE0134 // joints (phase 4k): this is the backend that implements them

// Moving geometry as kinematic bodies with a velocity (issue #261). A mover's brush is built as a static
// like any other; the mover turns it into a kinematic body, then drives it with a pose and the velocity
// it is moving at, so the solver sees the motion: a crate on a lift is carried by friction instead of
// sinking through a floor that teleports under it, and a character standing on one can read how fast
// its ground is going.
public sealed partial class PhysicsSpace
{
    // Shapes and hull offsets a body took over from the static it was (keyed by body handle).
    private readonly Dictionary<int, TypedIndex> _ownedBodyShapes = new();
    private readonly Dictionary<int, Vector3> _bodyHullOffsets = new();

    public PhysicsBody MakeKinematic(in PhysicsBody body)
    {
        if (!body.IsStatic) return body;
        var staticHandle = new StaticHandle(body.Handle);
        if (!Simulation.Statics.StaticExists(staticHandle)) return body;
        if (AnchorsAJoint(body.Handle)) return body;   // a joint names its static; leave it as it is

        var description = Simulation.Statics[staticHandle];
        var shape = description.Shape;
        var pose = description.Pose;
        Simulation.Statics.Remove(staticHandle);

        var handle = Simulation.Bodies.Add(BodyDescription.CreateKinematic(pose, new CollidableDescription(shape, 0.1f), new BodyActivityDescription(0.01f)));
        _data.StaticBecameBody(body.Handle, handle.Value);
        if (_ownedShapes.Remove(body.Handle, out var owned)) _ownedBodyShapes[handle.Value] = owned;
        if (_hullOffsets.Remove(body.Handle, out var offset)) _bodyHullOffsets[handle.Value] = offset;
        return new PhysicsBody { Handle = handle.Value, IsStatic = false };
    }

    public void MoveKinematic(in PhysicsBody body, Vector3 position, Vector3 velocity)
    {
        if (body.IsStatic)
        {
            MoveStatic(body, position);
            return;
        }
        var handle = new BodyHandle(body.Handle);
        if (!Simulation.Bodies.BodyExists(handle)) return;
        var reference = Simulation.Bodies[handle];
        if (!reference.Kinematic) return;
        var offset = _bodyHullOffsets.TryGetValue(body.Handle, out var centre) ? centre : Vector3.Zero;
        reference.Pose.Position = position + offset;
        reference.Velocity.Linear = velocity;
        reference.Awake = true;
        Simulation.Bodies.UpdateBounds(handle);
    }

    public void MoveKinematic(in PhysicsBody body, in Pose pose, Vector3 velocity, Vector3 angularVelocity)
    {
        var rotation = pose.Rotation.LengthSquared() < 0.5f ? Quaternion.Identity : Quaternion.Normalize(pose.Rotation);
        if (body.IsStatic)
        {
            var staticHandle = new StaticHandle(body.Handle);
            if (!Simulation.Statics.StaticExists(staticHandle)) return;
            var hull = _hullOffsets.TryGetValue(body.Handle, out var c) ? c : Vector3.Zero;
            Simulation.Statics[staticHandle].Pose = new RigidPose(pose.Position + Vector3.Transform(hull, rotation), rotation);
            Simulation.Statics.UpdateBounds(staticHandle);
            return;
        }
        var handle = new BodyHandle(body.Handle);
        if (!Simulation.Bodies.BodyExists(handle)) return;
        var reference = Simulation.Bodies[handle];
        if (!reference.Kinematic) return;

        // Bepu poses the shape's centre, which turns round the entity's origin with the body: the centre
        // moves at the origin's velocity plus the turn's, ω × r.
        var offset = _bodyHullOffsets.TryGetValue(body.Handle, out var centre) ? centre : Vector3.Zero;
        var arm = Vector3.Transform(offset, rotation);
        reference.Pose = new RigidPose(pose.Position + arm, rotation);
        reference.Velocity.Linear = velocity + Vector3.Cross(angularVelocity, arm);
        reference.Velocity.Angular = angularVelocity;
        reference.Awake = true;
        Simulation.Bodies.UpdateBounds(handle);
    }

    // How fast a point fixed to the body is moving, in the world: its centre's velocity and its turn,
    // v + ω × r. What a character standing on a turning platform or a swinging door is carried at.
    internal Vector3 PointVelocityOf(in PhysicsBody body, Vector3 point)
    {
        if (body.IsStatic) return Vector3.Zero;
        var handle = new BodyHandle(body.Handle);
        if (!Simulation.Bodies.BodyExists(handle)) return Vector3.Zero;
        var reference = Simulation.Bodies[handle];
        return reference.Velocity.Linear + Vector3.Cross(reference.Velocity.Angular, point - reference.Pose.Position);
    }

    private bool AnchorsAJoint(int staticHandle)
    {
        for (int i = 0; i < _jointHigh && JointCount > 0; i++)
            if (_joints[i].Used && _joints[i].StaticB == staticHandle) return true;
        return false;
    }

    // A body that took over a static's shape gives it back when it goes (RemoveBody).
    private void ReleaseBodyShape(int handle)
    {
        if (_ownedBodyShapes.Remove(handle, out var owned)) Simulation.Shapes.RemoveAndDispose(owned, _pool);
        _bodyHullOffsets.Remove(handle);
    }
}
#pragma warning restore SAGE0134
