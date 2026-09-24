#nullable enable
using System;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// The kinematic character controller (docs/design/10 §3, TODO F7): a capsule moved by sweeps, not a
// dynamic body, because a rigid body makes bad stairs and worse netcode. Deterministic and headless:
// the same code runs on a future server.

// Movement tuning as data (10 §3). A game can give different profiles to the player, a guard or a
// horse without touching code.
[Record("movement_profile")]
public sealed class MovementProfileRecord
{
    public float WalkSpeed = 4.2f;
    public float RunSpeed = 7f;
    public float Acceleration = 55f;        // m/s² toward the wished velocity on the ground
    public float AirAcceleration = 12f;
    public float Friction = 8f;             // deceleration with no input, on the ground
    public float JumpSpeed = 5.2f;
    public float Gravity = -18f;            // snappier than reality, like most first-person games
    public float MaxSlopeDegrees = 50f;
    public float StepHeight = 0.45f;
    public float Radius = 0.35f;
    public float StandHeight = 1.8f;
    public float CrouchHeight = 1.05f;
    public float CrouchSpeedScale = 0.45f;
    public float GroundSnap = 0.35f;        // how far it sticks to the ground when walking downhill
    public float EyeOffset = -0.18f;        // eye height relative to the top of the capsule

    public static readonly RecordId Default = new("sage", "default_movement");

    // The values above, for when the record is missing: one shared instance, so the controller and the
    // camera can never disagree about a default (review #43).
    internal static readonly MovementProfileRecord Fallback = new();
}

// A character the engine moves. The entity's Transform is its feet position, and its Collider is a
// Collider.Standing capsule anchored there (10 "As built"). Add one with World.AddCharacter, which
// gives it the collider, the kinematic body and the intent to match.
public struct CharacterController : IComponent
{
    public RecordId Profile;
    public byte Layer;              // its own layer, excluded from its sweeps ("player" or "enemy")
    public Vector3 Velocity;
    [Transient] public float Height;          // derived from Crouching and the profile            // current capsule height; 0 = take the profile's StandHeight
    [Transient] public Vector3 GroundNormal;  // GroundCheck overwrites all three every tick
    [Transient] public bool Grounded;
    [Transient] public bool OnSteep;        // touching a surface steeper than the slope limit: it slides down it
    public bool Crouching;

    // The layer is required: it is both what the character collides as and what its sweeps ignore, so
    // a default here would silently disagree with the entity's Collider (review #43). World.AddCharacter
    // keeps the two in step.
    public static CharacterController Create(byte layer, RecordId profile = default) =>
        new() { Profile = profile, Layer = layer, GroundNormal = Vector3.UnitY };

    // Where this character looks from, given its feet: the camera, a swing and the Use action all
    // start here, and they must agree (16 §3.2).
    public static Vector3 EyeOf(Vector3 feet, in CharacterController character, MovementProfileRecord profile)
    {
        float height = character.Height > 0f ? character.Height : profile.StandHeight;
        return feet + Vector3.UnitY * MathF.Max(height + profile.EyeOffset, 0.2f);
    }
}

// Adding a character to a world (16 §3.1).
public static class CharacterExtensions
{
    // Everything a controller needs to drive a body: the controller, a Pawn, an intent already facing
    // the way the entity was placed, a standing capsule on the character's own layer, and a kinematic
    // body so the world collides with it. Doing this in one place is what keeps the sweep layer, the
    // collider layer and the capsule's height in agreement (review #43, #44).
    public static void AddCharacter(this World world, Entity entity, byte layer, RecordId profileId = default)
    {
        var records = world.Resources.Get<RecordStore>();
        var id = profileId.IsEmpty ? MovementProfileRecord.Default : profileId;
        var profile = records.TryGet(id, out MovementProfileRecord found) ? found : MovementProfileRecord.Fallback;

        // A character sweeps against everything *except its own layer* (see `CharacterMovementSystem`),
        // which is what lets creatures on one layer pass through each other. That makes the **default**
        // layer a trap: terrain collision and every other unlabelled static live there too, so a
        // character left on it ignores the ground and falls for ever, silently. Cost: one afternoon,
        // twice — once in the Sandbox's early days and once writing the guide's example game.
        if (layer == world.Resources.Get<PhysicsSpace>().Layers.Default)
            Log.Warn(LogCat.Physics, $"{World.Describe(entity)}: character on the 'default' physics layer "
                                   + "ignores everything else on it, including the terrain it should stand on. "
                                   + "Give it a layer of its own (\"character\": { \"layer\": \"player\" }).");

        world.Add(entity, CharacterController.Create(layer, profileId));
        world.Add(entity, new Pawn());
        // Seeded from the transform, or the first tick would spin every character to yaw 0 and throw
        // away the direction the scene placed it facing (review #43).
        world.Add(entity, new PawnIntent { Yaw = SageMath.YawOf(world.Get<Transform>(entity).LocalRotation) });
        world.Add(entity, Collider.Standing(profile.Radius, profile.StandHeight, layer));
        world.Add(entity, RigidBody.Kinematic());
    }
}

