#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;

namespace Sage.UI;

// The options screen (issue #339): graphics, audio, controls and gameplay settings, each a `ui_option`
// record over a cvar, so a game adds, moves or takes away a setting with content (a patch, or
// `"disabled": true`) and the screen is a ui_layout over the `ui_options` view-model.
//
//   { "type": "ui_option", "id": "master_volume", "page": "audio", "order": 10, "label": "@rpg.options.master",
//     "kind": "slider", "cvar": "snd_volume", "min": 0, "max": 1, "step": 0.05, "percent": true }
//   { "type": "ui_option", "id": "vsync", "page": "graphics", "label": "@rpg.options.vsync", "kind": "toggle", "cvar": "r_vsync" }
//   { "type": "ui_option", "id": "resolution", "page": "graphics", "label": "@rpg.options.resolution", "kind": "choice",
//     "choices": [ { "label": "1280 x 720", "set": { "vid_width": "1280", "vid_height": "720" } } ] }
//
// A setting is the cvar's: what the screen shows is read from it, and what the player changes is written
// to it on Apply (and so takes effect the way a console `set` does), Archive cvars being saved to
// config.cfg when the game closes, as every archived cvar is. A cvar the running host does not have (a
// headless one has no `snd_volume`) is left off the screen.

[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public enum OptionPage { Graphics, Audio, Controls, Gameplay }

[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public enum OptionKind { Slider, Toggle, Choice }

// Where a choice's list comes from besides `choices`.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public enum OptionChoicesFrom
{
    None,
    // The languages some mount has strings for (Localisation.Languages), each a value of the cvar
    // (`lang`), shown as `@lang.<code>` when a table has it ("English"), else as the code.
    Languages,
}

[Record("ui_option", Plugin = UiModule.Id)]
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class OptionRecord
{
    [Property(Tooltip = "The options page it is on: graphics, audio, controls or gameplay")]
    public OptionPage Page;

    [Property(Tooltip = "Where on its page, lowest first; ties go by id")]
    public int Order;

    [Property(Tooltip = "What it is called; '@ns.key' is a localisation key")]
    public string Label = "";

    [Property(Tooltip = "slider (a number between min and max), toggle (on or off) or choice (one of a list)")]
    public OptionKind Kind;

    [Property(Tooltip = "The cvar it reads and sets; a choice may set others instead, with each choice's `set`")]
    public string Cvar = "";

    // ---- a slider

    [Property(Tooltip = "A slider's lowest value")]
    public float Min;

    [Property(Tooltip = "A slider's highest value")]
    public float Max = 1f;

    [Property(Min = 0, Tooltip = "The values a slider snaps to, from min; 0: any")]
    public float Step;

    [Property(Tooltip = "A slider's value is shown as a percentage of 1 (0.5 is 50%)")]
    public bool Percent;

    [Property(Min = 0, Max = 6, Tooltip = "Decimal places a slider's value is shown with")]
    public int Decimals;

    [Property(Tooltip = "Shown as a slider's value while it is at the cvar's default (\"Camera's own\" for a fov of 0); '@ns.key' is a localisation key")]
    public string DefaultLabel = "";

    // ---- a toggle

    [Property(Tooltip = "The cvar's value when a toggle is on")]
    public string On = "1";

    [Property(Tooltip = "The cvar's value when a toggle is off")]
    public string Off = "0";

    // ---- a choice

    [Property(Tooltip = "A choice's options, in order: each a label and the cvar's value, and/or other cvars it sets (a preset, a resolution)")]
    public List<OptionChoice> Choices = new();

    [Property(Tooltip = "Options a choice takes from the game besides `choices`: languages (each language some mount has strings for)")]
    public OptionChoicesFrom ChoicesFrom;
}

[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class OptionChoice
{
    [Property(Tooltip = "What it is called; '@ns.key' is a localisation key")]
    public string Label = "";

    [Property(Tooltip = "The option's cvar's value for this choice; empty: it sets only `set`")]
    public string Value = "";

    [Property(Tooltip = "Other cvars it sets, by name: a quality preset's, a resolution's width and height. The choice shown is the first whose values are all as the cvars are")]
    public Dictionary<string, string> Set = new();
}

// The view-model of the options screen: a list of rows per page, each a slider, a toggle or a choice
// (the layout shows the one its kind says), staged until Apply. Revert puts back what the cvars say;
// Defaults stages every option's default. Back with changes not applied asks to apply or discard them.
// The `keys` button opens the controls screen (ui_controls, the rebinding screen of #328) beside it: the
// screen record `controls` in the options screen's own namespace.
[ViewModel("ui_options")]
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class OptionsView : IViewModel
{
    public const string ApplyButton = "apply", RevertButton = "revert", DefaultsButton = "defaults", KeysButton = "keys";
    public const string ControlsScreen = "controls";

    public sealed class Row
    {
        internal Row(RecordId id, OptionRecord record, CVar? cvar, IReadOnlyList<OptionChoice> choices)
        {
            Id = id;
            Record = record;
            CVar = cvar;
            OptionChoices = choices;
        }

        public RecordId Id { get; }
        public OptionRecord Record { get; }
        internal CVar? CVar { get; }
        internal IReadOnlyList<OptionChoice> OptionChoices { get; }

        public string Label { get; internal set; } = "";
        public OptionKind Kind => Record.Kind;
        public bool IsSlider => Record.Kind == OptionKind.Slider;
        public bool IsToggle => Record.Kind == OptionKind.Toggle;
        public bool IsChoice => Record.Kind == OptionKind.Choice;
        public float Min => Record.Min;
        public float Max => Record.Max;

        // What the player has made it; written to the cvars on Apply. Bound by the layout.
        public float Value { get; set; }
        public bool Checked { get; set; }
        public int Selected { get; set; } = -1;

        // A choice's options as shown; "Custom" last when the cvars match none of them.
        public List<string> Choices { get; internal set; } = new();

        // A slider's value as text: "50%", "1.25", or the default's label.
        public string ValueText { get; internal set; } = "";

        // Changed and not yet applied.
        public bool Dirty { get; internal set; }

        // What the cvars say now.
        internal float ReadValue;
        internal bool ReadChecked;
        internal int ReadSelected = -1;
        internal int CustomIndex = -1;
    }

    private readonly List<Row> _all = new();
    private int _cvarVersion = -1, _textVersion = -1;
    private object? _records;
    private Localisation? _text;
    private RecordId _controls;
    private bool _placed;

    public List<Row> Graphics { get; } = new();
    public List<Row> Audio { get; } = new();
    public List<Row> Controls { get; } = new();
    public List<Row> Gameplay { get; } = new();

    // The tab shown (bound to the tabs).
    public int Page { get; set; }

    public IReadOnlyList<Row> All => _all;

    public bool HasChanges { get; private set; }

    // The controls screen can be opened from here.
    public bool HasControls => !_controls.IsEmpty;

    // What the last Apply did, as a key (@rpg.options.…); empty before anything was applied.
    public string Message { get; private set; } = "";
    public string MessageDetail { get; private set; } = "";
    public bool HasMessage => Message.Length > 0;

    public Row? Find(string id) => _all.FirstOrDefault(r => r.Id.Name == id || r.Id.ToString() == id);

    public void Refresh(in UiBindContext context)
    {
        if (context.World?.Engine is not { } engine) return;
        context.World.Resources.TryGet(out Localisation? text);
        int textVersion = text?.Version ?? 0;
        if (!ReferenceEquals(_records, engine.Records) || textVersion != _textVersion || !ReferenceEquals(text, _text))
        {
            _records = engine.Records;
            _text = text;
            _textVersion = textVersion;
            Build(engine);
            _cvarVersion = -1;
        }
        if (!_placed) FindControls(engine, context.World);
        if (_cvarVersion == engine.CVars.Version) return;
        _cvarVersion = engine.CVars.Version;
        foreach (var row in _all) Read(engine.CVars, row);
        Recount();
    }

    public void Changed(Widget widget, in UiBindContext context)
    {
        if (UiScreen.RowOf(widget) is not Row row) return;
        if (row.IsSlider) row.Value = Snap(row.Record, row.Value);
        if (row.IsChoice && row.CustomIndex >= 0 && row.Selected == row.CustomIndex) row.Selected = row.ReadSelected;   // "Custom" is what it was
        Update(row);
        Recount();
    }

    public bool Activate(Widget widget, in UiBindContext context)
    {
        if (context.World?.Engine is not { } engine) return false;
        switch (widget.Name)
        {
            case ApplyButton: Apply(engine.CVars); return true;
            case RevertButton: Revert(); return true;
            case DefaultsButton: Defaults(engine.CVars); return true;
            case KeysButton:
                if (_controls.IsEmpty || !context.World.Resources.TryGet(out UiScreenStack? stack) || stack == null) return false;
                stack.Open(_controls, context);
                return true;
        }
        return false;
    }

    // Back with changes not applied asks; otherwise the screen closes.
    public bool Back(in UiBindContext context)
    {
        if (!HasChanges || context.World?.Engine is not { } engine || !context.World.Resources.TryGet(out UiScreenStack? stack) || stack == null)
            return false;
        var layer = stack.Layers.FirstOrDefault(l => ReferenceEquals(l.Screen?.ViewModel, this));
        stack.Confirm("@rpg.options.unsaved_title", "@rpg.options.unsaved", yes =>
        {
            if (yes) Apply(engine.CVars);
            else Revert();
            if (layer != null && !HasChanges) stack.Close(layer);
        }, confirm: "@rpg.options.apply", cancel: "@rpg.options.discard", focusConfirm: true);
        return true;
    }

    // ---- what the buttons do, for a game's code as well as the screen ------------------------------

    // Writes every changed row to its cvars. Returns how many were applied.
    public int Apply(CVarRegistry cvars)
    {
        var problems = new List<string>();
        int applied = 0;
        // A copy: a cvar set here may rebuild the rows on the spot (`lang` reloads the text, and the screen with it).
        foreach (var row in _all.ToArray())
        {
            if (!row.Dirty) continue;
            applied++;
            switch (row.Kind)
            {
                case OptionKind.Slider: Set(row.CVar!, FormatNumber(row.CVar!, row.Value), problems); break;
                case OptionKind.Toggle: Set(row.CVar!, row.Checked ? row.Record.On : row.Record.Off, problems); break;
                case OptionKind.Choice:
                    if (row.Selected < 0 || row.Selected >= row.OptionChoices.Count) break;
                    var choice = row.OptionChoices[row.Selected];
                    if (row.CVar != null && choice.Value.Length > 0) Set(row.CVar, choice.Value, problems);
                    foreach (var (name, value) in choice.Set)
                        if (cvars.Find(name) is { } other) Set(other, value, problems);   // one this host lacks: nothing to set
                    break;
            }
        }
        foreach (var row in _all) Read(cvars, row);
        _cvarVersion = cvars.Version;
        Recount();
        (Message, MessageDetail) = problems.Count > 0 ? ("@rpg.options.invalid", string.Join("; ", problems))
                                 : applied > 0 ? ("@rpg.options.applied", "") : ("", "");
        foreach (string problem in problems) Log.Warn(LogCat.UI, $"options: {problem}");
        return applied;
    }

    // Puts every row back as the cvars are.
    public void Revert()
    {
        foreach (var row in _all)
        {
            row.Value = row.ReadValue;
            row.Checked = row.ReadChecked;
            row.Selected = row.ReadSelected;
            Update(row);
        }
        Recount();
        Message = MessageDetail = "";
    }

    // Stages every option's default: the cvars' defaults, applied with Apply.
    public void Defaults(CVarRegistry cvars)
    {
        foreach (var row in _all)
        {
            switch (row.Kind)
            {
                case OptionKind.Slider: if (TryNumber(row.CVar!.DefaultString, out float value)) row.Value = value; break;
                case OptionKind.Toggle: row.Checked = Same(row.CVar!.DefaultString, row.Record.On); break;
                case OptionKind.Choice:
                    int index = Match(row, cvars, defaults: true);
                    if (index >= 0) row.Selected = index;
                    break;
            }
            Update(row);
        }
        Recount();
    }

    // ---- building and reading ---------------------------------------------------------------------

    private void Build(Engine engine)
    {
        var records = engine.Records;
        var cvars = engine.CVars;
        var drafts = _all.Where(r => r.Dirty).ToDictionary(r => r.Id, r => (r.Value, r.Checked, r.Selected));
        _all.Clear();
        Graphics.Clear();
        Audio.Clear();
        Controls.Clear();
        Gameplay.Clear();

        var options = records.Ids("ui_option").Select(id => (Id: id, Record: records.TryGet(id, out OptionRecord r) ? r : null))
            .Where(o => o.Record != null).OrderBy(o => o.Record!.Order).ThenBy(o => o.Id.ToString(), StringComparer.Ordinal);
        foreach (var (id, record) in options)
        {
            var cvar = record!.Cvar.Length > 0 ? cvars.Find(record.Cvar) : null;
            if (record.Cvar.Length > 0 && cvar == null)
            {
                Log.Once(LogCat.UI, LogLevel.Info, "option-cvar:" + id, $"ui_option {id}: this host has no cvar '{record.Cvar}', so it is not shown");
                continue;
            }
            if (cvar != null && cvar.Flags.HasFlag(CVarFlags.Cheat))
            {
                Log.Once(LogCat.UI, LogLevel.Warn, "option-cheat:" + id, $"ui_option {id}: '{record.Cvar}' is a cheat cvar, which an options screen does not set");
                continue;
            }
            var choices = record.Kind == OptionKind.Choice ? ChoicesOf(record) : Array.Empty<OptionChoice>();
            if (record.Kind == OptionKind.Choice && choices.Count == 0) continue;
            if (record.Kind == OptionKind.Choice && cvar == null && choices.SelectMany(c => c.Set.Keys).All(name => cvars.Find(name) == null))
            {
                Log.Once(LogCat.UI, LogLevel.Info, "option-cvar:" + id, $"ui_option {id}: this host has none of the cvars its choices set, so it is not shown");
                continue;
            }
            if (record.Kind != OptionKind.Choice && cvar == null) continue;   // the content check says so at load

            var row = new Row(id, record, cvar, choices) { Label = Text(record.Label) };
            Read(cvars, row);
            if (drafts.TryGetValue(id, out var draft)) (row.Value, row.Checked, row.Selected) = draft;
            Update(row);
            _all.Add(row);
            PageOf(record.Page).Add(row);
        }
        _placed = false;
        Recount();
    }

    // The controls screen: `controls` beside the options screen's own record, once the screen is on the
    // stack (it is read once before it is).
    private void FindControls(Engine engine, World world)
    {
        if (!world.Resources.TryGet(out UiScreenStack? stack) || stack == null) { _placed = true; return; }
        var layers = stack.Layers;
        for (int i = 0; i < layers.Count; i++)
        {
            if (layers[i].Screen is not { } screen || !ReferenceEquals(screen.ViewModel, this)) continue;
            _placed = true;
            var id = new RecordId(screen.Id.Namespace, ControlsScreen);
            _controls = engine.Records.Exists("screen", id) ? id : default;
            return;
        }
    }

    private List<Row> PageOf(OptionPage page) => page switch
    {
        OptionPage.Graphics => Graphics,
        OptionPage.Audio => Audio,
        OptionPage.Controls => Controls,
        _ => Gameplay,
    };

    private IReadOnlyList<OptionChoice> ChoicesOf(OptionRecord record)
    {
        if (record.ChoicesFrom != OptionChoicesFrom.Languages) return record.Choices;
        var list = new List<OptionChoice>(record.Choices);
        foreach (string language in _text?.Languages ?? new[] { Localisation.DefaultLanguage })
            if (!list.Any(c => Same(c.Value, language)))
                list.Add(new OptionChoice { Label = _text != null && _text.Has("@lang." + language) ? "@lang." + language : language, Value = language });
        return list;
    }

    // What the cvars say, into the row's read state, and into what it shows unless the player changed it.
    private void Read(CVarRegistry cvars, Row row)
    {
        switch (row.Kind)
        {
            case OptionKind.Slider:
                row.ReadValue = TryNumber(row.CVar!.ValueString, out float value) ? value : row.Record.Min;
                if (!row.Dirty) row.Value = row.ReadValue;
                break;
            case OptionKind.Toggle:
                row.ReadChecked = Same(row.CVar!.ValueString, row.Record.On);
                if (!row.Dirty) row.Checked = row.ReadChecked;
                break;
            case OptionKind.Choice:
                int index = Match(row, cvars, defaults: false);
                var labels = row.OptionChoices.Select(c => Text(c.Label)).ToList();
                row.CustomIndex = index < 0 ? labels.Count : -1;
                if (index < 0)
                {
                    labels.Add(Text("@rpg.options.custom"));
                    index = row.CustomIndex;
                }
                if (!labels.SequenceEqual(row.Choices)) row.Choices = labels;   // a new list: the dropdown reads it again
                row.ReadSelected = index;
                if (!row.Dirty) row.Selected = index;
                break;
        }
        Update(row);
    }

    // The first choice whose values are all as the cvars are (or would be, at their defaults); -1: none.
    private static int Match(Row row, CVarRegistry cvars, bool defaults)
    {
        for (int i = 0; i < row.OptionChoices.Count; i++)
        {
            var choice = row.OptionChoices[i];
            bool matches = choice.Value.Length == 0 || row.CVar == null || Same(defaults ? row.CVar.DefaultString : row.CVar.ValueString, choice.Value);
            foreach (var (name, value) in choice.Set)
            {
                if (!matches) break;
                var other = cvars.Find(name);
                matches = other == null || Same(defaults ? other.DefaultString : other.ValueString, value);
            }
            if (matches) return i;
        }
        return -1;
    }

    private void Update(Row row)
    {
        row.Dirty = row.Kind switch
        {
            OptionKind.Slider => MathF.Abs(row.Value - row.ReadValue) > 1e-5f,
            OptionKind.Toggle => row.Checked != row.ReadChecked,
            _ => row.Selected != row.ReadSelected,
        };
        if (!row.IsSlider) return;
        var record = row.Record;
        if (record.DefaultLabel.Length > 0 && TryNumber(row.CVar!.DefaultString, out float fallback) && MathF.Abs(row.Value - fallback) < 1e-5f)
            row.ValueText = Text(record.DefaultLabel);
        else if (record.Percent)
            row.ValueText = (row.Value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
        else
            row.ValueText = row.Value.ToString("F" + Math.Clamp(record.Decimals, 0, 6), CultureInfo.InvariantCulture);
    }

    private void Recount() => HasChanges = _all.Any(r => r.Dirty);

    private string Text(string text) => _text?.Text(text) ?? text;

    private static float Snap(OptionRecord record, float value)
    {
        float lo = MathF.Min(record.Min, record.Max), hi = MathF.Max(record.Min, record.Max);
        if (record.Step > 0f) value = record.Min + MathF.Round((value - record.Min) / record.Step) * record.Step;
        return Math.Clamp(value, lo, hi);
    }

    private static void Set(CVar cvar, string value, List<string> problems)
    {
        if (!cvar.TrySet(value, out string? error)) problems.Add(error ?? $"{cvar.Name}: '{value}' was refused");
    }

    // A number as the cvar takes it: whole for an int cvar.
    private static string FormatNumber(CVar cvar, float value) => cvar.TypeName == "int"
        ? ((long)MathF.Round(value)).ToString(CultureInfo.InvariantCulture)
        : value.ToString("0.######", CultureInfo.InvariantCulture);

    private static bool TryNumber(string text, out float value)
    {
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)) { value = 1f; return true; }
        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)) { value = 0f; return true; }
        return false;
    }

    // Two cvar values the same: as text ignoring case, as numbers, or as flags ("1" and "true").
    internal static bool Same(string a, string b)
    {
        a = a.Trim();
        b = b.Trim();
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        return TryNumber(a, out float x) && TryNumber(b, out float y) && MathF.Abs(x - y) < 1e-5f;
    }
}

