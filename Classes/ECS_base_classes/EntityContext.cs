using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Xna.Framework.Graphics;
using sage_engine;

class EntityContext {
    private static EntityContext instance = null;
    private static int contextEntityCount = 0;
    private Dictionary<long, Entity> entities =  new Dictionary<long, Entity>();
    //organises entities into groups based on components they have
    private Dictionary<String, List<Entity>> entityGroups = new Dictionary<String, List<Entity>>();

    private EntityContext() { }

    public static EntityContext getInstance() {
        if (instance == null) {
            instance = new EntityContext();
        }
        return instance;
    }

    public Entity createEntity() {
        Entity entity = new Entity();
        entity.setId(contextEntityCount);
        entity.addComponent(new TransformComponent());
        entities.Add(entity.getId(), entity);
        contextEntityCount++;
        return entity;
    }
    
    public void removeEntity(Entity entity) {
        if (entities.ContainsKey(entity.getId())) {
            entities.Remove(entity.getId());
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
            return entityGroups[group];
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
                resultSet.UnionWith(entityGroups[group]);
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

    //get all groups
    public Dictionary<String, List<Entity>> getGroups() {
        return entityGroups;
    }
}