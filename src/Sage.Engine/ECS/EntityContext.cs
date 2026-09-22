using System;
using System.Collections.Generic;
using System.Linq; 

namespace sage_engine;
internal class EntityContext {
    private static EntityContext instance = null;
    private Dictionary<long, Entity> _entities =  new Dictionary<long, Entity>();
    public Dictionary<long, Entity> EntitiesDict
    {
        get
        {
            return _entities;
        }
        set
        {
            _entities = value;
        }
    }
    //organises entities into groups based on components they have
    private Dictionary<String, Dictionary<long, Entity>> _entityGroups = new Dictionary<String, Dictionary<long, Entity>>();
    private Dictionary<String, Dictionary<long, Entity>> EntityGroupsDict
    {
        get
        {
            return _entityGroups;
        }
    }
    private Random _randomNumberGenerator = new Random();

    // Push/observer surface. Raised at the single mutation points below so subscribers
    // never have to poll or diff. Ordering guarantees: OnEntityAdded fires before any
    // OnComponentAdded for that entity, and OnEntityRemoved fires after the matching
    // OnComponentRemoved events for its components.
    public event Action<Entity> OnEntityAdded;
    public event Action<Entity> OnEntityRemoved;
    public event Action<Entity, IComponent> OnComponentAdded;
    public event Action<Entity, IComponent> OnComponentRemoved;

    private EntityContext() { }

    public static EntityContext getInstance() {
        if (instance == null) {
            instance = new EntityContext();
        }
        return instance;
    }

    public Entity createEntity(String name = "Untitled_Entity")
    {
        Entity entity = new Entity();
        entity.Name = name;

        long newId;
        do {
            newId = _randomNumberGenerator.NextInt64();
        } while (_entities.ContainsKey(newId));
        entity.setId(newId);

        _entities.Add(entity.getId(), entity);
        // Announce the entity exists before attaching its components, so subscribers
        // never see a component event for an entity they haven't been told about.
        OnEntityAdded?.Invoke(entity);
        addComponentFor(entity, new ComponentTransform());
        return entity;
    }
    
    public void removeEntity(Entity entity) {
        if (!_entities.ContainsKey(entity.getId())) {
            throw new KeyNotFoundException("Entity with ID " + entity.getId()+ " not found.");
        }

        _entities.Remove(entity.getId());
        foreach (var (group_name, group_entities) in _entityGroups)
        {
            group_entities.Remove(entity.getId());
        }

        // Announce each component's removal before the entity itself, keeping the event
        // stream symmetric (every OnComponentAdded gets a matching OnComponentRemoved).
        // Snapshot the values: a handler must not mutate the collection mid-iteration.
        foreach (IComponent component in entity.Components.Values.ToList())
        {
            OnComponentRemoved?.Invoke(entity, component);
        }
        OnEntityRemoved?.Invoke(entity);
    }

    public Entity getEntity(long id) {
        if (_entities.ContainsKey(id)) {
            return _entities[id];
        } else {
            return null;
        }
    }

    // gets entities based on groups they belong to
    public List<Entity> getAllEntitiesFromListOfGroups(List<String> groups) {
        HashSet<Entity> resultSet = new HashSet<Entity>();
        foreach (var group in groups) {
            Log.Trace(LogCat.World, $"group: {group}");
            if (_entityGroups.ContainsKey(group)) {
                resultSet.UnionWith(_entityGroups[group].Values.ToList());
            }
        }
        return new List<Entity>(resultSet);
    } 

    //read-only view of groups — addComponentFor / removeComponentFor are the only writers
    public IReadOnlyDictionary<string, IReadOnlyDictionary<long, Entity>> getGroups() {
        return _entityGroups.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyDictionary<long, Entity>)kvp.Value
        );
    }

    public void addComponentFor(Entity entity, IComponent component)
    {
        if (component == null) {
            throw new ArgumentNullException(nameof(component));
        }

        string groupName = component.GetType().Name;
        if (!_entityGroups.ContainsKey(groupName))
        {
            _entityGroups[groupName] = new Dictionary<long, Entity>();
        }

        entity.addComponent(component.GetType(), component);
        _entityGroups[groupName].Add(entity.getId(), entity);
        OnComponentAdded?.Invoke(entity, component);
    }
 
    public void removeComponentFor(Entity entity, IComponent component)
    {
        if (component == null) {
            throw new ArgumentNullException(nameof(component));
        }

        string groupName = component.GetType().Name;
        if (_entityGroups.ContainsKey(groupName))
        {
            _entityGroups[groupName].Remove(entity.getId());
        }
        entity.removeComponent(component.GetType());
        OnComponentRemoved?.Invoke(entity, component);
    }
}