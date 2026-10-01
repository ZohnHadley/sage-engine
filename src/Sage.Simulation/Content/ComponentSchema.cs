#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using F = Friflo.Engine.ECS;

namespace Sage.Simulation;

// Components and tags by stable id (docs/design/09 §3.1, REDESIGN §3.4, issue #16). Prefabs name a
// component in JSON, saves are keyed by it, the console prints it and the editor's inspector shows it.
//
// **Ids, not type names.** A component is `[Component("sage:ai_state")]`, and that string is its
// identity everywhere data meets code. Until issue #16 it was the bare C# type name, matched ignoring
// case, so two assemblies that each declared `Health` silently overwrote each other and renaming a
// struct broke every prefab and save that named it. Ids are matched exactly (case-sensitively).
//
// **Where the ids come from.** Every assembly built with Sage.Generators carries a generated table
// (IGeneratedComponents) listing its declarations, and an undeclared IComponent or ITag struct there
// is a build error (SAGE0004). An assembly built without the generator (tests, tools) is read by
// reflection over the same attributes. Components are per assembly, not per plugin: a component type
// is in Friflo's schema whether or not its plugin is loaded, so its id is too. Two types claiming one
// id, in any assemblies, is an error the first time the schema is used, naming both.
//
// **Friflo's own components** (EntityName, TreeNode, UniqueEntity, Position, Rotation, Scale3, its own
// Transform, the Disabled tag) share Friflo's schema but are not Sage's. They have no id, so content
// cannot name them and saves do not write them; the entity's name is written on the entity (09 §3.4).
//
// **Types, not Friflo's schema handles** (issue #25): a component or tag is its System.Type here, and
// what Friflo calls it (ComponentType, TagType) stays inside this class.
//
// **Bare names** in a prefab resolve in the prefab's own namespace first, then in `sage`, so a game's
// content can write its own components and the engine's briefly (`"hop"`, `"sprite_renderer"`) and a
// game component deliberately shadows an engine one of the same name. Anything else — another mod's
// component, say — is written in full. A name that resolves nowhere is an error that lists the ids it
// might have meant; one spelled the old way (`"AIState"`) names the id to write instead.
public sealed class ComponentSchema
{
    // Where bare names fall back to after the file's own namespace.
    public const string EngineNamespace = "sage";

    private sealed class Entry
    {
        public required ComponentDeclaration Declaration;
        public F.ComponentType? Component;
        public F.TagType? Tag;
    }

    private readonly Dictionary<string, Entry> _components = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _tags = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, Entry> _byType = new();
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
        EcsSchema.EnsureInitialized();
        var schema = F.EntityStore.GetEntitySchema();

        // By the lookup dictionaries rather than the Components/Tags spans: index 0 of a span is
        // Friflo's reserved blank entry, and asking a blank for its name throws.
        var components = new Dictionary<Type, F.ComponentType>();
        foreach (var (type, component) in schema.ComponentTypeByType) components[type] = component;
        var tags = new Dictionary<Type, F.TagType>();
        foreach (var (type, tag) in schema.TagTypeByType) tags[type] = tag;

        var schemaTypes = components.Keys.Concat(tags.Keys).ToList();
        var declarations = new List<ComponentDeclaration>();
        foreach (var assembly in schemaTypes.Select(t => t.Assembly).Distinct())
            declarations.AddRange(DeclarationsOf(assembly, schemaTypes));
        CheckUnique(declarations);

