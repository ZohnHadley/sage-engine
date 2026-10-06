#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// Hand IK (docs/design/12 "As built (attachments and IK)", issue #361): an arm — shoulder, elbow,
// hand — reaches a target after the animation's blend: the off hand onto a rifle's foregrip, a hand
// onto a lever, a ledge, a door handle. Each arm is a two-bone chain (TwoBoneIk), the elbow bending down
// and back, the hand keeping the rotation the animation gave it.
//
// An arm's goal is, in order:
//   - `bone`: a joint of the same skeleton, plus `offset` in its space. A weapon grip: the off hand to
//     a point in the gun hand's space, which moves with the gun hand in the same tick;
//   - else the component's `LeftTarget`/`RightTarget`: an entity, plus `offset` in its space (a lever,
//     a ledge marker), which gameplay sets and clears. An entity that is itself attached to a bone
//     (BoneAttachment) is followed as of the last tick: to grip a held weapon, name the bone instead;
//   - else nothing: the arm keeps the animated pose.
//
//   part "hand_ik": { "left":  { "shoulder": "upper_arm.L", "elbow": "forearm.L", "hand": "hand.L",
//                                "bone": "hand.R", "offset": [0, 0.3, 0] },
//                     "right": { "shoulder": "upper_arm.R", "elbow": "forearm.R", "hand": "hand.R" },
//                     "weight": 1 }
//
// Content, not state: the component is [Transient], and the part puts it back when a load respawns
// the prefab; whoever set a target sets it again.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public sealed class HandIkArm
{
    [Property(Tooltip = "The upper arm (the chain's root)")]
    public string Shoulder = "";
    [Property(Tooltip = "The forearm (where the arm bends)")]
    public string Elbow = "";
    [Property(Tooltip = "The wrist (what reaches the target)")]
    public string Hand = "";
    [Property(Tooltip = "A joint of the same skeleton to reach (a grip on what the other hand holds); empty = the component's target")]
    public string Bone = "";
    [Property(Unit = "m", Tooltip = "Where the hand goes, in the space of `bone` or of the target")]
    public Vector3 Offset;
    [Property(Min = 0, Max = 1, Tooltip = "How much of the reach to apply over the animation")]
    public float Weight = 1f;
}

