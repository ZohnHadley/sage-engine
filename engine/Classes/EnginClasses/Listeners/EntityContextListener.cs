using System;
using System.Collections.Generic;

namespace sage_engine;

// Enrichment layer over EntityContext's raw mutation events (the KeyboardListener of the
// entity world). Subscribes once to the context, re-broadcasts the raw events, and derives
// higher-level archetype views so systems can hold live entity lists instead of rescanning.
internal class EntityContextListener
{
    // Re-broadcast of the raw EntityContext events. Consumers subscribe here
    // rather than to EntityContext directly.
    public event Action<Entity> OnEntityAdded;
    public event Action<Entity> OnEntityRemoved;
    public event Action<Entity, IComponent> OnComponentAdded;
    public event Action<Entity, IComponent> OnComponentRemoved;

    // Live archetype views maintained incrementally as components/entities change.
    private readonly List<ArchetypeView> _views = new List<ArchetypeView>();

    private static EntityContextListener instance;

    public static EntityContextListener getInstance()
    {
        if (instance == null)
        {
            instance = new EntityContextListener();
        }
        return instance;
    }

    private EntityContextListener()
    {
        EntityContext context = EntityContext.getInstance();
        context.OnEntityAdded += HandleEntityAdded;
        context.OnEntityRemoved += HandleEntityRemoved;
        context.OnComponentAdded += HandleComponentAdded;
        context.OnComponentRemoved += HandleComponentRemoved;
    }

    // Register interest in entities that own ALL of the given component types. Returns a
    // live view whose Entities collection and OnEnter/OnExit edges stay current without
    // any per-frame scanning. Seeds against entities that already exist.
    public ArchetypeView Track(params string[] componentTypeNames)
    {
        ArchetypeView view = new ArchetypeView(componentTypeNames);
        foreach (Entity entity in EntityContext.getInstance().EntitiesDict.Values)
        {
            view.Reevaluate(entity);
        }
        _views.Add(view);
        return view;
    }

    // Each handler updates the archetype views FIRST, then re-broadcasts the raw event, so
    // that any consumer reacting to the re-broadcast sees views already consistent.

    private void HandleEntityAdded(Entity entity)
    {
        foreach (ArchetypeView view in _views)
        {
            view.Reevaluate(entity);
        }
        OnEntityAdded?.Invoke(entity);
    }

    private void HandleEntityRemoved(Entity entity)
    {
        foreach (ArchetypeView view in _views)
        {
            view.Remove(entity);
        }
        OnEntityRemoved?.Invoke(entity);
    }

    private void HandleComponentAdded(Entity entity, IComponent component)
    {
        foreach (ArchetypeView view in _views)
        {
            view.Reevaluate(entity);
        }
        OnComponentAdded?.Invoke(entity, component);
    }

    private void HandleComponentRemoved(Entity entity, IComponent component)
    {
        foreach (ArchetypeView view in _views)
        {
            view.Reevaluate(entity);
        }
        OnComponentRemoved?.Invoke(entity, component);
    }
}
