#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Simulation;

// Which sounds play, how loud, and which one is dropped when there are too many (docs/design/11 §3,
// TODO F4).
//
// **In the engine, with no MonoGame in sight**, so all of it can be tested without a sound card: the
// decisions here are gain from distance, pan from direction, per-sound limits and cooldowns, bus
// volumes, and which voice is stolen when the cap is reached. What is left for the client is starting
// and stopping actual `SoundEffectInstance`s — the same split as `Panel` and `PanelView`, or
// `RenderSnapshot` and the renderer.
//
// The mixer never loads anything and never blocks. It hands out `Voice` records; the backend reads
// them, starts what is new, updates what moved, and reports what has finished.
public readonly record struct VoiceHandle(int Id)
{
    public bool IsValid => Id != 0;
    public static readonly VoiceHandle None = new(0);
}

// One playing sound, as the mixer sees it. The backend fills in nothing; it only reads.
public sealed class Voice
{
    public VoiceHandle Handle;
    public RecordId Sound = default;
    public SoundRecord? Record;
    public AssetPath Asset;
    public Vector3 Position;             // origin space, like everything else (R6)
    public bool Positional;
    public bool Loop;
    public float Volume = 1f;            // the caller's own scale, before jitter, bus and distance
    public float Pitch;                  // -1..1: jitter plus doppler, what the backend applies
    internal float BasePitch;            // the jitter alone
    public Vector3 Velocity;             // origin space, units per second: derived or given (SetVelocity)
    public Vector3 Direction;            // the emitter's forward for a cone; zero is omnidirectional
    internal Vector3 PrevPosition;
    internal bool VelocityGiven;
    public float Gain;                   // what the backend should apply, after everything
    public float Pan;                    // -1 left, +1 right
    public double StartedAt;
    public bool Started;                 // the backend has begun it
    public bool Stopping;                // the backend should stop it and drop it

    // Music's (issue #325, MusicPlayer): never stolen and not counted against the voice cap, because the
    // score dropping out for a footstep is the one thing worse than no score. Streamed, from LoopStart to
    // LoopEnd (frames; 0 is the end of the file) once it loops.
    internal bool Music;
    internal long LoopStart;
    internal long LoopEnd;
}

// What the player set, as opposed to what a world is doing: bus volumes and the voice cap.
//
// **A mixer is per world** — a voice's position is in that world's origin space (R6), and two worlds do
// not share a frame, any more than they share a `RenderSnapshot`. Volumes are not per world: somebody
// who moves the music slider means all of it, and a world created after they moved it must not start at
// full volume. So the mixers share one of these, and the cvars write it once.
public sealed class AudioSettings
{
    private readonly float[] _busVolume = new float[Enum.GetValues<AudioBus>().Length];

    public AudioSettings()
    {
        for (int i = 0; i < _busVolume.Length; i++) _busVolume[i] = 1f;
    }

    // The cap across everything. Beyond it the quietest voice is stolen, which is the one a player is
    // least likely to notice going.
    public int MaxVoices { get; set; } = 32;

    // Doppler: how much of the real shift is applied (0 off, 1 as physics has it), and the speed of
    // sound it is measured against, in the world's units per second (issue #335; cvar `snd_doppler`).
    public float DopplerScale { get; set; } = 1f;

    public float SpeedOfSound { get; set; } = 343f;

    public float Volume(AudioBus bus) => _busVolume[(int)bus];

    public void SetVolume(AudioBus bus, float volume) => _busVolume[(int)bus] = Math.Clamp(volume, 0f, 1f);
}

public sealed class AudioMixer
{
    private readonly List<Voice> _voices = new();
    private readonly Dictionary<RecordId, double> _lastPlayed = new();
    private readonly AudioSettings _settings;
    private readonly Random _random = new();

    private int _nextId = 1;
    private double _now;
    private double _lastUpdate = double.NaN;
    private Vector3 _prevListener;
    private bool _listenerVelocityGiven;

    // Given no settings it keeps its own, which is what a test wants: one object to set up, and no
    // volume left over from the last test that touched a shared one.
    public AudioMixer(AudioSettings? settings = null) => _settings = settings ?? new AudioSettings();

    public int MaxVoices
    {
        get => _settings.MaxVoices;
        set => _settings.MaxVoices = value;
    }

    public IReadOnlyList<Voice> Voices => _voices;

    public int Playing => _voices.Count;

    // How many were refused since the last reset, for `snd_stats`: a number nobody looks at until the
    // day the audio sounds thin, and then the first thing they want.
    public int Refused { get; private set; }

    public int Stolen { get; private set; }

    public Vector3 ListenerPosition { get; private set; }

    // Units per second, from the listener's change of position between updates unless set.
    public Vector3 ListenerVelocity { get; private set; }

