#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Sage.UI;

// The controls screen's view-model (issue #328; docs/design/08 §3.2, §14): every button action of a
// context with the inputs it has now, a Rebind button that waits for the next key, mouse button or pad
// button the player presses (the engine's capture mode, InputRebinds.BeginCapture, which the client feeds),
// a Reset for an action the player has changed and one for all of them, and a tab per context. It is a view
// of `Engine.Rebinds` and writes nothing itself: a change goes to `user://input.json` there, the same
// calls the console's `bind`, `unbind` and `bind_reset` make, and is in force at once.
//
// An input another action in the context already uses is a conflict: the capture ends, the screen says
// which action has it, and the player replaces (it leaves the other) or keeps. An axis action's composite
// (WASD on Move) is never taken, only reported. Back cancels a capture or a conflict before it closes
// the screen.
//   screen rpg:controls — layout rpg:controls (the RPG kit's content); a game makes its own over `ui_controls`.
//
// Rows are made when something changes (a capture, a reset, a console `bind`), not every frame.
[ViewModel("ui_controls")]
[System.Diagnostics.CodeAnalysis.Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class ControlsView : IViewModel
{
    public const string RebindButton = "rebind", ResetButton = "reset", ResetAllButton = "resetAll",
                        ReplaceButton = "replace", KeepButton = "keep", TabPrefix = "tab";

    public sealed class Row
    {
        public string Action { get; internal set; } = "";

        // The inputs, "E, Mouse Left"; "-" when none.
        public string Bindings { get; internal set; } = "-";
        public bool Overridden { get; internal set; }
        public bool Capturing { get; internal set; }
        public string RebindLabel => Capturing ? "@rpg.controls.press" : "@rpg.controls.rebind";
    }

    private readonly List<Row> _pool = new();
    private InputRebinds? _rebinds;
    private int _version = -1, _seenResult = -1;
    private InputContext _context = InputContext.Gameplay;

    public List<Row> Rows { get; } = new();
    public int RowCount => Rows.Count;
    public bool NoRows => Rows.Count == 0;

    // The context shown, and the key of its name (@rpg.controls.ctx_Gameplay).
    public InputContext Context => _context;
    public string ContextLabel => "@rpg.controls.ctx_" + _context;

    // Waiting for a press, and for which action.
    public bool Capturing { get; private set; }
    public string CaptureAction { get; private set; } = "";

    // A capture ended in a conflict: who has the input, and whether the player may take it.
    public bool ConflictPending { get; private set; }
    public bool CanReplace { get; private set; }
    public string ConflictInput { get; private set; } = "";
    public string ConflictOthers { get; private set; } = "";

    // What the last change did, as a key (@rpg.controls.…); empty before anything was changed.
    public string Message { get; private set; } = "";
    public string MessageDetail { get; private set; } = "";
    public bool HasMessage => Message.Length > 0;

    public void Refresh(in UiBindContext context)
    {
        if (context.World?.Engine is not { } engine) return;
        var rebinds = engine.Rebinds;
        if (!ReferenceEquals(_rebinds, rebinds))
        {
            _rebinds = rebinds;
            _version = -1;
            _seenResult = rebinds.ResultVersion;   // what happened before the screen opened is not its news
        }
        if (_version == rebinds.Version) return;
        _version = rebinds.Version;
        Rebuild(rebinds);
    }

    public bool Activate(Widget widget, in UiBindContext context)
    {
        if (context.World?.Engine is not { } engine) return false;
        var rebinds = engine.Rebinds;
        string name = widget.Name ?? "";
        if (name.StartsWith(TabPrefix, StringComparison.Ordinal) && Enum.TryParse(name.AsSpan(TabPrefix.Length), true, out InputContext tab))
        {
            rebinds.CancelCapture();
            _context = tab;
            _version = -1;
            return true;
        }
        switch (name)
        {
            case ResetAllButton: rebinds.Reset(); return true;
            case ReplaceButton: rebinds.Resolve(replace: true); return true;
            case KeepButton: rebinds.Resolve(replace: false); return true;
        }
        if (UiScreen.RowOf(widget) is not Row row) return false;
        switch (name)
        {
            case RebindButton:
                if (rebinds.Capturing && rebinds.CaptureAction == row.Action) rebinds.CancelCapture();
                else rebinds.BeginCapture(_context, row.Action);
                return true;
            case ResetButton:
                rebinds.Reset(_context, row.Action);
                return true;
        }
        return false;
    }

    // Back puts down a capture or a conflict first; the next Back closes the screen.
    public bool Back(in UiBindContext context)
    {
        if (context.World?.Engine is not { } engine) return false;
        var rebinds = engine.Rebinds;
        if (!rebinds.Capturing && rebinds.Pending == null) return false;
        rebinds.CancelCapture();
        return true;
    }

    private void Rebuild(InputRebinds rebinds)
    {
        Rows.Clear();
        int used = 0;
        foreach (var action in rebinds.ActionsIn(_context))
        {
            if (action.Kind != ActionKind.Button) continue;   // an axis (Move, Look) is not one input to press
            if (used == _pool.Count) _pool.Add(new Row());
            var row = _pool[used++];
            var bindings = rebinds.Bindings(_context, action.Name);
            row.Action = action.Name;
            row.Bindings = bindings.Count == 0 ? "-" : string.Join(", ", bindings.Select(b => b.DisplayName));
            row.Overridden = rebinds.IsOverridden(_context, action.Name);
            row.Capturing = rebinds.Capturing && rebinds.CaptureContext == _context && rebinds.CaptureAction == action.Name;
            Rows.Add(row);
        }

        Capturing = rebinds.Capturing;
        CaptureAction = rebinds.CaptureAction;
        var pending = rebinds.Pending;
        ConflictPending = pending != null;
        CanReplace = pending is { CanReplace: true };
        ConflictInput = pending?.Input.DisplayName ?? "";
        ConflictOthers = pending == null ? "" : string.Join(", ", pending.Conflicts);

        if (rebinds.ResultVersion != _seenResult)
        {
            _seenResult = rebinds.ResultVersion;
            var result = rebinds.LastResult;
            (Message, MessageDetail) = result.Status switch
            {
                RebindStatus.Applied => ("@rpg.controls.saved", ""),
                RebindStatus.Conflict => (result.CanReplace ? "" : "@rpg.controls.conflict_locked", string.Join(", ", result.Conflicts ?? Array.Empty<string>())),   // a replaceable one is asked by the conflict line
                RebindStatus.Invalid => ("@rpg.controls.invalid", result.Message),
                RebindStatus.CannotSave => ("@rpg.controls.cannot_save", ""),
                _ => ("", ""),
            };
        }
    }
}
