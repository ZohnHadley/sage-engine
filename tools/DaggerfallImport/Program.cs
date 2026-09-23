// Imports art from your own copy of Daggerfall into the Sandbox (docs/design/05, 06 §3.8).
//
// Daggerfall's art is Bethesda's, so none of it is in this repository and none of it should be
// committed to it. The tool reads the files from the copy you own and writes into two paths that
// .gitignore keeps out of git — the same arrangement Daggerfall Unity uses, where the project
// supplies the engine and you supply the assets.
//
// What it writes:
//   games/Sandbox/content/textures/daggerfall/*.png   sprite sheets and flat textures
//   games/Sandbox/content/data/daggerfall.json        sprite_sheet, material and spawn records
//
// Creature archives suit this engine almost exactly. Daggerfall stores five views of a monster
// (front, front-side, side, back-side, back) and mirrors the other three, which is what a
// `sprite_sheet` record calls `directions: 5` (06 §3.8) — the same trick Doom used. Records come in
// groups of five, one group per animation: 0-4 walking, 5-9 attacking.
//
// Usage:
//   dotnet run --project tools/DaggerfallImport --
//       [--arena2 <ARENA2 dir>] [--connect <DaggerfallConnect.dll>] [--out <content dir>]
//       [--scale <metres per pixel>]

using System.Globalization;
using System.Text;
using DaggerfallImport;

const string DefaultArena2 = @"C:\Program Files (x86)\Steam\steamapps\common\The Elder Scrolls Daggerfall\DF\DAGGER\ARENA2";
const string DefaultConnect = @"C:\Program Files (x86)\Daggerfall Workshop\Daggerfall Imaging 2\DaggerfallConnect.dll";

var options = ParseArgs(args);
if (options.ContainsKey("help"))
{
    Console.WriteLine("""
        DaggerfallImport — turns your own Daggerfall install into Sandbox content.

          --arena2  <dir>   ARENA2 folder (default: the Steam install)
          --connect <dll>   DaggerfallConnect.dll (default: Daggerfall Imaging 2's copy)
          --out     <dir>   the Sandbox's content folder (default: games/Sandbox/content)
          --scale   <m/px>  world size of one sprite pixel (default 1/64, which makes a 113 px
                            skeleton 1.77 m tall)

        Nothing it writes belongs in git: the art is Bethesda's.
        """);
    return 0;
}

string arena2 = options.GetValueOrDefault("arena2", DefaultArena2);
string connect = options.GetValueOrDefault("connect", DefaultConnect);
string outDir = options.GetValueOrDefault("out", DefaultContentDir());
float metresPerPixel = float.Parse(options.GetValueOrDefault("scale", "0.015625"), CultureInfo.InvariantCulture);

if (!Directory.Exists(arena2)) return Missing("ARENA2 folder", arena2);
if (!File.Exists(connect)) return Missing("DaggerfallConnect.dll", connect);

// `--contact 504` dumps every record of an archive into one PNG, which is how you pick what to
// import without opening a GUI: the records are numbered left to right, top to bottom.
if (options.TryGetValue("contact", out var contactArg) && contactArg.Length > 0)
{
    var contactReader = new Arena2Reader(connect, arena2);
    bool isArchive = int.TryParse(contactArg, out int contactArchive);
    var all = isArchive ? contactReader.Read(contactArchive, 0, 999) : contactReader.ReadCif(contactArg, 0, 999);
    var firsts = all.Where(f => f.Index == 0).ToList();
    string what = isArchive ? $"TEXTURE.{contactArchive:000}" : contactArg;
    if (firsts.Count == 0) { Console.Error.WriteLine($"{what}: nothing to show"); return 1; }

    int cw = firsts.Max(f => f.Width), ch = firsts.Max(f => f.Height);
    int cols = Math.Min(8, firsts.Count), rows = (firsts.Count + cols - 1) / cols;
    var contact = new Sheet(cw * cols, ch * rows);
    for (int i = 0; i < firsts.Count; i++) contact.Blit(firsts[i], cw, ch, i % cols, i / cols);
    string contactPath = Path.Combine(options.GetValueOrDefault("out", Directory.GetCurrentDirectory()),
                                      $"contact_{(isArchive ? contactArchive.ToString("000") : Path.GetFileNameWithoutExtension(contactArg))}.png");
    contact.Save(contactPath);
    Console.WriteLine($"{what}: {firsts.Count} records, {cols}x{rows} grid of {cw}x{ch} cells -> {contactPath}");
    for (int i = 0; i < firsts.Count; i++)
        Console.WriteLine($"  record {firsts[i].Record,2}: cell ({i % cols},{i / cols})  {firsts[i].Width}x{firsts[i].Height}");
    return 0;
}

