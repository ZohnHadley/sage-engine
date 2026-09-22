# 09 — Serialization and Saves

## 1. Purpose and scope
One declaration per type drives every place data is read or written:
- prefab and record JSON;
- map files;
- save games;
- the editor inspector;
- the I/O dispatch tables.

This doc defines:
- the **attribute contract**;
- the **source generator**;
- the **save model** (what's saved, where, how it's versioned);
- the **map file layout**.

Not in scope: the record merge rules (05), I/O semantics (04), streaming (14).

## 2. Research basis
- **One reflection source** (Unreal UObject/UHT) vs **Source's four declarations** of the same field (DATADESC, SendTable, prediction table, FGD): survey §1.3, §1.4, §2.2.
- **Compile-time generation instead of runtime reflection** in C#: source generators (MemoryPack, MessagePack-CSharp, Friflo's query generator). Runtime reflection allocates, is slow and hostile to AOT/trimming (survey §3.5).
- **Save versioning** like protobuf: stable field tags, never reused, explicit upgraders (survey §3.5).
- **Never save behaviour/script state:** Skyrim Papyrus orphaned-script saves (survey §1.5).
- **One file per placed entity** for merge-friendly maps: UE World Partition's One File Per Actor (survey §2.2).

## 3. Concepts

### 3.1 Attribute contract

