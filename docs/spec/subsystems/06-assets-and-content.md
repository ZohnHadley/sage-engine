# 06 · Assets and content

> Status: partly built. The virtual file system, records, strict loading, patches, schemas, `sage validate` and hot reload of records, textures, models, sounds and compiled effects work. Zip mounts, asset scopes with unloading, cooked formats and mipmaps are not built. Owning assemblies: `Sage.Core` (`Content`), `Sage.Simulation` (`Content`), `Sage.Client` (`Assets`), `Sage.Cli`. Design doc: [05 Assets and VFS](../../design/05-assets-and-vfs.md).

## 1. Purpose and scope

Everything a game is, other than code, reaches the engine through this subsystem: records (typed JSON documents), prefabs and scenes, textures, models, sounds and shaders. It gives every file one name (a virtual path), resolves it through layered mounts so a mod can replace what the game ships, loads records strictly so mistakes are reported at load with a file and line, and reloads changed files while the game runs.

It does not decide what a record means (the plugin that declares the record type does), draw anything (sheet [07](07-rendering.md)), or manage mods as a feature: manifests, load order and conflict reports are in sheet [19](19-modding.md), which uses this layer. Saves are sheet [15](15-saves.md). String tables for localisation are in sheet [13](13-ui.md).

## 2. Responsibilities

- Present one namespace of virtual paths over folder mounts and assembly-resource mounts, last mount wins, case-insensitive, with `..` escapes rejected.
- Load `data/**/*.json` records in mount order: definitions before patches within a mount, then the patches, with `base` inheritance and `abstract` templates.
- Merge patches field by field, and report each write's origin (file, line, mount) so errors and conflict reports point at the right file.
- Check every record at load: unknown fields at any depth (with the nearest name), values of the wrong shape, `RecordRef` targets by type, asset paths that name no file, and checks that plugins add (prefab bodies, AI schedules).
- Generate JSON Schemas for VS Code from the same metadata the inspector uses, and the `sage validate` and `sage schema` commands.
- Load client assets (textures, models, compiled effects, sounds, fonts) through the VFS with a cache, and reload them in place when files change.