[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
[Transient]
[Component("sage:hand_ik")]
public struct HandIk : IComponent
{
    public HandIkArm? Left;
    public HandIkArm? Right;
    [Property(Min = 0, Max = 1, Tooltip = "How much of both arms' reach to apply over the animation")]
    public float Weight;
    [Property(Tooltip = "What the left hand reaches for when its arm names no bone; none = nothing")]
    public Entity LeftTarget;
    [Property(Tooltip = "What the right hand reaches for when its arm names no bone; none = nothing")]
    public Entity RightTarget;

    // What the last tick did, for debugging and tests: whether each hand had a goal and got there (a goal
    // out of reach leaves the arm stretched toward it and this false).
    public bool LeftReached;
    public bool RightReached;

    internal Skeleton? ResolvedFor;
    internal int LeftShoulder, LeftElbow, LeftHand, LeftBone, RightShoulder, RightElbow, RightHand, RightBone;
}

[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
[PrefabPart("hand_ik", Plugin = RegistrationOwners.Core)]
public sealed class HandIkPart : IPrefabPart
{
    [Property(Tooltip = "The left arm: shoulder, elbow and hand joints, and what it reaches")]
    public HandIkArm? Left;
    [Property(Tooltip = "The right arm: shoulder, elbow and hand joints, and what it reaches")]
    public HandIkArm? Right;
    [Property(Min = 0, Max = 1, Tooltip = "How much of both arms' reach to apply over the animation")]
    public float Weight = 1f;

    public void Apply(in PrefabPartContext ctx)
    {
        if (!Complete(Left) && !Complete(Right))
        {
            ctx.Error("names no arm: `left` and `right` each want a shoulder, an elbow and a hand joint");
            return;
        }
        ctx.World.Add(ctx.Entity, new HandIk { Left = Left, Right = Right, Weight = Math.Clamp(Weight, 0f, 1f) });
    }

    private static bool Complete(HandIkArm? arm) =>
        arm != null && !string.IsNullOrWhiteSpace(arm.Shoulder) && !string.IsNullOrWhiteSpace(arm.Elbow) && !string.IsNullOrWhiteSpace(arm.Hand);
}

// Late phase, after aim and look-at IK (they move the shoulders) and before attachments (a thing held in
// a hand follows the hand where IK put it). Allocation-free once resolved.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
[System(Id, Phase.Late, After = new[] { AimIkSystem.Id, LookAtIkSystem.Id, "?sage.animation.foot_ik" })]
internal sealed class HandIkSystem : ISystem
{
    public const string Id = "sage.animation.hand_ik";

    private readonly World _world;
    private readonly Query<HandIk> _hands;

    public HandIkSystem(World world)
    {
        _world = world;
        _hands = world.Query<HandIk>().WithoutAnyTags(Tags.Get<Ragdolled>());   // a ragdoll's arms are physics'
    }

    public void Run(in SystemContext ctx)
    {
        if (!_world.Resources.TryGet<SkeletonPoses>(out var poses) || poses == null || poses.Count == 0) return;
        foreach (var (hands, entities) in _hands.Chunks)
        {
            var span = hands.Span;
            for (int n = 0; n < span.Length; n++)
            {
                ref var ik = ref span[n];
                var entity = entities.EntityAt(n);
                if (!poses.TryGet(entity, out var pose)) continue;
                if (!ReferenceEquals(ik.ResolvedFor, pose.Skeleton)) Resolve(ref ik, pose.Skeleton, entity);
                Reach(ref ik, pose, entity, _world);
            }
        }
    }

    internal static void Reach(ref HandIk ik, SkeletonPose pose, Entity entity, World world)
    {
        float weight = float.IsNaN(ik.Weight) ? 0f : Math.Clamp(ik.Weight, 0f, 1f);
        ik.LeftReached = weight > 0f && ReachArm(pose, entity, world, ik.Left, ik.LeftShoulder, ik.LeftElbow, ik.LeftHand, ik.LeftBone, ik.LeftTarget, weight);
        ik.RightReached = weight > 0f && ReachArm(pose, entity, world, ik.Right, ik.RightShoulder, ik.RightElbow, ik.RightHand, ik.RightBone, ik.RightTarget, weight);
    }

    private static bool ReachArm(SkeletonPose pose, Entity entity, World world, HandIkArm? arm, int shoulder, int elbow, int hand, int bone,
                                 Entity target, float weight)
    {
        if (arm == null || shoulder < 0 || elbow < 0 || hand < 0) return false;
        float w = float.IsFinite(arm.Weight) ? Math.Clamp(arm.Weight, 0f, 1f) * weight : 0f;
        if (w <= 0f) return false;

        // The goal, in model space.
        var model = pose.ModelSpace;
        Vector3 goal;
        if (bone >= 0) goal = Vector3.Transform(arm.Offset, model[bone]);
        else if (world.IsAlive(target))
        {
            var inWorld = Vector3.Transform(arm.Offset, BoneAttachments.WorldPose(target).ToMatrix());
            if (!Matrix4x4.Invert(BoneAttachments.WorldPose(entity).ToMatrix(), out var toModel)) return false;
            goal = Vector3.Transform(inWorld, toModel);
        }
        else return false;

        // The elbow bends down and back (the model's -Y and +Z), as an arm does reaching forward.
        var elbowAt = model[elbow].Translation;
        float length = Vector3.Distance(model[shoulder].Translation, elbowAt) + Vector3.Distance(elbowAt, model[hand].Translation);
        var pole = elbowAt + new Vector3(0f, -1f, 0.5f) * MathF.Max(length, 0.1f);
        bool reachable = TwoBoneIk.Solve(pose, shoulder, elbow, hand, goal, pole, w, keepTipRotation: true);
        return reachable && Vector3.Distance(pose.ModelSpace[hand].Translation, goal) < 1e-3f;
    }

    // Names to indices, once per skeleton. A joint the skeleton lacks is said once, and that arm (or its
    // bone goal) is left alone.
    internal static void Resolve(ref HandIk ik, Skeleton skeleton, Entity entity)
    {
        ik.ResolvedFor = skeleton;
        ResolveArm(skeleton, ik.Left, entity, "left", out ik.LeftShoulder, out ik.LeftElbow, out ik.LeftHand, out ik.LeftBone);
        ResolveArm(skeleton, ik.Right, entity, "right", out ik.RightShoulder, out ik.RightElbow, out ik.RightHand, out ik.RightBone);
    }

    private static void ResolveArm(Skeleton skeleton, HandIkArm? arm, Entity entity, string side, out int shoulder, out int elbow, out int hand, out int bone)
    {
        shoulder = elbow = hand = bone = -1;
        if (arm == null) return;
        int s = Find(skeleton, arm.Shoulder, entity, side + ".shoulder");
        int e = Find(skeleton, arm.Elbow, entity, side + ".elbow");
        int h = Find(skeleton, arm.Hand, entity, side + ".hand");
        if (s < 0 || e < 0 || h < 0) return;
        if (!skeleton.IsInBranch(e, s) || !skeleton.IsInBranch(h, e) || e == s || h == e)
        {
            Log.Warn(LogCat.Animation, $"{World.Describe(entity)}: hand_ik {side}: {arm.Shoulder} → {arm.Elbow} → {arm.Hand} is not one branch of the skeleton; that arm does not reach");
            return;
        }
        if (!string.IsNullOrEmpty(arm.Bone))
        {
            int b = Find(skeleton, arm.Bone, entity, side + ".bone");
            if (b < 0) return;
            if (skeleton.IsInBranch(b, s))
            {
                Log.Warn(LogCat.Animation, $"{World.Describe(entity)}: hand_ik {side}: bone '{arm.Bone}' moves with the arm reaching for it; that arm does not reach");
                return;
            }
            bone = b;
        }
        shoulder = s;
        elbow = e;
        hand = h;
    }

    private static int Find(Skeleton skeleton, string name, Entity entity, string field)
    {
        int index = string.IsNullOrEmpty(name) ? -1 : skeleton.IndexOf(name);
        if (index < 0) Log.Warn(LogCat.Animation, $"{World.Describe(entity)}: hand_ik {field} names joint '{name}', which its skeleton does not have");
        return index;
    }
}
