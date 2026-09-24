#nullable enable
using System;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// What the simulation looks like, drawn into the world (docs/design/06 §3.2, 10 §9, 16 §11). These
// systems cost one bool test each when their cvar is off; with it on they are the difference between
// "the creature isn't attacking and I don't know why" and seeing its sight cone miss you by a metre.

// Late phase: every collider in the world, in its real place. The colour says how it moves, which is
// how a kinematic body that should be static (or a capsule sunk into the ground, review #44) shows up
// at a glance.
public sealed class PhysicsDebugSystem : ISystem
{
    private readonly ArchetypeQuery<Transform, Collider> _colliders;
    private readonly ArchetypeQuery<Transform, CharacterController> _characters;
    private readonly DebugDraw _debug;
    private readonly RecordStore _records;
    private readonly ActiveCamera _camera;
    private readonly CVar<bool> _enabled;

    // Far enough to cover what you are looking at, near enough that a streamed-in city doesn't turn
    // into a wireframe soup (and a line budget, 06 §3.2).
    private const float DrawRange = 35f;

    // The cvar is the module's, registered once at Init: a system lives per world, and a game with
    // an overworld and a battle scene has two of those (review #57).
    public PhysicsDebugSystem(World world, RecordStore records, CVar<bool> enabled)
    {
        _colliders = world.Query<Transform, Collider>();
        _characters = world.Query<Transform, CharacterController>();
        _debug = world.Debug();
        _camera = world.Resources.Get<ActiveCamera>();
        _records = records;
        _enabled = enabled;
    }

    public void Run(in SystemContext ctx)
    {
        if (!_enabled.Value || !_debug.Enabled) return;

        foreach (var (transforms, colliders, _) in _colliders.Chunks)
        {
            var t = transforms.Span;
            var c = colliders.Span;
            for (int n = 0; n < t.Length; n++)
            {
                if (SageMath.DistanceXZ(t[n].LocalPosition, _camera.Position) > DrawRange) continue;
                var pose = Pose.FromLocal(t[n]);
                Vector3 center = c[n].CenterAt(pose);
                uint colour = c[n].IsTrigger ? DebugColour.Magenta : DebugColour.Cyan;
                switch (c[n].Shape)
                {
                    case ColliderShape.Sphere:
                        _debug.Sphere(center, c[n].Size.X, colour);
                        break;
                    case ColliderShape.Capsule:
                        float height = c[n].Size.Y + 2f * c[n].Size.X;
                        _debug.Capsule(center - Vector3.UnitY * (height * 0.5f), c[n].Size.X, height, colour);
                        break;
                    case ColliderShape.Mesh:
                        break;                                   // terrain: far too many lines to be useful
                    default:
                        _debug.Box(center, c[n].Size * 0.5f, pose.Rotation, colour);
                        break;
                }
            }
        }

        // A character's capsule is not its collider — it is what the controller sweeps — so it gets
        // drawn from the controller's own numbers, along with the ground it thinks it is standing on.
        foreach (var (transforms, characters, _) in _characters.Chunks)
        {
            var t = transforms.Span;
            var c = characters.Span;
            for (int n = 0; n < t.Length; n++)
            {
                if (SageMath.DistanceXZ(t[n].LocalPosition, _camera.Position) > DrawRange) continue;
                var profile = _records.TryGet(c[n].Profile.IsEmpty ? MovementProfileRecord.Default : c[n].Profile,
                                              out MovementProfileRecord found) ? found : MovementProfileRecord.Fallback;
                float height = c[n].Height > 0f ? c[n].Height : profile.StandHeight;
                Vector3 feet = t[n].LocalPosition;
                _debug.Capsule(feet, profile.Radius, height, c[n].Grounded ? DebugColour.Green : DebugColour.Orange);
                _debug.Arrow(feet, feet + c[n].GroundNormal * 0.6f, c[n].OnSteep ? DebugColour.Red : DebugColour.Yellow);
            }
        }
    }
}

// Late phase: what each agent knows (16 §3.4). The cone is what it can see, the line is what it is
// chasing, and the colour is how close it thinks it is to swinging.
public sealed class AIDebugSystem : ISystem
{
    private readonly ArchetypeQuery<Transform, AIState> _agents;
    private readonly DebugDraw _debug;
    private readonly RecordStore _records;
    private readonly ActiveCamera _camera;
    private readonly CVar<bool> _enabled;
    private static readonly AIProfileRecord Fallback = new();

    private static bool Has(ulong conditions, AICondition flag) => (conditions & (ulong)flag) != 0;

    private const float DrawRange = 35f;

    public AIDebugSystem(World world, RecordStore records, CVar<bool> enabled)
    {
        _agents = world.Query<Transform, AIState>();
        _debug = world.Debug();
        _camera = world.Resources.Get<ActiveCamera>();
        _records = records;
        _enabled = enabled;
    }

    public void Run(in SystemContext ctx)
    {
        if (!_enabled.Value || !_debug.Enabled) return;
        var world = ctx.World;

        foreach (var (transforms, states, entities) in _agents.Chunks)
        {
            var t = transforms.Span;
            var s = states.Span;
            for (int n = 0; n < t.Length; n++)
            {
                if (SageMath.DistanceXZ(t[n].LocalPosition, _camera.Position) > DrawRange) continue;
                var profile = _records.TryGet(s[n].Profile.IsEmpty ? AIProfileRecord.Default : s[n].Profile,
                                              out AIProfileRecord found) ? found : Fallback;
                Vector3 eye = t[n].LocalPosition + Vector3.UnitY * 1.4f;
                float yaw = SageMath.YawOf(t[n].LocalRotation);
                ulong conditions = s[n].Conditions;   // bits, not Enum.HasFlag: it boxes (R18)

                // The cone it can see through, on the ground, and how far it can see.
                _debug.Cone(t[n].LocalPosition + Vector3.UnitY * 0.05f, yaw, profile.SightAngleDegrees,
                            MathF.Min(profile.SightRange, 12f), DebugColour.Grey);

                // What it is after, if anything, and whether it thinks it can reach it.
                if (!s[n].Target.IsNull && world.IsAlive(s[n].Target))
                {
                    Vector3 target = world.Get<Transform>(s[n].Target).LocalPosition + Vector3.UnitY * 1.2f;
                    uint colour = Has(conditions, AICondition.EnemyInMeleeRange) ? DebugColour.Red
                        : Has(conditions, AICondition.SeeEnemy) ? DebugColour.Yellow
                        : DebugColour.Grey;
                    _debug.Line(eye, target, colour);
                    _debug.Cross(target, 0.2f, colour);
                }

                // Melee range as a ring, so "why is it standing there?" answers itself.
                if (Has(conditions, AICondition.SeeEnemy))
                    _debug.Circle(t[n].LocalPosition + Vector3.UnitY * 0.05f, Vector3.UnitX, Vector3.UnitZ,
                                  profile.MeleeRange, DebugColour.Orange);

                // And the spell's reach for a caster, which is the other ring its decisions turn on
                // (16 §3.4): inside it the agent casts or holds, outside it closes in.
                if (!s[n].Spell.IsEmpty && _records.TryGet(s[n].Spell, out AbilityRecord spell))
                    _debug.Circle(t[n].LocalPosition + Vector3.UnitY * 0.05f, Vector3.UnitX, Vector3.UnitZ,
                                  spell.Range * 0.9f, DebugColour.Magenta);
            }
        }
    }
}
