# Making a game with Sage

This is the practical guide: what you write, where it goes, and in what order, using the engine **as it
is today** rather than as it is planned. Everything here is something the Sandbox already does — if a
thing is not in this guide, assume the engine does not do it yet, and check the "What is not here yet"
section at the end before building around it.

The design docs in [`docs/design/`](design/00-index.md) explain *why* each system is shaped the way it
is. This explains how to use them.

**There is a runnable example of everything in §2–§4**: [`games/Hello`](../games/Hello) is the smallest
game this engine can run — four files and about fifty lines of code — and it is built by the solution and
exercised by the test suite, so it cannot quietly stop working. Read it alongside this, or start by
copying it:

```bash
dotnet run --project src/Sage.Host -c Development -- -game games/Hello
```

---

## 1. What you supply, and what the engine already does

The engine gives you: a fixed-tick simulation with an ECS, physics, a character controller, terrain,
brush levels, AI, combat, items, abilities, factions, dialogue, quests, particles, weather, audio, a
console, saves, hot reload, and a renderer. **It gives you no game.** What a game supplies is:

| You write | In what form |
|---|---|
| What exists in your world | **records** — JSON files under your game's `content/data/` |
| What things are made of | **prefabs** — records that list components and parts |
| Where things are | a **scene** record, and/or `.map` levels drawn in TrenchBroom |
| Rules that are yours | a small amount of **C#** in your game module |
| How it looks on screen | your **HUD** and screens, in your game's client half |
| Art and sound | PNG, WAV, `.glb` — read at runtime, no build step |

The split that matters: **your simulation half references `Sage.Engine` only** — no MonoGame — so it can
be tested headlessly and could run on a server one day. Anything that needs a screen goes in a second
project, your *client half*.

---

## 2. The shape of a game

Copy the Sandbox's layout; it is the worked example this guide refers to throughout.

```
games/YourGame/                 the simulation half
    YourGame.csproj             references src/Sage.Engine only
    game.json                   the manifest the host reads
    YourGameModule.cs           your IGameModule
    content/
        data/*.json             records: prefabs, items, materials, scene…
        textures/*.png
        audio/*.wav
        models/*.glb
        maps/*.map
games/YourGame.Client/          the client half (optional but usual)
    YourGame.Client.csproj      references Sage.Engine, Sage.Client, YourGame
    YourGameClientModule.cs     HUD, screens, anything with a screen
```

Both projects are plain libraries, and both reference the engine **compile-time only** so the host's
copies are used at run time:

```xml
<PropertyGroup>
  <OutputType>Library</OutputType>
  <RootNamespace>YourGame</RootNamespace>
  <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
</PropertyGroup>
<ItemGroup>
  <ProjectReference Include="..\..\src\Sage.Engine\Sage.Engine.csproj" Private="false" />
</ItemGroup>
```

That is *all* a project under `games/` needs, because `games/Directory.Build.props` gives every one of
them the rest: `net8.0`, nullable, the Debug/Development/Shipping configurations, and three global
usings — `sage_engine`, `Friflo.Engine.ECS`, and an alias making `Transform` mean **Sage's** rather than
Friflo's, which ships a `Transform` of its own. Put your game somewhere else and you inherit none of
that: add the target framework yourself, `using sage_engine;` in every file, and the alias, or the first
time you name `Transform` the compiler cannot tell which one you mean.

### `game.json`

```json
{
  "name": "Your Game",
  "id": "yourgame",
  "assembly": "bin/{config}/net8.0/YourGame.dll",
  "mounts": ["content"],
  "modsDirectory": "mods",
  "modules": {
    "disable": [],
    "add": ["../YourGame.Client/bin/{config}/net8.0/YourGame.Client.dll"]
  }
}
```

- **`id` is your record namespace.** Everything your game defines is `yourgame:something`, and that is
  how mods and the engine tell your records from `sage:`'s.
