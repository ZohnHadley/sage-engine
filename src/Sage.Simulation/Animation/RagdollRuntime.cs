#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

namespace Sage.Simulation;

// Going ragdoll (issue #246, phase 4k; docs/design/12 "As built (going ragdoll, issue #246)"): a skeletal
// character hands its pose from its animator to physics bodies built from its model's `ragdoll` records.
//
//   part "ragdoll": { "record": "knight_ragdoll", "onDeath": true, "hitImpulse": 30 }   (every field optional)
//
// **Start** (Ragdolls.Start, the `Ragdoll` input, a death when `onDeath`): one dynamic body per resolved
// body, posed where its joint is now (its model-space pose times the entity's transform, times the
// body's offset and turn), all in one fresh collision group and all owned by the character entity (a ray
// on a limb hits the character); a joint per body to the body of its nearest bodied ancestor, at the
// child joint's origin, with the record's limits measured from the skeleton's rest pose. Velocities are
// seeded from the last two ticks' poses (recorded while the ragdoll is idle); an impulse goes to the
// body nearest its point (or to every body by its mass, with no point). The entity's own collider (a
// character's capsule) joins the group and turns into a trigger, so it stops blocking things and rays
// while the body lies there (Ragdoll.SolidCapsule remembers to undo it); the `sage:ragdolled` tag stops
// the character controller, foot IK and aim IK; the animator is suspended (it owns nothing meanwhile);
// and every child bone attachment with `drop` set is let go with a dynamic body of its own.
//
// **Each tick** (RagdollSystem, Phase.Animation after the animator): body poses become joint locals —
// a bodied joint's rotation relative to its parent joint, its bone length kept; an unbodied joint (the
// spine, a hand) keeps the local it had at the hand-off — then ToModelSpace; the entity's root follows
// the root body (the pelvis) on X and Z, with Y on the ground under it.
//
// **Saved:** the component's settings, `Active` (and `SolidCapsule`), and since #248 whether it has
// settled and each body's state (RagdollSettling.cs): a load rebuilds the bodies where they were, moving
// as they were.
[Experimental(RagdollApi.Experimental, UrlFormat = RagdollApi.Url)]
[Component("sage:ragdoll")]
public struct Ragdoll : IComponent
{
    [RecordRef("ragdoll"), Property(Tooltip = "One ragdoll record to use; none = every ragdoll record for the model, in id order")]
    public RecordId Record;
    [Property(Tooltip = "Goes ragdoll when it dies (Died)")]
    public bool OnDeath;
    [Property(Min = 0, Unit = "N*s", Tooltip = "The impulse a damaging hit gives the body nearest where it landed, along the hit (on the killing blow, and on a body already down)")]
    public float HitImpulse;
    [Property(Tooltip = "Physics has the pose: the bodies are built and the animator is suspended")]
    public bool Active;
    [Property(Tooltip = "Set while the ragdoll has turned the entity's solid collider into a trigger; Stop makes it solid again")]
    public bool SolidCapsule;
    // Settling and saves (issue #248, RagdollSettling.cs).
    [Property(Category = "State", Tooltip = "Every body has lain still for the record's settleTime (OnSettled fired); its bodies sleep")]
    public bool Settled;
    [Property(Category = "State", Min = 0, Unit = "s", Tooltip = "How long every body has been slower than the record's settleSpeed")]
    public float RestTime;
    [Property(Category = "State", Tooltip = "While active: each body's position, rotation and velocities, in the ragdoll table's order, so a load puts them back")]
    public RagdollBodyState[]? Bodies;
}

// Tag: the entity is a ragdoll now. The character controller, foot IK and aim IK leave it alone.
[Experimental(RagdollApi.Experimental, UrlFormat = RagdollApi.Url)]
[Tag("sage:ragdolled")]
public struct Ragdolled : ITag { }

