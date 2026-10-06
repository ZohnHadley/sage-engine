#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Sage.UI;

// The UI's records and text (docs/design/13 "As built (style and layout records)", issue #96): the
// `ui_style`, `ui_layout` and `screen` record types, the `view_model` vocabulary, the string tables and
// the `lang` cvar. A base plugin (BasePlugins.All), so every host — the game, a dedicated server, `sage
// validate` — reads and checks the same content; nothing here draws (#97).
//
// Provides, in Init, for a module that depends on it (and to every world's resources): Localisation,
// UiStyles and UiScreens. Content changing — a record file, a string table (hot reload watches
// `strings/` beside `data/`), the language — rebuilds the styles, reloads the tables and builds every
// open screen again.
[Plugin(Id, "0.1.0")]
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class UiModule : IModule
{
    public const string Id = "sage.ui";

    private Engine? _engine;
    private CVar<string>? _lang;

    public Localisation Localisation { get; } = new();
    public UiStyles Styles { get; } = new();

    // The fonts styles name (#338), read through the VFS; from Init on.
    public UiFonts Fonts { get; private set; } = null!;

    // From Init on.
    public UiScreens Screens { get; private set; } = null!;

    public void Init(ModuleContext ctx)
    {
        _engine = ctx.Engine;
        var viewModels = ctx.Engine.Vocabularies.Of<IViewModel>();   // made now, so it is sealed with the rest
        Fonts = new UiFonts(ctx.Engine.Vfs);
        Screens = new UiScreens(ctx.Engine.Records, viewModels, Styles, Localisation);
        ctx.Provide(Localisation);
        ctx.Provide(Fonts);
        ctx.Provide(Styles);
        ctx.Provide(Screens);

        _lang = ctx.Engine.CVars.Register("lang", Localisation.DefaultLanguage, CVarFlags.Archive,
            "The language text is shown in: strings/<lang>/*.json in any mount, English for what it lacks (05 §3.7).");
        _lang.Changed += _ =>
        {
            if (!_started) return;
            Localisation.Load(ctx.Engine.Vfs, _lang.Value);
            Screens.RebuildAll();
        };

        // Opening a widget screen by hand (#97): what a key bound with UiScreenStack.Bind does, for any
        // screen record, in every world — the subject its bindings and conditions ask about is the
        // local player, when there is one.
        ctx.Engine.CVars.RegisterCommand("ui_open", CVarFlags.None,
            "ui_open <screen> [other]: open a screen record (a ui_layout over its view-model) on top of the world's widget screens; " +
            "other names the entity it is about besides the player (the corpse to loot, the merchant).", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "ui_open <screen> [other]"); return; }
            var id = ctx.Engine.Records.Resolve("screen", a[0]);
            if (id.IsEmpty) return;
            foreach (var world in ctx.Engine.Worlds)
                if (world.Resources.TryGet<UiScreenStack>(out var stack) && stack != null)
                {
                    var other = a.Count > 1 ? world.FindByName(a[1]) : default;
                    if (a.Count > 1 && other.IsNull) { Log.Warn(LogCat.Console, $"'{world.Name}': nothing is called '{a[1]}'"); continue; }
                    stack.Open(id, new UiBindContext(world, LocalPlayer(world), other));
                    Log.Info(LogCat.Console, $"'{world.Name}': {id} open ({stack.Layers.Count} layer(s))");
                }
        });
        ctx.Engine.CVars.RegisterCommand("ui_close", CVarFlags.None,
            "ui_close [all]: close the top widget screen (all: every layer, at once).", a =>
        {
            foreach (var world in ctx.Engine.Worlds)
                if (world.Resources.TryGet<UiScreenStack>(out var stack) && stack != null)
                {
                    if (a.Count > 0 && string.Equals(a[0], "all", StringComparison.OrdinalIgnoreCase)) stack.CloseAll();
                    else stack.CloseTop();
                }
        });

        ctx.Engine.Records.AddCheck<UiLayoutRecord>(UiContentChecks.Layout);
        ctx.Engine.Records.AddCheck<ScreenRecord>((screen, check) => UiContentChecks.ScreenViewModel(screen, check, viewModels));

        ctx.Engine.CVars.RegisterCommand("loc", CVarFlags.None,
            "loc <@ns.key> [count]: what a localisation key shows in the current language; loc alone: the language and how many keys it has.", a =>
        {
            if (a.Count == 0)
            {
                Log.Info(LogCat.Console, $"lang {Localisation.Language}: {Localisation.Count} key(s)");
                return;
            }
            string key = a[0].StartsWith('@') ? a[0] : "@" + a[0];
            string text = a.Count > 1 && double.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n)
                ? Localisation.Text(key, n) : Localisation.Text(key);
            Log.Info(LogCat.Console, $"{key} = \"{text}\"");
        });
    }

    private bool _started, _fontsRead;

    public void Start(ModuleContext ctx)
    {
        _started = true;
        ContentChanged();
        ctx.Engine.Records.Reloaded += ContentChanged;
        ctx.Engine.Signals.WorldCreated += OpenTitle;
    }

    // A world made waiting at the title (game.json's `"title"`, Scenes.Title, issue #342) shows that screen,
    // on top of whatever the modules opened (a HUD): modal, and neither Back nor a click beside it closes
    // it. Its view-model starts the game (Engine.BeginGame) or loads a save, and closes it.
    private void OpenTitle(World world)
    {
#pragma warning disable SAGE0121   // the title is part of the scenes API (#29, #342)
        if (!Scenes.AtTitle(world) || _engine!.Scenes.Title is not { IsEmpty: false } title) return;
#pragma warning restore SAGE0121
        if (!world.Resources.TryGet<UiScreenStack>(out var stack) || stack == null) return;
        var layer = stack.Open(title, new UiBindContext(world));
        layer.CloseOnBack = false;
        layer.CloseOnClickOutside = false;
    }

    public void OnWorldCreated(World world)
    {
        world.Resources.Add(Localisation);
        world.Resources.Add(Styles);
        world.Resources.Add(Screens);
        world.Resources.Add(Fonts);
        // The widget screens this world has open (#97): headless here, drawn and fed by the client.
        world.Resources.Add(new UiScreenStack(Screens, Styles, world, Fonts));
    }

    // The entity a screen opened from the console is about: the first player-controlled one.
    private static Entity LocalPlayer(World world)
    {
        foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities) return entity;
        return default;
    }

    private void ContentChanged()
    {
        var engine = _engine!;
        if (_fontsRead) Fonts.Clear();   // a font file may have changed with the rest: read again on next use
        _fontsRead = true;
        Styles.Rebuild(engine.Records);
        Localisation.Load(engine.Vfs, _lang!.Value);
        UiContentChecks.Screens(engine.Records, engine.Vocabularies.Of<IViewModel>());
        if (BuildInfo.IsDevBuild) UiContentChecks.Keys(engine.Records, Localisation);
        Screens.RebuildAll();
    }
}