var textureDir = Path.Combine(outDir, "textures", "daggerfall");
Directory.CreateDirectory(textureDir);
var reader = new Arena2Reader(connect, arena2);
var records = new List<string>();

// ---- what to import ---------------------------------------------------------------------------
// The tables are the manifest: add a row to bring something else in. Archive numbers and record
// meanings come from the files themselves — `--contact <archive>` dumps one as a labelled grid,
// which is how these were chosen.

// Monsters and human enemies. Every one of them stores 5 views per animation (the other three are
// mirrored), records 0-4 walking and 5-9 attacking, so one shape reads them all. `scale` is a
// fudge over the shared metres-per-pixel: a Daggerfall rat is drawn nearly as tall as an orc.
var creatures = new (int Archive, string Id, float Scale, int HitFrame)[]
{
    (255, "rat",      0.5f,  2),
    (257, "spriggan", 1f,    2),
    (262, "orc",      1f,    3),
    (270, "skeleton", 1f,    2),
    (272, "zombie",   1f,    2),
    (277, "gargoyle", 1f,    3),
    (487, "knight",   1f,    3),   // "Medium Fighter": a human enemy, same layout
};
foreach (var c in creatures)
    records.Add(Creature(reader, textureDir, c.Archive, c.Id, metresPerPixel * c.Scale,
                         walk: (0, 4), attack: (5, 9), hitFrame: c.HitFrame));

// Townspeople: single-view flats, the way Daggerfall dresses a street corner. Their art fills its
// frame, where a monster's sits in a cell sized for its widest attack frame, so they need roughly
// three quarters of the shared scale to stand the same height as everything else.
const float PersonScale = 0.72f;
var people = new (int Record, string Id, float Scale)[]
{
    (12, "guard_blue",  PersonScale),
    (13, "guard_green", PersonScale),
    (8,  "jester",      PersonScale),
    (14, "noble",       PersonScale),
};
foreach (var person in people)
    records.Add(Flat(reader, textureDir, archive: 357, record: person.Record, id: person.Id,
                     metresPerPixel * person.Scale));

// Temperate woodland flats: trees, rocks and undergrowth.
var nature = new (int Record, string Id, float Scale)[]
{
    (13, "tree_pine",   2f),
    (16, "tree_oak",    2f),
    (17, "tree_autumn", 2f),
    (12, "tree_gnarled",2f),
    (30, "tree_dead",   2f),
    (19, "stump",       1.5f),
    (3,  "rocks",       1.6f),
    (4,  "rock",        1.6f),
    (28, "bush",        1.4f),
    (29, "grass",       1.2f),
};
foreach (var flat in nature)
    records.Add(Flat(reader, textureDir, archive: 504, record: flat.Record, id: "df_" + flat.Id,
                     metresPerPixel * flat.Scale));

// Equipment lying on the ground (16 §3.2, F19). Each one becomes a billboard, an `item` record and
// — for the weapons — the `attack` record equipping it hands to the wielder's Melee.
// `Cif` is the first-person view of the weapon — Daggerfall draws the weapon alone, with no hand,
// which is why holding one looks the way it does. Record 0 is the idle pose and the rest are
// five-frame swings (13 §3).
var equipment = new (int Record, string Id, string Label, EquipKind Kind, float Damage, float Reach, float Cooldown, float Weight, string Cif)[]
{
    (3,  "df_sword",  "iron longsword", EquipKind.Weapon, 26f, 2.5f, 0.6f,  5f, "WEAPON04.CIF"),
    (6,  "df_mace",   "war hammer",     EquipKind.Weapon, 34f, 2.2f, 0.95f, 9f, "WEAPON07.CIF"),
    (10, "df_shield", "kite shield",    EquipKind.Shield, 0f,  0f,   0f,    7f, ""),
};
foreach (var e in equipment)
{
    records.Add(Flat(reader, textureDir, archive: 207, record: e.Record, id: e.Id + "_flat", metresPerPixel * 1.3f));
    if (e.Cif.Length > 0) records.Add(Viewmodel(reader, textureDir, e.Cif, e.Id + "_fp", swingRecord: 2));
    records.Add(Equipment(e.Id, e.Label, e.Kind, e.Damage, e.Reach, e.Cooldown, e.Weight, e.Cif.Length > 0));
}

