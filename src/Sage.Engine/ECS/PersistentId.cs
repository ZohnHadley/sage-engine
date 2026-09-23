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

    // Round-trips `ToString`, for saves and map files (09 §3.3). A malformed id is not an exception:
    // one bad entity in a save should cost that entity, not the load.
    public static bool TryParse(string? text, out PersistentId id)
    {
        if (Guid.TryParseExact(text, "N", out var value) || Guid.TryParse(text, out value))
        {
            id = new PersistentId(value);
            return !id.IsEmpty;
        }
        id = default;
        return false;
    }

    // Derives a stable id from a name, so a thing placed by a scene or a map has the *same* identity
    // every run without anybody authoring a GUID. Same name in, same id out, for ever.
    public static PersistentId FromName(string name)
    {
        Span<byte> hash = stackalloc byte[16];
        System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name), hash);
        return new PersistentId(new Guid(hash));
    }
}

// Marks an entity as persistent. World keeps a PersistentId → Entity index from it (World.Resolve).
// Entities without it are never saved (docs/design/09 §3.5).
public struct Persistent : IComponent
{
    public PersistentId Id;
}
