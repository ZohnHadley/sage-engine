#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Sage.UI;

// Opening and closing widget screens from content (issue #344): actions by name, so a dialogue option, a
// layout's button (UiNode.Actions), a trigger's `then` or a quest stage reaches a screen without C#.
//
//   { "text": "Show me your wares.", "end": true, "actions": [ { "open_screen": "rpg:shop" } ] }
//
// The screen opens on the world's UiScreenStack with the action's Subject and Other as its own — in a
// conversation the player and the speaker, on a button the screen's own — so the shop sells the
// speaker's goods and the loot window shows the corpse that was used.

[Action("open_screen", Plugin = UiModule.Id)]
internal sealed class OpenScreenAction : IAction
{
    [EntryValue, Property(Tooltip = "The screen record to open, about the subject (the player) and the other (the speaker, the corpse)")]
    public RecordRef<ScreenRecord> Screen;

    public void Run(in ActionContext context)
    {
        if (!Screen.IsEmpty) UiScreenActions.Open(context.World, Screen.Id, context.Subject, context.Other);
    }
}

// `{ "close_screen": {} }`: the top window closes, as Back would; `{ "close_screen": { "all": true } }`: every one.
[Action("close_screen", Plugin = UiModule.Id)]
internal sealed class CloseScreenAction : IAction
{
    [Property(Tooltip = "Every window at once, not only the top one")]
    public bool All;

    public void Run(in ActionContext context)
    {
        if (!context.World.Resources.TryGet<UiScreenStack>(out var stack) || stack == null) return;
        if (All) stack.CloseAll();
        else stack.CloseTop();
    }
}

// `{ "command": "ui_open rpg:shop" }`: a console command run as if typed (issue #347), so a button reaches
// whatever a module made a command, cheats still needing sv_cheats. From a button, `{path}` in it is
// read from the pressed widget's row (or `scope`) — `$parent.` the one around it, `$root.` the view-model —
// and put in as one argument: `{ "command": "give_item {id} {$root.count}" }`. A placeholder that leads
// nowhere, or one outside a button, is left as written, which is said once.
[Action("command", Plugin = UiModule.Id)]
internal sealed class UiCommandAction : IAction
{
    [EntryValue, Property(Tooltip = "The console line to run, as typed; from a button, {path} is read from its row or the view-model")]
    public string Line = "";

    public void Run(in ActionContext context)
    {
        if (Line.Length == 0 || context.World.Engine is not { } engine) return;
        string line = Line.Contains('{') ? Fill(Line, UiActionScope.Current) : Line;
        engine.CVars.Execute(line, ExecSource.Console);
    }

    internal static string Fill(string line, UiActionScope? scope)
    {
        var builder = new StringBuilder(line.Length + 16);
        int i = 0;
        while (i < line.Length)
        {
            int open = line.IndexOf('{', i);
            int close = open < 0 ? -1 : line.IndexOf('}', open + 1);
            if (open < 0 || close < 0) { builder.Append(line, i, line.Length - i); break; }
            builder.Append(line, i, open - i);
            string path = line[(open + 1)..close].Trim();
            if (scope != null && scope.TryRead(path, out object? value)) builder.Append(Argument(value));
            else
            {
                Log.Once(LogCat.UI, LogLevel.Warn, $"command:{line}:{path}",
                    $"command '{line}': {{{path}}} " + (scope == null ? "is only filled in from a button" : "reads nothing") + "; it is left as written");
                builder.Append(line, open, close + 1 - open);
            }
            i = close + 1;
        }
        return builder.ToString();
    }

    // One console argument: quoted when it has a space or a ';' (so a row's name cannot add a command),
    // with no quotes or line breaks of its own.
    private static string Argument(object? value)
    {
        string text = value switch
        {
            null => "",
            bool flag => flag ? "1" : "0",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        text = text.Replace("\"", "", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
        return text.Length == 0 || text.AsSpan().IndexOfAny(" \t;/") >= 0 ? $"\"{text}\"" : text;
    }
}

// What a button's actions run about (UiScreen.RunActions, issue #347): the widget pressed, its screen's
// root and view-model — what a `command`'s {placeholders} read. Set only while they run.
internal sealed class UiActionScope
{
    [ThreadStatic] internal static UiActionScope? Current;

    private readonly Widget _widget;
    private readonly Widget _root;
    private readonly object? _viewModel;

    public UiActionScope(Widget widget, Widget root, object? viewModel)
    {
        _widget = widget;
        _root = root;
        _viewModel = viewModel;
    }

    // `path` from the pressed widget's scope: its row's (or a `scope`'s) Data, the nearest first, then
    // the view-model; `$parent.` one further out, `$root.` the view-model.
    public bool TryRead(string path, out object? value)
    {
        value = null;
        string rest = BindingScope.Split(path, out int up);
        var scopes = new List<object>();
        for (var w = _widget; w != null; w = w.Parent)
        {
            if (w.Data != null && (scopes.Count == 0 || !ReferenceEquals(scopes[^1], w.Data))) scopes.Add(w.Data);
            if (w == _root) break;
        }
        if (_viewModel != null) scopes.Add(_viewModel);
        object? source = up < 0 ? _viewModel : up < scopes.Count ? scopes[up] : null;
        if (source == null) return false;
        if (BindingPaths.IsSelf(rest)) { value = source; return true; }
        if (!BindingPaths.TryWalk(source.GetType(), rest, out _, out _, out _)) return false;
        value = BindingPaths.ObjectReader(source.GetType(), rest)(source);
        return true;
    }
}

[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public static class UiScreenActions
{
    // Opens `screen` on the world's widget stack about `subject` and `other`; null when the world has no
    // stack (a game without sage.ui) or there is no such screen record, which is said once.
    public static UiLayer? Open(World world, RecordId screen, Entity subject, Entity other = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.Resources.TryGet<UiScreenStack>(out var stack) || stack == null) return null;
        if (!world.Resources.Get<RecordStore>().TryGet(screen, out ScreenRecord _))
        {
            Log.Once(LogCat.UI, LogLevel.Error, $"open_screen:{screen}", $"No screen record {screen} to open");
            return null;
        }
        return stack.Open(screen, new UiBindContext(world, subject, other));
    }
}
