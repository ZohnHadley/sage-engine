#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using SharpGLTF.Schema2;

namespace Sage.Simulation;

// What a skinned `.glb` holds for animation: its skeleton and every clip made for it.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public sealed class AnimationSet
{
    private readonly AnimationClip[] _clips;

    public AnimationSet(string source, Skeleton skeleton, AnimationClip[] clips)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(clips);
        Source = source;
        Skeleton = skeleton;
        _clips = (AnimationClip[])clips.Clone();
    }

    // Where it was read from ("mount:path"), for messages.
    public string Source { get; }
    public Skeleton Skeleton { get; }
    public IReadOnlyList<AnimationClip> Clips => _clips;

    // Null when there is no clip of that name (ordinal).
    public AnimationClip? FindClip(string name)
    {
        foreach (var clip in _clips)
            if (string.Equals(clip.Name, name, StringComparison.Ordinal)) return clip;
        return null;
    }
}

// Reads a skin and its clips from a `.glb` (docs/design/12 §3, issue #116), through the VFS, with
// SharpGLTF, and keeps what it read by AssetPath.
//
// **Loading is content-time, never per tick.** Load reads the file the first time a path is asked for
// (at spawn, when a record is checked, or up front) and remembers the answer, a failure included, so a
// broken file warns once. A system that runs every tick asks TryGet, which only looks in the cache: it
// never opens a file, and it allocates nothing.
//
// A bad or missing file is a content problem, not a crash: it is logged as a warning (LogCat.Animation)
// and Load returns null.
//
// What is read: the first skin in the file (its joints, their parents, their rest TRS and inverse bind
// matrices; a missing inverse bind accessor means identity, as glTF says), and every animation's
// translation, rotation and scale channels that target one of its joints. Channels on other nodes
// (the mesh, a camera) and morph weights are skipped. A joint whose parent is not a joint is a root;
// the transforms of non-joint nodes above it are not applied (glTF would), which is logged.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public sealed class GltfAnimationReader
{
    private readonly VirtualFileSystem _vfs;
    private readonly Dictionary<AssetPath, AnimationSet?> _cache = new();
    private RecordStore? _events;

    public GltfAnimationReader(VirtualFileSystem vfs)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        _vfs = vfs;
    }

    // The engine's reader gives the clips it loads the events of the `anim_events` records for their
    // model (issue #119), and gives them again after every records reload.
    internal void UseEvents(RecordStore records)
    {
        _events = records;
        records.Reloaded += ApplyEventsToAll;
    }

    private void ApplyEventsToAll()
    {
        if (_events == null) return;
        foreach (var (path, set) in _cache)
            if (set != null) AnimEvents.Apply(_events, path, set);
    }

    // Paths asked for so far, including those that failed.
    public int Count => _cache.Count;

    // Reads the file the first time, then answers from the cache. Null (after one warning) when the file
    // is missing, is not a .glb or has no skin.
    public AnimationSet? Load(AssetPath path)
    {
        if (_cache.TryGetValue(path, out var cached)) return cached;
        var set = ReadFromVfs(path);
        if (set != null && _events != null) AnimEvents.Apply(_events, path, set);
        _cache[path] = set;
        return set;
    }

    // The cache only: never reads a file (a tick may call it). False when the path was never loaded or
    // failed to.
    public bool TryGet(AssetPath path, [NotNullWhen(true)] out AnimationSet? set)
    {
        if (_cache.TryGetValue(path, out set) && set != null) return true;
        set = null;
        return false;
    }

    // Drops a path, so the next Load reads the file again (hot reload).
    public bool Forget(AssetPath path) => _cache.Remove(path);

    private AnimationSet? ReadFromVfs(AssetPath path)
    {
        if (path.IsEmpty)
        {
            Log.Warn(LogCat.Animation, "Skinned model: no path given");
            return null;
        }
        var file = path.Path;
        if (_vfs.Which(file) is not { } mount)
        {
            Log.Warn(LogCat.Animation, $"Skinned model '{file}': not in any mount");
            return null;
        }
        string source = $"{mount.Name}:{file}";
        try
        {
            using var stream = mount.Open(file);
            return Read(stream, source);
        }
        catch (IOException ex)
        {
            Log.Warn(LogCat.Animation, $"Skinned model {source}: could not be read ({ex.Message})");
            return null;
        }
    }

    // Reads a skeleton and its clips from a stream holding a `.glb`, without the cache. `name` is for
    // messages. Null, after a warning, when it cannot.
    public static AnimationSet? Read(Stream stream, string name)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(name);
        ModelRoot root;
        try
        {
            // Binary only, as MeshGeometry.ReadGlb: a text .gltf keeps its buffers beside it (05 §8).
            root = ModelRoot.ReadGLB(stream, new ReadSettings());
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat.Animation, $"Skinned model {name}: not a .glb ({ex.GetType().Name}: {ex.Message})");
            return null;
        }

        try
        {
            return Build(root, name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An accessor of the wrong type, a rotation channel of shorts SharpGLTF cannot decode, a
            // skeleton that breaks the order: a content problem, logged, as the header was.
            Log.Warn(LogCat.Animation, $"Skinned model {name}: could not read its skin or clips ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    private static AnimationSet? Build(ModelRoot root, string name)
    {
        if (root.LogicalSkins.Count == 0)
        {
            Log.Warn(LogCat.Animation, $"Skinned model {name}: has no skin");
            return null;
        }
        var skin = root.LogicalSkins[0];
        if (root.LogicalSkins.Count > 1)
            Log.Debug(LogCat.Animation, $"Skinned model {name}: {root.LogicalSkins.Count} skins; reading the first, '{skin.Name}'");

        int count = skin.JointsCount;
        if (count == 0)
        {
            Log.Warn(LogCat.Animation, $"Skinned model {name}: its skin has no joints");
            return null;
        }

        // Skin index of each joint node, and each joint's nearest joint ancestor (in skin indices).
        var skinIndexOfNode = new Dictionary<int, int>(count);
        var nodes = new Node[count];
        var inverseBind = new Matrix4x4[count];
        for (int s = 0; s < count; s++)
        {
            var (node, ibm) = skin.GetJoint(s);
            if (!skinIndexOfNode.TryAdd(node.LogicalIndex, s))
            {
                Log.Warn(LogCat.Animation, $"Skinned model {name}: its skin lists node '{node.Name}' twice");
                return null;
            }
            nodes[s] = node;
            inverseBind[s] = ibm;
        }

        var parentSkin = new int[count];
        int ignoredAncestors = 0;
        for (int s = 0; s < count; s++)
        {
            parentSkin[s] = -1;
            for (var up = nodes[s].VisualParent; up != null; up = up.VisualParent)
            {
                if (skinIndexOfNode.TryGetValue(up.LogicalIndex, out int p)) { parentSkin[s] = p; break; }
                if (!up.LocalMatrix.IsIdentity) ignoredAncestors++;
            }
        }
        if (ignoredAncestors > 0)
            Log.Warn(LogCat.Animation, $"Skinned model {name}: {ignoredAncestors} non-joint node(s) above its joints have a transform, which is not applied " +
                                       "(apply it in the exporter, or make the node a joint)");

        // Parents first: the file's order when it already is, otherwise each joint after its ancestors.
        var order = new List<int>(count);
        var placed = new bool[count];
        for (int s = 0; s < count; s++) Place(s, parentSkin, placed, order);
        var jointOfSkin = new int[count];
        for (int j = 0; j < count; j++) jointOfSkin[order[j]] = j;

        var names = new string[count];
        var parents = new int[count];
        var rest = new Pose[count];
        var ibms = new Matrix4x4[count];
        for (int j = 0; j < count; j++)
        {
            int s = order[j];
            var node = nodes[s];
            names[j] = string.IsNullOrEmpty(node.Name) ? $"joint{s}" : node.Name;
            parents[j] = parentSkin[s] < 0 ? -1 : jointOfSkin[parentSkin[s]];
            rest[j] = RestOf(node, name);
            ibms[j] = inverseBind[s];
        }
        var skeleton = new Skeleton(names, parents, rest, ibms, jointOfSkin);

        var jointOfNode = new Dictionary<int, int>(count);
        foreach (var (nodeIndex, s) in skinIndexOfNode) jointOfNode[nodeIndex] = jointOfSkin[s];

        var clips = new List<AnimationClip>(root.LogicalAnimations.Count);
        var clipNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var animation in root.LogicalAnimations)
        {
            string clipName = string.IsNullOrEmpty(animation.Name) ? $"clip{animation.LogicalIndex}" : animation.Name;
            if (!clipNames.Add(clipName))
            {
                Log.Warn(LogCat.Animation, $"Skinned model {name}: two clips are called '{clipName}'; the second is skipped");
                continue;
            }
            clips.Add(ReadClip(animation, clipName, count, jointOfNode, name));
        }

        Log.Debug(LogCat.Animation, $"Skinned model {name}: {count} joints, {clips.Count} clip(s)");
        return new AnimationSet(name, skeleton, clips.ToArray());
    }

    private static void Place(int s, int[] parentSkin, bool[] placed, List<int> order)
    {
        if (placed[s]) return;
        placed[s] = true;   // before the parent, so a cycle (a malformed file) cannot recurse for ever
        if (parentSkin[s] >= 0) Place(parentSkin[s], parentSkin, placed, order);
        order.Add(s);
    }

    private static Pose RestOf(Node node, string name)
    {
        var local = node.LocalTransform;
        if (!local.IsMatrix)
            return new Pose { Position = local.Translation, Rotation = Quaternion.Normalize(local.Rotation), Scale = local.Scale };
        if (Matrix4x4.Decompose(node.LocalMatrix, out var scale, out var rotation, out var translation))
            return new Pose { Position = translation, Rotation = Quaternion.Normalize(rotation), Scale = scale };
        Log.Warn(LogCat.Animation, $"Skinned model {name}: joint '{node.Name}' has a matrix that is not a rotation, scale and translation; using identity");
        return Pose.Identity;
    }

    private static AnimationClip ReadClip(SharpGLTF.Schema2.Animation animation, string clipName, int joints,
                                          Dictionary<int, int> jointOfNode, string name)
    {
        var clip = new AnimationClip(clipName, joints, Math.Max(0f, animation.Duration));
        int skipped = 0;
        foreach (var channel in animation.Channels)
        {
            var node = channel.TargetNode;
            if (node == null || !jointOfNode.TryGetValue(node.LogicalIndex, out int joint)) { skipped++; continue; }
            switch (channel.TargetNodePath)
            {
                case PropertyPath.translation:
                    ReadVectors(channel.GetTranslationSampler(), out var tMode, out var tTimes, out var tValues);
                    clip.SetTranslation(joint, tMode, tTimes, tValues);
                    break;
                case PropertyPath.scale:
                    ReadVectors(channel.GetScaleSampler(), out var sMode, out var sTimes, out var sValues);
                    clip.SetScale(joint, sMode, sTimes, sValues);
                    break;
                case PropertyPath.rotation:
                    ReadRotations(channel.GetRotationSampler(), out var rMode, out var rTimes, out var rValues);
                    clip.SetRotation(joint, rMode, rTimes, rValues);
                    break;
                default:
                    skipped++;
                    break;
            }
        }
        if (skipped > 0)
            Log.Debug(LogCat.Animation, $"Skinned model {name}: clip '{clipName}' has {skipped} channel(s) on no joint or not a transform; skipped");
        return clip;
    }

    private static AnimationInterpolation ModeOf(AnimationInterpolationMode mode) => mode switch
    {
        AnimationInterpolationMode.STEP => AnimationInterpolation.Step,
        AnimationInterpolationMode.CUBICSPLINE => AnimationInterpolation.CubicSpline,
        _ => AnimationInterpolation.Linear,
    };

    private static void ReadVectors(IAnimationSampler<Vector3> sampler, out AnimationInterpolation mode, out float[] times, out Vector3[] values)
    {
        mode = ModeOf(sampler.InterpolationMode);
        var t = new List<float>();
        var v = new List<Vector3>();
        if (mode == AnimationInterpolation.CubicSpline)
        {
            foreach (var (time, (tangentIn, value, tangentOut)) in sampler.GetCubicKeys())
            {
                t.Add(time); v.Add(tangentIn); v.Add(value); v.Add(tangentOut);
            }
        }
        else
        {
            foreach (var (time, value) in sampler.GetLinearKeys()) { t.Add(time); v.Add(value); }
        }
        times = t.ToArray();
        values = v.ToArray();
    }

    private static void ReadRotations(IAnimationSampler<Quaternion> sampler, out AnimationInterpolation mode, out float[] times, out Quaternion[] values)
    {
        mode = ModeOf(sampler.InterpolationMode);
        var t = new List<float>();
        var v = new List<Quaternion>();
        if (mode == AnimationInterpolation.CubicSpline)
        {
            foreach (var (time, (tangentIn, value, tangentOut)) in sampler.GetCubicKeys())
            {
                t.Add(time); v.Add(tangentIn); v.Add(value); v.Add(tangentOut);
            }
        }
        else
        {
            foreach (var (time, value) in sampler.GetLinearKeys()) { t.Add(time); v.Add(value); }
        }
        times = t.ToArray();
        values = v.ToArray();
    }
}
