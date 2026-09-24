#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Friflo.Engine.ECS;

namespace sage_engine;

// Components and tags by name (docs/design/09 §3.1). Prefabs name a component in JSON, the console
// prints one back, and the editor's inspector (15) will want the same thing. Until the source
// generator exists (09), this is reflection over the ECS schema, which Friflo already builds by
// scanning the loaded assemblies — so a component type is addressable the moment it is declared,
// with nothing to register.
//
// Names are the C# type name, matched case-insensitively: `"Transform"` and `"transform"` both work,
// because record JSON is lower-case by convention and component types are not.
public sealed class ComponentSchema
{
    private readonly Dictionary<string, ComponentType> _components = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TagType> _tags = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _json;
    private bool _built;

    public ComponentSchema(JsonSerializerOptions json) => _json = json;

    // Built on first use, never in the constructor. Friflo builds its schema by scanning the loaded
    // assemblies and then freezes it (03 §3.1), and the host constructs the Engine *before* it loads
    // the game assembly — so reading it early would leave every game-defined component nameless.
    // By the time anything asks (a spawn, a console command) the modules are all in.
    private void Build()
    {
        if (_built) return;
        _built = true;
        EcsSchema.EnsureInitialized();
        var schema = EntityStore.GetEntitySchema();
        // By the lookup dictionaries rather than the Components/Tags spans: index 0 of a span is
        // Friflo's reserved blank entry, and asking a blank for its name throws.
        foreach (var (type, component) in schema.ComponentTypeByType) _components[type.Name] = component;
        foreach (var (name, tag) in schema.TagTypeByName) _tags[name] = tag;
    }

    public IEnumerable<string> ComponentNames { get { Build(); return _components.Keys; } }
    public IEnumerable<string> TagNames { get { Build(); return _tags.Keys; } }

    public bool TryComponent(string name, out ComponentType type)
    {
        Build();
        return _components.TryGetValue(name, out type);
    }

    public bool TryTag(string name, out TagType type)
    {
        Build();
        return _tags.TryGetValue(name, out type);
    }

    // Reads `fields` as that component type and puts it on the entity, replacing any existing one.
    // Returns false and says why on bad JSON: one bad component costs that component, not the spawn.
    public bool Add(Entity entity, in ComponentType type, JsonNode? fields, string where)
    {
        object? value;
        try
        {
            value = fields is null
                ? Activator.CreateInstance(type.Type)
                : JsonSerializer.Deserialize(fields.ToJsonString(), type.Type, _json);
        }
        catch (JsonException ex)
        {
            Log.Error(LogCat.Records, $"{where}: {type.Name}: {ex.Message}");
            return false;
        }

        if (value is null) return false;
        EntityUtils.AddEntityComponentValue(entity, type, value);
        return true;
    }

    // Writes a boxed component back onto an entity — the other half of `Read`, and what an editor's
    // inspector needs: a component is a struct, so editing one means boxing it, changing a field by
    // reflection and putting the box back (15 §3). The generated metadata (09 §3.2) replaces the
    // reflection, not this call.
    public void Write(Entity entity, in ComponentType type, object value)
    {
        if (entity.IsNull || value is null) return;
        EntityUtils.AddEntityComponentValue(entity, type, value);
    }

    // The component as a boxed value, for printing (`ent_dump`). Null when the entity hasn't got one
    // — asking Friflo for a component that isn't there throws, so the archetype is checked first.
    public object? Read(Entity entity, in ComponentType type) =>
        !entity.IsNull && entity.Archetype is { } archetype && archetype.ComponentTypes.Contains(type)
            ? EntityUtils.GetEntityComponent(entity, type)
            : null;

    // Every component on the entity, in schema order, as (name, value).
    public IEnumerable<(string Name, object Value)> ComponentsOf(Entity entity)
    {
        if (entity.IsNull) yield break;
        Build();
        foreach (var (_, type) in _components)
        {
            var value = Read(entity, type);
            if (value != null) yield return (type.Name, value);
        }
    }

    public IEnumerable<string> TagsOf(Entity entity)
    {
        if (entity.IsNull) yield break;
        foreach (var tag in entity.Tags) yield return tag.TagName;
    }
}
