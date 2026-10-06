#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage.Simulation;

public enum RebindStatus
{
    Applied,       // changed, saved and in force
    Unchanged,     // nothing to change
    Conflict,      // another action in the context uses that input; nothing was changed
    Invalid,       // not a binding this action can have, or an action nobody registered
    CannotSave,    // no user folder, or it can't be written; nothing was changed
}

// What a rebind did. `Conflicts` names the other actions that use the input; `CanReplace` says whether the
// player may take it from them (an axis action's composite, WASD, is never stripped by a button's rebind).
public readonly record struct RebindResult(RebindStatus Status, string Message = "",
                                           IReadOnlyList<string>? Conflicts = null, bool CanReplace = false)
{
    public bool Ok => Status is RebindStatus.Applied or RebindStatus.Unchanged;
}

// A capture that ended in a conflict, waiting for the player to say replace or cancel.
public sealed record PendingRebind(InputContext Context, string Action, InputBinding Input, IReadOnlyList<string> Conflicts, bool CanReplace);

// The player's rebinds (docs/design/08 §3.2, §7; issue #328). They are **record patches** in `user://input.json`,
// mounted after the game and every mod, so the one merge rule decides and nothing here merges anything:
//
//   [ { "type": "input_map", "id": "user:rebinds_gameplay", "context": "Gameplay",      // what the player chose,
//       "actions": { "Jump": [ { "key": "F" } ] } },                                      // an action's whole list
//     { "type": "input_map", "id": "sage:gameplay", "patch": true,                        // and the maps that bound
//       "actions": { "Jump": [] } } ]                                                     // it say nothing of it
//
// A rebound action has exactly the bindings the player gave it; resetting it deletes its lines and the
// game's own come back. Every change rewrites the file (temp file, then move), reloads records, and so
// the client's `Rebuild` runs: the new key fires the next frame. A file that cannot be written changes
// nothing. The names of keys are the client's (it knows MonoGame's), so a `Validator` the client sets says
// whether an input can drive an action; headless, only the shape is checked.
//
// Capture mode (the controls screen): BeginCapture(context, action), then the client offers the next input
// the player presses (Offer). A conflict ends the capture with a PendingRebind to Resolve.
public sealed class InputRebinds
{
    private static readonly InputContext[] Contexts = { InputContext.Gameplay, InputContext.UI, InputContext.Editor, InputContext.Console };

    private readonly ActionRegistry _actions;
    private readonly RecordStore _records;
    private readonly string? _file;
    private bool _captureFresh;

    internal InputRebinds(ActionRegistry actions, RecordStore records, string? file)
    {
        _actions = actions;
        _records = records;
        _file = file;
        records.Reloaded += () => Version++;
    }

    // Where the player's rebinds are written; null when the app keeps none (a test, a tool).
    public string? FilePath => _file;

    // The client sets it: null when the input may drive the action, else why not (an unknown key name).
    public Func<ActionInfo, InputBinding, string?>? Validator { get; set; }

    // Bumped whenever bindings or the capture state change, so a screen rebuilds only then.
    public int Version { get; private set; }

    // ---- What is bound ----

    // The contexts that have any map.
    public IEnumerable<InputContext> ContextsInUse => Contexts.Where(c => MapsOf(c).Any());

    // The actions a context's maps name, in the order they first appear (an action with no bindings left
    // is still named, so it can be bound again).
    public IReadOnlyList<ActionInfo> ActionsIn(InputContext context)
    {
        var seen = new List<ActionInfo>();
        foreach (var (_, map) in MapsOf(context))
            foreach (string name in map.Actions.Keys)
                if (_actions.TryGet(name, out var info) && !seen.Contains(info)) seen.Add(info);
        return seen;
    }

    // What the action has in the context now: every map's list, in map order, as the client builds them.
    public IReadOnlyList<InputBinding> Bindings(InputContext context, string action)
    {
        var all = new List<InputBinding>();
        foreach (var (_, map) in MapsOf(context))
            if (map.Actions.TryGetValue(action, out var list)) all.AddRange(list);
        return all;
    }

    public bool IsOverridden(InputContext context, string action) =>
        Overrides().ContainsKey((context, Canonical(action)));

    // The actions in the context that an input triggers.
    public IReadOnlyList<string> ActionsFor(InputContext context, InputBinding input)
    {
        var found = new List<string>();
        foreach (var info in ActionsIn(context))
            if (Bindings(context, info.Name).Any(b => b.Overlaps(input))) found.Add(info.Name);
        return found;
    }

    // ---- Changing it ----

    // Adds the input to the action (`bind <input> <action>`); an input it already has is no change.
    public RebindResult Bind(InputContext context, string action, InputBinding input, bool replaceConflicts = false) =>
        Note(Change(context, action, input, replaceConflicts, (list, a) =>
        {
            if (list.Any(b => b.SameInput(input))) return list;
            list.Add(input.Clone());
            return list;
        }));

