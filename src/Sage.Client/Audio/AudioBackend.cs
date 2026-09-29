#nullable enable
using System;
using System.Collections.Generic;
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
}

// Nothing at all: a headless host, a test, or `snd_enabled 0`. The mixer still runs, so everything
// downstream behaves identically — which is what makes "silent" a configuration rather than a branch
// through the game's code.
internal sealed class NullAudioBackend : IAudioBackend
{
    public int Playing => 0;

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

// MonoGame's `SoundEffect`, one `SoundEffectInstance` per voice.
//
// Pan and volume are applied directly rather than through `Apply3D`: the mixer has already done the
// distance and direction sums in origin space (R6), and handing MonoGame a second listener would mean
// two sets of numbers that can disagree. `Apply3D` becomes worth it with doppler and reverb, which is
// the same day the backend is swapped.
internal sealed class MonoGameAudioBackend : IAudioBackend
{
    private readonly ContentService _content;
    private readonly Dictionary<int, SoundEffectInstance> _instances = new();
    private readonly List<int> _finished = new();

    // Set the first time MonoGame reports there is no audio device (a server, a CI runner, a machine
    // with sound off). From then on this backend behaves as NullAudioBackend: the game runs silent
    // instead of dying on its first sound, which is what it did until 2026-09-27.
    private bool _noDevice;

    public MonoGameAudioBackend(ContentService content) => _content = content;

    public int Playing => _instances.Count;

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
            _instances.Clear();
            NullAudioBackend.Drain(mixer);
        }
    }

    private void ApplyToDevice(AudioMixer mixer)
    {
        _finished.Clear();

        foreach (var voice in mixer.Voices)
        {
            if (voice.Stopping)
            {
                if (_instances.Remove(voice.Handle.Id, out var stopping))
                {
                    stopping.Stop(immediate: true);
                    stopping.Dispose();
                }
                _finished.Add(voice.Handle.Id);
                continue;
            }

            if (!voice.Started)
            {
                voice.Started = true;
                var effect = _content.LoadSound(voice.Asset);
                if (effect == null) { _finished.Add(voice.Handle.Id); continue; }

                var instance = effect.CreateInstance();
                instance.IsLooped = voice.Loop;
                _instances[voice.Handle.Id] = instance;
                Push(instance, voice);
                instance.Play();
                continue;
            }

            if (!_instances.TryGetValue(voice.Handle.Id, out var live)) { _finished.Add(voice.Handle.Id); continue; }

            // A one-shot that has run out is the mixer's to forget; it cannot tell on its own, because
            // only the backend knows how long the file was.
            if (live.State == SoundState.Stopped && !voice.Loop)
            {
                live.Dispose();
                _instances.Remove(voice.Handle.Id);
                _finished.Add(voice.Handle.Id);
                continue;
            }

            Push(live, voice);
        }

        foreach (int id in _finished) mixer.Remove(new VoiceHandle(id));
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
        _instances.Clear();
    }
}
