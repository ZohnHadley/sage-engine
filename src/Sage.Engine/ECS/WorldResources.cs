#nullable enable
using System;
using System.Collections.Generic;

namespace sage_engine;

// Per-world singletons (docs/design/03 §3.4): GameRules, the physics space, the origin sector,
// event queues... the equivalent of Overwatch's singleton components. No static singletons.
public sealed class WorldResources
{
    private readonly Dictionary<Type, object> _items = new();

    public void Set<T>(T resource) where T : class => _items[typeof(T)] = resource;

    public T Get<T>() where T : class =>
        _items.TryGetValue(typeof(T), out var r)
            ? (T)r
            : throw new InvalidOperationException($"World resource {typeof(T).Name} is not installed.");

    public bool TryGet<T>(out T? resource) where T : class
    {
        if (_items.TryGetValue(typeof(T), out var r)) { resource = (T)r; return true; }
        resource = null;
        return false;
    }

    public bool Remove<T>() where T : class => _items.Remove(typeof(T));

    // Disposes IDisposable resources in reverse insertion order (docs/design/03 §5).
    internal void DisposeAll()
    {
        var items = new List<object>(_items.Values);
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] is IDisposable d)
            {
                try { d.Dispose(); }
                catch (Exception ex) { Log.Error(LogCat.World, $"Disposing world resource {items[i].GetType().Name} failed: {ex.Message}"); }
            }
        }
        _items.Clear();
    }
}
