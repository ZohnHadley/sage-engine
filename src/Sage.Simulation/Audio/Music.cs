#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Sage.Simulation;

// Music (issue #325, docs/design/11 §3): a track for where the player is and what is going on, which
// changes when either does — the dungeon's drone, the town's tune, the fight's drums. Daggerfall, Morrowind
// and S.T.A.L.K.E.R. all pick their music from the state of the world, so the state is the world's:
//
//   - a `music` record says what a track *is*: its file, the layers (stems) played in step with it, where it
//     loops and how long it takes to fade in and out;
//   - the `Music` world resource says which track is playing, which one it is fading from and how far it
//     has got, and how intense things are (which layers are heard). Saved, like the weather;
//   - content changes it with data alone: a scene's `"environment": { "music": ... }` when the player
//     arrives, the `PlayMusic` / `StopMusic` / `SetMusicIntensity` inputs on any wire, and the `play_music`,
//     `stop_music` and `music_intensity` actions in a `then`;
//   - the `MusicPlayer` turns that into voices: one streamed voice per stem of at most two tracks, faded
//     against each other, outside the voice cap and never stolen. Headless, so a test hears a crossfade
//     tick by tick; the client only asks it to update once a frame.
//
//   { "type": "music", "id": "wander", "track": "music/wander.ogg", "loopStart": 44100, "fadeIn": 3,
//     "layers": [ { "asset": "music/wander_drums.ogg", "intensity": 0.5 } ] }

// What a track is, as data. The files are Ogg Vorbis and stream as they play (PcmStreamer).
[Record("music", Plugin = "sage.client")]
public sealed class MusicRecord
{
    [AssetKind("sound"), Property(Tooltip = "The track: an Ogg Vorbis (.ogg) file, streamed as it plays")]
    public AssetPath Track;

    [Property(Tooltip = "Stems played in step with the track, each heard while the world's music intensity is at or above its own")]
    public List<MusicLayer> Layers = new();

    [Property(Min = 0, Max = 1, Tooltip = "Loudness before the Music and Master buses")]
    public float Volume = 1f;

    [Property(Tooltip = "Play it again when it ends; off: it plays once and the music is silent after it")]
    public bool Loop = true;

    // Where a loop winds back to and where it wraps, in samples per channel (frames) from the file's start:
    // an intro before LoopStart is heard once, a tail after LoopEnd never. The same for every layer.
    [Property(Min = 0, Unit = "samples", Tooltip = "Where a loop winds back to, in samples per channel from the start: an intro before it plays once")]
    public long LoopStart;
    [Property(Min = 0, Unit = "samples", Tooltip = "Where a loop wraps, in samples per channel from the start; 0: the end of the file")]
    public long LoopEnd;

    [Property(Min = 0, Unit = "s", Tooltip = "Seconds it takes to come in, crossfading from what was playing (unless the change names its own)")]
    public float FadeIn = 2f;
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds it takes to fade to silence when the music is stopped (unless the stop names its own)")]
    public float FadeOut = 2f;

    // The files are what the music player streams: Ogg Vorbis, and a loop's points in order. Run wherever
    // the record type is registered (the client, `sage validate`).
    internal static void Check(MusicRecord music, RecordCheck check)
    {
        if (music.Track.IsEmpty) check.Error("track", "a music record needs a \"track\": an .ogg file");
        else if (!PcmStreamer.IsOgg(music.Track)) check.Error("track", $"'{music.Track.Path.Value}' is not an .ogg; music streams, and only Ogg Vorbis streams");
        for (int i = 0; i < music.Layers.Count; i++)
        {
            var layer = music.Layers[i];
            if (layer.Asset.IsEmpty) check.Error($"layers[{i}].asset", "a layer needs an \"asset\": an .ogg file");
            else if (!PcmStreamer.IsOgg(layer.Asset)) check.Error($"layers[{i}].asset", $"'{layer.Asset.Path.Value}' is not an .ogg; music streams, and only Ogg Vorbis streams");
        }
        if (music.LoopStart < 0) check.Error("loopStart", "a sample from 0");
        if (music.LoopEnd < 0) check.Error("loopEnd", "a sample from 0, or 0 for the end of the file");
        else if (music.LoopEnd != 0 && music.LoopEnd <= music.LoopStart)
            check.Error("loopEnd", $"{music.LoopEnd} is not after loopStart ({music.LoopStart}); a loop wraps after it starts");
        if (music.FadeIn < 0) check.Error("fadeIn", "seconds, from 0");
        if (music.FadeOut < 0) check.Error("fadeOut", "seconds, from 0");
    }
}