    // Sets the action's input of this kind (what a controls screen does): the first keyboard-or-mouse
    // binding is replaced by a key or mouse input, the first gamepad one by a gamepad input, the others
    // of that kind go; with none of the kind, it is added.
    public RebindResult Rebind(InputContext context, string action, InputBinding input, bool replaceConflicts = false) =>
        Note(Change(context, action, input, replaceConflicts, (list, a) =>
        {
            int at = list.FindIndex(b => b.IsGamepad == input.IsGamepad && b.Composite == null);
            var next = list.Where(b => b.IsGamepad != input.IsGamepad || b.Composite != null).ToList();
            if (at < 0) next.Add(input.Clone());
            else next.Insert(Math.Min(at, next.Count), input.Clone());
            return next;
        }));

    // Takes the input off the action; with no action, off every action in the context that has it.
    public RebindResult Unbind(InputContext context, string? action, InputBinding input) => Note(UnbindCore(context, action, input));

    private RebindResult UnbindCore(InputContext context, string? action, InputBinding input)
    {
        var changes = new Dictionary<(InputContext, string), List<InputBinding>?>();
        var names = action != null
            ? new[] { Canonical(action) }
            : ActionsIn(context).Select(i => i.Name).ToArray();
        foreach (string name in names)
        {
            if (!_actions.TryGet(name, out _)) return Invalid($"no action named '{name}'");
            var list = Bindings(context, name).Select(b => b.Clone()).ToList();
            if (list.RemoveAll(b => b.SameInput(input)) > 0) changes[(context, name)] = list;
        }
        if (changes.Count == 0) return new RebindResult(RebindStatus.Unchanged, $"{input.DisplayName} is not bound{(action != null ? $" to {action}" : "")} in {context}");
        return Apply(changes, $"{input.DisplayName} unbound{(action != null ? $" from {action}" : "")} in {context}");
    }

    // Back to the game's own bindings: one action, every action of a context, or all of them.
    public RebindResult Reset(InputContext? context = null, string? action = null) => Note(ResetCore(context, action));

    private RebindResult ResetCore(InputContext? context, string? action)
    {
        var overrides = Overrides();
        var changes = new Dictionary<(InputContext, string), List<InputBinding>?>();
        foreach (var key in overrides.Keys)
            if ((context == null || key.Item1 == context) &&
                (action == null || string.Equals(key.Item2, action, StringComparison.OrdinalIgnoreCase)))
                changes[key] = null;
        if (changes.Count == 0) return new RebindResult(RebindStatus.Unchanged, "nothing was rebound");
        return Apply(changes, $"{changes.Count} action(s) back to their defaults");
    }

    // ---- Capture next input ----

    public bool Capturing { get; private set; }
    public InputContext CaptureContext { get; private set; }
    public string CaptureAction { get; private set; } = "";
    public PendingRebind? Pending { get; private set; }

    // What the last change said (a command's, a capture's, a screen's), for a screen to show; ResultVersion
    // counts them.
    public RebindResult LastResult { get; private set; }
    public int ResultVersion { get; private set; }

    private RebindResult Note(RebindResult result)
    {
        LastResult = result;
        ResultVersion++;
        Version++;
        return result;
    }

    public bool BeginCapture(InputContext context, string action)
    {
        if (!_actions.TryGet(action, out var info) || info.Kind != ActionKind.Button) return false;
        Capturing = true;
        CaptureContext = context;
        CaptureAction = info.Name;
        Pending = null;
        _captureFresh = true;
        Version++;
        return true;
    }

    public void CancelCapture()
    {
        if (!Capturing && Pending == null) return;
        Capturing = false;
        Pending = null;
        Version++;
    }

    // The client calls this once a frame while Capturing and skips the frame if true: the press that
    // started a capture (a click on its button) is still down and must not be what it captures.
    public bool TakeFresh()
    {
        bool fresh = _captureFresh;
        _captureFresh = false;
        return fresh;
    }

    // The player pressed this. The capture ends either way; a conflict waits in `Pending`.
    public RebindResult Offer(InputBinding input)
    {
        if (!Capturing) return new RebindResult(RebindStatus.Unchanged, "not capturing");
        Capturing = false;
        var result = Rebind(CaptureContext, CaptureAction, input);
        if (result.Status == RebindStatus.Conflict)
            Pending = new PendingRebind(CaptureContext, CaptureAction, input, result.Conflicts ?? Array.Empty<string>(), result.CanReplace);
        Version++;
        return result;
    }

