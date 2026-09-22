# Engine Architecture Survey

How existing engines are structured, why, and what went badly — condensed from research done 2026-09-22 for Sage's design. Each section ends with the **Sage takeaway**. Claims marked **[unverified]** came from the researchers' knowledge and weren't confirmed against a primary source in that session.

Referenced by: `ARCHITECTURE.md`, `docs/design/*`.

---

## 1. Classic engines

### 1.1 id Tech 1–3 (Quake, Quake 2, Quake 3)
- **Quake 1**: one executable + the QuakeC VM (`progs.dat`) for game logic. Startup runs through the console command buffer (`quake.rc`). WinQuake/GLQuake variants were `#ifdef` builds of the same code — "code entropy" that Quake 2 restructured away.
- **Quake 2**: core exe + swappable DLLs — renderer (`ref_soft`/`ref_gl`) and game (`game.dll`) — joined only by structs of function pointers (`game_import_t`/`game_export_t`). Single-player runs client and server in one process over a **loopback** buffer, so there is one code path.
- **Quake 3**: three VMs — `game` (server), `cgame` (client prediction, feeding the renderer), `q3_ui` — written in C, compiled to bytecode, JIT'd to x86. Goal: the safety/portability of QuakeC with the speed of native DLLs; a .qvm "isn't able to do nasty things that DLLs can." One entry point (`vmMain`), engine services via `trap_*` syscalls.
- **Event queue**: "every single input (keyboard, win32 message, mouse, UDP socket) is converted into an event_t", enabling recorded, replayable sessions.
- **Filesystem**: `.pk3` = zip; layered search paths (`baseq3` then the `fs_game` mod dir; higher paks override lower). Cvars have flags (`CVAR_ARCHIVE` persists, `SERVERINFO` replicates).
- **What went badly**: Q2's native game DLLs were unsafe to download → Q3 returned to a VM. Q1's `#ifdef` variants were unmaintainable.
- Sources: https://fabiensanglard.net/quake2/index.php · https://fabiensanglard.net/quake3/ · https://fabiensanglard.net/quake3/qvm.php · https://fabiensanglard.net/quakeSource/index.php · https://github.com/id-Software/Quake-III-Arena/blob/master/code/qcommon/files.c

**Sage takeaway**: function-table-style narrow boundaries between engine and game; layered VFS with zip paks; flagged cvars; a single input/event queue makes replays cheap.

### 1.2 GoldSrc (Half-Life 1)
- Heavily modified Quake engine. Game code in `hl.dll` (server) and `client.dll` (HUD, effects, later prediction), talking to the engine through function tables.
- **Entities**: C++ `CBaseEntity` classes linked to map classnames (`LINK_ENTITY_TO_CLASS`); map keyvalues are fed to `KeyValue()`. Level logic = `targetname`/`target` firing `Use()`.
- **AI**: *tasks* (single actions via `StartTask`/`RunTask`) → *schedules* (task lists) with an interrupt mask of *conditions* (`bits_COND_NEW_ENEMY | …`); `GetSchedule()` picks the next.
- **Lessons**: fixed Quake-era limits (edicts, clipnodes) constrained mappers; client and server code were separate codebases, duplicating prediction code — Source's `shared` folder fixed it **[partly interpretation]**.
- Sources: https://twhl.info/wiki/page/GoldSrc · https://twhl.info/wiki/page/VERC:_Half-Life_AI,_Schedules_and_Tasks

**Sage takeaway**: HL1's schedule/task/condition AI is simple, robust and a good first AI model. Named-target level logic is the ancestor of entity I/O.

### 1.3 Source
- **Library tiers**: `tier0`/`vstdlib` (platform, memory, spew/logging, ConVars, KeyValues) → `tier1` (containers, interface system) → higher tiers → engine → `server.dll`/`client.dll`, with `game/shared` compiled into both.
- **Interfaces**: each DLL exports `CreateInterface(name)` returning *versioned* interfaces (`"VFileSystem009"`), so DLLs can ship independently.
- **Reflection by macros, declared several times**: `DATADESC` (keyfields for Hammer, save/restore, I/O inputs/outputs), `SendTable`/`RecvTable` (networking), prediction tables, plus the **FGD** file for Hammer — the same field declared in 3–4 places, a well-known pain point **[unverified that all are separate; high confidence]**.
- **Entity I/O**: designers wire `OnTrigger → target,Input,param,delay` in Hammer. Flexible, but string-resolved at runtime and untyped.
- **Content**: `gameinfo.txt` SearchPaths (`game+mod`, `platform`…); first match wins; VPKs outrank loose files; mods mount base content beneath their own.
- Sources: https://github.com/quiverteam/Engine/wiki/Engine-Components · https://developer.valvesoftware.com/wiki/Data_Descriptions · https://developer.valvesoftware.com/wiki/The_GameInfo.txt_File_Structure · https://valvedev.info/guides/entity-interactions-in-sources-input-output-system/