// "ragdoll": {} — goes ragdoll on death, on the `Ragdoll` input or from code, with the bodies of its
// model's `ragdoll` records (the model is the pose's: the animator's, or whatever registered it).
[Experimental(RagdollApi.Experimental, UrlFormat = RagdollApi.Url)]
[PrefabPart("ragdoll", Plugin = RegistrationOwners.Core, After = new[] { "skinned_mesh", "animator" })]
public sealed class RagdollPart : IPrefabPart
{
    [RecordRef("ragdoll"), Property(Tooltip = "One ragdoll record to use; left out, every ragdoll record for the model")]
    public RecordId Record;
    [Property(Tooltip = "Goes ragdoll when it dies")]
    public bool OnDeath = true;
    [Property(Min = 0, Unit = "N*s", Tooltip = "The impulse a damaging hit gives the body nearest where it landed, along the hit")]
    public float HitImpulse = 30f;
    // Getting up (issue #247, RagdollGetUp.cs): kept in their own component, sage:ragdoll_get_up.
    [Property(Min = 0, Unit = "s", Tooltip = "Gets up by itself this long after going down (never once it has died); 0 = only when told (the GetUp input)")]
    public float GetUpAfter;
    [Property(Tooltip = "The animator state it gets up with from lying face up")]
    public string GetUpBack = RagdollGetUp.DefaultBack;
    [Property(Tooltip = "The animator state it gets up with from lying face down")]
    public string GetUpFront = RagdollGetUp.DefaultFront;
    [Property(Min = 0, Unit = "s", Tooltip = "How long the pose it lay in fades into the get-up state")]
    public float GetUpFade = RagdollGetUp.DefaultFade;

    public void Apply(in PrefabPartContext ctx)
    {
        ctx.World.Add(ctx.Entity, new Ragdoll { Record = Record, OnDeath = OnDeath, HitImpulse = MathF.Max(0f, HitImpulse) });
        ctx.World.Add(ctx.Entity, new RagdollGetUp
        {
            After = MathF.Max(0f, GetUpAfter), Back = GetUpBack ?? "", Front = GetUpFront ?? "", Fade = MathF.Max(0f, GetUpFade),
        });
    }
}

public static partial class Ragdolls
{
    public const string SystemId = "sage.animation.ragdoll";
    // The entity input: `{ "input": "Ragdoll" }`, or with an impulse, `"parameter": "0 0 40"` (N·s, world).
    public const string Input = "Ragdoll";

    // Collision groups the ragdolls of a world take, one each, counting up from here (a game's own groups
    // stay below it).
    public const int FirstGroup = 0x4000_0000;

    internal static void Register(Engine engine)
    {
        RegisterSettling(engine);
        engine.Inputs.Register<Ragdoll>(Input, static (World world, in IOContext io) =>
        {
            Vector3? impulse = TryParseVector(io.Parameter, out var v) ? v : null;
            if (!Start(world, io.Self, impulse) && !IsActive(world, io.Self))
                Log.Warn(LogCat.Events, $"I/O: {Input} at {World.Describe(io.Self)}: it cannot go ragdoll (no pose, no ragdoll record for its model, or no physics)");
        });
    }

    // Is it a ragdoll now?
    public static bool IsActive(World world, Entity entity) =>
        !entity.IsNull && world.IsAlive(entity) && world.TryGet<Ragdoll>(entity, out var r) && r.Active;

    // Hands the entity's pose to physics (see the top of this file). `impulse` (N·s, world) goes to the
    // body nearest `point`, or with no point to every body by its mass. False — and nothing changed — when
    // it has no Ragdoll, is one already, has no registered pose, its model has no ragdoll records, or the
    // world has no physics. Structural (a tag, dropped attachments): never call it inside a query's loop.
    public static bool Start(World world, Entity entity, Vector3? impulse = null, Vector3? point = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (entity.IsNull || !world.IsAlive(entity) || !world.Has<Ragdoll>(entity)) return false;
        if (world.Get<Ragdoll>(entity).Active) return false;
        if (!world.Resources.TryGet<IPhysicsWorld>(out var physics) || physics == null) return false;
        if (!world.Resources.TryGet<RagdollInstances>(out var instances) || instances == null) return false;
        if (!world.Resources.TryGet<SkeletonPoses>(out var poses) || poses == null || !poses.TryGet(entity, out var pose, out var model)) return false;
        var instance = instances.For(entity);
        if (!instance.Prepare(world, entity, world.Get<Ragdoll>(entity).Record, pose, model)) return false;

        pose.Local.CopyTo(instance.Handoff);
        instance.Build(world, physics, entity, pose, seed: true);

        ref var ragdoll = ref world.Get<Ragdoll>(entity);
        ragdoll.Active = true;
        ResetSettling(ref ragdoll);
        ragdoll.SolidCapsule = TakeCapsule(world, physics, entity, instance.Group);
        entity.AddTag<Ragdolled>();
        Animators.Suspend(world, entity);
        Drop(world, physics, entity, instance);
        if (impulse is { } j) instance.Push(physics, j, point);
        return true;
    }

