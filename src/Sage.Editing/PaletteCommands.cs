#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace Sage.Editing;

// The palette's console commands (issue #222): `ed_palette` lists what can be placed, `ed_place` places it.
// The viewport click is Sage.Editor's; this is the same placing, typed.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class PaletteCommands
{
    public static void Register(CVarRegistry cvars, Func<EditDocument?> document)
    {
        cvars.RegisterCommand("ed_palette", CVarFlags.None, "ed_palette [search]: the prefabs that can be placed, by namespace.", a =>
        {
            if (document() is not { } doc) { Log.Warn(LogCat.Console, "no world yet: the palette works once a world exists"); return; }
            var palette = new PrefabPalette(doc.Engine.Records) { Search = a.Rest };
            var groups = palette.Groups();
            if (groups.Count == 0) { Log.Info(LogCat.Console, "no prefab matches"); return; }
            foreach (var group in groups)
                Log.Info(LogCat.Console, $"{group.Namespace}: {string.Join(", ", group.Prefabs.Select(p => p.Name))}");
        });

        cvars.RegisterCommand("ed_place", CVarFlags.DevOnly,
            "ed_place <prefab> [x y z] [yaw] [name]: place a prefab in the open document (at the origin with no position); undoable.", a =>
        {
            if (document() is not { } doc) { Log.Warn(LogCat.Console, "no world yet: ed_place works once a world exists"); return; }
            if (!doc.IsOpen) { Log.Warn(LogCat.Console, "ed_place: no document open (doc_new or doc_open)"); return; }
            if (a.Count == 0) { Log.Warn(LogCat.Console, "ed_place <prefab> [x y z] [yaw] [name]"); return; }
            var prefab = doc.Engine.Records.Resolve("prefab", a[0]);
            if (prefab.IsEmpty) return;

            int next = 1;
            Vector3 at = Vector3.Zero;
            if (a.Count >= 4 && Number(a[1], out float x) && Number(a[2], out float y) && Number(a[3], out float z))
            {
                at = new Vector3(x, y, z);
                next = 4;
            }
            float yaw = 0f;
            if (next < a.Count && Number(a[next], out float given)) { yaw = given; next++; }
            string name = string.Join(' ', Enumerable.Range(next, a.Count - next).Select(i => a[i]));

            if (Placing.Place(doc, prefab, at, yaw, name) is { } placed)
                Log.Info(LogCat.Console, $"placed {placed.Name} ({prefab}) at {at.X:0.##} {at.Y:0.##} {at.Z:0.##}");
        });
    }

    private static bool Number(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
