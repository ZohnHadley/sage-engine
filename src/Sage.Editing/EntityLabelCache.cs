#nullable enable
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Editing;

// The outliner's row labels, made once (TODO #41, issue #374). A label is `World.Describe(entity)` plus
// the "##id" ImGui keeps for identity; building one per entity per frame cost about 4 KB a frame once
// terrain chunks became entities. A label is rebuilt only when the entity's name changes (or its id is
// reused), so a frame over unchanged entities allocates nothing.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class EntityLabelCache
{
    private readonly Dictionary<int, (string? Name, string Label)> _labels = new();

    public int Count => _labels.Count;

    // The row label for `entity`: "name (id)##id", or "entity id##id" when it has no name.
    public string Label(Entity entity)
    {
        int id = entity.Id;
        string? name = entity.Name;
        if (_labels.TryGetValue(id, out var cached) && ReferenceEquals(cached.Name, name)) return cached.Label;
        string label = name is null ? $"entity {id}##{id}" : $"{name} ({id})##{id}";
        _labels[id] = (name, label);
        return label;
    }

    // Drops labels of entities no longer in `live` once the cache is well past it (a label is a few
    // dozen bytes, so this is housekeeping, not per-frame work).
    public void Prune(IReadOnlyCollection<Entity> live)
    {
        if (_labels.Count <= live.Count * 2 + 64) return;
        var keep = new HashSet<int>();
        foreach (var e in live) keep.Add(e.Id);
        var dead = new List<int>();
        foreach (var id in _labels.Keys) if (!keep.Contains(id)) dead.Add(id);
        foreach (var id in dead) _labels.Remove(id);
    }
}
