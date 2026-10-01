# The Sage editor

This is for people who build levels: placing things, tuning them, wiring a plate to a door, and trying it.
Sage's editor is a **mode of the game's own executable**: it loads your game exactly as a run does, the same
content, plugins and rules, and shows the level without playing it. You place prefabs from a palette, move
and turn them, change a field for one of them, wire them together, press Play to try it and Stop to come
back, and save. **You never edit JSON to do it**, and what you save is the file your game loads.

If you are making the game rather than its levels, read [MAKING_A_GAME.md](MAKING_A_GAME.md) first: prefabs,
scenes and placements are explained there (§3 to §5), and the editor builds out of exactly those. The design
behind this page is [design/15-editor.md](design/15-editor.md) (§10e to §10n), and the worked example is
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

## 2. What a level is

A level is a **scene** (`"type": "scene"`: the ground, the player's start, the sky) that names one or more
**placements documents** (`"type": "placements"`): lists of prefabs, each with a position, a turn about Y, a
name, and optionally the fields it changes from its prefab (**overrides**) and its wires (**outputs**). The
editor edits one placements document at a time; that is "the document" below. The status bar says which,
and whether it is saved.

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
| Middle | **Viewport** | The level itself. Click to select; drag a gizmo to move or turn; a small toolbar at its top picks the gizmo and the snapping, and holds the **Play** button |
| Left | **Outliner** | Every entity in the edit world, by name. A row selects exactly that entity; Delete on a placed one deletes its placement |
| Left (tab) | **Palette** | The prefabs you can place, by namespace, with a search box |
| Right | **Inspector** | The selection: its placement (position, yaw, name, frame) and every field of its components and parts |
| Right (tab) | **Records** | Any record, by type: a form and the raw JSON, saved into the file it came from |
| Right (tab) | **I/O** | The selection's wires: what it fires, at whom, and adding new ones |
| Bottom | **Log** | The log, filtered by level and category |
| Bottom (tab) | **Problems** | What is wrong with the content and the open document, by file |
| Bottom (tab) | **Console** | Every editor action as a typed command (§9) |
| Bottom edge | **Status bar** | The document and whether it is saved, the selection, the world, the camera, and the problem count |

The menus: **File** (New, Open, Save, Close, Exit), **Edit** (Undo and Redo, each saying what it will undo),
**View** (Free camera, Viewport, Reset layout). `ed_viewport 1` (View → Viewport) opens a second view of
the free camera in a window of its own.

### The viewport and gizmos

- **Select** by clicking. A click on part of a placed prefab (a child) selects the placed thing. Things with
  no collider are found by a small sphere at their origin.
- **Move** with the move gizmo: drag an arrow to move along one axis, a square to move in a plane.
  **Turn** with the rotate gizmo: drag the ring to turn about Y. A whole drag is one undo step.
- **Snapping** is on by default: moves snap to a 0.5 m grid, turns to 15°. The toolbar or `ed_snap`,
  `ed_grid`, `ed_angle` change it.
- **Wires** are drawn as lines between wired things, the selection's brighter, with an arrowhead and the
  output and input they connect.
- The editor's own cameras are never picked.

### The palette

Type in the search box to narrow the list (every word must appear in the prefab's id). **Click a prefab to
arm it**, then click in the viewport: it is placed on the first surface under the cursor (or the ground
plane), selected, and the palette stays armed so you can place another. Click the prefab again, or press
Escape, to stop. Each placement gets a unique name (`crate`, `crate_2`, ...). Abstract prefabs, templates
that are only ever another prefab's `base`, are not offered.

### The inspector and overrides

The inspector shows the selection's placement as a form (at, yaw, name, frame), then a group per component
and part with every field, its value, its range and unit, and a tooltip.

- **Changing a field changes this placement only.** It is written as an **override** in the document, not
  into the prefab: every other door still takes two seconds. Overridden fields are marked; **revert**
  takes the override away, back to the prefab's value.
- **Hover a field to see where its value came from**: "this placement", the prefab's file and line, a base
  prefab's, a mod's patch, or "default".
- A part's options (a `body`'s size, a `mover`'s seconds) are edited as the part's, and the component the
  part builds from them is shown read-only beside it.
- An entity the document did not place (one the scene placed, or a prefab's child) is shown read-only:
  there is nowhere to save an edit to it.

### Records

The Records tab opens any record (a prefab, an item, a sound) as a form over its JSON, with every field's
range and where it was last set. Edits have their own undo (Ctrl+Z / Ctrl+Shift+Z / Ctrl+S while the panel
has the focus) and are saved into the file the record came from, or as a patch (§8).

### I/O: wiring

Select the thing that fires (a pressure plate), open **I/O**, and add a wire: choose an **output**
(`OnStartTouch`, typed or picked from the suggestions), press **pick target** and click the target in the
viewport or the outliner, choose one of the **inputs** that target takes (a door's `Open`, `Close`,
`Toggle`), then a delay and a value if you want them. Only inputs the target actually takes are offered,
and a target needs a name: the panel offers to give it one. Each wire's delay and value can be edited in the
list. A wire's `requires` condition is shown but not edited yet (§11).

### Problems and the log

**Problems** lists errors and warnings from loading your content (with file and line), conflicts between
mods, and what is wrong with the open document as you edit it: a placement whose prefab is gone, a wire to
a target that is not in the document, two placements with one name or one id. Click a row to select the
placement or open the record. The count is in the status bar.

**Log** is the log, at or above a level, with categories hidden or shown one at a time.

## 4. The keys

| Key | What |
|---|---|
| W A S D | Fly the free camera |
| Right mouse drag | Turn the free camera |
| Left click | Select (or place, with the palette armed; or pick a wire's target) |
| G / R | Move gizmo / rotate gizmo |
| F | Frame the selection (`ed_frame`) |
| Delete | Delete the selected placement (`ed_delete`) |
| Ctrl+D | Duplicate it (`ed_duplicate`) |
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
not where it was saved, so undoing back to the save makes it clean again. The record browser has its own
history, apart from the document's.

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

After a save the game loads exactly what you saved: there is no export step.

## 9. Every command

Everything the editor does is a console command as well, so a script or a test can press it the way you
do. Names are placements' names or ids; quote a name with spaces (`"cart lamp"`). Positions are metres
in the document's frame; angles are degrees.

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

**Placing, selecting and moving**

| Command | What |
|---|---|
| `ed_palette [search]` | The prefabs you can place, by namespace |
| `ed_place <prefab> [x y z] [yaw] [name]` | Place a prefab (at the origin with no position) |
| `ed_select [name]` | Select a placement, or an entity by name; nothing clears the selection |
| `ed_move [name] <x> <y> <z>` | Put a placement (else the selection) there |
| `ed_rotate [name] <yaw>` | Turn it to that yaw |
| `ed_delete [name]` | Take it out of the document |
| `ed_duplicate [name]` | Copy it, with a name of its own, and select the copy |
| `ed_frame` | Move the free camera to look at the selection |
| `ed_gizmo [move\|rotate]` | Which gizmo the selection shows |
| `ed_snap [0\|1]`, `ed_grid [metres]`, `ed_angle [degrees]` | Snapping on or off, the grid (0.5), the angle step (15) |

**Tuning**

| Command | What |
|---|---|
| `ed_set <placement> <component.field> <value>` | Override one field for this placement: `ed_set door mover.seconds 0.5`. Also `at`, `yaw`, `name`, `relativeTo`: `ed_set door at 0 1.5 -4` |
| `ed_revert <placement> <component.field>` | Take the override away |
| `ed_inspect <placement>` | Every field, which are overridden (`*`) and where each value came from |

A value is read by the field's type: numbers within the field's range, `true`/`false` (or `yes`, `on`, `1`),
vectors as `x y z`, enums by name, record ids checked against the records of the right type, and JSON for
lists and objects. Write `components.` or `parts.` in front when a component and a part share a name; on
its own, `mover.seconds` means the part's field, the one you can change.

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

**Playing, problems and the screen**

| Command | What |
|---|---|
| `ed_play [x y z [yaw]]` | Play from the free camera (or from that point) |
| `ed_stop` | Stop, back to the editor |
| `ed_problems` | The problems, by file |
| `ed_layout` | Put the panels back |
| `ed_viewport 1` | A second view of the free camera in a window |
| `cam_set <x> <y> <z> [yaw] [pitch]` | Put the free camera somewhere |
| `ent_select <name>` | Select any entity by name for the inspector |

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
included, then boots the saved game without the editor and steps on the plate there too.

## 11. What is not built yet

These are phase 10b's and later (docs/REDESIGN.md §5, design/15 §11):

- **Brushes and block-out geometry.** Walls and rooms are drawn in TrenchBroom as `.map` files today
  (design/15 §10a); the editor places prefabs.
- **An asset browser** and material preview: models, textures and sounds are named by path in records.
- **A behaviour-tree view**, and **a conditions and actions editor**: a wire's `requires` and a state
  machine's actions are edited as JSON (in the Records tab) for now.
- Multi-select and box select, scaling, and rotation about X and Z (a placement has only a yaw).
- Editing nested objects and lists of a component in the inspector (`ed_set` takes them as JSON), and
  overriding a component the prefab does not name.
- Several documents open at once, and a separate editor executable for modders.
