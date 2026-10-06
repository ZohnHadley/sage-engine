#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Gameplay;

// Foot IK (docs/design/12 "As built (attachments and IK)", issue #120): feet planted on the ground the
// physics world actually has — a ramp, a stair, a rock — rather than on the flat floor the clip was
// animated on. Every tick, in Phase.Late, after the animation graph's blend:
//
//   1. a ray from above each foot down (IPhysicsWorld.Raycast, the facade: any backend) finds the
//      ground under it; the foot's target is that point plus `footHeight` (the ankle above the sole);
//   2. the pelvis drops by whatever the lower foot needs (never more than `maxPelvisDrop`, never up), so
//      the leg on the low side can reach. With `pelvisSmoothing` (seconds, issue #361) the drop eases
//      toward that rather than jumping to it, and a sudden step of the entity itself (a character
//      controller stepping up a stair) is taken out of it first, so the hips glide up a staircase
//      instead of popping a step at a time;
//   3. each leg is a two-bone chain (hip, knee, foot) solved onto its target (TwoBoneIk), bending
//      forward, with the foot keeping the rotation the animation gave it;
//   4. each planted foot then tilts to the ground's normal (issue #361), at most `maxFootTilt` degrees,
//      so a sole lies along a ramp or a rock rather than level through it.
//
// A foot whose ray finds nothing keeps the animated pose. The pose is the one the entity registered with
// SkeletonPoses; the entity itself is ignored by the rays (its own capsule is not the ground).
//
//   part "foot_ik": { "pelvis": "pelvis",
//                     "left":  { "hip": "thigh.L", "knee": "shin.L", "foot": "foot.L" },
//                     "right": { "hip": "thigh.R", "knee": "shin.R", "foot": "foot.R" },
//                     "footHeight": 0.08, "rayAbove": 0.5, "rayBelow": 0.5, "maxPelvisDrop": 0.4, "weight": 1,
//                     "maxFootTilt": 30, "pelvisSmoothing": 0.1 }
//
// Content, not state: the component is [Transient], and the part puts it back when a load respawns the
// prefab.

