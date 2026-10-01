#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Sage.Core;

// The one data-record pipeline for every definition (docs/design/05 §3.5): items, spells, materials,
// input maps, spawns... Record files are `data/**/*.json` in any mount, each an array of records
// (or a single record):
//
//   { "type": "spawn", "id": "bunny", "base": "creature_base", "model": "stanford_bunny", ... }
//   { "type": "spawn", "id": "sandbox:bunny", "patch": true, "tags+": ["boss"], "position": [0,1,0] }
//
// Load order = mount order; within one mount, definitions load before patches (file names don't
// matter). The first definition of an id creates the record; a later one with "patch": true merges FIELD BY FIELD (scalars replace, objects merge recursively, lists replace unless
// patched with "field+": [...] / "field-": [...]). A later non-patch redefinition is an error (it would
// silently wipe the earlier one — Bethesda's "rule of one"); it's logged with both files and applied
// as a patch. A bare id in a field means the namespace of the file it is written in, so a game's patch
// of an engine record that says "sound": "hit_flesh" means the game's hit_flesh (R11). "disabled": true in a patch removes the record. "base" inherits from another record of
// the same type, resolved after merging. "abstract": true marks a template: usable as a base, never
// built into a record itself (and not inherited).
//
// v1 deserializes with System.Text.Json reflection; the compile-time generated readers/validators
// (docs/design/09) replace that later with the same rules.
public sealed class RecordStore
{
    // "$schema" is the editor's (a JSON Schema for autocomplete, REDESIGN §4.2), not the record's.
    private static readonly HashSet<string> MetaKeys = new(StringComparer.Ordinal) { "type", "id", "base", "patch", "disabled", "abstract", "$schema" };

    private sealed class RawRecord
    {
        public required RecordId Id;
        public required string Type;
        public required JsonObject Fields;          // merged, meta keys removed except "base"
        public required string DefinedIn;
        public required RecordSource Source;         // where the defining record is written

        // Every write to the record, in load order (4j-2): which file and mount set, added to, removed
        // from or disabled what, at the deepest path the file named. What rec_get, the content report
        // and errors' file:line all read. Compact on purpose: one small struct per top-level field a
        // definition writes and per leaf a patch writes; paths are only built for nested writes.
        public readonly List<FieldWrite> Writes = new();

        // The record this one inherits from, once bases are resolved: its writes are this record's
        // too, "via base", for the fields this record does not write itself.
        public RawRecord? Base;

        // The record (file and position) that last wrote top-level `field`, here or in a base; null
        // when none did.
        public RecordSource? SourceOf(string field)
        {
            for (int i = Writes.Count - 1; i >= 0; i--)
                if (Writes[i].Op != RecordWriteOp.Disable && FieldWrite.IsUnder(Writes[i].Path, field)) return Writes[i].Source;
            return Base?.SourceOf(field);
        }

        // "game:data/items.json" for the file that last wrote top-level `field`, with "(via base …)"
        // when it was inherited; null when nothing wrote it.
        public string? OriginOf(string field)
        {
            for (int i = Writes.Count - 1; i >= 0; i--)
                if (Writes[i].Op != RecordWriteOp.Disable && FieldWrite.IsUnder(Writes[i].Path, field)) return Writes[i].Source.File.Name;
            return Base?.OriginOf(field) is { } inherited ? $"{inherited} (via base {Base.Id})" : null;
        }

        // Where to point a person at `path` ("$.tasks[1]", or a field name): the file that set that
        // field, at the deepest part of the path that file wrote; the record itself when no file did.
        public string At(string? path)
        {
            string? field = JsonSource.FieldOf(path);
            return field != null && SourceOf(field) is { } source ? source.At(path) : Source.At();
        }
        public bool Disabled;
        public bool Abstract;
    }

    private readonly Dictionary<string, Type> _typesByName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _namesByType = new();
    private readonly Dictionary<(string Type, RecordId Id), object> _records = new();

    // Records made at run time rather than read from a file: a custom spell the player composed
    // (F21's spellmaker), and later a quest generated for them. They are kept apart because a
    // content reload rebuilds `_records` from disk, and a player's own spell must survive that — it
    // did not come from disk and there is nothing on disk to rebuild it from. The *data* behind them
    // lives in the save (09 §3.1), so this is a cache, not a store of record.
    private readonly Dictionary<(string Type, RecordId Id), object> _runtime = new();
    private readonly Dictionary<(string Type, RecordId Id), RawRecord> _raw = new();

    // What the last load did that left no record behind, for the content report (4j-2): records a
    // patch disabled (their writes), redefinitions, and patches of records that were never defined.
    private readonly Dictionary<(string Type, RecordId Id), RawRecord> _disabled = new();
    private readonly List<(string Type, RecordId Id, RecordSource Source, RecordSource Existing)> _redefinitions = new();
    private readonly List<(string Type, RecordId Id, RecordSource Source)> _skippedPatches = new();
    private readonly JsonSerializerOptions _json;
    private VirtualFileSystem? _vfs;