// Flat textures, for the ground and for meshes: temperate grass, a boulder face, dungeon stone.
records.Add(FlatTexture(reader, textureDir, archive: 302, record: 2, id: "df_ground"));
records.Add(FlatTexture(reader, textureDir, archive: 302, record: 10, id: "df_rock_face"));
records.Add(FlatTexture(reader, textureDir, archive: 322, record: 2, id: "df_stone"));

records.Add(Scene(creatures.Select(c => c.Id).ToArray(), people.Select(p => p.Id).ToArray()));

var written = records.Where(r => r.Length > 0).ToList();
var dataFile = Path.Combine(outDir, "data", "daggerfall.json");
Directory.CreateDirectory(Path.GetDirectoryName(dataFile)!);
File.WriteAllText(dataFile, "[\n" + string.Join(",\n\n", written) + "\n]\n");

Console.WriteLine($"\n{written.Count} record blocks -> {dataFile}");
Console.WriteLine($"PNGs -> {textureDir}");
Console.WriteLine("Run the game and the Daggerfall content is in the scene; delete daggerfall.json to go back.");
return 0;

static int Missing(string what, string path)
{
    Console.Error.WriteLine($"Can't find the {what}: {path}");
    Console.Error.WriteLine("Pass --arena2 / --connect if your install is elsewhere, or --help.");
    return 1;
}

// ---- importers ----------------------------------------------------------------------------------

// One monster, every animation in a single sheet laid out [direction][frame], so a frame index is
// direction * columns + column and a clip's `dirs` can list them directly.
static string Creature(Arena2Reader reader, string outDir, int archive, string id, float metresPerPixel,
                       (int First, int Last) walk, (int First, int Last) attack, int hitFrame)
{
    var walking = reader.Read(archive, walk.First, walk.Last);
    var attacking = reader.Read(archive, attack.First, attack.Last);
    if (walking.Count == 0) { Console.Error.WriteLine($"TEXTURE.{archive:000}: no frames in records {walk.First}-{walk.Last}"); return ""; }

    // One cell large enough for every frame, so the grid stays regular and the records stay simple.
    int cellW = Math.Max(walking.Max(f => f.Width), attacking.Count > 0 ? attacking.Max(f => f.Width) : 0);
    int cellH = Math.Max(walking.Max(f => f.Height), attacking.Count > 0 ? attacking.Max(f => f.Height) : 0);
    int walkFrames = walking.Max(f => f.Index) + 1;
    int attackFrames = attacking.Count > 0 ? attacking.Max(f => f.Index) + 1 : 0;
    int directions = walking.Max(f => f.Record) - walk.First + 1;
    int columns = walkFrames + attackFrames;

    var sheet = new Sheet(cellW * columns, cellH * directions);
    foreach (var f in walking) sheet.Blit(f, cellW, cellH, f.Index, f.Record - walk.First);
    foreach (var f in attacking) sheet.Blit(f, cellW, cellH, walkFrames + f.Index, f.Record - attack.First);
    sheet.Save(Path.Combine(outDir, id + ".png"));

    var frames = new StringBuilder();
    for (int d = 0; d < directions; d++)
        for (int c = 0; c < columns; c++)
            frames.Append($"      {{ \"rect\": [{c * cellW}, {d * cellH}, {cellW}, {cellH}] }},\n");

    string Dirs(int first, int count) => "[" + string.Join(", ",
        Enumerable.Range(0, directions).Select(d => "[" + string.Join(", ",
            Enumerable.Range(0, count).Select(n => (d * columns + first + n).ToString())) + "]")) + "]";

    string size = Size(cellW * metresPerPixel, cellH * metresPerPixel);
    Console.WriteLine($"TEXTURE.{archive:000} -> {id}.png  {sheet.Width}x{sheet.Height}px  " +
                      $"{directions} directions, {walkFrames} walk + {attackFrames} attack frames, {size} m");

    string attackClip = attackFrames == 0 ? "" : $$"""
        ,
            "attack": {
              "fps": 10,
              "loop": false,
              "dirs": {{Dirs(walkFrames, attackFrames)}},
              "events": [ { "frame": {{Math.Min(hitFrame, attackFrames - 1)}}, "name": "hit" } ]
            }
        """.TrimEnd();

    return $$"""
      {
        // Daggerfall TEXTURE.{{archive:000}}: five views, the other three mirrored (06 §3.8).
        "type": "sprite_sheet",
        "id": "{{id}}",
        "texture": "textures/daggerfall/{{id}}.png",
        "directions": {{directions}},
        "size": {{size}},
        "material": "sage:sprite_lit",
        "frames": [
    {{frames.ToString().TrimEnd('\n', ',')}}
        ],
        "animations": {
          "idle": { "fps": 8, "loop": true, "dirs": {{Dirs(0, walkFrames)}} }{{attackClip}}
        }
      }
    """;
}

