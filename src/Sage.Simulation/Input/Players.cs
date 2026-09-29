#nullable enable
using System;
using Friflo.Engine.ECS;

namespace Sage.Simulation;

public static class Players
{
    // Every cheat that acts on "the player" means the same thing by it (16 §3.1): each entity tagged
    // PlayerControlled, in every world.
    //
    // The list is materialised first because these are console commands and console commands do
    // structural things: `drop` destroys an entity and creates a pickup, which inside a live query
    // throws (03 §3.2). It did, the moment saves gave a reason to drop something.
    public static void ForEachPlayer(this Engine engine, Action<World, Entity> act)
    {
        foreach (var world in engine.Worlds)
            foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList())
                act(world, entity);
    }
}
