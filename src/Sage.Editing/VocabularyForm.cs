#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage.Editing;

// The conditions and actions editor (issue #370): a form over any open vocabulary's value — a wire's
// `requires`, a dialogue option's `conditions`, a quest's objectives, an AI condition — built from what the
// vocabulary declares (its registered entries, and each entry's fields with their [Property] metadata), so a
// designer picks `has_item` from a list and fills in its item and count instead of typing JSON.
//
// **It edits JSON, in the long form.** The value is read as content writes it (a bare id, the shorthand
// `{ "has_item": "key_iron" }`, or `{ "condition": "has_item", "item": "key_iron" }`) and every edit writes it
// back in the long form, which reads the same: a form has one shape to show. Each edit is one command of
// whatever holds the value — a record's own history for a record (SetRecordValue), the placements
// document's for a wire (SetOutputs) — so the editors' undo takes it back one step at a time.
//
// **Paths** inside the value are RecordPath's: "" is the value itself, `[1]` an item of a list (the form of
// a `List<ICondition>` field), `of[0]` the first condition inside an `all`, `[1].of` the condition a `not`
// holds. A setting is named beside the entry's path: `SetParameter("of[0]", "count", "2")`.
//
// **Checks** (Validate) run on every read, so the panel shows them as they happen: an id nothing registered,
// a setting the entry does not have, a record that does not exist, a number out of its range, an enum name
// that is not one; then the value is read the way the game will read it, and what that refuses is a problem
// too. The problems panel shows the same lines (ProblemList: a wire's `requires` always, the form's own
// value while it is open).

// One field of an entry, as the form offers it: its metadata, whether it is the field the shorthand's value
// fills, the vocabulary it holds when it is itself a condition or a list of them, and what a new entry has.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed record VocabularyParameter(FieldMetadata Field, bool IsValue, Vocabulary? Nested, bool NestedList, JsonNode? Default)
{
    public string Name => Field.JsonName;

    // "int 0..  hp — How many": what the form's tooltip and `ed_vocab` say about it.
    public string Describe()
    {
        string type = Nested != null ? (NestedList ? $"list of {Nested.Name}" : Nested.Name) : Field.RecordType is { Length: > 0 } r ? $"{r} id" : Field.TypeName;
        string range = Field.Min != null || Field.Max != null ? $" {Field.Min?.ToString(System.Globalization.CultureInfo.InvariantCulture)}..{Field.Max?.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "";
        string unit = Field.Unit != null ? " " + Field.Unit : "";
        string choices = Field.EnumValues.Count > 0 ? $" ({string.Join("|", Field.EnumValues)})" : "";
        string tip = Field.Tooltip != null ? " — " + Field.Tooltip : "";
        return $"{Name}: {type}{range}{unit}{choices}{(IsValue ? " [value]" : "")}{tip}";
    }
}

// One registered entry of a vocabulary, with its fields.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed record VocabularyChoice(Vocabulary Vocabulary, VocabularyEntry Entry, IReadOnlyList<VocabularyParameter> Parameters)
{
    public string Id => Entry.Id;
    public string Owner => Entry.Owner;

    public VocabularyParameter? Parameter(string name) =>
        Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(p.Field.Name, name, StringComparison.OrdinalIgnoreCase));
}

