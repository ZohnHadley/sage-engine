#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework.Audio;

namespace Sage.Client;

// Making the noise (docs/design/11 §3, TODO F4).
//
// The mixer decided what plays and how loud (`AudioMixer`, engine side, testable); this owns the part
// that needs a sound card. Behind an interface, because 11 §3 wants the option of OpenAL Soft or FMOD
// later for occlusion and reverb, and because a headless run needs a backend that does nothing.
internal interface IAudioBackend : IDisposable
{
    // Starts, updates and finishes voices to match the mixer. Called once a frame.
    void Apply(AudioMixer mixer);

    int Playing { get; }

    // The sound behind `asset` is about to be disposed (hot reload, issue 4h-3): drop every instance
    // made from it. The mixer decides what happens to the voices (`AudioMixer.Invalidate`).
    void Release(AssetPath asset);
}

// Nothing at all: a headless host, a test, or `snd_enabled 0`. The mixer still runs, so everything
// downstream behaves identically — which is what makes "silent" a configuration rather than a branch
// through the game's code.
internal sealed class NullAudioBackend : IAudioBackend
{
    public int Playing => 0;

    public void Release(AssetPath asset) { }

    public void Apply(AudioMixer mixer) => Drain(mixer);

    // A one-shot with nothing to play it must still end, or the mixer fills up with voices that
    // never finish and quietly refuses everything after the first thirty-two.
    internal static void Drain(AudioMixer mixer)
    {
        for (int i = mixer.Voices.Count - 1; i >= 0; i--)
        {
            var voice = mixer.Voices[i];
            if (voice.Stopping || !voice.Loop) mixer.Remove(voice.Handle);
        }
    }

    public void Dispose() { }
}

// MonoGame's `SoundEffect`, one `SoundEffectInstance` per voice; a streamed sound (issue #326) is a
// `DynamicSoundEffectInstance` fed by a `PcmStreamer`, which does the decoding, the looping and the
// buffer bound headlessly, so all this does is top the device's queue up once a frame.
//
// Pan and volume are applied directly rather than through `Apply3D`: the mixer has already done the
// distance and direction sums in origin space (R6), and handing MonoGame a second listener would mean
// two sets of numbers that can disagree. `Apply3D` becomes worth it with doppler and reverb, which is
// the same day the backend is swapped.
internal sealed class MonoGameAudioBackend : IAudioBackend
{
    private readonly ContentService _content;
    private readonly Dictionary<int, SoundEffectInstance> _instances = new();
    private readonly Dictionary<int, AssetPath> _assets = new();   // which file each instance was made from
    private readonly Dictionary<int, Streamed> _streams = new();     // the streamed voices' decoders
    private readonly List<int> _finished = new();

    // A streamed voice: its decoder, and the instance's SubmitBuffer as made once for it.
    private sealed class Streamed
    {
        public required PcmStreamer Streamer;
        public required DynamicSoundEffectInstance Instance;
        public required Action<byte[], int> Submit;
    }

    // Set the first time MonoGame reports there is no audio device (a server, a CI runner, a machine
    // with sound off). From then on this backend behaves as NullAudioBackend: the game runs silent
    // instead of dying on its first sound, which is what it did until 2026-09-27.
    private bool _noDevice;

    public MonoGameAudioBackend(ContentService content) => _content = content;

    public int Playing => _instances.Count;

    public void Release(AssetPath asset)
    {
        _finished.Clear();
        foreach (var (id, from) in _assets)
            if (from == asset) _finished.Add(id);
        foreach (int id in _finished) Drop(id);
        _finished.Clear();
    }

    // Stops a voice's instance and lets go of it and its decoder.
    private void Drop(int id)
    {
        if (_instances.Remove(id, out var instance)) { instance.Stop(immediate: true); instance.Dispose(); }
        if (_streams.Remove(id, out var stream)) stream.Streamer.Dispose();
        _assets.Remove(id);
    }

    public void Apply(AudioMixer mixer)
    {
        if (_noDevice) { NullAudioBackend.Drain(mixer); return; }
        try
        {
            ApplyToDevice(mixer);
        }
        catch (NoAudioHardwareException ex)
        {
            _noDevice = true;
            Log.Warn(LogCat.Audio, $"No audio device ({ex.Message}); sound is off for this run. " +
                                   "`snd_enabled 0` turns it off on purpose.");
            foreach (var instance in _instances.Values) instance.Dispose();
            foreach (var stream in _streams.Values) stream.Streamer.Dispose();
            _instances.Clear();
            _streams.Clear();
            _assets.Clear();
            NullAudioBackend.Drain(mixer);
        }
    }

