# Sage · API and interface contract

> Version 1, 2026-10-02. This is the contract game code, kits and mods program against: what is
> promised, how it may change, and the rules a caller must follow. [SRS.md](SRS.md) says what the engine
> must do (this document serves REQ-QUAL-06 and REQ-QUAL-10); [`../MAKING_A_GAME.md`](../MAKING_A_GAME.md)
> teaches the same API by example; [`../RELEASING.md`](../RELEASING.md) is the release procedure; and
> REDESIGN §3.3–§3.6 ([`../REDESIGN.md`](../REDESIGN.md)) is the reasoning behind it.

The authority on what is public is the pair of `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` files
beside each assembly's `.csproj`. Where this document and those files disagree, the files win and this
document has a bug.

## 1. Purpose and the stability promise

### 1.1 What the contract covers

| Surface | Where it is defined | Checked by |
|---|---|---|
| Public C# API of `Sage.Core`, `Sage.Simulation`, `Sage.Physics3D`, `Sage.Gameplay`, `Sage.UI`, `Sage.Editing`, `Sage.Client`, `Sage.Kits.Rpg`, `Sage.Kits.Rpg.Client` | `src/<Assembly>/PublicAPI.*.txt` | PublicApiAnalyzers (RS0016/RS0017) on every build |
| Stable ids: components, tags, record types, saved resources, prefab parts, systems, render passes, vocabulary entries, plugins | The declaring attribute (§4) | Sage.Generators (SAGE00xx), the registry dump |
| Data formats: `game.json`, `mod.json`, records, prefabs, scenes, saves | Record classes and their metadata; JSON Schemas in `schemas/` | Strict loading, `sage validate`, golden saves |
| Console vocabulary: commands, cvars, entity inputs and outputs, input actions | Registrations in modules' `Init` | The registry dump and `tools/check_docs.py` |

Not covered: anything `internal` (the engine's own systems, built-in vocabulary entries, the renderer's
snapshot, audio backends, the `.map` reader, console registrars), the host (`Sage.Host`), the `sage`
CLI's C# API, the editor host (`Sage.Editor`) and the generators. Nothing compiles against those.

### 1.2 Versioning

- **One version, from git tags.** `build/Sage.Version.props` runs MinVer in every engine project and in
  the `Sage.Sdk`, `Sage.Player` and `Sage.Templates` packages. A tagged commit `v0.1.0` is `0.1.0`; later
  commits are `0.1.1-alpha.0.N`. `BuildInfo.EngineVersion` (with the commit hash) is what the host logs,
  saves record and `version` prints; `BuildInfo.EngineSemVersion` is what ranges are checked against.
- **SemVer, 0.x rules.** Before 1.0 the *minor* is the breaking number: 0.2.0 may break what 0.1.x code
  compiles against; a 0.1.x patch may not. From 1.0 the major is. "Breaking" means a declared public
  symbol was removed or changed, or a record, component or save format changed without an upgrader.
- **Release state.** 0.1.0's API is frozen: it is in the Shipped files (#295 R1-3), and the release workflow
  (`.github/workflows/release.yml`) builds, tests and packs the tagged commit and attaches the packages to a
  GitHub release. `v0.1.0` is tagged (on `main` 3bf1881) and released with its packages; builds after it
  report `0.1.1-alpha.0.N`.

### 1.3 The declared API files

Every public type or member of the assemblies in §1.1 has a line in Shipped (released) or Unshipped
(changed since). A public addition without a line is **RS0016**; a declared symbol that is gone or
changed is **RS0017**; both are errors in every configuration on both CI jobs. Removing a shipped symbol
means writing it into Unshipped prefixed `*REMOVED*`. The diff of these files is the API review. Tagging a
release moves Unshipped into Shipped (RELEASING §4).

### 1.4 Experimental areas

