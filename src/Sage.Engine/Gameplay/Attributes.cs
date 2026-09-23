#nullable enable
using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;

namespace sage_engine;

// Attributes and tags (docs/design/16 §3.3, TODO F18), the data every gameplay system reads: health,
// mana, resistances, and the states an entity is in. Effects (Effects.cs) are the only thing that
// changes them, so buffs, damage over time and cooldowns all go through one path.

// An attribute's definition: its default base value and the range it is clamped to.
[Record("attribute")]
public sealed class AttributeRecord
{
    public float Start;          // the value an entity gets when it is created ("base" is reserved
                                 // for record inheritance, 05 §3.5)
    public float Min;
    public float Max = float.MaxValue;

    // How this pool is *spent* (16 §3.3): an effect whose modifier takes one unit of it, scaled by
    // the magnitude of the spend. Naming it here rather than in every ability keeps the rule that
    // nothing subtracts an attribute directly — mana leaves the same way health does.
    public RecordId SpendEffect;

    public static readonly RecordId Health = new("sage", "health");
}

// A gameplay tag. Names are hierarchical by convention: "state.dead", "element.fire".
[Record("tag")]
public sealed class TagRecord
{
    public string Description = "";

    public static readonly RecordId Dead = new("sage", "state.dead");
    public static readonly RecordId Invulnerable = new("sage", "state.invulnerable");
}

// Record ids → small indices, so components hold numbers instead of strings. Rebuilt when records
// reload; ids an entity already holds stay valid because the order is stable (sorted).
public sealed class GameplayRegistries
{
    private readonly Dictionary<RecordId, int> _attributes = new();
    private readonly List<RecordId> _attributeIds = new();
    private readonly Dictionary<RecordId, int> _tags = new();
    private readonly List<RecordId> _tagIds = new();

    public const int MaxTags = 64;   // a ulong bitset; a real bitset class comes if a game needs more

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
public struct Attributes : IComponent
{
    public AttributeSet Values;

    public static Attributes Create() => new() { Values = new AttributeSet() };
}

// A 64-tag bitset (16 §3.3). Tags come from effects (granted while active) or from gameplay directly.
public struct GameplayTags : IComponent
{
    public ulong Bits;
    public ulong Granted;   // the part currently granted by active effects

    public readonly bool Has(int tag) => tag >= 0 && (Bits & (1UL << tag)) != 0;
    public readonly bool HasAll(ulong mask) => (Bits & mask) == mask;
    public readonly bool HasAny(ulong mask) => (Bits & mask) != 0;

    public void Add(int tag) { if (tag >= 0) Bits |= 1UL << tag; }
    public void Remove(int tag) { if (tag >= 0) Bits &= ~(1UL << tag); }
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
