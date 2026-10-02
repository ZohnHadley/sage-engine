#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
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
// 2. *No visited-sector rule yet.* Every persistent entity is written. Since 4i-5 one spawned from a
//    prefab is written as a **diff against its prefab as spawned** (SaveDiff), so a rebalance reaches
//    what the game never changed.
//    **A load reconciles** (issue 4i-3, format 3): what content places — scenes, placements documents,
//    `.map`s and the children of their prefabs — is placed again from the content as it is now, the
//    save's **tombstones** remove what the game destroyed, and the saved state is laid onto what matches
//    by id (ContentBaseline). Before format 3 a load destroyed everything and rebuilt the file, so a
//    destroyed entity's tombstone was its absence, and a placement added to the content since was lost.
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
// Format 3 (4i-3) adds the world's scene, each entity content placed has the `source` that placed it (a
// prefab's child the game spawned names its `parent`), and `tombstones` lists, by source, what content
// placed that the game destroyed.
// Format 4 (4g-1) adds `dormant`: by source, the state of content that is not in the world (a scene the
// player left, a level waiting for its ground), each with the `sector` its positions are relative to, and
// a runtime spawn's cell (`sage:cell`). Live entities stay in `entities`.
public sealed partial class SaveSystem
{
    public const int FormatVersion = 4;

    // The oldest format this build upgrades. Format 1 (C# type names) is read for one release after
    // issue #20, then this becomes 2 and `From1` goes (docs/design/09 §11).
    public const int OldestReadableFormat = 1;

    private readonly Engine _engine;
    private readonly SaveSerializer _serializer;

    internal SaveSystem(Engine engine)
    {
        _engine = engine;
        _serializer = new SaveSerializer(engine.Components);
        // A record reloaded is updated in place, so a prefab's baseline cannot tell by the instance (4i-5).
        engine.Records.Reloaded += () => _recordsLoaded++;
    }

    private int _recordsLoaded;

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

    // Saves every world the engine has, as a manual save. Called at a tick boundary, never mid-tick: a
    // save taken half-way through a phase would catch components some systems had updated and others had
    // not. Called during a tick (by a system), it is a request instead and runs when the tick ends (4i-6).
    public bool Save(string slot) => Save(slot, SaveKind.Manual);