    // Answers a conflict: replace takes the input from the other actions (when that is allowed).
    public RebindResult Resolve(bool replace)
    {
        if (Pending is not { } pending) return new RebindResult(RebindStatus.Unchanged, "no conflict to resolve");
        Pending = null;
        var result = replace && pending.CanReplace
            ? Rebind(pending.Context, pending.Action, pending.Input, replaceConflicts: true)
            : Note(new RebindResult(RebindStatus.Unchanged, "kept as it was"));
        Version++;
        return result;
    }

    // ---- Machinery ----

    private RebindResult Change(InputContext context, string action, InputBinding input, bool replaceConflicts,
                                Func<List<InputBinding>, ActionInfo, List<InputBinding>> edit)
    {
        if (!_actions.TryGet(action, out var info)) return Invalid($"no action named '{action}'");
        if (CheckShape(info, input) is { } why) return Invalid(why);

        var conflicts = new List<string>();
        bool canReplace = true;
        foreach (var other in ActionsIn(context))
        {
            if (other == info) continue;
            foreach (var b in Bindings(context, other.Name))
                if (b.Overlaps(input))
                {
                    if (!conflicts.Contains(other.Name)) conflicts.Add(other.Name);
                    if (b.Composite != null || other.Kind != ActionKind.Button) canReplace = false;
                }
        }
        if (conflicts.Count > 0 && (!replaceConflicts || !canReplace))
            return new RebindResult(RebindStatus.Conflict,
                $"{input.DisplayName} is already used by {string.Join(", ", conflicts)} in {context}", conflicts, canReplace);

        var current = Bindings(context, info.Name).Select(b => b.Clone()).ToList();
        var edited = edit(current, info);
        var changes = new Dictionary<(InputContext, string), List<InputBinding>?>();
        if (!SameList(edited, Bindings(context, info.Name)))
            changes[(context, info.Name)] = edited;
        foreach (string name in conflicts)   // replace: the input leaves the others
        {
            var list = Bindings(context, name).Select(b => b.Clone()).ToList();
            list.RemoveAll(b => b.Overlaps(input));
            changes[(context, name)] = list;
        }
        if (changes.Count == 0) return new RebindResult(RebindStatus.Unchanged, $"{info.Name} already has {input.DisplayName} in {context}");
        return Apply(changes, $"{info.Name}: {string.Join(", ", edited.Select(b => b.DisplayName))} in {context}");
    }

    private string? CheckShape(ActionInfo action, InputBinding input)
    {
        int set = (input.Key != null ? 1 : 0) + (input.Mouse != null ? 1 : 0) + (input.Gamepad != null ? 1 : 0) + (input.Composite != null ? 1 : 0);
        if (set != 1) return "a binding needs exactly one of key, mouse, gamepad, composite";
        if (Validator?.Invoke(action, input) is { } error) return error;
        if (Validator == null && action.Kind == ActionKind.Button && input.Composite != null)
            return $"{input} can't drive the {action.Kind} action {action.Name}";
        return null;
    }

    private static bool SameList(List<InputBinding> a, IReadOnlyList<InputBinding> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.SameInput(p.Second));

    private RebindResult Invalid(string why)
    {
        var result = new RebindResult(RebindStatus.Invalid, why);
        Log.Warn(LogCat.Input, why);
        return result;
    }

    // The maps of a context, in the order the client builds from them, without the player's own.
    private IEnumerable<(RecordId Id, InputMapRecord Map)> MapsOf(InputContext context)
    {
        foreach (var id in _records.Ids("input_map"))
            if (_records.TryGet(id, out InputMapRecord map) && map.Context == context)
                yield return (id, map);
    }

    private static RecordId UserMap(InputContext context) => new(UserInputMount.Namespace, "rebinds_" + context.ToString().ToLowerInvariant());

    private string Canonical(string action) => _actions.TryGet(action, out var info) ? info.Name : action;

    // The player's lists as the file has them now: the actions of the `user:rebinds_<context>` maps.
    private Dictionary<(InputContext, string), List<InputBinding>> Overrides()
    {
        var overrides = new Dictionary<(InputContext, string), List<InputBinding>>();
        foreach (var context in Contexts)
            if (_records.TryGet(UserMap(context), out InputMapRecord map))
                foreach (var (name, list) in map.Actions)
                    overrides[(context, Canonical(name))] = list.Select(b => b.Clone()).ToList();
        return overrides;
    }

