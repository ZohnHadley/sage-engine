# Is it production ready? (2026-09-24)

**No.** This is the honest assessment, written the day after the first vertical slice closed, so that
the answer and its reasons live with the code rather than in a conversation.

> **Corrected 2026-09-25.** This review is cited from `ARCHITECTURE.md`, `TODO.md`, the handoff and
> two design docs as *the* account of what is missing, so rows that stop being true are worse here than
> anywhere else. Four of them had: **audio** (F4 shipped the same day this was written — the slice is
> not silent, and two systems read `CueTriggered`), **entity I/O** and **factions/quests** (F17 and F24
> shipped), and the **editor** (F28 shipped documents, an outliner and an editable inspector). Those
> rows are struck through below, with what is actually left in their place. The counts in the next
> paragraph are from 2026-09-24 and are superseded by the census at the end.

It is a **well-architected pre-alpha**: 17 roadmap items done, 14 partly done, **24 not started** (25 once this review adds R18). It
can build a small demo of a Daggerfall-like. It cannot yet build the Daggerfall-like.

The word means two different things and the answer differs:

| "Production ready" as… | Answer |
|---|---|
| *An engine other people build games on* (a Godot/Unity substitute) | **No, and not soon.** No editor, no packaging, no platform testing, no asset server, no mod loading, one game using it. |
| *A foundation for this game* | **Not yet, but the shape is right.** The architecture is holding; the missing pieces are features, not rewrites — with the exceptions in §4. |

---

## 1. What is genuinely strong

Three things, and they are the ones that usually decide whether a hobby engine survives.

**The boundaries are real and the compiler enforces them.** `Sage.Engine` has no MonoGame reference,
so 347 tests run with no window and no graphics device, and the property cannot rot without the build
failing. That is also exactly the shape a dedicated server needs, which is why "multiplayer later"
stayed cheap.

