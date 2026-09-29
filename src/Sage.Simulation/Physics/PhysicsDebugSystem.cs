#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// Physics drawn into the world (docs/design/06 §3.2, 10 §9): one bool test when its cvar is off.

// Late phase: every collider in the world, in its real place — drawn by the backend from what it
// actually simulates (IPhysicsWorld.DrawDebug, issue #30), so brush hulls show up as well as prefab
// colliders, a 2D backend draws through the same cvar, and a body out of step with its transform shows
// where physics thinks it is. Triggers are magenta, which is how a volume that should block (or a
// capsule sunk into the ground, review #44) shows up at a glance.
[System("sage.physics.debug", Phase.Late)]
public sealed class PhysicsDebugSystem : ISystem
{
    private readonly Query<Transform, CharacterController> _characters;
    private readonly DebugDraw _debug;
    private readonly RecordStore _records;
    private readonly World _world;
    private IPhysicsWorld? _physics;
    private ActiveCamera? _camera;
    private readonly CVar<bool> _enabled;

    // Far enough to cover what you are looking at, near enough that a streamed-in city doesn't turn
    // into a wireframe soup (and a line budget, 06 §3.2).
    private const float DrawRange = 35f;

    // The cvar is the physics module's, registered once at Init: a system lives per world, and a game
    // with an overworld and a battle scene has two of those (review #57).
    public PhysicsDebugSystem(World world, RecordStore records, CVar<bool> enabled)
    {
        _characters = world.Query<Transform, CharacterController>();
        _debug = world.Debug();
        _world = world;
        _records = records;
        _enabled = enabled;
    }

    public void Run(in SystemContext ctx)
    {
        if (!_enabled.Value || !_debug.Enabled) return;

        // Only what is near the camera; everything, in a world with no camera to be near (issue #13).
        if (_camera == null) _world.Resources.TryGet(out _camera);
        Vector3 around = _camera?.Position ?? Vector3.Zero;
        float range = _camera != null ? DrawRange : float.PositiveInfinity;

        if (_physics == null) _world.Resources.TryGet(out _physics);
        _physics?.DrawDebug(_debug, around, range);

        // A character's capsule is not its collider — it is what the controller sweeps — so it gets
        // drawn from the controller's own numbers, along with the ground it thinks it is standing on.
        foreach (var (transforms, characters, _) in _characters.Chunks)
        {
            var t = transforms.Span;
            var c = characters.Span;
            for (int n = 0; n < t.Length; n++)
            {
                if (SageMath.DistanceXZ(t[n].LocalPosition, around) > range) continue;
                var profile = CharacterConventions.Of(ctx.World).ProfileOf(_records, c[n].Profile);
                float height = c[n].Height > 0f ? c[n].Height : profile.StandHeight;
                Vector3 feet = t[n].LocalPosition;
                _debug.Capsule(feet, profile.Radius, height, c[n].Grounded ? DebugColour.Green : DebugColour.Orange);
                _debug.Arrow(feet, feet + c[n].GroundNormal * 0.6f, c[n].OnSteep ? DebugColour.Red : DebugColour.Yellow);
            }
        }
    }
}
