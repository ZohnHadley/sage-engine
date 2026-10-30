#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using F = Friflo.Engine.ECS;

namespace Sage.Simulation;

// A save's entities, captured on the tick and written on the writer's thread (issue #285).
//
// The first background save (SaveBackground) still built every entity's JSON on the tick, leaving only the
// component values to the writer: 10k crates cost the tick 60 ms and more, which is not "does not stall a
// frame". Now the tick only *copies*: for each archetype that holds persistent entities, every component
// column it saves is copied whole (Friflo keeps an archetype's components in one array per type, so a copy
// is a memcpy), with its entity ids, ids, prefabs, names, placeholders and unknown data. The writer turns
// the copies into the entities' JSON — ids as text, the diff against the prefab, tags, the lot — exactly as
// a save between ticks does, because it is the same code (BuildEntity): a save written now and one written
// in the background are the same file.
//
// **What stays on the tick** is what needs the world, and only where an entity has it:
// - a component whose type is not values and strings all the way down (SaveSerializer.CanDefer) and cannot be
//   captured either: one with a dictionary in it, a class, a plugin converter that is not an ISaveCapture —
//   serialised there and then, per entity. Entity references, lists, arrays and gameplay's attributes and
//   tags no longer are (issue #602, CapturePlan): the column is copied, then what in it the game could
//   change (a list, an attribute set) is copied row by row, and the writer writes it in a dialect of its own
//   that reads nothing of the world — an entity as the persistent id the tick gave it, an attribute by the
//   name it had then;
// - where content placed it (ContentBaseline, one lookup an entity), its parent's id for a child its
//   parent's prefab placed, its kept placement's overrides and wires (KeptPlacement), and whether content's
//   entity still stands where it was placed;
// - a copy of each prefab baseline the entities are diffed against: once a prefab, not once an entity
//   (SpawnBaseline.CopyForWriter), because a JSON tree is not safe to read from two threads.
// So the state a save holds is the tick's, all of it: nothing the writer reads is the world's.
public sealed partial class SaveSystem
{
    // One component type of a batch: its values copied (a type the writer can serialise alone), or its
    // entries serialised on the tick (null: it could not be, and the log said so).
    internal sealed class CapturedColumn
    {
        public CapturedColumn(ComponentDeclaration declaration, Array? values, JsonNode?[]? written, JsonSerializerOptions? json = null)
        {
            Declaration = declaration;
            Values = values;
            Written = written;
            Json = json;
        }

        public ComponentDeclaration Declaration { get; }
        public Array? Values { get; }
        public JsonNode?[]? Written { get; }
        // The dialect its values are written in when it is not the world's: the writer's (CapturePlan, #602).
        public JsonSerializerOptions? Json { get; }
    }

    // Entities with the same components and tags (one archetype, or one entity), captured together: a row
    // per entity in every array.
    internal sealed class CapturedBatch
    {
        public required Persistent[] Persistent { get; init; }
        public FromPrefab[]? From { get; init; }
        public SpawnBaseline?[]? Baselines { get; init; }   // the writer's own copies
        public string?[]? Names { get; init; }
        public string?[]? Placeholders { get; init; }       // SavePlaceholder.Saved
        public UnknownSavedData[]? Unknown { get; init; }
        public string?[]? Sources { get; init; }
        public string?[]? Parents { get; init; }
        public JsonObject?[]? Kept { get; init; }           // KeptPlacement's entries
        public bool[]? AtPlacement { get; init; }
        public required List<CapturedColumn> Columns { get; init; }
        public required HashSet<string> Has { get; init; }  // every saved component id it has
        public required List<string> Tags { get; init; }
        public int[]? EntityIds { get; init; }               // the entities' ids in the store (a whole archetype)
        public int Count => Persistent.Length;
    }

    // One world's entities as captured: the batches, and how many components were serialised on the tick.
    internal sealed class CapturedEntities
    {
        public List<CapturedBatch> Batches { get; } = new();
        public int Entities;
        public int SerialisedOnTick;
    }

    private sealed class CaptureContext
    {
        public CaptureContext(World world, JsonSerializerOptions json, ContentBaseline? content, bool forWriter = false)
        {
            World = world;
            Json = json;
            Content = content;
            ForWriter = forWriter;
        }

