#nullable enable
using System;
using System.Numerics;

namespace Sage.Physics3D;

// The kinematic character controller (docs/design/10 §3, TODO F7): a capsule moved by sweeps, not a
// dynamic body, because a rigid body makes bad stairs and worse netcode. Deterministic and headless:
// the same code runs on a future server.

// PrePhysics, before the bodies are synced (10 §3): turns intent into movement, resolves it against
// the world with sweeps, and leaves the result in Transform.
[System("sage.character.move", Phase.PrePhysics, Before = new[] { "sage.physics.sync" })]
internal sealed class CharacterMovementSystem : ISystem
{
    private const float Skin = 0.02f;         // never move fully into a surface
    private const float GroundOffset = 0.06f; // horizontal sweeps start this far above the feet
    private const int SlideIterations = 4;   // 10 §3: collide-and-slide, up to four planes
    private const int DepenetrationIterations = 4;
    private const float DepenetrationSlop = 0.005f;   // overlaps shallower than this are resting contact

    private readonly Query<Transform, CharacterController, PawnIntent> _characters;
    private readonly RecordStore _records;
    private readonly PhysicsSpace _space;
    private readonly ActionId _jump, _crouch, _run;
    private readonly CharacterConventions _conventions;
    private readonly OverlapHit[] _overlaps = new OverlapHit[8];

    public CharacterMovementSystem(World world, RecordStore records, ActionRegistry actions)
    {
        // A ragdoll (issue #246) is physics' now: the controller leaves it lying where it fell.
#pragma warning disable SAGE0134 // the ragdoll's tag: experimental with the rest of phase 4k
        _characters = world.Query<Transform, CharacterController, PawnIntent>().WithoutAnyTags(Tags.Get<Ragdolled>());
#pragma warning restore SAGE0134
        _records = records;
        _space = world.Resources.Get<PhysicsSpace>();
        _conventions = CharacterConventions.Of(world);
        _jump = actions.Get(_conventions.JumpAction);
        _crouch = actions.Get(_conventions.CrouchAction);
        _run = actions.Get(_conventions.RunAction);
    }

    public void Run(in SystemContext ctx)
    {
        float dt = ctx.Tick.Dt;
        foreach (var (transforms, characters, intents, _) in _characters.Chunks)
        {
            var t = transforms.Span;
            var c = characters.Span;
            var i = intents.Span;
            for (int n = 0; n < t.Length; n++)
                Move(ref t[n], ref c[n], in i[n], dt);
        }
    }

