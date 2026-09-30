#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Sage.Simulation;

// Save and load (docs/design/09 §3.5–3.6, TODO F27). What closes the vertical slice: walk somewhere,
// fight something, pick something up, cast a spell, save, come back to it.
//
// **Three deviations from 09, each deliberate.**
//
// 1. *JSON, not tagged binary.* Component ids and field names are the stable identity. Binary is a
//    format change, which is what the upgrader mechanism (§3.6) exists for, and a save you can read in
//    a text editor is worth a great deal while the save system is the thing being debugged.
// 2. *No maps, so no visited-sector rule and no tombstones.* A baseline here is the game's own
//    records, not map files. v1 writes **every persistent entity in full** and load recreates them,
//    so a destroyed entity's tombstone is simply its absence. Tombstones come back with maps (§3.4),
//    because that is when the baseline starts being re-instantiated underneath the save.
// 3. *Reflection, not the generator.* As with records (05 §3.5), and it goes the same way.
//
// A world is not only its entities: a `[SavedResource]` singleton (the spellbook, later the quest log)
// is written beside them and replaced on load, and can rebuild whatever it implies (`ISavedResource`).
//
// What is *not* a deviation: an entity is rebuilt by spawning its prefab and laying the saved
// components over the top. That is why `Populate` was split out in F31 — a character's collider,
// controller and intent come back because the prefab puts them there, not because they were saved.
//
// **Versions** (issue #20, REDESIGN §4.5). Two kinds, for two kinds of change:
//
// - `FormatVersion` is the layout of the files themselves. An older one is brought up to date by a
//   format upgrader (`UpgradeWorld`) before anything reads it; a newer one is refused, because a
//   format we do not understand would corrupt a playthrough quietly.
// - Each component and saved resource carries its own `version`, and its [Upgrade] methods rewrite
//   an older shape (SaveSerializer). That is what a game uses when it renames a field.
//
// Format 1 is what main wrote before issue #20: components keyed by C# type name, and components and
// resources written bare. Format 2 keys them by stable id and wraps each as `{ "version", "data" }`.
public sealed class SaveSystem
{
    public const int FormatVersion = 2;

    // The oldest format this build upgrades. Format 1 (C# type names) is read for one release after
    // issue #20, then this becomes 2 and `From1` goes (docs/design/09 §11).
    public const int OldestReadableFormat = 1;

    private readonly Engine _engine;
    private readonly SaveSerializer _serializer;

    internal SaveSystem(Engine engine)
    {
        _engine = engine;
        _serializer = new SaveSerializer(engine.Components);
    }

    // ---- saved world resources ----------------------------------------------------------------------

    // A registered `[SavedResource]` type, with the three things a save needs doing to it. They are
    // closures made where `T` is still known, because `WorldResources` is keyed on `typeof(T)` and
    // reflecting a generic `Set` back into shape would be a lot of machinery for no gain.
    private readonly record struct SavedResource(
        string Name, Type Type, int Version, Func<World, object?> Read, Action<World, object> Write, Func<object> Create);

    private readonly Dictionary<string, SavedResource> _resources = new(StringComparer.Ordinal);

    // Modules register theirs in Init, beside their record types (01 §5.1). Explicit, like records:
    // scanning assemblies for the attribute would put a mod's resource in every save whether the game
    // that reads it back knows what to do with it or not.
    public void RegisterResource<T>() where T : class, new()
    {
        var attr = typeof(T).GetCustomAttribute<SavedResourceAttribute>()
                   ?? throw new InvalidOperationException($"{typeof(T).Name} has no [SavedResource(\"name\")] attribute.");
        if (_resources.TryGetValue(attr.Name, out var existing) && existing.Type != typeof(T))
            throw new InvalidOperationException($"Saved resource '{attr.Name}' is already registered by {existing.Type.Name}.");

        _engine.Registrations.Record("saved resource", attr.Name);   // who registered it (issue #12)
        _resources[attr.Name] = new SavedResource(
            attr.Name, typeof(T), attr.Version,
            world => world.Resources.TryGet<T>(out var r) ? r : null,
            (world, value) => world.Resources.Replace((T)value),
            () => new T());
    }

    // Converters a plugin adds to the save dialect (SaveJson), made per save and per load because they
    // need the world: gameplay's attributes and tags are written by name, not by index. Registered in
    // Init (SAGE0020), in the order plugins initialise; they come after the engine's own entity converter.
    private readonly List<Func<World, RecordStore, JsonConverter>> _converters = new();

