#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Sage.Simulation;

// The experimental id and link every public type of ragdolls carries (phase 4k, issue #243,
// docs/MAKING_A_GAME.md §10b): the record is the first piece; the bodies, joints and the Ragdoll
// component that build on it follow in #244 onwards and may change what this describes.
internal static class RagdollApi
{
    internal const string Experimental = "SAGE0134";
    internal const string Url = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api";
}

// The shape of one ragdoll body. `None` is what a body that left its shape out gets: a load error.
[Experimental(RagdollApi.Experimental, UrlFormat = RagdollApi.Url)]
public enum RagdollShape { None, Capsule, Box, Sphere }

[Experimental(RagdollApi.Experimental, UrlFormat = RagdollApi.Url)]
public enum RagdollJointKind { Ball, Hinge }

// How a body hangs from the nearest bodied ancestor, at its own joint's origin. A ball joint swings
// within a cone about `Axis` and twists about it between `TwistMin` and `TwistMax`; a hinge turns about
// `Axis` between `Min` and `Max`. The root body of a ragdoll (the pelvis) has no joint: whatever is here is ignored.
[Experimental(RagdollApi.Experimental, UrlFormat = RagdollApi.Url)]
public sealed class RagdollJointData
{
    [Property(Tooltip = "Ball: swings in a cone and twists. Hinge: turns about one axis between min and max")]
    public RagdollJointKind Kind;
    [Property(Min = 0, Max = 180, Unit = "deg", Tooltip = "Ball: the cone's half-angle the bone may swing from where it hangs")]
    public float Swing = 45f;
    [Property(Min = -180, Max = 180, Unit = "deg", Tooltip = "Ball: how far it may twist one way about the axis")]
    public float TwistMin = -30f;
    [Property(Min = -180, Max = 180, Unit = "deg", Tooltip = "Ball: how far it may twist the other way about the axis")]
    public float TwistMax = 30f;
    [Property(Tooltip = "In the joint's space: the hinge's axis, or a ball's twist axis; (0, 0, 0) = the bone's own direction, +Y")]
    public Vector3 Axis = Vector3.UnitY;
    [Property(Min = -180, Max = 180, Unit = "deg", Tooltip = "Hinge: the lowest angle about the axis")]
    public float Min = -90f;
    [Property(Min = -180, Max = 180, Unit = "deg", Tooltip = "Hinge: the highest angle about the axis")]
    public float Max = 90f;
}

// One body of a ragdoll: a shape on a joint, and the joint that links it up.
[Experimental(RagdollApi.Experimental, UrlFormat = RagdollApi.Url)]
public sealed class RagdollBodyData
{
    [Property(Tooltip = "Capsule, Box or Sphere")]
    public RagdollShape Shape;
    [Property(Unit = "m", Tooltip = "Capsule: radius, length of the cylinder between the caps. Box: full extents. Sphere: radius")]
    public Vector3 Size;
    [Property(Unit = "m", Tooltip = "The body's centre in the joint's space")]
    public Vector3 Offset;
    [Property(Unit = "deg", Tooltip = "How the shape is turned in the joint's space: pitch, yaw, roll")]
    public Vector3 Angles;
    [Property(Min = 0, Unit = "kg", Tooltip = "Its mass")]
    public float Mass;
    [Property(Tooltip = "The joint to the nearest ancestor joint that has a body; none on the root body")]
    public RagdollJointData Joint = new();
}

// The ragdoll of one model (phase 4k, docs/design/12 "As built (the ragdoll record, issue #243)"): the
// bodies that stand in for its bones when it falls, keyed by joint name, as `skeleton_sockets` keys
// sockets by model. Several records may name the same model (a mod adds a tail or a cape's bodies): a
// body is looked up in each, in id order, and one whose name is already taken is skipped; the
// record-level settings come from the first.
//
//   { "type": "ragdoll", "id": "knight", "model": "models/knight.glb", "friction": 0.7,
//     "bodies": { "pelvis": { "shape": "Box", "size": [0.3, 0.16, 0.2], "mass": 10 },
//                 "thigh_l": { "shape": "Capsule", "size": [0.075, 0.27, 0], "offset": [0, -0.21, 0], "mass": 8,
//                              "joint": { "kind": "Ball", "swing": 70, "twistMin": -30, "twistMax": 30, "axis": [0, -1, 0] } },
//                 "shin_l":  { "shape": "Capsule", "size": [0.06, 0.30, 0], "offset": [0, -0.21, 0], "mass": 4,
//                              "joint": { "kind": "Hinge", "axis": [-1, 0, 0], "min": 0, "max": 140 } } } }
[Experimental(RagdollApi.Experimental, UrlFormat = RagdollApi.Url)]
[Record("ragdoll", Plugin = RegistrationOwners.Core)]
public sealed class RagdollRecord
{
    [Property(Tooltip = "The skinned model (.glb) whose skeleton these bodies are on")]
    [AssetKind("mesh")] public AssetPath Model;
    [Property(Tooltip = "Bodies by joint name, as the model writes it (\"thigh_l\")")]
    public Dictionary<string, RagdollBodyData> Bodies = new(StringComparer.Ordinal);
    [Property(Min = 0, Tooltip = "Friction of every body")]
    public float Friction = 0.6f;
    [Property(Min = 0, Max = 1, Tooltip = "Linear and angular damping of every body: the share of its speed lost each second")]
    public float Damping = 0.1f;
    [Property(Min = 0, Unit = "m/s", Tooltip = "Slower than this, a body is at rest")]
    public float SettleSpeed = 0.15f;
    [Property(Min = 0, Unit = "s", Tooltip = "Every body at rest this long and the ragdoll has settled")]
    public float SettleTime = 1f;
}