    public Vector3 ListenerForward { get; private set; } = -Vector3.UnitZ;

    public Vector3 ListenerRight { get; private set; } = Vector3.UnitX;

    public void SetBusVolume(AudioBus bus, float volume) => _settings.SetVolume(bus, volume);

    public void SetListener(Vector3 position, Quaternion rotation)
    {
        ListenerPosition = position;
        ListenerForward = Vector3.Transform(TransformMath.Forward, rotation);
        ListenerRight = Vector3.Transform(Vector3.UnitX, rotation);
    }

    // Gives the listener's velocity (a physics body's) instead of deriving it from its movement.
    public void SetListenerVelocity(Vector3 velocity)
    {
        ListenerVelocity = velocity;
        _listenerVelocityGiven = true;
    }

    // Gives a voice's velocity instead of deriving it from its movement; and the way it faces, for a
    // cone (zero is omnidirectional).
    public void SetVelocity(VoiceHandle handle, Vector3 velocity)
    {
        var voice = Find(handle);
        if (voice == null) return;
        voice.Velocity = velocity;
        voice.VelocityGiven = true;
        Compute(voice);
    }

    public void Aim(VoiceHandle handle, Vector3 direction)
    {
        var voice = Find(handle);
        if (voice == null) return;
        voice.Direction = direction.LengthSquared() < 1e-8f ? Vector3.Zero : Vector3.Normalize(direction);
        Compute(voice);
    }

    // Asks for a sound. Returns a voice, or `None` when the mixer refused it — too many already, too
    // soon after the last one, or nothing to play. A caller never has to check before asking, which is
    // what keeps the calling code one line.
    public VoiceHandle Play(RecordId sound, SoundRecord? record, Vector3 position, bool positional,
                            float volume = 1f, bool loop = false)
    {
        if (record == null || record.Variations.Count == 0) return Refuse(sound, "nothing to play");

        // Cooldown: the same sound twice in the same instant is one sound with a phasing artefact.
        if (record.Cooldown > 0f && _lastPlayed.TryGetValue(sound, out double last) && _now - last < record.Cooldown)
            return Refuse(sound, "still cooling down");

        if (CountOf(sound) >= Math.Max(record.MaxInstances, 1) && !StealOldest(sound))
            return Refuse(sound, "too many of it already");
        if (Capped() >= MaxVoices && !StealQuietest(record))
            return Refuse(sound, "every voice is busy with something louder");

        var voice = new Voice
        {
            Handle = new VoiceHandle(_nextId++),
            Sound = sound,
            Record = record,
            Asset = record.Variations[_random.Next(record.Variations.Count)],
            Position = position,
            Positional = positional && !record.Is2D,
            Loop = loop,
            Volume = volume * Jitter(record.Volume, record.VolumeJitter),
            BasePitch = record.PitchJitter <= 0f ? 0f : (float)(_random.NextDouble() * 2 - 1) * record.PitchJitter,
            PrevPosition = position,
            StartedAt = _now,
        };
        voice.Pitch = voice.BasePitch;
        Compute(voice);
        _voices.Add(voice);
        _lastPlayed[sound] = _now;

        // "Why can I not hear it" is the whole of audio debugging, and the answer is either a line here
        // or a line in `Refuse`. Trace is compiled out of Shipping builds entirely (02 §3.2).
        Log.Trace(LogCat.Audio, $"play {sound} gain {voice.Gain:F2} pan {voice.Pan:F2}" +
                                $"{(voice.Loop ? " loop" : "")} ({_voices.Count}/{MaxVoices} voices)");
        return voice.Handle;
    }

    // The record a music voice is computed with: 2D, on the Music bus, streamed. One for every music voice,
    // and never handed to anybody who could change it.
    private static readonly SoundRecord MusicSound = new() { Bus = AudioBus.Music, Stream = true, MaxDistance = 0f, MaxInstances = int.MaxValue };

    // A stem of a music track (issue #325, MusicPlayer): 2D, streamed, on the Music bus, and outside the
    // voice cap — never refused for being one too many and never stolen, so the music does not drop out
    // because a fight got loud. Its volume is the player's to move (SetVolume) as it fades.
    internal VoiceHandle PlayMusic(RecordId music, AssetPath asset, float volume, bool loop, long loopStart, long loopEnd)
    {
        var voice = new Voice
        {
            Handle = new VoiceHandle(_nextId++),
            Sound = music,
            Record = MusicSound,
            Asset = asset,
            Loop = loop,
            Volume = volume,
            StartedAt = _now,
            Music = true,
            LoopStart = loopStart,
            LoopEnd = loopEnd,
        };
        Compute(voice);
        _voices.Add(voice);
        Log.Trace(LogCat.Audio, $"music {music} {asset} gain {voice.Gain:F2}{(loop ? " loop" : "")}");
        return voice.Handle;
    }

