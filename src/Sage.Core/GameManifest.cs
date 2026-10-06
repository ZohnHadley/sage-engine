#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Sage.Core;

// game.json, like Source's gameinfo.txt (docs/design/01 §3.3):
//   {
//     "name": "Sandbox", "id": "sandbox",
//     "assembly": "bin/{config}/net8.0/Sandbox.dll",   // {config} = the build's configuration name
//     "mounts": ["content"],                           // relative to the game folder, mounted in order
//     "modules": { "disable": [] },
//     "kits": ["sage.kits.rpg"],                       // kits the game is built on (issue #27)
//     "scene": "main",                                 // the scene every world starts in (issue #29)
//     "title": "rpg:title",                            // the screen shown before it starts (issue #342)
//     "sage": "^0.1",                                  // the engine versions it was made for (issue #31)
//     "version": "1.0.0",                              // the game's own version, for a mod's "gameVersion" (4j)
//     "cook": { "uncompressed": ["textures/ui/**"] }   // what `sage cook` leaves uncompressed (issue #302)
//   }
public sealed class GameManifest
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Assembly { get; set; } = "";
    public List<string> Mounts { get; set; } = new();
    public string ModsDirectory { get; set; } = "mods";
    public ModuleSettings Modules { get; set; } = new();

    // Which of the engine's simulation plugins this game uses, by id: "sage.physics3d", or
    // "sage.gameplay.*" for a family. Plugins they require come too. Null (the key left out) means all of
    // them, as before; an empty list means none — a game with no physics and no gameplay (issue #12).
    public List<string>? Plugins { get; set; }

    // The kits this game is built on, by plugin id: "sage.kits.rpg" (issue #27). A kit is not part of
    // the base engine, so it is never loaded unless a game names it here — and a game that names one
    // also references its assembly, compile-time, like the base. The host finds `Sage.Kits.Rpg.dll`
    // (an id's assembly is the id with each part capitalised) beside the game's assembly, its
    // `modules.add` assemblies or the host itself; a host with a window loads `….Client.dll` too.
    public List<string> Kits { get; set; } = new();

    // The `scene` record every world starts in: "main" (this game's namespace) or "ns:main". Left out,
    // worlds start empty and the game's rules place what they want (issue #29).
    public string? Scene { get; set; }

    // The `screen` record a game shows before its world starts (issue #342): "rpg:title" or "title" (this
    // game's namespace). A host with a window opens it over a world that waits, paused and with no scene
    // placed, until the player chooses a new game or a save to load. Left out, the world starts at once.
    public string? Title { get; set; }

    // The engine versions this game was made for, as a range: "^0.1", ">=0.1 <0.3" (VersionRange; issue
    // #31). Checked when the manifest loads, against BuildInfo.EngineSemVersion, so a game run on an engine
    // it was not made for stops with that rather than with whatever broke first. Left out, any engine.
    public string? Sage { get; set; }

    // This game's own version, "1.4.0": what a mod's "gameVersion" range is checked against (phase 4j).
    // Left out, the check is skipped with a note.
    public string? Version { get; set; }

    // How `sage cook` (which `sage package` runs) cooks this game's content (issue #302): textures are
    // block-compressed (BC1/BC3) unless "compress" is false or the texture's path, inside its mount, matches
    // one of "uncompressed" ("textures/ui/**", "*.png"): pixel art, UI and fonts usually want their exact
    // pixels, which a cooked texture keeps as premultiplied RGBA.
    public CookSettings Cook { get; set; } = new();

    public sealed class CookSettings
    {
        public bool Compress { get; set; } = true;
        public List<string> Uncompressed { get; set; } = new();
    }

    public sealed class ModuleSettings
    {
        public List<string> Disable { get; set; } = new();
        public List<string> Add { get; set; } = new();
    }

    // Folder the manifest was loaded from; mounts and the assembly path are relative to it.
    public string Directory { get; private set; } = "";

    public string AssemblyPath => Resolve(Assembly);

    // `modules.add`: further assemblies whose IModules are added after the game's own (01 §3.3).
    public IEnumerable<string> ModuleAssemblies => Modules.Add.Select(Resolve);

    private string Resolve(string path) =>
        Path.GetFullPath(Path.Combine(Directory, path.Replace("{config}", BuildInfo.ConfigurationName)));

    // The game folder a host runs (issue #32): `-game <folder>`, else a `game` folder beside the executable
    // (how a packaged game ships). Nothing else: a dev build used to load games/Sandbox when it found this
    // repository above it, so a forgotten -game ran the wrong game without a word. Now it is an error that
    // says what to pass, and lists the repository's games when there is one.
    public static string Locate(string? gameOption, string executableDirectory)
    {
        if (!string.IsNullOrEmpty(gameOption))
            return Path.GetFullPath(gameOption);
        string beside = Path.Combine(executableDirectory, "game");
        if (File.Exists(Path.Combine(beside, "game.json")))
            return Path.GetFullPath(beside);

        string message = "No game to run: pass -game <folder> (a folder with a game.json), or put the game in a " +
                         $"'game' folder beside the executable ({Path.GetFullPath(executableDirectory)}). A game built " +
                         "with Sage.Sdk passes -game itself: `dotnet run` in its folder.";
        for (var dir = new DirectoryInfo(executableDirectory); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "Sage.sln"))) continue;
            var games = new[] { "games", Path.Combine("tests", "games") }
                .Select(folder => Path.Combine(dir.FullName, folder))
                .Where(System.IO.Directory.Exists)
                .SelectMany(System.IO.Directory.GetDirectories)
                .Where(game => File.Exists(Path.Combine(game, "game.json")))
                .Select(game => Path.GetRelativePath(dir.FullName, game).Replace('\\', '/'))
                .OrderBy(game => game, StringComparer.Ordinal)
                .ToList();
            if (games.Count > 0)
                message += $" Games in {dir.FullName}: {string.Join(", ", games)}.";
            break;
        }
        throw new FileNotFoundException(message);
    }

    public static GameManifest Load(string gameDirectory)
    {
        string path = Path.Combine(gameDirectory, "game.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"No game.json in {Path.GetFullPath(gameDirectory)} (pass -game <folder>).");
        GameManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<GameManifest>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                // A misspelt key ("mount", "modules": { "disabled": … }) used to be ignored, so the game
                // silently ran without what it asked for (issue #12).
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }
        // "assembly" is optional: a game made only of data and engine modules has no code of its own.
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id))
            throw new InvalidDataException($"{path}: \"id\" is required.");
        RecordId.Parse(manifest.Id, manifest.Id);   // the id is the game's record namespace: same rules
        manifest.Directory = Path.GetFullPath(gameDirectory);
        if (!string.IsNullOrWhiteSpace(manifest.Version))
        {
            try { SemVersion.Parse(manifest.Version, $"{path}: \"version\""); }
            catch (FormatException ex) { throw new InvalidDataException(ex.Message, ex); }
        }
        manifest.CheckEngine(BuildInfo.EngineSemVersion, BuildInfo.EngineVersion, path);
        return manifest;
    }

    // "sage", against the engine running it; the engine's version is a parameter for the tests.
    internal void CheckEngine(SemVersion engine, string engineText, string path)
    {
        if (string.IsNullOrWhiteSpace(Sage)) return;
        VersionRange range;
        try { range = VersionRange.Parse(Sage, $"{path}: \"sage\""); }
        catch (FormatException ex) { throw new InvalidDataException(ex.Message, ex); }
        if (!range.Contains(engine))
            throw new InvalidDataException($"{path}: this game was made for Sage {range} (\"sage\"), but this is Sage {engineText}. " +
                                           "Run it with an engine in that range, or update the game and its \"sage\" range.");
    }
}
