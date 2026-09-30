#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// Sockets and bone attachments (docs/design/12 "As built (attachments and IK)", issue #120, TODO F12):
// a sword in a hand, a helmet on a head, a rider on a saddle, a torch on a belt.
//
// **A socket** is a joint plus an offset from it, named, so content says "hand_r" rather than
// "mixamorig:RightHand plus eight centimetres": the model can be re-rigged, and every prefab that holds
// something in its right hand follows. Sockets are declared in a `skeleton_sockets` record keyed by the
// model they belong to:
//
//   { "type": "skeleton_sockets", "id": "knight", "model": "models/knight.glb",
//     "sockets": { "hand_r": { "joint": "hand.R", "offset": [0, 0.08, 0], "angles": [0, 90, 0] },
//                  "back":   { "joint": "spine.003", "offset": [0, 0.1, 0.15] } } }
//
// Why a record keyed by model rather than a field of #118's `animator` part: sockets are a fact about
// the *rig*, not about one prefab or one graph. Every prefab that uses the knight model shares them, a
// prop with a skeleton and no graph (a mannequin, first-person arms driven by code) has them too, a mod
// adds a socket with a second record for the same model (lookups search every record for it, by id)
// without patching the first, and #118's part stays #118's.
//
// **An attachment** is the `sage:bone_attachment` component (and the `bone_attachment` part) on the
// entity that follows: a socket, or a bone by name, plus an offset of its own. It follows the socket of
// its `Target` — or, when there is none, of its parent (World.SetParent), which is the usual case: the
// sword is the knight's child, so the attachment only writes the sword's local transform and the
// engine's transform propagation does the rest. AttachmentSystem runs in Phase.Late, after the IK.

// A named place on a skeleton: a joint and an offset in the joint's space.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public sealed class SkeletonSocket
{
    [Property(Tooltip = "The joint it hangs from, by name as the model writes it")]
    public string Joint = "";
    [Property(Unit = "m", Tooltip = "Where it sits in the joint's space")]
    public Vector3 Offset;
    [Property(Unit = "deg", Tooltip = "How it is turned in the joint's space: pitch, yaw, roll")]
    public Vector3 Angles;
}

// The sockets of one model (see above). Several records may name the same model; a socket is looked up
// in each, in id order.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
[Record("skeleton_sockets", Plugin = RegistrationOwners.Core)]
public sealed class SkeletonSocketsRecord
{
    [AssetKind("mesh")] public AssetPath Model;
    public Dictionary<string, SkeletonSocket> Sockets = new(StringComparer.OrdinalIgnoreCase);
}

// Follows a socket or a bone of another entity's skeleton (see above). Saved: an attached sword is
// still in the hand after a load. What it resolved to is not, and is found again on the first tick.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
[Component("sage:bone_attachment")]
public struct BoneAttachment : IComponent
{
    [Property(Tooltip = "Whose skeleton to follow; none = this entity's parent")]
    public Entity Target;
    [Property(Tooltip = "A socket of the target's model (skeleton_sockets); empty = follow `bone`")]
    public string Socket;
    [Property(Tooltip = "A joint by name, when there is no socket")]
    public string Bone;
    [Property(Unit = "m", Tooltip = "Where this sits in the socket's space")]
    public Vector3 Offset;
    [Property(Unit = "deg", Tooltip = "How this is turned in the socket's space: pitch, yaw, roll")]
    public Vector3 Angles;

    // Resolved on the first tick the target has a pose (and again if its skeleton changes): the joint,
    // or -1 when the socket or bone could not be found (said once), and the socket's offset times ours.
    internal Skeleton? ResolvedFor;
    internal int Joint;
    internal Matrix4x4 JointOffset;
}