- `{config}` is replaced with Debug / Development / Shipping, so one manifest works for all three.
- `mounts` are folders layered over the engine's own content, **later wins** — which is how a game (or a
  mod) replaces an engine texture without touching it.
- `modules.disable` switches off engine modules by name. A game with no AI drops `AIModule` and nothing
  else changes.

### Running it

```bash
dotnet run --project src/Sage.Host -c Development -- -game games/YourGame
```

In a **dev build with no `-game`**, the host walks up from the executable looking for `Sage.sln` and
loads `games/Sandbox`. In a shipping build it looks for a `game` folder next to the executable. Console
commands can be run at startup with `+`:

```bash
dotnet run --project src/Sage.Host -c Development -- -game games/YourGame +sv_cheats 1 +god 1
```

---

## 3. Records: everything your game *is*

A record is a JSON object with a `type` and an `id`, in any `data/**/*.json` file in any mount. Files can
have comments and trailing commas. A record is **content, and read-only at run time**.

```json
[
  {
    "type": "prefab",
    "id": "goblin",
    "components": { "AIState": {} },
    "parts": {
      "sprite": { "sheet": "goblin" },
      "body": { "shape": "Capsule", "radius": 0.35, "height": 1.8 },
      "character": {},
      "attributes": {},
      "faction": "goblins"
    }
  }
]
```

Four things to know before you write many:

- **Ids are namespaced.** Inside your files a bare `goblin` means `yourgame:goblin`; write `sage:lit_default`
  to reach the engine's.
- **`base` inherits** from another record of the same type, and `"patch": true` merges into one that
  already exists — which is how a mod changes one field of yours without copying the file.
- **Every `RecordId` field is checked** when records load: a reference to something that does not exist
  is an error with the file and the field named, not a surprise at run time.
- **They hot reload.** Save the file and the running game picks it up; the Sandbox respawns its scene.

A game can define **its own record types** — the Sandbox's `scene` is one. A plain class with public
fields, an attribute, and one line in `Init`:

```csharp
[Record("quest_board")]
public sealed class QuestBoardRecord
{
    public List<RecordId> Offers = new();
    public float RefreshHours = 24f;
}

// in Init, before records load:
ctx.Engine.Records.Register<QuestBoardRecord>();
```

Without that `Register` line the JSON is read, matched to nothing, and ignored.

`rec_list <type>`, `rec_print <id>` and `rec_reload` in the console are how you check what actually
loaded — and `rec_print` is the fastest way to learn a record's fields, because it prints the merged
result with the file each field came from.

### Every record type there is

Twenty-four, and a game may use as few as it likes. Their fields are documented in the design doc named
beside each group; `rec_print sage:<id>` on one of the engine's own is usually quicker.

| For | Types |
|---|---|
| **Things that exist** (05) | `prefab` — components and parts; `scene` — where things start; `tag` |
| **Look** (06, 07) | `material` — shader, technique, params; `sprite_sheet` — frames, direction groups, animation events |
| **Sound** (11) | `sound` — the file, gain, limits; `cue` — the moment a sound is asked for |
| **Levels** (15) | `map` — a `.map` file, its scale and where it stands |
| **Movement and bodies** (10, 16) | `movement_profile` — speed, jump, eye height, step; `physics_layers` — what collides with what |
| **Fighting** (16) | `attack` — reach, damage, timing, viewmodel; `damage_type`; `effect` — what a hit leaves behind; `attribute` — health and the rest |
| **Magic** (16) | `ability` — cost, cast time, payload, cues |
| **Carrying** (16) | `item` — what it is, what it weighs, what equipping it does |
| **Minds** (16) | `ai_profile` — sight, memory, speeds; `ai_schedule` — the tasks a creature runs |
| **People** (16) | `faction` — who hates whom; `dialogue` — lines and choices; `quest` — stages and objectives |
| **Weather and effects** (06) | `weather` — what falls, wind, fog, light; `particle` — emitters |
| **Controls** (08) | `input_map` — actions bound to keys and buttons |

