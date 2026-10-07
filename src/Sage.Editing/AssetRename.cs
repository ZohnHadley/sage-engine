#nullable enable
#pragma warning disable SAGE0132 // which mounts are mods is ContentReport's: data mods' experimental API, which this reads
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Sage.Editing;

// One place a content file names an asset: a record field, a prefab's or a placement override's, a map
// entity's key — written as a quoted path ("textures/brick.png"), as every one of them is.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10)
public sealed record AssetReference(IMount Mount, VirtualPath File, int Line, int Column)
{
    public override string ToString() => $"{ContentReport.NameOf(Mount)}:{File}:{Line}:{Column}";
}

// The files that name an asset (issue #366): every record file (`.json`: records, prefabs, placements
// documents, UI) and every `.map` in every mount, read as text, so what is found is what a person would
// find with a search, comments aside. Walks the mounts, so it is for a tool, never a frame.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10)
public static class AssetReferences
{
    // The files a reference can be in.
    public static bool Scanned(VirtualPath file) =>
        file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".map", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<AssetReference> Find(VirtualFileSystem vfs, VirtualPath asset)
    {
        var pattern = Pattern(asset);
        var found = new List<AssetReference>();
        foreach (var (file, mount) in vfs.Enumerate(null, "*", recursive: true))
        {
            if (!Scanned(file)) continue;
            string text = Read(mount, file);
            foreach (Match match in pattern.Matches(text))
            {
                var (line, column) = LineOf(text, match.Index);
                found.Add(new AssetReference(mount, file, line, column));
            }
        }
        return found;
    }