// A single-image billboard: a tree, a bush, a rock.
static string Flat(Arena2Reader reader, string outDir, int archive, int record, string id, float metresPerPixel)
{
    var frames = reader.Read(archive, record, record);
    if (frames.Count == 0) { Console.Error.WriteLine($"TEXTURE.{archive:000} record {record}: nothing there"); return ""; }

    var frame = frames[0];
    var sheet = new Sheet(frame.Width, frame.Height);
    sheet.Blit(frame, frame.Width, frame.Height, 0, 0);
    sheet.Save(Path.Combine(outDir, id + ".png"));

    string size = Size(frame.Width * metresPerPixel, frame.Height * metresPerPixel);
    Console.WriteLine($"TEXTURE.{archive:000}:{record} -> {id}.png  {frame.Width}x{frame.Height}px  {size} m");

    return $$"""
      {
        // Daggerfall TEXTURE.{{archive:000}}, record {{record}}.
        "type": "sprite_sheet",
        "id": "{{id}}",
        "texture": "textures/daggerfall/{{id}}.png",
        "directions": 1,
        "size": {{size}},
        "material": "sage:sprite_lit",
        "frames": [ { "rect": [0, 0, {{frame.Width}}, {{frame.Height}}] } ]
      }
    """;
}

// A wall or ground texture, as a material meshes and terrain can use.
static string FlatTexture(Arena2Reader reader, string outDir, int archive, int record, string id)
{
    var frames = reader.Read(archive, record, record);
    if (frames.Count == 0) { Console.Error.WriteLine($"TEXTURE.{archive:000} record {record}: nothing there"); return ""; }

    var frame = frames[0];
    var sheet = new Sheet(frame.Width, frame.Height);
    sheet.Blit(frame, frame.Width, frame.Height, 0, 0, opaque: true);
    sheet.Save(Path.Combine(outDir, id + ".png"));
    Console.WriteLine($"TEXTURE.{archive:000}:{record} -> {id}.png  {frame.Width}x{frame.Height}px  material sandbox:{id}");

    return $$"""
      {
        // Daggerfall TEXTURE.{{archive:000}}, record {{record}}. A texture is just a material param
        // (07 §3.3), so this inherits the engine's lit material and swaps the albedo.
        "type": "material",
        "id": "{{id}}",
        "base": "sage:lit_default",
        "params": { "Albedo": "textures/daggerfall/{{id}}.png" }
      }
    """;
}

// An item record, plus whatever equipping it gives you: a weapon's swing, or a shield's armour.
static string Equipment(string id, string label, EquipKind kind, float damage, float reach, float cooldown, float weight, bool hasViewmodel)
{
    if (kind == EquipKind.Shield)
        return $$"""
      {
        // Armour as an effect, so the same record could come from a spell (16 §3.3).
        "type": "effect",
        "id": "{{id}}_guard",
        "duration": "Infinite",
        "modifiers": [ { "attribute": "sage:armor", "op": "Add", "value": 25 } ]
      },

      {
        "type": "item",
        "id": "{{id}}",
        "label": "{{label}}",
        "sheet": "{{id}}_flat",
        "slot": "OffHand",
        "effects": ["{{id}}_guard"],
        "weight": {{F(weight)}},
        "value": 60
      }
    """;

    // A weapon with a first-person sheet says so on its attack record, which is where the HUD looks.
    // JSON is happy with a leading comma on its own line, which saves escaping quotes in here.
    string viewmodelLine = hasViewmodel ? $$"""

        , "viewmodel": "{{id}}_fp"
        """ : "";

    return $$"""
      {
        "type": "attack",
        "id": "{{id}}_swing",
        "base": "sage:default_attack",
        "damage": {{F(damage)}},
        "reach": {{F(reach)}},
        "radius": 0.4,
        "windupTime": 0.25,
        "recoverTime": 0.2,
        "cooldown": {{F(cooldown)}}{{viewmodelLine}}
      },

      {
        // Equipping it hands `attack` to the wielder's Melee: combat never learns that swords exist.
        "type": "item",
        "id": "{{id}}",
        "label": "{{label}}",
        "sheet": "{{id}}_flat",
        "slot": "MainHand",
        "attack": "{{id}}_swing",
        "weight": {{F(weight)}},
        "value": 120
      }
    """;
}

