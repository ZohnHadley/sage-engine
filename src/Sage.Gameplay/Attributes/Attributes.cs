#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Gameplay;

// Attributes and tags (docs/design/16 §3.3, TODO F18), the data every gameplay system reads: health,
// mana, resistances, and the states an entity is in. Effects (Effects.cs) are the only thing that
// changes them, so buffs, damage over time and cooldowns all go through one path.

// An attribute's definition: its default base value and the range it is clamped to.
[Record("attribute", Plugin = "sage.gameplay.attributes")]
public sealed class AttributeRecord
{
    [Property(Category = "Vitals", Tooltip = "The value an entity starts with")]
    public float Start;          // the value an entity gets when it is created ("base" is reserved
                                 // for record inheritance, 05 §3.5)
    [Property(Category = "Vitals", Tooltip = "The lowest the value can go; it is clamped here")]
    public float Min;
    [Property(Category = "Vitals", Tooltip = "The highest the value can go; it is clamped here")]
    public float Max = float.MaxValue;

    // How this pool is *spent* (16 §3.3): an effect whose modifier takes one unit of it, scaled by
    // the magnitude of the spend. Naming it here rather than in every ability keeps the rule that
    // nothing subtracts an attribute directly — mana leaves the same way health does.
    [Property(Tooltip = "The effect that spends one unit of this attribute (mana, stamina)")]
    public RecordRef<EffectRecord> SpendEffect;
}

// A gameplay tag. Names are hierarchical by convention: "state.dead", "element.fire".
[Record("tag", Plugin = "sage.gameplay.attributes")]
public sealed class TagRecord
{
    public string Description = "";
}

// Record ids → small indices, so components hold numbers instead of strings. Rebuilt when records
// reload; ids an entity already holds stay valid because the order is stable (sorted).
public sealed class GameplayRegistries
{
    private readonly Dictionary<RecordId, int> _attributes = new();
    private readonly List<RecordId> _attributeIds = new();
    private readonly Dictionary<RecordId, int> _tags = new();
    private readonly List<RecordId> _tagIds = new();

    public const int MaxTags = TagSet.Capacity;   // 256 (issue #28; it was one ulong, 64)

    public int AttributeCount => _attributeIds.Count;
    public int TagCount => _tagIds.Count;

    public int Attribute(RecordId id) => _attributes.TryGetValue(id, out int index) ? index : -1;
    public RecordId AttributeId(int index) => _attributeIds[index];
    public int Tag(RecordId id) => _tags.TryGetValue(id, out int index) ? index : -1;
    public RecordId TagId(int index) => _tagIds[index];

    // Called after records load or reload.
    public void Rebuild(RecordStore records)
    {
        _attributes.Clear();
        _attributeIds.Clear();
        foreach (var id in records.Ids("attribute"))
        {
            _attributes[id] = _attributeIds.Count;
            _attributeIds.Add(id);
        }

        _tags.Clear();
        _tagIds.Clear();
        foreach (var id in records.Ids("tag"))
        {
            if (_tagIds.Count == MaxTags)
            {
                Log.Error(LogCat.Gameplay, $"More than {MaxTags} tag records; '{id}' and any after it are ignored (16 §3.3)");
                break;
            }
            _tags[id] = _tagIds.Count;
            _tagIds.Add(id);
        }
        Log.Debug(LogCat.Gameplay, $"Gameplay ids: {_attributeIds.Count} attributes, {_tagIds.Count} tags");
    }
}

// An entity's attribute values: Base is what it would be with nothing applied, Current is after the
// active effects. Effects write Base only for instant changes (damage); everything else recomputes
// Current every tick.
public sealed class AttributeSet
{
    private float[] _base = Array.Empty<float>();
    private float[] _current = Array.Empty<float>();
    private bool[] _known = Array.Empty<bool>();

    public float BaseOf(int attribute) => Valid(attribute) ? _base[attribute] : 0f;
    public float this[int attribute] => Valid(attribute) ? _current[attribute] : 0f;
    public bool Has(int attribute) => Valid(attribute) && _known[attribute];

    public void SetBase(int attribute, float value)
    {
        if (!Grow(attribute)) return;
        _base[attribute] = value;
        _current[attribute] = value;
        _known[attribute] = true;
    }

    internal void SetCurrent(int attribute, float value)
    {
        if (Valid(attribute)) _current[attribute] = value;
    }

    private bool Valid(int attribute) => attribute >= 0 && attribute < _base.Length;

    private bool Grow(int attribute)
    {
        if (attribute < 0) return false;
        if (attribute < _base.Length) return true;
        int size = Math.Max(attribute + 1, 8);
        Array.Resize(ref _base, size);
        Array.Resize(ref _current, size);
        Array.Resize(ref _known, size);
        return true;
    }
}

// The component: one set per entity, filled from attribute records when the entity is created.
[Component("sage:attributes")]
public struct Attributes : IComponent
{
    public AttributeSet Values;

    public static Attributes Create() => new() { Values = new AttributeSet() };
}

// A tag bitset (16 §3.3). Tags come from effects (granted while active) or from gameplay directly.
[Component("sage:gameplay_tags")]
public struct GameplayTags : IComponent
{
    public TagSet Bits;
    [Transient] public TagSet Granted;   // the part granted by active effects: rebuilt from ActiveEffects; saving it would strip real tags

