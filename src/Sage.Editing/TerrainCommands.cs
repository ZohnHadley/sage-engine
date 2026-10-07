#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

namespace Sage.Editing;

// The terrain tools' console commands (issue #372): every brush the panel has is a command too, so a
// script or a test sculpts the way a person does.
//
//   ed_terrain                                          what is being sculpted, and the brush
//   ed_sculpt <raise|lower|smooth|flatten> <x> <z> [radius] [strength] [height]   one dab, one undo step
//   ed_paint <layer> <x> <z> [radius] [strength]         one dab of a layer
//   ed_water <x> <z> <height|off>                       the water over the sector at x, z
//   ed_terrain_save, ed_terrain_undo, ed_terrain_redo, ed_terrain_history
//
// Positions are absolute metres (a sector is 1024 m), as `warp` takes them.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class TerrainCommands
{
    public static void Register(CVarRegistry cvars, Func<TerrainDocument?> document)
    {
        cvars.RegisterCommand("ed_terrain", CVarFlags.DevOnly, "ed_terrain: the terrain being sculpted, its sculpted sectors and the brush.", _ =>
        {
            if (document() is { } doc) Log.Info(LogCat.Console, doc.Describe());
            else Log.Warn(LogCat.Console, "ed_terrain: no world yet");
        });

        cvars.RegisterCommand("ed_sculpt", CVarFlags.DevOnly,
            "ed_sculpt <raise|lower|smooth|flatten> <x> <z> [radius] [strength] [height]: one dab of the brush at absolute metres (one undo step).", a =>
        {
            if (a.Count < 3 || !Enum.TryParse<TerrainTool>(a[0], ignoreCase: true, out var tool) || tool == TerrainTool.Paint)
            {
                Log.Warn(LogCat.Console, "ed_sculpt <raise|lower|smooth|flatten> <x> <z> [radius m] [strength] [flatten height m]");
                return;
            }
            if (!Ready(document, out var doc) || !Point(a[1], a[2], out float x, out float z)) return;
            doc.Brush.Tool = tool;
            if (a.Count > 3 && Number(a[3]) is { } radius) doc.Brush.Radius = radius;
            if (a.Count > 4 && Number(a[4]) is { } strength) doc.Brush.Strength = strength;
            doc.Brush.FlattenHeight = a.Count > 5 ? Number(a[5]) : null;
            Dab(doc, x, z);
        });

        cvars.RegisterCommand("ed_paint", CVarFlags.DevOnly,
            "ed_paint <layer> <x> <z> [radius] [strength]: paint a terrain layer (0-3) at absolute metres (one undo step).", a =>
        {
            if (a.Count < 3 || !int.TryParse(a[0], out int layer) || layer < 0 || layer > 3)
            {
                Log.Warn(LogCat.Console, "ed_paint <layer 0-3> <x> <z> [radius m] [strength 0-1]");
                return;
            }
            if (!Ready(document, out var doc) || !Point(a[1], a[2], out float x, out float z)) return;
            doc.Brush.Tool = TerrainTool.Paint;
            doc.Brush.Layer = layer;
            if (a.Count > 3 && Number(a[3]) is { } radius) doc.Brush.Radius = radius;
            if (a.Count > 4 && Number(a[4]) is { } strength) doc.Brush.Strength = strength;
            Dab(doc, x, z);
        });

        cvars.RegisterCommand("ed_water", CVarFlags.DevOnly,
            "ed_water <x> <z> <height|off>: the water surface over the sector at absolute metres x, z (one undo step).", a =>
        {
            if (a.Count < 3) { Log.Warn(LogCat.Console, "ed_water <x> <z> <height m|off>"); return; }
            if (!Ready(document, out var doc) || !Point(a[0], a[1], out float x, out float z)) return;
            float? level = string.Equals(a[2], "off", StringComparison.OrdinalIgnoreCase) ? null : Number(a[2]);
            if (level == null && !string.Equals(a[2], "off", StringComparison.OrdinalIgnoreCase)) { Log.Warn(LogCat.Console, $"ed_water: '{a[2]}' is not a height"); return; }
            var sector = Terrain.SectorOf(x, z);
            Log.Info(LogCat.Console, doc.SetWater(sector, level) ? $"{doc.History.Entries[doc.History.Position - 1].Description}" : "ed_water: nothing changed");
        });

        cvars.RegisterCommand("ed_terrain_save", CVarFlags.DevOnly, "ed_terrain_save: write the terrain's sculpt to its file.",
            _ => { if (Ready(document, out var doc)) doc.Save(); });

        cvars.RegisterCommand("ed_terrain_undo", CVarFlags.DevOnly, "ed_terrain_undo: undo the last brush stroke.", _ =>
        {
            if (Ready(document, out var doc)) Log.Info(LogCat.Console, doc.Undo() ? $"undone; {doc.Title}" : "nothing to undo");
        });

        cvars.RegisterCommand("ed_terrain_redo", CVarFlags.DevOnly, "ed_terrain_redo: redo the stroke last undone.", _ =>
        {
            if (Ready(document, out var doc)) Log.Info(LogCat.Console, doc.Redo() ? $"redone; {doc.Title}" : "nothing to redo");
        });

        cvars.RegisterCommand("ed_terrain_history", CVarFlags.DevOnly, "ed_terrain_history: the brush strokes, oldest first.", _ =>
        {
            if (!Ready(document, out var doc)) return;
            var history = doc.History;
            if (history.Entries.Count == 0) { Log.Info(LogCat.Console, "no strokes yet"); return; }
            for (int i = 0; i < history.Entries.Count; i++)
                Log.Info(LogCat.Console, $"{(i < history.Position ? " " : "~")} {i + 1}. {history.Entries[i].Description}");
        });
    }

    private static void Dab(TerrainDocument doc, float x, float z)
    {
        var terrain = doc.Terrain!;
        var at = terrain.Origin.ToOrigin(new Vector3(x, 0, z));
        var stroke = doc.Stroke(at);
        Log.Info(LogCat.Console, stroke != null ? $"{stroke.Description}; ground {terrain.HeightAt(at.X, at.Z):0.##} m" : "nothing changed");
    }

    private static bool Ready(Func<TerrainDocument?> document, out TerrainDocument doc)
    {
        doc = document()!;
        if (doc is { CanEdit: true }) return true;
        Log.Warn(LogCat.Console, doc == null ? "no world yet" : "no terrain to sculpt: the world's ground has no generator (a scene's `terrain`)");
        return false;
    }

    private static bool Point(string x, string z, out float px, out float pz)
    {
        px = pz = 0;
        if (Number(x) is { } a && Number(z) is { } b) { px = a; pz = b; return true; }
        Log.Warn(LogCat.Console, $"'{x} {z}' is not a position (absolute metres)");
        return false;
    }

    private static float? Number(string text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : null;
}
