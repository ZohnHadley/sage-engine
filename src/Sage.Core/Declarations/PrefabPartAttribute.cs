#nullable enable
using System;

namespace sage_engine;

// Declares a prefab part (issue #17): a class implementing IPrefabPart whose fields are the part's
// options. Here, in the kernel, because the metadata table reads it without knowing prefabs.
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PrefabPartAttribute : Attribute
{
    public PrefabPartAttribute(string id) { Id = id; }

    // The key a prefab writes under "parts". One id, one part: a second is an error (issue #17).
    public string Id { get; }

    // The plugin that registers it; inferred when the assembly has one [Plugin] (SAGE0011).
    public string? Plugin { get; set; }

    // Parts that must have been applied first, by id: `pickup` checks for a sprite and a collider and
    // only adds its own when there are none. Parts otherwise run in id order (see PrefabRegistry).
    public string[] After { get; set; } = Array.Empty<string>();

    // A field a bare value fills: `"faction": "beasts"` is `"faction": { "id": "beasts" }` when this is
    // "Id". A part that takes one value should not make every prefab write an object for it.
    public string? Shorthand { get; set; }
}
