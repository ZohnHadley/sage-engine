#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Sage.Core;

// mod.json, at the root of a mod's folder (docs/design/17-modding.md; phase 4j):
//   {
//     "id": "better_blades", "name": "Better Blades", "version": "1.2.0", "author": "...", "description": "...",
//     "game": "village", "gameVersion": ">=0.1",           // which game, and which of its versions
//     "sage": ">=0.1",                                     // the engine versions it was made for
//     "dependencies": { "other_mod": "^1.0" },             // mods that must be active, loaded before it
//     "loadAfter": ["x"], "loadBefore": ["y"],             // order, when those mods are there
//     "incompatible": ["z"]                                // refused when one of these is loaded before it
//   }
// Read as strictly as game.json: an unknown key is an error. "assemblies" and "kind" are known only so a
// code mod can be refused by name (ModLoadOrder): code mods are phase 9.
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // data mods (phase 4j): may change before 1.0
public sealed class ModManifest
{
    // The mod's record namespace and its mount's name (mods/<id>): what its data/ defines is <id>:...
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "0.0.0";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";

    // The game's id this mod is for; "*" or left out, any.
    public string? Game { get; set; }
    // A range against game.json's "version"; left out, any.
    public string? GameVersion { get; set; }
    // A range against the engine, as game.json's "sage" is. Left out, any.
    public string? Sage { get; set; }

    // id -> version range ("" or "*": any).
    public Dictionary<string, string> Dependencies { get; set; } = new();
    public List<string> LoadAfter { get; set; } = new();
    public List<string> LoadBefore { get; set; } = new();
    public List<string> Incompatible { get; set; } = new();

    // Not supported in phase 4j: a mod that names either is refused with "code mods are phase 9".
    public List<string> Assemblies { get; set; } = new();
    public string? Kind { get; set; }

    // The mod's folder (its mount root), set by Load.
    public string Directory { get; private set; } = "";

    public SemVersion SemVersion => SemVersion.Parse(Version, $"mod '{Id}' \"version\"");

    public bool AsksForCode => Assemblies.Count > 0 || string.Equals(Kind, "code", StringComparison.OrdinalIgnoreCase);

    public static ModManifest Load(string folder)
    {
        string path = Path.Combine(folder, "mod.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"No mod.json in {Path.GetFullPath(folder)}.");
        ModManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id))
            throw new InvalidDataException($"{path}: \"id\" is required.");
        try
        {
            manifest.Validate();
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }
        manifest.Directory = Path.GetFullPath(folder);
        if (manifest.Name.Length == 0) manifest.Name = manifest.Id;
        return manifest;
    }

    // The id follows the namespace rules (lower case letters, digits, _ . -; no colon). Everything that is
    // a version or a range must parse, so a typo stops here rather than at the first comparison.
    private void Validate()
    {
        if (Id.Contains(':') || !IsNamespace(Id))
            throw new FormatException($"\"id\" '{Id}' is not a valid namespace (lower case letters, digits, _ . - only).");
        _ = SemVersion;
        if (!string.IsNullOrWhiteSpace(GameVersion)) _ = VersionRange.Parse(GameVersion, "\"gameVersion\"");
        if (!string.IsNullOrWhiteSpace(Sage)) _ = VersionRange.Parse(Sage, "\"sage\"");
        foreach (var (dep, range) in Dependencies)
        {
            if (!IsNamespace(dep)) throw new FormatException($"\"dependencies\" names '{dep}', which is not a mod id.");
            _ = VersionRange.Parse(range ?? "", $"\"dependencies\" '{dep}'");
        }
        foreach (string other in LoadAfter.Concat(LoadBefore).Concat(Incompatible))
            if (!IsNamespace(other)) throw new FormatException($"'{other}' is not a mod id (in loadAfter, loadBefore or incompatible).");
        if (Kind != null && !string.Equals(Kind, "data", StringComparison.OrdinalIgnoreCase) && !string.Equals(Kind, "code", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"\"kind\" is 'data' or 'code', not '{Kind}'.");
    }

    private static bool IsNamespace(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s)
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_' || c == '.' || c == '-')) return false;
        return true;
    }

    public override string ToString() => $"{Id} {Version}";
}
