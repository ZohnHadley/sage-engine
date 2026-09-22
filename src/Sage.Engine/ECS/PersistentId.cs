#nullable enable
using System;
using Friflo.Engine.ECS;

namespace sage_engine;

// Stable identity for placed or saved entities (docs/design/03 §3.3, 09). Runtime handles
// (Friflo `Entity`) are only valid for one world and one session; maps, saves and quests use this.
public readonly record struct PersistentId(Guid Value)
{
    public static PersistentId New() => new(Guid.NewGuid());
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString("N");
}

// Marks an entity as persistent. World keeps a PersistentId → Entity index from it (World.Resolve).
// Entities without it are never saved (docs/design/09 §3.5).
public struct Persistent : IComponent
{
    public PersistentId Id;
}
