#nullable enable
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// FrameUpdate, before the director: every enabled FirstPersonRig puts its camera's CameraPose in the head
// of what it follows, at display rate, from the interpolated pose and the view angles the last command
// carried (06 §3.3, 16 §3.2). Issue #78: this was FirstPersonCameraSystem, which wrote ActiveCamera
// directly and flagged it DrivenByRig; now it is one camera entity's rig among others, and the director
// decides whether that camera draws the screen (and mirrors it into ActiveCamera; `cam_free`'s debug camera
// outranks it, #81). The character plugin installs it. Allocates nothing.
[Experimental("SAGE0123")]
[System(Id, Phase.FrameUpdate, Before = new[] { CameraDirector.Id })]
public sealed class FirstPersonRigSystem : ISystem
{
    public const string Id = "sage.camera.first_person";

    private readonly World _world;
    private readonly RecordStore _records;
    private readonly Query<FirstPersonRig, CameraPose> _rigs;

    public FirstPersonRigSystem(World world, RecordStore records)
    {
        _world = world;
        _records = records;
        _rigs = world.Query<FirstPersonRig, CameraPose>();
    }

    public void Run(in SystemContext ctx)
    {
        float alpha = ctx.Frame.Alpha;
        foreach (var (rigs, poses, _) in _rigs.Chunks)
        {
            var rig = rigs.Span;
            var pose = poses.Span;
            for (int i = 0; i < rig.Length; i++)
            {
                if (!rig[i].Enabled) continue;
                if (!CameraRigs.TryHead(_world, _records, rig[i].Follow, alpha, out var head, out var rotation)) continue;
                pose[i] = new CameraPose(head, rotation);
            }
        }
    }
}
