#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;

namespace Sage.Editing;

// The inspector's console commands (issue #223): what the panel does, typed, so a test or a script sets
// and reverts an override the way a person does (phase 10a decision 6).
//
//   ed_set <name> <component.field> <value...>   an override (or `at`/`yaw`/`name`/`relativeTo`)
//   ed_revert <name> <component.field>           back to the prefab's value
//   ed_inspect <name>                            every field, its value, overridden or not, and who set it
//
// `<name>` is a placement of the open document, by name or id; quote one with spaces ("cart lamp"). The
// value is the rest of the line, read by the field's shape (InspectorValue): `ed_set gate at 1 2 3`.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class InspectorCommands
{
    public static void Register(CVarRegistry cvars, Func<EditDocument?> document)
    {
        cvars.RegisterCommand("ed_set", CVarFlags.DevOnly,
            "ed_set <placement> <component.field> <value>: override one field for this placement (or set its at, yaw, name, relativeTo).", a =>
        {
            if (a.Count < 3) { Log.Warn(LogCat.Console, "ed_set <placement> <component.field> <value>"); return; }
            if (!Model(document, a[0], out var model)) return;
            string value = string.Join(' ', a.Args.Skip(2));
            if (model.TrySet(a[1], value, out string error)) Log.Info(LogCat.Console, Done(model));
            else Log.Warn(LogCat.Console, $"ed_set: {error}");
        });

        cvars.RegisterCommand("ed_revert", CVarFlags.DevOnly,
            "ed_revert <placement> <component.field>: take the override away, back to the prefab's value.", a =>
        {
            if (a.Count < 2) { Log.Warn(LogCat.Console, "ed_revert <placement> <component.field>"); return; }
            if (!Model(document, a[0], out var model)) return;
            if (model.TryRevert(a[1], out string error)) Log.Info(LogCat.Console, Done(model));
            else Log.Warn(LogCat.Console, $"ed_revert: {error}");
        });

        cvars.RegisterCommand("ed_inspect", CVarFlags.DevOnly,
            "ed_inspect <placement>: its fields, which it overrides (*) and where each value came from.", a =>
        {
            if (a.Count < 1) { Log.Warn(LogCat.Console, "ed_inspect <placement>"); return; }
            if (Model(document, a[0], out var model)) Log.Info(LogCat.Console, Describe(model));
        });
    }

    // The model as ed_inspect prints it.
    public static string Describe(InspectorModel model)
    {
        var text = new StringBuilder(World.Describe(model.Entity));
        if (model.Placement is { } p)
            text.Append($"\n  placement: at {InspectorValue.Format(p.At)}, yaw {InspectorValue.Format(p.Yaw)}, name '{p.Name}'"
                        + (p.RelativeTo is { } frame ? $", relativeTo {frame}" : ""));
        foreach (var group in model.Groups)
        {
            text.Append("\n  ").Append(group);
            if (group.Note != null) text.Append("  — ").Append(group.Note);
            foreach (var row in group.Rows) text.Append("\n    ").Append(row);
        }
        return text.ToString();
    }

    // A typed command is a gesture of its own: the next one is a new edit, not merged into this one.
    private static string Done(InspectorModel model)
    {
        var log = model.Document!.History;
        log.EndMerge();
        return log.CanUndo ? log.Entries[log.Position - 1].Description : "done";
    }

    private static bool Model(Func<EditDocument?> document, string name, [NotNullWhen(true)] out InspectorModel? model)
    {
        model = null;
        var doc = document();
        if (doc is not { IsOpen: true })
        {
            Log.Warn(LogCat.Console, "no document open (doc_open)");
            return false;
        }
        if (doc.Find(name) is not { } placement)
        {
            Log.Warn(LogCat.Console, $"'{name}' is not a placement of {doc.Id}");
            return false;
        }
        var entity = doc.EntityOf(placement);
        if (entity.IsNull || !doc.World.IsAlive(entity))
        {
            Log.Warn(LogCat.Console, $"'{name}' placed nothing (its prefab did not spawn)");
            return false;
        }
        model = InspectorModel.Of(doc, doc.World, entity);
        return true;
    }
}
