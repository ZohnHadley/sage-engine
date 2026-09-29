# Plan for #24 — the assembly split and `Sage.*` namespaces (2026-09-29)

A read-only survey of `main` at 12cfdaa, before #21 landed; #21 since added `Content/RecordSchemas.cs` (`SchemaShapeAttribute` belongs in Core), the `schema` verb and the `schemas/` CI check. Line numbers are as of that commit. The implementation follows it on branch `claude/p3-assembly-split`; where they differ, the branch and REDESIGN §3.1 win.

## 0. Decisions
- Reference direction: `Core ← Simulation ← Physics3D ← Gameplay`; `Client → Gameplay`; `Editor → Client`; `Host → Client (+Editor)`; `Cli`, `Testing` → Gameplay.
- `Engine`, `World`, `IModule`, `ModuleManager`, `SageApp`, `HostLoop`, `GameRules` live in **Sage.Simulation** (World↔Engine and IModule.OnWorldCreated(World) make them inseparable). Core keeps the world-free kernel: cvars/console, log, diagnostics, VFS, records, declaration attributes, plugin attributes/versions, RegistrationLedger, RegistrationSeal, GameManifest, Time, UserPaths, BuildInfo. Note as a deliberate deviation in REDESIGN §3.1.
- Character controller (CharacterController.cs, CharacterModule, CharacterPart, BodyPart, PhysicsDebugSystem) → **Sage.Physics3D**. Gameplay references Physics3D until #30.
- `PawnIntent`, `Pawn`, `PlayerControlled`, `PlayerControlSystem` → **Sage.Simulation/Input** (streaming, maps, entity I/O, scale commands, controller all read them).
- Minimal `IPhysicsWorld` seed in Simulation now (#30's seed): BodyCount, StaticCount, LastStepMilliseconds, TriggerEnter, TriggerExit, AddHull(entity, points, position, layer=0, isTrigger=false) → PhysicsBody, Rebase(offset). `PhysicsSpace : IPhysicsWorld`; PhysicsModule.OnWorldCreated also `Resources.Add<IPhysicsWorld>(space)`. `PhysicsData.cs` (no Bepu types) → Simulation.
- **No Sage.Kits.Rpg projects in #24.** Client hard-instantiates DialogueScreen (src/Sage.Client/UI/DialogueSystem.cs:16). Park kit screens in `Sage.Gameplay/Rpg/` for #27. Test the "no base references a kit" analyzer with an in-memory fake `Sage.Kits.*` assembly.
- Plugin ids unchanged (game.json `plugins`/`disable` and `sage.gameplay.*` wildcards depend on them).
- One PR, four commits, each green: (1) decouple inside Sage.Engine; (2) physical split, namespace still sage_engine; (3) rename namespaces; (4) analyzer, guard tests, docs.

## 1. Cycle-causing references and resolutions
### 1a. Core-bound code referencing Simulation+
- Core/Engine.cs:15-128 (World, SaveSystem, PrefabRegistry/Checks/Record, ComponentSchema, SystemCatalog, EntityInputs/Outputs, ActionRegistry, GameRules) → move Engine to S; GameRules.cs → S; EntityIO.cs → S.
- Core/Modules.cs:26,37,133,152 → S.
- Core/SageApp.cs:88-98 (SimulationModules builds PhysicsModule/StreamingModule/MapModule/GameplayModules.All), :223 WorldCommands, :250,268 World → S, invert plugin list (§1d).
- Core/GeneratedRegistrations.cs:31-38 (RegistrationBuilder holds Engine/Saves/Prefabs/IPrefabPart) → S; generators updated (§3).
- Core/Plugins.cs:37-50 PluginInfo.Of(IModule), ModuleKind → split: PluginInfo → S (App/PluginInfo.cs); PluginAttribute, RequiresPluginAttribute, SemVersion, VersionRange, RegistrationLedger stay in C.
- Core/Plugins.cs:174 RegistrationLedger.Owner { get; internal set; } (set from Engine.cs:47,49, Modules.cs:171,173) → public setter.
- Core/Metadata.cs:245-249 reads ComponentAttribute/TagAttribute/SavedResourceAttribute/PrefabPartAttribute; :331 TransientAttribute → move declaration attributes to Core (`Sage.Core/Declarations/`): all of ECS/ComponentDeclarations.cs (ComponentAttribute, TagAttribute, UpgradeAttribute, ComponentDeclaration, IGeneratedComponents, GeneratedComponentsAttribute — no Friflo), PrefabPartAttribute (out of Content/Prefab.cs), SavedResourceAttribute/TransientAttribute (out of Content/SaveAttributes.cs). SystemAttribute stays in S (needs Phase).
- Core/Metadata.cs:378 typeof(Friflo.Engine.ECS.Entity) → compare `type.FullName == "Friflo.Engine.ECS.Entity"` (as MetadataGenerator.cs:391 does); Core has no Friflo reference.
- Content/RecordStore.cs:36-39 RecordWorldExtensions.Records(this World) → Sage.Simulation/Content/RecordWorldExtensions.cs.
- Content/RecordStore.cs:98 uses EntityJsonConverter (Content/RecordId.cs:79, Friflo Entity) → move converter to S; Engine ctor inserts it into Records.Json.Converters at index 3 before `new ComponentSchema(Records.Json)`. Risk: a test building `new RecordStore()` alone deserialising an Entity field; fallback: Core keeps a private Friflo PackageReference with a `// #25` TODO.
- Core/MessageLog.cs:37-39,83-89 → S. Core/HostLoop.cs:31 → S. Core/SageMath.cs:46 (TransformMath.Forward) → S. Core/RegistryDump.cs:78,87,108-110,176 → S. Core/Diagnostics/ScaleCommands.cs:57-93 → S (:93 via IPhysicsWorld).
- False positives: LogCat.World (field), CrashReporter.SystemInfo (method), Standing (= Collider.Standing).
### 1b. Simulation-bound code referencing Physics3D/Gameplay
- Content/SaveJson.cs:45-46 builds AttributeSetSaveConverter/GameplayTagsSaveConverter (:98-185) → add `SaveSystem.AddConverter(Func<World, RecordStore, JsonConverter>)`; SaveJson.For appends registered factories after EntitySaveConverter (where they sit today); move both converters to Sage.Gameplay/Attributes/AttributeSaveConverters.cs; AttributesModule.Init registers them; add SaveSystem.AddConverter to the SAGE0020 list (RegistrationStageAnalyzer.cs:39-53). Save format unchanged (golden saves prove it).
- World/MapLevel.cs:478 PhysicsSpace.AddHull, :692 PhysicsBody, :708,722 MapCollisionSystem → IPhysicsWorld.AddHull returning PhysicsBody (now in S).
- World/MapLevel.cs:472 `if (entity.HasComponent<Mover>()) ...Closed = at` → add `MapLevels.SolidSpawned` (Action<World, Entity, Vector3>) raised at that line; MoverModule.OnWorldCreated subscribes when TryGet<MapLevels> succeeds (MoverModule comes after MapModule).
- World/MapLevel.cs:607 GameplayModules.ForEachPlayer (internal, GameplayModules.cs:64) → S as `public static void ForEachPlayer(this Engine, Action<World, Entity>)` in new Sage.Simulation/Input/Players.cs; update Gameplay/Spellmaker.cs:257,273,299 and GameplayModules.cs commands.
- MapLevel.cs:621-623 PawnIntent; World/Streaming.cs:104,174 and Gameplay/EntityIO.cs:431 PlayerControlled → solved by moving PawnIntent.cs to S.
- World/Origin.cs:107 TryGet<PhysicsSpace> then Rebase → TryGet<IPhysicsWorld>, same point.
- Gameplay/EntityIO.cs:335-354 TriggerOutputSystem reads PhysicsSpace.TriggerEnter/Exit → Get<IPhysicsWorld>().
- Rendering/FloatingText.cs:114-130 DamageNumbers.ColourFor uses DamageTypeRecord → move DamageNumbers to Sage.Gameplay/Combat/DamageNumbers.cs (client ParticleSystems.cs:115 references Gameplay anyway).
### 1c. Physics3D-bound code referencing Gameplay
- CharacterController.cs:103-106,121,128,149,380,426,432 Pawn/PawnIntent/PlayerControlled → solved by PawnIntent in S.
- PhysicsSystems.cs:231 PhysicsDebugSystem (in Gameplay/GameplayDebug.cs:15) → split GameplayDebug.cs: PhysicsDebugSystem → P; AIDebugSystem → G.
- CharacterModule (GameplayModules.cs:110-150) → P (Character/CharacterModule.cs). CharacterPart/BodyPart (PrefabParts.cs:25,53) → P.
- MovementProfileRecord.Fallback (CharacterController.cs:37, internal), used from G (Combat.cs:286, Items.cs:419, AIThinkSystem.cs:384, AbilitySystem.cs:133, GameplayDebug.cs:91) → public.
- FirstPersonCameraSystem stays in P (queries CharacterController/MovementProfileRecord) until the camera-rig work.
### 1d. The one list of simulation plugins
- New `public static class BasePlugins { public static IModule[] All() }` in Sage.Gameplay: same order as today (PhysicsModule, StreamingModule, MapModule, then GameplayModules.All()). GameplayModules.All() keeps returning the same 11 modules (ModuleSetTests).
- Replace `SageAppOptions.IncludeSimulationModules` (SageApp.cs:29) with `IReadOnlyList<IModule> AvailablePlugins { get; init; } = []`; ChooseSimulationModules(game.Plugins) selects from it.
- Callers: Host/Program.cs:57 and HeadlessApp.cs:147 (Simulation()) pass BasePlugins.All(); ContentValidation.cs:77 gets ValidateOptions.AvailablePlugins (Cli/Program.cs:59, StrictContentTests.cs:251,275 pass BasePlugins.All()); tests relying on the true default gain `AvailablePlugins = BasePlugins.All()` (SageAppTests.cs:116,139,234,264; ModuleSetTests.cs:107; PluginTests.cs:105,156,165,180,204,212,219); tests passing IncludeSimulationModules=false drop it; SageApp.SimulationModules() users (SageAppTests.cs:130,147, PluginTests.cs:213, DeclarationTests.cs:70) → BasePlugins.All().
### 1e. Areas
Rendering data (RenderData, Particles, Weather, Lights, SpriteData, DebugDraw, FloatingTexts) → S Rendering/ (minus DamageNumbers). Audio → S Audio/. Input (PlayerCommand, ActionRegistry, InputMapRecord, InputGating) + PawnIntent.cs + Players.cs → S Input/. UI view-models (Screen, Panel, PanelLayout, TextField) → S UI/. Kit screens (SpellmakerScreen, JournalScreen, DialogueScreen, GameplayPanels, Spellmaker) → G Rpg/. Animation (SpriteAnimation.cs, AnimationModule, SpritePart) and lights (LightsModule, LightPart) → G Animation/, Lights/. Content: SaveSystem, SaveJson, SaveSerializer, Prefab, Placements, ComponentSchema, EditorDocument, ContentValidation → S Content/; records and VFS → C Content/. World: Terrain, Streaming, Origin → S World/; MapLevel (split), MapFile, BrushGeometry, FgdExport, PrefabKeys → S Levels/. Core diagnostics Assert, Profiler, CrashReporter → C; ScaleCommands → S.
### 1f. Declaration placement rule
Generated registrations are included per assembly: Engine includes typeof(Engine).Assembly (becomes S) and ModuleManager includes each loaded module's assembly. So a declaration outside S must be owned by a plugin whose module is in the same assembly, or it silently never registers. S is always included (sage.core, sage.client records stay there, plus physics_layers once PhysicsData moves). All current declarations satisfy this. ContentValidation.cs:97 keeps working. Add a guard test: for each base assembly other than S, every owner in its GeneratedRegistrations.Owners has an IModule with that [Plugin] id in the same assembly. System ordering by id across assemblies is safe (SystemScheduler includes each added system's assembly, :86,109).
### 1g. Internals crossing boundaries
RegistrationLedger.Owner setter, MovementProfileRecord.Fallback, ForEachPlayer (moved). Editor→Client once Client's IVT to Editor is dropped: InputDevices.Keyboard/Mouse (InputDevices.cs:13-14), KeyboardListener, MouseListener, MouseButton (DevCamera.cs:43,75,90). Compile after split is the proof for the rest.

