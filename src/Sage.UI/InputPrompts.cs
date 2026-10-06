#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Sage.UI;

// Input glyphs in text (issue #352; docs/design/08 §3.1, 13 §3): `{action:Use}` in any localised text — a
// string table's line or a layout's own — shows what the player presses for that action on the device
// they are using now, so "{action:Use}   Pick up {item}" reads "E   Pick up sword" at the keyboard and
// "X   Pick up sword" once they pick up the pad ("Square" on a PlayStation pad). It follows rebinds, the
// last-used device (Engine.LastDevice) and the pad's family, and anything showing the text is worked out
// again when one of them moves: Localisation.Version moves with them.
//
// - **The binding** is InputGlyphs.Pick's: the first of the action's bindings (Engine.Rebinds, the player's
//   rebinds included) for the device in use, the other family's when it has none. Gameplay's bindings are
//   asked first, then UI's, the editor's and the console's.
// - **The glyph registry** is a string table: a binding's glyph is the text of `@input.<glyph key>`
//   (InputGlyphs.GlyphKey: `@input.key.Space`, `@input.xbox.A`, `@input.playstation.A`, `@input.mouse.Left`)
//   when a table has it, so a translation names the keys in its language and a game whose font has button
//   pictures maps them to its characters; otherwise the built-in name (InputGlyphs.DefaultName: Keyboard,
//   Xbox and PlayStation names).
// - **The pad family** is the client's guess from the pad's name, unless `joy_glyphs` says xbox or
//   playstation.
// - An action nobody binds shows its own name, and says so once.
//
// Resolving a prompt again allocates only when what it shows changed: names are kept per action.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class InputPrompts
{
    public const string Placeholder = "action:";
    public const string GlyphTablePrefix = "@input.";

    private static readonly InputContext[] Contexts = { InputContext.Gameplay, InputContext.UI, InputContext.Editor, InputContext.Console };

    private readonly Engine _engine;
    private readonly Localisation _text;
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);
    private InputRebinds? _rebinds;
    private int _rebindsVersion = -1, _deviceVersion = -1, _tablesVersion = -1;
    private PadFamily? _forced, _lastForced;
    private int _version;

    internal InputPrompts(Engine engine, Localisation text)
    {
        _engine = engine;
        _text = text;
    }

    // The pad family the prompts show whatever pad is in use (`joy_glyphs xbox|playstation`); null: the pad's own.
    public PadFamily? ForcedPad
    {
        get => _forced;
        set => _forced = value;
    }

    public InputDeviceKind Device => _engine.LastDevice.Current;
    public PadFamily Pad => _forced ?? _engine.LastDevice.Pad;

    // Moves whenever what a prompt shows may have: a rebind, the device in use, the pad's family, the tables.
    public int Version
    {
        get
        {
            Sync();
            return _version;
        }
    }

    // The binding of `action` a prompt shows now, or null when nothing binds it.
    public InputBinding? Binding(string action)
    {
        var rebinds = _engine.Rebinds;
        foreach (var context in Contexts)
            if (InputGlyphs.Pick(rebinds.Bindings(context, action), Device) is { } binding) return binding;
        return null;
    }

    // What the player presses for `action` now: "E", "X", "Square"; the action's name when nothing binds it.
    public string Glyph(string action)
    {
        Sync();
        if (_names.TryGetValue(action, out var name)) return name;
        var binding = Binding(action);
        if (binding == null)
        {
            if (_warned.Add(action)) Log.Warn(LogCat.UI, $"{{action:{action}}}: no input binds '{action}'; the prompt shows its name");
            name = action;
        }
        else name = Name(binding);
        _names[action] = name;
        return name;
    }

    // A binding's glyph: the string table's `@input.<glyph key>`, else the built-in name.
    public string Name(InputBinding binding)
    {
        var pad = Pad;
        string key = GlyphTablePrefix + InputGlyphs.GlyphKey(binding, pad);
        return _text.Find(key) is { } entry ? entry.Other : InputGlyphs.DefaultName(binding, pad);
    }

    private void Sync()
    {
        var rebinds = _engine.Rebinds;
        var device = _engine.LastDevice;
        int tables = _text.TablesVersion;
        if (ReferenceEquals(rebinds, _rebinds) && rebinds.Version == _rebindsVersion && device.Version == _deviceVersion
            && tables == _tablesVersion && _forced == _lastForced)
            return;
        _rebinds = rebinds;
        _rebindsVersion = rebinds.Version;
        _deviceVersion = device.Version;
        _tablesVersion = tables;
        _lastForced = _forced;
        _names.Clear();
        _version++;
    }

    // `joy_glyphs`: auto, xbox or playstation.
    internal static bool TryParse(string value, out PadFamily? pad)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "" or "auto": pad = null; return true;
            case "xbox": pad = PadFamily.Xbox; return true;
            case "playstation" or "ps": pad = PadFamily.PlayStation; return true;
            default: pad = null; return false;
        }
    }
}