    public RecordStore()
    {
        _json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            IncludeFields = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            // A field the type does not have is an error (issue #22): `"light": {"color": …}` used to be
            // dropped without a word because the field is `Colour`. The load reports every such field
            // first, with the nearest real one (JsonMembers); this is the backstop for anything read
            // with these options that the load did not look at — a prefab body read at spawn.
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            // Explicit, because the load asks it for types' contracts (GetTypeInfo) and the .NET 8
            // runtime will not supply a default one (the save bug CI's comment describes).
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            Converters = { new Vector2JsonConverter(), new Vector3JsonConverter(), new QuaternionJsonConverter(), new JsonStringEnumConverter() },
            // The ECS's Entity converter (EntityJsonConverter) is inserted by the Engine, which owns the ECS.
        };
    }

    // How Sage reads JSON: one dialect, so a prefab's component fields parse exactly like a record's.
    public JsonSerializerOptions Json => _json;

    public int Count => _records.Count;
    public int ErrorCount { get; private set; }
    public int WarningCount { get; private set; }

    // Whether an asset a record names that is in no mount is an error (`sage validate`) or a warning
    // (a dev build's load). Shipping does not look: the game has shipped with what it has.
    public bool MissingAssetsAreErrors { get; set; }

    // Raised on the main thread after Load/Reload. Records that still exist keep the same instances
    // (updated in place), so references held by systems stay valid.
    public event Action? Reloaded;

    // ---- Schemas ----------------------------------------------------------------------------------

    // Closed by the first Load: a type registered after that had its records skipped as unknown.
    public RegistrationSeal TypeSeal { get; } = new("record type", "every record of that type in the content was skipped as unknown");

    // Who registered each record type (issue #12); set by the Engine.
    public RegistrationLedger? Ledger { get; set; }

    // Modules register their record types in Init (docs/design/01 §5.1), before records load.
    public void Register<T>() where T : class, new()
    {
        var attr = typeof(T).GetCustomAttribute<RecordAttribute>()
                   ?? throw new InvalidOperationException($"{typeof(T).Name} has no [Record(\"type\")] attribute.");
        TypeSeal.Check(attr.Type);
        if (_typesByName.TryGetValue(attr.Type, out var existing) && existing != typeof(T))
            throw new InvalidOperationException($"Record type '{attr.Type}' is already registered by {existing.Name}.");
        _typesByName[attr.Type] = typeof(T);
        _namesByType[typeof(T)] = attr.Type;
        Ledger?.Record("record type", attr.Type);
    }

    public IEnumerable<string> TypeNames => _typesByName.Keys.OrderBy(n => n, StringComparer.Ordinal);

    // The C# type registered for a record type name, or null (the metadata table is keyed by it).
    public Type? TypeOf(string typeName) => _typesByName.TryGetValue(typeName, out var type) ? type : null;

    // ---- Lookup -----------------------------------------------------------------------------------

    public bool TryGet<T>(RecordId id, out T record) where T : class
    {
        if (_records.TryGetValue((TypeName<T>(), id), out var r)) { record = (T)r; return true; }
        record = null!;
        return false;
    }

    public bool TryGet<T>(RecordRef<T> id, out T record) where T : class => TryGet(id.Id, out record);

    // A record that must be there. Missing is a bug, and a dev build stops on it (issue #22): the
    // default-constructed placeholder it used to hand back is how a typo became a zero-damage sword.
    // The placeholder is kept only as Shipping's safety net, and said loudly once per id there, so a
    // player's game carries on (05 §8). Code that can do without a record asks TryGet.
    public T Get<T>(RecordId id) where T : class, new()
    {
        if (TryGet(id, out T record)) return record;
        string type = TypeName<T>();
        string message = $"no {type} record {id}" + Spelling.Suggest(id.ToString(), Ids(type).Select(i => i.ToString()));
        if (BuildInfo.IsDevBuild) throw new KeyNotFoundException(message);
        Log.Once(LogCat.Records, LogLevel.Error, $"missing:{type}:{id}", $"{message}; using a blank one (Shipping carries on)");
        return new T();
    }

    public T Get<T>(RecordRef<T> id) where T : class, new() => Get<T>(id.Id);

    public IReadOnlyList<T> All<T>() where T : class
    {
        string type = TypeName<T>();
        return _records.Where(kv => kv.Key.Type == type).OrderBy(kv => kv.Key.Id.ToString(), StringComparer.Ordinal)
                       .Select(kv => (T)kv.Value).ToList();
    }

    // The records of a type as the content now stands: while a load's checks run (AddCheck), the ones
    // being loaded, which `All` does not have yet; otherwise the loaded ones, as `All`. For a check of
    // one type that depends on another's records — a wire naming an input only a state machine
    // listens for (issue #92) — whichever order the types are checked in.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // state machines (#92): may change before 1.0
    public IReadOnlyList<T> Latest<T>() where T : class
    {
        if (_checking == null) return All<T>();
        string type = TypeName<T>();
        return _checking.Where(kv => kv.Key.Item1 == type).OrderBy(kv => kv.Key.Item2.ToString(), StringComparer.Ordinal)
                        .Select(kv => (T)kv.Value).ToList();
    }

    public IEnumerable<RecordId> Ids(string type) =>
        _records.Keys.Where(k => k.Type == type).Select(k => k.Id).OrderBy(i => i.ToString(), StringComparer.Ordinal);

    // The templates of a type: records marked "abstract", loaded but never built, so Ids leaves them
    // out. What "base" may also name (a JSON Schema's enum of bases, issue #21).
    public IEnumerable<RecordId> AbstractIds(string type) =>
        _raw.Where(kv => kv.Key.Item1 == type && kv.Value.Abstract).Select(kv => kv.Key.Item2).OrderBy(i => i.ToString(), StringComparer.Ordinal);

    public bool Exists(RecordId id) => _records.Keys.Any(k => k.Id == id);

    public bool Exists(string type, RecordId id) => _records.ContainsKey((type, id));

    // The record type name of a registered record class ("item" for ItemRecord); null if unregistered.
    public string? TypeNameOf(Type type) => _namesByType.TryGetValue(type, out var n) ? n : null;

    private string TypeName<T>() =>
        _namesByType.TryGetValue(typeof(T), out var n) ? n : throw new InvalidOperationException($"{typeof(T).Name} is not a registered record type.");

    // ---- Loading ------------------------------------------------------------------------------------

    public void Load(VirtualFileSystem vfs)
    {
        TypeSeal.Seal("records were loaded");
        _vfs = vfs;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ErrorCount = 0;
        WarningCount = 0;
        _redefinitions.Clear();
        _skippedPatches.Clear();
        _disabled.Clear();
        var raw = ReadAndMerge(vfs);
        ResolveBases(raw);
        var built = Build(raw);
        ValidateReferences(built, raw);
        RunChecks(built, raw);

        // Keep existing instances (hot reload): copy new values into them, add new, drop removed.
        var result = new Dictionary<(string, RecordId), object>();
        foreach (var (key, record) in built)
        {
            if (_records.TryGetValue(key, out var existing) && existing.GetType() == record.GetType())
            {
                CopyInto(record, existing);
                result[key] = existing;
            }
            else result[key] = record;
        }
        _records.Clear();
        foreach (var kv in result) _records[kv.Key] = kv.Value;
        foreach (var kv in _runtime) _records[kv.Key] = kv.Value;   // re-applied over the fresh set
        _raw.Clear();
        foreach (var kv in raw) _raw[kv.Key] = kv.Value;

        Log.Info(LogCat.Records, $"Loaded {_records.Count} records of {_records.Keys.Select(k => k.Type).Distinct().Count()} types in {watch.ElapsedMilliseconds} ms" +
                                  (ErrorCount > 0 ? $" ({ErrorCount} errors, see above)" : ""));
        Reloaded?.Invoke();
    }

    // Adds or replaces a record that was made rather than loaded. Survives `Reload`, which is the
    // whole point: a player's custom spell is not content and cannot be rebuilt from a file.
    public void AddRuntime<T>(RecordId id, T record) where T : class
    {
        string type = TypeNameOf<T>();
        if (type.Length == 0) return;
        _runtime[(type, id)] = record;
        _records[(type, id)] = record;
    }

    public bool RemoveRuntime<T>(RecordId id) where T : class
    {
        string type = TypeNameOf<T>();
        // Only what was added at run time: a file could define the same id, and "remove my spell"
        // must never take a content record with it.
        if (type.Length == 0 || !_runtime.Remove((type, id))) return false;
        _records.Remove((type, id));
        return true;
    }

    // Everything added at run time, so whatever owns the data behind it can withdraw what it no longer
    // accounts for — a load replacing a spellbook, for one (09 §3.1).
    public IEnumerable<(string Type, RecordId Id, object Record)> RuntimeRecords =>
        _runtime.Select(kv => (kv.Key.Type, kv.Key.Id, kv.Value));

    // `TypeName<T>` for a caller that must not be thrown at: a record made at run time is made while
    // the game is running, and a misregistered type is a bug to report, not a reason to stop play.
    private string TypeNameOf<T>()
    {
        if (_namesByType.TryGetValue(typeof(T), out string? name)) return name;
        Assert.Ensure(false, $"{typeof(T).Name} is not a registered record type; register it in a module's Init");
        return "";
    }

    public void Reload()
    {
        if (_vfs == null) { Log.Warn(LogCat.Records, "Reload before Load: nothing to reload"); return; }
        Load(_vfs);
    }

    private Dictionary<(string, RecordId), RawRecord> ReadAndMerge(VirtualFileSystem vfs)
    {
        var raw = new Dictionary<(string, RecordId), RawRecord>();
        var warnedTypes = new HashSet<string>();
        var entries = new List<(int Mount, bool IsPatch, RecordSource Source, string Namespace, string Type, RecordId Id, JsonObject Obj)>();
        var mountIndex = vfs.Mounts.Select((m, i) => (m, i)).ToDictionary(x => x.m, x => x.i);

        foreach (var (path, mount) in vfs.Enumerate(VirtualPath.Parse("data"), "*.json"))
        {
            string name = $"{mount.Name}:{path}";
            JsonSource file;
            JsonNode? root;
            try
            {
                using (var stream = mount.Open(path))
                using (var bytes = new MemoryStream())
                {
                    stream.CopyTo(bytes);
                    file = new JsonSource(name, bytes.ToArray(), mount);
                }
            }
            catch (IOException ex)
            {
                Error($"{name}: cannot read: {ex.Message}");
                continue;
            }
            try
            {
                root = JsonNode.Parse(file.Utf8, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (JsonException ex)
            {
                string where = ex.LineNumber is long line ? file.AtLine(line, ex.BytePositionInLine ?? 0) : name;
                Error($"{where}: invalid JSON: {WithoutPosition(ex.Message)}");
                continue;
            }

            var items = root is JsonArray array ? array.ToList() : new List<JsonNode?> { root };
            for (int index = 0; index < items.Count; index++)
            {
                var source = new RecordSource(file, index);
                if (items[index] is not JsonObject obj) { Error($"{source.At()}: a record must be a JSON object"); continue; }
                string? type = (string?)obj["type"];
                string? idText = (string?)obj["id"];
                if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(idText)) { Error($"{source.At()}: record needs \"type\" and \"id\""); continue; }
                if (!_typesByName.ContainsKey(type))
                {
                    if (warnedTypes.Add(type)) Warn(LogCat.Records, $"{source.At("type")}: unknown record type '{type}' (no module registered it); skipped");
                    continue;
                }

                RecordId id;
                try { id = RecordId.Parse(idText, mount.RecordNamespace); }
                catch (FormatException ex) { Error($"{source.At("id")}: {ex.Message}"); continue; }

                entries.Add((mountIndex[mount], (bool?)obj["patch"] ?? false, source, mount.RecordNamespace, type, id, obj));
            }
        }

        // Mount order decides; within one mount, every definition comes before any patch, so a patch
        // doesn't depend on its file's name sorting after the definition's (OrderBy is stable).
        foreach (var (_, isPatch, source, ns, type, id, obj) in entries.OrderBy(e => e.Mount).ThenBy(e => e.IsPatch))
        {
            bool disabled = (bool?)obj["disabled"] ?? false;
            var key = (type, id);
            var fields = StripMeta(obj);
            string file = source.File.Name;

            // A bare id means the namespace of the file it is written in (R11), which is not the
            // record's when a game patches an engine record: "sound": "hit_flesh" in the game's patch
            // of sage:physical is the game's hit_flesh. Such ids are written out in full here, before
            // the merge, because afterwards nothing knows which file a value came from.
            bool foreign = !string.Equals(ns, id.Namespace, StringComparison.OrdinalIgnoreCase);
            if (foreign) fields = QualifyFields(fields, type, ns);

            if (!raw.TryGetValue(key, out var existing))
            {
                if (isPatch)
                {
                    Warn(LogCat.Records, $"{source.At()}: patch for {type} {id}, which isn't defined (yet) in load order; skipped");
                    _skippedPatches.Add((type, id, source));
                    continue;
                }
                var record = new RawRecord { Id = id, Type = type, Fields = new JsonObject(), DefinedIn = file, Source = source };
                MergeInto(record.Fields, fields, record.Writes, source, null, define: true);
                record.Disabled = disabled;
                if (disabled) record.Writes.Add(new FieldWrite("", source, RecordWriteOp.Disable));
                record.Abstract = (bool?)obj["abstract"] ?? false;
                raw[key] = record;
                continue;
            }

            if (!isPatch)
            {
                Error($"{source.At()}: {type} {id} is already defined at {existing.Source.At()}; use \"patch\": true to change it. Treated as a patch.");
                _redefinitions.Add((type, id, source, existing.Source));
            }
            // The record's own bare ids are written out too, in its own namespace, so that a list the
            // patch adds to or removes from compares like with like ("tags-": ["sage:x"] against "x").
            if (foreign) existing.Fields = QualifyFields(existing.Fields, type, id.Namespace);
            MergeInto(existing.Fields, fields, existing.Writes, source, null, define: false);
            if (obj.ContainsKey("disabled"))
            {
                existing.Disabled = disabled;
                // Disabling is a write of the whole record; "disabled": false puts it back, which is a
                // write of that flag.
                existing.Writes.Add(new FieldWrite(disabled ? "" : "disabled", source, disabled ? RecordWriteOp.Disable : RecordWriteOp.Set));
            }
            if (obj.ContainsKey("abstract")) existing.Abstract = (bool?)obj["abstract"] ?? false;
            Log.Debug(LogCat.Records, $"{file} patches {type} {id}");
        }

        foreach (var key in raw.Where(kv => kv.Value.Disabled).Select(kv => kv.Key).ToList())
        {
            Log.Debug(LogCat.Records, $"{key.Item1} {key.Item2} disabled by a patch");
            _disabled[key] = raw[key];
            raw.Remove(key);
        }
        return raw;
    }

    // System.Text.Json ends its messages with where it was in the text it read ("Path: $.x |
    // LineNumber: 0 | BytePositionInLine: 12."). For a record that is the merged copy, not the file, so
    // the numbers would mislead: the caller says where instead.
    private static string WithoutPosition(string message)
    {
        int at = message.IndexOf(" Path: ", StringComparison.Ordinal);
        if (at < 0) at = message.IndexOf(" LineNumber: ", StringComparison.Ordinal);
        return at < 0 ? message : message[..at];
    }

    // `Qualify` for a record's top-level fields, which may also carry "base" and the "field+" /
    // "field-" list operations of a patch.
    private JsonObject QualifyFields(JsonObject fields, string type, string ns)
    {
        var qualified = (JsonObject)Qualify(fields, _typesByName[type], ns)!;
        if (qualified["base"] is JsonValue baseValue && baseValue.GetValueKind() == JsonValueKind.String &&
            (string?)baseValue is { Length: > 0 } baseText && !baseText.Contains(':'))
            qualified["base"] = $"{ns}:{baseText.Trim()}";
        return qualified;
    }

    private static JsonObject StripMeta(JsonObject obj)
    {
        var copy = new JsonObject();
        foreach (var (k, v) in obj)
            if (!MetaKeys.Contains(k) || k == "base")
                copy[k] = v?.DeepClone();
        return copy;
    }

    // Per-field merge (05 §3.5). `writes`, when given, gets one entry per write (4j-2): for a
    // definition (`define`) one per top-level field; for a patch one at the deepest path it names, so a
    // patch of `"stats": { "agility": 5 }` is a set of `stats.agility` and `"parts": { "inv": { "items+":
    // […] } }` an add to `parts.inv.items`. `prefix` is the path of `target` inside the record.
    private static void MergeInto(JsonObject target, JsonObject patch, List<FieldWrite>? writes, RecordSource source, string? prefix, bool define)
    {
        foreach (var (key, value) in patch)
        {
            string field = key;
            RecordWriteOp op;
            if (key.EndsWith('+') && key.Length > 1)
            {
                field = key[..^1];
                op = RecordWriteOp.Add;
                var list = target[field] as JsonArray ?? new JsonArray();
                if (value is JsonArray add) foreach (var item in add) list.Add(item?.DeepClone());
                target[field] = list;
            }
            else if (key.EndsWith('-') && key.Length > 1)
            {
                field = key[..^1];
                op = RecordWriteOp.Remove;
                if (target[field] is JsonArray list && value is JsonArray remove)
                {
                    foreach (var r in remove)
                        for (int i = list.Count - 1; i >= 0; i--)
                            if (JsonNode.DeepEquals(list[i], r)) list.RemoveAt(i);
                }
            }
            else if (value is JsonObject childPatch && target[key] is JsonObject childTarget)
            {
                MergeInto(childTarget, childPatch, writes, source, writes == null ? null : prefix == null ? key : $"{prefix}.{key}", define);
                continue;
            }
            else
            {
                op = define ? RecordWriteOp.Define : RecordWriteOp.Set;
                target[key] = value?.DeepClone();
            }

            writes?.Add(new FieldWrite(prefix == null ? field : $"{prefix}.{field}", source, op));
        }
    }

    private void ResolveBases(Dictionary<(string, RecordId), RawRecord> raw)
    {
        var resolved = new HashSet<(string, RecordId)>();
        foreach (var key in raw.Keys.ToList())
            Resolve(key, new HashSet<(string, RecordId)>());

        JsonObject? Resolve((string Type, RecordId Id) key, HashSet<(string, RecordId)> chain)
        {
            if (!raw.TryGetValue(key, out var record)) return null;
            if (resolved.Contains(key)) return record.Fields;
            if (!chain.Add(key))
            {
                Error($"{record.At("base")}: {key.Type} {key.Id}: \"base\" cycle ({string.Join(" -> ", chain.Select(c => c.Item2))})");
                return null;
            }
            // "base" is reserved for inheritance (05 §3.5): a record that wants a field of its own by
            // that name has to call it something else, and a non-string value is a clear error rather
            // than a crash.
            string? baseText = null;
            if (record.Fields["base"] is JsonNode baseNode)
            {
                if (baseNode.GetValueKind() == System.Text.Json.JsonValueKind.String) baseText = (string?)baseNode;
                else Error($"{record.At("base")}: {key.Type} {key.Id}: \"base\" must be the id of another record, not {baseNode.GetValueKind()} " +
                           "(\"base\" is reserved for inheritance; rename the field)");
            }
            if (baseText is { Length: > 0 })
            {
                RecordId baseId;
                try { baseId = RecordId.Parse(baseText, record.Id.Namespace); }
                catch (FormatException ex) { Error($"{record.At("base")}: {ex.Message}"); baseId = default; }
                var baseFields = baseId.IsEmpty ? null : Resolve((key.Type, baseId), chain);
                if (baseFields == null && !baseId.IsEmpty)
                    Error($"{record.At("base")}: {key.Type} {key.Id}: base {baseId} not found (or broken)");
                else if (baseFields != null)
                {
                    // A base in another namespace wrote its ids in its own terms: "physical" in an
                    // engine record means sage:physical even when a game's record inherits it. The
                    // merged JSON is deserialized in the *child's* namespace, so those ids are
                    // qualified here first (review #56). Only fields the record type declares as
                    // RecordId are touched — paths, labels and clip names are left alone.
                    var merged = baseId.Namespace == record.Id.Namespace || !_typesByName.TryGetValue(key.Type, out var baseClr)
                        ? (JsonObject)baseFields.DeepClone()
                        : (JsonObject)Qualify(baseFields, baseClr, baseId.Namespace)!;
                    merged.Remove("base");
                    var own = (JsonObject)record.Fields.DeepClone();
                    own.Remove("base");
                    MergeInto(merged, own, null, record.Source, null, define: false);
                    record.Fields.Clear();
                    foreach (var (k, v) in merged) record.Fields[k] = v?.DeepClone();
                    record.Base = raw[(key.Type, baseId)];
                }
            }
            record.Fields.Remove("base");
            resolved.Add(key);
            chain.Remove(key);
            return record.Fields;
        }
    }

    private Dictionary<(string, RecordId), object> Build(Dictionary<(string, RecordId), RawRecord> raw)
    {
        var built = new Dictionary<(string, RecordId), object>();
        foreach (var (key, record) in raw)
        {
            if (record.Abstract) continue;   // templates: bases only
            var clr = _typesByName[record.Type];
            // Every field the type hasn't got, at any depth, each where it is written (issue #22). A
            // record with one is skipped like any other that doesn't read: its author meant something
            // by that field, and a record built without it is not the record they wrote.
            var unknown = JsonMembers.Find(record.Fields, clr, _json);
            foreach (var field in unknown)
                Error($"{record.At(field.Path)}: {record.Type} {record.Id}: {field.Message} (record skipped)");
            if (unknown.Count > 0) continue;

            RecordParseContext.Namespace = record.Id.Namespace;
            try
            {
                var value = record.Fields.Deserialize(clr, _json);
                if (value != null) built[key] = value;
            }
            catch (JsonException ex)
            {
                // The path is into the merged record; `At` turns it back into the file that wrote it.
                Error($"{record.At(ex.Path)}: {record.Type} {record.Id}: {WithoutPosition(ex.Message)} (record skipped)");
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or NotSupportedException)
            {
                Error($"{record.Source.At()}: {record.Type} {record.Id}: {WithoutPosition(ex.Message)} (record skipped)");
            }
            finally
            {
                RecordParseContext.Namespace = null;
            }
        }
        return built;
    }

    // References to other records must exist (05 §3.5), and a RecordRef<T> must name a record of *its*
    // type (issue #22); asset paths must name a file some mount has. At any depth: a list of objects
    // holding ids is checked like a field holding one.
    private void ValidateReferences(Dictionary<(string, RecordId), object> built, Dictionary<(string, RecordId), RawRecord> raw)
    {
        var ids = built.Keys.Select(k => k.Item2).ToHashSet();
        foreach (var (key, record) in built)
            CheckValues(record, "", built, ids, path => raw[key].At(path), $"{key.Item1} {key.Item2}");
    }

    // The references and asset paths inside `value`, which was read from content: each problem is
    // reported at `at(path)` and prefixed with `what` (the record).
    private void CheckValues(object? value, string prefix, Dictionary<(string, RecordId), object> built, HashSet<RecordId> ids,
                             Func<string, string> at, string what)
    {
        foreach (var (path, found, kind) in ContentValues.Find(value, _json, prefix))
        {
            string field = JsonMembers.Display(path);
            switch (found)
            {
                case IRecordRef reference when !reference.Id.IsEmpty:
                {
                    // A reference to a type this app has not registered (a client record in a headless
                    // server) cannot be checked, and is not wrong for being unreadable here.
                    string? type = TypeNameOf(reference.Target);
                    if (type == null || built.ContainsKey((type, reference.Id))) break;
                    var other = built.Keys.Where(k => k.Item2 == reference.Id).Select(k => k.Item1).OrderBy(t => t, StringComparer.Ordinal).ToList();
                    Error($"{at(path)}: {what}: '{field}' refers to {type} {reference.Id}, which doesn't exist" +
                          (other.Count > 0 ? $" ({reference.Id} is {Article(other[0])} {string.Join(" and ", other)})"
                                           : Spelling.Suggest(reference.Id.ToString(), built.Keys.Where(k => k.Item1 == type).Select(k => k.Item2.ToString()))));
                    break;
                }
                // A plain id whose `[RecordRef]` names a type this app has not registered (a mesh
                // renderer's material in a headless server) can't be checked either.
                case RecordId id when !id.IsEmpty && !ids.Contains(id) && (kind == null || _namesByType.ContainsValue(kind)):
                    Error($"{at(path)}: {what}: '{field}' refers to {id}, which doesn't exist");
                    break;
                case AssetPath asset when !asset.IsEmpty:
                    CheckAsset(asset, at(path), what, field);
                    break;
            }
        }
    }

    private static string Article(string word) => "aeiou".Contains(char.ToLowerInvariant(word[0])) ? "an" : "a";

    private void CheckAsset(AssetPath asset, string where, string what, string field)
    {
        if (_vfs == null || (!BuildInfo.IsDevBuild && !MissingAssetsAreErrors)) return;
        if (AssetChecks.Exists(_vfs, asset.Path)) return;
        string message = $"{where}: {what}: '{field}' names {asset}, which is in no mount";
        if (MissingAssetsAreErrors) Error(message);
        // A compiled shader missing from a build made without them (SageSkipShaders) is the shader
        // pipeline's news, and already said in that category.
        else Warn(AssetChecks.IsCompiledShader(asset.Path) ? LogCat.Shaders : LogCat.Records, message);
    }

    // ---- Checks a plugin adds ---------------------------------------------------------------------

    private readonly Dictionary<Type, List<Action<object, RecordCheck>>> _checks = new();
    private Dictionary<(string, RecordId), object>? _checking;
    private HashSet<RecordId>? _checkingIds;

    // A check for one record type, run at every load once all records are built (issue #22): what a
    // record means beyond its fields' types — a prefab's components and parts, an AI schedule's task
    // names — found at load, at its line, rather than when something first uses it. Added in a
    // plugin's Init, before content loads. A check reports through `RecordCheck`; it never skips the
    // record (a prefab with one bad part still spawns without it).
    public void AddCheck<T>(Action<T, RecordCheck> check) where T : class
    {
        if (!_checks.TryGetValue(typeof(T), out var list)) _checks[typeof(T)] = list = new();
        list.Add((record, context) => check((T)record, context));
    }

    private void RunChecks(Dictionary<(string, RecordId), object> built, Dictionary<(string, RecordId), RawRecord> raw)
    {
        if (_checks.Count == 0) return;
        _checking = built;
        _checkingIds = built.Keys.Select(k => k.Item2).ToHashSet();
        try
        {
            foreach (var (key, record) in built.OrderBy(kv => kv.Key.Item1, StringComparer.Ordinal).ThenBy(kv => kv.Key.Item2.ToString(), StringComparer.Ordinal))
            {
                if (!_checks.TryGetValue(record.GetType(), out var checks)) continue;
                var context = new RecordCheck(this, key.Item1, key.Item2, raw[key].At);
                foreach (var check in checks)
                {
                    string? outer = RecordParseContext.Namespace;
                    RecordParseContext.Namespace = key.Item2.Namespace;
                    try { check(record, context); }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        Error($"{raw[key].Source.At()}: {key.Item1} {key.Item2}: a check failed: {ex.Message}");
                    }
                    finally { RecordParseContext.Namespace = outer; }
                }
            }
        }
        finally
        {
            _checking = null;
            _checkingIds = null;
        }
    }

    // For RecordCheck: while checks run, "exists" means in the set being loaded.
    internal bool ExistsWhileChecking(string? type, RecordId id) =>
        type == null ? (_checkingIds?.Contains(id) ?? Exists(id))
                     : (_checking?.ContainsKey((type, id)) ?? Exists(type, id));

    internal bool TryGetWhileChecking<T>(RecordId id, out T? record) where T : class
    {
        record = null;
        if (!_namesByType.TryGetValue(typeof(T), out var type)) return false;
        object? found = null;
        if (_checking != null) _checking.TryGetValue((type, id), out found);
        else _records.TryGetValue((type, id), out found);
        record = found as T;
        return record != null;
    }

    internal void CheckValuesWhileChecking(object? value, string path, Func<string, string> at, string what) =>
        CheckValues(value, path, _checking ?? _records, _checkingIds ?? _records.Keys.Select(k => k.Id).ToHashSet(), at, what);

    internal void ReportError(string message) => Error(message);
    internal void ReportWarning(string message) => Warn(LogCat.Records, message);

    // Rewrites every bare RecordId inside `node` to `ns:id`, guided by the record type's own fields so
    // that only ids are affected. Runs for an inherited record whose base is in another namespace, and
    // for a record written in a file of another namespace (a patch, R11), so it builds fresh nodes
    // rather than trying to re-parent the originals.
    private JsonNode? Qualify(JsonNode? node, Type type, string ns)
    {
        if (node == null) return null;

        if (IsReference(type))
        {
            if (node.GetValueKind() != JsonValueKind.String) return node.DeepClone();
            string? text = (string?)node;
            return string.IsNullOrWhiteSpace(text) || text.Contains(':') ? node.DeepClone() : JsonValue.Create($"{ns}:{text.Trim()}");
        }

        // A vocabulary entry (issue #28) is qualified as the type its key names; one in shorthand
        // (issue #89) as its long form, so the value its id carries is qualified as the field it fills.
        if (node is JsonObject && PolymorphicShorthand?.Invoke(type, node) is JsonObject expanded) node = expanded;
        if (node is JsonObject && PolymorphicTypes?.Invoke(type, node) is { } concrete) type = concrete;

        if (node is JsonArray array)
        {
            var element = ElementType(type);
            if (element == null) return node.DeepClone();
            var result = new JsonArray();
            foreach (var item in array) result.Add(Qualify(item, element, ns));
            return result;
        }

        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            var valueType = DictionaryValueType(type);
            foreach (var (name, value) in obj)
            {
                // "spells+" / "spells-" in a patch are the spells field too.
                string memberName = name.Length > 1 && (name[^1] == '+' || name[^1] == '-') ? name[..^1] : name;
                var member = valueType != null ? null
                    : SettableMembers(type).FirstOrDefault(m => string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase));
                var memberType = valueType ?? (member == null ? null : MemberType(member));
                // A body whose shape depends on its key (a prefab's components and parts): each entry
                // is qualified as the type its key names, when a plugin has said how to find it.
                if (member != null && value is JsonObject bodies && _bodies.TryGetValue((type, member.Name), out var typeOf))
                {
                    var qualified = new JsonObject();
                    foreach (var (key, body) in bodies)
                        qualified[key] = typeOf(key, body, ns) is { } bodyType ? Qualify(body, bodyType, ns) : body?.DeepClone();
                    result[name] = qualified;
                    continue;
                }
                result[name] = memberType == null ? value?.DeepClone() : Qualify(value, memberType, ns);
            }
            return result;
        }
        return node.DeepClone();
    }

    private readonly Dictionary<(Type, string), Func<string, JsonNode?, string, Type?>> _bodies = new();

    // For a field typed as an interface or abstract class whose JSON says which concrete type it is (a
    // vocabulary entry, issue #28): that type, or null. Set by the Engine over its Vocabularies.
    public Func<Type, JsonNode?, Type?>? PolymorphicTypes { get; set; }

    // And for one written in a shorthand (`{ "has_item": "key" }`, issue #89): its long form, or null.
    // Set by the Engine over its Vocabularies (Vocabularies.Expand).
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // stage 2 logic (#89): may change before 1.0
    public Func<Type, JsonNode?, JsonNode?>? PolymorphicShorthand { get; set; }

    // For a record field that maps a key to a body whose type the key decides — a prefab's
    // "components" and "parts" — how to find that type: `typeOf(key, body, fileNamespace)`, null when
    // it can't be told. With it, bare ids inside those bodies are qualified like the record's own
    // fields when a patch or a base comes from another namespace (R11, issue #22).
    public void AddBodyTypes<T>(string member, Func<string, JsonNode?, string, Type?> typeOf) where T : class =>
        _bodies[(typeof(T), member)] = typeOf;

    private static bool IsReference(Type type) =>
        type == typeof(RecordId) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(RecordRef<>));

    private static Type? ElementType(Type type) =>
        type.IsArray ? type.GetElementType()
        : type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? type.GetGenericArguments()[0]
        : null;

    private static Type? DictionaryValueType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>) ? type.GetGenericArguments()[1] : null;

    private static Type MemberType(MemberInfo member) =>
        member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;

    // An id as a person types it: "practice_sword" finds sandbox:practice_sword wherever it lives,
    // while "sandbox:practice_sword" is taken as written. For consoles and cheats — game code names
    // records in full (05 §3.5).
    public RecordId Resolve(string type, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        RecordId id;
        try { id = RecordId.Parse(text, "sage"); }
        catch (FormatException ex) { Log.Warn(LogCat.Console, ex.Message); return default; }
        // Says so here rather than handing back an empty id for the caller to print as a blank:
        // every console command that resolves a name got "cannot equip : there is no such thing"
        // otherwise, and the useful half of that sentence is the name the player typed.
        var found = text.Contains(':')
            ? (_records.ContainsKey((type, id)) ? id : default)
            : Ids(type).FirstOrDefault(i => i.Name == id.Name);
        if (found.IsEmpty) Log.Warn(LogCat.Console, $"there is no {type} called '{text}'");
        return found;
    }

    private static IEnumerable<MemberInfo> SettableMembers(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Instance).Cast<MemberInfo>()
            .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && p.GetIndexParameters().Length == 0));

    private static void CopyInto(object from, object to)
    {
        foreach (var member in SettableMembers(from.GetType()))
        {
            if (member is FieldInfo f) f.SetValue(to, f.GetValue(from));
            else if (member is PropertyInfo p) p.SetValue(to, p.GetValue(from));
        }
    }

    private void Error(string message)
    {
        ErrorCount++;
        Log.Error(LogCat.Records, message);
    }

    private void Warn(LogCat category, string message)
    {
        WarningCount++;
        Log.Warn(category, message);
    }

    // ---- Tools ------------------------------------------------------------------------------------

    // The file a record is defined in, or null when nothing of that id was loaded. An editor saves
    // a record back where it found it (15 §3, F28) — writing it to a file named after the record instead
    // would leave two definitions of the same id, which is the one thing the record loader cannot sort
    // out for itself.
    public string? FileOf(string type, RecordId id)
    {
        if (!_raw.TryGetValue((type, id), out var record)) return null;
        string file = record.Source.File.Name;   // "mount:path"
        int colon = file.IndexOf(':');
        return colon >= 0 && colon + 1 < file.Length ? file[(colon + 1)..] : null;
    }

    // Where a loaded record, or a field inside it ("Tasks[2]"), is written: "game:data/ai.json:14:9".
    // For problems found after the load — a task name only a world's registry can judge — so they point
    // at a line like the load's own errors do. The bare id when the record came from no file.
    public string Where(string type, RecordId id, string? path = null) =>
        _raw.TryGetValue((type, id), out var raw) ? raw.At(path) : id.ToString();

    // Every write the last load made to a record, in the order they apply (4j-2): what its bases wrote
    // first, deepest base first, each `Via` that base, then the record's own definition and patches.
    // A record a patch disabled still has its writes; one never loaded has none. What rec_get and the
    // content report (`mod_conflicts`) are made of.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // data mods (4j-2): may change before 1.0
    public IReadOnlyList<RecordWrite> Writes(string type, RecordId id)
    {
        if (!_raw.TryGetValue((type, id), out var record) && !_disabled.TryGetValue((type, id), out record)) return Array.Empty<RecordWrite>();
        var writes = new List<RecordWrite>();
        Add(record, default);
        return writes;

        void Add(RawRecord r, RecordId via)
        {
            if (r.Base != null) Add(r.Base, via.IsEmpty ? r.Base.Id : via);
            foreach (var write in r.Writes) writes.Add(new RecordWrite(write, via));
        }
    }

    // For the content report: every record the last load read, disabled ones included, with its own
    // writes (not its bases'), and what it did that left no record.
    internal IEnumerable<(string Type, RecordId Id, RecordSource Source, bool Disabled, List<FieldWrite> Writes)> Provenance() =>
        _raw.Values.Concat(_disabled.Values).Select(r => (r.Type, r.Id, r.Source, r.Disabled, r.Writes));

    internal IReadOnlyList<(string Type, RecordId Id, RecordSource Source, RecordSource Existing)> Redefinitions => _redefinitions;
    internal IReadOnlyList<(string Type, RecordId Id, RecordSource Source)> SkippedPatches => _skippedPatches;

    // The mounts the last load read from; null before it.
    internal VirtualFileSystem? Vfs => _vfs;

    // The merged record, the file each top-level field came from and, where more than one file wrote
    // a field, every write to it in order (rec_get).
    public string Describe(string type, RecordId id)
    {
        if (!_raw.TryGetValue((type, id), out var raw))
        {
            if (!_disabled.TryGetValue((type, id), out raw)) return $"{type} {id}: not found";
            var by = raw.Writes.LastOrDefault(w => w.Op == RecordWriteOp.Disable);
            return $"{type} {id}: disabled" + (by.Path == null ? "" : $" by {by.Source.At("disabled")}");
        }
        var lines = new List<string> { $"{type} {id} (defined in {raw.DefinedIn})", raw.Fields.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) };
        var writes = Writes(type, id);
        var fields = new SortedSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var write in writes)
        {
            string field = FieldWrite.TopLevel(write.Path);
            if (field.Length > 0 && field != "base" && field != "disabled" && seen.Add(field)) fields.Add(field);
        }
        foreach (var field in fields)
        {
            lines.Add($"  {field} <- {raw.OriginOf(field)}");
            var touching = writes.Where(w => FieldWrite.IsUnder(w.Path, field)).ToList();
            if (touching.Select(w => w.File).Distinct(StringComparer.Ordinal).Count() > 1)
                foreach (var write in touching) lines.Add($"      {write}");
        }
        return string.Join('\n', lines);
    }

    public void RegisterCommands(CVarRegistry cvars)
    {
        cvars.RegisterCommand("rec_list", CVarFlags.None, "rec_list [type]: list record types, or the ids of one type.", a =>
        {
            if (a.Count == 0)
            {
                foreach (var t in TypeNames) Log.Info(LogCat.Console, $"  {t}: {Ids(t).Count()} records");
                return;
            }
            foreach (var id in Ids(a[0])) Log.Info(LogCat.Console, $"  {id}");
        });
        cvars.RegisterCommand("rec_get", CVarFlags.None, "rec_get <type> <id>: the merged record and which file set each field.", a =>
        {
            if (a.Count < 2) { Log.Warn(LogCat.Console, "rec_get <type> <id>"); return; }
            Log.Info(LogCat.Console, Describe(a[0], Resolve(a[0], a[1])));
        });
        cvars.RegisterCommand("rec_reload", CVarFlags.None, "Reload and re-merge all record files.", _ => Reload());
        // With the record commands, so it answers with no mods too: a game patching a kit (4j-2).
        cvars.RegisterCommand("mod_conflicts", CVarFlags.None, "mod_conflicts [mount]: what each mount added, patched and shadowed, and where mods conflict.", a =>
        {
            if (_vfs == null) { Log.Warn(LogCat.Console, "mod_conflicts: no content is loaded"); return; }
            var report = ContentReport.Build(this, _vfs);
            foreach (var line in report.Lines(a.Count > 0 ? a[0] : null)) Log.Info(LogCat.Console, line);
        });
    }
}