    private void Move(ref Transform transform, ref CharacterController character, in PawnIntent intent, float dt)
    {
        var profile = _conventions.ProfileOf(_records, character.Profile);
        var mask = LayerMask.All.Except(character.Layer);
        if (character.Height <= 0) character.Height = profile.StandHeight;

        Vector3 position = transform.LocalPosition;
        // Inside something (a door closed on it, a teleport, a spawn): out first, or every sweep below
        // starts overlapping and sees nothing (issue #259).
        position = Depenetrate(position, ref character, profile, mask);
        Crouch(ref character, in intent, profile, position, mask);

        // Which way the player wants to go, in the view's frame.
        float yaw = intent.Yaw;
        var forward = SageMath.ForwardFromYaw(yaw);
        var right = new Vector3(-forward.Z, 0, forward.X);
        Vector3 wish = right * intent.Move.X + forward * intent.Move.Y;
        if (wish.LengthSquared() > 1f) wish = Vector3.Normalize(wish);

        float speed = (intent.Held.Has(_run) ? profile.RunSpeed : profile.WalkSpeed) * (character.Crouching ? profile.CrouchSpeedScale : 1f);
        Accelerate(ref character, wish * speed, character.Grounded ? profile.Acceleration : profile.AirAcceleration,
                   character.Grounded && wish.LengthSquared() < 0.01f ? profile.Friction : 0f, dt);

        if (character.Grounded && intent.Pressed.Has(_jump))
        {
            character.Velocity.Y = profile.JumpSpeed;
            character.Grounded = false;
        }
        else if (!character.Grounded)
        {
            character.Velocity.Y += profile.Gravity * dt;
        }
        else if (character.Velocity.Y < 0)
        {
            character.Velocity.Y = 0;
        }

        // Standing on something too steep to walk on: slide down it instead of climbing (10 §3).
        if (!character.Grounded && character.OnSteep)
        {
            Vector3 normal = character.GroundNormal;
            Vector3 downhill = -Vector3.UnitY - normal * Vector3.Dot(-Vector3.UnitY, normal);
            if (downhill.LengthSquared() > 1e-6f)
            {
                downhill = Vector3.Normalize(downhill);
                character.Velocity += downhill * (MathF.Abs(profile.Gravity) * (1f - normal.Y) * dt);

                // And it can't push itself uphill.
                Vector3 uphill = -downhill with { Y = 0 };
                if (uphill.LengthSquared() > 1e-6f)
                {
                    uphill = Vector3.Normalize(uphill);
                    float climbing = Vector3.Dot(character.Velocity, uphill);
                    if (climbing > 0) character.Velocity -= uphill * climbing;
                }
            }
        }

        // Walking along a slope instead of into it.
        if (character.Grounded && character.GroundNormal.Y > 0.01f)
        {
            Vector3 horizontal = character.Velocity with { Y = 0 };
            if (horizontal.LengthSquared() > 1e-6f)
            {
                Vector3 along = horizontal - character.GroundNormal * Vector3.Dot(horizontal, character.GroundNormal);
                if (along.LengthSquared() > 1e-6f)
                    character.Velocity = Vector3.Normalize(along) * horizontal.Length() + Vector3.UnitY * character.Velocity.Y;
            }
        }

        // The body faces where its controller is looking. One yaw convention engine-wide (SageMath),
        // so this is all of it: no correction between movement, sprites and the camera.
        transform.LocalRotation = SageMath.RotationFromYaw(yaw);

        bool wasGrounded = character.Grounded;
        Vector3 motion = character.Velocity * dt;
        // The steepest ground that still counts as ground, once per move instead of once per sweep.
        float cosSlope = MathF.Cos(profile.MaxSlopeDegrees * MathF.PI / 180f);
        position = SlideHorizontal(position, motion with { Y = 0 }, ref character, profile, cosSlope, mask);
        position = MoveVertical(position, motion.Y, ref character, profile, mask);
        GroundCheck(ref position, ref character, profile, cosSlope, mask, wasGrounded);
        transform.LocalPosition = position;
    }

    // Accelerates the horizontal velocity toward `target`, with friction when there's no input.
    private static void Accelerate(ref CharacterController character, Vector3 target, float acceleration, float friction, float dt)
    {
        Vector3 velocity = character.Velocity with { Y = 0 };
        if (friction > 0)
        {
            float speed = velocity.Length();
            float drop = friction * dt * MathF.Max(speed, 1f);
            velocity = speed > drop ? velocity * ((speed - drop) / speed) : Vector3.Zero;
        }
        Vector3 delta = target - velocity;
        float distance = delta.Length();
        if (distance > 1e-5f)
            velocity += delta / distance * MathF.Min(acceleration * dt, distance);
        character.Velocity = velocity with { Y = character.Velocity.Y };
    }

    // Horizontal movement: collide-and-slide, with a step-up attempt when something low blocks the
    // way (10 §3). The sweeps start a little above the feet, or the ground the capsule is resting on
    // answers every horizontal sweep at zero distance and the character can never move.
    private Vector3 SlideHorizontal(Vector3 position, Vector3 motion, ref CharacterController character, MovementProfileRecord profile, float cosSlope, LayerMask mask)
    {
        float lift = MathF.Min(GroundOffset, profile.StepHeight * 0.5f);
        float height = MathF.Max(character.Height - lift, 0.2f);

        for (int iteration = 0; iteration < SlideIterations; iteration++)
        {
            float distance = motion.Length();
            if (distance < 1e-5f) break;
            Vector3 direction = motion / distance;

            var hit = Sweep(position + Vector3.UnitY * lift, height, profile.Radius, direction, distance, mask);
            if (!hit.Hit)
            {
                position += motion;
                break;
            }

            // A low obstacle: try to step onto it instead of sliding along it.
            if (character.Grounded && hit.Normal.Y < cosSlope &&
                TryStep(position, motion, character.Height, profile, mask, cosSlope, out Vector3 stepped))
            {
                position = stepped;
                break;
            }

            float travel = MathF.Max(0f, hit.Distance - Skin);
            Vector3 moved = direction * travel;
            position += moved;
            Vector3 remaining = motion - moved;
            motion = remaining - hit.Normal * Vector3.Dot(remaining, hit.Normal);
            character.Velocity -= hit.Normal * Vector3.Dot(character.Velocity, hit.Normal);

            // Sliding along a face steeper than the slope limit must not carry the character up it:
            // projecting onto a steep plane otherwise turns "walk into the cliff" into "climb it".
            if (hit.Normal.Y < cosSlope)
            {
                if (motion.Y > 0) motion.Y = 0;
                if (character.Velocity.Y > 0) character.Velocity.Y = 0;
            }
        }
        return position;
    }

