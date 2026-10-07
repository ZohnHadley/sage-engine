#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;

namespace Sage.Editing;

// The animation preview's console commands (issue #362): every button of the editor's Animation panel is
// one, so a script, a smoke run or a test presses it the way a person does (phase 10a decision 6).
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class AnimationPreviewCommands
{
    public static void Register(CVarRegistry cvars, Func<AnimationPreview?> preview)
    {
        ArgumentNullException.ThrowIfNull(cvars);
        ArgumentNullException.ThrowIfNull(preview);

        cvars.RegisterCommand("anim_preview", CVarFlags.DevOnly,
            "anim_preview [graph | model.glb] [model.glb]: open an anim_graph (on the model a prefab plays it on, or the one named) or a model's clips "
            + "in the animation preview, and say what it shows; no argument: what is open, or the graphs there are.", a =>
        {
            if (preview() is not { } p) { Log.Warn(LogCat.Console, "anim_preview: no preview here"); return; }
            if (a.Count == 0)
            {
                if (p.IsOpen) Log.Info(LogCat.Console, p.Describe());
                else
                {
                    var subjects = AnimationPreview.Subjects(p.Engine.Records);
                    Log.Info(LogCat.Console, subjects.Count == 0 ? "anim_preview: no anim_graph records"
                        : "anim_preview <graph>: " + string.Join(", ", subjects.Select(s => s.Model.IsEmpty ? s.Graph.ToString() : $"{s.Graph} ({s.Model})")));
                }
                return;
            }
            bool model = a[0].EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || a[0].EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);
            var graph = model ? default : p.Engine.Records.Resolve("anim_graph", a[0]);   // says so when there is none
            if (!model && graph.IsEmpty) return;
            bool opened = model ? p.OpenModel(AssetPath.Intern(a[0])) : p.Open(graph, a.Count > 1 ? AssetPath.Intern(a[1]) : default);
            if (!opened) { Log.Warn(LogCat.Console, $"anim_preview: {p.Error}"); return; }
            Log.Info(LogCat.Console, p.Describe());
        });

        cvars.RegisterCommand("anim_preview_clip", CVarFlags.DevOnly,
            "anim_preview_clip <clip> [seconds]: show one clip of the previewed model, scrubbed to that time; with no clip, back to the graph.", a =>
        {
            if (!Open(preview, out var p)) return;
            if (a.Count == 0)
            {
                if (!p.ShowGraph()) Log.Warn(LogCat.Console, "anim_preview_clip: no graph open (anim_preview <graph>)");
                else Log.Info(LogCat.Console, $"anim_preview: back to the graph, t={p.Time.ToString("F2", CultureInfo.InvariantCulture)}s");
                return;
            }
            float time = a.Count > 1 && float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float t) ? t : 0f;
            if (!p.ShowClip(a[0], time))
            {
                Log.Warn(LogCat.Console, $"anim_preview_clip: {p.Model} has no clip '{a[0]}' ({string.Join(", ", p.Clips)})");
                return;
            }
            var joints = p.Joints;
            Log.Info(LogCat.Console, string.Create(CultureInfo.InvariantCulture,
                $"anim_preview: clip {p.Clip} at {p.ClipTime:F2}/{p.ClipDuration:F2}s, {joints.Count} joints, {p.Sockets.Count} socket(s)")
                + string.Concat(p.ClipMarkers.Select(m => string.Create(CultureInfo.InvariantCulture, $" | {m.Name}@{m.Time:F2}s"))));
        });

        cvars.RegisterCommand("anim_preview_param", CVarFlags.DevOnly,
            "anim_preview_param <param> [value]: set a param of the previewed graph (a trigger needs no value).", a =>
        {
            if (!Open(preview, out var p)) return;
            if (a.Count == 0) { Log.Warn(LogCat.Console, "anim_preview_param <param> [value]"); return; }
            float value = a.Count > 1 && float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 1f;
            if (!p.SetParam(a[0], value))
                Log.Warn(LogCat.Console, $"anim_preview_param: {p.Graph} has no param '{a[0]}' ({string.Join(", ", p.Params.Select(x => x.Name))})");
        });

        cvars.RegisterCommand("anim_preview_state", CVarFlags.DevOnly,
            "anim_preview_state <state> [layer]: put a layer of the previewed graph (the base when none is named) in a state.", a =>
        {
            if (!Open(preview, out var p)) return;
            if (a.Count == 0) { Log.Warn(LogCat.Console, "anim_preview_state <state> [layer]"); return; }
            if (!p.PlayState(a[0], a.Count > 1 ? a[1] : null))
                Log.Warn(LogCat.Console, $"anim_preview_state: no state '{a[0]}' in {(a.Count > 1 ? $"layer '{a[1]}'" : "the base layer")} of {p.Graph}");
            else Log.Info(LogCat.Console, "anim_preview:" + p.DescribeLayers());
        });

        cvars.RegisterCommand("anim_preview_step", CVarFlags.DevOnly,
            "anim_preview_step [seconds]: run the previewed graph on (a tick by default), and say where its layers are and what events it raised.", a =>
        {
            if (!Open(preview, out var p)) return;
            if (!p.ShowGraph()) { Log.Warn(LogCat.Console, "anim_preview_step: no graph open (anim_preview <graph>)"); return; }
            float seconds = a.Count > 0 && float.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float s) ? s : AnimationPreview.TickSeconds;
            float from = p.Time;
            p.Step(seconds);
            var raised = p.Events.Where(e => e.Time > from).Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Name}@{e.Time:F2}s"));
            Log.Info(LogCat.Console, string.Create(CultureInfo.InvariantCulture, $"anim_preview: t={p.Time:F2}s, events: ")
                                     + (raised.Any() ? string.Join(" ", raised) : "none") + p.DescribeLayers());
        });

        cvars.RegisterCommand("anim_preview_close", CVarFlags.DevOnly, "anim_preview_close: close the animation preview.",
            _ => preview()?.Close());
    }

    private static bool Open(Func<AnimationPreview?> preview, [NotNullWhen(true)] out AnimationPreview? p)
    {
        p = preview();
        if (p is { IsOpen: true }) return true;
        Log.Warn(LogCat.Console, "anim_preview: nothing open (anim_preview <graph>)");
        return false;
    }
}
