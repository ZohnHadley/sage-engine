#nullable enable
using System;
using System.Numerics;

namespace Sage.Gameplay;

// What the simulation looks like, drawn into the world (docs/design/06 §3.2, 10 §9, 16 §11). These
// systems cost one bool test each when their cvar is off; with it on they are the difference between
// "the creature isn't attacking and I don't know why" and seeing its sight cone miss you by a metre.

// Late phase: what each agent knows (16 §3.4). The cone is what it can see, the line is what it is
// chasing, and the colour is how close it thinks it is to swinging.
[System("sage.ai.debug", Phase.Late)]
internal sealed class AIDebugSystem : ISystem
{
    private readonly Query<Transform, AIState> _agents;
    private readonly DebugDraw _debug;
    private readonly VisualLog _log;
    private const string Category = "ai";
    private readonly RecordStore _records;
    private readonly World _world;
    private ActiveCamera? _camera;
    private readonly CVar<bool> _enabled;
    private static readonly AIProfileRecord Fallback = new();

    private static bool Has(ulong conditions, AICondition flag) => (conditions & (ulong)flag) != 0;

    private const float DrawRange = 35f;

    public AIDebugSystem(World world, RecordStore records, CVar<bool> enabled)
    {
        _agents = world.Query<Transform, AIState>();
        _debug = world.Debug();
        _log = world.VisualLog();
        _world = world;
        _records = records;
        _enabled = enabled;
    }

    // Only what is near the camera; everything, in a world with no camera to be near (issue #13).
    private bool OutOfRange(Vector3 at)
    {
        if (_camera == null) _world.Resources.TryGet(out _camera);
        return _camera != null && SageMath.DistanceXZ(at, _camera.Position) > DrawRange;
    }

    public void Run(in SystemContext ctx)
    {
        // Drawn now with `ai_debug`, kept in the visual log with `vlog_record` ("ai", issue #300): the same
        // shapes, so a decision can be scrubbed back to after the tick that made it has gone.
        bool draw = _enabled.Value && _debug.Enabled;
        bool record = _log.Recording;
        if (!draw && !record) return;
        var world = ctx.World;

        foreach (var (transforms, states, entities) in _agents.Chunks)
        {
            var t = transforms.Span;
            var s = states.Span;
            for (int n = 0; n < t.Length; n++)
            {
                if (OutOfRange(t[n].LocalPosition)) continue;
                var profileId = s[n].Profile.IsEmpty ? world.Conventions().AiProfile.Id : s[n].Profile;
                var profile = !profileId.IsEmpty && _records.TryGet(profileId, out AIProfileRecord found) ? found : Fallback;
                Vector3 feet = t[n].LocalPosition;
                Vector3 eye = feet + Vector3.UnitY * 1.4f;
                Vector3 ground = feet + Vector3.UnitY * 0.05f;
                float yaw = SageMath.YawOf(t[n].LocalRotation);
                ulong conditions = s[n].Conditions;   // bits, not Enum.HasFlag: it boxes (R18)
                var self = entities.EntityAt(n);

                // The cone it can see through, on the ground, and how far it can see.
                float sight = MathF.Min(profile.SightRange, 12f);
                if (draw) _debug.Cone(ground, yaw, profile.SightAngleDegrees, sight, DebugColour.Grey);
                if (record)
                {
                    _log.Cone(Category, ground, yaw, profile.SightAngleDegrees, sight, DebugColour.Grey, entity: self);
                    _log.Point(Category, eye, Describe(world, s[n]), DebugColour.White, self, 0.1f);
                }

                // What it is after, if anything, and whether it thinks it can reach it.
                if (!s[n].Target.IsNull && world.IsAlive(s[n].Target))
                {
                    Vector3 target = world.Get<Transform>(s[n].Target).LocalPosition + Vector3.UnitY * 1.2f;
                    uint colour = Has(conditions, AICondition.EnemyInMeleeRange) ? DebugColour.Red
                        : Has(conditions, AICondition.SeeEnemy) ? DebugColour.Yellow
                        : DebugColour.Grey;
                    if (draw)
                    {
                        _debug.Line(eye, target, colour);
                        _debug.Cross(target, 0.2f, colour);
                    }
                    if (record)
                    {
                        _log.Line(Category, eye, target, colour, entity: self);
                        _log.Point(Category, target, null, colour, s[n].Target);
                    }
                }

                // Melee range as a ring, so "why is it standing there?" answers itself.
                if (Has(conditions, AICondition.SeeEnemy))
                {
                    if (draw) _debug.Circle(ground, Vector3.UnitX, Vector3.UnitZ, profile.MeleeRange, DebugColour.Orange);
                    if (record) _log.Circle(Category, ground, profile.MeleeRange, DebugColour.Orange, entity: self);
                }

                // And the spell's reach for a caster, which is the other ring its decisions turn on
                // (16 §3.4): inside it the agent casts or holds, outside it closes in.
                if (!s[n].Spell.IsEmpty && _records.TryGet(s[n].Spell, out AbilityRecord spell))
                {
                    if (draw) _debug.Circle(ground, Vector3.UnitX, Vector3.UnitZ, spell.Range * 0.9f, DebugColour.Magenta);
                    if (record) _log.Circle(Category, ground, spell.Range * 0.9f, DebugColour.Magenta, entity: self);
                }
            }
        }
    }

    // The visual log's line about an agent: its schedule and task, what it is after and what it knows.
    private static string Describe(World world, in AIState s)
    {
        string target = s.Target.IsNull || !world.IsAlive(s.Target) ? "no target" : "target " + World.Describe(s.Target);
        string brain = !s.Tree.IsEmpty ? $"tree {s.Tree} node" : s.Schedule.IsEmpty ? "(no schedule) task" : $"{s.Schedule} task";   // #387
        return $"{brain} {s.TaskIndex}, {target}, conditions 0x{s.Conditions:X}";
    }
}
