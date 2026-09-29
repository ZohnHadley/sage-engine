#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// Camera rigs (issues #78, #79; REDESIGN §5 phase 4a, decision D2).
//
// A rig is a component on a **camera entity** that follows something — a pawn's head, a point over its
// shoulder — and writes the camera's `CameraPose` in FrameUpdate, before the CameraDirector reads it. The
// pawn itself carries no camera state, so its saves stay about the pawn; what the player chose about the
// view (which rig is on) is saved on the camera entity, like anything else about it.
//
// The player's own camera is an entity spawned from the `sage:player_camera` prefab by PlayerCameraSystem
// (a character plugin system) for each player pawn that has none following it. Experimental (SAGE0123):
// the multi-view renderer (#77) and the editor camera (#81) still push on this.

// The player's camera: the one PlayerCameraSystem spawned (or relinked) to follow a player pawn. Other
// cameras may follow the player too — a cutscene camera, a security monitor — without being this.
[Experimental("SAGE0123")]
[Tag("sage:player_camera")]
public struct PlayerCamera : ITag { }

// "components": { "sage:first_person_rig": { "enabled": true } }, or the `first_person_rig` part.
//
// Puts the camera in `Follow`'s head: at the character's eye (CharacterController.EyeOf — the height the
// swing and the Use action start from, so all three agree) of its interpolated pose, looking where its
// PawnIntent's view angles say. `Follow` is an entity, so content cannot write it; PlayerCameraSystem sets
// it for the player's camera, and a game's code sets it for its own. A pawn without a character sits the
// camera at its origin; one without a PawnIntent looks the way its transform does.
//
// The body it sits in is not drawn from this camera's view (`ShowBody` off, the default): a first-person
// camera inside its own pawn would otherwise see the inside of its head, and a third-person one behind it
// must see it (CameraRigs.HiddenBy, which the renderer asks per view — #77's seam).
[Experimental("SAGE0123")]
[Component("sage:first_person_rig")]
public struct FirstPersonRig : IComponent
{
    public Entity Follow;   // by PersistentId in a save; null when it is gone (the rig then leaves the pose alone)
    [Property(Tooltip = "Off: it leaves the camera's pose alone (the player camera's other rig may be on)")]
    public bool Enabled;
    [Property(Tooltip = "Draw the followed body from this view (a game with full-body awareness); off hides it")]
    public bool ShowBody;
}

// "components": { "sage:third_person_rig": { … } }, or the `third_person_rig` part (which has working
// defaults; a zeroed struct has no distance and would sit in the head).
//
// Over the shoulder of `Follow`: from its eye (as FirstPersonRig), out by `ShoulderOffset` in the view's
// frame (x right, y up, z back) and back by `Distance` along the view, looking the way its view angles
// say — so the centre of the screen looks where the pawn aims, a shoulder-width to the side. A sphere of
// `ProbeRadius` is swept from the head toward that point against `CollisionLayers` (the pawn itself is
// ignored): a wall in the way pulls the camera in **at once** (a camera behind a wall shows the wall),
// and when the way clears it eases back out over `Smoothing` seconds (exponential; 0 = at once).
//
// The eased state is a *length* along the boom (`Boom`), not a position, so a floating-origin rebase has
// nothing of the rig's to move; it is transient, and a rig switched on (the 1P/3P toggle, a load) starts
// at the length the probe allows rather than easing out from the head.
[Experimental("SAGE0123")]
[Component("sage:third_person_rig")]
public struct ThirdPersonRig : IComponent
{
    public const float DefaultDistance = 3f;
    public const float DefaultProbeRadius = 0.25f;
    public const float DefaultSmoothing = 0.3f;
    public static Vector3 DefaultShoulderOffset => new(0.45f, 0.25f, 0f);

    public Entity Follow;   // by PersistentId in a save; null when it is gone (the rig then leaves the pose alone)
    [Property(Tooltip = "Off: it leaves the camera's pose alone (the player camera's other rig may be on)")]
    public bool Enabled;
    [Property(Min = 0, Unit = "m", Tooltip = "How far behind the shoulder, along the view, when nothing is in the way")]
    public float Distance;
    [Property(Unit = "m", Tooltip = "From the eye, in the view's frame: x right, y up, z back")]
    public Vector3 ShoulderOffset;
    [Property(Min = 0, Unit = "m", Category = "Collision", Tooltip = "Radius of the sphere swept from the head to keep the camera out of walls")]
    public float ProbeRadius;
    [Property(Category = "Collision", Tooltip = "Physics layers the probe stops at, as a LayerMask's bits (the part takes names); 0 = every layer")]
    public uint CollisionLayers;
    [Property(Min = 0, Unit = "s", Category = "Collision", Tooltip = "How long it takes to ease back out once the way clears; 0 = at once")]
    public float Smoothing;

