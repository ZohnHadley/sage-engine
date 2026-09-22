using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// The temporary bunny test scene (moves to games/Sandbox in migration step 5, docs/design/01 §10).

// Tag: turn to face the camera every tick.
internal struct FacesCamera : ITag { }

// World resource: where the camera is, updated by the host every frame.
internal sealed class CameraTarget
{
    public Vector3 Position;
}

// Gameplay phase (Fixed): turns FacesCamera entities towards the camera. Running at the tick rate
// and drawn interpolated, it also demonstrates the loop: try `sim_tickrate 5` and the bunny still
// turns smoothly. (Sprite billboarding proper is a renderer job; docs/design/06 §3.8.)
internal sealed class FaceCameraSystem : ISystem
{
    private readonly ArchetypeQuery<Transform> _facing;

    public FaceCameraSystem(World world)
    {
        _facing = world.Query<Transform>().AllTags(Tags.Get<FacesCamera>());
    }

    public void Run(in SystemContext ctx)
    {
        Vector3 camera = ctx.World.Resources.Get<CameraTarget>().Position;
        foreach (var (transforms, _) in _facing.Chunks)
        {
            var t = transforms.Span;
            for (int n = 0; n < t.Length; n++)
                TransformMath.Billboard(ref t[n], camera);
        }
    }
}
