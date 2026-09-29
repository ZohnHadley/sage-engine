#nullable enable
using F = Friflo.Engine.ECS;

namespace Sage.Simulation;

// Deferred structural changes (docs/design/03 §3.5, issue #25): what a system records while it iterates
// a query, where changing the world directly would throw. The world plays them back, in the order they
// were recorded, at the end of the current phase (and on World.FlushCommands()).
//
//     foreach (var (healths, entities) in _alive.Chunks) ...
//         if (h[n].Value <= 0) ctx.Commands.Destroy(entities.EntityAt(n));
//
// One of three kinds of "command" in Sage, each with its own name: an EntityCommands records ECS
// changes; a ConsoleCommand is something typed at the console (`ent_list`); a PlayerCommand is one
// tick of a player's input. Deferring a system's *own* work (not a structural change) is `Deferred<T>`.
public sealed class EntityCommands
{
    private readonly F.CommandBuffer _buffer;

    internal EntityCommands(F.CommandBuffer buffer)
    {
        _buffer = buffer;
        _buffer.ReuseBuffer = true;
    }

    // How many changes are waiting.
    public int Count => _buffer.EntityCommandsCount + _buffer.ComponentCommandsCount +
                        _buffer.TagCommandsCount + _buffer.ChildCommandsCount;

    // Adds the component, or replaces its value if the entity has one by then.
    public void Add<T>(Entity entity, in T component) where T : struct, IComponent =>
        _buffer.AddComponent(entity.Id, component);

    public void Remove<T>(Entity entity) where T : struct, IComponent => _buffer.RemoveComponent<T>(entity.Id);

    public void AddTag<T>(Entity entity) where T : struct, ITag => _buffer.AddTag<T>(entity.Id);
    public void RemoveTag<T>(Entity entity) where T : struct, ITag => _buffer.RemoveTag<T>(entity.Id);

    public void SetParent(Entity child, Entity parent) => _buffer.AddChild(parent.Id, child.Id);

    // Destroys the entity (World.EntityDestroyed and its ComponentRemoved are raised at playback).
    public void Destroy(Entity entity) => _buffer.DeleteEntity(entity.Id);

    internal bool HasPending => Count > 0;

    internal void Playback() => _buffer.Playback();
}
