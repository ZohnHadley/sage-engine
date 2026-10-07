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
//   ed_add <name> <component.list> [value]       an element at the end of a list (or `<key> [value]`, a map's)
//   ed_remove <name> <component.list[i]>         an element (or an entry) out
//   ed_reorder <name> <component.list[i]> <to>   an element moved
//   ed_add_component / ed_remove_component <name> <component>   one the prefab does not name, for this one
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

        // Lists, maps and components the prefab does not name (#368).
        cvars.RegisterCommand("ed_add", CVarFlags.DevOnly,
            "ed_add <placement> <component.list> [value]: add an element at the end of a list (its default, or the value); for a map, <key> [value].", a =>
        {
            if (a.Count < 2) { Log.Warn(LogCat.Console, "ed_add <placement> <component.list> [value]"); return; }
            if (!Model(document, a[0], out var model)) return;
            if (model.TryAddItem(a[1], string.Join(' ', a.Args.Skip(2)), out string error)) Log.Info(LogCat.Console, Done(model));
            else Log.Warn(LogCat.Console, $"ed_add: {error}");
        });

        cvars.RegisterCommand("ed_remove", CVarFlags.DevOnly,
            "ed_remove <placement> <component.list[i]>: take an element out of a list (or an entry out of a map).", a =>
        {
            if (a.Count < 2) { Log.Warn(LogCat.Console, "ed_remove <placement> <component.list[i]>"); return; }
            if (!Model(document, a[0], out var model)) return;
            if (model.TryRemoveItem(a[1], out string error)) Log.Info(LogCat.Console, Done(model));
            else Log.Warn(LogCat.Console, $"ed_remove: {error}");
        });

        cvars.RegisterCommand("ed_reorder", CVarFlags.DevOnly,
            "ed_reorder <placement> <component.list[i]> <to>: move an element of a list to another index.", a =>
        {
            if (a.Count < 3 || !int.TryParse(a[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int to))
            {
                Log.Warn(LogCat.Console, "ed_reorder <placement> <component.list[i]> <to>");
                return;
            }
            if (!Model(document, a[0], out var model)) return;
            if (model.TryMoveItem(a[1], to, out string error)) Log.Info(LogCat.Console, Done(model));
            else Log.Warn(LogCat.Console, $"ed_reorder: {error}");
        });

        cvars.RegisterCommand("ed_add_component", CVarFlags.DevOnly,
            "ed_add_component <placement> <component>: add a component the prefab does not name to this placement alone.", a =>
        {
            if (a.Count < 2) { Log.Warn(LogCat.Console, "ed_add_component <placement> <component>"); return; }
            if (!Model(document, a[0], out var model)) return;
            if (model.AddComponent(a[1], out string error)) Log.Info(LogCat.Console, Done(model));
            else Log.Warn(LogCat.Console, $"ed_add_component: {error}");
        });

        cvars.RegisterCommand("ed_remove_component", CVarFlags.DevOnly,
            "ed_remove_component <placement> <component>: take away a component this placement added.", a =>
        {
            if (a.Count < 2) { Log.Warn(LogCat.Console, "ed_remove_component <placement> <component>"); return; }
            if (!Model(document, a[0], out var model)) return;
            if (model.Group(a[1]) is not { } group) { Log.Warn(LogCat.Console, $"ed_remove_component: '{a[1]}' has no {a[1]}"); return; }
            if (model.RemoveComponent(group, out string error)) Log.Info(LogCat.Console, Done(model));
            else Log.Warn(LogCat.Console, $"ed_remove_component: {error}");
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
            foreach (var row in group.Rows) Append(text, row, "\n    ");
        }
        return text.ToString();
    }

    // A row and what is inside it (a list's elements, an object's fields), indented under it.
    private static void Append(StringBuilder text, InspectorRow row, string indent)
    {
        text.Append(indent).Append(row);
        foreach (var child in row.Children) Append(text, child, indent + "  ");
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