    // The same, saying which kind of save it is (4i-6): the header keeps it, and SaveSlot.Kind reads it.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public bool Save(string slot, SaveKind kind)
    {
        if (IsMidTick)
        {
            RequestSave(slot, kind);
            return true;
        }

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
                ["kind"] = KindName(kind),   // quick, auto or manual (4i-6)
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
                // The active data mods (phase 4j-4), in load order; their mounts are left out of "content".
                ["mods"] = new JsonArray(CurrentMods().Select(m => (JsonNode)new JsonObject
                {
                    ["id"] = m.Id, ["version"] = m.Version,
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
            _sinceAutosave = 0;   // the autosave clock counts from the last save of any kind
            Log.Info(LogCat.Save, $"Saved '{slot}' ({KindName(kind)}): {total} entities across {_engine.Worlds.Count} world(s)");
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
        world.Resources.TryGet<ContentBaseline>(out var baseline);

        // Disabled ones included: a placeholder is disabled so that nothing else sees it (issue 4i-2).
        foreach (var entity in world.PersistentIncludingDisabled())
            if (WriteEntity(world, entity, json, baseline) is { } saved) entities.Add(saved);

        // **Which sector these positions are relative to** (R6, 14 §3). Every position in this file is
        // in origin space; without the origin, a save taken a hundred kilometres out would load its
        // entities into the starting sector and put the player under ground that is not theirs.
        var sector = world.Origin().Sector;
        var root = new JsonObject
        {
            ["origin"] = new JsonObject { ["x"] = sector.X, ["z"] = sector.Z },
        };
        // The scene the world is in, which a load places (4i-3).
        if (world.Resources.TryGet<ActiveScene>(out var active) && active is { Id.IsEmpty: false })
            root["scene"] = active.Id.ToString();
        root["entities"] = entities;
        // Content not in the world just now, with its state (4g-1): a scene the player left, with what was
        // dropped there, or a level still waiting for its ground. Each keeps the sector its positions are
        // relative to, so the file's origin does not matter to them.
        if (baseline != null)
        {
            var dormant = new JsonObject();
            foreach (var (source, cell) in baseline.DormantCells)
            {
                if (cell.Entities.Count == 0) continue;
                dormant[source] = new JsonObject
                {
                    ["sector"] = new JsonObject { ["x"] = cell.Frame.X, ["z"] = cell.Frame.Z },
                    ["entities"] = new JsonArray(cell.Entities.Select(e => (JsonNode)e.DeepClone()).ToArray()),
                };
            }
            if (dormant.Count > 0) root["dormant"] = dormant;
        }
        // What content placed that the game destroyed, by the source that placed it (4i-3).
        if (baseline?.Tombstones(world) is { Count: > 0 } tombstones)
        {
            var dead = new JsonObject();
            foreach (var (from, ids) in tombstones)
                dead[from] = new JsonArray(ids.Select(id => (JsonNode)id).ToArray());
            root["tombstones"] = dead;
        }

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

    // One persistent entity as a save writes it; null for one without an id.
    private JsonObject? WriteEntity(World world, Entity entity, JsonSerializerOptions json, ContentBaseline? baseline)
    {
        var persistent = entity.GetComponent<Persistent>();
        if (persistent.Id.IsEmpty) return null;

        // An entity whose prefab this game does not have goes back as it came (issue 4i-2).
        if (world.TryGet<SavePlaceholder>(entity, out var placeholder)
            && JsonNode.Parse(placeholder.Saved ?? "") is JsonObject kept)
        {
            kept["id"] = persistent.Id.ToString();
            return kept;
        }

        var saved = new JsonObject { ["id"] = persistent.Id.ToString() };
        // What placed it (4i-3): content, which a load places again and lays this onto; or, for a child
        // its parent's prefab placed, the parent, which brings it back when it is spawned.
        if (baseline != null && baseline.IsBaseline(persistent.Id) && baseline.TryGetSource(persistent.Id, out var source))
            saved["source"] = source;
        else if (entity.Tags.Has<FromParentPrefab>() && !entity.Parent.IsNull
                 && entity.Parent.TryGetComponent<Persistent>(out var parent) && !parent.Id.IsEmpty)
            saved["parent"] = parent.Id.ToString();
        if (world.TryGet<FromPrefab>(entity, out var from) && !from.Prefab.IsEmpty)
            saved["prefab"] = from.Prefab.ToString();
        if (entity.Name is { Length: > 0 } named)
            saved["name"] = named;
        // Spawned from a prefab: only what differs from it as spawned, and what it lost (4i-5, SaveDiff).
        var asSpawned = world.TryGet<FromPrefab>(entity, out var spawned) ? spawned.Baseline : null;
        var removed = new JsonArray();
        var components = _serializer.WriteComponents(world, entity, json, asSpawned, removed);
        if (asSpawned != null)
        {
            saved["diff"] = true;
            if (removed.Count > 0) saved["removed"] = removed;
        }
        var tags = _serializer.WriteTags(world, entity);
        // What the save said that this game has no component or tag for, back as it was (issue 4i-2).
        if (world.TryGet<UnknownSavedData>(entity, out var unknown))
            MergeUnknown(components, tags, unknown);
        // Content's entity still where content placed it (4m-4): no transform, so a load leaves it where the
        // content places it then, and moving the placement moves it. A runtime spawn always has one: it is
        // rebuilt where it stood.
        if (saved.ContainsKey("source") && spawned.HasPlaced && AtItsPlacement(world, entity, spawned))
            components.Remove(SaveSerializer.TransformId);
        saved["components"] = components;
        if (tags.Count > 0) saved["tags"] = tags;
        return saved;
    }

    // A cell going dormant (4g-1): these entities, and the persistent children their prefabs placed, as a
    // save writes them — in the frame the world is in now.
    internal List<JsonObject> Capture(World world, IEnumerable<Entity> roots)
    {
        var json = SaveJson.For(world, _engine.Records, _converters);
        world.Resources.TryGet<ContentBaseline>(out var baseline);
        var result = new List<JsonObject>();
        var seen = new HashSet<Entity>();
        void Write(Entity entity)
        {
            if (!seen.Add(entity) || !world.Has<Persistent>(entity)) return;
            if (WriteEntity(world, entity, json, baseline) is { } saved) result.Add(saved);
            if (entity.ChildCount == 0) return;
            foreach (var child in entity.ChildEntities)
                if (child.Tags.Has<FromParentPrefab>()) Write(child);
        }
        foreach (var entity in roots) Write(entity);
        return result;
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

    // Loads into the engine's existing worlds by reconciling (4i-3): content is placed again as it is now,
    // the save's tombstones removed from it and its state laid onto what matches by id, and what the game
    // made is spawned again from the file (Apply). Non-persistent things (terrain chunks, the ground) are
    // the world's own business and are left alone — they are rebuilt by the systems that own them.
    //
    // **A load cannot half-happen** (REDESIGN §4.5, issue 4i-2). Every world file is read, parsed and
    // upgraded, and every entity's id, prefab and name is read, before any world is touched: a save
    // that is corrupt anywhere is refused whole and the game is left as it was. What is left to do
    // after that point is laying data that has already been read onto the worlds.
    //
    // Called during a tick, it is a request and runs when the tick ends (4i-6), as Save does.
    public bool Load(string slot)
    {
        if (IsMidTick)
        {
            RequestLoad(slot);
            return true;
        }

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

        _sinceAutosave = 0;   // a load is a fresh start for the autosave clock
        Log.Info(LogCat.Save, $"Loaded '{slot}': {total} entities");
        return true;
    }

    // One world file, read and upgraded, and nothing done with it yet.
    private sealed record PreparedWorld(World World, string Where, SectorCoord? Origin, List<PreparedEntity> Entities, JsonObject? Resources,
                                        RecordId? Scene, Dictionary<string, HashSet<PersistentId>> Tombstones, bool AbsentIsDestroyed,
                                        Dictionary<string, DormantCell> Dormant);

    // `Source`: the content that placed it (format 3), else null. `Parent`: for a prefab's child the game
    // spawned, its parent's id.
    private readonly record struct PreparedEntity(PersistentId Id, RecordId Prefab, string? Name, JsonObject Saved,
                                                  string? Source, PersistentId Parent);

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

        var list = ReadEntities(entities, where);

        // Content not in the world when it was saved, with the sector its positions are relative to (4g-1).
        // Read through as the entities are, so a malformed one refuses the save before anything changes.
        var dormant = new Dictionary<string, DormantCell>(StringComparer.Ordinal);
        if (root["dormant"] is JsonObject cells)
            foreach (var (source, node) in cells)
            {
                if (node is not JsonObject cell || cell["entities"] is not JsonArray asleep)
                    throw new InvalidDataException($"{where}: dormant '{source}' has no \"entities\"");
                var frame = cell["sector"] is JsonObject sector
                    ? new SectorCoord((int?)sector["x"] ?? 0, (int?)sector["z"] ?? 0)
                    : origin ?? default;
                var read = dormant[source] = new DormantCell(frame);
                foreach (var entry in ReadEntities(asleep, $"{where}: dormant '{source}'"))
                    read.Entities.Add(entry.Saved);
            }

        RecordId? scene = (string?)root["scene"] is { Length: > 0 } sceneText ? RecordId.Parse(sceneText, "sage") : null;

        var tombstones = new Dictionary<string, HashSet<PersistentId>>(StringComparer.Ordinal);
        if (root["tombstones"] is JsonObject dead)
            foreach (var (source, ids) in dead)
            {
                if (ids is not JsonArray array) throw new InvalidDataException($"{where}: tombstones for '{source}' are not a list");
                var set = tombstones[source] = new HashSet<PersistentId>();
                foreach (var node in array)
                    if (PersistentId.TryParse((string?)node, out var id)) set.Add(id);
                    else Log.Warn(LogCat.Save, $"{where}: a tombstone for '{source}' is not an id; skipped");
            }

        bool absentIsDestroyed = root["absentIsDestroyed"] is JsonValue flag && flag.TryGetValue(out bool yes) && yes;
        return new PreparedWorld(world, where, origin, list, root["resources"] as JsonObject, scene, tombstones, absentIsDestroyed, dormant);
    }

    // Saved entities, each with its id, prefab, name, source and parent read: a malformed prefab id
    // throws (and a load refuses the save), an entity with no usable id is skipped.
    private static List<PreparedEntity> ReadEntities(IEnumerable<JsonNode?> entities, string where)
    {
        var list = new List<PreparedEntity>();
        foreach (var node in entities)
        {
            if (node is not JsonObject saved) continue;
            if (!PersistentId.TryParse((string?)saved["id"], out var id))
            {
                Log.Warn(LogCat.Save, $"{where}: an entity has no usable \"id\"; skipped");
                continue;
            }

            var prefab = (string?)saved["prefab"] is { Length: > 0 } text ? RecordId.Parse(text, "sage") : default;
            string? source = (string?)saved["source"] is { Length: > 0 } s ? s : null;
            PersistentId.TryParse((string?)saved["parent"], out var parent);
            list.Add(new PreparedEntity(id, prefab, (string?)saved["name"], saved, source, parent));
        }
        return list;
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

        var baseline = ContentIds.Baseline(world);

        // **Out with what the game made** (4i-3). Everything persistent that content did not place — the
        // player, what was dropped or summoned, the last load's placeholders (disabled, so asked for by
        // name) — belonged to the game being left, and the file says what of it to bring back.
        foreach (var entity in world.PersistentIncludingDisabled().ToEntityList())
            if (world.IsAlive(entity) && !baseline.IsBaseline(entity.GetComponent<Persistent>().Id))
                world.Destroy(entity);
        // And what it made that no save names (4m-4): a `"persist": false` spawn (an effect's spark, a cue's
        // prop) is not in this file either, so it goes too. What the engine and the game make for themselves
        // (cameras, a viewmodel, a level's brushes) is not tagged Unsaved and stays.
        int unsaved = 0;
        foreach (var entity in world.UnsavedIncludingDisabled().ToEntityList())
            if (world.IsAlive(entity)) { world.Destroy(entity); unsaved++; }
        if (unsaved > 0) Log.Debug(LogCat.Save, $"{where}: {unsaved} unsaved runtime spawn(s) removed");
        world.FlushCommands();
        // The handles that stood for sleeping entities were the game's being left (4m-4); the file's
        // references to what sleeps in it are minted again as they are read.
        if (world.Resources.TryGet<SleepingHandles>(out var sleeping) && sleeping != null) sleeping.Clear();

        // **Content, placed again from what the game has now**: the save's scene (or the world's own, when
        // the save names none or one this game does not have), its documents and levels, and levels a
        // person loaded by hand. The save's tombstones wait for their sources, and each source removes its
        // own as it is placed (ContentBaseline.Finish) — the same path a hot reload takes.
        var scene = CurrentScene(world);
        if (file.Scene is { } savedScene)
        {
            if (_engine.Records.TryGet(savedScene, out SceneRecord _)) scene = savedScene;
            else Log.Warn(LogCat.Save, $"{where}: the save's scene '{savedScene}' is not in this game; the world stays in '{scene}'");
        }
        // A streamed scene's content is kept by sector (4g-3): what an older save, or one from before the
        // scene was streamed, holds under `scene:` is moved to the sector that places it now.
        if (_engine.Scenes.StreamedFor(world, scene) is { } streamed) Rekey(file, streamed);
        _engine.Scenes.Replace(world, scene, () => baseline.Reset(file.Tombstones, file.Dormant), levels: true);
        MapLoader.EnsureEntities(world);
        world.FlushCommands();

        // A save before format 3 had no tombstones: it wrote every persistent entity, so a scene placement
        // it does not list is one the game destroyed (a child had no id then, and goes with its parent).
        if (file.AbsentIsDestroyed)
        {
            var listed = file.Entities.Select(e => e.Id).ToHashSet();
            foreach (var source in baseline.LiveSources.Where(s => s.StartsWith("scene:", StringComparison.Ordinal)).ToList())
                foreach (var id in baseline.PlacedBy(source).ToList())
                    if (world.Resolve(id) is { IsNull: false } placed && !listed.Contains(id) && !placed.Tags.Has<FromParentPrefab>())
                        world.Destroy(placed);
            world.FlushCommands();
        }

        var dialect = SaveJson.For(world, _engine.Records, _converters);
        int total = Restore(world, file.Entities, dialect, where, baseline);

        // The scene's player is the save's, with its wiring (issues #29, #90) — before the resources,
        // because entity I/O's puts back how often each wire fired.
        _engine.Scenes.AfterLoad(world);

        ReadResources(world, file.Resources, dialect, where);

        world.FlushCommands();
        return total;
    }

    // A save's `scene:<id>` content, for a scene that streams now (4g-3): ids keep 4i's formula, so each is
    // found in the sector that places it. Tombstones and the state of what content placed move to that
    // sector's source; a runtime spawn's cell becomes the sector it stands in; a dormant `scene:` cell is
    // split by sector. A format 2 save had no tombstones: a placement it does not list was destroyed, and
    // is tombstoned in its sector here, since no sector is placed while the load runs. What the content no
    // longer places is left as it was, and dropped as any load drops it.
    private static void Rekey(PreparedWorld file, StreamedScene streamed)
    {
        string old = ContentIds.SceneSource(streamed.Scene);
        var sources = streamed.ContentSources(out var roots);

        void Tombstone(string source, PersistentId id)
        {
            if (!file.Tombstones.TryGetValue(source, out var set)) file.Tombstones[source] = set = new HashSet<PersistentId>();
            set.Add(id);
        }

        int moved = 0;
        if (file.Tombstones.Remove(old, out var dead))
            foreach (var id in dead)
                if (sources.TryGetValue(id, out var source)) { Tombstone(source, id); moved++; }
        if (file.AbsentIsDestroyed)
        {
            var listed = file.Entities.Select(e => e.Id).ToHashSet();
            foreach (var id in roots)
                if (!listed.Contains(id)) { Tombstone(sources[id], id); moved++; }
        }

        // Live entities: content's to their sector, runtime spawns' cells by where they stand.
        var frame = file.Origin ?? default;
        for (int i = 0; i < file.Entities.Count; i++)
        {
            var entry = file.Entities[i];
            if ((entry.Source == old || (entry.Source == null && file.AbsentIsDestroyed)) && sources.TryGetValue(entry.Id, out var source))
            {
                entry.Saved["source"] = source;
                file.Entities[i] = entry with { Source = source };
                moved++;
            }
            else if (entry.Source == null && entry.Parent.IsEmpty && RekeyCell(entry.Saved, old, frame, streamed)) moved++;
        }

        // A dormant `scene:` cell (the player had left the scene when it was saved): each entry to its sector's
        // cell, a prefab child to its parent's.
        if (file.Dormant.Remove(old, out var cell))
        {
            var placedIn = new Dictionary<PersistentId, string>();
            var children = new List<JsonObject>();
            void Into(string source, JsonObject saved)
            {
                if (!file.Dormant.TryGetValue(source, out var into)) file.Dormant[source] = into = new DormantCell(cell.Frame);
                into.Add(saved, cell.Frame);
                moved++;
            }
            foreach (var saved in cell.Entities)
            {
                PersistentId.TryParse((string?)saved["id"], out var id);
                if (saved.ContainsKey("parent")) { children.Add(saved); continue; }
                string source;
                if (sources.TryGetValue(id, out var content))
                {
                    source = content;
                    saved["source"] = source;
                }
                else
                {
                    Cells.TryPosition(saved, out var at);
                    var absolute = at + cell.Frame.Origin(Terrain.SectorSize);
                    source = streamed.SourceOf(Terrain.SectorOf(absolute.X, absolute.Z));
                    if (Cells.CellOf(saved) == old) Cells.SetCell(saved, source);
                }
                placedIn[id] = source;
                Into(source, saved);
            }
            foreach (var saved in children)
            {
                PersistentId.TryParse((string?)saved["parent"], out var parent);
                PersistentId.TryParse((string?)saved["id"], out var id);
                var source = placedIn.TryGetValue(parent, out var p) ? p : sources.TryGetValue(id, out var c) ? c : old;
                placedIn[id] = source;
                Into(source, saved);
            }
        }
        if (moved > 0) Log.Info(LogCat.Save, $"{file.Where}: {moved} entr(ies) of '{old}' moved to its sectors (it streams now)");
    }

    // A runtime spawn the save gave the scene's cell (From3) belongs to the sector it stands in.
    private static bool RekeyCell(JsonObject saved, string old, SectorCoord frame, StreamedScene streamed)
    {
        if (Cells.CellOf(saved) != old || !Cells.TryPosition(saved, out var at)) return false;
        var absolute = at + frame.Origin(Terrain.SectorSize);
        Cells.SetCell(saved, streamed.SourceOf(Terrain.SectorOf(absolute.X, absolute.Z)));
        return true;
    }

    // The spawn path a load's first pass and a cell waking share (4g-1): saved entities into a world whose
    // content is placed. Returns how many were laid onto something.
    private int Restore(World world, IReadOnlyList<PreparedEntity> entries, JsonSerializerOptions dialect, string where, ContentBaseline baseline)
    {
        // **Pass one: every entity, with its identity, and nothing else.** Components come after, so
        // that by the time one mentions another entity that entity exists and the reference resolves
        // as it is read. Two passes instead of a fix-up list, and nesting comes free.
        //
        // An entity already here by its id is content's, placed again just now: the save is laid onto it
        // (`matched`). One content placed that is not here was either removed from the content since
        // (its source is here: it is dropped) or belongs to content not in the world yet (kept, pending).
        // Anything else the game made, and is spawned again from its prefab — with its prefab's children,
        // which find their saved state by their derived ids rather than being spawned a second time.
        var rebuilt = new List<(Entity Entity, JsonObject Saved, bool Matched)>();
        var placeholders = new List<(Entity Entity, JsonObject Saved)>();
        var children = new List<PreparedEntity>();
        int dropped = 0;
        foreach (var entry in entries)
        {
            var (id, prefab, name, saved, source, parent) = entry;
            if (!parent.IsEmpty && source is null) { children.Add(entry); continue; }

            var existing = world.Resolve(id);
            if (!existing.IsNull) { rebuilt.Add((existing, saved, true)); continue; }
            if (source != null)
            {
                if (baseline.IsLive(source)) dropped++;
                else baseline.AddPendingState(source, saved, world.Origin().Sector);
                continue;
            }

            // Spawned from its prefab so the *parts* come back — a character's capsule, controller and
            // intent were never in the save because the prefab puts them there (F31). Spawned where it was
            // saved, so a part that reads its placement (a mover's closed position, a character's yaw)
            // reads that rather than the origin, as it did when its diff was taken (4i-5).
            Entity entity = default;
            if (prefab.IsEmpty) entity = world.Create(Transform.Identity);
            else if (_engine.Records.TryGet(prefab, out PrefabRecord _))
                entity = world.SpawnWithoutId(prefab, SavedTransform(saved, dialect, where));

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
            else rebuilt.Add((entity, saved, false));

            ContentIds.Assign(world, entity, id);   // and its prefab's children theirs, derived from it
            // Entity.Name adds the name when the entity has none (Friflo's own setter threw on an entity
            // without one, which is every prefab-less entity: a golden save found it, #20).
            if (name is { Length: > 0 })
                entity.Name = name;
        }
        world.FlushCommands();

        // A prefab's children, found under the parents spawned above.
        foreach (var (id, _, name, saved, _, _) in children)
        {
            var child = world.Resolve(id);
            if (!child.IsNull) rebuilt.Add((child, saved, true));
            else Log.Debug(LogCat.Save, $"{where}: {name ?? id.ToString()} is not among its parent's prefab's children any more; dropped");
        }
        if (dropped > 0)
            Log.Info(LogCat.Save, $"{where}: {dropped} saved entit(ies) the content no longer places were dropped");

        // Pass two: the state.
        foreach (var (entity, saved, matched) in rebuilt)
            LayState(world, entity, saved, matched, dialect, where);

        // A placeholder stands where the entity stood (for a tool that looks), and then out of sight.
        foreach (var (entity, saved) in placeholders)
        {
            if (saved["components"] is JsonObject components && components["sage:transform"] is JsonNode transform)
                _serializer.ReadComponents(world, entity, new JsonObject { ["sage:transform"] = transform.DeepClone() }, dialect, where);
            World.SetEnabled(entity, false);
        }

        world.FlushCommands();
        return rebuilt.Count + placeholders.Count;
    }

    private static RecordId CurrentScene(World world) =>
        world.Resources.TryGet<ActiveScene>(out var active) && active != null ? active.Id : default;

    // The transform a saved entity stood at, or the identity when it has none that reads.
    private Transform SavedTransform(JsonObject saved, JsonSerializerOptions dialect, string where)
    {
        if (saved["components"] is not JsonObject components || components[SaveSerializer.TransformId] is not JsonObject entry)
            return Transform.Identity;
        int version = _engine.Components.DeclarationOf(typeof(Transform))?.Version ?? 1;
        var value = SaveSerializer.ReadEntry(typeof(Transform), SaveSerializer.TransformId, version, entry.DeepClone(), dialect,
                                             $"{where}: component '{SaveSerializer.TransformId}'");
        return value is Transform transform ? transform : Transform.Identity;
    }

    // One saved entity's state onto an entity that exists. `matched`: content placed it (or its parent's
    // prefab did), so its tags are the save's exactly — a tag the game took off stays off. Its components
    // are laid over what content gave it; one the save does not have is left as content made it.
    //
    // A diffed entity (`"diff": true`, 4i-5): its prefab, as it is now, has already been applied — by the
    // spawn or by content placing it — and each saved entry is laid over that field by field; its
    // `removed` are taken off; its tags are the save's exactly, as a matched entity's are.
    private void LayState(World world, Entity entity, JsonObject saved, bool matched, JsonSerializerOptions dialect, string where)
    {
        var unknown = new JsonObject();
        var unknownTags = new JsonArray();
        var components = saved["components"] as JsonObject;
        bool diff = saved["diff"] is JsonValue flag && flag.TryGetValue(out bool yes) && yes;
        if (components != null)
            _serializer.ReadComponents(world, entity, components, dialect, where, unknown, merge: diff);
        if (diff && saved["removed"] is JsonArray removed)
            _serializer.RemoveComponents(entity, removed);
        if (matched || diff) _serializer.ClearTags(entity);
        if (saved["tags"] is JsonArray tags)
            _serializer.ReadTags(world, entity, tags, where, unknownTags);
        if (unknown.Count > 0 || unknownTags.Count > 0)
        {
            world.Remove<UnknownSavedData>(entity);
            world.Add(entity, new UnknownSavedData { Components = unknown.ToJsonString(), Tags = unknownTags.ToJsonString() });
        }

        // Where it was, and both poses with it: a teleport, so a physics body made (or moved) from them
        // starts there rather than where the content put it.
        if (components?.ContainsKey("sage:transform") == true && world.Has<Transform>(entity))
            world.Teleport(entity, world.Get<Transform>(entity));
    }

    // A dormant cell's source is placed again (4g-1, ContentBaseline.Finish): its state, brought into the
    // frame the world is in now, is laid onto what the source placed by id, and its runtime spawns are
    // spawned again — the load's own path. What the source no longer places is dropped.
    internal void Wake(World world, string source, DormantCell cell)
    {
        if (cell.Entities.Count == 0) return;
        cell.MoveTo(world.Origin().Sector);
        string where = $"dormant {source}";
        var entries = ReadEntities(cell.Entities.ToArray(), where);
        cell.Entities.Clear();
        int count = Restore(world, entries, SaveJson.For(world, _engine.Records, _converters), where, ContentIds.Baseline(world));
        Log.Debug(LogCat.Save, $"{source}: woke with {count} entit(ies)");
    }

    // An entity something kept out of the world spawned again (4g-6, CellContent.Restore): its save entries
    // (the root's first, then the children its prefab placed), the root put at `position` in the frame the
    // world is in now, through the load's own path. The root, or null when an entity with its id is already
    // in the world (nothing is spawned twice) or it could not be rebuilt.
    internal Entity Revive(World world, IReadOnlyList<JsonObject> saved, System.Numerics.Vector3 position, string where)
    {
        if (saved.Count == 0 || saved[0]["id"] is not JsonValue idValue || !idValue.TryGetValue(out string? text)
            || !PersistentId.TryParse(text, out var id)) return default;
        if (!world.Resolve(id).IsNull)
        {
            Log.Warn(LogCat.Save, $"{where}: {text} is already in the world; not spawned a second time");
            return default;
        }
        var entries = new JsonNode?[saved.Count];
        for (int i = 0; i < saved.Count; i++) entries[i] = saved[i].DeepClone();
        Cells.SetPosition((JsonObject)entries[0]!, position);
        Restore(world, ReadEntities(entries, where), SaveJson.For(world, _engine.Records, _converters), where, ContentIds.Baseline(world));
        return world.Resolve(id);
    }

    // ---- what a prefab spawned as (4i-5) --------------------------------------------------------------

    // Called by a prefab spawn once the prefab (with `overrides`) is applied and before anything else is
    // done to the entity: notes its components as they are now as the baseline a save diffs it against
    // (SaveDiff). Taken once per prefab and overrides in each world, and shared.
    internal void NoteSpawned(World world, Entity entity, RecordId prefab, PrefabRecord record, PrefabOverrides? overrides)
    {
        if (!world.Has<FromPrefab>(entity)) return;
        var baselines = world.Resources.GetOrAdd(() => new PrefabBaselines());
        baselines.Since(_recordsLoaded);
        string key = PrefabBaselines.KeyOf(overrides);
        if (!baselines.TryGet(prefab, record, key, out var baseline))
        {
            baselines.Dialect ??= SaveJson.For(world, _engine.Records, _converters);
            baseline = new SpawnBaseline(_serializer.WriteComponents(world, entity, baselines.Dialect, baseline: null, removed: null, quiet: true),
                                         _serializer.FromPlacementIds(entity));
            baselines.Add(prefab, record, key, baseline);
        }
        // What derives from where this one was placed is this one's own (4m-4): only those components.
        else if (baseline.FromPlacement.Count > 0)
            baseline = new SpawnBaseline(baseline, _serializer.WriteComponents(world, entity, baselines.Dialect!, baseline: null, removed: null,
                                                                                quiet: true, only: baseline.FromPlacement));
        ref var from = ref world.Get<FromPrefab>(entity);
        from.Baseline = baseline;
        // And where it was placed, so content's entity still standing there is not written with a transform.
        from.Placed = world.Get<Transform>(entity);
        from.PlacedFrame = FrameOf(world);
        from.HasPlaced = true;
    }

    private static SectorCoord FrameOf(World world) =>
        world.Resources.TryGet<Origin>(out var origin) && origin != null ? origin.Sector : default;

    // Whether content's entity stands where it was placed (4m-4): then a save writes no transform for it,
    // and a load leaves it where the content puts it now. Compared in absolute space, so a rebase between
    // the spawn and the save does not count as a move.
    private static bool AtItsPlacement(World world, Entity entity, in FromPrefab from)
    {
        if (!from.HasPlaced || !world.TryGet<Transform>(entity, out var now)) return false;
        var was = from.Placed;
        var position = now.LocalPosition;
        var placed = was.LocalPosition;
        if (entity.Parent.IsNull)   // a root's position is in the world's frame; a child's is its parent's
        {
            position += FrameOf(world).Origin(Terrain.SectorSize);
            placed += from.PlacedFrame.Origin(Terrain.SectorSize);
        }
        const float Epsilon = 1e-4f;
        return Vector3.DistanceSquared(position, placed) <= Epsilon * Epsilon
               && MathF.Abs(MathF.Abs(Quaternion.Dot(now.LocalRotation, was.LocalRotation)) - 1f) <= Epsilon
               && Vector3.DistanceSquared(now.LocalScale, was.LocalScale) <= Epsilon * Epsilon;
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
    // A mod's mount (`mods/<id>`) is left out: the header's "mods" says it, with the version.
    private IEnumerable<SavedContent> CurrentContent() =>
        _engine.Vfs.Mounts.Where(m => !IsModMount(m.Name)).Select(m => new SavedContent(m.Name, m.RecordNamespace));

    private static bool IsModMount(string mount) => mount.StartsWith("mods/", StringComparison.OrdinalIgnoreCase);

    // The active mods now, in load order (Engine.Mods, phase 4j).
#pragma warning disable SAGE0132 // Engine.Mods is experimental in the same phase as the header's mods
    private IEnumerable<SavedMod> CurrentMods() =>
        _engine.Mods.Active.Select(m => new SavedMod(m.Id, m.Version));
#pragma warning restore SAGE0132

    internal static List<SavedMod>? ReadMods(JsonObject header)
    {
        if (header["mods"] is not JsonArray list) return null;   // written before 4j-4: nothing to compare
        var mods = new List<SavedMod>();
        foreach (var node in list)
            if (node is JsonObject m && m["id"] is JsonValue id && id.TryGetValue(out string? text) && text is { Length: > 0 })
                mods.Add(new SavedMod(text, m["version"] is JsonValue v && v.TryGetValue(out string? version) ? version ?? "" : ""));
        return mods;
    }

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
    private List<string> Mismatches(JsonObject header) => Mismatches(ReadPlugins(header), ReadContent(header), ReadMods(header));

    private List<string> Mismatches(IReadOnlyList<SavedPlugin>? plugins, IReadOnlyList<SavedContent>? content, IReadOnlyList<SavedMod>? mods)
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
            var then = content.Where(c => !IsModMount(c.Mount)).Select(c => c.Mount).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var c in content)
                if (!IsModMount(c.Mount) && !now.Contains(c.Mount)) result.Add($"content '{c.Mount}' is not mounted");
            foreach (var mount in now)
                if (!then.Contains(mount)) result.Add($"content '{mount}' was not mounted then");
        }
        if (mods != null)
        {
            var now = CurrentMods().ToDictionary(m => m.Id, m => m.Version, StringComparer.OrdinalIgnoreCase);
            var then = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in mods)
            {
                then.Add(m.Id);
                if (!now.TryGetValue(m.Id, out var version)) result.Add($"mod '{m.Id}' {m.Version} is not active");
                else if (!string.Equals(version, m.Version, StringComparison.Ordinal))
                    result.Add($"mod '{m.Id}' was {m.Version} and is {version}");
            }
            foreach (var (id, _) in now)
                if (!then.Contains(id)) result.Add($"mod '{id}' was not active then");
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
                case 2: From2(root); break;
                case 3: From3(root); break;
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

    // Format 2 → 3 (issue 4i-3). A format 2 file lists every persistent entity there was and has no
    // tombstones: what the scene placed and the file does not list had been destroyed. Marked so, and the
    // load removes those after placing the scene again. Its entities name no `source`; the ones content
    // placed are found by their ids all the same (a scene placement's formula has not changed).
    private static void From2(JsonObject root) => root["absentIsDestroyed"] = true;

    // Format 3 → 4 (issue 4g-1). A format 3 file has no `dormant`: what it holds for content not in the
    // world (a level waiting for its ground) is in `entities` with its `source`, in the file's frame, and a
    // load keeps it pending as before. What is new is that a runtime spawn belongs to a cell: one in a
    // format 3 file was made in the file's scene (nothing left a scene with its spawns before 4g-1), so it is
    // given that scene's cell and goes dormant when the player leaves the scene. The player is not — nor the
    // camera that follows it, nor a prefab's child (it goes with its parent).
    private static void From3(JsonObject root)
    {
        if ((string?)root["scene"] is not { Length: > 0 } scene || root["entities"] is not JsonArray entities) return;
        string cell = "scene:" + scene;
        string player = PersistentId.FromName($"scene:{scene}:player").ToString();
        foreach (var node in entities)
        {
            if (node is not JsonObject entity || entity.ContainsKey("source") || entity.ContainsKey("parent")) continue;
            if ((string?)entity["id"] == player) continue;
            if (entity["tags"] is JsonArray tags && tags.Any(t => (string?)t is Cells.PlayerControlledId or Cells.PlayerCameraId or Cells.FromParentPrefabId))
                continue;
            if (entity["components"] is not JsonObject components || components.ContainsKey(Cells.CellId)) continue;
            components[Cells.CellId] = SaveSerializer.Entry(1, new JsonObject { [nameof(InCell.Source)] = cell });
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
            var kind = SaveKind.Manual;
            List<SavedPlugin>? plugins = null;
            List<SavedContent>? content = null;
            List<SavedMod>? mods = null;
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
                    kind = ReadKind(header);
                    plugins = ReadPlugins(header);
                    content = ReadContent(header);
                    mods = ReadMods(header);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
            {
                /* a broken header still lists, as one that cannot be loaded */
            }
            slots.Add(new SaveSlot(name, saved, format, game, engine, kind,
                plugins ?? new List<SavedPlugin>(), content ?? new List<SavedContent>(),
                mods ?? new List<SavedMod>(), Mismatches(plugins, content, mods)));
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
        cvars.RegisterCommand("save", CVarFlags.None, "save [slot]: write a save (no slot: a quick-save, as quicksave).", a =>
        {
            if (a.Count > 0) Save(a[0], SaveKind.Manual);
            else Save(QuickSlot, SaveKind.Quick);
        });

        cvars.RegisterCommand("load", CVarFlags.None, "load [slot]: read a save back (no slot: the quick-save).", a =>
            Load(a.Count > 0 ? a[0] : QuickSlot));

        RegisterQuickCommands(cvars);

        cvars.RegisterCommand("saves", CVarFlags.None, "What saves exist.", _ =>
        {
            int n = 0;
            Rescan();
            var kinds = Slots.ToDictionary(s => s.Name, s => KindName(s.Kind), StringComparer.Ordinal);
            foreach (var (slot, saved, entities) in List())
            {
                string when = saved == default ? "unknown" : saved.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                Log.Info(LogCat.Console, $"  {slot,-20} {kinds.GetValueOrDefault(slot, "manual"),-7} {when}   {entities} entities");
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
    internal SaveSlot(string name, DateTime savedUtc, int formatVersion, string game, string engineVersion, SaveKind kind,
                      IReadOnlyList<SavedPlugin> plugins, IReadOnlyList<SavedContent> content,
                      IReadOnlyList<SavedMod> mods, IReadOnlyList<string> mismatches)
    {
        Name = name;
        SavedUtc = savedUtc;
        FormatVersion = formatVersion;
        Game = game;
        EngineVersion = engineVersion;
        Kind = kind;
        Plugins = plugins;
        Content = content;
        Mods = mods;
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

    // Who asked for it (issue 4i-6): a quick-save, an autosave or a save the player named. A save from
    // before headers said is Manual.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public SaveKind Kind { get; }

    // The runtime plugins (id and version) and the content mounts that were loaded when it was written
    // (issue 4i-2). Empty for a save from before headers listed them.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public IReadOnlyList<SavedPlugin> Plugins { get; }

    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public IReadOnlyList<SavedContent> Content { get; }

    // The data mods (id and version, in load order) that were active when it was written (phase 4j-4).
    // Empty for a save from before headers listed them.
    [Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public IReadOnlyList<SavedMod> Mods { get; }

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

// A data mod as a save's header lists it (phase 4j-4): its id and its version when the save was written.
[Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public readonly record struct SavedMod(string Id, string Version);
