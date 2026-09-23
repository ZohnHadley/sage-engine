# Code Review Log (archived from TODO.md, 2026-09-22)

History of the code review of `engine/` (2026-05-25 → 2026-09-21): every finding with its problem, fix and resolution notes. **Open items are tracked in `TODO.md`**; this file is kept for context (why the code looks the way it does). Item numbers (#1–#55) are stable and still referenced from `TODO.md` and `docs/design/*`. **File paths below predate the 2026-09-22 solution split** (`engine/…` is now `src/Sage.*/…`; see `ARCHITECTURE.md` §7).

---

Generated from a full read-through of the engine source on 2026-05-25.
Last verified against source: 2026-09-21 (post input-listener / push-observer entity-context / LookAt+Billboard round, `dcfbd12` + uncommitted `UtilAssets.cs` / `Content.mgcb` changes).

Legend:
- [ ] = open
- [X] = done
- [~] = partially done / regressed
- **Severity**: Critical / Wiring / Latent / Cosmetic

Path note: engine code now lives under `engine/Classes/EnginClasses/` (ECS, camera, input, listeners). `Camera.cs` was replaced by `DevCamera.cs`. Paths and line numbers below are current as of 2026-09-21.

---

## Critical bugs (wrong behavior or crashes)

### [X] 1. `ModelRendererSystem` caches its entity list once and never refreshes
- **File**: `engine/Classes/EnginClasses/ECS/systems/ModelRendererSystem.cs:12-17`
- **Problem**: The component query was run once in the singleton constructor, so entities created later were never rendered.
- **Resolution**: The system now holds a live `ArchetypeView` from `EntityContextListener.getInstance().Track("ComponentTransform", "ComponentMeshRenderer")`. `EntityContext` raises `OnEntityAdded/Removed` and `OnComponentAdded/Removed`, the listener re-evaluates every view, and `_renderables.Entities` stays current without per-frame rescans. `Game1.Initialize` brings the listener up before `LoadContent` (`Game1.cs:37`).

### [X] 2. Quaternion components used as if they were Euler-degrees
- **File**: `engine/Classes/EnginClasses/ECS/systems/ModelRendererSystem.cs:50`
- **Resolution**: `effect.World = Matrix.CreateFromQuaternion(entityRotation) * worldPositionMatrix;`. Scale is still not applied — see #32.

### [X] 3. `ComponentTransform` defaults are broken for rotation/scale
- **File**: `engine/Classes/EnginClasses/ECS/components/ComponentTransform.cs:6-8`
- **Resolution**: Backing fields are initialized: `_position = Vector3.Zero`, `_rotation = Quaternion.Identity`, `_scale = Vector3.One`. Public properties are now PascalCase: `Position`, `Rotation`, `Scale`.

### [X] 4. `EntityContext.removeEntity` does not clean up `entityGroups`
- **File**: `engine/Classes/EnginClasses/ECS_base_classes/EntityContext.cs:68-87`
- **Resolution**: `removeEntity` removes the id from every group, then fires `OnComponentRemoved` for each component (over a `ToList()` snapshot) followed by `OnEntityRemoved`.

### [X] 5. Camera movement is not frame-rate independent
- **File**: `engine/Classes/EnginClasses/CameraClasses/DevCamera.cs:70-97`
- **Resolution**: `DevCamera.update(gameTime)` builds a move vector from WASD (polled via `InputSystem.IsKeyDown`), normalizes it so diagonals aren't faster, and applies `move * speed * dt`.

### [X] 6. `EntityContextMenuUI` opens a popup that's never defined
- **File**: `engine/Classes/Screens/EntityContextMenuUI.cs:36-42`
- **Resolution**: Uses `BeginPopupContextItem("ctx_" + entity.getId())` right after `TreeNodeEx`; ImGui handles the right-click edge and open/close.
- **Follow-up done**: `Delete` calls `context.removeEntity(entity)`. Deleting mid-loop is safe because the loop iterates `context.EntitiesDict.Values.ToList()` (a copy), not the live dict.

---

## Missing connections / dead wiring

### [X] 7. `EditorManager.current_fps` is never set
- **Resolution**: Dead `current_fps` / `setCurrentFPS` removed. FPS overlay is not implemented — re-open if needed.

### [X] 8. None of the debug modes are wired up
- **Resolution**: `debugModes` removed from `EditorManager` (commented out at `EditorManager.cs:9`). Note a similar unwired leftover now exists in `InputSystem` — see #36.

### [X] 9. `UtilAssets.lightIconTexture` is declared but never loaded or used
- **Files**: `engine/Classes/UtilAssets.cs:7, 11`, `engine/Content/Content.mgcb`, `engine/Content/light.png`
- **Status (uncommitted)**: The load is commented out (`// TODO: Fix FreeImage dependency`) and the `light.png` entry was removed from `Content.mgcb`. The `lightIconTexture` field and `Content/light.png` still exist, and nothing references either.
- **Fix**: Either delete the field and `light.png` to finish dropping it, or fix the FreeImage/texture pipeline issue, restore the mgcb entry, and use it as a light-source icon in the editor.
- **Resolution (2026-09-22, quick-fix batch)**: the `lightIconTexture` field, its commented-out load, the `light.png` mgcb entry and `engine/Content/light.png` were removed; the unused duplicates in `Content/myAssets/` (a second `light.png` and a copy of `stanford_bunny.fbx`) followed with the solution split. Runtime PNG loading returns with the asset system (TODO R12, `docs/design/05`).

### [X] 10. `Primitive.cs` is an empty abstract class
- **Resolution**: File deleted.

### [X] 11. `EditorManager.getGraphicsDeviceManager()` returns a never-assigned field
- **File**: `engine/Classes/EditorManager.cs:36-45`
- **Resolution**: `getGraphicsDeviceManager` and the stored field are gone. The constructor takes no arguments; `setGraphicsDeviceManager(gdm, width, height)` only applies the back-buffer size and does not keep `gdm`. (The method name is now a bit misleading — something like `applyWindowSize` would describe it better.)

### [X] 12. `Game1.cs` is in `Content/`
- **Resolution**: Moved to `engine/Game1.cs`.

---

## Latent bugs / design issues

### [X] 13. `addComponent` doesn't enforce one-component-per-type
- **File**: `engine/Classes/EnginClasses/ECS_base_classes/Entity.cs:34-41`
- **Resolution**: `_components` is `Dictionary<Type, IComponent>`; `_components.Add` throws on duplicate type. (Group/archetype matching still uses type-name strings — see #38.)

### [X] 14. `Tag` system uses reference equality
- **Resolution**: Tag system removed entirely. Re-introduce `Type`-keyed if needed.

### [X] 15. Entity/context internals exposed for direct mutation
- **Files**: `engine/Classes/EnginClasses/ECS_base_classes/EntityContext.cs:9-19, 110-115`, `engine/Classes/EnginClasses/ECS_base_classes/Entity.cs:14, 53-55`
- **Done**: `addComponentFor` / `removeComponentFor` keep `_components` and `_entityGroups` in sync and fire the component events. `Entity.addComponent` / `removeComponent` are `internal`. `getGroups()` returns an `IReadOnlyDictionary` view.
- **Regressed / still open**:
  - `Entity.Components` has a public **setter**, and `Entity.getComponents()` returns the live internal dictionary. Anyone can add or remove components without touching groups or firing `OnComponentAdded/Removed`, which silently desyncs every `ArchetypeView`.
  - `EntityContext.EntitiesDict` is public with a **setter** and returns the live dict. Callers can add or remove entities or swap the whole dictionary without firing `OnEntityAdded/Removed`.
- **Fix**: Remove both setters. Expose `IReadOnlyDictionary<Type, IComponent>` / `IReadOnlyDictionary<long, Entity>` (or `IReadOnlyCollection<Entity>`) instead of the concrete dictionaries, and delete the redundant `getComponents()`.
- **Resolution (2026-09-22, migration step 3 — `World` over Friflo.Engine.ECS)**: the old `Entity`/`EntityContext` classes were deleted. `World` exposes no mutable collections; all changes go through `World.Create/Destroy/Add/Remove` (or its command buffer), which always raise notifications.

### [X] 16. Entity ID type inconsistency
- **Resolution**: Standardized on `long` everywhere.

### [X] 17. Camera rotation drifts and has no pitch clamp
- **File**: `engine/Classes/EnginClasses/CameraClasses/DevCamera.cs:29-33, 54-57, 99-114`
- **Resolution**: Yaw/pitch are stored as floats (radians); `rebuildForward()` rebuilds `camForward` from them; pitch is clamped to ±89° and yaw is wrapped. Mouse-look now runs in `OnMouseDrag` (subscribed to `InputSystem.OnMouseDrag`, right button only) instead of the old `CustomMovement`.

### [X] 18. `EditorUI` "Exit" calls `System.Environment.Exit(0)`
- **Resolution**: `EditorUI.Draw(Game game)` calls `game.Exit()`.

### [X] 19. Mouse delta on first frame of right-click can snap
- **Files**: `engine/Classes/EnginClasses/Listeners/MouseListener.cs:99-136`, `DevCamera.cs:38-41`
- **Resolution**: Replaced the old manual rising-edge check. `MouseListener` re-anchors each button at its press position and only promotes a hold to a drag after `DragThreshold` (5 px) of travel. `OnDrag` then delivers per-frame deltas. The camera only rotates from `OnMouseDrag`, so a re-press can't snap. `MouseListener` also seeds both mouse states in its constructor so the first frame has no bogus delta.

### [X] 20. `createEntity` adds to `entityGroups` before adding to `entities`
- **File**: `engine/Classes/EnginClasses/ECS_base_classes/EntityContext.cs:49-66`
- **Resolution**: Insert into `_entities`, fire `OnEntityAdded`, then `addComponentFor(entity, new ComponentTransform())`. Subscribers see the entity before any of its component events.

---

## Cosmetic / style

### [X] 21. Namespace mismatch
- **Regressed**: `engine/Classes/EnginClasses/ECS/systems/TransfomSystem.cs:4` declares `namespace sage_engin;` (typo) and adds `using sage_engine;` to compensate.
- **Fix**: `namespace sage_engine;`, drop the `using`.
- **Resolution (2026-09-22, quick-fix batch)**: `TransfomSystem.cs` (the only file with the `sage_engin` namespace) was deleted (see #35).

### [X] 22. Mixed visibility
- **Policy**: `internal` everywhere except `Game1`.
- **Regressed**: `public enum MouseButton` (`engine/Classes/EnginClasses/Listeners/MouseListener.cs:190`).
- **Fix**: Make it `internal`.
- **Resolution (2026-09-22, quick-fix batch)**: `MouseButton` is now `internal`.

### [X] 23. Interface naming
- **Resolution**: `IComponent`, `IComponentSystem` (now `: IEnginSystem`), `IEnginSystem` under `ECS_base_classes/Interfaces/`.

### [X] 24. Typos
- **Earlier round**: Fixed in `GameAssets.cs` (now `UtilAssets.cs`), `EditorManager.cs`, `ModelRendererSystem.cs`.
- **New**:
  - Folder `EnginClasses/` → `EngineClasses/`
  - `IEnginSystem` → `IEngineSystem` (file + type)
  - `TransfomSystem.cs` → `TransformSystem.cs` (the class inside is already `TransformSystem`)
  - `sage_engin` namespace (see #21)
  - `DevCamera.cs:24` "what the camera cans see"
  - `Game1.cs:88` "z buffer dept clear"
  - `EditorManager.windowWidth` vs `WindowHeight` — inconsistent casing (should be `WindowWidth`)
- **Resolution (2026-09-22, solution split)**: the `EnginClasses/` folder and `TransfomSystem.cs` are gone, `IEnginSystem` → `IEngineSystem`, "cans see" and "dept" comments fixed, `windowWidth` → `WindowWidth`.

### [X] 25. Dead / redundant code
- **Done**: Identity multiplication removed; manual light setup collapsed to `EnableDefaultLighting()`.
- **Still present** in `engine/Classes/EnginClasses/ECS/systems/ModelRendererSystem.cs`:
  - Lines 53-54: `lightDirection` is computed and normalized but never used.
  - Lines 34-37: `if (_renderables.Entities.Count == 0) return;` is redundant — the `foreach` already does nothing on an empty set.
  - Lines 43-45: `getComponent<ComponentTransform>()` and the World matrix are recomputed per effect inside the mesh loop. Hoist them to per-entity.
  - `update(GameTime)` is an empty body.
- Other new dead code is tracked in #36.
- **Resolution (2026-09-22, migration step 6)**: `ModelRendererSystem` and `BasicEffect` were replaced by Extract (`MeshExtract`) + `RenderSnapshot` + the `Renderer` with material records and `lit.fx` (docs/design/06 §3.11, 07 §3.6).

### [X] 26. Bunny spawn loop size
- **File**: `engine/Game1.cs:58-66`
- **History**: `z < 1` → 8×8 → 20×20 → now **`x < 1` × `z < 1`** again (changed in `dcfbd12`, probably to test Billboard on one entity).
- **Related**: `Game1.cs:80` billboards only `bunnyEntity`, i.e. the *last* entity spawned. With a grid, only one bunny would face the camera.
- **Fix**: Decide the intended grid size. If billboarding should apply to all renderables, move it into a system (e.g. the empty `TransformSystem`, #35) that iterates an archetype view.
- **Resolution (2026-09-22, migration step 5)**: the spawn loop is gone. `games/Sandbox` spawns one entity per `spawn` record (`content/data/scene.json`), and `FaceCameraSystem` turns *every* entity tagged `FacesCamera` towards the camera. Proper sprite billboarding still moves to the renderer (F1).

---

## Issues introduced during the refactor (added 2026-06-14)

### [X] 27. Two `ComponentTransform` instances per entity (desync hazard)
- **Resolution**: `Entity` no longer has its own transform field. The instance added by `createEntity → addComponentFor` is the single source of truth, read via `entity.getComponent<ComponentTransform>()` (`Game1.cs:64, 80`; `ModelRendererSystem.cs:43`). `getComponent<T>` uses `TryGetValue(typeof(T), …)` and throws on miss.

### [X] 28. `createEntity` ID generation is flaky
- **Resolution**: A single `_randomNumberGenerator`, `NextInt64()`, and a `do/while ContainsKey` retry loop (`EntityContext.cs:29, 54-58`).

### [X] 29. Stale `// TODO` comment in `Entity.cs`
- **Resolution**: Deleted.

---

## New issues (added 2026-09-21)

### [X] 30. `ComponentTransform.LookAt` produces an inverted rotation
- **File**: `engine/Classes/EnginClasses/ECS/components/ComponentTransform.cs`
- **Problem**: Used `Matrix.CreateLookAt` (a *view* matrix, the inverse of a world transform), so the quaternion had the inverse orientation. It also went NaN when the target was straight above/below, and was undefined when the target sat on the object.
- **Resolution**: Builds `Matrix.CreateWorld(Vector3.Zero, direction, up)` so local Forward (-Z) points at the target. Returns early (keeps the current rotation) if the target is within ~1e-4 units. A shared `UpHintFor(direction)` helper swaps the up hint to `Vector3.Forward` when the direction is within ~2.5° of vertical.

### [X] 31. `ComponentTransform.Billboard` arguments were swapped
- **File**: `engine/Classes/EnginClasses/ECS/components/ComponentTransform.cs`
- **Problem**: Called `Matrix.CreateBillboard(targetPosition, Position, …)`, which passes the camera as the object, so the facing was flipped 180°.
- **Resolution**: Now `Matrix.CreateBillboard(Position, targetPosition, UpHintFor(direction), Vector3.Forward)`, with the same too-close guard and vertical up-hint as `LookAt`. Local Forward (-Z) faces the target.
- **Note**: With the corrected convention, `LookAt(cam)` and `Billboard(cam)` now give the same orientation. If the bunny model's visible front is +Z rather than -Z, it will now show its back to the camera. That is a model-orientation question (fix it in the model or with a per-mesh offset), not a reason to re-swap the arguments.

### [X] 32. Renderer ignores `ComponentTransform.Scale` — **Severity: Latent**
- **File**: `engine/Classes/EnginClasses/ECS/systems/ModelRendererSystem.cs:47-50`
- **Problem**: `effect.World = CreateFromQuaternion(rotation) * translation` has no scale term, so setting `Scale` does nothing.
- **Fix**: `effect.World = Matrix.CreateScale(transform.Scale) * Matrix.CreateFromQuaternion(transform.Rotation) * Matrix.CreateTranslation(transform.Position);`. Consider a `ComponentTransform.WorldMatrix` property so every system builds it the same way.
- **Resolution (2026-09-22, quick-fix batch)**: `ModelRendererSystem` now builds `effect.World = CreateScale(transform.Scale) * CreateFromQuaternion(rotation) * translation`.

### [X] 33. `removeComponentFor` can leave state half-updated — **Severity: Latent**
- **File**: `engine/Classes/EnginClasses/ECS_base_classes/EntityContext.cs:134-147`
- **Problem**:
  - The entity is removed from the group *before* `entity.removeComponent(...)`, which throws if the entity doesn't have that component. After the throw, the group no longer holds the entity, but the event never fired.
  - `OnComponentRemoved` receives the caller's `component` argument, not the instance actually stored on the entity. Passing a fresh `new ComponentMeshRenderer(...)` removes the real one but reports the wrong object.
  - `removeEntity` also doesn't clear the entity's `_components`. Fine for the archetype views (they use `Remove`), but a handler holding the dead entity still sees all its components.
- **Fix**: Look up the stored instance first (`removeComponentFor<T>(entity)` or by `Type`), validate, then mutate the entity, then the group, then fire the event with the stored instance.
- **Resolution (2026-09-22, migration step 3 — `World` over Friflo.Engine.ECS)**: `World.Remove<T>` checks the component exists first and returns false with no side effects if not; notifications come from the store, so they always describe what was actually removed.

### [X] 34. `Entity.addComponent` silently swallows null — **Severity: Cosmetic**
- **File**: `engine/Classes/EnginClasses/ECS_base_classes/Entity.cs:34-41`
- **Problem**: `Console.WriteLine` + return, with the throw commented out. Unreachable today because `addComponentFor` already throws `ArgumentNullException`, but the two layers disagree.
- **Fix**: Throw (matching `removeComponent`), or drop the check since `internal` callers already validate.
- **Resolution (2026-09-22, migration step 3 — `World` over Friflo.Engine.ECS)**: moot: components are structs now (they can't be null), and `World.Add` refuses duplicates with an `Ensure`.

### [X] 35. `TransformSystem` is a stub that throws — **Severity: Wiring**
- **File**: `engine/Classes/EnginClasses/ECS/systems/TransfomSystem.cs`
- **Problem**: It is never created. `update` throws `NotImplementedException`, so it would crash the frame if anything ever called it. It also has the namespace typo (#21) and file-name typo (#24).
- **Fix**: Implement it (e.g. host billboard/look-at behaviour, see #26) or delete it until needed.
- **Resolution (2026-09-22, quick-fix batch)**: the stub was deleted. Its replacement is `TransformPropagationSystem` (`docs/design/03` §3.6).

### [X] 36. Dead fields / methods / stale comments — **Severity: Cosmetic**
- `InputSystem.isDebug` and `key_binds["toggle_debug"]` (`InputSystem.cs:14-17`) — never read. Same pattern as the removed `debugModes` (#8).
- `Entity.context` (`Entity.cs:7`) — assigned, never used. It also forces an `EntityContext` singleton to exist for every `new Entity()`.
- `EditorUI.context` (`EditorUI.cs:11, 15`) — never used.
- `EditorManager.getCamera()` — no callers. `setCamera` is only called from `Game1`.
- `DevCamera.Target` / `_camTarget` (`DevCamera.cs:13-17, 49`) — never read, and it's set to a *direction* (`camForward`), not a point.
- `EntityContext.getGroups()` and `getAllEntitiesFromListOfGroups()` — no callers since `ArchetypeView` replaced them. `getAllEntitiesFromListOfGroups` also has a leftover `Console.WriteLine` in its loop and returns the **union** (any group), unlike `ArchetypeView`'s all-of match.
- `ArchetypeView.cs:7` and `EntityContextListener.cs:12` refer to `EntityContextSystem`, which was deleted in `dcfbd12`.
- `EditorManager.cs:18-19, 29-30` — commented-out `ApplyChanges` lines. The one in the *width* setter sets `PreferredBackBufferHeight` (copy-paste bug if ever re-enabled).
- Unused `using`s (e.g. `System.Collections.Generic` in `IComponentSystem.cs`, `System` in `IComponent.cs`, `Microsoft.Xna.Framework.Input` in `EditorUI.cs` / `EntityContextMenuUI.cs`, self-`using sage_engine;` in the Screens files).
- **Resolution (2026-09-22, quick-fix batch)**: removed `InputSystem.isDebug`/`key_binds`, `Entity.context`, `EditorUI.context`, `DevCamera.Target`/`_camTarget`, and the stale `EntityContextSystem` comments in `ArchetypeView.cs`/`EntityContextListener.cs`. Remaining: `EditorManager.getCamera`, `getGroups`, `getAllEntitiesFromListOfGroups`, unused `using`s (tracked in `TODO.md`).
- **Resolution (2026-09-22, migration step 3 — `World` over Friflo.Engine.ECS)**: `EditorManager.getCamera` removed; `getGroups`/`getAllEntitiesFromListOfGroups` went with `EntityContext`. (Unused `using`s aren't tracked any more.)

### [X] 37. Input bypasses `InputSystem` in `Game1` — **Severity: Cosmetic**
- **File**: `engine/Game1.cs:75`
- **Problem**: Escape and GamePad Back are read via `Keyboard.GetState()` / `GamePad.GetState()` *before* `InputSystem.update`. That is the last direct hardware read outside the listeners.
- **Fix**: Move `InputSystem.getInstance().update(gameTime)` first and use `input.IsKeyPressed(Keys.Escape)`. GamePad can stay direct until there's a gamepad listener.
- **Resolution (2026-09-22, migration step 6)**: Escape and gamepad Start/Back are bindings of the `Menu` action (`engine_content/data/input.json`); `Game1` reads `actions.Pressed(Menu)`. There's a `GamepadListener` now. (The step-2 version, `input.IsKeyPressed(Keys.Escape)`, never fired: see #40.)

### [X] 38. Component groups/archetypes are keyed by type-name strings — **Severity: Latent**
- **Files**: `EntityContext.cs:123, 140`, `Entity.cs:64-72`, `ArchetypeView.cs:12, 27-37`, `ModelRendererSystem.cs:16`
- **Problem**: Components are stored by `Type`, but groups and archetypes match on `GetType().Name` strings (`Track("ComponentTransform", "ComponentMeshRenderer")`). A typo matches nothing and fails silently, renames aren't caught by the compiler, and two same-named types in different namespaces would collide. `Entity.hasComponent(string)` also scans every component linearly instead of using the dictionary.
- **Fix**: Key on `Type`: `Track(params Type[])` or `Track<T1, T2>()`, `hasComponent(Type)` → `_components.ContainsKey(type)`, and `Dictionary<Type, Dictionary<long, Entity>>` for groups.
- **Resolution (2026-09-22, migration step 3 — `World` over Friflo.Engine.ECS)**: string-keyed groups and `ArchetypeView` replaced by typed Friflo queries (`World.Query<T1, T2>()`).

### [X] 39. `DevCamera` mouse sensitivity scaling is odd — **Severity: Cosmetic**
- **File**: `engine/Classes/EnginClasses/CameraClasses/DevCamera.cs:104-107`
- **Problem**: Dividing the delta by the display size and multiplying it by the back-buffer size makes look speed depend on window size (a small window gives slow look), which is the opposite of the comment's claim. It also queries `GraphicsAdapter.DefaultAdapter.CurrentDisplayMode` on every drag event.
- **Fix**: Use raw pixel delta × a sensitivity constant (radians per pixel). Scale by nothing, or by DPI if needed.
- **Resolution (2026-09-22, migration step 6)**: look = pixel delta × 0.004 rad/px × `m_sensitivity` (Archive cvar, shared with the `Look` action), with `m_invert_y`.

### [X] 40. Polled input edges were always false — **Severity: Wiring** (found 2026-09-22, migration step 6)
- **Files**: `src/Sage.Client/Input/KeyboardListener.cs`, `MouseListener.cs`
- **Problem**: `Update` copied the current state into the previous one at the *end*, so after polling, `IsKeyPressed`/`IsKeyReleased`, `IsButtonPressed`/`Released`, `PositionDelta` and `ScrollWheelDelta` compared a state with itself. Only the events (raised inside `Update`) saw edges. So `Game1`'s `input.IsKeyPressed(Keys.Escape)` (step 2) could never fire, and Escape never quit.
- **Resolution (2026-09-22, migration step 6)**: both listeners roll the previous state at the *start* of `Update`, so polled edges stay valid until the next poll. `InputActions` relies on this.

### [~] 41. Per-frame allocations in the host — **Severity: Latent** (found 2026-09-22, migration step 6)
- **Where**: the host frame (`src/Sage.Host/Game1.cs`), measured with `mem_warn_bytes 1` (`StatOverlay`).
- **Problem**: steady-state frames should allocate nothing (02 §4.6). The world's systems don't: toggling the Extract, Render and gameplay systems off changes nothing. But the frame as a whole allocates 176 B, the same on the commit before step 6. Likely candidates are ImGui.NET's per-frame layout or MonoGame's input polling.
- **Fix**: find it with an allocation profiler (dotnet-counters / dotnet-trace GC allocation ticks); fix it, or accept and document it as the baseline.

### [X] 42. A record with a non-string "base" crashed the record store — **Severity: Wiring** (found 2026-09-22, F18)
- **File**: `src/Sage.Engine/Content/RecordStore.cs` (`ResolveBases`)
- **Problem**: `"base"` is the reserved key for record inheritance, and the store read it with a blind cast. An `attribute` record with a numeric field of its own called `base` (`{ "type": "attribute", "id": "health", "base": 100 }`) threw `InvalidOperationException` out of `Load`, which took the whole game down at boot rather than reporting a bad record.
- **Resolution (2026-09-22, F18)**: a non-string `base` is now an error naming the record and saying the key is reserved; loading continues. The engine's `attribute` records call the field `start`, and 05 §3.5 lists the reserved keys.
- **Cause (2026-09-22, F1)**: the ImGui entity inspector (`EntityContextMenuUI`). It allocates about 130 bytes per listed entity per frame (the `$"{World.Describe(entity)}##{id}"` label, and reflection when a node is expanded). With the inspector's draw call removed, frames allocate **0 bytes** — the engine loop, the renderer and the rest of the host frame are allocation-free, as `SteadyStateTicksAndFrames_DoNotAllocate` also asserts for 200 entities.
- **Partly fixed (F1, F13)**: the window skips its whole listing when collapsed, and `ui_entities 0` hides it entirely. With it hidden the frame allocates 0 bytes, including the terrain chunks' meshes and the sprite batcher. Still open: while it is open the cost grows with entity count, so cache the labels (or list only visible rows) before scenes get big. It is a dev-build tool only (02 §4.6).

---

## Consistency pass over steps 1-19 (2026-09-22)

Read the whole of `src/` and `games/` against the design docs before starting combat (F20), looking
for conventions that had drifted apart rather than for new features. Items #43-#52 come from that
pass; the first two were real, silent bugs.

### [X] 43. Two facing conventions, bridged by a single `+PI` — **Severity: Wiring** (found 2026-09-22)
- **Files**: `src/Sage.Engine/Rendering/SpriteData.cs`, `src/Sage.Engine/Gameplay/CharacterController.cs`, `games/Sandbox/SandboxModule.cs`
- **Problem**: the sprite system read an entity's front as its local **+Z** while movement, the camera, `TransformMath.Forward`, the AI and the input code all used **-Z**. The two only agreed because `CharacterMovementSystem` wrote `Quaternion.CreateFromYawPitchRoll(yaw + MathF.PI, ...)` — one half turn, in a movement system, holding two subsystems together. Consequences: `TransformMath.LookAt` pointed a sprite 180° away from its target; a `spawn` record's `yaw` meant one direction for a prop and the opposite for a character; and because `PawnIntent.Yaw` was never seeded from the transform, the first tick spun every character to yaw 0 and discarded the direction the scene had placed it facing. The Sandbox's "watcher", authored to look at the player, stood with its back to them.
- **Resolution**: `src/Sage.Engine/Core/SageMath.cs` states the convention once (+Y up, front is local -Z, yaw 0 faces -Z, positive yaw counter-clockwise seen from above) and owns `WrapPi/WrapTau`, `ForwardFromYaw`, `RotationFromYaw`, `YawOf`, `YawTo`, `DistanceXZ`, `TurnToward` and `InCone` — the four private copies of those helpers (AI, input, sprites, dev camera) are gone. `SpriteMath.DirectionIndex` reads the entity's -Z, the `+PI` is deleted, and `World.AddCharacter` seeds `PawnIntent.Yaw` from the transform. Tests: `DirectionZeroIsAlwaysTheViewFromWhereTheEntityLooks`, `ACharacterKeepsFacingTheWayItWasPlaced`.

### [X] 44. Colliders were anchored at the shape's centre, so anything standing on its feet was buried — **Severity: Wiring** (found 2026-09-22)
- **Files**: `src/Sage.Engine/Physics/PhysicsData.cs`, `PhysicsSpace.cs`, `PhysicsSystems.cs`
- **Problem**: Bepu poses a shape by its centre, and `PhysicsSyncSystem` pushed the entity's transform in as that centre. But a character's transform is its **feet** (`CharacterController`), and the Sandbox's trees were authored standing on the ground. Their capsules therefore spanned `feet - h/2 .. feet + h/2`: half underground, half the height they looked. The character controller's own sweeps used a different pose (`feet + h/2`), so the shape a character swept with and the shape the world collided with were in different places.
- **Resolution**: `Collider` gained an explicit `Center` offset and a `Collider.Standing(radius, totalHeight)` factory that owns the height-to-cylinder conversion; `Add`, `SetPose`, `Sweep` and the dynamic write-back all apply (and undo) it. The character controller sweeps with `Collider.Standing` too, so there is one shape. Tests: `AStandingCapsuleIsAnchoredAtItsFeet`, `ADynamicColliderWithAnOffsetCentreWritesBackTheEntitysPosition`.

### [X] 45. Physics layer indices were hard-coded — **Severity: Latent** (found 2026-09-22)
- **Files**: `src/Sage.Engine/Gameplay/AI.cs`, `AIThinkSystem.cs`, `CharacterController.cs`, `games/Sandbox/SandboxModule.cs`
- **Problem**: `LayerMask.All.Except(2)`, `const int AgentLayer = 2`, `byte layer = 1` — five sites carried literals for layers the `physics_layers` record names. `LayerMatrix.IndexOf`/`Name` existed for exactly this and were called only from tests, so a game that reordered its layers would silently repoint AI vision and self-exclusion sweeps.
- **Resolution**: `LayerMatrix` resolves `Player`, `Enemy` and `Trigger` once when the record is applied (with a warning and a documented fallback when a game drops one), and every site reads them from the space. `CharacterController.Create` no longer defaults its layer, since that default disagreed with `Collider`'s.

### [X] 46. `ActiveCamera` had two overlapping camera flags — **Severity: Cosmetic** (found 2026-09-22)
- **File**: `src/Sage.Engine/ECS/ActiveCamera.cs`
- **Problem**: `OwnedByRig` (set by the rig) and `RigEnabled` (added for `cam_free`) read as two names for one thing, and `Game1` tested both, one of them redundantly.
- **Resolution**: kept both — they are genuinely policy and status — but named and documented as such: `RigEnabled` (the host: may rigs drive at all?) and `DrivenByRig` (a rig: I drove it this frame).

### [X] 47. The default-`GameRules` guard could never fire — **Severity: Cosmetic** (found 2026-09-22)
- **Files**: `src/Sage.Engine/Gameplay/PawnIntent.cs`, `src/Sage.Engine/Core/Engine.cs`
- **Problem**: `GameplayModule.OnWorldCreated` installed `DefaultGameRules` only "if the game hasn't" — but engine modules always run before the game module, so the check was always true and the instance was always thrown away a moment later.
- **Resolution**: the default is installed in `Engine.CreateWorld` after `NotifyWorldCreated`, which is the first point where the question has an answer. Test: `AWorldWithoutAGamesRulesGetsTheDefaultOnes` (unchanged, still passes).

### [X] 48. AI intent was acted on a tick late — **Severity: Latent** (found 2026-09-22)
- **File**: `src/Sage.Engine/Gameplay/PawnIntent.cs`
- **Problem**: `AIThinkSystem` ran in `Phase.AI` (5) while `CharacterMovementSystem`, which consumes `PawnIntent`, runs in `Phase.PrePhysics` (1). Every creature's decisions were therefore executed on the *next* tick, while the player's (written in `Phase.Commands`) were same-tick — and `AIThinkSystem`'s own comment claimed the opposite. `FaceTarget` also tested convergence against a yaw the body had not adopted yet.
- **Resolution**: `AIThinkSystem` moved to `Phase.Commands`, after `PlayerControlSystem`. Both controllers now write intent in the same phase, and everything downstream reads it in the tick it was decided; death from an AI hit is likewise noticed in the same tick.

### [X] 49. `WorldResources` claimed an ordering `Dictionary` does not give — **Severity: Latent** (found 2026-09-22)
- **File**: `src/Sage.Engine/ECS/WorldResources.cs`
- **Problem**: teardown documented "reverse insertion order" but enumerated `Dictionary.Values`, whose order is unspecified and shifts after a `Remove`. The physics space's disposal depends on it.
- **Resolution**: installation order is kept in a parallel list; `Set` re-registers a replaced instance, `Remove` drops it, and a resource installed under two types is disposed once.

### [X] 50. The AI's sight cone was authored but never checked — **Severity: Wiring** (found 2026-09-22)
- **File**: `src/Sage.Engine/Gameplay/AIThinkSystem.cs`
- **Problem**: `AIProfileRecord.SightAngleDegrees` (200°) was in the record and in `ai.json`, and `FindNearestPlayer`'s comment said "inside the sight cone" — but there was no cone test, so creatures noticed the player through the backs of their heads.
- **Resolution**: perception takes the agent's yaw and checks `SageMath.InCone`. Test: `ItDoesNotNoticeAPlayerBehindItsBack`.

### [X] 51. `sandbox_autowalk` never turned — **Severity: Cosmetic** (found 2026-09-22)
- **File**: `games/Sandbox/SandboxModule.cs`
- **Problem**: the cvar promised "turning this many degrees per second" and added a step to `PawnIntent.Yaw` each tick — but `PlayerControlSystem` had already overwritten that field with the (unchanging) view yaw, so the result was a constant offset and the pawn walked in a straight line. The smoke-test coverage it was written for wasn't happening.
- **Resolution**: the system accumulates its own yaw, seeded from the pawn's when it is switched on.

### [X] 52. The same code, written twice — **Severity: Cosmetic** (found 2026-09-22)
- **Where**: four angle-wrap helpers (`AIMath`, `CommandLatch`, `SpriteMath`, `DevCamera`), two forward-from-yaw expressions, two capsule height conversions with different clamps, and the box mesh built once in `Renderer.CreateBox` and once in `CreateErrorMesh`.
- **Resolution**: angles live in `SageMath` (`AIMath` is a public thin wrapper, since `IAITask` is public and games write their own tasks); capsule dimensions in `Collider.Standing`; the box corners in `Renderer.BuildBox`.

### [X] 53. Physics queries were stopped by trigger volumes — **Severity: Wiring** (found 2026-09-22, F20)
- **Files**: `src/Sage.Engine/Physics/PhysicsCallbackData.cs` (`Allows`), `PhysicsSpace.cs` (the three query handlers)
- **Problem**: raycasts, sweeps and overlaps filtered by collision layer only, so a trigger — a thing with no surface, whose whole job is to report overlaps — blocked line of sight, stopped a character sweep and would have swallowed a sword swing. The Sandbox only escaped it because its one trigger hangs in the air.
- **Resolution (2026-09-22, F20)**: queries skip triggers by default; `includeTriggers: true` asks for them. Test: `QueriesSeeThroughTriggersUnlessTheyAskForThem`.

### [X] 54. The placeholder creature's art was mirrored, so its facing read wrong — **Severity: Cosmetic** (found 2026-09-23)
- **Files**: the generator for `games/Sandbox/content/textures/creature.png` (now `games/Sandbox/tools/make_placeholder_art.py`), `src/Sage.Client/Rendering/SpriteBatcher.cs`
- **Problem**: reported three times as "the sprite facing looks a bit off", and twice investigated in the *code*, which was right both times. The fault was in the art: the generator drew the nose at `+sin(angle)` when the projection of an entity's facing onto the screen's right axis is `-sin(angle)`, so every angled view showed the creature turned the wrong way — while the pack on its back, drawn with the correct sign, showed it turned the right way. Direction *selection* matched Doom's rotation order all along; the sheet it was selecting from did not.
- **Resolution (2026-09-23)**: the generator's sign is fixed and the sheet regenerated; the generator now lives in the repo beside the art it makes, since an untracked script is how the convention got lost in the first place. `DirectionGroupsFollowDoomsRotationOrder` pins the numbering to the Doom wiki's description so it cannot drift again, and 06 §3.8 states what the art has to look like. Verified by measuring a rendered row of eight creatures at known yaws against the angles the geometry demands — all eight agree.
- **Also**: billboards went back to **view-plane alignment**, which is what Doom and Daggerfall used. Turning each quad toward the camera's position (tried on 2026-09-22) is defensible at a distance and wrong up close, where a sprite rotates away as the player passes its origin; Daggerfall Unity's author reached the same conclusion. `r_sprite_facecamera 1` switches back for comparison.

### [X] 55. A character placed inside the ground fell through the world — **Severity: Wiring** (found 2026-09-23)
- **File**: `src/Sage.Engine/Gameplay/CharacterController.cs` (`GroundCheck`)
- **Problem**: found while dressing the Sandbox in Daggerfall art. A skeleton spawned at terrain height vanished; it was at **y = -16 m** and still falling. Its capsule had started a few centimetres *inside* the collision mesh, where every sweep begins overlapping — and an overlapping sweep carries no normal, so the space reports nothing at all (10 §4). The ground check read "nothing below me", the character fell, and the next tick was no different. It only showed up now because `Terrain.HeightAt` interpolates the heightfield bilinearly while the collision mesh is triangulated, so the two disagree by a little, either way, depending on the spot. The Sandbox's player start has sat at `y: 1` since F7 — a workaround for this, written before anyone knew what it was working around.
- **Resolution (2026-09-23)**: when the downward sweep finds nothing, the controller casts a ray down from the capsule's centre before believing it. If that finds walkable ground within reach, the feet are put back on top of it. One ray, only in the case that used to be unrecoverable. Test: `ACharacterStandingInsideTheGroundIsPutBackOnTopOfIt`.

### Noted, not fixed (carried into TODO)
- `PhysicsSpace.OverlapBox` is broad-phase only: it can report entities whose shapes don't touch. The name should say so once a narrow-phase version exists (10 §4).
- An empty `LayerMask` means "every layer", so a fully-`Except`-ed mask inverts its own intent. Wants `LayerMask?` for "unspecified" and `Bits == 0` meaning "nothing".
- Crouching shrinks the character's sweep capsule but not its `Collider`, so a crouching character still blocks the world at full height. Needs a shape update path in `PhysicsSpace`.
- `AICondition.TaskFailed`/`ScheduleDone` are set and then overwritten by the next `Perceive`; only conditions listed in a schedule's `interrupts` are ever read. A schedule's `interrupts` also can't fire on its first task (that guard stops an immediate re-interrupt), so single-task schedules rely on schedule *choice* instead.
- The dev camera's look scale (`DevCamera.LookRadiansPerPixel`) is 1.6x the `Look` action's, and it re-implements invert-Y. It should bind through the action (08 §14 step 4).
- `PhysicsSpace.Rebase`, `SetVelocity`, `IsAwake` and `EntityOf` have no callers yet (14/F20 work); `Pawn`, `GameplayTags.HasAll/HasAny` and `Effects.Remove` likewise.