Not responsible for: unloading assets (not built, #308), compiling shaders at build time (the build runs `mgfxc`), or the editor's asset browser (#366).

## 3. Placement and dependencies

| Part | Assembly | Notes |
|---|---|---|
| `VirtualFileSystem`, mounts, `AssetPath`, `RecordStore`, `RecordId`, `RecordRef<T>`, content checks, `RecordHotReload` | `Sage.Core` | No simulation or MonoGame types. |
| Record schemas, content validation, scenes, prefabs, `ShaderIncludes` | `Sage.Simulation` | `RecordSchemas`, `ContentValidation`. |
| `ContentService`, `AssetHotReload`, `ShaderRecompiler`, `GltfLoader` | `Sage.Client` | MonoGame: decodes textures and sounds, builds effects. Tests cannot reference it, so it is covered by the smoke run. |
| `sage validate`, `sage schema`, `sage mods` | `Sage.Cli` | Headless, throwaway user folder. |

Mounts are made at boot by `SageApp`: engine content under namespace `sage`, each plugin's embedded content under the plugin's record namespace, the game's mounts under the game id, then mods under their ids. A mount's `RecordNamespace` is what a bare id in its files means.

## 4. Interfaces

| Type (file) | Role |
|---|---|
| `IMount`, `FolderMount`, `AssemblyContentMount` (`Content/VirtualFileSystem.cs`) | Where files come from. `PhysicalPath` and `WritablePath` are null for a mount that is not a folder. |
| `VirtualFileSystem` | `Mount`, `Exists`, `Open`, `Enumerate`, `Which`, `Shadows()`. |
| `VirtualPath`, `AssetPath` | Normalised, validated names. `AssetPath.Intern` for a cached key. |
| `RecordStore` (`Content/RecordStore.cs`) | `Register<T>`, `Load(vfs)`, `Get`, `TryGet`, `All`, `Ids`, `AddRuntime`, `AddCheck`, `Reloaded` event, `LoadErrors`, `MissingAssetsAreErrors`. |
| `RecordId`, `RecordRef<T>` | A namespaced id (`ns:name`); a typed reference that is checked at load. |
| `RecordCheck` | What a plugin check is given: `At(path)` gives `file:line:column`, plus `Error` and `Warn`. |
| `RecordHotReload` | Watches `data/` and `strings/` in folder mounts; reloads once changes are quiet for 200 ms. |
| `ContentService` (`Sage.Client/Assets`) | `LoadTexture`, `LoadEffect`, `LoadSound`, `LoadFont`, `Reload`, `Cached`, `Reloaded` event. |
| `AssetHotReload`, `ShaderRecompiler` | Watch folder mounts for assets and for `.fx`/`.fxh` sources. |
| `RecordSchemas`, `SchemaCatalog` (`Simulation/Content/RecordSchemas.cs`) | Write the schemas. |

Attributes that describe fields: `[Record("type")]`, `[RecordRef("type")]` on a `RecordId`, `[AssetKind("kind")]` on an `AssetPath`, `[Property(...)]`.

Commands: `vfs_mounts`, `vfs_which <path>`, `vfs_ls [dir]`, `rec_list [type]`, `rec_get <type> <id>` (the merged record and which file set each field), `rec_reload`, `asset_list`, `asset_reload [path]`.

Command line: `sage validate <game>`, `sage schema <games...> --out <dir>`, `sage mods <game>` (flags `--mods`, `--game-mods`, `--mounts dir[=ns]`, `--engine-content`).

## 5. Data model

A record file is a JSON or JSONC array of records, or one record:

```
{ "type": "item", "id": "falchion", "base": "weapon_base", "damage": 12 }
{ "type": "item", "id": "sandbox:falchion", "patch": true, "tags+": ["boss"], "damage": 14 }
```

| Meta key | Meaning |
|---|---|
| `type`, `id` | The record's type (declared by `[Record]`) and its id; a bare id takes the file's mount namespace. |
| `base` | Inherit from another record of the same type, resolved after merging. |
| `abstract` | A template: usable as a base, never built. |
| `patch` | Merge into an earlier record field by field: scalars replace, objects merge, lists replace. |
| `field+`, `field-` | Append to, or remove entries from, a list field. Removal writes the entry out in full. |
| `disabled` | Remove the record (in a patch). |
| `$schema` | For the editor only. |

A redefinition without `patch` is an error that names both places, and is applied as a patch anyway. Colours read as hex, arrays or packed numbers. `MaterialRecord` params may name a texture or `"rt:name"` for a render target.

Asset kinds the client reads: textures (`.png`, `.jpg`, `.bmp`, `.tga`, `.gif`, `.psd`, `.hdr`; premultiplied on load), models (`.glb`), sounds (`.wav`), compiled effects (`.mgfxo`). Shaders are HLSL `.fx`, built into `.mgfxo` by `mgfxc`. Nothing goes through a content pipeline.

Schemas in `schemas/`: `record.schema.json` (root), one `<type>.schema.json` per record type, `prefab-components`, `prefab-parts`, `ids`, `vocabularies`, `mod`, `game`. They are generated, never edited, and CI diffs them.

## 6. Lifecycle and data flow

1. Boot (`SageApp`): mounts are added, then `RecordStore.Load` reads every record, merges patches, resolves bases, deserialises, and runs the checks. Record types and checks are registered in module `Init` (sealed afterwards; registering later is build error SAGE0020).
2. Content errors are counted. A dev build refuses to start on them where a blank record would otherwise be handed back; `sage validate` exits 1.
3. Scenes and prefabs are read after records, and their component and part bodies are checked at their own lines.
4. At run time the client loads an asset on first use and caches it for the life of the process. A failed load logs once and returns null; callers draw a placeholder (the magenta error material for meshes).
5. Hot reload (dev builds): a watcher notes a change, the main thread polls each frame, and after 200 ms of quiet the file is reloaded. Records reload as a set (`Reloaded` fires, instances update in place, string tables are re-read). Assets reload individually: the new object is built before the old one is disposed, so a bad file leaves the old texture on screen. Models swap in the renderer's mesh table, sounds stop or restart their voices, a `.mgfxo` swaps compiled effects, and a changed `.fx` or `.fxh` is recompiled in the background (effects that include a changed header are rebuilt too).

## 7. Threading, memory and performance

Record loading and checks run on the main thread at boot. File watchers raise events on thread-pool threads that only set a flag or add to a list; reload work happens in `Poll()` on the main thread. Shader compilation runs on a background task, one at a time. The client cache holds every loaded texture, mesh and effect until the process ends: GPU memory grows with the world visited (REQ-PERF-06 in the [SRS](../SRS.md); #308). Textures have no mipmaps and no compression (#317). Record reload of the Sandbox is held within a time budget (test: TheSandboxsRecordsReloadWithinBudget).

## 8. Errors and diagnostics

Every content error carries `file:line:column` for the deepest part the file wrote, for example `game:data/prefabs.json:12:9: prefab hut: unknown field 'rnage'; did you mean 'range'?`. An error in a patched field points at the patch (test: AnErrorInAPatchedField_PointsAtThePatch); a field set by inheritance says `via base`.

| Mistake | Result |
|---|---|
| Invalid JSON | Error with line and column; other files still load. |
| Unknown field or enum value | Error naming the nearest valid name; the record is skipped. |
| `RecordRef` to a missing or wrong-typed record | Error at the reference. |
| Asset path in no mount | Warning in a dev build, error in `sage validate` and in Shipping. A missing compiled shader in a build made without shaders is reported under `Shaders`. |
| Redefinition without `patch` | Error naming both places; applied as a patch. |
| Patch of an unknown id | Skipped with a note in the content report. |

Log categories: `Records`, `Assets`, `Shaders`. `sage validate` prints `WARN` and `ERROR` lines and a summary, and exits 1 on errors. `rec_get` shows which file set each field. A problems list inside the running game is not built (#301); the editor has one.

## 9. Requirements

| ID | Requirement (shall ...) | Priority | Status | Evidence or issue |
|---|---|---|---|---|
| REQ-ASSET-01 | The VFS shall layer mounts with the last mount winning, resolve paths case-insensitively and reject paths that escape a mount. | Must | Done | test: LaterMount_ShadowsEarlier_AndEnumerateIsInMountOrder |
| REQ-ASSET-02 | The VFS shall read zip archives as mounts, with the same ordering rules, and hot reload shall skip them. | Must | Not started | #397 (with #293 for packaging) |
| REQ-ASSET-03 | Records shall load definitions before patches within a mount, and merge patches field by field. | Must | Done | test: Patch_MergesFieldByField |
| REQ-ASSET-04 | A patch shall append (`field+`) and remove (`field-`) list entries. | Must | Done | test: Patch_MergesFieldByField |
| REQ-ASSET-05 | A patch shall edit one list entry by key, remove by key (`$remove`) and replace a whole list. | Should | Not started | #399 |
| REQ-ASSET-06 | Every record error shall name file, line and column, and a bad record shall not stop other files loading. | Must | Done | test: RecordErrors_SayFileLineAndColumn |
| REQ-ASSET-07 | Unknown fields at any depth shall be errors with the nearest valid name. | Must | Done | test: UnknownFields_AtAnyDepth_AreErrorsWithTheNearestName |
| REQ-ASSET-08 | `RecordRef` fields shall be checked by target type at load. | Must | Done | test: RecordRefs_AreCheckedByType |
| REQ-ASSET-09 | An asset path that names no file shall be a warning in development and an error when validating or shipping. | Must | Done | test: AssetPaths_MustBeInAMount_AWarningInDev_AnErrorWhenValidating |
| REQ-ASSET-10 | A bare id in a file shall mean that file's mount namespace, including in a mod's patch of a game record. | Must | Done | test: Patch_BareIdsMeanThePatchingFilesNamespace |
| REQ-ASSET-11 | Mod asset paths shall be namespaced so two mods can ship the same file name without a silent override. | Must | Not started | #398 |
| REQ-ASSET-12 | JSON Schemas shall be generated from field metadata and content, committed, and checked for staleness in CI. | Must | Done | test: TheCommittedSchemas_AreWhatSageSchemaWrites |
| REQ-ASSET-13 | `sage validate` shall load a game headless, print every error and warning, and exit non-zero on errors. | Must | Done | test: Validate_TheSandboxAndHelloHaveNoErrors |
| REQ-ASSET-14 | Records shall hot reload in a running game without a restart. | Must | Done | test: Reload_UpdatesInstancesInPlace |
| REQ-ASSET-15 | Textures, models, sounds and compiled effects shall hot reload individually, and a failed reload shall keep the old asset. | Should | Done (client; smoke run) | `Sage.Client/Assets/AssetHotReload.cs`; test: ReplacingAFileStopsItsOneShotsRestartsItsLoopsAndLeavesOthersAlone |
| REQ-ASSET-16 | A changed `.fx` source shall be recompiled and swapped in, with compile errors in the log and a visible fallback on failure. | Should | Done for the game and engine (Windows or Wine); mods open | `Sage.Client/Assets/ShaderRecompiler.cs`; mods: #400 |
| REQ-ASSET-17 | Assets shall have scopes (sector, game, UI), ref-counted release, eviction on unload and an upload budget. | Must | Partial: sector scopes for meshes, ref-counted (test: `ASectorsAssetsAreReleasedWhenItUnloadsAndSharedOnesAreKept`); textures, sounds, UI scope and an upload budget open | #308 |
| REQ-ASSET-18 | Textures shall load with mipmaps and optional compression, and the loader shall read the formats art tools write. | Should | Partial: PNG, JPG, BMP, TGA, GIF load; no mips | #317 |
| REQ-ASSET-19 | A cook step shall produce binary meshes and compressed textures for Shipping, loaded when present. | Could | Not started | #302 |
| REQ-ASSET-20 | Prefabs and scenes shall reload into running worlds. | Could | Not started | #287 |
| REQ-ASSET-21 | The game shall offer a problems list of load errors without the editor. | Could | Not started | #301 |

## 10. Open work

Milestone 4n, Rendering and assets (epic #304):

- #308 4n-4 AssetServer: scopes, ref-counting, eviction and mesh/texture memory budget (P1)
- #317 4n-13 Texture pipeline: mipmaps, compression, TGA/DDS, cooked formats (P2)
- #321 4n-17 Model format coverage: a documented glTF subset, vertex colours and a second UV set (P3)

Milestone 9, Code mods and packaging (epic #395):

- #397 9-2 Zip mounts and .sagemod packages, hardened (zip-slip, limits) (P1)
- #398 9-3 Namespaced mod assets (P1)
- #399 9-4 Keyed list merge, `$remove` and `replace` in record patches (P1)
- #400 9-5 Shader and `.fx` support in mods, and mod-side tooling (P2)

Milestone R1, Tooling and the first release (epic #292):

- #293 R1-1 sage package: build a shippable game folder (P1)
- #302 R1-10 Cooked asset formats and a cook step for Shipping (P3)
- #301 R1-9 An in-game problems list without the editor (P3)

Milestone 4m, World, logic and saves: #287 4m-13 Hot reload for data beyond records (P3).

Milestone 10b, Editor part 2: #366 10b-1 Asset browser and material preview (P1).

## 11. References

- [Design 05, Assets and VFS](../../design/05-assets-and-vfs.md): §3.1 VFS, §3.5 data records and their build notes, §3.6 client loading and hot reload, §3.7 string tables, §8 errors.
- [Design 07, Materials and shaders](../../design/07-materials-and-shaders.md) §3.3 material records.
- [REDESIGN](../../REDESIGN.md) §4.2 Content authoring and errors.
- [MAKING_A_GAME](../../MAKING_A_GAME.md) §3 Records and §9 Checking your game without playing it.
- [MODDING](../../MODDING.md) §5 to §6 for patches and the conflict report.
- Related sheets: [03 App and loop](03-app-and-loop.md) for boot and sealing, [04 ECS and scenes](04-ecs-and-scenes.md) for prefabs and scenes, [07 Rendering](07-rendering.md), [19 Modding](19-modding.md), [20 Tooling and release](20-tooling-and-release.md).
