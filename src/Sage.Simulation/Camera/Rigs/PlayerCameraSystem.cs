#nullable enable
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// FrameUpdate, before the rigs: every player pawn (PlayerControlled, with a PawnIntent) has a camera that
// follows it (issue #78, decision D2). The character plugin installs it.
//
// **Where the camera comes from.** The `sage:player_camera` prefab (engine content; a game patches it to
// change the player's view — field of view, which rig it starts in), spawned at the pawn and pointed at it.
// A world with no such prefab (a test or tool that mounts no engine content) gets the same camera built
// from the parts' defaults. A game that wants no player camera disables this system by id, or puts a
// camera of higher priority on the screen.
//
// **Saves.** The camera entity is saved when its pawn is: it gets a Persistent id derived from the pawn's
// (so the same pawn's camera has the same id every run), and a load rebuilds it from its prefab with its
// saved components over the top — each rig's Follow comes back as a reference to the rebuilt pawn, and
// the player's choice of rig (first or third person) with it. A load destroys and rebuilds every persistent entity, so the camera
// is neither duplicated nor lost. What a save cannot carry is repaired here, once, rather than every frame:
//   - a save from before #78 has no camera: the pawn has none following it, and one is spawned;
//   - a player camera whose pawn is gone (a non-persistent camera beside a reloaded pawn, a pawn a game
//     destroyed and replaced) is **relinked** to the player that has none, rather than a second made.
//
// The steady state is two short loops over a handful of entities and allocates nothing; a spawn happens
// once per player.
[Experimental("SAGE0123")]
[System(Id, Phase.FrameUpdate, Before = new[] { FirstPersonRigSystem.Id, ThirdPersonRigSystem.Id, CameraDirector.Id })]
public sealed class PlayerCameraSystem : ISystem
{
    public const string Id = "sage.camera.player";

    // The prefab every player camera is spawned from: engine content, patchable by a game.
    public static readonly RecordId Prefab = new("sage", "player_camera");

    private readonly World _world;
    private readonly Query<PawnIntent> _players;
    private readonly Query<Camera> _cameras;

    public PlayerCameraSystem(World world)
    {
        _world = world;
        _players = world.Query<PawnIntent>().AllTags(Tags.Get<PlayerControlled>());
        _cameras = world.Query<Camera>().AllTags(Tags.Get<PlayerCamera>());
    }

    public void Run(in SystemContext ctx)
    {
        // One pawn without a camera per frame: there is one local player, and a second would wait a frame.
        Entity lonely = default;
        foreach (var (_, pawns) in _players.Chunks)
        {
            for (int i = 0; i < pawns.Length && lonely.IsNull; i++)
            {
                var pawn = pawns.EntityAt(i);
                if (!IsFollowed(pawn)) lonely = pawn;
            }
            if (!lonely.IsNull) break;
        }
        if (lonely.IsNull) return;

        var orphan = FindOrphan();
        if (!orphan.IsNull) Point(orphan, lonely);
        else Spawn(_world, lonely);
    }

    private bool IsFollowed(Entity pawn)
    {
        foreach (var (_, cameras) in _cameras.Chunks)
            for (int i = 0; i < cameras.Length; i++)
                if (Follows(cameras.EntityAt(i)) == pawn) return true;
        return false;
    }

    // A player camera whose pawn is gone.
    private Entity FindOrphan()
    {
        foreach (var (_, cameras) in _cameras.Chunks)
            for (int i = 0; i < cameras.Length; i++)
            {
                var camera = cameras.EntityAt(i);
                if (!_world.IsAlive(Follows(camera))) return camera;
            }
        return default;
    }

    // What a player camera follows: its rigs' Follow (the same pawn for both).
    private Entity Follows(Entity camera) =>
        _world.TryGet<FirstPersonRig>(camera, out var first) && !first.Follow.IsNull ? first.Follow
        : _world.TryGet<ThirdPersonRig>(camera, out var third) ? third.Follow : default;

    private void Point(Entity camera, Entity pawn)
    {
        Aim(_world, camera, pawn);
        Log.Info(LogCat.World, $"The player camera {World.Describe(camera)} follows {World.Describe(pawn)} now");
    }

    // A camera for `pawn`: the prefab when there is one, else the parts' defaults. Public so a game's rules
    // can make one on the spot (a pawn possessed mid-frame) rather than wait for the next frame.
    public static Entity Spawn(World world, Entity pawn)
    {
        var at = world.TryGet<Transform>(pawn, out var transform) ? transform.LocalPosition : default;
        Entity camera = default;
        if (world.Engine is { } engine && engine.Records.TryGet(Prefab, out PrefabRecord _))
            camera = world.Spawn(Prefab, at);
        if (camera.IsNull)
        {
            camera = world.Create(Transform.At(at), "player camera");
            world.Add(camera, Camera.Perspective());
            new FirstPersonRigPart().AddTo(world, camera, pawn);
            new ThirdPersonRigPart { Enabled = false }.AddTo(world, camera, pawn);
            new ViewmodelPart().AddTo(world, camera);   // first-person arms, shown when gameplay names some (#121)
        }

        if (!camera.Tags.Has<PlayerCamera>()) camera.AddTag<PlayerCamera>();
        if (!world.Has<FirstPersonRig>(camera) && !world.Has<ThirdPersonRig>(camera))
            new FirstPersonRigPart().AddTo(world, camera, pawn);   // a patched prefab with no rig at all
        Aim(world, camera, pawn);

        // Saved with its pawn, under an id derived from the pawn's, so a load finds the same camera.
        if (world.TryGet<Persistent>(pawn, out var persistent) && !persistent.Id.IsEmpty && !world.Has<Persistent>(camera))
            world.Add(camera, new Persistent { Id = PersistentId.FromName($"camera:{persistent.Id}") });
        return camera;
    }

    private static void Aim(World world, Entity camera, Entity pawn)
    {
        if (world.Has<FirstPersonRig>(camera)) world.Get<FirstPersonRig>(camera).Follow = pawn;
        if (world.Has<ThirdPersonRig>(camera)) world.Get<ThirdPersonRig>(camera).Follow = pawn;
    }
}
