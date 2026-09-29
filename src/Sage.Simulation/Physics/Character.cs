#nullable enable
using System;
using System.Numerics;
using Friflo.Engine.ECS;

namespace Sage.Simulation;

// What a character *is*, as opposed to how a backend moves it (REDESIGN §0.5 "Character controller:
// PawnIntent drives both; character part settings", issue #30): its movement profile, its controller
// state and the one call that gives an entity all of it. The 3D controller that sweeps a capsule
// through Bepu (CharacterMovementSystem) and the first-person rig are Sage.Physics3D's; combat, items,
// abilities and AI read only this, so they need no physics backend to compile against.

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

    public static readonly RecordId Default = new("sage", "default_movement");

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
    [RecordRef("movement_profile"), Property(Tooltip = "How it moves; empty = sage:default_movement")]
    public RecordId Profile;
    [Property(Min = 0, Max = 31, Tooltip = "Its own physics layer, which its sweeps ignore")]
    public byte Layer;              // its own layer, excluded from its sweeps ("player" or "enemy")
    [Property(Unit = "m/s", Tooltip = "Current velocity")]
    public Vector3 Velocity;
    [Transient] public float Height;          // derived from Crouching and the profile            // current capsule height; 0 = take the profile's StandHeight
    [Transient] public Vector3 GroundNormal;  // GroundCheck overwrites all three every tick
    [Transient] public bool Grounded;
    [Transient] public bool OnSteep;        // touching a surface steeper than the slope limit: it slides down it
    [Property(Tooltip = "Crouched: the capsule is the profile's crouch height")]
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