// What the vocabularies hold, for a picker: every vocabulary, every entry of one with its fields.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class VocabularyCatalog
{
    public static IReadOnlyList<Vocabulary> All(Engine engine) => engine.Vocabularies.All.ToList();

    public static Vocabulary? Named(Engine engine, string name) =>
        engine.Vocabularies.All.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

    // The entries, by id; only those whose id or owner contains `search` (ignoring case) when one is given.
    public static IReadOnlyList<VocabularyChoice> Entries(Engine engine, Vocabulary vocabulary, string search = "")
    {
        var list = new List<VocabularyChoice>();
        foreach (var entry in vocabulary.Entries)
        {
            if (search.Length > 0 && !entry.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !Vocabulary.Normalize(entry.Id).Contains(Vocabulary.Normalize(search), StringComparison.Ordinal)
                && !entry.Owner.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(Describe(engine, vocabulary, entry));
        }
        return list;
    }

    public static VocabularyChoice? Entry(Engine engine, Vocabulary vocabulary, string id) =>
        vocabulary.TryFind(id, out var entry) ? Describe(engine, vocabulary, entry) : null;

    // The vocabulary a value of `type` is (an `ICondition`), or a list of (a `List<ICondition>`); null for neither.
    public static Vocabulary? Of(Engine engine, Type type, out bool many)
    {
        many = false;
        if (Vocabularies.IsVocabulary(type)) return engine.Vocabularies.Of(type);
        if (Metadata.KindOf(type, out var element) == ValueKind.List && element != null && Vocabularies.IsVocabulary(element))
        {
            many = true;
            return engine.Vocabularies.Of(element);
        }
        return null;
    }

    // The vocabulary a field holds, and whether it holds a list of entries; null when it holds none.
    public static Vocabulary? Of(Engine engine, FieldMetadata? field, out bool many)
    {
        many = false;
        if (field == null) return null;
        if (Vocabularies.IsVocabulary(field.Type)) return engine.Vocabularies.Of(field.Type);
        if (field.Kind == ValueKind.List && field.Item is { } item && Vocabularies.IsVocabulary(item.Type))
        {
            many = true;
            return engine.Vocabularies.Of(item.Type);
        }
        return null;
    }

    // The record store's options, writing what a default may be and content cannot say in JSON numbers (a
    // `min` of -infinity) as the named literal: a form only shows it.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> Lenient = new();

    internal static JsonSerializerOptions Writing(Engine engine) =>
        Lenient.GetValue(engine.Records.Json, static json => new JsonSerializerOptions(json)
        {
            NumberHandling = json.NumberHandling | System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        });

    private static VocabularyChoice Describe(Engine engine, Vocabulary vocabulary, VocabularyEntry entry)
    {
        var meta = Metadata.Of(entry.Type);
        string? valueField = Vocabulary.ValueFieldName(entry.Type);
        JsonObject? defaults = null;
        try
        {
            if (meta.CreateDefault() is { } made)
                defaults = JsonSerializer.SerializeToNode(made, entry.Type, Writing(engine)) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { }

        var parameters = new List<VocabularyParameter>();
        foreach (var field in meta.Fields)
        {
            if (field.Transient) continue;
            var nested = Of(engine, field, out bool many);
            JsonNode? fallback = defaults == null ? null
                : defaults.FirstOrDefault(p => string.Equals(p.Key, field.JsonName, StringComparison.OrdinalIgnoreCase)).Value?.DeepClone();
            parameters.Add(new VocabularyParameter(field, field.Name == valueField, nested, many, fallback));
        }
        return new VocabularyChoice(vocabulary, entry, parameters);
    }
}

// Where a form's value lives, and how a change to it is one command there.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public interface IVocabularySlot
{
    // "wire 1 of plate: requires", "dialogue sandbox:guard nodes[0].options[0].conditions".
    string Label { get; }
    // The file it is in, as the problems panel groups by ("mount:path"), and the record or placement it is about.
    string File { get; }
    RecordId Record { get; }
    Placement? Placement { get; }
    // What a bare record id inside it means.
    string Namespace { get; }
    // Whether it is still there to edit (the record open, the wire not removed).
    bool IsOpen { get; }

    JsonNode? Read();
    bool Write(JsonNode? value, [NotNullWhen(false)] out string? error);

    // The value or anything about it changed: an edit, an undo, a save.
    event Action? Changed;
}

// What the form shows for one entry: where it is, which vocabulary it is of, what it names (null when
// nothing registered names that), its settings, and what is wrong with it.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed record VocabularyRow(string Path, int Depth, Vocabulary Vocabulary, string Id, VocabularyChoice? Entry,
                                   IReadOnlyList<VocabularySetting> Settings, IReadOnlyList<string> Problems);

// One setting of an entry: the field, the value written (or null, the default), and its path. For a
// setting that holds entries itself, `Children` are their paths (one for a single entry, each item of a list).
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed record VocabularySetting(VocabularyParameter Parameter, string Path, JsonNode? Value)
{
    public bool IsSet => Value != null;

    // The value as the form's text box shows it: the written value, else the default, without JSON quotes.
    public string Text => Show(Value ?? Parameter.Default);

    internal static string Show(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue(out string? s) => s ?? "",
        JsonArray a when a.All(e => e is JsonValue) => string.Join(" ", a.Select(e => Show(e))),
        _ => node.ToJsonString(),
    };
}

// One problem of the value, at its path.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public readonly record struct VocabularyProblem(string Path, string Message)
{
    public override string ToString() => Path.Length > 0 ? $"{Path}: {Message}" : Message;
}

[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class VocabularyForm
{
    private const string Root = "v";
    private readonly Dictionary<string, VocabularyChoice?> _choices = new(StringComparer.OrdinalIgnoreCase);

    public VocabularyForm(Engine engine, IVocabularySlot slot, Vocabulary vocabulary, bool many)
    {
        Engine = engine;
        Slot = slot;
        Vocabulary = vocabulary;
        Many = many;
    }

    public Engine Engine { get; }
    public IVocabularySlot Slot { get; }
    public Vocabulary Vocabulary { get; }
    // True when the value is a list of entries (a `List<ICondition>`), false for one (`ICondition?`).
    public bool Many { get; }
    public string Label => Slot.Label;

    // ---- Opening --------------------------------------------------------------------------------------

    // The field at `path` of an open record (`nodes[0].options[1].conditions`, `place[0].outputs[0].requires`):
    // null, with the reason, when the record's type declares no vocabulary there.
    public static VocabularyForm? ForRecord(RecordDocument record, string path, out string? error)
    {
        error = null;
        if (RecordPath.Parse(path) is not { Count: > 0 })
        {
            error = $"'{path}' is not a path into {record.Type} {record.Id}";
            return null;
        }
        var type = TypeAt(record, path);
        bool many = false;
        var vocabulary = type == null ? null : VocabularyCatalog.Of(record.Engine, type, out many);
        if (vocabulary == null)
        {
            error = type == null
                ? $"{record.Type} declares no field at '{path}'"
                : $"'{path}' is a {Metadata.ShortName(type)}, not a condition, an action or another vocabulary's entry";
            return null;
        }
        return new VocabularyForm(record.Engine, new RecordSlot(record, path), vocabulary, many);
    }

    // The `requires` of wire `index` (from 0) of a placement.
    public static VocabularyForm? ForWire(EditDocument document, Placement placement, int index, out string? error)
    {
        error = null;
        if (index < 0 || index >= placement.Outputs.Count)
        {
            error = $"{AddPlacement.Label(placement)} has no wire {index + 1}";
            return null;
        }
        var vocabulary = VocabularyCatalog.Of(document.Engine, RequiresField, out _)!;
        return new VocabularyForm(document.Engine, new WireSlot(document, placement, index, vocabulary), vocabulary, many: false);
    }

    // The C# type of the value at `path` in a record, walked through the record type's fields: the metadata
    // table stops describing nested objects a few levels down (a dialogue option is four), the types do not.
    internal static Type? TypeAt(RecordDocument record, string path)
    {
        var type = record.Engine.Records.TypeOf(record.Type);
        if (type == null || RecordPath.Parse(path) is not { } parts) return null;
        foreach (var part in parts)
        {
            if (part.Name != null)
            {
                if (Metadata.KindOf(type, out var value) == ValueKind.Map) { type = value!; continue; }
                if (Metadata.Of(type).Field(part.Name) is not { } field) return null;
                type = field.Type;
            }
            else
            {
                if (Metadata.KindOf(type, out var element) != ValueKind.List) return null;
                type = element!;
            }
        }
        return type;
    }

    // Connection.Requires, read from the metadata (so this names no experimental type).
    internal static FieldMetadata RequiresField => Metadata.Of(typeof(Connection)).Field(nameof(Connection.Requires))!;

    // ---- Reading --------------------------------------------------------------------------------------

    // The value in the long form ({ "<key>": "<id>", …settings }), or null when there is none.
    public JsonNode? Value => Normalize(Engine, Slot.IsOpen ? Slot.Read() : null, Vocabulary, Many);

    // The entries the picker offers for the entry at `path` (the root's vocabulary, or a nested one's).
    public IReadOnlyList<VocabularyChoice> Choices(string path = "", string search = "") =>
        VocabularyCatalog.Entries(Engine, VocabularyAt(path) ?? Vocabulary, search);

    // Every entry of the value, depth first, as the form draws them.
    public IReadOnlyList<VocabularyRow> Rows()
    {
        var rows = new List<VocabularyRow>();
        var value = Value;
        if (Many)
        {
            if (value is JsonArray list)
                for (int i = 0; i < list.Count; i++) Walk(list[i], $"[{i}]", 0, Vocabulary, rows);
        }
        else if (value != null) Walk(value, "", 0, Vocabulary, rows);
        return rows;
    }

    // What is wrong with the value: each entry's problems, then what the game's reader refuses.
    public IReadOnlyList<VocabularyProblem> Validate()
    {
        var problems = new List<VocabularyProblem>();
        var value = Value;
        if (Many && value is not (null or JsonArray)) problems.Add(new VocabularyProblem("", $"a list of {Vocabulary.Name}s is a JSON array"));
        foreach (var row in Rows())
            foreach (var problem in row.Problems) problems.Add(new VocabularyProblem(row.Path, problem));
        if (problems.Count == 0 && value != null && Read(Engine, Vocabulary, Many, value, Slot.Namespace, out string? error) == null)
            problems.Add(new VocabularyProblem("", error!));
        return problems;
    }

    // The problems as the problems panel's rows.
    public IReadOnlyList<Problem> Problems() =>
        Validate().Select(p => new Problem(ProblemSeverity.Error, Slot.File, 0, $"{Label}: {p}", Slot.Record,
                                           Slot.Placement?.Id ?? "", Slot.Placement)).ToList();

    // The value as `ed_vocab_show` prints it: one line per entry, indented, with its settings.
    public IReadOnlyList<string> Lines()
    {
        var lines = new List<string> { $"{Label} ({(Many ? "a list of " : "")}{Vocabulary.Name})" };
        var rows = Rows();
        if (rows.Count == 0) lines.Add(Many ? "  (empty: ed_vocab_add . <id>)" : "  (none: ed_vocab_pick . <id>)");
        foreach (var row in rows)
        {
            string settings = string.Join(", ", row.Settings.Where(s => s.IsSet && s.Parameter.Nested == null).Select(s => $"{s.Parameter.Name}={s.Text}"));
            lines.Add($"{new string(' ', 2 + row.Depth * 2)}{(row.Path.Length > 0 ? row.Path : ".")}: {row.Id}{(settings.Length > 0 ? "  " + settings : "")}");
        }
        foreach (var problem in Validate()) lines.Add($"  problem: {problem}");
        return lines;
    }

    // The vocabulary of the entry at `path` ("" is the root's); null when the path names no entry.
    public Vocabulary? VocabularyAt(string path)
    {
        if (path.Length == 0) return Vocabulary;
        var parts = RecordPath.Parse(path);
        if (parts == null) return null;
        var vocabulary = Vocabulary;
        bool list = Many;
        JsonNode? node = Value;
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (list)
            {
                if (part.Name != null) return null;
                list = false;
                node = node is JsonArray a && part.Index < a.Count ? a[part.Index] : null;
                continue;
            }
            if (part.Name == null) return null;
            // A setting of the entry `node` names: it must hold entries itself.
            var parameter = ChoiceOf(node as JsonObject, vocabulary)?.Parameter(part.Name);
            if (parameter?.Nested == null) return null;
            vocabulary = parameter.Nested;
            list = parameter.NestedList;
            node = RecordPath.Get(node, new[] { part });
        }
        return list ? null : vocabulary;
    }

    // ---- Editing ----------------------------------------------------------------------------------------

    // Makes the entry at `path` the `id` entry. The settings the new entry also has are kept.
    public bool Choose(string path, string id, [NotNullWhen(false)] out string? error)
    {
        var vocabulary = VocabularyAt(path);
        if (vocabulary == null) return Fail(out error, Many && path.Length == 0
            ? $"this is a list: choose an item ([0]) or add one (ed_vocab_add . {id})"
            : $"'{path}' is not an entry of {Label}");
        if (VocabularyCatalog.Entry(Engine, vocabulary, id) is not { } choice) return Fail(out error, vocabulary.Unknown(id));

        var root = Wrap();
        var old = RecordPath.Get(root, Full(path)) as JsonObject;
        var made = new JsonObject { [vocabulary.Key] = choice.Id };
        if (old != null)
            foreach (var (name, value) in old)
                if (!string.Equals(name, vocabulary.Key, StringComparison.OrdinalIgnoreCase) && choice.Parameter(name) is { } kept)
                    made[kept.Name] = value?.DeepClone();
        if (!RecordPath.Set(root, Full(path), made, out _, out _)) return Fail(out error, $"'{path}' cannot be set");
        return Commit(root, out error);
    }

    // Adds an `id` entry at the end of the list at `listPath` ("" for a list value, `of` for an `all`).
    public bool Add(string listPath, string id, [NotNullWhen(false)] out string? error)
    {
        var vocabulary = ListVocabulary(listPath);
        if (vocabulary == null) return Fail(out error, $"'{(listPath.Length > 0 ? listPath : ".")}' is not a list of entries in {Label}");
        if (VocabularyCatalog.Entry(Engine, vocabulary, id) is not { } choice) return Fail(out error, vocabulary.Unknown(id));

        var root = Wrap();
        var parts = Full(listPath);
        var list = RecordPath.Get(root, parts) as JsonArray;
        if (list == null)
        {
            list = new JsonArray();
            if (!RecordPath.Set(root, parts, list, out _, out _)) return Fail(out error, $"'{listPath}' cannot be made");
        }
        list.Add(new JsonObject { [vocabulary.Key] = choice.Id });
        return Commit(root, out error);
    }

    // Sets a setting of the entry at `path` from text, read by the field's kind (InspectorValue): a record id
    // checked against the records of its type, a number inside its range, an enum by name. Empty text (or
    // `default`) takes the setting out, so the entry has its default.
    public bool SetParameter(string path, string name, string text, [NotNullWhen(false)] out string? error)
    {
        text = text.Trim();
        if (text.Length == 0 || text == "default") return SetParameter(path, name, (JsonNode?)null, out error);
        if (!Setting(path, name, out var parameter, out error)) return false;
        if (!InspectorValue.TryParse(parameter.Field, text, out var value, out string why, Engine.Records, Slot.Namespace))
            return Fail(out error, $"{parameter.Name}: {why}");
        return SetParameter(path, name, value, out error);
    }

    // Sets a setting to a JSON value; null takes it out — also a setting the entry does not have, which is
    // how the form clears one a person mistyped.
    public bool SetParameter(string path, string name, JsonNode? value, [NotNullWhen(false)] out string? error)
    {
        var root = Wrap();
        var entry = RecordPath.Get(root, Full(path)) as JsonObject;
        string? written = entry?.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Key;
        if (!Setting(path, name, out var parameter, out error))
        {
            if (value != null || entry == null || written == null || VocabularyAt(path) is not { } vocabulary
                || string.Equals(written, vocabulary.Key, StringComparison.OrdinalIgnoreCase)) return false;
            entry.Remove(written);
            return Commit(root, out error);
        }
        if (entry == null) return Fail(out error, $"no entry at '{path}'");
        string? key = entry.FirstOrDefault(p => string.Equals(p.Key, parameter.Name, StringComparison.OrdinalIgnoreCase)).Key;
        if (value == null) entry.Remove(key ?? parameter.Name);
        else entry[key ?? parameter.Name] = value.DeepClone();
        return Commit(root, out error);
    }

    // Takes the entry at `path` out: an item of a list goes, a single entry becomes none.
    public bool Remove(string path, [NotNullWhen(false)] out string? error)
    {
        if (path.Length == 0) return Clear(out error);
        var root = Wrap();
        var parts = Full(path);
        if (!RecordPath.Remove(root, parts)) return Fail(out error, $"nothing at '{path}'");
        return Commit(root, out error);
    }

    // No entry at all (an empty list, for a list).
    public bool Clear([NotNullWhen(false)] out string? error)
    {
        var root = new JsonObject { [Root] = Many ? new JsonArray() : null };
        return Commit(root, out error);
    }

    // ---- How the value is read and written -----------------------------------------------------------

    private bool Setting(string path, string name, [NotNullWhen(true)] out VocabularyParameter? parameter, [NotNullWhen(false)] out string? error)
    {
        parameter = null;
        var vocabulary = VocabularyAt(path);
        var node = RecordPath.Get(Wrap(), Full(path)) as JsonObject;
        if (vocabulary == null || node == null) return Fail(out error, $"no entry at '{(path.Length > 0 ? path : ".")}'");
        if (ChoiceOf(node, vocabulary) is not { } choice) return Fail(out error, $"the entry at '{path}' names no registered {vocabulary.Name}: choose one first");
        parameter = choice.Parameter(name);
        if (parameter == null)
            return Fail(out error, $"{vocabulary.Name} '{choice.Id}' has no setting '{name}'"
                                   + Spelling.Suggest(name, choice.Parameters.Select(p => p.Name))
                                   + $" (it has: {(choice.Parameters.Count == 0 ? "none" : string.Join(", ", choice.Parameters.Select(p => p.Name)))})");
        if (parameter.Nested != null)
            return Fail(out error, $"'{parameter.Name}' holds {parameter.Nested.Name}s: {(parameter.NestedList ? "add one to it" : "choose one at it")} instead");
        error = null;
        return true;
    }

    // The list at `listPath`'s vocabulary, when it is a list of entries.
    private Vocabulary? ListVocabulary(string listPath)
    {
        if (listPath.Length == 0) return Many ? Vocabulary : null;
        var parts = RecordPath.Parse(listPath);
        if (parts is not { Count: > 0 } || parts[^1].Name is not { } name) return null;
        string owner = RecordPath.Format(parts, parts.Count - 1);
        var vocabulary = VocabularyAt(owner);
        if (vocabulary == null) return null;
        var parameter = ChoiceOf(RecordPath.Get(Wrap(), Full(owner)) as JsonObject, vocabulary)?.Parameter(name);
        return parameter is { Nested: { } nested, NestedList: true } ? nested : null;
    }

    private JsonObject Wrap() => new() { [Root] = Value?.DeepClone() };

    private static List<RecordPath.Segment> Full(string path)
    {
        var parts = new List<RecordPath.Segment> { new(Root, 0) };
        parts.AddRange(RecordPath.Parse(path) ?? new List<RecordPath.Segment>());
        return parts;
    }

    private bool Commit(JsonObject root, [NotNullWhen(false)] out string? error)
    {
        if (!Slot.IsOpen) return Fail(out error, $"{Label} is no longer open");
        return Slot.Write(root[Root]?.DeepClone(), out error);
    }

    private static bool Fail(out string? error, string message)
    {
        error = message;
        return false;
    }

    private VocabularyChoice? ChoiceOf(JsonObject? entry, Vocabulary vocabulary)
    {
        if (entry == null || IdOf(entry, vocabulary) is not { } id) return null;
        string key = vocabulary.Name + "\n" + id;
        if (!_choices.TryGetValue(key, out var choice)) _choices[key] = choice = VocabularyCatalog.Entry(Engine, vocabulary, id);
        return choice;
    }

    private static string? IdOf(JsonObject entry, Vocabulary vocabulary)
    {
        foreach (var (name, value) in entry)
            if (string.Equals(name, vocabulary.Key, StringComparison.OrdinalIgnoreCase))
                return value is JsonValue v && v.TryGetValue(out string? text) ? text : null;
        return null;
    }

    private void Walk(JsonNode? node, string path, int depth, Vocabulary vocabulary, List<VocabularyRow> rows)
    {
        var problems = new List<string>();
        var settings = new List<VocabularySetting>();
        if (node is not JsonObject entry)
        {
            problems.Add($"a {vocabulary.Name} is its id, or {{ \"{vocabulary.Key}\": \"<id>\", … }}");
            rows.Add(new VocabularyRow(path, depth, vocabulary, "", null, settings, problems));
            return;
        }
        string id = IdOf(entry, vocabulary) ?? "";
        var choice = ChoiceOf(entry, vocabulary);
        if (id.Length == 0) problems.Add($"no {vocabulary.Name} chosen");
        else if (choice == null) problems.Add(vocabulary.Unknown(id));
        var row = new VocabularyRow(path, depth, vocabulary, choice?.Id ?? id, choice, settings, problems);
        rows.Add(row);
        if (choice == null) return;

        foreach (var (name, _) in entry)
        {
            if (string.Equals(name, vocabulary.Key, StringComparison.OrdinalIgnoreCase) || choice.Parameter(name) != null) continue;
            problems.Add($"{vocabulary.Name} '{choice.Id}' has no setting '{name}'" + Spelling.Suggest(name, choice.Parameters.Select(p => p.Name)));
        }
        foreach (var parameter in choice.Parameters)
        {
            var value = entry.FirstOrDefault(p => string.Equals(p.Key, parameter.Name, StringComparison.OrdinalIgnoreCase)).Value;
            string at = RecordDocument.ChildPath(path, parameter.Name);
            settings.Add(new VocabularySetting(parameter, at, value));
            if (parameter.Nested is { } nested)
            {
                if (parameter.NestedList)
                {
                    if (value is JsonArray items)
                        for (int i = 0; i < items.Count; i++) Walk(items[i], $"{at}[{i}]", depth + 1, nested, rows);
                    else if (value != null) problems.Add($"'{parameter.Name}' is a list of {nested.Name}s");
                }
                else if (value != null) Walk(value, at, depth + 1, nested, rows);
                continue;
            }
            if (value != null && Check(parameter.Field, value, Slot.Namespace) is { } wrong) problems.Add($"{parameter.Name}: {wrong}");
        }
    }

    // What is wrong with one setting's value: a record that does not exist, a number out of range, a name
    // that is not one of the enum's.
    private string? Check(FieldMetadata field, JsonNode value, string ns)
    {
        switch (field.Kind)
        {
            case ValueKind.RecordId when field.RecordType is { Length: > 0 } type && value is JsonValue v && v.TryGetValue(out string? text):
                if (string.IsNullOrWhiteSpace(text)) return null;
                RecordId id;
                try { id = RecordId.Parse(text, ns); }
                catch (FormatException ex) { return ex.Message; }
                return Engine.Records.Exists(type, id) ? null
                    : $"no {type} '{id}'" + Spelling.Suggest(id.ToString(), Engine.Records.Ids(type).Select(i => i.ToString()));
            case ValueKind.List when field.Item is { } item && value is JsonArray items:
                foreach (var element in items)
                    if (element != null && Check(item, element, ns) is { } wrong) return wrong;
                return null;
            case ValueKind.Integer or ValueKind.Number when value is JsonValue n && n.TryGetValue(out double number):
                if (field.Min is { } min && number < min) return $"{number} is below {min}";
                if (field.Max is { } max && number > max) return $"{number} is above {max}";
                return null;
            case ValueKind.Integer or ValueKind.Number:
                return $"{value.ToJsonString()} is not a number";
            case ValueKind.Enum when value is JsonValue e && e.TryGetValue(out string? name):
                return field.EnumValues.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ? null
                    : $"'{name}' is not one of {string.Join(", ", field.EnumValues)}";
            default:
                return null;
        }
    }

    // ---- Between JSON and the game's objects -------------------------------------------------------------

    // The value in the long form: a bare id an object, the shorthand expanded, settings that hold entries
    // read the same way. What it cannot make sense of it leaves as it is, for Validate to say so.
    internal static JsonNode? Normalize(Engine engine, JsonNode? node, Vocabulary vocabulary, bool many)
    {
        if (!many) return NormalizeEntry(engine, node, vocabulary);
        if (node is not JsonArray list) return node?.DeepClone();
        var made = new JsonArray();
        foreach (var item in list) made.Add(NormalizeEntry(engine, item, vocabulary));
        return made;
    }

    private static JsonNode? NormalizeEntry(Engine engine, JsonNode? node, Vocabulary vocabulary)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonValue value when value.TryGetValue(out string? id):
                return new JsonObject { [vocabulary.Key] = id };
            case JsonObject obj:
            {
                JsonObject entry;
                try { entry = vocabulary.Expand(obj) ?? (JsonObject)obj.DeepClone(); }
                catch (JsonException) { return obj.DeepClone(); }
                if (IdOf(entry, vocabulary) == null && vocabulary.Default != null && !entry.Any(p => string.Equals(p.Key, vocabulary.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    var keyed = new JsonObject { [vocabulary.Key] = vocabulary.Default };
                    foreach (var (name, setting) in entry) keyed[name] = setting?.DeepClone();
                    entry = keyed;
                }
                if (IdOf(entry, vocabulary) is { } named && vocabulary.TryFind(named, out var registered))
                {
                    var meta = Metadata.Of(registered.Type);
                    foreach (var (name, setting) in entry.ToArray())
                    {
                        if (meta.Field(name) is not { } field) continue;
                        bool nested = Vocabularies.IsVocabulary(field.Type);
                        bool nestedList = field.Kind == ValueKind.List && field.Item is { } item && Vocabularies.IsVocabulary(item.Type);
                        if (!nested && !nestedList) continue;
                        var inner = nested ? field.Type : field.Item!.Type;
                        entry[name] = Normalize(engine, setting, engine.Vocabularies.Of(inner), nestedList);
                    }
                }
                return entry;
            }
            default:
                return node.DeepClone();
        }
    }

    // Reads `value` as the game reads it (with bare record ids in `ns`): the object, or null with why not.
    internal static object? Read(Engine engine, Vocabulary vocabulary, bool many, JsonNode value, string ns, out string? error)
    {
        error = null;
        var type = many ? typeof(List<>).MakeGenericType(vocabulary.EntryType) : vocabulary.EntryType;
        string? previous = RecordParseContext.Namespace;
        RecordParseContext.Namespace = ns;
        try
        {
            var made = value.Deserialize(type, engine.Records.Json);
            if (made == null) error = $"no {vocabulary.Name}";
            return made;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or KeyNotFoundException)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            RecordParseContext.Namespace = previous;
        }
    }

    // An entry object as JSON in the long form, with the settings that are at their defaults left out: what
    // a wire's `requires` (an object, not JSON) reads as in the form.
    internal static JsonNode? Write(Engine engine, Vocabulary vocabulary, object? entry)
    {
        if (entry == null) return null;
        var node = JsonSerializer.SerializeToNode(entry, vocabulary.EntryType, VocabularyCatalog.Writing(engine));
        return Compact(engine, vocabulary, node);
    }

    private static JsonNode? Compact(Engine engine, Vocabulary vocabulary, JsonNode? node)
    {
        if (node is not JsonObject entry || IdOf(entry, vocabulary) is not { } id || VocabularyCatalog.Entry(engine, vocabulary, id) is not { } choice)
            return node;
        foreach (var (name, value) in entry.ToArray())
        {
            if (choice.Parameter(name) is not { } parameter) continue;
            if (parameter.Nested is { } nested)
            {
                if (value is JsonArray items)
                {
                    if (items.Count == 0) { entry.Remove(name); continue; }
                    for (int i = 0; i < items.Count; i++)
                    {
                        var compacted = Compact(engine, nested, items[i]);
                        if (!ReferenceEquals(compacted, items[i])) items[i] = compacted?.DeepClone();
                    }
                }
                else if (value == null) entry.Remove(name);
                else Compact(engine, nested, value);
                continue;
            }
            if (JsonNode.DeepEquals(value, parameter.Default)) entry.Remove(name);
        }
        return entry;
    }
}