// A stem of a track: drums that come in when a fight starts, strings when it is going badly.
public sealed class MusicLayer
{
    [AssetKind("sound"), Property(Tooltip = "The stem: an Ogg Vorbis (.ogg) file as long as the track, streamed in step with it")]
    public AssetPath Asset;

    [Property(Min = 0, Max = 1, Tooltip = "Loudness beside the track")]
    public float Volume = 1f;

    [Property(Min = 0, Max = 1, Tooltip = "Heard while the world's music intensity is at or above this; 0: always")]
    public float Intensity;

    [Property(Min = 0, Unit = "s", Tooltip = "Seconds it takes to come in or go out as the intensity crosses its own")]
    public float Fade = 1f;
}

// What the world's music is doing: the track playing (or coming in), the one going out, how far between
// them, and how intense things are. One per world.
//
// Saved (09 §3.1), so a load is back in the crypt's drone rather than the title music; the tracks start
// again from the top. Like the weather's, the rate is finite even for a cut: `Infinity` is not JSON.
[SavedResource("music", Plugin = RegistrationOwners.Core)]
public sealed class Music
{
    private const float Instant = 1000f;

    public RecordId Track { get; set; }          // what is playing, or coming in; empty: silence
    public RecordId Previous { get; set; }       // what is going out; empty once it has
    public float Blend { get; set; } = 1f;       // 0 = all Previous, 1 = all Track
    public float BlendRate { get; set; } = 1f;   // per second

    private float _intensity;

    // 0 calm to 1 desperate: which of a track's layers are heard (MusicLayer.Intensity).
    public float Intensity
    {
        get => _intensity;
        set => _intensity = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
    }

    public bool Settled => Blend >= 1f;

    // Fades to `track` over `seconds`, from whatever is playing. A change mid-fade starts from where it is:
    // back to the track going out turns the fade round where it stands, and to a third track lets the
    // quieter of the two go and fades from the louder.
    public void Play(RecordId track, float seconds)
    {
        if (track == Track) return;
        float rate = !float.IsFinite(seconds) || seconds <= 0.001f ? Instant : 1f / seconds;
        if (!Settled && track == Previous)
        {
            (Previous, Track) = (Track, Previous);
            Blend = 1f - Blend;
        }
        else
        {
            Previous = !Settled && Blend < 0.5f ? Previous : Track;
            Track = track;
            Blend = Previous.IsEmpty && Track.IsEmpty ? 1f : 0f;
        }
        BlendRate = rate;
    }

    // Fades to silence over `seconds`.
    public void Stop(float seconds) => Play(default, seconds);

    public void Advance(float dt)
    {
        if (Settled)
        {
            Previous = default;
            return;
        }
        Blend = MathF.Min(1f, Blend + BlendRate * dt);
        if (Settled) Previous = default;
    }

    // How loud `track` is now, 0 to 1, before its own volume: an equal-power crossfade, so the music is as
    // loud half-way through a change as either side of it.
    public float GainOf(RecordId track)
    {
        if (track.IsEmpty) return 0f;
        float t = Math.Clamp(Blend, 0f, 1f);
        if (track == Track) return MathF.Sin(t * MathF.PI * 0.5f);
        if (track == Previous) return MathF.Cos(t * MathF.PI * 0.5f);
        return 0f;
    }
}

// Changing the world's music from content: what the inputs, the actions and a scene's environment call.
public static class MusicRules
{
    // The inputs a wire sends: at any entity (`!self` will do), because the music is the world's.
    public const string PlayInput = "PlayMusic";               // "<music> [seconds]"
    public const string StopInput = "StopMusic";               // "[seconds]"
    public const string IntensityInput = "SetMusicIntensity";  // "<0..1>"

    // The fallback fade when a track has no record to say (a server, which has no `music` record type).
    public const float DefaultFade = 2f;

    public static Music Of(World world) => world.Resources.GetOrAdd(static () => new Music());

    // Fades the world's music to `track`, over `seconds` or, when that is null, the track's own fadeIn.
    public static void Play(World world, RecordId track, float? seconds = null)
    {
        var music = Of(world);
        if (track == music.Track) return;
        float fade = seconds ?? (Find(world, track) is { } record ? record.FadeIn : DefaultFade);
        music.Play(track, fade);
        Log.Debug(LogCat.Audio, $"'{world.Name}': music {(track.IsEmpty ? "stops" : $"to {track}")} over {fade:0.##}s");
    }

    // Fades it to silence, over `seconds` or the playing track's own fadeOut.
    public static void Stop(World world, float? seconds = null)
    {
        var music = Of(world);
        if (music.Track.IsEmpty) return;
        Play(world, default, seconds ?? (Find(world, music.Track) is { } record ? record.FadeOut : DefaultFade));
    }

    public static void SetIntensity(World world, float intensity) => Of(world).Intensity = intensity;