// What the load cannot see in a layout's fields alone (issue #96): the tree its parents make, which
// widgets hold children, what a binding may name, and — for a screen — whether its view-model has the
// paths its layout binds. Errors at the record's line, like every other content check (05 §3.6).
internal static class UiContentChecks
{
    private static readonly string[] Leaves = { "label", "button", "image", "bar", "slider", "checkbox", "dropdown", "text_field" };

    public static void Layout(UiLayoutRecord layout, RecordCheck check)
    {
        foreach (var (name, node) in layout.Nodes)
        {
            string at = $"Nodes['{name}']";
            if (node.Widget.Length == 0) check.Error(at, $"node '{name}' needs a widget: one of {string.Join(", ", WidgetTypes.Names)}");

            if (node.Parent.Length > 0)
            {
                if (!layout.Nodes.TryGetValue(node.Parent, out var parent))
                    check.Error($"{at}.Parent", $"node '{name}' is in '{node.Parent}', which is not a node of this layout" + Spelling.Suggest(node.Parent, layout.Nodes.Keys));
                else if (Array.IndexOf(Leaves, parent.Widget) >= 0)
                    check.Error($"{at}.Parent", $"node '{name}' is in '{node.Parent}', a {parent.Widget}, which holds no children");
                else if (Cycles(layout, name))
                    check.Error($"{at}.Parent", $"node '{name}' is inside itself (its parents go round in a circle)");
            }

            int children = layout.Nodes.Values.Count(n => n.Parent == name && name.Length > 0);
            if (node.Widget == "scroll" && children > 1)
                check.Error(at, $"a scroll holds one child, and '{name}' has {children}: put them in a stack inside it");

            if (node.Bind.Length > 0 && UiBindings.Primary(node.Widget) == null)
                check.Error($"{at}.Bind", $"a {node.Widget} has no main value to `bind`; name the property in `bindings` (" + string.Join(", ", UiBindings.All) + ")");
            foreach (var (target, path) in node.Bindings)
            {
                string key = target.ToLowerInvariant();
                if (Array.IndexOf(UiBindings.All, key) < 0)
                    check.Error($"{at}.Bindings['{target}']", $"no property '{target}' to bind" + Spelling.Suggest(target, UiBindings.All) + $" (there are: {string.Join(", ", UiBindings.All)})");
                else if (!UiBindings.Applies(node.Widget, key))
                    check.Error($"{at}.Bindings['{target}']", $"a {node.Widget} has no '{key}'");
                if (path.Split('.').Any(s => s.Length == 0) && !BindingPaths.IsSelf(path))
                    check.Error($"{at}.Bindings['{target}']", $"'{path}' is not a path: names separated by '.', or '.' for the object itself");
            }

            if (UiBindings.Of(node).Any(b => b.Target == UiBindings.Rows) && children != 1)
                check.Error(at, $"'{name}' binds its rows, so it holds one node, the template each row is made from; it has {children}");
            if (node.Args.Count > 0 && node.Widget is not ("label" or "button"))
                check.Error($"{at}.Args", $"a {node.Widget} has no text to fill placeholders in");
            if ((node.Wrap || node.Overflow != null || node.MaxWidth > 0f) && node.Widget is not ("label" or "button"))
                check.Error($"{at}.{(node.Wrap ? "Wrap" : node.Overflow != null ? "Overflow" : "MaxWidth")}", $"a {node.Widget} has no text to wrap or cut");
            foreach (var (field, neighbour) in new[] { ("FocusUp", node.FocusUp), ("FocusDown", node.FocusDown), ("FocusLeft", node.FocusLeft), ("FocusRight", node.FocusRight) })
                if (neighbour.Length > 0 && !layout.Nodes.ContainsKey(neighbour))
                    check.Error($"{at}.{field}", $"node '{name}' goes to '{neighbour}', which is not a node of this layout" + Spelling.Suggest(neighbour, layout.Nodes.Keys));
        }
    }

