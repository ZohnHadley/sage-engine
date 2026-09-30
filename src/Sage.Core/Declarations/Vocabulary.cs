#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage.Core;

// Open vocabularies (docs/REDESIGN.md §4.3 stage 1, issue #28): the words content may use for what
// something *does* — a quest objective's kind, a dialogue condition, an effect's execution, what using
// an item does — as registries a plugin adds to, not enums the engine owns. The pattern is the one
// `AITaskRegistry` and `EntityInputs` already had, declared through the generator like records:
//
//   [Vocabulary("quest_objective", Key = "kind", Default = "kill")]
//   public abstract class QuestObjective { … }                       // what an entry is
//
//   public sealed class QuestObjectiveAttribute : VocabularyEntryAttribute<QuestObjective> { … }
//
//   [QuestObjective("reach", Plugin = "sage.gameplay.quests")]
//   public sealed class ReachObjective : QuestObjective { public Vector3 At; public float Radius = 2f; … }
//
// becomes a registration made for its plugin just before that plugin's Init (VocabularyGenerator), and
// content names it: `{ "kind": "reach", "at": [4, 0, 9] }`, or a bare `"reach"` when it takes nothing.
// The engine's own entries are registered the same way, so a game's `is_night` and the engine's
// `SeeEnemy` are the same kind of thing. Registration closes when content loads (RegistrationSeal).

// Marks the type a vocabulary's entries implement (an interface or an abstract class). `Name` is what
// schemas, the registry dump and errors call it; `Key` is the field that names the entry in a JSON
// object; `Default` is the entry an object without that field means (the old implicit kind).
//
// `Shorthand` (issue #89) lets an object name its entry by using the id *as* a property, the way
// REDESIGN §4.3 stage 2 writes conditions and actions:
//
//   { "has_item": "key_iron", "count": 2 }     = { "condition": "has_item", "item": "key_iron", "count": 2 }
//   { "not": { "var": "alarm", "eq": 1 } }     = { "condition": "not", "of": { … } }
//   { "set_stage": { "quest": "q", "stage": "b" } }   the value is the settings when it is an object
//
// The first property that names a registered entry is the entry; the rest are its settings. Its value
// fills the entry's [EntryValue] field, unless it is an object and that field is not itself a
// vocabulary, in which case it is the entry's settings. Only for a vocabulary without a `Default`: an
// object without the key has no other meaning there, so no content that loaded before reads differently.
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class, Inherited = false)]
public sealed class VocabularyAttribute : Attribute
{
    public VocabularyAttribute(string name) { Name = name; }

    public string Name { get; }
    public string Key { get; set; } = "kind";
    public string? Default { get; set; }

    [Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // stage 2 logic (#89): may change before 1.0
    public bool Shorthand { get; set; }
}

// The field of an entry that the shorthand's value fills: `{ "var": "alarm" }` sets the `var`
// condition's `Name`, `{ "all": [ … ] }` the `all` condition's `Of` (VocabularyAttribute.Shorthand).
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // stage 2 logic (#89): may change before 1.0
public sealed class EntryValueAttribute : Attribute
{
}

// The base of every attribute that declares an entry: `[AICondition("is_night")]`,
// `[QuestObjective("reach")]`. A plugin declares a vocabulary's attribute by deriving from this, and the
// generator (VocabularyGenerator) registers every class that carries one for the plugin that owns it:
// `Plugin = "id"`, or the assembly's only [Plugin] (SAGE0100 otherwise). One class may declare several
// ids; each id is its own entry and gets its own instance.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public abstract class VocabularyEntryAttribute<TEntry> : Attribute where TEntry : class
{
    protected VocabularyEntryAttribute(string id) { Id = id; }

    public string Id { get; }
    public string? Plugin { get; set; }
}

// A string field (or a list of them) that names an entry of a vocabulary by id, as a schedule's
// `interrupts` name AI conditions: `sage schema` offers the registered ids, and the plugin that owns the
// record checks them when content loads.
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class VocabularyRefAttribute : Attribute
{
    public VocabularyRefAttribute(string vocabulary) { Vocabulary = vocabulary; }

    public string Vocabulary { get; }
}

// One registered entry: its id as registered, the type that implements it and the plugin that did.
public sealed class VocabularyEntry
{
    internal VocabularyEntry(string id, Type type, string owner, Func<object> create)
    {
        Id = id;
        Type = type;
        Owner = owner;
        Create = create;
    }

