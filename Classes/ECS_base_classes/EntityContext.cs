using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Xna.Framework.Graphics;
using sage_engine;

class EntityContext {
    private static EntityContext instance = null;
    private int contextEntityCount = 0;
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
        entity.id = contextEntityCount;
        entities.Add(entity.id, entity);
 
        contextEntityCount++;
        return entity;
    }
    
    public void removeEntity(Entity entity) {
        if (entities.ContainsKey(entity.id)) {
            entities.Remove(entity.id);
        } else {
            throw new KeyNotFoundException("Entity with ID " + entity.id + " not found.");
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

    public List<Entity> getAllEntities(String group) {
        if (entityGroups.ContainsKey(group)) {
            return entityGroups[group];
        } else {
            return new List<Entity>();
        }
    }

    public List<Entity> getAllEntities(List<String> groups) {
        List<Entity> result = new List<Entity>();
        foreach (var group in groups) {
            if (entityGroups.ContainsKey(group)) {
                result.AddRange(entityGroups[group]);
            }
        }
        return result;
    } 

    //get all groups
    public Dictionary<String, List<Entity>> getGroups() {
        return entityGroups;
    }
}