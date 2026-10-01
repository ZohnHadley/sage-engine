#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;

namespace Sage.Editing;

// Where play starts from: a camera's eye and the way it faces (yaw in degrees, as a placement's). The
// player stands on the ground below the eye, facing the same way.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public readonly record struct PlayStart(Vector3 Eye, float Yaw);

// Play-in-editor (issue #226; phase 10a decision 2, design/15 §10f): Play makes a **second, real world**
// from the document and the game plays in it; Stop throws it away. The edit world is never unpaused or
// simulated, so nothing a play does — a door opened, a crate knocked over, a goblin killed — can reach the
// document or the entities the editor shows: they are in another world, and that world is destroyed.
//
// - **The play world is built the way the host builds its main world** (`Engine.CreatePlayWorld`): every
//   module furnishes it, the game's rules are made and *started*, so it has its player and whatever the
//   rules add, and its Fixed systems run. It is in the scene the edit world shows (a new level's scene once
//   it is a record), and the document is placed from the editor's copy in memory, not from its file: an
//   unsaved placement, override or wire plays.
// - **The player** is the one the rules spawned at the scene's start; given a `PlayStart` (the free
//   camera, in the host), it is moved to the ground below that eye, standing as high above it as the
//   scene's start stands above its own ground, and turned the camera's way.
// - **The session is the only thing that knows**: the host asks `IsPlaying` and `World` to choose what
//   it ticks into, draws and sends input to, and hears `Started` / `Stopped`.
//
// One session per document; a second Play while playing is refused rather than stacking worlds.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class PlaySession
{
    public const string WorldName = "play";
    private const float DefaultStand = 1f;    // metres above the ground when the scene's start says nothing
    private const float MaxStand = 3f;

    public PlaySession(EditDocument document)
    {
        Document = document;
    }

    public EditDocument Document { get; }
    public World EditWorld => Document.World;

    // The play world while playing; null otherwise.
    public World? World { get; private set; }
    public bool IsPlaying => World != null;

    // Where `ed_play` with no position starts from: the host's free camera. Null headless.
    public Func<PlayStart?>? Camera { get; set; }

    // The play world was made (and its player placed), or is about to be destroyed.
    public event Action<World>? Started;
    public event Action<World>? Stopping;
    // Play stopped: the play world is gone, and the edit world is what there is again.
    public event Action? Stopped;

    // The scene play starts in: a new level's own scene once it is a record, else the one the edit world
    // shows. Empty when the edit world has none (the game's start scene is used, which may be none too).
    public RecordId Scene =>
        !Document.Scene.IsEmpty && Document.Engine.Records.Exists("scene", Document.Scene) ? Document.Scene : Scenes.Current(EditWorld);

    // Builds the play world from the document and starts it; null (and a log line) when already playing.
    public World? Play(PlayStart? from = null)
    {
        if (World != null)
        {
            Log.Warn(LogCat.Editor, "Already playing: ed_stop first");
            return null;
        }

        var engine = Document.Engine;
        var scene = Scene;
        // A document that is not open plays as the scene alone, from the store.
        var world = Document.IsOpen
            ? engine.CreatePlayWorld(WorldName, scene, Document.Id, Document.Record)
            : engine.CreatePlayWorld(WorldName, scene, default, new PlacementsRecord());
        World = world;
        if (from is { } start) MovePlayer(world, start);
        Log.Info(LogCat.Editor, $"Playing '{(Document.IsOpen ? Document.Title : "(no document)")}' in scene " +
                                $"{(scene.IsEmpty ? "(none)" : scene.ToString())}: ed_stop (or Escape) returns to the editor");
        Started?.Invoke(world);
        return world;
    }

    // Throws the play world away; the edit world and the document are as they were before Play. False
    // when not playing.
    public bool Stop()
    {
        if (World is not { } world) return false;
        Stopping?.Invoke(world);
        World = null;
        if (world.Engine is { } engine && engine.Worlds.Contains(world)) engine.DestroyWorld(world);
        Log.Info(LogCat.Editor, "Stopped playing: back to the editor");
        Stopped?.Invoke();
        return true;
    }

    // ---- The player ---------------------------------------------------------------------------------

    private void MovePlayer(World world, PlayStart start)
    {
        var player = Scenes.Player(world);
        if (player.IsNull || !world.Has<Transform>(player))
        {
            Log.Warn(LogCat.Editor, "Playing from the camera: the rules spawned no player, so there is nobody to move");
            return;
        }

        // Measured in the edit world, whose origin (R6) need not be the new world's: through absolute metres.
        var at = world.Origin().ToOrigin(EditWorld.Origin().ToAbsolute(StandBelow(EditWorld, start.Eye)));
        float yaw = start.Yaw * MathF.PI / 180f;
        var where = Transform.At(at);
        where.LocalRotation = SageMath.RotationFromYaw(yaw);
        world.Teleport(player, where);
        // And face that way: the view angles are the host's (PlayerInput.RequestView), and a pawn's body
        // turns to them every tick.
        world.Resources.GetOrAdd(() => new PlayerInput()).RequestView(yaw);
    }

    // Where a player put down below `eye` stands, read off the edit world (whose physics mirrors the
    // scene, and which is the same scene as the play world's): on the first surface below the eye, as high
    // above it as the scene's own player start stands above the surface below *it* (a pawn whose origin is
    // its middle starts a metre up, one whose origin is its feet on the ground). The eye itself when there
    // is nothing below it.
    public static Vector3 StandBelow(World world, Vector3 eye)
    {
        if (Placing.Surface(world, new EditorRay(eye, -Vector3.UnitY)) is not { } ground) return eye;
        float stand = DefaultStand;
        if (world.PlayerStart() is { } start
            && Placing.Surface(world, new EditorRay(start + new Vector3(0, 0.05f, 0), -Vector3.UnitY)) is { } below)
            stand = Math.Clamp(start.Y - below.Y, 0f, MaxStand);
        return ground + new Vector3(0, stand, 0);
    }
}