| Attribute | On | Meaning |
|---|---|---|
| `[Component("health", Version = 1)]` | struct | Registers a component. The name is the stable identity in files (never the C# type name, so types can be renamed or moved) |
| `[Saved(1)]` | field | Persisted in saves and maps, with a **stable tag** (int). Tags are never reused, even after a field is deleted |
| `[Property(Min=…, Max=…, Category=…, Tooltip=…)]` | field | Shown and editable in the inspector (15). A field can be `[Property]` without being `[Saved]` (runtime-only debug values) |
| `[Transient]` | field | Explicitly never saved (caches, handles to subsystem data like physics bodies) |
| `[Record("item")]` | class | A record schema (05 §3.5); its fields use `[Property]`/`[Saved]` the same way |
| `[GameEvent]` | struct | Game event (04) |
| `[Output]` / `[Input("Open")]` | field / static method | Entity I/O (04) |
| `[SavedResource("time_of_day", Version = 1)]` | class/struct | Per-world state saved with the world (`GameRules` state, quest log, calendar) |

A field with neither `[Saved]` nor `[Transient]` gets a **compile-time warning** from the generator, so nothing is left out of saves by accident.

### 3.2 The source generator (`Sage.Generators`)
A Roslyn incremental generator, referenced as an analyzer by `Sage.Engine`, `Sage.Framework` and game assemblies. For each annotated type it emits:

| Output | Used by |
|---|---|
| JSON reader/writer (`Utf8JsonReader`/`Utf8JsonWriter`, with line info on errors) | prefab and record files, maps, editor documents, save debug dumps |
| Binary reader/writer (tagged fields) | saves |
| Inspector metadata: field list, types, ranges, categories, and ref getters/setters (no reflection) | editor inspector (15), `ent_dump` (03) |
| I/O dispatch table: output ids, input name → method | entity I/O (04) |
| Record validator (types, ranges, reference checks) | `RecordStore` (05) |
| `Registration.g.cs`, with a single `RegisterAll(ModuleRegistry)` | called by each assembly's module `Init` |

Generator diagnostics (compile errors): a duplicate `[Saved]` tag, a duplicate component name, an unsupported field type, a missing `[Saved]`/`[Transient]` decision (warning).

### 3.3 Supported field types
- Primitives, `string`, enums.
- `System.Numerics` `Vector2/3/4`, `Quaternion`.
- `Rgba`, `SectorCoord`, `AssetPath` (written as the path string), `RecordId` (written as `ns:name`).
- `EntityRef`: written as the target's `PersistentId`. The target must have one; otherwise a dev warning, and the reference is written as null.
- Nested structs marked `[Serializable]`, fixed-size arrays, and pooled small lists.

Deliberately **not** supported: object references, delegates, `Type`, polymorphic "any object" fields. Loading therefore never instantiates arbitrary .NET types from a file, which also closes the class of deserialization exploits `BinaryFormatter` was known for.

### 3.4 Maps
A map is a folder:
```
maps/<name>/
  map.json                 header: display name, spaces (exterior + interiors), settings, default spawn, generator seed
  sectors/<x>_<z>.json     placed entities per 1024 m sector (exterior)
  interiors/<id>.json      placed entities per interior space (14)
```
- Each placed entity: `persistentId`, optional `name` (for I/O), `prefab` (`RecordId`), the components that **override** the prefab (only differing fields), and `io` connections (04 §7).
- Per-sector files match streaming (14) and keep map diffs small in git. **Later:** one file per placed entity (UE One File Per Actor) if team editing makes per-sector files conflict.

### 3.5 Saves: what's saved
- **Baselines** are the map files plus procedural generation from the seed (Daggerfall-style). They're never saved, only referenced by map name + seed + content hashes.
- **Rule: every sector (or interior) that has been loaded during the playthrough is saved in full**, meaning all its entities that have a `Persistent` component. Unvisited sectors cost nothing. This is simpler and more robust than tracking field-level deltas. Finer change tracking can come later if saves get large.
- **Destroyed baseline entities** in a saved sector are recorded as tombstones (their `PersistentId`). On load they're skipped when the baseline is instantiated.
- **Runtime-spawned entities** that should persist (dropped items, recruited NPCs) get a `Persistent` component when spawned. Everything else (projectiles, effects, corpses on a timer) is simply not saved.
- **Saved resources** (`[SavedResource]`): game rules state, time, quest log, faction standings.
- **Never saved:**
  - system state (systems are stateless by rule, 03);
  - delegates, coroutines or scripts;
  - subsystem internals (physics bodies are rebuilt from components on load, 10);
  - render state.

  AI "what am I doing" state is component data (current schedule record id + task index, 16), so it's saved like any other field.

### 3.6 Saves: format and versioning
```
user://saves/<slot>/
  header.json      format version, game id + version, mod list with content hashes, map, seed,
                   play time, real time, player location, thumbnail file name
  world_<name>.bin tagged binary: [saved resources] [per sector/interior: tombstones + entities]
  thumb.png
```
- **Binary blocks** are `(componentName, version, length, tagged fields…)`:
  - unknown components are skipped (a removed mod's data doesn't break the load; it's logged);
  - unknown tags are skipped;
  - missing tags get default values.
- **Upgraders** handle changes that tags alone can't express (splitting a field, changing units): `[Upgrade("health", from: 1)] static void V1ToV2(ref SavedFields f)`. They're chained `v1→v2→v3` at load.
- **Mod mismatch** between the save header and the current load order: the player is warned. The load continues if possible; missing records resolve to placeholders (05 §8).
- Saves are written to a temp folder, then renamed into place, so a crash mid-save never corrupts the previous save.
- Autosaves rotate (`save_autosave_count`, default 3).

## 4. Public API sketch

```csharp
[AttributeUsage(AttributeTargets.Struct)] public sealed class ComponentAttribute : Attribute { public ComponentAttribute(string name); public int Version { get; init; } = 1; }
[AttributeUsage(AttributeTargets.Field)]  public sealed class SavedAttribute : Attribute { public SavedAttribute(int tag); }
[AttributeUsage(AttributeTargets.Field)]  public sealed class TransientAttribute : Attribute { }
[AttributeUsage(AttributeTargets.Field)]  public sealed class PropertyAttribute : Attribute { public float Min, Max; public string? Category, Tooltip; }

[Component("health", Version = 1)]
public struct Health
{
    [Saved(1), Property(Min = 0)] public float Current;
    [Saved(2), Property(Min = 1)] public float Max;
    [Transient] public float LastDamageTime;
}

public sealed class SaveSystem                            // per Engine
{
    public SaveResult Save(string slot, World world);     // at a tick boundary, never mid-tick
    public LoadResult Load(string slot);                  // tears down worlds, rebuilds from baseline + save
    public IReadOnlyList<SaveHeader> List();
}

public interface IComponentSerializer<T> where T : struct  // generated
{
    void WriteJson(Utf8JsonWriter w, in T value);
    bool ReadJson(ref Utf8JsonReader r, ref T value, in SerializeContext ctx);   // ctx: file, line, EntityRef resolution
    void WriteBinary(ref BinaryWriterRef w, in T value);
    void ReadBinary(ref BinaryReaderRef r, ref T value, int version, in SerializeContext ctx);
}
```

## 5. Lifecycle

```
save:  (tick boundary) → header → saved resources → for each visited sector/interior:
         tombstones + persistent entities (prefab id + [Saved] fields of every component) → temp → rename
load:  read header (check game, mods) → destroy worlds → create world → instantiate map baselines for
         saved sectors, skipping tombstones → apply saved entities (by PersistentId: overwrite or create)
         → saved resources → resolve EntityRefs from PersistentIds → subsystems rebuild (physics bodies, 10)
         → GameRules.OnLoaded
```

## 6. Threading and memory
- Saving snapshots component data on the main thread at a tick boundary (fast: a copy into pooled buffers). Compression and disk writing then run as a background job, so the game doesn't hitch.
- Loading happens behind a loading screen. Parsing can run in jobs; instantiation runs on the main thread.
- Generated serializers don't allocate for primitive and struct fields. Strings allocate on read (acceptable at load time).

## 7. File formats
Summarised above:
- JSON for authored data (prefab/record bodies, maps, editor documents);
- tagged binary + a JSON header for saves;
- `save_dump <slot>` converts a save to JSON for debugging.

## 8. Errors and fallbacks
| Failure | Behaviour |
|---|---|
| JSON parse/type error in a map or prefab | `Error` with file:line:field; that entity (or record) is skipped, the rest loads |
| `EntityRef` to an entity without a `PersistentId` at save time | Dev warning naming both entities; saved as null |
| Save load: an unknown component (mod removed) | Skipped, listed once in the log (`Save`, `Warn`) |
| Save load: a missing upgrader for an old version | That component uses defaults; `Error` logged |
| Corrupt save | Load aborted cleanly, `Error`; the game stays where it was |
| Crash during save | The previous save is untouched (temp + rename) |

## 9. Debug and tooling hooks
- **Commands:**
  - `save <slot>`, `load <slot>`;
  - `save_dump <slot>` (JSON);
  - `ser_check` (round-trips every component type through JSON and binary, and reports differences; dev);
  - `ent_dump` uses the generated inspector metadata (03).
- **Cvars:** `save_autosave_count`, `save_compress`.
- **Log category:** `Save`.

## 10. Mapping from today's code
| Today | Becomes |
|---|---|
| `EntityContextMenuUI` inspector: `GetType().GetProperties()` + `GetValue` per frame (runtime reflection, allocating) | Generated inspector metadata (15) |
| `EditorUI` File → New/Open/Save stubs | Map documents (15) + `SaveSystem` |
| Random `long` ids (`EntityContext.createEntity`) | `PersistentId` for anything saved or placed (03) |

## 11. v1 scope vs later
- **v1:**
  - the attributes;
  - the generator (JSON, binary, inspector metadata, registration, I/O tables, record validation);
  - maps as per-sector/interior JSON;
  - saves with the visited-sector rule, tombstones, saved resources, tagged binary, a header with mods, temp+rename;
  - `ser_check`.
- **Later:** upgraders beyond v1 (none are needed until the first format change), finer change tracking, one-file-per-entity maps, save thumbnails, compression tuning.

## 12. Multiplayer-later notes
The same generated metadata gains a `[Replicated]` flag and a quantization hint, producing delta encoders for snapshots. That's the whole reason to have one declaration (readiness rule 7).

## 13. Open questions
- If Friflo is adopted (03 §3.1), does its component storage let our generated serializers read and write components directly (by ref)? The spike must confirm (requirement E7). Friflo's own JSON serializer would then go unused.
- Binary compression: none, Deflate (built in), or a faster codec? Start with `System.IO.Compression` Brotli/Deflate at the fastest level; measure.

## 14. Build steps
1. Attributes + `Sage.Generators` with JSON read/write and registration; the `ser_check` round-trip test (TODO R11, R4).
2. Inspector metadata output (with 15).
3. Map files: per-sector JSON, `PersistentId`, prefab overrides (TODO F27, with 14).
4. `SaveSystem`: header, visited-sector rule, tombstones, saved resources, tagged binary, temp+rename (TODO F27).
5. I/O dispatch tables (with 04, TODO F17).