// "bone_attachment": { "socket": "hand_r" }  or  { "bone": "hand.R", "offset": [0, 0.1, 0], "angles": [0, 0, 90] }
// Follows the socket of the entity's parent (whatever spawns the sword parents it to the hand's owner:
// BoneAttachments.Attach does both).
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
[PrefabPart("bone_attachment", Plugin = RegistrationOwners.Core, Shorthand = nameof(Socket))]
public sealed class BoneAttachmentPart : IPrefabPart
{
    [Property(Tooltip = "A socket of the parent's model (skeleton_sockets); empty = follow `bone`")]
    public string Socket = "";
    [Property(Tooltip = "A joint by name, when there is no socket")]
    public string Bone = "";
    [Property(Unit = "m", Tooltip = "Where this sits in the socket's space")]
    public Vector3 Offset;
    [Property(Unit = "deg", Tooltip = "How this is turned in the socket's space: pitch, yaw, roll")]
    public Vector3 Angles;

    public void Apply(in PrefabPartContext ctx)
    {
        if (string.IsNullOrWhiteSpace(Socket) && string.IsNullOrWhiteSpace(Bone))
        {
            ctx.Error("names neither a socket nor a bone to follow");
            return;
        }
        ctx.World.Add(ctx.Entity, new BoneAttachment { Socket = Socket, Bone = Bone, Offset = Offset, Angles = Angles });
    }
}

// Attaching from code, and looking a socket up.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public static class BoneAttachments
{
    // Parents `child` to `owner` and makes it follow `socket` of the owner's skeleton (a socket of the
    // owner's model; see SkeletonSocketsRecord). Replaces an attachment the child already had.
    public static void Attach(World world, Entity child, Entity owner, string socket, Vector3 offset = default, Vector3 angles = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.SetParent(child, owner);
        child.AddComponent(new BoneAttachment { Socket = socket ?? "", Bone = "", Offset = offset, Angles = angles });
    }

    // The same, following a joint by name rather than a socket.
    public static void AttachToBone(World world, Entity child, Entity owner, string bone, Vector3 offset = default, Vector3 angles = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.SetParent(child, owner);
        child.AddComponent(new BoneAttachment { Socket = "", Bone = bone ?? "", Offset = offset, Angles = angles });
    }

    // The socket `name` of `model`, searching every skeleton_sockets record for that model in id order.
    // Allocates (it walks the records): call it when resolving, never per tick.
    public static bool TryFindSocket(RecordStore records, AssetPath model, string name, [NotNullWhen(true)] out SkeletonSocket? socket)
    {
        ArgumentNullException.ThrowIfNull(records);
        socket = null;
        if (model.IsEmpty || string.IsNullOrEmpty(name)) return false;
        foreach (var record in records.All<SkeletonSocketsRecord>())
            if (record.Model == model && record.Sockets.TryGetValue(name, out var found) && found != null)
            {
                socket = found;
                return true;
            }
        return false;
    }

    // A socket's (or an attachment's) offset as a matrix in the joint's space.
    public static Matrix4x4 OffsetMatrix(Vector3 offset, Vector3 angles)
    {
        const float ToRadians = MathF.PI / 180f;
        var pose = new Pose
        {
            Position = offset,
            Rotation = Quaternion.CreateFromYawPitchRoll(angles.Y * ToRadians, angles.X * ToRadians, angles.Z * ToRadians),
            Scale = Vector3.One,
        };
        return PoseSampler.Compose(in pose);
    }

    // Content checks (issue #22's rule: a mistake is a load error, at its line).
    internal static void Check(SkeletonSocketsRecord record, RecordCheck check)
    {
        if (record.Model.IsEmpty) check.Error("model", "names no model: sockets are looked up by the model their skeleton comes from");
        foreach (var (name, socket) in record.Sockets)
        {
            if (socket == null) { check.Error($"sockets.{name}", "is empty"); continue; }
            if (string.IsNullOrWhiteSpace(socket.Joint)) check.Error($"sockets.{name}.joint", "names no joint");
        }
    }

    // An entity's world pose from its transform and its parents', as of now (GlobalTransform is only
    // brought up to date at the end of PostPhysics and Late).
    internal static Pose WorldPose(Entity entity)
    {
        var local = entity.TryGetComponent<Transform>(out var t) ? Pose.FromLocal(t) : Pose.Identity;
        var parent = entity.Parent;
        return parent.IsNull ? local : Pose.Combine(WorldPose(parent), local);
    }
}

