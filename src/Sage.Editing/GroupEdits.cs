#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;

namespace Sage.Editing;

// Several placements edited as one (issue #367): a selection of ten crates moved, turned or scaled
// together, deleted or duplicated together — and taken back by one undo.

// Which way the gizmo's axes point: the world's, or the selected placement's own (its rotation). The
// scale gizmo is always in local space, since a placement's scale is along its own axes.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public enum GizmoSpace { World, Local }

// Several placements' fields set at once: one undo step. A drag of a group sends one of these a frame,
// and those for the same placements merge, as SetPlacement's do.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class SetPlacements : IEditorCommand
{
    private readonly EditDocument _document;
    private readonly Placement[] _placements;
    private readonly PlacementFields[] _before;
    private PlacementFields[] _after;

    public SetPlacements(EditDocument document, IReadOnlyList<Placement> placements, IReadOnlyList<PlacementFields> fields)
    {
        if (placements.Count != fields.Count) throw new ArgumentException("one set of fields per placement", nameof(fields));
        _document = document;
        _placements = placements.ToArray();
        _before = _placements.Select(PlacementFields.Of).ToArray();
        _after = fields.ToArray();
    }

    public IReadOnlyList<Placement> Placements => _placements;
    public IReadOnlyList<PlacementFields> Before => _before;
    public IReadOnlyList<PlacementFields> After => _after;

    public string Description
    {
        get
        {
            bool moved = false, turned = false, scaled = false;
            for (int i = 0; i < _before.Length; i++)
            {
                moved |= _before[i].At != _after[i].At;
                turned |= _before[i].Yaw != _after[i].Yaw || _before[i].Pitch != _after[i].Pitch || _before[i].Roll != _after[i].Roll;
                scaled |= _before[i].Scale != _after[i].Scale;
            }
            string verb = scaled && !turned ? "Scale" : turned && !scaled ? "Rotate" : moved && !turned && !scaled ? "Move" : "Set";
            return $"{verb} {_placements.Length} placements";
        }
    }

    public void Do() => Set(_after);
    public void Undo() => Set(_before);

    private void Set(PlacementFields[] fields)
    {
        for (int i = 0; i < _placements.Length; i++) fields[i].ApplyTo(_placements[i]);
        foreach (var placement in _placements) _document.Respawn(placement);
    }

    public bool TryMerge(IEditorCommand next)
    {
        if (next is not SetPlacements later || later._placements.Length != _placements.Length) return false;
        for (int i = 0; i < _placements.Length; i++)
            if (!ReferenceEquals(later._placements[i], _placements[i])) return false;
        _after = later._after;
        return true;
    }
}

// Commands done as one: deleting or duplicating a selection. Undone in reverse.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class CommandGroup : IEditorCommand
{
    private readonly IEditorCommand[] _commands;

    public CommandGroup(string description, IEnumerable<IEditorCommand> commands)
    {
        Description = description;
        _commands = commands.ToArray();
    }

    public string Description { get; }
    public IReadOnlyList<IEditorCommand> Commands => _commands;

    public void Do()
    {
        foreach (var command in _commands) command.Do();
    }

    public void Undo()
    {
        for (int i = _commands.Length - 1; i >= 0; i--) _commands[i].Undo();
    }
}