### Every prefab part the engine provides

A prefab's `components` block sets components directly; its `parts` block calls these, which is the
usual way, because a part does the assembling for you:

| Part | Gives the entity |
|---|---|
| `body` | a collider and a rigid body — `shape` (Box/Sphere/Capsule), `size` or `radius`/`height`, `mass` |
| `character` | the kinematic character controller, and with it the ability to walk |
| `sprite` | a billboard sprite from a `sprite_sheet` |
| `mover` | geometry that slides — `open`, `seconds`, `closeAfter` (F17) |
| `audio` | a sound it makes on its own — `sound`, `loop`, `volume` |
| `particles` | an effect it gives off — `effect` |
| `attributes` | health and the rest, from `attribute` records |
| `melee` | an attack it can make |
| `abilities` | spells it can cast |
| `effects` | effects already on it |
| `inventory` | somewhere to put things |
| `pickup` | makes it something you can pick up — `item`, `count` |
| `loot` | what it drops |
| `faction` | who it belongs to |
| `dialogue` | something to say |

Fifteen, and that is all of them — a game adds its own in `Init` the same way. The Sandbox has two:
`hop` (a creature that bounces, eleven lines, in its simulation half) and `box_mesh` (a plain box mesh,
in its *client* half, because building a mesh needs a renderer). A part whose module is missing (a mesh part in a headless run) is
declared `Optional` so the same prefab loads with no renderer at all.

---

## 4. From empty to walking about

Four things, in this order, and you can walk around your own world.

**1. A world, with ground.** Your module's `OnWorldCreated` is where a world is furnished. Terrain comes
first, because things are placed on top of it:

```csharp
public void OnWorldCreated(World world)
{
    var terrain = world.Resources.Get<Terrain>();
    terrain.Generator = new YourGenerator();      // ITerrainGenerator: you fill a heightfield
    terrain.Seed = 1;
    terrain.Load(SectorCoord.Zero);

    world.Resources.Set<GameRules>(new YourRules(this));
    world.AddSystem(new YourOwnSystem(world), Phase.Gameplay);
}
```

**2. Rules.** `GameRules` is an abstract class with four hooks, and a game overrides the ones it cares
about. Install it as a world resource, as above; without one the engine uses `DefaultGameRules`, which
does nothing at all:

```csharp
public sealed class YourRules : GameRules
{
    public override void OnWorldStarted(World world) => SpawnPlayer(world);

    public override Entity SpawnPlayer(World world)
    {
        // Two conversions, and both matter. `ToOrigin` turns absolute metres into the frame the
        // simulation is using right now (R6: after travelling far enough, the world shifts under you),
        // and the terrain height is what stops the player spawning inside the ground or above it.
        var at = world.Origin().ToOrigin(new Vector3(512, 0, 512));
        at.Y = world.Resources.Get<Terrain>().HeightAt(at.X, at.Z) + 1f;
        return world.Spawn(new RecordId("yourgame", "player"), at);
    }

    public override void OnEntityDied(World world, Entity victim, Entity killer) { }
    public override void OnLoaded(World world) { }          // after a save is loaded
}
```

**3. A player.** It is an ordinary prefab with a `character` part, tagged `PlayerControlled`:

```json
{
  "type": "prefab",
  "id": "player",
  "tags": ["PlayerControlled"],
  "parts": {
    "character": { "layer": "player" },
    "attributes": {},
    "inventory": { "capacity": 60 }
  }
}
```

**Give the character a layer.** A character sweeps against everything *except its own* layer — which is
what lets creatures pass through one another — and terrain collision lives on `default`. Leave a
character there and it walks through the world and falls for ever. The engine warns about it now; that
warning exists because this example fell through its own terrain for an hour.