    // A voice's own scale, moved while it plays: a fade.
    internal void SetVolume(VoiceHandle handle, float volume)
    {
        var voice = Find(handle);
        if (voice == null) return;
        voice.Volume = volume;
        Compute(voice);
    }

    // Voices that count against MaxVoices: all but the music's.
    private int Capped()
    {
        int n = 0;
        foreach (var voice in _voices)
            if (!voice.Music) n++;
        return n;
    }

    private VoiceHandle Refuse(RecordId sound, string why)
    {
        Refused++;
        Log.Trace(LogCat.Audio, $"refused {sound}: {why}");
        return VoiceHandle.None;
    }

    public void Stop(VoiceHandle handle)
    {
        var voice = Find(handle);
        if (voice != null) voice.Stopping = true;
    }

    public void StopAll()
    {
        foreach (var voice in _voices) voice.Stopping = true;
    }

    // The file behind `asset` has been replaced (hot reload, issue 4h-3): what was playing it is playing
    // something that no longer exists. A one-shot is stopped, since restarting it would replay a
    // footstep nobody asked for; a loop is marked not started, which makes the backend begin it again
    // from the new file. Voices on other files are untouched. Returns how many voices were affected.
    public int Invalidate(AssetPath asset)
    {
        int n = 0;
        foreach (var voice in _voices)
        {
            if (voice.Asset != asset || voice.Stopping) continue;
            if (voice.Loop) voice.Started = false;
            else voice.Stopping = true;
            n++;
        }
        return n;
    }

    public Voice? Find(VoiceHandle handle)
    {
        foreach (var voice in _voices)
            if (voice.Handle == handle) return voice;
        return null;
    }

    public void Move(VoiceHandle handle, Vector3 position)
    {
        var voice = Find(handle);
        if (voice == null) return;
        voice.Position = position;
        Compute(voice);
    }

    // Recomputes every voice against the listener. Called once a frame by the client, before it pushes
    // gains into the backend.
    public void Update(double now)
    {
        bool first = double.IsNaN(_lastUpdate);
        double dt = first ? 0 : now - _lastUpdate;
        _now = now;
        if (first) _prevListener = ListenerPosition;

        // Velocity is how far things moved since the last update, so a game needs to say nothing to get
        // doppler. Under a millisecond there is no speed to measure: keep the last one.
        if (dt > 1e-3)
        {
            if (!_listenerVelocityGiven) ListenerVelocity = (ListenerPosition - _prevListener) / (float)dt;
            foreach (var voice in _voices)
                if (!voice.VelocityGiven) voice.Velocity = (voice.Position - voice.PrevPosition) / (float)dt;
        }
        if (first || dt > 1e-3)
        {
            _lastUpdate = now;
            _prevListener = ListenerPosition;
            foreach (var voice in _voices) voice.PrevPosition = voice.Position;
        }
        foreach (var voice in _voices) Compute(voice);
    }

    // The backend calls this when a one-shot has finished, and after it has stopped a stopping voice.
    public void Remove(VoiceHandle handle)
    {
        for (int i = 0; i < _voices.Count; i++)
            if (_voices[i].Handle == handle) { _voices.RemoveAt(i); return; }
    }

    // Everything an origin rebase moves has to include sounds, or a waterfall stays a kilometre behind
    // the world it belongs to (R6).
    public void Rebase(Vector3 offset)
    {
        // Everything moves together, so nothing has moved relative to anything: no speed from a rebase.
        foreach (var voice in _voices) { voice.Position += offset; voice.PrevPosition += offset; }
        ListenerPosition += offset;
        _prevListener += offset;
    }

    public void ResetStats() { Refused = 0; Stolen = 0; }

    // ---- the sums ------------------------------------------------------------------------------------

    private void Compute(Voice voice)
    {
        var record = voice.Record!;
        float bus = _settings.Volume(record.Bus) * _settings.Volume(AudioBus.Master);

        if (!voice.Positional)
        {
            voice.Gain = Math.Clamp(voice.Volume * bus, 0f, 1f);
            voice.Pan = 0f;
            return;
        }

        var to = voice.Position - ListenerPosition;
        float distance = to.Length();

        float attenuation = Attenuation(record, distance) * Cone(record, voice, to, distance);

        voice.Gain = Math.Clamp(voice.Volume * bus * attenuation, 0f, 1f);
        voice.Pan = distance < 0.001f ? 0f : Math.Clamp(Vector3.Dot(to / distance, ListenerRight), -1f, 1f);
        voice.Pitch = Math.Clamp(voice.BasePitch + Doppler(record, voice, to, distance), -1f, 1f);
    }

