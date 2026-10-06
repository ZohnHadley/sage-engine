#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// Look-at IK (docs/design/12 "As built (attachments and IK)", issue #361): a head — and the neck and
// spine under it, each by its share and within its limits — turns toward something in the world: the
// player an NPC is talking to, a noise a guard heard, a door that opened. It is aim IK (AimChainIk)
// whose pitch and yaw come from a point rather than from gameplay: the direction from the eye joint to
// the target, in the character's own space.
//
//   part "look_at_ik": { "joints": [ { "joint": "neck", "weight": 0.4, "pitchLimit": 30, "yawLimit": 40 },
//                                    { "joint": "head", "weight": 0.6, "pitchLimit": 40, "yawLimit": 50 } ],
//                        "eye": "head", "maxAngle": 120, "smoothing": 0.15, "weight": 1 }
//
// Gameplay points it with `Target` (an entity, plus `Offset` in its space: eye height on a person) or,
// with no target, at `Point` in the world when `HasPoint`. With neither, or a target further round than
// `maxAngle` from the character's forward (behind it), the head eases back to the animation's. The
// limits clamp: a target off to the side gets the head as far round as its joints allow, no further
// (test: ALookAtTargetBehindTheShoulderIsClampedToTheChainsLimits).
//
// Content, not state: the component is [Transient] and the part puts it back when a load respawns the
// prefab; whoever points it points it again.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
[Transient]
[Component("sage:look_at_ik")]
public struct LookAtIk : IComponent
{
    [Property(Tooltip = "What to look at; none = `point`, when it has one")]
    public Entity Target;
    [Property(Unit = "m", Tooltip = "Where on the target to look, in its space (eye height on a person)")]
    public Vector3 Offset;
    [Property(Unit = "m", Tooltip = "A point in the world to look at when there is no target")]
    public Vector3 Point;
    [Property(Tooltip = "Whether `point` is set (with no target and no point, the head looks ahead)")]
    public bool HasPoint;
    [Property(Min = 0, Max = 1, Tooltip = "How much of the look to apply over the animation")]
    public float Weight;
    [Property(Min = 0, Max = 180, Unit = "deg", Tooltip = "A target further round than this from the character's forward is ignored (it looks ahead)")]
    public float MaxAngle;
    [Property(Min = 0, Unit = "s", Tooltip = "How long the head takes to turn to a new look (about two thirds of the way); 0 = at once")]
    public float Smoothing;
    // The chain, hips first, as content wrote it, and the joint the look is measured from (empty: the
    // chain's last joint).
    public List<AimIkJoint>? Joints;
    public string Eye;

    // What the last tick did, for debugging and tests: the look applied (radians, before each joint's
    // limits; the chain clamps it) and whether there was something to look at within `maxAngle`.
    public float Pitch;
    public float Yaw;
    public bool Looking;

    internal Skeleton? ResolvedFor;
    internal AimJoint[]? Chain;
    internal int EyeJoint;
    internal bool Started;
}

[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
[PrefabPart("look_at_ik", Plugin = RegistrationOwners.Core)]
public sealed class LookAtIkPart : IPrefabPart
{
    [Property(Tooltip = "The joints that turn, from the hips up (the neck and head, usually)")]
    public List<AimIkJoint> Joints = new();
    [Property(Tooltip = "The joint the look is measured from; empty = the last joint")]
    public string Eye = "";
    [Property(Min = 0, Max = 1, Tooltip = "How much of the look to apply over the animation")]
    public float Weight = 1f;
    [Property(Min = 0, Max = 180, Unit = "deg", Tooltip = "A target further round than this from the character's forward is ignored (it looks ahead)")]
    public float MaxAngle = 120f;
    [Property(Min = 0, Unit = "s", Tooltip = "How long the head takes to turn to a new look (about two thirds of the way); 0 = at once")]
    public float Smoothing = 0.15f;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Joints.Count == 0)
        {
            ctx.Error("lists no joints to turn");
            return;
        }
        for (int i = 0; i < Joints.Count; i++)
            if (Joints[i] == null || string.IsNullOrWhiteSpace(Joints[i].Joint))
            {
                ctx.Error($"joints[{i}] names no joint");
                return;
            }
        ctx.World.Add(ctx.Entity, new LookAtIk
        {
            Joints = Joints,
            Eye = Eye ?? "",
            Weight = Math.Clamp(Weight, 0f, 1f),
            MaxAngle = float.IsFinite(MaxAngle) ? Math.Clamp(MaxAngle, 0f, 180f) : 180f,
            Smoothing = float.IsFinite(Smoothing) ? MathF.Max(0f, Smoothing) : 0f,
        });
    }
}

// Late phase, after aim IK (a look turns the head on top of where the aim left the spine) and before
// hand IK and attachments. Allocation-free once resolved.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
[System(Id, Phase.Late, After = new[] { AimIkSystem.Id, "?sage.animation.foot_ik" })]
internal sealed class LookAtIkSystem : ISystem
{
    public const string Id = "sage.animation.look_at_ik";