## 2. Move map (paths relative to src/; namespace = assembly name; use git mv)
### Sage.Engine/Core/
- BuildInfo, GameManifest, RegistrationSeal, Time, UserPaths → Sage.Core/
- Console/{CVar,CVarRegistry,CommandLine,CoreCVars} → Sage.Core/Console/
- Diagnostics/{Assert,CrashReporter,Profiler} → Sage.Core/Diagnostics/; Diagnostics/ScaleCommands → Sage.Simulation/Diagnostics/ (IPhysicsWorld)
- Logging/{Log,LogCat,LogHandlers,LogRateLimiter,LogSinks} → Sage.Core/Logging/
- Metadata.cs → Sage.Core/Declarations/Metadata.cs (:378 by name)
- Plugins.cs → split: Sage.Core/Declarations/Plugins.cs (attributes, SemVersion, VersionRange, RegistrationLedger with public Owner setter) + Sage.Simulation/App/PluginInfo.cs
- Engine, Modules, SageApp, HostLoop, GeneratedRegistrations, RegistryDump → Sage.Simulation/App/
- MessageLog, SageMath → Sage.Simulation/ECS/
### Sage.Engine/Content/
- AssetPath, ColourJson, ContentChecks, JsonSource, RecordCheck, RecordHotReload, RecordRef, SaveUpgrades, VirtualFileSystem → Sage.Core/Content/
- RecordId.cs → Sage.Core/Content/ minus EntityJsonConverter → Sage.Simulation/Content/EntityJsonConverter.cs
- RecordStore.cs → Sage.Core/Content/ minus RecordWorldExtensions → Sage.Simulation/Content/RecordWorldExtensions.cs; drop converter at :98
- Prefab.cs → Sage.Simulation/Content/ minus PrefabPartAttribute → Sage.Core/Declarations/PrefabPartAttribute.cs
- SaveAttributes.cs → split: TransientAttribute, SavedResourceAttribute → Sage.Core/Declarations/SaveAttributes.cs; FromPrefab, ISavedResource → Sage.Simulation/Content/SaveAttributes.cs
- SaveJson.cs → Sage.Simulation/Content/ minus the two gameplay converters
- ComponentSchema, ContentValidation (+AvailablePlugins), EditorDocument, Placements, SaveSerializer, SaveSystem (+AddConverter) → Sage.Simulation/Content/
### Sage.Engine/ECS/
- ComponentDeclarations.cs → Sage.Core/Declarations/
- everything else keeps its sub-path under Sage.Simulation/ECS/ (ActiveCamera, Components/{GlobalTransform,Transform}, Deferred, EcsSchema, Events/GameEvents, PersistentId, Systems/{ISystem,PhaseContracts,SystemDeclarations,SystemScheduler,TransformPropagation,WorldSystems}, World, WorldCommands, WorldResources)
### Presentation/input
Input/* → Sage.Simulation/Input/; Rendering/* → Sage.Simulation/Rendering/ (FloatingText minus DamageNumbers); Audio/* → Sage.Simulation/Audio/; UI/* → Sage.Simulation/UI/ (Panel.cs:114,118,121,130 `sage_engine.Log.Info` → `Sage.Core.Log.Info`); Animation/SpriteAnimation.cs → Sage.Gameplay/Animation/.
### Physics/
PhysicsData.cs → Sage.Simulation/Physics/ (+ new IPhysicsWorld.cs); PhysicsSpace.cs, PhysicsSystems.cs, PhysicsCallbackData.cs → Sage.Physics3D/. In PhysicsSpace.cs:44,51 add `using BepuSimulation = BepuPhysics.Simulation;` (namespace trap, §4).
### World/
Origin, Streaming, Terrain → Sage.Simulation/World/; BrushGeometry, FgdExport, MapFile, PrefabKeys → Sage.Simulation/Levels/. MapLevel.cs splits into Sage.Simulation/Levels/: MapRecord (27-55), MapLevel (56-124), SolidEntity (125-147), MapLevels (148-169, + SolidSpawned), MapLoader (170-487, IPhysicsWorld, raise SolidSpawned), MapEntityIO (489-566), MapModule (567-672, Players.ForEachPlayer), MapCollisionSystem (674-738, IPhysicsWorld), MapGeometry (740), MapSolid (748), FromMap (758).
### Gameplay/
AI.cs, AIThinkSystem.cs → Sage.Gameplay/AI/; Abilities, AbilityPayload, AbilityRules, AbilitySystem, Projectile → Abilities/; Attributes, Effects (+ AttributeSaveConverters.cs) → Attributes/; Combat (+ DamageNumbers.cs) → Combat/; Items → Items/; Factions → Factions/; Dialogue, Quests → Narrative/; Navigation → Navigation/; Movers → Movers/ (subscribe SolidSpawned); Spellmaker, SpellmakerScreen, JournalScreen, DialogueScreen, GameplayPanels → Rpg/; CharacterController.cs → Sage.Physics3D/Character/ (public Fallback); PawnIntent.cs → Sage.Simulation/Input/; GameRules.cs → Sage.Simulation/App/; EntityIO.cs → Sage.Simulation/Logic/ (IPhysicsWorld); GameplayDebug.cs split (PhysicsDebugSystem → Sage.Physics3D/Debug/, AIDebugSystem → Sage.Gameplay/AI/).
PrefabParts.cs splits: CharacterPart → Physics3D/Character/; BodyPart → Physics3D/; LightPart → Gameplay/Lights/; SpritePart → Gameplay/Animation/; AttributesPart, EffectsPart → Attributes/; MeleePart → Combat/; InventoryPart, PickupPart (incl. nested Stack) → Items/; AbilitiesPart → Abilities/; FactionPart → Factions/; DialoguePart → Narrative/.
GameplayModules.cs splits: GameplayModules (All, AddGameplay; ForEachPlayer removed) 23-73 → Gameplay/GameplayModules.cs (+ new BasePlugins.cs); AttributesModule 75-107 → Attributes/ (registers save converters); CharacterModule 110-150 → Sage.Physics3D/Character/; AnimationModule 152-171 → Animation/; LightsModule 173-179 → Lights/; CombatModule 181-221 → Combat/; ItemsModule 223-302 → Items/; AbilitiesModule 304-375 → Abilities/; AIModule 377-494 → AI/; NavDebugSystem 496-562 → Navigation/; FactionsModule 564-651 → Factions/.
### Other projects
Sage.Client (26 files), Sage.Editor (8), Sage.Host: no moves; namespaces Sage.Client/Sage.Editor/Sage.Host. Host/Cli Program.cs lose `using sage_engine;`; Host/Program.cs:57 gains AvailablePlugins; src/Sage.Host/app.manifest:3 name="sage_engine" → Sage.Host. tests/Sage.Testing (5 files) namespace Sage.Testing, HeadlessApp uses BasePlugins. tests/Sage.Tests (64 files) namespace Sage.Tests; delete per-file `using sage_engine;`.
### New csproj
Sage.Core (no packages; SageSimulationOnly=true; IVT Sage.Tests); Sage.Simulation (→ Core; Friflo); Sage.Physics3D (→ Simulation; BepuPhysics); Sage.Gameplay (→ Physics3D); Sage.Client → Gameplay; Sage.Editor → Client; Sage.Host → Client (+Editor as today); Sage.Cli → Gameplay; Sage.Testing → Gameplay; Sage.Tests → Testing, Generators, Sandbox, Hello (+ explicit base refs). Delete src/Sage.Engine; update Sage.sln; remove `<RootNamespace>sage_engine</RootNamespace>` (src/Directory.Build.props:9). IVT only Sage.Tests on Core, Simulation, Physics3D, Gameplay; drop Client's IVT to Editor/Host/Tests and Editor's to Host.

## 3. Generators and analyzers
Hard-coded `private const string Ns = "sage_engine"` in: ComponentGenerator.cs:34 (uses :35-37,73,107,111,113,114), DeclarationAnalyzer.cs:24 (:46-50), MetadataGenerator.cs:39 (:61-65,85,116-157,282-285,358,389-390,440,442), PartGenerator.cs:30 (:31-33,145,149,155,196,230,254), RegistrationGenerator.cs:26 (:27-29,110,114,120), RegistrationStageAnalyzer.cs:28 (:39-53,65-68), StrictSavesAnalyzer.cs:24 (:42-45; comment :19). Replace with one public `src/Sage.Generators/SageTypes.cs` of fully qualified names:
- Sage.Core.: ComponentAttribute, TagAttribute, UpgradeAttribute, IGeneratedComponents, GeneratedComponentsAttribute, ComponentDeclaration, RecordAttribute, SavedResourceAttribute, TransientAttribute, PrefabPartAttribute, PluginAttribute, PropertyAttribute, RecordRefAttribute, AssetKindAttribute, IGeneratedMetadata, GeneratedMetadataAttribute, TypeMetadata, FieldMetadata, DeclarationKind, ValueKind, RecordId, AssetPath, RecordRef<T>, CVarRegistry, RecordStore.
- Sage.Simulation.: SystemAttribute, IGeneratedSystems, GeneratedRegistrationsAttribute, IGeneratedRegistrations, RegistrationBuilder, IPrefabPart, ISystem, IModule, IGameModule, ModuleManager, ActionRegistry, EntityInputs, PrefabRegistry, SaveSystem (+AddConverter).
- Unchanged until #25: "Friflo.Engine.ECS.Entity", IComponent, ITag.
- PartGenerator.cs:196 `mustImplement.Substring(Ns.Length + 1)` → text after last '.'.
- Test: every SageTypes constant resolves via Type.GetType against base assemblies.
(NOTE: #21 may add schema-generation code that also names types — include it.)
Tests hard-coding names: ComponentIdTests.cs:163 (`global::Sage.Core.GeneratedComponentsAttribute`), MetadataTests.cs:255,256,262, DiagnosticsTests.cs:16,36 (`Sage.Core.Assert`), in-source `using sage_engine;` at AnalyzerTests.cs:24, DeclarationTests.cs:81,99,113,134,148,173,192,212,226,245, MetadataTests.cs:238,276, ComponentIdTests.cs:154,184,199 → `using Sage.Core; using Sage.Simulation;`; reference lists at ComponentIdTests.cs:217, DeclarationTests.cs:263, MetadataTests.cs:292, AnalyzerTests.cs:248 → add Core and Simulation .Location. Tests that silently weaken unless they iterate every base assembly: ComponentIdTests.cs:37,46, DeclarationTests.cs:69, MetadataTests.cs:31,109,133, ModuleSetTests.cs:46 → add `EngineAssemblies.Base` helper in Sage.Testing. RegistryDumpTests.cs:38 expects "Sage.Engine" → "Sage.Simulation".
Analyzers (commit 4): SAGE0024 default `SageSimulationOnly=true` in src/Directory.Build.props, Client/Editor/Host set false; emit `[assembly: AssemblyMetadata("SageSimulationOnly","true")]`. New SAGE0025 "a base assembly references a kit" (Sage.Architecture, Error), gated by compiler-visible `SageBaseAssembly=true` on Core, Simulation, Physics3D, Gameplay, Client, Editor: report referenced assemblies named Sage.Kits.* and symbol uses whose ContainingAssembly starts with Sage.Kits.; test with an in-memory emitted Sage.Kits.Rpg (MetadataReference.CreateFromImage); add to AnalyzerReleases.Unshipped.md and MAKING_A_GAME §10a. Runtime guard test: no base assembly references MonoGame.Framework, Sage.Client or Sage.Kits.*; Core references no Friflo/Bepu.

## 4. Name/reflection coupling
- Golden saves: safe (component ids; format1 C# short names via ComponentSchema.Former Type.Name, :268).
- Record JSON, plugin/system/component ids: strings, unaffected.
- Registry dump clrType/assembly values change; check_docs reads names only; RegistryDumpTests.cs:38 edit.
- games/Directory.Build.props:15-17 usings → Sage.Core, Sage.Simulation, Sage.Physics3D, Sage.Gameplay, Friflo.Engine.ECS (removal is #25), alias Transform → Sage.Simulation.Transform. Move base ProjectReferences (all four, Private="false") into the props so game csprojs stop naming Sage.Engine. Sandbox.Client keeps Sage.Client ref + `<Using Include="Sage.Client"/>`. Check games/*/bin/*/net8.0 still holds only the game DLL (unsure whether transitive refs honour Private=false).
- Friflo Transform ambiguity: after rename, Physics3D, Gameplay, Client, Editor, Host, Cli, Testing, Tests need `<Using Include="Sage.Simulation.Transform" Alias="Transform"/>` or CS0104.
- `Simulation` namespace trap: inside namespace Sage.Physics3D, `Simulation` binds to namespace Sage.Simulation before `using BepuPhysics`; PhysicsSpace.cs:44 (`internal Simulation Simulation`) and :51 (`Simulation.Create`) break → alias.
- No other name collisions (MonoGame, Bepu, ImGui, Sandbox, Hello) except Friflo Transform.
- Friflo schema scanning (EcsSchema.EnsureInitialized): one process-wide schema from loaded assemblies via GetAssemblies + GetReferencedAssemblies (believed recursive, unconfirmed). Verify the `ECS schema: N component types, M tags` debug line matches before/after, and add a test asserting the schema holds a component from each base assembly (sage:pawn_intent in S, a character-type in P, sage:attributes in G).
- LoadGame/LoadModules: paths, unaffected.
- Docs: ~55 non-history `src/Sage.Engine/...cs` mentions in docs/design/01-16, ARCHITECTURE.md:47,67,78,223, README.md:221,227, MAKING_A_GAME.md:37,49,59,73,79-81,803, CLAUDE.md:47, THIRD_PARTY_NOTICES.md:11-12 (Bepu → Physics3D, Friflo → Simulation), docs/design/02:145. Leave docs/history/ alone. REDESIGN §3.1 note on deviations. check_docs --fix counts.

## 5. Sequencing (one PR, four commits, each green)
0. Baseline before starting: Development build, -dump-registry, sage validate output for Sandbox/Hello/no-plugins, the ECS schema debug line.
1. Decouple inside Sage.Engine (~25 files): IPhysicsWorld seed; SaveSystem.AddConverter; MapLevels.SolidSpawned; Players.ForEachPlayer; BasePlugins + AvailablePlugins; attribute relocation and file splits (GameplayModules, MapLevel, PrefabParts, GameplayDebug, Plugins, SaveAttributes, SaveJson, FloatingText, RecordStore, RecordId); internal→public. Registry dump must match baseline exactly minus clrType/assembly (`jq 'walk(if type=="object" then del(.clrType,.assembly) else . end)'`).
2. Physical split, namespace still sage_engine: create 4 projects, git mv, fix references, restrict IVT, sln, games props, test assembly-identity code. Any "type not found" = missed upward reference: fix the design, don't add a reference.
3. Rename namespaces: sed per project, global usings per csproj, Transform alias, BepuSimulation alias, SageTypes, test strings, Panel.cs, app.manifest, RootNamespaces. Acceptance: `grep -rn "namespace sage_engine"` empty; `git grep sage_engine -- src tests games` empty (docs history excepted).
4. Analyzer and docs: SAGE0025, SageSimulationOnly default + metadata, guard tests (base references, declaration-owner placement, schema coverage, SageTypes resolution), docs, check_docs counts.
Verify after each commit: registry-dump diff, full tests Debug+Development, sage validate ×3, smoke runs (Sandbox, no-plugins, Hello), golden saves; optionally scale_spawn 2000 tree / scale_report.
Risks: (1) silent registration loss (owner-placement test, registry diff, check_docs record count catch it); (2) Friflo schema load order; (3) save converters per plugin (SaveTests, GoldenSaveTests); (4) public API growth from dropping IVT (#31 trims); (5) lookup traps (Transform, Simulation); (6) merge conflicts (~250 files) — land #21 first, freeze src/Sage.Engine while #24 is open.

## 6. Test projects
Sage.Testing → Gameplay; uses PhysicsModule (HeadlessApp.cs:33) and GameplayModules; namespace Sage.Testing; add EngineAssemblies.Base. Sage.Tests → Testing, Generators (library), Sandbox, Hello + explicit base refs; RootNamespace Sage.Tests; global usings Sage.Testing, Sage.Core, Sage.Simulation, Sage.Physics3D, Sage.Gameplay + Transform alias; keep `using Assert = Xunit.Assert;`. Core, Simulation, Physics3D, Gameplay each `<InternalsVisibleTo Include="Sage.Tests"/>`; nothing else.