    // Writes the file with `changes` applied (a null list removes an action's override), then reloads
    // records so the bindings are rebuilt. Nothing is changed if the file cannot be written.
    private RebindResult Apply(Dictionary<(InputContext, string), List<InputBinding>?> changes, string message)
    {
        if (_file == null)
            return new RebindResult(RebindStatus.CannotSave, "there is no user folder to keep the bindings in");
        var next = Overrides();
        foreach (var (key, list) in changes)
        {
            if (list == null) next.Remove(key);
            else next[key] = list;
        }
        try
        {
            string? dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string temp = _file + ".tmp";
            System.IO.File.WriteAllText(temp, Serialise(next).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            System.IO.File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(LogCat.Input, $"Couldn't write {_file}: {ex.Message}");
            return new RebindResult(RebindStatus.CannotSave, $"could not write {_file}: {ex.Message}");
        }
        _records.Reload();
        Log.Info(LogCat.Input, message);
        return new RebindResult(RebindStatus.Applied, message);
    }

    // One definition per context for what the player chose, and a patch emptying that action in every map of
    // the context that bound it, so the player's list is the whole list.
    private JsonArray Serialise(Dictionary<(InputContext, string), List<InputBinding>> overrides)
    {
        var array = new JsonArray();
        foreach (var context in Contexts)
        {
            var mine = overrides.Where(kv => kv.Key.Item1 == context).OrderBy(kv => kv.Key.Item2, StringComparer.OrdinalIgnoreCase).ToList();
            if (mine.Count == 0) continue;

            var actions = new JsonObject();
            foreach (var ((_, name), list) in mine)
                actions[name] = new JsonArray(list.Select(b => (JsonNode)b.ToJson()).ToArray());
            array.Add(new JsonObject
            {
                ["type"] = "input_map",
                ["id"] = UserMap(context).ToString(),
                ["context"] = context.ToString(),
                ["actions"] = actions,
            });

            foreach (var (id, map) in MapsOf(context))
            {
                if (id == UserMap(context)) continue;
                var emptied = new JsonObject();
                foreach (var ((_, name), _) in mine)
                    foreach (var key in map.Actions.Keys)
                        if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                            emptied[key] = new JsonArray();
                if (emptied.Count == 0) continue;
                array.Add(new JsonObject
                {
                    ["type"] = "input_map",
                    ["id"] = id.ToString(),
                    ["patch"] = true,
                    ["actions"] = emptied,
                });
            }
        }
        return array;
    }

    // ---- Console ----

    internal void RegisterCommands(CVarRegistry cvars)
    {
        cvars.RegisterCommand("bind", CVarFlags.None,
            "bind <input> <action> [context]: add a key (E, key:E, mouse:Right, pad:A) to an action; saved in user://input.json.", a =>
        {
            if (a.Count < 2) throw new ArgumentException("usage: bind <input> <action> [context]");
            if (!InputBinding.TryParse(a[0], out var input, out string error)) throw new ArgumentException(error);
            Print(Bind(ContextOf(a, 2, a[1]), a[1], input));
        });
        cvars.RegisterCommand("unbind", CVarFlags.None,
            "unbind <input> [action] [context]: take an input off an action (or every action in the context); saved in user://input.json.", a =>
        {
            if (a.Count < 1) throw new ArgumentException("usage: unbind <input> [action] [context]");
            if (!InputBinding.TryParse(a[0], out var input, out string error)) throw new ArgumentException(error);
            string? action = null;
            InputContext? context = null;
            for (int i = 1; i < a.Count; i++)
                if (Enum.TryParse(a[i], true, out InputContext parsed) && Enum.IsDefined(parsed)) context = parsed;
                else action = a[i];
            Print(Unbind(context ?? (action != null ? ContextOf(a, int.MaxValue, action) : InputContext.Gameplay), action, input));
        });
        cvars.RegisterCommand("bind_reset", CVarFlags.None,
            "bind_reset [action|all] [context]: put an action (or everything) back to the game's own bindings.", a =>
        {
            string? action = null;
            InputContext? context = null;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Equals("all", StringComparison.OrdinalIgnoreCase)) continue;
                else if (Enum.TryParse(a[i], true, out InputContext parsed) && Enum.IsDefined(parsed)) context = parsed;
                else action = a[i];
            if (a.Count == 0) throw new ArgumentException("usage: bind_reset <action|all> [context]");
            Print(Reset(context, action));
        });
    }

    // The context an action's bind goes to when none is named: Gameplay if it has bindings there (or has none
    // anywhere), else the first context that has it.
    private InputContext ContextOf(ConsoleArgs a, int index, string action)
    {
        if (index < a.Count)
        {
            if (Enum.TryParse(a[index], true, out InputContext named) && Enum.IsDefined(named)) return named;
            throw new ArgumentException($"unknown context '{a[index]}' (Gameplay, UI, Editor, Console)");
        }
        foreach (var context in Contexts)
            if (ActionsIn(context).Any(i => string.Equals(i.Name, action, StringComparison.OrdinalIgnoreCase)))
                return context;
        return InputContext.Gameplay;
    }

    private static void Print(RebindResult result)
    {
        if (result.Ok) Log.Info(LogCat.Console, "  " + result.Message);
        else throw new InvalidOperationException(result.Message);
    }
}