    // An impulse (N·s, world) at a point to the body nearest it, or with no point to every body by its
    // mass. False when it is not a ragdoll.
    public static bool ApplyImpulse(World world, Entity entity, Vector3 impulse, Vector3? point = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!IsActive(world, entity) || !world.Resources.TryGet<IPhysicsWorld>(out var physics) || physics == null) return false;
        if (!world.Resources.TryGet<RagdollInstances>(out var instances) || instances?.Find(entity) is not { Built: true } instance) return false;
        instance.Push(physics, impulse, point);
        return true;
    }

    // Takes the bodies and joints away and gives the pose back to the animator (resumed, so it writes
    // its own pose from the next tick), the character controller, foot and aim IK and the entity's
    // collider (solid again). Dropped attachments stay dropped. Getting up (#247) builds on this with a
    // fade from the ragdoll's last pose. False when it is not a ragdoll. Structural, as Start.
    public static bool Stop(World world, Entity entity)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!IsActive(world, entity)) return false;
        world.Resources.TryGet<IPhysicsWorld>(out var physics);
        if (world.Resources.TryGet<RagdollInstances>(out var instances) && instances?.Find(entity) is { } instance && physics != null)
            instance.Remove(physics);
        ref var ragdoll = ref world.Get<Ragdoll>(entity);
        ragdoll.Active = false;
        ResetSettling(ref ragdoll);
        if (physics != null && world.Has<PhysicsBody>(entity))
        {
            if (ragdoll.SolidCapsule && world.Has<Collider>(entity))
            {
                // Solid again: the physics sync builds its body afresh next tick, outside any group.
                world.Get<Collider>(entity).IsTrigger = false;
                physics.RemoveBody(world.Get<PhysicsBody>(entity));
                world.Remove<PhysicsBody>(entity);
            }
            else physics.SetGroup(world.Get<PhysicsBody>(entity), 0);
        }
        ragdoll.SolidCapsule = false;
        entity.RemoveTag<Ragdolled>();
        Animators.Resume(world, entity);
        return true;
    }

    // The entity's own collider joins the ragdoll's group and, when solid, becomes a trigger (a capsule
    // standing over a corpse would block the player and every ray). True when it made it a trigger.
    private static bool TakeCapsule(World world, IPhysicsWorld physics, Entity entity, int group)
    {
        if (!world.Has<PhysicsBody>(entity) || !world.Has<Collider>(entity)) return false;
        ref var collider = ref world.Get<Collider>(entity);
        ref var body = ref world.Get<PhysicsBody>(entity);
        if (collider.IsTrigger || body.IsStatic || physics.IsDynamic(body))
        {
            physics.SetGroup(body, group);
            return false;
        }
        collider.IsTrigger = true;
        var rigid = world.TryGet<RigidBody>(entity, out var r) ? r : RigidBody.Kinematic();
        physics.RemoveBody(body);
        body = physics.AddBody(entity, collider, rigid, BoneAttachments.WorldPose(entity), group);
        return true;
    }

    // Lets go of every child bone attachment marked `drop`: unparented where it is, given a dynamic body
    // (its own collider, or a small box) in the ragdoll's group and the velocity of the body nearest it.
    private static void Drop(World world, IPhysicsWorld physics, Entity entity, RagdollInstance instance)
    {
        var dropping = instance.Dropping;
        dropping.Clear();
        foreach (var child in entity.ChildEntities)
            if (child.TryGetComponent<BoneAttachment>(out var a) && a.Drop && (a.Target.IsNull || a.Target == entity)) dropping.Add(child);
        foreach (var item in dropping)
        {
            var pose = BoneAttachments.WorldPose(item);
            world.ClearParent(item);
            world.Remove<BoneAttachment>(item);
            world.Teleport(item, new Transform { LocalPosition = pose.Position, LocalRotation = pose.Rotation, LocalScale = pose.Scale });
            if (world.TryGet<PhysicsBody>(item, out var old))
            {
                physics.RemoveBody(old);
                world.Remove<PhysicsBody>(item);
            }
            if (!world.Has<Collider>(item)) world.Add(item, Collider.Box(new Vector3(0.1f)));
            var rigid = world.TryGet<RigidBody>(item, out var r) ? r : default;
            rigid.Kind = BodyKind.Dynamic;
            if (rigid.Mass <= 0f) rigid.Mass = 1f;
            if (world.Has<RigidBody>(item)) world.Get<RigidBody>(item) = rigid;
            else world.Add(item, rigid);
            var body = physics.AddBody(item, world.Get<Collider>(item), rigid, pose, instance.Group);
            world.Add(item, body);
            int nearest = instance.Nearest(physics, pose.Position);
            if (nearest >= 0) physics.SetVelocity(body, physics.VelocityOf(instance.Bodies[nearest]));
        }
        dropping.Clear();
    }

    internal static bool TryParseVector(string? text, out Vector3 value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var span = text.AsSpan().Trim();
        Span<float> v = stackalloc float[3];
        for (int i = 0; i < 3; i++)
        {
            span = span.TrimStart();
            int end = span.IndexOfAny(' ', '\t', ',');
            var token = end < 0 ? span : span[..end];
            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return false;
            span = end < 0 ? ReadOnlySpan<char>.Empty : span[(end + 1)..];
        }
        value = new Vector3(v[0], v[1], v[2]);
        return true;
    }
}

