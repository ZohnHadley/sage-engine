# The Sage editor

This is for people who build levels: placing things, tuning them, wiring a plate to a door, and trying it.
Sage's editor is a **mode of the game's own executable**: it loads your game exactly as a run does, the same
content, plugins and rules, and shows the level without playing it. You place prefabs from a palette, move
and turn them, change a field for one of them, wire them together, press Play to try it and Stop to come
back, and save. **You never edit JSON to do it**, and what you save is the file your game loads.

If you are making the game rather than its levels, read [MAKING_A_GAME.md](MAKING_A_GAME.md) first: prefabs,
scenes and placements are explained there (§3 to §5), and the editor builds out of exactly those. The design
behind this page is [design/15-editor.md](design/15-editor.md) (§10e to §10z), and the worked example is
`tests/games/editor` (§10).

---

## 1. Opening the editor

```bash
Sage.Host -game <folder> -edit                       # the game's start scene and the first document it places
Sage.Host -game <folder> -edit <scene>               # that scene, and its first placements document
Sage.Host -game <folder> -edit <placements>          # that document, in the scene that names it
```

From this repository, with the dev host:

```bash
dotnet run --project src/Sage.Host -c Development -- -game tests/games/editor -edit level
dotnet run --project src/Sage.Host -c Development -- -game games/Sandbox -edit yard
dotnet run --project games/Hello -c Development -- -edit       # a Sage.Sdk game passes -game itself
```

- **The editor is in Debug and Development builds only.** A Shipping build has no editor: `-edit` is
  ignored with a warning and the game runs.
- A name without a namespace is looked up in every namespace. A name that is neither a scene nor a
  placements document is an error in the log, and the start scene opens instead.
- A scene that places no document opens with no document: **File → New** (`doc_new`) starts one, and
  `doc_level <name>` starts a whole new level (§6).
- **The level is shown, not played.** Nothing walks, falls, thinks or counts the hours: no Fixed system
  runs, and there is no player. Physics is mirrored so clicks can find things, but a crate placed in mid-air
  hangs there until you play.
- Escape does not quit the editor (it stops a play, §7). **File → Exit**, or `quit` at the console, does.

### For modders: the editor beside a shipped game

