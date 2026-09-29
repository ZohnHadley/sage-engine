#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace Sage.Simulation;

// TrenchBroom `.map` files: the text a level is written in (docs/design/15 §3, TODO F16).
//
// **Why a format from 1996.** This engine has no level editor yet (F28) and does not need one to have
// levels: TrenchBroom is a good editor that already exists, and its `.map` is a text format that says
// what a level *is* rather than how one engine stored it. A room is a handful of convex solids and a
// list of things standing in it, which is exactly what a first-person game wants and exactly what a
// heightmap cannot give you — an interior, with a ceiling.
//
// A `.map` is entities; an entity is key/values and, if it is a solid entity, brushes; a brush is a
// list of faces; and **a face is a plane, not a polygon**. The brush is the intersection of the
// half-spaces behind its planes, so the polygons have to be worked out (`BrushGeometry`). Writing
// solids as planes is what makes them always closed and always convex, which is worth more to a
// physics engine than any mesh format.
//
// Three dialects are read, because TrenchBroom writes whichever the game profile asks for:
//   - **Standard** (Quake): `TEX xoff yoff rot xscale yscale`, texture axes derived from the face normal;
//   - **Valve 220** (Half-Life, and TrenchBroom's default for Quake): `TEX [ ux uy uz uoff ] [ ... ] rot xscale yscale`,
//     which writes the axes down instead and so survives a face being rotated;
//   - **Quake 2/3**: standard plus trailing surface flags, which are read past.
public sealed class MapFace
{
    public Vector3 P1, P2, P3;          // the three points as written, in map space
    public string Texture = "";

    // Valve 220 only. `HasAxes` says whether these were written down or have to be derived.
    public bool HasAxes;
    public Vector3 UAxis, VAxis;
    public float UOffset, VOffset;

    public float Rotation, UScale = 1f, VScale = 1f;

    // Filled by `BrushGeometry.Build`: the plane, and the polygon where this face meets the others.
    public Vector3 Normal;
    public float Distance;
    public readonly List<Vector3> Polygon = new();

    public int Line;                    // where it was written, for anything that goes wrong later
}

public sealed class MapBrush
{
    public readonly List<MapFace> Faces = new();
    public int Line;
}

public sealed class MapEntity
{
    public readonly Dictionary<string, string> Keys = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<MapBrush> Brushes = new();
    public int Line;

    public string ClassName => Keys.TryGetValue("classname", out var c) ? c : "";

    // `"origin" "0 64 32"`, in map space. Missing or malformed reads as the origin, which is where a
    // mapper who forgot to set one will find their entity.
    public bool TryGetVector(string key, out Vector3 value)
    {
        value = Vector3.Zero;
        if (!Keys.TryGetValue(key, out var text)) return false;
        return MapFile.TryParseVector(text, out value);
    }

    public float GetFloat(string key, float fallback = 0f) =>
        Keys.TryGetValue(key, out var text)
        && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            ? v : fallback;
}

public sealed class MapFile
{
    public readonly List<MapEntity> Entities = new();

