#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Simulation;

// One bus's meter: what the voices on it add up to, computed from their effective gains. No PCM is
// measured (issue 4o-12): a voice's `Gain` is already volume x bus x master x distance, so the sum is
// what the player would hear at most, and costs nothing to work out.
public readonly record struct BusLevel(AudioBus Bus, int Voices, float Peak, float Level, float Setting);

// One line of the `snd_debug` overlay: a voice as the mixer sees it.
public readonly record struct VoiceRow(RecordId Sound, AssetPath Asset, AudioBus Bus, float Distance, float Gain, float Pan,
                                       bool Positional, bool Loop, bool Stopping);

// What the audio tools show (issue 4o-12): per-bus levels and the active voices with their distances,
// worked out headlessly from a mixer so a test can check them and the editor panel, `snd_debug` and
// `snd_stats` all read the same numbers. It allocates only when asked for a new list (`Refresh`).
public sealed class AudioMonitor
{
    private readonly BusLevel[] _buses = new BusLevel[Enum.GetValues<AudioBus>().Length];
    private readonly List<VoiceRow> _rows = new();

    // One entry per bus, in `AudioBus` order. `Master` meters everything: the total of all voices, the
    // way a master fader reads, with the master volume already in each gain.
    public IReadOnlyList<BusLevel> Buses => _buses;

    // Active voices, loudest first (what is audible matters more than what started first).
    public IReadOnlyList<VoiceRow> Voices => _rows;

    public BusLevel Bus(AudioBus bus) => _buses[(int)bus];

    // Reads the mixer as it is now.
    public void Refresh(AudioMixer mixer)
    {
        _rows.Clear();
        Span<int> count = stackalloc int[_buses.Length];
        Span<float> peak = stackalloc float[_buses.Length];
        Span<float> sum = stackalloc float[_buses.Length];

        foreach (var voice in mixer.Voices)
        {
            if (voice.Stopping) continue;   // on its way out: nothing of it will be heard
            var bus = voice.Record?.Bus ?? AudioBus.Sfx;
            float distance = voice.Positional ? (voice.Position - mixer.ListenerPosition).Length() : 0f;
            _rows.Add(new VoiceRow(voice.Sound, voice.Asset, bus, distance, voice.Gain, voice.Pan, voice.Positional, voice.Loop, false));
            Add(count, peak, sum, (int)bus, voice.Gain);
            Add(count, peak, sum, (int)AudioBus.Master, voice.Gain);
        }

        for (int i = 0; i < _buses.Length; i++)
        {
            var bus = (AudioBus)i;
            // The sum of gains, clamped: two half-loud voices read as a full bar, which is what two of
            // them sound like. A peak is the loudest single voice.
            _buses[i] = new BusLevel(bus, count[i], peak[i], Math.Min(sum[i], 1f), mixer.BusVolume(bus));
        }
        _rows.Sort(static (a, b) => b.Gain.CompareTo(a.Gain));
    }

    private static void Add(Span<int> count, Span<float> peak, Span<float> sum, int i, float gain)
    {
        count[i]++;
        peak[i] = MathF.Max(peak[i], gain);
        sum[i] += gain;
    }
}
