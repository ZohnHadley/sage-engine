using System;
using Microsoft.Xna.Framework;

namespace sage_engine;

// Central facade over the EntityContextListener, mirroring InputSystem over its input
// listeners. Owns the single shared listener and forwards its events so the rest of the
// engine subscribes here instead of touching EntityContext directly.
internal class EntityContextSystem : IEnginSystem
{
    private static EntityContextSystem instance;
    private EntityContextListener _listener;

    // Entity/component change events, forwarded from the single shared listener.
    public event Action<Entity> OnEntityAdded
    {
        add { _listener.OnEntityAdded += value; }
        remove { _listener.OnEntityAdded -= value; }
    }
    public event Action<Entity> OnEntityRemoved
    {
        add { _listener.OnEntityRemoved += value; }
        remove { _listener.OnEntityRemoved -= value; }
    }
    public event Action<Entity, IComponent> OnComponentAdded
    {
        add { _listener.OnComponentAdded += value; }
        remove { _listener.OnComponentAdded -= value; }
    }
    public event Action<Entity, IComponent> OnComponentRemoved
    {
        add { _listener.OnComponentRemoved += value; }
        remove { _listener.OnComponentRemoved -= value; }
    }

    private EntityContextSystem()
    {
        _listener = new EntityContextListener();
    }

    public static EntityContextSystem getInstance()
    {
        if (instance == null)
        {
            instance = new EntityContextSystem();
        }
        return instance;
    }

    // Register interest in entities owning ALL the given component types; returns a live
    // view that stays current without per-frame scanning.
    public ArchetypeView Track(params string[] componentTypeNames) => _listener.Track(componentTypeNames);

    // Dispatch is synchronous (events fire directly from EntityContext mutations), so there
    // is no per-frame work today. Kept to satisfy IEnginSystem and as the seam for a
    // deferred/queued dispatch upgrade later.
    public void update(GameTime deltaTime) { }
}