    // The record, when the world has the record type (the client) and the track is one.
    internal static MusicRecord? Find(World world, RecordId track)
    {
        if (track.IsEmpty || !world.Resources.TryGet<RecordStore>(out var records) || records == null) return null;
        if (records.TypeNameOf(typeof(MusicRecord)) == null) return null;
        return records.TryGet(track, out MusicRecord record) ? record : null;
    }

    // "sandbox:wander", or "wander" when the record type knows one by that name.
    internal static RecordId Parse(World world, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return default;
        try
        {
            if (text.Contains(':', StringComparison.Ordinal)
                || !world.Resources.TryGet<RecordStore>(out var records) || records == null
                || records.TypeNameOf(typeof(MusicRecord)) == null)
                return RecordId.Parse(text, "sage");
            return records.Resolve("music", text);
        }
        catch (FormatException ex)
        {
            Log.Warn(LogCat.Events, $"music: '{text}' is no music id ({ex.Message})");
            return default;
        }
    }

    private static float? Seconds(string text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float s) && s >= 0f ? s : null;

    internal static void Register(Engine engine)
    {
        // `PlayMusic` "sandbox:hut 3": that track, over three seconds (or its own fadeIn).
        engine.Inputs.Register(PlayInput, static (World world, in IOContext io) =>
        {
            var parts = io.Parameter.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                Log.Warn(LogCat.Events, $"I/O: {PlayInput} at {World.Describe(io.Self)} names no music (\"<music> [seconds]\")");
                return;
            }
            var track = Parse(world, parts[0]);
            if (track.IsEmpty) return;
            Play(world, track, parts.Length > 1 ? Seconds(parts[1]) : null);
        });
        // `StopMusic` "4": to silence over four seconds (or the track's own fadeOut).
        engine.Inputs.Register(StopInput, static (World world, in IOContext io) => Stop(world, Seconds(io.Parameter.Trim())));
        // `SetMusicIntensity` "0.8": which layers are heard.
        engine.Inputs.Register(IntensityInput, static (World world, in IOContext io) => SetIntensity(world, io.Number(0f)));
    }
}

// The music fades in the simulation, at tick rate, so a save and a server have the same idea of it as the
// client does; the client only follows it (MusicPlayer).
[System(Id, Phase.Late)]
internal sealed class MusicSystem : ISystem
{
    public const string Id = "sage.world.music";

    private readonly World _world;

    public MusicSystem(World world) => _world = world;

    public void Run(in SystemContext ctx)
    {
        if (_world.Resources.TryGet<Music>(out var music) && music != null) music.Advance(ctx.Tick.Dt);
    }
}

// ---- the words ------------------------------------------------------------------------------------------

// `{ "play_music": "sandbox:crypt", "fade": 4 }`: the world's music fades to that track, over the track's
// own fadeIn when no fade is given.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Action("play_music", Plugin = RegistrationOwners.Core)]
internal sealed class PlayMusicAction : IAction
{
    [EntryValue, Property(Tooltip = "The music record to fade to")]
    public RecordRef<MusicRecord> Music;
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds the crossfade takes; left out, the track's own fadeIn")]
    public float? Fade;

    public void Run(in ActionContext context)
    {
        if (!Music.IsEmpty) MusicRules.Play(context.World, Music.Id, Fade);
    }
}

// `{ "stop_music": 3 }`: the music fades to silence over three seconds; `{ "stop_music": {} }`, over the
// playing track's own fadeOut.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Action("stop_music", Plugin = RegistrationOwners.Core)]
internal sealed class StopMusicAction : IAction
{
    [EntryValue, Property(Min = 0, Unit = "s", Tooltip = "Seconds the fade to silence takes; left out, the track's own fadeOut")]
    public float? Fade;

    public void Run(in ActionContext context) => MusicRules.Stop(context.World, Fade);
}

// `{ "music_intensity": 0.8 }`: which layers of the playing tracks are heard.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Action("music_intensity", Plugin = RegistrationOwners.Core)]
internal sealed class MusicIntensityAction : IAction
{
    [EntryValue, Property(Min = 0, Max = 1, Tooltip = "0 calm to 1 desperate: a layer is heard at or above its own intensity")]
    public float Intensity;

    public void Run(in ActionContext context) => MusicRules.SetIntensity(context.World, Intensity);
}

// ---- the player ---------------------------------------------------------------------------------------