// What a ragdoll of one entity holds while it lives: transient, never saved.
internal sealed class RagdollInstance
{
    public Entity Entity;
    public RagdollTable? Table;
    public Skeleton? ResolvedFor;
    public RecordId ResolvedRecord;
    public bool Missing;           // no table: said once

    public PhysicsBody[] Bodies = Array.Empty<PhysicsBody>();
    public PhysicsJoint[] Joints = Array.Empty<PhysicsJoint>();
    public bool Built;
    public int Group;
    public bool Quiet;             // every body asleep, and the pose and states written since (#248)

    // Per joint: the local each had at the hand-off (what an unbodied joint keeps), and model-space scratch.
    public Pose[] Handoff = Array.Empty<Pose>();
    public Pose[] Model = Array.Empty<Pose>();
    // Per body: its rest rotation in model space (the joint limits' zero), and its joint's world pose this tick.
    public Quaternion[] RestRotation = Array.Empty<Quaternion>();
    public Pose[] JointWorld = Array.Empty<Pose>();
    public Pose[] BodyPose = Array.Empty<Pose>();
    public float RestRootHeight;   // the root body's joint above the model's origin, at rest
    public float TotalMass;

    // While idle: each body's world centre and rotation over the last two ticks it was animated.
    public Vector3[] PreviousPosition = Array.Empty<Vector3>(), CurrentPosition = Array.Empty<Vector3>();
    public Quaternion[] PreviousRotation = Array.Empty<Quaternion>(), CurrentRotation = Array.Empty<Quaternion>();
    public long PreviousTick = -10, CurrentTick = -10;
    public float RecordedDt;

    public readonly List<Entity> Dropping = new();

