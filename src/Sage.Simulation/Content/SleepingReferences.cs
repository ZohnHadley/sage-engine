#nullable enable
using System.Collections.Generic;

namespace Sage.Simulation;

// References to entities that went to sleep (issue 4m-4; the 4g-1 limit "a live entity's reference to
// one that went to sleep does not resolve while it sleeps").
//
// A component that holds an `Entity` (an effect's source, an AI's target) holds a handle: a slot and a
// revision in this run. When the entity it names goes dormant with its cell (Cells.cs) or is taken by a
// handoff (CellContent.Release), that handle dies, and when the entity comes back it has a new one. So
// the reference was lost twice over: in play it never found the woken entity again, and a save wrote it
// as null, so not even a load could.
//
// - **A tombstoned handle.** As an entity goes to sleep its handle is noted against its persistent id.
//   `World.Resolve(Entity)` turns such a handle into the entity with that id once it is back (and the null
//   entity while it sleeps); a live handle is itself.
// - **A save keeps the reference**: the save dialect's entity converter writes a tombstoned handle as the
//   id it stands for, and a load (or a cell waking) that reads an id whose entity is asleep in a dormant
//   cell mints a tombstoned handle for it, so the reference resolves as soon as the entity wakes.
//
// A world resource, cleared by a load (the handles it held belonged to the game being left).
internal sealed class SleepingHandles
{
    private readonly Dictionary<Entity, PersistentId> _ids = new();

    public int Count => _ids.Count;

    public void Note(Entity handle, PersistentId id)
    {
        if (!id.IsEmpty) _ids[handle] = id;
    }

    public bool TryGet(Entity handle, out PersistentId id) => _ids.TryGetValue(handle, out id);

    public void Clear() => _ids.Clear();

    // An entity, and the children its prefab placed, is going to sleep: its handles stand for its ids.
    public static void Asleep(World world, Entity entity)
    {
        if (!world.IsAlive(entity)) return;
        var handles = world.Resources.GetOrAdd(() => new SleepingHandles());
        Note(handles, entity);

        static void Note(SleepingHandles handles, Entity entity)
        {
            if (entity.TryGetComponent<Persistent>(out var persistent)) handles.Note(entity, persistent.Id);
            if (entity.ChildCount == 0) return;
            foreach (var child in entity.ChildEntities)
                if (child.Tags.Has<FromParentPrefab>()) Note(handles, child);
        }
    }

    // A handle that stands for `id`, an entity asleep in a dormant cell, for a reference read while it
    // sleeps: one made and destroyed at once, so it is dead now and resolves once the entity is back.
    public static Entity Mint(World world, PersistentId id)
    {
        var handle = world.Create($"asleep {id}");
        world.Destroy(handle);
        world.Resources.GetOrAdd(() => new SleepingHandles()).Note(handle, id);
        return handle;
    }

    public static bool TryGetId(World world, Entity handle, out PersistentId id)
    {
        id = default;
        return world.Resources.TryGet<SleepingHandles>(out var handles) && handles != null && handles.TryGet(handle, out id);
    }
}
