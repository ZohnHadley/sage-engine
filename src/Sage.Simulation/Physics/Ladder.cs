#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// Ladders (docs/design/10 §3, issue #263): a trigger volume a character climbs instead of walking
// through. Half-Life's func_ladder and STALKER's ladders are the same thing — an invisible box in front of
// the rungs — and this is that box's meaning. The volume itself is any trigger collider (a `body` with
// `"trigger": true`, or a brush entity with `"trigger" "1"`); its top and bottom are the volume's own, so
// a ladder is as tall as it is drawn. The controller that climbs it is Sage.Physics3D's
// CharacterMovementSystem: pushing toward the rungs climbs, pulling away climbs down, Jump lets go, and
// near the top a walkable ledge within step height is stepped onto.
[Component("sage:ladder")]
public struct Ladder : IComponent
{
    // Which way the climbable face faces — out toward the climber — as a yaw in degrees on top of the
    // entity's own (SageMath's convention: 0 faces -Z, 180 faces +Z). A climber stands on this side.
    [Property(Min = -360, Max = 360, Unit = "deg", Tooltip = "Which way the climbable side faces, as a yaw on top of the entity's own")]
    public float Facing;

    [Property(Min = 0, Max = 20, Unit = "m/s", Tooltip = "Climbing speed; 0 = the default (2.5 m/s)")]
    public float Speed;

    public const float DefaultSpeed = 2.5f;

    // The climbable face's outward normal in world space, for an entity turned to `rotation`.
    public readonly Vector3 NormalFor(Quaternion rotation) =>
        SageMath.ForwardFromYaw(SageMath.YawOf(rotation) + Facing * MathF.PI / 180f);

    public readonly float SpeedOrDefault => Speed > 0f ? Speed : DefaultSpeed;
}

// "ladder": { "facing": 180, "speed": 2.5 }
//
// Makes the entity's trigger volume a ladder. The volume comes from the entity's own collider — a `body`
// part with `"trigger": true` on a prefab, or the brushes of a `"trigger" "1"` brush entity in a map whose
// classname is a prefab with this part (`"classname" "ladder"`, with `"ladder.facing"` to turn it).
[PrefabPart("ladder", Plugin = "sage.gameplay.character")]
public sealed class LadderPart : IPrefabPart
{
    [Property(Min = -360, Max = 360, Unit = "deg", Tooltip = "Which way the climbable side faces, as a yaw on top of the entity's own")]
    public float Facing;
    [Property(Min = 0, Max = 20, Unit = "m/s", Tooltip = "Climbing speed; 0 = the default (2.5 m/s)")]
    public float Speed = Ladder.DefaultSpeed;

    public void Apply(in PrefabPartContext ctx) => ctx.World.Add(ctx.Entity, new Ladder { Facing = Facing, Speed = Speed });
}
