#nullable enable
using System;

namespace Sage.Simulation;

// Engine signals (04 §3.1 mechanism 4, §3.5; issue #282): the rare things that happen to a world rather
// than in it — it was created, a scene was placed in it, it was paused — in two forms:
//
// - **On the engine** (`engine.Signals`), plain C# events, for engine-side code that is not a system: a
//   tool, a module, the client. Raised on the main thread where the thing happens, never inside a tick's
//   phases. Handlers are cheap (invalidate a cache, mark something dirty) and run nothing of gameplay.
// - **In the world**, as the `EngineSignal` game event on its Fixed queue, for systems: read with a cursor
//   like any other event (`world.Events.Reader<EngineSignal>(this)`), so a system never runs inside
//   whoever loaded the scene. A reader asked for in a system's constructor sees the `WorldCreated` of
//   its own world, because systems are added before the world is finished.
//
// **Pause is not raised here yet.** Who owns pausing (`World.Paused` today, world time and its scale in
// #283) calls `RaisePaused(world, paused)` when it changes. Note for a reader: a Fixed system that does
// not run while paused (RunCondition.WhenNotPaused, the default) reads `Paused` only when the world
// resumes, and in a world that keeps ticking while paused `ev_maxage` may drop it first; read it from a
// Frame-schedule system or one that runs `Always`.
public sealed class EngineSignals
{
    // A world has been made and furnished: every module's resources and systems, its scene placed and its
    // rules started. Raised at the end of Engine.CreateWorld (and CreateEditWorld, CreatePlayWorld).
    public event Action<World>? WorldCreated;

    // A world is about to be destroyed (Engine.DestroyWorld): it is still whole.
    public event Action<World>? WorldDestroying;

    // A scene has been placed in a world: its start scene when it was made, or another by `scene_load`
    // or a journey (Scenes.Load, Travel). Not raised for the start of a world with no scene.
    public event Action<World, RecordId>? SceneLoaded;

    // A world was paused (true) or resumed (false), by whoever owns pause calling RaisePaused.
    public event Action<World, bool>? PauseChanged;

    internal void RaiseWorldCreated(World world)
    {
        world.Events.Send(new EngineSignal(EngineSignalKind.WorldCreated, default));
        WorldCreated?.Invoke(world);
    }

    internal void RaiseWorldDestroying(World world) => WorldDestroying?.Invoke(world);

    internal void RaiseSceneLoaded(World world, RecordId scene)
    {
        world.Events.Send(new EngineSignal(EngineSignalKind.SceneLoaded, scene));
        SceneLoaded?.Invoke(world, scene);
    }

    // The hook for pause (#283): call it when a world's pause changes, not every tick it stays paused.
    public void RaisePaused(World world, bool paused)
    {
        world.Events.Send(new EngineSignal(paused ? EngineSignalKind.Paused : EngineSignalKind.Resumed, default));
        PauseChanged?.Invoke(world, paused);
    }
}

public enum EngineSignalKind
{
    WorldCreated,
    SceneLoaded,
    Paused,
    Resumed,
}

// An engine signal as a world's systems read it (see EngineSignals). `Scene` is the scene placed, for
// SceneLoaded; empty otherwise.
[GameEvent]
public readonly record struct EngineSignal(EngineSignalKind Kind, RecordId Scene);