    public string Id { get; }
    public Type Type { get; }
    public string Owner { get; }
    internal Func<object> Create { get; }

    public override string ToString() => $"{Id} ({Type.Name}, {Owner})";
}

// A vocabulary: ids to the types that implement them. Ids match ignoring case, `_` and `-`, so
// `"TouchArea"`, `"touch_area"` and `"toucharea"` are one word — content already spelled some of these
// as enum names ("Kill", "SeeEnemy"), and a designer should not have to know which convention a word
// was born in.
public abstract class Vocabulary
{
    private readonly Dictionary<string, VocabularyEntry> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, VocabularyEntry> _byType = new();
    private readonly List<VocabularyEntry> _entries = new();
    private readonly RegistrationLedger? _ledger;

    private protected Vocabulary(Type entryType, VocabularyAttribute declared, RegistrationLedger? ledger)
    {
        EntryType = entryType;
        Name = declared.Name;
        Key = declared.Key;
        Default = declared.Default;
        Shorthand = declared.Shorthand && declared.Default == null;
        _ledger = ledger;
        Seal = new RegistrationSeal($"{Name} entry", "content read before it could not name it");
    }

    public string Name { get; }
    public string Key { get; }
    public string? Default { get; }
    public Type EntryType { get; }

    // Whether an entry may be written `{ "<id>": value, …settings }` (VocabularyAttribute.Shorthand).
    public bool Shorthand { get; }

    // Closed when content loads: an entry registered later was missing when the records naming it read.
    public RegistrationSeal Seal { get; }

    // Every entry, by id.
    public IReadOnlyList<VocabularyEntry> Entries => _entries.OrderBy(e => e.Id, StringComparer.Ordinal).ToList();

    public IEnumerable<string> Ids => _entries.Select(e => e.Id).OrderBy(i => i, StringComparer.Ordinal);

    public bool TryFind(string id, out VocabularyEntry entry) => _byKey.TryGetValue(Normalize(id), out entry!);

    public bool Contains(string id) => _byKey.ContainsKey(Normalize(id));

    // The id a type was registered under (its first), for writing an entry back out.
    public string? IdOf(Type type) => _byType.TryGetValue(type, out var entry) ? entry.Id : null;

    // "no quest_objective 'raech'; did you mean 'reach'? (there are: have, kill, reach, talk)"
    public string Unknown(string id) =>
        $"no {Name} '{id}'" + Spelling.Suggest(id, Ids) + (_entries.Count == 0 ? " (none are registered)" : $" (there are: {string.Join(", ", Ids)})");

    // The type an entry written as `node` is: a bare id, or an object naming it under `Key` (or, in
    // shorthand, by a property named for it).
    public Type? TypeOf(JsonNode? node)
    {
        string? id = node switch
        {
            JsonValue value when value.TryGetValue(out string? text) => text,
            JsonObject obj => KeyOf(obj, out var named) ? named : ShorthandOf(obj, out _)?.Id ?? Default,
            _ => null,
        };
        return id != null && TryFind(id, out var entry) ? entry.Type : null;
    }

    // The entry a shorthand object names, and the property that names it: the first that is an id.
    private VocabularyEntry? ShorthandOf(JsonObject obj, out string? property)
    {
        property = null;
        if (!Shorthand) return null;
        foreach (var (name, _) in obj)
            if (TryFind(name, out var entry))
            {
                property = name;
                return entry;
            }
        return null;
    }

