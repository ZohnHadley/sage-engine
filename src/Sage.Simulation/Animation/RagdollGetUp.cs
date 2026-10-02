#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// Getting up from a ragdoll (issue #247, phase 4k; docs/design/12 "As built (getting up, issue #247)").
//
//   part "ragdoll": { "getUpAfter": 3, "getUpBack": "getup_back", "getUpFront": "getup_front", "getUpFade": 0.2 }
//
// **Ragdolls.GetUp** (also the `GetUp` input, and `getUpAfter` seconds after it went down): reads which
// way the body lies from the pelvis (the root body's joint) — its forward, the model's -Z, pointing at the
// sky is face up — and snaps the entity's root under the pelvis: X and Z the pelvis's, Y the ground under
// it (a ray that ignores the entity), and the yaw the one the get-up clips start from: **face up, the
// character faces its feet** (its head lies behind it, +Z in model space: it sits up looking along its
// legs); **face down, it faces where its head points** (it pushes up and stands facing that way). The
// pose the ragdoll left is re-expressed under the new root (only the root body's joint changes: the
// pelvis stays exactly where it lay), Ragdolls.Stop hands the character back (bodies gone, capsule solid,
// untagged, animator resumed), and Animators.PlayFrom enters `getUpBack` or `getUpFront` fading from that
// pose over `getUpFade` seconds. The graph takes it from there: `anim_finished` back to idle.
//
// **getUpAfter** counts the seconds the ragdoll has lain settled (#248; saved, in RagdollGetUp.Down), and
// starts again if something unsettles it. A character that has died
// (Gameplay sets StayDown on `Died`) never gets up by itself; GetUp from code or a wire still works, and
// clears StayDown.
[Experimental(RagdollApi.Experimental, UrlFormat = RagdollApi.Url)]
[Component("sage:ragdoll_get_up")]
public struct RagdollGetUp : IComponent
{
    public const string DefaultBack = "getup_back";
    public const string DefaultFront = "getup_front";
    public const float DefaultFade = 0.2f;

    [Property(Min = 0, Unit = "s", Tooltip = "Gets up by itself this long after coming to rest; 0 = only when told (GetUp)")]
    public float After;
    [Property(Tooltip = "The animator state it gets up with from lying face up; empty = getup_back")]
    public string Back;
    [Property(Tooltip = "The animator state it gets up with from lying face down; empty = getup_front")]
    public string Front;
    [Property(Min = 0, Unit = "s", Tooltip = "How long the pose it lay in fades into the get-up state")]
    public float Fade;
    [Property(Unit = "s", Tooltip = "How long it has lain settled (counts while the ragdoll is settled)")]
    public float Down;
    [Property(Tooltip = "It has died: getUpAfter no longer gets it up (GetUp still does, and clears this)")]
    public bool StayDown;

    public readonly string BackState => string.IsNullOrEmpty(Back) ? DefaultBack : Back;
    public readonly string FrontState => string.IsNullOrEmpty(Front) ? DefaultFront : Front;

    public static RagdollGetUp Defaults => new() { Back = DefaultBack, Front = DefaultFront, Fade = DefaultFade };
}

public static partial class Ragdolls
{
    public const string GetUpSystemId = "sage.animation.ragdoll_get_up";
    // The entity input: `{ "input": "GetUp" }`.
    public const string GetUpInput = "GetUp";

    private const float GetUpGroundRay = 3f;

    internal static void RegisterGetUp(Engine engine)
    {
        engine.Inputs.Register<Ragdoll>(GetUpInput, static (World world, in IOContext io) =>
        {
            if (!GetUp(world, io.Self) && IsActive(world, io.Self))
                Log.Warn(LogCat.Events, $"I/O: {GetUpInput} at {World.Describe(io.Self)}: it cannot get up");
        });
    }