That tag is what makes the rest of the engine treat it as *the* player: **the first-person camera rig
puts the camera in its head automatically** (any entity with a character controller, view angles and
that tag), the cheats act on it, and the HUD reads it. You do not write a camera.

**4. A scene** — a record listing what stands where. The Sandbox's `scene` record has a `player`
placement and a list of `place` entries, each a prefab id, a position and a yaw; its module reads the
record and calls `world.Spawn` for each. A scene is a *game's* record rather than an engine one, which
means you can define placement however suits your game; copying the Sandbox's is the quick way.

Run it, and `ent_list` — or the entity outliner in the dev UI — shows what actually spawned.

**That is the whole minimum**: a `.csproj`, a `game.json`, one module, one `GameRules`, one prefab
record, and a terrain generator of about six lines. No client half is needed to *see* anything — the
engine's own client module draws the world, the crosshair and the dev UI; your client half is for your
HUD and your screens. This guide was checked by building exactly that and walking about in it.

## 5. Levels: rooms, doors and triggers

Outdoors is terrain; **indoors is brushes**. The engine reads TrenchBroom `.map` files directly
(standard, Valve 220 and Quake 2/3 dialects) — 32 map units to the metre, Z-up becomes Y-up.

Point TrenchBroom at an entity definition file generated from your own prefabs:

```bash
dotnet run --project src/Sage.Host -c Development -- -game games/YourGame +fgd_export +quit 0
```

Then name the level in a record and load it from your scene:

```json
{ "type": "map", "id": "tavern", "file": "maps/tavern.map", "at": [520, 0, 490], "onTerrain": true }
```

In the editor:

- **`classname` is a prefab id.** An entity called `goblin` spawns `yourgame:goblin`. `origin`, `angle`
  and `targetname` are read; a classname with no prefab is left for your game to read off the level.
- **A face's texture name is a material id**: a face textured `wall` looks for `yourgame:wall`.
- **Brushes with a classname become a solid entity** — a door, a lift, a trigger volume — which is an
  ordinary entity that owns its geometry. Give it a prefab with a `mover` part and it moves.
- **`"trigger" "1"`** makes its volume something you walk into rather than against, and it is not drawn.

**Wiring** is what makes a level do anything. An entity fires a named *output*; you wire it in the map to
a named *input* on another entity:

```
"classname" "door"
"targetname" "tavern_door"
"OnUse" "!self,Open"                       target,input[,parameter,delay,times]
```

Outputs the engine fires: `OnUse`, `OnStartTouch` / `OnEndTouch`, `OnFullyOpen` / `OnFullyClosed`.
Inputs it offers: `Open`, `Close`, `Toggle`, `Kill`, `Say`, `Fire` — `io_list` prints the live list, and
your own modules can register more. Targets can be a `targetname` or `!self` / `!activator` / `!caller`.

Connections are **checked when the level loads**: a typo names the map file and line rather than a door
that quietly never opens. `map_load`, `map_list`, `map_unload` and `ent_fire <name> <input>` drive it
from the console, and `io_trace 1` logs every wire as it fires.

---

## 6. Where your C# goes: modules, phases and systems

Your own code is smaller than you expect: most of a game is records. What C# you do write goes in one
of two halves, and keeping the split in mind answers most questions about where:

- **Simulation half** — anything that decides something: rules, spawning, AI behaviour you add, what an
  item does, what a quest wants. Testable headlessly, which is the point.
- **Client half** — anything that *shows* something: the HUD, screens, a prefab part that builds a mesh.

