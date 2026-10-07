#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Sage.UI;

// The mods screen's view-model (phase 4j, issue 4j-6; docs/design/17-modding.md): the mods found, each
// with what the next start will do with it and why, switches to turn one on or off, and buttons to move
// it earlier or later in the load order (the last one wins), and a second tab with the content report's
// conflicts. It is a view of `Engine.ModManager` (ModManager.Next, Enable, Disable, Move) and writes
// nothing itself: the player's choices go to `user://mods.json` through those calls, the same ones the
// console's `mod_enable`, `mod_disable` and `mod_move` make, and apply at the next start (decision 3) —
// the screen says so once something changed.
//   screen rpg:mods — layout rpg:mods (the RPG kit's content); a game makes its own over `ui_mods`.
//
// Rows are shown as the next start would load them: the active in load order, then the switched off,
// then the refused with their reasons. A refusal cannot be switched (the mod is wrong, not off). The
// order is the player's preference, so a dependency, loadAfter or loadBefore can still decide: a move
// that changes nothing says so. The words are string keys a layout shows with `@`: the kit's `rpg`
// table has them (`rpg.mods.*`), and a game's own screen supplies the same keys.
//
// Refresh allocates nothing once warm: the rows are made when the screen opens and after each of its own
// buttons, not every frame (a console `mod_enable` meanwhile shows when the screen is opened again).
[Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // data mods (phase 4j): may change before 1.0
[ViewModel("ui_mods")]
public sealed class ModsView : IViewModel
{
    public const string ToggleButton = "toggle", EarlierButton = "earlier", LaterButton = "later",
                        ModsTab = "tabMods", ConflictsTab = "tabConflicts";

    // The state keys of a row.
    public const string StateActive = "@rpg.mods.state_active", StateOff = "@rpg.mods.state_off", StateRefused = "@rpg.mods.state_refused";

    public sealed class ModRow
    {
        public string Id { get; internal set; } = "";
        public string Name { get; internal set; } = "";
        public string Version { get; internal set; } = "";
        public string Author { get; internal set; } = "";
        public string Description { get; internal set; } = "";

        // A code mod (phase 9, issue #396): its mod.json names assemblies, which run with the game's full
        // trust and are not sandboxed. A layout shows a warning for it.
        public bool ContainsCode { get; internal set; }

        // "1." for the active, in load order; empty otherwise.
        public string Place { get; internal set; } = "";

        // @rpg.mods.state_active / state_off / state_refused, and for a refusal, why.
        public string State { get; internal set; } = "";
        public string Reason { get; internal set; } = "";
        public bool HasReason => Reason.Length > 0;

        // This run loaded it (so a change shows only at the next start).
        public bool LoadedNow { get; internal set; }

        public bool Active { get; internal set; }
        public bool Refused { get; internal set; }

        // Switched off or on; a refused mod has no switch.
        public bool CanToggle => !Refused;
        public string ToggleLabel => Active ? "@rpg.mods.turn_off" : "@rpg.mods.turn_on";
        public bool CanMove => Active;
    }

    public sealed class ConflictRow
    {
        public string Line { get; internal set; } = "";
    }

    private readonly List<ModRow> _pool = new();
    private readonly List<ConflictRow> _conflictPool = new();
    private ModManager? _mods;
    private bool _dirty = true, _conflictsDirty = true;

    public List<ModRow> Mods { get; } = new();
    public List<ConflictRow> Conflicts { get; } = new();

    public int ModCount => Mods.Count;
    public int ConflictCount => Conflicts.Count;
    public bool NoMods => Mods.Count == 0;
    public bool NoConflicts => Conflicts.Count == 0;

    // Which tab: the mods, or the conflicts between them.
    public bool ShowConflicts { get; private set; }
    public bool ShowMods => !ShowConflicts;

    // A change was made that only a restart applies (this screen's, or the console's before it opened).
    public bool RestartNeeded { get; private set; }

    // What the last button did, as a key (@rpg.mods.…) with the mod it was about; empty at first.
    public string Message { get; private set; } = "";
    public string MessageMod { get; private set; } = "";

    public void Refresh(in UiBindContext context)
    {
        if (context.World?.Engine is not { } engine) return;
        if (!ReferenceEquals(_mods, engine.ModManager))
        {
            _mods = engine.ModManager;
            _dirty = _conflictsDirty = true;
        }
        if (_dirty) Rebuild(_mods);
        if (ShowConflicts && _conflictsDirty) RebuildConflicts(engine);
        RestartNeeded = _mods.RestartNeeded;
    }

    public bool Activate(Widget widget, in UiBindContext context)
    {
        if (context.World?.Engine is not { } engine) return false;
        var mods = engine.ModManager;
        switch (widget.Name)
        {
            case ModsTab: ShowConflicts = false; return true;
            case ConflictsTab: ShowConflicts = true; _conflictsDirty = true; return true;
        }
        if (UiScreen.RowOf(widget) is not ModRow row) return false;
        try
        {
            switch (widget.Name)
            {
                case ToggleButton:
                    if (row.Refused) return true;
                    if (row.Active) mods.Disable(row.Id); else mods.Enable(row.Id);
                    Say(row.Active ? "@rpg.mods.turned_off" : "@rpg.mods.turned_on", row.Id);
                    break;
                case EarlierButton: Shift(mods, row, -1); break;
                case LaterButton: Shift(mods, row, +1); break;
                default: return false;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            Say("@rpg.mods.cannot_save", row.Id);   // no user folder, or it can't be written: nothing was changed for the next start
        }
        _dirty = true;
        return true;
    }

    // One place earlier (-1) or later (+1) among the active mods: into the neighbour's place in the
    // player's order. The order is a preference (dependencies still decide), so say when nothing moved.
    private void Shift(ModManager mods, ModRow row, int by)
    {
        int at = Mods.IndexOf(row), to = at + by;
        if (at < 0 || to < 0 || to >= Mods.Count || !Mods[to].Active) { Say("@rpg.mods.at_end", row.Id); return; }
        var order = mods.PlayerOrder();
        mods.Move(row.Id, order.IndexOf(Mods[to].Id) + 1);
        var after = mods.Next().Active;
        Say(after.Count > to && after[to].Id == row.Id ? "@rpg.mods.moved" : "@rpg.mods.pinned", row.Id);
    }

    private void Say(string key, string mod)
    {
        Message = key;
        MessageMod = mod;
    }

    private void Rebuild(ModManager mods)
    {
        _dirty = false;
        var next = mods.Next();
        Mods.Clear();
        int used = 0, place = 0;
        ModRow Row()
        {
            if (used == _pool.Count) _pool.Add(new ModRow());
            return _pool[used++];
        }
        var loaded = mods.Loaded.Active;
        bool LoadedNow(string id) { foreach (var m in loaded) if (m.Id == id) return true; return false; }

        foreach (var mod in next.Active)
        {
            var row = Fill(Row(), mod);
            row.Active = true; row.Refused = false;
            row.State = StateActive; row.Reason = "";
            row.Place = $"{++place}.";
            row.LoadedNow = LoadedNow(mod.Id);
            Mods.Add(row);
        }
        foreach (var mod in next.Disabled)
        {
            var row = Fill(Row(), mod);
            row.Active = false; row.Refused = false;
            row.State = StateOff; row.Reason = ""; row.Place = "";
            row.LoadedNow = LoadedNow(mod.Id);
            Mods.Add(row);
        }
        foreach (var refused in next.Refused)
        {
            var row = Row();
            row.Id = refused.Id; row.Name = refused.Id; row.Version = ""; row.Author = ""; row.Description = ""; row.ContainsCode = false;
            var manifest = mods.Found.FirstOrDefault(m => m.Id == refused.Id);
            if (manifest != null) Fill(row, manifest);
            row.Active = false; row.Refused = true;
            row.State = StateRefused; row.Reason = refused.Reason; row.Place = "";
            row.LoadedNow = false;
            Mods.Add(row);
        }
    }

    private static ModRow Fill(ModRow row, ModManifest mod)
    {
        row.Id = mod.Id;
        row.Name = string.IsNullOrEmpty(mod.Name) ? mod.Id : mod.Name;
        row.Version = mod.Version;
        row.Author = mod.Author;
        row.Description = mod.Description;
        row.ContainsCode = mod.AsksForCode;
        return row;
    }

    // The report of the content as loaded: the conflicts between mods, as it prints them.
    private void RebuildConflicts(Engine engine)
    {
        _conflictsDirty = false;
        Conflicts.Clear();
        int used = 0;
        foreach (var conflict in ContentReport.Build(engine.Records, engine.Vfs).Conflicts)
        {
            if (used == _conflictPool.Count) _conflictPool.Add(new ConflictRow());
            var row = _conflictPool[used++];
            row.Line = conflict.Line;
            Conflicts.Add(row);
        }
    }
}