**Sage takeaway**: copy the *ideas* (tiers, shared sim code, I/O, search paths, console/spew), avoid the *multiple declarations* — one attribute source generates everything. Resolve and type-check I/O at load time.

### 1.4 Unreal Engine 1–3
- Sweeney's stated goals: "a network replication architecture for simpler automated multiplayer… a sandboxed scripting language less crash prone than C++. Garbage collection, serialization, persistence."
- **UObject**: one reflection system powers GC, serialization, editor property panels and replication. `AActor` = placeable, networked subclass. Packages (`.u`, maps) hold cross-referencing objects.
- **UnrealScript**: ~20× slower than C++ but "write scripts that are almost always idle" — script reacts to events, averaging 5–10% CPU; heavy work (movement, physics) is native. States + latent functions (`Sleep`, `FinishAnim`).
- **Networking** (1999 doc): generalized client-server; "the server's game state can always be regarded as the one true game state"; roles (Authority, AutonomousProxy, SimulatedProxy); replicate actors (by relevancy), variables (conditions), functions (RPCs); `NetPriority` for bandwidth. "There is not now and never will be sufficient bandwidth for complete game-state updates."
- **Lessons**: UnrealScript was dropped in UE4 (performance; keeping the script/native boundary in sync) **[characterization]**.
- Sources: https://x.com/timsweeneyepic/status/1454188583368237056 · https://beyondunrealwiki.github.io/pages/unrealscript-language-refe2.html · https://www.zx.net.nz/mirror/unreal.epicgames.com/Network.htm

**Sage takeaway**: one reflection source for GC/serialization/editor/replication; gameplay logic event-driven and mostly idle; heavy per-frame work native (for us: tight systems over components).