// What the load can say about an option on its own (issue #339): a slider's range, a choice's choices.
internal static class OptionChecks
{
    public static void Check(OptionRecord option, RecordCheck check)
    {
        if (option.Kind != OptionKind.Choice && option.Cvar.Length == 0)
            check.Error("Cvar", $"a {option.Kind.ToString().ToLowerInvariant()} needs the cvar it sets");
        if (option.Kind == OptionKind.Slider && !(option.Max > option.Min))
            check.Error("Max", $"a slider's max ({option.Max}) has to be above its min ({option.Min})");
        if (option.Kind == OptionKind.Choice)
        {
            if (option.Choices.Count == 0 && option.ChoicesFrom == OptionChoicesFrom.None)
                check.Error("Choices", "a choice needs its choices, or choicesFrom");
            if (option.ChoicesFrom == OptionChoicesFrom.Languages && option.Cvar.Length == 0)
                check.Error("Cvar", "choicesFrom languages sets a cvar (lang): name it");
            for (int i = 0; i < option.Choices.Count; i++)
            {
                var choice = option.Choices[i];
                if (choice.Value.Length > 0 && option.Cvar.Length == 0)
                    check.Error($"Choices[{i}].Value", "a choice's value is for the option's cvar, and it names none; use `set`");
                if (choice.Value.Length == 0 && choice.Set.Count == 0)
                    check.Error($"Choices[{i}]", "a choice sets nothing: give it a value or `set`");
            }
        }
    }
}
