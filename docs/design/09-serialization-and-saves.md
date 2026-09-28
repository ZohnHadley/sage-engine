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

> **Nothing has pressure-tested the component shapes yet (engine review 2026-09-23, item 4 → F27).**
> `Inventory.Items` and `ActiveEffects.Effects` are lists; `AIState.Target` and `ActiveEffect.Source`
> are `Entity` handles; several components hold `RecordId`s. Entity references need stable ids across
> a save and lists need ownership and versioning rules — so either build saves sooner than the roadmap
> says, or fix the contract here now so that new components are born compliant.

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
| `[SavedResource("time_of_day", Version = 1)]` | class/struct | Per-world state saved with the world (`GameRules` state, quest log, calendar). **As built:** the name only — versioning waits for the first format change, as everywhere else here |

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
- **Built (F31, 2026-09-23):** the prefab half. `world.Populate(entity, record, id)` applies a prefab
  to an entity that already exists, which is what a map load needs after it has created one with its
  saved id. Component data is read by name through `ComponentSchema`, the same path a save's
  per-component data will take — written against reflection now, against the generated readers later
  (§3.2), with the record pipeline's JSON dialect either way. See 05 "As built (prefabs)".
- Per-sector files match streaming (14) and keep map diffs small in git. **Later:** one file per placed entity (UE One File Per Actor) if team editing makes per-sector files conflict.

### 3.5 Saves: what's saved
- **Baselines** are the map files plus procedural generation from the seed (Daggerfall-style). They're never saved, only referenced by map name + seed + content hashes.
- **Rule: every sector (or interior) that has been loaded during the playthrough is saved in full**, meaning all its entities that have a `Persistent` component. Unvisited sectors cost nothing. This is simpler and more robust than tracking field-level deltas. Finer change tracking can come later if saves get large.
- **Destroyed baseline entities** in a saved sector are recorded as tombstones (their `PersistentId`). On load they're skipped when the baseline is instantiated.
- **Runtime-spawned entities** that should persist (dropped items, recruited NPCs) get a `Persistent` component when spawned. Everything else (projectiles, effects, corpses on a timer) is simply not saved.
- **Saved resources** (`[SavedResource]`): game rules state, time, quest log, faction standings, the
  spellbook of composed spells (16 §3.3). Built 2026-09-23 — see "As built (saved resources)".
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

### As built (saves v1, 2026-09-23 — F27)

- **Code:** `src/Sage.Engine/Content/SaveSystem.cs` (slots, header, temp+rename), `SaveSerializer.cs`
  (components in and out), `SaveJson.cs` (the dialect and its three world-aware converters),
  `SaveAttributes.cs` (`[Transient]`, `FromPrefab`). Tests in `tests/Sage.Tests/Content/SaveTests.cs`.
  Console: `save [slot]`, `load [slot]`, `saves`.

**Four deviations from the plan above, each deliberate.**

| Planned | Built | Why |
|---|---|---|
| `[Saved(tag)]` opt-in | **opt-out**: everything public except `[Transient]` | There is no generator to warn about a field that decided neither way, so opt-in would silently drop a field added next month. Losing the player's inventory is worse than writing a number that could have been recomputed |
| tagged binary | **JSON** | Field names are the stable identity for now. Binary is a format change, which is what §3.6's upgraders are for — and a save you can read in a text editor is worth a lot while the save system is the thing being debugged |
| visited-sector rule + tombstones | **every persistent entity, in full** | A baseline here is the game's own records, not map files. Load recreates from the file, so a destroyed entity's tombstone is simply its absence. Both come back with maps (§3.4), because that is when a baseline starts being re-instantiated underneath the save |
| the source generator | **reflection** | As with records (05 §3.5), and it goes the same way |

- **An entity is rebuilt by spawning its prefab, then laying the saved components over the top.** That
  is what `FromPrefab` records and why `Populate` was split out in F31: a character's capsule,
  controller and intent come back because the prefab puts them there, not because they were saved.
- **Loading is two passes** — every entity with its identity first, then all the state. With every
  entity already present, a reference resolves as it is *read*, so there is no fix-up list and a
  reference nested three deep (`ActiveEffect.Source`, inside a `List<>`, inside a component) works
  like any other field. The first version of this walked a component's own fields looking for
  `Entity`, which looked reasonable and silently wrote that one as null.
- **Three things are saved by name, not by the number they are stored as**, because each number is
  assigned in record load order and means something else after a content change:
  - an entity → its `PersistentId`;
  - `Attributes` → `{ "sage:health": 65 }` (base values only — current ones are recomputed);
  - `GameplayTags` → a list of tag ids (owned only — granted ones return with their effects).

  There is a test that adds an attribute record *between* save and load, shifting every index, because
  a round trip inside one process proves nothing about this.
- **Not written at all:** anything `[Transient]` (a ground normal, an input mask, a mid-swing timer),
  `GlobalTransform` (derived, and its `Previous` is a one-tick render snapshot), `PhysicsBody` (a Bepu
  handle, rebuilt from `Collider`/`RigidBody`), and `EntityName` (a name is a string on the entity, not
  a component that also carries its own UTF-8 bytes).
- **`Remaining = +∞`** is how an everlasting effect says so, so the dialect allows named floating-point
  literals. Without that, one such effect failed the *whole* component silently.
