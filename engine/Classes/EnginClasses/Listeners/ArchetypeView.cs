using System;
using System.Collections.Generic;

namespace sage_engine;

// A live, incrementally-maintained set of entities that own ALL of a given set of
// component types. Created via EntityContextListener.Track / EntityContextSystem.Track.
// Membership and the OnEnter/OnExit edges stay current as components and entities
// change, so consumers never have to rescan the world each frame.
internal sealed class ArchetypeView
{
    private readonly string[] _requiredTypes;
    private readonly Dictionary<long, Entity> _matching = new Dictionary<long, Entity>();

    // Live view of the matching set (no per-call allocation). Treat as read-only and do
    // not mutate the entity context while iterating it.
    public IReadOnlyCollection<Entity> Entities => _matching.Values;

    public event Action<Entity> OnEnter;   // entity started matching ALL required types
    public event Action<Entity> OnExit;    // entity stopped matching (lost a component or was deleted)

    internal ArchetypeView(string[] requiredTypes)
    {
        _requiredTypes = requiredTypes ?? Array.Empty<string>();
    }

    private bool Matches(Entity entity)
    {
        foreach (string type in _requiredTypes)
        {
            if (!entity.hasComponent(type))
            {
                return false;
            }
        }
        return true;
    }

    // Re-check an entity whose component set changed; fire an edge only on a transition.
    internal void Reevaluate(Entity entity)
    {
        bool wasIn = _matching.ContainsKey(entity.getId());
        bool isIn = Matches(entity);

        if (isIn && !wasIn)
        {
            _matching.Add(entity.getId(), entity);
            OnEnter?.Invoke(entity);
        }
        else if (!isIn && wasIn)
        {
            _matching.Remove(entity.getId());
            OnExit?.Invoke(entity);
        }
    }

    // Force-remove a deleted entity. Used on entity removal, where the components are
    // still attached to the (now defunct) entity object so Matches would still pass.
    internal void Remove(Entity entity)
    {
        if (_matching.Remove(entity.getId()))
        {
            OnExit?.Invoke(entity);
        }
    }
}
