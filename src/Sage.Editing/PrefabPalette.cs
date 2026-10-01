#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Sage.Editing;

// The prefabs of one namespace that match the palette's search, in id order.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed record PrefabGroup(string Namespace, IReadOnlyList<RecordId> Prefabs);

// The prefab palette (issue #222, docs/design/15 §6): every prefab the game's records define, by
// namespace, narrowed by a search string, with one of them armed for placing. The panel in Sage.Editor
// draws `Groups` and calls `Arm`; `ed_palette` prints the same list. Abstract prefabs (templates a record's
// `base` names) are not in the record store's ids, so they are not offered; a prefab that is only ever a
// child of another is a prefab like any other, and is.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class PrefabPalette
{
    private readonly RecordStore _records;

    public PrefabPalette(RecordStore records) => _records = records;

    // Whitespace-separated words, each of which must appear (any case) in the prefab's full id.
    public string Search { get; set; } = "";

    // The prefab the next click places; empty when none is armed.
    public RecordId Armed { get; private set; }
    public bool IsArmed => !Armed.IsEmpty;

    public void Arm(RecordId prefab) => Armed = prefab;
    public void Disarm() => Armed = default;

    // Namespaces in order, each with its matching prefabs; a namespace with none is left out. Read from the
    // record store each call, so a hot reload that adds a prefab shows at once.
    public IReadOnlyList<PrefabGroup> Groups()
    {
        string[] words = Search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return _records.Ids("prefab")
            .Where(id => words.All(w => id.ToString().Contains(w, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(id => id.Namespace)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new PrefabGroup(g.Key, g.ToList()))
            .ToList();
    }

    public int Count => Groups().Sum(g => g.Prefabs.Count);
}
