#nullable enable

namespace Sage.Simulation;

// Game flow lives in one place (docs/design/16 §3.1, readiness rule 5): spawning the player, deaths,
// time of day, what happens after a save is loaded. A game subclasses this and installs it as a world
// resource in its module's OnWorldCreated; the engine calls OnWorldStarted once every module has had
// its turn with the new world. In future multiplayer this becomes server-only, like UE's GameMode.
public abstract class GameRules
{
    // Every module has installed its systems and resources; the world is ready to be populated.
    public virtual void OnWorldStarted(World world) { }

    // Where and how the local player enters the world.
    public virtual Entity SpawnPlayer(World world) => default;

    // Something died (F20 calls this once combat exists).
    public virtual void OnEntityDied(World world, Entity victim, Entity killer) { }

    // A save has just been loaded into this world (09).
    public virtual void OnLoaded(World world) { }
}

// What a world gets when a game doesn't supply rules of its own.
public sealed class DefaultGameRules : GameRules { }
