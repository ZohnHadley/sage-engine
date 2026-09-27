# Sage redesign plan: an engine for designers, developers and modders

*Planning document, 2026-09-27. Based on `main` at `141097d` (112 commits). Six parallel reviews read
the whole repo: docs and history, core, content and modding, gameplay, rendering and editor, and code health.
Nothing was changed. Paths are relative to the repo root. Suggested home: `docs/REDESIGN.md`.*

---

## 0. The short version

Sage's foundations are good. Keep them:
- the headless sim/client split, enforced by the compiler;
- the record pipeline with per-field patching and provenance;
- the VFS;
- entity I/O;
- the console culture;
- phase contracts.

These are exactly what a designer, developer and modder engine needs. The redesign keeps them.
**Sage mostly doesn't need a rewrite. It needs its promises turned into mechanisms.**

The review found five structural problems. Each hurts at least one audience badly:

| # | Problem | Hurts most | Evidence |
|---|---|---|---|
| 1 | **Registration fails silently.** An attribute alone does nothing, and a manual `Register` call is needed too. MAKING_A_GAME §10 lists 11 "silently does nothing" traps, and the code review found 10 more. | Developers | `[SavedResource]` bug shipped 3×; point lights dead in Shipping with 514 tests green (handoff-09-25 §2); `Modules.cs:66,187`; `SystemScheduler.cs:72` |
| 2 | **"Engine" and "RPG kit" are one assembly.** 37% of `Sage.Engine` is Daggerfall-flavoured gameplay. The `Sage.Framework` layer in ARCHITECTURE §2 was never built. | Developers, modders | `src/Sage.Engine/Gameplay/` (7,117 lines); `Effects.cs:354-355` calls `Factions`/`Quests` directly; `SpellmakerScreen.cs:10-13` |
| 3 | **Designer logic stops at C#.** AI conditions, schedule choice, quest objectives, dialogue conditions and outcomes, effect executions, ability delivery and item use are all closed enums or private code. | Designers, modders | `AI.cs:17-44`, `AIThinkSystem.cs:300-330`, `Quests.cs:19-23`, `Dialogue.cs:21-71`, `Abilities.cs:19-26` |
| 4 | **Modding is a design doc, not a feature.** There's no mod loader. `ModsDirectory` is parsed and never used. Assemblies go into the Default load context. Asset paths collide silently. Saves don't record the mod list. Shipping has no tools. | Modders | `GameManifest.cs:23`, `Modules.cs:185`, `SaveSystem.cs:102-109`, TODO F37 `[ ]` |
| 5 | **Authoring tools are a dev overlay on the live world.** There's no undo, no placing, and no record forms. Inspector edits are silently lost on save. JSON has no schema, and a missing record becomes a default-constructed one. | Designers | `EntityInspectorWindow.cs:72-76` vs `Placements.cs:78-92`; `RecordStore.cs:116-121`; `PrefabParts.cs:22-35` |

**The core move:** make every declaration (component, record type, system, prefab part, condition or
action, cvar) a **single typed declaration with a stable string id, discovered at build time by a source
generator.** Everything the three audiences need hangs off that one registry:
- registration that can't be forgotten;
- JSON Schemas and autocomplete;
- editor forms;
- the TrenchBroom FGD;
- save identity and upgraders;
- mod conflict reports;
- analyzers.

The docs already call the generator "the biggest single unlock" (09 §3.2) and still list it fourth. **In
this plan it comes first.**

---

## 1. Who we are designing for

