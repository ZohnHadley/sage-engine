using System;using System.Collections.Generic;
namespace sage_engine;

class Entity{
    private EntityContext context = EntityContext.getInstance();

    public long id {get; set; } // TODO : make so entity has random generated id and adds itself to context 
    private String name {get; set; }
    private List<Component> components = new List<Component>();

    public Entity() {
        this.name = "Untitled_Entity";
        addComponent(new TransformComponent()); // add default transform component to all entities
     } 

    public Entity(String name) {
        this.name = name;
        addComponent(new TransformComponent()); // add default transform component to all entities
    }

    public void addComponent(Component component) { 
        if (component == null) {
            throw new ArgumentNullException("Component cannot be null.");
        }

        if (components.Contains(component)) {
            throw new ArgumentException("Component already exists in entity.");
        }

        if (!context.getGroups().ContainsKey(component.GetType().Name))
        {
            context.getGroups()[component.GetType().Name] = new List<Entity>();
        }

        context.getGroups()[component.GetType().Name].Add(this);
        components.Add(component);
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
}