    // Vertical movement: falling, jumping, and hitting a ceiling.
    private Vector3 MoveVertical(Vector3 position, float motion, ref CharacterController character, MovementProfileRecord profile, LayerMask mask)
    {
        if (MathF.Abs(motion) < 1e-5f) return position;
        Vector3 direction = motion > 0 ? Vector3.UnitY : -Vector3.UnitY;
        var hit = Sweep(position, character.Height, profile.Radius, direction, MathF.Abs(motion), mask);
        if (!hit.Hit) return position + direction * MathF.Abs(motion);

        character.Velocity.Y = 0;   // landed, or hit a ceiling
        return position + direction * MathF.Max(0f, hit.Distance - Skin);
    }

    // Sweep up, forward, then down: the classic step-up (10 §3). Accepts the result only if it lands
    // on something walkable and actually made progress.
    private bool TryStep(Vector3 position, Vector3 motion, float height, MovementProfileRecord profile, LayerMask mask, float cosSlope, out Vector3 stepped)
    {
        stepped = position;
        float step = profile.StepHeight;
        var up = Sweep(position, height, profile.Radius, Vector3.UnitY, step, mask);
        float rise = up.Hit ? MathF.Max(0f, up.Distance - Skin) : step;
        if (rise < 0.02f) return false;

        Vector3 raised = position + Vector3.UnitY * rise;
        float distance = motion.Length();
        Vector3 direction = motion / distance;

        // Far enough onto the step that the capsule stands on its top face: landing only a few
        // centimetres past the lip hits the *edge*, whose blended normal looks like a cliff.
        float wanted = MathF.Max(distance, profile.Radius + 2f * Skin);
        var forward = Sweep(raised, height, profile.Radius, direction, wanted, mask);
        float travelled = forward.Hit ? MathF.Max(0f, forward.Distance - Skin) : wanted;
        if (travelled < profile.Radius * 0.5f) return false;   // not enough room on top to stand

        Vector3 ahead = raised + direction * travelled;
        var down = Sweep(ahead, height, profile.Radius, -Vector3.UnitY, rise + Skin, mask);
        if (!down.Hit || down.Normal.Y < cosSlope) return false;

        stepped = ahead - Vector3.UnitY * MathF.Max(0f, down.Distance - Skin);
        return true;
    }

    // Is there ground under the feet? Snaps onto it while walking, so going downhill doesn't launch.
    private void GroundCheck(ref Vector3 position, ref CharacterController character, MovementProfileRecord profile, float cosSlope, LayerMask mask, bool wasGrounded)
    {
        float reach = wasGrounded && character.Velocity.Y <= 0.01f ? profile.GroundSnap : Skin * 2f;
        var hit = Sweep(position + Vector3.UnitY * Skin, character.Height, profile.Radius, -Vector3.UnitY, reach + Skin, mask);

        if (hit.Hit && hit.Normal.Y >= cosSlope)
        {
            character.Grounded = true;
            character.OnSteep = false;
            character.GroundNormal = hit.Normal;
            position += Vector3.UnitY * (Skin - MathF.Max(0f, hit.Distance - Skin));
            if (character.Velocity.Y < 0) character.Velocity.Y = 0;
            return;
        }

        // Nothing under the capsule. Before believing that, check whether the capsule is *inside* the
        // ground: a sweep that starts overlapping carries no normal, so the space reports nothing at
        // all (10 §4), and a character placed a few centimetres into a hill would fall through the
        // world for ever. A ray down from the capsule's centre still finds the surface, so the feet
        // can be put back on top of it (review #55).
        if (!hit.Hit)
        {
            float half = character.Height * 0.5f;
            var recovery = _space.Raycast(position + Vector3.UnitY * half, -Vector3.UnitY, half + reach + Skin, mask);
            if (recovery.Hit && recovery.Normal.Y >= cosSlope)
            {
                position = recovery.Position + Vector3.UnitY * Skin;
                character.Grounded = true;
                character.OnSteep = false;
                character.GroundNormal = recovery.Normal;
                if (character.Velocity.Y < 0) character.Velocity.Y = 0;
                return;
            }
        }

        character.Grounded = false;
        character.OnSteep = hit.Hit;                                     // a steep face: slide off it
        character.GroundNormal = hit.Hit ? hit.Normal : Vector3.UnitY;
    }