    private readonly World _world;
    private readonly Query<LookAtIk> _looking;

    public LookAtIkSystem(World world)
    {
        _world = world;
        _looking = world.Query<LookAtIk>().WithoutAnyTags(Tags.Get<Ragdolled>());   // a corpse looks at nothing
    }

    public void Run(in SystemContext ctx)
    {
        if (!_world.Resources.TryGet<SkeletonPoses>(out var poses) || poses == null || poses.Count == 0) return;
        foreach (var (looks, entities) in _looking.Chunks)
        {
            var span = looks.Span;
            for (int n = 0; n < span.Length; n++)
            {
                ref var look = ref span[n];
                var entity = entities.EntityAt(n);
                if (!poses.TryGet(entity, out var pose)) continue;
                if (!ReferenceEquals(look.ResolvedFor, pose.Skeleton)) Resolve(ref look, pose.Skeleton, entity);
                if (look.Chain == null || look.Chain.Length == 0) continue;
                Look(ref look, pose, entity, _world, ctx.Tick.Dt);
            }
        }
    }

    internal static void Look(ref LookAtIk look, SkeletonPose pose, Entity entity, World world, float dt)
    {
        // Where to look, as a pitch and a yaw from the character's forward (-Z), measured from the eye.
        float pitch = 0f, yaw = 0f;
        look.Looking = false;
        if (TryTarget(in look, world, out var target))
        {
            var toModel = BoneAttachments.WorldPose(entity).ToMatrix();
            if (Matrix4x4.Invert(toModel, out var inverse))
            {
                var eye = pose.ModelSpace[look.EyeJoint].Translation;
                var direction = Vector3.Transform(target, inverse) - eye;
                float length = direction.Length();
                if (length > 1e-4f)
                {
                    direction /= length;
                    float off = MathF.Acos(Math.Clamp(-direction.Z, -1f, 1f));   // from forward, any way round
                    float maxAngle = float.IsFinite(look.MaxAngle) ? Math.Clamp(look.MaxAngle, 0f, 180f) : 180f;
                    if (off <= maxAngle * (MathF.PI / 180f) + 1e-5f)
                    {
                        pitch = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f));
                        yaw = MathF.Atan2(-direction.X, -direction.Z);
                        look.Looking = true;
                    }
                }
            }
        }

        // Eased toward it (from the first tick's look at once).
        float tau = float.IsFinite(look.Smoothing) ? look.Smoothing : 0f;
        if (look.Started && tau > 0f && dt > 0f)
        {
            float k = 1f - MathF.Exp(-dt / tau);
            pitch = look.Pitch + (pitch - look.Pitch) * k;
            yaw = look.Yaw + (yaw - look.Yaw) * k;
        }
        look.Started = true;
        look.Pitch = pitch;
        look.Yaw = yaw;
        if (pitch == 0f && yaw == 0f) return;
        AimChainIk.Solve(pose, look.Chain, pitch, yaw, look.Weight);
    }

    private static bool TryTarget(in LookAtIk look, World world, out Vector3 target)
    {
        if (world.IsAlive(look.Target))
        {
            target = Vector3.Transform(look.Offset, BoneAttachments.WorldPose(look.Target).ToMatrix());
            return true;
        }
        target = look.Point;
        return look.HasPoint;
    }

    // Names to indices, once per skeleton. A joint the skeleton lacks is said once and left out.
    internal static void Resolve(ref LookAtIk look, Skeleton skeleton, Entity entity)
    {
        const float ToRadians = MathF.PI / 180f;
        look.ResolvedFor = skeleton;
        look.EyeJoint = -1;
        var joints = look.Joints;
        if (joints == null || joints.Count == 0) { look.Chain = Array.Empty<AimJoint>(); return; }
        var chain = new List<AimJoint>(joints.Count);
        foreach (var joint in joints)
        {
            if (joint == null) continue;
            int index = skeleton.IndexOf(joint.Joint);
            if (index < 0)
            {
                Log.Warn(LogCat.Animation, $"{World.Describe(entity)}: look_at_ik names joint '{joint.Joint}', which its skeleton does not have");
                continue;
            }
            chain.Add(new AimJoint(index, joint.Weight, joint.PitchLimit * ToRadians, joint.YawLimit * ToRadians));
        }
        look.Chain = chain.ToArray();
        if (look.Chain.Length == 0) return;
        look.EyeJoint = look.Chain[^1].Joint;
        if (!string.IsNullOrEmpty(look.Eye))
        {
            int eye = skeleton.IndexOf(look.Eye);
            if (eye >= 0) look.EyeJoint = eye;
            else Log.Warn(LogCat.Animation, $"{World.Describe(entity)}: look_at_ik eye names joint '{look.Eye}', which its skeleton does not have; it looks from '{skeleton.NameOf(look.EyeJoint)}'");
        }
    }
}