    // The long form of an entry written in shorthand — `{ "<Key>": "<id>", …settings }` — or null when
    // `obj` is not shorthand (it has the key, or this vocabulary has none, or nothing in it is an id).
    // What the reader and the namespace qualifier (RecordStore.PolymorphicShorthand) both read.
    public JsonObject? Expand(JsonObject obj)
    {
        if (!Shorthand || KeyOf(obj, out _)) return null;
        var entry = ShorthandOf(obj, out string? property);
        if (entry == null) return null;

        var value = obj[property!];
        var result = new JsonObject { [Key] = entry.Id };
        var field = ValueFieldOf(entry.Type);
        if (field != null && !(value is JsonObject && !Vocabularies.IsVocabulary(field.Value.Type)))
        {
            result[JsonMembers.JsonName(field.Value.Name)] = value?.DeepClone();
        }
        else if (value is JsonObject settings)
        {
            foreach (var (name, setting) in settings) result[name] = setting?.DeepClone();
        }
        else if (value is not null && !(value is JsonValue flag && flag.TryGetValue(out bool yes) && yes))
        {
            throw new JsonException($"{Name} '{entry.Id}' takes no value of its own: write {{ \"{property}\": {{ …settings }} }}");
        }

        foreach (var (name, setting) in obj)
        {
            if (name == property) continue;
            if (result.Any(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)))
                throw new JsonException($"{Name} '{entry.Id}': '{name}' is given twice");
            result[name] = setting?.DeepClone();
        }
        return result;
    }

    // Why an object without the key names no entry: the nearest id to what it wrote.
    internal string NoEntry(JsonObject obj)
    {
        if (Shorthand)
        {
            // The property most like an id: the one a typo was made in.
            string? best = null;
            int bestDistance = int.MaxValue;
            foreach (var (name, _) in obj)
                foreach (var id in _entries)
                {
                    int d = Spelling.Distance(Normalize(name), Normalize(id.Id));
                    if (d < bestDistance) { bestDistance = d; best = name; }
                }
            if (best != null) return Unknown(best);
        }
        return $"a {Name} needs \"{Key}\"" + (Shorthand ? ", or its id as a property" : "") + $": one of {string.Join(", ", Ids)}";
    }

    private static (string Name, Type Type)? ValueFieldOf(Type type)
    {
        foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance))
            if (member.GetCustomAttribute<EntryValueAttribute>(true) != null)
                return member switch
                {
                    FieldInfo f => (f.Name, f.FieldType),
                    PropertyInfo p => (p.Name, p.PropertyType),
                    _ => null,
                };
        return null;
    }

    // The member an entry's shorthand value fills, for schemas.
    public static string? ValueFieldName(Type type) => ValueFieldOf(type)?.Name;

    internal bool KeyOf(JsonObject obj, out string? id)
    {
        foreach (var (name, value) in obj)
            if (string.Equals(name, Key, StringComparison.OrdinalIgnoreCase))
            {
                id = value is JsonValue v && v.TryGetValue(out string? text) ? text : null;
                return true;
            }
        id = null;
        return false;
    }

    // Ids compare without case, underscores or hyphens.
    public static string Normalize(string id)
    {
        Span<char> buffer = id.Length <= 128 ? stackalloc char[id.Length] : new char[id.Length];
        int n = 0;
        foreach (char c in id.Trim())
            if (c is not ('_' or '-')) buffer[n++] = char.ToLowerInvariant(c);
        return new string(buffer[..n]);
    }

    private protected void Add(string id, Type type, Func<object> create)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException($"A {Name} entry needs an id ({type.Name}).");
        if (!EntryType.IsAssignableFrom(type))
            throw new InvalidOperationException($"{type.Name} is registered as {Name} '{id}' but is not a {EntryType.Name}.");
        Seal.Check(id);
        string key = Normalize(id);
        if (_byKey.TryGetValue(key, out var existing))
            throw new InvalidOperationException(
                $"{Name} '{id}' is already registered by {existing.Owner} ({existing.Type.Name}); {_ledger?.Owner ?? "host"} registers it again ({type.Name}).");
        var entry = new VocabularyEntry(id.Trim(), type, _ledger?.Owner ?? "host", create);
        _byKey[key] = entry;
        _byType.TryAdd(type, entry);
        _entries.Add(entry);
        _ledger?.Record(Name, id);
    }

    internal object CreateObject(VocabularyEntry entry) => entry.Create();
}