        public World World { get; }
        public JsonSerializerOptions Json { get; }
        public ContentBaseline? Content { get; }
        public Dictionary<SpawnBaseline, SpawnBaseline> Copies { get; } = new(ReferenceEqualityComparer.Instance);
        public int SerialisedOnTick;

        // Built later, on another thread (a save's writer): a component that needs the world is captured for
        // the writer's dialect where it can be (CapturePlan, issue #602), not serialised now. False: built at
        // once, on the tick (a cell going dormant), where the world's dialect is the one to use.
        public bool ForWriter { get; }
        public CapturedEntityIds? Ids;
        public bool NeedsIds;
        private JsonSerializerOptions? _writer;

        public JsonSerializerOptions Writer => _writer ??= SaveJson.ForWriter(Json, Ids ??= new CapturedEntityIds());
    }

    // ---- on the tick --------------------------------------------------------------------------------

    // Every persistent entity of the world, disabled ones included, archetype by archetype. `forWriter`: the
    // entities are built later, by the writer (CaptureContext.ForWriter).
    private CapturedEntities CaptureEntities(World world, JsonSerializerOptions json, ContentBaseline? content, bool forWriter = true)
    {
        var context = new CaptureContext(world, json, content, forWriter);
        var result = new CapturedEntities();
        foreach (var archetype in world.PersistentArchetypes().ToArray())
            if (CaptureBatch(context, archetype, only: null) is { } batch)
            {
                result.Batches.Add(batch);
                foreach (var persistent in batch.Persistent)
                    if (!persistent.Id.IsEmpty) result.Entities++;
            }
        // Something captured names entities: their persistent ids now, for the writer's entity converter.
        if (context.NeedsIds && context.Ids is { } ids)
        {
            var store = world.Store;
            foreach (var batch in result.Batches)
                for (int i = 0; i < batch.Count; i++)
                    if (!batch.Persistent[i].Id.IsEmpty)
                        ids.Live[new Entity(store.GetEntityById(batch.EntityIds![i]))] = batch.Persistent[i].Id;
            if (world.Resources.TryGet<SleepingHandles>(out var asleep) && asleep is { Count: > 0 })
                ids.Asleep = asleep.Copy();
        }
        result.SerialisedOnTick = context.SerialisedOnTick;
        return result;
    }