    // The path inside quotes, any case, with or without a leading slash: how JSON and a .map write one.
    internal static Regex Pattern(VirtualPath asset) =>
        new("(?<=\")/?" + Regex.Escape(asset.Value) + "(?=\")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string Read(IMount mount, VirtualPath file)
    {
        using var stream = mount.Open(file);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static (int Line, int Column) LineOf(string text, int index)
    {
        int line = 1, start = 0;
        for (int i = 0; i < index; i++)
            if (text[i] == '\n') { line++; start = i + 1; }
        return (line, index - start + 1);
    }
}

// Renaming an asset (issue #366; 05 §3.2: identity is the path, so a rename is a refactor): the file moves
// inside the game's own folder, with its cooked stand-in beside it (`.sgtex`, `.sgmesh`), and every file of
// the game's that names it is rewritten to name the new path, comments and layout kept (only the quoted path
// changes). The records are then read again, and an open placements document and record are opened again
// from the files, so what the editor shows is what was written.
//
// **Planned, then applied.** `Plan` finds everything and says what stands in the way; `Apply` does nothing
// unless nothing does. What stands in the way: the asset is not the game's to move (the engine's, a kit's, a
// mod's: a game changes those by shipping its own file at the same path), the new path is taken or of another
// kind, a file the game does not own names it (it would be left naming nothing), or an open document has
// unsaved edits (they would be lost to the reopen: save or undo them first).
//
// Not on an undo history: it changes files, as a save does. Renaming it back is the undo, and says so.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10)
public sealed class AssetRename
{
    private static readonly string[] CookedExtensions = { ".sgtex", ".sgmesh" };

    private readonly Engine _engine;
    private readonly EditDocument? _document;
    private readonly RecordEditor? _records;
    private readonly List<string> _problems = new();
    private readonly List<(string From, string To)> _moves = new();

    private AssetRename(Engine engine, VirtualPath from, VirtualPath to, EditDocument? document, RecordEditor? records)
    {
        _engine = engine;
        From = from;
        To = to;
        _document = document;
        _records = records;
    }

    public VirtualPath From { get; }
    public VirtualPath To { get; }

    // Every file that names `From`, in every mount; the ones the rename rewrites are those in `Mount`.
    public IReadOnlyList<AssetReference> References { get; private set; } = Array.Empty<AssetReference>();

    // The game's folder the asset is moved in; null when the game has none.
    public IMount? Mount { get; private set; }

    // Why it cannot be applied; empty when it can.
    public IReadOnlyList<string> Problems => _problems;
    public bool CanApply => _problems.Count == 0;

    // The files the rename rewrites (the game's own that name it), each once.
    public IReadOnlyList<VirtualPath> Rewrites => References.Where(r => r.Mount == Mount).Select(r => r.File).Distinct().ToList();

    public static AssetRename Plan(Engine engine, VirtualPath from, VirtualPath to, EditDocument? document = null, RecordEditor? records = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var plan = new AssetRename(engine, from, to, document, records);
        plan.Check();
        return plan;
    }

    private void Check()
    {
        var vfs = _engine.Vfs;
        Mount = RecordDocument.GameMount(_engine);
        var owner = vfs.Which(From);
        if (owner == null) { _problems.Add($"{From} is in no mount"); return; }
        if (From == To) _problems.Add("the new path is the old one");
        if (AssetKinds.Of(From) == null) _problems.Add($"{From} is not an asset the editor knows the kind of");
        else if (!string.Equals(From.Extension, To.Extension, StringComparison.OrdinalIgnoreCase))
            _problems.Add($"keep the extension ({From.Extension}): a reference names the file, and the kind comes from it");
        if (vfs.Exists(To)) _problems.Add($"{To} is already taken (by {ContentReport.NameOf(vfs.Which(To))})");

        if (Mount == null) _problems.Add("the game has no folder of its own to rename in");
        else if (owner != Mount)
            _problems.Add($"{From} is {ContentReport.NameOf(owner)}'s, not the game's: a game replaces it by shipping a file at the same path, and cannot rename it");

        References = AssetReferences.Find(vfs, From);
        foreach (var reference in References.Where(r => r.Mount != Mount))
            _problems.Add($"{reference} names it, and is not the game's to change");

        if (_document is { Dirty: true }) _problems.Add($"the open document {_document.Id} has unsaved edits: save or undo them first");
        if (_records?.Current is { Dirty: true } record) _problems.Add($"the open record {record.Type} {record.Id} has unsaved edits: save or undo them first");

        if (Mount != null && owner == Mount)
        {
            foreach (string extension in CookedExtensions.Prepend(""))
            {
                var source = VirtualPath.Parse(From.Value + extension);
                if (extension.Length > 0 && !Mount.Exists(source)) continue;
                string? physical = Mount.PhysicalPath(source);
                string? target = Mount.WritablePath(VirtualPath.Parse(To.Value + extension));
                if (physical == null || target == null) { _problems.Add($"{Mount.Name} cannot be written to"); break; }
                _moves.Add((physical, target));
            }
        }
    }

    // Moves the file and rewrites the references; false (and nothing changed) when Problems has any, or
    // when the disk refuses, in which case what was done is put back.
    public bool Apply()
    {
        if (!CanApply)
        {
            foreach (string problem in _problems) Log.Warn(LogCat.Editor, $"Cannot rename {From}: {problem}");
            return false;
        }

        var pattern = AssetReferences.Pattern(From);
        var written = new List<(string Path, string Before)>();
        var moved = new List<(string From, string To)>();
        try
        {
            foreach (var file in Rewrites)
            {
                string path = Mount!.PhysicalPath(file) ?? throw new IOException($"{file} is not on disk");
                string before = File.ReadAllText(path);
                string after = pattern.Replace(before, m => (m.Value.StartsWith('/') ? "/" : "") + To.Value);
                File.WriteAllText(path, after, new UTF8Encoding(encoderShouldEmitUTF8Identifier: HasBom(path)));
                written.Add((path, before));
            }
            foreach (var (from, to) in _moves)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(from, to);
                moved.Add((from, to));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(LogCat.Editor, $"Could not rename {From} to {To}: {ex.Message} (put back as it was)");
            foreach (var (from, to) in moved) File.Move(to, from);
            foreach (var (path, before) in written) File.WriteAllText(path, before);
            return false;
        }

        Log.Info(LogCat.Editor, $"Renamed {From} to {To}: {written.Count} file(s) rewritten ({string.Join(", ", Rewrites)}). " +
                                $"To take it back: ed_asset_rename {To} {From}");

        // What the editor holds is read again from what was written.
        _engine.Records.Reload();
        if (_document is { IsOpen: true } document) document.Open(document.Id);
        if (_records?.Current is { } record) _records.Open(record.Type, record.Id);
        return true;
    }

    private static bool HasBom(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[3];
        return stream.Read(head) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
    }
}