    // Was the ragdoll lying face up (its pelvis's front, the model's -Z, towards the sky)? Null when it is
    // not a ragdoll or its pose is not registered.
    public static bool? IsFaceUp(World world, Entity entity)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!TryPelvis(world, entity, out _, out var pelvis, out _, out _)) return null;
        return Vector3.Transform(-Vector3.UnitZ, pelvis.Rotation).Y >= 0f;
    }

    // Gets a ragdoll back on its feet (see the top of this file). False — and nothing changed — when it is
    // not a ragdoll or has no registered pose. True when it has handed the pose back, even if the animator
    // lacks the state (said once per call, as a warning). Structural, as Start: never inside a query's loop.
    public static bool GetUp(World world, Entity entity)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!TryPelvis(world, entity, out var pose, out var pelvis, out int pelvisJoint, out var oldRoot)) return false;
        var settings = world.TryGet<RagdollGetUp>(entity, out var g) ? g : RagdollGetUp.Defaults;
        bool faceUp = Vector3.Transform(-Vector3.UnitZ, pelvis.Rotation).Y >= 0f;

        if (entity.Parent.IsNull)
        {
            // The new root: under the pelvis, on the ground, turned as the clip starts.
            var transform = world.Get<Transform>(entity);
            var head = Vector3.Transform(Vector3.UnitY, pelvis.Rotation);
            var facing = faceUp ? new Vector2(-head.X, -head.Z) : new Vector2(head.X, head.Z);
            if (facing.LengthSquared() > 1e-4f)
                transform.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(-facing.X, -facing.Y));
            float y = oldRoot.Position.Y;
            if (world.Resources.TryGet<IPhysicsWorld>(out var physics) && physics != null)
            {
                var hit = physics.Raycast(pelvis.Position, -Vector3.UnitY, GetUpGroundRay, default, false, entity);
                if (hit.Hit) y = hit.Position.Y;
            }
            transform.LocalPosition = new Vector3(pelvis.Position.X, y, pelvis.Position.Z);
            world.Teleport(entity, transform);

            // The pelvis where it lay, under the new root; its ancestors keep their locals.
            var newRoot = Pose.FromLocal(transform).ToMatrix();
            Matrix4x4.Invert(newRoot, out var inverseRoot);
            var model = pose.ModelSpace[pelvisJoint] * oldRoot.ToMatrix() * inverseRoot;
            int parent = pose.Skeleton.Parents[pelvisJoint];
            if (parent >= 0)
            {
                Matrix4x4.Invert(pose.ModelSpace[parent], out var inverseParent);
                model *= inverseParent;
            }
            pose.Local[pelvisJoint] = PoseMath.ToPose(model);
            PoseSampler.ToModelSpace(pose.Skeleton, pose);
        }

        Stop(world, entity);
        if (world.Has<RagdollGetUp>(entity))
        {
            ref var state = ref world.Get<RagdollGetUp>(entity);
            state.Down = 0f;
            state.StayDown = false;
        }
        // PlayFrom copies the pose at once; the animator overwrites it from its next tick.
        string name = faceUp ? settings.BackState : settings.FrontState;
        float fade = settings.Fade >= 0f && float.IsFinite(settings.Fade) ? settings.Fade : RagdollGetUp.DefaultFade;
        if (!Animators.PlayFrom(world, entity, pose, name, fade))
            Log.Warn(LogCat.Animation, $"{World.Describe(entity)}: got up, but its animator has no state '{name}' to get up with");
        return true;
    }

    // The ragdoll's registered pose, the world pose of its root body's joint (the pelvis), that joint and
    // the entity's root, as the ragdoll last wrote them.
    private static bool TryPelvis(World world, Entity entity, out SkeletonPose pose, out Pose pelvis, out int joint, out Pose root)
    {
        pose = null!;
        pelvis = default;
        joint = -1;
        root = default;
        if (!IsActive(world, entity)) return false;
        if (!world.Resources.TryGet<SkeletonPoses>(out var poses) || poses == null || !poses.TryGet(entity, out var registered, out var model)) return false;
        pose = registered;
        if (!world.Resources.TryGet<RagdollInstances>(out var instances) || instances == null) return false;
        var instance = instances.For(entity);
        if (!instance.Prepare(world, entity, world.Get<Ragdoll>(entity).Record, pose, model)) return false;
        joint = instance.Table!.Bodies[0].Joint;
        root = BoneAttachments.WorldPose(entity);
        pelvis = PoseMath.ToPose(pose.ModelSpace[joint] * root.ToMatrix());
        return true;
    }
}

// Phase.Animation, after the ragdoll has written this tick's pose: counts how long each ragdoll has been
// down and gets up the ones whose `getUpAfter` has passed (unless they have died). Allocation-free per tick.
[Experimental(RagdollApi.Experimental, UrlFormat = RagdollApi.Url)]
[System(Ragdolls.GetUpSystemId, Phase.Animation, After = new[] { Ragdolls.SystemId })]
internal sealed class RagdollGetUpSystem : ISystem
{
    private readonly World _world;
    private readonly Query<Ragdoll, RagdollGetUp> _ragdolls;
    private readonly List<Entity> _rising = new();

    public RagdollGetUpSystem(World world)
    {
        _world = world;
        _ragdolls = world.Query<Ragdoll, RagdollGetUp>();
    }

    public void Run(in SystemContext ctx)
    {
        float dt = ctx.Tick.Dt;
        _rising.Clear();
        foreach (var (ragdolls, settings, entities) in _ragdolls.Chunks)
        {
            var r = ragdolls.Span;
            var g = settings.Span;
            for (int n = 0; n < r.Length; n++)
            {
                ref var getUp = ref g[n];
                // Counts while the body lies settled (#248): a shove that unsettles it starts the count again.
                if (!r[n].Active || !r[n].Settled)
                {
                    getUp.Down = 0f;
                    continue;
                }
                getUp.Down += dt;
                if (getUp.After > 0f && !getUp.StayDown && getUp.Down >= getUp.After) _rising.Add(entities.EntityAt(n));
            }
        }
        foreach (var entity in _rising) Ragdolls.GetUp(_world, entity);
        _rising.Clear();
    }
}
