#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Sage.Core;

// `sage mods pack <mod folder>` (issue #397): a mod's folder into one `.sagemod`, a zip with mod.json at its
// root and the folder's files under their paths, which the game finds beside its mod folders and mounts as a
// ZipMount. The mod.json is read first, so a broken mod is not packed; the files go in by path, with one fixed
// date, so the same folder packs to the same bytes; dot files and folders (`.git`, `.vscode`) stay out, and a
// symbolic link or a path a mount could not take is refused rather than packed. The archive is then opened
// as the game would open it, so what packs is what loads.
//
// A code mod (issue #396) packs with its built assemblies: its bin/ goes in (every configuration built, as
// "assemblies" may name `bin/{config}/X.dll`), its obj/ (build intermediates, with the builder's own paths in
// them) stays out, and a mod whose assemblies are not built in any configuration is refused: build it first.
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // mods (phase 9): may change before 1.0
public static class ModPackage
{
    private static readonly DateTimeOffset Stamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // The file name `pack` writes when it is given none: `<id>-<version>.sagemod`.
    public static string DefaultFileName(ModManifest mod) => $"{mod.Id}-{mod.Version}{ModManifest.PackageExtension}";

    // Packs `folder` into `output` (default: DefaultFileName in the current folder); returns the mod and the
    // number of files. Throws InvalidDataException (or IOException) saying what is wrong; writes nothing then.
    public static (ModManifest Mod, int Files, string Output) Pack(string folder, string? output = null)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"no mod folder {Path.GetFullPath(folder)}");
        var mod = ModManifest.Load(folder);
        string root = mod.Directory;
        output = Path.GetFullPath(output ?? DefaultFileName(mod));
        if (!output.EndsWith(ModManifest.PackageExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{output}: a packed mod's name ends in {ModManifest.PackageExtension}");

        var files = new List<(string Relative, string Full)>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Collect(root, root, output, files, seen, skipObj: mod.AsksForCode);
        foreach (string assembly in mod.Assemblies)
        {
            string a = assembly.Replace('\\', '/');
            bool built = Enum.GetNames<BuildConfig>().Append(BuildInfo.ConfigurationName).Distinct()
                .Any(config => seen.ContainsKey(a.Replace("{config}", config)));
            if (!built)
                throw new InvalidDataException($"{Path.Combine(root, "mod.json")}: its assembly {assembly} is not built (build the mod, then pack it)");
        }
        files.Sort((a, b) => StringComparer.Ordinal.Compare(a.Relative, b.Relative));

        string? dir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string temp = output + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var (relative, full) in files)
                {
                    var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
                    entry.LastWriteTime = Stamp;
                    using var to = entry.Open();
                    using var from = File.OpenRead(full);
                    from.CopyTo(to);
                }
            }
            // Read back as the game reads it: the limits, and a mod.json that loads.
            using (new ZipMount("pack", temp, mod.Id)) { }
            File.Move(temp, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return (mod, files.Count, output);
    }

    private static void Collect(string root, string dir, string output, List<(string, string)> files, Dictionary<string, string> seen, bool skipObj)
    {
        foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            if (info.Name.StartsWith('.')) continue;
            if (skipObj && info is DirectoryInfo && dir == root && info.Name.Equals("obj", StringComparison.OrdinalIgnoreCase)) continue;
            string relative = Path.GetRelativePath(root, info.FullName).Replace(Path.DirectorySeparatorChar, '/');
            if (info.LinkTarget != null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"{info.FullName}: is a symbolic link; a packed mod holds its own files only");
            if (info is DirectoryInfo sub)
            {
                Collect(root, sub.FullName, output, files, seen, skipObj);
                continue;
            }
            if (string.Equals(info.FullName, output, StringComparison.OrdinalIgnoreCase)
                || string.Equals(info.FullName, output + ".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            if (ZipMount.Unsafe(relative) is { } why)
                throw new InvalidDataException($"{info.FullName}: '{relative}' {why}");
            if (seen.TryGetValue(relative, out string? other))
                throw new InvalidDataException($"{info.FullName}: '{relative}' and '{other}' are one path to the game (paths ignore case)");
            seen[relative] = relative;
            files.Add((relative, info.FullName));
        }
    }
}