    // Gain from distance by the record's curve. Linear is the default: not inverse-square, because a
    // game is not a physics demo, and a curve a designer can predict from two numbers is worth more.
    internal static float Attenuation(SoundRecord record, float distance)
    {
        float min = record.MinDistance, max = record.MaxDistance;
        if (distance <= min) return 1f;
        if (distance >= max) return 0f;
        float t = (distance - min) / MathF.Max(max - min, 0.001f);
        switch (record.Rolloff)
        {
            case Rolloff.Log:
            {
                // Min/d, rescaled so it is 1 at Min and 0 at Max instead of cutting off at Min/Max.
                float floor = min / max;
                return min <= 0f ? 1f - t : Math.Clamp((min / distance - floor) / (1f - floor), 0f, 1f);
            }
            case Rolloff.Custom when record.RolloffCurve.Count > 0:
            {
                var c = record.RolloffCurve;
                if (c.Count == 1) return Math.Clamp(c[0], 0f, 1f);
                float at = t * (c.Count - 1);
                int i = Math.Min((int)at, c.Count - 2);
                return Math.Clamp(c[i] + (c[i + 1] - c[i]) * (at - i), 0f, 1f);
            }
            default:
                return 1f - t;
        }
    }

    // A directional emitter: 1 inside the inner cone, OuterGain outside the outer, a blend between.
    private static float Cone(SoundRecord record, Voice voice, Vector3 to, float distance)
    {
        if (record.ConeOuter >= 360f && record.ConeInner >= 360f) return 1f;
        if (voice.Direction == Vector3.Zero || distance < 0.001f) return 1f;
        float cos = Math.Clamp(Vector3.Dot(voice.Direction, -to / distance), -1f, 1f);
        float angle = MathF.Acos(cos) * 180f / MathF.PI;               // off-axis, degrees
        float inner = record.ConeInner * 0.5f, outer = MathF.Max(record.ConeOuter, record.ConeInner) * 0.5f;
        if (angle <= inner) return 1f;
        if (angle >= outer) return record.ConeOuterGain;
        return 1f + (record.ConeOuterGain - 1f) * (angle - inner) / (outer - inner);
    }

    // The pitch change, in octaves, from the emitter and listener moving along the line between them:
    // f'/f = (c + v_listener toward) / (c + v_emitter away). Each speed is capped under c so a teleport
    // cannot divide by nothing, and the sum is clamped to MonoGame's octave by the caller.
    private float Doppler(SoundRecord record, Voice voice, Vector3 to, float distance)
    {
        float scale = _settings.DopplerScale * record.Doppler;
        if (scale <= 0f || !voice.Positional || distance < 0.001f) return 0f;
        float c = MathF.Max(_settings.SpeedOfSound, 1f);
        var dir = to / distance;                                        // listener -> emitter
        float listener = Math.Clamp(Vector3.Dot(ListenerVelocity, dir) * scale, -0.9f * c, 0.9f * c);
        float emitter = Math.Clamp(Vector3.Dot(voice.Velocity, dir) * scale, -0.9f * c, 0.9f * c);
        return MathF.Log2((c + listener) / (c + emitter));
    }

    // One source of randomness, the mixer's own: `Random.Shared` would have made a seeded mixer
    // unseedable, which is the first thing wanted the day a test cares which variation played.
    private float Jitter(float value, float jitter) =>
        jitter <= 0f ? value : Math.Clamp(value + ((float)_random.NextDouble() * 2f - 1f) * jitter, 0f, 1f);

    private int CountOf(RecordId sound)
    {
        int n = 0;
        foreach (var voice in _voices)
            if (voice.Sound == sound && !voice.Stopping && !voice.Music) n++;
        return n;
    }

    // The oldest instance of the *same* sound: a fifth footstep replaces the first, which nobody hears
    // going because it is the one furthest through its own noise.
    private bool StealOldest(RecordId sound)
    {
        Voice? oldest = null;
        foreach (var voice in _voices)
            if (voice.Sound == sound && !voice.Stopping && !voice.Music && (oldest == null || voice.StartedAt < oldest.StartedAt))
                oldest = voice;
        if (oldest == null) return false;
        oldest.Stopping = true;
        Stolen++;
        return true;
    }

    // At the global cap, the quietest voice goes — unless the newcomer is quieter still, in which case
    // nothing is stolen and the newcomer is refused. Priority beats loudness, so a death rattle is not
    // dropped for a footstep standing closer.
    private bool StealQuietest(SoundRecord incoming)
    {
        Voice? quietest = null;
        foreach (var voice in _voices)
        {
            if (voice.Stopping || voice.Music) continue;   // music is never stolen (issue #325)
            if (voice.Record!.Priority > incoming.Priority) continue;
            if (quietest == null || voice.Gain < quietest.Gain) quietest = voice;
        }
        if (quietest == null) return false;
        quietest.Stopping = true;
        Stolen++;
        return true;
    }
}
