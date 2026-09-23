# Engine review — what building the slice taught us (2026-09-23)

An architecture review written from the inside: this is what it was actually like to build F18
(attributes and effects), F22 (AI), F20 (combat), F19 (items and interaction), F5's debug draw and
the v1 HUD on top of this engine, over 2026-09-22/23. It is deliberately about the **engine**, not
the Sandbox — where a point is really about the game layer it says so.

The bug-by-bug record lives in [`code-review-log.md`](code-review-log.md). This is the level above:
the things that are not bugs, that no test will fail on, and that get more expensive per feature.

Items are referenced from `TODO.md` by their R/F ids.

---

## What is paying for itself

**Records with patch, inheritance and hot reload (R11).** Adding an item, an attack, a creature or a
sprite sheet is a JSON edit, live, while the game runs. `rec_get` naming the file that set each field
— `damageType <- engine:data/combat.json (via base sage:default_attack)` — found a real bug (#56) in
seconds. Patching rather than replacing let the Sandbox give the *engine's* bare-handed attack a
first-person look without owning the record.

**Controller → `PawnIntent` → everything (16 §3.1).** Making creatures fight was one line:
`intent.Pressed |= Attack`. Player melee and creature melee are the same code path, so a creature
can miss, and a weapon changes both identically. This fell out of the design rather than being
retrofitted, which is the strongest evidence for it.

**Effects as the only way attributes change (16 §3.3).** Every time something new had to change a
number — the `god` cheat, a shield's armour, damage with a magnitude, a creature's tough hide — the
answer already existed. One path also means one place to make saves and multiplayer correct later.

**A simulation with no MonoGame dependency.** 212 tests, no window, including combat, AI, items and
the character controller. Features arrive with tests because testing them is not a chore.

---

## Where the debt is

The pattern: the **data and simulation layers are in good shape; the glue between systems is where
the cost is accumulating**. None of it is hard to fix and all of it gets worse per feature.

### 1. Four hand-rolled event queues, and no bus (04 → **R13**)
`CombatEvents`, `InteractionEvents`, `AnimationEvents` and `MessageLog` are each a bespoke world
resource with its own `Clear()` and its own lifetime rule, and each rule lives in a comment.
Combat's says, in effect, *"anything else that damages must run after this system, or the clear
moves elsewhere"* — a constraint the compiler cannot see and a test will not catch. Doc 04 designed
the fix (typed events per schedule, per-reader cursors) and it is unbuilt, so every new feature
invents another queue. Abilities, cues and quests are all queue-shaped.

**Resolved 2026-09-23 (R13).** `GameEvents` with per-reader cursors; all four retired. Building it
corrected this item in one way worth recording: **two of the four were not event queues at all.**
`InteractionEvents.Hovered` ("what is in reach right now") is state a HUD must be able to *ask*, not
a change to be read once, and `MessageLog` is a display buffer aged in display time. They became an
`InteractionState` resource and a `Said` event the log reads. The lesson generalises — "several
features grew their own container" is not the same as "several features grew their own queue", and
the fix for the first is not always the bus. Only `CombatEvents` and `AnimationEvents` were queues.

**Worse than it looks:** the lifetimes genuinely differ (`AnimationEvents` is refilled in the
Animation phase and read a tick later by combat; `CombatEvents` is cleared at the start of Gameplay;
`MessageLog` ages per frame). That variety is exactly what a bus with cursors exists to normalise.

### 2. Structural changes in query loops are taxed by hand (03 → **R14**)
Melee hits (`Combat._pending`), interactions (`Items._pending`) and deaths (`Effects._died`) each
keep a private list and a post-loop pass, because touching another entity's components inside a
query throws. That is three copies of one pattern — on top of the structural changes the `World`
itself already buffers — each an opportunity for the ordering bugs the log already records (#48,
and the F18 `StructuralChangeException` before it). The `CommandBuffer` exists but is not ergonomic for
"do this, with this data, after the loop". A `Defer(...)` helper, or command-buffer support for the
gameplay cases, removes a recurring class of mistake.

### 3. No entity templates, so every game invents spawning (05/09 → **F31**, promoted)
The Sandbox invented `spawn` records; when the Daggerfall importer needed to place things it had to
generate *Sandbox-specific* JSON, because the engine has no opinion about "a thing you can place".
Prefabs are the designed answer and they are sitting in Phase 6. The longer they wait, the more
games write their own placement layer and the more tools are written against it. A prefab record
would also give the engine one obvious entry point (`world.Spawn(prefab, at)`) where today a creature
needs eight calls made in the right order (§8).

### 4. Nothing has pressure-tested the component shapes for saving (09 → **F27**, earlier)
`Inventory.Items` is a `List<ItemStack>`; `ActiveEffects.Effects` is a `List<ActiveEffect>`;
`AIState.Target` and `ActiveEffect.Source` are `Entity` handles; `Melee` holds record ids. Entity
references need stable ids across a save, and lists need ownership and versioning rules. Every
feature adds more of these, and none of them have been tested against a serializer because there
isn't one. Either build F27 sooner than the roadmap says, or write the contract now (which fields
are saved, how an `Entity` is persisted, how a list versions) so that new components are born
compliant.