// The world's music made into voices (issue #325, docs/design/11 §13 "music wants a second mixer path"): it
// owns a *deck* for each of at most two tracks, the one playing and the one going out, and a deck owns one
// streamed voice per stem — the track and its layers, started together so they keep step. Each update sets
// every voice's volume from the crossfade (Music.GainOf), the track's volume and, for a layer, its own fade
// toward the world's intensity. Its voices are the mixer's music voices (AudioMixer.PlayMusic): outside the
// voice cap, never stolen, on the Music bus.
//
// Headless: the client's audio system calls Update once a frame before the backend applies the mixer, and a
// test calls it tick by tick and reads the gains.
internal sealed class MusicPlayer
{
    internal sealed class Deck
    {
        public required RecordId Track;
        public MusicRecord? Record;            // null: no such record, so silent
        public VoiceHandle[] Voices = Array.Empty<VoiceHandle>();   // the track, then each layer
        public float[] LayerGain = Array.Empty<float>();             // each layer's own fade, 0..1
    }

    private readonly List<Deck> _decks = new(2);

    public IReadOnlyList<Deck> Decks => _decks;

    // The deck playing `track`, or null.
    public Deck? DeckOf(RecordId track)
    {
        foreach (var deck in _decks)
            if (deck.Track == track) return deck;
        return null;
    }

    // Follows the world's music: starts what has come in, stops what has gone, and sets every voice's
    // volume. `dt` is the frame's, for the layers' fades.
    public void Update(Music music, RecordStore? records, AudioMixer mixer, float dt)
    {
        // What has gone out (or was replaced mid-fade) stops.
        for (int i = _decks.Count - 1; i >= 0; i--)
        {
            var deck = _decks[i];
            if (deck.Track == music.Track || deck.Track == music.Previous) continue;
            Stop(deck, mixer);
            _decks.RemoveAt(i);
        }

        Ensure(music.Previous, music, records, mixer);
        Ensure(music.Track, music, records, mixer);

        foreach (var deck in _decks)
        {
            if (deck.Record == null) continue;
            float gain = music.GainOf(deck.Track) * deck.Record.Volume;
            SetVolume(deck, 0, gain, mixer);
            for (int l = 0; l < deck.Record.Layers.Count && l + 1 < deck.Voices.Length; l++)
            {
                var layer = deck.Record.Layers[l];
                float target = music.Intensity >= layer.Intensity ? 1f : 0f;
                float step = layer.Fade <= 0.001f ? 1f : dt / layer.Fade;
                ref float own = ref deck.LayerGain[l];
                own = own < target ? MathF.Min(target, own + step) : MathF.Max(target, own - step);
                SetVolume(deck, l + 1, gain * layer.Volume * own, mixer);
            }
        }
    }

    // Everything stops and is forgotten: sound turned off, or the world going. The next Update starts the
    // world's music again from the top.
    public void Silence(AudioMixer mixer)
    {
        foreach (var deck in _decks) Stop(deck, mixer);
        _decks.Clear();
    }

    private void Ensure(RecordId track, Music music, RecordStore? records, AudioMixer mixer)
    {
        if (track.IsEmpty || DeckOf(track) != null) return;
        MusicRecord? record = null;
        if (records != null && records.TypeNameOf(typeof(MusicRecord)) != null && records.TryGet(track, out MusicRecord found)) record = found;
        var deck = new Deck { Track = track, Record = record };
        _decks.Add(deck);
        if (record == null)
        {
            Log.Once(LogCat.Audio, LogLevel.Warn, $"music-missing:{track}", $"No music called '{track}': silence instead");
            return;
        }

        deck.Voices = new VoiceHandle[1 + record.Layers.Count];
        deck.LayerGain = new float[record.Layers.Count];
        float gain = music.GainOf(track) * record.Volume;
        deck.Voices[0] = Start(track, record, record.Track, gain, mixer);
        for (int l = 0; l < record.Layers.Count; l++)
        {
            var layer = record.Layers[l];
            deck.LayerGain[l] = music.Intensity >= layer.Intensity ? 1f : 0f;   // a track starts with its layers where they belong
            deck.Voices[l + 1] = Start(track, record, layer.Asset, gain * layer.Volume * deck.LayerGain[l], mixer);
        }
    }

    private static VoiceHandle Start(RecordId track, MusicRecord record, AssetPath asset, float volume, AudioMixer mixer) =>
        asset.IsEmpty ? VoiceHandle.None : mixer.PlayMusic(track, asset, volume, record.Loop, record.LoopStart, record.LoopEnd);

    // A voice the backend has finished with (a track that does not loop, at its end) is not started again.
    private static void SetVolume(Deck deck, int stem, float volume, AudioMixer mixer)
    {
        var handle = deck.Voices[stem];
        if (!handle.IsValid) return;
        if (mixer.Find(handle) == null) { deck.Voices[stem] = VoiceHandle.None; return; }
        mixer.SetVolume(handle, volume);
    }

    private static void Stop(Deck deck, AudioMixer mixer)
    {
        foreach (var handle in deck.Voices)
            if (handle.IsValid) mixer.Stop(handle);
    }
}