    public void AddConverter(Func<World, RecordStore, JsonConverter> make) => _converters.Add(make);

    // The registered saved resources, by name: for the registry dump and tools (issue #18).
    public IEnumerable<(string Name, Type Type, int Version)> Resources =>
        _resources.Values.OrderBy(r => r.Name, StringComparer.Ordinal).Select(r => (r.Name, r.Type, r.Version));

    private string? _root;

    // Where saves live. Settable so a tool or a test can point it somewhere of its own rather than
    // moving the *process's* user folder, which is global and would pull it out from under anything
    // else running at the same time.
    public string Root
    {
        get => _root ??= Path.Combine(UserPaths.Root, "saves");
        set { _root = value; _slots = null; }
    }

    private string SlotDirectory(string slot) => Path.Combine(Root, Sanitise(slot));

    // ---- writing ------------------------------------------------------------------------------------

    // Saves every world the engine has. Called at a tick boundary, never mid-tick: a save taken
    // half-way through a phase would catch components some systems had updated and others had not.
    public bool Save(string slot)
    {
        string directory = SlotDirectory(slot);
        // Written beside the real folder and moved into place, so a crash mid-save leaves the previous
        // save intact rather than half of two (09 §3.6).
        string staging = directory + ".writing";

        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);

            var header = new JsonObject
            {
                ["formatVersion"] = FormatVersion,
                ["game"] = UserPaths.GameId,
                ["engineVersion"] = BuildInfo.EngineVersion,
                ["savedUtc"] = DateTime.UtcNow.ToString("o"),
                ["worlds"] = new JsonArray(_engine.Worlds.Select(w => (JsonNode)w.Name!).ToArray()),
                // What made the world it holds (REDESIGN §4.5, issue 4i-2): a load compares these with what
                // is loaded now and says what differs, and a load menu shows it (SaveSlot.Mismatches).
                ["plugins"] = new JsonArray(CurrentPlugins().Select(p => (JsonNode)new JsonObject
                {
                    ["id"] = p.Id, ["version"] = p.Version,
                }).ToArray()),
                ["content"] = new JsonArray(CurrentContent().Select(c => (JsonNode)new JsonObject
                {
                    ["mount"] = c.Mount, ["namespace"] = c.Namespace,
                }).ToArray()),
            };
            File.WriteAllText(Path.Combine(staging, "header.json"), header.ToJsonString(Indented));

