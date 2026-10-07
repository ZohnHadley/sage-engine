#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace Sage.Editing;

// The asset browser's console commands (issue #366): every button of the Assets panel is one of these
// (phase 10a decision 6), so a script and a test press them too.
//
//   ed_assets [kind|all] [mount=<name>] [search...]   list
//   ed_asset_refs <path>                              where it is named
//   ed_asset_pick <path> <field>                      into the open record's field (params.Albedo)
//   ed_asset_pick <path> <placement> <comp.field>     into a placement's field (mesh_renderer.mesh)
//   ed_asset_place <path> [x y z] [yaw]               into the open document
//   ed_asset_rename <path> <new path>                 move it and rewrite what names it
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10)
public static class AssetCommands
{
    public static void Register(CVarRegistry cvars, Func<AssetBrowser?> browser, Func<EditDocument?> document, Func<RecordEditor?> records)
    {
        cvars.RegisterCommand("ed_assets", CVarFlags.None,
            "ed_assets [kind|all] [mount=<name>] [search]: the assets in the VFS (texture, mesh, sound, map, font, shader), by path, with the mount that provides each.", a =>
        {
            if (browser() is not { } b) { Log.Warn(LogCat.Console, "no asset browser yet"); return; }
            int next = 0;
            b.Kind = "";
            b.Mount = "";
            if (a.Count > 0 && (AssetKinds.All.Contains(a[0], StringComparer.OrdinalIgnoreCase) || a[0] == "all"))
            {
                b.Kind = a[0] == "all" ? "" : a[0].ToLowerInvariant();
                next = 1;
            }
            if (next < a.Count && a[next].StartsWith("mount=", StringComparison.OrdinalIgnoreCase)) b.Mount = a[next++]["mount=".Length..];
            b.Search = string.Join(' ', Enumerable.Range(next, a.Count - next).Select(i => a[i]));
            b.Refresh();
            var found = b.Filtered();
            foreach (var entry in found) Log.Info(LogCat.Console, "  " + entry);
            Log.Info(LogCat.Console, $"{found.Count} asset(s); kinds: {string.Join(", ", b.Kinds().Select(k => $"{k.Kind} {k.Count}"))}; mounts: {string.Join(", ", b.Mounts())}");
        });

        cvars.RegisterCommand("ed_asset_refs", CVarFlags.None, "ed_asset_refs <path>: every content file that names an asset, at its line.", a =>
        {
            if (browser() is not { } b) { Log.Warn(LogCat.Console, "no asset browser yet"); return; }
            if (a.Count == 0 || !AssetBrowser.TryParse(a[0], out var path)) { Log.Warn(LogCat.Console, "ed_asset_refs <path>"); return; }
            var references = AssetReferences.Find(b.Engine.Vfs, path);
            foreach (var reference in references) Log.Info(LogCat.Console, "  " + reference);
            Log.Info(LogCat.Console, $"{path}: named {references.Count} time(s)");
        });

        cvars.RegisterCommand("ed_asset_pick", CVarFlags.DevOnly,
            "ed_asset_pick <path> <field> | <path> <placement> <component.field>: put an asset in the open record's field, or in a placement's; undoable.", a =>
        {
            if (a.Count < 2 || !AssetBrowser.TryParse(a[0], out var asset))
            {
                Log.Warn(LogCat.Console, "ed_asset_pick <path> <field>   or   ed_asset_pick <path> <placement> <component.field>");
                return;
            }
            string error;
            if (a.Count == 2)
            {
                if (records()?.Current is not { } record) { Log.Warn(LogCat.Console, "no record open: ed_rec_open <type> <id>"); return; }
                if (AssetPicking.ToRecord(record, a[1], asset, out error)) Log.Info(LogCat.Console, $"{record.Id}.{a[1]} = {asset}");
                else Log.Warn(LogCat.Console, error);
                return;
            }
            if (document() is not { IsOpen: true } doc) { Log.Warn(LogCat.Console, "no document open (doc_new or doc_open)"); return; }
            if (doc.Find(a[1]) is not { } placement) { Log.Warn(LogCat.Console, $"'{a[1]}' is not a placement of {doc.Id}"); return; }
            if (AssetPicking.ToPlacement(doc, placement, a[2], asset, out error)) { doc.History.EndMerge(); Log.Info(LogCat.Console, $"{a[1]}.{a[2]} = {asset}"); }
            else Log.Warn(LogCat.Console, error);
        });

        cvars.RegisterCommand("ed_asset_place", CVarFlags.DevOnly,
            "ed_asset_place <path> [x y z] [yaw]: place an asset in the open document (the prefab that uses it, or a model on its own); undoable.", a =>
        {
            if (a.Count == 0 || !AssetBrowser.TryParse(a[0], out var asset)) { Log.Warn(LogCat.Console, "ed_asset_place <path> [x y z] [yaw]"); return; }
            if (document() is not { } doc) { Log.Warn(LogCat.Console, "no world yet"); return; }
            var at = Vector3.Zero;
            float yaw = 0f;
            if (a.Count >= 4 && Number(a[1], out float x) && Number(a[2], out float y) && Number(a[3], out float z)) at = new Vector3(x, y, z);
            if (a.Count >= 5 && Number(a[4], out float turn)) yaw = turn;
            if (AssetPicking.Place(doc, asset, at, yaw, out string error) is { } placed)
                Log.Info(LogCat.Console, $"placed {placed.Name} ({placed.Prefab.Id}) at {at.X:0.##} {at.Y:0.##} {at.Z:0.##}");
            else Log.Warn(LogCat.Console, error);
        });

        cvars.RegisterCommand("ed_asset_rename", CVarFlags.DevOnly,
            "ed_asset_rename <path> <new path>: move one of the game's assets and rewrite every file of the game's that names it.", a =>
        {
            if (browser() is not { } b) { Log.Warn(LogCat.Console, "no asset browser yet"); return; }
            if (a.Count < 2 || !AssetBrowser.TryParse(a[0], out var from) || !AssetBrowser.TryParse(a[1], out var to))
            {
                Log.Warn(LogCat.Console, "ed_asset_rename <path> <new path>");
                return;
            }
            var plan = AssetRename.Plan(b.Engine, from, to, document(), records());
            if (plan.Apply()) b.Select(to);
        });
    }

    private static bool Number(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
