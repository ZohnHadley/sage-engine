#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Simulation;

// **The visual logger** (docs/design/02 §9, Unreal's idea; issue #300): debug shapes that are *kept*.
//
// DebugDraw shows what is true this tick and forgets it. That is the wrong tool for "why did it path
// there?", because by the time you are asking, the tick that decided it has gone. A visual log records
// each shape with the tick it was drawn on and a category ("ai", "physics", a game's own), keeps the last
// `vlog_ticks` ticks of them, and shows one tick at a time — the newest (live), or any earlier one you scrub
// to (`vlog_at`, `vlog_step`, the editor's Visual log window). A shape can carry a line of text and the
// entity it is about, which `vlog_list <tick>` and the window print.
//
// One per world, like DebugDraw, so `world.VisualLog()` needs no null check. Nothing is recorded unless
// `vlog_record` is on: every call is then one bool test, and a caller with text to build checks
// `Recording` first so it formats nothing. Recording works headless (a server, a test); drawing goes
// through the client's debug lines (`r_debugdraw`) like everything else drawn for a developer.
public enum VisualShape : byte { Line, Arrow, Cross, Box, Sphere, Capsule, Circle, Cone }

// One recorded shape. A and B are its points (a line's ends, a box's centre and half extents, a cone's apex
// and B.X its yaw); Size is a radius, a cross's size or a cone's range; Extra a capsule's height or a cone's
// angle in degrees.
public readonly record struct VisualLogEntry(long Tick, string Category, VisualShape Shape, Vector3 A, Vector3 B,
    float Size, float Extra, uint Colour, string? Text, Entity Entity);

public sealed class VisualLog
{
    public const int DefaultHistoryTicks = 600;   // ten seconds at 60 Hz
    private const int MaxEntriesPerTick = 4096;   // a runaway loop costs its tick's shapes, not the heap

    private readonly List<VisualLogEntry> _entries = new(256);
    private int _head;                 // first entry still in the window; compacted when it passes half
    private long _tick;                // the tick shapes are recorded on (BeginTick)
    private int _thisTick;             // shapes recorded on it
    private bool _warned;
    private readonly HashSet<string> _categories = new(StringComparer.Ordinal);
    private readonly List<string> _sortedCategories = new();
    private readonly HashSet<string> _shown = new(StringComparer.OrdinalIgnoreCase);
    private bool _showAll = true;
    private readonly DebugDraw _lines = new() { Enabled = true };   // decomposes a shape into segments

    // The cvars, in a world an engine made: Recording and HistoryTicks follow them at each tick, the shown
    // categories whenever they are read.
    internal VisualLogCVars? Settings { get; set; }
    private string? _appliedShow;

    // On while `vlog_record` is (the world sets it each tick); a test or a tool may set it directly.
    public bool Recording { get; set; }

    // How many ticks of shapes are kept (`vlog_ticks`).
    public int HistoryTicks { get; set; } = DefaultHistoryTicks;

    // The tick being shown: null follows the newest (live).
    public long? ScrubTick { get; set; }

    public int Count => _entries.Count - _head;
    public long OldestTick => Count > 0 ? _entries[_head].Tick : _tick;
    public long NewestTick => Count > 0 ? _entries[^1].Tick : _tick;

    // The tick drawn: the scrubbed one, held inside what is kept, or the newest.
    public long ShownTick => ScrubTick is { } at ? Math.Clamp(at, OldestTick, NewestTick) : NewestTick;

    // Every category recorded so far, sorted.
    public IReadOnlyList<string> Categories => _sortedCategories;

    // ---- recording ------------------------------------------------------------------------------

    public void Line(string category, Vector3 from, Vector3 to, uint colour = DebugColour.White, string? text = null, Entity entity = default) =>
        Add(category, VisualShape.Line, from, to, 0f, 0f, colour, text, entity);

    public void Arrow(string category, Vector3 from, Vector3 to, uint colour = DebugColour.White, string? text = null, Entity entity = default) =>
        Add(category, VisualShape.Arrow, from, to, 0f, 0f, colour, text, entity);

    // A point, with what is to be said about it: the shape that carries most text.
    public void Point(string category, Vector3 at, string? text = null, uint colour = DebugColour.White, Entity entity = default, float size = 0.2f) =>
        Add(category, VisualShape.Cross, at, Vector3.Zero, size, 0f, colour, text, entity);

    public void Box(string category, Vector3 center, Vector3 halfExtents, uint colour = DebugColour.White, string? text = null, Entity entity = default) =>
        Add(category, VisualShape.Box, center, halfExtents, 0f, 0f, colour, text, entity);

    public void Sphere(string category, Vector3 center, float radius, uint colour = DebugColour.White, string? text = null, Entity entity = default) =>
        Add(category, VisualShape.Sphere, center, Vector3.Zero, radius, 0f, colour, text, entity);

    public void Capsule(string category, Vector3 feet, float radius, float height, uint colour = DebugColour.White, string? text = null, Entity entity = default) =>
        Add(category, VisualShape.Capsule, feet, Vector3.Zero, radius, height, colour, text, entity);

    // A horizontal ring, for a range.
    public void Circle(string category, Vector3 center, float radius, uint colour = DebugColour.White, string? text = null, Entity entity = default) =>
        Add(category, VisualShape.Circle, center, Vector3.Zero, radius, 0f, colour, text, entity);

    // A sight cone or an attack arc on the ground: `yaw` in radians, `degrees` across, `range` long.
    public void Cone(string category, Vector3 at, float yaw, float degrees, float range, uint colour = DebugColour.White, string? text = null, Entity entity = default) =>
        Add(category, VisualShape.Cone, at, new Vector3(yaw, 0f, 0f), range, degrees, colour, text, entity);