public sealed class Vocabulary<TEntry> : Vocabulary where TEntry : class
{
    private readonly Dictionary<VocabularyEntry, TEntry> _shared = new();

    internal Vocabulary(VocabularyAttribute declared, RegistrationLedger? ledger) : base(typeof(TEntry), declared, ledger) { }

    // What generated code calls (RegistrationBuilder.Vocabulary), and what a module's Init may call by hand.
    public void Register<T>(string id) where T : class, TEntry, new() => Add(id, typeof(T), static () => new T());

    // An entry made by a function rather than new(): one that needs a service, or a test's.
    public void Register(string id, Type type, Func<TEntry> create) => Add(id, type, create);

    // One shared instance per id: for entries that hold no settings of their own (an AI condition, a
    // delivery), which code names by id and asks every tick. Null when there is no such entry.
    public TEntry? Find(string id)
    {
        if (!TryFind(id, out var entry)) return null;
        if (!_shared.TryGetValue(entry, out var instance)) _shared[entry] = instance = (TEntry)entry.Create();
        return instance;
    }

    // A new instance, for entries that carry settings read from content.
    public TEntry Create(string id) =>
        TryFind(id, out var entry) ? (TEntry)entry.Create() : throw new KeyNotFoundException(Unknown(id));
}

// Every vocabulary an engine has, made on first use from the entry type's [Vocabulary].
public sealed class Vocabularies
{
    private readonly Dictionary<Type, Vocabulary> _byType = new();
    private string? _sealedAt;

    // Who registered each entry (issue #12); set by the Engine.
    public RegistrationLedger? Ledger { get; set; }

    public static bool IsVocabulary(Type type) => type.GetCustomAttribute<VocabularyAttribute>(false) != null;

    public Vocabulary<TEntry> Of<TEntry>() where TEntry : class => (Vocabulary<TEntry>)Of(typeof(TEntry));

    public Vocabulary Of(Type entryType)
    {
        if (_byType.TryGetValue(entryType, out var found)) return found;
        var declared = entryType.GetCustomAttribute<VocabularyAttribute>(false)
                       ?? throw new InvalidOperationException($"{entryType.Name} is not a vocabulary: mark it [Vocabulary(\"name\")].");
        if (_byType.Values.FirstOrDefault(v => v.Name == declared.Name) is { } same)
            throw new InvalidOperationException($"Two vocabularies are named '{declared.Name}': {same.EntryType.FullName} and {entryType.FullName}.");
        var made = (Vocabulary)Activator.CreateInstance(typeof(Vocabulary<>).MakeGenericType(entryType),
            BindingFlags.Instance | BindingFlags.NonPublic, null, new object?[] { declared, Ledger }, null)!;
        if (_sealedAt != null) made.Seal.Seal(_sealedAt);
        _byType[entryType] = made;
        return made;
    }

    // Every vocabulary made so far, by name.
    public IEnumerable<Vocabulary> All => _byType.Values.OrderBy(v => v.Name, StringComparer.Ordinal);

    public Vocabulary? Named(string name) => _byType.Values.FirstOrDefault(v => v.Name == name);

    // The concrete type a JSON value of `type` is, when `type` is a vocabulary (RecordStore's
    // PolymorphicTypes: bare ids inside an entry are qualified like any other field's).
    public Type? ConcreteTypeOf(Type type, JsonNode? node) =>
        IsVocabulary(type) ? Of(type).TypeOf(node) : null;

    // An entry written in shorthand, in its long form (Vocabulary.Expand); null for anything else.
    public JsonNode? Expand(Type type, JsonNode? node) =>
        node is JsonObject obj && IsVocabulary(type) ? Of(type).Expand(obj) : null;

    public void Seal(string stage)
    {
        _sealedAt ??= stage;
        foreach (var vocabulary in _byType.Values) vocabulary.Seal.Seal(stage);
    }
}
