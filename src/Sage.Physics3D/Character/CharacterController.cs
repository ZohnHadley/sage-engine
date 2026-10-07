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
    private readonly World _world;
    private readonly WaterVolumes _water;
    // The space's gravity where the character being moved is (4m-17), times its profile's.
#pragma warning disable SAGE0129 // space gravity (4m-17): physics ships with the engine that declares it
    private SpaceGravity? _spaceGravity;
#pragma warning restore SAGE0129
    private float _gravityScale = 1f;

    // The outputs a character fires going into and out of water (issue #262); declared by CharacterModule.
    internal const string OnEnterWater = "OnEnterWater";
    internal const string OnExitWater = "OnExitWater";
    private readonly OverlapHit[] _ladderHits = new OverlapHit[8];

    public CharacterMovementSystem(World world, RecordStore records, ActionRegistry actions)
    {
        _world = world;
        _water = new WaterVolumes(world);
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
#pragma warning disable SAGE0129 // space gravity (4m-17): physics ships with the engine that declares it
        _spaceGravity ??= _world.Resources.TryGet<SpaceGravity>(out var gravity) ? gravity : null;
#pragma warning restore SAGE0129
        foreach (var (transforms, characters, intents, entities) in _characters.Chunks)
        {
            var t = transforms.Span;
            var c = characters.Span;
            var i = intents.Span;
            for (int n = 0; n < t.Length; n++)
                Move(entities.EntityAt(n), ref t[n], ref c[n], in i[n], dt);
        }
    }

    private void Move(Entity entity, ref Transform transform, ref CharacterController character, in PawnIntent intent, float dt)
    {
        var profile = _conventions.ProfileOf(_records, character.Profile);
        _gravityScale = _spaceGravity?.ScaleAt(_world, transform.LocalPosition) ?? 1f;
#pragma warning restore SAGE0129
        var mask = LayerMask.All.Except(character.Layer);
        // Height is not saved: a character loaded crouched comes back crouched, under whatever it was under.
        if (character.Height <= 0) character.Height = character.Crouching ? profile.CrouchHeight : profile.StandHeight;

        var mode = ModeOf(in character, profile);
        if (mode is MovementMode.Fly or MovementMode.Noclip)
            Fly(entity, ref transform, ref character, in intent, profile, mask, noclip: mode == MovementMode.Noclip, dt);
        else
            Walk(entity, ref transform, ref character, in intent, profile, mask, airStrafe: mode == MovementMode.AirStrafe, dt);

        // The world sees the capsule the controller sweeps: a crouching character's Collider shrinks with it.
        FitCollider(entity, character.Height, profile.Radius);
    }

    // The mode it moves in (issue #267): its own, else its profile's, else Walk.
    internal static MovementMode ModeOf(in CharacterController character, MovementProfileRecord profile) =>
        character.Mode != MovementMode.Default ? character.Mode
        : profile.Mode != MovementMode.Default ? profile.Mode : MovementMode.Walk;

    // On its feet: walking, swimming, climbing — the controller's whole move short of flying.
    private void Walk(Entity entity, ref Transform transform, ref CharacterController character, in PawnIntent intent, MovementProfileRecord profile, LayerMask mask, bool airStrafe, float dt)
    {
        Vector3 position = transform.LocalPosition;
        // Standing on something that moves (a lift, a mover, issue #261): carried with it, before anything
        // else, so what follows walks relative to the ground as it is now.
        if (character.Grounded && character.GroundVelocity != Vector3.Zero)
            position += character.GroundVelocity * dt;
        // Inside something (a door closed on it, a teleport, a spawn): out first, or every sweep below
        // starts overlapping and sees nothing (issue #259).
        position = Depenetrate(position, ref character, profile, mask);
        bool inWater = Wet(entity, position, ref character, profile, dt, out var water);
        // A swimmer's Crouch is "swim down", not a crouch.
        Crouch(ref character, intent.Held.Has(_crouch) && !character.Swimming, profile, position, mask, dt);

        // On a ladder the climb is the whole move (issue #263).
        if (Climb(ref position, ref character, in intent, profile, mask, dt))
        {
            transform.LocalRotation = SageMath.RotationFromYaw(intent.Yaw);
            transform.LocalPosition = position;
            return;
        }

        // Which way the player wants to go, in the view's frame.
        float yaw = intent.Yaw;
        var forward = SageMath.ForwardFromYaw(yaw);
        var right = new Vector3(-forward.Z, 0, forward.X);

        if (character.Swimming) Swim(ref character, in intent, profile, forward, right, in water, dt);
        else OnFoot(ref character, in intent, profile, forward, right, airStrafe, dt);

        // Water slows whatever moves through it, toward its current: a swimmer, someone wading, a fall
        // into a lake (issue #262).
        if (inWater) character.Velocity = BuoyancySystem.Drag(character.Velocity, water.Current, water.Drag * character.Immersion, dt);

        // Root motion (issue #357): an animator whose state takes it says how far the clip's root went last
        // tick, and that is the move across the ground (and up, for a climb with rootMotionY, instead of
        // gravity), swept and stepped like any other. Taken every tick so it never goes stale; a swimmer swims.
#pragma warning disable SAGE0126 // root motion: experimental with the rest of the animation API
        if (Animators.TryTakeRootMotion(_world, entity, out var root, out bool rootVertical) && !character.Swimming && dt > 0f)
            character.Velocity = new Vector3(root.X / dt, rootVertical ? root.Y / dt : character.Velocity.Y, root.Z / dt);
#pragma warning restore SAGE0126

        // The body faces where its controller is looking. One yaw convention engine-wide (SageMath),
        // so this is all of it: no correction between movement, sprites and the camera.
        transform.LocalRotation = SageMath.RotationFromYaw(yaw);

        // A swimmer neither snaps to the ground nor counts as standing on it.
        bool wasGrounded = character.Grounded && !character.Swimming;
        Vector3 motion = character.Velocity * dt;
        // The steepest ground that still counts as ground, once per move instead of once per sweep.
        float cosSlope = MathF.Cos(profile.MaxSlopeDegrees * MathF.PI / 180f);
        position = SlideHorizontal(position, motion with { Y = 0 }, ref character, profile, cosSlope, mask);
        position = MoveVertical(position, motion.Y, ref character, profile, mask);
        GroundCheck(ref position, ref character, profile, cosSlope, mask, wasGrounded);
        transform.LocalPosition = position;
    }

    // On land, or wading: walking, jumping, falling and sliding down what is too steep.
    private void OnFoot(ref CharacterController character, in PawnIntent intent, MovementProfileRecord profile, Vector3 forward, Vector3 right, bool airStrafe, float dt)
    {
        Vector3 wish = right * intent.Move.X + forward * intent.Move.Y;
        if (wish.LengthSquared() > 1f) wish = Vector3.Normalize(wish);

        float speed = (intent.Held.Has(_run) ? profile.RunSpeed : profile.WalkSpeed) * (character.Crouching ? profile.CrouchSpeedScale : 1f)
                      * (character.SpeedScale > 0f ? character.SpeedScale : 1f);   // gameplay's pace (#384)
        bool jump = character.Grounded && intent.Pressed.Has(_jump);
        if (airStrafe)
            AirStrafe(ref character, wish, speed, profile, jump, dt);
        else
            Accelerate(ref character, wish * speed, character.Grounded ? profile.Acceleration : profile.AirAcceleration,
                       character.Grounded && wish.LengthSquared() < 0.01f ? profile.Friction : 0f, dt);

        if (jump)
        {
            character.Velocity.Y = profile.JumpSpeed;
            character.Grounded = false;
        }
        else if (!character.Grounded)
        {
            character.Velocity.Y += profile.Gravity * _gravityScale * dt;
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
                character.Velocity += downhill * (MathF.Abs(profile.Gravity) * _gravityScale * (1f - normal.Y) * dt);

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
    }

    // Is it in water, and how deep (issue #262)? Sets the controller's water state, counts the breath
    // hook, and fires OnEnterWater / OnExitWater on the character and on the volume when that changes.
    private bool Wet(Entity entity, Vector3 feet, ref CharacterController character, MovementProfileRecord profile, float dt, out WaterSample water)
    {
        float stand = MathF.Max(profile.StandHeight, 0.1f);
        water = default;
        bool found = _water.Any && _water.Find(feet, feet.Y, feet.Y + character.Height, out water);

        character.Immersion = found ? Math.Clamp((water.Surface - feet.Y) / stand, 0f, 1f) : 0f;
        bool inWater = character.Immersion > 0f;
        character.Swimming = inWater && character.Immersion >= profile.SwimDepth;
        character.Underwater = inWater && CharacterController.EyeOf(feet, character, profile).Y < water.Surface;
        character.UnderwaterSeconds = character.Underwater ? character.UnderwaterSeconds + dt : 0f;

        if (inWater && !character.InWater)
        {
            character.InWater = true;
            character.Water = water.Volume;
            _world.FireOutput(entity, OnEnterWater, water.Volume);
            _world.FireOutput(water.Volume, OnEnterWater, entity);
        }
        else if (!inWater && character.InWater)
        {
            var left = character.Water;
            character.InWater = false;
            character.Water = default;
            _world.FireOutput(entity, OnExitWater, left);
            if (!left.IsNull && _world.IsAlive(left)) _world.FireOutput(left, OnExitWater, entity);
        }
        else if (inWater)
        {
            // Still in water: loaded in it (Water is not saved), or swum from one volume into the next.
            character.Water = water.Volume;
        }
        return inWater;
    }

    // Swimming (issue #262): moves where it looks, Jump swims up and Crouch down, it drifts up to float
    // with its head out, and the water's current carries it. It cannot swim higher than it floats: out
    // of the water is by climbing (SlideHorizontal's step, with SwimClimbHeight) or wading ashore.
    private void Swim(ref CharacterController character, in PawnIntent intent, MovementProfileRecord profile, Vector3 forward, Vector3 right, in WaterSample water, float dt)
    {
        Vector3 look = forward * MathF.Cos(intent.Pitch) + Vector3.UnitY * MathF.Sin(intent.Pitch);
        Vector3 wish = right * intent.Move.X + look * intent.Move.Y;
        if (wish.LengthSquared() > 1f) wish = Vector3.Normalize(wish);
        Vector3 target = wish * profile.SwimSpeed;

        // At rest it rises to its float line and sits there: a spring toward it, never faster than SwimRise up.
        float stand = MathF.Max(profile.StandHeight, 0.1f);
        float rise = Math.Clamp((character.Immersion - profile.SwimFloatDepth) * stand * 4f, -profile.SwimSpeed, profile.SwimRise);
        float vertical = (intent.Held.Has(_jump) ? 1f : 0f) - (intent.Held.Has(_crouch) ? 1f : 0f);
        target.Y = vertical != 0f ? vertical * profile.SwimSpeed : target.Y + rise;
        if (character.Immersion <= profile.SwimFloatDepth && target.Y > rise) target.Y = rise;

        target += water.Current;
        Vector3 delta = target - character.Velocity;
        float distance = delta.Length();
        if (distance > 1e-5f)
            character.Velocity += delta / distance * MathF.Min(profile.SwimAcceleration * dt, distance);
        character.Grounded = false;
        character.OnSteep = false;
    }

    // GoldSrc's movement (issue #267; Half-Life 1's PM_Friction, PM_Accelerate and PM_AirAccelerate). On
    // the ground, friction always and acceleration that only adds speed along the wish, up to the wished
    // speed. In the air, the speed added along the wish is capped at AirStrafeSpeed (0.76 m/s, its 30
    // units) but the acceleration is not, so a wish nearly at right angles to the velocity — strafing
    // while turning toward the strafe — adds speed every tick without the cap ever being reached. A jump
    // on the landing tick skips the ground's friction, which is what keeps that speed across a bunny-hop.
    private static void AirStrafe(ref CharacterController character, Vector3 wish, float speed, MovementProfileRecord profile, bool jumped, float dt)
    {
        Vector3 velocity = character.Velocity with { Y = 0 };
        float amount = wish.Length();
        Vector3 direction = amount > 1e-4f ? wish / amount : Vector3.Zero;
        float wishSpeed = speed * amount;
        float added, acceleration;

        if (character.Grounded && !jumped)
        {
            float current = velocity.Length();
            float drop = profile.Friction * dt * MathF.Max(current, 1f);
            velocity = current > drop ? velocity * ((current - drop) / current) : Vector3.Zero;
            added = wishSpeed - Vector3.Dot(velocity, direction);
            acceleration = profile.Acceleration * dt;
        }
        else
        {
            added = MathF.Min(wishSpeed, profile.AirStrafeSpeed) - Vector3.Dot(velocity, direction);
            acceleration = profile.AirStrafeAccelerate * wishSpeed * dt;
        }
        if (added > 0f && amount > 1e-4f) velocity += direction * MathF.Min(acceleration, added);
        character.Velocity = velocity with { Y = character.Velocity.Y };
    }

    // Flying (issue #267): Fly and Noclip, for editors and debugging. No gravity, no ground, no water or
    // ladders: it moves where it looks (pitch included), Jump rises and Crouch sinks, and it stops when
    // nothing is pressed. Fly slides along what it meets; Noclip goes through everything.
    private void Fly(Entity entity, ref Transform transform, ref CharacterController character, in PawnIntent intent, MovementProfileRecord profile, LayerMask mask, bool noclip, float dt)
    {
        Vector3 position = transform.LocalPosition;
        if (!noclip) position = Depenetrate(position, ref character, profile, mask);
        Wet(entity, position, ref character, profile, dt, out _);   // the breath hook still counts
        // It stands up (Crouch is "down" now), given the room; a noclipper needs none.
        if (noclip) { character.Crouching = false; character.Height = MoveToward(character.Height, profile.StandHeight, CrouchRate(profile) * dt); }
        else Crouch(ref character, false, profile, position, mask, dt);

        character.Grounded = false;
        character.OnSteep = false;
        character.Climbing = false;
        character.LetGo = false;
        character.GroundNormal = Vector3.UnitY;
        character.GroundVelocity = Vector3.Zero;

        var forward = SageMath.ForwardFromYaw(intent.Yaw);
        var right = new Vector3(-forward.Z, 0, forward.X);
        Vector3 look = forward * MathF.Cos(intent.Pitch) + Vector3.UnitY * MathF.Sin(intent.Pitch);
        Vector3 wish = right * intent.Move.X + look * intent.Move.Y;
        wish.Y += (intent.Held.Has(_jump) ? 1f : 0f) - (intent.Held.Has(_crouch) ? 1f : 0f);
        if (wish.LengthSquared() > 1f) wish = Vector3.Normalize(wish);
        Vector3 target = wish * (profile.FlySpeed * (intent.Held.Has(_run) ? 2f : 1f));

        Vector3 delta = target - character.Velocity;
        float distance = delta.Length();
        if (distance > 1e-5f)
            character.Velocity += delta / distance * MathF.Min(profile.Acceleration * dt, distance);

        Vector3 motion = character.Velocity * dt;
        position = noclip ? position + motion : SlideClimbing(position, motion, character.Height, profile, mask);
        transform.LocalRotation = SageMath.RotationFromYaw(intent.Yaw);
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

            // A low obstacle: try to step onto it instead of sliding along it. A swimmer with its head
            // out climbs out onto a ledge the same way, from further below (issue #262).
            float step = character.Grounded ? profile.StepHeight
                       : character.Swimming && !character.Underwater ? profile.SwimClimbHeight : 0f;
            if (step > 0f && hit.Normal.Y < cosSlope &&
                TryStep(position, motion, character.Height, step, profile, mask, cosSlope, out Vector3 stepped))
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
    private bool TryStep(Vector3 position, Vector3 motion, float height, float step, MovementProfileRecord profile, LayerMask mask, float cosSlope, out Vector3 stepped)
    {
        stepped = position;
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
        // A swimmer rising off the bottom is leaving it: snapping it back down would pin it there.
        if (character.Swimming && character.Velocity.Y > 0.01f)
        {
            character.Grounded = false;
            character.OnSteep = false;
            character.GroundNormal = Vector3.UnitY;
            return;
        }

        float reach = wasGrounded && character.Velocity.Y <= 0.01f ? profile.GroundSnap : Skin * 2f;
        var hit = Sweep(position + Vector3.UnitY * Skin, character.Height, profile.Radius, -Vector3.UnitY, reach + Skin, mask);

        if (hit.Hit && hit.Normal.Y >= cosSlope)
        {
            character.Grounded = true;
            character.OnSteep = false;
            character.GroundNormal = hit.Normal;
            character.GroundVelocity = GroundVelocityOf(hit.Entity, position);
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
                character.GroundVelocity = GroundVelocityOf(recovery.Entity, recovery.Position);
                character.OnSteep = false;
                character.GroundNormal = recovery.Normal;
                if (character.Velocity.Y < 0) character.Velocity.Y = 0;
                return;
            }
        }

        character.Grounded = false;
        character.GroundVelocity = Vector3.Zero;
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

    // Ladders (issue #263, 10 §3): a character overlapping a `Ladder` trigger volume catches it when it is
    // in the air (stepping off the top of one, jumping at one) or pushes toward its rungs from the ground.
    // On it there is no gravity: pushing toward the rungs climbs, pulling away climbs down, sideways moves
    // along it at half speed. It lets go when it jumps (pushed off the face), when it reaches the ground
    // climbing down, when it leaves the volume, and at the top, where a walkable ledge within step height
    // is stepped onto — the same step-up a stair uses. Returns true when the climb was this tick's move.
    private bool Climb(ref Vector3 position, ref CharacterController character, in PawnIntent intent, MovementProfileRecord profile, LayerMask mask, float dt)
    {
        if (!FindLadder(position, character.Height, profile.Radius, mask, out var ladder, out var normal))
        {
            character.Climbing = false;   // off the side, or out of the top with nothing to step onto
            character.LetGo = false;
            return false;
        }

        // Jumped off: not caught again until it has left the volume or landed.
        if (character.LetGo)
        {
            if (!character.Grounded) return false;
            character.LetGo = false;
        }

        // The wish in the world, split into toward the rungs (climb) and along them (sideways).
        var forward = SageMath.ForwardFromYaw(intent.Yaw);
        var right = new Vector3(-forward.Z, 0, forward.X);
        Vector3 wish = right * intent.Move.X + forward * intent.Move.Y;
        if (wish.LengthSquared() > 1f) wish = Vector3.Normalize(wish);
        float toward = -Vector3.Dot(wish, normal);
        var side = new Vector3(-normal.Z, 0, normal.X);
        float along = Vector3.Dot(wish, side);

        if (!character.Climbing)
        {
            // Walking past the bottom of a ladder, or away from it, is not climbing it.
            if (character.Grounded && toward <= 0.3f) return false;
            character.Climbing = true;
            character.Velocity = Vector3.Zero;
        }

        if (intent.Pressed.Has(_jump))
        {
            // Let go, pushed off the face; the normal move takes it from here.
            character.Climbing = false;
            character.LetGo = true;
            character.Velocity = normal * profile.WalkSpeed + Vector3.UnitY * profile.JumpSpeed * 0.5f;
            character.Grounded = false;
            return false;
        }

        float speed = ladder.SpeedOrDefault;
        character.Velocity = Vector3.UnitY * (toward * speed) + side * (along * speed * 0.5f);
        character.Grounded = false;   // no step-ups while sliding sideways on the rungs
        character.OnSteep = false;
        float cosSlope = MathF.Cos(profile.MaxSlopeDegrees * MathF.PI / 180f);

        // At the top: a ledge behind the rungs within step height is stepped onto, and that is the dismount
        // — a capsule's width onto it, so it stands on the top rather than balancing on the lip.
        if (toward > 0f &&
            TryStep(position, -normal * (2f * profile.Radius + Skin), character.Height, profile.StepHeight, profile, mask, cosSlope, out Vector3 stepped) &&
            Vector3.Dot(stepped - position, -normal) >= profile.Radius)   // over the lip, not stopped against it
        {
            position = stepped;
            character.Climbing = false;
            character.Velocity = Vector3.Zero;
            GroundCheck(ref position, ref character, profile, cosSlope, mask, wasGrounded: true);
            return true;
        }

        position = SlideClimbing(position, character.Velocity * dt, character.Height, profile, mask);
        GroundCheck(ref position, ref character, profile, cosSlope, mask, wasGrounded: false);

        // At the bottom, climbing down: standing again.
        if (character.Grounded && toward < 0f) character.Climbing = false;
        return true;
    }

    // Collide-and-slide in three dimensions, with no steps and no slope rules: a climber caught on the lip
    // at the top slides down off it instead of hanging there, and one sliding sideways follows the wall.
    private Vector3 SlideClimbing(Vector3 position, Vector3 motion, float height, MovementProfileRecord profile, LayerMask mask)
    {
        for (int iteration = 0; iteration < SlideIterations; iteration++)
        {
            float distance = motion.Length();
            if (distance < 1e-5f) break;
            Vector3 direction = motion / distance;
            var hit = Sweep(position, height, profile.Radius, direction, distance, mask);
            if (!hit.Hit) return position + motion;

            float travel = MathF.Max(0f, hit.Distance - Skin);
            position += direction * travel;
            Vector3 remaining = motion - direction * travel;
            motion = remaining - hit.Normal * Vector3.Dot(remaining, hit.Normal);
        }
        return position;
    }

    // The ladder the capsule overlaps, and its climbable face's outward normal.
    private bool FindLadder(Vector3 position, float height, float radius, LayerMask mask, out Ladder ladder, out Vector3 normal)
    {
        var shape = Collider.Standing(radius, height);
        var pose = new Pose { Position = position, Rotation = Quaternion.Identity, Scale = Vector3.One };
        int count = _space.Overlap(shape, pose, _ladderHits, mask, includeTriggers: true);
        for (int i = 0; i < count; i++)
        {
            var entity = _ladderHits[i].Entity;
            if (entity.IsNull || !entity.TryGetComponent<Ladder>(out ladder)) continue;
            var rotation = entity.TryGetComponent<Transform>(out var transform) ? transform.LocalRotation : Quaternion.Identity;
            normal = ladder.NormalFor(rotation);
            return true;
        }
        ladder = default;
        normal = Vector3.UnitZ;
        return false;
    }

    // A kinematic body's velocity where the feet are (a mover, issue #261; turning, as a hinged door or
    // a turntable does, issue #266: v + ω × r); nothing else under the feet moves the feet.
    private Vector3 GroundVelocityOf(Entity ground, Vector3 feet)
    {
        if (ground.IsNull || !ground.TryGetComponent<PhysicsBody>(out var body) || body.IsStatic || _space.IsDynamic(body))
            return Vector3.Zero;
        return _space.PointVelocityOf(body, feet);
    }

    // Crouching shrinks the capsule, over the profile's CrouchTime, and standing up needs headroom for the
    // whole of the rest of its height (10 §3); a character that has run out of it stays as low as it is.
    // Crouching is true from the moment it starts going down until it starts standing up, so the
    // crouched speed applies all the way down.
    private void Crouch(ref CharacterController character, bool wants, MovementProfileRecord profile, Vector3 position, LayerMask mask, float dt)
    {
        float stand = profile.StandHeight;
        float target = wants ? MathF.Min(profile.CrouchHeight, stand) : stand;
        if (target > character.Height + 1e-5f)
        {
            // Standing up: is there room?
            var hit = Sweep(position, character.Height, profile.Radius, Vector3.UnitY, stand - character.Height + Skin, mask);
            if (hit.Hit)
            {
                character.Crouching = true;   // stay crouched under the ceiling
                return;
            }
        }
        character.Crouching = wants;
        character.Height = MoveToward(character.Height, target, CrouchRate(profile) * dt);
    }

    // How fast the capsule's height changes, in m/s; instant with no CrouchTime.
    private static float CrouchRate(MovementProfileRecord profile) =>
        profile.CrouchTime > 0f ? MathF.Max(profile.StandHeight - profile.CrouchHeight, 0.01f) / profile.CrouchTime : float.PositiveInfinity;

    private static float MoveToward(float from, float to, float step) =>
        from < to ? MathF.Min(from + step, to) : MathF.Max(from - step, to);

    // Keeps the entity's Collider the height the controller sweeps (issue #267): a crouched character is
    // hit, stood on and seen at its crouched height. Only a Collider.Standing capsule is fitted, and only
    // when the height changed; the body keeps its handle (IPhysicsWorld.SetShape). A body not built yet
    // is built from the fitted Collider by the sync.
    private void FitCollider(Entity entity, float height, float radius)
    {
        if (!entity.TryGetComponent<Collider>(out var collider) || collider.Shape != ColliderShape.Capsule
            || collider.Center.X != 0f || collider.Center.Z != 0f || collider.Center.Y <= 0f || MathF.Abs(collider.Center.Y * 2f - height) < 1e-4f)
            return;
        var fitted = Collider.Standing(collider.Size.X > 0f ? collider.Size.X : radius, height);
        collider.Size = fitted.Size;
        collider.Center = fitted.Center;
        _world.Get<Collider>(entity) = collider;
        if (entity.TryGetComponent<PhysicsBody>(out var body))
            _space.SetShape(body, collider, PhysicsPoses.WorldPose(entity));
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
