#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// Aim IK on an entity (docs/design/12 "As built (attachments and IK)", issue #120): the spine, neck and
// head turn toward `Pitch`/`Yaw` after the animation graph's blend, each joint by its share and within
// its limits (AimChainIk). Gameplay (or #118's Animator, from its `aim_pitch`/`aim_yaw` parameters)
// writes Pitch and Yaw; AimIkSystem applies them in Phase.Late to the pose the entity registered with
// SkeletonPoses.
//
//   part "aim_ik": { "joints": [ { "joint": "spine",  "weight": 0.3, "pitchLimit": 20, "yawLimit": 30 },
//                                { "joint": "chest",  "weight": 0.3, "pitchLimit": 20, "yawLimit": 30 },
//                                { "joint": "head",   "weight": 0.4, "pitchLimit": 50, "yawLimit": 70 } ],
//                    "weight": 1 }
//
// Content, not state: the component is [Transient] and the part puts it back when a load respawns the
// prefab (the aim itself is rewritten every tick by whoever drives it).

// One joint of an aim chain, by name. Limits in degrees, either way.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public sealed class AimIkJoint
{
    [Property(Tooltip = "The joint, by name as the model writes it")]
    public string Joint = "";
    [Property(Min = 0, Max = 1, Tooltip = "Its share of the aim; the chain's shares usually add up to 1")]
    public float Weight = 1f;
    [Property(Min = 0, Max = 180, Unit = "deg", Tooltip = "How far this joint alone may pitch, up or down")]
    public float PitchLimit = 45f;
    [Property(Min = 0, Max = 180, Unit = "deg", Tooltip = "How far this joint alone may yaw, left or right")]
    public float YawLimit = 60f;
}

[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
[Transient]
[Component("sage:aim_ik")]
public struct AimIk : IComponent
{
    [Property(Unit = "rad", Tooltip = "Where to aim: up (+) or down (-) from the character's forward")]
    public float Pitch;
    [Property(Unit = "rad", Tooltip = "Where to aim: left (+) or right (-) of the character's forward")]
    public float Yaw;
    [Property(Min = 0, Max = 1, Tooltip = "How much of the aim to apply over the animation")]
    public float Weight;
    // The chain, hips first, as content wrote it.
    public List<AimIkJoint>? Joints;

    // Resolved against the pose's skeleton on the first tick it has one (joint names to indices).
    internal Skeleton? ResolvedFor;
    internal AimJoint[]? Chain;
}

[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
[PrefabPart("aim_ik", Plugin = RegistrationOwners.Core)]
public sealed class AimIkPart : IPrefabPart
{
    [Property(Tooltip = "The joints that turn, from the hips up")]
    public List<AimIkJoint> Joints = new();
    [Property(Min = 0, Max = 1, Tooltip = "How much of the aim to apply over the animation")]
    public float Weight = 1f;

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
        ctx.World.Add(ctx.Entity, new AimIk { Weight = Math.Clamp(Weight, 0f, 1f), Joints = Joints });
    }
}

// Late phase, first: turns every aiming entity's chain. Allocation-free once resolved.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
[System(Id, Phase.Late, After = new[] { "?sage.animation.foot_ik" })]
internal sealed class AimIkSystem : ISystem
{
    public const string Id = "sage.animation.aim_ik";

    private readonly World _world;
    private readonly Query<AimIk> _aiming;

    public AimIkSystem(World world)
    {
        _world = world;
        _aiming = world.Query<AimIk>();
    }

    public void Run(in SystemContext ctx)
    {
        if (!_world.Resources.TryGet<SkeletonPoses>(out var poses) || poses == null || poses.Count == 0) return;
        foreach (var (aims, entities) in _aiming.Chunks)
        {
            var span = aims.Span;
            for (int n = 0; n < span.Length; n++)
            {
                ref var aim = ref span[n];
                var entity = entities.EntityAt(n);
                if (!poses.TryGet(entity, out var pose)) continue;
                if (!ReferenceEquals(aim.ResolvedFor, pose.Skeleton)) Resolve(ref aim, pose.Skeleton, entity);
                if (aim.Chain == null || aim.Chain.Length == 0) continue;
                AimChainIk.Solve(pose, aim.Chain, aim.Pitch, aim.Yaw, aim.Weight);
            }
        }
    }

    // Names to indices, once per skeleton. A joint the skeleton lacks is said once and left out.
    internal static void Resolve(ref AimIk aim, Skeleton skeleton, Entity entity)
    {
        const float ToRadians = MathF.PI / 180f;
        aim.ResolvedFor = skeleton;
        var joints = aim.Joints;
        if (joints == null || joints.Count == 0) { aim.Chain = Array.Empty<AimJoint>(); return; }
        var chain = new List<AimJoint>(joints.Count);
        foreach (var joint in joints)
        {
            if (joint == null) continue;
            int index = skeleton.IndexOf(joint.Joint);
            if (index < 0)
            {
                Log.Warn(LogCat.Animation, $"{World.Describe(entity)}: aim_ik names joint '{joint.Joint}', which its skeleton does not have");
                continue;
            }
            chain.Add(new AimJoint(index, joint.Weight, joint.PitchLimit * ToRadians, joint.YawLimit * ToRadians));
        }
        aim.Chain = chain.ToArray();
    }
}
