#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace sage_engine;

// Per-world singletons (docs/design/03 §3.4): GameRules, the physics space, the origin sector,
// event queues... the equivalent of Overwatch's singleton components. No static singletons.
public sealed class WorldResources
{
    private readonly Dictionary<Type, object> _items = new();
    private readonly List<object> _order = new();   // installation order, for teardown

    // Installs a resource the world doesn't have yet. A second install of the same type is an error:
    // it used to replace the first without a word and never dispose it (issue #13), so two modules both
    // installing one resource was a bug nobody heard about. Swapping one on purpose is Replace.
    public void Add<T>(T resource) where T : class
    {
        if (_items.TryGetValue(typeof(T), out var existing))
            throw new InvalidOperationException($"World resource {typeof(T).Name} is already installed ({existing.GetType().Name}); " +
                                                "use Replace to swap it on purpose.");
        _items[typeof(T)] = resource;
        _order.Add(resource);
    }

    // Swaps a resource in on purpose (a save restoring the journal, a game's own rules), installing it if
    // there was none. The one it replaces is disposed if it is IDisposable and not still installed under
    // another type.
    public void Replace<T>(T resource) where T : class
    {
        if (_items.TryGetValue(typeof(T), out var previous) && !ReferenceEquals(previous, resource))
        {
            _order.Remove(previous);
            if (previous is IDisposable disposable && !_items.Any(kv => kv.Key != typeof(T) && ReferenceEquals(kv.Value, previous)))
            {
                try { disposable.Dispose(); }
                catch (Exception ex) { Log.Error(LogCat.World, $"Disposing replaced world resource {previous.GetType().Name} failed: {ex.Message}"); }
            }
        }
        else if (previous != null)
        {
            return;   // the same object again: nothing to do
        }
        _items[typeof(T)] = resource;
        _order.Add(resource);
    }

    [Obsolete("Say which you mean: Add (install; an error if one is there) or Replace (swap on purpose, disposing the old).")]
    public void Set<T>(T resource) where T : class => Replace(resource);

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

    public bool Remove<T>() where T : class
    {
        if (!_items.Remove(typeof(T), out var removed)) return false;
        _order.Remove(removed);
        return true;
    }

    // Disposes IDisposable resources in reverse insertion order (docs/design/03 §5). Dictionary order is
    // unspecified and shifts after a Remove, so the order is kept alongside it (review #49): physics
    // spaces and renderer resources are torn down after whatever was installed on top of them.
    internal void DisposeAll()
    {
        var items = new List<object>(_order);
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] is not IDisposable d) continue;
            if (items.IndexOf(items[i]) != i) continue;   // installed under two types: dispose once
            try { d.Dispose(); }
            catch (Exception ex) { Log.Error(LogCat.World, $"Disposing world resource {items[i].GetType().Name} failed: {ex.Message}"); }
        }
        _items.Clear();
        _order.Clear();
    }
}