// The first-person view of a weapon, as one row of frames: the idle pose, then one swing. The HUD
// plays frame 0 at rest and the rest across the wind-up and recovery (13 §3).
static string Viewmodel(Arena2Reader reader, string outDir, string cif, string id, int swingRecord)
{
    var idle = reader.ReadCif(cif, 0, 0);
    var swing = reader.ReadCif(cif, swingRecord, swingRecord);
    if (idle.Count == 0) { Console.Error.WriteLine($"{cif}: no idle frame"); return ""; }

    var all = new List<Arena2Reader.Frame>(idle);
    all.AddRange(swing);
    int cellW = all.Max(f => f.Width), cellH = all.Max(f => f.Height);

    var sheet = new Sheet(cellW * all.Count, cellH);
    for (int i = 0; i < all.Count; i++) sheet.Blit(all[i], cellW, cellH, i, 0);
    sheet.Save(Path.Combine(outDir, id + ".png"));

    var frames = new StringBuilder();
    for (int i = 0; i < all.Count; i++)
        frames.AppendLine($"      {{ \"rect\": [{i * cellW}, 0, {cellW}, {cellH}] }},");

    Console.WriteLine($"{cif} -> {id}.png  {sheet.Width}x{sheet.Height}px  1 idle + {swing.Count} swing frames");

    return $$"""
      {
        // Daggerfall {{cif}}: the weapon as its wielder sees it.
        "type": "sprite_sheet",
        "id": "{{id}}",
        "texture": "textures/daggerfall/{{id}}.png",
        "directions": 1,
        "frames": [
    {{frames.ToString().TrimEnd()}}
        ]
      }
    """;
}