    // `only`: these entities of the archetype (one, for a cell going dormant); null: all of it, by columns.
    private CapturedBatch? CaptureBatch(CaptureContext context, F.Archetype archetype, F.Entity[]? only)
    {
        int count = only?.Length ?? archetype.Count;
        if (count == 0) return null;
        var types = archetype.ComponentTypes;
        var store = (F.EntityStore)archetype.Store;
        int[]? ids = only == null ? archetype.EntityIds.ToArray() : null;
        F.Entity At(int row) => only != null ? only[row] : store.GetEntityById(ids![row]);

        var persistent = Column<Persistent>(archetype, only);
        var from = types.Has<FromPrefab>() ? Column<FromPrefab>(archetype, only) : null;
        SpawnBaseline?[]? baselines = null;
        if (from != null)
        {
            baselines = new SpawnBaseline?[count];
            SpawnBaseline? last = null, lastCopy = null;
            for (int i = 0; i < count; i++)
            {
                var baseline = from[i].Baseline;
                if (baseline == null) continue;
                if (!ReferenceEquals(baseline, last)) { last = baseline; lastCopy = baseline.CopyForWriter(context.Copies); }
                baselines[i] = lastCopy;
                from[i].Baseline = null;   // the world's: the writer reads its copy
            }
        }
        string?[]? names = null;
        if (types.Has<F.EntityName>())
        {
            var column = Column<F.EntityName>(archetype, only);
            names = new string?[count];
            for (int i = 0; i < count; i++) names[i] = column[i].value;
        }
        string?[]? placeholders = null;
        if (types.Has<SavePlaceholder>())
        {
            var column = Column<SavePlaceholder>(archetype, only);
            placeholders = new string?[count];
            for (int i = 0; i < count; i++) placeholders[i] = column[i].Saved;
        }
        var unknown = types.Has<UnknownSavedData>() ? Column<UnknownSavedData>(archetype, only) : null;

        // Where each came from: content (by id), or the parent whose prefab placed it.
        string?[]? sources = null, parents = null;
        if (context.Content is { } content)
            for (int i = 0; i < count; i++)
                if (!persistent[i].Id.IsEmpty && content.TryGetSource(persistent[i].Id, out var source))
                    (sources ??= new string?[count])[i] = source;
        if (archetype.Tags.Has<FromParentPrefab>())
            for (int i = 0; i < count; i++)
            {
                if (sources?[i] != null) continue;
                var parent = At(i).Parent;
                if (!parent.IsNull && parent.TryGetComponent<Persistent>(out var p) && !p.Id.IsEmpty)
                    (parents ??= new string?[count])[i] = p.Id.ToString();
            }
        JsonObject?[]? kept = null;
        if (types.Has<PrefabOverridden>() || types.Has<IOConnections>())
            for (int i = 0; i < count; i++)
            {
                if (sources?[i] != null || parents?[i] != null) continue;
                var entries = new JsonObject();
                KeptPlacement.Write(context.World, At(i).AsSage(), entries, _engine.Records.Json);
                if (entries.Count > 0) (kept ??= new JsonObject?[count])[i] = entries;
            }
        bool[]? atPlacement = null;
        if (sources != null && from != null)
            for (int i = 0; i < count; i++)
                if (sources[i] != null && from[i].HasPlaced && AtItsPlacement(context.World, At(i).AsSage(), from[i]))
                    (atPlacement ??= new bool[count])[i] = true;

        // The components, in the order the archetype has them (the order a save always wrote them in).
        var columns = new List<CapturedColumn>();
        var has = new HashSet<string>(StringComparer.Ordinal);
        foreach (var componentType in types)
        {
            var type = componentType.Type;
            if (!_serializer.TryDeclared(type, out var declaration) || !SaveSerializer.IsSaved(type)) continue;
            has.Add(declaration.Id);
            var values = ColumnOf(type)(archetype, only);
            if (_serializer.CanDefer(type, context.Json))
            {
                columns.Add(new CapturedColumn(declaration, values, null));
                continue;
            }
            // Not plain, but what in it the game could change can be copied (issue #602): copied now, written
            // by the writer in its dialect, which reads nothing of the world.
            if (context.ForWriter && CapturePlan.For(type, context.Json) is { } plan)
            {
                plan.Fix?.Invoke(values);
                if (plan.Entities) context.NeedsIds = true;
                columns.Add(new CapturedColumn(declaration, values, null, context.Writer));
                continue;
            }
            var written = new JsonNode?[count];
            for (int i = 0; i < count; i++)
            {
                if (persistent[i].Id.IsEmpty) continue;   // not written at all
                try { written[i] = JsonSerializer.SerializeToNode(values.GetValue(i), type, context.Json); }
                catch (Exception ex) when (ex is NotSupportedException or JsonException or ArgumentException)
                {
                    SaveSerializer.Unwritable(declaration.Id, ex);
                }
                context.SerialisedOnTick++;
            }
            columns.Add(new CapturedColumn(declaration, null, written));
        }

        return new CapturedBatch
        {
            Persistent = persistent, From = from, Baselines = baselines, Names = names, Placeholders = placeholders,
            Unknown = unknown, Sources = sources, Parents = parents, Kept = kept, AtPlacement = atPlacement,
            Columns = columns, Has = has, Tags = _serializer.SavedTags(At(0).AsSage()), EntityIds = ids,
        };
    }

    // An archetype's column of `T`, copied: whole, or the rows of `only`.
    private static T[] Column<T>(F.Archetype archetype, F.Entity[]? only) where T : struct, F.IComponent
    {
        if (only == null) return archetype.Components<T>().ToArray();
        var result = new T[only.Length];
        for (int i = 0; i < only.Length; i++) result[i] = only[i].GetComponent<T>();
        return result;
    }

    // `Column<T>` for a type known at run time, made once per type.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Func<F.Archetype, F.Entity[]?, Array>> Columns = new();

