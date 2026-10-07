#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage.Editing;

// Blocking a level out of brushes (issue #61): boxes, wedges and cylinders on the grid, a material per
// face, without TrenchBroom. A brush is a placement of `sage:brush`, `sage:wedge` or `sage:cylinder`
// (BrushPart, Sage.Simulation), so moving, turning, scaling, duplicating, deleting, undo and saving are a
// placement's (ViewportTools, the gizmos); what is here is what only a brush has — its shape, its size and
// its faces' materials — each one command on the document's log, written as the placement's overrides of
// its `brush` part. The Brushes panel (Sage.Editor) and the `ed_brush*` commands press these.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class BlockoutTools
{
    public const string Part = "brush";

    // The engine's prefab for each shape (engine_content/data/blockout.json).
    public static RecordId PrefabOf(BrushShape shape) => shape switch
    {
        BrushShape.Wedge => new RecordId("sage", "wedge"),
        BrushShape.Cylinder => new RecordId("sage", "cylinder"),
        _ => new RecordId("sage", "brush"),
    };

    // A placement's brush as it was built (its entity's BlockoutBrush); false when it is not a brush.
    public static bool TryGet(EditDocument document, Placement placement, out BlockoutBrush brush)
    {
        brush = default;
        var entity = document.EntityOf(placement);
        if (entity.IsNull || !document.World.IsAlive(entity) || !document.World.Has<BlockoutBrush>(entity)) return false;
        brush = document.World.Get<BlockoutBrush>(entity);
        return true;
    }

    public static bool IsBrush(EditDocument document, Placement placement) => TryGet(document, placement, out _);

    // The document's brushes, in document order.
    public static IEnumerable<Placement> Brushes(EditDocument document) => document.Placements.Where(p => IsBrush(document, p)).ToList();

    // `size` with every axis on the grid (`grid` metres; 0 leaves it), and never below one step.
    public static Vector3 SnapSize(Vector3 size, float grid) => grid > 0f
        ? new Vector3(MathF.Max(grid, Snap.ToGrid(size.X, grid)), MathF.Max(grid, Snap.ToGrid(size.Y, grid)), MathF.Max(grid, Snap.ToGrid(size.Z, grid)))
        : size;

    public static Vector3 SnapPoint(Vector3 at, float grid) =>
        grid > 0f ? new Vector3(Snap.ToGrid(at.X, grid), Snap.ToGrid(at.Y, grid), Snap.ToGrid(at.Z, grid)) : at;

    // ---- The edits ----------------------------------------------------------------------------------

    // A new brush, its floor's middle at `at` (the document's frame), `size` metres, every face `material`
    // (empty: plain); both snapped to `grid`. One undo step. Null when no document is open.
    public static Placement? Place(EditDocument document, BrushShape shape, Vector3 at, Vector3 size, RecordId material = default,
                                   string name = "", float grid = 0f)
    {
        if (!document.IsOpen) return null;
        var prefab = PrefabOf(shape);
        if (!document.Engine.Records.Exists("prefab", prefab))
        {
            Log.Warn(LogCat.Editor, $"No prefab '{prefab}' to place a {shape.ToString().ToLowerInvariant()} from (is the engine's content mounted?)");
            return null;
        }
        size = SnapSize(size, grid);
        if (!(size.X > 0f && size.Y > 0f && size.Z > 0f)) { Log.Warn(LogCat.Editor, $"A brush needs a size above 0, not {size}"); return null; }
        var body = new JsonObject { ["size"] = Node(document, size) };
        if (!material.IsEmpty) body["material"] = material.ToString();
        var placement = new Placement
        {
            Prefab = prefab,
            At = SnapPoint(at, grid),
            Name = Placing.UniqueName(document, prefab, name),
            Overrides = new PrefabOverrides { Parts = new JsonObject { [Part] = body } },
        };
        document.History.EndMerge();
        bool done = document.Execute(new AddPlacement(document, placement));
        document.History.EndMerge();
        return done ? placement : null;
    }

    // The brush resized to `size` (snapped to `grid`). A drag of the size is one step (SetOverride merges).
    public static bool SetSize(EditDocument document, Placement placement, Vector3 size, float grid = 0f)
    {
        size = SnapSize(size, grid);
        if (!(size.X > 0f && size.Y > 0f && size.Z > 0f)) { Log.Warn(LogCat.Editor, $"A brush needs a size above 0, not {size}"); return false; }
        return Set(document, placement, "size", Node(document, size));
    }

    // The brush made another shape; a cylinder's `sides` too when given. A face's material that the new
    // shape has no face for stays in the document and is a problem the panel and the load name.
    public static bool SetShape(EditDocument document, Placement placement, BrushShape shape, int? sides = null)
    {
        if (!IsBrush(document, placement)) return NotABrush(placement);
        var commands = new List<IEditorCommand> { new SetOverride(document, placement, OverrideSection.Part, Part, "shape", Node(document, shape)) };
        if (sides is { } count)
        {
            if (count < BrushPart.MinSides || count > BrushPart.MaxSides)
            {
                Log.Warn(LogCat.Editor, $"A cylinder has {BrushPart.MinSides} to {BrushPart.MaxSides} sides, not {count}");
                return false;
            }
            commands.Add(new SetOverride(document, placement, OverrideSection.Part, Part, "sides", JsonValue.Create(count)));
        }
        document.History.EndMerge();
        bool done = document.Execute(commands.Count == 1 ? commands[0] : new CommandGroup($"Make {AddPlacement.Label(placement)} a {shape.ToString().ToLowerInvariant()}", commands));
        document.History.EndMerge();
        return done;
    }

    public static bool SetSides(EditDocument document, Placement placement, int sides)
    {
        if (sides < BrushPart.MinSides || sides > BrushPart.MaxSides)
        {
            Log.Warn(LogCat.Editor, $"A cylinder has {BrushPart.MinSides} to {BrushPart.MaxSides} sides, not {sides}");
            return false;
        }
        return Set(document, placement, "sides", JsonValue.Create(sides));
    }

    // A material for one face (`face` null: the whole brush). An empty `material` takes the face's own away,
    // so it is the brush's again (for the whole brush: plain). A material that is not a record is refused.
    public static bool SetMaterial(EditDocument document, Placement placement, BrushFace? face, RecordId material)
    {
        if (!TryGet(document, placement, out var brush)) return NotABrush(placement);
        if (face is { } f && !BrushPart.FacesOf(brush.Shape).Contains(f))
        {
            Log.Warn(LogCat.Editor, $"A {brush.Shape.ToString().ToLowerInvariant()} has no {f.ToString().ToLowerInvariant()} face "
                                  + $"(it has {string.Join(", ", BrushPart.FacesOf(brush.Shape).Select(x => x.ToString().ToLowerInvariant()))})");
            return false;
        }
        if (!material.IsEmpty && !IsMaterial(document.Engine, material))
        {
            Log.Warn(LogCat.Editor, $"'{material}' is not a material (see rec_list material)");
            return false;
        }
        string field = face is { } named ? FieldOf(named) : "material";
        if (material.IsEmpty)
        {
            document.History.EndMerge();
            bool cleared = document.Execute(new ClearOverride(document, placement, OverrideSection.Part, Part, field));
            document.History.EndMerge();
            return cleared;
        }
        return Set(document, placement, field, JsonValue.Create(material.ToString()), merge: false);
    }

    // The face a click's ray meets first, on whatever brush of the document it meets first: for painting.
    public static (Placement Placement, BrushFace Face)? FaceAt(EditDocument document, in EditorRay ray)
    {
        if (!document.IsOpen) return null;
        var direction = Vector3.Normalize(ray.Direction);
        (Placement, BrushFace, float)? best = null;
        foreach (var placement in document.Placements)
        {
            if (!TryGet(document, placement, out var brush)) continue;
            var entity = document.EntityOf(placement);
            var transform = document.World.Get<Transform>(entity);
            if (brush.FaceHit(transform.LocalPosition, transform.LocalRotation, ray.Origin, direction) is not { } hit) continue;
            if (best is { } b && hit.Distance >= b.Item3) continue;
            best = (placement, hit.Face, hit.Distance);
        }
        return best is { } found ? (found.Item1, found.Item2) : null;
    }

    // A click with a material in hand: the face under it takes the material (the whole brush when `whole`).
    // The brush and face it painted, or null when the ray met no brush.
    public static (Placement Placement, BrushFace Face)? PaintAt(EditDocument document, in EditorRay ray, RecordId material, bool whole = false)
    {
        if (FaceAt(document, ray) is not { } hit) return null;
        return SetMaterial(document, hit.Placement, whole ? null : hit.Face, material) ? hit : null;
    }

    // The part's JSON field for a face.
    public static string FieldOf(BrushFace face) => face.ToString().ToLowerInvariant();

    public static bool TryParseFace(string text, out BrushFace face) =>
        Enum.TryParse(text, ignoreCase: true, out face) && Enum.IsDefined(face) && !int.TryParse(text, out _);

    public static bool TryParseShape(string text, out BrushShape shape)
    {
        shape = BrushShape.Box;
        if (string.Equals(text, "brush", StringComparison.OrdinalIgnoreCase)) return true;
        return Enum.TryParse(text, ignoreCase: true, out shape) && Enum.IsDefined(shape) && !int.TryParse(text, out _);
    }

    // A material record, when this app has the type: a host with no client (a headless tool) has no
    // `material` records to look in, and takes the id as written (the load checks it where it can).
    public static bool IsMaterial(Engine engine, RecordId id) =>
        engine.Records.Exists("material", id) || !engine.Records.Ids("material").Any();

    // Every material there is, for a picker.
    public static IReadOnlyList<RecordId> Materials(Engine engine) =>
        engine.Records.Ids("material").OrderBy(id => id.ToString(), StringComparer.Ordinal).ToList();

    // One brush, as `ed_brushes` lists it.
    public static string Describe(EditDocument document, Placement placement)
    {
        if (!TryGet(document, placement, out var brush)) return $"{AddPlacement.Label(placement)}: not a brush";
        var text = new StringBuilder($"{AddPlacement.Label(placement)}: {brush.Shape.ToString().ToLowerInvariant()} ");
        text.Append(CultureInfo.InvariantCulture, $"{brush.Size.X:0.###} x {brush.Size.Y:0.###} x {brush.Size.Z:0.###} m at {placement.At}");
        if (brush.Shape == BrushShape.Cylinder) text.Append(CultureInfo.InvariantCulture, $", {brush.Sides} sides");
        text.Append(", ").Append(brush.Material.IsEmpty ? "plain" : brush.Material.ToString());
        foreach (var face in BrushPart.FacesOf(brush.Shape))
            if (!brush.FaceMaterial(face).IsEmpty) text.Append(CultureInfo.InvariantCulture, $", {FieldOf(face)} {brush.FaceMaterial(face)}");
        return text.ToString();
    }

    private static bool Set(EditDocument document, Placement placement, string field, JsonNode? value, bool merge = true)
    {
        if (!IsBrush(document, placement)) return NotABrush(placement);
        if (!merge) document.History.EndMerge();
        bool done = document.Execute(new SetOverride(document, placement, OverrideSection.Part, Part, field, value));
        if (!merge) document.History.EndMerge();
        return done;
    }

    private static bool NotABrush(Placement placement)
    {
        Log.Warn(LogCat.Editor, $"'{AddPlacement.Label(placement)}' is not a brush");
        return false;
    }

    private static JsonNode? Node<T>(EditDocument document, T value) => JsonSerializer.SerializeToNode(value, document.Engine.Records.Json);

    // ---- The console --------------------------------------------------------------------------------

    // `ed_brush`, `ed_brush_size`, `ed_brush_shape`, `ed_brush_material` and `ed_brushes`, on the selection's
    // document, snapping to `tools`' grid when snapping is on.
    public static void Register(CVarRegistry cvars, Func<EditorSelection?> selection, ViewportTools tools)
    {
        cvars.RegisterCommand("ed_brush", CVarFlags.DevOnly,
            "ed_brush <box|wedge|cylinder> <x> <y> <z> [<w> <h> <d>] [material] [name]: a brush standing at x y z (its floor's middle), snapped to the grid, and select it.", a =>
        {
            if (!Current(selection, out var s)) return;
            const string usage = "ed_brush <box|wedge|cylinder> <x> <y> <z> [<w> <h> <d>] [material] [name]";
            if (a.Count < 4 || !TryParseShape(a[0], out var shape) || !Numbers(a, 1, 3, out var at)) { Log.Warn(LogCat.Console, usage); return; }
            int next = 4;
            Vector3 size = shape switch { BrushShape.Wedge => new Vector3(2, 1, 2), BrushShape.Cylinder => new Vector3(1, 2, 1), _ => new Vector3(2) };
            if (a.Count >= 7 && Numbers(a, 4, 3, out var given)) { size = given; next = 7; }
            RecordId material = default;
            if (a.Count > next)
            {
                if (!TryMaterial(s.Document.Engine, a[next], out material)) return;
                next++;
            }
            string name = a.Count > next ? string.Join(' ', a.Args.Skip(next)) : "";
            if (Place(s.Document, shape, at, size, material, name, tools.GridStep) is not { } placed) return;
            s.Select(placed);
            Log.Info(LogCat.Console, $"placed {Describe(s.Document, placed)}");
        });

        cvars.RegisterCommand("ed_brush_size", CVarFlags.DevOnly,
            "ed_brush_size [name] <w> <h> <d>: resize a brush (else the selected one), snapped to the grid.", a =>
        {
            if (!Brush(selection, a, 3, "ed_brush_size [name] <w> <h> <d>", out var s, out var placement)) return;
            if (!Numbers(a, a.Count - 3, 3, out var size)) { Log.Warn(LogCat.Console, "ed_brush_size [name] <w> <h> <d>"); return; }
            s.Document.History.EndMerge();
            if (SetSize(s.Document, placement, size, tools.GridStep)) Log.Info(LogCat.Console, Describe(s.Document, placement));
            s.Document.History.EndMerge();
        });

        cvars.RegisterCommand("ed_brush_shape", CVarFlags.DevOnly,
            "ed_brush_shape [name] <box|wedge|cylinder> [sides]: make a brush (else the selected one) another shape.", a =>
        {
            const string usage = "ed_brush_shape [name] <box|wedge|cylinder> [sides]";
            if (!Current(selection, out var s)) return;
            int at = -1;
            for (int i = a.Count - 1; i >= 0 && at < 0; i--) if (TryParseShape(a[i], out _) && !string.Equals(a[i], "brush", StringComparison.OrdinalIgnoreCase)) at = i;
            if (at < 0 || !TryParseShape(a[at], out var shape)) { Log.Warn(LogCat.Console, usage); return; }
            int? sides = null;
            if (a.Count > at + 1)
            {
                if (!int.TryParse(a[at + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) { Log.Warn(LogCat.Console, usage); return; }
                sides = n;
            }
            if (!Named(s, string.Join(' ', a.Args.Take(at)), a.Name, out var placement)) return;
            if (SetShape(s.Document, placement, shape, sides)) Log.Info(LogCat.Console, Describe(s.Document, placement));
        });

        cvars.RegisterCommand("ed_brush_material", CVarFlags.DevOnly,
            "ed_brush_material [name] [face] <material|none>: a material for a face (top, bottom, north, south, east, west, side) or the whole brush (else the selected one).", a =>
        {
            const string usage = "ed_brush_material [name] [face] <material|none>";
            if (!Current(selection, out var s)) return;
            if (a.Count == 0) { Log.Warn(LogCat.Console, usage); return; }
            RecordId material = default;
            if (!IsNone(a[^1]) && !TryMaterial(s.Document.Engine, a[^1], out material)) return;
            int rest = a.Count - 1;
            BrushFace? face = null;
            if (rest > 0 && TryParseFace(a[rest - 1], out var parsed)) { face = parsed; rest--; }
            else if (rest > 0 && string.Equals(a[rest - 1], "all", StringComparison.OrdinalIgnoreCase)) rest--;
            if (!Named(s, string.Join(' ', a.Args.Take(rest)), a.Name, out var placement)) return;
            if (SetMaterial(s.Document, placement, face, material)) Log.Info(LogCat.Console, Describe(s.Document, placement));
        });

        cvars.RegisterCommand("ed_brushes", CVarFlags.DevOnly, "ed_brushes: the open document's brushes, their shapes, sizes and materials.", _ =>
        {
            if (!Current(selection, out var s)) return;
            if (!s.Document.IsOpen) { Log.Warn(LogCat.Console, "ed_brushes: no document is open"); return; }
            var brushes = Brushes(s.Document).ToList();
            var text = new StringBuilder($"{s.Document.Id}: {brushes.Count} brush(es)");
            foreach (var brush in brushes) text.Append("\n  ").Append(Describe(s.Document, brush));
            Log.Info(LogCat.Console, text.ToString());
        });
    }

    private static bool IsNone(string text) => text is "none" or "-" or "\"\"";

    private static bool TryMaterial(Engine engine, string text, out RecordId material)
    {
        material = default;
        try { material = RecordId.Parse(text, EditDocument.GameNamespace(engine)); }
        catch (FormatException) { Log.Warn(LogCat.Console, $"'{text}' is not a record id"); return false; }
        // A bare name may be the engine's (`lit_default`) rather than the game's.
        if (!IsMaterial(engine, material) && text.IndexOf(':') < 0 && IsMaterial(engine, new RecordId("sage", text))) material = new RecordId("sage", text);
        if (IsMaterial(engine, material)) return true;
        Log.Warn(LogCat.Console, $"'{material}' is not a material (see rec_list material)");
        return false;
    }

    // The brush named by the arguments before the last `numbers`, else the selection's.
    private static bool Brush(Func<EditorSelection?> selection, ConsoleArgs a, int numbers, string usage,
                              [NotNullWhen(true)] out EditorSelection? s, [NotNullWhen(true)] out Placement? placement)
    {
        placement = null;
        if (!Current(selection, out s)) return false;
        if (a.Count < numbers) { Log.Warn(LogCat.Console, usage); return false; }
        return Named(s, string.Join(' ', a.Args.Take(a.Count - numbers)), a.Name, out placement);
    }

    private static bool Named(EditorSelection s, string name, string command, [NotNullWhen(true)] out Placement? placement)
    {
        placement = null;
        if (!s.Document.IsOpen) { Log.Warn(LogCat.Console, $"{command}: no document is open"); return false; }
        placement = name.Length > 0 ? s.Document.Find(name) : s.Placement;
        if (placement == null)
        {
            Log.Warn(LogCat.Console, name.Length > 0 ? $"{command}: no placement called '{name}'" : $"{command}: nothing selected");
            return false;
        }
        if (!IsBrush(s.Document, placement)) return NotABrush(placement);
        return true;
    }

    private static bool Numbers(ConsoleArgs a, int first, int count, out Vector3 value)
    {
        value = default;
        var n = new float[3];
        if (first < 0 || a.Count < first + count) return false;
        for (int i = 0; i < count; i++)
            if (!float.TryParse(a[first + i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i])) return false;
        value = new Vector3(n[0], n[1], n[2]);
        return true;
    }

    private static bool Current(Func<EditorSelection?> selection, [NotNullWhen(true)] out EditorSelection? s)
    {
        s = selection();
        if (s != null) return true;
        Log.Warn(LogCat.Console, "no world yet: the editor's brush commands work once a world exists");
        return false;
    }
}
