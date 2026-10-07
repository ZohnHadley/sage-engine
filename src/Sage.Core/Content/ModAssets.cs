#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Sage.Core;

// Which bare asset paths in a mod's record files are that mod's own (issue #398): a path the mod ships and
// no mount that is not a mod has. Such a path is written `mod_id:path` when the records load, so it finds the
// mod's file whichever mod loads later with a file at the same path, and two mods' `textures/falchion.png`
// are two assets, not a conflict. A path the game, the engine or a kit also has stays bare: the mod's file
// replaces theirs for everyone (an override, as before), and two mods doing that are a conflict.
internal sealed class ModAssets
{
    private readonly VirtualFileSystem _vfs;
    private readonly Dictionary<(IMount, string), string?> _seen = new();
    private readonly Dictionary<IMount, Func<string, string?>> _byMount = new();

    public ModAssets(VirtualFileSystem vfs) { _vfs = vfs; }

    // What to make of a bare asset path in a file of `mount`: null for a mount that is not a mod's.
    public Func<string, string?>? For(IMount? mount)
    {
        if (mount == null || !ContentReport.IsModMount(mount)) return null;
        if (!_byMount.TryGetValue(mount, out var map)) _byMount[mount] = map = text => Own(mount, text);
        return map;
    }

    private string? Own(IMount mount, string text)
    {
        if (_seen.TryGetValue((mount, text), out var known)) return known;
        string? own = null;
        try
        {
            var path = VirtualPath.Parse(text);
            var mine = VirtualPath.InNamespace(mount.RecordNamespace, path);
            bool ships = _vfs.Mounts.Any(m => string.Equals(m.RecordNamespace, mount.RecordNamespace, StringComparison.OrdinalIgnoreCase) && m.Exists(mine));
            if (ships && !_vfs.Mounts.Any(m => !ContentReport.IsModMount(m) && m.Exists(path))) own = mine.Value;
        }
        catch (ArgumentException) { }   // not a path: the field's converter says so
        _seen[(mount, text)] = own;
        return own;
    }
}