    private static Func<F.Archetype, F.Entity[]?, Array> ColumnOf(Type type) =>
        Columns.GetOrAdd(type, static t =>
        {
            var method = typeof(SaveSystem).GetMethod(nameof(Column), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(t);
            var typed = method.CreateDelegate(typeof(Func<,,>).MakeGenericType(typeof(F.Archetype), typeof(F.Entity[]), t.MakeArrayType()));
            return (Func<F.Archetype, F.Entity[]?, Array>)typed;
        });

    // One entity alone (a cell going dormant, a hand-off), built at once by the caller.
    private CapturedBatch? CaptureOne(CaptureContext context, Entity entity)
    {
        var raw = entity.Raw;
        if (raw.IsNull || raw.Archetype is not { } archetype) return null;
        return CaptureBatch(context, archetype, new[] { raw });
    }

    // ---- on the writer ------------------------------------------------------------------------------

    // The entities' JSON, in the order they were captured.
    internal static JsonArray BuildEntities(CapturedEntities captured, JsonSerializerOptions json)
    {
        var entities = new JsonArray();
        foreach (var batch in captured.Batches)
            for (int i = 0; i < batch.Count; i++)
                if (BuildEntity(batch, i, json) is { } saved) entities.Add(saved);
        return entities;
    }

    // One persistent entity as a save writes it; null for one without an id. Reads nothing but the batch.
    internal static JsonObject? BuildEntity(CapturedBatch batch, int i, JsonSerializerOptions json)
    {
        var persistent = batch.Persistent[i];
        if (persistent.Id.IsEmpty) return null;

        // An entity whose prefab this game does not have goes back as it came (issue 4i-2).
        if (batch.Placeholders?[i] is { } placeholder && JsonNode.Parse(placeholder) is JsonObject kept)
        {
            kept["id"] = persistent.Id.ToString();
            return kept;
        }

        var saved = new JsonObject { ["id"] = persistent.Id.ToString() };
        // What placed it (4i-3): content, which a load places again and lays this onto; or, for a child
        // its parent's prefab placed, the parent, which brings it back when it is spawned.
        if (batch.Sources?[i] is { } source) saved["source"] = source;
        else if (batch.Parents?[i] is { } parent) saved["parent"] = parent;
        if (batch.From is { } from && !from[i].Prefab.IsEmpty)
            saved["prefab"] = from[i].Prefab.ToString();
        if (batch.Names?[i] is { Length: > 0 } named)
            saved["name"] = named;
        // A root no content places (one that left its sector, #279): what its placement said, kept.
        if (batch.Kept?[i] is { } placement)
            foreach (var (key, node) in placement.ToList())
            {
                placement.Remove(key);
                saved[key] = node;
            }
        // Spawned from a prefab: only what differs from it as spawned, and what it lost (4i-5, SaveDiff).
        var asSpawned = batch.Baselines?[i];
        var components = new JsonObject();
        foreach (var column in batch.Columns)
        {
            var declaration = column.Declaration;
            var dialect = column.Json ?? json;
            JsonNode? data;
            if (column.Values is { } values)
            {
                object value = values.GetValue(i)!;
                // Not for a captured one (#602): its Equals may read what the copy shares with the world (a
                // field the save does not write); the diff below leaves it out just the same.
                if (column.Json == null && SaveSerializer.UnchangedSinceSpawn(declaration.Id, declaration.Version, declaration.Type, value, asSpawned)) continue;
                try { data = JsonSerializer.SerializeToNode(value, declaration.Type, dialect); }
                catch (Exception ex) when (ex is NotSupportedException or JsonException or ArgumentException)
                {
                    SaveSerializer.Unwritable(declaration.Id, ex);
                    continue;
                }
            }
            else if (column.Written![i] is { } written)
            {
                data = written;
                column.Written[i] = null;   // given to this entity's tree
            }
            else continue;
            if (SaveSerializer.Diffed(declaration.Id, declaration.Version, declaration.Type, data, asSpawned, dialect) is { } entry)
                components[declaration.Id] = entry;
        }
        if (asSpawned != null)
        {
            saved["diff"] = true;
            var removed = new JsonArray();
            foreach (var (id, _) in asSpawned.Components)
                if (!batch.Has.Contains(id)) removed.Add(id);
            if (removed.Count > 0) saved["removed"] = removed;
        }
        var tags = new JsonArray();
        foreach (var tag in batch.Tags) tags.Add(tag);
        // What the save said that this game has no component or tag for, back as it was (issue 4i-2).
        if (batch.Unknown is { } unknown)
            MergeUnknown(components, tags, unknown[i]);
        // Content's entity still where content placed it (4m-4): no transform, so a load leaves it where the
        // content places it then, and moving the placement moves it. A runtime spawn always has one: it is
        // rebuilt where it stood.
        if (batch.AtPlacement?[i] == true)
            components.Remove(SaveSerializer.TransformId);
        saved["components"] = components;
        if (tags.Count > 0) saved["tags"] = tags;
        return saved;
    }
}