### 1.5 Bethesda Gamebryo/Creation (Morrowind → Skyrim), Daggerfall
- **Data model**: content is records in `.esm` (master) and `.esp` (plugin) files keyed by FormID. The shipped game is itself the first master — official content and mods share one format.
- **"Rule of one"**: when several plugins change the same record, the last loaded wins the *whole record* — no per-field merge. This is why LOOT, xEdit and "bashed patches" exist.
- **Papyrus** (Skyrim): threaded VM with per-frame budgets; script instances and state are **baked into saves**, so removing a mod leaves orphaned scripts erroring forever — save cleaners exist because of this.
- **Daggerfall Unity**: reads the original data files at runtime; mods are asset bundles (textures, models, C# scripts) applied through replacement hooks (`TextureReplacement`, `WorldDataReplacement`) respecting load order.
- Sources: https://loot.github.io/docs/help/Introduction-To-Load-Orders.html · https://ck.uesp.net/wiki/Data_file · https://en.uesp.net/wiki/Skyrim_Mod:Mod_File_Format · https://ck.uesp.net/w/index.php?title=Threading_Notes_(Papyrus) · https://github.com/Interkarma/daggerfall-unity · https://www.dfworkshop.net/modding-support/

**Sage takeaway**: data-record content with load order — but merge **per field**, and never serialize behaviour/script state into saves.

### 1.6 Mount & Blade: Warband
- Closed C++ engine reads a **module** (text files + resources). The Python **Module System** is only a build step that compiles `module_*.py` into those text files. The "language" is opcode tuples (`try_begin`, `try_for_range`…), not Python.
- Engine calls named `game_*` scripts and fires triggers; campaign/economy/dialog are script, combat/agents/rendering are native.
- **Lessons**: no real functions or locals (fixed registers `reg0…`), integer-only network messages, mods ship as *whole modules* so two mods can't combine without hand-merging. Community built compilers (WRECK) and engine patches (WSE) to escape limits **[WSE unverified]**.
- Sources: https://mbmodwiki.github.io/Official_Module_System_Documentation:_Part_01 · https://mbmodwiki.github.io/Module_System

**Sage takeaway**: give game/mod authors a real language (C#) and **additive** mods, not whole-module replacement.

### 1.7 Lugaru
- Wolfire on the 2010 source release: the style is "what you might expect from a self-taught high school student" — monolithic and globals-heavy; the OSS fork's goal is layered modules and no globals. Worth studying for procedural, physics-driven combat animation, not structure.
- Sources: https://www.wolfire.com/blog/2010/05/Lugaru-goes-open-source/ · https://github.com/WolfireGames/lugaru

---

## 2. Modern engines

### 2.1 Game Engine Architecture (Jason Gregory) — the reference stack
Bottom to top: hardware/OS/SDKs → **platform independence layer** → **core systems** (allocators, math, strings/hashed ids, logging, asserts, reflection/serialization, jobs) → **resource manager** → peer subsystems (low-level renderer, scene/culling, VFX, front end; collision/physics; animation; input; audio; online) → **gameplay foundations** (object model, events/messaging, scripting, world loading/streaming) → **game-specific subsystems**. Tools/asset pipeline sit beside the runtime. Dependencies point down only.
- Sources: https://www.gameenginebook.com/ · https://cphoto.fit.vutbr.cz/ludo/lec/IZHV_3_GameEngine_dark_handout.pdf

**Sage takeaway**: this is Sage's layer model; "gameplay foundations" ≈ Sage's framework layer.

### 2.2 Unreal Engine 4/5
- **Modules/plugins**: `*.Build.cs` declares dependencies; module types Runtime / Editor / DeveloperTool; runtime may never depend on editor — enforced by the build tool.
- **UObject + UHT**: `UCLASS`/`UPROPERTY` parsed by Unreal Header Tool into generated code driving GC, serialization, editor panels, Blueprint, replication, config. Costs: custom toolchain, UObjects too heavy for "many small things".
- **Gameplay Framework**, split by network authority and lifetime: GameMode (server only, rules), GameState (replicated match state), PlayerState (per player, survives pawn death), PlayerController (the player's will, RPC home), Pawn/Character (possessable body), HUD (client). AIController possesses Pawns the same way. Criticised as shooter-shaped.
- **GAS**: abilities, attributes, data-driven GameplayEffects (buffs/modifiers), GameplayTags, GameplayCues (cosmetics), prediction keys. Steep learning curve; strong fit for RPG stats/buffs.
- **Replication**: per-actor relevancy/property compare scales poorly → **Replication Graph** (spatial grid nodes shared across connections; built for Fortnite's 100 players / ~50k actors) → **Iris** (quantized replication state separate from gameplay objects, parallel, opt-in).
- **Mass Entity**: UE5 archetype ECS for crowds/traffic, *alongside* Actors (Mass entities become Actors near the player).
- **World Partition + LWC**: one level cut into streamed grid cells around streaming sources; one file per actor (no source-control lock fights); HLODs. LWC makes core math `double` (≈21 km → 88 million km); GPU uses tile-relative floats.
- **Subsystems**: engine-managed singletons scoped to Engine/Editor/GameInstance/World/LocalPlayer.
- **Threading [detail unverified]**: game thread, render thread one frame behind (components create render-thread-owned scene proxies), RHI thread; TaskGraph; RDG render graph.
- Sources: https://dev.epicgames.com/documentation/en-us/unreal-engine/unreal-engine-modules · https://dev.epicgames.com/documentation/en-us/unreal-engine/unreal-header-tool-for-unreal-engine · https://dev.epicgames.com/documentation/en-us/unreal-engine/gameplay-framework-in-unreal-engine · https://dev.epicgames.com/documentation/unreal-engine/gameplay-ability-system-for-unreal-engine · https://dev.epicgames.com/documentation/unreal-engine/replication-graph-in-unreal-engine · https://dev.epicgames.com/documentation/en-us/unreal-engine/introduction-to-iris-in-unreal-engine · https://dev.epicgames.com/documentation/en-us/unreal-engine/overview-of-mass-entity-in-unreal-engine · https://dev.epicgames.com/documentation/en-us/unreal-engine/world-partition-in-unreal-engine · https://dev.epicgames.com/documentation/unreal-engine/large-world-coordinates-in-unreal-engine-5 · https://dev.epicgames.com/documentation/unreal-engine/programming-subsystems-in-unreal-engine

**Sage takeaway**: build-enforced module kinds; one reflection source (C# attributes + source generators instead of UHT); a lean authority-shaped gameplay framework (GameRules + Controller/Pawn); GAS-like abilities; cell streaming + large-world coordinates.

### 2.3 Unity
- **GameObject/MonoBehaviour**: components carry data *and* logic; engine calls `Update` per script across the native/managed boundary. Known costs: scattered heap objects, per-object update overhead, GC spikes, hidden script-order dependencies, main-thread only.
- **Entities (DOTS)**: archetypes in 16 KB chunks (SoA); systems + Burst jobs. **Baking**: authoring GameObjects in subscenes are converted by Bakers into runtime entity data — "authoring data is for humans, runtime data is for the CPU". Pain: two parallel worlds, long preview churn.
- **Netcode for Entities**: client/server worlds in one process, ghosts (interpolated/predicted), snapshot + rollback/resim in a fixed prediction group.
- **asmdef**: split scripts into assemblies with explicit references (incremental compile, enforced deps); domain reloads after compile are a notorious cost.
- Sources: https://docs.unity3d.com/Packages/com.unity.entities@1.0/manual/baking-overview.html · https://docs.unity3d.com/Packages/com.unity.entities@1.0/manual/editor-authoring-runtime.html · https://docs.unity3d.com/Packages/com.unity.netcode@1.5/manual/ghost-snapshots.html · https://johnaustin.io/articles/2020/domain-reloads-in-unity

**Sage takeaway**: separate authoring from runtime data; csproj per *layer* as the asmdef equivalent; avoid per-object virtual `Update` in hot paths.

### 2.4 Godot 4
- **Servers + RIDs**: RenderingServer, PhysicsServer, AudioServer, NavigationServer expose flat APIs over opaque handles. Stated reason: **multithreading** — class hierarchies broke down with threads; servers give one command channel, can run on their own thread, and make change detection trivial. Nodes are thin front ends; you can bypass them.
- **Scene tree**: inheritance-heavy nodes, composition via scenes (which double as prefabs), signals for decoupling. Linietsky's argument against ECS: nodes are easier to use; performance-critical work is in servers; ECS pays off mainly at "dozens of thousands of objects".
- **GDExtension**: native libraries via a C ABI + generated bindings, no engine recompile. **Resources**: ref-counted, serializable, cached by path. Godot 4.x added **`.uid` files** to survive renames **[date unverified]**.
- Sources: https://godotengine.org/article/why-does-godot-use-servers-and-rids/ · https://godotengine.org/article/why-isnt-godot-ecs-based-game-engine/ · https://docs.godotengine.org/en/latest/engine_details/engine_api/gdextension/what_is_gdextension.html

**Sage takeaway**: heavy subsystems own their data behind handles (hybrid model); ECS for gameplay composition. Path-based resource identity with uid sidecars as the rename fix.

### 2.5 Bevy (Rust)
- **Everything is a plugin**: `App` = container of plugins; `DefaultPlugins` supplies window/input/render/assets, each swappable.
- **ECS core**: archetype tables (+ sparse sets); systems are functions whose parameters declare exactly what they access, so the scheduler parallelizes safely.
- **Schedules**: Startup → PreUpdate → Update → PostUpdate; `FixedUpdate` runs 0..N times per frame (64 Hz default) for physics/AI/rules; system sets, `.before/.after`, run conditions, states.
- **Render world**: a separate ECS world; an **Extract** step copies only render-needed data each frame → Prepare → Queue → render graph. Reason: pipelined rendering (next sim frame runs while the previous renders).
- **Events**: `EventReader` cursors per system; known pitfalls mixing `FixedUpdate` and frame schedules **[pitfall detail from community discussion]**.
- Pain: no official editor yet; breaking changes each release.
- Sources: https://docs.rs/bevy/latest/bevy/app/struct.FixedUpdate.html · https://bevy-cheatbook.github.io/programming/schedules.html · https://bevy-cheatbook.github.io/gpu/stages.html · https://github.com/bevyengine/bevy/discussions/13494

**Sage takeaway**: engine features as modules with a default set; fixed + frame schedules with declared access; explicit Extract into a render snapshot; per-reader event cursors.

### 2.6 O3DE / Lumberyard
- **Gems** = modules (code/assets) enabled per project; game code is itself a Gem.
- **Component entities**: components declare provided/required/incompatible *services* for dependency sorting; **editor components build runtime components** (another authoring/runtime split).
- **EBus**: typed request/notification buses addressed globally or per entity, across DLLs. Pain: indirection makes debugging hard **[unverified]**.
- Sources: https://www.docs.o3de.org/docs/user-guide/programming/components/overview/ · https://docs.o3de.org/docs/user-guide/programming/messaging/ebus/

**Sage takeaway**: authoring/runtime split again; avoid a global message bus as the *main* coupling mechanism.

### 2.7 C# engines: Stride and Flax
- **Stride**: Entities + EntityComponents with **EntityProcessors** per component type doing the work (ECS-like with class components); `SyncScript`/`AsyncScript` (async/await micro-threads)/`StartupScript`; `ServiceRegistry`; asset compiler separates editor assets from runtime content; WPF editor (Windows only).
- **Flax**: C++ engine, C# editor; game code in C#/C++/visual scripts; Flax.Build generates C# bindings from annotated headers. **Hot reload** serializes the scene, reloads only user assemblies, deserializes.
- Sources: https://doc.stride3d.net/4.3/en/manual/get-started/key-concepts.html · https://docs.flaxengine.com/manual/scripting/index.html · https://flaxengine.com/blog/flax-facts-16-scripts-hot-reload/

**Sage takeaway**: component-type processors (= Sage systems over queries) work fine in C#; if game-assembly hot reload is ever added, use serialize → reload → restore.

### 2.8 Studio and data-oriented engines
- **Bitsquid/Stingray**: components are "managers" owning dense arrays; entities are index+generation ids; transforms a flat hierarchy array; entity resources compiled offline into runtime blobs. http://bitsquid.blogspot.com/2014/08/building-data-oriented-entity-system.html
- **Our Machinery**: everything is a plugin DLL registering C-struct APIs in an API registry, versioned, hot-reloadable. **The Truth**: central editor data model — objects by id, typed properties, immutable snapshots, atomic commits → undo/redo, copy/paste, prefabs, collaboration "for free". https://ourmachinery.com/post/the-story-behind-the-truth-designing-a-data-model/ (archive mirror may be needed)
- **Frostbite FrameGraph** (GDC 2017): passes declare reads/writes per frame; the graph culls, aliases memory, inserts barriers. Now standard (UE RDG). https://www.gdcvault.com/play/1024612/FrameGraph-Extensible-Rendering-Architecture-in
- **Naughty Dog fibers** (GDC 2015): one worker per core, 160 fibers, jobs wait on counters; game/render/GPU stages overlapped; frame-tagged linear allocators. https://gdcvault.com/play/1022186/Parallelizing-the-Naughty-Dog-Engine
- **Destiny** (GDC 2015): update thread simulates frame N+1 while render thread submits N; buffered scene state. https://advances.realtimerendering.com/destiny/gdc_2015/
- **Overwatch** (GDC 2017): strict ECS (components no behaviour, systems no state, singleton components); only ~3 systems (movement, weapons, state script) know about prediction/rollback. https://www.gdcvault.com/play/1024001/-Overwatch-Gameplay-Architecture-and
- **idTech 7**: no main or render thread — everything is jobs **[from HN quote of Axel Gneiting]**. https://news.ycombinator.com/item?id=22700563

**Sage takeaway**: generational ids; command-log editor model (a small Truth); a fixed pass list now, frame graph only if needed; jobs over dedicated threads; keep "special" systems (netcode, later) few.

---

## 3. Cross-cutting patterns

### 3.1 Game loop
Fiedler's "Fix Your Timestep": accumulate real time, run fixed `dt` steps, clamp frame time (avoid the spiral of death), render with `alpha = accumulator/dt` interpolation between previous and current state. Physics is only stable at a fixed step; any future netcode indexes by tick. In MonoGame set `IsFixedTimeStep = false` and run your own accumulator (MonoGame's fixed mode catches up by calling Update repeatedly without interpolation).
- https://gafferongames.com/post/fix_your_timestep/ · https://gameprogrammingpatterns.com/game-loop.html

### 3.2 Entity models
- Inheritance trees break on cross-cutting features (Dungeon Siege's move to data-driven components, Bilas GDC 2002). https://www.gamedevs.org/uploads/data-driven-game-object-system.pdf
- Archetype storage (flecs, DOTS, Arch, Friflo): fast multi-component iteration, costly add/remove. Sparse sets (EnTT, DefaultEcs): cheap add/remove, slower joins. Frequently toggled state → fields, not structural tags (Mertens). https://ajmmertens.medium.com/building-an-ecs-2-archetypes-and-vectorization-fe21690805f9 · https://github.com/SanderMertens/ecs-faq · https://ajmmertens.medium.com/why-storing-state-machines-in-ecs-is-a-bad-idea-742de7a18e59
- **Hybrid is the norm** (UE Actors+Mass, Unity GameObjects+Entities, Godot nodes+servers). Keep transform hierarchy as a narrow `Parent` + propagation pass, not the main object model.

### 3.3 Large worlds
32-bit float precision ≈ 1 mm at 10 km, ≈ 8 mm at 100 km. Options: floating origin (Daggerfall Unity: 800 m terrain zones in rings around the player, world shifted back toward origin, terrain generated with jobs), `double` world positions with camera-relative rendering (UE5 LWC), sector/zone coordinates. Interiors as separate spaces avoid the problem entirely (Daggerfall, Bethesda).
- https://github.com/Interkarma/daggerfall-unity/blob/master/Assets/Scripts/Terrain/StreamingWorld.cs · https://www.dfworkshop.net/streaming-world-part-2/

### 3.4 Assets
Source (authoring) formats vs cooked runtime formats; stable identity (GUID sidecars in Unity, paths + uid in Godot); cooked cache keyed by source hash + importer version; hot reload requires handle indirection. SharpGLTF is the standard .NET glTF library (1.0.6 stable). https://github.com/vpenades/SharpGLTF

### 3.5 Reflection and serialization
Runtime reflection is simple but slow, allocating, and hostile to trimming/AOT. Roslyn source generators give compile-time serializers and metadata (MemoryPack, MessagePack-CSharp, Friflo's query generator). Version saves like protobuf: stable field tags, never reuse, explicit `vN→vN+1` upgraders; keep save data separate from runtime state.

### 3.6 Modules and services
Engines use explicit module registration with Init/Shutdown and declared dependencies (UE `IModuleInterface`), not general DI containers. Service locators are acceptable for truly global services but hide dependencies (Nystrom). https://gameprogrammingpatterns.com/service-locator.html

### 3.7 C# concerns and library status (checked 2026-09)
- **GC**: zero allocations per frame in steady state — structs, `Span<T>`, pools, no LINQ/closures/boxing in hot loops; watch with `dotnet-counters` and `GC.GetAllocatedBytesForCurrentThread()`.
- **Arch**: archetype ECS, Apache-2.0, 2.1.0; last release over a year old (GitHub/NuGet dates disagree) — mature but slow-moving. https://github.com/genaray/Arch
- **Friflo.Engine.ECS**: v3.6.0 (README), relations, hierarchy, command buffers, events, JSON serialization, NativeAOT, managed-only; used by a MonoGame Switch/Steam title. https://github.com/friflo/Friflo.Engine.ECS
- **BepuPhysics v2**: .NET 8, SIMD, CCD, ragdoll and character demos (dynamic character, no crouch/step-up out of the box). Determinism is local only; full-state snapshot/restore not implemented. https://github.com/bepu/bepuphysics2 · https://github.com/bepu/bepuphysics2/discussions/327
- **LiteNetLib** (1.3.x) + **LiteEntitySystem** (same author, snapshot/prediction layer). https://github.com/RevenantX/LiteNetLib · https://github.com/RevenantX/LiteEntitySystem
- **MonoGame**: 3.8.5 (2026-07-15) adds **DesktopVK** (Vulkan via SDL2/FAudio, intended to replace DesktopGL "over the next few years") and **WindowsDX12**, plus a code-centric C# content builder; 3.8.5.1 on 2026-08-14. DesktopGL shaders: `vs/ps_2_0` and `vs/ps_3_0` via MojoShader; no default parameter values, no preshaders on GL. `DrawInstancedPrimitives` exists on DesktopGL but needs OpenGL 3.2+ and has had bug reports. `Texture2D.FromStream` uses StbImageSharp (png/jpg/bmp/gif; not tga), optional premultiply processor. Shaders compile with `dotnet-mgfxc` (a dotnet tool; native on Windows, Wine elsewhere).
  https://monogame.net/blog/2026-07-15-3.8.5-release-2026/ · https://docs.monogame.net/articles/getting_started/content_pipeline/custom_effects.html · https://docs.monogame.net/articles/getting_started/tools/mgfxc.html · https://github.com/MonoGame/MonoGame/pull/4920 · https://github.com/MonoGame/MonoGame/pull/6008
- **FNA**: exact XNA 4.0 reimplementation; FNA3D defaults to SDL_GPU (Vulkan/D3D12/Metal) since 25.03; very stable; no compute in the API. https://github.com/FNA-XNA/FNA3D

### 3.8 Why hobby engines fail
Building general features no game asks for; editor scope creep; purity rewrites; graphics-first while loop/content are unproven; retrofitting multiplayer; too many target genres at once. Remedies: "write games, not engines" — extract engine code on the second use (Petrie); compression-oriented programming — write usage code first, then compress into APIs (Muratori); purpose-built small engines (Blow).
- https://geometrian.com/programming/tutorials/write-games-not-engines/ **[canonical host unverified]** · https://caseymuratori.com/blog_0015 **[unverified]**

---

## 4. Patterns every engine converges on → Sage decisions

| # | Pattern | Seen in | Sage decision |
|---|---|---|---|
| 1 | Build-enforced boundaries | UE Build.cs module types, Unity asmdef, O3DE Gems, Q2 function tables | csproj per **layer**; logical `IModule`s inside (design/01) |
| 2 | Explicit sim → render hand-off | Godot servers, Bevy Extract, UE scene proxies, Destiny | Extract phase → pooled `RenderSnapshot` (design/06) |
| 3 | Authoring vs runtime data | Unity baking, O3DE editor components, Bitsquid compile, The Truth | Editor documents → bake; command log for undo (design/15) |
| 4 | Jobs, not subsystem threads | Naughty Dog, idTech 7, Destiny | Job layer over the .NET thread pool; `GraphicsDevice` main thread only (design/02) |
| 5 | Hybrid entity models | UE Actors+Mass, Unity GO+Entities, Godot nodes+servers | ECS for gameplay; subsystems own data behind handles (design/03, 06, 10) |
| 6 | One reflection source | UObject/UHT, UnrealScript; anti-pattern: Source's DATADESC+SendTable+FGD | Attributes + source generator (design/09) |
| 7 | Layered VFS + load order | Q3 pk3, Source gameinfo, Bethesda plugins | Mount order engine → framework → game → mods; per-field record merge (design/05, 17) |
| 8 | Extract the engine from games | Petrie, Muratori, Blow | Engine/framework code only on second use; Daggerfall-like drives v1 |

---

## 5. Networking reference (for the later multiplayer phase)

Not in scope now; recorded so the readiness rules make sense and the phase has a starting point.

- **Server-authoritative snapshots with client prediction** (Quake/HL/Source/Unreal/Overwatch): clients send tick-stamped input commands; the server simulates and sends snapshots; clients predict only the locally controlled entity and reconcile by re-simulating unacknowledged inputs; remote entities are interpolated (Source default 100 ms). Carmack (1996): "Simulating 300 ms on the client side in a Quake style game is just out of the question" — predict the local player only. https://fabiensanglard.net/quakeSource/johnc-log.aug.htm · https://www.gabrielgambetta.com/client-server-game-architecture.html
- **Quake 3 delta compression**: per-client history of 32 snapshots; each new snapshot is delta-compressed against the last one *that client acknowledged*; losses self-heal without per-field reliability. https://fabiensanglard.net/quake3/network.php
- **Lag compensation** (Bernier, GDC 2001): the server rewinds other players' hitboxes to what the shooter saw (latency + interpolation delay) before hit tests. https://www.gamedevs.org/uploads/latency-compensation-in-client-server-protocols.pdf · https://developer.valvesoftware.com/wiki/Source_Multiplayer_Networking
- **Relevancy and priority** (Unreal `NetPriority`, Replication Graph spatial grids, Iris quantized state separate from gameplay objects) for large worlds and large battles.
- **Small netcode surface** (Overwatch): only a few systems need to know about prediction/rollback if components are data and systems are pure.
- **Lockstep** (Age of Empires, "1500 Archers" by Terrano & Bettner): bandwidth independent of unit count but requires bit-exact determinism. https://www.gamedeveloper.com/programming/1500-archers-on-a-28-8-network-programming-in-age-of-empires-and-beyond
- **Rollback** (GGPO): lockstep + predicted remote inputs; suits 2–8 players with small state. https://www.ggpo.net/
- **For Sage**: Bepu is only locally deterministic and can't snapshot/restore full state, and .NET floating point isn't guaranteed identical across machines → **snapshot replication, not lockstep or rollback**. Predict only the local player's kinematic character controller.
