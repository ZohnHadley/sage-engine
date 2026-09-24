# 05 — Assets, VFS and Data Records

## 1. Purpose and scope
Where content comes from and how it's loaded:
- the **virtual file system** (mounts, priorities, the writable user folder);
- **assets** (textures, meshes, sprite sheets, sounds, effects): identity, handles, async loading, lifetime, hot reload, placeholders;
- **data records** (items, spells, materials, input maps, prefabs…): the one pipeline for every definition, with load-order merging.

Not in scope: what the renderer does with a texture (06), effect compilation details (07), map and save files (09), mod management UI (17).

## 2. Research basis
- Layered search paths / paks: Quake 3 pk3 + `fs_game`, Source `gameinfo.txt` SearchPaths (survey §1.1, §1.3).
- Content as records with load order, and why whole-record overrides ("rule of one") cause mod conflicts: Bethesda (survey §1.5).
- Additive mods instead of whole-module replacement: Warband (survey §1.6).
- Authoring vs runtime formats; stable identity (Unity GUIDs, Godot paths + `.uid`); handles for hot reload: survey §2.3, §2.4, §3.4.
- Data-driven templates: Dungeon Siege (survey §3.2).
- MonoGame specifics (verified 2026-09): `Texture2D.FromStream` uses StbImageSharp (png/jpg/bmp/gif; **not tga**) with an optional premultiply processor. 3.8.5 adds a code-centric C# content builder (survey §3.7).

## 3. Concepts

