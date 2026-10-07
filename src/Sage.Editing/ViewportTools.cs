#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace Sage.Editing;

// Which gizmo the selection shows: move (axes and planes), turn (a ring about each axis) or scale.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public enum GizmoMode { Move, Rotate, Scale }

// Selecting and moving things in the viewport (issue #221), without the viewport: the snapping settings,
// which gizmo is up, the edits a selection takes (move, turn, delete, duplicate), and their console
// commands. The ImGui viewport (Sage.Editor) draws the gizmo and presses these; a test presses the
// commands, which is how the tools are checked (phase 10a decision 6).
//
// Every edit is one command in the document's log: a console move or a keyboard delete closes the merge
// behind it, so the next drag of the same thing is an undo step of its own rather than folding into it.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class ViewportTools
{
    public const float DefaultGrid = 0.5f, DefaultAngleStep = 15f, DefaultScaleStep = 0.1f;

    // Snapping, as `ed_snap`, `ed_grid` and `ed_angle` set it: commands rather than cvars, because a
    // tool registered after config.cfg was read could not have cvars (CVarRegistry.CVarSeal), and these
    // are the editor's state, not settings a game keeps.
    public bool Snap { get; set; } = true;
    public float Grid { get; set; } = DefaultGrid;               // metres
    public float AngleStep { get; set; } = DefaultAngleStep;     // degrees
    public float ScaleStep { get; set; } = DefaultScaleStep;     // a factor (#367)
    public GizmoMode Mode { get; set; } = GizmoMode.Move;
    public GizmoSpace Space { get; set; } = GizmoSpace.World;    // #367; the scale gizmo is always local

    // What a drag snaps to now: 0 when snapping is off.
    public float GridStep => Snap && Grid > 0f ? Grid : 0f;
    public float AngleStepDegrees => Snap && AngleStep > 0f ? AngleStep : 0f;
    public float ScaleStepFactor => Snap && ScaleStep > 0f ? ScaleStep : 0f;

    // ---- The edits ----------------------------------------------------------------------------------

    // `placement` to `at` (the document's frame, as the file says it): one undo step.
    public static void Move(EditDocument document, Placement placement, Vector3 at) =>
        Set(document, placement, PlacementFields.Of(placement) with { At = at });

    // `placement` turned to `yawDegrees` about Y: one undo step.
    public static void Rotate(EditDocument document, Placement placement, float yawDegrees) =>
        Set(document, placement, PlacementFields.Of(placement) with { Yaw = WrapDegrees(yawDegrees) });

    private static void Set(EditDocument document, Placement placement, PlacementFields fields)
    {
        document.History.EndMerge();
        document.Execute(new SetPlacement(document, placement, fields));
        document.History.EndMerge();
    }

    // ---- Groups (issue #367): every edit of several placements is one undo step ------------------------

    // Every placement moved by `delta` (origin space).
    public static void MoveBy(EditDocument document, IReadOnlyList<Placement> placements, Vector3 delta) =>
        SetAll(document, placements, placements.Select(p => PlacementFields.Of(p) with { At = p.At + delta }).ToList());

    // Every placement turned by `turn` (origin space) about the last one's position, the gizmo's pivot:
    // their positions swing round it and each turns by the same amount.
    public static void RotateBy(EditDocument document, IReadOnlyList<Placement> placements, Quaternion turn)
    {
        if (placements.Count == 0) return;
        Vector3 pivot = OriginOf(document, placements[^1]);
        SetAll(document, placements, placements.Select(p =>
            PlacementGroup.Rotated(PlacementFields.Of(p), OriginOf(document, p), pivot, Quaternion.Normalize(turn))).ToList());
    }

    // Every placement scaled by `factors` along the axes `axes` turns X, Y and Z to (the last one's
    // rotation, for the scale gizmo), about the last one's position.
    public static void ScaleBy(EditDocument document, IReadOnlyList<Placement> placements, Vector3 factors, Quaternion axes)
    {
        if (placements.Count == 0) return;
        Vector3 pivot = OriginOf(document, placements[^1]);
        SetAll(document, placements, placements.Select(p =>
            PlacementGroup.Scaled(PlacementFields.Of(p), OriginOf(document, p), pivot, axes, factors)).ToList());
    }

    // Every placement taken out of the document: one undo step puts them all back where they were.
    public static void Delete(EditDocument document, IReadOnlyList<Placement> placements)
    {
        if (placements.Count == 0) return;
        if (placements.Count == 1) { Delete(document, placements[0]); return; }
        document.History.EndMerge();
        document.Execute(new CommandGroup($"Delete {placements.Count} placements",
            placements.ToList().Select(p => (IEditorCommand)new RemovePlacement(document, p))));
    }

    // A copy of each placement straight after it, as Duplicate does one; the copies are selected (the
    // last original's copy last, so it has the gizmo). One undo step. The copies.
    public static IReadOnlyList<Placement> Duplicate(EditorSelection selection, IReadOnlyList<Placement> placements)
    {
        if (placements.Count == 1) return Duplicate(selection, placements[0]) is { } one ? new[] { one } : Array.Empty<Placement>();
        var document = selection.Document;
        var originals = placements.Where(p => document.IndexOf(p) >= 0).ToList();
        if (originals.Count == 0) return Array.Empty<Placement>();

        // Copied in selection order, so names count up the way they were picked; inserted from the last in
        // the document to the first, so each index is still right when its turn comes.
        var copies = new Dictionary<Placement, Placement>(ReferenceEqualityComparer.Instance);
        var adds = new List<IEditorCommand>();
        var taken = new List<string>();
        foreach (var original in originals)
        {
            var copy = EditDocument.Copy(original);
            copy.Id = "";
            if (copy.Name.Length > 0) copy.Name = UniqueName(document, copy.Name, taken);
            if (copy.Name.Length > 0) taken.Add(copy.Name);
            copies[original] = copy;
        }
        var ids = new List<string>();
        foreach (var original in originals.OrderByDescending(document.IndexOf))
        {
            var copy = copies[original];
            // AddPlacement gives it an id of its own; two copies made before either is in the document
            // would get the same one, so each is told which the others took.
            copy.Id = UniqueId(document, copy.Name.Length > 0 ? copy.Name : copy.Prefab.Id.Name, ids);
            ids.Add(copy.Id);
            adds.Add(new AddPlacement(document, copy, document.IndexOf(original) + 1));
        }
        document.History.EndMerge();
        document.Execute(new CommandGroup($"Duplicate {originals.Count} placements", adds));
        var result = originals.Select(o => copies[o]).ToList();
        selection.Select(result);
        return result;
    }

    private static void SetAll(EditDocument document, IReadOnlyList<Placement> placements, IReadOnlyList<PlacementFields> after)
    {
        if (PlacementGroup.Command(document, placements, after) is not { } command) return;
        document.History.EndMerge();
        document.Execute(command);
        document.History.EndMerge();
    }

    // The axes the gizmo of `placement` has in `space`: the world's, or its own.
    public static Quaternion AxesOf(Placement placement, GizmoSpace space) =>
        space == GizmoSpace.Local ? PlacementFields.Of(placement).Rotation : Quaternion.Identity;

    // Takes the placement out of the document; a selection of it clears (EditorSelection).
    public static void Delete(EditDocument document, Placement placement)
    {
        document.History.EndMerge();
        document.Execute(new RemovePlacement(document, placement));
    }

    // A copy of `placement` straight after it in the document, where it stands, with a name and id of its
    // own (`crate` → `crate_2`), selected so a drag that follows moves the copy. The copy, or null.
    public static Placement? Duplicate(EditorSelection selection, Placement placement)
    {
        var document = selection.Document;
        int index = document.IndexOf(placement);
        if (index < 0) return null;
        var copy = EditDocument.Copy(placement);
        copy.Id = "";   // AddPlacement gives it one of its own, from its name
        if (copy.Name.Length > 0) copy.Name = UniqueName(document, copy.Name);
        document.History.EndMerge();
        document.Execute(new AddPlacement(document, copy, index + 1));
        selection.Select(copy);
        return copy;
    }

    // `wanted`, else it with the next free number (`crate_2`, `crate_3`), among the document's names.
    public static string UniqueName(EditDocument document, string wanted) => UniqueName(document, wanted, Array.Empty<string>());

    private static string UniqueName(EditDocument document, string wanted, IReadOnlyCollection<string> alsoTaken)
    {
        bool Taken(string name) => document.Placements.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                                   || alsoTaken.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (!Taken(wanted)) return wanted;
        string stem = wanted;
        int underscore = wanted.LastIndexOf('_');
        if (underscore > 0 && int.TryParse(wanted.AsSpan(underscore + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            stem = wanted[..underscore];
        for (int n = 2; ; n++)
            if (!Taken($"{stem}_{n}")) return $"{stem}_{n}";
    }

    // EditDocument.UniqueId, with `alsoTaken` counted as taken too.
    private static string UniqueId(EditDocument document, string wanted, IReadOnlyCollection<string> alsoTaken)
    {
        string id = document.UniqueId(wanted);
        if (!alsoTaken.Contains(id, StringComparer.OrdinalIgnoreCase)) return id;
        for (int n = 2; ; n++)
        {
            string candidate = document.UniqueId($"{id}_{n}");
            if (!alsoTaken.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return candidate;
        }
    }

    // Where a placement stands in the world (origin space): where its gizmo is drawn.
    public static Vector3 OriginOf(EditDocument document, Placement placement) =>
        document.World.PlacementPosition(placement, document.Record.Origin, document.Record.RelativeTo);

    // Where a camera looking along `rotation` stands to frame `target` from `distance` metres (F).
    public static Vector3 FramePosition(Vector3 target, Quaternion rotation, float distance) =>
        target - CameraMath.Forward(rotation) * distance;

    // ---- The console --------------------------------------------------------------------------------

    // The tools and their commands, acting on whatever `selection` says is current (the selection belongs
    // to a document, which belongs to a world, and the world changes).
    public static ViewportTools Register(CVarRegistry cvars, Func<EditorSelection?> selection)
    {
        var tools = new ViewportTools();

        cvars.RegisterCommand("ed_select", CVarFlags.DevOnly,
            "ed_select [name]: select a placement (by name or id) or an entity (by name); nothing clears the selection.", a =>
        {
            if (!Current(selection, out var s)) return;
            if (a.Count == 0) { s.Clear(); Log.Info(LogCat.Console, "selection cleared"); return; }
            if (s.Document.IsOpen && s.Document.Find(a.Rest) is { } placement) s.Select(placement);
            else if (s.World.FindByName(a.Rest) is { IsNull: false } entity) s.Select(entity);
            else { Log.Warn(LogCat.Console, $"ed_select: nothing called '{a.Rest}'"); return; }
            Log.Info(LogCat.Console, $"selected {Describe(s)}");
        });

        cvars.RegisterCommand("ed_move", CVarFlags.DevOnly,
            "ed_move [name] <x> <y> <z>: put a placement (else the selection) at x y z, in its document's frame.", a =>
        {
            if (!Target(selection, a, 3, "ed_move [name] <x> <y> <z>", out var s, out var placement, out var n)) return;
            Move(s.Document, placement, new Vector3(n[0], n[1], n[2]));
            Log.Info(LogCat.Console, $"moved {AddPlacement.Label(placement)} to {placement.At}");
        });

        cvars.RegisterCommand("ed_rotate", CVarFlags.DevOnly,
            "ed_rotate [name] <yaw>: turn a placement (else the selection) to yaw degrees about Y.", a =>
        {
            if (!Target(selection, a, 1, "ed_rotate [name] <yaw>", out var s, out var placement, out var n)) return;
            Rotate(s.Document, placement, n[0]);
            Log.Info(LogCat.Console, $"turned {AddPlacement.Label(placement)} to {placement.Yaw:0.###}°");
        });

        cvars.RegisterCommand("ed_delete", CVarFlags.DevOnly, "ed_delete [name]: take a placement (else the selection, all of it) out of the document.", a =>
        {
            if (a.Count == 0 && Group(selection, out var group))
            {
                ViewportTools.Delete(group.Document, group.Placements);
                Log.Info(LogCat.Console, $"deleted {group.Placements.Count} placements");
                return;
            }
            if (!Target(selection, a, 0, "ed_delete [name]", out var s, out var placement, out _)) return;
            Delete(s.Document, placement);
            Log.Info(LogCat.Console, $"deleted {AddPlacement.Label(placement)}");
        });

        cvars.RegisterCommand("ed_duplicate", CVarFlags.DevOnly,
            "ed_duplicate [name]: copy a placement (else the selection, all of it) with a name of its own, and select the copy.", a =>
        {
            if (a.Count == 0 && Group(selection, out var group))
            {
                var copies = Duplicate(group, group.Placements);
                Log.Info(LogCat.Console, $"duplicated {copies.Count} placements");
                return;
            }
            if (!Target(selection, a, 0, "ed_duplicate [name]", out var s, out var placement, out _)) return;
            if (Duplicate(s, placement) is { } copy) Log.Info(LogCat.Console, $"duplicated {AddPlacement.Label(placement)} as {AddPlacement.Label(copy)}");
        });

        // ---- Several at once (issue #367) ----

        cvars.RegisterCommand("ed_select_add", CVarFlags.DevOnly,
            "ed_select_add <name>: add a placement to the selection, or take it out if it is in it (Ctrl+click).", a =>
        {
            if (!Current(selection, out var s)) return;
            if (a.Count == 0) { Log.Warn(LogCat.Console, "ed_select_add <name>"); return; }
            if (!s.Document.IsOpen || s.Document.Find(a.Rest) is not { } placement) { Log.Warn(LogCat.Console, $"ed_select_add: no placement called '{a.Rest}'"); return; }
            s.Toggle(placement);
            Log.Info(LogCat.Console, $"{s.Placements.Count} selected");
        });

        cvars.RegisterCommand("ed_select_all", CVarFlags.DevOnly,
            "ed_select_all [prefab]: select every placement of the open document (or every one of that prefab).", a =>
        {
            if (!Current(selection, out var s)) return;
            if (!s.Document.IsOpen) { Log.Warn(LogCat.Console, "ed_select_all: no document is open"); return; }
            var all = s.Document.Placements.Where(p => a.Count == 0 || string.Equals(p.Prefab.Id.Name, a[0], StringComparison.OrdinalIgnoreCase)
                                                                    || string.Equals(p.Prefab.Id.ToString(), a[0], StringComparison.OrdinalIgnoreCase)).ToList();
            s.Select(all);
            Log.Info(LogCat.Console, $"{all.Count} selected");
        });

        cvars.RegisterCommand("ed_nudge", CVarFlags.DevOnly, "ed_nudge <dx> <dy> <dz>: move everything selected by that much (metres); one undo step.", a =>
        {
            if (!Group(selection, out var s) || !Numbers(a, 3, "ed_nudge <dx> <dy> <dz>", out var n)) return;
            MoveBy(s.Document, s.Placements, new Vector3(n[0], n[1], n[2]));
            Log.Info(LogCat.Console, $"moved {s.Placements.Count} placement(s)");
        });

        cvars.RegisterCommand("ed_turn", CVarFlags.DevOnly,
            "ed_turn <x|y|z> <degrees>: turn everything selected about the gizmo's axis (world or local, ed_space), round the last one selected.", a =>
        {
            if (!Group(selection, out var s)) return;
            if (a.Count < 2 || AxisNamed(a[0]) is not { } axis || Number(a[1]) is not { } degrees) { Log.Warn(LogCat.Console, "ed_turn <x|y|z> <degrees>"); return; }
            var direction = RotateGizmo.AxisOf(axis, AxesOf(s.Placement!, tools.Space));
            RotateBy(s.Document, s.Placements, Quaternion.CreateFromAxisAngle(Vector3.Normalize(direction), degrees * MathF.PI / 180f));
            Log.Info(LogCat.Console, $"turned {s.Placements.Count} placement(s) {degrees:0.###}° about {a[0]}");
        });

        cvars.RegisterCommand("ed_scale", CVarFlags.DevOnly,
            "ed_scale <factor> | <x> <y> <z>: scale everything selected along the last one's own axes, round it; one undo step.", a =>
        {
            if (!Group(selection, out var s)) return;
            int count = a.Count >= 3 ? 3 : 1;
            if (!Numbers(a, count, "ed_scale <factor> | <x> <y> <z>", out var n)) return;
            var factors = count == 3 ? new Vector3(n[0], n[1], n[2]) : new Vector3(n[0]);
            if (!(factors.X > 0f && factors.Y > 0f && factors.Z > 0f)) { Log.Warn(LogCat.Console, "ed_scale: a factor must be above 0"); return; }
            ScaleBy(s.Document, s.Placements, factors, AxesOf(s.Placement!, GizmoSpace.Local));
            Log.Info(LogCat.Console, $"scaled {s.Placements.Count} placement(s) by {factors}");
        });

        cvars.RegisterCommand("ed_space", CVarFlags.DevOnly, "ed_space [world|local]: whether the move and turn gizmos follow the world's axes or the selection's own.", a =>
        {
            if (a.Count > 0)
            {
                if (a[0].StartsWith("w", StringComparison.OrdinalIgnoreCase)) tools.Space = GizmoSpace.World;
                else if (a[0].StartsWith("l", StringComparison.OrdinalIgnoreCase)) tools.Space = GizmoSpace.Local;
                else { Log.Warn(LogCat.Console, "ed_space [world|local]"); return; }
            }
            Log.Info(LogCat.Console, $"gizmo space: {tools.Space.ToString().ToLowerInvariant()}");
        });

        cvars.RegisterCommand("ed_scalestep", CVarFlags.DevOnly, "ed_scalestep [factor]: the step a scale drag snaps to (0 = none).", a =>
        {
            if (a.Count > 0 && Number(a[0]) is { } step) tools.ScaleStep = MathF.Max(step, 0f);
            Log.Info(LogCat.Console, $"scale step {tools.ScaleStep:0.###}{(tools.Snap ? "" : " (snap off)")}");
        });

        cvars.RegisterCommand("ed_snap", CVarFlags.DevOnly, "ed_snap [0|1]: snap gizmo drags to the grid and the angle step (no argument: say which).", a =>
        {
            if (a.Count > 0) tools.Snap = a[0] is "1" or "true" or "on";
            Log.Info(LogCat.Console, $"snap {(tools.Snap ? "on" : "off")}: grid {tools.Grid:0.###} m, angle {tools.AngleStep:0.###}°");
        });

        cvars.RegisterCommand("ed_grid", CVarFlags.DevOnly, "ed_grid [metres]: the grid a move snaps to (0 = none).", a =>
        {
            if (a.Count > 0 && Number(a[0]) is { } grid) tools.Grid = MathF.Max(grid, 0f);
            Log.Info(LogCat.Console, $"grid {tools.Grid:0.###} m{(tools.Snap ? "" : " (snap off)")}");
        });

        cvars.RegisterCommand("ed_angle", CVarFlags.DevOnly, "ed_angle [degrees]: the step a turn snaps to (0 = none).", a =>
        {
            if (a.Count > 0 && Number(a[0]) is { } step) tools.AngleStep = MathF.Max(step, 0f);
            Log.Info(LogCat.Console, $"angle step {tools.AngleStep:0.###}°{(tools.Snap ? "" : " (snap off)")}");
        });

        cvars.RegisterCommand("ed_gizmo", CVarFlags.DevOnly, "ed_gizmo [move|rotate|scale]: which gizmo the selection shows.", a =>
        {
            if (a.Count > 0)
            {
                if (a[0].StartsWith("rot", StringComparison.OrdinalIgnoreCase)) tools.Mode = GizmoMode.Rotate;
                else if (a[0].StartsWith("sc", StringComparison.OrdinalIgnoreCase)) tools.Mode = GizmoMode.Scale;
                else if (a[0].StartsWith("mov", StringComparison.OrdinalIgnoreCase) || a[0].StartsWith("tr", StringComparison.OrdinalIgnoreCase)) tools.Mode = GizmoMode.Move;
                else { Log.Warn(LogCat.Console, "ed_gizmo [move|rotate|scale]"); return; }
            }
            Log.Info(LogCat.Console, $"gizmo: {tools.Mode.ToString().ToLowerInvariant()}");
        });

        BlockoutTools.Register(cvars, selection, tools);   // ed_brush* (#61): brushes snap to this grid
        return tools;
    }

    // The placement a command acts on: the one named by every argument before the last `numbers` (a name
    // may have spaces; quote it or not), else the selection's; and those numbers.
    private static bool Target(Func<EditorSelection?> selection, ConsoleArgs a, int numbers, string usage,
                               [NotNullWhen(true)] out EditorSelection? s, [NotNullWhen(true)] out Placement? placement, out float[] values)
    {
        placement = null;
        values = new float[numbers];
        if (!Current(selection, out s)) return false;
        if (!s.Document.IsOpen) { Log.Warn(LogCat.Console, $"{a.Name}: no document is open"); return false; }
        if (a.Count < numbers) { Log.Warn(LogCat.Console, usage); return false; }
        for (int i = 0; i < numbers; i++)
        {
            if (Number(a[a.Count - numbers + i]) is not { } value) { Log.Warn(LogCat.Console, usage); return false; }
            values[i] = value;
        }
        string name = string.Join(' ', a.Args.Take(a.Count - numbers));
        if (name.Length > 0)
        {
            placement = s.Document.Find(name);
            if (placement == null) Log.Warn(LogCat.Console, $"{a.Name}: no placement called '{name}' in {s.Document.Id}");
        }
        else
        {
            placement = s.Placement;
            if (placement == null)
                Log.Warn(LogCat.Console, s.IsEmpty ? $"{a.Name}: nothing selected" : $"{a.Name}: the selection is not one of {s.Document.Id}'s placements");
        }
        return placement != null;
    }

    // The selection, when it holds at least one of the open document's placements.
    private static bool Group(Func<EditorSelection?> selection, [NotNullWhen(true)] out EditorSelection? s)
    {
        if (!Current(selection, out s)) return false;
        if (!s.Document.IsOpen || s.Placements.Count == 0)
        {
            Log.Warn(LogCat.Console, "nothing of the open document is selected");
            return false;
        }
        return true;
    }

    private static bool Numbers(ConsoleArgs a, int count, string usage, out float[] values)
    {
        values = new float[count];
        if (a.Count < count) { Log.Warn(LogCat.Console, usage); return false; }
        for (int i = 0; i < count; i++)
        {
            if (Number(a[i]) is not { } value) { Log.Warn(LogCat.Console, usage); return false; }
            values[i] = value;
        }
        return true;
    }

    private static GizmoHandle? AxisNamed(string text) => text.ToLowerInvariant() switch
    {
        "x" => GizmoHandle.X,
        "y" => GizmoHandle.Y,
        "z" => GizmoHandle.Z,
        _ => null,
    };

    private static float? Number(string text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : null;

    internal static float WrapDegrees(float degrees)
    {
        degrees %= 360f;
        if (degrees > 180f) degrees -= 360f;
        else if (degrees <= -180f) degrees += 360f;
        return degrees;
    }

    private static string Describe(EditorSelection s) =>
        s.Placement is { } placement ? $"{AddPlacement.Label(placement)} ({s.Document.Id})" : World.Describe(s.Entity);

    private static bool Current(Func<EditorSelection?> selection, [NotNullWhen(true)] out EditorSelection? s)
    {
        s = selection();
        if (s != null) return true;
        Log.Warn(LogCat.Console, "no world yet: the editor's selection commands work once a world exists");
        return false;
    }
}

// One drag of a gizmo handle (issue #221): begun on the mouse going down over a handle, updated every
// frame the mouse moves, ended when it comes up. Every update is a SetPlacement measured from where the
// drag began, and they merge, so **a drag is one undo step** (CommandLog's merging, §10e).
//
// Since issue #367 a drag may hold several placements (the selection) and is a SetPlacements then; the
// gizmo stands on the last of them (`Placement`), which the group turns and scales about. `Space` says
// whether the handles are the world's axes or that placement's own; a scale drag is always its own.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class GizmoDrag
{
    private readonly EditDocument _document;
    private readonly EditorRay _start;
    private readonly Placement[] _placements;
    private readonly PlacementFields[] _from;
    private readonly Vector3[] _origins;

    private GizmoDrag(EditDocument document, Placement[] placements, GizmoMode mode, GizmoHandle handle, EditorRay start, GizmoSpace space)
    {
        _document = document;
        _placements = placements;
        Mode = mode;
        Handle = handle;
        Space = mode == GizmoMode.Scale ? GizmoSpace.Local : space;
        _start = start;
        _from = placements.Select(PlacementFields.Of).ToArray();
        _origins = placements.Select(p => ViewportTools.OriginOf(document, p)).ToArray();
        Origin = _origins[^1];
        Axes = Space == GizmoSpace.Local ? _from[^1].Rotation : Quaternion.Identity;
    }

    public Placement Placement => _placements[^1];
    public IReadOnlyList<Placement> Placements => _placements;
    public GizmoMode Mode { get; }
    public GizmoHandle Handle { get; }      // a turn's ring: X, Y or Z (None is Y, the one ring there was)
    public GizmoSpace Space { get; }
    public Vector3 Origin { get; }          // where the gizmo's placement stood when the drag began (origin space)
    public Quaternion Axes { get; }         // which way the handles pointed when it began

    // A drag of `placement`'s gizmo from `startRay`; null when it is not the open document's.
    public static GizmoDrag? Begin(EditDocument document, Placement placement, GizmoMode mode, GizmoHandle handle, in EditorRay startRay) =>
        Begin(document, new[] { placement }, mode, handle, startRay);

    // A drag of a group's gizmo (the last placement's); null when none of them is the open document's.
    public static GizmoDrag? Begin(EditDocument document, IReadOnlyList<Placement> placements, GizmoMode mode, GizmoHandle handle,
                                   in EditorRay startRay, GizmoSpace space = GizmoSpace.World)
    {
        if (!document.IsOpen) return null;
        var members = placements.Where(p => document.IndexOf(p) >= 0).ToArray();
        if (members.Length == 0) return null;
        if (mode == GizmoMode.Move && handle == GizmoHandle.None) return null;
        if (mode == GizmoMode.Rotate && handle == GizmoHandle.None) handle = GizmoHandle.Y;
        if (mode == GizmoMode.Rotate && handle is not (GizmoHandle.X or GizmoHandle.Y or GizmoHandle.Z)) return null;
        if (mode == GizmoMode.Scale && handle is not (GizmoHandle.X or GizmoHandle.Y or GizmoHandle.Z or GizmoHandle.All)) return null;
        document.History.EndMerge();   // whatever came before is its own step
        return new GizmoDrag(document, members, mode, handle, startRay, space);
    }

    // The pointer is on `ray` now: the placements go where that puts them, snapped by `grid` metres,
    // `angleStep` degrees or `scaleStep` (0 = off). False when nothing changed (no answer for this ray, or
    // the same place).
    public bool Update(in EditorRay ray, float grid = 0f, float angleStep = 0f) => Update(ray, grid, angleStep, 0f);

    public bool Update(in EditorRay ray, float grid, float angleStep, float scaleStep)
    {
        if (_placements.Any(p => _document.IndexOf(p) < 0)) return false;
        var after = new PlacementFields[_placements.Length];
        switch (Mode)
        {
            case GizmoMode.Move:
            {
                if (TranslateGizmo.Drag(Handle, _start, ray, Origin, Axes, grid) is not { } delta) return false;
                for (int i = 0; i < after.Length; i++) after[i] = _from[i] with { At = _from[i].At + delta };
                break;
            }
            case GizmoMode.Rotate:
            {
                if (RotateGizmo.Drag(Handle, _start, ray, Origin, Axes, angleStep) is not { } angle) return false;
                var turn = Quaternion.CreateFromAxisAngle(Vector3.Normalize(RotateGizmo.AxisOf(Handle, Axes)), angle);
                for (int i = 0; i < after.Length; i++) after[i] = PlacementGroup.Rotated(_from[i], _origins[i], Origin, turn);
                break;
            }
            default:
            {
                if (ScaleGizmo.Drag(Handle, _start, ray, Origin, Axes, scaleStep) is not { } factors) return false;
                for (int i = 0; i < after.Length; i++) after[i] = PlacementGroup.Scaled(_from[i], _origins[i], Origin, Axes, factors);
                break;
            }
        }
        return PlacementGroup.Command(_document, _placements, after) is { } command && _document.Execute(command);
    }

    // The mouse came up: the next edit is a step of its own.
    public void End() => _document.History.EndMerge();
}
