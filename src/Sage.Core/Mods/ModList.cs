#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Sage.Core;

// user://mods.json: the player's choices, per user (phase 4j):
//   { "order": ["better_blades", "rival_trade"], "disabled": ["old_mod"] }
// A mod the file doesn't know is enabled and goes after the ones it does. Read leniently (a broken file is
// the default list and a warning, never a crash) and written atomically, as config.cfg is.
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // data mods (phase 4j): may change before 1.0
public sealed class ModList
{
    public List<string> Order { get; set; } = new();
    public List<string> Disabled { get; set; } = new();

    public bool IsDisabled(string id) => Disabled.Contains(id, StringComparer.Ordinal);

    // Where the user's list lives.
    public static string DefaultPath => Path.Combine(UserPaths.Root, "mods.json");

    // A missing file is the default list; so is one that cannot be read, with `warning` saying why.
    public static ModList Load(string path, out string? warning)
    {
        warning = null;
        if (!File.Exists(path)) return new ModList();
        try
        {
            var list = JsonSerializer.Deserialize<ModList>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            });
            if (list == null) throw new JsonException("the file is empty");
            list.Order ??= new();
            list.Disabled ??= new();
            list.Order = list.Order.Distinct(StringComparer.Ordinal).ToList();
            return list;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warning = $"{path}: {ex.Message} Using the default mod order (every mod on, in id order).";
            return new ModList();
        }
    }

    // Temp file, then move into place: a crash mid-write never leaves half a list.
    public void Save(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        File.Move(temp, path, overwrite: true);
    }
}
