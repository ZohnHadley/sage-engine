using System;
using System.Collections.Generic;
namespace sage_engine;

internal class Entity{

    private long id;
    private String _name;
    private Dictionary<Type, IComponent> _components = new Dictionary<Type, IComponent>();

    public String Name {get{return _name;} set{_name = value;}}
    public Dictionary<Type, IComponent> Components { get { return _components; } set {_components = value;} }

    public Entity(){}

    public long getId() {
        return id;
    }
    
    public String getName() {
        return _name;
    } 

    public void setId (long id) {
        this.id = id;
    }

    public void setName (String name) {
        this._name = name;
    }

    internal void addComponent(Type type, IComponent component) {
        if (component == null) {
            //throw new ArgumentNullException("Component cannot be null.");
            Log.Error(LogCat.World, $"addComponent: null component for entity {_name} ({id}); ignored");
            return;
        }
        _components.Add(type, component);
    }

    internal void removeComponent(Type type) {
        if (type == null) {
            throw new ArgumentNullException("Type cannot be null.");
        }
        if (!_components.ContainsKey(type)) {
            throw new ArgumentException("Component of type " + type.Name + " not found in entity.");
        }
        _components.Remove(type);
    }

    public Dictionary<Type, IComponent> getComponents() {
        return _components;
    } 

    public T getComponent<T>() where T : IComponent {
        if (_components.TryGetValue(typeof(T), out IComponent component)) {
            return (T)component;
        }
        throw new ArgumentException("Component of type " + typeof(T).Name + " not found in entity.");
    }

    internal bool hasComponent(string componentType)
    {
        foreach (var (component_type , component) in _components) {
            if (component.GetType().Name == componentType) {
                return true;
            }
        }
        return false;
    }

}