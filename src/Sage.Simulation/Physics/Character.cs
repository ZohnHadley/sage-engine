#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// What a character *is*, as opposed to how a backend moves it (REDESIGN §0.5 "Character controller:
// PawnIntent drives both; character part settings", issue #30): its movement profile, its controller
// state and the one call that gives an entity all of it. The 3D controller that sweeps a capsule
// through Bepu (CharacterMovementSystem) and the first-person rig are Sage.Physics3D's; combat, items,
// abilities and AI read only this, so they need no physics backend to compile against.

// How a character moves (issue #267, docs/design/10 "As built (movement modes)"). A profile names one
// (Walk unless it says otherwise); a character can override it (CharacterController.Mode), which is what
// the `noclip` and `fly` console commands do. Swimming and ladders are part of Walk and AirStrafe.
public enum MovementMode : byte
{
    Default,     // on a controller: take the profile's. On a profile: Walk
    Walk,        // on its feet, with the profile's air control: accelerates toward the wished velocity
    AirStrafe,   // GoldSrc (Half-Life 1): air acceleration that only adds speed along the wish, so strafing
                 // while turning gains speed, and a jump on the landing tick skips friction (bunny-hopping)
    Fly,         // no gravity, moves where it looks, Jump up and Crouch down, still collides (editors, debugging)
    Noclip,      // Fly through everything: no collision, no depenetration
}

// Movement tuning as data (10 §3). A game can give different profiles to the player, a guard or a
// horse without touching code.
[Record("movement_profile", Plugin = "sage.gameplay.character")]
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

    // Movement modes (issue #267).
    [Property(Tooltip = "Walk, AirStrafe (GoldSrc bunny-hopping), Fly or Noclip; Default = Walk")]
    public MovementMode Mode = MovementMode.Walk;
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds to go from standing to crouched and back; 0 = instant")]
    public float CrouchTime = 0.2f;
    [Property(Min = 0, Unit = "m/s", Tooltip = "AirStrafe: the most speed the air acceleration adds along the wish (GoldSrc's 30 units)")]
    public float AirStrafeSpeed = 0.76f;
    [Property(Min = 0, Tooltip = "AirStrafe: GoldSrc's sv_airaccelerate, times the wished speed per second")]
    public float AirStrafeAccelerate = 10f;
    [Property(Min = 0, Unit = "m/s", Tooltip = "Fly and Noclip: speed where it looks; Run doubles it")]
    public float FlySpeed = 8f;

    // Swimming (issue #262), in a water volume. Depths are fractions of StandHeight under the surface,
    // so a tall creature and a short one swim at the same point of their bodies.
    public float SwimSpeed = 3f;            // m/s, in any direction (where it looks, plus Jump up / Crouch down)
    public float SwimAcceleration = 10f;    // m/s² toward the wished velocity in water
    public float SwimDepth = 0.6f;          // swims once this much of it is under; below it, it wades
    public float SwimFloatDepth = 0.75f;    // how much of it is under when it floats at rest
    public float SwimRise = 1f;             // m/s it drifts up toward the surface with no input; 0 = neutral
    public float SwimClimbHeight = 1.7f;    // how far above its feet a ledge may be to climb out onto it

    // The values above, for when the record is missing: one shared instance, so the controller and the
    // camera can never disagree about a default (review #43).
    public static readonly MovementProfileRecord Fallback = new();
}

