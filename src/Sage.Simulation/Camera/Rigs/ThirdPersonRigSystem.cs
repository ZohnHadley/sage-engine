#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// FrameUpdate, before the director and after the first-person rig: every enabled ThirdPersonRig puts its
// camera over its pawn's shoulder and keeps it out of the walls (issue #79). The character plugin
// installs it. Allocates nothing.
//
// **Collision is a query outside the tick, and that is allowed.** The physics world is single-threaded
// and only steps inside a fixed tick (Phase.Physics); FrameUpdate runs after the frame's ticks, on the
// same thread, so a sweep here reads a settled world, as AI vision's raycasts do in theirs. What it reads
// is the *last tick's* bodies while the camera follows the pose interpolated between the last two: at
// most one tick apart, which for a camera probe (a quarter-metre sphere) is well inside its radius at any
// speed a character moves. A world without physics has no probe, and the camera sits at full distance.
[Experimental("SAGE0123")]
[System(Id, Phase.FrameUpdate, After = new[] { FirstPersonRigSystem.Id }, Before = new[] { CameraDirector.Id })]
public sealed class ThirdPersonRigSystem : ISystem
{
    public const string Id = "sage.camera.third_person";

    // Shorter than this and there is no boom to speak of: the camera sits in the head.
    private const float MinBoom = 1e-4f;

    private readonly World _world;
    private readonly RecordStore _records;
    private readonly Query<ThirdPersonRig, CameraPose> _rigs;
    private IPhysicsWorld? _physics;

    public ThirdPersonRigSystem(World world, RecordStore records)
    {
        _world = world;
        _records = records;
        _rigs = world.Query<ThirdPersonRig, CameraPose>();
    }

    public void Run(in SystemContext ctx)
    {
        // Installed by the physics plugin, which may come after this one is built: looked up until found.
        if (_physics == null) _world.Resources.TryGet(out _physics);

        float alpha = ctx.Frame.Alpha;
        float dt = ctx.Frame.Dt;
        foreach (var (rigs, poses, _) in _rigs.Chunks)
        {
            var rig = rigs.Span;
            var pose = poses.Span;
            for (int i = 0; i < rig.Length; i++)
            {
                ref var r = ref rig[i];
                if (!r.Enabled)
                {
                    r.Settled = false;   // switched back on, it starts where the probe allows
                    continue;
                }
                if (!CameraRigs.TryHead(_world, _records, r.Follow, alpha, out var head, out var rotation)) continue;
                pose[i] = new CameraPose(Place(ref r, head, rotation, dt), rotation);
            }
        }
    }

    // Where the camera goes this frame, and the rig's eased boom updated.
    private Vector3 Place(ref ThirdPersonRig rig, Vector3 head, Quaternion rotation, float dt)
    {
        var offset = rig.ShoulderOffset + new Vector3(0f, 0f, MathF.Max(rig.Distance, 0f));
        var boom = Vector3.Transform(offset, rotation);
        float length = boom.Length();
        if (!(length > MinBoom))
        {
            rig.Boom = 0f;
            rig.Settled = true;
            return head;
        }
        var direction = boom / length;

        // How far the probe gets: a hit pulls the camera in to where the sphere touched.
        float allowed = length;
        if (_physics != null && rig.ProbeRadius > 0f)
        {
            var probe = Collider.Sphere(rig.ProbeRadius);
            var from = new Pose { Position = head, Rotation = Quaternion.Identity, Scale = Vector3.One };
            var hit = _physics.Sweep(probe, from, direction, length, rig.Collision, ignore: rig.Follow);
            if (hit.Hit) allowed = Math.Clamp(hit.Distance, 0f, length);
        }

        // In at once, out eased: a camera behind a wall shows the wall, and one that springs back the
        // moment the wall is passed jolts.
        float eased;
        if (!rig.Settled || allowed <= rig.Boom || rig.Smoothing <= 0f) eased = allowed;
        else eased = rig.Boom + (allowed - rig.Boom) * (1f - MathF.Exp(-MathF.Max(dt, 0f) / rig.Smoothing));
        rig.Boom = MathF.Min(eased, length);
        rig.Settled = true;
        return head + direction * rig.Boom;
    }
}
