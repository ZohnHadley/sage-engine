# 17 — Modding (short)

## 1. Purpose and scope
How mods are packaged, ordered, loaded and reported. Mods can:
- **shadow assets** by path;
- **add and patch records per field**;
- add maps and shaders;
- (trusted) add **C# assemblies**.

Decision D3: **data mods + trusted C# mods**; no sandboxed scripting.

## 2. Research basis
- Quake 3 `fs_game` + pk3 and Source SearchPaths: layered mounts (survey §1.1, §1.3).
- Bethesda plugins and the whole-record "rule of one" → conflicts, LOOT, xEdit, bashed patches (survey §1.5).
- Warband mods replace the whole module and can't be combined (survey §1.6).
- Skyrim Papyrus script state baked into saves (survey §1.5).
- Modern .NET has no sandbox for loaded assemblies. Quake 3 used a VM for downloadable code for this reason (survey §1.1).

## 3. Key decisions
- **A mod** is a folder or `.pak` under `mods/<id>/` with a `mod.json`:
  ```json
  { "id": "better_goblins", "name": "Better Goblins", "version": "1.2.0", "author": "…",
    "game": "sandbox", "gameVersion": ">=0.3",
    "dependencies": [ { "id": "core_patch", "version": ">=1.0" } ],
    "loadAfter": ["hd_textures"], "assemblies": [] }
  ```
  The mod `id` is its **record namespace** (05 §3.5).
- **Load order:** `user://mods.json` lists enabled mods in order. At boot, the order is validated and auto-sorted by `dependencies` and `loadAfter`/`loadBefore` (topological sort; the user's order breaks ties). Missing dependencies disable the mod with an error. Mods are mounted after the game (05 §3.1).
- **Additive by design:** mods patch records per field (`"patch": true`, list `+`/`-` ops) instead of replacing whole records or modules. Two mods that touch different fields of the same item both work.
- **C# mod assemblies (trusted):**
  - loaded into an `AssemblyLoadContext`;
  - they implement `IModule` (01) and register systems, records, loaders and cvars like any module;
  - the mod list UI marks them "contains code";
  - they are **never auto-downloaded** (the multiplayer-later rule, 05 §12);
  - no sandbox is promised.
- **Conflict report:** at boot, the `Mods` log category plus `user://logs/mod_report.txt` lists, per mod:
  - which asset paths it shadows, and from whom;
  - which records it patches, field by field;
  - non-patch redefinitions (errors);
  - missing references.

  Also available through the `mod_conflicts` command.
- **Saves** record the mod list + content hashes (09 §3.6). Removing a mod warns but still loads: unknown components are skipped, and missing records become placeholders. There's no orphaned script state, because behaviour is never saved.

## 4. API sketch
```csharp
public sealed class ModManifest { public string Id, Name, Version; public string Game; public VersionRange GameVersion; public List<ModDependency> Dependencies; public List<string> LoadAfter, LoadBefore, Assemblies; }
public sealed class ModManager                     // Engine service, used during boot (01 step 4)
{
    public IReadOnlyList<ModManifest> Available { get; }
    public IReadOnlyList<ModManifest> LoadOrder { get; }   // validated and sorted
    public ModReport Report { get; }                        // shadows, patches, errors
}
```

## 11. v1 scope vs later
- **v1:**
  - the `mods/` folder;
  - `mod.json`;
  - `user://mods.json` load order with a topological sort;
  - VFS mounting;
  - record patches;
  - the conflict report + `mod_conflicts`;
  - the mod list in the save header;
  - log category `Mods`.
- **Later:** C# mod assemblies, an in-game mod manager UI, `.pak` packaging tool, Steam Workshop, per-field conflict UI in the editor (15).

## 14. Build steps
1. `ModManager`: discovery, manifests, load order, mounting (with 05; TODO F37). *The pieces it builds on exist since migration step 5: VFS mounts with shadowing, per-mount record namespaces, the per-field patch merge and `rec_get`'s per-field origin (05 §3.6). `game.json` already has `modsDirectory`.*
2. Conflict report (depends on the record merge reporting, 05).
3. Save header mod list (with 09).
4. C# mod assemblies (later).