    private static bool Cycles(UiLayoutRecord layout, string start)
    {
        string current = start;
        for (int steps = 0; steps <= layout.Nodes.Count; steps++)
        {
            if (!layout.Nodes.TryGetValue(current, out var node) || node.Parent.Length == 0) return false;
            current = node.Parent;
            if (current == start) return true;
        }
        return true;
    }

    public static void ScreenViewModel(ScreenRecord screen, RecordCheck check, Vocabulary<IViewModel> viewModels)
    {
        if (screen.ViewModel.Length > 0 && !viewModels.Contains(screen.ViewModel))
            check.Error("ViewModel", viewModels.Unknown(screen.ViewModel));
    }

    // Each screen's layout against its view-model's type: every path a node binds must be there, and
    // inside a row template, on the rows' element type. After the load, since it reads two records.
    public static void Screens(RecordStore records, Vocabulary<IViewModel> viewModels)
    {
        foreach (var id in records.Ids("screen"))
        {
            if (!records.TryGet(id, out ScreenRecord screen) || !records.TryGet(screen.Layout.Id, out UiLayoutRecord layout)) continue;
            Type? type = screen.ViewModel.Length > 0 && viewModels.TryFind(screen.ViewModel, out var entry) ? entry.Type : null;
            if (screen.ViewModel.Length > 0 && type == null) continue;   // the screen check said so
            var tree = new LayoutTree(layout);
            CheckPaths(records, id, screen, layout, tree, "", type);
        }
    }

