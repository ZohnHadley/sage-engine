#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace sage_engine;

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
    public float Pitch;                  // -1..1
    public float Gain;                   // what the backend should apply, after everything
    public float Pan;                    // -1 left, +1 right
    public double StartedAt;
    public bool Started;                 // the backend has begun it
    public bool Stopping;                // the backend should stop it and drop it
}

public sealed class AudioMixer
{
    private readonly List<Voice> _voices = new();
    private readonly Dictionary<RecordId, double> _lastPlayed = new();
    private readonly float[] _busVolume = new float[Enum.GetValues<AudioBus>().Length];
    private readonly Random _random = new();

    private int _nextId = 1;
    private double _now;

    public AudioMixer()
    {
        for (int i = 0; i < _busVolume.Length; i++) _busVolume[i] = 1f;
    }

    // The cap across everything. Beyond it the quietest voice is stolen, which is the one a player is
    // least likely to notice going.
    public int MaxVoices { get; set; } = 32;

    public IReadOnlyList<Voice> Voices => _voices;

    public int Playing => _voices.Count;

    // How many were refused since the last reset, for `snd_stats`: a number nobody looks at until the
    // day the audio sounds thin, and then the first thing they want.
    public int Refused { get; private set; }

    public int Stolen { get; private set; }

    public Vector3 ListenerPosition { get; private set; }

    public Vector3 ListenerForward { get; private set; } = -Vector3.UnitZ;

    public Vector3 ListenerRight { get; private set; } = Vector3.UnitX;

    public float BusVolume(AudioBus bus) => _busVolume[(int)bus];

    public void SetBusVolume(AudioBus bus, float volume) => _busVolume[(int)bus] = Math.Clamp(volume, 0f, 1f);

    public void SetListener(Vector3 position, Quaternion rotation)
    {
        ListenerPosition = position;
        ListenerForward = Vector3.Transform(TransformMath.Forward, rotation);
        ListenerRight = Vector3.Transform(Vector3.UnitX, rotation);
    }

    // Asks for a sound. Returns a voice, or `None` when the mixer refused it — too many already, too
    // soon after the last one, or nothing to play. A caller never has to check before asking, which is
    // what keeps the calling code one line.
    public VoiceHandle Play(RecordId sound, SoundRecord? record, Vector3 position, bool positional,
                            float volume = 1f, bool loop = false)
    {
        if (record == null || record.Variations.Count == 0) { Refused++; return VoiceHandle.None; }

        // Cooldown: the same sound twice in the same instant is one sound with a phasing artefact.
        if (record.Cooldown > 0f && _lastPlayed.TryGetValue(sound, out double last) && _now - last < record.Cooldown)
        {
            Refused++;
            return VoiceHandle.None;
        }

        if (CountOf(sound) >= Math.Max(record.MaxInstances, 1) && !StealOldest(sound)) { Refused++; return VoiceHandle.None; }
        if (_voices.Count >= MaxVoices && !StealQuietest(record)) { Refused++; return VoiceHandle.None; }

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
            Pitch = record.PitchJitter <= 0f ? 0f : (float)(_random.NextDouble() * 2 - 1) * record.PitchJitter,
            StartedAt = _now,
        };
        Compute(voice);
        _voices.Add(voice);
        _lastPlayed[sound] = _now;
        return voice.Handle;
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
        _now = now;
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
        foreach (var voice in _voices) voice.Position += offset;
        ListenerPosition += offset;
    }

    public void ResetStats() { Refused = 0; Stolen = 0; }

    // ---- the sums ------------------------------------------------------------------------------------

    private void Compute(Voice voice)
    {
        var record = voice.Record!;
        float bus = _busVolume[(int)record.Bus] * _busVolume[(int)AudioBus.Master];

        if (!voice.Positional)
        {
            voice.Gain = Math.Clamp(voice.Volume * bus, 0f, 1f);
            voice.Pan = 0f;
            return;
        }

        var to = voice.Position - ListenerPosition;
        float distance = to.Length();

        // Linear between Min and Max. Not inverse-square: a game is not a physics demo, and a curve a
        // designer can predict from two numbers is worth more here than one that is correct.
        float attenuation = distance <= record.MinDistance ? 1f
            : distance >= record.MaxDistance ? 0f
            : 1f - (distance - record.MinDistance) / MathF.Max(record.MaxDistance - record.MinDistance, 0.001f);

        voice.Gain = Math.Clamp(voice.Volume * bus * attenuation, 0f, 1f);
        voice.Pan = distance < 0.001f ? 0f : Math.Clamp(Vector3.Dot(to / distance, ListenerRight), -1f, 1f);
    }

    private static float Jitter(float value, float jitter) =>
        jitter <= 0f ? value : Math.Clamp(value + (Random.Shared.NextSingle() * 2f - 1f) * jitter, 0f, 1f);

    private int CountOf(RecordId sound)
    {
        int n = 0;
        foreach (var voice in _voices)
            if (voice.Sound == sound && !voice.Stopping) n++;
        return n;
    }

    // The oldest instance of the *same* sound: a fifth footstep replaces the first, which nobody hears
    // going because it is the one furthest through its own noise.
    private bool StealOldest(RecordId sound)
    {
        Voice? oldest = null;
        foreach (var voice in _voices)
            if (voice.Sound == sound && !voice.Stopping && (oldest == null || voice.StartedAt < oldest.StartedAt))
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
            if (voice.Stopping) continue;
            if (voice.Record!.Priority > incoming.Priority) continue;
            if (quietest == null || voice.Gain < quietest.Gain) quietest = voice;
        }
        if (quietest == null) return false;
        quietest.Stopping = true;
        Stolen++;
        return true;
    }
}