// A character the engine moves. The entity's Transform is its feet position, and its Collider is a
// Collider.Standing capsule anchored there (10 "As built"). Add one with World.AddCharacter, which
// gives it the collider, the kinematic body and the intent to match.
[Component("sage:character_controller")]
public struct CharacterController : IComponent
{
    [RecordRef("movement_profile"), Property(Tooltip = "How it moves; empty = the game's default (gameplay_conventions)")]
    public RecordId Profile;
    [Property(Min = 0, Max = 31, Tooltip = "Its own physics layer, which its sweeps ignore")]
    public byte Layer;              // its own layer, excluded from its sweeps ("player" or "enemy")
    [Property(Unit = "m/s", Tooltip = "Current velocity")]
    public Vector3 Velocity;
    [Transient] public float Height;          // current capsule height, between the profile's crouch and stand heights; 0 = from Crouching
    [Transient] public Vector3 GroundNormal;  // GroundCheck overwrites all three every tick
    [Transient] public bool Grounded;
    [Transient] public bool OnSteep;        // touching a surface steeper than the slope limit: it slides down it
    [Transient] public Vector3 GroundVelocity;   // how fast what it stands on moves (a lift, issue #261): it is carried along
    [Property(Tooltip = "Crouched or crouching: the capsule and its Collider shrink toward the profile's crouch height")]
    public bool Crouching;
    // A mode of its own over the profile's (issue #267): `noclip` and `fly` set it, Default gives it back.
    [Property(Tooltip = "Default = the profile's mode; the noclip and fly console commands set Noclip or Fly")]
    public MovementMode Mode;
    // On a ladder (issue #263). Not saved: a character loaded inside a ladder volume in mid-air catches it
    // again on its first tick, so a save mid-climb still loads on the ladder.
    [Transient] public bool Climbing;
    [Transient] public bool LetGo;   // jumped off a ladder: no ladder catches it until it leaves the volume or lands

    // Water (issue #262). InWater and UnderwaterSeconds are saved, so a load does not fire OnEnterWater
    // again or give a diver a fresh breath; the rest is derived every tick.
    [Property(Tooltip = "Its feet are in a water volume (set by the controller)")]
    public bool InWater;
    [Transient] public bool Swimming;       // deep enough to swim: SwimDepth of it under
    [Transient] public bool Underwater;     // its eyes are under the surface
    [Transient] public float Immersion;     // how much of it is under, 0 (dry) to 1 (submerged)
    [Transient] public Entity Water;        // the water volume it is in
    // The breath hook: how long its eyes have been under, 0 once they are out. Gameplay decides what
    // that costs (a breath meter, drowning damage, a spell that lets it breathe); the engine only counts.
    [Property(Unit = "s", Tooltip = "Seconds its eyes have been under water; 0 in air")]
    public float UnderwaterSeconds;

    // The layer is required: it is both what the character collides as and what its sweeps ignore, so
    // a default here would silently disagree with the entity's Collider (review #43). World.AddCharacter
    // keeps the two in step.
    public static CharacterController Create(byte layer, RecordId profile = default) =>
        new() { Profile = profile, Layer = layer, GroundNormal = Vector3.UnitY };

    // Where this character looks from, given its feet: the camera, a swing and the Use action all
    // start here, and they must agree (16 §3.2).
    public static Vector3 EyeOf(Vector3 feet, in CharacterController character, MovementProfileRecord profile)
    {
        float height = character.Height > 0f ? character.Height : character.Crouching ? profile.CrouchHeight : profile.StandHeight;
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
        var profile = CharacterConventions.Of(world).ProfileOf(records, profileId);

        // A character sweeps against everything *except its own layer* (see `CharacterMovementSystem`),
        // which is what lets creatures on one layer pass through each other. That makes the **default**
        // layer a trap: terrain collision and every other unlabelled static live there too, so a
        // character left on it ignores the ground and falls for ever, silently. Cost: one afternoon,
        // twice — once in the Sandbox's early days and once writing the guide's example game.
        //
        // Rate-limited rather than once per character: a game that spawns a hundred creatures the wrong
        // way would otherwise print a hundred lines, each with a different entity name so that none of
        // them collapse — which is how a useful warning turns into noise nobody reads.
        if (layer == world.Resources.Get<IPhysicsWorld>().Layers.Default)
            Log.Every(LogCat.Physics, LogLevel.Warn, "character-on-default-layer", TimeSpan.FromSeconds(5),
                      $"{World.Describe(entity)}: character on the 'default' physics layer ignores everything "
                      + "else on it, including the terrain it should stand on. Give it a layer of its own "
                      + "(\"character\": { \"layer\": \"player\" }).");

        world.Add(entity, CharacterController.Create(layer, profileId));
        world.Add(entity, new Pawn());
        // Seeded from the transform, or the first tick would spin every character to yaw 0 and throw
        // away the direction the scene placed it facing (review #43).
        world.Add(entity, new PawnIntent { Yaw = SageMath.YawOf(world.Get<Transform>(entity).LocalRotation) });
        world.Add(entity, Collider.Standing(profile.Radius, profile.StandHeight, layer));
        world.Add(entity, RigidBody.Kinematic());
    }
}
