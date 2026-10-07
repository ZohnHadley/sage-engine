#nullable enable
using System;
using System.Numerics;

namespace Sage.Gameplay;

// Combat movement (docs/design/16 §3.4 "keeping distance, ... self-buffs and healing", issue #388): tasks a
// schedule or a behaviour tree puts together into a fighting style, in data alone.
//
//   Strafe          { "seconds": 1.5 }   step sideways round the target, facing it
//   RetreatToRange  { "distance": 8 }    back away, facing it, until it is that far off (a ranged creature's)
//   TakeCover       { "distance": 10 }   make for the nearest place within that reach the target cannot see
//   Flee            { "distance": 20 }   turn and run from the target (or a noise) until that far away
//   HealSelf        { "giveUpAfter": 2 } cast the best self-delivered spell it knows that restores health
//   Flank           { "distance": 3 }    make for its own side of the target, as its squad spreads out
//   CallForHelp     { "distance": 25 }   shout: squadmates and allies in earshot take its target (Squads.cs)
//
// Which runs when is the profile's business: a `utility` option on `target_distance` keeps an archer at its
// range, one on `health` sends a creature running or healing, `rules` on `SeeEnemy` start a pack's flank.

internal static class CombatMovement
{
    // Bodies give no cover and are no wall to back into: creatures and players are left out of every ray.
    public static LayerMask Solid(IPhysicsWorld space) => LayerMask.All.Except(space.Layers.Enemy).Except(space.Layers.Player);

    // What it is getting away from or fighting: its target, where it sees it or last saw it; else what it
    // heard. False with neither.
    public static bool Threat(ref AITaskContext c, out Vector3 at)
    {
        var target = c.State.Target;
        if (!target.IsNull && c.World.IsAlive(target) && c.World.TryGet<Transform>(target, out var t))
        {
            at = (c.State.Conditions & (ulong)AICondition.SeeEnemy) != 0 ? t.LocalPosition : c.State.LastSeen;
            return true;
        }
        if (c.State.HeardUntil > 0f) { at = c.State.Heard; return true; }
        at = default;
        return false;
    }

    public static void Face(ref AITaskContext c, Vector3 at) =>
        c.Intent.Yaw = AIMath.TurnToward(c.Intent.Yaw, AIMath.YawTo(c.Transform.LocalPosition, at), c.Profile.TurnSpeedDegrees * MathF.PI / 180f * c.Dt);

    // Metres of room from `self` along `direction` (flat), up to `distance`, at shin height.
    public static float Room(ref AITaskContext c, Vector3 self, Vector3 direction, float distance)
    {
        var hit = c.Space.Raycast(self + Vector3.UnitY * (c.Movement.StepHeight + 0.1f), direction, distance, Solid(c.Space));
        return hit.Hit ? hit.Distance : distance;
    }

    // Its own right, from the way it faces.
    public static Vector3 Right(float yaw)
    {
        var forward = SageMath.ForwardFromYaw(yaw);
        return new Vector3(-forward.Z, 0, forward.X);
    }
}

// Step sideways round the target for `seconds` (default 1.5), facing it: the side with more room, turning
// back when that side runs out. Fails with no target.
internal sealed class StrafeTask : IAITask
{
    public const string ArgumentName = "seconds";
    public string? Argument => ArgumentName;
    private const float Probe = 1.2f;

    public AITaskStatus Start(ref AITaskContext c)
    {
        if (!CombatMovement.Threat(ref c, out _)) return AITaskStatus.Failed;
        var self = c.Transform.LocalPosition;
        var right = CombatMovement.Right(c.Intent.Yaw);
        float r = CombatMovement.Room(ref c, self, right, Probe * 2f), l = CombatMovement.Room(ref c, self, -right, Probe * 2f);
        c.State.MoveSide = (sbyte)(r > l ? 1 : l > r ? -1 : (c.Entity.Id & 1) == 0 ? 1 : -1);
        return AITaskStatus.Running;
    }