// PrePhysics, before the bodies are synced (10 §3): turns intent into movement, resolves it against
// the world with sweeps, and leaves the result in Transform.
public sealed class CharacterMovementSystem : ISystem
{
    private const float Skin = 0.02f;         // never move fully into a surface
    private const float GroundOffset = 0.06f; // horizontal sweeps start this far above the feet
    private const int SlideIterations = 4;   // 10 §3: collide-and-slide, up to four planes

    private readonly ArchetypeQuery<Transform, CharacterController, PawnIntent> _characters;
    private readonly RecordStore _records;
    private readonly PhysicsSpace _space;
    private readonly ActionId _jump, _crouch, _run;

    public CharacterMovementSystem(World world, RecordStore records, ActionRegistry actions)
    {
        _characters = world.Query<Transform, CharacterController, PawnIntent>();
        _records = records;
        _space = world.Resources.Get<PhysicsSpace>();
        _jump = actions.Get("Jump");
        _crouch = actions.Get("Crouch");
        _run = actions.Get("Run");
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
        var profile = _records.TryGet(character.Profile.IsEmpty ? MovementProfileRecord.Default : character.Profile, out MovementProfileRecord found)
            ? found : DefaultProfile;
        var mask = LayerMask.All.Except(character.Layer);
        if (character.Height <= 0) character.Height = profile.StandHeight;

        Vector3 position = transform.LocalPosition;
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

    private static readonly MovementProfileRecord DefaultProfile = new();

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
    // Sweeps that start already touching something carry no normal, so the space reports them as a
    // flag rather than a hit (PhysicsSpace.SweepHandler); the skin width is what keeps the capsule out
    // of surfaces. Real depenetration needs a shape-overlap query (10 §4, not built yet).
    private SweepHit Sweep(Vector3 feet, float height, float radius, Vector3 direction, float distance, LayerMask mask)
    {
        // The same factory the entity's own collider uses, so the shape it sweeps and the shape the
        // world sees are the same size in the same place (review #44).
        var shape = Collider.Standing(radius, height);
        var pose = new Pose { Position = feet, Rotation = Quaternion.Identity, Scale = Vector3.One };
        return _space.Sweep(shape, pose, direction, MathF.Max(distance, 1e-4f), mask);
    }
}

// FrameUpdate: puts the camera in the player's head, at display rate, from the interpolated pose and
// the view angles the command carried (06 §3.3, 16 §3.2). While this drives the camera it says so
// (ActiveCamera.DrivenByRig) and the editor's free camera stands aside; cam_free clears
// ActiveCamera.RigEnabled and this system gives the camera back.
public sealed class FirstPersonCameraSystem : ISystem
{
    private readonly ArchetypeQuery<GlobalTransform, CharacterController, PawnIntent> _pawns;
    private readonly ActiveCamera _camera;
    private readonly RecordStore _records;

    public FirstPersonCameraSystem(World world, RecordStore records)
    {
        _pawns = world.Query<GlobalTransform, CharacterController, PawnIntent>().AllTags(Tags.Get<PlayerControlled>());
        _camera = world.Resources.Get<ActiveCamera>();
        _records = records;
    }

    public void Run(in SystemContext ctx)
    {
        if (!_camera.RigEnabled)   // cam_free: the editor camera is flying instead
        {
            _camera.DrivenByRig = false;
            return;
        }

        float alpha = ctx.Frame.Alpha;
        foreach (var (globals, characters, intents, _) in _pawns.Chunks)
        {
            if (globals.Length == 0) continue;
            ref readonly var character = ref characters.Span[0];   // one local player
            var profile = _records.TryGet(character.Profile.IsEmpty ? MovementProfileRecord.Default : character.Profile, out MovementProfileRecord found)
                ? found : null;
            float eye = character.Height + (profile ?? MovementProfileRecord.Fallback).EyeOffset;
            _camera.Position = globals.Span[0].Interpolated(alpha).Position + Vector3.UnitY * eye;
            _camera.Rotation = Quaternion.CreateFromYawPitchRoll(intents.Span[0].Yaw, intents.Span[0].Pitch, 0);
            _camera.DrivenByRig = true;
            return;
        }
    }
}
