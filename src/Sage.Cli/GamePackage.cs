#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage.Cli;

// `sage package <game> --out <dir>` (issue #293, REDESIGN §4.8): a folder a player can run, made of a Shipping
// host and the game beside it.
//
//   <out>/Sage.Host(.exe), Sage.*.dll, MonoGame, runtimes/, Content/   the host's build output, as built
//   <out>/game/game.json                                                 the manifest, its paths rewritten
//   <out>/game/bin/<Name>.dll                                            "assembly" and "modules.add" (and a kit
//                                                                        the host does not have)
//   <out>/game/<mount>/                                                  each mount, copied
//   <out>/game/<modsDirectory>/                                          the game's own mods, when it has any
//
// The host runs the `game` folder beside it when no -game is given (GameManifest.Locate), so the folder starts
// by running Sage.Host. Nothing else of the game folder is copied: not its sources, project files, obj/ or its
// tools/. The host must be a Shipping build: one with the editor or ImGui in it (Debug, Development) is
// refused rather than stripped, because its own code still names them. The `sage` CLI, which the Player
// package keeps beside its host, is left out: a player has no use for it.
//
// The game must have been built in the same configuration: "{config}" in game.json is that configuration
// when its files are found, and the written manifest names the copies with no "{config}" left in it.
internal sealed class PackageOptions
{
    public required string GameDirectory { get; init; }
    public required string OutputDirectory { get; init; }
    public required string HostDirectory { get; init; }

    // What "{config}" means in the game's manifest when its built files are found.
    public string Configuration { get; init; } = "Shipping";
}

internal sealed class PackageResult
{
    public List<string> Errors { get; } = new();

    // What was written under the output folder, relative to it, with '/' separators.
    public List<string> Files { get; } = new();

    public bool Ok => Errors.Count == 0;
}

internal static class GamePackage
{
    // The developer's tools a host built with SAGE_DEV carries: the editor, its headless model, ImGui and its
    // native library, and ImGui's saved layout.
    internal static readonly string[] DevToolPatterns = { "Sage.Editor.*", "Sage.Editing.*", "*ImGui*", "cimgui*", "imgui.ini" };

    // The `sage` CLI beside a Player package's host, left out of the package.
    internal static readonly string[] CliFiles = { "sage", "sage.exe", "sage.dll", "sage.pdb", "sage.deps.json", "sage.runtimeconfig.json" };

    public static bool IsDevTool(string fileName) => DevToolPatterns.Any(pattern => Matches(fileName, pattern));

