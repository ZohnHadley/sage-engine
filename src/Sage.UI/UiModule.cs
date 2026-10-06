#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text;
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

    // Whether the crosshair is drawn (CrosshairView); the client's until issue #350 moved the crosshair onto widgets.
    public const string CrosshairCVar = "ui_crosshair";

    private Engine? _engine;
    private CVar<string>? _lang, _styleSet;
    private CVar<float>? _uiScale, _textScale;
    private CVar<bool>? _subtitles, _captions;

    public Localisation Localisation { get; } = new();
    public UiStyles Styles { get; } = new();

    // From Init on: the input glyphs `{action:...}` shows in text (issue #352).
    public InputPrompts Prompts { get; private set; } = null!;

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
        Prompts = new InputPrompts(ctx.Engine, Localisation);
        Localisation.Prompts = Prompts;
        ctx.Provide(Localisation);
        ctx.Provide(Fonts);
        ctx.Provide(Styles);
        ctx.Provide(Screens);
        ctx.Provide(Prompts);

        var glyphs = ctx.Engine.CVars.Register("joy_glyphs", "auto", CVarFlags.Archive,
            "Whose button names prompts show for a pad: auto (the pad's own, from its name), xbox or playstation (issue #352).");
        void ApplyGlyphs()
        {
            if (InputPrompts.TryParse(glyphs.Value, out var pad)) Prompts.ForcedPad = pad;
            else Log.Warn(LogCat.Console, $"joy_glyphs '{glyphs.Value}': auto, xbox or playstation");
        }
        glyphs.Changed += _ => ApplyGlyphs();
        ApplyGlyphs();

        _lang = ctx.Engine.CVars.Register("lang", Localisation.DefaultLanguage, CVarFlags.Archive,
            "The language text is shown in: strings/<lang>/*.json in any mount, English for what it lacks (05 §3.7).");
        _lang.Changed += _ =>
        {
            if (!_started) return;
            LoadLanguage();
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

        // Accessibility (issue #351): the player's scale, text size, style set and subtitles, live.
        _uiScale = ctx.Engine.CVars.Register("ui_scale", 1f, CVarFlags.Archive,
            "How big the game's UI is drawn, 1 = as designed (0.5 to 3); the layout reflows to fit.", UiRoot.MinUiScale, UiRoot.MaxUiScale);
        _textScale = ctx.Engine.CVars.Register("ui_text_scale", 1f, CVarFlags.Archive,
            "How big the UI's text is, 1 = as designed (0.5 to 3), on top of ui_scale.", UiRoot.MinUiScale, UiRoot.MaxUiScale);
        _styleSet = ctx.Engine.CVars.Register("ui_style_set", "", CVarFlags.Archive,
            "The ui_style_set the UI is drawn with (high contrast, a colour-blind palette), by id; empty: the styles as written.");
        ctx.Engine.CVars.Register(CrosshairCVar, true, CVarFlags.Archive, "Draw the crosshair while a camera rig has the view (13 §3).");
        _subtitles = ctx.Engine.CVars.Register("subtitles", true, CVarFlags.Archive, "Show lines of dialogue that are heard as subtitles.");
        _captions = ctx.Engine.CVars.Register("captions", false, CVarFlags.Archive, "Show captions of sounds (\"[door creaks]\") that have one.");
        _uiScale.Changed += _ => EachStack(stack => stack.UiScale = _uiScale.Value);
        _textScale.Changed += _ => EachStack(stack => stack.TextScale = _textScale.Value);
        _subtitles.Changed += _ => EachWorld(world => { if (world.Resources.TryGet<Subtitles>(out var s) && s != null) s.ShowDialogue = _subtitles.Value; });
        _captions.Changed += _ => EachWorld(world => { if (world.Resources.TryGet<Subtitles>(out var s) && s != null) s.ShowCaptions = _captions.Value; });
        _styleSet.Changed += _ =>
        {
            if (!_started) return;
            Styles.UseSet(ActiveSet());
            Screens.RebuildAll();
        };

        ctx.Engine.Records.AddCheck<UiLayoutRecord>(UiContentChecks.Layout);
        ctx.Engine.Records.AddCheck<UiStyleSetRecord>(UiAccessibilityChecks.StyleSet);
        ctx.Engine.Records.AddCheck<ScreenRecord>((screen, check) => UiContentChecks.ScreenViewModel(screen, check, viewModels));
        ctx.Engine.Records.AddCheck<OptionRecord>(OptionChecks.Check);   // the options screen's settings (issue #339)

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
        ctx.Engine.CVars.RegisterCommand("loc_check", CVarFlags.None,
            "loc_check: every language's tables against English's (missing and stray keys, placeholders, plural forms) and its characters against the fonts that draw them, as `sage validate` does.", _ =>
        {
            int problems = UiContentChecks.Languages(ctx.Engine.Records, ctx.Engine.Vfs, Fonts);
            Log.Info(LogCat.Console, $"loc_check: {problems} problem(s) in {Localisation.LanguagesIn(ctx.Engine.Vfs).Count} language(s)");
        });
    }

    // The `lang` cvar's tables, and what its `language` record says: direction and fonts (#345).
    private void LoadLanguage()
    {
        var engine = _engine!;
        Localisation.Load(engine.Vfs, _lang!.Value);
        var record = UiContentChecks.FindLanguage(engine.Records, Localisation.Language)?.Record;
        Localisation.Describe(record?.Direction ?? TextDirection.Auto, record?.Fonts.ToArray() ?? Array.Empty<AssetPath>());
        Fonts.SetFallbacks(Localisation.Fonts);
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
        world.Resources.Add(Prompts);
        world.Resources.Add(Styles);
        world.Resources.Add(Screens);
        world.Resources.Add(Fonts);
        // The widget screens this world has open (#97): headless here, drawn and fed by the client.
        var subtitles = new Subtitles(Localisation) { ShowDialogue = _subtitles!.Value, ShowCaptions = _captions!.Value };
        world.Resources.Add(subtitles);
        world.Resources.Add(new UiScreenStack(Screens, Styles, world, Fonts)
        {
            UiScale = _uiScale!.Value,
            TextScale = _textScale!.Value,
            Subtitles = subtitles,
        });
        world.AddSystem(new SubtitleSystem(world));
    }

    private void EachWorld(Action<World> act)
    {
        if (_engine == null) return;
        foreach (var world in _engine.Worlds) act(world);
    }

    private void EachStack(Action<UiScreenStack> act) =>
        EachWorld(world => { if (world.Resources.TryGet<UiScreenStack>(out var stack) && stack != null) act(stack); });

    // The style set the cvar names, by id ("high_contrast" finds it in any namespace); empty when none.
    private RecordId ActiveSet() => _styleSet!.Value.Length == 0 ? default : _engine!.Records.Resolve("ui_style_set", _styleSet.Value);

    // The entity a screen opened from the console is about: the first player-controlled one.
    private static Entity LocalPlayer(World world)
    {
        foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities) return entity;
        return default;
    }

    private void ContentChanged()
    {
        var engine = _engine!;
        Styles.UseSet(ActiveSet());
        if (_fontsRead) Fonts.Clear();   // a font file may have changed with the rest: read again on next use
        _fontsRead = true;
        Styles.Rebuild(engine.Records);
        LoadLanguage();
        UiContentChecks.Includes(engine.Records);
        UiContentChecks.Screens(engine.Records, engine.Vocabularies.Of<IViewModel>());
        if (BuildInfo.IsDevBuild) UiContentChecks.Keys(engine.Records, Localisation);
        // Every translation's completeness (#345): `sage validate` (and loc_check) only, as it reads
        // every language's tables and fonts.
        if (engine.Records.MissingAssetsAreErrors) UiContentChecks.Languages(engine.Records, engine.Vfs, Fonts);
        if (BuildInfo.IsDevBuild) UiAccessibilityChecks.Contrast(engine.Records);
        Screens.RebuildAll();
    }
}