    // Pushes the capsule out of whatever it overlaps (issue #259): the deepest overlap first, along its
    // way out, a few times over, since getting out of one surface can put it into another. At most a
    // radius per tick, so something deeply inside a wall comes out over a few ticks instead of jumping
    // through the far side. Velocity into the surface is dropped, as a sweep that hit it would.
    private Vector3 Depenetrate(Vector3 position, ref CharacterController character, MovementProfileRecord profile, LayerMask mask)
    {
        var shape = Collider.Standing(profile.Radius, character.Height);
        float budget = profile.Radius;
        for (int iteration = 0; iteration < DepenetrationIterations && budget > 0f; iteration++)
        {
            var pose = new Pose { Position = position, Rotation = Quaternion.Identity, Scale = Vector3.One };
            int count = _space.Overlap(shape, pose, _overlaps, mask);
            if (count == 0 || _overlaps[0].Depth < DepenetrationSlop) break;

            var deepest = _overlaps[0];
            float push = MathF.Min(deepest.Depth + Skin, budget);
            position += deepest.Normal * push;
            budget -= push;
            float into = Vector3.Dot(character.Velocity, deepest.Normal);
            if (into < 0) character.Velocity -= deepest.Normal * into;
        }
        return position;
    }

    // Crouching shrinks the capsule; standing up needs headroom (10 §3).
    private void Crouch(ref CharacterController character, in PawnIntent intent, MovementProfileRecord profile, Vector3 position, LayerMask mask)
    {
        bool wants = intent.Held.Has(_crouch);
        if (wants == character.Crouching)
        {
            character.Height = character.Crouching ? profile.CrouchHeight : profile.StandHeight;
            return;
        }

        if (wants)
        {
            character.Crouching = true;
            character.Height = profile.CrouchHeight;
            return;
        }

        // Standing up: is there room?
        float needed = profile.StandHeight - profile.CrouchHeight;
        var hit = Sweep(position, profile.CrouchHeight, profile.Radius, Vector3.UnitY, needed + Skin, mask);
        if (hit.Hit) return;   // stay crouched under the ceiling
        character.Crouching = false;
        character.Height = profile.StandHeight;
    }

    // The capsule stands on `feet`; Bepu's capsule is centred on its pose with the cylinder along Y.
    //
    // Sweeps that start already touching something carry no normal, and the capsule rests a skin width
    // above the floor all the time, so the controller asks the space to leave those out
    // (ignoreInitialOverlaps) and sees only what it is moving into; the skin width is what keeps the
    // capsule out of surfaces, and Depenetrate gets it out of anything it ends up inside (issue #259).
    private SweepHit Sweep(Vector3 feet, float height, float radius, Vector3 direction, float distance, LayerMask mask)
    {
        // The same factory the entity's own collider uses, so the shape it sweeps and the shape the
        // world sees are the same size in the same place (review #44).
        var shape = Collider.Standing(radius, height);
        var pose = new Pose { Position = feet, Rotation = Quaternion.Identity, Scale = Vector3.One };
        return _space.Sweep(shape, pose, direction, MathF.Max(distance, 1e-4f), mask, ignoreInitialOverlaps: true);
    }
}
