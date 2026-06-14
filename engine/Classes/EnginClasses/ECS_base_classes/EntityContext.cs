using System;
using System.Collections.Generic;
using System.Linq; 

namespace sage_engine;
internal class EntityContext {
    private static EntityContext instance = null;
    private Dictionary<long, Entity> entities =  new Dictionary<long, Entity>();
    //organises entities into groups based on components they have
    private Dictionary<String, Dictionary<long, Entity>> entityGroups = new Dictionary<String, Dictionary<long, Entity>>();
    private Random _randomNumberGenerator = new Random();
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
        } while (entities.ContainsKey(newId));
        entity.setId(newId);

        entities.Add(entity.getId(), entity);
        addComponentFor(entity, new ComponentTransform());
        return entity;
    }
    
    public void removeEntity(Entity entity) {
        if (entities.ContainsKey(entity.getId())) {
            entities.Remove(entity.getId());
            foreach (var (group_name, group_entities) in entityGroups)
            {
                group_entities.Remove(entity.getId());
            }
        } else {
            throw new KeyNotFoundException("Entity with ID " + entity.getId()+ " not found.");
        }
    }

    public Entity getEntity(long id) {
        if (entities.ContainsKey(id)) {
            return entities[id];
        } else {
            return null;
        }
    }

    public List<Entity> getAllEntities() {
        List<Entity> result = new List<Entity>();
        foreach (var entity in entities.Values) {
            result.Add(entity);
        }
        return result;
    } 

    public List<Entity> getAllEntitiesFromGroup(String group) {
        if (entityGroups.ContainsKey(group)) {

            return entityGroups[group].Values.ToList();
        } else {
            return new List<Entity>();
        }
    }

    // gets entities based on groups they belong to
    public List<Entity> getAllEntitiesFromListOfGroups(List<String> groups) {
        HashSet<Entity> resultSet = new HashSet<Entity>();
        foreach (var group in groups) {
            Console.WriteLine("group: " + group);
            if (entityGroups.ContainsKey(group)) {
                resultSet.UnionWith(entityGroups[group].Values.ToList());
            }
        }
        return new List<Entity>(resultSet);
    } 

    public List<Entity> getAllEntitiesWithListOfComponents(List<String> ComponentTypes) {
        List<Entity> result = new List<Entity>();

        foreach (Entity entity in entities.Values) {
            bool hasAllComponents = true;
            foreach (String componentType in ComponentTypes) {
                if (!entity.hasComponent(componentType)) {
                    hasAllComponents = false;
                    //when last component is not found (has all == false), break out of the loop and return a empty list
                    break;
                }
            }

            if (hasAllComponents) {
                result.Add(entity);
            }
        }

        return result;
    }  

    //read-only view of groups — addComponentFor / removeComponentFor are the only writers
    public IReadOnlyDictionary<string, IReadOnlyDictionary<long, Entity>> getGroups() {
        return entityGroups.ToDictionary(
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
        if (!entityGroups.ContainsKey(groupName))
        {
            entityGroups[groupName] = new Dictionary<long, Entity>();
        }

        entity.addComponent(component.GetType(), component);
        entityGroups[groupName].Add(entity.getId(), entity);
    }
 
    public void removeComponentFor(Entity entity, IComponent component)
    {
        if (component == null) {
            throw new ArgumentNullException(nameof(component));
        }

        string groupName = component.GetType().Name;
        if (entityGroups.ContainsKey(groupName))
        {
            entityGroups[groupName].Remove(entity.getId());
        }
        entity.removeComponent(component.GetType());
    }
}