    private void ApplyToDevice(AudioMixer mixer)
    {
        _finished.Clear();

        foreach (var voice in mixer.Voices)
        {
            int id = voice.Handle.Id;
            if (voice.Stopping)
            {
                Drop(id);
                _finished.Add(id);
                continue;
            }

            if (!voice.Started)
            {
                voice.Started = true;
                Drop(id);   // a restart (a reloaded file) begins again from nothing
                var instance = PcmStreamer.Streams(voice.Record, voice.Asset) ? StartStream(voice) : StartWhole(voice);
                if (instance == null) { _finished.Add(id); continue; }
                _instances[id] = instance;
                _assets[id] = voice.Asset;
                Push(instance, voice);
                instance.Play();
                continue;
            }

            if (!_instances.TryGetValue(id, out var live)) { _finished.Add(id); continue; }

            // A streamed voice is topped up to its few buffers, and has finished when the decoder has
            // nothing more and the device has played what it was given.
            if (_streams.TryGetValue(id, out var stream))
            {
                if (TryPump(id, voice.Asset, stream) && stream.Streamer.Finished(stream.Instance.PendingBufferCount))
                {
                    Drop(id);
                    _finished.Add(id);
                    continue;
                }
                if (!_instances.ContainsKey(id)) { _finished.Add(id); continue; }
                Push(live, voice);
                continue;
            }

            // A one-shot that has run out is the mixer's to forget; it cannot tell on its own, because
            // only the backend knows how long the file was.
            if (live.State == SoundState.Stopped && !voice.Loop)
            {
                Drop(id);
                _finished.Add(id);
                continue;
            }

            Push(live, voice);
        }

        foreach (int id in _finished) mixer.Remove(new VoiceHandle(id));
    }

    private SoundEffectInstance? StartWhole(Voice voice)
    {
        var effect = _content.LoadSound(voice.Asset);
        if (effect == null) return null;
        var instance = effect.CreateInstance();
        instance.IsLooped = voice.Loop;
        return instance;
    }

    // A DynamicSoundEffectInstance at the file's rate and channels, with its first buffers queued before
    // it plays. The streamer loops, so the instance never does.
    private SoundEffectInstance? StartStream(Voice voice)
    {
        var streamer = _content.OpenSoundStream(voice.Asset, voice.Loop, voice.LoopStart, voice.LoopEnd);   // music's loop points (#325)
        if (streamer == null) return null;
        var instance = new DynamicSoundEffectInstance(streamer.SampleRate, streamer.Channels == 1 ? AudioChannels.Mono : AudioChannels.Stereo);
        var stream = new Streamed
        {
            Streamer = streamer,
            Instance = instance,
            Submit = (buffer, bytes) => instance.SubmitBuffer(buffer, 0, bytes),
        };
        _streams[voice.Handle.Id] = stream;
        if (!TryPump(voice.Handle.Id, voice.Asset, stream)) { instance.Dispose(); return null; }
        return instance;
    }

    // Tops the queue up. A file that breaks off part-way costs the voice, not the frame (11 §8).
    private bool TryPump(int id, AssetPath asset, Streamed stream)
    {
        try
        {
            stream.Streamer.Pump(stream.Instance.PendingBufferCount, stream.Submit);
            return true;
        }
        catch (InvalidDataException ex)
        {
            Log.Once(LogCat.Audio, LogLevel.Error, $"sound-stream-read:{asset}", $"Streamed sound '{asset}' stopped: {ex.Message}");
            Drop(id);
            return false;
        }
    }

    private static void Push(SoundEffectInstance instance, Voice voice)
    {
        instance.Volume = Math.Clamp(voice.Gain, 0f, 1f);
        instance.Pan = Math.Clamp(voice.Pan, -1f, 1f);
        instance.Pitch = Math.Clamp(voice.Pitch, -1f, 1f);
    }

    public void Dispose()
    {
        foreach (var instance in _instances.Values) { instance.Stop(immediate: true); instance.Dispose(); }
        foreach (var stream in _streams.Values) stream.Streamer.Dispose();
        _instances.Clear();
        _streams.Clear();
        _assets.Clear();
    }
}