    public AITaskStatus Run(ref AITaskContext c)
    {
        if (!c.World.IsAlive(c.State.Target) || !CombatMovement.Threat(ref c, out var at)) { c.Intent.Move = Vector2.Zero; return AITaskStatus.Failed; }
        if (c.State.TaskTime >= (c.Param > 0 ? c.Param : 1.5f)) { c.Intent.Move = Vector2.Zero; return AITaskStatus.Succeeded; }
        CombatMovement.Face(ref c, at);
        var self = c.Transform.LocalPosition;
        if (CombatMovement.Room(ref c, self, CombatMovement.Right(c.Intent.Yaw) * c.State.MoveSide, Probe) < Probe)
            c.State.MoveSide = (sbyte)-c.State.MoveSide;
        c.Intent.Move = new Vector2(c.State.MoveSide, 0);
        return AITaskStatus.Running;
    }
}

// Back away from the target, facing it, until it is `distance` metres off (default 8): what keeps a ranged
// creature at its range. Sidesteps what is behind it; fails when it is cornered (or after 6 s), so the
// schedule after it — usually a fight — runs instead.
internal sealed class RetreatToRangeTask : IAITask
{
    public const string ArgumentName = "distance";
    public string? Argument => ArgumentName;
    private const float Probe = 1.2f;
    private const float GiveUpSeconds = 6f;

    public AITaskStatus Run(ref AITaskContext c)
    {
        if (!c.World.IsAlive(c.State.Target) || !CombatMovement.Threat(ref c, out var at)) { c.Intent.Move = Vector2.Zero; return AITaskStatus.Failed; }
        var self = c.Transform.LocalPosition;
        if (AIMath.DistanceXZ(self, at) >= (c.Param > 0 ? c.Param : 8f)) { c.Intent.Move = Vector2.Zero; return AITaskStatus.Succeeded; }
        if (c.State.TaskTime > GiveUpSeconds) { c.Intent.Move = Vector2.Zero; return AITaskStatus.Failed; }

        CombatMovement.Face(ref c, at);
        var away = self - at;
        away.Y = 0;
        away = away.LengthSquared() > 1e-6f ? Vector3.Normalize(away) : -SageMath.ForwardFromYaw(c.Intent.Yaw);
        var right = CombatMovement.Right(c.Intent.Yaw);
        if (CombatMovement.Room(ref c, self, away, Probe) >= Probe)
        {
            // Straight back, in its own frame: it faces the target, so that is mostly backwards.
            c.Intent.Move = new Vector2(Vector3.Dot(away, right), Vector3.Dot(away, SageMath.ForwardFromYaw(c.Intent.Yaw)));
            return AITaskStatus.Running;
        }
        // A wall behind: along it, whichever way has room.
        float r = CombatMovement.Room(ref c, self, right, Probe), l = CombatMovement.Room(ref c, self, -right, Probe);
        if (r < Probe && l < Probe) { c.Intent.Move = Vector2.Zero; return AITaskStatus.Failed; }   // cornered
        c.Intent.Move = new Vector2(r >= l ? 1 : -1, 0);
        return AITaskStatus.Running;
    }
}

// Make for the nearest place within `distance` metres (default 10) that the target (or what it heard)
// cannot see — a wall or a crate between them — and stop there. Fails when there is none, or it has not got
// there in 10 s.
internal sealed class TakeCoverTask : IAITask
{
    public const string ArgumentName = "distance";
    public string? Argument => ArgumentName;
    private const int Directions = 16;
    private const float Step = 1.5f;
    private const float Reached = 0.6f;
    private const float GiveUpSeconds = 10f;

    public AITaskStatus Start(ref AITaskContext c)
    {
        if (!CombatMovement.Threat(ref c, out var threat)) return AITaskStatus.Failed;
        return Find(ref c, threat, out c.State.MoveGoal) ? AITaskStatus.Running : AITaskStatus.Failed;
    }

    public AITaskStatus Run(ref AITaskContext c)
    {
        var self = c.Transform.LocalPosition;
        if (AIMath.DistanceXZ(self, c.State.MoveGoal) <= Reached)
        {
            c.Intent.Move = Vector2.Zero;
            if (CombatMovement.Threat(ref c, out var at)) CombatMovement.Face(ref c, at);
            return AITaskStatus.Succeeded;
        }
        if (c.State.TaskTime > GiveUpSeconds) { c.Intent.Move = Vector2.Zero; return AITaskStatus.Failed; }
        MoveToTargetTask.WalkToward(ref c, self, c.State.MoveGoal, default);
        return AITaskStatus.Running;
    }