    // Resolves the table for this pose's skeleton (once per skeleton and record) and sizes the arrays.
    public bool Prepare(World world, Entity entity, RecordId record, SkeletonPose pose, AssetPath model)
    {
        if (ReferenceEquals(ResolvedFor, pose.Skeleton) && ResolvedRecord == record) return Table != null;
        ResolvedFor = pose.Skeleton;
        ResolvedRecord = record;
        Table = null;
        if (world.Resources.TryGet<RecordStore>(out var records) && records != null)
        {
            if (!record.IsEmpty)
            {
                if (records.TryGet(record, out RagdollRecord one)) Table = Ragdolls.Resolve(pose.Skeleton, new[] { one });
            }
            else Table = Ragdolls.Resolve(records, pose.Skeleton, model);
        }
        if (Table == null || Table.Count == 0)
        {
            Table = null;
            if (!Missing)
                Log.Warn(LogCat.Animation, $"{World.Describe(entity)}: no ragdoll for its model '{(model.IsEmpty ? "(none)" : model.Path.ToString())}'" +
                                           (record.IsEmpty ? "" : $" (record {record})") + "; it cannot go ragdoll");
            Missing = true;
            return false;
        }

        var skeleton = pose.Skeleton;
        int bodies = Table.Count, joints = skeleton.JointCount;
        Bodies = new PhysicsBody[bodies];
        Joints = new PhysicsJoint[bodies];
        RestRotation = new Quaternion[bodies];
        JointWorld = new Pose[bodies];
        BodyPose = new Pose[bodies];
        PreviousPosition = new Vector3[bodies];
        CurrentPosition = new Vector3[bodies];
        PreviousRotation = new Quaternion[bodies];
        CurrentRotation = new Quaternion[bodies];
        PreviousTick = CurrentTick = -10;
        Handoff = new Pose[joints];
        Model = new Pose[joints];

        // The rest pose in model space: the zero of every joint limit.
        var parents = skeleton.Parents;
        var rest = skeleton.RestPose;
        for (int j = 0; j < joints; j++) Model[j] = parents[j] < 0 ? rest[j] : Pose.Combine(Model[parents[j]], rest[j]);
        TotalMass = 0f;
        for (int i = 0; i < bodies; i++)
        {
            var b = Table.Bodies[i];
            RestRotation[i] = Quaternion.Normalize(Model[b.Joint].Rotation * b.Rotation);
            TotalMass += b.Mass;
        }
        RestRootHeight = Model[Table.Bodies[0].Joint].Position.Y;
        return true;
    }

    // Each body's world centre and rotation as the pose has them now (idle ragdolls, every tick).
    public void Record(SkeletonPose pose, in Pose root, long tick, float dt)
    {
        if (Table == null) return;
        (PreviousPosition, CurrentPosition) = (CurrentPosition, PreviousPosition);
        (PreviousRotation, CurrentRotation) = (CurrentRotation, PreviousRotation);
        PreviousTick = CurrentTick;
        CurrentTick = tick;
        RecordedDt = dt;
        var model = pose.ModelSpace;
        var rootMatrix = root.ToMatrix();
        for (int i = 0; i < Table.Count; i++)
        {
            var b = Table.Bodies[i];
            ref readonly var m = ref model[b.Joint];
            CurrentPosition[i] = Vector3.Transform(Vector3.Transform(b.Offset, m), rootMatrix);
            CurrentRotation[i] = Quaternion.Normalize(root.Rotation * Quaternion.CreateFromRotationMatrix(m) * b.Rotation);
        }
    }

