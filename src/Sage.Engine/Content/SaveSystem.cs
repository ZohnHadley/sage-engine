#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Friflo.Engine.ECS;

namespace sage_engine;

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

    private string? _root;

    // Where saves live. Settable so a tool or a test can point it somewhere of its own rather than
    // moving the *process's* user folder, which is global and would pull it out from under anything
    // else running at the same time.
    public string Root
    {
        get => _root ??= Path.Combine(UserPaths.Root, "saves");
        set => _root = value;
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
        var json = SaveJson.For(world, _engine.Records);
        var entities = new JsonArray();

        foreach (var entity in world.Query<Persistent>().Entities)
        {
            var persistent = entity.GetComponent<Persistent>();
            if (persistent.Id.IsEmpty) continue;

            var saved = new JsonObject { ["id"] = persistent.Id.ToString() };
            if (world.TryGet<FromPrefab>(entity, out var from) && !from.Prefab.IsEmpty)
                saved["prefab"] = from.Prefab.ToString();
            if (entity.TryGetComponent<Friflo.Engine.ECS.EntityName>(out var named) && !string.IsNullOrEmpty(named.value))
                saved["name"] = named.value;
            saved["components"] = _serializer.WriteComponents(world, entity, json);
            var tags = _serializer.WriteTags(world, entity);
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
        if (resources.Count > 0) root["resources"] = resources;

        return (root.ToJsonString(Indented), entities.Count);
    }

    // ---- reading ------------------------------------------------------------------------------------

    public bool Exists(string slot) => File.Exists(Path.Combine(SlotDirectory(slot), "header.json"));

    // Loads into the engine's existing worlds: every persistent entity is removed and rebuilt from the
    // file. Non-persistent things (terrain chunks, the ground) are the world's own business and are
    // left alone — they are rebuilt by the systems that own them.
    public bool Load(string slot)
    {
        string directory = SlotDirectory(slot);
        if (!Exists(slot)) { Log.Warn(LogCat.Save, $"No save '{slot}' in {Root}"); return false; }

        try
        {
            var header = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "header.json"))) as JsonObject;
            int version = (int?)header?["formatVersion"] ?? 0;
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

            int total = 0;
            foreach (var world in _engine.Worlds)
            {
                string file = Path.Combine(directory, $"world_{Sanitise(world.Name)}.json");
                if (!File.Exists(file)) { Log.Warn(LogCat.Save, $"'{slot}' has nothing for world '{world.Name}'"); continue; }
                total += ReadWorld(world, File.ReadAllText(file), version, $"{slot}/world_{world.Name}");
            }

            foreach (var world in _engine.Worlds)
                world.Resources.Get<GameRules>().OnLoaded(world);

            Log.Info(LogCat.Save, $"Loaded '{slot}': {total} entities");
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error(LogCat.Save, $"Load '{slot}' failed: {ex.Message}");
            return false;
        }
    }

    private int ReadWorld(World world, string json, int format, string where)
    {
        if (JsonNode.Parse(json) is not JsonObject root || root["entities"] is not JsonArray entities)
        {
            Log.Error(LogCat.Save, $"{where}: no \"entities\"");
            return 0;
        }
        UpgradeWorld(root, format);

        // Back to the frame these positions were written in, *before* anything is placed in it. This
        // also brings the terrain rings and the physics world along, because that is what a rebase
        // does — the same path streaming uses when the player walks there (R6).
        if (root["origin"] is JsonObject savedOrigin)
            world.Rebase(new SectorCoord((int?)savedOrigin["x"] ?? 0, (int?)savedOrigin["z"] ?? 0));

        // Out with the old. Everything persistent is in the file, so anything here that is not was
        // destroyed before the save was taken — which is what a tombstone would have said.
        foreach (var entity in world.Query<Persistent>().Entities.ToEntityList())
            world.Destroy(entity);
        world.FlushCommands();

        // **Pass one: every entity, with its identity, and nothing else.** Components come after, so
        // that by the time one mentions another entity that entity exists and the reference resolves
        // as it is read. Two passes instead of a fix-up list, and nesting comes free.
        var rebuilt = new List<(Entity Entity, JsonObject Saved)>();
        foreach (var node in entities)
        {
            if (node is not JsonObject saved) continue;
            if (!PersistentId.TryParse((string?)saved["id"], out var id))
            {
                Log.Warn(LogCat.Save, $"{where}: an entity has no usable \"id\"; skipped");
                continue;
            }

            var prefab = saved["prefab"] is JsonValue p && (string?)p is { Length: > 0 } text
                ? RecordId.Parse(text, "sage") : default;

            // Spawned from its prefab so the *parts* come back — a character's capsule, controller and
            // intent were never in the save because the prefab puts them there (F31).
            var entity = prefab.IsEmpty ? world.Create(Transform.Identity) : world.Spawn(prefab);
            if (entity.IsNull) { Log.Warn(LogCat.Save, $"{where}: {id} could not be rebuilt from {prefab}"); continue; }

            world.Add(entity, new Persistent { Id = id });
            // Added rather than assigned when the entity has no name yet: Friflo's `Name` setter throws on
            // an entity without one, which is every prefab-less entity (a golden save found it, #20).
            if ((string?)saved["name"] is { Length: > 0 } name)
                entity.AddComponent(new Friflo.Engine.ECS.EntityName(name));
            rebuilt.Add((entity, saved));
        }
        world.FlushCommands();

        // Pass two: the state.
        var dialect = SaveJson.For(world, _engine.Records);
        foreach (var (entity, saved) in rebuilt)
        {
            if (saved["components"] is JsonObject components)
                _serializer.ReadComponents(world, entity, components, dialect, where);
            if (saved["tags"] is JsonArray tags)
                _serializer.ReadTags(world, entity, tags, where);
        }

        ReadResources(world, root["resources"] as JsonObject, dialect, where);

        world.FlushCommands();
        return rebuilt.Count;
    }

    // Every registered resource is replaced, including the ones the file says nothing about: a load
    // is a different game, and a spellbook left over from the last one would be a set of spells the
    // player never made. Absent in the file means "you had none", not "keep what you have".
    private void ReadResources(World world, JsonObject? saved, JsonSerializerOptions dialect, string where)
    {
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
