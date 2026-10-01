#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace Sage.Editing;

// Which gizmo the selection shows: move (axes and planes) or turn about Y.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public enum GizmoMode { Move, Rotate }

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
    public const float DefaultGrid = 0.5f, DefaultAngleStep = 15f;

    // Snapping, as `ed_snap`, `ed_grid` and `ed_angle` set it: commands rather than cvars, because a
    // tool registered after config.cfg was read could not have cvars (CVarRegistry.CVarSeal), and these
    // are the editor's state, not settings a game keeps.
    public bool Snap { get; set; } = true;
    public float Grid { get; set; } = DefaultGrid;               // metres
    public float AngleStep { get; set; } = DefaultAngleStep;     // degrees
    public GizmoMode Mode { get; set; } = GizmoMode.Move;

    // What a drag snaps to now: 0 when snapping is off.
    public float GridStep => Snap && Grid > 0f ? Grid : 0f;
    public float AngleStepDegrees => Snap && AngleStep > 0f ? AngleStep : 0f;

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
    public static string UniqueName(EditDocument document, string wanted)
    {
        bool Taken(string name) => document.Placements.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (!Taken(wanted)) return wanted;
        string stem = wanted;
        int underscore = wanted.LastIndexOf('_');
        if (underscore > 0 && int.TryParse(wanted.AsSpan(underscore + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            stem = wanted[..underscore];
        for (int n = 2; ; n++)
            if (!Taken($"{stem}_{n}")) return $"{stem}_{n}";
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

        cvars.RegisterCommand("ed_delete", CVarFlags.DevOnly, "ed_delete [name]: take a placement (else the selection) out of the document.", a =>
        {
            if (!Target(selection, a, 0, "ed_delete [name]", out var s, out var placement, out _)) return;
            Delete(s.Document, placement);
            Log.Info(LogCat.Console, $"deleted {AddPlacement.Label(placement)}");
        });

        cvars.RegisterCommand("ed_duplicate", CVarFlags.DevOnly,
            "ed_duplicate [name]: copy a placement (else the selection) with a name of its own, and select the copy.", a =>
        {
            if (!Target(selection, a, 0, "ed_duplicate [name]", out var s, out var placement, out _)) return;
            if (Duplicate(s, placement) is { } copy) Log.Info(LogCat.Console, $"duplicated {AddPlacement.Label(placement)} as {AddPlacement.Label(copy)}");
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

        cvars.RegisterCommand("ed_gizmo", CVarFlags.DevOnly, "ed_gizmo [move|rotate]: which gizmo the selection shows.", a =>
        {
            if (a.Count > 0)
            {
                if (a[0].StartsWith("rot", StringComparison.OrdinalIgnoreCase)) tools.Mode = GizmoMode.Rotate;
                else if (a[0].StartsWith("mov", StringComparison.OrdinalIgnoreCase) || a[0].StartsWith("tr", StringComparison.OrdinalIgnoreCase)) tools.Mode = GizmoMode.Move;
                else { Log.Warn(LogCat.Console, "ed_gizmo [move|rotate]"); return; }
            }
            Log.Info(LogCat.Console, $"gizmo: {tools.Mode.ToString().ToLowerInvariant()}");
        });

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
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class GizmoDrag
{
    private readonly EditDocument _document;
    private readonly EditorRay _start;
    private readonly PlacementFields _from;

    private GizmoDrag(EditDocument document, Placement placement, GizmoMode mode, GizmoHandle handle, EditorRay start)
    {
        _document = document;
        Placement = placement;
        Mode = mode;
        Handle = handle;
        _start = start;
        _from = PlacementFields.Of(placement);
        Origin = ViewportTools.OriginOf(document, placement);
    }

    public Placement Placement { get; }
    public GizmoMode Mode { get; }
    public GizmoHandle Handle { get; }      // None for a turn: the ring is the one handle
    public Vector3 Origin { get; }          // where the placement stood when the drag began (origin space)

    // A drag of `placement`'s gizmo from `startRay`; null when it is not the open document's.
    public static GizmoDrag? Begin(EditDocument document, Placement placement, GizmoMode mode, GizmoHandle handle, in EditorRay startRay)
    {
        if (!document.IsOpen || document.IndexOf(placement) < 0) return null;
        if (mode == GizmoMode.Move && handle == GizmoHandle.None) return null;
        document.History.EndMerge();   // whatever came before is its own step
        return new GizmoDrag(document, placement, mode, handle, startRay);
    }

    // The pointer is on `ray` now: the placement goes where that puts it, snapped by `grid` metres or
    // `angleStep` degrees (0 = off). False when nothing changed (no answer for this ray, or the same place).
    public bool Update(in EditorRay ray, float grid = 0f, float angleStep = 0f)
    {
        if (_document.IndexOf(Placement) < 0) return false;
        var fields = _from;
        if (Mode == GizmoMode.Move)
        {
            if (TranslateGizmo.Drag(Handle, _start, ray, Origin, grid) is not { } delta) return false;
            fields = fields with { At = _from.At + delta };
        }
        else
        {
            if (RotateGizmo.Drag(_start, ray, Origin, angleStep) is not { } angle) return false;
            fields = fields with { Yaw = ViewportTools.WrapDegrees(_from.Yaw + angle * 180f / MathF.PI) };
        }
        if (fields == PlacementFields.Of(Placement)) return false;
        return _document.Execute(new SetPlacement(_document, Placement, fields));
    }

    // The mouse came up: the next edit is a step of its own.
    public void End() => _document.History.EndMerge();
}
