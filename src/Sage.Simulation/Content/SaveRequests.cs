#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json.Nodes;

namespace Sage.Simulation;

// Who asked for a save (issue 4i-6): the header keeps it ("kind"), and SaveSlot.Kind reads it back, so a
// load menu can show quick-saves and autosaves apart from the saves the player named.
[Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public enum SaveKind { Manual, Quick, Auto }

// Quick-save, autosave and saves asked for during a tick (docs/design/09 "As built (quick-save and
// autosave, issue 4i-6)").
//
// **A save runs at a tick boundary, never mid-tick.** A request made during a world's tick (a system, a
// trigger, the F5 key read in Commands) is queued and runs when that tick ends (World.RunFixed calls
// TickEnded once every phase is done); one made between ticks (the console, a menu) is already at a
// boundary and runs at once. Save and Load called mid-tick become requests the same way.
//
// **What a tick boundary runs, when several things were asked for in one tick:** every save first, in
// the order asked, then at most one load. The saves all describe the tick that just ran, which is what
// whoever asked saw; the load replaces it. Two saves to one slot are one write (the later kind wins); two
// loads are the last one asked for; one autosave at most.
//
// **Autosaves** rotate through `save_autosave_slots` slots (`autosave1`…, default 3), each time taking
// the first that does not exist and then the oldest. They are taken every `save_autosave_interval`
// seconds of simulation time since the last save or load of any kind (0: never on a timer), and at the
// end of the first tick after a scene change (Scenes.Load), so they record the new scene settled and
// once however many worlds changed. `save_autosave 0` turns both off; Autosave() still works, because
// that is a game asking.
public sealed partial class SaveSystem
{
    // The slot quick-save and quick-load use.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public const string QuickSlot = "quick";

    // Autosave slots are this and a number from 1.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public const string AutosavePrefix = "autosave";

    // The engine's actions for them (registered by the Engine, bound to F5 and F9 in engine content's
    // input maps; a game rebinds them with its own `input_map`, decision 7).
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public const string QuickSaveAction = "QuickSave";

    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public const string QuickLoadAction = "QuickLoad";

    // Saves asked for this tick, in order; a null slot is an autosave, whose slot is chosen when it runs.
    private readonly List<(string? Slot, SaveKind Kind, string? Title)> _pendingSaves = new(4);
    private string? _pendingLoad;
    private bool _autosaveDue;
    private float _sinceAutosave;

    private CVar<bool>? _autosave;
    private CVar<int>? _autosaveSlots;
    private CVar<float>? _autosaveInterval;
    private CVar<string>? _quickSlot;
    private CVar<string>? _autosavePrefix;

    // Whether any of the engine's worlds is in the middle of a tick just now.
    internal bool IsMidTick
    {
        get
        {
            var worlds = _engine.Worlds;
            for (int i = 0; i < worlds.Count; i++)
                if (worlds[i].InFixedTick) return true;
            return false;
        }
    }

    // Something is waiting for the end of the tick.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public bool HasPendingRequests => _pendingSaves.Count > 0 || _pendingLoad != null;

    // Asks for a save at the next tick boundary: at the end of this tick when one is running, else now.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public void RequestSave(string slot, SaveKind kind = SaveKind.Manual) => RequestSave(slot, kind, null);

    // The same, with the title a menu shows (issue #285). Between ticks it is snapshotted now and, like
    // one asked for mid-tick, written in the background (`save_background`).
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public void RequestSave(string slot, SaveKind kind, string? title)
    {
        if (IsMidTick) Queue(slot, kind, title);
        else SaveWhilePlaying(slot, kind, title);
    }

    // Asks for a load at the next tick boundary, after any save asked for in the same tick.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public void RequestLoad(string slot)
    {
        if (!IsMidTick) { Load(slot); return; }
        if (_pendingLoad != null && !SameSlot(_pendingLoad, slot))
            Log.Info(LogCat.Save, $"Load '{slot}' asked for after load '{_pendingLoad}' in the same tick: '{slot}' is the one that runs");
        _pendingLoad = slot;
    }

    // F5 and F9, and the `quicksave` and `quickload` commands.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public void QuickSave() => RequestSave(QuickSlotName, SaveKind.Quick);

    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public void QuickLoad() => RequestLoad(QuickSlotName);

    // The slot quick-save and quick-load use (issue #285): `save_quick_slot`, QuickSlot by default. A game
    // with a save per character sets it to that character's slot.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public string QuickSlotName => _quickSlot is { Value: { Length: > 0 } name } && !string.IsNullOrWhiteSpace(name) ? name : QuickSlot;

    // What autosave slots are called before their number (issue #285): `save_autosave_prefix`, AutosavePrefix
    // by default.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public string AutosavePrefixName => _autosavePrefix is { Value: { Length: > 0 } prefix } && !string.IsNullOrWhiteSpace(prefix) ? prefix : AutosavePrefix;

    // An autosave into the next slot of the rotation, at the next tick boundary.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public void Autosave()
    {
        if (IsMidTick) Queue(null, SaveKind.Auto, null);
        else SaveWhilePlaying(NextAutosaveSlot(), SaveKind.Auto, null);
    }

    // The slot the next autosave writes: the first of `autosave1`…`autosaveN` that does not exist, else
    // the one written longest ago (the lower number when two say the same time). A slot of that name the
    // player saved into (not an autosave, issue #285) is theirs and is never overwritten: the rotation
    // passes over it and, when it has to, takes the next number after the last.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public string NextAutosaveSlot()
    {
        int count = Math.Max(1, _autosaveSlots?.Value ?? 3);
        string prefix = AutosavePrefixName;
        string? oldest = null;
        DateTime oldestTime = DateTime.MaxValue;
        int taken = 0;
        for (int i = 1; taken < count && i <= count + 100; i++)
        {
            string name = prefix + i;
            if (!Exists(name)) return name;
            var (kind, when) = SlotInfo(name);
            if (kind != SaveKind.Auto) continue;   // the player's own save, under an autosave's name
            taken++;
            if (when < oldestTime) { oldest = name; oldestTime = when; }
        }
        return oldest ?? prefix + 1;
    }

    private (SaveKind Kind, DateTime When) SlotInfo(string name)
    {
        foreach (var slot in Slots)
            if (SameSlot(slot.Name, name)) return (slot.Kind, slot.SavedUtc);
        return (SaveKind.Auto, default);   // not listed (unreadable): the first to go
    }

    // Removes a slot from disk (a menu's Delete). Returns whether there was one and it went.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public bool Delete(string slot)
    {
        WaitForWrites();   // a save of it still on its way would put it back
        string directory = SlotDirectory(slot);
        if (!Directory.Exists(directory)) { Log.Warn(LogCat.Save, $"No save '{slot}' in {Root} to delete"); return false; }
        try
        {
            Directory.Delete(directory, recursive: true);
            if (Directory.Exists(directory + ".writing")) Directory.Delete(directory + ".writing", recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(LogCat.Save, $"Delete '{slot}' failed: {ex.Message}");
            return false;
        }
        finally { _slots = null; }
        Log.Info(LogCat.Save, $"Deleted '{slot}'");
        return true;
    }

    // ---- the tick boundary --------------------------------------------------------------------------

    // A world's tick has ended (World.RunFixed). Advances the autosave clock on the engine's first world,
    // then runs what was asked for. Allocates nothing when there is nothing to do.
    internal void TickEnded(World world, float dt)
    {
        if (_autosave is { Value: true } && _autosaveInterval is { Value: > 0f } interval
            && _engine.Worlds.Count > 0 && ReferenceEquals(world, _engine.Worlds[0]))
        {
            _sinceAutosave += dt;
            if (_sinceAutosave >= interval.Value)
            {
                _sinceAutosave = 0f;   // and again after a whole interval, even when this one fails
                _autosaveDue = true;
            }
        }
        if (_autosaveDue)
        {
            _autosaveDue = false;
            Queue(null, SaveKind.Auto, null);
        }
        if (!HasPendingRequests || IsMidTick) return;

        // Snapshotted now, at the boundary; written in the background (issue #285). The autosave's slot is
        // chosen first, so choosing it does not wait for a save this boundary just handed to the writer.
        for (int i = 0; i < _pendingSaves.Count; i++)
            if (_pendingSaves[i].Slot == null) _pendingSaves[i] = (NextAutosaveSlot(), _pendingSaves[i].Kind, _pendingSaves[i].Title);
        foreach (var (slot, kind, title) in _pendingSaves)
            SaveWhilePlaying(slot!, kind, title);
        _pendingSaves.Clear();
        if (_pendingLoad is { } load)
        {
            _pendingLoad = null;
            Load(load);
        }
    }

    // Scenes.Load: the world is in another scene. An autosave at the end of the next tick.
    internal void SceneChanged()
    {
        if (_autosave is { Value: true }) _autosaveDue = true;
    }

    private void Queue(string? slot, SaveKind kind, string? title)
    {
        for (int i = 0; i < _pendingSaves.Count; i++)
        {
            var pending = _pendingSaves[i].Slot;
            if (slot == null ? pending == null : pending != null && SameSlot(pending, slot))
            {
                _pendingSaves[i] = (slot, kind, title);
                return;
            }
        }
        _pendingSaves.Add((slot, kind, title));
    }

    private static bool SameSlot(string a, string b) =>
        string.Equals(Sanitise(a), Sanitise(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ---- the header's kind ----------------------------------------------------------------------------

    private static string KindName(SaveKind kind) => kind switch
    {
        SaveKind.Quick => "quick",
        SaveKind.Auto => "auto",
        _ => "manual",
    };

    private static SaveKind ReadKind(JsonObject header) =>
        header["kind"] is JsonValue value && value.TryGetValue(out string? text)
            ? text switch { "quick" => SaveKind.Quick, "auto" => SaveKind.Auto, _ => SaveKind.Manual }
            : SaveKind.Manual;

    // ---- console ----------------------------------------------------------------------------------------

    private void RegisterQuickCommands(CVarRegistry cvars)
    {
        _autosave = cvars.Register("save_autosave", true, CVarFlags.Archive,
            "Autosave on a timer (save_autosave_interval) and when the scene changes; 0 turns both off.");
        _autosaveSlots = cvars.Register("save_autosave_slots", 3, CVarFlags.Archive,
            "How many autosave slots (autosave1…) the autosaves rotate through, reusing the oldest.", 1, 20);
        _autosaveInterval = cvars.Register("save_autosave_interval", 300f, CVarFlags.Archive,
            "Seconds of play between autosaves, counted from the last save or load; 0: none on a timer.", 0f, 86400f);

        _quickSlot = cvars.Register("save_quick_slot", QuickSlot, CVarFlags.Archive,
            "The slot quick-save (F5) and quick-load (F9) use.");
        _autosavePrefix = cvars.Register("save_autosave_prefix", AutosavePrefix, CVarFlags.Archive,
            "What autosave slots are called before their number (autosave1…).");

        cvars.RegisterCommand("quicksave", CVarFlags.None, "Quick-save into the quick slot (save_quick_slot; F5), at the next tick boundary.", _ => QuickSave());
        cvars.RegisterCommand("quickload", CVarFlags.None, "Load the quick slot (save_quick_slot; F9), at the next tick boundary.", _ => QuickLoad());
        cvars.RegisterCommand("autosave", CVarFlags.None, "Autosave now, into the next slot of the rotation.", _ => Autosave());
        cvars.RegisterCommand("save_delete", CVarFlags.None, "save_delete <slot>: delete a save.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "save_delete <slot>"); return; }
            Delete(a[0]);
        });
    }
}
