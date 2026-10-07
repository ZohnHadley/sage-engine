#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Editing;

// What a terrain brush does (issue #372).
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public enum TerrainTool
{
    Raise,     // up by `strength` metres a dab at the centre
    Lower,     // down by `strength` metres a dab
    Smooth,    // toward the average of each vertex's neighbours, by `strength` (0–1)
    Flatten,   // toward `FlattenHeight` (else the height where the stroke began), by `strength` (0–1)
    Paint,     // layer `Layer` painted over the material's rules, by `strength` (0–1)
}

// The brush the terrain tools draw with: what it does, how wide it is and how hard.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class TerrainBrush
{
    public TerrainTool Tool = TerrainTool.Raise;
    public float Radius = 16f;          // metres; the falloff is smooth to nothing at the edge
    public float Strength = 1f;         // metres a dab (raise, lower), else 0–1
    public int Layer = 1;               // which terrain layer Paint paints (0–3)
    public float? FlattenHeight;        // absolute metres; null: where the stroke began

    public override string ToString() => Tool switch
    {
        TerrainTool.Paint => $"paint layer {Layer}, radius {Radius:0.#} m, strength {Strength:0.##}",
        TerrainTool.Flatten => $"flatten to {(FlattenHeight is { } h ? $"{h:0.#} m" : "the stroke's start")}, radius {Radius:0.#} m, strength {Strength:0.##}",
        _ => $"{Tool.ToString().ToLowerInvariant()}, radius {Radius:0.#} m, strength {Strength:0.##}",
    };
}

// The terrain the editor sculpts (issue #372; docs/design/15 §11 "terrain tools", 14 §3).
//
// **It edits the world's sculpt, not its heights.** The ground is a generator's (a `terrain` record) with a
// sculpt over it (TerrainSculpt): heights added per vertex, layers painted, water by sector. The document
// keeps a working copy of the sculpt; a brush stroke changes the copy where the brush passed, hands the
// changed sectors to the world's Terrain (Resculpt) and generates the loaded ones again (Refresh), so what
// the editor shows is exactly what streaming will load from the saved file. Nothing is read back from the
// meshes.
//
// **A stroke is one undo step**: BeginStroke, a Dab per frame the mouse is down, EndStroke — the sectors'
// sculpt before the first dab and after the last make one TerrainStroke in the history. The history is the
// document's own, apart from the placements document's and the record browser's.
//
// **Saving** writes the whole sculpt to the terrain's file (`terrain/<record>.sterrain`, or the record's
// `sculpt`), in the mount the file is already in, else the terrain record's namespace's.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class TerrainDocument
{
    private const int Cells = Terrain.SectorResolution - 1;
    private const float Spacing = Terrain.SectorSize / Cells;   // 8 m between vertices

    private TerrainSculpt _working = new();
    private Dictionary<SectorCoord, SculptSector?>? _strokeBefore;   // a stroke under way: each touched sector as it was
    private float? _strokeFlatten;
    private Vector3 _strokeAt;
    private int _strokeDabs;

    public TerrainDocument(World world)
    {
        World = world;
        Engine = world.Engine ?? throw new ArgumentException("terrain is edited in a world with an engine", nameof(world));
        History.Changed += () => Changed?.Invoke();
    }

    public Engine Engine { get; }
    public World World { get; }
    public CommandLog History { get; } = new();
    public TerrainBrush Brush { get; } = new();

    // The world's terrain, when it has ground to sculpt (a generator: a scene's `terrain` record, or a game's own).
    public Terrain? Terrain => World.Resources.TryGet<Terrain>(out var terrain) && terrain?.Generator != null ? terrain : null;
    public bool CanEdit => Terrain != null;

    public RecordId Record => Terrain?.Record ?? default;

    // The VFS path the sculpt is saved to; "" when the ground came from no terrain record.
    public string SculptPath => Terrain?.SculptPath ?? "";

    public bool Dirty => History.Dirty;
    public bool Stroking => _strokeBefore != null;

    // The working sculpt: read it freely, change it only through the brush and commands.
    public TerrainSculpt Sculpt => _working;

    public event Action? Changed;

    public string Title => Record.IsEmpty ? "(no terrain)" : $"terrain {Record}{(Dirty ? " *" : "")}";

    // Takes the world's sculpt as it is now as the working copy, with no history. Called when the world's
    // ground changes under the editor (a scene placed); false when there is nothing to sculpt.
    public bool Reset()
    {
        _strokeBefore = null;
        _working = Terrain?.CopySculpt() ?? new TerrainSculpt();
        History.Clear();
        return CanEdit;
    }

    // Before the first edit, the working copy is whatever the world's ground has now (a scene placed since
    // the document was made brought its sculpt with it).
    private void Sync()
    {
        if (History.Entries.Count == 0 && _strokeBefore == null && Terrain is { } terrain) _working = terrain.CopySculpt();
    }

    // ---- Brush strokes -------------------------------------------------------------------------------

    public void BeginStroke()
    {
        Sync();
        _strokeBefore = new Dictionary<SectorCoord, SculptSector?>();
        _strokeFlatten = Brush.FlattenHeight;
        _strokeDabs = 0;
    }

    // One dab of the brush at `at` (origin space, as transforms and the pointer's ray are). Begins a stroke
    // when none is under way. False when there is no terrain.
    public bool Dab(Vector3 at)
    {
        if (Terrain is not { } terrain) return false;
        if (_strokeBefore == null) BeginStroke();
        var absolute = terrain.Origin.ToAbsolute(at);
        if (_strokeDabs++ == 0) _strokeAt = absolute;

        float radius = MathF.Max(Brush.Radius, Spacing * 0.5f);
        long x0 = (long)MathF.Floor((absolute.X - radius) / Spacing), x1 = (long)MathF.Ceiling((absolute.X + radius) / Spacing);
        long z0 = (long)MathF.Floor((absolute.Z - radius) / Spacing), z1 = (long)MathF.Ceiling((absolute.Z + radius) / Spacing);

        // The ground under the brush and a vertex round it, loaded so its heights can be read.
        var around = SectorsOf(x0 - 1, z0 - 1, x1 + 1, z1 + 1);
        foreach (var coord in around) terrain.Load(coord);

        // Heights as they are now (with a margin, for Smooth), read before any is written.
        int w = (int)(x1 - x0) + 3, h = (int)(z1 - z0) + 3;
        var current = new float[w * h];
        for (int z = 0; z < h; z++)
            for (int x = 0; x < w; x++)
                current[z * w + x] = HeightAtVertex(terrain, x0 - 1 + x, z0 - 1 + z);
        if (Brush.Tool == TerrainTool.Flatten && _strokeFlatten == null)
            _strokeFlatten = HeightAtVertex(terrain, (long)MathF.Round(absolute.X / Spacing), (long)MathF.Round(absolute.Z / Spacing));

        float strength = Brush.Tool is TerrainTool.Raise or TerrainTool.Lower ? Brush.Strength : Math.Clamp(Brush.Strength, 0f, 1f);
        int layer = Math.Clamp(Brush.Layer, 0, TerrainSplat_MaxLayers - 1);
        bool changed = false;
        for (long gz = z0; gz <= z1; gz++)
            for (long gx = x0; gx <= x1; gx++)
            {
                float dx = gx * Spacing - absolute.X, dz = gz * Spacing - absolute.Z;
                float d = MathF.Sqrt(dx * dx + dz * dz);
                if (d >= radius) continue;
                float t = d / radius, falloff = (1f - t * t) * (1f - t * t);
                int i = (int)(gz - z0 + 1) * w + (int)(gx - x0 + 1);
                float now = current[i];
                if (Brush.Tool == TerrainTool.Paint)
                {
                    changed |= PaintVertex(gx, gz, layer, strength * falloff);
                    continue;
                }
                float wanted = Brush.Tool switch
                {
                    TerrainTool.Raise => now + strength * falloff,
                    TerrainTool.Lower => now - strength * falloff,
                    TerrainTool.Smooth => now + ((current[i - 1] + current[i + 1] + current[i - w] + current[i + w]) * 0.25f - now) * strength * falloff,
                    _ => now + (_strokeFlatten!.Value - now) * strength * falloff,
                };
                if (wanted == now) continue;
                changed |= OffsetVertex(gx, gz, wanted - now);
            }

        if (changed) Push(SectorsOf(x0 - 1, z0 - 1, x1 + 1, z1 + 1));
        return true;
    }

    // The stroke done: one TerrainStroke in the history for every sector it changed, or nothing when it
    // changed nothing. Returns the command (null for none).
    public IEditorCommand? EndStroke()
    {
        if (_strokeBefore is not { } before) return null;
        _strokeBefore = null;
        var changes = new List<SculptChange>();
        foreach (var (coord, was) in before)
        {
            var now = _working.Get(coord);
            if (was == null ? now == null || now.IsEmpty : was.SameAs(now)) continue;
            changes.Add(new SculptChange(coord, was, now?.Clone()));
        }
        if (changes.Count == 0) return null;
        var stroke = new TerrainStroke(this, $"{Verb(Brush.Tool)} at {_strokeAt.X:0} {_strokeAt.Z:0} ({_strokeDabs} dab{(_strokeDabs == 1 ? "" : "s")})", changes, applied: true);
        History.Execute(stroke);
        History.EndMerge();
        return stroke;
    }

    // A whole one-dab stroke: what a console command or a click does.
    public IEditorCommand? Stroke(Vector3 at)
    {
        BeginStroke();
        Dab(at);
        return EndStroke();
    }

    // A sector's water surface set (absolute metres), or taken away (null): one undo step.
    public bool SetWater(SectorCoord coord, float? level)
    {
        if (!CanEdit) return false;
        Sync();
        var before = _working.Get(coord)?.Clone();
        var after = before?.Clone() ?? new SculptSector(coord);
        after.Water = level;
        if (before == null ? after.IsEmpty : before.SameAs(after)) return false;
        History.Execute(new TerrainStroke(this, level is { } l ? $"Water at {l:0.#} m over sector {coord}" : $"No water over sector {coord}",
            new List<SculptChange> { new(coord, before, after.IsEmpty ? null : after) }, applied: false));
        History.EndMerge();
        return true;
    }

    public bool Undo() => History.Undo();
    public bool Redo() => History.Redo();

    private static string Verb(TerrainTool tool) => tool switch
    {
        TerrainTool.Raise => "Raise",
        TerrainTool.Lower => "Lower",
        TerrainTool.Smooth => "Smooth",
        TerrainTool.Flatten => "Flatten",
        _ => "Paint",
    };

    // ---- What a stroke writes -------------------------------------------------------------------------

    private const int TerrainSplat_MaxLayers = 4;

    // `delta` metres added to the sculpt at an absolute vertex, in every sector that shares it.
    private bool OffsetVertex(long gx, long gz, float delta)
    {
        foreach (var (sector, x, z) in Sharing(gx, gz)) sector.Heights[z * SculptSector.Resolution + x] += delta;
        return true;
    }

    // `amount` (0–1) more of `layer` painted at an absolute vertex; the layers after it fade by as much, so
    // what was painted last is what shows (the overlay is in layer order).
    private bool PaintVertex(long gx, long gz, int layer, float amount)
    {
        if (amount <= 0f) return false;
        bool changed = false;
        foreach (var (sector, x, z) in Sharing(gx, gz))
        {
            sector.Paint ??= new uint[SculptSector.Vertices];
            int i = z * SculptSector.Resolution + x;
            uint before = sector.Paint[i], paint = before;
            for (int l = 0; l < TerrainSplat_MaxLayers; l++)
            {
                float a = ((paint >> (8 * l)) & 0xFF) / 255f;
                if (l == layer) a += (1f - a) * amount;
                else if (l > layer) a *= 1f - amount;
                uint b = (uint)MathF.Round(Math.Clamp(a, 0f, 1f) * 255f);
                paint = (paint & ~(0xFFu << (8 * l))) | (b << (8 * l));
            }
            sector.Paint[i] = paint;
            changed |= paint != before;
        }
        return changed;
    }

    // The working sectors that hold an absolute vertex (one, two at an edge, four at a corner), each with the
    // vertex's index in it; taken into the stroke's "before" the first time it touches them.
    private IEnumerable<(SculptSector Sector, int X, int Z)> Sharing(long gx, long gz)
    {
        long sx = FloorDiv(gx, Cells), sz = FloorDiv(gz, Cells);
        int lx = (int)(gx - sx * Cells), lz = (int)(gz - sz * Cells);
        for (int bz = 0; bz < (lz == 0 ? 2 : 1); bz++)
            for (int bx = 0; bx < (lx == 0 ? 2 : 1); bx++)
            {
                var coord = new SectorCoord((int)sx - bx, (int)sz - bz);
                if (_strokeBefore != null && !_strokeBefore.ContainsKey(coord)) _strokeBefore[coord] = _working.Get(coord)?.Clone();
                yield return (_working.GetOrAdd(coord), bx == 1 ? Cells : lx, bz == 1 ? Cells : lz);
            }
    }

    // The working sectors handed to the world's terrain, and the loaded ones among `coords` generated again.
    private void Push(IEnumerable<SectorCoord> coords)
    {
        if (Terrain is not { } terrain) return;
        var list = coords.ToList();
        foreach (var coord in list) terrain.Resculpt(coord, _working.Get(coord));
        foreach (var coord in list) terrain.Refresh(coord);   // after all of them: an edge's normals read both sides
    }

    // What a command does to the document: these sectors' sculpt set (null: none), and the world shown it.
    internal void Put(IReadOnlyList<SculptChange> changes, bool before)
    {
        var touched = new HashSet<SectorCoord>();
        foreach (var change in changes)
        {
            var s = before ? change.Before : change.After;
            if (s == null) _working.Remove(change.Coord);
            else _working.Set(s.Clone());
            for (int z = -1; z <= 1; z++)
                for (int x = -1; x <= 1; x++)
                    touched.Add(new SectorCoord(change.Coord.X + x, change.Coord.Z + z));
        }
        if (Terrain is not { } terrain) return;
        foreach (var change in changes) terrain.Resculpt(change.Coord, _working.Get(change.Coord));
        foreach (var coord in touched) terrain.Refresh(coord);   // the neighbours too: their edge normals read these
    }

    private static IEnumerable<SectorCoord> SectorsOf(long x0, long z0, long x1, long z1)
    {
        for (long sz = FloorDiv(z0, Cells); sz <= FloorDiv(z1, Cells); sz++)
            for (long sx = FloorDiv(x0, Cells); sx <= FloorDiv(x1, Cells); sx++)
                yield return new SectorCoord((int)sx, (int)sz);
    }

    // The ground's height at an absolute vertex, from its loaded sector.
    private static float HeightAtVertex(Terrain terrain, long gx, long gz)
    {
        long sx = FloorDiv(gx, Cells), sz = FloorDiv(gz, Cells);
        var sector = terrain.Load(new SectorCoord((int)sx, (int)sz));
        return sector == null ? 0f : sector.Heights[(int)(gx - sx * Cells), (int)(gz - sz * Cells)];
    }

    private static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    // ---- Seeing it ------------------------------------------------------------------------------------

    // The ground around `at` (origin space) loaded, `radius` sectors each way: an edit world streams nothing,
    // so the editor loads what it looks at.
    public void LoadAround(Vector3 at, int radius = 1)
    {
        if (Terrain is not { } terrain) return;
        var centre = terrain.Origin.SectorOf(at);
        for (int z = -radius; z <= radius; z++)
            for (int x = -radius; x <= radius; x++)
                terrain.Load(new SectorCoord(centre.X + x, centre.Z + z));
    }

    // Where the ray meets the loaded ground (origin space), or null when it does not within `reach` metres.
    public Vector3? Pick(EditorRay ray, float reach = 4000f)
    {
        if (Terrain is not { } terrain) return null;
        float step = 2f, previous = 0f;
        bool wasAbove = Above(terrain, ray.Origin);
        for (float t = step; t <= reach; t += step)
        {
            var p = ray.At(t);
            if (!terrain.IsLoaded(terrain.Origin.SectorOf(p))) { previous = t; continue; }
            bool above = Above(terrain, p);
            if (wasAbove && !above)
            {
                float lo = previous, hi = t;   // the crossing, to a centimetre
                for (int i = 0; i < 16; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (Above(terrain, ray.At(mid))) lo = mid; else hi = mid;
                }
                var hit = ray.At(hi);
                return hit with { Y = terrain.HeightAt(hit.X, hit.Z) };
            }
            wasAbove = above;
            previous = t;
        }
        return null;
    }

    private static bool Above(Terrain terrain, Vector3 p) => p.Y >= terrain.HeightAt(p.X, p.Z);

    // ---- Saving ---------------------------------------------------------------------------------------

    // Where a save writes: the file the sculpt was read from, if a mount can write it, else a new one in the
    // mount of the terrain record's namespace. "" when there is nowhere.
    public string FilePath()
    {
        if (Record.IsEmpty || SculptPath.Length == 0) return "";
        var path = VirtualPath.Parse(SculptPath);
        if (Engine.Vfs.Which(path) is { } holder && holder.PhysicalPath(path) is { } existing) return existing;
        foreach (var mount in Engine.Vfs.Mounts.Reverse())
            if (string.Equals(mount.RecordNamespace, Record.Namespace, StringComparison.OrdinalIgnoreCase) && mount.WritablePath(path) is { } file)
                return file;
        return "";
    }

    public bool Save()
    {
        if (!CanEdit) { Log.Warn(LogCat.Editor, "No terrain to save: the world's ground has no generator"); return false; }
        Sync();
        string file = FilePath();
        if (file.Length == 0)
        {
            Log.Error(LogCat.Editor, Record.IsEmpty
                ? "Nowhere to save the sculpt: the world's ground did not come from a terrain record"
                : $"Nowhere to save '{SculptPath}': no writable mount for namespace '{Record.Namespace}'");
            return false;
        }
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            using (var stream = File.Create(temp)) _working.Write(stream);
            File.Move(temp, file, overwrite: true);   // whole or not at all
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(LogCat.Editor, $"Could not save the terrain sculpt to {file}: {ex.Message}");
            return false;
        }
        History.MarkSaved();
        Log.Info(LogCat.Editor, $"Saved the sculpt of terrain '{Record}' ({_working.Count} sector(s)) to {file}");
        return true;
    }

    public string Describe() =>
        !CanEdit ? "no terrain to sculpt: the world's ground has no generator (a scene's `terrain`)"
        : $"{Title}: {_working.Count} sculpted sector(s), {_working.Sectors.Count(s => s.Water != null)} with water; " +
          $"saved to {(SculptPath.Length > 0 ? SculptPath : "(nowhere: not a terrain record's ground)")}; brush: {Brush}";
}

// One sector's sculpt before and after a command.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed record SculptChange(SectorCoord Coord, SculptSector? Before, SculptSector? After);

// A brush stroke, or a sector's water set: the sectors' sculpt before and after. A stroke was applied as it
// was drawn, so its first Do has nothing left to do.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class TerrainStroke : IEditorCommand
{
    private readonly TerrainDocument _document;
    private readonly List<SculptChange> _changes;
    private bool _applied;

    internal TerrainStroke(TerrainDocument document, string description, List<SculptChange> changes, bool applied)
    {
        _document = document;
        Description = description;
        _changes = changes;
        _applied = applied;
    }

    public string Description { get; }
    public IReadOnlyList<SculptChange> Changes => _changes;

    public void Do()
    {
        if (_applied) { _applied = false; return; }
        _document.Put(_changes, before: false);
    }

    public void Undo() => _document.Put(_changes, before: true);
}
