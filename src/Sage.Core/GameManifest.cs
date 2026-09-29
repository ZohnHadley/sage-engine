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
//     "modules": { "disable": [] }
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
        return manifest;
    }
}