    // The bodies and joints, posed as `pose` is now on the entity; seeded with the recorded velocities
    // when `seed` and the last two recordings are a tick apart and recent.
    public void Build(World world, IPhysicsWorld physics, Entity entity, SkeletonPose pose, bool seed)
    {
        var table = Table!;
        Entity = entity;
        if (Group == 0) Group = world.Resources.Get<RagdollInstances>().NextGroup();
        var root = BoneAttachments.WorldPose(entity);
        var rootMatrix = root.ToMatrix();
        var model = pose.ModelSpace;
        byte layer = world.TryGet<Collider>(entity, out var own) ? own.Layer : (byte)0;
        bool seeding = seed && CurrentTick - PreviousTick == 1 && world.Tick - CurrentTick <= 1 && RecordedDt > 0f;

        var bodyPoses = BodyPose;
        for (int i = 0; i < table.Count; i++)
        {
            var b = table.Bodies[i];
            var jointWorld = PoseMath.ToPose(model[b.Joint] * rootMatrix);
            JointWorld[i] = jointWorld;
            var p = new Pose
            {
                Position = jointWorld.Position + Vector3.Transform(b.Offset, jointWorld.Rotation),
                Rotation = Quaternion.Normalize(jointWorld.Rotation * b.Rotation),
                Scale = Vector3.One,
            };
            bodyPoses[i] = p;
            var collider = ShapeOf(b, layer);
            var rigid = new RigidBody { Kind = BodyKind.Dynamic, Mass = b.Mass, Friction = table.Friction };
            Bodies[i] = physics.AddBody(entity, collider, rigid, p, Group);
            if (seeding)
            {
                float dt = RecordedDt;
                physics.SetVelocity(Bodies[i], (CurrentPosition[i] - PreviousPosition[i]) / dt);
                physics.SetAngularVelocity(Bodies[i], AngularVelocity(PreviousRotation[i], CurrentRotation[i], dt));
            }
        }
        for (int i = 0; i < table.Count; i++)
        {
            var b = table.Bodies[i];
            Joints[i] = default;
            if (b.Parent < 0) continue;
            var a = bodyPoses[i];
            var parent = bodyPoses[b.Parent];
            var desc = JointDesc.FromWorld(b.JointKind == RagdollJointKind.Hinge ? JointKind.Hinge : JointKind.Ball, a, parent,
                                           JointWorld[i].Position, Vector3.Transform(b.Axis, JointWorld[i].Rotation));
            desc.Axis = Vector3.Normalize(Vector3.Transform(b.Axis, Quaternion.Conjugate(b.Rotation)));
            desc.Rest = Quaternion.Normalize(Quaternion.Conjugate(RestRotation[b.Parent]) * RestRotation[i]);
            if (b.JointKind == RagdollJointKind.Hinge)
            {
                desc.HingeMin = b.HingeMin;
                desc.HingeMax = b.HingeMax;
            }
            else
            {
                desc.Swing = b.Swing;
                desc.TwistMin = b.TwistMin;
                desc.TwistMax = b.TwistMax;
            }
            Joints[i] = physics.AddJoint(Bodies[i], Bodies[b.Parent], desc);
        }
        Built = true;
    }

    private static Collider ShapeOf(ResolvedRagdollBody b, byte layer) => b.Shape switch
    {
        RagdollShape.Sphere => Collider.Sphere(b.Size.X, layer),
        RagdollShape.Capsule => Collider.Capsule(b.Size.X, MathF.Max(b.Size.Y, 0.01f), layer),
        _ => Collider.Box(b.Size, layer),
    };

    // The rotation from `from` to `to` over `dt`, as radians per second about a world axis.
    internal static Vector3 AngularVelocity(Quaternion from, Quaternion to, float dt)
    {
        var delta = Quaternion.Normalize(to * Quaternion.Conjugate(from));
        if (delta.W < 0f) delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);
        var axis = new Vector3(delta.X, delta.Y, delta.Z);
        float sin = axis.Length();
        if (sin < 1e-6f) return Vector3.Zero;
        float angle = 2f * MathF.Atan2(sin, delta.W);
        return axis / sin * (angle / dt);
    }

    public int Nearest(IPhysicsWorld physics, Vector3 point)
    {
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < Bodies.Length; i++)
        {
            float d = Vector3.DistanceSquared(physics.PoseOf(Bodies[i]).Position, point);
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        return best;
    }

    public void Push(IPhysicsWorld physics, Vector3 impulse, Vector3? point)
    {
        if (!Built || Bodies.Length == 0) return;
        if (point is { } at)
        {
            int i = Nearest(physics, at);
            if (i >= 0) physics.ApplyImpulse(Bodies[i], impulse, at);
            return;
        }
        var dv = impulse / MathF.Max(TotalMass, 1e-3f);
        for (int i = 0; i < Bodies.Length; i++)
        {
            physics.SetVelocity(Bodies[i], physics.VelocityOf(Bodies[i]) + dv);
        }
    }

    // Takes the bodies (and with them the joints) out of physics.
    public void Remove(IPhysicsWorld physics)
    {
        if (!Built) return;
        for (int i = 0; i < Bodies.Length; i++)
        {
            physics.RemoveBody(Bodies[i]);
            Bodies[i] = default;
            Joints[i] = default;
        }
        Built = false;
    }
}

