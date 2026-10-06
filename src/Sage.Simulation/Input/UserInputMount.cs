#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Sage.Simulation;

// `user://input.json` as a content mount (docs/design/08 §3.2, §7): the one file, seen as `data/input.json`
// in the record namespace `user`, mounted after the game and every mod, so its `"patch": true` records win
// over all of them with the one merge rule records already have. A missing file is no records. A corrupt
// one is ignored with a warning and kept as `input.json.bad`, so the defaults are used and the player's
// file is not silently overwritten by the next rebind.
internal sealed class UserInputMount : IMount
{
    public const string Namespace = "user";
    private static readonly VirtualPath Virtual = VirtualPath.Parse("data/input.json");
    private readonly string _file;

    public UserInputMount(string file) { _file = Path.GetFullPath(file); }

    public string Name => "user/input";
    public string RecordNamespace => Namespace;

    public bool Exists(VirtualPath path) => Is(path) && File.Exists(_file);

    public Stream Open(VirtualPath path) =>
        Is(path) && File.Exists(_file)
            ? new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
            : throw new FileNotFoundException($"'{path}' not found in mount {Name}");

    public string? PhysicalPath(VirtualPath path) => Is(path) && File.Exists(_file) ? _file : null;

    public string? WritablePath(VirtualPath path) => Is(path) ? _file : null;

    public IEnumerable<VirtualPath> Enumerate(VirtualPath? directory, string searchPattern, bool recursive)
    {
        if (directory is { } d && d.Value.Length > 0 && !string.Equals(d.Value, "data", StringComparison.OrdinalIgnoreCase)) yield break;
        if (searchPattern != "*" && searchPattern != "*.json" && searchPattern != "input.json") yield break;
        if (File.Exists(_file) && Readable()) yield return Virtual;
    }

    private static bool Is(VirtualPath path) => string.Equals(path.Value, Virtual.Value, StringComparison.OrdinalIgnoreCase);

    // Whether the file parses; if not, set it aside as `.bad` (the newest bad file replaces the older).
    private bool Readable()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_file), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
            });
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            string bad = _file + ".bad";
            string kept = bad;
            try { File.Move(_file, bad, overwrite: true); }
            catch (Exception move) when (move is IOException or UnauthorizedAccessException) { kept = _file + " (could not be set aside)"; }
            Log.Warn(LogCat.Input, $"{_file}: {ex.Message} Using the default bindings; the file is kept as {kept}.");
            return false;
        }
    }

    public override string ToString() => $"{Name} ({_file})";
}
