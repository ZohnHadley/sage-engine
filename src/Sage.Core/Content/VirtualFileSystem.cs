#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sage.Core;

// A path inside the VFS (docs/design/05 §3.1): forward slashes, lower case, no leading slash, no "..".
// It is the identity of an asset and what mods shadow.
public readonly record struct VirtualPath
{
    public string Value { get; }

    private VirtualPath(string value) { Value = value; }

    public static VirtualPath Parse(string path)
    {
        string p = path.Replace('\\', '/').Trim().Trim('/').ToLowerInvariant();
        while (p.Contains("//")) p = p.Replace("//", "/");
        if (p.Length == 0) throw new ArgumentException("Empty virtual path.", nameof(path));
        if (p.Split('/').Any(seg => seg == ".." || seg == "."))
            throw new ArgumentException($"Virtual path '{path}' may not contain '.' or '..' segments.", nameof(path));
        if (p.Contains(':'))
            throw new ArgumentException($"Virtual path '{path}' may not contain ':'.", nameof(path));
        return new VirtualPath(p);
    }

    public string Extension => Path.GetExtension(Value);
    public string Directory => Value.Contains('/') ? Value.Substring(0, Value.LastIndexOf('/')) : "";
    public override string ToString() => Value;
}

// A folder (later also a .pak zip) added to the VFS. RecordNamespace is the namespace bare record ids
// in this mount get: "sage" for engine content, the game's id, a mod's id (05 §3.5).
public interface IMount
{
    string Name { get; }
    string RecordNamespace { get; }
    bool Exists(VirtualPath path);
    Stream Open(VirtualPath path);
    IEnumerable<VirtualPath> Enumerate(VirtualPath? directory, string searchPattern, bool recursive);
    string? PhysicalPath(VirtualPath path);

    // Where a file *would* live if something wrote it here, whether or not it exists yet — which is what
    // an editor needs to save a new document (15 §3, F28). Null for a mount that cannot be written to,
    // which is the honest answer for a zip and the reason this is not just `PhysicalPath`: that one
    // resolves files that exist, and a new document is precisely a file that does not.
    string? WritablePath(VirtualPath path);   // for tools, logs and hot reload; null for archives
}

public sealed class FolderMount : IMount
{
    public FolderMount(string name, string directory, string recordNamespace)
    {
        Name = name;
        Root = Path.GetFullPath(directory);
        RecordNamespace = recordNamespace;
    }

    public string Name { get; }
    public string Root { get; }
    public string RecordNamespace { get; }

    public bool Exists(VirtualPath path) => Resolve(path) != null;

    public Stream Open(VirtualPath path) =>
        Resolve(path) is { } file
            ? new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
            : throw new FileNotFoundException($"'{path}' not found in mount {Name}");

    public string? PhysicalPath(VirtualPath path) => Resolve(path);

    public string? WritablePath(VirtualPath path) =>
        Path.Combine(Root, path.Value.Replace('/', Path.DirectorySeparatorChar));

    public IEnumerable<VirtualPath> Enumerate(VirtualPath? directory, string searchPattern, bool recursive)
    {
        string dir = directory is { } d ? ResolveDirectory(d) ?? "" : Root;
        if (dir.Length == 0 || !System.IO.Directory.Exists(dir))
            yield break;
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var file in System.IO.Directory.EnumerateFiles(dir, searchPattern, option).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            yield return VirtualPath.Parse(Path.GetRelativePath(Root, file));
    }

    // Case-insensitive lookup, so content behaves the same on Windows and Linux (05 §3.1).
    private string? Resolve(VirtualPath path)
    {
        string direct = Path.Combine(Root, path.Value.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(direct)) return direct;
        string? dir = path.Directory.Length == 0 ? Root : ResolveDirectory(VirtualPath.Parse(path.Directory));
        if (dir == null) return null;
        string name = path.Value.Substring(path.Value.LastIndexOf('/') + 1);
        return System.IO.Directory.EnumerateFiles(dir).FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
    }

    private string? ResolveDirectory(VirtualPath dirPath)
    {
        string current = Root;
        foreach (var segment in dirPath.Value.Split('/'))
        {
            string exact = Path.Combine(current, segment);
            if (System.IO.Directory.Exists(exact)) { current = exact; continue; }
            var match = System.IO.Directory.Exists(current)
                ? System.IO.Directory.EnumerateDirectories(current).FirstOrDefault(d => string.Equals(Path.GetFileName(d), segment, StringComparison.OrdinalIgnoreCase))
                : null;
            if (match == null) return null;
            current = match;
        }
        return current;
    }

