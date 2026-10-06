#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Sage.UI;

// Subtitles and captions (issue #351): lines of dialogue ("Guard: Halt!") and captions of what is heard
// ("[door creaks]"), shown at the bottom of the screen for as long as it takes to read them. A world
// resource every world has (UiModule): a game says a line with Say, and a `sound` record with a
// `caption` says its own when it plays (SubtitleSystem); the `subtitles` and `captions` cvars turn the
// two kinds on and off. The world's UiScreenStack shows them, over every layer, in a SubtitleBox.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public enum SubtitleKind { Dialogue, Caption }

[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public readonly record struct SubtitleLine(SubtitleKind Kind, string Speaker, string Text, float Remaining);

[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class Subtitles
{
    private readonly List<SubtitleLine> _lines = new();
    private readonly Localisation? _text;

    public Subtitles(Localisation? text = null) { _text = text; }

    // Whether lines of dialogue and captions of sounds are shown; one turned off drops what it showed.
    public bool ShowDialogue
    {
        get => _showDialogue;
        set { if (_showDialogue == value) return; _showDialogue = value; if (!value) Drop(SubtitleKind.Dialogue); }
    }

    public bool ShowCaptions
    {
        get => _showCaptions;
        set { if (_showCaptions == value) return; _showCaptions = value; if (!value) Drop(SubtitleKind.Caption); }
    }

    private bool _showDialogue = true, _showCaptions;

    // At most this many lines at once: a new one pushes the oldest out.
    public int MaxLines { get; set; } = 3;

    // How long a line stays when its caller gives no time: long enough to read, never less than MinSeconds.
    public float MinSeconds { get; set; } = 2f;
    public float SecondsPerCharacter { get; set; } = 0.06f;

    // Oldest first.
    public IReadOnlyList<SubtitleLine> Lines => _lines;

    // Moves whenever Lines changes.
    public int Version { get; private set; }

    // A line of dialogue. Texts starting with '@' are localisation keys. Returns whether it shows.
    public bool Say(string speaker, string text, float seconds = 0f) => Add(SubtitleKind.Dialogue, speaker ?? "", text, seconds);

    // A caption of a sound, shown in brackets.
    public bool Caption(string text, float seconds = 0f) => Add(SubtitleKind.Caption, "", text, seconds);

    public void Clear()
    {
        if (_lines.Count == 0) return;
        _lines.Clear();
        Version++;
    }

    // Time passes: lines whose time is up go.
    public void Advance(float seconds)
    {
        if (_lines.Count == 0 || !(seconds > 0f)) return;
        bool gone = false;
        for (int i = _lines.Count - 1; i >= 0; i--)
        {
            var line = _lines[i];
            float left = line.Remaining - seconds;
            if (left <= 0f) { _lines.RemoveAt(i); gone = true; }
            else _lines[i] = line with { Remaining = left };
        }
        if (gone) Version++;
    }

    // How a line reads on screen: "Speaker: text", or "[text]" for a caption.
    public static string Format(in SubtitleLine line) =>
        line.Kind == SubtitleKind.Caption ? "[" + line.Text + "]"
        : line.Speaker.Length > 0 ? line.Speaker + ": " + line.Text
        : line.Text;

    private bool Add(SubtitleKind kind, string speaker, string text, float seconds)
    {
        if (string.IsNullOrWhiteSpace(text) || !(kind == SubtitleKind.Dialogue ? _showDialogue : _showCaptions)) return false;
        speaker = Localise(speaker);
        text = Localise(text);
        if (!(seconds > 0f)) seconds = MathF.Max(MinSeconds, text.Length * SecondsPerCharacter);
        // The same line again (a looping sound, a bark repeated) keeps its place and starts its time over.
        for (int i = 0; i < _lines.Count; i++)
            if (_lines[i].Kind == kind && _lines[i].Speaker == speaker && _lines[i].Text == text)
            {
                _lines[i] = _lines[i] with { Remaining = MathF.Max(_lines[i].Remaining, seconds) };
                return true;
            }
        _lines.Add(new SubtitleLine(kind, speaker, text, seconds));
        while (_lines.Count > Math.Max(MaxLines, 1)) _lines.RemoveAt(0);
        Version++;
        return true;
    }

    private string Localise(string text) => text.Length > 0 && _text != null ? _text.Text(text) : text;

    private void Drop(SubtitleKind kind)
    {
        if (_lines.RemoveAll(l => l.Kind == kind) > 0) Version++;
    }
}

// The lines of a Subtitles, as labels in a column at the bottom of the screen; hidden while there are
// none. Never hit by the pointer and never focused, so it sits over a menu without getting in its way.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class SubtitleBox : Stack
{
    private readonly List<Label> _pool = new();
    private int _seen = -1;

    public SubtitleBox(Subtitles feed) : base(Orientation.Column)
    {
        Feed = feed ?? throw new ArgumentNullException(nameof(feed));
        Anchors = Anchors.Bottom;
        HAlign = Align.Center;
        Margin = new Thickness(0f, 0f, 0f, 48f);
        Spacing = 4f;
        HitTestable = false;
        Visible = false;
    }

    public override string TypeName => "subtitles";

    public Subtitles Feed { get; }

    // The style ids lines of dialogue and captions are drawn in, and what their text is sized and padded
    // with (the style's textScale and padding, which UiScreenStack copies in).
    public string? LineStyle { get; set; }
    public string? CaptionStyle { get; set; }
    public float LineTextScale { get; set; } = 1f;
    public float CaptionTextScale { get; set; } = 1f;
    public Thickness LinePadding { get; set; }
    public Thickness CaptionPadding { get; set; }

    // Brings the labels up to the feed's lines. Returns whether anything changed; allocates nothing
    // when the feed has not.
    public bool Sync()
    {
        if (_seen == Feed.Version) return false;
        _seen = Feed.Version;
        var lines = Feed.Lines;
        while (_pool.Count < lines.Count)
            _pool.Add(new Label { HitTestable = false, TextAlign = Align.Center, HAlign = Align.Center });
        Clear();
        for (int i = 0; i < lines.Count; i++)
        {
            var label = _pool[i];
            bool caption = lines[i].Kind == SubtitleKind.Caption;
            label.Text = Subtitles.Format(lines[i]);
            label.Style = caption ? CaptionStyle ?? LineStyle : LineStyle;
            label.TextScale = caption ? CaptionTextScale : LineTextScale;
            label.Padding = caption ? CaptionPadding : LinePadding;
            Add(label);
        }
        Visible = lines.Count > 0;
        return true;
    }
}

// Captions from sounds (issue #351): a `sound` record with a `caption` puts it on the world's Subtitles
// when it is asked to play (SoundRequested), and the lines' time runs on with the simulation's, so a
// paused game keeps its subtitles up.
[System(Id, Phase.Late)]
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
internal sealed class SubtitleSystem : ISystem
{
    public const string Id = "sage.ui.subtitles";

    private readonly EventReader<SoundRequested> _sounds;

    public SubtitleSystem(World world)
    {
        _sounds = world.Events.Reader<SoundRequested>(this);
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        if (!world.Resources.TryGet<Subtitles>(out var subtitles) || subtitles == null)
        {
            if (_sounds.HasPending) foreach (ref readonly var _ in _sounds.Read()) { }
            return;
        }
        if (_sounds.HasPending)
        {
            // The `sound` type is the client's (and `sage validate`'s): a world without it has no captions.
            var records = world.Engine?.Records;
            if (records != null && records.TypeNameOf(typeof(SoundRecord)) == null) records = null;
            foreach (ref readonly var sound in _sounds.Read())
            {
                if (records == null || sound.Sound.IsEmpty || !records.TryGet(sound.Sound, out SoundRecord record) || record.Caption.Length == 0) continue;
                if (record.Speaker.Length > 0 || record.Bus == AudioBus.Voice) subtitles.Say(record.Speaker, record.Caption);
                else subtitles.Caption(record.Caption);
            }
        }
        subtitles.Advance(ctx.Tick.Dt);
    }
}