            int total = 0;
            foreach (var world in _engine.Worlds)
            {
                var (json, count) = WriteWorld(world);
                File.WriteAllText(Path.Combine(staging, $"world_{Sanitise(world.Name)}.json"), json);
                total += count;
            }

            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            Directory.Move(staging, directory);
            _slots = null;   // the listing a menu shows has a new or newer slot in it
            Log.Info(LogCat.Save, $"Saved '{slot}': {total} entities across {_engine.Worlds.Count} world(s)");
            return true;
        }
        // Everything, on purpose: a save is the one operation where a leaked half-written folder is
        // worse than the original fault. The first version listed the exceptions it expected and an
        // unexpected one (a float that had become infinity) escaped it, leaving `slot.writing` behind
        // and the previous save untouched but shadowed in the listing.
        catch (Exception ex)
        {
            Log.Error(LogCat.Save, $"Save '{slot}' failed: {ex.Message}");
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { /* nothing more to try */ }
            return false;
        }
    }

    private (string Json, int Count) WriteWorld(World world)
    {
        var json = SaveJson.For(world, _engine.Records, _converters);
        var entities = new JsonArray();

        // Disabled ones included: a placeholder is disabled so that nothing else sees it (issue 4i-2).
        foreach (var entity in world.PersistentIncludingDisabled())
        {
            var persistent = entity.GetComponent<Persistent>();
            if (persistent.Id.IsEmpty) continue;

            // An entity whose prefab this game does not have goes back as it came (issue 4i-2).
            if (world.TryGet<SavePlaceholder>(entity, out var placeholder)
                && JsonNode.Parse(placeholder.Saved ?? "") is JsonObject kept)
            {
                kept["id"] = persistent.Id.ToString();
                entities.Add(kept);
                continue;
            }

            var saved = new JsonObject { ["id"] = persistent.Id.ToString() };
            if (world.TryGet<FromPrefab>(entity, out var from) && !from.Prefab.IsEmpty)
                saved["prefab"] = from.Prefab.ToString();
            if (entity.Name is { Length: > 0 } named)
                saved["name"] = named;
            var components = _serializer.WriteComponents(world, entity, json);
            var tags = _serializer.WriteTags(world, entity);
            // What the save said that this game has no component or tag for, back as it was (issue 4i-2).
            if (world.TryGet<UnknownSavedData>(entity, out var unknown))
                MergeUnknown(components, tags, unknown);
            saved["components"] = components;
            if (tags.Count > 0) saved["tags"] = tags;
            entities.Add(saved);
        }

        // **Which sector these positions are relative to** (R6, 14 §3). Every position in this file is
        // in origin space; without the origin, a save taken a hundred kilometres out would load its
        // entities into the starting sector and put the player under ground that is not theirs.
        var sector = world.Origin().Sector;
        var root = new JsonObject
        {
            ["origin"] = new JsonObject { ["x"] = sector.X, ["z"] = sector.Z },
            ["entities"] = entities,
        };

        var resources = new JsonObject();
        foreach (var kind in _resources.Values.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            object? value = kind.Read(world);
            if (value is null) continue;   // this world does not have one: nothing to say about it
            try
            {
                resources[kind.Name] = SaveSerializer.Entry(kind.Version, JsonSerializer.SerializeToNode(value, kind.Type, json));
            }
            catch (Exception ex) when (ex is NotSupportedException or JsonException or ArgumentException)
            {
                Log.Error(LogCat.Save, $"Saved resource '{kind.Name}' cannot be written: {ex.Message}");
            }
        }
        // Resources the last load found and this game has no type for (a mod's quest log), back as they were.
        if (_unknownResources.TryGetValue(world, out var unknownResources))
            foreach (var (name, entry) in unknownResources)
                if (!resources.ContainsKey(name) && entry != null) resources[name] = entry.DeepClone();
        if (resources.Count > 0) root["resources"] = resources;

        return (root.ToJsonString(Indented), entities.Count);
    }

    private static void MergeUnknown(JsonObject components, JsonArray tags, in UnknownSavedData unknown)
    {
        if (!string.IsNullOrEmpty(unknown.Components) && JsonNode.Parse(unknown.Components) is JsonObject kept)
            foreach (var (id, entry) in kept.ToList())
            {
                if (components.ContainsKey(id)) continue;   // the entity has one now: what it has wins
                kept.Remove(id);
                components[id] = entry;
            }
        if (!string.IsNullOrEmpty(unknown.Tags) && JsonNode.Parse(unknown.Tags) is JsonArray keptTags)
        {
            var have = tags.Select(t => (string?)t).ToHashSet(StringComparer.Ordinal);
            foreach (var tag in keptTags)
                if ((string?)tag is { Length: > 0 } id && have.Add(id)) tags.Add(id);
        }
    }

    // The saved resources a load found that no registered type claims, per world, for the next save.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<World, JsonObject> _unknownResources = new();

    // ---- reading ------------------------------------------------------------------------------------

    public bool Exists(string slot) => File.Exists(Path.Combine(SlotDirectory(slot), "header.json"));

    // Loads into the engine's existing worlds: every persistent entity is removed and rebuilt from the
    // file. Non-persistent things (terrain chunks, the ground) are the world's own business and are
    // left alone — they are rebuilt by the systems that own them.
    //
    // **A load cannot half-happen** (REDESIGN §4.5, issue 4i-2). Every world file is read, parsed and
    // upgraded, and every entity's id, prefab and name is read, before any world is touched: a save
    // that is corrupt anywhere is refused whole and the game is left as it was. What is left to do
    // after that point is laying data that has already been read onto the worlds.
    public bool Load(string slot)
    {
        string directory = SlotDirectory(slot);
        if (!Exists(slot)) { Log.Warn(LogCat.Save, $"No save '{slot}' in {Root}"); return false; }

        JsonObject header;
        var prepared = new List<PreparedWorld>();
        try
        {
            header = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "header.json"))) as JsonObject
                     ?? throw new InvalidDataException("header.json is not a JSON object");
            int version = header["formatVersion"] is JsonValue f ? (int)f : 0;
            if (version > FormatVersion || version < OldestReadableFormat)
            {
                // Newer than this build, or older than any upgrader here: refusing is honest, and
                // loading a format we do not understand would corrupt a playthrough quietly.
                Log.Error(LogCat.Save, $"Save '{slot}' is format {version}; this build reads formats " +
                                       $"{OldestReadableFormat} to {FormatVersion}");
                return false;
            }
            if (version < FormatVersion)
                Log.Info(LogCat.Save, $"Save '{slot}' is format {version}; upgrading it to {FormatVersion} as it loads");

            foreach (var world in _engine.Worlds)
            {
                string file = Path.Combine(directory, $"world_{Sanitise(world.Name)}.json");
                if (!File.Exists(file)) { Log.Warn(LogCat.Save, $"'{slot}' has nothing for world '{world.Name}'"); continue; }
                prepared.Add(Prepare(world, File.ReadAllText(file), version, $"{slot}/world_{world.Name}"));
            }
        }
        // Wide on purpose, and safe to be: nothing has been changed yet. A malformed record id is a
        // FormatException, a value of the wrong JSON kind an InvalidOperationException; before 4i-2 the
        // first escaped the catch half-way through a load.
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException
                                      or FormatException or InvalidOperationException or ArgumentException
                                      or NotSupportedException or OverflowException)
        {
            Log.Error(LogCat.Save, $"Load '{slot}' failed, and nothing was changed: {ex.Message}");
            return false;
        }

        // Written by other plugins or content than these: said, and loaded anyway (the placeholders and
        // the kept data below are what make that safe).
        var mismatches = Mismatches(header);
        if (mismatches.Count > 0)
            Log.Warn(LogCat.Save, $"Save '{slot}' was written with other plugins or content; loading it anyway: " +
                                  string.Join("; ", mismatches));

        int total = 0;
        foreach (var world in prepared)
            total += Apply(world);

        foreach (var world in _engine.Worlds)
            world.Resources.Get<GameRules>().OnLoaded(world);

        Log.Info(LogCat.Save, $"Loaded '{slot}': {total} entities");
        return true;
    }

    // One world file, read and upgraded, and nothing done with it yet.
    private sealed record PreparedWorld(World World, string Where, SectorCoord? Origin, List<PreparedEntity> Entities, JsonObject? Resources);

    private readonly record struct PreparedEntity(PersistentId Id, RecordId Prefab, string? Name, JsonObject Saved);

    // Everything about a world file that can fail, done without touching the world: a problem here
    // throws, and Load refuses the save.
    private PreparedWorld Prepare(World world, string json, int format, string where)
    {
        if (JsonNode.Parse(json) is not JsonObject root || root["entities"] is not JsonArray entities)
            throw new InvalidDataException($"{where}: no \"entities\"");
        UpgradeWorld(root, format);

        SectorCoord? origin = root["origin"] is JsonObject savedOrigin
            ? new SectorCoord((int?)savedOrigin["x"] ?? 0, (int?)savedOrigin["z"] ?? 0)
            : null;

        var list = new List<PreparedEntity>(entities.Count);
        foreach (var node in entities)
        {
            if (node is not JsonObject saved) continue;
            if (!PersistentId.TryParse((string?)saved["id"], out var id))
            {
                Log.Warn(LogCat.Save, $"{where}: an entity has no usable \"id\"; skipped");
                continue;
            }

            var prefab = (string?)saved["prefab"] is { Length: > 0 } text ? RecordId.Parse(text, "sage") : default;
            list.Add(new PreparedEntity(id, prefab, (string?)saved["name"], saved));
        }

        return new PreparedWorld(world, where, origin, list, root["resources"] as JsonObject);
    }

    private int Apply(PreparedWorld file)
    {
        var world = file.World;
        string where = file.Where;

        // Back to the frame these positions were written in, *before* anything is placed in it. This
        // also brings the terrain rings and the physics world along, because that is what a rebase
        // does — the same path streaming uses when the player walks there (R6).
        if (file.Origin is { } origin)
            world.Rebase(origin);

        // Out with the old. Everything persistent is in the file, so anything here that is not was
        // destroyed before the save was taken — which is what a tombstone would have said. Disabled
        // ones too: the last load's placeholders.
        foreach (var entity in world.PersistentIncludingDisabled().ToEntityList())
            world.Destroy(entity);
        world.FlushCommands();

        // **Pass one: every entity, with its identity, and nothing else.** Components come after, so
        // that by the time one mentions another entity that entity exists and the reference resolves
        // as it is read. Two passes instead of a fix-up list, and nesting comes free.
        var rebuilt = new List<(Entity Entity, JsonObject Saved)>();
        var placeholders = new List<(Entity Entity, JsonObject Saved)>();
        foreach (var (id, prefab, name, saved) in file.Entities)
        {
            // Spawned from its prefab so the *parts* come back — a character's capsule, controller and
            // intent were never in the save because the prefab puts them there (F31).
            Entity entity = default;
            if (prefab.IsEmpty) entity = world.Create(Transform.Identity);
            else if (_engine.Records.TryGet(prefab, out PrefabRecord _)) entity = world.SpawnWithoutId(prefab);

            if (entity.IsNull)
            {
                // No such prefab in this game (a mod removed, a prefab renamed), or it could not be
                // spawned: kept as an inert placeholder rather than dropped (issue 4i-2).
                Log.Once(LogCat.Save, LogLevel.Warn, $"placeholder:{prefab}",
                    $"{where}: no prefab '{prefab}' to rebuild {name ?? id.ToString()} from; it is kept, inert, " +
                    "and written back unchanged by the next save");
                entity = world.Create(Transform.Identity);
                world.Add(entity, new SavePlaceholder { Prefab = prefab, Saved = saved.ToJsonString() });
                placeholders.Add((entity, saved));
            }
            else rebuilt.Add((entity, saved));

            world.Add(entity, new Persistent { Id = id });
            // Entity.Name adds the name when the entity has none (Friflo's own setter threw on an entity
            // without one, which is every prefab-less entity: a golden save found it, #20).
            if (name is { Length: > 0 })
                entity.Name = name;
        }
        world.FlushCommands();

        // Pass two: the state.
        var dialect = SaveJson.For(world, _engine.Records, _converters);
        foreach (var (entity, saved) in rebuilt)
        {
            var unknown = new JsonObject();
            var unknownTags = new JsonArray();
            if (saved["components"] is JsonObject components)
                _serializer.ReadComponents(world, entity, components, dialect, where, unknown);
            if (saved["tags"] is JsonArray tags)
                _serializer.ReadTags(world, entity, tags, where, unknownTags);
            if (unknown.Count > 0 || unknownTags.Count > 0)
                world.Add(entity, new UnknownSavedData { Components = unknown.ToJsonString(), Tags = unknownTags.ToJsonString() });
        }

        // A placeholder stands where the entity stood (for a tool that looks), and then out of sight.
        foreach (var (entity, saved) in placeholders)
        {
            if (saved["components"] is JsonObject components && components["sage:transform"] is JsonNode transform)
                _serializer.ReadComponents(world, entity, new JsonObject { ["sage:transform"] = transform.DeepClone() }, dialect, where);
            World.SetEnabled(entity, false);
        }

        // What the scene placed is the scene's again, with its wiring (issues #29, #90) — before the
        // resources, because entity I/O's puts back how often each of those wires fired.
        _engine.Scenes.AfterLoad(world);

        ReadResources(world, file.Resources, dialect, where);

        world.FlushCommands();
        return rebuilt.Count + placeholders.Count;
    }

    // ---- the header's plugins and content (issue 4i-2) ---------------------------------------------

    // The runtime plugins loaded now: an editor's or a tool's own modules are not part of a game's state.
    private IEnumerable<SavedPlugin> CurrentPlugins() =>
        _engine.Modules.Modules
            .Select(m => _engine.Modules.Plugin(m))
            .Where(p => p.Kind == ModuleKind.Runtime)
            .Select(p => new SavedPlugin(p.Id, p.Version.ToString()))
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase);

    // The content mounted now, in priority order.
    private IEnumerable<SavedContent> CurrentContent() =>
        _engine.Vfs.Mounts.Select(m => new SavedContent(m.Name, m.RecordNamespace));

    internal static List<SavedPlugin>? ReadPlugins(JsonObject header)
    {
        if (header["plugins"] is not JsonArray list) return null;   // written before 4i-2: nothing to compare
        var plugins = new List<SavedPlugin>();
        foreach (var node in list)
            if (node is JsonObject p && p["id"] is JsonValue id && id.TryGetValue(out string? text) && text is { Length: > 0 })
                plugins.Add(new SavedPlugin(text, p["version"] is JsonValue v && v.TryGetValue(out string? version) ? version ?? "" : ""));
        return plugins;
    }

    internal static List<SavedContent>? ReadContent(JsonObject header)
    {
        if (header["content"] is not JsonArray list) return null;
        var content = new List<SavedContent>();
        foreach (var node in list)
            if (node is JsonObject c && c["mount"] is JsonValue m && m.TryGetValue(out string? mount) && mount is { Length: > 0 })
                content.Add(new SavedContent(mount, c["namespace"] is JsonValue n && n.TryGetValue(out string? ns) ? ns ?? "" : ""));
        return content;
    }

    // What differs between the plugins and content a header lists and what is loaded now, one line
    // each. Empty when they match, or when the header is from before headers listed them.
    private List<string> Mismatches(JsonObject header) => Mismatches(ReadPlugins(header), ReadContent(header));

    private List<string> Mismatches(IReadOnlyList<SavedPlugin>? plugins, IReadOnlyList<SavedContent>? content)
    {
        var result = new List<string>();
        if (plugins != null)
        {
            var now = CurrentPlugins().ToDictionary(p => p.Id, p => p.Version, StringComparer.OrdinalIgnoreCase);
            var then = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in plugins)
            {
                then.Add(p.Id);
                if (!now.TryGetValue(p.Id, out var version)) result.Add($"plugin '{p.Id}' {p.Version} is not loaded");
                else if (!string.Equals(version, p.Version, StringComparison.Ordinal))
                    result.Add($"plugin '{p.Id}' was {p.Version} and is {version}");
            }
            foreach (var (id, version) in now)
                if (!then.Contains(id)) result.Add($"plugin '{id}' {version} was not loaded then");
        }
        if (content != null)
        {
            var now = CurrentContent().Select(c => c.Mount).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var then = content.Select(c => c.Mount).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var c in content)
                if (!now.Contains(c.Mount)) result.Add($"content '{c.Mount}' is not mounted");
            foreach (var mount in now)
                if (!then.Contains(mount)) result.Add($"content '{mount}' was not mounted then");
        }
        return result;
    }

    // Every registered resource is replaced, including the ones the file says nothing about: a load
    // is a different game, and a spellbook left over from the last one would be a set of spells the
    // player never made. Absent in the file means "you had none", not "keep what you have".
    private void ReadResources(World world, JsonObject? saved, JsonSerializerOptions dialect, string where)
    {
        // What no registered resource claims is kept for the next save (issue 4i-2).
        var unknown = new JsonObject();
        if (saved != null)
            foreach (var (name, entry) in saved)
                if (!_resources.ContainsKey(name) && entry != null)
                {
                    Log.Once(LogCat.Save, LogLevel.Warn, $"unknown-resource:{name}",
                        $"{where}: no saved resource '{name}' in this game; kept for the next save, unused");
                    unknown[name] = entry.DeepClone();
                }
        _unknownResources.AddOrUpdate(world, unknown);

        foreach (var kind in _resources.Values)
        {
            object? value = null;
            if (saved?[kind.Name] is JsonNode node)
                value = SaveSerializer.ReadEntry(kind.Type, kind.Name, kind.Version, node, dialect, $"{where}: resource '{kind.Name}'");

            kind.Write(world, value ?? kind.Create());
        }

        // After they are all installed, because one may look at another.
        foreach (var kind in _resources.Values)
            if (kind.Read(world) is ISavedResource restored) restored.AfterLoad(world);
    }

    // ---- format upgraders (issue #20) --------------------------------------------------------------

    // Brings a world file from `format` up to FormatVersion, one step at a time, in memory. The file on
    // disk is left as it was: the next save writes the current format.
    private void UpgradeWorld(JsonObject root, int format)
    {
        for (int from = format; from < FormatVersion; from++)
        {
            switch (from)
            {
                case 1: From1(root); break;
            }
        }
    }

    // Format 1 → 2. Components were keyed by C# type name and written bare; now they are keyed by
    // stable id and wrapped as `{ "version": 1, "data": … }` — version 1 because nothing had a version
    // before, so every shape in a format 1 save is the first. A name that matches no component is
    // kept as it is, and reading says it is unknown, as it would have then.
    //
    // For one release after issue #20, so saves from main keep loading. Remove it with
    // OldestReadableFormat = 2, together with ComponentSchema's type-name lookup.
    private void From1(JsonObject root)
    {
        var schema = _engine.Components;
        if (root["entities"] is JsonArray entities)
        {
            foreach (var node in entities)
            {
                if (node is not JsonObject entity) continue;
                if (entity["components"] is JsonObject components)
                {
                    var byId = new JsonObject();
                    foreach (var (name, data) in components.ToList())
                    {
                        components.Remove(name);
                        string id = schema.TryFormerComponent(name, typeNames: true, out var declaration) ? declaration.Id : name;
                        byId[id] = SaveSerializer.Entry(1, data);
                    }
                    entity["components"] = byId;
                }
                if (entity["tags"] is JsonArray tags)
                {
                    var ids = new JsonArray();
                    foreach (var tag in tags)
                    {
                        string name = (string?)tag ?? "";
                        ids.Add(schema.TryFormerTag(name, typeNames: true, out var declaration) ? declaration.Id : name);
                    }
                    entity["tags"] = ids;
                }
            }
        }

        if (root["resources"] is JsonObject resources)
        {
            foreach (var (name, data) in resources.ToList())
            {
                resources.Remove(name);
                resources[name] = SaveSerializer.Entry(1, data);
            }
        }
    }

    // ---- listing ------------------------------------------------------------------------------------

    public IEnumerable<(string Slot, DateTime SavedUtc, int Entities)> List()
    {
        if (!Directory.Exists(Root)) yield break;
        foreach (string directory in Directory.EnumerateDirectories(Root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string headerPath = Path.Combine(directory, "header.json");
            if (!File.Exists(headerPath)) continue;

            DateTime saved = default;
            int entities = 0;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(headerPath)) is JsonObject header)
                    DateTime.TryParse((string?)header["savedUtc"], out saved);
                foreach (string file in Directory.EnumerateFiles(directory, "world_*.json"))
                    if (JsonNode.Parse(File.ReadAllText(file)) is JsonObject world && world["entities"] is JsonArray list)
                        entities += list.Count;
            }
            catch (Exception ex) when (ex is IOException or JsonException) { /* a broken save still lists */ }

            yield return (Path.GetFileName(directory), saved, entities);
        }
    }

    // ---- slots, for a menu (issue #99) ----------------------------------------------------------------

    private List<SaveSlot>? _slots;
    private int _slotsVersion;

    // The saves a load menu lists, newest first: each slot's name and what its header says — when it was
    // written, by which game and engine, in which format, and whether this build can read it back. Read
    // from disk once and kept: a menu reads this every frame it is open, so the folder is scanned again
    // only after a Save, a change of Root, or Rescan (a menu opening calls it, in case another process
    // wrote a save meanwhile). Unlike List it opens only the headers, never the world files.
    public IReadOnlyList<SaveSlot> Slots => _slots ??= ScanSlots();

    // Moves each time Slots is read from disk again, so a view-model rebuilds its rows only then.
    public int SlotsVersion { get { _ = Slots; return _slotsVersion; } }

    // Forget the listing; the next read of Slots scans the folder again.
    public void Rescan() => _slots = null;

    private List<SaveSlot> ScanSlots()
    {
        _slotsVersion++;
        var slots = new List<SaveSlot>();
        if (!Directory.Exists(Root)) return slots;
        foreach (string directory in Directory.EnumerateDirectories(Root))
        {
            string name = Path.GetFileName(directory);
            if (name.EndsWith(".writing", StringComparison.Ordinal)) continue;   // a save interrupted mid-write
            string headerPath = Path.Combine(directory, "header.json");
            if (!File.Exists(headerPath)) continue;

            DateTime saved = default;
            int format = 0;
            string game = "", engine = "";
            List<SavedPlugin>? plugins = null;
            List<SavedContent>? content = null;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(headerPath)) is JsonObject header)
                {
                    if (DateTime.TryParse((string?)header["savedUtc"], System.Globalization.CultureInfo.InvariantCulture,
                                          System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var when))
                        saved = when;
                    format = header["formatVersion"] is JsonValue f && f.TryGetValue(out int n) ? n : 0;
                    game = header["game"] is JsonValue g && g.TryGetValue(out string? id) ? id ?? "" : "";
                    engine = header["engineVersion"] is JsonValue e && e.TryGetValue(out string? v) ? v ?? "" : "";
                    plugins = ReadPlugins(header);
                    content = ReadContent(header);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
            {
                /* a broken header still lists, as one that cannot be loaded */
            }
            slots.Add(new SaveSlot(name, saved, format, game, engine,
                plugins ?? new List<SavedPlugin>(), content ?? new List<SavedContent>(), Mismatches(plugins, content)));
        }
        slots.Sort((a, b) =>
        {
            int byTime = b.SavedUtc.CompareTo(a.SavedUtc);
            return byTime != 0 ? byTime : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });
        return slots;
    }

    // ---- console ------------------------------------------------------------------------------------

    public void RegisterCommands(CVarRegistry cvars)
    {
        cvars.RegisterCommand("save", CVarFlags.None, "save [slot]: write a save (default slot \"quick\").", a =>
            Save(a.Count > 0 ? a[0] : "quick"));

        cvars.RegisterCommand("load", CVarFlags.None, "load [slot]: read a save back (default slot \"quick\").", a =>
            Load(a.Count > 0 ? a[0] : "quick"));

        cvars.RegisterCommand("saves", CVarFlags.None, "What saves exist.", _ =>
        {
            int n = 0;
            foreach (var (slot, saved, entities) in List())
            {
                string when = saved == default ? "unknown" : saved.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                Log.Info(LogCat.Console, $"  {slot,-20} {when}   {entities} entities");
                n++;
            }
            Log.Info(LogCat.Console, n == 0 ? $"no saves in {Root}" : $"{n} save(s) in {Root}");
        });
    }

    // The resolver is explicit because the .NET 8 runtime refuses to write a JsonValue with options
    // that have none ("must specify a TypeInfoResolver"); .NET 9 and later default it, which is how
    // every save failing on net8.0 hid behind RollForward=Major on machines with only a newer runtime.
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    // A slot is a folder name, and a folder name is not a place to trust a string.
    private static string Sanitise(string name)
    {
        var clean = name.Trim();
        foreach (char bad in Path.GetInvalidFileNameChars()) clean = clean.Replace(bad, '_');
        return clean.Length == 0 ? "unnamed" : clean;
    }
}