### 5. `GameplayModule` is becoming a god-object (01 §3.1 → **R15**)
It registers nine record types, three cvars, seven console commands and six input actions, and
installs nine systems across six phases. The two-level module system was designed for exactly this
split — Combat, Items, AI and Character as logical modules inside `Sage.Engine` — and the split
hasn't happened, so adding a feature means editing the module, the systems, the cvar registrations
and the commands in four places. Related: cvars belong to the module that owns them (review #57),
which is easier to keep true when the module is the size of one feature.

### 6. A game assembly cannot be tested headlessly (01 §3.1 → **R15**)
`games/Sandbox` mixes simulation (rules, combat log, AI toys, spawning) with client code (the HUD
needs `Color`, `Rectangle`, `Texture2D`), so none of the game's own simulation can be tested without
MonoGame. The `Sage.Framework` / `Sage.Framework.Client` split in 01 should be the pattern for games
too: `Sandbox` + `Sandbox.Client`. The engine can make that the documented shape before games get
big enough for it to hurt.

### 7. Phase guarantees live in prose (03 §3.5 → **R16**)
`before:`/`after:` ordering works, but *what a phase promises* — "PawnIntent is final after Commands",
"transforms are settled after PostPhysics" — exists only in comments. Review #48 (AI intent acted on
a tick late) was precisely a violated prose guarantee, and it survived a docs audit because the docs
agreed with the comment, not with the code. Dev-build assertions (a phase declaring which components
it may write, checked when `SAGE_DEV` is on) would turn that class into a failing test.

### 8. Discoverability of the gameplay API
Making a creature today is `world.Create`, `Add(MeshRenderer)`, `AddCharacter`, `Add(AIState)`,
`Add(Melee.With(...))`, `AddInventory`, `AddAttributes`, `Effects.Apply` — eight calls in a specific
order, spread over five static classes, with nothing to find them from. Each call is good in
isolation; there is no map. Prefabs (#3) subsume most of it; until then, one worked example in the
docs is cheap.

---

## What I would build next, by leverage

1. **The event bus (R13)** — before another feature invents a sixth queue.
2. **Prefabs (F31)** — before another tool generates game-specific placement JSON. `ent_spawn` falls
   out of it for free.
3. **The serialization contract, then saves (F27)** — while the component set is still small enough
   to fix cheaply.
4. **Feature modules and the game sim/client split (R15)**, which is mostly moving code.
5. **Phase contracts with dev assertions (R16)**, which is small and prevents a whole bug class.

The tooling items already agreed (entity inspection and spawning, input scripting for automated
checks, asset hot reload) stay worth doing early: each one makes everything after it faster, and the
first of them merges neatly into prefabs.