// Where the imported art lands in the test scene. The layout is generated rather than written out,
// so adding a creature to the table above puts it in the line-up without any more editing.
static string Scene(string[] creatures, string[] people)
{
    var blocks = new List<string>
    {
        """
      {
        // The ground wears Daggerfall's temperate grass: patching the engine's material rather than
        // replacing it is what record patches are for (05 §3.5).
        "type": "material",
        "id": "sage:terrain_default",
        "patch": true,
        "params": { "Albedo": "textures/daggerfall/df_ground.png" }
      }
    ""","""
      {
        // The crates are Daggerfall boulder.
        "type": "material",
        "id": "crate",
        "patch": true,
        "params": { "Albedo": "textures/daggerfall/df_rock_face.png", "AlbedoColor": [1, 1, 1, 1] }
      }
    ""","""
      {
        // Dungeon stone, for the ruin.
        "type": "material",
        "id": "df_stone_wall",
        "base": "sage:lit_default",
        "params": { "Albedo": "textures/daggerfall/df_stone.png" }
      }
    ""","""
      {
        // Every imported creature fights with the records the placeholder one uses (16 §3.2): the
        // art is all that changed, which is the point of keeping gameplay in data.
        "type": "spawn",
        "id": "df_creature",
        "abstract": true,
        "animate": true,
        "character": true,
        "attack": "claws",
        "effects": ["tough_hide"]
      }
    """,
    };

    // A line-up you can walk along and inspect from any angle, and hit: they stand still because
    // nothing is driving them (a character without a controller simply holds its ground).
    float x = -(creatures.Length - 1) * 1.5f;
    foreach (var id in creatures)
    {
        blocks.Add($$"""
      { "type": "spawn", "id": "df_{{id}}", "base": "df_creature", "name": "{{id}}", "sheet": "{{id}}", "position": [{{F(x)}}, 0, -16], "yaw": 180 }
    """);
        x += 3f;
    }

    // Loot: a weapon where you start, and the rest out by the ruin.
    var loot = new (string Id, float X, float Z)[] { ("df_sword", 1.2f, 3.5f), ("df_mace", -13f, -16f), ("df_shield", -17f, -18f) };
    for (int i = 0; i < loot.Length; i++)
        blocks.Add($$"""
      { "type": "spawn", "id": "df_loot_{{i}}", "name": "{{loot[i].Id}}", "item": "{{loot[i].Id}}", "position": [{{F(loot[i].X)}}, 0, {{F(loot[i].Z)}}] }
    """);

    // Two that come after you, from opposite sides.
    blocks.Add("""
      { "type": "spawn", "id": "df_hunter_a", "base": "df_creature", "name": "skeleton hunter", "sheet": "skeleton", "position": [6, 0, -9], "yaw": 180, "ai": true }
    """);
    blocks.Add("""
      { "type": "spawn", "id": "df_hunter_b", "base": "df_creature", "name": "orc hunter", "sheet": "orc", "position": [-6, 0, -10], "yaw": 180, "ai": true }
    """);

    // Townspeople: flats with a collider, so they are something to walk around rather than through.
    var wherePeople = new (float X, float Z, string Id)[] { (-11, -6, ""), (-9.5f, -8, ""), (10.5f, -7, ""), (12, -9, "") };
    for (int i = 0; i < people.Length && i < wherePeople.Length; i++)
        blocks.Add($$"""
      { "type": "spawn", "id": "df_person_{{i}}", "sheet": "{{people[i]}}", "name": "{{people[i]}}", "collider": "Capsule", "colliderSize": [0.35, 1.8, 0], "position": [{{F(wherePeople[i].X)}}, 0, {{F(wherePeople[i].Z)}}] }
    """);

    // A ruin to break the line of sight: four walls with a gap, which is also something for the AI
    // to lose you behind (16 §3.4).
    var walls = new (float X, float Z, float W, float D)[] { (-15, -20, 8, 0.6f), (-15, -14, 3, 0.6f), (-18.6f, -17, 0.6f, 6), (-11.4f, -17, 0.6f, 6) };
    for (int i = 0; i < walls.Length; i++)
        blocks.Add($$"""
      { "type": "spawn", "id": "df_wall_{{i}}", "name": "ruin wall {{i}}", "boxMesh": [{{F(walls[i].W)}}, 3, {{F(walls[i].D)}}], "colliderSize": [{{F(walls[i].W)}}, 3, {{F(walls[i].D)}}], "material": "df_stone_wall", "position": [{{F(walls[i].X)}}, 1.5, {{F(walls[i].Z)}}] }
    """);

    // Woodland: trees get a collider, undergrowth does not.
    var trees = new (string Id, float X, float Z, bool Solid)[]
    {
        ("df_tree_pine", -8, -12, true), ("df_tree_oak", 9, -13, true), ("df_tree_autumn", -12, -17, true),
        ("df_tree_gnarled", 13, -18, true), ("df_tree_dead", 4, -21, true), ("df_tree_pine", -20, -10, true),
        ("df_stump", -5, -7, false), ("df_rocks", 7, -5, false), ("df_rock", -3, -13, false),
        ("df_bush", 2, -6, false), ("df_bush", -7, -19, false), ("df_grass", 5, -12, false),
        ("df_grass", -2, -18, false), ("df_grass", 11, -11, false),
    };
    for (int i = 0; i < trees.Length; i++)
    {
        var t = trees[i];
        // A plain escaped string: a raw one would need its own delimiter run to survive the quotes.
        string collider = t.Solid ? "\"collider\": \"Capsule\", \"colliderSize\": [0.4, 3, 0], " : "";
        blocks.Add($$"""
      { "type": "spawn", "id": "df_flat_{{i}}", "sheet": "{{t.Id}}", "name": "{{t.Id}}_{{i}}", {{collider}}"position": [{{F(t.X)}}, 0, {{F(t.Z)}}] }
    """);
    }

    return string.Join(",\n\n", blocks.Select(b => b.TrimEnd()));
}

static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

static string Size(float w, float h) =>
    $"[{w.ToString("0.##", CultureInfo.InvariantCulture)}, {h.ToString("0.##", CultureInfo.InvariantCulture)}]";

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) continue;
        string key = args[i][2..];
        result[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "";
    }
    return result;
}

// games/Sandbox/content, found from wherever the tool was run.
static string DefaultContentDir()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "Sage.sln")))
            return Path.Combine(d.FullName, "games", "Sandbox", "content");
    return Path.Combine(Directory.GetCurrentDirectory(), "games", "Sandbox", "content");
}

// Kinds of equipment this tool knows how to write records for.
enum EquipKind { Weapon, Shield }