    // The worldspawn entity holds the level's own brushes and its settings; everything else stands in it.
    public MapEntity? Worldspawn
    {
        get
        {
            foreach (var e in Entities)
                if (string.Equals(e.ClassName, "worldspawn", StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }
    }

    // Reads a `.map`. Returns false with a message naming the line, because a level that will not load
    // is a thing a mapper has to fix and "parse error" is not an instruction (05 §8).
    public static bool TryParse(string text, out MapFile map, out string error)
    {
        map = new MapFile();
        error = "";
        var reader = new Reader(text);

        while (true)
        {
            var token = reader.Next();
            if (token == null) return true;                 // end of file, cleanly
            if (token != "{") { error = $"line {reader.Line}: expected '{{' to open an entity, found '{token}'"; return false; }
            if (!ParseEntity(reader, map, out error)) return false;
        }
    }

    private static bool ParseEntity(Reader reader, MapFile map, out string error)
    {
        var entity = new MapEntity { Line = reader.Line };
        error = "";

        while (true)
        {
            var token = reader.Next();
            if (token == null) { error = $"line {reader.Line}: file ended inside an entity"; return false; }
            if (token == "}") { map.Entities.Add(entity); return true; }

            if (token == "{")
            {
                if (!ParseBrush(reader, entity, out error)) return false;
                continue;
            }

            // A key/value pair: two quoted strings. The reader hands back quoted tokens with the quotes
            // stripped, so a key that *is* a brace is still a key.
            if (!reader.WasQuoted) { error = $"line {reader.Line}: expected a key in quotes, found '{token}'"; return false; }
            var value = reader.Next();
            if (value == null || !reader.WasQuoted) { error = $"line {reader.Line}: key '{token}' has no value"; return false; }
            entity.Keys[token] = value;
        }
    }

    private static bool ParseBrush(Reader reader, MapEntity entity, out string error)
    {
        var brush = new MapBrush { Line = reader.Line };
        error = "";

        while (true)
        {
            var token = reader.Peek();
            if (token == null) { error = $"line {reader.Line}: file ended inside a brush"; return false; }
            if (token == "}") { reader.Next(); break; }

            if (!ParseFace(reader, out var face, out error)) return false;
            brush.Faces.Add(face);
        }

        // Fewer than four planes cannot close a solid. Saying so here is better than handing the
        // geometry builder something that quietly produces nothing.
        if (brush.Faces.Count < 4)
        {
            error = $"line {brush.Line}: a brush needs at least 4 faces, found {brush.Faces.Count}";
            return false;
        }

        entity.Brushes.Add(brush);
        return true;
    }

    private static bool ParseFace(Reader reader, out MapFace face, out string error)
    {
        face = new MapFace { Line = reader.Line };
        error = "";

        if (!ParsePoint(reader, out face.P1, out error)) return false;
        if (!ParsePoint(reader, out face.P2, out error)) return false;
        if (!ParsePoint(reader, out face.P3, out error)) return false;

        var texture = reader.Next();
        if (texture == null) { error = $"line {reader.Line}: face has no texture"; return false; }
        face.Texture = texture;

        // Valve 220 writes the texture axes in brackets; the standard format leaves them to be derived
        // from the face normal.
        if (reader.Peek() == "[")
        {
            face.HasAxes = true;
            if (!ParseAxis(reader, out face.UAxis, out face.UOffset, out error)) return false;
            if (!ParseAxis(reader, out face.VAxis, out face.VOffset, out error)) return false;
        }
        else
        {
            if (!ParseFloat(reader, out face.UOffset, out error)) return false;
            if (!ParseFloat(reader, out face.VOffset, out error)) return false;
        }

        if (!ParseFloat(reader, out face.Rotation, out error)) return false;
        if (!ParseFloat(reader, out face.UScale, out error)) return false;
        if (!ParseFloat(reader, out face.VScale, out error)) return false;

        // A scale of zero would divide by it later; Quake tools treat it as 1 and so do we.
        if (face.UScale == 0f) face.UScale = 1f;
        if (face.VScale == 0f) face.VScale = 1f;

        // Quake 2/3 add surface flags, contents and value after the scales. Nothing here uses them yet,
        // so they are read past rather than refused — a map from another profile still loads.
        while (reader.Peek() is { } next && IsNumber(next)) reader.Next();

        return true;
    }

    private static bool ParsePoint(Reader reader, out Vector3 point, out string error)
    {
        point = Vector3.Zero;
        error = "";
        if (reader.Next() != "(") { error = $"line {reader.Line}: expected '(' to open a plane point"; return false; }
        if (!ParseFloat(reader, out float x, out error)) return false;
        if (!ParseFloat(reader, out float y, out error)) return false;
        if (!ParseFloat(reader, out float z, out error)) return false;
        if (reader.Next() != ")") { error = $"line {reader.Line}: expected ')' to close a plane point"; return false; }
        point = new Vector3(x, y, z);
        return true;
    }

    private static bool ParseAxis(Reader reader, out Vector3 axis, out float offset, out string error)
    {
        axis = Vector3.Zero;
        offset = 0f;
        error = "";
        if (reader.Next() != "[") { error = $"line {reader.Line}: expected '[' to open a texture axis"; return false; }
        if (!ParseFloat(reader, out float x, out error)) return false;
        if (!ParseFloat(reader, out float y, out error)) return false;
        if (!ParseFloat(reader, out float z, out error)) return false;
        if (!ParseFloat(reader, out offset, out error)) return false;
        if (reader.Next() != "]") { error = $"line {reader.Line}: expected ']' to close a texture axis"; return false; }
        axis = new Vector3(x, y, z);
        return true;
    }

    private static bool ParseFloat(Reader reader, out float value, out string error)
    {
        error = "";
        var token = reader.Next();
        if (token != null && float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return true;
        value = 0f;
        error = $"line {reader.Line}: expected a number, found '{token ?? "end of file"}'";
        return false;
    }

    internal static bool TryParseVector(string text, out Vector3 value)
    {
        value = Vector3.Zero;
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) return false;
        if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) return false;
        if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) return false;
        value = new Vector3(x, y, z);
        return true;
    }

    private static bool IsNumber(string token) =>
        float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    // A tokeniser rather than a line parser, because the format is free-form: a face may be split over
    // lines and often is. `//` runs to the end of the line, which is how TrenchBroom writes its own
    // layer and group comments.
    private sealed class Reader
    {
        private readonly string _text;
        private int _index;
        private string? _peeked;
        private bool _peekedQuoted;

        public int Line { get; private set; } = 1;
        public bool WasQuoted { get; private set; }

        public Reader(string text) => _text = text;

        public string? Peek()
        {
            if (_peeked != null) return _peeked;
            bool quoted = WasQuoted;
            _peeked = Read();
            _peekedQuoted = WasQuoted;
            WasQuoted = quoted;
            return _peeked;
        }

        public string? Next()
        {
            if (_peeked != null)
            {
                var token = _peeked;
                WasQuoted = _peekedQuoted;
                _peeked = null;
                return token;
            }
            return Read();
        }

        private string? Read()
        {
            WasQuoted = false;
            SkipBlank();
            if (_index >= _text.Length) return null;

            char c = _text[_index];
            if (c == '"')
            {
                _index++;
                int start = _index;
                while (_index < _text.Length && _text[_index] != '"')
                {
                    if (_text[_index] == '\n') Line++;
                    _index++;
                }
                var value = _text[start.._index];
                if (_index < _text.Length) _index++;            // the closing quote
                WasQuoted = true;
                return value;
            }

            if (c is '{' or '}' or '(' or ')' or '[' or ']')
            {
                _index++;
                return c.ToString();
            }

            int from = _index;
            while (_index < _text.Length && !char.IsWhiteSpace(_text[_index])
                   && _text[_index] is not ('{' or '}' or '(' or ')' or '[' or ']' or '"'))
                _index++;
            return _text[from.._index];
        }

        private void SkipBlank()
        {
            while (_index < _text.Length)
            {
                char c = _text[_index];
                if (c == '\n') { Line++; _index++; continue; }
                if (char.IsWhiteSpace(c)) { _index++; continue; }
                if (c == '/' && _index + 1 < _text.Length && _text[_index + 1] == '/')
                {
                    while (_index < _text.Length && _text[_index] != '\n') _index++;
                    continue;
                }
                return;
            }
        }
    }
}
