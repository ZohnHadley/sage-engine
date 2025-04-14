using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
namespace sage_engine;

class Entity{
    private EntityContext context = EntityContext.getInstance();

    private int id; // TODO : make so entity has random generated id and adds itself to context 
    private String name;
    private List<Component> components = new List<Component>();
    private List<Tag> tags = new List<Tag>();
    private ComponentTransform transformComponent;

    public Entity() {
        this.name = "Untitled_Entity";
    }

    public Entity(String name) {
        this.name = name;
    }

    public int getId() {
        return id;
    }
    
    public String getName() {
        return name;
    }

    public ComponentTransform transform () {
        if (transformComponent == null) {
            transformComponent = getComponent<ComponentTransform>();
        }  
        return transformComponent;
    }

    public void setId (int id) {
        this.id = id;
    }

    public void setName (String name) {
        this.name = name;
    }
 
    public void setTransform (ComponentTransform transformComponent) {
        if (transformComponent == null) {
            throw new ArgumentNullException("TransformComponent cannot be null.");
        }
        this.transformComponent = transformComponent;
    } 

    public Vector3 getPosition () {
        return transformComponent.position;
    }

    public Quaternion getRotation () {
        return transformComponent.rotation;
    }

    public Vector3 getScale () {
        return transformComponent.scale;
    }


    public void addComponent(Component component) { 
        if (component == null) {
            //throw new ArgumentNullException("Component cannot be null.");
            Console.WriteLine("Component cannot be null.");
            return;
        }

        if (components.Contains(component)) {
            //throw new ArgumentException("Component already exists in entity.");
            Console.WriteLine("Component already exists in entity.");
            return;
        }

        if (!context.getGroups().ContainsKey(component.GetType().Name))
        {
            context.getGroups()[component.GetType().Name] = new List<Entity>();
        }

        components.Add(component);
        context.getGroups()[component.GetType().Name].Add(this);
    }

    public void removeComponent(Component component) {
        if (component == null) {
            throw new ArgumentNullException("Component cannot be null.");
        }

        if (!components.Contains(component)) {
            throw new ArgumentException("Component not found in entity.");
        }

        if (components.Contains(component)) {
            components.Remove(component);
            context.getGroups()[component.GetType().Name].Remove(this);
        } else {
            throw new ArgumentException("Component not found in entity.");
        }
    } 

    public List<Component> getComponents() {
        return components;
    } 

    public T getComponent<T>() where T : Component {
        foreach (Component component in components) {
            if (component is T) {
                return (T)component;
            }
        }
        throw new ArgumentException("Component of type " + typeof(T).Name + " not found in entity.");
    }

    internal bool hasComponent(string componentType)
    {
        foreach (Component component in components) {
            if (component.GetType().Name == componentType) {
                return true;
            }
        }
        return false;
    }

    public void addTag(Tag tag) {
        if (tag == null) {
            throw new ArgumentNullException("Tag cannot be null.");
        }

        if (tags.Contains(tag)) {
            throw new ArgumentException("Tag already exists in entity.");
        }

        tags.Add(tag);
    }

    public void removeTag(Tag tag) {
        if (tag == null) {
            throw new ArgumentNullException("Tag cannot be null.");
        }

        if (!tags.Contains(tag)) {
            throw new ArgumentException("Tag not found in entity.");
        }

        tags.Remove(tag);
    }

    public List<Tag> getTags() {
        return tags;
    }

    public bool hasTag(Tag tag) {
        return tags.Contains(tag);
    }
}