// A form's value in an open record (the record browser's): each edit a SetRecordValue in its history.
internal sealed class RecordSlot : IVocabularySlot
{
    private readonly RecordDocument _record;
    private readonly string _path;

    public RecordSlot(RecordDocument record, string path)
    {
        _record = record;
        _path = path;
    }

    public string Label => $"{_record.Type} {_record.Id} {_path}";
    public string File => _record.Engine.Records.FileOf(_record.Type, _record.Id) ?? $"{_record.Type} {_record.Id}";
    public RecordId Record => _record.Id;
    public Placement? Placement => null;
    public string Namespace => _record.Id.Namespace;
    public bool IsOpen => true;
    public RecordDocument Document => _record;

    public event Action? Changed
    {
        add => _record.Changed += value;
        remove => _record.Changed -= value;
    }

    public JsonNode? Read() => _record.Get(_path);

    public bool Write(JsonNode? value, [NotNullWhen(false)] out string? error)
    {
        error = null;
        // One edit, one step: SetRecordValue merges a drag on one path, which the form's edits are not.
        _record.History.EndMerge();
        if (_record.Set(_path, value)) return true;
        error = $"cannot set '{_path}' in {_record.Type} {_record.Id}";
        return false;
    }
}

// A wire's `requires` in the placements document: each edit a SetOutputs, one undo, the entity re-spawned.
internal sealed class WireSlot : IVocabularySlot
{
    private readonly EditDocument _document;
    private readonly Placement _placement;
    private readonly int _index;
    private readonly Vocabulary _vocabulary;

