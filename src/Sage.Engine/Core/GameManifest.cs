#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace sage_engine;

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
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Assembly))
            throw new InvalidDataException($"{path}: \"id\" and \"assembly\" are required.");
        RecordId.Parse(manifest.Id, manifest.Id);   // the id is the game's record namespace: same rules
        manifest.Directory = Path.GetFullPath(gameDirectory);
        return manifest;
    }
}