        foreach (var declaration in declarations)
        {
            var entry = new Entry { Declaration = declaration };
            if (declaration.IsTag)
            {
                // A declaration whose type is not in Friflo's schema cannot be used anyway; leave it out.
                if (!tags.TryGetValue(declaration.Type, out var tag)) continue;
                entry.Tag = tag;
                _tags[declaration.Id] = entry;
            }
            else
            {
                if (!components.TryGetValue(declaration.Type, out var component)) continue;
                entry.Component = component;
                _components[declaration.Id] = entry;
            }
            _byType[declaration.Type] = entry;
        }
        _built = true;
    }

    // An assembly's declarations: its generated table when it has one, otherwise its attributes.
    private static IEnumerable<ComponentDeclaration> DeclarationsOf(Assembly assembly, IEnumerable<Type> schemaTypes)
    {
        if (assembly.GetCustomAttribute<GeneratedComponentsAttribute>() is { } generated)
            return ((IGeneratedComponents)Activator.CreateInstance(generated.Type)!).Declarations;

        var found = new List<ComponentDeclaration>();
        foreach (var type in schemaTypes)
        {
            if (type.Assembly != assembly) continue;
            if (type.GetCustomAttribute<ComponentAttribute>() is { } c)
                found.Add(new ComponentDeclaration(type, c.Id, c.Version, c.FormerNames ?? Array.Empty<string>(), IsTag: false));
            else if (type.GetCustomAttribute<TagAttribute>() is { } t)
                found.Add(new ComponentDeclaration(type, t.Id, 1, t.FormerNames ?? Array.Empty<string>(), IsTag: true));
        }
        return found;
    }

    // One id, one type — across every loaded assembly, which is the check the generator cannot make.
    // Also rejects a malformed id or version from an assembly that was built without the generator.
    internal static void CheckUnique(IEnumerable<ComponentDeclaration> declarations)
    {
        var seen = new Dictionary<(bool, string), ComponentDeclaration>();
        foreach (var d in declarations)
        {
            if (!ComponentDeclaration.IsValidId(d.Id))
                throw new InvalidOperationException(
                    $"{d.Type.FullName}: '{d.Id}' is not a component id (namespace:name, lower-case letters, digits, _ . -)");
            if (d.Version < 1)
                throw new InvalidOperationException($"{d.Type.FullName}: a component's Version starts at 1");
            if (seen.TryGetValue((d.IsTag, d.Id), out var first))
                throw new InvalidOperationException(
                    $"{(d.IsTag ? "Tag" : "Component")} id '{d.Id}' is declared by both {Describe(first.Type)} and {Describe(d.Type)}");
            seen[(d.IsTag, d.Id)] = d;
        }

        static string Describe(Type t) => $"{t.FullName} ({t.Assembly.GetName().Name})";
    }

    public IEnumerable<string> ComponentIds { get { Build(); return _components.Keys; } }
    public IEnumerable<string> TagIds { get { Build(); return _tags.Keys; } }

    // Every declared component and tag, for tools and tests.
    public IEnumerable<ComponentDeclaration> Declarations
    {
        get { Build(); return _byType.Values.Select(e => e.Declaration); }
    }

    // ---- by exact id ----------------------------------------------------------------------------------

    public bool TryComponent(string id, [MaybeNullWhen(false)] out Type type)
    {
        Build();
        type = _components.TryGetValue(id, out var entry) ? entry.Component?.Type : null;
        return type != null;
    }

    public bool TryTag(string id, [MaybeNullWhen(false)] out Type type)
    {
        Build();
        type = _tags.TryGetValue(id, out var entry) ? entry.Tag?.Type : null;
        return type != null;
    }

    // The declaration of a component or tag type; null for one with no id (Friflo's own).
    public ComponentDeclaration? DeclarationOf(Type type)
    {
        Build();
        return _byType.TryGetValue(type, out var entry) ? entry.Declaration : null;
    }

    public string? IdOf(Type type) => DeclarationOf(type)?.Id;

    // ---- by what content writes -------------------------------------------------------------------------

    // A component named in a file whose namespace is `fileNamespace`: an id as written, or a bare name
    // in that namespace and then in `sage` (see the top of this file). `error` says why not, and what
    // it might have meant.
    public bool TryResolveComponent(string name, string fileNamespace, [MaybeNullWhen(false)] out Type type,
                                    [MaybeNullWhen(true)] out string error)
    {
        Build();
        var entry = Resolve(_components, name, fileNamespace, "component", out error);
        type = entry?.Component?.Type;
        return type != null;
    }

    public bool TryResolveTag(string name, string fileNamespace, [MaybeNullWhen(false)] out Type type,
                              [MaybeNullWhen(true)] out string error)
    {
        Build();
        var entry = Resolve(_tags, name, fileNamespace, "tag", out error);
        type = entry?.Tag?.Type;
        return type != null;
    }

    private static Entry? Resolve(Dictionary<string, Entry> table, string name, string fileNamespace, string kind,
                                  out string? error)
    {
        error = null;
        if (name.Contains(':'))
        {
            if (table.TryGetValue(name, out var exact)) return exact;
        }
        else
        {
            if (table.TryGetValue(fileNamespace + ":" + name, out var own)) return own;
            if (table.TryGetValue(EngineNamespace + ":" + name, out var engine)) return engine;
        }

        // Nothing: say what it might have been. The old spelling (a C# type name, with its capitals)
        // first, because every file written before issue #16 is spelled that way. A lower-case name is
        // a bare id that isn't here, not an old type name.
        var legacy = !name.Any(char.IsUpper) ? new List<string>()
            : table.Values.Where(e => string.Equals(e.Declaration.Type.Name, name, StringComparison.OrdinalIgnoreCase))
                          .Select(e => e.Declaration.Id).OrderBy(i => i, StringComparer.Ordinal).ToList();
        if (legacy.Count > 0)
        {
            error = $"'{name}' is a C# type name; {kind}s are named by id since issue #16: write " +
                    string.Join(" or ", legacy.Select(i => $"\"{i}\"")) + " (see `ent_types`)";
            return null;
        }

        string bare = name.Contains(':') ? name.Substring(name.IndexOf(':') + 1) : name;
        var candidates = table.Values.Where(e => string.Equals(e.Declaration.Name, bare, StringComparison.OrdinalIgnoreCase))
                                     .Select(e => e.Declaration.Id).OrderBy(i => i, StringComparer.Ordinal).ToList();
        // Nothing by that name anywhere: the nearest spelling, if one is close (issue #22).
        if (candidates.Count == 0 && Spelling.Nearest(bare, table.Values.Select(e => e.Declaration.Name)) is { } near)
        {
            var close = table.Values.Where(e => e.Declaration.Name == near).Select(e => e.Declaration.Id).OrderBy(i => i, StringComparer.Ordinal).ToList();
            error = $"no {kind} '{name}'; did you mean {string.Join(" or ", close.Select(i => $"\"{i}\""))}? (see `ent_types`)";
            return null;
        }
        string where = fileNamespace == EngineNamespace ? $"'{EngineNamespace}'" : $"'{fileNamespace}' or '{EngineNamespace}'";
        error = candidates.Count > 0
            ? $"no {kind} '{name}' here; did you mean {string.Join(" or ", candidates.Select(i => $"\"{i}\""))}?"
            : name.Contains(':')
                ? $"no {kind} '{name}' (see `ent_types`)"
                : $"no {kind} '{name}' in {where} (see `ent_types`)";
        return null;
    }

    // ---- by what an old save wrote (issue #20) ----------------------------------------------------------

    // A component a save names that is not a current id: one it used to be saved under
    // ([Component(FormerNames = …)]), or — for saves from before issue #16, keyed by C# type name — its
    // type's name, ignoring case as those saves were read. The type-name half goes when format 1 saves
    // stop being read (SaveSystem.FormatVersion).
    //
    // `typeNames` is for format 1 saves only; a current save names components by id or former id.
    internal bool TryFormerComponent(string name, bool typeNames, [MaybeNullWhen(false)] out ComponentDeclaration declaration)
    {
        Build();
        declaration = Former(_components, name, typeNames);
        return declaration != null;
    }

    internal bool TryFormerTag(string name, bool typeNames, [MaybeNullWhen(false)] out ComponentDeclaration declaration)
    {
        Build();
        declaration = Former(_tags, name, typeNames);
        return declaration != null;
    }

    private static ComponentDeclaration? Former(Dictionary<string, Entry> table, string name, bool typeNames)
    {
        foreach (var entry in table.Values)
            if (entry.Declaration.FormerNames.Contains(name, StringComparer.Ordinal)) return entry.Declaration;
        if (!typeNames) return null;
        foreach (var entry in table.Values)
            if (string.Equals(entry.Declaration.Type.Name, name, StringComparison.OrdinalIgnoreCase)) return entry.Declaration;
        return null;
    }

    // ---- values ---------------------------------------------------------------------------------------

    // Reads `fields` as that component type and puts it on the entity, replacing any existing one.
    // Returns false and says why on bad JSON: one bad component costs that component, not the spawn.
    // `reported`: the content load already said what is wrong with these fields, at their line
    // (issue #22), so a failure here is logged at Debug rather than again as an error.
    public bool Add(Entity entity, Type type, JsonNode? fields, string where, bool reported = false)
    {
        object? value;
        try
        {
            value = fields is null
                ? Activator.CreateInstance(type)
                : JsonSerializer.Deserialize(fields.ToJsonString(), type, _json);
        }
        catch (JsonException ex)
        {
            string message = $"{where}: {IdOf(type) ?? type.Name}: {ex.Message}";
            if (reported) Log.Debug(LogCat.Records, message);
            else Log.Error(LogCat.Records, message);
            return false;
        }

        if (value is null) return false;
        Write(entity, type, value);
        return true;
    }

    // Puts a tag, by its type, on the entity (prefabs and saves name tags by id).
    public void AddTag(Entity entity, Type tag)
    {
        if (entity.IsNull) return;
        if (!F.EntityStore.GetEntitySchema().TagTypeByType.TryGetValue(tag, out var tagType))
            throw new ArgumentException($"{tag.FullName} is not a tag (a struct implementing ITag)", nameof(tag));
        entity.Raw.AddTags(new F.Tags(tagType));
    }

    // Takes a tag off (a load laying saved tags over what content placed, 4i-3).
    internal void RemoveTag(Entity entity, Type tag)
    {
        if (entity.IsNull) return;
        if (F.EntityStore.GetEntitySchema().TagTypeByType.TryGetValue(tag, out var tagType))
            entity.Raw.RemoveTags(new F.Tags(tagType));
    }

    // Takes a component off by its type (a load removing what a diffed entity's prefab gave it, 4i-5).
    internal void Remove(Entity entity, Type type)
    {
        if (entity.IsNull || entity.Raw.Archetype is not { } archetype) return;
        var component = ComponentTypeOf(type);
        if (archetype.ComponentTypes.Contains(component)) F.EntityUtils.RemoveEntityComponent(entity.Raw, component);
    }

    // Friflo's handle for a component type, declared or not (the inspector writes Friflo's own too).
    private static F.ComponentType ComponentTypeOf(Type type) =>
        F.EntityStore.GetEntitySchema().ComponentTypeByType.TryGetValue(type, out var component)
            ? component
            : throw new ArgumentException($"{type.FullName} is not a component (a struct implementing IComponent)", nameof(type));

    // Writes a boxed component back onto an entity — the other half of `Read`, and what an editor's
    // inspector needs: a component is a struct, so editing one means boxing it, changing a field by
    // reflection and putting the box back (15 §3). The generated metadata (09 §3.2) replaces the
    // reflection, not this call.
    public void Write(Entity entity, Type type, object value)
    {
        if (entity.IsNull || value is null) return;
        F.EntityUtils.AddEntityComponentValue(entity.Raw, ComponentTypeOf(type), value);
    }

    // The component as a boxed value, for printing (`ent_dump`). Null when the entity hasn't got one
    // — asking Friflo for a component that isn't there throws, so the archetype is checked first.
    public object? Read(Entity entity, Type type)
    {
        if (entity.IsNull || entity.Raw.Archetype is not { } archetype) return null;
        var component = ComponentTypeOf(type);
        return archetype.ComponentTypes.Contains(component) ? F.EntityUtils.GetEntityComponent(entity.Raw, component) : null;
    }

    // Every declared component on the entity, in schema order, as (id, value). Friflo's own components
    // have no id and are left out.
    public IEnumerable<(string Id, object Value)> ComponentsOf(Entity entity)
    {
        if (entity.IsNull || entity.Raw.Archetype is not { } archetype) yield break;
        Build();
        foreach (var type in archetype.ComponentTypes)
        {
            if (!_byType.TryGetValue(type.Type, out var entry)) continue;
            var value = F.EntityUtils.GetEntityComponent(entity.Raw, type);
            if (value != null) yield return (entry.Declaration.Id, value);
        }
    }

    // The ids of the entity's declared tags (Friflo's own Disabled has none).
    public IEnumerable<string> TagsOf(Entity entity)
    {
        if (entity.IsNull) yield break;
        Build();
        foreach (var tag in entity.Tags)
            if (_byType.TryGetValue(tag, out var entry)) yield return entry.Declaration.Id;
    }
}
