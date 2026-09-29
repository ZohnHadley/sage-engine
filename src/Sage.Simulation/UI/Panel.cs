#nullable enable
using System;
using System.Collections.Generic;

namespace sage_engine;

// What a screen is made of, as data (docs/design/13 §3, TODO F38, R17).
//
// The engine draws no screens: what a spellbook looks like is the game's business and which UI
// library draws it is still an open decision (ARCHITECTURE D8). What the engine *can* own is the
// answer to "what would a spellbook have to show?" — a list of rows with names, details, and whether
// each one can be used right now — because every one of those is a simulation question.
//
// This is the same rule the rest of the engine already follows. The renderer gets a `RenderSnapshot`
// rather than reading components; the client gets `CueTriggered` rather than being called; a screen
// gets a `Panel`. The payoff is the same too: a **headless test can assert what a screen would show**,
// which is worth more than it sounds — "the spellbook greys out the spell you cannot afford, and says
// why" is a test here, and a screenshot comparison otherwise.
//
// A panel is **retained and rebuilt on demand**, not per frame: a screen rebuilds it when it opens and
// after the player does something. Rebuilding every frame would allocate a string per row per frame,
// which the frame budget does not allow (02 §4.6).

// One line of a screen. `Id` is what to act on, and everything else is what to show.
public readonly record struct PanelRow(
    RecordId Id,
    string Name,        // what to call it
    string Detail,      // the second column: "12 mana", "3 kg", "×4"
    int Count,          // a stack size, or 1
    bool Selected,      // readied, equipped, worn — the thing a screen ticks
    bool Enabled,       // usable right now
    string Reason)      // why not, in words a screen can show, when Enabled is false
{
    public static PanelRow Of(RecordId id, string name, string detail = "", int count = 1,
                             bool selected = false, bool enabled = true, string reason = "") =>
        new(id, name, detail, count, selected, enabled, reason);
}

// A titled list of rows. Reused between rebuilds so a screen that is open does not allocate a list a
// frame; the rows themselves hold strings, which is why rebuilding is something a screen *asks for*.
public sealed class Panel
{
    private readonly List<PanelRow> _rows = new();

    public string Title { get; private set; } = "";

    public IReadOnlyList<PanelRow> Rows => _rows;

    public int Count => _rows.Count;

    // Bumped on every rebuild, so a screen that caches layout knows when to redo it.
    public int Version { get; private set; }

    // What the panel is about — the entity whose spellbook or inventory this is, when it has one.
    public Friflo.Engine.ECS.Entity Subject { get; private set; }

    // Set when the subject cannot have this panel at all: not "you are carrying nothing" but "this is
    // not something that carries anything". A screen shows the first as an empty list and the second
    // as a mistake.
    public string Problem { get; private set; } = "";

    public PanelRow this[int index] => _rows[index];

    public bool TryFind(RecordId id, out PanelRow row)
    {
        foreach (var candidate in _rows)
            if (candidate.Id == id) { row = candidate; return true; }
        row = default;
        return false;
    }

    // The row a screen would act on by default: what is already selected, or the first usable one.
    public bool TryCurrent(out PanelRow row)
    {
        foreach (var candidate in _rows)
            if (candidate.Selected) { row = candidate; return true; }
        foreach (var candidate in _rows)
            if (candidate.Enabled) { row = candidate; return true; }
        row = default;
        return false;
    }

    // ---- building (for panel builders; a screen only reads) -----------------------------------------

    public Panel Begin(string title, Friflo.Engine.ECS.Entity subject = default, string problem = "")
    {
        _rows.Clear();
        Title = title;
        Subject = subject;
        Problem = problem;
        Version++;
        return this;
    }

    public Panel Add(in PanelRow row)
    {
        _rows.Add(row);
        return this;
    }

    public Panel Sort(Comparison<PanelRow> order)
    {
        _rows.Sort(order);
        return this;
    }

    // Writes the panel to the console, which is what the engine has instead of screens until F38.
    // It is also the dogfooding: `spells` and `inv` print *this*, so the console and a future screen
    // cannot disagree about what you are carrying.
    public void Log(LogCat category)
    {
        if (Problem.Length > 0)
        {
            sage_engine.Log.Info(category, Problem);
            return;
        }

        sage_engine.Log.Info(category, Title);
        if (_rows.Count == 0)
        {
            sage_engine.Log.Info(category, "  (nothing)");
            return;
        }

        foreach (var row in _rows)
        {
            string mark = row.Selected ? "*" : " ";
            string count = row.Count > 1 ? $" ×{row.Count}" : "";
            string why = row.Enabled ? "" : $"   — {row.Reason}";
            sage_engine.Log.Info(category, $" {mark} {row.Name,-24}{count,-5} {row.Detail,-16}{why}");
        }
    }
}