// One leg, by joint names as the model writes them.
[Experimental("SAGE0126", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // skeletal animation (#120)
public sealed class FootIkLeg
{
    [Property(Tooltip = "The thigh (the chain's root)")]
    public string Hip = "";
    [Property(Tooltip = "The shin (where the leg bends)")]
    public string Knee = "";
    [Property(Tooltip = "The ankle (what is placed on the ground)")]
    public string Foot = "";
}

[Experimental("SAGE0126", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // skeletal animation (#120)
[Transient]
[Component("sage:foot_ik")]
public struct FootIk : IComponent
{
    [Property(Tooltip = "The joint lowered so both feet reach (usually the hips); empty = none")]
    public string Pelvis;
    public FootIkLeg? Left;
    public FootIkLeg? Right;
    [Property(Min = 0, Unit = "m", Tooltip = "How far the ankle joint sits above the sole")]
    public float FootHeight;
    [Property(Min = 0, Unit = "m", Tooltip = "The ray starts this far above the animated foot (how high a step it finds)")]
    public float RayAbove;
    [Property(Min = 0, Unit = "m", Tooltip = "And looks this far below it (how deep a dip it reaches into)")]
    public float RayBelow;
    [Property(Min = 0, Unit = "m", Tooltip = "The most the pelvis may drop")]
    public float MaxPelvisDrop;
    [Property(Min = 0, Max = 1, Tooltip = "How much of the correction to apply over the animation")]
    public float Weight;
    [Property(Min = 0, Max = 90, Unit = "deg", Tooltip = "How far a planted foot may tilt to lie along the ground; 0 = feet stay as animated")]
    public float MaxFootTilt;
    [Property(Min = 0, Unit = "s", Tooltip = "How long the pelvis takes to ease to a new height (about two thirds of the way); 0 = at once")]
    public float PelvisSmoothing;

    // What the last tick did, for debugging and tests: the pelvis offset applied (metres, world; below
    // zero is down, and only while easing after a step down is it ever above), which feet found ground
    // and how far each was tilted (degrees).
    public float PelvisDrop;
    public bool LeftGrounded;
    public bool RightGrounded;
    public float LeftTilt;
    public float RightTilt;

    // Resolved against the pose's skeleton (-1: none).
    internal Skeleton? ResolvedFor;
    internal int PelvisJoint, LeftHip, LeftKnee, LeftFoot, RightHip, RightKnee, RightFoot;
    // Pelvis smoothing: whether a tick has run yet, and where the entity stood (world Y) on the last one.
    internal bool Smoothed;
    internal float LastRootY;
}

[Experimental("SAGE0126", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // skeletal animation (#120)
[PrefabPart("foot_ik", Plugin = "sage.gameplay.animation")]
public sealed class FootIkPart : IPrefabPart
{
    [Property(Tooltip = "The joint lowered so both feet reach (usually the hips); empty = none")]
    public string Pelvis = "";
    [Property(Tooltip = "The left leg: hip, knee and foot joints")]
    public FootIkLeg? Left;
    [Property(Tooltip = "The right leg: hip, knee and foot joints")]
    public FootIkLeg? Right;
    [Property(Min = 0, Unit = "m", Tooltip = "How far the ankle joint sits above the sole")]
    public float FootHeight = 0.08f;
    [Property(Min = 0, Unit = "m", Tooltip = "The ray starts this far above the animated foot (how high a step it finds)")]
    public float RayAbove = 0.5f;
    [Property(Min = 0, Unit = "m", Tooltip = "And looks this far below it (how deep a dip it reaches into)")]
    public float RayBelow = 0.5f;
    [Property(Min = 0, Unit = "m", Tooltip = "The most the pelvis may drop")]
    public float MaxPelvisDrop = 0.4f;
    [Property(Min = 0, Max = 1, Tooltip = "How much of the correction to apply over the animation")]
    public float Weight = 1f;
    [Property(Min = 0, Max = 90, Unit = "deg", Tooltip = "How far a planted foot may tilt to lie along the ground; 0 = feet stay as animated")]
    public float MaxFootTilt = 30f;
    [Property(Min = 0, Unit = "s", Tooltip = "How long the pelvis takes to ease to a new height (about two thirds of the way); 0 = at once")]
    public float PelvisSmoothing = 0.1f;

    public void Apply(in PrefabPartContext ctx)
    {
        if (!Complete(Left) && !Complete(Right))
        {
            ctx.Error("names no leg: `left` and `right` each want a hip, a knee and a foot joint");
            return;
        }
        ctx.World.Add(ctx.Entity, new FootIk
        {
            Pelvis = Pelvis ?? "",
            Left = Left,
            Right = Right,
            FootHeight = MathF.Max(0f, FootHeight),
            RayAbove = MathF.Max(0f, RayAbove),
            RayBelow = MathF.Max(0f, RayBelow),
            MaxPelvisDrop = MathF.Max(0f, MaxPelvisDrop),
            Weight = Math.Clamp(Weight, 0f, 1f),
            MaxFootTilt = float.IsFinite(MaxFootTilt) ? Math.Clamp(MaxFootTilt, 0f, 90f) : 0f,
            PelvisSmoothing = float.IsFinite(PelvisSmoothing) ? MathF.Max(0f, PelvisSmoothing) : 0f,
        });
    }

    private static bool Complete(FootIkLeg? leg) =>
        leg != null && !string.IsNullOrWhiteSpace(leg.Hip) && !string.IsNullOrWhiteSpace(leg.Knee) && !string.IsNullOrWhiteSpace(leg.Foot);
}

// Late phase, before aim IK and attachments (the pelvis moves everything above it). Allocation-free once
// resolved. Needs an IPhysicsWorld; a world without one plants nothing.
[Experimental("SAGE0126", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // skeletal animation (#120)
[System(Id, Phase.Late)]
internal sealed class FootIkSystem : ISystem
{
    public const string Id = "sage.animation.foot_ik";

    private readonly World _world;
    private readonly Query<FootIk> _feet;
    private readonly IPhysicsWorld? _physics;

    public FootIkSystem(World world)
    {
        _world = world;
        _feet = world.Query<FootIk>().WithoutAnyTags(Tags.Get<Ragdolled>());   // a ragdoll's feet are physics' (issue #246)
        world.Resources.TryGet(out _physics);   // a world may have no physics at all
    }

    public void Run(in SystemContext ctx)
    {
        if (_physics == null || !_world.Resources.TryGet<SkeletonPoses>(out var poses) || poses == null || poses.Count == 0) return;
        foreach (var (feet, entities) in _feet.Chunks)
        {
            var span = feet.Span;
            for (int n = 0; n < span.Length; n++)
            {
                ref var ik = ref span[n];
                var entity = entities.EntityAt(n);
                if (!poses.TryGet(entity, out var pose)) continue;
                if (!ReferenceEquals(ik.ResolvedFor, pose.Skeleton)) Resolve(ref ik, pose.Skeleton, entity);
                Plant(ref ik, pose, entity, _physics, ctx.Tick.Dt);
            }
        }
    }

    // A step of the entity itself bigger than this is a teleport, not a stair: the pelvis does not ease
    // across it.
    private const float TeleportHeight = 2f;

    internal static void Plant(ref FootIk ik, SkeletonPose pose, Entity entity, IPhysicsWorld physics, float dt)
    {
        ik.LeftGrounded = ik.RightGrounded = false;
        ik.LeftTilt = ik.RightTilt = 0f;
        float weight = float.IsNaN(ik.Weight) ? 0f : Math.Clamp(ik.Weight, 0f, 1f);
        if (weight <= 0f)
        {
            ik.PelvisDrop = 0f;
            ik.Smoothed = false;
            return;
        }

        var root = WorldPose(entity);
        var toWorld = root.ToMatrix();
        if (!Matrix4x4.Invert(toWorld, out var toModel)) return;

        // 1. The ground under each foot, and how far each ankle has to move to sit on it.
        bool left = Probe(ref ik, pose, ik.LeftFoot, toWorld, entity, physics, out var leftTarget, out float leftOffset, out var leftNormal);
        bool right = Probe(ref ik, pose, ik.RightFoot, toWorld, entity, physics, out var rightTarget, out float rightOffset, out var rightNormal);
        ik.LeftGrounded = left;
        ik.RightGrounded = right;

        // 2. The pelvis drops for the lower foot (never rises: a foot above the ground bends its knee);
        //    with no foot on the ground, it comes back to where the animation has it.
        float lowest = left || right ? MathF.Min(left ? leftOffset : 0f, right ? rightOffset : 0f) : 0f;
        float drop = Math.Clamp(lowest, -ik.MaxPelvisDrop, 0f) * weight;
        drop = Smooth(ref ik, drop, root.Position.Y, dt);
        if (!left && !right && drop == 0f) { ik.PelvisDrop = 0f; return; }
        if (drop != 0f && ik.PelvisJoint >= 0)
        {
            var model = pose.ModelSpace;
            var inModel = Vector3.TransformNormal(new Vector3(0f, drop, 0f), toModel);
            int parent = pose.Skeleton.Parents[ik.PelvisJoint];
            var inParent = inModel;
            if (parent >= 0)
            {
                if (!Matrix4x4.Invert(model[parent], out var fromParent)) return;
                inParent = Vector3.TransformNormal(inModel, fromParent);
            }
            pose.Local[ik.PelvisJoint].Position += inParent;
            PoseSampler.ToModelSpace(pose.Skeleton, pose, ik.PelvisJoint);
        }
        ik.PelvisDrop = ik.PelvisJoint >= 0 ? drop : 0f;

        // 3. Each grounded leg onto its target, bending forward (the model's -Z); 4. its foot along the ground.
        float maxTilt = float.IsFinite(ik.MaxFootTilt) ? Math.Clamp(ik.MaxFootTilt, 0f, 90f) * (MathF.PI / 180f) : 0f;
        if (left && PlaceLeg(pose, ik.LeftHip, ik.LeftKnee, ik.LeftFoot, Vector3.Transform(leftTarget, toModel), weight))
            ik.LeftTilt = TiltFoot(pose, ik.LeftFoot, Vector3.TransformNormal(leftNormal, toModel), maxTilt, weight);
        if (right && PlaceLeg(pose, ik.RightHip, ik.RightKnee, ik.RightFoot, Vector3.Transform(rightTarget, toModel), weight))
            ik.RightTilt = TiltFoot(pose, ik.RightFoot, Vector3.TransformNormal(rightNormal, toModel), maxTilt, weight);
    }

    // The pelvis offset this tick: `target` at once without smoothing (or on the first tick, or after a
    // teleport); otherwise last tick's offset, less however far the entity itself rose since (so the hips
    // stay where they were in the world when it steps up a stair), eased toward `target` exponentially.
    internal static float Smooth(ref FootIk ik, float target, float rootY, float dt)
    {
        float tau = float.IsFinite(ik.PelvisSmoothing) ? ik.PelvisSmoothing : 0f;
        float rose = rootY - ik.LastRootY;
        bool snap = tau <= 0f || !ik.Smoothed || !(dt > 0f) || !float.IsFinite(rose) || MathF.Abs(rose) > TeleportHeight;
        ik.LastRootY = rootY;
        ik.Smoothed = true;
        if (snap) return target;
        float limit = MathF.Max(ik.MaxPelvisDrop, 0f) + TeleportHeight;
        float from = Math.Clamp(ik.PelvisDrop - rose, -limit, limit);
        float eased = target + (from - target) * MathF.Exp(-dt / tau);
        return MathF.Abs(eased - target) < 1e-5f ? target : eased;
    }

    // The ray under one foot. The target is in world space.
    private static bool Probe(ref FootIk ik, SkeletonPose pose, int foot, in Matrix4x4 toWorld, Entity entity, IPhysicsWorld physics,
                              out Vector3 target, out float offset, out Vector3 normal)
    {
        target = default;
        offset = 0f;
        normal = Vector3.UnitY;
        if (foot < 0) return false;
        var at = Vector3.Transform(pose.ModelSpace[foot].Translation, toWorld);
        float length = ik.RayAbove + ik.RayBelow;
        if (length <= 0f) return false;
        var hit = physics.Raycast(at + new Vector3(0f, ik.RayAbove, 0f), -Vector3.UnitY, length, ignore: entity);
        if (!hit.Hit) return false;
        target = new Vector3(at.X, hit.Position.Y + ik.FootHeight, at.Z);
        if (hit.Normal.LengthSquared() > 1e-8f) normal = Vector3.Normalize(hit.Normal);
        offset = target.Y - at.Y;
        return true;
    }

    private static bool PlaceLeg(SkeletonPose pose, int hip, int knee, int foot, Vector3 target, float weight)
    {
        if (hip < 0 || knee < 0 || foot < 0) return false;
        var model = pose.ModelSpace;
        var current = model[foot].Translation;
        if (weight < 1f) target = Vector3.Lerp(current, target, weight);
        // The knee bends forward: the pole is a leg's length in front of it.
        var kneeAt = model[knee].Translation;
        float length = Vector3.Distance(model[hip].Translation, kneeAt) + Vector3.Distance(kneeAt, current);
        TwoBoneIk.Solve(pose, hip, knee, foot, target, kneeAt - Vector3.UnitZ * MathF.Max(length, 0.1f), 1f, keepTipRotation: true);
        return true;
    }

    // Turns the foot (in model space, about the ankle) by what takes the model's up onto the ground's
    // normal, at most `maxTilt` radians and `weight` of the way. Returns the tilt in degrees.
    private static float TiltFoot(SkeletonPose pose, int foot, Vector3 normal, float maxTilt, float weight)
    {
        if (maxTilt <= 0f || normal.LengthSquared() < 1e-8f) return 0f;
        normal = Vector3.Normalize(normal);
        float angle = MathF.Acos(Math.Clamp(normal.Y, -1f, 1f));
        if (angle < 1e-4f || normal.Y <= 0f) return 0f;   // level, or not a floor at all
        float tilt = MathF.Min(angle, maxTilt) * weight;
        var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, normal));
        // About the ankle in model space: local' = parent⁻¹ · turn · parent · local.
        var parents = pose.Skeleton.Parents;
        var parentRotation = Quaternion.Identity;
        for (int j = parents[foot]; j >= 0; j = parents[j]) parentRotation = pose.Local[j].Rotation * parentRotation;
        ref var local = ref pose.Local[foot];
        local.Rotation = Quaternion.Normalize(Quaternion.Conjugate(parentRotation) * Quaternion.CreateFromAxisAngle(axis, tilt) * parentRotation * local.Rotation);
        PoseSampler.ToModelSpace(pose.Skeleton, pose, foot);
        return tilt * (180f / MathF.PI);
    }

    // Names to indices, once per skeleton. A joint the skeleton lacks is said once, and that leg (or the
    // pelvis) is left alone.
    internal static void Resolve(ref FootIk ik, Skeleton skeleton, Entity entity)
    {
        ik.ResolvedFor = skeleton;
        ik.PelvisJoint = string.IsNullOrEmpty(ik.Pelvis) ? -1 : Find(skeleton, ik.Pelvis, entity, "pelvis");
        ResolveLeg(skeleton, ik.Left, entity, "left", out ik.LeftHip, out ik.LeftKnee, out ik.LeftFoot);
        ResolveLeg(skeleton, ik.Right, entity, "right", out ik.RightHip, out ik.RightKnee, out ik.RightFoot);
    }

    private static void ResolveLeg(Skeleton skeleton, FootIkLeg? leg, Entity entity, string side, out int hip, out int knee, out int foot)
    {
        hip = knee = foot = -1;
        if (leg == null) return;
        int h = Find(skeleton, leg.Hip, entity, side + ".hip");
        int k = Find(skeleton, leg.Knee, entity, side + ".knee");
        int f = Find(skeleton, leg.Foot, entity, side + ".foot");
        if (h < 0 || k < 0 || f < 0) return;
        if (!skeleton.IsInBranch(k, h) || !skeleton.IsInBranch(f, k) || k == h || f == k)
        {
            Log.Warn(LogCat.Animation, $"{World.Describe(entity)}: foot_ik {side}: {leg.Hip} → {leg.Knee} → {leg.Foot} is not one branch of the skeleton; that leg is not placed");
            return;
        }
        hip = h;
        knee = k;
        foot = f;
    }

    private static int Find(Skeleton skeleton, string name, Entity entity, string field)
    {
        int index = string.IsNullOrEmpty(name) ? -1 : skeleton.IndexOf(name);
        if (index < 0) Log.Warn(LogCat.Animation, $"{World.Describe(entity)}: foot_ik {field} names joint '{name}', which its skeleton does not have");
        return index;
    }

    // The entity's world pose from its transform chain, as of now (GlobalTransform is refreshed only at
    // the end of PostPhysics and Late).
    internal static Pose WorldPose(Entity entity)
    {
        var local = entity.TryGetComponent<Transform>(out var t) ? Pose.FromLocal(t) : Pose.Identity;
        var parent = entity.Parent;
        return parent.IsNull ? local : Pose.Combine(WorldPose(parent), local);
    }
}