    public static PackageResult Run(PackageOptions options)
    {
        var result = new PackageResult();
        string game = Path.GetFullPath(options.GameDirectory);
        string host = Path.GetFullPath(options.HostDirectory);
        string output = Path.GetFullPath(options.OutputDirectory);

        // ---- What goes in, checked before anything is written ----
        if (!File.Exists(Path.Combine(host, "Sage.Host.dll")))
        {
            result.Errors.Add($"No host in {host} (no Sage.Host.dll): build it with dotnet build src/Sage.Host -c {options.Configuration}, or pass --host.");
            return result;
        }
        var devTools = Directory.EnumerateFiles(host, "*", SearchOption.AllDirectories)
            .Where(file => IsDevTool(Path.GetFileName(file))).Select(file => Relative(host, file)).ToList();
        if (devTools.Count > 0)
        {
            result.Errors.Add($"The host in {host} has developer tools in it ({string.Join(", ", devTools.Take(4))}): it is a Debug or " +
                              "Development build. A package takes a Shipping host (dotnet build src/Sage.Host -c Shipping).");
            return result;
        }

        GameManifest manifest;
        JsonObject json;
        try
        {
            manifest = GameManifest.Load(game);
            json = JsonNode.Parse(File.ReadAllText(Path.Combine(game, "game.json")), new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                                  new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                   ?? throw new InvalidDataException($"{Path.Combine(game, "game.json")}: not a JSON object.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or FormatException or JsonException)
        {
            result.Errors.Add(ex.Message);
            return result;
        }

        string Built(string path) => Path.GetFullPath(Path.Combine(game, path.Replace("{config}", options.Configuration)));

        // The assemblies: "assembly" first, then "modules.add", each into game/bin/ by its file name.
        var assemblies = new List<(string Source, string Target)>();
        var assemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? AddAssembly(string named)
        {
            string source = Built(named);
            if (!File.Exists(source))
            {
                result.Errors.Add($"game.json names \"{named}\", and there is no {source}: build the game in {options.Configuration} first " +
                                  $"(dotnet build -c {options.Configuration}).");
                return null;
            }
            string target = "bin/" + Path.GetFileName(source);
            if (!assemblyNames.Add(Path.GetFileName(source)))
            {
                result.Errors.Add($"game.json names two assemblies called {Path.GetFileName(source)}; a package puts them in one folder.");
                return null;
            }
            assemblies.Add((source, target));
            return target;
        }
        string? assembly = string.IsNullOrWhiteSpace(manifest.Assembly) ? null : AddAssembly(manifest.Assembly);
        var modules = manifest.Modules.Add.Select(AddAssembly).ToList();

        // A kit is loaded from beside the host or beside the game's assemblies (ModuleManager.LoadKit): one the host
        // does not have, found beside the game's, goes with them.
        foreach (string kit in manifest.Kits)
        {
            string name = ModuleManager.KitAssemblyName(kit);
            foreach (string file in new[] { name + ".dll", name + ".Client.dll" })
            {
                if (File.Exists(Path.Combine(host, file)) || assemblyNames.Contains(file)) continue;
                string? beside = assemblies.Select(a => Path.Combine(Path.GetDirectoryName(a.Source)!, file)).FirstOrDefault(File.Exists);
                if (beside == null) continue;   // the host says which kit is missing when it boots
                assemblyNames.Add(file);
                assemblies.Add((beside, "bin/" + file));
            }
        }

        // The mounts: one inside the game folder keeps its path; one outside it (a shared folder) goes under mounts/.
        var mounts = new List<(string Source, string Target)>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string mount in manifest.Mounts)
        {
            string source = Path.GetFullPath(Path.Combine(game, mount));
            if (!Directory.Exists(source))
            {
                result.Errors.Add($"game.json mounts \"{mount}\", and there is no folder {source}.");
                continue;
            }
            string target = Inside(game, source) && source != game ? Relative(game, source)
                : "mounts/" + Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
            string unique = target;
            for (int n = 2; !targets.Add(unique); n++) unique = $"{target}-{n}";
            mounts.Add((source, unique));
        }
        if (mounts.Any(m => m.Source == game))
            result.Errors.Add("game.json mounts the game folder itself: a package copies mounts, so mount a content folder inside it.");

        // The game's own mods (game.json "modsDirectory"), when it ships some.
        string? mods = null;
        if (!string.IsNullOrWhiteSpace(manifest.ModsDirectory))
        {
            string source = Path.GetFullPath(Path.Combine(game, manifest.ModsDirectory));
            if (Inside(game, source) && source != game && Directory.Exists(source)) mods = source;
        }

        if (output == game || Inside(output, game) || Inside(output, host) || Inside(host, output)
            || Inside(game, output) && mounts.Select(m => m.Source).Append(mods).OfType<string>().Any(source => source == output || Inside(source, output)))
            result.Errors.Add($"The output folder {output} overlaps the game or the host it is made from: package somewhere else.");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any() && !IsPackage(output))
            result.Errors.Add($"{output} is not empty and is not a package this command wrote: name a new folder (or an old package, which is replaced).");
        if (!result.Ok) return result;

        // ---- Writing ----
        if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        Directory.CreateDirectory(output);

        // The host, as built, less the CLI (and a `game` folder someone left beside it).
        foreach (string file in Directory.EnumerateFiles(host, "*", SearchOption.AllDirectories))
        {
            string relative = Relative(host, file);
            if (!relative.Contains('/') && CliFiles.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;
            if (relative.StartsWith("game/", StringComparison.OrdinalIgnoreCase)) continue;
            Copy(file, relative);
        }
        // The apphost starts the game, and a host from the Player package may have lost its executable bits
        // (NuGet does not keep them): anyone may run it.
        string apphost = Path.Combine(output, "Sage.Host");
        if (!OperatingSystem.IsWindows() && File.Exists(apphost))
            File.SetUnixFileMode(apphost, File.GetUnixFileMode(apphost) | UnixFileMode.UserRead | UnixFileMode.UserExecute
                                          | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        foreach (var (source, target) in assemblies)
        {
            Copy(source, "game/" + target);
            string pdb = Path.ChangeExtension(source, ".pdb");
            if (File.Exists(pdb)) Copy(pdb, "game/" + Path.ChangeExtension(target, ".pdb"));
        }
        foreach (var (source, target) in mounts) CopyTree(source, "game/" + target);
        if (mods != null) CopyTree(mods, "game/" + Relative(game, mods));

        // The manifest, everything as the game wrote it except the paths, which name the copies.
        if (assembly != null) Set(json, "assembly", JsonValue.Create(assembly));
        Set(json, "mounts", new JsonArray(mounts.Select(m => (JsonNode?)JsonValue.Create(m.Target)).ToArray()));
        if (Get(json, "modules") is JsonObject moduleSettings)
            Set(moduleSettings, "add", new JsonArray(modules.OfType<string>().Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()));
        string manifestText = $"// Written by `sage package` from the game's own game.json, built in {options.Configuration}.\n" +
                              json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        File.WriteAllText(Path.Combine(output, "game", "game.json"), manifestText);
        result.Files.Add("game/game.json");
        result.Files.Sort(StringComparer.Ordinal);
        return result;

        void Copy(string source, string relative)
        {
            string target = Path.Combine(output, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);   // keeps the apphost's executable bit on Unix
            result.Files.Add(relative.Replace('\\', '/'));
        }

        void CopyTree(string source, string relative)
        {
            Directory.CreateDirectory(Path.Combine(output, relative));
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                Copy(file, relative + "/" + Relative(source, file));
        }
    }

    // A folder this command wrote: the host and a game beside it.
    private static bool IsPackage(string folder) =>
        File.Exists(Path.Combine(folder, "Sage.Host.dll")) && File.Exists(Path.Combine(folder, "game", "game.json"));

    private static bool Inside(string folder, string path)
    {
        string relative = Path.GetRelativePath(folder, path);
        return relative == "." || !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static string Relative(string folder, string path) => Path.GetRelativePath(folder, path).Replace('\\', '/');

    // game.json's keys are matched without case, as GameManifest reads them.
    private static JsonNode? Get(JsonObject json, string key) =>
        json.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    private static void Set(JsonObject json, string key, JsonNode value)
    {
        string existing = json.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key;
        json[existing] = value;
    }

    private static bool Matches(string fileName, string pattern)
    {
        string regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(fileName, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