// The scale gizmo's maths (issue #367): an axis handle along each of the placement's own axes, with a
// box at its end, and a centre handle (`All`) for every axis at once. A drag gives the factors the
// placement's scale is multiplied by: how much further from the centre the pointer is than where it
// started, along the axis (or in the plane facing the camera, for the centre).
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class ScaleGizmo
{
    internal const float CentreGrabRadius = 0.15f;   // of size
    public const float MinFactor = 0.01f;            // a drag past the centre does not flatten or invert it

    // The handle under `ray`: the centre first, else an axis, else None.
    public static GizmoHandle HitTest(in EditorRay ray, Vector3 origin, float size, Quaternion orientation)
    {
        if (size <= 0f) return GizmoHandle.None;
        if (EditorPicking.RaySphere(ray, origin, CentreGrabRadius * size) is not null) return GizmoHandle.All;
        return TranslateGizmo.AxisHit(TranslateGizmo.Local(ray, origin, orientation), Vector3.Zero, size);
    }

    // The factors a drag of `handle` from `startRay` to `currentRay` scales by, along the gizmo's own
    // axes: 1 on the axes it did not move. `step` (0 = off) snaps the factor. Null when the drag has no
    // answer (a ray along the axis, or one that started on the centre itself).
    public static Vector3? Drag(GizmoHandle handle, in EditorRay startRay, in EditorRay currentRay, Vector3 origin, Quaternion orientation, float step = 0f)
    {
        float factor;
        if (handle == GizmoHandle.All)
        {
            // In the plane through the centre facing the pointer as the drag began.
            Vector3 normal = -startRay.Direction;
            if (startRay.IntersectPlane(origin, normal) is not { } t0 || currentRay.IntersectPlane(origin, normal) is not { } t1) return null;
            float from = Vector3.Distance(startRay.At(t0), origin);
            if (from < 1e-5f) return null;
            factor = Vector3.Distance(currentRay.At(t1), origin) / from;
        }
        else if (handle is GizmoHandle.X or GizmoHandle.Y or GizmoHandle.Z)
        {
            Vector3 axis = TranslateGizmo.AxisOf(handle);
            var a = TranslateGizmo.Local(startRay, origin, orientation).ClosestToLine(Vector3.Zero, axis);
            var b = TranslateGizmo.Local(currentRay, origin, orientation).ClosestToLine(Vector3.Zero, axis);
            if (a is not { } start || b is not { } now || MathF.Abs(start.OnLine) < 1e-5f) return null;
            factor = now.OnLine / start.OnLine;
        }
        else return null;

        factor = Snapped(factor, step);
        return handle == GizmoHandle.All ? new Vector3(factor)
            : handle == GizmoHandle.X ? new Vector3(factor, 1f, 1f)
            : handle == GizmoHandle.Y ? new Vector3(1f, factor, 1f)
            : new Vector3(1f, 1f, factor);
    }

    // `factor` to the nearest `step` (0 = off), never below MinFactor (or one step).
    public static float Snapped(float factor, float step)
    {
        if (step > 0f) factor = MathF.Max(MathF.Round(factor / step, MidpointRounding.AwayFromZero) * step, step);
        return MathF.Max(factor, MinFactor);
    }
}

// What moving, turning and scaling a group does to each placement in it. The group turns and scales
// about a pivot (the gizmo: the selection's last placement), so each one's position swings round it as
// well as its own rotation or scale changing.
internal static class PlacementGroup
{
    // `fields` (whose placement stood at `origin`, origin space) turned by `turn` (origin space) about `pivot`.
    public static PlacementFields Rotated(PlacementFields fields, Vector3 origin, Vector3 pivot, Quaternion turn)
    {
        Vector3 swung = pivot + Vector3.Transform(origin - pivot, turn);
        fields = fields with { At = fields.At + (swung - origin) };

        // A placement with only a yaw, turned about the vertical, keeps only a yaw: added exactly, so a
        // quarter turn of a yaw of 30 is 120, not 119.99999.
        Vector3 axis = new(turn.X, turn.Y, turn.Z);
        if (fields.Pitch == 0f && fields.Roll == 0f && axis.LengthSquared() > 1e-12f
            && MathF.Abs(axis.X) < 1e-6f && MathF.Abs(axis.Z) < 1e-6f)
        {
            float angle = 2f * MathF.Atan2(axis.Y, turn.W);
            return fields with { Yaw = ViewportTools.WrapDegrees(fields.Yaw + angle * 180f / MathF.PI) + 0f };
        }
        return fields.WithRotation(Quaternion.Concatenate(fields.Rotation, turn));
    }

    // `fields` scaled by `factors` along the axes `axes` turns X, Y and Z to, about `pivot`: its position
    // along those axes, and its own scale by the same factors.
    public static PlacementFields Scaled(PlacementFields fields, Vector3 origin, Vector3 pivot, Quaternion axes, Vector3 factors)
    {
        var inverse = Quaternion.Inverse(Quaternion.Normalize(axes));
        Vector3 local = Vector3.Transform(origin - pivot, inverse) * factors;
        Vector3 moved = pivot + Vector3.Transform(local, axes);
        return fields with { At = fields.At + (moved - origin), Scale = fields.Scale * factors };
    }

    // The placements' fields after `change`, as one command: a SetPlacement for one (so a single drag
    // reads and merges as it always has), a SetPlacements for more. Null when nothing would change.
    public static IEditorCommand? Command(EditDocument document, IReadOnlyList<Placement> placements, IReadOnlyList<PlacementFields> after)
    {
        bool changed = false;
        for (int i = 0; i < placements.Count && !changed; i++) changed = after[i] != PlacementFields.Of(placements[i]);
        if (!changed) return null;
        return placements.Count == 1 ? new SetPlacement(document, placements[0], after[0]) : new SetPlacements(document, placements, after);
    }
}