A game packaged with `sage package <game> --out <dir> --editor` (or `Sage.Sdk`'s
`-p:SagePackageEditor=true`) has a Development host in an `editor/` folder beside its Shipping one, and two
launchers, `edit.sh` and `edit.cmd`, which open the editor on that game (issue #375). The Shipping host at
the top of the package is unchanged. A modder runs

```bash
./edit.sh [level] +ed_mod <their mod>
```

and everything they save goes into their mod's folder (§8, "Saving into a mod"). `--editor-host <dir>`
picks the host to put in `editor/`; a Shipping host, which has no editor, is refused.

## 2. What a level is

A level is a **scene** (`"type": "scene"`: the ground, the player's start, the sky) that names one or more
**placements documents** (`"type": "placements"`): lists of prefabs, each with a position, a turn (`yaw`
about Y, and `pitch` and `roll` when it is tipped), a `scale`, a name, and optionally the fields it changes
from its prefab (**overrides**) and its wires (**outputs**). The editor can hold several documents open, each
in a tab (§3, "Document tabs"); the active tab's is "the document" below. The status bar says which, and
whether it is saved.

```jsonc
{ "prefab": "editor:door", "at": [0, 1.5, -4], "yaw": 180, "name": "door", "id": "door",
  "overrides": { "parts": { "mover": { "seconds": 0.5 } } } }
```

That is what the editor writes for a door you placed, turned and made faster. You will not have to read it,
but it is good to know there is nothing else: no editor-only file, no project database.

## 3. The layout

The screen is the level, seen through a free camera, with panels docked around it. Panels can be dragged,
tabbed and re-docked; **View → Reset layout** (`ed_layout`) puts them back.

| Where | Panel | What it is for |
|---|---|---|
| Top | **Document tabs** | One tab per open placements document, with an unsaved dot, a close button and **+** (issue #375; below) |
| Middle | **Viewport** | The level itself. Click to select; drag a gizmo to move, turn or scale; a small toolbar at its top picks the gizmo, the space and the snapping, and holds the **Play** button |
| Left | **Outliner** | Every entity in the edit world, by name. A row selects exactly that entity, Ctrl+click adds it; Delete on a placed one deletes its placement |
| Left (tab) | **Palette** | The prefabs you can place, by namespace, with a search box |
| Left (tab) | **Terrain** | Sculpt, paint and water for the terrain (issue #372; below) |
| Left (tab) | **Brushes** | Boxes, wedges and cylinders to block out rooms, and their face materials (issue #61; below) |
| Right | **Inspector** | The selection: its placement (position, yaw, pitch, roll, scale, name, frame) and every field of its components and parts, lists and nested objects included |
| Right (tab) | **Records** | Any record, by type: a form and the raw JSON, shown in the game as you edit, saved into the file it came from |
| Right (tab) | **I/O** | The selection's wires: what it fires, at whom, and adding new ones |
| Right (tab) | **Conditions** | A form for a wire's `requires`, or any conditions or actions field of a record (issue #370; below) |
| Right (tab) | **Animation** | A preview of any `anim_graph` or a model's clips: scrub, drive params and states, see events and sockets (issue #362; below) |
| Right (tab) | **AI Graph** | The state machine, schedule or routine open in Records, as a tree, edited, with the states an agent is in lit (issue #369; below) |
| Bottom | **Log** | The log, filtered by level and category |
| Bottom (tab) | **Problems** | What is wrong with the content and the open document, by file |
| Bottom (tab) | **Assets** | Every model, texture, sound, map, font and shader, by kind and mount, with previews (issue #366; below) |
| Bottom (tab) | **Console** | Every editor action as a typed command (§9) |
| Floating | **Audio** | A meter for each bus (its voices, the level and the peak) and a list of every `sound` record with a **Play** button (issue #336; below) |
| Bottom edge | **Status bar** | The document and whether it is saved, the selection, the world, the camera, and the problem count |

The menus: **File** (New, Open, Save, Close, Exit), **Edit** (Undo and Redo, each saying what it will undo),
**View** (Free camera, Viewport, Reset layout). `ed_viewport 1` (View → Viewport) opens a second view of
the free camera in a window of its own. `ed_panel <title>` brings any panel to the front, and `ed_panel all`
shows each in turn, which is how CI draws every panel in the real host (issue #371).

### Document tabs

Each open placements document has a tab across the top, in an edit world of its own (placed with the scene
that names the document) and with its own undo history (issue #375). The active tab is the one the menus,
the tools and `doc_*`, `ed_undo` and `ed_redo` act on. **+** opens a document in a new tab, starts a new
one, or picks where saves go ("Save into", §8). Opening a document that is already open switches to its
tab. A tab with unsaved changes shows a dot, and closing it is refused until it is saved or closed with
`ed_tab_close !`. The free camera remembers where it was in each tab, and switching tabs while playing stops
the play first. Records keep their own panel; tabs are placements documents only.

### The Audio panel

**Audio** floats: it has no dock slot, so drag it where you like. The bars are the
buses (`Master` is the total of everything playing), each with its voice count and its peak. Pick a sound and press
**Play** to hear it, in 2D at the listener, whatever its cooldown or instance limit says. The panel remembers the
sound it played: save a changed `.wav` or `.ogg` under a folder mount and the sound plays again, so you can edit a
file and hear it without leaving the editor. The same preview is the `snd_play <sound>` command. `snd_debug 1`
puts the voices (loudest first, with their distances and gains) and the bus levels in the stat overlay. The numbers
are `AudioMonitor`'s; the panel only draws them.

### The Animation window

**Animation** (issue #362) is a tab beside the inspector, and comes forward when something is opened in it. Pick
an `anim_graph` — it opens on the model the first prefab whose `animator` names it plays it on, else on that
prefab's `skinned_mesh` — or a model's clips, and press **Open**. The skeleton is drawn as a stick figure (bones,
joints and the model's sockets, with labels); drag it to turn it. Nothing in the edited level changes: the graph
runs in a world of its own.

- **Clip:** any clip of the model, scrubbed with the slider or by clicking its timeline, looping or not. Its
  events (from `anim_events` records) are markers on the timeline, and a button per event jumps to it.
- **Graph:** the real animator. Every param the graph declares is a control (a float drags, a bool is a
  checkbox, a trigger is a button), and every state of every layer is a button that puts the layer there. Each
  layer shows its clip's timeline with markers and a playhead, the blend's weights, what it is fading from, and
  the events it raised. Play, pause, speed, a single step and restart run it. A param the graph fills from the
  body (`from: Speed`) is the preview's to set, since nothing moves.

The same preview is the `anim_preview` commands (§9), which is how CI drives it.

### The viewport and gizmos

- **Select** by clicking. A click on part of a placed prefab (a child) selects the placed thing. Things with
  no collider are found by a small sphere at their origin.
- **Select several** with Ctrl+click (here or in the outliner), which adds or takes away one, or by
  dragging a box over empty picture, which selects what is drawn inside it (Ctrl or Shift adds to the
  selection). The last one picked is the one the inspector and the gizmo show (issue #367).
- **Move** with the move gizmo (G): drag an arrow to move along one axis, a square to move in a plane.
  **Turn** with the rotate gizmo (R): three rings turn about X, Y and Z. **Scale** with the scale gizmo (T):
  drag an axis box to stretch along it, or the centre to scale evenly. A whole drag is one undo step, and
  with several selected they all move, turn or scale together, about the last one picked.
- **World or local space** (L, or the toolbar): in local space the move and rotate gizmos follow the
  selection's own axes. Scaling is always along its own axes.
- **Snapping** is on by default: moves snap to a 0.5 m grid, turns to 15°, scaling to steps of 0.1. The
  toolbar or `ed_snap`, `ed_grid`, `ed_angle`, `ed_scalestep` change it.
- **Wires** are drawn as lines between wired things, dim, with an arrowhead. The selection's **link
  view** is drawn bright and labelled with the output and input: the wires that leave it in yellow, the
  wires that reach it in green (from placements and map entities alike), a wire to a group fanned out to
  every member, and a wire whose target is not in the world as a short red stub with the name it waits
  for (issue #276).
- The editor's own cameras are never picked.

### The palette

Type in the search box to narrow the list (every word must appear in the prefab's id). **Click a prefab to
arm it**, then click in the viewport: it is placed on the first surface under the cursor (or the ground
plane), selected, and the palette stays armed so you can place another. Click the prefab again, or press
Escape, to stop. Each placement gets a unique name (`crate`, `crate_2`, ...). Abstract prefabs, templates
that are only ever another prefab's `base`, are not offered.

### The inspector and overrides

The inspector shows the selection's placement as a form (at, yaw, pitch, roll, scale, name, frame), then a
group per component and part with every field, its value, its range and unit, and a tooltip.

- **Changing a field changes this placement only.** It is written as an **override** in the document, not
  into the prefab: every other door still takes two seconds. Overridden fields are marked; **revert**
  takes the override away, back to the prefab's value.
- **Hover a field to see where its value came from**: "this placement", the prefab's file and line, a base
  prefab's, a mod's patch, or "default".
- A part's options (a `body`'s size, a `mover`'s seconds) are edited as the part's, and the component the
  part builds from them is shown read-only beside it.
- **Lists, maps and nested objects** are tree nodes with a row per element and field (issue #368). Each
  element has up and down arrows and `x`, and `+ add` at the end adds one (a map asks for its key). An edit
  inside a field overrides the whole field, so **revert** on any row inside it takes the field back to the
  prefab's.
- **Add component...** at the bottom adds a component the prefab does not have to this placement alone (a
  light on one crate). Its group says "added by this placement", its fields are editable, and **remove** in
  its header takes it off again. The prefab's own components cannot be removed.
- **revert all** takes every override of the placement away at once (issue #372).
- An asset dragged from the **Assets** panel onto a field that names an asset sets it.
- An entity the document did not place (one the scene placed, or a prefab's child) is shown read-only:
  there is nowhere to save an edit to it.

### Records

The Records tab opens any record (a prefab, an item, a sound) as a form over its JSON, with every field's
range and where it was last set. Edits have their own undo (Ctrl+Z / Ctrl+Shift+Z / Ctrl+S while the panel
has the focus) and are saved into the file the record came from, or as a patch (§8).

**Edits show in the game as you make them** (issue #366): each edit, undo and redo of the open record is
applied to the running world before you save, so a texture picked for a material is on the wall at once.
Closing the record without saving puts back what the files say. `ed_rec_live 0` turns this off.

A `material` record has a **Material preview** strip: its albedo, normal, specular, emissive and
environment maps and its effect, each a slot an asset from the **Assets** panel can be dropped on. A field
that holds conditions, actions or another vocabulary's entries has a `<field>...` button that opens the
**Conditions** form on it, even before the field is written.

### I/O: wiring

Select the thing that fires (a pressure plate), open **I/O**, and add a wire: choose an **output**
(`OnStartTouch`, typed or picked from the suggestions), press **pick target** and click the target in the
viewport or the outliner, choose one of the **inputs** that target takes (a door's `Open`, `Close`,
`Toggle`), then a delay and a value if you want them. Only inputs the target actually takes are offered,
and a target needs a name: the panel offers to give it one. Each wire's delay and value can be edited in the
list. A wire's **requires** button (`requires *` when it has one) opens its condition in the
**Conditions** form.

A target can also be a **group**: type `@lamps` (every entity in the group `lamps`, given by a prefab's
`sage:io_group` component or a map's `group` key), `@class:torch` or `@tag:ns:id`. The list says whom a
group reaches now, and the panel lists the wires that **reach** the selection below its own. What was
actually delivered is `io_history [name]` in the console (the last 256 inputs, in the play world while
playing).

### Problems and the log

**Problems** lists errors and warnings from loading your content (with file and line), conflicts between
mods, and what is wrong with the open document as you edit it: a placement whose prefab is gone, a wire to
a target that is not in the document, two placements with one name or one id. Click a row to select the
placement or open the record. The count is in the status bar.

Outside the editor, the content half of the same list is the console's `problems` (in every build, Shipping
included), and a dev build's overlay shows a red or yellow count in the top-left corner while there are any
(`ui_problems 0` hides it; issue #301). The document's own problems are only here. In `-edit` mode the corner count
shows only while playing; otherwise the status bar has it.

**Log** is the log, at or above a level, with categories hidden or shown one at a time. A play session
logs into a log of its own, and every line also reaches the editor's log and its file (issue #49).

### Assets

The **Assets** panel (issue #366) lists every model, texture, sound, map, font and shader the game can load,
filtered by kind, by mount (a mod by its id) and by search words, with a thumbnail for each texture. Select
one to see its mount, which mounts it shadows, its size and a large preview; **Play** plays a sound.

- **Drag it** onto an inspector field that names an asset (an override, undone with `ed_undo`), onto a
  Records field that names one, or onto a slot of a material's preview strip.
- **Drag it into the viewport**, or press **Place in the document**, to place it: the first prefab whose
  record names the asset, or else the model on its own (`sage:static_mesh` with the model as its mesh).
- **Find references** lists every content file and line that names it.
- **Rename** moves one of your game's assets (and its cooked file) and rewrites every file of your game
  that names it. It is refused for the engine's, a kit's or a mod's assets, when another game or mod names
  it, and while an open document or record has unsaved edits. It is not on an undo history: rename it back
  to undo it.

### Blocking out with brushes

Rooms, walls, floors and ramps can be drawn in the editor (issue #61). A **brush** is a placement of one of
three engine prefabs, `sage:brush` (a box), `sage:wedge` (a ramp rising to its north, -Z, side) and
`sage:cylinder`, so you move, turn, select, duplicate, delete, undo and save brushes like anything else.
Scaling one resizes it. A brush is solid to walk on and into and is drawn with its materials; its `at` is
the middle of its floor.

The **Brushes** panel arms a shape: click in the viewport to stand one on the surface under the cursor,
snapped to the grid, at the size and in the material set in the panel. With a material chosen, **Paint
faces with this material** makes a click paint the face under the cursor (Shift+click the whole brush).
The selected brush's shape, sides, size and per-face materials (top, bottom, north, south, east, west, and a
cylinder's side) are edited in the panel, each one undo step. **Show grid** draws the grid on the
selection's floor. Textures are laid in world space, `textureScale` metres to a repeat, so neighbouring
brushes continue each other's texture. `sage validate` checks brushes: sizes above zero, 3 to 64 sides,
materials that exist, and no material for a face the shape does not have.

TrenchBroom `.map` levels still load as before (design/15 §10a); there is no export to one.

### Terrain

The **Terrain** panel (issue #372) sculpts the ground of a terrain world: pick a tool (Raise, Lower, Smooth,
Flatten to a height, or Paint a layer of the `terrain_material`), a radius and a strength, and drag in the
viewport; a whole drag is one stroke and one undo step. The panel also sets the water level of the sector
the camera is in, and has its own undo, redo and save, apart from the document's. A save writes a
`.sterrain` file beside your content, which the `terrain` record's `sculpt` names (or
`terrain/<terrain>.sterrain` by default), and the game streams the sculpted ground with no seams at sector
edges. Paint shows only with a `terrain_material`; water is a volume you can swim in, not yet drawn.

### The AI Graph

**AI Graph** (issue #369) shows the record open in **Records** when it is a `state_machine` (a tree of
states, nested and parallel, with their transitions; the machine's own transitions are under "(any)"), an
`ai_schedule` (its tasks), a `routine` (its entries) or a `behaviour_tree` (see below). Add, remove, move, reparent and rename nodes; each
edit is a step in the record's own undo, and a renamed state is renamed everywhere a transition or
`initial` names it. While playing, the states the chosen agent is in, and the task it is on, are lit (a
picker chooses the agent). 
#### A behaviour-tree view

A `behaviour_tree` record (issue #387) is drawn as the tree of its nodes, one line each, such as
`fight: MoveToTarget  distance 2  [when SeeEnemy]`: a task with its arguments, a composite
(`sequence`, `selector`, `utility`), a decorator (`invert`, `succeed`, `repeat`, `cooldown`), a blackboard
`set` or `check`, and the guard a node carries. While playing, the node the chosen agent is running and its
ancestors are lit. Add a node (into a composite), remove one, move it among its siblings and rename it (a
task node's rename changes its task; any other gets a `name`); each is one step in the record's own undo.
Reparenting is refused.

### Conditions

**Conditions** (issue #370) is a form for a condition or action without JSON. Open it from a wire's
**requires** button in **I/O**, or a `<field>...` button in **Records** (a dialogue option's `conditions...`
or `actions...`). Each entry is a searchable list of the ids the game registers (who registers each, and its
settings, in the tooltip); each setting is a checkbox, a list of names, a list of records of the right type,
or a text box checked against its range; `reset` puts a setting back to its default; `all`, `any` and `not`
nest rows of their own (`+ of` adds one); `x` removes an entry. Each edit is one undo step in the owner's
history: the record's, or the document's for a wire. What is wrong (an unknown id, a setting the entry does
not take, a record that does not exist, a number out of range) is shown under the form and in **Problems**.

### The visual log

`vlog_window 1` opens the **Visual log** window (issue #300), in a dev build with or without `-edit`. It is the
timeline of the debug shapes the engine keeps while `vlog_record 1` is on: a slider over the ticks kept (`vlog_ticks`,
ten seconds by default), a Live button to follow the newest, a checkbox per category (they write `vlog_show`), and the
shown tick's shapes with their text. The shapes themselves are drawn in the world through the debug lines, so
`r_debugdraw 1` shows them. The AI debug view (category `ai`: what an agent saw and where it stood) and
`phys_debug` (category `physics`: each character's capsule and ground normal) record into it, so "why did it path
there?" is scrubbed back to the tick that decided it. Every control is a console command too: `vlog_at <tick|-ticks|live>`,
`vlog_step [ticks]`, `vlog_list [tick]`, `vlog_clear` (tests: ShapesAreKeptPerTickAndScrubbedBackTo,
VlogShowPicksTheCategoriesDrawnAndListed, TheVisualLogKeepsWhatAnAgentSawAndWhereItStood). Combat, ability and
navigation debug, and the colliders `phys_debug` draws, are not recorded.

## 4. The keys

| Key | What |
|---|---|
| W A S D | Fly the free camera |
| Right mouse drag | Turn the free camera |
| Left click | Select (or place, with the palette or a brush armed; or pick a wire's target; or paint a face) |
| Ctrl+click | Add to the selection, or take away (`ed_select_add`) |
| Left drag on empty picture | Box select (with Ctrl or Shift: add to the selection) |
| G / R / T | Move gizmo / rotate gizmo / scale gizmo |
| L | World or local space for the gizmos (`ed_space`) |
| F | Frame the selection (`ed_frame`) |
| Delete | Delete the selected placements (`ed_delete`) |
| Ctrl+D | Duplicate them (`ed_duplicate`) |
| Ctrl+Z | Undo (`ed_undo`) |
| Ctrl+Y or Ctrl+Shift+Z | Redo (`ed_redo`) |
| Ctrl+P | Play, or stop playing (`ed_play` / `ed_stop`) |
| Escape | Stop playing; disarm the palette; cancel picking a wire's target |
| `~` | Open or close the console |

Keys are never taken from a text field: typing in the console or the inspector is safe. W and E are the
camera's, which is why the gizmos are on G and R.

## 5. Undo

**Every change is one step in the document's history**: a placement, a drag, a field, a wire, a delete.
Undo and redo walk it (Edit menu, Ctrl+Z, `ed_undo [n]`); `ed_history` lists it, with where you last saved.
A new change after an undo drops what was undone. The document is **modified** whenever the history is
not where it was saved, so undoing back to the save makes it clean again. Each tab's document has its own
history. The record browser has its own history, apart from the document's, and so do the AI Graph's and
the Conditions form's edits of a record; the terrain has one of its own too (`ed_terrain_undo`).

## 6. Making a new level

- `doc_level storeroom` (or **File → New** for a document alone) starts a new level: a scene `storeroom`
  that places a new document `storeroom_placements`. Place things as usual.
- The first save writes both, each a file of its own in your game's `data/` folder
  (`storeroom.json`, `storeroom_placements.json`). Point game.json's `"scene"` at it, or reach it with a
  load door or `scene_load storeroom`.

## 7. Playing

Press **Play** (the toolbar, Ctrl+P, or `ed_play`). The editor builds a **second, real world** from the
document as it is now, saved or not, with your game's rules started: its player, its systems, everything a
run has. The player stands on the ground below the free camera, facing the way it faces. While playing,
the game has the screen and the input, and a slim bar with a **Stop** button replaces the panels; `~`
still opens the console over the game.

**Stop** (the button, Escape, Ctrl+P, or `ed_stop`) throws the play world away. Whatever happened in it,
a door opened, a crate knocked over, a goblin killed, stays there: the document, its history and the level
you were editing are exactly as you left them. Edits are made in the editor, never during play.

## 8. Saving

**File → Save** (`doc_save`) writes the document back to **the file it came from**, and only what changed:

- **Your comments stay**, and so do the key order, the formatting, the other records in the same file and
  the fields the file leaves at their defaults. A moved crate changes the one line its position is on.
- New values are written as a person would write them: camel case, enums by name, vectors on one line.
- A new document (or level) is a file of its own in your game's `data/` folder.

Records edited in the **Records** tab are saved into the file that defined them when that file is your
game's. A record that comes from somewhere else, the engine, a kit or a mod, is never written into
someone else's files: the change is saved as a **patch** in your game's
`data/patches/<type>_<namespace>_<name>.json`, naming only what changed, the way a mod changes a record
([MODDING.md](MODDING.md) §5). A patch cannot remove a field, so a removal is reported and left.

**Saving into a mod** (issue #375). With `ed_mod <id>` (a loaded mod; also **+** → "Save into" in the
tabs), the mod's folder is the only one written. A document the mod defines saves in place, and a new one is
made in the mod. A document the mod does not own, such as the shipped game's level, is saved as a patch in
`<mod>/data/patches/placements_<namespace>_<name>.json` holding its whole list of placements, which later
saves change in place; the tab and the title say "(patch)". A new document in another namespace than the
mod's is refused. `ed_mod -` goes back to saving each document into its own file. The choice is for this
session only.

After a save the game loads exactly what you saved: there is no export step.

## 9. Every command

Everything the editor does is a console command as well, so a script or a test can press it the way you
do. Names are placements' names or ids; quote a name with spaces (`"cart lamp"`). Positions are metres
in the document's frame; angles are degrees. **JSON goes in single quotes** (issue #373): everything up to
the closing `'` is taken as it is, double quotes, backslashes, `;` and `//` included (`\'` is a quote), as in
`ed_set door loot '{"id":"x"}'`. A lone `'` is just a character, and double quotes work as before.

**The document**

| Command | What |
|---|---|
| `doc_open <id>` | Open a placements document |
| `doc_new [id]` | Start an empty placements document |
| `doc_level <scene>` | Start a new level: a scene naming a new document; both written on the first save |
| `doc_save` | Write the document back to its file (§8) |
| `doc_close` | Close it, removing what it placed |
| `doc_status` | What is open, where it saves, and whether it is saved |
| `ed_undo [n]`, `ed_redo [n]` | Undo or redo the last change (or that many) |
| `ed_history` | The document's changes, oldest first, which are undone, and where it was saved |
| `ed_tabs` | The open tabs, the active one marked, and where each saves |
| `ed_tab <n\|id>` | Make tab n (1 is the first), or the tab with that document, active |
| `ed_tab_open <id>` | Open a document in a tab of its own, or switch to its tab |
| `ed_tab_new [id]` | A new tab with an empty document |
| `ed_tab_close [n] [!]` | Close the active tab, or tab n; `!` closes it with unsaved changes |
| `ed_mod [id\|-]` | Save into that loaded mod only, another's document as a patch there; `-` saves each document into its own file again |

**Placing, selecting and moving**

| Command | What |
|---|---|
| `ed_palette [search]` | The prefabs you can place, by namespace |
| `ed_place <prefab> [x y z] [yaw] [name]` | Place a prefab (at the origin with no position) |
| `ed_select [name]` | Select a placement, or an entity by name; nothing clears the selection |
| `ed_select_add <name>` | Add a placement to the selection, or take it out (Ctrl+click) |
| `ed_select_all [prefab]` | Select every placement of the document, or every one of a prefab |
| `ed_move [name] <x> <y> <z>` | Put a placement (else the selection) there |
| `ed_nudge <dx> <dy> <dz>` | Move everything selected by that much |
| `ed_rotate [name] <yaw>` | Turn it to that yaw |
| `ed_turn <x\|y\|z> <degrees>` | Turn everything selected about the gizmo's axis, round the last one selected |
| `ed_scale <f> \| <x> <y> <z>` | Scale everything selected along the last one's own axes |
| `ed_delete [name]` | Take it out of the document (no name: everything selected) |
| `ed_duplicate [name]` | Copy it, with a name of its own, and select the copy (no name: everything selected) |
| `ed_frame` | Move the free camera to look at the selection |
| `ed_gizmo [move\|rotate\|scale]` | Which gizmo the selection shows |
| `ed_space [world\|local]` | Whether the move and rotate gizmos follow the world's axes or the selection's own |
| `ed_snap [0\|1]`, `ed_grid [metres]`, `ed_angle [degrees]`, `ed_scalestep [factor]` | Snapping on or off, the grid (0.5), the angle step (15), the scale step (0.1) |

**Tuning**

| Command | What |
|---|---|
| `ed_set <placement> <component.field> <value>` | Override one field for this placement: `ed_set door mover.seconds 0.5`. Also `at`, `yaw`, `pitch`, `roll`, `scale`, `name`, `relativeTo`: `ed_set door at 0 1.5 -4`. A path reaches inside lists and objects: `ed_set trader inventory.items[2].item knife` |
| `ed_revert <placement> <component.field>` | Take the override away |
| `ed_revert_all <placement>` | Take every override away, back to the prefab (one undo step) |
| `ed_inspect <placement>` | Every field, which are overridden (`*`) and where each value came from; a list's elements indented under it |
| `ed_add <placement> <component.list> [value]` | An element at the end of a list (its default, or the value); for a map, `<key> [value]`: `ed_add trader inventory.items` |
| `ed_remove <placement> <component.list[i]>` | Take an element (or a map's entry) out |
| `ed_reorder <placement> <component.list[i]> <to>` | Move an element to another index |
| `ed_add_component <placement> <component>` | Add a component the prefab does not name to this placement alone: `ed_add_component crate point_light` |
| `ed_remove_component <placement> <component>` | Take away a component this placement added |
| `ed_make_prefab <id> [placement...]` | Nest the placements (or the selection) into a new prefab, written to your game's `data/`, and place it where they stood |

A value is read by the field's type: numbers within the field's range, `true`/`false` (or `yes`, `on`, `1`),
vectors as `x y z`, enums by name, record ids checked against the records of the right type, and JSON for
lists and objects (in single quotes). Write `components.` or `parts.` in front when a component and a part
share a name; on its own, `mover.seconds` means the part's field, the one you can change.

**Wiring**

| Command | What |
|---|---|
| `ed_wire <from> <output> <to> <input> [delay] [value]` | Wire an output to a target's input: `ed_wire plate OnStartTouch door Open` |
| `ed_unwire <from> <index\|output>` | Remove a wire by its number in `ed_wires`, or every wire of an output |
| `ed_wires <name>` | A placement's wires, numbered |

**Records**

| Command | What |
|---|---|
| `ed_rec_open <type> <id>` | Open a record in the record browser |
| `ed_rec_get [path]` | A field (or all of it), and the file that wrote it |
| `ed_rec_set <path> <value>` | Set a field: a JSON literal (`120`, `true`, `"Bob"`, `[1,2,3]`) or a bare word, a string |
| `ed_rec_undo [n]`, `ed_rec_redo [n]` | The record's own undo and redo |
| `ed_rec_save` | Save it (§8) |
| `ed_rec_close` | Close it, dropping unsaved edits |
| `ed_rec_live [0\|1]` | Show the open record's edits in the game before a save (on by default) |

**Assets**

| Command | What |
|---|---|
| `ed_assets [kind\|all] [mount=<name>] [search]` | The assets, by path, with the mount that provides each |
| `ed_asset_refs <path>` | Every content file that names an asset, at its line |
| `ed_asset_pick <path> <field>` | Put an asset in a field of the open record |
| `ed_asset_pick <path> <placement> <component.field>` | Put it in a placement's field, as an override |
| `ed_asset_place <path> [x y z] [yaw]` | Place it: the prefab that uses it, or the model on its own |
| `ed_asset_rename <path> <new path>` | Move one of your game's assets and rewrite every file of your game that names it |

**Brushes**

| Command | What |
|---|---|
| `ed_brush <box\|wedge\|cylinder> <x> <y> <z> [<w> <h> <d>] [material] [name]` | A brush standing at x y z (its floor's middle), snapped to the grid, and select it |
| `ed_brush_size [name] <w> <h> <d>` | Resize a brush (else the selected one) |
| `ed_brush_shape [name] <box\|wedge\|cylinder> [sides]` | Make it another shape |
| `ed_brush_material [name] [face\|all] <material\|none>` | A material for one face (top, bottom, north, south, east, west, side) or the whole brush; `none` gives a face the brush's again |
| `ed_brushes` | The document's brushes, their shapes, sizes and materials |

**Terrain**

| Command | What |
|---|---|
| `ed_terrain` | The terrain being sculpted, its sculpted sectors and the brush |
| `ed_sculpt <raise\|lower\|smooth\|flatten> <x> <z> [radius] [strength] [height]` | One dab of a tool at x z (absolute metres) |
| `ed_paint <layer> <x> <z> [radius] [strength]` | Paint a layer (0 to 3) |
| `ed_water <x> <z> <height\|off>` | Water in that sector at that height, or none |
| `ed_terrain_save` | Write the `.sterrain` file |
| `ed_terrain_undo`, `ed_terrain_redo`, `ed_terrain_history` | The terrain's own undo, redo and history of strokes |

**AI graphs** (the record open in the record browser)

| Command | What |
|---|---|
| `ed_ai_tree` | The open `state_machine`, `ai_schedule` or `routine` as a tree, with transitions |
| `ed_ai_add <parent\|-> <name> [index]` | Add a state (under a parent, or at the top) or a task or entry |
| `ed_ai_remove <node>` | Remove it |
| `ed_ai_move <node> <index>` | Move it among its siblings |
| `ed_ai_reparent <state> <parent\|-> [index]` | Put a state under another (state machines only) |
| `ed_ai_rename <node> <name>` | Rename it, and every transition and `initial` that names a state |
| `ed_ai_active [entity]` | The nodes an entity is in now (in the play world while playing); no name: who runs it |

A node is a state's name, a path, or a list index (`2` or `#2`).

**Conditions and actions**

| Command | What |
|---|---|
| `ed_vocab [vocabulary] [search]` | The vocabularies, or one's entries with every setting (type, range, unit, enum values, tooltip) |
| `ed_vocab_rec <path>` | Open the form on a field of the open record: `ed_vocab_rec nodes[0].options[0].conditions` |
| `ed_vocab_wire <placement> <n>` | Open it on wire n's `requires` (n as `ed_wires` numbers it) |
| `ed_vocab_show` | The entries, their settings and the problems |
| `ed_vocab_pick <path\|.> <id>` | Make the entry there that one (keeping the settings both have) |
| `ed_vocab_add <listpath\|.> <id>` | Add an entry to a list (`.` for a list value, `of` for an `all` or `any`) |
| `ed_vocab_param <path\|.> <setting> [value]` | Set a setting, read by its type; no value puts back its default |
| `ed_vocab_remove <path\|.>` | Take an entry out (`.` clears the value) |
| `ed_vocab_close` | Close the form |

Paths inside the value are like a record's: `of[0]`, `[1].of`.

**Playing, problems and the screen**

| Command | What |
|---|---|
| `ed_play [x y z [yaw]]` | Play from the free camera (or from that point) |
| `ed_stop` | Stop, back to the editor |
| `ed_problems` | The problems, by file (`problems`, outside the editor too, lists the content's) |
| `ed_layout` | Put the panels back |
| `ed_panel [all\|<title>]` | Bring a panel to the front (any case, or a unique start of its title), or each in turn; with nothing, list them |
| `ed_viewport 1` | A second view of the free camera in a window |
| `cam_set <x> <y> <z> [yaw] [pitch]` | Put the free camera somewhere |
| `ent_select <name>` | Select any entity by name for the inspector |

**The animation preview** (issue #362; they work in any dev build, and the **Animation** window shows them in the editor)

| Command | What |
|---|---|
| `anim_preview [graph \| model.glb] [model.glb]` | Open a graph (on the model a prefab plays it on, or the one named) or a model's clips; with nothing, list the graphs |
| `anim_preview_clip <clip> [seconds]` | Show one clip scrubbed to that time; with no clip, back to the graph |
| `anim_preview_param <param> [value]` | Set a param of the graph (a trigger needs no value) |
| `anim_preview_state <state> [layer]` | Put a layer (the base when none is named) in a state |
| `anim_preview_step [seconds]` | Run the graph on (a tick by default) and say where its layers are and what events it raised |
| `anim_preview_close` | Close the preview |

## 10. The worked example

`tests/games/editor` has no C# and no kit: four prefabs (a pressure plate, which is a trigger volume; a door
with a mover; a crate; a light) and an empty level, the scene `level` with a floor and a player start,
naming `level_placements`, which holds a comment and nothing else.

```
Sage.Host -game tests/games/editor -edit level
ed_place plate 0 0.5 2 0 plate
ed_place door 0 1.5 -4 180 door
ed_set door mover.seconds 0.5
ed_wire plate OnStartTouch door Open
ed_play                       (walk onto the plate: the door slides open in half a second)
ed_stop
doc_save
```

The saved file keeps its comment, and running the game without `-edit` plays the level you built.
`tests/Sage.Tests/Editing/EditorExitTests.cs` does exactly this through the console, undo and redo
included, then boots the saved game without the editor and steps on the plate there too, and
`tools/editor_smoke.sh` does it in the real host, drawing every panel on the way.

## 11. What is not built yet

Phases 10a and 10b are built (design/15 §10e to §10z). What is left (docs/REDESIGN.md §5, design/15 §11):

- **Brushes are simple solids.** There is no CSG (cutting one brush out of another), no vertex editing, no
  per-face texture offset or rotation, no brush entity (a brush that is a door or a trigger: use a prefab
  with its own collider), no lightmaps for editor brushes and no export to `.map`.
- **Asset previews are pictures of textures.** A model shows a labelled box rather than a thumbnail, and a
  material is previewed by its texture slots and the live world, not a rendered ball.
- **Pitch, roll and scale do not reach everywhere.** Far proxies and travel entries use only the yaw, a
  prefab's `children` have only a yaw, and a placement's scale scales its transform but not its physics
  collider.
- **Water from the terrain tools is not drawn**: it is a volume to swim in. A far-ring copy of the ground
  already loaded keeps its old shape until it is generated again.
- **Tabs hold placements documents only**; a patch saved into a mod replaces a level's list but cannot
  remove a record; the mod to save into is chosen each session.
- **The panels' clicks and drags are not tested**: CI draws every panel, but drives it through the console.
- Editing an `anim_graph` in the **Animation** window: it previews a graph, which is edited in the Records tab,
  and it draws the skeleton as a stick figure rather than the skinned mesh.