// Late phase, after the IK: moves every attachment to its socket. Allocation-free once resolved.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
[System(Id, Phase.Late, After = new[] { AimIkSystem.Id, "?sage.animation.foot_ik" })]
internal sealed class AttachmentSystem : ISystem
{
    public const string Id = "sage.animation.attachments";

    private readonly World _world;
    private readonly Query<BoneAttachment, Transform> _attached;

    public AttachmentSystem(World world)
    {
        _world = world;
        _attached = world.Query<BoneAttachment, Transform>();
    }

    public void Run(in SystemContext ctx)
    {
        if (!_world.Resources.TryGet<SkeletonPoses>(out var poses) || poses == null || poses.Count == 0) return;
        foreach (var (attachments, transforms, entities) in _attached.Chunks)
        {
            var a = attachments.Span;
            var t = transforms.Span;
            for (int n = 0; n < a.Length; n++)
            {
                ref var attachment = ref a[n];
                var entity = entities.EntityAt(n);
                var parent = entity.Parent;
                var owner = attachment.Target.IsNull ? parent : attachment.Target;
                if (owner.IsNull || !poses.TryGet(owner, out var pose, out var model)) continue;
                if (!ReferenceEquals(attachment.ResolvedFor, pose.Skeleton)) Resolve(ref attachment, pose.Skeleton, model, entity, owner);
                if (attachment.Joint < 0) continue;

                // In the owner's space: the attachment's offset, the socket's, then the joint's pose.
                var inOwner = attachment.JointOffset * pose.ModelSpace[attachment.Joint];
                Matrix4x4 local;
                if (owner == parent) local = inOwner;
                else
                {
                    var world = inOwner * BoneAttachments.WorldPose(owner).ToMatrix();
                    if (parent.IsNull) local = world;
                    else if (Matrix4x4.Invert(BoneAttachments.WorldPose(parent).ToMatrix(), out var toParent)) local = world * toParent;
                    else continue;
                }
                var p = PoseMath.ToPose(in local);
                ref var transform = ref t[n];
                transform.LocalPosition = p.Position;
                transform.LocalRotation = p.Rotation;
                transform.LocalScale = p.Scale;
            }
        }
    }

    private void Resolve(ref BoneAttachment attachment, Skeleton skeleton, AssetPath model, Entity entity, Entity owner)
    {
        attachment.ResolvedFor = skeleton;
        attachment.Joint = -1;
        attachment.JointOffset = Matrix4x4.Identity;

        string? joint;
        var socketOffset = Matrix4x4.Identity;
        if (!string.IsNullOrEmpty(attachment.Socket))
        {
            if (_world.Engine is not { } engine || !BoneAttachments.TryFindSocket(engine.Records, model, attachment.Socket, out var socket))
            {
                Log.Warn(LogCat.Animation, $"{World.Describe(entity)}: no socket '{attachment.Socket}' on {World.Describe(owner)}'s model " +
                                           $"'{(model.IsEmpty ? "(none)" : model.Path.ToString())}' (skeleton_sockets); it stays where it is");
                return;
            }
            joint = socket.Joint;
            socketOffset = BoneAttachments.OffsetMatrix(socket.Offset, socket.Angles);
        }
        else joint = attachment.Bone;

        int index = string.IsNullOrEmpty(joint) ? -1 : skeleton.IndexOf(joint);
        if (index < 0)
        {
            Log.Warn(LogCat.Animation, $"{World.Describe(entity)}: {World.Describe(owner)}'s skeleton has no joint '{joint}'; it stays where it is");
            return;
        }
        attachment.Joint = index;
        attachment.JointOffset = BoneAttachments.OffsetMatrix(attachment.Offset, attachment.Angles) * socketOffset;
    }
}
