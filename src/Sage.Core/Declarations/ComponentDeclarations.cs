#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Core;

// Stable ids for components and tags (docs/REDESIGN.md §3.4, issue #16).
//
//   [Component("sage:health", Version = 2)]
//   public struct Health : IComponent { public float Value; [Upgrade(1)] static void From1(ref JsonObject o) => o.RenameField("Hp", "Value"); }
//
// The id is what a prefab writes, what a save is keyed by and what `ent_dump` prints. It is chosen once
// and left alone, so the C# type can be renamed or moved without breaking a single content file or
// save. Before this a component was its bare C# type name, matched case-insensitively, and two
// assemblies each declaring `Health` silently overwrote each other (`ComponentSchema`, issue #16).
//
// Ids are `namespace:name` in the record id alphabet (lower-case letters, digits, `_`, `.`, `-`):
// `sage:` for the engine's, a game's or mod's own namespace for theirs. Sage.Generators writes each
// assembly's table from these attributes and makes an IComponent or ITag struct without one a build
// error (SAGE0004), so a component cannot be left unnamed.
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class ComponentAttribute : Attribute
{
    public ComponentAttribute(string id) { Id = id; }
    public string Id { get; }

    // The version of the component's *saved shape*. Bump it when a field is renamed, moved or removed,
    // and add an [Upgrade(previous)] method that rewrites the old shape (issue #20). Adding a field
    // needs no bump: an old save simply leaves it at its default.
    public int Version { get; set; } = 1;

    // Ids (or, for saves written before issue #16, C# type names) this component used to be saved
    // under. What renaming a component's id, or its type before ids existed, costs: one line here.
    public string[]? FormerNames { get; set; }
}

// The same for a tag (ITag): tags carry no data, so they have an id and nothing else.
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class TagAttribute : Attribute
{
    public TagAttribute(string id) { Id = id; }
    public string Id { get; }
    public string[]? FormerNames { get; set; }
}

// Marks a static method that rewrites a component's (or saved resource's) saved JSON from version
// `FromVersion` to the next one (issue #20). Upgraders run in order on load, oldest first, before
// the JSON is read into the type, so a save several versions old goes through each step:
//
//   [Upgrade(1)] static void From1(ref JsonObject o) { o.RenameField("Hp", "Value"); }
//
// The signature is fixed (static, void, one `ref JsonObject`); Sage.Generators checks it (SAGE0007).
// They are found by reflection when an old save is read, not listed in generated code, so an upgrader
// may be private — it is part of the type's past, not its API.
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class UpgradeAttribute : Attribute
{
    public UpgradeAttribute(int fromVersion) { FromVersion = fromVersion; }
    public int FromVersion { get; }
}

// One declared component or tag, as the generated table (or, for an assembly built without
// Sage.Generators, reflection over the same attributes) describes it.
public sealed record ComponentDeclaration(Type Type, string Id, int Version, IReadOnlyList<string> FormerNames, bool IsTag)
{
    public string Namespace => Id.Substring(0, Id.IndexOf(':'));
    public string Name => Id.Substring(Id.IndexOf(':') + 1);

    // `namespace:name`, both halves in the record id alphabet (RecordId): the one rule the generator
    // and the runtime both apply.
    public static bool IsValidId(string id)
    {
        int colon = id.IndexOf(':');
        if (colon <= 0 || colon == id.Length - 1 || id.IndexOf(':', colon + 1) >= 0) return false;
        foreach (char c in id)
            if (!(c == ':' || char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_' || c == '.' || c == '-'))
                return false;
        return true;
    }
}

// What Sage.Generators writes into each assembly that declares components or tags. Components are per
// assembly, not per plugin: a component type exists (Friflo's schema has it) whether or not the plugin
// that uses it is loaded, so its id has to as well — unlike records, which a plugin registers.
public interface IGeneratedComponents
{
    IReadOnlyList<ComponentDeclaration> Declarations { get; }
}

// Marks an assembly's generated component table, so ComponentSchema finds it without scanning types.
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GeneratedComponentsAttribute : Attribute
{
    public GeneratedComponentsAttribute(Type type) { Type = type; }
    public Type Type { get; }
}