| Audience | Works in | Needs from Sage | Today |
|---|---|---|---|
| **Game designer** (non-programmer) | Editor, TrenchBroom, JSON with autocomplete | Place things, wire logic, tune numbers, make creatures, quests and dialogue *without C#*; errors they can see; undo | Hot-reload tuning is great. Everything else is hand JSON or C#. Errors go to a log file |
| **Game developer** (C#) | IDE, a game project | A small, typed, discoverable API; a project template that builds outside this repo; testable rules; extension points instead of forks | Good docs and a headless-test story. Games build only inside `games/`. Friflo leaks into every signature. One namespace, `sage_engine`, holds 350 public types |
| **Modder** (third party, shipped game) | Mod folder or package, the shipped editor, TrenchBroom | Add or patch content safely; declare dependencies; see conflicts; optionally ship code; saves that survive | Nothing is built beyond VFS shadowing |

**Design rule for every feature:** say which of the three audiences it serves, what they touch (data,
editor or C#), and how a mistake shows itself to *that* audience. A log line is fine for a
developer. It doesn't count as an error for a designer or modder.

---

## 2. What to keep (verified strengths)

- **Sim/client split.** `Sage.Engine` has no MonoGame, tests can't reference the client, and decisions
  live engine-side (`AudioMixer`, `Panel`, `InputGating`). This is also the future dedicated server.
- **Record pipeline.** Namespaced ids, `base`/`abstract`, per-field patch with `field+`/`field-`,
  redefinition treated as an error (the Bethesda lesson), `FieldOrigins` provenance with `rec_get`,
  record instances stable across hot reload, and runtime records that survive reload.
- **VFS** with shadowing and diagnostics (`vfs_which`, `vfs_mounts`), plus runtime asset loading (R12). A
  dropped-in file just works.
- **Entity I/O.** Resolved at load, errors name the file and line, deterministic, with a budget,
  `io_trace` and FGD export.
- **`PawnIntent` as the single control seam.** Players and AI are the same to movement and combat.
- **Event bus** with per-reader cursors, per-schedule queues, and an `ev_maxage` backstop that names the
  lagging reader.
- **Phase contracts, `Deferred<T>`, deterministic ordering, and loud errors on cycles.**
- **Console culture.** Every menu item is a command, there's scripted input with `wait`, and three build
  configurations.
- **TrenchBroom for brushes** instead of writing our own brush editor.
- **Screens as headless view-models,** and "can I?" answered in one place (R17).
- **Docs-as-tests** (`check_docs.py`) and the second-pass habit.

---

## 3. Target architecture

### 3.1 Assemblies (one namespace per assembly: `Sage.*`, not `sage_engine`)

```
                ┌─────────────────────────── games / mods ───────────────────────────┐
                │  MyGame (sim)   MyGame.Client   SomeMod (data)   SomeMod.Code (C#)   │
                └───────▲──────────────▲──────────────────────────────▲───────────────┘
   Kits (optional) ─────┤              │                              │
   Sage.Kits.Rpg        │  (spellmaker, readied spell, journal, dialogue/quest screens,
   Sage.Kits.Rpg.Client │   Daggerfall sprite conventions)
   Sage.Kits.Shooter…   │  (later: HL1 weapons, Lugaru melee, Warband battles)
                        │
   Sage.Gameplay ───────┤  attributes/tags/effects, damage, inventory, interaction, factions,
                        │  AI (behaviour trees + tasks), navigation, dialogue & quest *models*,
                        │  conditions/actions vocabulary, logic entities
   Sage.Simulation ─────┤  World/ECS facade, scheduler, events, physics, character controller,
                        │  transforms, streaming/origin, prefabs, entity I/O, saves, levels (.map)
   Sage.Core ───────────┘  app kernel, plugins, records, VFS, cvars/console, logging, diagnostics
                           — no MonoGame, no Friflo types in its public API

   Sage.Client            MonoGame: renderer (pass registry), UI drawing, audio backend, input devices
   Sage.Editor            editor model + ImGui/tool UI (plugins can extend it)
   Sage.Generators        source generator + analyzers (the declaration registry)
   Sage.Testing           headless SageApp builder, MountFixture, EventProbe — used by engine *and* games
   Sage.Sdk               MSBuild SDK + `dotnet new` templates (game, game-client, mod-data, mod-code)

   Hosts (thin):  Sage.Player (game exe) · Sage.Server (headless) · Sage.EditorHost · `sage` CLI
```

**Hard rules, enforced by the build:**
- No Simulation, Gameplay or Kit assembly references MonoGame. Keep it the way it is today, and add a
  banned-API analyzer.
- `Sage.Gameplay` never references a Kit.
- Nothing in Core, Simulation or Gameplay knows a `sage:` record id by constant.
  Conventions come from a `gameplay_conventions` record that the kit or game supplies. That replaces 15
  hard-coded `RecordId` constants (`Attributes.cs:26,35-36`, `AIThinkSystem.cs:326-330`, …).
- Cross-module calls become events. `Effects.Die` raises `Died`, and factions and quests subscribe to it.
- No `InternalsVisibleTo` except to test assemblies. If the editor needs something, it's public API.

### 3.2 One boot path: `SageApp`

Today boot is split between inline `Program.cs` and `Game1.Initialize`, and it's re-implemented
differently in 35 tests. The host ticks only one world (`Game1.cs:227-256`), although `Engine.Worlds` is
a list.

```csharp
SageApp.Create(args)
    .UseGame("games/MyGame")          // game.json → mounts, plugins, kit
    .AddPlugins(DefaultPlugins.Client) // Server host omits this
    .Run();                            // explicit stages: Discover → Register (sealed) → Load content
                                       // → Build ECS schema → Start → Create worlds → Loop (ticks every world)
```

- The **Player, Server, Editor and Test** hosts all call this. `Sage.Testing.HeadlessApp` is the same
  builder without a GPU, so tests run what ships. That removes the `ModuleSetTests` class of bug by
  construction.
- **Stages are sealed.** Registering a record type, cvar, action or plugin after its stage **throws**
  instead of being dropped. That fixes the cvar-in-`Start` trap (#58) and `Add` after `InitAll`.
- **The ECS schema is built once, after all plugins and mods load.** It stops depending on assembly
  load order (`EcsSchema.cs`).
- **Global statics become per-app services:** `Log`, `Profiler`, `CrashReporter`, `UserPaths`, the
  `AssetPath` interner and `BuildInfo`. `Log` can keep a static facade. Payoff: parallel tests, and an
  editor that can host a play session.

### 3.3 Plugins replace `IModule` with `Type[]` dependencies

```jsonc
// plugin identity, via [assembly: SagePlugin(...)] or plugin.json
{ "id": "sage.gameplay.ai", "version": "0.4.0", "sage": ">=0.4 <0.5",
  "kind": "simulation",               // simulation | client | editor | tool (enforced per host)
  "dependencies": { "sage.gameplay.effects": ">=0.4" } }
```

- Dependencies are string ids with SemVer ranges, not CLR `Type`s. A mod no longer has to compile
  against the exact assembly it depends on.
- Everything a plugin registers is **recorded against the plugin id.** That gives:
  - a conflict report, for example "two plugins set `GameRules`" or "mod X replaced system Y";
  - unloading;
  - an editor that can show where a thing came from.
- `Kind` is enforced: editor plugins load only in the editor host. Today `ModuleKind` is shown in
  `modules` and never enforced (`Modules.cs:168`).
- **Stable system ids** (`sage.character.move`) with `world.Systems.Replace/Disable(id)`, so mods can
  swap an engine system. A `before`/`after` that crosses phases becomes an error
  (`SystemScheduler.cs:72-73`). Event readers are released when their system is removed
  (`GameEvents.cs:154`).
- `GameRules` is created by an explicit `IGame.CreateRules(World)`. It's no longer a resource you must
  `Set` at the right moment (`Engine.cs:67-72`).
- `WorldResources.Set` splits into `Add` (throws if present) and `Replace` (logged, disposes the old
  value). `World` stops installing Terrain, Weather, PlayerInput and RenderEnvironment into every world
  (`World.cs:62-82`). Those plugins add them.

### 3.4 Declarations: the source generator (the load-bearing piece)

**One attribute per concept. The generator produces the registration, the metadata and the schema.
An analyzer rejects anything half-declared.**

```csharp
[Component("sage:health", Version = 2)]
public struct Health : IComponent {
    [Property(Min = 0, Unit = "hp", Tooltip = "Current hit points")] public float Value;
    [Transient] public float LastHitTime;          // not saved
    [Upgrade(1)] static void From1(ref JsonObject o) { o.Rename("hp", "value"); }
}

[Record("sage:item")]           public sealed class ItemRecord { public RecordRef<SoundRecord> PickupSound; … }
[PrefabPart("sage:light")]      public sealed class LightPart  { public Color Colour = Color.White; … }   // public, attributed
[System("sage.ai.think", Phase.Gameplay, After = "sage.ai.perceive")] public sealed class AIThinkSystem : ISystem { … }
[Condition("sage:has_item")]    public sealed class HasItem : ICondition { public RecordRef<ItemRecord> Item; public int Count = 1; }
[Action("sage:start_quest")]    public sealed class StartQuest : IAction { public RecordRef<QuestRecord> Quest; }
```

What the generator emits:

1. **Registration code** for each assembly, called by the plugin loader. You can no longer declare a
   type and forget to register it. That closes MAKING_A_GAME §10 items 1–4 and 7–9.
2. **A metadata table**: stable id, fields, types, units, ranges, tooltips, categories, enums and
   reference targets. The inspector, record forms, FGD export, console `ent_dump` and the docs all use
   it. Reflection stays only as a dev fallback, and the stack becomes trim/AOT-safe later.
3. **JSON Schemas** (`schemas/*.schema.json`) with a `type` discriminator. Record ids become
   `enum`/autocomplete lists at runtime through `sage schema`. See §4.2.
4. **Save identity**: a component is saved under its stable id, not its CLR name
   (`SaveSerializer.cs:85-91`), and carries a version with upgraders.
5. **Analyzers (compile errors):**
   - `[SavedResource]` without a plugin;
   - a cvar registered outside `Register`;
   - a `RecordRef<T>` to an unregistered type;
   - a public component field without a `[Property]`/`[Transient]` decision, when strict mode is on.

The ids are **namespaced strings**, so two mods each defining `Health` no longer silently overwrite each
other (`ComponentSchema.cs:39`).

### 3.5 Own the ECS vocabulary

Friflo is in every public signature today: `Entity`, `IComponent`, `ArchetypeQuery`, `CommandBuffer`,
`Tags`, plus a global `using Friflo.Engine.ECS` for games and a `Transform` alias hack
(`games/Directory.Build.props:14-18`). That makes Friflo 3.6's API and schema behaviour part of every
game's and every mod's contract.

- `Sage.Simulation` exposes `Entity` (a Sage handle), `IComponent`, `Query<T1..Tn>` (a zero-cost
  struct over Friflo chunks), `Commands` and `Tags`. Friflo becomes a private implementation detail.
- The three different "commands" get separate names:
  - console: `ConsoleCommand`
  - ECS: `EntityCommands`
  - input: `PlayerCommand`
- In Shipping, `World.Get<T>` on a missing component must not return a shared static dummy
  (`World.cs:143-144`). Use `TryGet`/`ref` with a dev assert, and fail loudly in all configurations for
  writes.

### 3.6 Public API discipline

- Add `PublicApiAnalyzers` (`PublicAPI.Shipped.txt`) on Core, Simulation, Gameplay and Client. Mark
  unstable areas `[Experimental]`.
- Use SemVer from git (MinVer or Nerdbank.GitVersioning) feeding `BuildInfo.EngineVersion`, which is
  always `1.0.0.0` today.
- Plugins and mods declare the `sage` range they need, and the loader checks it.

---

## 4. Pillar plans

### 4.1 Gameplay layering and data-only games (serves all three)

- **Split** `Sage.Engine/Gameplay` into:
  - `Sage.Gameplay`: generic;
  - `Sage.Kits.Rpg`: the spellmaker, readied `Selected` spell, journal and dialogue screens,
    `sage:mana` cost, and sprite clip names `attack`/`hit`/`idle`. The Kit exposes these as conventions
    in data.
- Move dialogue and quest record registration out of `FactionsModule` (`GameplayModules.cs:544-554`).
  Split `GameplayModules.cs` (634 lines) and `MapLevel.cs` (757 lines, 11 types) into one file per module.
- **Promote scene spawning into the engine.** `SceneRecord`, the spawn loop and respawn-on-reload are
  Sandbox C# today (`SandboxModule.cs:39,56-77,154`), and every game rewrites them.
- **Data-only games.** `LoadGame` must stop requiring exactly one `IGameModule`
  (`Modules.cs:206-207`). With a kit chosen in `game.json`, a terrain or level record and a
  `gameplay_conventions` record, a designer can make a playable game **with zero C#**. This is the
  single biggest statement the redesign can make to designers.
- Ship **templates**: `Empty`, `RPG` (Daggerfall-like) and `FPS` (HL1-like, once the Shooter kit exists).
  Each is data-only and runs on the stock Player host.

### 4.2 Content authoring and errors (designers first)

- **JSON Schema everywhere.** Every data file gets a `$schema`. The SDK and template write
  `.vscode/settings.json` mappings, so VS Code gives autocomplete, hover docs and red squiggles *before*
  the game runs. `sage schema` also emits the id enums from loaded content, including mods.
- **Strict loading by default.**
  - Unknown fields in components and part options are an error (`UnmappedMemberHandling.Disallow`).
    Today `"light": {"color": …}` is silently ignored (`PrefabParts.cs:22-35,77-82`).
  - Prefab bodies are validated at **load**, not spawn (`Prefab.cs:31-37`).
  - `AssetPath` fields are checked for existence.
  - `RecordRef<T>` is type-checked. Today one id set spans all types (`RecordStore.cs:436`).
  - Errors carry **file:line:column**.
- **Missing records fail visibly.** `RecordStore.Get<T>` returning `new T()` (`RecordStore.cs:116-121`)
  stays only as a Shipping safety net. In dev, a missing reference is a load error and never a
  zero-damage sword.
- **In-game problems panel.** Every content error or warning appears as a clickable list in the dev
  overlay and editor, grouped by file, with a count badge on the HUD. It opens the file at the line.
  "Grep the log for ERROR" stops being a designer's job.
- **Nicer data:**
  - Replace the string mini-languages (`"Wait:1.5"`) with objects (`{"task":"wait","seconds":1.5}`).
  - Colours as `"#FFB0A0"`, not `4294960111`.
  - Units in schema metadata, not in field names.
  - Case-sensitive component names, so typos are caught.
- **`sage validate <game> [--mods …]`** runs headless. It loads every mount, runs all checks and exits
  non-zero. It serves as CI for games and a pre-publish check for mods.
- Fix the doc drift found in the review: `rec_print` should be `rec_get`, "twenty-four" should be 25,
  and so on (see §7).

### 4.3 Designer logic: open vocabularies, not a second language

This is a direct conflict with decision D3 ("no scripting"), and the reviews disagreed. My
recommendation is a staged approach: build the vocabulary first, and decide on a language later against
measured need.

**Stage 1: open the closed sets into registries.** This follows the existing `AITaskRegistry` and
`EntityInputs` pattern, declared through §3.4.

| Closed today | Becomes |
|---|---|
| AI conditions enum, 64-bit mask (`AI.cs:17-44`) | `[AICondition]` registry (perception defined by the game) |
| `ChooseSchedule` private static (`AIThinkSystem.cs:300-330`) | selector named by `ai_profile` (see stage 3) |
| `ObjectiveKind { Kill, Have }` (`Quests.cs:19-23`) | `[QuestObjective]` registry (reach, talk, use, custom) |
| Dialogue `Requires` and `Then` fixed fields (`Dialogue.cs:21-71`) | `ICondition` / `IAction` |
| `AbilityTargeting` enum, `internal` payload (`Abilities.cs:19-26`, `AbilityPayload.cs:16`) | `[AbilityDelivery]` registry |
| Effects only modify numbers | `[EffectExecution]` registry (knockback, teleport, summon, dispel) |
| Items have no "use" (`Items.cs:21-35`) | `[ItemUse]` registry (consume, read, cast) |
| 64-tag and 64-action caps (`Attributes.cs:48`, `PlayerCommand.cs:26`) | bitset grows, or tags become interned ids |

**Stage 2: one shared condition and action language in data.** Anything that decides or does
something uses the same typed vocabulary, including dialogue, quests, triggers, items, AI and I/O:

```jsonc
"requires": { "all": [ { "has_item": "sandbox:key_iron" },
                       { "not": { "quest_stage": { "quest": "thin_the_wood", "atLeast": 2 } } } ] },
"then":     [ { "give_item": "sandbox:gold", "count": 20 },
              { "fire": "hut_door", "input": "Open" },          // dialogue can now open a door
              { "set_stage": { "quest": "thin_the_wood", "stage": 3 } } ]
```

- It's schema-validated and hot-reloadable. Mods can extend it with data, and programmers extend it by
  adding `[Condition]`/`[Action]` types.
- It's safe for untrusted mods, because it can only call registered verbs.
- It's saveable, because the save stores *which* verbs ran, not script state (the Papyrus lesson that
  17 §2 cites).

**Stage 3: entity I/O as the level-logic backbone.**
- Add logic entities: `logic_relay`, `logic_counter`, `logic_timer`, `logic_compare`, `logic_branch` and
  `math_*`.
- Add bridge inputs and outputs:
  - quests: `SetStage` and `OnStageChanged`;
  - dialogue: `Start`;
  - `GiveItem`, `ApplyEffect`, `SetFaction`;
  - `OnDeath`, `OnDamaged` and `OnPickedUp`, raised from existing events.
- Today there are only 6 inputs (`EntityIO.cs:324-336`, `Movers.cs:108-110`).
- **Per-entity map keys.** TrenchBroom properties override prefab-part fields using the generated
  metadata, so each variant no longer needs its own prefab (MAKING_A_GAME.md:541).
- An I/O output and a data `action` become the same thing: `fire` is an action, and an input can take
  `then` actions.

**Stage 4: AI as data behaviour trees.**
- Leaves are the existing `IAITask`s, and today's schedules become a `sequence` node.
- The trees hot-reload, mods can patch them, and a graph editor can come later.

**Stage 5 (decision gate): embedded scripting.** Only if stages 1–4 plainly fall short for real
content, add a sandboxed language (Lua through MoonSharp, or a small expression language). It would be
scoped to *implementing* registry entries: a Lua function as an `ICondition`, `IAction`, `IEffectExecution`
or AI task. It would run deterministically in the sim phase and never hold saved state. A visual graph
editor, if one ever exists, is a front end over stages 2–4, never a new runtime.

### 4.4 Modding (build 17-modding.md, plus what it's missing)

**Package.** A mod is a folder or a `.sagemod` zip holding a manifest and `content/`, plus optional
`bin/`.

```jsonc
// mod.json
{ "id": "thieves_guild", "name": "Thieves' Guild", "version": "1.2.0",
  "game": "sandbox", "gameVersion": ">=0.3", "sage": ">=0.4 <0.5",
  "kind": "data",                         // data | code  (code = trusted assemblies, flagged in UI)
  "dependencies": { "better_ai": ">=2.0" }, "loadAfter": ["hd_textures"], "conflicts": ["old_guild"],
  "shadows": ["textures/creature.png"] }  // intentional asset overrides; others must be namespaced
```

- **Namespaced assets.** Asset paths become `ns:textures/x.png`, and a bare path means the mount's
  namespace. Overriding another namespace's file must appear in `shadows`. That turns silent last-wins
  collisions (`VirtualFileSystem.cs:163-168`) into declared ones.
- **Load order:**
  - a deterministic topological sort on `dependencies` and `loadAfter`/`loadBefore`, with the user's
    `mods.json` order breaking ties;
  - printable with `mod_order`;
  - SemVer checked, with refusal shown in the mod manager and not only in the log.
- **Conflict report.** Extend `FieldOrigins` (`RecordStore.cs:49`) into "field X of record Y was
  written by mods A and B, and B won". Include plugin-level conflicts (§3.3). Show it in a pre-play
  **mod manager screen**, available in Shipping, and as `mod_conflicts`.
- **Patch semantics fixes:**
  - R11: bare ids in a patch resolve against the *patching* file's namespace.
  - `"$remove": true` for keys. Today `null` means "apply with defaults" (`Prefab.cs:185-186`).
  - **Keyed list merge** for lists of objects with `id`/`key` (dialogue nodes, quest stages,
    placements), so a mod can change the third option of a conversation.
  - An explicit `"replace": true` for a whole-record override, which is impossible today
    (`RecordStore.cs:280-281`).
- **Code mods:**
  - one **collectible `AssemblyLoadContext`** per mod, sharing the `Sage.*` assemblies with the host and
    resolving the mod's own dependencies from its folder;
  - loaded before the ECS schema is built;
  - flagged "contains code" in the manager, never auto-downloaded, and clearly documented as
    **not sandboxed**.
  - Data mods get the stage-2 vocabulary, so most mods never need code.
- **Shaders in mods.** The host compiles `.fx` with mgfxc for any mount in dev builds, and on
  hot reload. The mod SDK ships a `sage build-mod` step that precompiles for packaging.
- **Tools for modders.** The editor host (§4.6) is **shippable next to a game**. A studio can choose to
  release it, with its build configuration separate from the dev overlay. Today Shipping contains no
  tools at all.
- **Hardening:** zip-slip checks, size limits, and no absolute paths in mounts.

### 4.5 Saves that survive content and mod changes

- **Header:** mod list with versions and content hashes (17 §3 and 09 §3.6 promise this;
  `SaveSystem.cs:102-109` doesn't write it). Load warns on mismatch, and the mod manager can offer to
  re-enable mods.
- **Versions:**
  - Per-component versions with `[Upgrade(n)]`.
  - Keep `FormatVersion` but allow upgrading older saves instead of refusing any mismatch
    (`SaveSystem.cs:203`).
  - Golden save files in tests: a save from each released version must still load.
- **Content vs state.** Go back to **opt-in** `[Saved]` for values that come from content. Today every
  public field is saved, so a designer's rebalance never reaches existing saves
  (`SaveAttributes.cs:8-15`, `SaveSystem.cs:270-288`).
- **Missing prefab:** spawn a placeholder that keeps the saved components, instead of dropping the
  entity (`SaveSystem.cs:270-271`).
- **Atomic load.** Parse every world before destroying any (`SaveSystem.cs:248`), and widen the catch
  (`FormatException` from `RecordId.Parse` escapes it today, `SaveSystem.cs:225,266`).
- **Runtime spawns persist by default.** `World.MakePersistent` is called only by tests, so nothing
  spawned at runtime survives a save.

### 4.6 The editor: a separate host around a document model

This is D7 as originally decided. The review found the in-game overlay can't reach it incrementally,
because it edits the live play world (`DevTools.cs:84-87`).

- **`Sage.EditorHost`** loads the same plugins through `SageApp` into an **edit world** that is built
  from **documents**: placements, prefabs, records and levels. The document is the source of truth, not
  the world. That fixes several problems in one go:
  - Inspector edits no longer get lost on save.
  - The inspector no longer touches entities no document owns.
  - Documents stop being bound to whichever world was created last (`DevTools.cs:82-94`).
- **Command model.** Every edit is an `IEditorCommand` with `Do`/`Undo` (sketched in 15 §4). Each
  command is also a console command, which keeps the "every menu item is a command" rule. There's an
  undo stack per document, and "dirty" is derived from the log position.
- **Play-in-editor.** Serialise the edit world into a fresh play world, run it, and throw it away on
  Stop.
- **Forms from metadata (§3.4):**
  - inspector and record editor with ranges, enums, tooltips, units, and pickers for record references
    and assets;
  - per-field provenance, meaning which file or mod set it (`Records.Describe` already computes this);
  - per-placement **overrides stored as diffs against the prefab**.
- **Write-back that preserves the file.** Edit only the touched record in place, keeping comments,
  order and formatting (a JSONC-preserving writer). Today the whole file is re-serialised: comments are
  lost, and `placements.json` has drifted to PascalCase keys and five-line vectors.
- **Scope, in order:**
  1. place a prefab from a palette, with picking and gizmos (F29);
  2. undo (F30);
  3. record forms;
  4. problems panel;
  5. conditions and actions editor;
  6. behaviour-tree view;
  7. asset browser;
  8. material preview.
- Keep TrenchBroom for brushes. The editor launches it and hot-reloads the `.map` file.
- The ImGui overlay stays in dev builds as a **debugger**: console, stats, outliner, `ent_dump`. It's
  not the authoring tool.
- **Renderer prerequisites:**
  - multiple views per snapshot (`RenderSnapshot.cs:75`);
  - render targets, for viewports and thumbnails;
  - a public `RenderContext` in place of today's `internal` `ResolveMesh`/`ResolveTexture`/`Materials`
    (`Renderer.cs:122-126`).

### 4.7 Rendering and UI extensibility

- **Render passes.** Replace the private static `Passes` array (`Renderer.cs:340`) with registered,
  ordered `IRenderPass`es, each with an Extract hook and a Draw hook and able to allocate render
  targets. Games and code mods can add post-processing, outlines and similar passes.
- **Public extract API,** so game code can produce render items without going through `MeshRenderer`.
- **UI:**
  - Keep `Screen`/`Panel` as view-models.
  - Add `ui_style` and `ui_layout` records (fonts, colours, anchors, templates) used by `PanelView` and
    the HUD. That replaces the hard-coded colours (`ScreenSystem.cs:136-144`) and the hand-placed HUD
    (`Sandbox.Client/Hud.cs`).
  - A screen can be **declared in a record** bound to a named panel provider, so designers and modders
    can add a screen, or re-lay-out one, without C#.
  - Add localisation keys now, while strings are few.
- **Asset hot reload** for meshes (`.glb`), sounds and `.fx`, in addition to textures and records.

### 4.8 SDK, packaging, CI and hygiene (developers first)

- **`Sage.Sdk`** is an MSBuild SDK: `<Project Sdk="Sage.Sdk/0.4.0">`. It carries:
  - target framework, usings, configurations and shader targets;
  - generation of `game.json` from csproj properties;
  - `dotnet run` launching the Player host with `-game`.

  Games build **out of tree**. This also fixes a bug: `Release` is an alias of `Shipping`, so `{config}`
  resolves to `bin/Shipping`, and the solution maps the games' Release to Debug
  (`GameManifest.cs:41`, `Sage.sln:238-267`). `dotnet publish` will probably not find the game DLL.
- **`dotnet new` templates:** `sage-game`, `sage-game-client`, `sage-mod-data` and `sage-mod-code`.
- **`sage` CLI:**
  - `new`, `run`, `validate`, `schema`, `package`;
  - `package` copies the Shipping build, packs mounts into zips and strips `tools/`.
- **CI (GitHub Actions), on Windows and Linux:**
  - `dotnet tool restore`, then build all three configurations;
  - tests with coverage;
  - `check_docs.py`, with the test count read from the test run;
  - public API diff;
  - template smoke test (`dotnet new sage-game && dotnet build && sage validate`);
  - a headless scripted run of `Hello`.
- **Build hygiene:**
  - `global.json`, a root `Directory.Build.props` (Nullable, warnings as errors, LangVersion,
    Deterministic) and `Directory.Packages.props`.
  - The props file doesn't set Nullable today, and 5 files lack `#nullable enable`.
- **Repo hygiene:**
  - Delete `pipelines/CustomModelPipeline`: dead XNA 4.0 code, with its committed `.vs/` folder.
  - Replace the vendored `packages/MonoGame.ImGuiNet-main` (696K, samples and FBX included) with the
    NuGet package, or `third_party/` plus a patch note.
  - Delete branch `dev_branch_test`. It has 23 throwaway commits from 2025 and no engine work.
  - Remove stale `.gitignore` lines.
- **Tests:**
  - Replace the 35 hand-rolled bootstraps with `Sage.Testing.HeadlessApp`, turn parallelisation back on
    once statics are gone (§3.2), and put test files in folders that match their subject (almost all
    are in `World/`).
  - Add golden saves and maps, a benchmark project (from `scale-2026-09-24.md`), and a first client
    smoke test through a scripted run.
- **Registry dump.** The engine emits `--dump-registry` JSON (commands, cvars, records, I/O, conditions
  and so on) from the §3.4 metadata. `check_docs.py` reads that instead of regexes over call shapes.

---

## 5. Roadmap

Each phase ends with an **exit criterion you can demonstrate**, which keeps the "dogfood in the same
commit" habit. The existing TODO ids are shown in brackets.

| Phase | Theme | Main work | Exit criterion |
|---|---|---|---|
| **0** | Clean ground (days) | Hygiene (§4.8), CI, `global.json` and props, licence decision (D9), fix the doc contradictions (§7), fix `Release`/`{config}` | CI green on Windows and Linux; `dev_branch_test` gone; no dead projects |
| **1** | Foundations | `SageApp` and one boot path; statics made per-app; plugin model with sealed stages and string ids; `Sage.Testing` | Every test and the Player host boot through the same builder; parallel tests; the host ticks every world |
| **2** | Declarations | Source generator and analyzers (§3.4): components, records, parts, systems; stable ids in saves; JSON Schema output; `sage validate`; strict loading; problems panel [09 §3.2, R11] | Deleting any `Register` call in the repo is a compile error; VS Code autocompletes `scene.json`; the inspector-loss and ignored-`color` bugs are errors |
| **3** | Layering | Split into Core, Simulation, Gameplay and Kits.Rpg; own the ECS API; namespaces; PublicApi analyzers; `Died` event; conventions record; engine-owned scenes; data-only games; `Sage.Sdk` and templates | `dotnet new sage-game` outside the repo builds and runs; an RPG template with **zero C#** plays; Hello doesn't load the RPG kit |
| **4** | Designer logic | Open registries (§4.3 stage 1); the conditions and actions vocabulary; logic entities and bridge I/O; per-entity map keys; AI behaviour trees [F17, F21–F23] | A quest where talking to an NPC opens a door, a counter gates a reward, and a creature has its own BT, built with no C# |
| **5** | Modding and saves | `mod.json`, `.sagemod`, load order, collectible ALC, namespaced assets, keyed list merge, `$remove`/`replace`, conflict report and mod manager; save header, upgraders, placeholders, atomic load [F37, F27] | Two example mods (one data, one code) that conflict on a field show the report; a save survives adding, removing and updating a mod |
| **6** | Editor | `Sage.EditorHost`, document model, command undo, PIE, gizmos and palette, schema forms, override diffs, comment-preserving write-back; renderer views and targets and public `RenderContext` [F28–F30, D7] | A designer builds the Sandbox yard (place, tune, wire, undo, play, save) without touching JSON, and the diff is clean |
| **7** | Extensibility and reach | Render pass registry, UI style and layout records, declarative screens, localisation keys, mesh/sound/`.fx` hot reload, `sage package`, shippable modding tools | A mod adds a post-process pass and a new screen; `sage package` produces a runnable build on a clean machine |
| **gate** | Scripting decision | Review stage 1–4 content: which asks needed C#? Decide on Lua or an expression language, or no | A written decision with evidence, replacing or confirming D3 |

**Parallel tracks that don't block the phases:** skeletal animation (F9–F12), which blocks three of the
four target games; the door depenetration bug (#61); and lightmaps. Put skeletal animation after phase 3,
so it lands in the right assembly.

**What deliberately waits:** multiplayer (keep the readiness rules), a visual graph editor,
DesktopVK, and AOT. Phase 2's generator makes AOT possible, but nothing needs it yet.

---

## 6. Decisions I need from you

These are the owner's calls, and the plan works with any answer. My recommendation comes first.

1. **Scripting (D3).**
   - Recommended: the typed vocabulary first, with a language decision after phase 4 on evidence.
   - Alternative: commit to Lua now for modders.
2. **Editor technology.**
   - Recommended: a MonoGame host with ImGui (proven in this repo, one stack).
   - Alternative: Avalonia, or a web front end over a headless `SageApp`. Nicer forms, but a second UI
     stack.
3. **Do modders get the editor?**
   - Recommended: yes, as an opt-in release by each game. This changes Shipping packaging.
4. **ECS ownership.**
   - Recommended: wrap Friflo behind Sage types (§3.5).
   - Alternative: accept Friflo as public API and skip the wrapper work.
5. **Licence (D9).** Still CC0, while the docs recommend MIT or Apache. This must be settled before
   anyone else builds on the engine.
6. **Target platforms for the first release.** Windows only, or Windows plus Linux. This affects CI
   and DesktopGL vs DesktopVK timing.
7. **Distribution.** A NuGet feed plus templates (recommended), or source-only ("clone the repo").

---

## 7. Doc contradictions to fix in phase 0

- README.md:46 says audio, streaming, pathfinding, factions and quests, and the editor are "not here
  yet", while the table above it says they exist.
- ARCHITECTURE.md's status line and 00-index say "design, nothing built", which is stale.
- ARCHITECTURE §2/§3 describe `Sage.Framework`, which doesn't exist (01 §3.1 admits this). This plan's
  §3.1 replaces it.
- D7 (separate editor host) contradicts what was built (15 §10b). This plan re-adopts D7.
- 13-ui §3 says "Game UI = to be decided", while D8 says decided.
- 01 §3.1 says glTF is "still to build", but SharpGLTF is in use.
- MAKING_A_GAME §3:
  - says "twenty-four" record types (25 exist) and leaves out `placements`;
  - refers to `rec_print`, but the command is `rec_get`.
- 16 §3.4 still mentions an `AIEvents` list, though the event bus exists. Docs 04 and 10 §14 list
  built things as unbuilt.
- F7 is marked done while depenetration is missing (#61).
- TODO.md has a stale header date and its bug section is titled `engine/`.
- 15-editor §10 maps to files that don't exist (`EntityContextMenuUI.cs`, `EditorManager.cs`).

---

## 8. Mapping to existing ids

| This plan | Existing ids |
|---|---|
| §3.4 generator | 09 §3.2, `RecordRef<T>`, R11 |
| §3.2/§3.3 app and plugins | R1 (statics), 01 §5.2, 01 §12 server |
| §4.1 layering | ARCH §2/§3 Framework, ARCH §5 ("belongs in the game's layer") |
| §4.3 logic | F17 (I/O), F21–F23, R16/R17 |
| §4.4 modding | F37, 17-modding, 05 §11 (paks), D3 |
| §4.5 saves | F27, F31, 09 §3.6 |
| §4.6 editor | F28–F30, D7, 15 §3–§4 |
| §4.7 render and UI | D8, 06, 13 |
| §4.8 SDK and CI | R19, readiness §2 ("nothing ever built for another machine") |