// One save on disk, as its header describes it (SaveSystem.Slots, issue #99): what a load menu lists.
public sealed class SaveSlot
{
    internal SaveSlot(string name, DateTime savedUtc, int formatVersion, string game, string engineVersion,
                      IReadOnlyList<SavedPlugin> plugins, IReadOnlyList<SavedContent> content, IReadOnlyList<string> mismatches)
    {
        Name = name;
        SavedUtc = savedUtc;
        FormatVersion = formatVersion;
        Game = game;
        EngineVersion = engineVersion;
        Plugins = plugins;
        Content = content;
        Mismatches = mismatches;
    }

    // The slot's name: what Save and Load take.
    public string Name { get; }

    // When it was written (UTC); default when the header does not say.
    public DateTime SavedUtc { get; }

    // The file layout it was written in (SaveSystem.FormatVersion then); 0 when the header is unreadable.
    public int FormatVersion { get; }

    // The game id and engine version that wrote it.
    public string Game { get; }
    public string EngineVersion { get; }

    // The runtime plugins (id and version) and the content mounts that were loaded when it was written
    // (issue 4i-2). Empty for a save from before headers listed them.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public IReadOnlyList<SavedPlugin> Plugins { get; }

    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public IReadOnlyList<SavedContent> Content { get; }

    // How those differ from what is loaded now, one readable line each ("plugin 'x' 0.1.0 is not
    // loaded"): what a load warns about, and what a menu can show beside the slot. It still loads.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public IReadOnlyList<string> Mismatches { get; }

    // Whether this build reads its format: not newer than FormatVersion, not older than OldestReadableFormat.
    public bool CanLoad => FormatVersion >= SaveSystem.OldestReadableFormat && FormatVersion <= SaveSystem.FormatVersion;

    public override string ToString() => Name;
}

// A plugin as a save's header lists it (issue 4i-2): its id and its version when the save was written.
[Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public readonly record struct SavedPlugin(string Id, string Version);

// A content mount as a save's header lists it (issue 4i-2): the mount's name (`engine`, `mygame/data`, a
// plugin's id) and the record namespace it gave bare ids.
[Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public readonly record struct SavedContent(string Mount, string Namespace);
