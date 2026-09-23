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

var textureDir = Path.Combine(outDir, "textures", "daggerfall");
Directory.CreateDirectory(textureDir);
var reader = new Arena2Reader(connect, arena2);
var records = new List<string>();

// ---- what to import ---------------------------------------------------------------------------
// Enough for the test scene: something to fight, something to stand on, something to hide behind.

// Skeleton Warrior: 5 directions x 4 walking frames, then 5 x 6 attacking frames.
records.Add(Creature(reader, textureDir, archive: 270, id: "skeleton", metresPerPixel,
                     walk: (0, 4), attack: (5, 9), hitFrame: 2));

// Temperate ground and rock: one for the terrain to wear, one for the crates.
records.Add(FlatTexture(reader, textureDir, archive: 302, record: 2, id: "df_ground"));
records.Add(FlatTexture(reader, textureDir, archive: 302, record: 10, id: "df_rock"));

// Woodland flats: two trees and a bush, as one-direction billboards.
foreach (var (record, id, scale) in new[] { (13, "df_tree_a", 2f), (16, "df_tree_b", 2f), (19, "df_bush", 1.5f) })
    records.Add(Flat(reader, textureDir, archive: 504, record: record, id: id, metresPerPixel * scale));

records.Add(Scene());

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

// Where the imported art lands in the test scene.
static string Scene() => """
      {
        // The ground wears Daggerfall's temperate terrain texture: patching the engine's material
        // rather than replacing it is what record patches are for (05 §3.5).
        "type": "material",
        "id": "sage:terrain_default",
        "patch": true,
        "params": { "Albedo": "textures/daggerfall/df_ground.png" }
      },

      {
        // The crates are Daggerfall rock.
        "type": "material",
        "id": "crate",
        "patch": true,
        "params": { "Albedo": "textures/daggerfall/df_rock.png", "AlbedoColor": [1, 1, 1, 1] }
      },

      {
        // The skeleton fights with the same records the placeholder creature does (16 §3.2): the art
        // is all that changed, which is the point of keeping gameplay in data.
        "type": "spawn",
        "id": "skeleton_base",
        "abstract": true,
        "sheet": "skeleton",
        "animate": true,
        "attack": "claws",
        "effects": ["tough_hide"]
      },

      { "type": "spawn", "id": "df_skeleton", "base": "skeleton_base", "name": "skeleton", "position": [3.5, 0, -9], "yaw": 180, "ai": true },

      { "type": "spawn", "id": "df_tree_1", "sheet": "df_tree_a", "name": "df_tree_1", "collider": "Capsule", "colliderSize": [0.4, 3, 0], "position": [-7, 0, -9] },
      { "type": "spawn", "id": "df_tree_2", "sheet": "df_tree_b", "name": "df_tree_2", "collider": "Capsule", "colliderSize": [0.4, 3, 0], "position": [8, 0, -13] },
      { "type": "spawn", "id": "df_bush_1", "sheet": "df_bush",   "name": "df_bush_1", "position": [-3.5, 0, -6] }
    """;

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