    private void Add(string category, VisualShape shape, Vector3 a, Vector3 b, float size, float extra, uint colour, string? text, Entity entity)
    {
        if (!Recording) return;
        if (_thisTick >= MaxEntriesPerTick)
        {
            if (!_warned) { Log.Warn(LogCat.Render, $"Visual log is over {MaxEntriesPerTick} shapes this tick; the rest are dropped"); _warned = true; }
            return;
        }
        _thisTick++;
        if (_categories.Add(category))
        {
            _sortedCategories.Add(category);
            _sortedCategories.Sort(StringComparer.Ordinal);
        }
        _entries.Add(new VisualLogEntry(_tick, category, shape, a, b, size, extra, colour, text, entity));
    }

    // Called at the start of every tick: shapes from here on are this tick's, and those older than the
    // history are dropped.
    public void BeginTick(long tick)
    {
        if (Settings is { } settings)
        {
            Recording = settings.Record.Value;
            HistoryTicks = settings.Ticks.Value;
        }
        _tick = tick;
        _thisTick = 0;
        _warned = false;
        long keepFrom = tick - Math.Max(1, HistoryTicks) + 1;
        while (_head < _entries.Count && _entries[_head].Tick < keepFrom) _head++;
        if (_head > 0 && _head * 2 >= _entries.Count)
        {
            _entries.RemoveRange(0, _head);
            _head = 0;
        }
    }

    public void Clear()
    {
        _entries.Clear();
        _head = 0;
        ScrubTick = null;
    }

    // ---- what is shown --------------------------------------------------------------------------

    // Which categories are drawn and listed: "*" (or nothing) for all, else a list ("ai physics"). In a world
    // an engine made, `vlog_show` says, and setting it replaces this.
    public void Show(string categories)
    {
        _shown.Clear();
        foreach (var c in categories.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            _shown.Add(c);
        _showAll = _shown.Count == 0 || _shown.Contains("*");
    }

    public bool IsShown(string category)
    {
        if (Settings is { } settings && !ReferenceEquals(settings.Show.Value, _appliedShow))
        {
            _appliedShow = settings.Show.Value;
            Show(_appliedShow);
        }
        return _showAll || _shown.Contains(category);
    }

    // The entries recorded on `tick`, in the order they were, of the shown categories unless `all`.
    public void CollectAt(long tick, List<VisualLogEntry> into, bool all = false)
    {
        for (int i = First(tick); i < _entries.Count && _entries[i].Tick == tick; i++)
            if (all || IsShown(_entries[i].Category)) into.Add(_entries[i]);
    }

    // How many shapes `tick` has, of the shown categories.
    public int CountAt(long tick)
    {
        int n = 0;
        for (int i = First(tick); i < _entries.Count && _entries[i].Tick == tick; i++)
            if (IsShown(_entries[i].Category)) n++;
        return n;
    }

    // The shown tick's shapes as line segments, for the client's debug lines.
    public void DrawShown(List<DebugLine> into)
    {
        if (Count == 0) return;
        long tick = ShownTick;
        for (int i = First(tick); i < _entries.Count && _entries[i].Tick == tick; i++)
        {
            var e = _entries[i];
            if (IsShown(e.Category)) Decompose(_lines, e);
        }
        _lines.CopyTo(into);
        _lines.Clear();
    }

    // The first entry of `tick` (or of the first tick after it): entries are in tick order.
    private int First(long tick)
    {
        int lo = _head, hi = _entries.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_entries[mid].Tick < tick) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    internal static void Decompose(DebugDraw draw, in VisualLogEntry e)
    {
        switch (e.Shape)
        {
            case VisualShape.Line: draw.Line(e.A, e.B, e.Colour); break;
            case VisualShape.Arrow: draw.Arrow(e.A, e.B, e.Colour); break;
            case VisualShape.Cross: draw.Cross(e.A, e.Size, e.Colour); break;
            case VisualShape.Box: draw.Box(e.A, e.B, e.Colour); break;
            case VisualShape.Sphere: draw.Sphere(e.A, e.Size, e.Colour); break;
            case VisualShape.Capsule: draw.Capsule(e.A, e.Size, e.Extra, e.Colour); break;
            case VisualShape.Circle: draw.Circle(e.A, Vector3.UnitX, Vector3.UnitZ, e.Size, e.Colour); break;
            case VisualShape.Cone: draw.Cone(e.A, e.B.X, e.Extra, e.Size, e.Colour); break;
        }
    }
}

public static class VisualLogExtensions
{
    // Every world has one (issue #300), so recording never needs a null check at the call site.
    public static VisualLog VisualLog(this World world) => world.Resources.Get<VisualLog>();
}

// `vlog_record`, `vlog_ticks` and `vlog_show`, registered once with the world console commands; each world reads them
// at the start of its ticks.
internal sealed class VisualLogCVars
{
    public VisualLogCVars(CVarRegistry cvars)
    {
        Record = cvars.Register("vlog_record", false, CVarFlags.DevOnly,
            "Record the visual log (issue #300): AI and physics shapes, and a game's own, kept per tick to scrub through (vlog_at).");
        Ticks = cvars.Register("vlog_ticks", VisualLog.DefaultHistoryTicks, CVarFlags.DevOnly,
            "Ticks of the visual log kept (600 = ten seconds at 60 Hz).", 1, 36000);
        Show = cvars.Register("vlog_show", "*", CVarFlags.DevOnly,
            "Visual log categories drawn and listed: * for all, - for none, else a list (\"ai physics\").");
    }

    public CVar<bool> Record { get; }
    public CVar<int> Ticks { get; }
    public CVar<string> Show { get; }
}