// The world's ragdolls, by entity (transient: the bodies are rebuilt after a load).
internal sealed class RagdollInstances
{
    private readonly Dictionary<int, RagdollInstance> _byEntity = new();
    private readonly List<int> _gone = new();
    private int _groups;

    public int Count => _byEntity.Count;

    public int NextGroup() => Ragdolls.FirstGroup + ++_groups;

    public RagdollInstance? Find(Entity entity) =>
        _byEntity.TryGetValue(entity.Id, out var instance) && instance.Entity == entity ? instance : null;

    public RagdollInstance For(Entity entity)
    {
        if (_byEntity.TryGetValue(entity.Id, out var instance))
        {
            if (instance.Entity == entity) return instance;
            // A reused id: whatever the old entity left is let go of first (Sweep normally has).
        }
        instance = new RagdollInstance { Entity = entity };
        _byEntity[entity.Id] = instance;
        return instance;
    }

    // Instances whose entity is gone or has lost its Ragdoll: their bodies are removed, and so are they.
    public void Sweep(World world, IPhysicsWorld? physics)
    {
        _gone.Clear();
        foreach (var (id, instance) in _byEntity)
            if (!world.IsAlive(instance.Entity) || !world.TryGet<Ragdoll>(instance.Entity, out var r) || (!r.Active && instance.Built))
                _gone.Add(id);
        foreach (var id in _gone)
        {
            var instance = _byEntity[id];
            if (physics != null) instance.Remove(physics);
            if (!world.IsAlive(instance.Entity) || !world.Has<Ragdoll>(instance.Entity)) _byEntity.Remove(id);
        }
    }
}

// Phase.Animation, after the animator: an idle ragdoll records where its bodies would be (for Start's
// velocities); an active one writes its pose from the bodies and moves its root under the pelvis.
// Allocation-free per tick once each ragdoll has been resolved.
[Experimental(RagdollApi.Experimental, UrlFormat = RagdollApi.Url)]
[System(Ragdolls.SystemId, Phase.Animation, After = new[] { Animators.SystemId })]
internal sealed class RagdollSystem : ISystem
{
    private const float GroundRay = 3f;

    private readonly World _world;
    private readonly Query<Ragdoll, Transform> _ragdolls;
    private readonly RagdollInstances _instances;

    public RagdollSystem(World world)
    {
        _world = world;
        _ragdolls = world.Query<Ragdoll, Transform>();
        _instances = world.Resources.GetOrAdd(static () => new RagdollInstances());
    }

    public void Run(in SystemContext ctx)
    {
        _world.Resources.TryGet<IPhysicsWorld>(out var physics);
        if (_instances.Count > 0) _instances.Sweep(_world, physics);
        if (!_world.Resources.TryGet<SkeletonPoses>(out var poses) || poses == null || poses.Count == 0) return;
        float dt = ctx.Tick.Dt;
        long tick = _world.Tick;
        foreach (var (ragdolls, transforms, entities) in _ragdolls.Chunks)
        {
            var r = ragdolls.Span;
            var t = transforms.Span;
            for (int n = 0; n < r.Length; n++)
            {
                var entity = entities.EntityAt(n);
                if (!poses.TryGet(entity, out var pose, out var model)) continue;
                ref var ragdoll = ref r[n];
                if (!ragdoll.Active)
                {
                    var idle = _instances.For(entity);
                    if (!idle.Prepare(_world, entity, ragdoll.Record, pose, model)) continue;
                    idle.Record(pose, BoneAttachments.WorldPose(entity), tick, dt);
                    continue;
                }
                if (physics == null) continue;
                var instance = _instances.For(entity);
                bool restored = false;
                if (!instance.Built)
                {
                    // A load: rebuild them from the pose as it stands (the joints' anchors and rest turns as
                    // Start made them), then put each body back as it was saved (#248).
                    if (!instance.Prepare(_world, entity, ragdoll.Record, pose, model)) continue;
                    pose.Local.CopyTo(instance.Handoff);
                    instance.Build(_world, physics, entity, pose, seed: false);
                    if (_world.TryGet<PhysicsBody>(entity, out var capsule)) physics.SetGroup(capsule, instance.Group);
                    restored = Ragdolls.RestoreBodies(physics, instance, ref ragdoll, entity);
                }
                // Settled and asleep, the pose written since: nothing moves (RagdollSettling.cs says why the pose may stay).
                else if (ragdoll.Settled && Ragdolls.IsQuiet(physics, instance)) continue;
                if (!restored)
                {
                    Damp(physics, instance, dt);
                    Ragdolls.Settle(_world, physics, instance, ref ragdoll, entity, dt);
                }
                Write(physics, instance, entity, ref t[n], pose);
                Ragdolls.Keep(physics, instance, ref ragdoll);
            }
        }
    }

