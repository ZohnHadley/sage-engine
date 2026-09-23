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
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class TransientAttribute : Attribute { }

// What a prefab-spawned entity came from (F31), so a save can rebuild it: loading is
// `Spawn(prefab)` and then the saved components over the top, which restores the *parts* a prefab
// applied (a character's collider, controller and intent) without saving any of them.
//
// Also what the editor's "revert to prefab" (15) will read.
public struct FromPrefab : Friflo.Engine.ECS.IComponent
{
    public RecordId Prefab;
}