    public override string ToString() => $"{Name} ({Root})";
}

// A plugin's content, carried in its assembly as embedded resources (issue #98, PluginContentAttribute):
// every resource whose logical name starts with `content/` is a file at the rest of the name. Read-only
// and with no file on disk, so it neither hot reloads nor is written by the editor — a game changes it
// with a patch in its own mount, which is what a kit's content is for. Case-insensitive, like a folder.
public sealed class AssemblyContentMount : IMount
{
    public const string Prefix = "content/";

    private readonly System.Reflection.Assembly _assembly;
    private readonly SortedDictionary<string, string> _files = new(StringComparer.Ordinal);   // virtual path -> resource

    public AssemblyContentMount(string name, System.Reflection.Assembly assembly, string recordNamespace)
    {
        Name = name;
        _assembly = assembly;
        RecordNamespace = recordNamespace;
        foreach (string resource in assembly.GetManifestResourceNames())
        {
            // Windows' MSBuild writes %(RecursiveDir) with backslashes.
            string logical = resource.Replace('\\', '/');
            if (!logical.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || logical.Length == Prefix.Length) continue;
            _files[VirtualPath.Parse(logical[Prefix.Length..]).Value] = resource;
        }
    }

    public string Name { get; }
    public string RecordNamespace { get; }

    // How many files it carries.
    public int Count => _files.Count;

    public bool Exists(VirtualPath path) => _files.ContainsKey(path.Value);

    public Stream Open(VirtualPath path) =>
        _files.TryGetValue(path.Value, out var resource) && _assembly.GetManifestResourceStream(resource) is { } stream
            ? stream
            : throw new FileNotFoundException($"'{path}' not found in mount {Name}");

    public string? PhysicalPath(VirtualPath path) => null;

    public string? WritablePath(VirtualPath path) => null;

    public IEnumerable<VirtualPath> Enumerate(VirtualPath? directory, string searchPattern, bool recursive)
    {
        string prefix = directory is { } d ? d.Value + "/" : "";
        foreach (string file in _files.Keys)
        {
            if (!file.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string rest = file[prefix.Length..];
            if (!recursive && rest.Contains('/')) continue;
            if (Matches(rest[(rest.LastIndexOf('/') + 1)..], searchPattern)) yield return VirtualPath.Parse(file);
        }
    }

    // `*` and `?` against a file name, ignoring case, as Directory.EnumerateFiles matches.
    private static bool Matches(string name, string pattern)
    {
        if (pattern is "*" or "*.*") return true;
        return Match(name.AsSpan(), pattern.AsSpan());

        static bool Match(ReadOnlySpan<char> s, ReadOnlySpan<char> p)
        {
            while (p.Length > 0)
            {
                if (p[0] == '*')
                {
                    p = p[1..];
                    for (int i = 0; i <= s.Length; i++)
                        if (Match(s[i..], p)) return true;
                    return false;
                }
                if (s.Length == 0 || (p[0] != '?' && char.ToLowerInvariant(p[0]) != char.ToLowerInvariant(s[0]))) return false;
                s = s[1..];
                p = p[1..];
            }
            return s.Length == 0;
        }
    }

    public override string ToString() => $"{Name} ({_assembly.GetName().Name}.dll, {Count} file(s))";
}

// Mounts in priority order: later mounts shadow earlier ones for the same path (05 §3.1).
// Mount order: engine content → framework content → game.json mounts → mods (load order).
public sealed class VirtualFileSystem
{
    private readonly List<IMount> _mounts = new();

    public IReadOnlyList<IMount> Mounts => _mounts;

    public void Mount(IMount mount)
    {
        _mounts.Add(mount);
        Log.Info(LogCat.VFS, $"Mounted {mount} (namespace '{mount.RecordNamespace}', priority {_mounts.Count})");
    }

    public bool Exists(VirtualPath path) => Which(path) != null;