An area that is new or known to be about to change carries
`[System.Diagnostics.CodeAnalysis.Experimental("SAGE01xx")]`. It is declared like the rest, but naming
it from another assembly is a build error until that project adds the id to `<NoWarn>`. Content is not
affected: records that use these areas load without any opt-in. Ids are never reused; an area leaves
experimental by deleting the attribute, which is not an API change. The full member lists are in
[MAKING_A_GAME §10b](../MAKING_A_GAME.md#10b-experimental-api).

| Id | Area |
|---|---|
| SAGE0120 | The open vocabularies' contracts: `ICondition`, `IAction`, `IItemUse`, `IAbilityDelivery`, `IEffectExecution`, `IAICondition`, `IAIScheduleSelector`, `QuestObjective`, their attributes and contexts |
| SAGE0121 | Scenes and placements in C#: `SceneRecord`, `Scenes`, `Placement`, `PlacementsRecord` |
| SAGE0122 | TrenchBroom brush maps: `MapRecord`, `MapLevel`, `BrushGeometry` |
| SAGE0123 | Cameras as entities, render targets, rigs, blends, scripted cameras |
| SAGE0124 | Phase 4b logic: `Conditions`, `Vars`, topics, the vocabulary shorthand, easing, timers, tweens, logic entities, state machines, `EntityIO.Fire` with a value |
| SAGE0125 | The retained game UI, all of `Sage.UI`, and the kit's view-models |
| SAGE0126 | Skeletal animation: skeletons, clips, skinning, sockets and IK, animation graphs, viewmodels, clip events |
| SAGE0127 | Weapons and combat: the hit pipeline, `hit_delivery`, hit locations, ammunition, spread and recoil |
| SAGE0128 | Reserved for phase 4f |
| SAGE0129 | The open world: calendar, dormant cells, streamed scenes, routines, travel, off-screen simulation, terrain materials (#307) |
| SAGE0130 | World clock and sky, the render pass registry, shadows (cascades, `ShadowAtlas`), fog, post effects, a material's surface fields (#410) |
| SAGE0131 | Saves you can trust: placeholders, prefab overrides, reconciling loads, quick-save and autosave |
| SAGE0132 | Data mods: manifests, load order, content report, `ModManager` |
| SAGE0133 | The editor's model, all of `Sage.Editing` |
| SAGE0134 | Physics joints and ragdolls |

New ids come from SAGE0120–0139.

### 1.5 Obsolete symbols

A shipped symbol that is replaced is not deleted in the same release. It is marked `[Obsolete("...")]`
with a message that names the replacement, kept for at least one minor release, and then removed with a
`*REMOVED*` line. Examples in the code: `WorldResources.Set` (use `Add` or `Replace`) and
`ActiveCamera.RigEnabled` / `DrivenByRig` (inert since #81). An obsolete member may become inert before it
is removed; its message says so.

### 1.6 Saying which engine you need

The plugin id `sage` is the engine. A plugin, kit or game module states the engine versions it was
built for with `[RequiresPlugin("sage", "^0.1")]`; a game can say the same in `game.json` as
`"sage": "^0.1"`, and a mod in `mod.json`. Ranges are `VersionRange` (`*`, `0.1.0`, `>=0.1 <0.3`,
`^0.1`, `~1.2`). An engine outside the range is a load error naming the plugin, the range and the
engine's version (test: APluginForAnotherEngineIsALoadErrorThatSaysBothVersions)
(test: AGameMadeForAnotherEngineIsALoadErrorThatSaysBothVersions). No plugin may take the id `sage`
(test: NoPluginMayCallItselfSage).

## 2. Layers and the shapes of a game

### 2.1 Which assemblies a game may use

| Assembly | Layer | May use MonoGame | Typical caller |
|---|---|---|---|
| `Sage.Core` | Kernel: logging, cvars, VFS, records, declarations | No | Everything |
| `Sage.Simulation` | World, ECS, events, entity I/O, prefabs, scenes, saves, `IPhysicsWorld` | No | Simulation half of a game |
| `Sage.Physics3D` | The BepuPhysics backend behind `IPhysicsWorld` | No | Rarely named directly |
| `Sage.Gameplay` | Rules, attributes, combat, items, AI, quests, dialogue | No | Simulation half |
| `Sage.UI` | Retained widgets, screens, view-models (headless) | No | Either half |
| `Sage.Editing` | The editor's headless document model | No | Tools, editor extensions |
| `Sage.Client` | Rendering, audio, input devices, screens | Yes | Client half of a game |
| `Sage.Kits.Rpg`, `.Client` | The action-RPG kit | Client half only | Games built on the kit |

Two analyzers police the layers. **SAGE0024**: a project with `<SageSimulationOnly>true</SageSimulationOnly>`
may not name a `Microsoft.Xna.Framework` type; every engine project under `src/` is simulation-only
except `Sage.Client`, `Sage.Editor` and `Sage.Host` (test: EveryBaseAssemblyIsSimulationOnly).
**SAGE0025**: a base-engine assembly may not reference a `Sage.Kits.*` assembly or use its types
(test: ABaseAssemblyThatUsesAKitIsABuildError). **SAGE0050**: no `Friflo.*` type may be named outside
`Sage.Simulation` (test: NamingFrifloOutsideTheEcsImplementationIsABuildError); use Sage's `Entity`,
`IComponent`, `ITag`, `Tags`, `Query<...>` and `EntityCommands`.

### 2.2 Three shapes of a game

1. **Data only.** A `game.json` and records; no assembly. The engine places the scene, spawns the player
   from it, and runs every plugin the manifest selects. Example: `tests/games/scene-only`.
2. **Data plus a game assembly.** One project built with `Sage.Sdk` holds exactly one public
   `IGameModule` (the simulation half); an optional second project, a plain `IModule`, is the client half
   and is loaded through `modules.add`. Examples: `games/Hello` (simulation only), `games/Sandbox` and
   `games/Sandbox.Client`.
3. **A kit.** A reusable assembly pair of genre rules on public base API only, with a `[Plugin]` module,
   optional `[PluginContent("ns")]` embedded content, and its own `PublicAPI` files. A game names it in
   `game.json` `"kits"` and in its project as `<SageKit Include="sage.kits.rpg" />` (SAGE0114 if one is
   missing). Example: `src/Sage.Kits.Rpg`.

## 3. Entry points

### 3.1 `game.json`

Read strictly by `GameManifest` (`src/Sage.Core/GameManifest.cs`): an unknown key is an error.

| Key | Meaning |
|---|---|
| `id` | The game's plugin id and its record namespace (`yourgame:thing`). Required. |
| `name` | Display name. |
| `assembly` | The simulation assembly, relative to the game folder; `{config}` is the build configuration. Omitted for a data-only game. |
| `mounts` | Content folders, layered after engine and kit content, later wins. |
| `modsDirectory` | The game's own data-mods folder (default `mods`). |
| `scene` | The `scene` record every world starts in. |
| `plugins` | Which engine plugins to load, by id or `family.*`; dependencies come along. Omitted means all. |
| `kits` | Kits the game is built on, by plugin id. |
| `modules.disable` / `modules.add` | Engine modules to switch off; further module assemblies (a client half). |
| `sage` | The engine range the game was made for. |
| `version` | The game's own version, which a mod's `gameVersion` is checked against. |

### 3.2 Modules and their hooks

A module implements `IModule` (`src/Sage.Simulation/App/Modules.cs`); the game's one module also
implements `IGameModule`. Hosts boot through `SageApp`, whose stages run the hooks in this order:

| Stage (`AppStage`) | Hook | Allowed | Not allowed |
|---|---|---|---|
| Create | none | The game assembly is loaded; the ECS schema is built from loaded assemblies | |
| Register | `Init(ModuleContext)` | Register cvars, commands, entity inputs and outputs, input actions, screens, render passes, save converters, optional parts, record checks | Reading records, loading assets |
| Configure | none | `config.cfg` runs, then launch arguments | |
| LoadContent | none | Records load from every mount and are checked | |
| Start | `Start(ModuleContext)` | Read records, load assets, take services with `ctx.Get<T>()` | Registering anything (SAGE0020) |
| CreateWorld | `OnWorldCreated(World)` | Install resources, add, replace or disable systems | Registering anything (SAGE0020) |
| CreateWorld | `IGameModule.CreateRules(World)` | Return the world's `GameRules` (null: `DefaultGameRules`); its `OnWorldStarted` runs next | Registering anything (SAGE0020) |
| Shutdown | `Shutdown()` | Release what `Start` took, in reverse order | |

Declared things (records, saved resources, prefab parts, vocabulary entries, components) are
registered by generated code just before the owning plugin's `Init`, so they exist by the time `Init`
runs. A cvar registered after `Init` misses `config.cfg`; that is why SAGE0020 makes registration in
`Start`, `OnWorldCreated`, `CreateRules` or a system's constructor or `Run` a build error
(test: RegisteringInInitIsNotAnError). At run time each registry has a `RegistrationSeal` that throws
`InvalidOperationException` naming the registry and the stage once it is closed.

`ModuleContext.Get<T>()` returns a service from the host or from a module listed in `Dependencies`;
anything else throws, which keeps dependency lists honest. `ModuleContext.Provide<T>()` offers one.

### 3.3 Plugins, ids and order

`[Plugin("id", "version")]` gives a module its identity; `[RequiresPlugin("id", range)]` names a
dependency by id, and `IModule.Dependencies` by C# type for assemblies that reference each other. Plugin
ids are lower-case and dotted by convention (`sage.gameplay.items`, `sandbox.client`); two modules with
one id are an error. Modules are sorted dependencies first; otherwise they keep the order they were added in (engine
plugins, kits, the game's assembly, then `modules.add`). A missing dependency or a version outside its
range is fatal at load. Everything a plugin registers is recorded against its id
(`RegistrationLedger`), which `plugins <id>`, `sys_list` and the registry dump report.

## 4. Declarations (the core contract)

"Declare, don't register": one attribute per concept, and Sage.Generators writes the registration, the
metadata table and the schema. When an assembly has several `[Plugin]`s, say which owns a declaration
with `Plugin = "id"`. All analyzer ids are errors; the table is
[MAKING_A_GAME §10a](../MAKING_A_GAME.md#10a-build-errors-the-sage-diagnostics) and the release list is
`src/Sage.Generators/AnalyzerReleases.Unshipped.md`.

| Attribute | Goes on | Stable id | Analyzer ids |
|---|---|---|---|
| `[Component("ns:name", Version, FormerNames)]` | `struct : IComponent` | The component id in prefabs and saves | SAGE0004–0007 |
| `[Tag("ns:name")]` | `struct : ITag` | The tag id | SAGE0004–0006 |
| `[Record("type", Reload)]` | A class with a public parameterless constructor | The record `type`; `Reload = ReloadPolicy.Live` makes a hot reload reach what was built from it (`prefab`, `scene`), `NextSpawn` (the default) only what is built next (#287; `RecordStore.ReloadPolicyOf`) | SAGE0001–0003, SAGE0021 |
| `[SavedResource("name", Version)]` | `class : ISavedResource` | The key in the save | SAGE0001–0003, SAGE0021–0022 |
| `[PrefabPart("id", After, Shorthand)]` | `class : IPrefabPart` | The key under `parts` | SAGE0010–0012 |
| `[System("id", Phase.X, Before, After)]` | `class : ISystem` | The system id | SAGE0011–0013 |
| `[RenderPass("ns:id", RenderStage.X)]` | `class : IRenderPass` | The pass id (SAGE0130) | Load error if missing |
| `[Vocabulary("name")]` and an entry attribute | An interface or abstract class; its entries | The entry id | SAGE0100–0104 |
| `[Property(Min, Max, Unit, Tooltip, Category)]` | A field | | SAGE0040 |
| `[RecordRef("type")]` / `[AssetKind("kind")]` | A `RecordId` / `AssetPath` field | | SAGE0041 / SAGE0042 |
| `[Transient]` | A field, or a component type | Not saved | SAGE0023 (strict saves) |
| `[Upgrade(fromVersion)]` | `static void X(ref JsonObject o)` on the type | | SAGE0007, SAGE0022 |
| `[SchemaShape(json)]` | A JSON converter or the type it reads | | |

**Components and tags.** Ids are `namespace:name` in the record id alphabet, chosen once. Every public
field of a component is saved unless `[Transient]`; adding a field needs no version bump. From
`games/Sandbox/SandboxModule.cs`:

```csharp
[Tag("sandbox:faces_camera")]
public struct FacesCamera : ITag { }

[Component("sandbox:hop")]
public struct Hop : IComponent
{
    [Property(Unit = "m", Tooltip = "The ground it lands back on")] public float BaseY;
    [Property(Unit = "m/s", Tooltip = "Upward speed; set by a hop, pulled down by gravity")] public float Velocity;
}
```

**Renaming a saved field** bumps `Version` and adds an upgrader; upgraders run oldest first before the
JSON is read. Renaming the id itself costs one `FormerNames` entry.

```csharp
[Component("yourgame:lamp", Version = 2)]
public struct Lamp : IComponent
{
    public float Lumens;
    [Upgrade(1)] private static void From1(ref JsonObject o) => o.RenameField("Brightness", "Lumens");
}
```

**Records**, with metadata, from `src/Sage.Kits.Rpg/ItemGrid.cs`:

```csharp
[Record("rpg_item", Plugin = RpgKitModule.Id)]
public sealed class RpgItemRecord
{
    [Property(Min = 1, Max = 16, Tooltip = "The squares it takes in an inventory grid, [columns, rows]")]
    public Vector2 Grid = Vector2.One;
}
```

A reference to another record is best typed `RecordRef<T>` (checked by type at load); a plain
`RecordId` field says what it names with `[RecordRef("anim_graph")]`, and an `AssetPath` field with
`[AssetKind("texture")]`. A record type can add semantic checks with `RecordStore.AddCheck<T>` in `Init`.

**Saved resources** are per-world singletons in the save, from `src/Sage.Kits.Rpg/Spellmaker.cs`:

```csharp
[SavedResource("spellbook", Plugin = RpgKitModule.Id)]
public sealed class Spellbook : ISavedResource
{
    public List<SpellDraft> Drafts { get; set; } = new();
    public void AfterLoad(World world) => Spellmaker.Restore(world);
}
```

**Prefab parts.** A new instance is read from the prefab's JSON for each entity, so the fields are that
entity's options. What a part needs beyond them it takes from `ctx.Get<T>()` at apply time.

```csharp
[PrefabPart("hop")]
public sealed class HopPart : IPrefabPart
{
    public void Apply(in PrefabPartContext ctx) =>
        ctx.World.Add(ctx.Entity, new Hop { BaseY = ctx.World.Get<Transform>(ctx.Entity).LocalPosition.Y });
}
```

A simulation half that knows a client-only part may be absent says so with
`ctx.Engine.Prefabs.Optional("box_mesh")` in `Init`.

**Systems.** The attribute gives the id, phase and order *by id within the phase*; the module still
constructs the system, so it can take what it needs through its constructor.

```csharp
[System("sandbox.combat_log", Phase.Late)]
public sealed class CombatLogSystem : ISystem
{
    private readonly EventReader<Damaged> _damage;
    public CombatLogSystem(World world) { _damage = world.Events.Reader<Damaged>(this); }
    public void Run(in SystemContext ctx) { foreach (ref readonly var ev in _damage.Read()) { /* ... */ } }
}
// in OnWorldCreated:  world.AddSystem(new CombatLogSystem(world));
```

Naming a system in another phase, or an id nothing declares, is an error when it is added; `"?id"` is a
soft dependency on a plugin that may be absent. `RunCondition` (`WhenNotPaused`, `Always`, `DevOnly`)
is set on the attribute.

**Vocabulary words** are the open sets content names by id. The engine's are listed in §8.3; a game adds
an entry with the vocabulary's attribute, and its own vocabulary with `[Vocabulary("name")]` on an
interface plus an attribute deriving from `VocabularyEntryAttribute<T>`.

```csharp
[Condition("is_night")]                       // { "is_night": true } in any "requires"
public sealed class IsNight : ICondition       // SAGE0120 (and SAGE0130 for WorldClock): opt in
{
    public bool Test(in ConditionContext c, out string why)
    { why = "it is day"; return WorldClock.Of(c.World).Hour is < 6.0 or >= 21.0; }
}
```

**`[SchemaShape]`** describes, as a JSON Schema fragment, a value read by a custom converter (a colour
written `"#RRGGBB"`); without it the schema is open and only the load checks the value. Examples are in
`src/Sage.UI/UiRecords.cs`.

## 5. The world API

All of this is on `World` (`Sage.Simulation`) or its extension classes; names are as declared.

| Need | API |
|---|---|
| Entity handle | `Entity`: `Id`, `Name`, `IsNull` (true once the entity is gone, even if its id is reused), `Tags`, `Parent` |
| Direct access (outside a query loop) | `world.Get<T>(e)`, `TryGet<T>`, `Has<T>`, `Add<T>`, `Remove<T>`, `Destroy(e)`, `IsAlive(e)`; `Get<T>` on a missing component throws in every build (test: Get_ReturnsRef_MissingComponentThrowsInEveryBuild) |
| Queries | `world.Query<T1..T5>()` made once in a constructor, filtered with `AllTags(Tags.Get<X>())` / `WithoutAllTags(...)`, walked with `.Chunks` (no allocation) or `.Entities`; `.Entities.ToEntityList()` copies |
| Deferred structural changes | `ctx.Commands` / `world.Commands` (`EntityCommands`): `Add`, `Remove`, `AddTag`, `RemoveTag`, `Destroy`, `SetParent`; applied at the end of the phase |
| Persistent identity | `world.MakePersistent(e)` returns a `PersistentId`; `world.Resolve(id)` finds the entity again |
| Resources | `world.Resources`: `Add<T>` (throws if present), `Replace<T>` (on purpose; disposes the old), `Get<T>` (throws if absent), `TryGet<T>`, `GetOrAdd<T>`, `Remove<T>` |
| Events | `world.Events.Send(in ev)`; `world.Events.Reader<T>(owner)`; `reader.Read()`; `Release(owner)` (§9) |
| Systems | `world.AddSystem(system)`; `world.Systems.Replace(id, s)`, `Disable(id)`, `Find(id)`; a system that implements `IDeclaresAccess` declares what it touches in `Declare(SystemAccess)` (`Reads<T>`, `Writes<T>`, `ReadsEvents<T>`, `Sends<T>`, `ReadsResource<T>`, `WritesResource<T>`, `Exclusive()`) and may run at the same time as others of its phase it does not conflict with (#288) |
| Entity I/O | `world.FireOutput(source, "OnX", activator)` (and value overloads); `world.IO().FireInput(target, "Open", parameter, delay)`; `world.FindByName(name)`; inputs registered with `engine.Inputs.Register(name, handler)` or `Register<TComponent>(...)`, outputs declared with `engine.Outputs.Declare(name, help)` |
| Spawning | `world.Spawn(prefabId, position, yawDegrees)`, and the overload with `PrefabOverrides` (SAGE0131) |
| Timers, tweens and spawners | Usually content (`timer`, `tween`, `spawner` parts); in code `Timers.Start`, `Tweens.Begin`, `Spawners.Spawn(world, spawner)` (SAGE0124) |
| Records | `world.Resources.Get<RecordStore>()` or `engine.Records`: `Get<T>(id)`, `TryGet<T>`, `All<T>()` |
| Physics | `world.Resources.Get<IPhysicsWorld>()`: `Raycast`, `Sweep`, `TriggerEnter`/`TriggerExit`, layers by name |

An input handler is an `EntityInput` delegate, `(World world, in IOContext io)`, where `io.Self` is the
target, `io.Activator` started the chain, `io.Caller` fired the output and `io.Parameter` is the text
(`io.Number()` reads it as a number).

## 6. Content as an interface

### 6.1 Records

A content file is a JSON (JSONC accepted) array of records. Every record has `type` and `id`; every
other key is a field of the record class, read strictly: an unknown field is an error at its file, line
and column with the nearest real name.

```jsonc
[
  { "type": "item", "id": "falchion", "label": "falchion", "attack": "falchion_cut", "value": 30 },
  { "type": "prefab", "id": "village:trader", "patch": true,
    "parts": { "inventory": { "items+": [ { "item": "falchion" } ] } } }
]
```

- **Ids** are `namespace:name`. A bare id means the namespace of the mount it is written in: the game's
  `id`, a mod's `id`, a kit's `[PluginContent]` namespace (`rpg`), or `sage` for engine content.
- **Patches** (`"patch": true`) name only what changes: values replace, objects merge recursively, lists
  replace unless written `field+` (append) or `field-` (remove equal entries), at any depth.
  `"disabled": true` in a patch removes the record. Redefining an existing id without `patch` is an error.
- Prefabs have `name`, `components` (keyed by component id), `tags`, `parts` (keyed by part id),
  `children` and `persist`. Scenes have `origin`, `relativeTo`, `player` and `place`, a list of
  placements with `prefab`, `at`, `yaw`, `name`, `id`, `outputs` and `overrides`. A wire is
  `{ "output", "target", "input", "parameter", "delay", "times" }`; targets may be a name, `!self`,
  `!activator` or `!caller`.

### 6.2 Schemas and validation

`sage schema <games...> --out schemas` writes a JSON Schema per record type, plus `game.json` and
`mod.json`, from the metadata table; the files in `schemas/` are generated, never edited, and CI diffs
them. `sage validate <game>` runs the game's whole content load headlessly and exits 1 on any error;
`--mods` and `--game-mods` add mods, `sage mods` reports load order and conflicts. The reference for
fields and parts is MAKING_A_GAME §3; for patches, [MODDING §5](../MODDING.md#5-adding-and-changing-records).

## 7. Console and cvars as an interface

Commands and cvars are registered on `engine.CVars` (a `CVarRegistry`) in `Init` only:

```csharp
var rate = ctx.Engine.CVars.Register("yourgame_spawn_rate", 2f, CVarFlags.Archive,
    "Seconds between spawns.", 0.1f, 60f);
ctx.Engine.CVars.RegisterCommand("yourgame_wave", CVarFlags.Cheat, "Start the next wave now.", args => { /* ... */ });
```

| Flag | Meaning |
|---|---|
| `Archive` | Saved to `config.cfg` on shutdown, read back after `Init` (then `autoexec.cfg`, #299) |
| `Cheat` | Changeable only while `sv_cheats` is 1 |
| `DevOnly` | Exists only in Debug and Development builds; not registered in Shipping, where a `config.cfg` or `autoexec.cfg` that names one is an Info note, not a warning (#293) |
| `ReadOnly` | Shown, not changeable from the console |

**Naming.** Lower-case `snake_case`, prefixed by area. The engine's prefixes include `r_` (rendering),
`sim_`, `ev_`, `ent_`, `sys_`, `rec_`, `io_`, `in_`, `snd_`, `phys_`, `nav_`, `ui_`, `save_`, `mod_`,
`ed_` (editor) and `sv_`. A game's or mod's own names start with its id (`sandbox_spawn_rate`). A name is
a public, versioned identifier once shipped: scripts, `config.cfg` and launch lines (`+name value`) use
it. The full set is in the registry dump (`Sage.Host -dump-registry`), and `help`, `find` and
`cvarlist` list it in game.

## 8. Extension points

### 8.1 Rendering (client half, SAGE0130)

A pass implements `IRenderPass` (`Extract(RenderContext)`, `Draw(RenderContext)`), is declared with
`[RenderPass("ns:id", RenderStage.X, After/Before)]`, and is added in `Init` with
`ctx.Get<RenderPasses>().Add(new MyPass())` (the module lists `ClientModule` in `Dependencies`). Stages run Shadow, then per view Opaque, AlphaTested, Sky,
Transparent, Debug, then PostProcess and Overlay. `RenderContext` gives the device, the world, views
(`AddView`, `ViewInfo`), items (`AddItem`), render targets (`Target`, `SetTarget`) and `DrawFullScreen`.
`r_passes` lists the order. An engine pass is replaced (`ctx.Get<RenderPasses>().Replace("sage:sky", new MySky())`,
same stage; it keeps the id and its ordering) or left out (`Disable("sage:debug", by)`) in `Init`; both are applied
at the seal, one change per id (two modules changing one pass is a load error naming both), and a constraint naming
a disabled pass is dropped (#322). `r_snapshot_dump [file]` writes the next frame's passes, views and items.

### 8.2 UI (SAGE0125)

Screens are `screen` and `ui_layout` records over a view-model. A view-model implements `IViewModel`
(`Refresh`, `Activate`, `Back`) and is declared `[ViewModel("id")]`; `UiScreenStack` opens screens and
binds them to input actions (`Bind`, `OpenHud`, `Close`); `Typing` says a text field has the keyboard. There is no
registry of code-built screens since #350: a screen is a record, and a tree built in C# is pushed with `Push`.

### 8.3 Vocabulary words

| Vocabulary | Interface | Attribute | Assembly |
|---|---|---|---|
| Conditions | `ICondition` | `[Condition]` | Simulation |
| Actions | `IAction` | `[Action]` | Simulation |
| Quest objectives | `QuestObjective` | `[QuestObjective]` | Gameplay |
| Item uses | `IItemUse` | `[ItemUse]` | Gameplay |
| Ability delivery | `IAbilityDelivery` | `[AbilityDelivery]` | Gameplay |
| Effect executions | `IEffectExecution` | `[EffectExecution]` | Gameplay |
| AI conditions, schedule selectors | `IAICondition`, `IAIScheduleSelector` | `[AICondition]`, `[AIScheduleSelector]` | Gameplay |
| Hit delivery | `IHitDelivery` | `[HitDelivery]` | Gameplay (SAGE0127) |
| View-models | `IViewModel` | `[ViewModel]` | UI |

Entries must be public (or internal) with a public parameterless constructor; settings are fields read
from the entry's JSON. Asking a condition must not allocate: `why` is a constant string
(test: EvaluatingConditionsAllocatesNothing). Vocabularies seal when content loads
(test: VocabulariesAreSealedWhenContentLoads).

### 8.4 Physics

Game code talks to physics only through `IPhysicsWorld` (`src/Sage.Simulation/Physics/IPhysicsWorld.cs`):
queries, triggers, layers by name, and since phase 4k joints and collision groups (SAGE0134). The Bepu
space in `Sage.Physics3D` is an implementation detail, so a 2D backend can replace it.

### 8.5 Saves

A component is saved by declaring it (§4); a world singleton by `[SavedResource]`; a value type the save
cannot write by default by `SaveSystem.AddConverter` in `Init`. `GameRules.OnLoaded` runs after a load;
`ISavedResource.AfterLoad` runs per resource. A save is snapshotted on the tick and, when taken while
playing, written on a thread-pool thread (#285): the snapshot copies component columns, a component made only of values and strings is serialised
by the writer, and one with entity references, lists or a plugin's converter (it may read the world) in the snapshot, on the tick, per entity. A client sets
`SaveSystem.Thumbnail` to hand over the frame's pixels; `SaveSystem.WaitForWrites`, `IsWriting` and
`Report(slot)` (a `SaveVersionReport`) are SAGE0131, as are `SaveSlot.Title`, `ThumbnailPath` and `Compressed`.

## 9. Events and messages

There are four mechanisms ([design 04 §3.1](../design/04-events-and-messaging.md)); game code uses the
first three.

| Mechanism | Delivery | Use for |
|---|---|---|
| Game events (`[GameEvent]` struct, `world.Events`) | Queued per schedule; each reader has a cursor | Facts between systems: `Damaged`, `Died`, `Used` |
| Entity I/O (outputs to inputs) | Queued by due time, dispatched in `Phase.EntityIO` | Level logic wired in data |
| Structural events (`Added<T>`, `Removed<T>`, #282) | Queued like game events, read by cursor (`world.Events.Reader<Added<Health>>(this)`); published only for a type somebody reads; the immediate `World.ComponentAdded`/`ComponentRemoved` stay for the world's own bookkeeping | Reacting to a component being gained or lost: mirrors, indexes, effects |
| Engine signals (`engine.Signals`, plain C# events; and the `EngineSignal` game event in each world) | Immediate, main thread, outside ticks; in the world, queued for systems | World created, scene loaded, world destroying, paused and resumed (raised by the world on the first tick that sees its pause changed); `RecordStore` reloads, cvar changes |

**Game event guarantees.** One queue per event type per schedule (Fixed by default). A reader sees every
event sent after it was created, exactly once, in send order, which is deterministic because systems run
in a deterministic order. A reader is always later than the sender: a reader that runs after the sender
in the same tick (a later phase, or later in the same phase) sees the event that tick; one that runs
before it sees the event on its next run; a Frame reader of a Fixed queue sees every tick's events, even
when several ticks ran in one frame. An event is
dropped once every reader has passed it; a queue with no readers drops at once. `ev_maxage` (default 8
ticks) drops older events with a warning naming the lagging reader. Pruning happens only at the end of
a schedule, never mid-phase. `ev_stats` and `ev_trace` show the queues.

**Entity I/O guarantees.** `FireOutput` queues each wire's input; a wire with no delay arrives in the
next `Phase.EntityIO` dispatch (the next tick when fired from that phase), unless fired with `sameTick`
(SAGE0124). Due inputs run in due time order, ties broken by queue order. One tick delivers at most
`EntityIO.Budget` inputs (256), with a warning past it. An input with no handler on the target is
refused with a warning naming the components that take it. A handler that throws costs that one
delivery: it is logged with the wire and the dispatch goes on (#402). A target `@group`, `@class:prefab`
or `@tag:id` is resolved when the input arrives, one delivery per member (#276). `io_list`, `io_trace`
and `io_history` show the wiring.

**Do not, in a handler or system:**
- call another system, or raise a C# event that runs gameplay code synchronously; send a fact instead;
- change structure (add, remove, destroy) while walking a query; record it on `ctx.Commands`;
- send events or fire I/O from a job or another thread;
- put an `Entity` in an event and assume it is alive when read; carry what the reader needs.

## 10. Threading and lifetime

- **Main thread only.** Worlds, their events, I/O and resources, the record store and the console are
  single-threaded in v1. Jobs may work over copied input and hand results to a system; they never touch
  world structure or send events. The one exception is a system that declares its access (#288): it may
  run on a scheduler worker beside others of its stage, so it touches only what it declared, records
  structural changes on `ctx.Commands`, and declares a resource it took in its constructor too (a dev
  build reports anything else it touches, `sys_access_check`).
- **No MonoGame in simulation** (SAGE0024). Anything that decides belongs in the simulation half and is
  testable headlessly through `HeadlessApp` (`tests/Sage.Testing`).
- **Entity handles are per session.** An `Entity` is valid for one world and one run; check `IsNull` or
  `IsAlive` before using a stored one. Across a save, a level change or another session, keep a
  `PersistentId` (or a record id, or an authored placement name) and resolve it.
- **Registration only during Register** (§3.2). Hand a system its services and cvars through its
  constructor; take event readers there too, with `this` as owner, so removal, replacement or disabling
  releases them (and disposes an `IDisposable` system).
- **Records are shared and read-only at run time.** A hot reload keeps instances that still exist, so do
  not cache values copied out of a record across `RecordStore` reload signals.

## 11. Error contract

| Kind of failure | What happens |
|---|---|
| A declaration mistake (missing id, bad upgrader, late registration, wrong layer) | A build error, SAGE00xx |
| A public API change without its line | A build error, RS0016/RS0017 |
| Content: unknown field, bad id, dangling reference, wrong asset kind, redefined record, unknown key in `game.json` | A load error at `mount:path:line:column`, counted in `RecordStore.ErrorCount` and kept in `RecordStore.LoadErrors`; `sage validate` exits 1; the record or field is skipped and the game still boots |
| Content warnings: a patch of a record nobody defined, a record type of a disabled plugin | A load warning, `RecordStore.LoadWarnings` |
| Mod conflicts | Warnings in `ContentReport` (`user://logs/mod_report.txt`, `sage mods`), never errors |
| Engine or plugin version outside a range, a missing plugin or kit | Fatal at load, naming both versions or where it looked |
| Registering after a registry sealed; `Resources.Add` of a resource already present; `Get` of a missing resource or component | `InvalidOperationException` |
| A prefab part that throws while applied | Logged as an error with the prefab and part; the entity spawns without it |
| An exception in a system, input handler or event reader | Not caught by the scheduler; reaches the host's `CrashReporter` |
| Invariants | `Assert.Dev` (dev builds), `Assert.Ensure` (logs, returns false), `Assert.Check` (fatal, `SageFatalException`) |

The rule is that bad data fails at load with its location, and bad code fails at build time where it
can. Neither should surface as a crash in play.

## 12. Compatibility

- **Save format.** `SaveSystem.FormatVersion` is 4. Saves are keyed by stable ids with a version per
  entry; older formats load, a newer one is refused. `tests/Sage.Tests/Content/Saves` holds golden saves
  of formats 1 to 4 that must keep loading (`GoldenSaveTests`).
- **Saved shapes.** Renaming, moving or removing a saved field bumps `Version` and adds an `[Upgrade]`;
  without one the load error names the component and the field. Adding a field needs nothing.
- **Content changes.** An entity whose prefab is gone loads as an inert `SavePlaceholder` and is written
  back unchanged; data for a component this game lacks is kept as `UnknownSavedData` (SAGE0131).
- **Mods.** `mod.json` carries `game`, `gameVersion` and `sage` ranges; saves record the active mods and
  warn, then load, when they differ ([MODDING §7](../MODDING.md#7-saves)).
- **Deprecation window.** A shipped API symbol is `[Obsolete]` for at least one minor release before
  removal (§1.5); a stable id is never reused for something else; an experimental id is never reused.

## 13. Planned changes to the contract

| Change | Effect on callers | Milestone |
|---|---|---|
| Per-app services: log, user folder and crash reporter per `SageApp` instead of per process (#49) | Code that reaches process-wide services (static `Log`, `UserPaths`) gets a per-app route; statics stay as the host app's | Stage E |
| Code mods: one `AssemblyLoadContext` per mod, loaded before registries seal, `[RequiresPlugin]` checked (#396 9-1) | Mods gain the game assembly's contract; `"assemblies"` in `mod.json` stops being refused | Phase 9 |
| Packed `.sagemod` mods and namespaced mod assets (#397 9-2, #398 9-3) | Asset paths gain a mod namespace | Phase 9 |
| Keyed list merge, `"$remove"` and `"replace": true` in patches (#399 9-4) | Lists of objects with a key merge by key; per-key conflicts | Phase 9 |
| First tagged release (#295 R1-3; the Shipped freeze is done, the tag is the owner's push) | Version numbers stop being pre-releases | R1 |

Each experimental area in §1.4 is expected to leave experimental once its first outside consumer has
used it; the reason is given per id in MAKING_A_GAME §10b.