    public WireSlot(EditDocument document, Placement placement, int index, Vocabulary vocabulary)
    {
        _document = document;
        _placement = placement;
        _index = index;
        _vocabulary = vocabulary;
    }

    public string Label => $"wire {_index + 1} of {AddPlacement.Label(_placement)}: requires";
    public string File
    {
        get
        {
            var writes = _document.Engine.Records.Writes("placements", _document.Id);
            return writes.Count > 0 ? writes[0].File : _document.Path.Length > 0 ? _document.Path : $"{_document.Id} (not saved)";
        }
    }
    public RecordId Record => default;
    public Placement? Placement => _placement;
    public string Namespace => _document.Id.IsEmpty ? EditDocument.GameNamespace(_document.Engine) : _document.Id.Namespace;
    public bool IsOpen => _document.IsOpen && _document.IndexOf(_placement) >= 0 && _index < _placement.Outputs.Count;
    public EditDocument Document => _document;
    public int Index => _index;

    public event Action? Changed
    {
        add => _document.Changed += value;
        remove => _document.Changed -= value;
    }

    public JsonNode? Read() =>
        IsOpen ? VocabularyForm.Write(_document.Engine, _vocabulary, VocabularyForm.RequiresField.Get!(_placement.Outputs[_index])) : null;

    public bool Write(JsonNode? value, [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (!IsOpen) { error = $"{Label} is no longer there"; return false; }
        object? requires = null;
        if (value != null)
        {
            // A wire holds the object the game reads, so a value the reader refuses cannot be written.
            requires = VocabularyForm.Read(_document.Engine, _vocabulary, false, value, Namespace, out error);
            if (requires == null) { error ??= "the game cannot read it"; return false; }
        }
        var wires = EditDocument.CopyOutputs(_placement.Outputs);
        VocabularyForm.RequiresField.Set!(wires[_index], requires);
        _document.History.EndMerge();
        if (_document.Execute(new SetOutputs(_document, _placement, wires))) return true;
        error = "no document is open";
        return false;
    }
}