// A body as Ragdolls.Resolve found it: a joint of a skeleton, no strings left to look up, angles in radians.
[Experimental(RagdollApi.Experimental, UrlFormat = RagdollApi.Url)]
public sealed class ResolvedRagdollBody
{
    internal ResolvedRagdollBody(string name) => Name = name;

    public string Name { get; }
    // The skeleton joint the body follows.
    public int Joint { get; internal set; }
    // The index (in the table) of the body this one is jointed to, or -1 for a root; always smaller than this body's own.
    public int Parent { get; internal set; } = -1;
    public RagdollShape Shape { get; internal set; }
    public Vector3 Size { get; internal set; }
    public Vector3 Offset { get; internal set; }
    // The shape's turn in the joint's space.
    public Quaternion Rotation { get; internal set; } = Quaternion.Identity;
    public float Mass { get; internal set; }
    public RagdollJointKind JointKind { get; internal set; }
    // Unit length, in the joint's space.
    public Vector3 Axis { get; internal set; } = Vector3.UnitY;
    public float Swing { get; internal set; }
    public float TwistMin { get; internal set; }
    public float TwistMax { get; internal set; }
    public float HingeMin { get; internal set; }
    public float HingeMax { get; internal set; }
}

// A model's ragdoll resolved against its skeleton: bodies in joint order (so a parent always comes before its children).
[Experimental(RagdollApi.Experimental, UrlFormat = RagdollApi.Url)]
public sealed class RagdollTable
{
    private readonly ResolvedRagdollBody[] _bodies;
    private readonly int[] _bodyOfJoint;

    internal RagdollTable(ResolvedRagdollBody[] bodies, int jointCount, float friction, float damping, float settleSpeed, float settleTime)
    {
        _bodies = bodies;
        _bodyOfJoint = new int[jointCount];
        Array.Fill(_bodyOfJoint, -1);
        for (int i = 0; i < bodies.Length; i++) _bodyOfJoint[bodies[i].Joint] = i;
        Friction = friction;
        Damping = damping;
        SettleSpeed = settleSpeed;
        SettleTime = settleTime;
    }

    public IReadOnlyList<ResolvedRagdollBody> Bodies => _bodies;
    public int Count => _bodies.Length;
    public float Friction { get; }
    public float Damping { get; }
    public float SettleSpeed { get; }
    public float SettleTime { get; }

    // The body that follows `joint`, or -1.
    public int BodyOfJoint(int joint) => (uint)joint < (uint)_bodyOfJoint.Length ? _bodyOfJoint[joint] : -1;
}

// Looking a ragdoll up and resolving it (pure data: the bodies themselves are built from the table by the physics side).
[Experimental(RagdollApi.Experimental, UrlFormat = RagdollApi.Url)]
public static class Ragdolls
{
    private sealed class Cached
    {
        public RagdollRecord[] Records = Array.Empty<RagdollRecord>();
        public RagdollTable Table = null!;
    }

    // A skeleton's tables by model; valid while the records they came from are the ones loaded (a reload makes new objects).
    private static readonly ConditionalWeakTable<Skeleton, Dictionary<AssetPath, Cached>> Tables = new();

