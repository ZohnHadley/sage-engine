#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// Which entity wears which pose (issue #120): the seam between whatever *writes* a skeleton's pose (the
// animation graph's Animator, #118; a test; a cut-scene player) and what *reads* it after the Animation
// phase (bone attachments, aim IK, foot IK, and #117's skinning at extract). A world resource, installed
// by the engine in every world.
//
// **The contract for a pose source** (docs/design/12 "As built (attachments and IK)"):
//   - register the pose once, when the entity gets one: `poses.Set(entity, pose, model)`, where `model` is
//     the skinned `.glb` it was made for (what `skeleton_sockets` records are keyed by); `Remove` it (and
//     Dispose the pose) when the entity loses it;
//   - every tick, by the end of Phase.Animation, rewrite pose.Local from scratch (sample, blend) and call
//     PoseSampler.ToModelSpace. IK in Phase.Late adjusts that result in place and keeps ModelSpace
//     current, so a source that did *not* rewrite Local each tick would see IK compound.
//
// Model space is the entity's own space: a joint's world transform is ModelSpace[j] times the entity's.
//
// Lookups are a dictionary by entity id and allocate nothing; a handle to a destroyed entity (or to an
// id since reused) is not found.
//
// **One registration feeds skinning too.** The engine makes each world's SkeletonPoses over its
// SkinPoses (#117's seam, what SkinnedMeshExtract draws from), and Set and Remove pass through to it, so
// a pose source registers once and the mesh is drawn in the pose IK left, at extract.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public sealed class SkeletonPoses
{
    private readonly Dictionary<int, Entry> _byEntity = new();
    private readonly SkinPoses? _skin;

    public SkeletonPoses() { }

    // The engine's: registrations pass through to the renderer's seam.
    internal SkeletonPoses(SkinPoses skin) => _skin = skin;

    private readonly struct Entry
    {
        public readonly Entity Entity;
        public readonly SkeletonPose Pose;
        public readonly AssetPath Model;

        public Entry(Entity entity, SkeletonPose pose, AssetPath model)
        {
            Entity = entity;
            Pose = pose;
            Model = model;
        }
    }

    public int Count => _byEntity.Count;

    // Registers (or replaces) the entity's pose. `model` may be empty: sockets then cannot be found by
    // model, and an attachment has to name a bone.
    public void Set(Entity entity, SkeletonPose pose, AssetPath model = default)
    {
        ArgumentNullException.ThrowIfNull(pose);
        if (entity.IsNull) throw new ArgumentException("A pose needs a live entity", nameof(entity));
        _byEntity[entity.Id] = new Entry(entity, pose, model);
        _skin?.Set(entity, pose);
    }

    // True when it was registered. Does not Dispose the pose: whoever made it owns it.
    public bool Remove(Entity entity)
    {
        if (!_byEntity.TryGetValue(entity.Id, out var entry) || !entry.Entity.Equals(entity)) return false;
        _byEntity.Remove(entity.Id);
        _skin?.Remove(entity);
        return true;
    }

    public bool TryGet(Entity entity, [NotNullWhen(true)] out SkeletonPose? pose) => TryGet(entity, out pose, out _);

    // The pose and the model it was registered with.
    public bool TryGet(Entity entity, [NotNullWhen(true)] out SkeletonPose? pose, out AssetPath model)
    {
        if (!entity.IsNull && _byEntity.TryGetValue(entity.Id, out var entry) && entry.Entity.Equals(entity) && !entry.Entity.IsNull
            && entry.Pose.JointCount > 0)
        {
            pose = entry.Pose;
            model = entry.Model;
            return true;
        }
        pose = null;
        model = default;
        return false;
    }
}