### 3.1 Virtual file system
- A **mount** is a folder or a `.pak` (a renamed zip, like Quake 3's pk3), with a priority. Later mounts **shadow** earlier ones for the same path.
- **Mount order:** engine content → framework content → `game.json` mounts → mods in load order (17) → (dev only) `dev_override/`.
- **`VirtualPath`:** forward slashes, lower-case, no leading slash: `textures/creatures/goblin.png`. Case-insensitive lookups, so Windows and Linux behave the same.
- **Enumeration merges across mounts:** `Vfs.Enumerate("data/items", "*.json")` returns every matching file from every mount, in mount order. That's how records from all mods are found.
- **`user://`** is a separate, **writable** root for config, saves, logs and screenshots:
  - dev builds: `<repo>/user/<gameid>/`;
  - otherwise: `%LOCALAPPDATA%/Sage/<gameid>/` (Windows) or the XDG equivalent.

  Read mounts are never written to (Source's `mod_write` and Godot's `user://` separate these the same way).

### 3.2 Asset identity
- **Identity = `VirtualPath`.** A mod replaces the goblin texture by shipping the same path. No manifest is needed (Quake/Source behaviour).
- **`AssetPath`** is an interned `VirtualPath` (an `int` into a global path table). It's cheap to store in components and compare, and it doesn't load anything (03 §10).
- **Renames** are handled by the editor's refactor command (15), which rewrites references in records, prefabs and maps. If renames become painful, add Godot-4-style `.uid` sidecar files later: a stable id per asset that resolves to its current path. References stay path-based either way.

### 3.3 Handles, scopes and lifetime
- **`AssetRef<T>`** is a small struct (slot index + generation) pointing at a loaded (or loading) asset. It is **not** ref-counted by itself; copying it is free.
- **`AssetScope`** owns loads. Loading "into" a scope adds a reference; disposing the scope releases all its references at once. Typical scopes:
  - `Engine` (fonts, error placeholders, core shaders: never released);
  - per `World`;
  - per level/sector (14);
  - per UI screen.
- **Lifetime:**
  1. While any scope holds it, the asset stays loaded.
  2. At zero references it moves to an LRU cache (budget `asset_cache_mb`, default 256).
  3. It's evicted when the budget is exceeded.
  4. A handle to an evicted asset reports `Unloaded`, and `Get` returns the placeholder with a dev warning.
- **States:** `Loading → Loaded | Failed`, then later `Unloaded`. Consumers either check `IsReady` or just draw. Loading and failed assets render as placeholders, so nothing ever has to null-check.

### 3.4 Loaders and the sim/client split
- An `IAssetLoader<T>` is registered by a module for one or more extensions. It has two stages:
  1. **Decode:** worker thread, pure CPU (parse glTF, decode PNG, read WAV), producing a CPU-side payload.
  2. **Finalize:** main thread; e.g. create the `Texture2D`/`VertexBuffer`. Runs from a **budgeted upload queue** (`asset_upload_ms`, default 2 ms per frame) so streaming never hitches the frame.
- **Simulation-side** loaders live in `Sage.Engine`: collision meshes, navmeshes, heightmap data, animation clips.
- **Client-side** loaders live in `Sage.Client`: textures, GPU meshes, sprite sheets, effects, sounds.

  A headless run (tests, a future server) simply has no client loaders registered, and render-only `AssetPath`s in components are never resolved.

**v1 asset types:**

| Type | Source format | Loader | Notes |
|---|---|---|---|
| `Texture` (MonoGame `Texture2D`) | `.png`, `.jpg` | `Texture2D.FromStream` + `PremultiplyAlpha` processor | tga isn't supported by `FromStream`; convert to png |
| `SpriteSheetData` | **built as the `sprite_sheet` record** (§3.5), not a `.sheet.json` asset (12 "As built") | **sim** | Timings and events are needed by the simulation (melee "hit" frames, 12). The texture is loaded separately |
| `SpriteSheet` | `.png` + its `SpriteSheetData` | client | the Daggerfall-style creature/NPC sprites (06, 12) |
| `Mesh` | `.glb` | SharpGLTF → our vertex/index buffers, one part per primitive | **built (R12)**: `GltfLoader`; node transforms baked in, winding flipped once on load. **Binary only** — a text `.gltf` keeps its buffers in files beside it, which a mount cannot always hand over, so it is refused rather than half-loaded (§8). Material slots still to come |
| `Effect` | `.mgfxo` (compiled from `.fx`, 07) | `new Effect(device, bytes)` | |
| `Sound` | `.wav` | `SoundEffect.FromStream` | `.ogg` needs a decoder library; see Open questions |
| `Heightmap` | 16-bit `.png` or `.r16` | sim | terrain (14) |
| `CollisionMesh` | `.glb` (a collision node) | sim | physics (10) |

### 3.5 Data records
One pipeline for **every** definition: items, spells, creatures, factions, loot tables, materials (07), input maps (08), sprite animation sets, and **prefabs** (a prefab record's body is component data, parsed by the serializer from 09).

### As built (records made at run time, 2026-09-23 — F21)
Not every record comes from a file. A spell the player composed in the spellmaker (16 §3.3) is an
`ability` record that no mount defines, and later a generated quest will be the same shape.

- `records.AddRuntime<T>(id, record)` / `RemoveRuntime<T>(id)` / `RuntimeRecords`. They are kept in a
  layer of their own and **re-applied after every load and hot reload**, because a reload rebuilds the
  record set from disk and there is nothing on disk to rebuild a player's spell from.
- `RemoveRuntime` only withdraws what was added at run time, so it can never take a record a file
  defines with it.
- **It is a cache, not a store of record.** The data behind such a record lives in the save (09
  "As built (saved resources)"), and the record is composed from it again on load. Nothing serializes
  a record.
- Composed records take a **namespace of their own** (`custom:`), so nothing a player made can shadow
  content, and `rec_list` shows at a glance which is which.

### As built (prefabs, 2026-09-23 — F31)

- **Code:** `src/Sage.Engine/Content/Prefab.cs` (`PrefabRecord`, `IPrefabPart`, `PrefabRegistry`,
  `world.Spawn`), `ComponentSchema.cs` (components and tags by name),
  `src/Sage.Engine/Gameplay/PrefabParts.cs` (the parts the engine's modules register). Tests in
  `tests/Sage.Tests/World/PrefabTests.cs`.
- **A prefab is a record**, so `base` inheritance, per-field patching, load order, validation and hot
  reload all come from §3.5 for nothing. A `goblin_chief` can `base` a `goblin` and override one
  field of one component, because the merge runs on the JSON tree before anything is deserialized.
- **Its body is two halves.** `"components"` is component data by type name, applied as written.
  `"parts"` are named setups a **module registered** — for the cases that are not one component.

  ```json
  { "type": "prefab", "id": "goblin", "base": "creature", "name": "goblin",
    "components": { "SpriteRenderer": { "sheet": "goblin", "size": [1.6, 1.9] } },
    "tags": ["Hostile"],
    "parts": { "character": { "layer": "enemy" }, "attributes": {},
               "melee": { "attack": "claw" }, "effects": ["tough_hide"] } }
  ```

- **Why two halves and not one.** A `SpriteRenderer` is data. "Make this a character" is a collider,
  a controller, an intent and a pawn that have to agree about radius, height and layer, so it is a
  part `CharacterModule` owns. The test is whether a game could get it wrong by writing the components
  itself: if yes, it is a part. This is what stops the record becoming the god-object the Sandbox's
  `spawn` record was — **adding a feature registers a part, it does not edit this record.**
- **Games and mods register their own** in their module's `Init`:
  `ctx.Engine.Prefabs.Register("loot", (world, entity, options, where) => …)`.
- **Component names come from the ECS schema**, which Friflo builds by scanning loaded assemblies, so
  a component type is addressable the moment it is declared — nothing to register, and a game's own
  components work. The schema is read on **first use**, never when the `Engine` is constructed, because
  the host loads the game assembly after that (03 §3.1). Names match case-insensitively.
- **Bare record ids inside a body** (`"attack": "claw"`) mean one in the prefab's own namespace, the
  same rule record fields follow. The pipeline qualifies its own fields at merge time (review #56) but
  cannot see inside a component body, whose shape isn't known until the component type is, so `Populate`
  sets the parse namespace around the whole build.
- **Order:** components, then tags, then parts in the order their modules registered them (module
  dependency order), so `character` has built the body before `melee` hangs an attack on it. The
  placement transform is set **before** the body (parts read it — `character` seeds the pawn's yaw
  from it, review #43) **and again after**, so a prefab may carry a `Transform` for a scale it always
  wants without deciding where this one went.
- **Failure is local.** An unknown component, tag or part costs that one thing and says so with the
  prefab id; the rest of the entity still comes up. An unknown *prefab* costs the spawn.
- **Known limitation:** parts run after components, so a part wins where both touch the same
  component — a prefab cannot shrink the capsule `character` builds by writing a `Collider`. The
  alternative (components last, as overrides) would let data silently break a character, so parts win
  until a part needs options for it.
- **Console:** `ent_spawn <prefab> [x y z] [yaw]` places one (3 m in front of the camera by default),
  `ent_dump <id|name>` prints every component on an entity with its non-default fields, and
  `ent_types [filter]` lists the names a prefab can use.
- **The Sandbox and the importer moved onto it the same day.** `SpawnRecord` (twenty fields, one per
  engine feature, with a hundred-line "if it has a sheet… else if it has a box mesh… else if it is a
  character" behind it) is gone; the Sandbox now has a four-field `scene` record of placements and
  calls `world.Spawn` once. The Daggerfall importer emits `prefab` records instead of game-shaped
  ones, and adds its placements with a **patch** (`"place+"`) on the scene it does not own — which is
  §3.5's list-append doing exactly the job it was designed for. The Sandbox registers two parts of
  its own, `box_mesh` (it needs the renderer, which is client-side) and `hop`.
- **Not done here:** placement/map files and overrides per placed entity (F27 §3.4), "revert to
  prefab" in the editor (15), and nested prefabs. A `scene` record is a game's own until then.

### As built (asset hot reload, 2026-09-23 — part of F32)

- **Code:** `src/Sage.Client/Assets/AssetHotReload.cs` (the watcher), `ContentService.Reload` and its
  `Reloaded` event, `VirtualFileSystem.VirtualPathOf` (disk path → virtual path). Tests for the path
  mapping in `tests/Sage.Tests/Content/ContentTests.cs`.
- **What it is for.** Records have hot-reloaded since R11; this is the other half. Regenerate a
  sprite sheet, alt-tab, and it is on the creature — which is most of what dressing a game in art
  consists of, and the Daggerfall import made the absence obvious.
- **Individually, not wholesale.** A record reload rebuilds every record because they are cheap and
  entangled. A texture is a GPU object with holders, so only the file that changed is reloaded.
- **Holders re-resolve.** The renderer keeps textures in a table by index and a built material keeps
  them by reference, so both listen: the table entry is repointed and `MaterialCache.Invalidate()`
  rebuilds materials lazily (which also picks up a reloaded effect, since its bindings are dropped).
  Anything that calls `LoadTexture` every frame — the HUD — needs nothing.
- **A failed reload keeps the old asset.** The new file is loaded before the old object is disposed,
  so a half-written PNG or a typo leaves what was on screen rather than a black square.
- **Debounced 200 ms**, like records: an art tool writing eight files produces one reload, and a file
  still being written is not read.
- **Compiled effects reload too** (`.mgfxo`), because the engine mount *is* where `dotnet-mgfxc`
  writes — recompile and the shader swaps. Running `mgfxc` when a `.fx` changes is still the build's
  job (07 §3.1); that is the rest of F32.
- **Console:** `asset_reload [path]` forces one (or all), `asset_list` shows what is loaded.
  `asset_hotreload` turns the watcher off; it defaults to on in a dev build with `developer 1`.
- **Almost everything cached can be reloaded (R12).** This bullet used to list what could not: `.xnb`
  models, fonts and textures belonged to MonoGame's `ContentManager`, which kept its own cache and
  handed back *the same instance*, so reloading one would have destroyed the live asset. There is no
  `ContentManager` any more — every asset is a file the engine reads itself — so textures, effects and
  the font all reload, and "rebuild the content to change it" went with the thing it described. **One
  refusal is left, and it is honest about itself:** a sound, because the mixer holds instances of it
  (see the table). `asset_reload` on one says it is loaded but cannot be swapped, rather than claiming
  it was never loaded.
- **A font is a texture, and reloads as one.** `LoadFont` goes through `LoadTexture`, so a font's path
  is always a texture entry too and always takes the texture branch of `Reload`; that branch drops the
  `BitmapFont` wrapper with it, and `UiRenderSystem` asks for the font every frame instead of holding
  one. Both halves are needed, and the bug that taught us so is worth keeping: a holder that keeps the
  old wrapper draws from a **disposed** texture, which GL renders as black boxes where the text was,
  with nothing logged anywhere.
- **Not done here:** the asset *server* with scopes, async loading and ref-counting (§3.3).

### What can be hot reloaded, and what formats load

Checked against the decoders compiled into MonoGame 3.8.x DesktopGL (StbImageSharp) and confirmed by
loading one of each through the sprite pipeline.

| Kind | Loose file | Hot reload | Notes |
|---|---|---|---|
| Texture | `.png` `.jpg` `.bmp` `.tga` `.gif` `.psd` `.hdr` | yes | `Texture2D.FromStream`. GIF gives frame 0; HDR is tone-mapped to 8-bit; 16-bit PNG is truncated to 8 |
| Compiled effect | `.mgfxo` | yes | the engine mount *is* where `dotnet-mgfxc` writes, so recompiling swaps the shader |
| Model | `.glb` | not yet | `GltfLoader` (SharpGLTF) reads it off the mount; the renderer caches meshes by path and does not re-resolve one yet. Text `.gltf` is not read at all |
| Sound | `.wav` | **no** | `SoundEffect.FromStream` (11 §3). `AudioBackend` keeps a `SoundEffectInstance` per voice and MonoGame disposes those with their `SoundEffect`, so swapping one means teaching the mixer to drop its voices first (F32). `asset_list` says so rather than offering it |
| Font | `.png` atlas | yes | a texture with a glyph grid over it (13 §3), so reloading the image reloads the font |
| `.tif` `.dds` `.webp` | — | — | **no runtime decoder** in StbImageSharp; convert to PNG (there is no MGCB left to take them through) |

Two MonoGame details worth knowing, because its own documentation states both backwards: **TGA does
load** at runtime, and **TIFF and DDS do not**. The stale comment dates from XNA, before MonoGame
moved `FromStream` onto StbImageSharp.

`FromStream` returns straight alpha and zeroes the colour of fully transparent pixels; the loader
premultiplies afterwards (07 §13), which is what the blend states expect and is also what stops
transparent texels bleeding dark halos into sprite edges under bilinear filtering.

### When an asset is missing or broken

Verified by deleting and corrupting files while the game ran. Nothing here stops the frame:

| What | What happens |
|---|---|
| Texture missing at load | one warning naming the path; the material draws the **checker placeholder** (texture id 0) |
| Mesh missing at load | one warning; the **error mesh** is drawn — a 1 m cube, never a silent gap (06 §8) |
| Effect or material broken | the material fails to build and `sage:error` is drawn in its place |
| Record id that does not resolve | `Ensure` fails once and a default-constructed record is returned |
| Prefab id that does not resolve | the spawn is refused and logged; nothing else in the scene is affected |
| Font missing | logged once; the HUD draws its bars and crosshair without text |
| **Reload** of a corrupt or half-written file | the decode fails, **the copy already loaded stays on screen**, and it says so |
| **Delete** while running | not reloaded at all: what is loaded stays. Said once, because "I deleted it and nothing happened" should not be a mystery. Export tools that write by delete-and-replace, and `git checkout`, both look like this |

The rule behind the table: a missing asset costs *that asset*, drawn as something obviously wrong, and
never the frame or the entity. The placeholder is deliberately ugly so that it reads as a bug rather
than as art.

- **Files:** `data/**/*.json` in any mount. A file holds an array of records:
  ```json
  [
    { "type": "item", "id": "iron_sword", "base": "sword_base",
      "name": "@items.iron_sword.name", "damage": 8, "weight": 3.5, "tags": ["blade", "iron"] },
    { "type": "material", "id": "goblin_sprite", "effect": "shaders/sprite.mgfxo",
      "technique": "Unlit", "pass": "AlphaTested", "params": { "Albedo": "textures/creatures/goblin.png", "Tint": [1,1,1,1], "AlphaCutoff": 0.5 } }
  ]
  ```
- **`RecordId` = `namespace:name`.** A bare `id` gets the namespace of the mount that defines it: the game's `id` from `game.json`, a mod's id, or `sage` for engine content. **Inside a patch, bare references resolve in the *patched record's* namespace**, not the patching mount's — a mod that points an engine record at one of its own records writes the id in full (`"attackEffect": "mymod:claw"`).
- **`base`, `type`, `id`, `patch`, `disabled` and `abstract` are reserved**: a record that wants a field of its own by one of those names has to call it something else (the engine's `attribute` records use `start`, not `base`).
- **`base`:** single inheritance of field values from another record of the same type (Dungeon Siege templates). It's resolved after merging. **`"abstract": true`** marks a template: it can be a `base` but never becomes a record itself (so `creature_base` doesn't spawn), and the flag isn't inherited.
- **Localized text** is a key (`@items.iron_sword.name`) into string tables, not inline text (ARCHITECTURE §4.7).
- **Load-order merge, per field, not whole-record:**
  1. Records are read in mount order. Within one mount, every definition is read before any patch, so file names don't matter.
  2. The first definition of an id creates the record.
  3. A later record with `"patch": true` and the same id **merges field by field**: scalars overwrite, objects merge recursively, lists are replaced unless patched with `"tags+": [...]` (append) / `"tags-": [...]` (remove).
  4. A later *non-patch* definition of an existing id is an error (it would silently wipe the earlier one, which is Bethesda's "rule of one" problem). It's logged with both files, and the patch wins so the game still runs.
  5. `"disabled": true` in a patch removes a record.
- **Typed schemas:** each record type is a C# class with `[Record("item")]`. A source generator (09) produces the JSON reader and validator. After all files are merged:
  - unknown fields → `Warn` (catches typos);
  - wrong types / out-of-range values → `Error` (the record is skipped);
  - `RecordId`/`AssetPath` references to things that don't exist → `Error` with the file and line.
- **`RecordStore`** holds merged, validated records per type. Lookups by `RecordId` are dictionary-based. For hot paths, use `RecordRef<T>` (a cached index).
- **Hot reload** (`rec_hotreload`, `DevOnly`, default on when `developer` ≥ 1): a changed `.json` re-runs load + merge + validate for that record type, replaces entries **in place** (so `RecordRef`s stay valid), then raises `RecordsReloaded(RecordType)` (04).

### 3.6 As built (migration step 5)
- **Code:** `src/Sage.Engine/Content/` (`VirtualFileSystem.cs`, `RecordId.cs`, `RecordStore.cs`, `RecordHotReload.cs`); tests in `tests/Sage.Tests/Content/ContentTests.cs`.
- **VFS:** folder mounts only. Priority is mount order (`Mount(IMount)` has no priority argument). The host mounts engine content (`<exe>/Content`, namespace `sage`), then the game's `game.json` mounts (namespace = game id). `user://` is `UserPaths` (02), not a VFS root yet. `VirtualPath.Parse` rejects `.`, `..` and `:`.
- **Records:** the rules above, deserialized with `System.Text.Json` reflection (fields and properties, case-insensitive names, comments and trailing commas allowed, `[x,y,z]` vectors, enums as strings). The generated readers (09) replace it later with the same rules. Differences from the design:
  - errors name the file and the record's index in it (`scene.json[1]`), not a line number;
  - no range checks yet (they come with `[Property]` attributes, 09);
  - reference validation checks that a referenced `RecordId` exists, not that it has the right type; `AssetPath` references aren't checked (no `AssetPath` yet);
  - no `RecordRef<T>` yet;
  - hot reload re-runs the whole load (all types; it takes about 1 ms today) and raises a plain `RecordStore.Reloaded` event, not `EngineSignals.RecordsReloaded(RecordType)` (04). Instances of records that still exist are updated in place.
- **Hot reload** (`RecordHotReload`, dev builds): a `FileSystemWatcher` on each folder mount's `data/`, polled from the main thread and reloaded after 200 ms of quiet, while `rec_hotreload` is on. `games/Sandbox` respawns its scene on reload, so editing `content/data/scene.json` updates the running game.
- **Assets — as built (R12, 2026-09-24):** `ContentService` (`src/Sage.Client/Assets/ContentService.cs`) loads everything through the VFS, so any asset can come from any mount and be shadowed like any other file:
  - models: `.glb`/`.gltf`, read by `GltfLoader` (SharpGLTF) into vertex and index buffers the renderer owns and disposes;
  - compiled effects (`.mgfxo`, step 6);
  - `.png`/`.jpg` textures via `Texture2D.FromStream`, premultiplied (step 6);
  - `.wav` sounds via `SoundEffect.FromStream` (11 §3);
  - the UI font, which is a texture with a glyph grid over it (13 §3).

  **Nothing is built by a content pipeline any more.** `ContentService.Open(path)` hands a mount's bytes to whoever knows the format, which is what lets a mod drop in a model or a game ship art made this morning; `Content.mgcb`, `MonoGame.Content.Builder.Task`, the `dotnet-mgcb*` tools, `VfsContentManager` and every `.xnb` branch are gone, and `dotnet-mgfxc` (07 §3.1) is the only build-time content step left. Everything is cached for the process. `AssetPath` (the interned path, §3.2) exists since step 6 and is what `MeshRenderer` and material records store. `AssetServer`, `AssetRef`, scopes and async loading are still to be built (§14 step 2).
- **Engine content** is `engine_content/` in the repo. The build copies it into the exe's `Content/`, the `engine` mount (07 §3.6).

## 4. Public API sketch

```csharp
public readonly struct VirtualPath { public static VirtualPath Parse(string s); }   // normalized
public readonly struct AssetPath   { public static AssetPath Intern(VirtualPath p); public VirtualPath Path { get; } }

public sealed class VirtualFileSystem
{
    public void Mount(IMount mount, int priority);                 // FolderMount, PakMount
    public bool Exists(VirtualPath path);
    public Stream Open(VirtualPath path);                          // highest-priority match
    public IMount? Which(VirtualPath path);                        // which mount provides it (for logs/tools)
    public IEnumerable<(VirtualPath, IMount)> Enumerate(VirtualPath dir, string pattern);  // all mounts, mount order
    public string UserRoot { get; }                                // user:// on disk
}

public readonly struct AssetRef<T> where T : class
{
    public AssetState State { get; }                               // Loading, Loaded, Failed, Unloaded
    public bool IsReady { get; }
    public T Get();                                                // the asset, or its placeholder if not ready
}

public sealed class AssetScope : IDisposable { public string Name { get; } }

public sealed class AssetServer
{
    public AssetScope CreateScope(string name);
    public AssetRef<T> Load<T>(AssetPath path, AssetScope scope) where T : class;   // async; returns immediately
    public AssetRef<T> LoadNow<T>(AssetPath path, AssetScope scope) where T : class; // blocking (boot, tools)
    public void RegisterLoader<T>(IAssetLoader<T> loader) where T : class;
    public void SetPlaceholder<T>(T placeholder) where T : class;
}

public interface IAssetLoader<T> where T : class
{
    IReadOnlyList<string> Extensions { get; }
    object Decode(Stream data, VirtualPath path);                 // worker thread; CPU only
    T Finalize(object decoded);                                   // main thread; GPU work allowed (client loaders)
    void Replace(T existing, object decoded);                     // hot reload in place
}

public readonly struct RecordId   { public string Namespace { get; } public string Name { get; } }
public readonly struct RecordType { public string Name { get; } }
public readonly struct RecordRef<T> where T : class { public T Value { get; } }

[AttributeUsage(AttributeTargets.Class)] public sealed class RecordAttribute : Attribute { public RecordAttribute(string type); }

public sealed class RecordStore                                   // as built (2026-09-23)
{
    public void Register<T>() where T : class, new();             // a [Record] type, in a module's Init
    public T Get<T>(RecordId id) where T : class, new();          // Ensure + placeholder record on miss
    public bool TryGet<T>(RecordId id, out T value) where T : class;
    public IReadOnlyList<T> All<T>() where T : class;
    public IEnumerable<RecordId> Ids(string type);
    public bool Exists(RecordId id);
    public RecordId Resolve(string type, string text);            // a bare name from a console command;
                                                                  // warns "there is no item called 'x'" and
                                                                  // returns empty rather than a blank id
    public void Load(VirtualFileSystem vfs);
    public void Reload();                                        // hot reload; keeps existing instances
    public event Action? Reloaded;

    // Records made rather than loaded (F21's spellmaker). Re-applied after every load.
    public void AddRuntime<T>(RecordId id, T record) where T : class;
    public bool RemoveRuntime<T>(RecordId id) where T : class;
    public IEnumerable<(string Type, RecordId Id, object Record)> RuntimeRecords { get; }
}
// No `RecordRef<T>` yet: a reference is a `RecordId` plus a lookup. The typed handle waits for the
// generator (R11's "left"), which is also what would validate it.
```

## 5. Data flow

```
boot:  VFS mounts ──► RecordStore: enumerate data/**/*.json → parse → merge (mount order) → resolve base → validate
play:  system asks Load<Texture>(path, levelScope)
         └► AssetRef (Loading) returned immediately; placeholder drawn
         └► job: VFS.Open → loader.Decode (worker)
         └► main-thread upload queue (≤ asset_upload_ms per frame) → loader.Finalize → Loaded
dev:   FileSystemWatcher (folder mounts) → debounce 200 ms → Decode → Replace in place
         → EngineSignals.AssetReloaded / RecordsReloaded → consumers refresh caches
unload: scope.Dispose() → refcounts drop → LRU cache → evict over budget
```

## 6. Threading and memory
- `Decode` runs on the job system. `Finalize`/`Replace` run on the main thread (the MonoGame `GraphicsDevice` rule).
- `VirtualFileSystem` reads are thread-safe; `Mount` happens only during boot or mod changes.
- The path intern table is append-only and lock-free for reads.
- Records are immutable during a tick. Hot-reload replacement happens at frame start, between ticks.
- Budgets: `asset_upload_ms` (per-frame GPU upload time) and `asset_cache_mb` (LRU).

## 7. File formats
| File | Format |
|---|---|
| `.pak` | zip (stored or deflate), paths inside = virtual paths |
| `data/**/*.json` | record arrays (§3.5) |
| `*.sheet.json` | sprite sheet: frame rects, pivot, animations, 8-direction frame groups (06, 12) |
| `strings/<lang>/*.json` | localization tables: key → text |
| Cooked formats (later) | `.sgmesh` (binary vertex/index blobs), compressed textures; produced by a cook tool, loaded in `Shipping` when present |

## 8. Errors and fallbacks
| Failure | Behaviour |
|---|---|
| Missing asset | `Warn` once per path with the mount search list; the placeholder is used (checkerboard texture, error mesh — a red "ERROR" box like Source's error model — or a silent sound) |
| Decode failure | `Error` with the exception and the mount that provided the file; placeholder |
| Missing record | `Ensure` fails; a placeholder record (type defaults) keeps the game running |
| Record validation error | Record skipped, `Error` with file:line:field |
| Non-patch redefinition | `Error` naming both files; the later one is treated as a patch |
| Corrupt `.pak` | Mount skipped, `Error`; boot continues |

## 9. Debug and tooling hooks
- **Cvars:** `asset_upload_ms`, `asset_cache_mb`, `asset_hotreload`, `rec_hotreload` (both `DevOnly`; default = `developer` ≥ 1), `vfs_log_overrides`.
- **Commands** (built in step 5: `vfs_which`, `vfs_ls`, `vfs_mounts`, `rec_get`, `rec_list`, `rec_reload`, and the `rec_hotreload` cvar):
  - `vfs_which <path>`, `vfs_ls <dir>`, `vfs_mounts`;
  - `asset_list [filter]` (state, scope refs, size);
  - `asset_reload <path|*>`;
  - `rec_get <type> <id>` (dumps the merged record *with the file each field came from*);
  - `rec_list <type>`, `rec_reload`.
- **Overlay:** loads in flight, upload ms used, cache size.
- **Log categories:**
  - `VFS`: mounts, and overrides when `vfs_log_overrides` is on (which mod shadows which path);
  - `Assets`: load, fail, evict, reload;
  - `Records`: merge report, validation, patches applied per mod.

## 10. Mapping from today's code
| Today | Becomes |
|---|---|
| `src/Sage.Client/Assets/UtilAssets.cs` (static `stanfordBunny`) | **Removed (step 5):** the Sandbox loads models through `ContentService` (VFS-backed, §3.6). Later: `AssetRef`s loaded into scopes |
| `src/Sage.Host/Content/Content.mgcb` + `stanford_bunny.fbx` | **Both deleted (R12, done).** Models load at runtime from `.glb` via SharpGLTF (`GltfLoader`). The Sandbox's placeholder is `games/Sandbox/content/models/bunny.glb`, generated by `games/Sandbox/tools/make_placeholder_model.py` — like the sprites and the sounds, the repository ships no art it did not make. Shaders compile with `dotnet-mgfxc` (07) |
| The FreeImage failure that removed `light.png` (review #9, 2026-09-22) | Textures load with `Texture2D.FromStream` (StbImageSharp, no FreeImage) |
| `Content.RootDirectory = "Content"` in `Game1` | VFS mounts from `game.json`. **Done (step 5)** for game content; **the line itself is gone (R12)** — nothing uses MonoGame's `ContentManager` any more, not the host, the client, or the vendored ImGui renderer |

## 11. v1 scope vs later
- **v1:**
  - VFS with folder mounts + `user://`;
  - `AssetPath` interning;
  - `AssetServer` with async decode + budgeted upload;
  - scopes + LRU;
  - placeholders;
  - loaders for Texture, SpriteSheet, Mesh (glTF), Effect, Sound (wav), Heightmap;
  - hot reload for folder mounts;
  - `RecordStore` with namespaces, `base`, patch merge, generated validation, hot reload;
  - localization keys (English table only).
- **Later:** `.pak` mounts, `.uid` sidecars (if needed), cooked formats + a cook tool (possibly on MonoGame 3.8.5's C# content builder — evaluate then), OGG music streaming, texture compression.

## 12. Multiplayer-later notes
A server needs the same records and simulation assets. The mod list plus record hashes will be compared on connect (as Quake 3's `sv_pure` checks paks). Only data and asset mods may be auto-downloaded; code mods never are (17).

## 13. Open questions
- OGG decoding: MonoGame's `Song`/`SoundEffect` support differs per platform. Evaluate NVorbis (managed OGG decoder) for music streaming when audio (11) starts. **[unverified which formats `SoundEffect.FromStream` accepts beyond WAV on DesktopGL]**
- JSON library: `System.Text.Json` source-generated readers vs a custom reader that keeps line numbers for error messages. Leaning towards **`Utf8JsonReader` + a generated per-type reader** (fast, with position info for errors).

### Namespaces and inheritance
A bare id means "in this file's namespace", and that rule follows the *value*, not the record that
ends up holding it: a base in another namespace wrote `"damageType": "physical"` meaning
`sage:physical`, and a game record inheriting it gets `sage:physical`, not `sandbox:physical`. The
merge qualifies inherited ids using the record type's own fields, so only ids are rewritten — paths,
labels and clip names are left exactly as the base wrote them (review #56).

## 14. Build steps
1. ~~VFS (folder mounts) + `game.json` mounts (with 01)~~ **Done 2026-09-22** (ARCHITECTURE §7 step 5). `user://` as a VFS root is still to do.
2. `AssetPath`, `AssetServer`, scopes, placeholders, texture + glTF + effect loaders; convert the bunny to `.glb`; fix TODO #9. **Half done:** `AssetPath` (step 6), the texture, effect, sound, font and glTF loaders, the bunny and #9 all came with **R12 (2026-09-24)**. What is left is the `AssetServer` proper — scopes, ref-counting, placeholders and eviction — which is why `ContentService` still caches everything for the life of the process.
3. Async decode + budgeted upload queue (with the 02 job system).
4. `RecordStore`: parsing, namespaces, `base`, patch merge, validation (TODO R11). **v1 done in step 5** with `System.Text.Json` reflection (§3.6); 09's generator replaces it later.
5. Hot reload for assets and records. **Records done in step 5**; assets come with the `AssetServer`.
