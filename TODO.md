# Sage Engine — Code Review Findings & TODO

Generated from a full read-through of the engine source on 2026-05-25.
Last verified against source: 2026-06-14 (post-#17/#18/#19/#29 round; visibility policy almost fully applied).

Legend:
- [ ] = open
- [X] = done
- [~] = partially done
- **Severity**: Critical / Wiring / Latent / Cosmetic

---

## Critical bugs (wrong behavior or crashes)

### [X] 1. `ModelRendererSystem` caches its entity list once and never refreshes
- **File**: `engine/Classes/ECS/systems/ModelRendererSystem.cs:18`
- **Problem**: The query `getAllEntitiesWithListOfComponents([...])` is run in the singleton constructor. Because the system is a singleton, the list is built once for the entire lifetime of the program. Any entity created after `ModelRendererSystem.getInstance()` is first called will never be rendered.
- **Why it "works" today**: All bunnies are spawned in `LoadContent` before `Draw` runs, so the first `getInstance()` call happens after all entities exist.
- **Fix**: Re-query each frame inside `render(...)`, or subscribe to an "entity added/component added" event in `EntityContext` to keep the list in sync.

### [X] 2. Quaternion components used as if they were Euler-degrees
- **File**: `engine/Classes/ECS/systems/ModelRendererSystem.cs:50`
- **Problem**:
  ```csharp
  Matrix.CreateFromYawPitchRoll(
      MathHelper.ToRadians(entityRotation.Y),
      MathHelper.ToRadians(entityRotation.X),
      MathHelper.ToRadians(entityRotation.Z));
  ```
  `entityRotation` is a `Quaternion`, so `.X .Y .Z` are imaginary components, not angles. Converting them with `ToRadians` is meaningless.
- **Secondary problem**: `worldRotationMatrix` is computed but never multiplied into `effect.World` (line 54) — rotation has zero effect right now.
- **Fix**: `effect.World = Matrix.CreateFromQuaternion(entityRotation) * worldPositionMatrix;`

### [X] 3. `ComponentTransform` defaults are broken for rotation/scale
- **File**: `engine/Classes/ECS/components/ComponentTransform.cs`
- **Problem**: Auto-properties default to `default(T)`. For `Quaternion` that's `(0,0,0,0)` (not identity `(0,0,0,1)`), and `Vector3 scale` defaults to `Zero`. As soon as the renderer actually uses rotation/scale, models will disappear or render as garbage.
- **Fix**: Initialize in the field/constructor:
  ```csharp
  public Quaternion rotation { get; set; } = Quaternion.Identity;
  public Vector3 scale { get; set; } = Vector3.One;
  ```

### [X] 4. `EntityContext.removeEntity` does not clean up `entityGroups`
- **File**: `engine/Classes/ECS_base_classes/EntityContext.cs:34-44`
- **Problem**: The entity is removed from the `entities` dict, but its references remain in every group list under `entityGroups`. Systems that iterate groups will operate on dead entities.
- **Fix**: Iterate the removed entity's components and remove the entity from each corresponding group list (and remove tags similarly if/when group tracking expands to tags).
- **Resolution**: `removeEntity` now iterates `entityGroups` and calls `group_entities.Remove(entity.getId())` for each group.

### [X] 5. Camera movement is not frame-rate independent
- **File**: `engine/Classes/CameraClasses/Camera.cs:119-139`
- **Problem**: Hard-coded `0.25f` per frame with no use of `gameTime.ElapsedGameTime`. Movement speed varies with FPS.
- **Fix**: Multiply movement by `(float)gameTime.ElapsedGameTime.TotalSeconds` (pass `gameTime` through to `CustomMovement`).

### [X] 6. `EntityContextMenuUI` opens a popup that's never defined
- **File**: `engine/Classes/Screens/EntityContextMenuUI.cs:35-41`
- **Problem**: `ImGui.OpenPopup("EntityContextMenuPopup")` has no matching `ImGui.BeginPopup("EntityContextMenuPopup")` block, so the context menu never actually appears.
- **Secondary problem**: `Mouse.GetState().RightButton == Pressed` fires every frame the button is held — should compare against previous mouse state to detect a click edge.
- **Fix**: Add a matching `BeginPopup/EndPopup` pair, and track previous mouse state for edge detection.
- **Resolution**: Switched to `BeginPopupContextItem("ctx_" + entity.getId())` placed immediately after `TreeNodeEx` (with the result captured into `nodeOpen`). ImGui handles right-click edge detection and popup open/close internally — no manual mouse-state tracking needed. The popup correctly binds to the entity row whether the tree is collapsed or expanded.
- **Follow-up done**: the `Delete` menu item now calls `context.removeEntity(entity)`. Safe to delete during iteration because `getAllEntities()` returns a fresh `List<Entity>` snapshot — the foreach iterates the copy, not the live dict.

---

## Missing connections / dead wiring

### [X] 7. `EditorManager.current_fps` is never set
- **File**: `engine/Classes/EditorManager.cs` (formerly lines 13, 67)
- **Problem**: `setCurrentFPS` has no callers. `Game1.Update`/`Draw` never compute FPS, and `EditorUI` never reads it. The `"showFPS"` debug mode default-true is dead too.
- **Fix**: Compute FPS in `Game1.Update` (e.g. `1f / (float)gameTime.ElapsedGameTime.TotalSeconds`) and render an overlay in `EditorUI` when `getDebugMode("showFPS")` is true.
- **Resolution**: Dead `current_fps` / `setCurrentFPS` were removed from `EditorManager`. FPS overlay is still not implemented — re-open if/when it's actually needed.

### [X] 8. None of the debug modes are wired up
- **File**: `engine/Classes/EditorManager.cs` (formerly lines 19-23)
- **Problem**: `showGrid`, `showBoundingBox`, `showCollision`, `showCameraInfo` are stored but no system reads them. The whole `debugModes` dictionary is currently dead.
- **Fix**: Either implement matching overlays/gizmos or remove until you actually need them.
- **Resolution**: The `debugModes` dictionary was removed from `EditorManager` (the field is now commented out at `EditorManager.cs:7`). Reintroduce when overlays/gizmos are actually being implemented.

### [ ] 9. `UtilAssets.lightIconTexture` is loaded but never used
- **File**: `engine/Classes/UtilAssets.cs:7, 11` (renamed from `GameAssets` since the original TODO entry)
- **Status**: Still loaded in `InitializeModels`, still has no references anywhere else in the engine.
- **Fix**: Use it for a light-source icon in the editor, or drop the load.

### [X] 10. `Primitive.cs` is an empty abstract class
- **File**: `engine/Classes/Primitives/Primitive.cs`
- **Fix**: Either flesh it out (cube/sphere/plane builders) or delete it.

### [X] 11. `EditorManager.getGraphicsDeviceManager()` returns a never-assigned field unless `setGraphicsDeviceManager` was called
- **File**: `engine/Classes/EditorManager.cs:8, 32-34`
- **Problem**: Works today because `Game1.Initialize` always calls the setter, but the contract is fragile. The constructor takes a `Game` but never uses it.
- **Fix**: Take the `GraphicsDeviceManager` in the constructor; remove the `Game` parameter or actually use it.

### [X] 12. `Game1.cs` is in `Content/`
- **File**: `engine/Content/Game1.cs`
- **Problem**: That folder is for MGCB-built assets. Compiles by accident because the csproj globs all `.cs` files. Confusing and risks the file being treated as content.
- **Fix**: Move to `engine/Game1.cs` (project root, next to `Program.cs`).

---

## Latent bugs / design issues

### [X] 13. `addComponent` doesn't enforce one-component-per-type
- **File**: `engine/Classes/ECS_base_classes/Entity.cs:11, 73-80`
- **Problem**: `components.Contains(component)` uses reference equality. Adding two `ComponentTransform` instances succeeds. `getComponent<T>()` then returns whichever it finds first.
- **Fix**: Store as `Dictionary<Type, Component>` and reject duplicates by type.
- **Resolution**: `Entity._components` is now `Dictionary<Type, IComponent>`. `addComponent` calls `_components.Add(type, component)`, which throws `ArgumentException` on duplicate keys — duplicates are now rejected by type.

### [X] 14. `Tag` system uses reference equality
- **Problem**: `tags.Contains(tag)` and `hasTag(tag)` compare references — two `new EditorOnly()` are distinct.
- **Fix**: Track tags by `Type`, or expose `hasTag<T>()`.
- **Resolution**: Tag system removed entirely — `Tag.cs` and `EditorOnly.cs` deleted; `Entity.cs` no longer has any tag fields or methods. Re-introduce with a `Type`-keyed implementation if/when tags are needed again.

### [X] 15. `EntityContext.getGroups()` returns the internal dictionary by reference
- **File**: `engine/Classes/ECS_base_classes/EntityContext.cs`
- **Problem**: `Entity.addComponent` (`Entity.cs:78-85`) mutates it directly — encapsulation leak and tight coupling.
- **Fix**: Move group bookkeeping into `EntityContext.addComponentFor(entity, component)` (or similar). Make `getGroups` return an `IReadOnly...` view.
- **Resolution**:
  - `EntityContext.addComponentFor(entity, component)` and `removeComponentFor(entity, component)` are now the only writers — they update `_components` and `entityGroups` together.
  - `Entity.addComponent` / `Entity.removeComponent` are `internal` and only touch `_components`; they no longer know `entityGroups` exists.
  - `getGroups()` now returns `IReadOnlyDictionary<string, IReadOnlyDictionary<long, Entity>>`. External callers can read but cannot mutate.
  - Note: the read view is rebuilt via `ToDictionary` on each call. Cheap for editor/inspector use; cache if it ever ends up in a hot loop.

### [X] 16. Entity ID type inconsistency
- **Files**: `engine/Classes/ECS_base_classes/EntityContext.cs`, `Entity.cs`
- **Problem**: `entities` dict is keyed by `long`, but `contextEntityCount` and `Entity.id` are `int`. `getEntity(long)` exists but `setId(int)` doesn't. Works by implicit conversion.
- **Fix**: Pick one (probably `int`) and use it everywhere.
- **Resolution**: Standardized on `long`. `Entity.id`, `getId()`, `setId()`, `entities` keys, and `getEntity()` all use `long`. `contextEntityCount` was removed in favor of generating IDs inside `createEntity`. The type-consistency problem is closed — but the new generation scheme has its own issues; see new item #28.

### [X] 17. Camera rotation drifts and has no pitch clamp
- **File**: `engine/Classes/CameraClasses/Camera.cs`
- **Problem**: Adding `mouse * normal` to `camForward` then normalizing causes gradual drift and lets the camera roll / get stuck near vertical.
- **Fix**: Store yaw and pitch as floats; rebuild `camForward` each frame from them; clamp pitch to ±89°.
- **Resolution**:
  - Added `private float yaw` and `private float pitch` fields (radians), plus `static readonly float PitchLimit = MathHelper.ToRadians(89f)`.
  - Constructor seeds them from the `rotation` Vector3 (`rotation.Y` → yaw, `rotation.X` → pitch with the clamp), then calls a new `rebuildForward()` helper that does `camForward = Vector3.Transform(Vector3.Forward, Matrix.CreateFromYawPitchRoll(yaw, pitch, 0))`.
  - Right-click drag updates yaw/pitch instead of `camForward`: `yaw -= mouse_x * sensitivity; pitch -= mouse_y * sensitivity;` then `MathHelper.WrapAngle(yaw)` and `MathHelper.Clamp(pitch, -PitchLimit, PitchLimit)`, then `rebuildForward()`. No more cumulative additions, no more re-normalization, no roll, no flipping near vertical.

### [X] 18. `EditorUI` "Exit" calls `System.Environment.Exit(0)`
- **File**: `engine/Classes/Screens/EditorUI.cs`
- **Problem**: Bypasses MonoGame shutdown (Content disposal, GraphicsDevice cleanup).
- **Fix**: Hold a reference to `Game1` and call its `Exit()` method.
- **Resolution**: `EditorUI.Draw` now takes a `Game game` parameter and calls `game.Exit()` for the menu's Exit item. `Game1.Draw` passes `this` (`EditorUI.GetInstance().Draw(this);`). MonoGame runs its shutdown path normally now.

### [X] 19. Mouse delta on first frame of right-click can snap
- **File**: `engine/Classes/CameraClasses/Camera.cs`
- **Problem**: Although `prevMouseState` is updated every frame (in both press/release branches), if the cursor moves a lot in a single frame the camera will still snap.
- **Fix**: On the rising edge of right-click, force the delta to zero for that frame.
- **Resolution**: `CustomMovement` now computes `bool risingEdge = mouseState.RightButton == Pressed && prevMouseState.RightButton == Released;`. The rotation block runs only when `Pressed && !risingEdge`, so the first frame after the user re-presses RMB doesn't apply any yaw/pitch delta. `prevMouseState = mouseState` moved to a single unconditional assignment at the end, replacing the duplicated press/release branches.

### [X] 20. `createEntity` adds to `entityGroups` before adding to `entities`
- **File**: `engine/Classes/ECS_base_classes/EntityContext.cs:25-33`
- **Problem**: Inside `addComponent(new ComponentTransform())` the entity gets registered into the groups dict, but it's not yet present in the master `entities` dict. Anyone iterating groups during that window sees a half-registered entity.
- **Fix**: Insert into `entities` first, then add components.
- **Resolution**: `createEntity` now does `entities.Add(...)` before `addComponentFor(entity, new ComponentTransform())`, so the entity is fully registered in `entities` before any group registration happens.

---

## Cosmetic / style

### [X] 21. Namespace mismatch
- `Camera.cs` and `EditorManager.cs` are in the global namespace; everything else is in `sage_engine`.
- **Fix**: Put them in `namespace sage_engine;`.

### [X] 22. Mixed visibility
- `Game1` is `public`; `Component`, `ComponentSystem`, `Tag`, `Entity`, etc. have no modifier (default `internal`).
- **Fix**: Pick a policy. If this is a single-assembly engine, `internal` everywhere except true API surfaces is fine.
- **Resolution**: Policy is `internal` everywhere except `Game1` (the entry point, stays `public`). Explicit `internal` is now on every other class/interface in the engine: `Entity`, `EntityContext`, `Camera`, `EditorManager`, `EditorUI`, `EntityContextMenuUI`, `ComponentTransform`, `ComponentMeshRenderer`, `ModelRendererSystem`, `IComponent`, `IComponentSystem`, `UtilAssets` (renamed from `GameAssets` in the same round).

### [X] 23. Interface naming
- `Component` and `ComponentSystem` would conventionally be `IComponent`, `IComponentSystem`. (`Tag` is no longer in the codebase — see #14.)
- **Fix**: Rename for clarity.
- **Resolution**: Both renamed. `IComponent.cs` and `IComponentSystem.cs` exist; `ModelRendererSystem : IComponentSystem` and all components implement `IComponent`.

### [X] 24. Typos
- Fixed in `GameAssets.cs`, `EditorManager.cs`, `ModelRendererSystem.cs`.

### [X] 25. Dead / redundant code
- `Matrix.Identity * worldPositionMatrix` — identity multiplication is a no-op.
- The commented `effect.DirectionalLight0.Enabled = true;` line is followed by `EnableDefaultLighting()`, which overrides all the manual light setup above it.
- **Fix**: Pick manual lights OR default lighting, not both.

### [X] 26. Bunny spawn loop is `z < 1`
- **File**: `engine/Game1.cs:57-65`
- Original note: only spawned one row of bunnies; probably intentional during testing.
- **Resolution**: Loop is now `x < 8` × `z < 8`, spawning an 8×8 grid.

---

## New issues introduced during the refactor (added 2026-06-14)

### [X] 27. Two `ComponentTransform` instances per entity (desync hazard)  — **Severity: Latent**
- **Files**: `engine/Classes/ECS_base_classes/Entity.cs:12`, `engine/Classes/ECS_base_classes/EntityContext.cs:28`
- **Problem**: `Entity` field-initializes `_transformComponent = new ComponentTransform()` (instance **A**), and `EntityContext.createEntity` then calls `addComponentFor(entity, new ComponentTransform())` which puts a *different* instance **B** into `_components`.
  - `entity.Transform` → A
  - `entity.getComponent<ComponentTransform>()` → B
  - Game1 writes via `bunnyEntity.Transform.position = ...` (A); `ModelRendererSystem` reads via `entity.Transform.position` (A). Works by luck because everyone uses the property.
  - Any code that ever reaches for the transform via the components dict (e.g. a generic inspector or future system) gets a stale, default-pose instance.
- **Fix**: Pick one storage location:
  - Drop the `_transformComponent` field and have `Transform` resolve via `getComponent<ComponentTransform>()` (cache the result). OR
  - Drop the `addComponentFor(entity, new ComponentTransform())` call in `createEntity` and instead register the field-initialized instance in `_components` (e.g. `addComponentFor(entity, entity.Transform)`).
- **Resolution**: Dropped the `_transformComponent` field, the `Transform` property, and `setTransform` from `Entity`. The `ComponentTransform` added by `createEntity → addComponentFor` is now the single source of truth, accessed via `entity.getComponent<ComponentTransform>()`. `Game1` writes positions through it (`Game1.cs:64`), `ModelRendererSystem` reads through it (`ModelRendererSystem.cs:42, 46`). One instance, no desync.
- **Followup bug fixed in same round**: `Entity.getComponent<T>()` had `if (component_type is T)` (testing the dictionary *key*, a `System.Type` instance), which always failed and made every transform read throw. Replaced with `_components.TryGetValue(typeof(T), …)` — O(1) lookup and structurally can't be confused with the key/value swap. The method still throws `ArgumentException` on miss (loud-failure contract intentionally preserved).
- **Followup cleanup**: `EntityContext.getComponentFor<T>` was deleted — it was a thin pass-through with no group-bookkeeping invariant to enforce on the read path, and its `if (component == null) return default;` branch was dead code. Callers go through `entity.getComponent<T>()` directly.

### [X] 28. `createEntity` ID generation is flaky  — **Severity: Latent**
- **File**: `engine/Classes/ECS_base_classes/EntityContext.cs:21-34`
- **Original code**: `long random_number = new System.Random().Next() + entities.Keys.ToList().Count;`
- **Problems**:
  - `new System.Random()` per call is seeded from the clock — collisions possible in same tick.
  - `entities.Keys.ToList()` allocates a `List<long>` just to read `.Count`.
  - `Random.Next()` returns a non-negative `int` (~2.1B max), so despite the field being `long`, only positive-int range is used.
  - If an ID happens to collide, `entities.Add(id, entity)` will throw at runtime — no retry loop.
- **Resolution**:
  - Single `_randomNumberGenerator` field on `EntityContext` (one `Random` instance for the context's lifetime).
  - Switched from `Next()` to `NextInt64()` — full non-negative `long` range (0 to `long.MaxValue`).
  - Allocating `.Keys.ToList()` deleted.
  - Wrapped the ID draw in a `do { newId = _randomNumberGenerator.NextInt64(); } while (entities.ContainsKey(newId));` loop. Collisions are astronomically unlikely in `long` range (≈ `N²/2^64`) but the retry costs nothing on the happy path and makes the `entities.Add` unconditionally safe.

### [X] 29. Stale `// TODO` comment in `Entity.cs`
- **File**: `engine/Classes/ECS_base_classes/Entity.cs:9` — `// TODO : make so entity has random generated id and adds itself to context`
- This is now done (random IDs are generated inside `EntityContext.createEntity`, and entities self-register via the context). Delete the comment.
- **Resolution**: Comment deleted.

---

## Suggested fix order (highest value first)

Remaining (as of 2026-06-14):

1. **#9** — Either use `lightIconTexture` for a light-source icon in the editor or drop the load.

Optional: add a `tryGetComponent<T>(out T)` on `Entity` if/when "maybe-component" reads are needed (currently every caller knows the component exists).

---

## Features not yet present (informational, not bugs)

- No physics / collision system.
- No audio.
- No scene serialization (save/load entities).
- No input manager (input read directly from `Keyboard.GetState()`/`Mouse.GetState()` in each class).
- No asset hot-reload.
- No gizmos (translate/rotate/scale handles in the editor).
- No undo/redo in the editor.