    // The ragdoll of `model` on `skeleton`, from every `ragdoll` record for the model in id order; null when there is none.
    // A body that names a joint the skeleton lacks is skipped with a warning (Animation category). The table is cached
    // per skeleton, model and records, so asking again is cheap and warns once; it still walks the records (call it when
    // building a ragdoll, never per tick).
    public static RagdollTable? Resolve(RecordStore records, Skeleton skeleton, AssetPath model)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(skeleton);
        var found = Find(records, model).ToArray();
        if (found.Length == 0) return null;
        var perSkeleton = Tables.GetValue(skeleton, _ => new Dictionary<AssetPath, Cached>());
        lock (perSkeleton)
        {
            if (perSkeleton.TryGetValue(model, out var cached) && cached.Records.AsSpan().SequenceEqual(found)) return cached.Table;
            var table = Resolve(skeleton, found, model);
            perSkeleton[model] = new Cached { Records = found, Table = table };
            return table;
        }
    }

    // The same, over records in hand and not cached.
    public static RagdollTable Resolve(Skeleton skeleton, IReadOnlyList<RagdollRecord> records) => Resolve(skeleton, records, default);

    private static RagdollTable Resolve(Skeleton skeleton, IReadOnlyList<RagdollRecord> records, AssetPath model)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(records);
        const float ToRadians = MathF.PI / 180f;
        var byJoint = new SortedDictionary<int, ResolvedRagdollBody>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
            foreach (var (name, data) in record.Bodies)
            {
                if (data == null || !taken.Add(name)) continue;
                int joint = skeleton.IndexOf(name);
                if (joint < 0)
                {
                    Log.Warn(LogCat.Animation, $"ragdoll{(model.IsEmpty ? "" : $" of '{model.Path}'")}: the skeleton has no joint '{name}'; that body is skipped");
                    continue;
                }
                var j = data.Joint ?? new RagdollJointData();
                var axis = j.Axis.LengthSquared() > 1e-8f ? Vector3.Normalize(j.Axis) : Vector3.UnitY;
                byJoint[joint] = new ResolvedRagdollBody(name)
                {
                    Joint = joint,
                    Shape = data.Shape,
                    Size = data.Size,
                    Offset = data.Offset,
                    Rotation = Quaternion.CreateFromYawPitchRoll(data.Angles.Y * ToRadians, data.Angles.X * ToRadians, data.Angles.Z * ToRadians),
                    Mass = data.Mass,
                    JointKind = j.Kind,
                    Axis = axis,
                    Swing = j.Swing * ToRadians,
                    TwistMin = j.TwistMin * ToRadians,
                    TwistMax = j.TwistMax * ToRadians,
                    HingeMin = j.Min * ToRadians,
                    HingeMax = j.Max * ToRadians,
                };
            }

        var bodies = new ResolvedRagdollBody[byJoint.Count];
        byJoint.Values.CopyTo(bodies, 0);
        var parents = skeleton.Parents;
        var indexOfJoint = new Dictionary<int, int>();
        for (int i = 0; i < bodies.Length; i++) indexOfJoint[bodies[i].Joint] = i;
        foreach (var body in bodies)
            for (int p = parents[body.Joint]; p >= 0; p = parents[p])
                if (indexOfJoint.TryGetValue(p, out var parent)) { body.Parent = parent; break; }

        var first = records.Count > 0 ? records[0] : new RagdollRecord();
        return new RagdollTable(bodies, skeleton.JointCount, first.Friction, first.Damping, first.SettleSpeed, first.SettleTime);
    }

    private static List<RagdollRecord> Find(RecordStore records, AssetPath model)
    {
        var found = new List<RagdollRecord>();
        if (model.IsEmpty) return found;
        foreach (var record in records.All<RagdollRecord>())
            if (record.Model == model) found.Add(record);
        return found;
    }

    // Content checks (issue #22's rule: a mistake is a load error, at its line).
    internal static void Check(RagdollRecord record, RecordCheck check)
    {
        if (record.Model.IsEmpty) check.Error("model", "names no model: ragdolls are looked up by the model their skeleton comes from");
        if (record.Bodies.Count == 0) check.Error("bodies", "has no bodies");
        foreach (var (name, body) in record.Bodies)
        {
            string at = $"bodies.{name}";
            if (body == null) { check.Error(at, "is empty"); continue; }
            if (body.Shape == RagdollShape.None) check.Error($"{at}.shape", "names no shape: capsule, box or sphere");
            else if (!SizeIsValid(body.Shape, body.Size))
                check.Error($"{at}.size", "must be positive (capsule: radius, then cylinder length; box: three extents; sphere: radius)");
            if (!(body.Mass > 0)) check.Error($"{at}.mass", "must be more than 0 kg");
            var j = body.Joint;
            if (j == null) continue;
            if (!(j.Swing >= 0 && j.Swing <= 180)) check.Error($"{at}.joint.swing", "must be between 0 and 180 degrees");
            if (j.TwistMin > j.TwistMax) check.Error($"{at}.joint.twistMin", "is more than twistMax");
            if (j.Min > j.Max) check.Error($"{at}.joint.min", "is more than max");
        }
    }

    private static bool SizeIsValid(RagdollShape shape, Vector3 size) => shape switch
    {
        RagdollShape.Sphere => size.X > 0,
        RagdollShape.Capsule => size.X > 0 && size.Y >= 0,
        _ => size.X > 0 && size.Y > 0 && size.Z > 0,
    };
}
