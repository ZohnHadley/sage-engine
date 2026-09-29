#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class, Inherited = false)]
public sealed class VocabularyAttribute : Attribute
{
    public VocabularyAttribute(string name) { Name = name; }

    public string Name { get; }
    public string Key { get; set; } = "kind";
    public string? Default { get; set; }
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
        _ledger = ledger;
        Seal = new RegistrationSeal($"{Name} entry", "content read before it could not name it");
    }

    public string Name { get; }
    public string Key { get; }
    public string? Default { get; }
    public Type EntryType { get; }

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

    // The type an entry written as `node` is: a bare id, or an object naming it under `Key`.
    public Type? TypeOf(JsonNode? node)
    {
        string? id = node switch
        {
            JsonValue value when value.TryGetValue(out string? text) => text,
            JsonObject obj => KeyOf(obj, out var named) ? named : Default,
            _ => null,
        };
        return id != null && TryFind(id, out var entry) ? entry.Type : null;
    }

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

    public void Seal(string stage)
    {
        _sealedAt ??= stage;
        foreach (var vocabulary in _byType.Values) vocabulary.Seal.Seal(stage);
    }
}
