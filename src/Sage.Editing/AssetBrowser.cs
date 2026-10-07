#nullable enable
#pragma warning disable SAGE0132 // which mounts are mods is ContentReport's: data mods' experimental API, which this reads
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

namespace Sage.Editing;

// What kind of asset a file is, by its extension: the kinds `[AssetKind]` names on a field (Metadata).
// A sprite sheet is a texture; a level is a `map`; a shader is the compiled `.mgfxo` a material names.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10)
public static class AssetKinds
{
    public const string Texture = "texture", Mesh = "mesh", Sound = "sound", Shader = "shader", Font = "font", Map = "map";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = Texture, [".jpg"] = Texture, [".jpeg"] = Texture, [".tga"] = Texture, [".bmp"] = Texture, [".gif"] = Texture,
        [".glb"] = Mesh,
        [".wav"] = Sound, [".ogg"] = Sound,
        [".mgfxo"] = Shader,
        [".ttf"] = Font, [".otf"] = Font,
        [".map"] = Map,
    };

    // Every kind, in the order the browser offers them.
    public static IReadOnlyList<string> All { get; } = new[] { Texture, Mesh, Sound, Map, Font, Shader };

    // The kind of the file at `path`, or null for a file that is not an asset (a record file, a string
    // table, a cooked `.sgtex` that only stands in for its loose file).
    public static string? Of(VirtualPath path) => ByExtension.TryGetValue(path.Extension, out var kind) ? kind : null;

    // Whether an asset at `path` can go in a field of kind `fieldKind` (null: any asset). A font field
    // also takes a `.png` grid atlas, as the engine's own font is one.
    public static bool Fits(string? fieldKind, VirtualPath path)
    {
        string? kind = Of(path);
        if (kind == null) return false;
        if (fieldKind == null || string.Equals(fieldKind, kind, StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(fieldKind, Font, StringComparison.OrdinalIgnoreCase) && path.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
    }
}

// One asset the browser lists: the file a load would read (the last mount that has it), and the mounts it
// shadows there (05 §3.1: a mod replacing a texture ships the same path).
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10)
public sealed record AssetEntry(VirtualPath Path, string Kind, IMount Mount, IReadOnlyList<IMount> Shadows)
{
    public string Name => System.IO.Path.GetFileName(Path.Value);
    public string Folder => Path.Directory;

    // The mount as the content report names it: a mod by its id, anything else by its mount name.
    public string Origin => ContentReport.NameOf(Mount);
    public bool FromMod => ContentReport.IsModMount(Mount);

    // Bytes on disk, -1 when the mount cannot say (an assembly's embedded content).
    public long Size => Mount.PhysicalPath(Path) is { } file && File.Exists(file) ? new FileInfo(file).Length : -1;

    public override string ToString() => $"{Path}  [{Kind}, {Origin}{(Shadows.Count > 0 ? $", shadows {string.Join(", ", Shadows.Select(ContentReport.NameOf))}" : "")}]";
}

// The asset browser (issue #366, docs/design/15 §3 and §11): every model, texture, sound, map, font and
// shader the VFS has, one row per path, narrowed by kind, by mount (a mod by its id) and by search words.
// The Assets panel in Sage.Editor draws `Filtered()` with thumbnails; `ed_assets` prints the same list.
//
// The list is read from the mounts when first asked for and again on `Refresh` (and whenever records
// reload: a save, a rename, a hot reload), so a browser left open does not walk the disk every frame.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10)
public sealed class AssetBrowser : IDisposable
{
    private IReadOnlyList<AssetEntry>? _entries;

    public AssetBrowser(Engine engine)
    {
        Engine = engine;
        engine.Records.Reloaded += Refresh;
    }

    public Engine Engine { get; }

    // "" for every kind, else one of AssetKinds.All.
    public string Kind { get; set; } = "";

    // "" for every mount, else a mount's name or a mod's id (AssetEntry.Origin).
    public string Mount { get; set; } = "";

    // Whitespace-separated words, each of which must appear (any case) in the path.
    public string Search { get; set; } = "";

    // The asset the browser has selected, or null.
    public AssetEntry? Selected { get; private set; }

    public void Dispose() => Engine.Records.Reloaded -= Refresh;

    // The mounts are read again on the next `Entries`.
    public void Refresh()
    {
        _entries = null;
        if (Selected is { } selected) Selected = Find(selected.Path);
    }

    // Every asset, by path.
    public IReadOnlyList<AssetEntry> Entries() => _entries ??= Scan(Engine.Vfs);

    // The assets the filters let through, by path.
    public IReadOnlyList<AssetEntry> Filtered()
    {
        string[] words = Search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return Entries().Where(e =>
                (Kind.Length == 0 || string.Equals(e.Kind, Kind, StringComparison.OrdinalIgnoreCase))
                && (Mount.Length == 0 || string.Equals(e.Origin, Mount, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(e.Mount.Name, Mount, StringComparison.OrdinalIgnoreCase))
                && words.All(w => e.Path.Value.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    // How many assets of each kind there are (whatever the filters say), in AssetKinds.All's order.
    public IReadOnlyList<(string Kind, int Count)> Kinds() =>
        AssetKinds.All.Select(k => (k, Entries().Count(e => e.Kind == k))).ToList();

    // The mounts that provide at least one asset, in mount order, by the name `Mount` takes.
    public IReadOnlyList<string> Mounts() =>
        Engine.Vfs.Mounts.Where(m => Entries().Any(e => e.Mount == m)).Select(ContentReport.NameOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public AssetEntry? Find(VirtualPath path) => Entries().FirstOrDefault(e => e.Path == path);

    public AssetEntry? Find(string path) => TryParse(path, out var p) ? Find(p) : null;

    public bool Select(VirtualPath? path)
    {
        Selected = path is { } p ? Find(p) : null;
        return Selected != null || path == null;
    }

    internal static bool TryParse(string text, out VirtualPath path)
    {
        try { path = VirtualPath.Parse(text); return true; }
        catch (ArgumentException) { path = default; return false; }
    }

    private static List<AssetEntry> Scan(VirtualFileSystem vfs)
    {
        var providers = new SortedDictionary<string, List<IMount>>(StringComparer.Ordinal);
        foreach (var (path, mount) in vfs.Enumerate(null, "*", recursive: true))
        {
            if (AssetKinds.Of(path) == null) continue;
            if (!providers.TryGetValue(path.Value, out var list)) providers[path.Value] = list = new List<IMount>(1);
            if (list.Count == 0 || list[^1] != mount) list.Add(mount);
        }
        var entries = new List<AssetEntry>(providers.Count);
        foreach (var (text, list) in providers)
        {
            var path = VirtualPath.Parse(text);
            entries.Add(new AssetEntry(path, AssetKinds.Of(path)!, list[^1], list.Take(list.Count - 1).Reverse().ToList()));
        }
        return entries;
    }
}
