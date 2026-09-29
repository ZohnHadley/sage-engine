#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace sage_engine;

// An interned VirtualPath (docs/design/05 §3.2): a small id that says *which* asset without loading
// it, cheap to store in components and to compare. 0 = none. The intern table only grows (a few
// thousand paths per game); lookups by id take a lock-free snapshot of the table.
[JsonConverter(typeof(AssetPathJsonConverter))]
public readonly struct AssetPath : IEquatable<AssetPath>
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, int> Ids = new(StringComparer.Ordinal);
    private static VirtualPath[] _paths = new VirtualPath[64];
    private static int _count = 1;   // slot 0 = none

    public readonly int Id;

    private AssetPath(int id) { Id = id; }

    public static AssetPath Intern(VirtualPath path)
    {
        lock (Lock)
        {
            if (Ids.TryGetValue(path.Value, out int id)) return new AssetPath(id);
            if (_count == _paths.Length)
            {
                var grown = new VirtualPath[_paths.Length * 2];
                Array.Copy(_paths, grown, _count);
                _paths = grown;
            }
            _paths[_count] = path;
            Ids[path.Value] = _count;
            return new AssetPath(_count++);
        }
    }

    public static AssetPath Intern(string path) => Intern(VirtualPath.Parse(path));

    public static AssetPath None => default;
    public bool IsEmpty => Id == 0;
    public VirtualPath Path => Id == 0 ? default : _paths[Id];

    public bool Equals(AssetPath other) => Id == other.Id;
    public override bool Equals(object? obj) => obj is AssetPath other && Equals(other);
    public override int GetHashCode() => Id;
    public static bool operator ==(AssetPath a, AssetPath b) => a.Id == b.Id;
    public static bool operator !=(AssetPath a, AssetPath b) => a.Id != b.Id;
    public override string ToString() => Id == 0 ? "" : _paths[Id].Value;
}

// "textures/goblin.png" in record files; "" or null = none.
internal sealed class AssetPathJsonConverter : JsonConverter<AssetPath>
{
    public override AssetPath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text)) return default;
        try { return AssetPath.Intern(text); }
        catch (ArgumentException ex) { throw new JsonException(ex.Message); }
    }

    public override void Write(Utf8JsonWriter writer, AssetPath value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
