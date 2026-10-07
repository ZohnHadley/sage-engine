#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage.Core;

// What a record check (RecordStore.AddCheck) is given: the record's type and id, where its fields are
// written, and the same checks the load runs on the record's own fields, for the parts of it only the
// check understands — a prefab's component bodies, whose type is not known until the id is resolved.
//
// A `path` is into the record as System.Text.Json writes one ("Parts['melee'].Attack", "Tasks[2]"),
// and becomes file:line:column: the deepest part of it that the file writing that field has.
public sealed class RecordCheck
{
    private readonly RecordStore _store;
    private readonly Func<string?, string> _at;

    internal RecordCheck(RecordStore store, string type, RecordId id, Func<string?, string> at)
    {
        _store = store;
        Type = type;
        Id = id;
        _at = at;
    }

    public string Type { get; }
    public RecordId Id { get; }

    // How content is read: the options a check should deserialize with.
    public JsonSerializerOptions Json => _store.Json;

    // "game:data/prefabs.json:12:9", for the record or somewhere inside it.
    public string At(string? path = null) => _at(path);

    // Counted in the store's ErrorCount: `sage validate` fails on it.
    public void Error(string? path, string message) => _store.ReportError($"{At(path)}: {Type} {Id}: {message}");
    public void Warn(string? path, string message) => _store.ReportWarning($"{At(path)}: {Type} {Id}: {message}");

    // Whether a record exists in the content being loaded: of `type` ("item"), or of any type when null.
    public bool Exists(string? type, RecordId id) => _store.ExistsWhileChecking(type, id);

    // Every id of `type` in the content being loaded: for a check whose rule names records by
    // something other than their id (an item's slot, by an equip_slot's name, issue #384).
    public IEnumerable<RecordId> Ids(string type) => _store.IdsWhileChecking(type);

    // Another record of the content being loaded, as built: for a check that follows references — a
    // prefab whose children contain it (phase 4i). Null when there is no such record of that type.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // saves you can trust (phase 4i)
    public bool TryGet<T>(RecordId id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? record) where T : class =>
        _store.TryGetWhileChecking(id, out record);

    // Every field `node` writes that `type` has not got, each an error at its path with the nearest
    // real field. False when there was one.
    public bool CheckFields(JsonNode? node, Type type, string path)
    {
        var unknown = JsonMembers.Find(node, type, Json, path);
        foreach (var field in unknown) Error(field.Path, field.Message);
        return unknown.Count == 0;
    }

    // The record references and asset paths inside `value` (read from this record at `path`), checked
    // as the record's own fields are: a reference must exist (and be of its RecordRef type), an asset
    // must be in a mount.
    public void CheckValues(object? value, string path) =>
        _store.CheckValuesWhileChecking(value, path, p => At(p), $"{Type} {Id}");
}