// What the load cannot see in a layout's fields alone (issue #96): the tree its parents make, which
// widgets hold children, what a binding may name, and — for a screen — whether its view-model has the
// paths its layout binds. Errors at the record's line, like every other content check (05 §3.6).
internal static class UiContentChecks
{
    private static readonly string[] Leaves = { "label", "button", "image", "bar", "slider", "checkbox", "dropdown", "text_field", "view" };

    public static void Layout(UiLayoutRecord layout, RecordCheck check)
    {
        foreach (var (name, node) in layout.Nodes)
        {
            string at = $"Nodes['{name}']";
            if (node.Widget.Length == 0 && node.Include.IsEmpty) check.Error(at, $"node '{name}' needs a widget: one of {string.Join(", ", WidgetTypes.Names)}");
            if (!node.Include.IsEmpty && Array.IndexOf(Leaves, node.Widget) >= 0)
                check.Error($"{at}.Include", $"node '{name}' is a {node.Widget}, which holds no children, so it cannot include a layout");
            if (node.Params.Count > 0 && node.Include.IsEmpty)
                check.Error($"{at}.Params", $"node '{name}' has params but includes no layout to fill in");

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
                if (!IsPath(path, out string? problem))
                    check.Error($"{at}.Bindings['{target}']", problem!);
            }
            foreach (var (field, path) in new[] { ("Bind", node.Bind), ("Scope", node.Scope) })
                if (path.Length > 0 && !IsPath(path, out string? problem)) check.Error($"{at}.{field}", problem!);

            if (UiBindings.Of(node).Any(b => b.Target == UiBindings.Rows) && children != 1)
                check.Error(at, $"'{name}' binds its rows, so it holds one node, the template each row is made from; it has {children}");
            if (node.Args.Count > 0 && node.Widget is not ("label" or "button"))
                check.Error($"{at}.Args", $"a {node.Widget} has no text to fill placeholders in");
            if ((node.Wrap || node.Overflow != null || node.MaxWidth > 0f) && node.Widget is not ("label" or "button"))
                check.Error($"{at}.{(node.Wrap ? "Wrap" : node.Overflow != null ? "Overflow" : "MaxWidth")}", $"a {node.Widget} has no text to wrap or cut");
            // A view's (issue #348).
            if (node.Widget != "view")
            {
                if (node.Target.Length > 0) check.Error($"{at}.Target", $"a {node.Widget} shows no render target; a view does");
                if (node.Camera != null) check.Error($"{at}.Camera", $"a {node.Widget} has no camera; a view does");
            }
            if (node.FogColour != null && node.Widget is not ("image" or "view"))
                check.Error($"{at}.FogColour", $"a {node.Widget} has no picture to cover with fog");
            if (node.Widget == "view" && node.Target.Length == 0 && node.Camera != null && !node.Bindings.Keys.Any(k => k.Equals(UiBindings.Target, StringComparison.OrdinalIgnoreCase)) && node.Bind.Length == 0)
                check.Error($"{at}.Target", $"view '{name}' has a camera and no `target` to draw into: name one");
            foreach (var (field, neighbour) in new[] { ("FocusUp", node.FocusUp), ("FocusDown", node.FocusDown), ("FocusLeft", node.FocusLeft), ("FocusRight", node.FocusRight) })
                if (neighbour.Length > 0 && !layout.Nodes.ContainsKey(neighbour))
                    check.Error($"{at}.{field}", $"node '{name}' goes to '{neighbour}', which is not a node of this layout" + Spelling.Suggest(neighbour, layout.Nodes.Keys));
        }
    }

    // A path as content writes it (issue #347): `$root.`/`$parent.` first, then names separated by '.',
    // each with any [index]es; a `{$param}` of an included layout is put in before it is read.
    private static bool IsPath(string path, out string? problem)
    {
        problem = null;
        if (path.Contains("{$", StringComparison.Ordinal)) return true;
        if (BindingPaths.TryParse(BindingScope.Split(path, out _), out _, out string? why)) return true;
        problem = $"{why}: a path is names separated by '.', each with any [index], '$parent.' or '$root.' first, or '.' for the object itself";
        return false;
    }

    // Every node's `include` (issue #347), which reads two records: the layout is there, does not include
    // itself through others, is given only params it has, and is given every `{$name}` it says that it
    // has no default for. After the load, like Screens.
    public static void Includes(RecordStore records)
    {
        foreach (var id in records.Ids("ui_layout"))
        {
            if (!records.TryGet(id, out UiLayoutRecord layout)) continue;
            foreach (var (name, node) in layout.Nodes)
            {
                if (node.Include.IsEmpty || !records.TryGet(node.Include.Id, out UiLayoutRecord included)) continue;
                string where = records.Where("ui_layout", id, $"Nodes['{name}'].Include");
                if (Loops(records, id, node.Include.Id, new HashSet<RecordId>()))
                {
                    Log.Error(LogCat.Records, $"{where}: ui_layout {id}: node '{name}' includes {node.Include.Id}, which includes {id} again: a layout cannot be inside itself");
                    continue;
                }
                var said = included.Nodes.Values.SelectMany(Texts).SelectMany(UiParams.Named).ToHashSet(StringComparer.Ordinal);
                foreach (var param in node.Params.Keys)
                    if (!said.Contains(param) && !included.Params.ContainsKey(param))
                        Log.Error(LogCat.Records, $"{records.Where("ui_layout", id, $"Nodes['{name}'].Params")}: ui_layout {id}: node '{name}' gives {node.Include.Id} " +
                                                  $"a param '{param}' it has not got" + Spelling.Suggest(param, said.Concat(included.Params.Keys)));
                foreach (var param in said)
                    if (!node.Params.ContainsKey(param) && !included.Params.ContainsKey(param))
                        Log.Error(LogCat.Records, $"{where}: ui_layout {id}: node '{name}' includes {node.Include.Id}, which says {{${param}}}, " +
                                                  $"and neither its params nor the layout's own give '{param}'");
            }
        }
    }

    // Whether `layout`, through the layouts it includes, comes back to `start`.
    private static bool Loops(RecordStore records, RecordId start, RecordId layout, HashSet<RecordId> seen)
    {
        if (layout == start) return true;
        if (!seen.Add(layout) || !records.TryGet(layout, out UiLayoutRecord record)) return false;
        foreach (var node in record.Nodes.Values)
            if (!node.Include.IsEmpty && Loops(records, start, node.Include.Id, seen)) return true;
        return false;
    }

    // The text and paths of a node that `{$name}` is put into, and its actions' text.
    private static IEnumerable<string> Texts(UiNode node) =>
        new[] { node.Text, node.Tooltip, node.Title, node.Placeholder, node.Bind, node.Scope }
            .Concat(node.Options).Concat(node.Args.Values).Concat(node.Bindings.Values).Concat(node.Params.Values)
            .Concat(node.Actions.SelectMany(UiParams.Texts));

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
            var tree = new LayoutTree(layout, records);
            CheckPaths(records, id, screen, tree, "", type, new List<Type?>());
        }
    }

    // `outer`: the types of the scopes around `source` (`$parent.`, `$root.`; issue #347), outermost first.
    private static void CheckPaths(RecordStore records, RecordId id, ScreenRecord screen, LayoutTree tree, string parent, Type? source, List<Type?> outer)
    {
        foreach (var (name, written) in tree.ChildrenOf(parent))
        {
            var node = written;
            Type? rows = null;
            // A `scope`: the node and what is inside it read from there.
            Type? nodeSource = source;
            bool scoped = node.Scope.Length > 0 && source != null;
            if (scoped)
            {
                if (!Walk(source, outer, node.Scope, out var leaf, out string? problem))
                {
                    Log.Error(LogCat.Records, $"{records.Where("screen", id, "Layout")}: screen {id}: layout {screen.Layout.Id} node '{name}' reads its scope from '{node.Scope}': {problem}");
                    continue;
                }
                if (leaf == null) continue;   // read from a scope that cannot be told: nothing below is checked
                if (leaf.IsValueType || leaf == typeof(string))
                {
                    Log.Error(LogCat.Records, $"{records.Where("screen", id, "Layout")}: screen {id}: layout {screen.Layout.Id} node '{name}' reads its scope from '{node.Scope}', " +
                                              $"a {leaf.Name}: a scope is an object whose members its paths name");
                    continue;
                }
                outer.Add(source);
                nodeSource = leaf;
            }
            foreach (var (target, path) in UiBindings.Of(node).Concat(node.Args.Select(a => ("arg " + a.Key, a.Value))))
            {
                if (nodeSource == null)
                {
                    Log.Error(LogCat.Records, $"{records.Where("screen", id, "Layout")}: screen {id}: layout {screen.Layout.Id} node '{name}' binds " +
                                              $"{target} to '{path}', and the screen names no view-model");
                    continue;
                }
                if (!Walk(nodeSource, outer, path, out var leaf, out string? problem))
                    Log.Error(LogCat.Records, $"{records.Where("screen", id, "Layout")}: screen {id}: layout {screen.Layout.Id} node '{name}' binds " +
                                              $"{target} to '{path}': {problem}");
                else if (leaf == null) { if (target == UiBindings.Rows) rows = typeof(object); }
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
                else if (target == UiBindings.Written(node.Widget) && !IsWritable(nodeSource, outer, path, out string? readOnly))
                    Log.Warn(LogCat.Records, $"{records.Where("screen", id, "Layout")}: screen {id}: layout {screen.Layout.Id} node '{name}' binds " +
                                             $"{target} to '{path}': {readOnly}, so what the player changes is not kept");
            }
            // A row template's paths are into its row, not the view-model, with the view-model around it;
            // a plain IList's rows can't be told.
            if (rows != null)
            {
                if (rows != typeof(object))
                {
                    outer.Add(nodeSource);
                    CheckPaths(records, id, screen, tree, name, rows, outer);
                    outer.RemoveAt(outer.Count - 1);
                }
            }
            else CheckPaths(records, id, screen, tree, name, nodeSource, outer);
            if (scoped) outer.RemoveAt(outer.Count - 1);
        }
    }

    // The type `path` starts from — this scope's, one further out per `$parent.`, the view-model's for
    // `$root.` — and where it ends. A scope that cannot be told (a plain IList's rows) passes.
    private static bool Walk(Type? source, List<Type?> outer, string path, out Type? leaf, out string? problem)
    {
        leaf = null;
        problem = null;
        string rest = BindingScope.Split(path, out int up);
        if (!From(source, outer, up, out var start, out problem)) return false;
        if (start == null) return true;   // leaf null: not known
        bool found = BindingPaths.TryWalk(start, rest, out _, out var end, out problem);
        leaf = end;
        return found;
    }

    private static bool IsWritable(Type? source, List<Type?> outer, string path, out string? problem)
    {
        string rest = BindingScope.Split(path, out int up);
        if (!From(source, outer, up, out var start, out problem)) return false;
        return start == null || BindingPaths.IsWritable(start, rest, out problem);
    }

    private static bool From(Type? source, List<Type?> outer, int up, out Type? start, out string? problem)
    {
        problem = null;
        start = up == 0 ? source : up < 0 ? (outer.Count == 0 ? source : outer[0]) : up <= outer.Count ? outer[^up] : null;
        if (up > outer.Count)
        {
            problem = up == 1 ? "there is no scope around this one: '$parent.' reaches out of a list's rows or a `scope`"
                              : $"there are not {up} scopes around this one";
            return false;
        }
        return true;
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

    // The `language` record for a language code, if content has one.
    public static (RecordId Id, LanguageRecord Record)? FindLanguage(RecordStore records, string code)
    {
        foreach (var id in records.Ids("language"))
            if (records.TryGet(id, out LanguageRecord record) && LanguageRecord.SameCode(record.CodeFor(id), code)) return (id, record);
        return null;
    }

    // How complete each translation is (issue #345), as warnings: for every language with tables but
    // English, the English keys it lacks (shown in English), keys only it has (no content can name
    // them), placeholders it adds or leaves out, plural forms its counts need and it lacks, and the
    // characters none of the fonts that would draw it has. Also a `language` record no tables are for.
    // Returns how many problems it said.
    public static int Languages(RecordStore records, VirtualFileSystem vfs, UiFonts fonts)
    {
        int problems = 0;
        void Warn(string message) { Log.Warn(LogCat.UI, message); problems++; }
        var languages = Localisation.LanguagesIn(vfs);

        foreach (var id in records.Ids("language"))
        {
            if (!records.TryGet(id, out LanguageRecord record)) continue;
            string code = record.CodeFor(id);
            if (!languages.Any(l => LanguageRecord.SameCode(l, code)))
                Warn($"{records.Where("language", id)}: language {id}: no mount has strings/{code}/, so nothing is shown in it");
        }

        var english = Localisation.Read(vfs, Localisation.DefaultLanguage);
        // The fonts any text may be drawn in, whatever the language: the styles' and the engine's own.
        var common = new List<TrueTypeFont>();
        foreach (var id in records.Ids("ui_style"))
            if (records.TryGet(id, out UiStyleRecord style) && UiFonts.IsTrueType(style.Font) && fonts.Get(style.Font) is { } font) common.Add(font);
        if (fonts.Quiet(UiFonts.EngineFont) is { } engineFont) common.Add(engineFont);

        foreach (string language in languages)
        {
            if (language == Localisation.DefaultLanguage) continue;
            var texts = Localisation.Read(vfs, language);
            var missing = english.Keys.Where(k => !texts.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
                Warn($"strings/{language}: {missing.Count} of {english.Count} key(s) have no '{language}' text and show in English: " +
                     string.Join(", ", missing.Take(10)) + (missing.Count > 10 ? $", and {missing.Count - 10} more" : ""));

            var uses = PluralRules.Uses(language);
            foreach (var (key, entry) in texts.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                if (!english.TryGetValue(key, out var source))
                {
                    Warn($"{entry.File}: '{key[1..]}' is in strings/{language} but not strings/{Localisation.DefaultLanguage}, so no content names it" +
                         Spelling.Suggest(key, english.Keys));
                    continue;
                }
                var theirs = Placeholders(source);
                var ours = Placeholders(entry);
                foreach (string name in ours.Except(theirs).OrderBy(n => n, StringComparer.Ordinal))
                    Warn($"{entry.File}: '{key[1..]}' names {{{name}}}, which the English text does not, so nothing fills it");
                foreach (string name in theirs.Except(ours).OrderBy(n => n, StringComparer.Ordinal))
                    Warn($"{entry.File}: '{key[1..]}' leaves out {{{name}}}, which the English text shows");
                if (source.IsPlural && !entry.IsPlural)
                    Warn($"{entry.File}: '{key[1..]}' has plural forms in English and one text in '{language}': give it the forms {string.Join(", ", uses.Select(Name))}");
                else if (entry.IsPlural)
                {
                    var lacking = uses.Where(c => c != PluralCategory.Other && !entry.HasForm(c)).ToList();
                    if (lacking.Count > 0)
                        Warn($"{entry.File}: '{key[1..]}' has no {string.Join(", ", lacking.Select(Name))} form(s), which counts in '{language}' take; they show its 'other'");
                }
            }

            // Characters no font that could draw them has: the language's own fonts, the styles' and the engine's.
            var chainFonts = (FindLanguage(records, language)?.Record.Fonts ?? new List<AssetPath>())
                .Select(fonts.Get).Where(f => f != null).Select(f => f!).Concat(common).ToList();
            if (chainFonts.Count == 0) continue;
            var chain = new FontChain(chainFonts[0], chainFonts.Skip(1));
            var lacked = new SortedSet<int>();
            foreach (var entry in texts.Values)
                foreach (string text in entry.Texts)
                    foreach (var rune in Scripts.Shape(text).EnumerateRunes())
                        if (!Rune.IsWhiteSpace(rune) && !Rune.IsControl(rune) && !chain.Covers(rune.Value)) lacked.Add(rune.Value);
            if (lacked.Count > 0)
                Warn($"strings/{language}: {lacked.Count} character(s) no font has, drawn as boxes: " +
                     string.Join(" ", lacked.Take(20).Select(c => $"{char.ConvertFromUtf32(c)} U+{c:X4}")) + (lacked.Count > 20 ? " ..." : "") +
                     $"; name a font that has them in a `language` record for '{language}'");
        }
        return problems;
    }

    private static string Name(PluralCategory category) => category.ToString().ToLowerInvariant();

    // The {placeholders} a text (every form of it) names, without their formats.
    private static HashSet<string> Placeholders(LocalisedText entry)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string text in entry.Texts)
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '{') continue;
                if (i + 1 < text.Length && text[i + 1] == '{') { i++; continue; }
                int close = text.IndexOf('}', i + 1);
                if (close < 0) break;
                string name = text[(i + 1)..close];
                int colon = name.IndexOf(':');
                names.Add(colon < 0 ? name : name[..colon]);
                i = close;
            }
        return names;
    }

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