    // Rings round itself, nearest first: the first point it can walk straight to, with ground under it, that
    // a ray from the threat's eye to a crouching head does not reach.
    internal static bool Find(ref AITaskContext c, Vector3 threat, out Vector3 cover)
    {
        cover = default;
        var self = c.Transform.LocalPosition;
        float reach = c.Param > 0 ? c.Param : 10f;
        var solid = CombatMovement.Solid(c.Space);
        var eye = threat + Vector3.UnitY * 1.4f;
        float best = float.MaxValue;
        for (float radius = Step; radius <= reach + 0.01f; radius += Step)
        {
            for (int i = 0; i < Directions; i++)
            {
                float yaw = i * MathF.Tau / Directions;
                var direction = SageMath.ForwardFromYaw(yaw);
                var point = self + direction * radius;
                if (CombatMovement.Room(ref c, self, direction, radius + c.Movement.Radius) < radius + c.Movement.Radius) continue;
                if (!c.Space.Raycast(point + Vector3.UnitY, -Vector3.UnitY, 3f, solid).Hit) continue;   // no floor there

                var head = point + Vector3.UnitY * 1.0f;
                var look = head - eye;
                float length = look.Length();
                if (length < 0.01f) continue;
                var hit = c.Space.Raycast(eye, look / length, length, solid);
                if (!hit.Hit || hit.Distance > length - 0.3f) continue;   // in plain view

                // Of the hidden points on this ring, the one further from the threat.
                float score = -AIMath.DistanceXZ(point, threat);
                if (score < best) { best = score; cover = point; }
            }
            if (best < float.MaxValue) return true;
        }
        return false;
    }
}

// Turn and run from the target — or, with none, from what it heard — until it is `distance` metres away
// (default 20). Fails with nothing to run from, or after 15 s.
internal sealed class FleeTask : IAITask
{
    public const string ArgumentName = "distance";
    public string? Argument => ArgumentName;
    private const float GiveUpSeconds = 15f;

    public AITaskStatus Run(ref AITaskContext c)
    {
        if (!CombatMovement.Threat(ref c, out var threat)) { c.Intent.Move = Vector2.Zero; return AITaskStatus.Failed; }
        var self = c.Transform.LocalPosition;
        if (AIMath.DistanceXZ(self, threat) >= (c.Param > 0 ? c.Param : 20f)) { c.Intent.Move = Vector2.Zero; return AITaskStatus.Succeeded; }
        if (c.State.TaskTime > GiveUpSeconds) { c.Intent.Move = Vector2.Zero; return AITaskStatus.Failed; }
        var away = self - threat;
        away.Y = 0;
        away = away.LengthSquared() > 1e-6f ? Vector3.Normalize(away) : -SageMath.ForwardFromYaw(c.Intent.Yaw);
        MoveToTargetTask.WalkToward(ref c, self, self + away * 4f, default);
        return AITaskStatus.Running;
    }
}

// Cast the best spell it knows that it casts on itself and that restores health (an effect adding to the
// conventions' health), as the cast system allows it — mana, cooldown and all. Fails with none to cast;
// `giveUpAfter` (default 2) seconds covers the wind-up.
internal sealed class HealSelfTask : IAITask
{
    public const string ArgumentName = "giveUpAfter";
    public string? Argument => ArgumentName;

    public AITaskStatus Start(ref AITaskContext c)
    {
        var heal = Choose(c.World, c.Entity);
        if (heal.IsEmpty) return AITaskStatus.Failed;
        c.World.Cast(c.Entity, heal);
        return AITaskStatus.Running;
    }

    public AITaskStatus Run(ref AITaskContext c)
    {
        c.Intent.Move = Vector2.Zero;
        if (!c.World.TryGet<Abilities>(c.Entity, out var abilities)) return AITaskStatus.Failed;
        if (!abilities.Casting.IsEmpty) return AITaskStatus.Running;
        if (c.State.TaskTime < c.Dt * 2f) return AITaskStatus.Running;
        return c.State.TaskTime > (c.Param > 0 ? c.Param : 2f) ? AITaskStatus.Failed : AITaskStatus.Succeeded;
    }