    private static void CheckPaths(RecordStore records, RecordId id, ScreenRecord screen, UiLayoutRecord layout, LayoutTree tree, string parent, Type? source)
    {
        foreach (var (name, node) in tree.ChildrenOf(parent))
        {
            Type? rows = null;
            foreach (var (target, path) in UiBindings.Of(node).Concat(node.Args.Select(a => ("arg " + a.Key, a.Value))))
            {
                if (source == null)
                {
                    Log.Error(LogCat.Records, $"{records.Where("screen", id, "Layout")}: screen {id}: layout {screen.Layout.Id} node '{name}' binds " +
                                              $"{target} to '{path}', and the screen names no view-model");
                    continue;
                }
                if (!BindingPaths.TryWalk(source, path, out _, out var leaf, out string? problem))
                    Log.Error(LogCat.Records, $"{records.Where("screen", id, "Layout")}: screen {id}: layout {screen.Layout.Id} node '{name}' binds " +
                                              $"{target} to '{path}': {problem}");
                else if (target == UiBindings.Rows)
                {
                    if (!typeof(IList).IsAssignableFrom(leaf))
                        Log.Error(LogCat.Records, $"{records.Where("screen", id, "Layout")}: screen {id}: layout {screen.Layout.Id} node '{name}' binds " +
                                                  $"its rows to '{path}', which is not a list");
                    else rows = BindingPaths.ElementType(leaf);
                }
                else if (!BindingPaths.IsReadable(leaf))
                    Log.Error(LogCat.Records, $"{records.Where("screen", id, "Layout")}: screen {id}: layout {screen.Layout.Id} node '{name}' binds " +
                                              $"{target} to '{path}', a {leaf.Name}: a binding reads a number, a flag, text or an object");
                // A form widget writes what the player sets back to its binding (issue #340): a path
                // that cannot be written shows the value and forgets every change, which is a warning.
                else if (target == UiBindings.Written(node.Widget) && !BindingPaths.IsWritable(source, path, out string? readOnly))
                    Log.Warn(LogCat.Records, $"{records.Where("screen", id, "Layout")}: screen {id}: layout {screen.Layout.Id} node '{name}' binds " +
                                             $"{target} to '{path}': {readOnly}, so what the player changes is not kept");
            }
            // A row template's paths are into its row, not the view-model; a plain IList's rows can't be told.
            if (rows != null) { if (rows != typeof(object)) CheckPaths(records, id, screen, layout, tree, name, rows); }
            else CheckPaths(records, id, screen, layout, tree, name, source);
        }
    }

    // Every '@key' any record says that no string table has: a warning each, at its line (issue #96,
    // D3). Walks the records' public fields as content wrote them; dev builds and `sage validate` only.
    public static void Keys(RecordStore records, Localisation text)
    {
        var all = typeof(RecordStore).GetMethod(nameof(RecordStore.All))!;
        foreach (string type in records.TypeNames)
        {
            var clr = records.TypeOf(type);
            if (clr == null) continue;
            var ids = records.Ids(type).ToList();
            var list = (IList)all.MakeGenericMethod(clr).Invoke(records, null)!;
            for (int i = 0; i < list.Count && i < ids.Count; i++)
                Walk(list[i], "", 0, (path, key) =>
                    Log.Warn(LogCat.UI, $"{records.Where(type, ids[i], path)}: {type} {ids[i]}: no text for '{key}' in strings/{text.Language} " +
                                        $"(nor strings/{Localisation.DefaultLanguage}); it shows as the key"), text, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }
    }

    private static void Walk(object? value, string path, int depth, Action<string, string> missing, Localisation text, HashSet<object> seen)
    {
        if (value == null || depth > 12) return;
        switch (value)
        {
            case string s:
                if (Localisation.IsKey(s) && LooksLikeKey(s) && text.Find(s) == null) missing(path, s);
                return;
            case IDictionary dictionary:
                if (!seen.Add(value)) return;
                foreach (DictionaryEntry entry in dictionary) Walk(entry.Value, $"{path}['{entry.Key}']", depth + 1, missing, text, seen);
                return;
            case IList list:
                if (!seen.Add(value)) return;
                for (int i = 0; i < list.Count; i++) Walk(list[i], $"{path}[{i}]", depth + 1, missing, text, seen);
                return;
        }
        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true) return;
        if (!type.IsValueType && !seen.Add(value)) return;
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (field.GetCustomAttribute<JsonIgnoreAttribute>() == null && Worth(field.FieldType))
                Walk(field.GetValue(value), Join(path, field.Name), depth + 1, missing, text, seen);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0
                && property.GetCustomAttribute<JsonIgnoreAttribute>() == null && Worth(property.PropertyType))
                Walk(property.GetValue(value), Join(path, property.Name), depth + 1, missing, text, seen);
    }

    // Strings, lists, maps and objects can hold a key; numbers, vectors and ids cannot.
    private static bool Worth(Type type) =>
        type == typeof(string) || typeof(IEnumerable).IsAssignableFrom(type) || (!type.IsValueType && !type.IsPrimitive) ;

    private static string Join(string path, string name) => path.Length == 0 ? name : path + "." + name;

    // "@ns.key": a namespace and at least one more part, names only — so "@" in an e-mail address or a
    // prose line starting with it is not taken for a key.
    private static bool LooksLikeKey(string text)
    {
        int dots = 0;
        for (int i = 1; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '.') { if (text[i - 1] == '.' || i == 1 || i == text.Length - 1) return false; dots++; }
            else if (!(char.IsLetterOrDigit(c) || c is '_' or '-')) return false;
        }
        return dots > 0;
    }
}
