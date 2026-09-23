# The first vertical slice, closed (2026-09-23)

The milestone TODO.md set out is met:

> A first-person player walks on one heightmap terrain sector with a few billboard-sprite trees. One
> billboard-sprite creature chases the player and attacks in melee. The player can swing a weapon,
> cast one data-defined fireball, pick up an item, then save and reload.

Every sentence of that runs, and three things arrived on top of it that the milestone did not ask for
and the engine turned out to be ready for: a spell that *travels*, a spell the **player invents**, and
a creature that **casts**.

This is the retrospective on the slice as a whole. Its two companions:

- [`engine-review-2026-09-23.md`](engine-review-2026-09-23.md) — the mid-slice architecture review
  (written after combat and items), which produced R13–R16, F31 and F27's promotion. Read that first
  for the "what is paying for itself / where is the debt" of the engine's *glue*.
- [`code-review-log.md`](code-review-log.md) — every bug worth remembering, one entry each.

---

## 1. What it cost

| | |
|---|---|
| Built over | 2026-09-22 and 2026-09-23 (the engine's solution split was 2026-09-21) |
| Roadmap items closed or started | R1–R5, R7–R11, R13–R16; F1–F3, F5–F7, F13, F18–F22, F27, F31, F32 |
| Still open from the milestone's own list | **F17** (entity I/O) and **F28–F30** (the editor) — neither was needed to close it; the console and hot reload stood in |
| Tests | 337, all headless, no window and no graphics device |

The slice did not need the editor, and that is the single most useful thing it proved about the plan.
Records with hot reload plus a console that can spawn, dump, cast, give, save and script input covered
every authoring need the slice had. The editor is worth building when *level geometry* arrives
(F16/F28), not before.

## 2. What the slice proved about the design

These are the four claims the earlier review made, re-checked now that magic and saves are on top of
them. All four held, and each one held *in a way its author had not specifically planned for*, which
is the interesting part.

**Records for every definition (R11).** By the end, a spell, its cooldown, the effect it applies, the
thing that flies, how the creature that throws it thinks, and the creature itself are all records in
the same pipeline, in files you can edit while the game runs. Then the spellmaker needed a record that
was **not in a file at all** — and the pipeline took it (`AddRuntime`), because "a record" was already
separate from "a file the records came from".

**Controller → `PawnIntent` → movement (16 §3.1).** Making a creature cast was `world.Cast(entity,
spell)` plus a yaw and a pitch, because the cast system reads the *intent*, not the input. The AI got
magic without the ability system learning that AI exists.

**Effects as the only way attributes change (16 §3.3).** A spell's mana cost is an effect. A cooldown
is an effect that grants a tag. A creature's mana regeneration is an infinite effect with a period,
authored in the Sandbox's own content with no engine change. Nothing anywhere subtracts a pool
directly, so saves store which effects are running and get all of it back for free.

**A simulation with no MonoGame dependency.** 337 headless tests, including three that build *two
engines in turn* — a save written by the first and loaded by the second, with the content changed in
between — which is the only way to prove that what a save stores survives a content update. The
two-*process* version of the same check is a pair of launch-argument runs against the real game. Both
are shapes a dedicated server needs, and both would have been untestable through a window.

## 3. The lesson the slice taught twice

**A rule a system applies is usually a rule something else needs to ask.**

It happened twice within a day, both times in the ability system, and both times the second caller
arrived so quickly that the extraction was not speculative:

1. **`AbilityPayload`** — "what this spell does, at this point". An instant cast resolves where the aim
   reaches; a projectile resolves where it arrives. Written twice, the two drift: one gets the burst
   rule or the friendly-fire rule and the other does not, and it surfaces as a bug report that spells
   behave differently depending on whether they travelled. (It very nearly did: the first projectile
   commit left `Self` targeting applying effects inline, bypassing the damage pipeline.)
2. **`AbilityRules`** — "may this be cast". `AbilitySystem` asks it to *start* a cast; an AI asks it to
   *decide whether to try*. Written twice, a creature walks into range and winds up a spell it cannot
   pay for, once per think, for ever. A HUD greying out a spell is the third caller, and it does not
   exist yet.

The generalisation for the docs: when a system's gate is expressed only *inside* the system, the only
way for anything else to find out the answer is to try and be refused. That is fine for a console
command and wrong for a UI or an AI. **R17** below is the audit that follows from it.

A second, smaller lesson: **derived data does not belong in a save.** A composed spell is an
`AbilityRecord`, and saving the record would have frozen a copy of content the player does not own —
so the save holds the player's *choices* and composes the record again on load. A rebalanced effect
then changes a player's old spell, which is what a player would expect and what a frozen copy could
never do. The same reasoning is why `GlobalTransform` and `Transform.LocalMatrix` are `[Transient]`.

## 4. Where the debt is now

The mid-slice review's finding — *"the data and simulation layers are in good shape; the glue between
systems is where the cost is accumulating"* — is no longer the live problem: R13–R16 fixed the glue.
What the last two features exposed instead:

### 1. Nothing the player can look at (13 → **F38**)
The spellmaker composes spells from the **console**. The inventory is a console command. The spellbook
is a console command. The HUD is four hand-placed rectangles. Decision **D8** (Gum vs Myra vs our own)
has been pending since the design pass, and the slice reached the end without it because every screen
it needed was a `Log.Info`. The next Daggerfall-like feature that is *worth playing* rather than
*worth testing* is a screen, and there is no roadmap item for one. That is now F38.

**Built the same day, both halves.** D8 was decided (our own, on `UiDraw`) and the spellbook and bag
are openable with `I` and `B`. What follows was the reasoning for splitting it in two, which is what
made the second half small.

 A screen is "what to show" (a simulation question) plus "how to
draw it" (decision D8, still open), and those separate cleanly: `Panel`/`PanelRow` and the
`GameplayPanels` builders now answer the first, with each row carrying whether it can be used and why
not — taken from the rule that would refuse the action. The console prints those panels, so it and a
future screen cannot drift apart, and a headless test can assert what a screen would show. See 13
"As built (the panel model)".

### 2. Gates that can only be answered by trying (16 → **R17**)
As §3 above. `AbilityRules` did it for casting; the same shape is still inside melee ("may I swing
now?"), interaction ("can I pick this up, and why not?") and items ("can I equip this?"). Each one is
a few lines to extract and each one is what a HUD or an AI will need. **Started 2026-09-23:**
`Items.CanEquip` is out of `Equip`, and `AbilityRules.Explain` gives a refusal words; melee and
interaction remain.

### 3. Two registration points per system (R15's price)
Splitting `GameplayModule` per feature was right, and it cost this: a new system is registered in its
module *and* the module is registered in the host's list. F21 forgot the second one once and the
symptom was a spell that silently did nothing. A `[Module]`-style discovery pass would remove it; it is
not worth building until a game other than the Sandbox exists.

### 4. Cues go nowhere (11, F4)
`CueTriggered` is raised by every cast and every impact and nothing listens, because audio is a later
phase. That is the design working — the simulation says *what happened* and never what it looks like —
but it means the slice is silent, and silence is the largest gap between "it runs" and "it is a game".

### 5. AI schedule selection is an if-chain with five branches
`ChooseSchedule` now reads: casting, melee-if-reachable, cast-if-possible, hold-if-a-spell-is-coming,
chase, idle. It is still clear, and it is one branch away from not being. Utility scoring (F22's
"left") is the replacement, and the tasks do not change when it lands — which was the point of the
HL1 shape.

### 6. Still no factions, so a firebug burns the watcher in front of it
Friendly fire is not a bug (F24), and the payload deliberately gathers everything and lets rules
decide. But "everything in the blast is a target" is currently the *whole* rule, and it is visible in
play.

## 5. What to build next, by leverage

1. **F38 game UI (a spellbook and an inventory screen)** — the slice is playable and unreadable. This
   is also the item that forces decision D8.
2. **F4 audio** — cues already exist at every interesting moment; this is wiring, not design.
3. **R6 + F14 large-world coordinates and streaming** — the one remaining *structural* item in the
   Daggerfall-like target. It touches transforms, physics and rendering, so it is cheaper now than
   after another ten features.
4. **F24 factions and dialogue** — turns creatures into a world rather than a shooting gallery, and
   gives the blast rule someone to ask.
5. **R12 (MonoGame 3.8.5 + dropping MGCB)** and **F9 skeletal animation** are the two big items after
   that, in that order, because the first is a day and the second is a phase.

---

## 6. The engine, counted

Every number here was read out of the code on 2026-09-23, not estimated. Production code only —
test fixtures define two more record types, and several more modules and systems, of their own.

| | Count | Notes |
|---|---|---|
| Record types (`[Record]`) | **17** | 16 in `Sage.Engine`, 1 in the Sandbox (`scene`). Every definition in the game is one of these |
| Console commands | **67** | across 15 files; the biggest groups are core (12), gameplay (10), input scripting (9) and entities/systems (8) |
| Cvars | **33** | 10 core, 5 host, 5 renderer, 4 gameplay, the rest one or two per subsystem |
| Input actions | **18** | 10 for play (`Move`, `Look`, `Jump`, `Crouch`, `Run`, `Attack`, `Use`, `Cast`, `Menu`, `ToggleConsole`) and 8 for screens (`MenuUp`/`MenuDown`/`MenuConfirm`/`MenuAlternate`/`MenuBack`, `Inventory`, `Spellbook`, `Spellmaker`) |
| Logical modules (`IModule`) | **11** | 8 in the engine, 1 client, 2 game (simulation + client halves) |
| Systems (`ISystem`) | **29** | across Fixed phases (Commands → Late) and Frame phases (FrameUpdate → Overlay) |
| Components | **23** | in `Sage.Engine`, all plain data |
| Game events (`[GameEvent]`) | **7** | `Damaged`, `Used`, `Said`, `AnimationEvent`, `AbilityCast`, `CastRefused`, `CueTriggered` |
| Prefab parts | **11** | 9 from engine modules, 1 from the game, 1 client-only — that last one the simulation also declares `Optional`, so a headless run skips it instead of warning |
| AI tasks | **5** | `Wait`, `FaceTarget`, `MoveToTarget`, `MeleeAttack`, `CastSpell` |
| Tests | **337** | in 30 test files, every one headless |

Two of these are worth watching rather than celebrating. **67 console commands** is the interface the
slice was authored through, and it is why F38 (a screen) kept not being urgent. **29 systems across 13
phases** (nine fixed, four frame) is where a parallel scheduler starts to be worth something, and it is
the reason R16's remaining half — per-system write declarations — is the prerequisite for it.