    // The castable self-delivered healing spell that heals most; empty with none.
    internal static RecordId Choose(World world, Entity self)
    {
        if (!world.TryGet<Abilities>(self, out var abilities) || abilities.Known is not { Count: > 0 }) return default;
        var records = world.Records();
        var health = world.Conventions().Health.Id;
        RecordId best = default;
        float most = 0f;
        foreach (var ability in abilities.Known)
        {
            if (!AbilityRules.CanCast(world, records, self, ability, in abilities, out var record, out _)) continue;
            if (!AbilityDeliveries.OnCaster(world, record)) continue;
            float heals = Heals(records, record, health);
            if (heals > most) { most = heals; best = ability; }
        }
        return best;
    }

    private static float Heals(RecordStore records, AbilityRecord ability, RecordId health)
    {
        float total = 0f;
        foreach (var effectId in ability.Effects)
        {
            if (!records.TryGet(effectId.Id, out EffectRecord effect)) continue;
            foreach (var modifier in effect.Modifiers)
                if (modifier.Attribute.Id == health && modifier.Op == ModifierOp.Add && modifier.Value > 0f) total += modifier.Value;
        }
        return total * MathF.Max(ability.Magnitude, 0f);
    }
}

// Make for its own side of the target, `distance` metres from it (default 3), as its squad spreads round it
// (Squads.cs): the leader and a creature fighting alone go straight in (it succeeds at once); the others take
// alternate sides of the way the squad came at it, 60 degrees out and wider for each pair. Succeeds there, or
// after 8 s, so the attack that follows it in the schedule runs either way.
internal sealed class FlankTask : IAITask
{
    public const string ArgumentName = "distance";
    public string? Argument => ArgumentName;
    private const float Reached = 1f;
    private const float GiveUpSeconds = 8f;

    public AITaskStatus Run(ref AITaskContext c)
    {
        if (!c.World.IsAlive(c.State.Target) || !CombatMovement.Threat(ref c, out var target)) { c.Intent.Move = Vector2.Zero; return AITaskStatus.Failed; }
        if (!Spot(c.World, c.Entity, c.State.Target, target, c.Param > 0 ? c.Param : 3f, out var spot)) return AITaskStatus.Succeeded;
        var self = c.Transform.LocalPosition;
        if (AIMath.DistanceXZ(self, spot) <= Reached || c.State.TaskTime > GiveUpSeconds)
        {
            c.Intent.Move = Vector2.Zero;
            return AITaskStatus.Succeeded;
        }
        MoveToTargetTask.WalkToward(ref c, self, spot, c.State.Target);
        return AITaskStatus.Running;
    }

    // Where `self` flanks `target` (standing at `at`) from; false when it goes straight in.
    internal static bool Spot(World world, Entity self, Entity target, Vector3 at, float distance, out Vector3 spot)
    {
        spot = default;
        var info = Squads.Of(world).Of(world, self);
        if (info == null || info.Target != target || info.EngagedCount < 2) return false;
        if (world.Get<Squad>(self).IsLeader) return false;
        int slot = 0;
        bool found = false;
        foreach (var member in info.Engaged)
        {
            if (member == self) { found = true; break; }
            if (!world.Get<Squad>(member).IsLeader) slot++;
        }
        if (!found) return false;
        float side = (slot & 1) == 0 ? -1f : 1f;
        float angle = MathF.Min(60f + 30f * (slot / 2), 150f) * MathF.PI / 180f;
        spot = at + SageMath.ForwardFromYaw(info.ApproachYaw + side * angle) * distance;
        spot.Y = at.Y;
        return true;
    }
}

// Shout for help (Squads.cs): a noise of kind `Alert`, heard `distance` metres off (default 25), that every
// squadmate and faction ally within it with nothing to fight answers by taking its target. Creatures that
// hear it look into it as they would any noise. Succeeds at once.
internal sealed class CallForHelpTask : IAITask
{
    public const string ArgumentName = "distance";
    public string? Argument => ArgumentName;

    public AITaskStatus Run(ref AITaskContext c)
    {
        Noises.Make(c.World, c.Entity, c.Transform.LocalPosition + Vector3.UnitY * 1.6f, c.Param > 0 ? c.Param : 25f, 1f, NoiseKind.Alert);
        return AITaskStatus.Succeeded;
    }
}
