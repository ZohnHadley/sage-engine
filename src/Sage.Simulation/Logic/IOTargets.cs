#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Simulation;

// Group targets (issue #276, 04 §3.4): a wire whose target starts with `@` reaches every entity a
// selector picks, not one name.
//
//   @lamps              every entity in the group `lamps` (its `sage:io_group`, or a map's `group` key)
//   @class:torch        every entity spawned from the prefab `torch` (any namespace), `@class:ns:torch` one
//   @tag:ns:id          every entity with that tag
//
// A selector is resolved when the input *arrives*, not when the output fires, the way a name is
// late-bound: a lamp spawned during a two-second delay is lit with the rest. Each member is one
// delivery and counts toward `io_maxdispatch`.

// The groups an entity is in, for `@group` targets: names separated by spaces or commas. A prefab writes
// it as a component (`"sage:io_group": { "names": "lamps hall" }`), a map entity as its `group` key.
[Component("sage:io_group")]
public struct IOGroup : IComponent
{
    [Property(Tooltip = "The groups this entity is in, separated by spaces or commas; a wire to @name reaches every member")]
    public string Names;

    // Whether `group` is one of the names. Allocation-free: the names are walked as spans.
    public readonly bool Has(ReadOnlySpan<char> group) => IOTargets.ListHas(Names, group);
}

public static class IOTargets
{
    internal enum Kind { Name, Special, Group, Class, Tag }

    // Whether a wire's target is a selector (`@…`) rather than a name.
    public static bool IsSelector(string? target) => target is { Length: > 1 } && target[0] == '@';

    internal static Kind KindOf(string target, out ReadOnlySpan<char> value)
    {
        value = target.AsSpan();
        if (target.Length == 0) return Kind.Name;
        if (target[0] == '!') return Kind.Special;
        if (target[0] != '@') return Kind.Name;
        var rest = target.AsSpan(1);
        if (rest.StartsWith("class:", StringComparison.OrdinalIgnoreCase)) { value = rest.Slice(6); return Kind.Class; }
        if (rest.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)) { value = rest.Slice(4); return Kind.Tag; }
        value = rest;
        return Kind.Group;
    }

    // Why a selector cannot work (an empty group, a tag nobody declares), or null. A plain name or
    // `!self` is always null here: a name that is not in the level yet is late-bound, not wrong.
    public static string? Problem(Engine engine, string target)
    {
        if (!IsSelector(target)) return target == "@" ? "'@' needs a group name after it (@lamps, @class:torch, @tag:ns:id)" : null;
        var kind = KindOf(target, out var value);
        if (value.Trim().Length == 0)
            return $"'{target}' names no {(kind == Kind.Class ? "prefab" : kind == Kind.Tag ? "tag" : "group")}";
        if (kind == Kind.Tag && !engine.Components.TryResolveTag(value.ToString(), ComponentSchema.EngineNamespace, out _, out var why))
            return $"'{target}': {why}";
        return null;
    }

    // Every entity of `world` the target means now, added to `into`: a selector's members, or the one
    // entity of that name. For tools (the editor's link view, `io_list`); the dispatch has its own
    // allocation-free copy of this on the world's EntityIO.
    public static void Members(World world, string target, List<Entity> into)
    {
        if (IsSelector(target))
        {
            if (world.Resources.TryGet<EntityIO>(out var io) && io != null) io.Collect(world, target, into);
            else new IOSelectorCache().Collect(world, target, into);
            return;
        }
        var named = world.FindByName(target);
        if (!named.IsNull) into.Add(named);
    }

    // `names` (space- or comma-separated) has `item`, ignoring case.
    internal static bool ListHas(string? names, ReadOnlySpan<char> item)
    {
        if (string.IsNullOrEmpty(names) || item.Length == 0) return false;
        var rest = names.AsSpan();
        while (rest.Length > 0)
        {
            int cut = rest.IndexOfAny(' ', ',');
            var token = cut < 0 ? rest : rest.Slice(0, cut);
            if (token.Length > 0 && token.Equals(item, StringComparison.OrdinalIgnoreCase)) return true;
            if (cut < 0) break;
            rest = rest.Slice(cut + 1);
        }
        return false;
    }

    // A prefab id against `@class:` text: `torch` matches any namespace's torch, `ns:torch` only that one.
    internal static bool ClassIs(RecordId prefab, ReadOnlySpan<char> text)
    {
        if (prefab.IsEmpty) return false;
        int colon = text.IndexOf(':');
        if (colon < 0) return text.Equals(prefab.Name, StringComparison.OrdinalIgnoreCase);
        return text.Slice(0, colon).Equals(prefab.Namespace, StringComparison.OrdinalIgnoreCase)
            && text.Slice(colon + 1).Equals(prefab.Name, StringComparison.OrdinalIgnoreCase);
    }
}

// The queries a selector walks, made once per world (making a query allocates; walking one does not),
// and each tag selector's tag set, resolved the first time it is used.
internal sealed class IOSelectorCache
{
    private World? _world;
    private Query<IOGroup> _groups;
    private Query<FromPrefab> _prefabs;
    private Query<Transform> _all;
    private readonly Dictionary<string, Tags?> _tags = new(StringComparer.OrdinalIgnoreCase);

    public void Collect(World world, string target, List<Entity> into)
    {
        if (!ReferenceEquals(world, _world))
        {
            _world = world;
            _groups = world.Query<IOGroup>();
            _prefabs = world.Query<FromPrefab>();
            _all = world.Query<Transform>();
            _tags.Clear();
        }

        switch (IOTargets.KindOf(target, out var value))
        {
            case IOTargets.Kind.Group:
                foreach (var (groups, entities) in _groups.Chunks)
                {
                    var span = groups.Span;
                    for (int i = 0; i < span.Length; i++)
                        if (span[i].Has(value)) into.Add(entities.EntityAt(i));
                }
                break;

            case IOTargets.Kind.Class:
                foreach (var (prefabs, entities) in _prefabs.Chunks)
                {
                    var span = prefabs.Span;
                    for (int i = 0; i < span.Length; i++)
                        if (IOTargets.ClassIs(span[i].Prefab, value)) into.Add(entities.EntityAt(i));
                }
                break;

            case IOTargets.Kind.Tag:
                if (!_tags.TryGetValue(target, out var tags))
                {
                    tags = world.Engine?.Components.TryTagSet(value.ToString(), out var set) == true ? set : null;
                    _tags[target] = tags;
                }
                if (tags is not { } wanted) break;
                foreach (var entity in _all.Entities)
                    if (entity.Tags.HasAll(wanted)) into.Add(entity);
                break;
        }
    }
}

// What became of one input (EntityIO's history, issue #276).
public enum IOOutcome
{
    Delivered,
    NoTarget,           // nothing by that name, or a selector with no members, when it arrived
    NoSuchInput,        // an input nothing registers (an `ent_fire` typo)
    NobodyTookIt,       // the target has no component that takes it
    Failed,             // its handler threw (logged with the wire; the dispatch went on, #402)
}

// One input as it was delivered: the last `EntityIO.HistoryCapacity` of these are kept, for `io_history`
// and the editor's link view. `Output` is empty for an input sent directly (`ent_fire`, a `fire` action).
public readonly record struct IORecord(long Tick, double Time, Entity Caller, string Output, Entity Target,
                                       string TargetName, string Input, string Parameter, IOOutcome Outcome);