    public readonly bool Has(int tag) => Bits.Has(tag);
    public readonly bool HasAll(in TagSet mask) => Bits.HasAll(mask);
    public readonly bool HasAny(in TagSet mask) => Bits.HasAny(mask);

    public void Add(int tag) => Bits.Add(tag);
    public void Remove(int tag) => Bits.Remove(tag);
}

// 256 tag bits in four words (issue #28: one word was 64 tags, and a game's own tags ran out long
// before its content did). A value, so a component holding one copies without allocating; the tick
// folds granted tags into one on the stack. Saved by name, never as bits (GameplayTagsSaveConverter).
public struct TagSet : IEquatable<TagSet>
{
    public const int Capacity = 256;

    private ulong _w0, _w1, _w2, _w3;

    public readonly bool Has(int tag) => tag is >= 0 and < Capacity && (Word(tag >> 6) & (1UL << (tag & 63))) != 0;

    public void Add(int tag)
    {
        if (tag is < 0 or >= Capacity) return;
        SetWord(tag >> 6, Word(tag >> 6) | (1UL << (tag & 63)));
    }

    public void Remove(int tag)
    {
        if (tag is < 0 or >= Capacity) return;
        SetWord(tag >> 6, Word(tag >> 6) & ~(1UL << (tag & 63)));
    }

    public readonly bool IsEmpty => (_w0 | _w1 | _w2 | _w3) == 0;
    public readonly bool HasAll(in TagSet mask) =>
        (_w0 & mask._w0) == mask._w0 && (_w1 & mask._w1) == mask._w1 && (_w2 & mask._w2) == mask._w2 && (_w3 & mask._w3) == mask._w3;
    public readonly bool HasAny(in TagSet mask) =>
        ((_w0 & mask._w0) | (_w1 & mask._w1) | (_w2 & mask._w2) | (_w3 & mask._w3)) != 0;

    // (this without `remove`) with `add`: how the tick swaps last tick's granted tags for this tick's.
    public readonly TagSet Replace(in TagSet remove, in TagSet add) => new()
    {
        _w0 = (_w0 & ~remove._w0) | add._w0,
        _w1 = (_w1 & ~remove._w1) | add._w1,
        _w2 = (_w2 & ~remove._w2) | add._w2,
        _w3 = (_w3 & ~remove._w3) | add._w3,
    };

    private readonly ulong Word(int index) => index switch { 0 => _w0, 1 => _w1, 2 => _w2, _ => _w3 };

    private void SetWord(int index, ulong value)
    {
        switch (index)
        {
            case 0: _w0 = value; break;
            case 1: _w1 = value; break;
            case 2: _w2 = value; break;
            default: _w3 = value; break;
        }
    }

    public readonly bool Equals(TagSet other) => _w0 == other._w0 && _w1 == other._w1 && _w2 == other._w2 && _w3 == other._w3;
    public readonly override bool Equals(object? obj) => obj is TagSet other && Equals(other);
    public readonly override int GetHashCode() => HashCode.Combine(_w0, _w1, _w2, _w3);
}

// Helpers for gameplay code that works with record ids rather than indices.
public static class GameplayExtensions
{
    public static float Attribute(this World world, Entity entity, RecordId attribute)
    {
        var registries = world.Resources.Get<GameplayRegistries>();
        return world.TryGet<Attributes>(entity, out var attributes)
            ? attributes.Values[registries.Attribute(attribute)] : 0f;
    }

    // Sets an entity up for gameplay: every attribute the records define at its starting value, plus
    // the effect and tag components. Call it once when the entity is created — effects never add
    // components themselves, because they are applied from inside system loops, where a structural
    // change is not allowed (03 §3.5).
    public static void AddAttributes(this World world, Entity entity)
    {
        var registries = world.Resources.Get<GameplayRegistries>();
        var records = world.Resources.Get<RecordStore>();
        if (!world.Has<Attributes>(entity)) world.Add(entity, Attributes.Create());
        ref var attributes = ref world.Get<Attributes>(entity);
        for (int i = 0; i < registries.AttributeCount; i++)
        {
            var id = registries.AttributeId(i);
            if (records.TryGet(id, out AttributeRecord record)) attributes.Values.SetBase(i, record.Start);
        }
        if (!world.Has<ActiveEffects>(entity)) world.Add(entity, ActiveEffects.Create());
        if (!world.Has<GameplayTags>(entity)) world.Add(entity, new GameplayTags());
    }

    public static bool HasTag(this World world, Entity entity, RecordId tag)
    {
        var registries = world.Resources.Get<GameplayRegistries>();
        return world.TryGet<GameplayTags>(entity, out var tags) && tags.Has(registries.Tag(tag));
    }

    public static void AddTag(this World world, Entity entity, RecordId tag)
    {
        if (!world.Has<GameplayTags>(entity)) world.Add(entity, new GameplayTags());
        ref var tags = ref world.Get<GameplayTags>(entity);
        tags.Add(world.Resources.Get<GameplayRegistries>().Tag(tag));
    }

    public static void RemoveTag(this World world, Entity entity, RecordId tag)
    {
        if (!world.Has<GameplayTags>(entity)) return;
        ref var tags = ref world.Get<GameplayTags>(entity);
        tags.Remove(world.Resources.Get<GameplayRegistries>().Tag(tag));
    }
}