    [Transient] public float Boom;       // the eased distance from the head along the boom, this frame
    [Transient] public bool Settled;     // Boom is meaningful (false: the next frame starts where the probe allows)

    public readonly LayerMask Collision => new(CollisionLayers);

    public static ThirdPersonRig Create(Entity follow = default, bool enabled = true) => new()
    {
        Follow = follow,
        Enabled = enabled,
        Distance = DefaultDistance,
        ShoulderOffset = DefaultShoulderOffset,
        ProbeRadius = DefaultProbeRadius,
        CollisionLayers = 1u,   // "default": terrain, brushes and the scenery, not creatures
        Smoothing = DefaultSmoothing,
    };
}

// What kind of rig draws the screen this frame, for the code that draws differently in each: the
// crosshair, a viewmodel, a body that should not be seen from inside.
[Experimental("SAGE0123")]
public enum CameraRigKind
{
    None,           // no rig drives the screen: a fixed or scripted camera, ActiveCamera (no camera entity), or cam_free
    FirstPerson,
    ThirdPerson,
}

[Experimental("SAGE0123")]
public static class CameraRigs
{
    // The rig driving the screen's view this frame: read from CameraViews once the director has run (from
    // late FrameUpdate on — Extract, Render and Overlay all see this frame's). The question the crosshair
    // and a viewmodel ask ("am I looking out of the player's eyes?") in place of the obsolete
    // ActiveCamera.DrivenByRig. Allocates nothing.
    public static CameraRigKind MainViewRig(this World world)
    {
        if (!world.Resources.TryGet<CameraViews>(out var views) || views == null || !views.HasMain) return CameraRigKind.None;
        var camera = views.Main.Entity;
        if (camera.IsNull) return CameraRigKind.None;
        return RigOf(world, camera);
    }

    // The rig driving `camera`: third person wins when both are on (its system runs after the first-person
    // one, so its pose is the one the director reads). The player's toggle keeps exactly one on.
    public static CameraRigKind RigOf(World world, Entity camera)
    {
        if (world.TryGet<ThirdPersonRig>(camera, out var third) && third.Enabled) return CameraRigKind.ThirdPerson;
        if (world.TryGet<FirstPersonRig>(camera, out var first) && first.Enabled) return CameraRigKind.FirstPerson;
        return CameraRigKind.None;
    }

    // The entity a view from `camera` should not draw: the body a first-person rig sits in (unless it
    // shows it), and nothing for any other camera. The renderer asks this once per view (#77's seam,
    // ViewSource.HiddenFor), so the third-person view behind the pawn draws it and the first-person view
    // inside it does not. One entity, not a subtree: what it holds is drawn unless hidden the same way.
    public static Entity HiddenBy(World world, Entity camera)
    {
        if (camera.IsNull || RigOf(world, camera) != CameraRigKind.FirstPerson) return default;
        var rig = world.Get<FirstPersonRig>(camera);
        return rig.ShowBody || !world.IsAlive(rig.Follow) ? default : rig.Follow;
    }

    // Where `pawn` looks from and which way, at this frame's interpolation: its eye and its view angles.
    // False when it is not there to follow (null, destroyed, or without a transform).
    internal static bool TryHead(World world, RecordStore records, Entity pawn, float alpha,
                                 out Vector3 head, out Quaternion rotation)
    {
        if (!world.TryGet<GlobalTransform>(pawn, out var global))
        {
            head = default;
            rotation = Quaternion.Identity;
            return false;
        }
        var pose = global.Interpolated(alpha);
        head = pose.Position;
        if (world.TryGet<CharacterController>(pawn, out var character))
        {
            var profile = CharacterConventions.Of(world).ProfileOf(records, character.Profile);
            head = CharacterController.EyeOf(pose.Position, character, profile);
        }
        rotation = world.TryGet<PawnIntent>(pawn, out var intent)
            ? Quaternion.CreateFromYawPitchRoll(intent.Yaw, intent.Pitch, 0f)
            : pose.Rotation;
        return true;
    }
}
