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

namespace sage_engine;

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
// as a patch. "disabled": true in a patch removes the record. "base" inherits from another record of
// the same type, resolved after merging. "abstract": true marks a template: usable as a base, never
// built into a record itself (and not inherited).
//
// v1 deserializes with System.Text.Json reflection; the compile-time generated readers/validators
// (docs/design/09) replace that later with the same rules.
// Where a world's records live. Beside `world.Messages()` and `world.Debug()`, and public for the same
// reason: a game's own systems and screens ask the record store as often as the engine's do, and two
// spellings of "where the records live" is how they end up pointing at different stores.
public static class RecordWorldExtensions
{
    public static RecordStore Records(this World world) => world.Resources.Get<RecordStore>();
}

public sealed class RecordStore
{
    private static readonly HashSet<string> MetaKeys = new(StringComparer.Ordinal) { "type", "id", "base", "patch", "disabled", "abstract" };

    private sealed class RawRecord
    {
        public required RecordId Id;
        public required string Type;
        public required JsonObject Fields;          // merged, meta keys removed except "base"
        public required string DefinedIn;
        public readonly Dictionary<string, string> FieldOrigins = new(StringComparer.Ordinal);   // top-level field → file
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
            Converters = { new Vector2JsonConverter(), new Vector3JsonConverter(), new QuaternionJsonConverter(), new EntityJsonConverter(), new JsonStringEnumConverter() },
        };
    }

    // How Sage reads JSON: one dialect, so a prefab's component fields parse exactly like a record's.
    public JsonSerializerOptions Json => _json;

    public int Count => _records.Count;
    public int ErrorCount { get; private set; }

    // Raised on the main thread after Load/Reload. Records that still exist keep the same instances
    // (updated in place), so references held by systems stay valid.
    public event Action? Reloaded;

    // ---- Schemas ----------------------------------------------------------------------------------

    // Modules register their record types in Init (docs/design/01 §5.1), before records load.
    public void Register<T>() where T : class, new()
    {
        var attr = typeof(T).GetCustomAttribute<RecordAttribute>()
                   ?? throw new InvalidOperationException($"{typeof(T).Name} has no [Record(\"type\")] attribute.");
        if (_typesByName.TryGetValue(attr.Type, out var existing) && existing != typeof(T))
            throw new InvalidOperationException($"Record type '{attr.Type}' is already registered by {existing.Name}.");
        _typesByName[attr.Type] = typeof(T);
        _namesByType[typeof(T)] = attr.Type;
    }

    public IEnumerable<string> TypeNames => _typesByName.Keys.OrderBy(n => n, StringComparer.Ordinal);

    // ---- Lookup -----------------------------------------------------------------------------------

    public bool TryGet<T>(RecordId id, out T record) where T : class
    {
        if (_records.TryGetValue((TypeName<T>(), id), out var r)) { record = (T)r; return true; }
        record = null!;
        return false;
    }

    // Missing: Ensure fails (once per call site) and a default-constructed placeholder is returned,
    // so the game keeps running (05 §8).
    public T Get<T>(RecordId id) where T : class, new()
    {
        if (TryGet(id, out T record)) return record;
        Assert.Ensure(false, $"Record {TypeName<T>()} {id} not found");
        return new T();
    }

    public IReadOnlyList<T> All<T>() where T : class
    {
        string type = TypeName<T>();
        return _records.Where(kv => kv.Key.Type == type).OrderBy(kv => kv.Key.Id.ToString(), StringComparer.Ordinal)
                       .Select(kv => (T)kv.Value).ToList();
    }

    public IEnumerable<RecordId> Ids(string type) =>
        _records.Keys.Where(k => k.Type == type).Select(k => k.Id).OrderBy(i => i.ToString(), StringComparer.Ordinal);

    public bool Exists(RecordId id) => _records.Keys.Any(k => k.Id == id);

    private string TypeName<T>() =>
        _namesByType.TryGetValue(typeof(T), out var n) ? n : throw new InvalidOperationException($"{typeof(T).Name} is not a registered record type.");

    // ---- Loading ------------------------------------------------------------------------------------

    public void Load(VirtualFileSystem vfs)
    {
        _vfs = vfs;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ErrorCount = 0;
        var raw = ReadAndMerge(vfs);
        ResolveBases(raw);
        var built = Build(raw);
        ValidateReferences(built, raw);

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
        var entries = new List<(int Mount, bool IsPatch, string Where, string File, string Type, RecordId Id, JsonObject Obj)>();
        var mountIndex = vfs.Mounts.Select((m, i) => (m, i)).ToDictionary(x => x.m, x => x.i);

        foreach (var (path, mount) in vfs.Enumerate(VirtualPath.Parse("data"), "*.json"))
        {
            string file = $"{mount.Name}:{path}";
            JsonNode? root;
            try
            {
                using var stream = mount.Open(path);
                root = JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (Exception ex)
            {
                Error($"{file}: invalid JSON: {ex.Message}");
                continue;
            }

            var items = root is JsonArray array ? array.ToList() : new List<JsonNode?> { root };
            int index = 0;
            foreach (var item in items)
            {
                string where = $"{file}[{index++}]";
                if (item is not JsonObject obj) { Error($"{where}: a record must be a JSON object"); continue; }
                string? type = (string?)obj["type"];
                string? idText = (string?)obj["id"];
                if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(idText)) { Error($"{where}: record needs \"type\" and \"id\""); continue; }
                if (!_typesByName.ContainsKey(type))
                {
                    if (warnedTypes.Add(type)) Log.Warn(LogCat.Records, $"{where}: unknown record type '{type}' (no module registered it); skipped");
                    continue;
                }

                RecordId id;
                try { id = RecordId.Parse(idText, mount.RecordNamespace); }
                catch (FormatException ex) { Error($"{where}: {ex.Message}"); continue; }

                entries.Add((mountIndex[mount], (bool?)obj["patch"] ?? false, where, file, type, id, obj));
            }
        }

        // Mount order decides; within one mount, every definition comes before any patch, so a patch
        // doesn't depend on its file's name sorting after the definition's (OrderBy is stable).
        foreach (var (_, isPatch, where, file, type, id, obj) in entries.OrderBy(e => e.Mount).ThenBy(e => e.IsPatch))
        {
            bool disabled = (bool?)obj["disabled"] ?? false;
            var key = (type, id);
            var fields = StripMeta(obj);

            if (!raw.TryGetValue(key, out var existing))
            {
                if (isPatch)
                {
                    Log.Warn(LogCat.Records, $"{where}: patch for {type} {id}, which isn't defined (yet) in load order; skipped");
                    continue;
                }
                var record = new RawRecord { Id = id, Type = type, Fields = new JsonObject(), DefinedIn = file };
                MergeInto(record.Fields, fields, record.FieldOrigins, file);
                record.Disabled = disabled;
                record.Abstract = (bool?)obj["abstract"] ?? false;
                raw[key] = record;
                continue;
            }

            if (!isPatch)
                Error($"{where}: {type} {id} is already defined in {existing.DefinedIn}; use \"patch\": true to change it. Treated as a patch.");
            MergeInto(existing.Fields, fields, existing.FieldOrigins, file);
            if (obj.ContainsKey("disabled")) existing.Disabled = disabled;
            if (obj.ContainsKey("abstract")) existing.Abstract = (bool?)obj["abstract"] ?? false;
            Log.Debug(LogCat.Records, $"{file} patches {type} {id}");
        }

        foreach (var key in raw.Where(kv => kv.Value.Disabled).Select(kv => kv.Key).ToList())
        {
            Log.Debug(LogCat.Records, $"{key.Item1} {key.Item2} disabled by a patch");
            raw.Remove(key);
        }
        return raw;
    }

    private static JsonObject StripMeta(JsonObject obj)
    {
        var copy = new JsonObject();
        foreach (var (k, v) in obj)
            if (!MetaKeys.Contains(k) || k == "base")
                copy[k] = v?.DeepClone();
        return copy;
    }

    // Per-field merge (05 §3.5). `origins` tracks which file set each top-level field (rec_get).
    private static void MergeInto(JsonObject target, JsonObject patch, Dictionary<string, string>? origins, string file)
    {
        foreach (var (key, value) in patch)
        {
            if (key.EndsWith('+') && key.Length > 1)
            {
                string field = key[..^1];
                var list = target[field] as JsonArray ?? new JsonArray();
                if (value is JsonArray add) foreach (var item in add) list.Add(item?.DeepClone());
                target[field] = list;
                if (origins != null) origins[field] = file;
            }
            else if (key.EndsWith('-') && key.Length > 1)
            {
                string field = key[..^1];
                if (target[field] is JsonArray list && value is JsonArray remove)
                {
                    foreach (var r in remove)
                        for (int i = list.Count - 1; i >= 0; i--)
                            if (JsonNode.DeepEquals(list[i], r)) list.RemoveAt(i);
                }
                if (origins != null) origins[field] = file;
            }
            else if (value is JsonObject childPatch && target[key] is JsonObject childTarget)
            {
                MergeInto(childTarget, childPatch, null, file);
                if (origins != null) origins[key] = file;
            }
            else
            {
                target[key] = value?.DeepClone();
                if (origins != null) origins[key] = file;
            }
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
                Error($"{key.Type} {key.Id}: \"base\" cycle ({string.Join(" -> ", chain.Select(c => c.Item2))})");
                return null;
            }
            // "base" is reserved for inheritance (05 §3.5): a record that wants a field of its own by
            // that name has to call it something else, and a non-string value is a clear error rather
            // than a crash.
            string? baseText = null;
            if (record.Fields["base"] is JsonNode baseNode)
            {
                if (baseNode.GetValueKind() == System.Text.Json.JsonValueKind.String) baseText = (string?)baseNode;
                else Error($"{record.DefinedIn}: {key.Type} {key.Id}: \"base\" must be the id of another record, not {baseNode.GetValueKind()} " +
                           "(\"base\" is reserved for inheritance; rename the field)");
            }
            if (baseText is { Length: > 0 })
            {
                RecordId baseId;
                try { baseId = RecordId.Parse(baseText, record.Id.Namespace); }
                catch (FormatException ex) { Error($"{record.DefinedIn}: {ex.Message}"); baseId = default; }
                var baseFields = baseId.IsEmpty ? null : Resolve((key.Type, baseId), chain);
                if (baseFields == null && !baseId.IsEmpty)
                    Error($"{record.DefinedIn}: {key.Type} {key.Id}: base {baseId} not found (or broken)");
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
                    MergeInto(merged, own, null, record.DefinedIn);
                    record.Fields.Clear();
                    foreach (var (k, v) in merged) record.Fields[k] = v?.DeepClone();
                    foreach (var (field, origin) in raw[(key.Type, baseId)].FieldOrigins)
                        record.FieldOrigins.TryAdd(field, $"{origin} (via base {baseId})");
                }
            }
            record.Fields.Remove("base");
            record.FieldOrigins.Remove("base");
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
            var known = SettableMembers(clr).Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (field, _) in record.Fields)
                if (!known.Contains(field))
                    Log.Warn(LogCat.Records, $"{record.DefinedIn}: {record.Type} {record.Id}: unknown field '{field}' (typo?)");

            RecordParseContext.Namespace = record.Id.Namespace;
            try
            {
                var value = record.Fields.Deserialize(clr, _json);
                if (value != null) built[key] = value;
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or NotSupportedException)
            {
                Error($"{record.DefinedIn}: {record.Type} {record.Id}: {ex.Message} (record skipped)");
            }
            finally
            {
                RecordParseContext.Namespace = null;
            }
        }
        return built;
    }

    // References to other records (RecordId fields, and lists/arrays of them) must exist (05 §3.5).
    private void ValidateReferences(Dictionary<(string, RecordId), object> built, Dictionary<(string, RecordId), RawRecord> raw)
    {
        var ids = built.Keys.Select(k => k.Item2).ToHashSet();
        foreach (var (key, record) in built)
        {
            foreach (var member in SettableMembers(record.GetType()))
            {
                object? value = member is FieldInfo f ? f.GetValue(record) : ((PropertyInfo)member).GetValue(record);
                foreach (var reference in References(value))
                    if (!reference.IsEmpty && !ids.Contains(reference))
                        Error($"{raw[key].DefinedIn}: {key.Item1} {key.Item2}: '{member.Name}' refers to {reference}, which doesn't exist");
            }
        }

        static IEnumerable<RecordId> References(object? value)
        {
            if (value is RecordId id) yield return id;
            else if (value is IEnumerable list and not string)
                foreach (var item in list)
                    if (item is RecordId r) yield return r;
        }
    }

    // Rewrites every bare RecordId inside `node` to `ns:id`, guided by the record type's own fields so
    // that only ids are affected. Runs once per inherited record at load, so it builds fresh nodes
    // rather than trying to re-parent the base's.
    private static JsonNode? Qualify(JsonNode? node, Type type, string ns)
    {
        if (node == null) return null;

        if (type == typeof(RecordId))
        {
            if (node.GetValueKind() != JsonValueKind.String) return node.DeepClone();
            string? text = (string?)node;
            return string.IsNullOrEmpty(text) || text.Contains(':') ? node.DeepClone() : JsonValue.Create($"{ns}:{text}");
        }

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
                var member = valueType != null ? null
                    : SettableMembers(type).FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
                var memberType = valueType ?? (member == null ? null : MemberType(member));
                result[name] = memberType == null ? value?.DeepClone() : Qualify(value, memberType, ns);
            }
            return result;
        }
        return node.DeepClone();
    }

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

    // ---- Tools ------------------------------------------------------------------------------------

    // The merged record as JSON plus the file each top-level field came from (rec_get).
    public string Describe(string type, RecordId id)
    {
        if (!_raw.TryGetValue((type, id), out var raw)) return $"{type} {id}: not found";
        var lines = new List<string> { $"{type} {id} (defined in {raw.DefinedIn})", raw.Fields.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) };
        foreach (var (field, file) in raw.FieldOrigins.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            lines.Add($"  {field} <- {file}");
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
    }
}