**The data pipeline.** One record pipeline — namespaced ids, `base` inheritance, per-field patch
merge, validation, hot reload — carries items, spells, materials, AI profiles, prefabs, input maps and
scenes. Editing the game while it runs is the normal way to work here, and adding a creature is a JSON
edit. It also absorbed two things it was not designed for without changing shape: prefabs (F31) and
records made at run time (F21's spellmaker).

**One way to do each thing.** Effects are the only way an attribute changes. One damage pipeline
serves swords, spells and fire. One set of cast rules is applied by the system, consulted by the AI
and shown by the UI. That consistency is why the spellmaker, AI casting and three screens each took
about a day: the answer already existed.

The supporting habits are real too: a dated review after each big step, "as built" sections in every
design doc, and a bug log with the reason each bug happened.

## 2. What is missing before a game ships

| Missing | What it costs today |
|---|---|
| ~~**Audio (F4)**~~ *(done 2026-09-24)* | ~~The game is **silent**.~~ Built: mixer, buses, cues, weather loops. **Left:** music and crossfades, OGG, reverb and occlusion, footsteps from animation events, screens making any sound, and `effect` records raising their cues. |
| **Large-world coordinates and streaming (R6, F14)** | One 1024 m sector. The premise of the target game — a world you travel across — is unbuilt. |
| ~~**Editor (F28)**~~ *(done 2026-09-24)* / **F29–F30** | ~~Levels are hand-written JSON.~~ Built: placement documents, outliner, editable inspector, and a Shipping build with no editor in it. **Left:** gizmos and picking (F29), undo/redo (F30), placing a prefab, per-entity overrides, play-in-editor. Editing runs against the live world, so `pause 1` first. |
| **Pathfinding (F23)** | Creatures steer with raycasts; real interiors will trap them. |
| **Skeletal animation (F9–F12)** | Acceptable for billboards, blocking for the HL1-, Lugaru- and Warband-like targets. |
| **Asset server (R12, 05 §3.3)** | Synchronous loads, no ref counting, no scopes, no budget. MGCB is still in the pipeline. |
| **Saves past v1 (F27)** | No maps or sectors, no tombstones, no upgraders. The format has never survived a migration. |
| **Mod loading (F37)** | Designed in doc 17, unbuilt — so "data + trusted C# mods" is a plan, not a feature. |
| ~~**Entity I/O (F17), factions and quests (F24)**~~ *(done 2026-09-24)* | ~~No triggers, no dialogue, no reason to go anywhere.~~ Built: outputs wired to inputs in map data and checked at load, movers, trigger volumes, factions, reputation, dialogue and quests. **Left:** `@group` targets, an editor link view, **kinematic movers** — see the defect below. |

**Added 2026-09-25, from a sweep of the code, the docs and the roadmap:**

| Missing | What it costs today |
|---|---|
| **A closing door goes through you, and you never get out** (F17 + F7) | Three pieces, each filed as a minor leftover, one reachable bug. A `Mover` calls `PhysicsSpace.MoveStatic`, which sets a pose and rebuilds bounds — no sweep, no push. `OnHitAtZeroT` is an **empty method**, so a sweep that starts already overlapping is discarded. And there is no depenetration pass. Stand in the hut doorway and wait five seconds for `closeAfter`: the door shuts through you, and from then on every sweep reports clear while you are inside a hull. Skin width is the only thing keeping a capsule out of geometry, and it only works if you never got in. F7 is marked **done**. |
| **Nothing spawned at run time survives a save** (F27) | `World.MakePersistent` is called only by tests. No spawn path — prefabs, drops, pickups, projectiles, map placement — calls it. |
| **Extension points nothing calls** | `PhysicsSpace.SetVelocity` (so knockback or an explosion would silently do nothing), `AudioMixer.SetBusVolume` (bus volumes are stuck at defaults — a volume slider has nothing to write to), `CVarRegistry.Complete` (console Tab-completion, built and never wired), `World.RemoveSystem`, `GameplayTags.HasAll`/`HasAny`. `CommandLatch.SetView` was one of these until 2026-09-25. |
| **Two lists of gameplay modules** *(fixed 2026-09-25)* | The host hand-wrote its own; `AddGameplay` held the other; tests used the second and the game used the first. `LightsModule` went into one and not the other and point lights were dead in the shipped build for a day, with an `ERROR` on every boot that nobody read. One list now, guarded by `ModuleSetTests`. |
| **Docs that contradict the code or each other** | 25 cross-doc contradictions found on 2026-09-25. Two were mapper-facing and are fixed: doc 16 named the interaction output `OnUsed` (it is `OnUse`), and docs 04 and 10 named `TriggerEntered`/`TriggerExited` game events, which **do not exist in the codebase**. `check_docs` catches neither class, because its vocabulary check covers command and cvar names and not output or event names. |
| **Streaming keeps what a game placed** | Unload destroys only `SectorOwned` entities, and nothing goes dormant: a prop or NPC a game puts in the world is never unloaded, and an NPC a few sectors out stands over a heightfield whose collision has gone. |
| **A missing record is a silent default** | `RecordStore.Get<T>` logs once per call site and returns a default-constructed record, so a typo'd id becomes zero damage, a silent sound or a default material, with the complaint deduplicated away. |
| Packaging, localisation, crash telemetry, non-Windows runs | None. Nothing has ever been built for another machine. |
| The source generator (09 §3.2) | Records and saves are reflection-based, which is documented as temporary in both. |

## 3. What has never been measured

The tests prove correctness, not fitness. Specifically:

- **Scale is untested.** The largest test creates **200 entities**; the Sandbox scene places 25. A
  Daggerfall-like needs thousands, most of them dormant, and nothing here has met that.
  **Answered 2026-09-24 (R18):** 2,201 entities cost 0.76 ms of a 16.67 ms tick and 1.08 ms a frame,
  and the measurement found two allocations no correctness test could see — 323 KB per tick became 40
  bytes. See [`scale-2026-09-24.md`](scale-2026-09-24.md).
- **Frame time has never been profiled** against a real scene. The profiler and `stat frame` exist and
  have never been pointed at anything demanding.
- **One platform.** Windows 10, one GPU. DesktopGL should run elsewhere; nobody has tried.
- **One resolution class.** The UI is raw pixels with no scaling, so a 4K screen draws a small box.

What *is* measured is worth keeping: the fixed loop allocates **zero bytes** in steady state, asserted
by a test, and the same discipline is applied to the HUD and to screens.

## 4. The three risks that could force rework

These are the ones where waiting makes the bill larger, rather than merely later.

1. **R6 (large-world coordinates) is still open.** Every feature added since the decision assumes
   world-space floats: transforms, physics bodies, the renderer's camera-relative path, the save
   format, the AI's distance checks. It was always meant to land early and has not. **This is the
   single most expensive item on the board, and it grows.**

   **Resolved 2026-09-24 (R6 + F14 v1).** Built the day this was written, and the estimate held: the
   change was wide rather than deep. What it touched is exactly the list above — and the save format
   was the one that would have been missed, because origin-space positions with no origin recorded
   look perfectly valid until a save made far from home loads in the wrong sector. See 14
   "As built (rings and rebasing)".
2. **The source generator is assumed by two subsystems that shipped without it.** Records and saves
   both say "reflection for now, generated later", and the generator changes how every component and
   record is *declared*. The longer both run on reflection, the more declarations there are to change.
3. **The multiplayer readiness rules are unproven.** Eight rules are followed carefully (fixed tick,
   data-only components, no MonoGame in the simulation, `PlayerCommand`, rules in `GameRules`). Not
   one of them has been tested by an actual network layer, so they are a well-argued hypothesis.

A smaller one worth naming: **Friflo.Engine.ECS** is a young dependency with a small community, and
the `World` facade is deliberately thin — queries are Friflo's own types in system code, so replacing
it would touch every system.

## 5. What "ready enough to make the game" would take

In the order that removes the most risk per week:

1. **R6 + F14** — large-world coordinates, origin rebasing, streaming rings. Structural, and the cost
   only rises.
2. **F4 audio** — the events already exist at every interesting moment; this is wiring, and it is the
   largest single step from "it runs" to "it is a game".
3. **F23 pathfinding** and **F24 factions/dialogue/quests** — creatures that can reach you and a
   reason to be somewhere.
4. **R12** (MonoGame 3.8.5, drop MGCB, runtime asset loaders) — a day or two, and it closes the gap
   between decision D10 and the code.
5. **The generator (09 §3.2)**, once the component set stops moving.
6. **A scale test**: one sector, two thousand entities, most dormant, profiled. Until that exists,
   every performance claim in these docs is an assumption.

The editor (F28–F30) can wait until level *geometry* arrives (F16). The slice proved that records plus
a console cover authoring for a long time — see [`vertical-slice-2026-09-23.md`](vertical-slice-2026-09-23.md) §1.

---

*Counts verified against the code on 2026-09-24: 24 open / 14 partial / 17 done roadmap items before
this review added R18, 347 tests, 200 entities in the largest test, 25 placements in the Sandbox
scene.*
