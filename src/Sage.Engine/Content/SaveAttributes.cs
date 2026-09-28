#nullable enable
using System;

namespace sage_engine;

// The saving contract (docs/design/09 §3.1, TODO F27).
//
// **Deviation from 09, deliberate and temporary.** The design has `[Saved(tag)]` opt-in, with the
// source generator warning about any field that decided neither way. There is no generator yet, so
// there is nothing to warn — and without a warning, opt-in means a field added next month is silently
// left out of saves. Losing the player's inventory is a worse failure than writing a number that
// could have been recomputed, so v1 inverts it: **every public field of a component is saved unless
// it is `[Transient]`**, which is also how the record pipeline already reads data (05 §3.5).
//
// When the generator lands it flips back to `[Saved]` with a compile-time warning, which is strictly
// better; `[Transient]` keeps its meaning either way, and marking the fields that must *never* be
// saved is the work that carries over.
//
// On a component or tag *type* it means the whole thing is never saved (issue #20): state derived from
// something else, or a handle that means nothing in another session. That is the list SaveSerializer
// used to keep by C# type name, which a rename would have silently emptied.
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Struct)]
public sealed class TransientAttribute : Attribute { }

// What a prefab-spawned entity came from (F31), so a save can rebuild it: loading is
// `Spawn(prefab)` and then the saved components over the top, which restores the *parts* a prefab
// applied (a character's collider, controller and intent) without saving any of them.
//
// Also what the editor's "revert to prefab" (15) will read.
[Component("sage:from_prefab")]
public struct FromPrefab : Friflo.Engine.ECS.IComponent
{
    public RecordId Prefab;
}

// A world resource that belongs in the save (docs/design/09 §3.1). Not everything about a world is
// on an entity: the spells the player composed, the quests they are on, how the world feels about
// them. Those are per-world singletons (03 §3.4), and a save that only wrote entities would lose them.
//
// The name is the key in the save file and is what makes it survive a rename of the class, so it is
// chosen once and left alone.
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SavedResourceAttribute : Attribute
{
    public SavedResourceAttribute(string name) { Name = name; }
    public string Name { get; }

    // The version of its saved shape, with [Upgrade] methods for older ones — as for a component
    // (ComponentAttribute.Version, issue #20).
    public int Version { get; set; } = 1;

    // The plugin that registers it, as for [Record] (issue #16).
    public string? Plugin { get; set; }
}

// For a saved resource whose data implies something else that has to be rebuilt — a spellbook's
// drafts become `ability` records again (F21). Called once the world around it has finished loading.
public interface ISavedResource
{
    void AfterLoad(World world);
}