Your module is an `IModule` (the game's own also implements `IGameModule`), and the four methods happen
in a fixed order, which matters more than it looks:

- `Init` — **register only**: record types, cvars, console commands, prefab parts, entity inputs. Records
  are not loaded yet.
- `Start` — records are loaded and the graphics device exists: read records, load assets.
- `OnWorldCreated` — per world: install resources and add systems to phases.
- `Shutdown` — in reverse order.

Registering a cvar in `Start` instead of `Init` is a real trap: `config.cfg` is executed between the two,
so the saved value is dropped with an "unknown cvar" warning and the setting silently never applies.

---

## 7. The HUD, and screens

The engine draws **no** part of your HUD. It gives you three things and your client half composes them:

- **`UiDraw`** — an immediate-mode queue in screen pixels, from any `FrameUpdate` system:

  ```csharp
  ui.Rect(x, y, width, height, colour);
  ui.Frame(x, y, width, height, colour, thickness);
  ui.Text(x, y, "100 / 100", colour, scale);
  ui.Image(texture, destination, colour);
  var size = ui.Measure(text);        // and ui.Size for the screen, ui.LineHeight for a line
  ```

- **`MessageLog`** — `world.Say("Picked up a sword", MessageKind.Good)` from anywhere in the simulation,
  including a headless server where nothing draws it. Your HUD decides how a message looks.
- **The crosshair**, which is the one thing the engine draws, because combat and Use both aim from the
  middle of the screen (`ui_crosshair`).

**Screens** — an inventory, a spellbook, a journal — are a panel model in the *simulation* (what a row
says and whether it can be used is a rule, not a drawing) with the client drawing them. A game binds one
to an action in its client module:

```csharp
var screens = world.Resources.Get<ScreenStack>();
screens.Bind(actions.Get("Inventory"), new InventoryScreen());
```

While a screen is open it takes the input, so gameplay does not also react.

---

## 8. Saving

An entity is saved if it carries `Persistent` — everything else is assumed to be rebuilt from records
and levels when the world loads. For a world-level thing of your own (a quest log, the weather), two
steps, and **both are needed**:

```csharp
[SavedResource("your_state")] public sealed class YourState { … }

engine.Saves.RegisterResource<YourState>();     // in Init: the attribute alone saves nothing
```

That second line is the trap — the attribute is decoration until something registers the type, which is
how three separate resources in this engine were "saved" and none of them were. `save <name>`,
`load <name>` and `saves` drive it from the console.

---

## 9. Checking your game without playing it

Three tools, in the order you should reach for them:

**Headless tests.** Your simulation half can be ticked in a test with no window and no graphics device.
This is the engine's own habit and the reason the split exists:

```csharp
var cvars = new CVarRegistry();
using var engine = new Engine(cvars, CoreCVars.Register(cvars));
engine.Modules.Add(new PhysicsModule());
engine.Modules.AddGameplay();
engine.Modules.Add(new YourGameModule());
engine.Modules.InitAll();
engine.Records.Load(engine.Vfs);
engine.Modules.StartAll();

var world = engine.CreateWorld("test");
world.RunFixed(1f / 60f);
```

**Scripted runs.** Drive the real game from the command line and read the log afterwards. Every one of
these is a console command, so anything you can type you can script:

```bash
dotnet run --project src/Sage.Host -c Development -- -game games/YourGame \
  +sv_cheats 1 +god 1 +log_file_level Trace \
  +wait 15 "+warp 520 500" +wait 2 "+in_look 0 10 0.5" \
  "+in_hold Use 0.3" +wait 2 +screenshot +wait 5 +quit 0
```

`wait` is in seconds. `in_tap <action>` presses for a frame, `in_hold <action> <seconds>` holds it,
`in_axis <action> <x> <y> <seconds>` moves a stick, `in_look <yaw> <pitch> <seconds>` turns the view, and
`in_cursor <x> <y>` moves the pointer. Actions are named in `engine_content/data/input.json` — `Use`,
`Move`, `Attack`, `Inventory` and the rest — **not** keys.

Screenshots land in `user/<game>/screenshots/`, logs in `user/<game>/logs/`. `log_level <category> trace`
turns up one part of the engine: `level`, `events`, `physics`, `ai`, `gameplay`, `assets`…

**Hot reload.** With the game running, save a record file and it reloads; save a PNG and the texture
swaps. `rec_reload`, `asset_reload [path]` and `asset_list` do it by hand.

---

## 10. Things that silently do nothing

Most of what costs a newcomer an afternoon in this engine is not an error message — it is something
quietly not happening. The engine's own history is mostly this list, so it is worth reading once:

1. **`[Record]` without `Records.Register<T>()`** is dead JSON: the file loads, the type matches nothing.
2. **`[SavedResource]` without `Saves.RegisterResource<T>()`** saves nothing. This exact bug shipped three
   times in this engine before anybody noticed.
3. **An action that is not `Actions.Register`ed** makes every binding to it ignored (with a warning), and
   `ScreenStack.Bind` to it does nothing at all. Register actions in `Init`.
4. **`GameRules` must be installed in `OnWorldCreated`.** The engine substitutes `DefaultGameRules` and
   calls `OnWorldStarted` as soon as the last module has furnished the world; set it any later and your
   world never starts.
5. **`Terrain.Load` without a `Generator`** trips an assert and loads nothing, after which `HeightAt`
   returns 0 everywhere — so everything you place stands at sea level.
6. **A character left on the `default` physics layer falls through the world**, because a character
   ignores its own layer and the terrain is on that one. It warns now, but the symptom is memorable:
   the camera follows the player down and every direction looks like empty sky.
7. **A cvar registered in `Start` instead of `Init`** is dropped from `config.cfg` with an "unknown cvar"
   warning, so the saved setting silently never applies.
8. **Prefab parts run in registration order, not JSON order**, and registering a name twice replaces it
   without complaint. A part your *client* half registers must be declared `Prefabs.Optional(name)` by
   your simulation half, or a headless run errors on every spawn of that prefab.
9. **Hot reload sweeps what the scene spawned — including the player.** If you respawn your scene on
   `RecordStore.Reloaded`, respawn the player too, or saving a JSON file leaves you with nothing to
   control.
10. **`PawnIntent` is final after the `Commands` phase.** Write it later and your input acts a tick late;
    the phase contract will tell you.
11. **`in_tap` presses for one frame**, so two in a row need a `wait` between them, and a HUD string
    rebuilt every frame allocates in the steady state — cache it and rebuild when it changes.

---

## 11. What is not here yet

Worth knowing before you plan around it:

- **No editor.** Levels are drawn in TrenchBroom; everything else is JSON and the console. The editor is
  TODO F28–F30.
- **No skeletal animation.** Characters are billboard sprites with direction groups (the Daggerfall
  model). Skeletal animation is a later phase.
- **No lighting indoors.** A room with a roof is lit by the sun it cannot see plus the ambient term, so
  interiors are dark. Point lights and lightmaps are still to come.
- **No multiplayer.** The engine follows rules that keep it possible (fixed tick, data-only components,
  no gameplay in rendering), but there is no networking. That is Phase 7.
- **No per-entity keys in maps** beyond `origin`, `angle`, `targetname` and `trigger`: setting a
  component's field from the editor waits on the source generator.
- **Saves cover the simulation, not your UI state**, and a mover is a moving static, so a door will not
  push a player leaning on it.

---

## 12. Where to look next

| If you want | Read |
|---|---|
| Why the engine is shaped like this | [`ARCHITECTURE.md`](../ARCHITECTURE.md) |
| What is built and what is next | [`TODO.md`](../TODO.md) |
| The record pipeline and the VFS | [`docs/design/05-assets-and-vfs.md`](design/05-assets-and-vfs.md) |
| Levels, brushes and the FGD | [`docs/design/15-editor.md`](design/15-editor.md) |
| Wiring, events and entity I/O | [`docs/design/04-events-and-messaging.md`](design/04-events-and-messaging.md) |
| Gameplay: rules, AI, abilities | [`docs/design/16-gameplay-framework.md`](design/16-gameplay-framework.md) |
| The HUD and screens | [`docs/design/13-ui.md`](design/13-ui.md) |