- **The origin sector is in the file** (R6, 2026-09-24). Every position written is in **origin space**,
  so a world file starts with the sector it was relative to, and loading rebases the world to it before
  placing anything. Without that, a save taken a hundred kilometres out would load its entities into
  the starting sector — the numbers would look right and the player would be standing on somebody
  else's ground.
- **Not done here:** maps, sectors, tombstones, binary, upgraders, thumbnails, autosave rotation, and
  the mod list in the header. `GameRules` state is still not saved — the mechanism exists now, and
  nothing in the Sandbox's rules has state worth keeping yet.

### As built (saved resources, F21/F27, 2026-09-23)
A world is not only its entities. The first thing that proved it was the spellmaker (16 §3.3): the
spells a player composed are the *world's*, not any one entity's.

- **`[SavedResource("name")]`** on a world-resource class (03 §3.4), and
  `engine.Saves.RegisterResource<T>()` in the owning module's `Init` — explicit, exactly as record
  types are registered, so a mod's resource is only in the save when the game that reads it back knows
  what to do with it. The attribute's *name* is the key in the file, so renaming the class is free.
- Written into the world file beside `entities`, read back in the same JSON dialect, and installed with
  `world.Resources.Set`.
- **A load replaces every registered resource, including the ones the file says nothing about.** Absent
  means "you had none", not "keep what you have" — otherwise a spell composed since the save would
  survive loading, which is a different game.
- **`ISavedResource.AfterLoad(world)`** for a resource whose data implies something to rebuild. The
  spellbook's drafts become `ability` records again through exactly the code path that composes one in
  the first place, so **no `AbilityRecord` is ever serialized** and a rebalanced effect changes a
  player's old spell instead of being frozen into it (records made at run time: 05 "As built").

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

public sealed class SaveSystem                             // per Engine; as built (2026-09-23)
{
    public string Root { get; set; }                       // default user://saves; settable for tools and tests
    public bool Save(string slot);                         // every world in the engine, at a tick boundary
    public bool Load(string slot);                         // rebuilds each persistent entity from its prefab + saved state
    public bool Exists(string slot);
    public IEnumerable<(string Slot, DateTime SavedUtc, int Entities)> List();
    public void RegisterResource<T>() where T : class, new();   // a [SavedResource]; in the owning module's Init
}                                                          // richer result types (SaveResult/LoadResult) when a caller needs them

public interface ISavedResource                           // optional, for a resource that implies more
{
    void AfterLoad(World world);                          // e.g. a spellbook's drafts become records again (16 §3.3)
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
| `EntityOutlinerWindow` inspector: runtime reflection over components, and a label string per listed entity per frame (allocating — open bug #41) | Generated inspector metadata (15) |
| `EditorUI` File → New/Open/Save stubs | Map documents (15) + `SaveSystem` |
| Random `long` ids (old `EntityContext.createEntity`) | **Done (step 3):** Friflo handles at runtime + `PersistentId`/`Persistent` for anything saved or placed (03) |

## 11. v1 scope vs later
- **v1:**
  - the attributes;
  - the generator (JSON, binary, inspector metadata, registration, I/O tables, record validation);
  - maps as per-sector/interior JSON;
  - saves with the visited-sector rule, tombstones, saved resources, tagged binary, a header with mods, temp+rename;
    **As built (2026-09-23):** saved resources, the header and temp+rename are in; the visited-sector
    rule and tombstones wait for maps to exist at all; tagged binary and the mod list in the header are
    deliberately deferred (see the deviations above). What replaced the first two is simpler: every
    persistent entity written in full, so a destroyed one's tombstone is its absence;
  - `ser_check`.
- **Later:** upgraders beyond v1 (none are needed until the first format change), finer change tracking, one-file-per-entity maps, save thumbnails, compression tuning.

## 12. Multiplayer-later notes
The same generated metadata gains a `[Replicated]` flag and a quantization hint, producing delta encoders for snapshots. That's the whole reason to have one declaration (readiness rule 7).

## 13. Open questions
- If Friflo is adopted (03 §3.1), does its component storage let our generated serializers read and write components directly (by ref)? The spike must confirm (requirement E7). Friflo's own JSON serializer would then go unused.
- Binary compression: none, Deflate (built in), or a faster codec? Start with `System.IO.Compression` Brotli/Deflate at the fastest level; measure.

### Sharp edge: `[SavedResource]` saves nothing on its own
The attribute names a resource in the save file; it does **not** put it there. A type is only written and
restored once something calls `Engine.Saves.RegisterResource<T>()`, which is how the spellbook has always
worked — and how reputation, the journal and the weather were all marked saved and none of them were
(found by F40's second pass, 2026-09-24). The engine registers its own (`Weather`) where the save system
is built; a module registers its own beside the records it owns.

There is no warning for this, because nothing can tell the difference between "a resource the game does
not want saved" and "a resource somebody forgot to register". The test is the check: a save round trip
per saved resource, asserting the value comes back.

## 14. Build steps
1. Attributes + `Sage.Generators` with JSON read/write and registration; the `ser_check` round-trip test (TODO R11, R4).
2. Inspector metadata output (with 15).
3. Map files: per-sector JSON, `PersistentId`, prefab overrides (TODO F27, with 14).
4. ~~`SaveSystem`: header, saved resources, temp+rename~~ **Done 2026-09-23** (TODO F27, "As built (saves v1)"): as JSON, with every persistent entity written in full. The visited-sector rule and tombstones arrive with maps; tagged binary is a format change, which is what the upgraders in §3.6 are for.
5. I/O dispatch tables (with 04, TODO F17).