    // The other direction: a file on disk to the virtual path it is mounted at, for a watcher that
    // only knows what the operating system told it (05 §3.6). Null when the file is under no folder
    // mount. The *shallowest* match wins where mounts nest, because that is the path anything asking
    // for it would have used.
    public VirtualPath? VirtualPathOf(string diskPath)
    {
        string full;
        try { full = Path.GetFullPath(diskPath); }
        catch (ArgumentException) { return null; }

        VirtualPath? best = null;
        int bestRootLength = -1;
        foreach (var mount in Mounts)
        {
            if (mount is not FolderMount folder) continue;
            string root = folder.Root;
            if (full.Length <= root.Length + 1) continue;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (full[root.Length] != Path.DirectorySeparatorChar && full[root.Length] != Path.AltDirectorySeparatorChar) continue;
            if (root.Length <= bestRootLength) continue;

            bestRootLength = root.Length;
            best = VirtualPath.Parse(full[(root.Length + 1)..]
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/'));
        }
        return best;
    }

    // The highest-priority mount that has the path.
    public IMount? Which(VirtualPath path)
    {
        for (int i = _mounts.Count - 1; i >= 0; i--)
            if (_mounts[i].Exists(path)) return _mounts[i];
        return null;
    }

    public Stream Open(VirtualPath path) =>
        Which(path)?.Open(path) ?? throw new FileNotFoundException($"'{path}' not found in any mount");

    // Every match from every mount, in mount order (lowest priority first). This is how records from
    // all mods are found (05 §3.5).
    public IEnumerable<(VirtualPath Path, IMount Mount)> Enumerate(VirtualPath? directory, string searchPattern, bool recursive = true)
    {
        foreach (var mount in _mounts)
            foreach (var path in mount.Enumerate(directory, searchPattern, recursive))
                yield return (path, mount);
    }

    // Every path more than one mount has, in path order: the last mount's file is the one anything
    // opens, and the others are hidden (05 §3.1). For the content report (4j-2): two mods shipping one
    // texture. Walks every mount's files, so it is for tools, never a frame. Record files and string
    // tables are in here too, though every mount's copy of those is read and merged.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // data mods (4j-2): may change before 1.0
    public IReadOnlyList<ShadowedAsset> Shadows()
    {
        var providers = new SortedDictionary<string, List<IMount>>(StringComparer.Ordinal);
        foreach (var mount in _mounts)
            foreach (var path in mount.Enumerate(null, "*", recursive: true))
            {
                if (!providers.TryGetValue(path.Value, out var list)) providers[path.Value] = list = new List<IMount>(2);
                if (list.Count == 0 || list[^1] != mount) list.Add(mount);
            }
        var shadows = new List<ShadowedAsset>();
        foreach (var (path, list) in providers)
            if (list.Count > 1) shadows.Add(new ShadowedAsset(VirtualPath.Parse(path), list[^1], list.Take(list.Count - 1).ToList()));
        return shadows;
    }

    public static void RegisterCommands(CVarRegistry cvars, VirtualFileSystem vfs)
    {
        cvars.RegisterCommand("vfs_which", CVarFlags.None, "vfs_which <path>: which mount provides a file (and which it shadows).", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "vfs_which <path>"); return; }
            var path = VirtualPath.Parse(a[0]);
            var providers = vfs._mounts.Where(m => m.Exists(path)).Reverse().ToList();
            if (providers.Count == 0) { Log.Info(LogCat.Console, $"{path}: not found"); return; }
            Log.Info(LogCat.Console, $"{path}: {providers[0].Name} ({providers[0].PhysicalPath(path)})");
            foreach (var shadowed in providers.Skip(1))
                Log.Info(LogCat.Console, $"  shadows {shadowed.Name}");
        });
        cvars.RegisterCommand("vfs_mounts", CVarFlags.None, "List mounts in priority order (last wins).", _ =>
        {
            for (int i = 0; i < vfs._mounts.Count; i++)
                Log.Info(LogCat.Console, $"  {i + 1}. {vfs._mounts[i]} ns={vfs._mounts[i].RecordNamespace}");
        });
        cvars.RegisterCommand("vfs_ls", CVarFlags.None, "vfs_ls [dir]: list files (all mounts, merged).", a =>
        {
            VirtualPath? dir = a.Count > 0 ? VirtualPath.Parse(a[0]) : null;
            foreach (var group in vfs.Enumerate(dir, "*", recursive: false).GroupBy(x => x.Path))
                Log.Info(LogCat.Console, $"  {group.Key}  ({string.Join(", ", group.Select(x => x.Mount.Name))})");
        });
    }
}