    // The record's damping: the share of each awake body's speed lost per second.
    private static void Damp(IPhysicsWorld physics, RagdollInstance instance, float dt)
    {
        float damping = instance.Table!.Damping;
        if (!(damping > 0f)) return;
        float keep = MathF.Pow(1f - Math.Clamp(damping, 0f, 1f), dt);
        for (int i = 0; i < instance.Bodies.Length; i++)
        {
            ref readonly var body = ref instance.Bodies[i];
            if (!physics.IsAwake(body)) continue;
            physics.SetVelocity(body, physics.VelocityOf(body) * keep);
            physics.SetAngularVelocity(body, physics.AngularVelocityOf(body) * keep);
        }
    }

    private void Write(IPhysicsWorld physics, RagdollInstance instance, Entity entity, ref Transform transform, SkeletonPose pose)
    {
        var table = instance.Table!;
        // Each bodied joint's world pose, from its body.
        for (int i = 0; i < table.Count; i++)
        {
            var b = table.Bodies[i];
            var bp = physics.PoseOf(instance.Bodies[i]);
            var rotation = Quaternion.Normalize(bp.Rotation * Quaternion.Conjugate(b.Rotation));
            instance.JointWorld[i] = new Pose { Position = bp.Position - Vector3.Transform(b.Offset, rotation), Rotation = rotation, Scale = Vector3.One };
        }

        // The root follows the pelvis: under it on the ground, its own turn kept.
        if (entity.Parent.IsNull)
        {
            var pelvis = instance.JointWorld[0].Position;
            var hit = physics.Raycast(pelvis, -Vector3.UnitY, GroundRay, default, false, entity);
            float y = hit.Hit ? hit.Position.Y : pelvis.Y - instance.RestRootHeight * transform.LocalScale.Y;
            transform.LocalPosition = new Vector3(pelvis.X, y, pelvis.Z);
        }
        var root = BoneAttachments.WorldPose(entity);
        var inverseRoot = Quaternion.Conjugate(root.Rotation);
        var rootScale = root.Scale;

        var skeleton = pose.Skeleton;
        var parents = skeleton.Parents;
        var local = pose.Local;
        var model = instance.Model;
        for (int j = 0; j < local.Length; j++)
        {
            int parent = parents[j];
            int bi = table.BodyOfJoint(j);
            var l = instance.Handoff[j];
            if (bi >= 0)
            {
                var world = instance.JointWorld[bi];
                var modelRotation = inverseRoot * world.Rotation;
                if (parent < 0) l.Rotation = Quaternion.Normalize(modelRotation);
                else l.Rotation = Quaternion.Normalize(Quaternion.Conjugate(model[parent].Rotation) * modelRotation);
                if (table.Bodies[bi].Parent < 0)
                {
                    // The root body places the whole skeleton: its joint goes where the body is.
                    var modelPosition = Vector3.Transform(world.Position - root.Position, inverseRoot) / rootScale;
                    l.Position = parent < 0
                        ? modelPosition
                        : Vector3.Transform(modelPosition - model[parent].Position, Quaternion.Conjugate(model[parent].Rotation)) / model[parent].Scale;
                }
            }
            local[j] = l;
            model[j] = parent < 0 ? l : Pose.Combine(model[parent], l);
        }
        PoseSampler.ToModelSpace(skeleton, pose);
    }
}
